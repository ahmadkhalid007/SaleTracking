using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

[Authorize]
public class DashboardController(ITrackingStore tracks, TrackingSchedule? schedule = null) : Controller
{
    private string CurrentUserEmail => User.FindFirstValue(ClaimTypes.Email)!;

    [HttpGet]
    public IActionResult Index()
    {
        var userTracks = tracks.GetForUser(CurrentUserEmail);
        return View(userTracks);
    }

    [HttpGet]
    public IActionResult TrackedProducts()
    {
        var userTracks = tracks.GetForUser(CurrentUserEmail);
        return View(userTracks);
    }

    [HttpGet]
    public IActionResult Products() => RedirectToAction(nameof(TrackedProducts));

    [HttpGet]
    public IActionResult Details(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return RedirectToAction(nameof(Index));

        var track = tracks.GetById(id, CurrentUserEmail);
        if (track is null)
        {
            TempData["ErrorMessage"] = $"Tracking entry '{id}' could not be found.";
            return RedirectToAction(nameof(Index));
        }

        return View(track);
    }

    [HttpGet]
    public IActionResult Edit(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return RedirectToAction(nameof(Index));

        var track = tracks.GetById(id, CurrentUserEmail);
        if (track is null)
        {
            TempData["ErrorMessage"] = $"Tracking entry '{id}' could not be found.";
            return RedirectToAction(nameof(Index));
        }

        var model = new EditTrackViewModel
        {
            TrackingId = track.TrackingId,
            ProductUrl = track.ProductUrl,
            CheckType = track.CheckType,
            TargetPrice = track.TargetPrice,
            CheckIntervalMinutes = track.CheckIntervalMinutes,
            StartDate = track.StartDate,
            StartTime = track.StartTime,
            EndDate = track.EndDate,
            EndTime = track.EndTime,
            DisableAfterNotification = track.DisableAfterNotification,
            IsActive = track.IsActive
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(string id, EditTrackViewModel model, string? returnUrl = null)
    {
        if (ProductUrlAttribute.TryNormalize(model.ProductUrl, out var productUrl))
            model.ProductUrl = productUrl;
        else
            ModelState.AddModelError(nameof(model.ProductUrl), ProductUrlAttribute.ValidationMessage);

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
        else if (model.EndDate.HasValue && model.EndDate < today)
        {
            ModelState.AddModelError(nameof(model.EndDate), "End date cannot be in the past.");
        }

        if (!ModelState.IsValid)
        {
            model.TrackingId = id;
            return View(model);
        }

        var updated = tracks.Update(id, CurrentUserEmail, model);
        if (!updated)
        {
            TempData["ErrorMessage"] = $"Unable to update tracking entry '{id}'.";
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = $"Tracking entry '{id}' was successfully updated.";
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckNow(string id, [FromServices] PriceTrackingProcessor processor, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return RedirectToAction(nameof(Index));

        var (success, _, message) = await processor.CheckSingleTrackAsync(id, CurrentUserEmail, HttpContext.RequestAborted);
        if (success)
        {
            TempData["SuccessMessage"] = message;
        }
        else
        {
            TempData["ErrorMessage"] = message;
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Toggle(string trackingId, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
            return RedirectToAction(nameof(Index));

        var toggled = tracks.Toggle(trackingId, CurrentUserEmail);
        if (toggled)
        {
            var track = tracks.GetById(trackingId, CurrentUserEmail);
            var state = track?.IsActive == true ? "resumed" : "paused";
            TempData["SuccessMessage"] = $"Tracking '{trackingId}' has been {state}.";
        }
        else
        {
            TempData["ErrorMessage"] = "Could not update tracking status.";
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(string id, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return RedirectToAction(nameof(Index));

        var deleted = tracks.Delete(id, CurrentUserEmail);
        if (deleted)
        {
            TempData["SuccessMessage"] = $"Tracking entry '{id}' was successfully deleted.";
        }
        else
        {
            TempData["ErrorMessage"] = $"Failed to delete tracking entry '{id}'.";
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Index));
    }
}
