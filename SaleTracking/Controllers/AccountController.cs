using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

public class AccountController(IAccountStore accounts, IWhatsAppVerificationService? whatsApp = null) : Controller
{
    [HttpGet]
    public IActionResult AdminLogin([FromServices] IAdminAccountStore admin)
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole("Admin"))
            return RedirectToAction("Index", "Settings", null, "whatsapp-sender");

        if (!admin.IsConfigured && AdminSetupAccess.IsLocal(HttpContext))
            return RedirectToAction(nameof(AdminSetup));

        ViewData["IsAdminLogin"] = true;
        return View("Login", new LoginViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminLogin(LoginViewModel model)
    {
        ViewData["IsAdminLogin"] = true;
        if (!ModelState.IsValid) return View("Login", model);

        var user = accounts.FindByEmail(model.Email.Trim());
        if (user is not { IsAdmin: true } || !PasswordSecurity.Verify(model.Password, user.PasswordHash))
        {
            ModelState.AddModelError("", "Admin email or password is incorrect.");
            return View("Login", model);
        }

        await SignIn(user, model.RememberMe);
        return RedirectToAction("Index", "Settings", null, "whatsapp-sender");
    }

    [HttpGet]
    public IActionResult AdminSetup([FromServices] IAdminAccountStore admin)
    {
        if (admin.IsConfigured) return RedirectToAction(nameof(AdminLogin));
        if (!AdminSetupAccess.IsLocal(HttpContext)) return NotFound();
        return View(new AdminSetupViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetup(AdminSetupViewModel model, [FromServices] IAdminAccountStore admin)
    {
        if (admin.IsConfigured || !AdminSetupAccess.IsLocal(HttpContext)) return NotFound();
        if (!ModelState.IsValid) return View(model);
        var user = new ApplicationUser
        {
            Name = model.Name.Trim(),
            Email = model.Email.Trim().ToLowerInvariant(),
            PasswordHash = PasswordSecurity.Hash(model.Password),
            IsAdmin = true,
            IsWhatsAppVerified = true,
            DefaultNotificationPreference = "Both"
        };
        if (!admin.TryCreate(user))
        {
            ModelState.AddModelError("", "Admin setup is already complete or this email is in use. Sign in or choose another email.");
            return View(model);
        }
        await SignIn(user, false);
        return RedirectToAction("Index", "Settings", null, "whatsapp-sender");
    }

    [HttpGet]
    public IActionResult Register() => View(new RegisterViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var email = model.Email.Trim().ToLowerInvariant();
        if (accounts.FindByEmail(email) != null)
        {
            ModelState.AddModelError("Email", "An account with this email already exists.");
            return View(model);
        }

        string phone;
        try
        {
            phone = WhatsAppPhoneNumber.Normalize(model.WhatsAppNumber);
        }
        catch (WhatsAppNotificationException ex)
        {
            ModelState.AddModelError(nameof(model.WhatsAppNumber), ex.Message);
            return View(model);
        }

        var user = new ApplicationUser
        {
            Name = model.Name.Trim(),
            Email = email,
            WhatsAppNumber = phone,
            PasswordHash = PasswordSecurity.Hash(model.Password),
            IsWhatsAppVerified = true,
            DefaultNotificationPreference = "Both"
        };

        if (!accounts.Add(user))
        {
            ModelState.AddModelError("Email", "An account with this email already exists.");
            return View(model);
        }

        await SignIn(user, false);
        TempData["Message"] = "Your account has been created successfully! Welcome to SaleTrack.";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    public IActionResult VerifyWhatsApp(string email)
    {
        var user = accounts.FindByEmail(email.Trim());
        if (user is { IsWhatsAppVerified: true })
            return RedirectToAction("Index", "Dashboard");

        return View(new VerifyWhatsAppViewModel { Email = email });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyWhatsApp(VerifyWhatsAppViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        if (!accounts.TryVerifyWhatsApp(model.Email.Trim(), model.Code, DateTimeOffset.UtcNow))
        {
            ModelState.AddModelError("", "That code is invalid or expired.");
            return View(model);
        }
        var user = accounts.FindByEmail(model.Email.Trim())!;
        await SignIn(user, false);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    public IActionResult Login()
    {
        var email = User.FindFirstValue(ClaimTypes.Email);
        if (User.Identity?.IsAuthenticated == true && email is not null && accounts.FindByEmail(email) is not null)
            return RedirectToAction("Index", User.IsInRole("Admin") ? "Settings" : "Dashboard");
        return View(new LoginViewModel());
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = accounts.FindByEmail(model.Email.Trim());
        if (user == null || !PasswordSecurity.Verify(model.Password, user.PasswordHash))
        {
            ModelState.AddModelError("", "Email or password is incorrect.");
            return View(model);
        }

        await SignIn(user, model.RememberMe);
        return RedirectToAction("Index", user.IsAdmin ? "Settings" : "Dashboard");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    private Task SignIn(ApplicationUser u, bool persistent) =>
        HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AccountClaims.Create(u),
            new AuthenticationProperties { IsPersistent = persistent });
}
