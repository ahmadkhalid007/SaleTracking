using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "SaleTrack Alerts";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Host) &&
        Port > 0 &&
        !string.IsNullOrWhiteSpace(SenderEmail) &&
        !string.IsNullOrWhiteSpace(Password);
}

public interface ISmtpClientWrapper
{
    Task SendMailAsync(SmtpOptions options, MailMessage message, CancellationToken cancellationToken);
}

public sealed class DefaultSmtpClientWrapper : ISmtpClientWrapper
{
    public async Task SendMailAsync(SmtpOptions options, MailMessage message, CancellationToken cancellationToken)
    {
        var username = string.IsNullOrWhiteSpace(options.Username) ? options.SenderEmail : options.Username;

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username.Trim(), options.Password.Replace(" ", "").Trim()),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 15000 // 15 seconds
        };

        await client.SendMailAsync(message, cancellationToken);
    }
}

public interface IEmailNotificationService
{
    bool IsConfigured { get; }
    SmtpOptions CurrentOptions { get; }
    Task<string> SendPriceAlertEmailAsync(ApplicationUser user, TrackingItem track, decimal currentPrice, CancellationToken cancellationToken = default);
    Task SendTestEmailAsync(string recipientEmail, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailNotificationService(
    ISmtpConfigurationStore configStore,
    ILogger<SmtpEmailNotificationService> logger,
    ISmtpClientWrapper? clientWrapper = null) : IEmailNotificationService
{
    private readonly ISmtpClientWrapper client = clientWrapper ?? new DefaultSmtpClientWrapper();

    public SmtpOptions CurrentOptions => configStore.GetOptions();

    public bool IsConfigured
    {
        get
        {
            var opts = CurrentOptions;
            return opts.IsValid;
        }
    }

    public async Task<string> SendPriceAlertEmailAsync(
        ApplicationUser user, TrackingItem track, decimal currentPrice, CancellationToken cancellationToken = default)
    {
        var options = CurrentOptions;
        if (!options.IsValid)
        {
            throw new InvalidOperationException(
                "SMTP email alerts are not configured. Please configure your Google SMTP and App Password in Settings.");
        }

        if (string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@'))
        {
            throw new ArgumentException("The user does not have a valid email address.", nameof(user));
        }

        var messageId = $"EMAIL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var (subject, htmlBody, plainTextBody) = BuildPriceAlertEmailContent(user, track, currentPrice, messageId);

        using var mail = new MailMessage
        {
            From = new MailAddress(options.SenderEmail.Trim(), options.SenderName.Trim()),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8
        };
        mail.To.Add(new MailAddress(user.Email.Trim(), user.Name.Trim()));

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(plainTextBody, Encoding.UTF8, "text/plain"));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, Encoding.UTF8, "text/html"));

        try
        {
            await client.SendMailAsync(options, mail, cancellationToken);
            logger.LogInformation("Sent price alert email {MessageId} to {Recipient} for track {TrackingId}",
                messageId, user.Email, track.TrackingId);
            return messageId;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to send price alert email to {Recipient} for track {TrackingId}",
                user.Email, track.TrackingId);
            var detail = ex is SmtpException smtp &&
                (smtp.StatusCode == SmtpStatusCode.ClientNotPermitted || smtp.Message.Contains("5.7", StringComparison.Ordinal))
                ? "SMTP authentication was rejected. Check the sender address, username, App Password and TLS settings in Settings."
                : "Email delivery was not confirmed. Check the SMTP host, port, TLS settings and connection in Settings; the next check will retry.";
            throw new InvalidOperationException(detail, ex);
        }
    }

    public async Task SendTestEmailAsync(string recipientEmail, CancellationToken cancellationToken = default)
    {
        var options = CurrentOptions;
        if (!options.IsValid)
        {
            throw new InvalidOperationException(
                "SMTP is not configured. Provide SMTP Host, Port, Sender Email, and your Google App Password.");
        }

        if (string.IsNullOrWhiteSpace(recipientEmail) || !recipientEmail.Contains('@'))
        {
            throw new ArgumentException("Please enter a valid recipient email address.", nameof(recipientEmail));
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(options.SenderEmail.Trim(), options.SenderName.Trim()),
            Subject = "✅ SaleTrack Test Email — Google SMTP & Authenticator Setup Verified!",
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };
        mail.To.Add(new MailAddress(recipientEmail.Trim()));

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px; color: #1e293b; }
                .card { max-width: 560px; margin: 0 auto; background: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); }
                .header { background: linear-gradient(135deg, #2563eb, #1d4ed8); padding: 28px 24px; text-align: center; color: #ffffff; }
                .content { padding: 28px 24px; }
                .status-box { background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 8px; padding: 16px; margin: 20px 0; color: #166534; font-weight: 500; }
                .footer { border-top: 1px solid #e2e8f0; padding: 16px 24px; font-size: 12px; color: #64748b; text-align: center; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="header">
                  <h1 style="margin: 0; font-size: 22px; font-weight: 700;">SaleTrack Alerts</h1>
                  <p style="margin: 6px 0 0 0; opacity: 0.9; font-size: 14px;">Email Configuration Verification</p>
                </div>
                <div class="content">
                  <h2 style="margin-top: 0; font-size: 18px; color: #0f172a;">SMTP Connection Successful!</h2>
                  <p>Congratulations! Your Google SMTP configuration with App Password authentication is working properly.</p>
                  <div class="status-box">
                    ✓ Google Authenticator App Password Verified<br>
                    ✓ Connected to {{options.Host}}:{{options.Port}}<br>
                    ✓ SSL/TLS Encryption Active
                  </div>
                  <p>Your users will now receive immediate email notifications whenever their tracked products meet their target prices.</p>
                </div>
                <div class="footer">
                  Sent from SaleTrack Admin • {{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}} UTC
                </div>
              </div>
            </body>
            </html>
            """;

        var plain = $"""
            SaleTrack Alerts - SMTP Verification
            ====================================

            Your Google SMTP configuration is working properly!
            - Host: {options.Host}:{options.Port}
            - Sender: {options.SenderEmail}
            - SSL/TLS: Enabled
            - Verified at: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            """;

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(plain, Encoding.UTF8, "text/plain"));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));

        await client.SendMailAsync(options, mail, cancellationToken);
    }

    private static (string subject, string html, string plain) BuildPriceAlertEmailContent(
        ApplicationUser user, TrackingItem track, decimal currentPrice, string messageId)
    {
        var targetStr = track.TargetPrice.ToString("N2", CultureInfo.InvariantCulture);
        var currentStr = currentPrice.ToString("N2", CultureInfo.InvariantCulture);
        var diff = track.TargetPrice - currentPrice;

        var isStockOnly = string.Equals(track.CheckType, "InStock", StringComparison.OrdinalIgnoreCase);
        var isBoth = string.Equals(track.CheckType, "Both", StringComparison.OrdinalIgnoreCase);

        string subject;
        string badgeText;
        string heroTitle;
        string heroSubtitle;
        string priceCardHtml;
        string plainText;

        if (isStockOnly)
        {
            subject = $"📦 Back in Stock Alert: Rs. {currentStr} - SaleTrack";
            badgeText = "Stock Alert Triggered";
            heroTitle = "Product Back in Stock!";
            heroSubtitle = $"Good news, {WebUtility.HtmlEncode(user.Name)}! A product you're monitoring is now back in stock and ready to buy.";
            priceCardHtml = $"""
                <div style="margin-bottom: 12px;">
                  <div class="price-label">Current Price</div>
                  <div class="price-val current">Rs. {currentStr}</div>
                </div>
                <div>
                  <div class="price-label">Stock Status</div>
                  <div class="price-val" style="color: #10b981; font-size: 18px;">● In Stock</div>
                </div>
                """;
            plainText = $"""
                🎉 STOCK ALERT: Product Back in Stock!
                ======================================

                Hi {user.Name},

                Great news! Your tracked product is now back in stock and ready to buy.

                Current Price: Rs. {currentStr}
                Stock Status:  In Stock

                Product Link:
                {track.ProductUrl}

                Tracking ID: {track.TrackingId}
                Alert ID:    {messageId}
                Timestamp:   {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

                ---
                SaleTrack - Automated Price Monitoring
                """;
        }
        else if (isBoth)
        {
            subject = $"🔥📦 [Both: Price & Stock Alert] Target Met & Back in Stock: Rs. {currentStr} (Target: Rs. {targetStr}) - SaleTrack";
            badgeText = "Both: Price & Stock Alert";
            heroTitle = "Both: Back in Stock & Target Price Reached!";
            heroSubtitle = $"Good news, {WebUtility.HtmlEncode(user.Name)}! Both conditions are met: your tracked product is BOTH back in stock AND has reached your target price threshold.";
            var savingsNote = diff > 0
                ? $"<div class=\"savings\">You save Rs. {diff.ToString("N2", CultureInfo.InvariantCulture)} below your target!</div>"
                : "";
            priceCardHtml = $"""
                <div style="margin-bottom: 8px;">
                  <span style="display:inline-block;background:#e0e7ff;color:#3730a3;font-size:11px;font-weight:700;padding:3px 10px;border-radius:12px;text-transform:uppercase;letter-spacing:0.04em;">Both Criteria Met</span>
                </div>
                <div style="margin-bottom: 12px;">
                  <div class="price-label">Current Price</div>
                  <div class="price-val current">Rs. {currentStr}</div>
                </div>
                <div style="margin-bottom: 8px;">
                  <div class="price-label">Your Target Threshold</div>
                  <div class="price-val">Rs. {targetStr}</div>
                </div>
                <div>
                  <div class="price-label">Stock Status</div>
                  <div class="price-val" style="color: #10b981; font-size: 16px;">● In Stock</div>
                </div>
                {savingsNote}
                """;
            plainText = $"""
                🎉 BOTH: PRICE & STOCK ALERT!
                =============================

                Hi {user.Name},

                Great news! Both conditions are met: your tracked product is BOTH back in stock and has reached your target price of Rs. {targetStr}.

                Current Price: Rs. {currentStr}
                Target Price:  Rs. {targetStr}
                Stock Status:  In Stock

                Product Link:
                {track.ProductUrl}

                Tracking ID: {track.TrackingId}
                Alert ID:    {messageId}
                Timestamp:   {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

                ---
                SaleTrack - Automated Price Monitoring
                """;
        }
        else
        {
            subject = $"🔥 Price Drop Alert: Rs. {currentStr} (Target: Rs. {targetStr}) - SaleTrack";
            badgeText = "Price Alert Triggered";
            heroTitle = "Target Price Reached!";
            heroSubtitle = $"Good news, {WebUtility.HtmlEncode(user.Name)}! A product you're monitoring just dropped in price.";
            var savingsNote = diff > 0
                ? $"<div class=\"savings\">You save Rs. {diff.ToString("N2", CultureInfo.InvariantCulture)} below your target!</div>"
                : "";
            priceCardHtml = $"""
                <div style="margin-bottom: 12px;">
                  <div class="price-label">Current Price</div>
                  <div class="price-val current">Rs. {currentStr}</div>
                </div>
                <div>
                  <div class="price-label">Your Target Threshold</div>
                  <div class="price-val">Rs. {targetStr}</div>
                </div>
                {savingsNote}
                """;
            plainText = $"""
                🎉 SALE ALERT: Target Price Reached!
                ====================================

                Hi {user.Name},

                Great news! Your tracked product has reached your target price of Rs. {targetStr}.

                Current Price: Rs. {currentStr}
                Target Price:  Rs. {targetStr}

                Product Link:
                {track.ProductUrl}

                Tracking ID: {track.TrackingId}
                Alert ID:    {messageId}
                Timestamp:   {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

                ---
                SaleTrack - Automated Price Monitoring
                """;
        }

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9; margin: 0; padding: 24px; color: #1e293b; }
                .container { max-width: 580px; margin: 0 auto; background: #ffffff; border-radius: 16px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.08); }
                .hero { background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 32px 24px; text-align: center; color: #ffffff; }
                .badge { display: inline-block; background: rgba(255, 255, 255, 0.2); backdrop-filter: blur(4px); padding: 4px 12px; border-radius: 9999px; font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 8px; }
                .body { padding: 32px 28px; }
                .price-card { background: #f8fafc; border: 2px dashed #cbd5e1; border-radius: 12px; padding: 20px; text-align: center; margin: 24px 0; }
                .price-row { display: flex; justify-content: space-around; align-items: center; margin-bottom: 8px; }
                .price-label { font-size: 13px; color: #64748b; font-weight: 500; text-transform: uppercase; }
                .price-val { font-size: 24px; font-weight: 800; color: #0f172a; }
                .price-val.current { color: #10b981; font-size: 32px; }
                .savings { display: inline-block; background: #dcfce7; color: #166534; font-weight: 700; font-size: 14px; padding: 6px 16px; border-radius: 20px; margin-top: 10px; }
                .btn { display: block; width: 100%; box-sizing: border-box; background: #2563eb; color: #ffffff !important; text-decoration: none; font-weight: 700; font-size: 16px; padding: 14px 20px; border-radius: 10px; text-align: center; margin: 24px 0 16px 0; }
                .url-box { word-break: break-all; font-size: 12px; color: #64748b; background: #f8fafc; padding: 10px; border-radius: 6px; }
                .meta { font-size: 12px; color: #94a3b8; border-top: 1px solid #f1f5f9; padding-top: 16px; margin-top: 24px; }
                .footer { background: #f8fafc; border-top: 1px solid #e2e8f0; padding: 20px 24px; font-size: 12px; color: #64748b; text-align: center; line-height: 1.5; }
              </style>
            </head>
            <body>
              <div class="container">
                <div class="hero">
                  <div class="badge">{{badgeText}}</div>
                  <h1 style="margin: 0; font-size: 26px; font-weight: 800;">{{heroTitle}}</h1>
                  <p style="margin: 8px 0 0 0; font-size: 15px; opacity: 0.95;">{{heroSubtitle}}</p>
                </div>

                <div class="body">
                  <div class="price-card">
                    {{priceCardHtml}}
                  </div>

                  <a href="{{WebUtility.HtmlEncode(track.ProductUrl)}}" class="btn" target="_blank" rel="noopener noreferrer">
                    View Product & Buy Now →
                  </a>

                  <div class="url-box">
                    <strong>Product URL:</strong><br>
                    <a href="{{WebUtility.HtmlEncode(track.ProductUrl)}}" style="color: #2563eb;">{{WebUtility.HtmlEncode(track.ProductUrl)}}</a>
                  </div>

                  <div class="meta">
                    <strong>Tracking Reference:</strong> {{messageId}} • <strong>Tracker ID:</strong> {{track.TrackingId}}<br>
                    <strong>Recorded At:</strong> {{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}} UTC
                  </div>
                </div>

                <div class="footer">
                  You are receiving this notification because you subscribed to price alerts on SaleTrack.<br>
                  You can manage your notification preferences anytime in your Account Settings.
                </div>
              </div>
            </body>
            </html>
            """;

        return (subject, html, plainText);
    }
}
