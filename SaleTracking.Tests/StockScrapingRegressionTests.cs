using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class StockScrapingRegressionTests
{
    private const string ProductUrl = "https://pk.lamaretail.com/collections/man-shirts/products/washed-lyocell-shirt-mas26tp056-charcoal?variant=47879079755954";
    private const string ProductJsonUrl = "https://pk.lamaretail.com/products/washed-lyocell-shirt-mas26tp056-charcoal.js";
    private const string Page = """
        <script>Shopify.shop = 'lama-retail.myshopify.com'; window.labels={soldOut:'Sold Out'};</script>
        <meta property="product:price:amount" content="9999">
        <script type="application/ld+json">
        {"@type":"Product","offers":[
          {"availability":"https://schema.org/OutOfStock","price":9999,"url":"https://pk.lamaretail.com/products/washed-lyocell-shirt-mas26tp056-charcoal?variant=123"},
          {"availability":"https://schema.org/InStock","price":10950,"url":"https://pk.lamaretail.com/products/washed-lyocell-shirt-mas26tp056-charcoal?variant=47879079755954"}
        ]}
        </script><template>Out of stock</template><button>Add to cart</button>
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShopifyUsesSelectedVariantForBothStockAndPrice(bool available)
    {
        var handler = new Routes(request => request.RequestUri!.AbsoluteUri == ProductJsonUrl
            ? Json($$"""{"handle":"washed-lyocell-shirt-mas26tp056-charcoal","variants":[{"id":123,"available":{{(!available).ToString().ToLowerInvariant()}},"price":999900},{"id":47879079755954,"available":{{available.ToString().ToLowerInvariant()}},"price":1095000}]}""")
            : Html(Page));
        var result = await Scraper(handler).ScrapeProductAsync(ProductUrl, default);
        Assert.Equal(10950m, result.Price);
        Assert.Equal(available, result.IsInStock);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task UnknownVariantDoesNotInheritAnotherSizesStockOrPrice()
    {
        var handler = new Routes(request => request.RequestUri!.AbsolutePath.EndsWith(".js")
            ? Json("""{"handle":"washed-lyocell-shirt-mas26tp056-charcoal","variants":[{"id":123,"available":true,"price":1095000}]}""")
            : Html(Page));
        var result = await Scraper(handler).ScrapeProductAsync(ProductUrl.Replace("47879079755954", "999"), default);
        Assert.Null(result.Price);
        Assert.Null(result.IsInStock);
        Assert.Contains("variant", result.Error);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    public async Task ApiFailureFallsBackToTheMatchingStructuredOffer(int status)
    {
        var handler = new Routes(request => request.RequestUri!.AbsolutePath.EndsWith(".js")
            ? new HttpResponseMessage((HttpStatusCode)status) : Html(Page));
        var result = await Scraper(handler).ScrapeProductAsync(ProductUrl, default);
        Assert.True(result.IsInStock);
        Assert.Equal(10950m, result.Price);
    }

    [Fact]
    public async Task MissingApiAndUnmatchedOffersRemainUnknown()
    {
        var handler = new Routes(request => request.RequestUri!.AbsolutePath.EndsWith(".js")
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : Html(Page));
        var result = await Scraper(handler).ScrapeProductAsync(ProductUrl.Replace("47879079755954", "999"), default);
        Assert.Null(result.IsInStock);
        Assert.Null(result.Price);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("<script>var labels={soldOut:'Sold Out'};</script><button>Add to cart</button>", true)]
    [InlineData("<script>{\"availability\":\"https://schema.org/InStock\"}</script><template>Out of stock</template>", true)]
    [InlineData("<script>{\"variants\":[{\"available\":true},{\"available\":false}]}</script><button>Add to cart</button>", true)]
    [InlineData("<script>{\"cart\":{\"quantity\":0},\"product\":{\"available\":true}}</script>", true)]
    [InlineData("<meta itemprop='price' content='100'>", null)]
    [InlineData("<script>{\"variants\":[{\"available\":true},{\"available\":false}]}</script>", null)]
    [InlineData("<button disabled>Add to cart</button>", false)]
    [InlineData("<meta itemprop='availability' content='out of stock'><button>Add to cart</button>", false)]
    public void GenericStockRequiresAvailabilityEvidence(string html, bool? expected) =>
        Assert.Equal(expected, GenericHtmlPriceScraper.DetectStockStatus(html, 100m));

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresRetainTheActualStatus(int status)
    {
        var result = await Scraper(new Routes(_ => new HttpResponseMessage((HttpStatusCode)status)))
            .ScrapeProductAsync(ProductUrl, default);
        Assert.Contains($"HTTP {status}", result.Error);
        Assert.Null(result.IsInStock);
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterAcrossProductsOnTheSameStore()
    {
        var clock = new Clock();
        var handler = new Routes(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        var scraper = new GenericHtmlPriceScraper(new HttpClient(handler), NullLogger<GenericHtmlPriceScraper>.Instance, clock);
        await scraper.ScrapeProductAsync(ProductUrl, default);
        await scraper.ScrapeProductAsync(ProductUrl.Replace("charcoal", "navy"), default);
        Assert.Equal(1, handler.Calls);
        clock.Now = clock.Now.AddMinutes(2);
        await scraper.ScrapeProductAsync(ProductUrl, default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringVariantFetchIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Routes(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".js"))
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return Html(Page);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scraper(handler).ScrapeProductAsync(ProductUrl, cancellation.Token));
    }

    private static GenericHtmlPriceScraper Scraper(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<GenericHtmlPriceScraper>.Instance);
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Routes(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
