using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class WhatsAppWebOptions
{
    public bool Enabled { get; set; } = true;
    public string BridgeUrl { get; set; } = "http://127.0.0.1:3210";
    public string ApiKey { get; set; } = "";
    public string TokenFile { get; set; } = "../.local/whatsapp/bridge-token";
    public bool AutoStart { get; set; } = true;
    public string BridgeDirectory { get; set; } = "../WhatsAppBridge";
    public string DataDirectory { get; set; } = "../.local/whatsapp";
    public string NodeExecutable { get; set; } = "";

    public bool HasValidAddress => Uri.TryCreate(BridgeUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";
}

public sealed record WhatsAppConnectionStatus(string State, bool Connected = false, string? SenderNumber = null);
public sealed record WhatsAppAdminStatus(string State, bool Connected = false, string? SenderNumber = null, string? Qr = null);

public interface IWhatsAppConnectionService
{
    Task<WhatsAppAdminStatus> GetAdminStatusAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IWhatsAppNotificationService
{
    bool IsConfigured { get; }
    Task<WhatsAppConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default);
    Task<string> SendPriceAlertAsync(ApplicationUser user, TrackingItem track, decimal currentPrice, CancellationToken cancellationToken = default);
}

public sealed class WhatsAppNotificationException(string message) : Exception(message);

public static class WhatsAppPhoneNumber
{
    public static string Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new WhatsAppNotificationException("Update your WhatsApp number in Settings using the international format (for example, +923001234567).");

        var normalized = phone.Trim().Replace(" ", "").Replace("-", "");
        if (Regex.IsMatch(normalized, "^03[0-9]{9}$")) normalized = "+92" + normalized[1..];
        else if (Regex.IsMatch(normalized, "^92[0-9]{10}$")) normalized = "+" + normalized;
        if (!Regex.IsMatch(normalized, "^\\+[1-9][0-9]{7,14}$"))
            throw new WhatsAppNotificationException("Update your WhatsApp number in Settings using the international format (for example, +923001234567).");
        return normalized;
    }
}

public sealed class WhatsAppWebNotificationService(
    HttpClient httpClient,
    IOptions<WhatsAppWebOptions> options,
    ILogger<WhatsAppWebNotificationService> logger) : IWhatsAppNotificationService, IWhatsAppConnectionService
{
    public bool IsConfigured => options.Value.Enabled && options.Value.HasValidAddress && ReadApiKey().Length >= 32;

    private string ReadApiKey()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey)) return options.Value.ApiKey.Trim();
        try { return File.ReadAllText(options.Value.TokenFile).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, options.Value.BridgeUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadApiKey());
        return request;
    }

    public async Task<WhatsAppConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return new(options.Value.Enabled ? "setup_required" : "disabled");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = Request(HttpMethod.Get, "/api/status");
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return new("unavailable");
            var status = await response.Content.ReadFromJsonAsync<WhatsAppConnectionStatus>(timeout.Token);
            return status is { Connected: true, State: "ready" } ? status : new(status?.State ?? "unavailable");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new("offline");
        }
    }

    public async Task<WhatsAppAdminStatus> GetAdminStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return new(options.Value.Enabled ? "setup_required" : "disabled");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var request = Request(HttpMethod.Get, "/api/connection");
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new("bridge_outdated");
            if (!response.IsSuccessStatusCode) return new("unavailable");
            var status = await response.Content.ReadFromJsonAsync<WhatsAppAdminStatus>(timeout.Token);
            if (status is null) return new("unavailable");
            if (status is { Connected: true, State: "ready" }) return status with { Qr = null };
            // QR is a PNG data URL only; never relay arbitrary URLs or markup to the browser.
            var qr = status.State == "qr" && status.Qr is { Length: < 120000 } value
                && Regex.IsMatch(value, @"\Adata:image/png;base64,[A-Za-z0-9+/]+=*\z") ? status.Qr : null;
            return new(status.State, Qr: qr);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new("offline");
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new WhatsAppNotificationException("WhatsApp is starting or needs setup. Try again shortly.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = Request(HttpMethod.Post, "/api/reconnect");
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new WhatsAppNotificationException("WhatsApp could not reconnect. Please try again shortly.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException)
        {
            throw new WhatsAppNotificationException("WhatsApp is unavailable. Check that the application has finished starting.");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new WhatsAppNotificationException("WhatsApp is unavailable. Refresh its status and try again.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = Request(HttpMethod.Post, "/api/disconnect");
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new WhatsAppNotificationException("Restart SaleTrack and its WhatsApp service to enable disconnect, then try again.");
            if (!response.IsSuccessStatusCode)
                throw new WhatsAppNotificationException("WhatsApp could not disconnect. Refresh its status and try again.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException)
        {
            throw new WhatsAppNotificationException("Disconnect was not confirmed. Refresh WhatsApp status before trying again.");
        }
    }

    public async Task<string> SendPriceAlertAsync(ApplicationUser user, TrackingItem track, decimal currentPrice, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new WhatsAppNotificationException("Ask the administrator to connect the WhatsApp sender in Settings before sending alerts.");
        if (!string.Equals(user.Email, track.OwnerEmail, StringComparison.OrdinalIgnoreCase))
            throw new WhatsAppNotificationException("The track owner could not be verified.");

        var phoneToUse = !string.IsNullOrWhiteSpace(user.WhatsAppNumber) ? user.WhatsAppNumber : track.TargetWhatsAppNumber;
        var phone = WhatsAppPhoneNumber.Normalize(phoneToUse);

        string message;
        if (string.Equals(track.CheckType, "InStock", StringComparison.OrdinalIgnoreCase))
        {
            var pricePart = currentPrice > 0
                ? $"Current price: Rs. {currentPrice.ToString("N2", CultureInfo.InvariantCulture)}.\n"
                : "";
            message = $"📦 *[Stock Alert]*: Good news! Your tracked product is now back in stock!\n"
                + pricePart
                + $"Check it out here: {track.ProductUrl}\nTracking ID: {track.TrackingId}";
        }
        else if (string.Equals(track.CheckType, "Both", StringComparison.OrdinalIgnoreCase))
        {
            message = $"🔥📦 *[Both: Price & Stock Alert]*: Good news! Both conditions are met: your tracked product is BOTH back in stock AND has reached your target price of Rs. {track.TargetPrice.ToString("N2", CultureInfo.InvariantCulture)}!\n"
                + $"Current price: Rs. {currentPrice.ToString("N2", CultureInfo.InvariantCulture)}.\n"
                + $"Check it out here: {track.ProductUrl}\nTracking ID: {track.TrackingId}";
        }
        else
        {
            message = $"🎯 *[Price Alert]*: Good news! Your tracked product has reached your target price of Rs. {track.TargetPrice.ToString("N2", CultureInfo.InvariantCulture)}.\n"
                + $"Current price: Rs. {currentPrice.ToString("N2", CultureInfo.InvariantCulture)}.\n"
                + $"Check it out here: {track.ProductUrl}\nTracking ID: {track.TrackingId}";
        }
        try
        {
            using var request = Request(HttpMethod.Post, "/api/messages");
            request.Content = JsonContent.Create(new
            {
                to = phone, message,
                idempotencyKey = $"{track.TrackingId}:{track.CreatedAt.UtcTicks}"
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<BridgeResult>(cancellationToken);
            if (!response.IsSuccessStatusCode || result is not { Success: true } || string.IsNullOrWhiteSpace(result.MessageId))
            {
                var error = result?.Code switch
                {
                    "not_on_whatsapp" or "invalid_number" => "Update your registered WhatsApp number in Settings; the current number cannot receive WhatsApp messages.",
                    "send_uncertain" or "recipient_changed" => "A previous WhatsApp send needs review. Check the business account's chat before creating another alert.",
                    "disconnected" => "Business WhatsApp is disconnected. An administrator can reconnect it in Settings; this alert will retry at a later check.",
                    _ => "The local WhatsApp service could not submit the alert. Check its connection and configuration."
                };
                throw new WhatsAppNotificationException(error);
            }
            logger.LogInformation("WhatsApp Web accepted alert for track {TrackingId}", track.TrackingId);
            return result.MessageId;
        }
        catch (WhatsAppNotificationException)
        {
            logger.LogWarning("WhatsApp Web could not submit alert for track {TrackingId}", track.TrackingId);
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogWarning("WhatsApp connection unavailable for track {TrackingId}", track.TrackingId);
            throw new WhatsAppNotificationException("The local WhatsApp service is unavailable or did not confirm the send. The next check will reuse the same alert reference to prevent duplicate submissions.");
        }
    }

    private sealed record BridgeResult(bool Success, string? MessageId, string? Code);
}
