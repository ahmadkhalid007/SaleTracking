using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaleTracking.Services;

public sealed partial class GenericHtmlPriceScraper
{
    private static bool IsShopifyProductPage(Uri uri, string html) =>
        ShopifyProductPathRegex().IsMatch(uri.AbsolutePath) &&
        (html.Contains("Shopify.shop", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("cdn.shopify.com", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("shopify-features", StringComparison.OrdinalIgnoreCase));

    private async Task<ProductScrapeResult?> TryFetchShopifyProductAsync(Uri uri, CancellationToken cancellationToken)
    {
        var path = ShopifyProductPathRegex().Match(uri.AbsolutePath);
        var productUri = new UriBuilder(uri)
        {
            Path = path.Groups["prefix"].Value + "/products/" + path.Groups["handle"].Value + ".js",
            Query = "",
            Fragment = ""
        }.Uri;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, productUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return HttpFailure(productUri, response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var product = document.RootElement;
            if (product.ValueKind != JsonValueKind.Object ||
                !product.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.String ||
                !string.Equals(handle.GetString(), Uri.UnescapeDataString(path.Groups["handle"].Value), StringComparison.Ordinal) ||
                !product.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
                return null;

            var variantId = GetQueryParam(uri.Query, "variant");
            JsonElement? selected = null;
            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object) continue;
                if (!string.IsNullOrWhiteSpace(variantId))
                {
                    if (variant.TryGetProperty("id", out var id) && id.ToString() == variantId)
                    {
                        selected = variant;
                        break;
                    }
                }
                else
                {
                    // Shopify selects the first available variant when no size was supplied.
                    selected ??= variant;
                    if (ReadAvailable(variant) == true)
                    {
                        selected = variant;
                        break;
                    }
                }
            }

            // A removed/invalid variant must never inherit another size's availability.
            if (selected is not { } item)
                return new ProductScrapeResult(null, null, "The selected product variant was not found. Check the size or color in the URL.");
            var price = ReadJsonPrice(item);
            return new ProductScrapeResult(price / 100m, ReadAvailable(item));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "Shopify product data unavailable for {ProductUrl}; checking structured offers.", uri);
            return null;
        }
    }

    private static bool? ReadAvailable(JsonElement item) =>
        item.TryGetProperty("available", out var available) ? available.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;

    private static decimal? ReadJsonPrice(JsonElement item)
    {
        if (!item.TryGetProperty("price", out var price)) return null;
        return price.ValueKind switch
        {
            JsonValueKind.Number when price.TryGetDecimal(out var value) && value > 0 => value,
            JsonValueKind.String => ParsePrice(price.GetString() ?? ""),
            _ => null
        };
    }

    private static ProductScrapeResult? ReadShopifyOffer(Uri uri, string html)
    {
        var variantId = GetQueryParam(uri.Query, "variant");
        var matches = new List<ProductScrapeResult>();
        foreach (Match script in JsonLdScriptRegex().Matches(html))
        {
            try
            {
                using var document = JsonDocument.Parse(script.Groups["json"].Value);
                foreach (var product in EnumerateJsonLdProducts(document.RootElement))
                {
                    if (!product.TryGetProperty("offers", out var offers)) continue;
                    foreach (var offer in EnumerateOffers(offers))
                    {
                        // The offer URL must identify this product AND the selected variant.
                        if (!offer.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                            !Uri.TryCreate(uri, url.GetString(), out var offerUri) || !SameShopifyProduct(uri, offerUri))
                            continue;
                        if (!string.IsNullOrWhiteSpace(variantId) && GetQueryParam(offerUri.Query, "variant") != variantId)
                            continue;
                        bool? stock = null;
                        if (offer.TryGetProperty("availability", out var availability) && availability.ValueKind == JsonValueKind.String)
                        {
                            var status = availability.GetString()!.TrimEnd('/').Split('/').Last();
                            stock = status switch
                            {
                                "InStock" => true,
                                "OutOfStock" or "SoldOut" or "Discontinued" or "BackOrder" => false,
                                _ => null
                            };
                        }
                        matches.Add(new ProductScrapeResult(ReadJsonPrice(offer), stock));
                    }
                }
            }
            catch (JsonException) { /* A malformed third-party script is not a stock signal. */ }
        }

        if (!string.IsNullOrWhiteSpace(variantId)) return matches.FirstOrDefault();
        return matches.FirstOrDefault(result => result.IsInStock == true) ?? matches.FirstOrDefault();
    }

    private static IEnumerable<JsonElement> EnumerateJsonLdProducts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var product in EnumerateJsonLdProducts(child)) yield return product;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out var type) &&
                (type.ValueKind == JsonValueKind.String && type.GetString() == "Product" ||
                 type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "Product")))
                yield return element;
            if (element.TryGetProperty("@graph", out var graph))
                foreach (var product in EnumerateJsonLdProducts(graph)) yield return product;
        }
    }

    private static IEnumerable<JsonElement> EnumerateOffers(JsonElement offers)
    {
        if (offers.ValueKind == JsonValueKind.Object) yield return offers;
        else if (offers.ValueKind == JsonValueKind.Array)
            foreach (var offer in offers.EnumerateArray())
                if (offer.ValueKind == JsonValueKind.Object) yield return offer;
    }

    private static bool SameShopifyProduct(Uri requested, Uri candidate)
    {
        var requestedPath = ShopifyProductPathRegex().Match(requested.AbsolutePath);
        var candidatePath = ShopifyProductPathRegex().Match(candidate.AbsolutePath);
        return requested.Host.Equals(candidate.Host, StringComparison.OrdinalIgnoreCase) && candidatePath.Success &&
            requestedPath.Groups["handle"].Value == candidatePath.Groups["handle"].Value;
    }

    [GeneratedRegex(@"^(?<prefix>.*?)(?:/collections/[^/]+)?/products/(?<handle>[^/]+)/?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ShopifyProductPathRegex();

    [GeneratedRegex("""<script\b[^>]*\btype\s*=\s*["']application/ld\+json["'][^>]*>(?<json>[\s\S]*?)</script\s*>""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex JsonLdScriptRegex();
}
