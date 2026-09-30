namespace SaleTracking.Models;

public static class AccountFieldRules
{
    // Complete addresses are accepted across providers, including subdomains and aliases.
    public const string EmailPattern = @"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,63}$";
    public const string EmailMessage = "Enter a complete email address, for example name@gmail.com.";

    public const string PasswordPattern = """^(?=.*[A-Z])(?=.*[0-9])(?=.*[!@#$%^&*()_+\-=\[\]{};:'",.<>/?\\|`~]).{8,100}$""";
    public const string PasswordMessage = "Use 8–100 characters with at least one capital letter, one number, and one special character (such as !, @, or #).";

    public const string PhonePattern = @"^(?:\+[1-9](?:[ -]*[0-9]){7,14}|92(?:[ -]*[0-9]){10}|03(?:[ -]*[0-9]){9})$";
    public const string PhoneMessage = "Enter a valid phone number using digits, for example 03001234567 or +923001234567. Letters are not allowed.";
}
