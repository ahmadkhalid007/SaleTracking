namespace SaleTracking.Models;

public class TrackingItem
{
    public string TrackingId { get; init; } = "";
    public string OwnerEmail { get; set; } = "";
    public string ProductUrl { get; set; } = "";
    public decimal TargetPrice { get; set; }
    public int CheckIntervalMinutes { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsNotified { get; set; }
    public bool DisableAfterNotification { get; set; }
    public decimal? CurrentPrice { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public string? NotificationMessageSid { get; set; }
    public string? LastCheckError { get; set; }
    public string? LastAlertError { get; set; }
    public string? TargetWhatsAppNumber { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CheckType { get; set; } = "TargetPrice";
    public bool? IsInStock { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    public string Status => IsNotified ? "Alert submitted" : (IsActive ? "Active" : "Disabled");
    public string CheckTypeDisplay => CheckType switch { "InStock" => "In Stock", "Both" => "Price & Stock", _ => "Target Price" };
    public string StockStatusDisplay => IsInStock == true ? "In Stock" : IsInStock == false ? "Out of Stock" : "Pending";
}
