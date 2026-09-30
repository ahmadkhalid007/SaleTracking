using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class AlertFlowRegressionTests
{
    [Theory]
    [InlineData("InStock")]
    [InlineData("Both")]
    [InlineData("TargetPrice")]
    public async Task UnreadableProductReturnsFailure(string condition)
    {
        var f = new Fixture(new ProductScrapeResult(null, null, "The store returned HTTP 429."));
        var track = f.Track(condition);
        var result = await f.Processor.CheckSingleTrackAsync(track.TrackingId, f.User.Email);
        Assert.False(result.success);
        Assert.Contains("HTTP 429", result.message);
        Assert.Equal(result.message, track.LastCheckError);
        Assert.False(track.IsNotified);
    }

    [Fact]
    public async Task StockAlertWithoutPriceHasNoFalsePriceError()
    {
        var f = new Fixture(new ProductScrapeResult(null, true));
        var track = f.Track("InStock");
        Assert.True((await f.Processor.CheckSingleTrackAsync(track.TrackingId, f.User.Email)).success);
        Assert.True(track.IsNotified);
        Assert.Null(track.LastCheckError);
    }

    [Theory]
    [InlineData("Both", false, false)]
    [InlineData("TargetPrice", true, true)]
    public void CreationNeverClaimsDeliveryWhenConditionOrSendingFails(string condition, bool stock, bool deliveryFails)
    {
        var f = new Fixture(new ProductScrapeResult(100m, stock));
        if (deliveryFails)
        {
            f.User.DefaultNotificationPreference = "WhatsApp";
            f.WhatsApp.Setup(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WhatsAppNotificationException("Sender disconnected"));
        }
        var controller = f.Controller();
        controller.NewSale(new CreateTrackViewModel { ProductUrl = "https://example.com/product", CheckType = condition, TargetPrice = 100m });
        Assert.False(Assert.Single(f.Tracks.GetForUser(f.User.Email)).IsNotified);
        Assert.DoesNotContain("dispatched", controller.TempData["SuccessMessage"]!.ToString());
        if (deliveryFails) Assert.Contains("Sender disconnected", controller.TempData["ErrorMessage"]!.ToString());
    }

    [Fact]
    public void CreationReportsSuccessfulStockAlertWithoutPrice()
    {
        var f = new Fixture(new ProductScrapeResult(null, true));
        var controller = f.Controller();
        controller.NewSale(new CreateTrackViewModel { ProductUrl = "https://example.com/product", CheckType = "InStock" });
        Assert.Contains("In-App alert posted", controller.TempData["SuccessMessage"]!.ToString());
        Assert.Null(controller.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task InAppSuccessIsNotDuplicatedWhenEmailRetries()
    {
        var f = new Fixture(new ProductScrapeResult(100m, true));
        f.User.DefaultNotificationPreference = "All";
        f.WhatsApp.SetReturnsDefault(Task.FromResult("WA-CONFIRMED"));
        f.Email.SetupSequence(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP rejected")).ReturnsAsync("EMAIL-CONFIRMED");
        var track = f.Track("InStock");
        track.DisableAfterNotification = true;
        Assert.False((await f.Processor.CheckSingleTrackAsync(track.TrackingId, f.User.Email)).success);
        Assert.True(track.IsActive);
        Assert.False(track.IsNotified);
        Assert.True((await f.Processor.CheckSingleTrackAsync(track.TrackingId, f.User.Email)).success);
        Assert.False(track.IsActive);
        Assert.Single(await f.InApp.GetNotificationsAsync(f.User.Email));
        f.WhatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    public void ScheduleUsesPakistanLocalTime(int utcHour, bool expected)
    {
        var schedule = new TrackingSchedule(TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"));
        var track = new TrackingItem { StartDate = new DateOnly(2026, 9, 29), StartTime = new TimeOnly(14, 0) };
        Assert.Equal(expected, schedule.CanAlert(track, new DateTimeOffset(2026, 9, 29, utcHour, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    public void TimeOnlyDailyWindowIsEnforced(int hour, bool expected)
    {
        var track = new TrackingItem { StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(17, 0) };
        Assert.Equal(expected, TrackingSchedule.Utc.CanAlert(track, new DateTimeOffset(2026, 9, 29, hour, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(1, true)]
    [InlineData(12, false)]
    public void OvernightTimeOnlyWindowIsEnforced(int hour, bool expected)
    {
        var track = new TrackingItem { StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(2, 0) };
        Assert.Equal(expected, TrackingSchedule.Utc.CanAlert(track, new DateTimeOffset(2026, 9, 29, hour, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ExistingListenerIsDetectedWithoutStoppingIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains($"port {port}", DevelopmentStartup.FindPortConflict($"http://localhost:{port}"));
        Assert.True(listener.Server.IsBound);
        Assert.Null(DevelopmentStartup.FindPortConflict("http://127.0.0.1:0"));
    }

    private sealed class Fixture
    {
        public InMemoryTrackingStore Tracks { get; } = new();
        public InMemoryAccountStore Accounts { get; } = new();
        public ApplicationUser User { get; } = new() { Email = "audit@example.com", WhatsAppNumber = "+923001234567", DefaultNotificationPreference = "InApp" };
        public Mock<IWhatsAppNotificationService> WhatsApp { get; } = new();
        public Mock<IEmailNotificationService> Email { get; } = new();
        public InMemoryInAppNotificationService InApp { get; } = new();
        public PriceTrackingProcessor Processor { get; }
        public Fixture(ProductScrapeResult result)
        {
            Accounts.Add(User);
            var scraper = new Mock<IProductScraper>();
            scraper.SetReturnsDefault(Task.FromResult(result));
            Processor = new(Tracks, Accounts, scraper.Object, WhatsApp.Object, TimeProvider.System,
                NullLogger<PriceTrackingProcessor>.Instance, Email.Object, InApp);
        }
        public TrackingItem Track(string condition) => Tracks.Create(User.Email,
            new CreateTrackViewModel { ProductUrl = "https://example.com/product", CheckType = condition, TargetPrice = 100m });
        public CreateController Controller()
        {
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Email, User.Email) }, "Test")) };
            return new(Tracks, Accounts, WhatsApp.Object, Processor)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
                TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>())
            };
        }
    }
}
