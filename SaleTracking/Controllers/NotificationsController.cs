using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

[Authorize]
public class NotificationsController(IInAppNotificationService notifService) : Controller
{
    private string CurrentUserEmail => User.FindFirstValue(ClaimTypes.Email) ?? "";

    [HttpGet]
    public async Task<IActionResult> Recent()
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        var notifications = await notifService.GetNotificationsAsync(email, 25);
        var unreadCount = await notifService.GetUnreadCountAsync(email);

        return Json(new
        {
            unreadCount,
            notifications = notifications.Select(n => new
            {
                id = n.Id,
                trackingId = n.TrackingId,
                productName = n.ProductName,
                productUrl = n.ProductUrl,
                currentPrice = n.CurrentPrice,
                targetPrice = n.TargetPrice,
                message = n.Message,
                isRead = n.IsRead,
                timeAgo = FormatTimeAgo(n.CreatedAt),
                createdAt = n.CreatedAt.ToString("g")
            })
        });
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> MarkRead([FromBody] MarkReadModel model)
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        await notifService.MarkAsReadAsync(model.Id, email);
        var unreadCount = await notifService.GetUnreadCountAsync(email);
        return Json(new { success = true, unreadCount });
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        await notifService.MarkAllAsReadAsync(email);
        return Json(new { success = true, unreadCount = 0 });
    }

    private static string FormatTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalSeconds < 60) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM dd");
    }

    public class MarkReadModel
    {
        public long Id { get; set; }
    }
}
