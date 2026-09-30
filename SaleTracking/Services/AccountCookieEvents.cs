using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SaleTracking.Services;

// Validate the account identity and administrator role against the current store.
public sealed class AccountCookieEvents(IAccountStore accounts) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/WhatsApp"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        return base.RedirectToLogin(context);
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
        var accountId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = string.IsNullOrWhiteSpace(email) ? null : accounts.FindByEmail(email);
        if (user is null || !user.IsWhatsAppVerified || accountId != user.Id.ToString()
            || context.Principal!.IsInRole("Admin") != user.IsAdmin)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
