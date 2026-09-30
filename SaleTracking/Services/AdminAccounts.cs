using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using SaleTracking.Models;

namespace SaleTracking.Services;

public static class AccountClaims
{
    public static ClaimsPrincipal Create(ApplicationUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name), new(ClaimTypes.Email, user.Email)
        };
        if (user.IsAdmin) claims.Add(new(ClaimTypes.Role, "Admin"));
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

public static class AdminSetupAccess
{
    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)
        && (string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(context.Request.Host.Host, out var host) && IPAddress.IsLoopback(host)))
        && !context.Request.Headers.ContainsKey("Forwarded")
        && !context.Request.Headers.ContainsKey("X-Forwarded-For")
        && !context.Request.Headers.ContainsKey("X-Forwarded-Host");
}

