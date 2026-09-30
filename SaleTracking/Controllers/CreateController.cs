using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

[Authorize]
public class CreateController(
    ITrackingStore tracks,
    IAccountStore accounts,
    IWhatsAppNotificationService whatsApp,
    PriceTrackingProcessor? processor = null,
    TrackingSchedule? schedule = null) : Controller
{
    public IActionResult Index()
    {
        if (TempData.Peek("SuccessMessage") is null)
            return RedirectToAction(nameof(NewSale));
        return View();
    }

    [HttpGet]
    public IActionResult NewSale()
    {
        var user = CurrentUser();
        if (user is null) return RedirectToAction("Login", "Account");
        var pref = (user.DefaultNotificationPreference ?? "WhatsApp").Trim();
        ViewData["WhatsAppConfigured"] = whatsApp.IsConfigured;
        ViewData["HasRegisteredPhone"] = !string.IsNullOrWhiteSpace(user.WhatsAppNumber);
        ViewData["NotificationPreference"] = pref;
        return View(new CreateTrackViewModel
        {
            CheckIntervalMinutes = user.DefaultCheckIntervalMinutes > 0 ? user.DefaultCheckIntervalMinutes : 1,
            WhatsAppNumber = user.WhatsAppNumber
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult NewSale(CreateTrackViewModel model, string? returnUrl = null)
    {
        var user = CurrentUser();
        if (user is null) return RedirectToAction("Login", "Account");

        if (ProductUrlAttribute.TryNormalize(model.ProductUrl, out var productUrl))
            model.ProductUrl = productUrl;
        else
            ModelState.AddModelError(nameof(model.ProductUrl), ProductUrlAttribute.ValidationMessage);

        var pref = (user.DefaultNotificationPreference ?? "WhatsApp").Trim();
        var isWhatsAppRequired = string.Equals(pref, "WhatsApp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pref, "Both", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pref, "All", StringComparison.OrdinalIgnoreCase);

        // Use authenticated owner's account number, or allow filling if account number is empty
        string? phoneCandidate = !string.IsNullOrWhiteSpace(user.WhatsAppNumber) ? user.WhatsAppNumber : model.WhatsAppNumber;
        ModelState.Remove(nameof(model.WhatsAppNumber));
        ViewData["WhatsAppConfigured"] = whatsApp.IsConfigured;
        ViewData["HasRegisteredPhone"] = !string.IsNullOrWhiteSpace(user.WhatsAppNumber);
        ViewData["NotificationPreference"] = pref;

        if (isWhatsAppRequired)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(phoneCandidate))
                {
                    ModelState.AddModelError(nameof(model.WhatsAppNumber), "A WhatsApp phone number is required to receive WhatsApp alerts.");
                }
                else
                {
                    var normalizedPhone = WhatsAppPhoneNumber.Normalize(phoneCandidate);
                    model.WhatsAppNumber = normalizedPhone;
                }
            }
            catch (WhatsAppNotificationException ex)
            {
                ModelState.AddModelError(nameof(model.WhatsAppNumber), ex.Message);
            }
        }
        else if (!string.IsNullOrWhiteSpace(phoneCandidate))
        {
            try
            {
                var normalizedPhone = WhatsAppPhoneNumber.Normalize(phoneCandidate);
                model.WhatsAppNumber = normalizedPhone;
            }
            catch
            {
                // Optional for in-app / email users
            }
        }

        var isPriceAlert = !string.Equals(model.CheckType, "InStock", StringComparison.OrdinalIgnoreCase);
        if (isPriceAlert && (!model.TargetPrice.HasValue || model.TargetPrice <= 0))
        {
            ModelState.AddModelError(nameof(model.TargetPrice), "A target price is required for price alerts.");
        }

        var today = (schedule ?? TrackingSchedule.Utc).Today(DateTimeOffset.UtcNow);
        if (model.StartDate.HasValue && model.EndDate.HasValue)
        {
            if (model.EndDate < model.StartDate)
            {
                ModelState.AddModelError(nameof(model.EndDate), "End date must be on or after the start date.");
            }
            else if (model.EndDate == model.StartDate && model.StartTime.HasValue && model.EndTime.HasValue && model.EndTime < model.StartTime)
            {
                ModelState.AddModelError(nameof(model.EndTime), "End time must be on or after the start time.");
            }
        }
        else if (model.EndDate.HasValue && model.EndDate.Value < today)
        {
            ModelState.AddModelError(nameof(model.EndDate), "End date cannot be in the past.");
        }

        if (!ModelState.IsValid) return View(model);

        if (!string.IsNullOrWhiteSpace(model.WhatsAppNumber) && string.IsNullOrWhiteSpace(user.WhatsAppNumber))
        {
            if (!accounts.UpdateProfile(user.Email, user.Name, user.Email, model.WhatsAppNumber,
                user.DefaultCheckIntervalMinutes, user.DefaultNotificationPreference ?? "WhatsApp"))
            {
                ModelState.AddModelError(nameof(model.WhatsAppNumber), "Unable to save your recipient number. Please try again.");
                return View(model);
            }
        }
        var track = tracks.Create(user.Email, model);

        // Immediate price check on creation:
        if (processor is not null)
        {
            try
            {
                var (success, price, message) = Task.Run(() => processor.CheckSingleTrackAsync(track.TrackingId, user.Email)).GetAwaiter().GetResult();
                if (success)
                    TempData["SuccessMessage"] = $"Track created! {message}";
                else
                {
                    TempData["SuccessMessage"] = "Track created. Monitoring is active.";
                    TempData["ErrorMessage"] = message;
                }
            }
            catch
            {
                TempData["SuccessMessage"] = "Track created. Monitoring is active.";
                TempData["ErrorMessage"] = "The initial check could not be completed. It will retry at the next scheduled check.";
            }
        }
        else
        {
            TempData["SuccessMessage"] = "Track created successfully";
        }

        TempData["TrackingId"] = track.TrackingId;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(Index));
    }

    private ApplicationUser? CurrentUser()
    {
        var email = User.FindFirstValue(ClaimTypes.Email);
        if (email is null) return null;

        return accounts.FindByEmail(email);
    }
}
