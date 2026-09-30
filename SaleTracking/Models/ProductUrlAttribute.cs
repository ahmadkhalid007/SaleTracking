using System.ComponentModel.DataAnnotations;

namespace SaleTracking.Models;

public sealed class ProductUrlAttribute : ValidationAttribute
{
    public const string ValidationMessage = "Enter a valid HTTP or HTTPS product link (for example, https://store.com/product).";

    public ProductUrlAttribute() : base(ValidationMessage) { }

    public override bool IsValid(object? value) => value is null || TryNormalize(value as string, out _);

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.Any(char.IsWhiteSpace) || candidate.Contains('\\')) return false;
        // Add HTTPS only for a missing scheme, never for ftp:, javascript:, etc.
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (candidate.Contains(':') || candidate.StartsWith("//")) return false;
            candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri)) return false;
        }
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown)
            return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
}
