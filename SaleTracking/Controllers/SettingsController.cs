using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

[Authorize]
public class SettingsController(IAccountStore accounts, ITrackingStore tracks) : Controller
{
    private string CurrentUserEmail => User.FindFirstValue(ClaimTypes.Email) ?? "";

    private ApplicationUser? CurrentUser() => accounts.FindByEmail(CurrentUserEmail);

    [HttpGet]
    public IActionResult Index()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return RedirectToAction("Login", "Account");
        }

        var model = new SettingsViewModel
        {
            Name = user.Name,
            Email = user.Email,
            WhatsAppNumber = user.WhatsAppNumber,
            DefaultCheckIntervalMinutes = user.DefaultCheckIntervalMinutes > 0 ? user.DefaultCheckIntervalMinutes : 1,
            DefaultNotificationPreference = string.IsNullOrWhiteSpace(user.DefaultNotificationPreference) ? "WhatsApp" : user.DefaultNotificationPreference
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SettingsViewModel model, string? returnUrl = null)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return RedirectToAction("Login", "Account");
        }

        var currentEmail = user.Email;

        // Clean and normalize WhatsApp number
        var phone = "";
        try
        {
            phone = WhatsAppPhoneNumber.Normalize(model.WhatsAppNumber);
        }
        catch (WhatsAppNotificationException ex)
        {
            ModelState.AddModelError(nameof(model.WhatsAppNumber), ex.Message);
        }

        // Validate password change only if user provided a new password
        var isChangingPassword = !string.IsNullOrWhiteSpace(model.NewPassword) || !string.IsNullOrWhiteSpace(model.CurrentPassword);
        if (isChangingPassword)
        {
            if (string.IsNullOrWhiteSpace(model.CurrentPassword))
            {
                ModelState.AddModelError(nameof(model.CurrentPassword), "Current password is required to change password.");
            }
            else if (!PasswordSecurity.Verify(model.CurrentPassword, user.PasswordHash))
            {
                ModelState.AddModelError(nameof(model.CurrentPassword), "The current password you entered is incorrect.");
            }

            if ((string.IsNullOrWhiteSpace(model.NewPassword) || !Regex.IsMatch(model.NewPassword, AccountFieldRules.PasswordPattern))
                && ModelState[nameof(model.NewPassword)]?.Errors.Count is not > 0)
            {
                ModelState.AddModelError(nameof(model.NewPassword), AccountFieldRules.PasswordMessage);
            }

            if (model.NewPassword != model.ConfirmNewPassword)
            {
                ModelState.AddModelError(nameof(model.ConfirmNewPassword), "New password and confirmation password do not match.");
            }
        }

        var targetEmail = (model.Email ?? "").Trim().ToLowerInvariant();
        if (!string.Equals(currentEmail, targetEmail, StringComparison.OrdinalIgnoreCase))
        {
            var existingWithEmail = accounts.FindByEmail(targetEmail);
            if (existingWithEmail != null && existingWithEmail.Id != user.Id)
            {
                ModelState.AddModelError(nameof(model.Email), "An account with this email address already exists.");
            }
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Update profile in store
        var profileUpdated = accounts.UpdateProfile(
            currentEmail,
            model.Name.Trim(),
            targetEmail,
            phone,
            model.DefaultCheckIntervalMinutes,
            model.DefaultNotificationPreference
        );

        if (!profileUpdated)
        {
            ModelState.AddModelError(string.Empty, "Unable to save profile changes. Please try again.");
            return View(model);
        }

        // If password was updated
        if (isChangingPassword && !string.IsNullOrWhiteSpace(model.NewPassword))
        {
            var newHash = PasswordSecurity.Hash(model.NewPassword);
            accounts.UpdatePassword(targetEmail, newHash);
        }

        // If email changed, migrate tracking records
        if (!string.Equals(currentEmail, targetEmail, StringComparison.OrdinalIgnoreCase))
        {
            tracks.MigrateUserTracks(currentEmail, targetEmail);
        }

        // Refresh authentication cookie with updated claims
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AccountClaims.Create(accounts.FindByEmail(targetEmail)!),
            new AuthenticationProperties { IsPersistent = true }
        );

        TempData["SuccessMessage"] = "Your settings and preferences have been updated successfully.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ChangePassword(string currentPassword, string newPassword, string confirmNewPassword, string? returnUrl = null)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return RedirectToAction("Login", "Account");
        }

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            TempData["ErrorMessage"] = "Current password is required to change password.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }

        if (!PasswordSecurity.Verify(currentPassword, user.PasswordHash))
        {
            TempData["ErrorMessage"] = "The current password you entered is incorrect.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(newPassword) || !Regex.IsMatch(newPassword, AccountFieldRules.PasswordPattern))
        {
            TempData["ErrorMessage"] = AccountFieldRules.PasswordMessage;
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }

        if (newPassword != confirmNewPassword)
        {
            TempData["ErrorMessage"] = "New password and confirmation password do not match.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }

        var newHash = PasswordSecurity.Hash(newPassword);
        accounts.UpdatePassword(user.Email, newHash);

        TempData["SuccessMessage"] = "Your password has been changed successfully.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }
}
