using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class PriceScraperTests
{
    [Theory]
    [InlineData("<meta itemprop='price' content='5,000'>", "5000")]
    [InlineData("<meta content='4,500.50' property='product:price:amount'>", "4500.50")]
    [InlineData("<meta itemprop='price' content='1.234,56'>", "1234.56")]
    [InlineData("{\"price\":1234.5678,\"currency\":\"PKR\"}", "1234.5678")]
    [InlineData("{\"price\":\"5,000\"}", "5000")]
    [InlineData("{\"price\":\"1234,56\"}", "1234.56")]
    [InlineData("{\"price\":5000}", "5000")]
    [InlineData("{\"price\":\"5000abc\"}", null)]
    [InlineData("{\"price\":\"5,00,0\"}", null)]
    [InlineData("{\"price\":1234.56abc}", null)]
    [InlineData("{\"price\":1,299}", null)]
    [InlineData("{\"price\":-5000}", null)]
    [InlineData("{\"price\":0}", null)]
    [InlineData("<meta itemprop='price'><meta content='1'>", null)]
    public async Task ReadsTheWholePriceWithoutTruncationOrCrossingElements(string html, string? expected)
    {
        var scraper = new GenericHtmlPriceScraper(new HttpClient(new Handler(html)), NullLogger<GenericHtmlPriceScraper>.Instance);
        var actual = await scraper.GetCurrentPriceAsync("https://example.com/product", CancellationToken.None);
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual);
    }

    [Theory]
    [InlineData("<span id='pdpPrice' class='current-price'>Rs. 83,999</span>", "83999")]
    [InlineData("<div class='product-price'>$49.99</div>", "49.99")]
    [InlineData("<span class='sale-price'>Rs. 800</span>", "800")]
    [InlineData("<span class='current-price'>Rs. 0</span>", null)]
    public async Task ReadsDomPriceWhenMetaAndJsonAreMissing(string html, string? expected)
    {
        var scraper = new GenericHtmlPriceScraper(new HttpClient(new Handler(html)), NullLogger<GenericHtmlPriceScraper>.Instance);
        var actual = await scraper.GetCurrentPriceAsync("https://example.com/product", CancellationToken.None);
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual);
    }

    [Fact]
    public async Task FallsBackToClientSideCatalogWhenProductQueryIsPresent()
    {
        var catalogJson = """
        [
          {"id": "nova-keyboard", "price": 49999},
          {"id": "aura-headphones", "price": 800}
        ]
        """;
        var pageHtml = "<html><head><title>Aura Store</title></head><body><p>Loaded from products.json</p></body></html>";

        var handler = new MultiRouteHandler(new Dictionary<string, (string content, string mediaType)>
        {
            ["https://example.com/product.html?id=aura-headphones"] = (pageHtml, "text/html"),
            ["https://example.com/products.json"] = (catalogJson, "application/json")
        });

        var scraper = new GenericHtmlPriceScraper(new HttpClient(handler), NullLogger<GenericHtmlPriceScraper>.Instance);
        var actual = await scraper.GetCurrentPriceAsync("https://example.com/product.html?id=aura-headphones", CancellationToken.None);
        Assert.Equal(800m, actual);
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scraper = new GenericHtmlPriceScraper(new HttpClient(new Handler("{}")), NullLogger<GenericHtmlPriceScraper>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scraper.GetCurrentPriceAsync("https://example.com/product", cancellation.Token));
    }

    private sealed class MultiRouteHandler(Dictionary<string, (string content, string mediaType)> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri?.AbsoluteUri ?? "";
            if (routes.TryGetValue(url, out var route))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(route.content, System.Text.Encoding.UTF8, route.mediaType)
                };
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Handler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}
