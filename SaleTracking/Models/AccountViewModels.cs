using System.ComponentModel.DataAnnotations;
namespace SaleTracking.Models;
public class RegisterViewModel
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required, EmailAddress]
    [RegularExpression(AccountFieldRules.EmailPattern, ErrorMessage = AccountFieldRules.EmailMessage)]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    [RegularExpression(AccountFieldRules.PasswordPattern, ErrorMessage = AccountFieldRules.PasswordMessage)]
    public string Password { get; set; } = "";

    [Required, Compare(nameof(Password)), DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = "";

    [Required, RegularExpression(AccountFieldRules.PhonePattern, ErrorMessage = AccountFieldRules.PhoneMessage)]
    [Display(Name = "WhatsApp number")]
    public string WhatsAppNumber { get; set; } = "";
}

public class LoginViewModel
{
    [Required, EmailAddress]
    [RegularExpression(AccountFieldRules.EmailPattern, ErrorMessage = AccountFieldRules.EmailMessage)]
    public string Email { get; set; } = "";

    // Strength rules apply when setting a password, so existing users can still sign in.
    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
}

public class VerifyWhatsAppViewModel
{
    [Required, EmailAddress]
    [RegularExpression(AccountFieldRules.EmailPattern, ErrorMessage = AccountFieldRules.EmailMessage)]
    public string Email { get; set; } = "";
    [Required, RegularExpression(@"^\d{6}$", ErrorMessage = "Enter the six-digit code.")]
    public string Code { get; set; } = "";
}

public class CreateTrackViewModel
{
    [Required, ProductUrl, Display(Name = "Product URL")]
    public string ProductUrl { get; set; } = "";

    [Display(Name = "Alert Condition / Check For")]
    public string CheckType { get; set; } = "TargetPrice";

    [Range(1, 999999999), DataType(DataType.Currency), Display(Name = "Target price (Rs.)")]
    public decimal? TargetPrice { get; set; }

    [Required, Range(1, 10080), Display(Name = "Check every")]
    public int CheckIntervalMinutes { get; set; } = 1;

    [Display(Name = "WhatsApp Number for Alerts")]
    [RegularExpression(AccountFieldRules.PhonePattern, ErrorMessage = AccountFieldRules.PhoneMessage)]
    public string? WhatsAppNumber { get; set; }

    [DataType(DataType.Date), Display(Name = "Start date")]
    public DateOnly? StartDate { get; set; }

    [DataType(DataType.Time), Display(Name = "Start time")]
    public TimeOnly? StartTime { get; set; }

    [DataType(DataType.Date), Display(Name = "End date")]
    public DateOnly? EndDate { get; set; }

    [DataType(DataType.Time), Display(Name = "End time")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Disable this track after the first notification")]
    public bool DisableAfterNotification { get; set; }
}

public class EditTrackViewModel
{
    public string TrackingId { get; set; } = "";

    [Required, ProductUrl, Display(Name = "Product URL")]
    public string ProductUrl { get; set; } = "";

    [Display(Name = "Alert Condition / Check For")]
    public string CheckType { get; set; } = "TargetPrice";

    [Range(1, 999999999), DataType(DataType.Currency), Display(Name = "Target price (Rs.)")]
    public decimal? TargetPrice { get; set; }

    [Required, Range(1, 10080), Display(Name = "Check every (minutes)")]
    public int CheckIntervalMinutes { get; set; } = 1;

    [Display(Name = "WhatsApp Number for Alerts")]
    [RegularExpression(AccountFieldRules.PhonePattern, ErrorMessage = AccountFieldRules.PhoneMessage)]
    public string? WhatsAppNumber { get; set; }

    [DataType(DataType.Date), Display(Name = "Start date")]
    public DateOnly? StartDate { get; set; }

    [DataType(DataType.Time), Display(Name = "Start time")]
    public TimeOnly? StartTime { get; set; }

    [DataType(DataType.Date), Display(Name = "End date")]
    public DateOnly? EndDate { get; set; }

    [DataType(DataType.Time), Display(Name = "End time")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Disable this track after the first notification")]
    public bool DisableAfterNotification { get; set; }

    [Display(Name = "Active status")]
    public bool IsActive { get; set; } = true;
}


public class SettingsViewModel
{
    [Required, StringLength(80)]
    [Display(Name = "Full Name")]
    public string Name { get; set; } = "";

    [Required, EmailAddress]
    [RegularExpression(AccountFieldRules.EmailPattern, ErrorMessage = AccountFieldRules.EmailMessage)]
    [Display(Name = "Email Address")]
    public string Email { get; set; } = "";

    [Required, RegularExpression(AccountFieldRules.PhonePattern, ErrorMessage = AccountFieldRules.PhoneMessage)]
    [Display(Name = "WhatsApp Number")]
    public string WhatsAppNumber { get; set; } = "";

    [Required, Range(1, 10080, ErrorMessage = "Interval must be between 1 and 10080 minutes.")]
    [Display(Name = "Default Check Interval (Minutes)")]
    public int DefaultCheckIntervalMinutes { get; set; } = 1;

    [Required]
    [Display(Name = "Default Notification Preference")]
    public string DefaultNotificationPreference { get; set; } = "WhatsApp";

    [DataType(DataType.Password)]
    [Display(Name = "Current Password")]
    public string? CurrentPassword { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    [RegularExpression(AccountFieldRules.PasswordPattern, ErrorMessage = AccountFieldRules.PasswordMessage)]
    public string? NewPassword { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm New Password")]
    public string? ConfirmNewPassword { get; set; }
}
