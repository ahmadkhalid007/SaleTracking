namespace SaleTracking.Models;

public class ApplicationUser
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string WhatsAppNumber { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsWhatsAppVerified { get; set; }
    public bool IsAdmin { get; init; }
    public string? VerificationCode { get; set; }
    public DateTimeOffset? CodeExpiresAt { get; set; }
    public int DefaultCheckIntervalMinutes { get; set; } = 1;
    public string DefaultNotificationPreference { get; set; } = "WhatsApp"; // "InApp", "WhatsApp", "Email", "Both", "All"
}
