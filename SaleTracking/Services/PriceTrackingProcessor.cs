using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class PriceTrackingProcessor(
    ITrackingStore tracks,
    IAccountStore accounts,
    IPriceScraper scraper,
    IWhatsAppNotificationService notifications,
    TimeProvider timeProvider,
    ILogger<PriceTrackingProcessor> logger,
    IEmailNotificationService? emailNotifications = null,
    IInAppNotificationService? inAppNotifications = null,
    TrackingSchedule? schedule = null)
{
    // Manual checks and the background worker share this singleton processor.
    private readonly SemaphoreSlim checkLock = new(1, 1);

    public async Task<(bool success, decimal? price, string message)> CheckSingleTrackAsync(
        string trackingId, string ownerEmail, CancellationToken cancellationToken = default)
    {
        await checkLock.WaitAsync(cancellationToken);
        try
        {
            var track = tracks.GetById(trackingId, ownerEmail);
            return track is null
                ? (false, null, $"Tracking entry '{trackingId}' could not be found.")
                : await CheckAsync(track, cancellationToken);
        }
        finally { checkLock.Release(); }
    }

    public async Task CheckDueTracksAsync(CancellationToken cancellationToken)
    {
        await checkLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var track in tracks.GetDueTracks(timeProvider.GetUtcNow()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tracks.GetById(track.TrackingId, track.OwnerEmail) is not null && CanAlert(track))
                    await CheckAsync(track, cancellationToken);
            }
        }
        finally { checkLock.Release(); }
    }

    private bool CanAlert(TrackingItem track) =>
        (schedule ?? TrackingSchedule.Utc).CanAlert(track, timeProvider.GetUtcNow());

    private async Task<(bool success, decimal? price, string message)> CheckAsync(
        TrackingItem track, CancellationToken cancellationToken)
    {
        var productUrl = track.ProductUrl;
        decimal? price;
        bool? inStock;
        string? checkError = null;
        try
        {
            if (scraper is IProductScraper productScraper)
            {
                var scrapeResult = await productScraper.ScrapeProductAsync(productUrl, cancellationToken);
                price = scrapeResult.Price;
                inStock = scrapeResult.IsInStock;
                checkError = scrapeResult.Error;
            }
            else
            {
                price = await scraper.GetCurrentPriceAsync(productUrl, cancellationToken);
                inStock = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A product can be edited or deleted while an HTTP fetch is in flight.
            var latest = tracks.GetById(track.TrackingId, track.OwnerEmail);
            if (latest is null || latest.ProductUrl != productUrl)
                return (false, null, "This tracker changed during the price check. Please check it again.");
            track = latest;
            if (price is <= 0) price = null;
            var needsPrice = !string.Equals(track.CheckType, "InStock", StringComparison.OrdinalIgnoreCase);
            var needsStock = string.Equals(track.CheckType, "InStock", StringComparison.OrdinalIgnoreCase)
                || string.Equals(track.CheckType, "Both", StringComparison.OrdinalIgnoreCase);
            if (needsPrice && price is null || needsStock && inStock is null)
                checkError ??= needsPrice && price is null
                    ? "Could not read a valid product price. Will retry at the next scheduled check."
                    : "Could not determine stock for the selected product or size. Will retry at the next scheduled check.";
            else checkError = null;
            tracks.RecordPriceCheck(track.TrackingId, price, inStock, timeProvider.GetUtcNow(), checkError);
            if (checkError is not null) return (false, price, checkError);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            tracks.RecordPriceCheck(track.TrackingId, null, null, timeProvider.GetUtcNow(),
                "The price check failed. Will retry at the next scheduled check.");
            logger.LogWarning(ex, "Price check failed for {TrackingId}", track.TrackingId);
            return (false, null, "Price check failed. Will retry at the next scheduled interval.");
        }

        bool shouldAlert = false;
        string alertHeadline = "";
        string inAppMessage = "";
        var checkType = string.IsNullOrWhiteSpace(track.CheckType) ? "TargetPrice" : track.CheckType;

        if (string.Equals(checkType, "InStock", StringComparison.OrdinalIgnoreCase))
        {
            if (inStock == true)
            {
                shouldAlert = true;
                alertHeadline = price.HasValue
                    ? $"Price checked: Rs. {price.Value:N2}! Product is now back in stock and"
                    : "Product is now back in stock and";
                inAppMessage = price.HasValue
                    ? $"[Stock Alert] Product is back in stock! Current price: Rs. {price.Value:N2}."
                    : "[Stock Alert] Product is now back in stock and available to buy!";
            }
        }
        else if (string.Equals(checkType, "Both", StringComparison.OrdinalIgnoreCase))
        {
            if (price.HasValue && price.Value <= track.TargetPrice && inStock == true)
            {
                shouldAlert = true;
                alertHeadline = $"Price checked: Rs. {price.Value:N2}! Target threshold met, product in stock and";
                inAppMessage = $"[Both: Price & Stock] Both conditions met! Product is back in stock AND target price reached (Target: Rs. {track.TargetPrice:N2}, Current: Rs. {price.Value:N2})!";
            }
        }
        else
        {
            if (price.HasValue && price.Value <= track.TargetPrice)
            {
                shouldAlert = true;
                alertHeadline = $"Price checked: Rs. {price.Value:N2}! Target threshold met and";
                inAppMessage = $"[Price Alert] Target threshold met! Current price is Rs. {price.Value:N2} (Target: Rs. {track.TargetPrice:N2}).";
            }
        }

        if (shouldAlert && !track.IsNotified && CanAlert(track))
        {
            var user = accounts.FindByEmail(track.OwnerEmail);
            if (user is null)
            {
                var errorMsg = "The owner account is unavailable. No alert was sent.";
                tracks.RecordAlertFailure(track.TrackingId, errorMsg);
                return (false, price, $"Price checked: {(price.HasValue ? $"Rs. {price.Value:N2}" : "available")}. Target reached, but the alert was not confirmed. {errorMsg}");
            }

            var preference = (user.DefaultNotificationPreference ?? "WhatsApp").Trim();
            var shouldSendInApp = string.Equals(preference, "InApp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference, "All", StringComparison.OrdinalIgnoreCase);
            var shouldSendWhatsApp = string.Equals(preference, "WhatsApp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference, "Both", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference, "All", StringComparison.OrdinalIgnoreCase);
            var shouldSendEmail = string.Equals(preference, "Email", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference, "Both", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference, "All", StringComparison.OrdinalIgnoreCase);

            if (!shouldSendInApp && !shouldSendWhatsApp && !shouldSendEmail)
            {
                shouldSendWhatsApp = true;
            }

            var receipts = tracks.GetNotificationReceipts(track.TrackingId);
            string? inAppMessageId = receipts.GetValueOrDefault("InApp");
            string? whatsAppMessageId = receipts.GetValueOrDefault("WhatsApp");
            string? emailMessageId = receipts.GetValueOrDefault("Email");
            string? inAppError = null;
            string? whatsAppError = null;
            string? emailError = null;

            if (shouldSendInApp && inAppMessageId is null && inAppNotifications is not null)
            {
                try
                {
                    var msg = inAppMessage;
                    var notifId = await inAppNotifications.CreateNotificationAsync(
                        user.Email,
                        track.TrackingId,
                        null,
                        track.ProductUrl,
                        price ?? 0m,
                        track.TargetPrice,
                        msg,
                        cancellationToken);
                    inAppMessageId = $"INAPP-{notifId}";
                    tracks.RecordNotificationReceipt(track.TrackingId, "InApp", inAppMessageId);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    inAppError = ex.Message;
                    logger.LogWarning(ex, "In-App notification failed for {TrackingId}", track.TrackingId);
                }
            }
            if (shouldSendInApp && inAppMessageId is null && inAppNotifications is null)
                inAppError = "In-App notifications are unavailable.";

            if (shouldSendWhatsApp && whatsAppMessageId is null)
            {
                try
                {
                    var userForWhatsApp = user;
                    if (string.IsNullOrWhiteSpace(userForWhatsApp.WhatsAppNumber) && !string.IsNullOrWhiteSpace(track.TargetWhatsAppNumber))
                    {
                        userForWhatsApp.WhatsAppNumber = track.TargetWhatsAppNumber;
                    }

                    whatsAppMessageId = await notifications.SendPriceAlertAsync(userForWhatsApp, track, price.GetValueOrDefault(), cancellationToken);
                    if (string.IsNullOrWhiteSpace(whatsAppMessageId))
                        whatsAppError = "WhatsApp did not confirm the alert submission.";
                    else tracks.RecordNotificationReceipt(track.TrackingId, "WhatsApp", whatsAppMessageId);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    whatsAppError = ex is WhatsAppNotificationException ? ex.Message
                        : "Could not submit the WhatsApp alert. Will retry at the next check.";
                    logger.LogWarning(ex, "WhatsApp alert failed for {TrackingId}", track.TrackingId);
                }
            }

            if (shouldSendEmail && emailMessageId is null && emailNotifications is not null)
            {
                try
                {
                    emailMessageId = await emailNotifications.SendPriceAlertEmailAsync(user, track, price.GetValueOrDefault(), cancellationToken);
                    if (string.IsNullOrWhiteSpace(emailMessageId))
                        emailError = "Email service did not confirm the alert submission.";
                    else tracks.RecordNotificationReceipt(track.TrackingId, "Email", emailMessageId);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    emailError = ex.Message;
                    logger.LogWarning(ex, "Email alert failed for {TrackingId}", track.TrackingId);
                }
            }
            if (shouldSendEmail && emailMessageId is null && emailNotifications is null)
                emailError = "Email notifications are unavailable. Check the SMTP configuration in Settings.";

            var allSucceeded = (!shouldSendInApp || !string.IsNullOrWhiteSpace(inAppMessageId))
                && (!shouldSendWhatsApp || !string.IsNullOrWhiteSpace(whatsAppMessageId))
                && (!shouldSendEmail || !string.IsNullOrWhiteSpace(emailMessageId));

            var descriptions = new List<string>();
            if (shouldSendInApp && !string.IsNullOrWhiteSpace(inAppMessageId)) descriptions.Add("In-App alert posted to website");
            if (shouldSendWhatsApp && !string.IsNullOrWhiteSpace(whatsAppMessageId)) descriptions.Add("WhatsApp alert submitted");
            if (shouldSendEmail && !string.IsNullOrWhiteSpace(emailMessageId)) descriptions.Add($"Email alert sent to {user.Email}");

            if (allSucceeded)
            {
                var messageParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(inAppMessageId)) messageParts.Add(inAppMessageId);
                if (!string.IsNullOrWhiteSpace(whatsAppMessageId)) messageParts.Add(whatsAppMessageId);
                if (!string.IsNullOrWhiteSpace(emailMessageId)) messageParts.Add(emailMessageId);
                var combinedMessageId = string.Join("; ", messageParts);

                tracks.MarkNotified(track.TrackingId, timeProvider.GetUtcNow(), combinedMessageId);

                var alertDescription = string.Join(" and ", descriptions) + ".";
                if (!string.IsNullOrWhiteSpace(inAppError)) alertDescription += $" (In-App error: {inAppError})";
                if (!string.IsNullOrWhiteSpace(whatsAppError)) alertDescription += $" (WhatsApp error: {whatsAppError})";
                if (!string.IsNullOrWhiteSpace(emailError)) alertDescription += $" (Email error: {emailError})";

                return (true, price, $"{alertHeadline} {alertDescription}");
            }
            else
            {
                var errors = new List<string>();
                if (!string.IsNullOrWhiteSpace(inAppError)) errors.Add($"In-App: {inAppError}");
                if (!string.IsNullOrWhiteSpace(whatsAppError)) errors.Add($"WhatsApp error: {whatsAppError}");
                if (!string.IsNullOrWhiteSpace(emailError)) errors.Add($"Email error: {emailError}");
                var failureMessage = errors.Count > 0
                    ? string.Join(" | ", errors)
                    : "Could not submit alerts. Will retry at the next check.";

                tracks.RecordAlertFailure(track.TrackingId, failureMessage);
                var priceLabel = price.HasValue ? $"Rs. {price.Value:N2}" : "Product checked";
                if (descriptions.Count > 0)
                    return (false, price, $"{string.Join(" and ", descriptions)}. Some alerts remain unconfirmed. {failureMessage} Successful channels will not be sent again.");
                return (false, price, $"{priceLabel}. Alert condition met, but the alert was not confirmed. {failureMessage}");
            }
        }

        var status = track.IsNotified ? " An alert has already been submitted for this tracker."
            : !CanAlert(track) ? " Alerts are paused or outside this tracker's schedule." : "";
        var stockNote = inStock == true ? " (In stock)" : inStock == false ? " (Out of stock)" : "";
        var priceSummary = price.HasValue ? $"Rs. {price.Value:N2}" : "Page checked";
        return (true, price, $"Checked: {priceSummary}{stockNote}. Condition: {track.CheckTypeDisplay}.{status}");
    }
}
