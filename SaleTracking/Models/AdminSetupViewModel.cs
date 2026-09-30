using System.ComponentModel.DataAnnotations;

namespace SaleTracking.Models;

public sealed class AdminSetupViewModel
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";
    [Required, EmailAddress, RegularExpression(AccountFieldRules.EmailPattern, ErrorMessage = AccountFieldRules.EmailMessage)]
    public string Email { get; set; } = "";
    [Required, DataType(DataType.Password), RegularExpression(AccountFieldRules.PasswordPattern, ErrorMessage = AccountFieldRules.PasswordMessage)]
    public string Password { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = "";
}
