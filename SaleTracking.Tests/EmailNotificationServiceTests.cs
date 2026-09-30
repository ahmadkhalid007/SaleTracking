using System.Net.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SaleTracking.Models;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class EmailNotificationServiceTests
{
    private sealed record SentMessageSnapshot(
        string FromAddress,
        string FromDisplayName,
        string ToAddress,
        string Subject,
        int AlternateViewsCount);

    private sealed class MockSmtpClientWrapper : ISmtpClientWrapper
    {
        public List<SentMessageSnapshot> SentMessages { get; } = [];
        public Exception? ExceptionToThrow { get; set; }

        public Task SendMailAsync(SmtpOptions options, MailMessage message, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            SentMessages.Add(new SentMessageSnapshot(
                message.From?.Address ?? "",
                message.From?.DisplayName ?? "",
                message.To.FirstOrDefault()?.Address ?? "",
                message.Subject ?? "",
                message.AlternateViews.Count));

            return Task.CompletedTask;
        }
    }

    private static (SmtpEmailNotificationService service, MockSmtpClientWrapper wrapper, Mock<ISmtpConfigurationStore> storeMock)
        CreateService(SmtpOptions? options = null)
    {
        var opts = options ?? new SmtpOptions
        {
            Host = "smtp.gmail.com",
            Port = 587,
            EnableSsl = true,
            SenderEmail = "sender@example.com",
            SenderName = "SaleTrack Alerts",
            Password = "abcd efgh ijkl mnop"
        };

        var storeMock = new Mock<ISmtpConfigurationStore>();
        storeMock.Setup(s => s.GetOptions()).Returns(opts);

        var wrapper = new MockSmtpClientWrapper();
        var service = new SmtpEmailNotificationService(
            storeMock.Object,
            NullLogger<SmtpEmailNotificationService>.Instance,
            wrapper);

        return (service, wrapper, storeMock);
    }

    [Theory]
    [InlineData(true, "authentication was rejected")]
    [InlineData(false, "delivery was not confirmed")]
    public async Task FailedSmtpSubmissionGivesActionableSettingsMessage(bool authenticationFailure, string expected)
    {
        var (service, wrapper, _) = CreateService();
        wrapper.ExceptionToThrow = authenticationFailure
            ? new SmtpException(SmtpStatusCode.ClientNotPermitted, "5.7.0 Authentication Required")
            : new SmtpException("Failure sending mail.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendPriceAlertEmailAsync(
            new ApplicationUser { Email = "shopper@example.com", Name = "Shopper" },
            new TrackingItem { ProductUrl = "https://example.com/product", TargetPrice = 100m }, 90m));
        Assert.Contains(expected, error.Message);
        Assert.Contains("Settings", error.Message);
        Assert.Empty(wrapper.SentMessages);
    }

    [Fact]
    public void SmtpOptions_Validation_WorksCorrectly()
    {
        var valid = new SmtpOptions
        {
            Host = "smtp.gmail.com",
            Port = 587,
            SenderEmail = "sender@example.com",
            Password = "app-password"
        };
        Assert.True(valid.IsValid);

        var emptyHost = new SmtpOptions { Host = "", Port = 587, SenderEmail = "sender@example.com", Password = "p" };
        Assert.False(emptyHost.IsValid);

        var zeroPort = new SmtpOptions { Host = "smtp.gmail.com", Port = 0, SenderEmail = "sender@example.com", Password = "p" };
        Assert.False(zeroPort.IsValid);

        var noEmail = new SmtpOptions { Host = "smtp.gmail.com", Port = 587, SenderEmail = "", Password = "p" };
        Assert.False(noEmail.IsValid);

        var noPassword = new SmtpOptions { Host = "smtp.gmail.com", Port = 587, SenderEmail = "sender@example.com", Password = "" };
        Assert.False(noPassword.IsValid);
    }

    [Fact]
    public void IsConfigured_ReturnsTrueOnlyWhenOptionsAreValid()
    {
        var (configuredService, _, _) = CreateService();
        Assert.True(configuredService.IsConfigured);

        var (unconfiguredService, _, _) = CreateService(new SmtpOptions { Host = "", Password = "" });
        Assert.False(unconfiguredService.IsConfigured);
    }

    [Fact]
    public async Task SendPriceAlertEmailAsync_BuildsAndSendsRichEmail()
    {
        var (service, wrapper, _) = CreateService();

        var user = new ApplicationUser
        {
            Name = "John Doe",
            Email = "john@example.com"
        };

        var track = new TrackingItem
        {
            TrackingId = "TRK-TEST1234",
            OwnerEmail = "john@example.com",
            TargetPrice = 5000m,
            ProductUrl = "https://example.com/shoes"
        };

        var messageId = await service.SendPriceAlertEmailAsync(user, track, 4500m);

        Assert.StartsWith("EMAIL-", messageId);
        Assert.Single(wrapper.SentMessages);

        var sent = wrapper.SentMessages[0];
        Assert.Equal("sender@example.com", sent.FromAddress);
        Assert.Equal("SaleTrack Alerts", sent.FromDisplayName);
        Assert.Equal("john@example.com", sent.ToAddress);
        Assert.Contains("Price Drop Alert", sent.Subject);

        // Verify alternate views (plain text + HTML)
        Assert.Equal(2, sent.AlternateViewsCount);
    }

    [Fact]
    public async Task SendPriceAlertEmailAsync_Throws_WhenUserEmailIsInvalid()
    {
        var (service, _, _) = CreateService();
        var user = new ApplicationUser { Name = "No Email", Email = "invalid-email" };
        var track = new TrackingItem { TrackingId = "TRK-01", ProductUrl = "https://example.com", TargetPrice = 100m };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SendPriceAlertEmailAsync(user, track, 90m));
    }

    [Fact]
    public async Task SendPriceAlertEmailAsync_Throws_WhenSmtpNotConfigured()
    {
        var (service, _, _) = CreateService(new SmtpOptions { Host = "", Password = "" });
        var user = new ApplicationUser { Name = "John", Email = "john@example.com" };
        var track = new TrackingItem { TrackingId = "TRK-01", ProductUrl = "https://example.com", TargetPrice = 100m };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendPriceAlertEmailAsync(user, track, 90m));
    }

    [Fact]
    public async Task SendTestEmailAsync_SendsVerificationEmail()
    {
        var (service, wrapper, _) = CreateService();

        await service.SendTestEmailAsync("admin@example.com");

        Assert.Single(wrapper.SentMessages);
        var sent = wrapper.SentMessages[0];
        Assert.Equal("admin@example.com", sent.ToAddress);
        Assert.Contains("Test Email", sent.Subject);
    }

    [Fact]
    public async Task SendTestEmailAsync_Throws_WhenRecipientInvalid()
    {
        var (service, _, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SendTestEmailAsync("not-an-email"));
    }

    [Fact]
    public async Task SendPriceAlertEmailAsync_BuildsAndSendsInStockEmail()
    {
        var (service, wrapper, _) = CreateService();

        var user = new ApplicationUser
        {
            Name = "Alice",
            Email = "alice@example.com"
        };

        var track = new TrackingItem
        {
            TrackingId = "TRK-STOCK1",
            OwnerEmail = "alice@example.com",
            CheckType = "InStock",
            TargetPrice = 0m,
            ProductUrl = "https://example.com/item"
        };

        var messageId = await service.SendPriceAlertEmailAsync(user, track, 3500m);

        Assert.StartsWith("EMAIL-", messageId);
        Assert.Single(wrapper.SentMessages);

        var sent = wrapper.SentMessages[0];
        Assert.Equal("alice@example.com", sent.ToAddress);
        Assert.Contains("Back in Stock Alert", sent.Subject);
        Assert.Equal(2, sent.AlternateViewsCount);
    }

    [Fact]
    public async Task SendPriceAlertEmailAsync_BuildsAndSendsBothEmail()
    {
        var (service, wrapper, _) = CreateService();

        var user = new ApplicationUser
        {
            Name = "Bob",
            Email = "bob@example.com"
        };

        var track = new TrackingItem
        {
            TrackingId = "TRK-BOTH1",
            OwnerEmail = "bob@example.com",
            CheckType = "Both",
            TargetPrice = 4000m,
            ProductUrl = "https://example.com/both-item"
        };

        var messageId = await service.SendPriceAlertEmailAsync(user, track, 3500m);

        Assert.StartsWith("EMAIL-", messageId);
        Assert.Single(wrapper.SentMessages);

        var sent = wrapper.SentMessages[0];
        Assert.Equal("bob@example.com", sent.ToAddress);
        Assert.Contains("Both: Price & Stock Alert", sent.Subject);
        Assert.Equal(2, sent.AlternateViewsCount);
    }
}
