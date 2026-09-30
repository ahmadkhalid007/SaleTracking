using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class EmailControllerTests
{
    private static (EmailController controller, Mock<ISmtpConfigurationStore> storeMock, Mock<IEmailNotificationService> serviceMock)
        CreateController()
    {
        var storeMock = new Mock<ISmtpConfigurationStore>();
        var serviceMock = new Mock<IEmailNotificationService>();

        var options = new SmtpOptions
        {
            Host = "smtp.gmail.com",
            Port = 587,
            EnableSsl = true,
            SenderEmail = "admin@example.com",
            SenderName = "SaleTrack Alerts",
            Password = "secret-app-password"
        };

        storeMock.Setup(s => s.GetOptions()).Returns(options);
        serviceMock.Setup(s => s.CurrentOptions).Returns(options);
        serviceMock.Setup(s => s.IsConfigured).Returns(true);

        var controller = new EmailController(
            storeMock.Object,
            serviceMock.Object,
            NullLogger<EmailController>.Instance);

        return (controller, storeMock, serviceMock);
    }

    [Fact]
    public void Status_ReturnsOptionsAndConfiguredState()
    {
        var (controller, _, _) = CreateController();

        var result = controller.Status() as JsonResult;
        Assert.NotNull(result);

        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.True(json.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("smtp.gmail.com", json.GetProperty("host").GetString());
        Assert.Equal(587, json.GetProperty("port").GetInt32());
        Assert.Equal("admin@example.com", json.GetProperty("senderEmail").GetString());
        Assert.True(json.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public void Save_SavesUpdatedOptions()
    {
        var (controller, storeMock, _) = CreateController();

        var model = new SaveSmtpSettingsModel
        {
            Host = "smtp.gmail.com",
            Port = 587,
            EnableSsl = true,
            SenderEmail = "new-sender@gmail.com",
            SenderName = "Custom Alerts",
            Password = "new-password-16"
        };

        var result = controller.Save(model) as OkObjectResult;
        Assert.NotNull(result);

        storeMock.Verify(s => s.SaveOptions(It.Is<SmtpOptions>(o =>
            o.Host == "smtp.gmail.com" &&
            o.SenderEmail == "new-sender@gmail.com" &&
            o.Password == "new-password-16"
        )), Times.Once);
    }

    [Fact]
    public void Save_RetainsExistingPassword_WhenNewPasswordIsBlank()
    {
        var (controller, storeMock, _) = CreateController();

        var model = new SaveSmtpSettingsModel
        {
            Host = "smtp.gmail.com",
            Port = 587,
            EnableSsl = true,
            SenderEmail = "admin@example.com",
            SenderName = "SaleTrack Alerts",
            Password = "" // blank password
        };

        var result = controller.Save(model) as OkObjectResult;
        Assert.NotNull(result);

        storeMock.Verify(s => s.SaveOptions(It.Is<SmtpOptions>(o =>
            o.Password == "secret-app-password"
        )), Times.Once);
    }

    [Fact]
    public async Task Test_SendsTestEmail_WhenValid()
    {
        var (controller, _, serviceMock) = CreateController();

        var model = new SendTestEmailModel { RecipientEmail = "recipient@example.com" };

        var result = await controller.Test(model, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);

        serviceMock.Verify(s => s.SendTestEmailAsync("recipient@example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Test_ReturnsFriendlyMessage_On535Error()
    {
        var (controller, _, serviceMock) = CreateController();

        serviceMock.Setup(s => s.SendTestEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("535 5.7.8 Username and Password not accepted"));

        var model = new SendTestEmailModel { RecipientEmail = "recipient@example.com" };

        var result = await controller.Test(model, CancellationToken.None) as ObjectResult;
        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);

        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("Google SMTP authentication failed (Code 535)", json.GetProperty("message").GetString());
    }
}
