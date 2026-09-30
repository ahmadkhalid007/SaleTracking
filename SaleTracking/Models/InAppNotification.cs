namespace SaleTracking.Models;

public class InAppNotification
{
    public long Id { get; set; }
    public string UserEmail { get; set; } = "";
    public string TrackingId { get; set; } = "";
    public string? ProductName { get; set; }
    public string ProductUrl { get; set; } = "";
    public decimal CurrentPrice { get; set; }
    public decimal TargetPrice { get; set; }
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
