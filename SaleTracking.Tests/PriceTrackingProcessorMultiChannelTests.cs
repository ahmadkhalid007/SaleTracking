using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Models;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class PriceTrackingProcessorMultiChannelTests
{
    private static (PriceTrackingProcessor processor, InMemoryTrackingStore tracks, InMemoryAccountStore accounts,
        Mock<IPriceScraper> scraper, Mock<IWhatsAppNotificationService> whatsApp, Mock<IEmailNotificationService> email)
        CreateProcessor()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var scraper = new Mock<IPriceScraper>();
        var whatsApp = new Mock<IWhatsAppNotificationService>();
        var email = new Mock<IEmailNotificationService>();

        whatsApp.Setup(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("WA-MSG-001");

        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("EMAIL-MSG-001");

        var processor = new PriceTrackingProcessor(
            tracks, accounts, scraper.Object, whatsApp.Object, TimeProvider.System,
            NullLogger<PriceTrackingProcessor>.Instance, email.Object);

        return (processor, tracks, accounts, scraper, whatsApp, email);
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenPreferenceIsWhatsApp_SendsOnlyWhatsApp()
    {
        var (processor, tracks, accounts, scraper, whatsApp, email) = CreateProcessor();

        var user = new ApplicationUser
        {
            Name = "Shopper",
            Email = "shopper@example.com",
            WhatsAppNumber = "+923001234567",
            DefaultNotificationPreference = "WhatsApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            TargetPrice = 1000m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(950m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Equal(950m, price);
        Assert.Contains("WhatsApp alert submitted", message);

        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 950m, It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);

        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.True(updatedTrack!.IsNotified);
        Assert.Equal("WA-MSG-001", updatedTrack.NotificationMessageSid);
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenPreferenceIsEmail_SendsOnlyEmail()
    {
        var (processor, tracks, accounts, scraper, whatsApp, email) = CreateProcessor();

        var user = new ApplicationUser
        {
            Name = "Email User",
            Email = "emailuser@example.com",
            DefaultNotificationPreference = "Email"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            TargetPrice = 1000m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(800m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Equal(800m, price);
        Assert.Contains("Email alert sent to emailuser@example.com", message);

        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 800m, It.IsAny<CancellationToken>()), Times.Once);
        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);

        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.True(updatedTrack!.IsNotified);
        Assert.Equal("EMAIL-MSG-001", updatedTrack.NotificationMessageSid);
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenPreferenceIsBoth_SendsBothWhatsAppAndEmail()
    {
        var (processor, tracks, accounts, scraper, whatsApp, email) = CreateProcessor();

        var user = new ApplicationUser
        {
            Name = "MultiChannel User",
            Email = "multichannel@example.com",
            WhatsAppNumber = "+923001234567",
            DefaultNotificationPreference = "Both"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            TargetPrice = 2000m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1800m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Equal(1800m, price);
        Assert.Contains("WhatsApp alert submitted and Email alert sent to multichannel@example.com", message);

        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 1800m, It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 1800m, It.IsAny<CancellationToken>()), Times.Once);

        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.True(updatedTrack!.IsNotified);
        Assert.Contains("WA-MSG-001", updatedTrack.NotificationMessageSid);
        Assert.Contains("EMAIL-MSG-001", updatedTrack.NotificationMessageSid);
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenBothConfiguredAndEmailFails_RetriesOnlyEmail()
    {
        var (processor, tracks, accounts, scraper, whatsApp, email) = CreateProcessor();

        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP connection refused"));

        var user = new ApplicationUser
        {
            Name = "Both User",
            Email = "both@example.com",
            WhatsAppNumber = "+923001234567",
            DefaultNotificationPreference = "Both"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            TargetPrice = 500m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(450m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.False(success);
        Assert.Equal(450m, price);
        Assert.Contains("WhatsApp alert submitted", message);
        Assert.Contains("Email error", message);

        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.False(updatedTrack!.IsNotified);
        Assert.Contains("SMTP connection refused", updatedTrack.LastAlertError);
        Assert.Equal("WA-MSG-001", tracks.GetNotificationReceipts(track.TrackingId)["WhatsApp"]);

        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("EMAIL-RECOVERED");
        Assert.True((await processor.CheckSingleTrackAsync(track.TrackingId, user.Email)).success);
        Assert.True(updatedTrack.IsNotified);
        Assert.Null(updatedTrack.LastAlertError);
        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenBothFail_RecordsFailureAndRetries()
    {
        var (processor, tracks, accounts, scraper, whatsApp, email) = CreateProcessor();

        whatsApp.Setup(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WhatsAppNotificationException("Bridge offline"));

        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP auth error"));

        var user = new ApplicationUser
        {
            Name = "Both Fail User",
            Email = "bothfail@example.com",
            WhatsAppNumber = "+923001234567",
            DefaultNotificationPreference = "Both"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            TargetPrice = 500m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(450m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.False(success);
        Assert.Contains("alert was not confirmed", message);

        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.False(updatedTrack!.IsNotified);
        Assert.NotNull(updatedTrack.LastAlertError);
    }
}
