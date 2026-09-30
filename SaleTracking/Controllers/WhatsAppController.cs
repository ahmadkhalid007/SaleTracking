using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

[Authorize(Roles = "Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsAppController(IWhatsAppConnectionService connection) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        Json(await connection.GetAdminStatusAsync(cancellationToken));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconnect(CancellationToken cancellationToken)
    {
        try
        {
            await connection.ReconnectAsync(cancellationToken);
            return Accepted(new { success = true });
        }
        catch (WhatsAppNotificationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        try
        {
            await connection.DisconnectAsync(cancellationToken);
            return Ok(new { success = true });
        }
        catch (WhatsAppNotificationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }
}
