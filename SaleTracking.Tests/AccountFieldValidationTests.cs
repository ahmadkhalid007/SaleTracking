using System.ComponentModel.DataAnnotations;
using SaleTracking.Models;

namespace SaleTracking.Tests;

public class AccountFieldValidationTests
{
    [Theory]
    [InlineData("name@gmail.com", true)]
    [InlineData("name@yahoo.com", true)]
    [InlineData("name+alerts@shop.example.co.uk", true)]
    [InlineData("namegmail.com", false)]
    [InlineData("name@gmail", false)]
    [InlineData("@gmail.com", false)]
    [InlineData("name@@gmail.com", false)]
    [InlineData("name@g mail.com", false)]
    [InlineData("name@-gmail.com", false)]
    [InlineData("name..surname@gmail.com", false)]
    public void EmailRequiresACompleteAddressAcrossProviders(string value, bool valid)
    {
        Assert.Equal(valid, ValidProperty(new RegisterViewModel(), nameof(RegisterViewModel.Email), value));
        Assert.Equal(valid, ValidProperty(new LoginViewModel(), nameof(LoginViewModel.Email), value));
        Assert.Equal(valid, ValidProperty(new SettingsViewModel(), nameof(SettingsViewModel.Email), value));
        Assert.Equal(valid, ValidProperty(new AdminSetupViewModel(), nameof(AdminSetupViewModel.Email), value));
    }

    [Theory]
    [InlineData("Password123!", true)]
    [InlineData("ABC12345@", true)]
    [InlineData("password123!", false)]
    [InlineData("Password123", false)]
    [InlineData("Password!!!", false)]
    [InlineData("Ab1!", false)]
    [InlineData("Password123 ", false)]
    [InlineData("Password123é", false)]
    public void NewPasswordsRequireLengthCapitalDigitAndSpecialCharacter(string value, bool valid)
    {
        Assert.Equal(valid, ValidProperty(new RegisterViewModel(), nameof(RegisterViewModel.Password), value));
        Assert.Equal(valid, ValidProperty(new SettingsViewModel(), nameof(SettingsViewModel.NewPassword), value));
    }

    [Theory]
    [InlineData("03001234567", true)]
    [InlineData("+923001234567", true)]
    [InlineData("923001234567", true)]
    [InlineData("+92 300-1234567", true)]
    [InlineData("0300abc4567", false)]
    [InlineData("+923001234567abc", false)]
    [InlineData("0300", false)]
    [InlineData("++923001234567", false)]
    public void PhoneFieldsRejectLettersAndInvalidFormats(string value, bool valid)
    {
        Assert.Equal(valid, ValidProperty(new RegisterViewModel(), nameof(RegisterViewModel.WhatsAppNumber), value));
        Assert.Equal(valid, ValidProperty(new SettingsViewModel(), nameof(SettingsViewModel.WhatsAppNumber), value));
        Assert.Equal(valid, ValidProperty(new CreateTrackViewModel(), nameof(CreateTrackViewModel.WhatsAppNumber), value));
    }

    [Fact]
    public void ExistingPasswordsCanStillBeUsedToLogInAndPasswordChangeIsOptional()
    {
        Assert.True(ValidProperty(new LoginViewModel(), nameof(LoginViewModel.Password), "legacy-password"));
        Assert.True(ValidProperty(new SettingsViewModel(), nameof(SettingsViewModel.NewPassword), null));
        Assert.False(ValidProperty(new RegisterViewModel(), nameof(RegisterViewModel.Password), null));
    }

    private static bool ValidProperty(object model, string member, object? value) =>
        Validator.TryValidateProperty(value, new ValidationContext(model) { MemberName = member }, []);
}
