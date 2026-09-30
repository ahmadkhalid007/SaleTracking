using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaleTracking.Models;

namespace SaleTracking.Services;

public class ProductScrapeResult
{
    public decimal? Price { get; }
    public bool? IsInStock { get; }
    public string? Error { get; }

    public ProductScrapeResult(decimal? price, bool? isInStock, string? error = null)
    {
        Price = price;
        IsInStock = isInStock;
        Error = error;
    }
}

public interface IPriceScraper
{
    Task<decimal?> GetCurrentPriceAsync(string productUrl, CancellationToken cancellationToken);
}

public interface IProductScraper : IPriceScraper
{
    Task<ProductScrapeResult> ScrapeProductAsync(string productUrl, CancellationToken cancellationToken);
}

// Generic fallback scraper. Add domain-specific IPriceScraper implementations as stores are supported.
public sealed partial class GenericHtmlPriceScraper(HttpClient httpClient, ILogger<GenericHtmlPriceScraper> logger,
    TimeProvider? timeProvider = null) : IProductScraper
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> rateLimitedHosts = new();

    public async Task<decimal?> GetCurrentPriceAsync(string productUrl, CancellationToken cancellationToken)
    {
        var result = await ScrapeProductAsync(productUrl, cancellationToken);
        return result.Price;
    }

    public async Task<ProductScrapeResult> ScrapeProductAsync(string productUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(productUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ProductScrapeResult(null, null, "The product URL is invalid.");
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (rateLimitedHosts.TryGetValue(uri.Authority, out var resumeAt) && resumeAt > now)
            return new ProductScrapeResult(null, null, "The store is rate limiting requests (HTTP 429). Waiting before retrying.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return HttpFailure(uri, response);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // Shopify pages contain every size, recommendations and theme labels such as
            // "Sold Out". Read the requested variant before considering page-wide text.
            if (IsShopifyProductPage(uri, html))
            {
                var product = await TryFetchShopifyProductAsync(uri, cancellationToken);
                if (product is null || product.Price is null && product.IsInStock is null)
                    product = ReadShopifyOffer(uri, html) ?? product;
                // Unknown availability must not turn into a false stock alert.
                return product ?? new ProductScrapeResult(null, null, "Could not read data for the selected product or size.");
            }
            
            decimal? price = null;
            // 1. Try standard meta tags (OpenGraph / Microdata)
            var match = PriceMetaRegex().Match(html);
            if (match.Success)
            {
                price = ParsePrice(match.Groups["price"].Value);
            }

            // 2. Try embedded JSON (JSON-LD, script payloads)
            if (price is not > 0)
            {
                match = JsonPriceRegex().Match(html);
                if (match.Success)
                {
                    price = ParsePrice(match.Groups["price"].Value);
                }
            }

            // 3. Try DOM price elements (e.g. class="current-price", id="pdpPrice", etc.)
            if (price is not > 0)
            {
                var domMatch = DomPriceRegex().Match(html);
                if (domMatch.Success)
                {
                    price = ParsePrice(domMatch.Groups["price"].Value);
                }
            }

            // 4. Try client-side API / catalog fallback (e.g. products.json matching URL query ?id=...)
            if (price is not > 0)
            {
                price = await TryFetchClientSideCatalogPriceAsync(uri, html, cancellationToken);
            }

            if (price is <= 0) price = null;
            var inStock = DetectStockStatus(html, price);

            return new ProductScrapeResult(price, inStock);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Price fetch failed for {ProductUrl}: {Message}", productUrl, ex.Message);
            return new ProductScrapeResult(null, null, ex is TaskCanceledException
                ? "The store request timed out. Will retry at the next scheduled check."
                : "Could not connect to the store. Will retry at the next scheduled check.");
        }
    }

    private ProductScrapeResult HttpFailure(Uri uri, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var retryAt = response.Headers.RetryAfter?.Date
                ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            rateLimitedHosts[uri.Authority] = retryAt > now ? retryAt : now.AddSeconds(1);
            return new ProductScrapeResult(null, null, "The store is rate limiting requests (HTTP 429). Waiting before retrying.");
        }
        return new ProductScrapeResult(null, null, $"The store returned HTTP {status}. " +
            (status == 404 ? "The product page was not found." : "Could not read product data. Will retry at the next scheduled check."));
    }

    private async Task<decimal?> TryFetchClientSideCatalogPriceAsync(Uri uri, string html, CancellationToken cancellationToken)
    {
        try
        {
            string? productId = GetQueryParam(uri.Query, "id")
                ?? GetQueryParam(uri.Query, "productId")
                ?? GetQueryParam(uri.Query, "product_id")
                ?? GetQueryParam(uri.Query, "sku")
                ?? GetQueryParam(uri.Query, "item")
                ?? GetQueryParam(uri.Query, "slug");

            if (html.Contains("products.json", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(productId))
            {
                var jsonUri = new Uri(uri, "products.json");
                using var jsonReq = new HttpRequestMessage(HttpMethod.Get, jsonUri);
                jsonReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                jsonReq.Headers.Accept.ParseAdd("application/json,text/plain,*/*");
                using var jsonResp = await httpClient.SendAsync(jsonReq, cancellationToken);
                if (jsonResp.IsSuccessStatusCode)
                {
                    var jsonContent = await jsonResp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(jsonContent);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            bool isMatch = string.IsNullOrWhiteSpace(productId);
                            if (!isMatch)
                            {
                                if (item.TryGetProperty("id", out var idProp) && string.Equals(idProp.GetString(), productId, StringComparison.OrdinalIgnoreCase))
                                    isMatch = true;
                                else if (item.TryGetProperty("sku", out var skuProp) && string.Equals(skuProp.GetString(), productId, StringComparison.OrdinalIgnoreCase))
                                    isMatch = true;
                                else if (item.TryGetProperty("slug", out var slugProp) && string.Equals(slugProp.GetString(), productId, StringComparison.OrdinalIgnoreCase))
                                    isMatch = true;
                            }

                            if (isMatch && item.TryGetProperty("price", out var priceProp))
                            {
                                if (priceProp.ValueKind == JsonValueKind.Number && priceProp.TryGetDecimal(out var p) && p > 0)
                                    return p;
                                if (priceProp.ValueKind == JsonValueKind.String)
                                {
                                    var parsed = ParsePrice(priceProp.GetString() ?? "");
                                    if (parsed is > 0) return parsed;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Client-side catalog check skipped for {ProductUrl}", uri);
        }

        return null;
    }

    private static string? GetQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }
        return null;
    }

    private static decimal? ParsePrice(string value)
    {
        var raw = value.Trim();
        if (raw.Contains(',') && raw.Contains('.'))
        {
            var decimalSeparator = raw.LastIndexOf(',') > raw.LastIndexOf('.') ? ',' : '.';
            var groupSeparator = decimalSeparator == ',' ? '.' : ',';
            var pattern = @"^\d{1,3}(?:" + Regex.Escape(groupSeparator.ToString()) + @"\d{3})+"
                + Regex.Escape(decimalSeparator.ToString()) + @"\d+$";
            if (!Regex.IsMatch(raw, pattern)) return null;
            raw = raw.Replace(groupSeparator.ToString(), "").Replace(decimalSeparator, '.');
        }
        else if (raw.Contains(','))
        {
            if (Regex.IsMatch(raw, @"^\d{1,3}(?:,\d{3})+$")) raw = raw.Replace(",", "");
            else if (Regex.IsMatch(raw, @"^\d+,\d{1,2}$")) raw = raw.Replace(',', '.');
            else return null;
        }
        if (!Regex.IsMatch(raw, @"^\d+(?:\.\d+)?$")) return null;
        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) && price > 0
            ? price : null;
    }

    [GeneratedRegex("""<meta\b[^>]*(?:property|itemprop|name)\s*=\s*["'](?:product:price:amount|og:price:amount|price)["'][^>]*content\s*=\s*["'](?<price>[^"']+)["']|<meta\b[^>]*content\s*=\s*["'](?<price>[^"']+)["'][^>]*(?:property|itemprop|name)\s*=\s*["'](?:product:price:amount|og:price:amount|price)["']""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PriceMetaRegex();
    [GeneratedRegex("""["']price["']\s*:\s*(?:["'](?<price>[^"']+)["']\s*(?=[,}])|(?<price>[0-9]+(?:\.[0-9]+)?)\s*(?=}|,\s*["'][^"']+["']\s*:))""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex JsonPriceRegex();
    [GeneratedRegex("""<[^>]*\b(?:id|class)\s*=\s*["'][^"']*\b(?:current-price|pdp-price|product-price|sale-price|special-price|offer-price|final-price)\b[^"']*["'][^>]*>(?:[^<0-9]*)(?<price>[0-9]{1,3}(?:[.,]\d{3})*(?:[.,]\d{2})?|[0-9]+(?:\.[0-9]+)?)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DomPriceRegex();
    [GeneratedRegex("""<meta\b[^>]*(?:property|itemprop|name)\s*=\s*["'](?:product:availability|og:availability|availability)["'][^>]*content\s*=\s*["'](?<status>[^"']+)["']|<meta\b[^>]*content\s*=\s*["'](?<status>[^"']+)["'][^>]*(?:property|itemprop|name)\s*=\s*["'](?:product:availability|og:availability|availability)["']""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex StockMetaRegex();

    [GeneratedRegex("""\b(?:sold\s*out|out\s*of\s*stock|currently\s*unavailable|temporarily\s*unavailable|temporarily\s*out\s*of\s*stock|item\s*unavailable|product\s*unavailable|not\s*in\s*stock|back\s*in\s*stock\s*soon|notify\s*me\s*when\s*(?:back\s*)?in\s*stock|notify\s*me\s*when\s*available|email\s*when\s*available|alert\s*me\s*when\s*(?:in\s*stock|available)|no\s*stock|zero\s*in\s*stock)\b""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex OutOfStockTextRegex();

    [GeneratedRegex("""<(?:button|input)\b[^>]*(?:\bdisabled\b|class=["'][^"']*\b(?:disabled|btn-disabled|is-disabled)\b)[^>]*(?:>[^<]*(?:add\s*to\s*cart|buy\s*now|add\s*to\s*bag|order\s*now)|value=["'][^"']*(?:add\s*to\s*cart|buy\s*now|add\s*to\s*bag|order\s*now))""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DisabledCartButtonRegex();

    [GeneratedRegex("""\b(?:class|id)\s*=\s*["'][^"']*\b(?:out-of-stock|outofstock|sold-out|soldout|product-unavailable|is-sold-out)\b[^"']*["']""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex OutOfStockDomClassRegex();

    [GeneratedRegex("""\b(?:in\s*stock|available\s*in\s*store|only\s*\d+\s*left\s*in\s*stock|ready\s*to\s*ship)\b""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InStockTextRegex();

    [GeneratedRegex("""<button\b(?![^>]*\bdisabled\b)[^>]*>[^<]*(?:add\s*to\s*cart|buy\s*now|add\s*to\s*bag|order\s*now)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ActiveCartButtonRegex();

    public static bool? DetectStockStatus(string html, decimal? price)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        // Hidden templates can contain every availability state of a theme.
        html = HiddenTemplateRegex().Replace(html, " ");
        var metaMatch = StockMetaRegex().Match(html);
        if (metaMatch.Success)
        {
            var content = metaMatch.Groups["status"].Value.Trim().ToLowerInvariant();
            if (content.Contains("outofstock") || content.Contains("out of stock") || content.Contains("soldout") || content.Contains("sold out") || content == "oos" || content.Contains("unavailable") || content.Contains("backorder") || content.Contains("discontinued"))
                return false;
            if (content.Contains("instock") || content.Contains("in stock") || content == "available") return true;
        }

        var unavailable = Regex.IsMatch(html,
            """schema\.org/(?:OutOfStock|SoldOut|Discontinued)|"availability"\s*:\s*"(?:OutOfStock|SoldOut|Discontinued)"|"(?:available|is_in_stock|in_stock|isAvailable)"\s*:\s*false\b|"outOfStock"\s*:\s*true\b|"stock_status"\s*:\s*"outofstock"\s*""",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var available = Regex.IsMatch(html,
            """schema\.org/InStock|"availability"\s*:\s*"InStock"|"(?:available|is_in_stock|in_stock|isAvailable)"\s*:\s*true\b|"outOfStock"\s*:\s*false\b|"stock_status"\s*:\s*"instock"\s*""",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        // Conflicting variants are not evidence that the selected size is sold out.
        // Generic quantity fields may describe an empty cart, so never use them here.
        if (available != unavailable) return available;

        html = NonVisibleContentRegex().Replace(html, " ");
        var disabledCart = DisabledCartButtonRegex().IsMatch(html);
        var activeCart = ActiveCartButtonRegex().IsMatch(html);
        if (activeCart != disabledCart) return activeCart;
        var soldOutText = OutOfStockDomClassRegex().IsMatch(html) || OutOfStockTextRegex().IsMatch(html);
        var inStockText = InStockTextRegex().IsMatch(html);
        if (soldOutText) return false;
        if (inStockText) return true;
        return null; // A price alone does not prove availability.
    }

    [GeneratedRegex(@"<(template|style)\b[^>]*>[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HiddenTemplateRegex();
    [GeneratedRegex(@"<(script|style|template)\b[^>]*>[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex NonVisibleContentRegex();
}

public sealed class PriceTrackingWorker(PriceTrackingProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Each track keeps its own interval; this timer only finds newly due tracks.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                await processor.CheckDueTracksAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
