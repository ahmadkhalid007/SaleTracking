using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class PriceTrackingWorkerTests
{
    private const string MessageSid = "SM11111111111111111111111111111111";

    [Theory]
    [InlineData(499999, true)]
    [InlineData(500000, true)]
    [InlineData(500001, false)]
    public async Task AlertsOnlyAtOrBelowTarget(int priceInPaisa, bool shouldAlert)
    {
        var fixture = new Fixture(priceInPaisa / 100m);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.Equal(priceInPaisa / 100m, fixture.Track.CurrentPrice);
        Assert.Equal(shouldAlert, fixture.Track.IsNotified);
        Assert.Equal(shouldAlert ? MessageSid : null, fixture.Track.NotificationMessageSid);
        fixture.Notifications.Verify(service => service.SendPriceAlertAsync(fixture.User, fixture.Track, priceInPaisa / 100m, It.IsAny<CancellationToken>()),
            shouldAlert ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task ChecksOnlyAfterTheChosenInterval_IncludingFirstCheck()
    {
        var fixture = new Fixture(6000m);
        fixture.Clock.Now = fixture.Track.CreatedAt.AddMinutes(5).AddTicks(-1);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Clock.Now = fixture.Track.CreatedAt.AddMinutes(5);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(4);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendsOneAlertAndHonorsAutoDisable(bool autoDisable)
    {
        var fixture = new Fixture(4500m);
        fixture.Track.DisableAfterNotification = autoDisable;
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.Equal(!autoDisable, fixture.Track.IsActive);
        fixture.Notifications.Verify(service => service.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(autoDisable ? 1 : 2));
    }

    [Fact]
    public async Task FailedSubmissionIsRetriedAtNextInterval_WithoutMarkingNotifiedOrDisabling()
    {
        var fixture = new Fixture(4500m);
        fixture.Track.DisableAfterNotification = true;
        fixture.Notifications.SetupSequence(service => service.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WhatsAppNotificationException("Provider rejected the message."))
            .ReturnsAsync(MessageSid);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.False(fixture.Track.IsNotified);
        Assert.True(fixture.Track.IsActive);
        Assert.NotNull(fixture.Track.LastAlertError);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Notifications.Verify(service => service.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.True(fixture.Track.IsNotified);
        Assert.False(fixture.Track.IsActive);
        Assert.Null(fixture.Track.LastAlertError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MissingOrInvalidPriceDoesNotAlert_AndRespectsInterval(int? price)
    {
        var fixture = new Fixture(price.HasValue ? price.Value : null);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.Null(fixture.Track.CurrentPrice);
        Assert.NotNull(fixture.Track.LastCheckError);
        Assert.Equal(fixture.Clock.Now, fixture.Track.LastCheckedAt);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Notifications.Verify(service => service.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScraperTimeoutDoesNotStopOtherDueTracks()
    {
        var fixture = new Fixture(4500m);
        fixture.Scraper.Setup(service => service.GetCurrentPriceAsync(fixture.Track.ProductUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("HTTP timeout"));
        var second = fixture.Tracks.Create(fixture.User.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/other", TargetPrice = 5000, CheckIntervalMinutes = 5
        });
        fixture.Scraper.Setup(service => service.GetCurrentPriceAsync(second.ProductUrl, It.IsAny<CancellationToken>())).ReturnsAsync(4000m);
        fixture.Clock.Now = second.CreatedAt.AddMinutes(5);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        Assert.NotNull(fixture.Track.LastCheckError);
        Assert.True(second.IsNotified);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("future")]
    [InlineData("expired")]
    public async Task HonorsActivityAndDateRange(string state)
    {
        var fixture = new Fixture(4500m);
        var today = DateOnly.FromDateTime(fixture.Clock.Now.UtcDateTime);
        if (state == "paused") fixture.Track.IsActive = false;
        if (state == "future") fixture.Track.StartDate = today.AddDays(1);
        if (state == "expired") fixture.Track.EndDate = today.AddDays(-1);
        await fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        fixture.Scraper.Verify(service => service.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotMarkAlertSent()
    {
        var fixture = new Fixture(4500m);
        using var cancellation = new CancellationTokenSource();
        fixture.Scraper.Setup(service => service.GetCurrentPriceAsync(fixture.Track.ProductUrl, It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ReturnsAsync(4500m);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.CheckDueTracksAsync(cancellation.Token));
        Assert.False(fixture.Track.IsNotified);
        Assert.Null(fixture.Track.LastCheckedAt);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("paused")]
    public async Task ManualCheckReadsPriceButDoesNotAlertOutsideSchedule(string state)
    {
        var fixture = new Fixture(4500m);
        var today = DateOnly.FromDateTime(fixture.Clock.Now.UtcDateTime);
        if (state == "future") fixture.Track.StartDate = today.AddDays(1);
        if (state == "expired") fixture.Track.EndDate = today.AddDays(-1);
        if (state == "paused") fixture.Track.IsActive = false;
        var result = await fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email);
        Assert.True(result.success);
        Assert.Equal(4500m, fixture.Track.CurrentPrice);
        fixture.Notifications.Verify(n => n.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualAlertFailureIsNotDisplayedAsSuccessOrExposedAsRawException()
    {
        var fixture = new Fixture(4500m);
        fixture.Notifications.Setup(n => n.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Sensitive internal details"));
        var result = await fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email);
        Assert.False(result.success);
        Assert.DoesNotContain("Sensitive", result.message);
        Assert.False(fixture.Track.IsNotified);
        Assert.NotNull(fixture.Track.LastAlertError);
    }

    [Fact]
    public async Task ManualCancellationDoesNotRecordPriceOrSend()
    {
        var fixture = new Fixture(4500m);
        using var cancellation = new CancellationTokenSource();
        fixture.Scraper.Setup(s => s.GetCurrentPriceAsync(fixture.Track.ProductUrl, It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ReturnsAsync(4500m);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email, cancellation.Token));
        Assert.Null(fixture.Track.LastCheckedAt);
        Assert.False(fixture.Track.IsNotified);
    }

    [Fact]
    public async Task DeletingTrackDuringPriceFetchPreventsAlert()
    {
        var fixture = new Fixture(4500m);
        fixture.Scraper.Setup(s => s.GetCurrentPriceAsync(fixture.Track.ProductUrl, It.IsAny<CancellationToken>()))
            .Callback(() => fixture.Tracks.Delete(fixture.Track.TrackingId, fixture.User.Email)).ReturnsAsync(4500m);
        var result = await fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email);
        Assert.False(result.success);
        fixture.Notifications.Verify(n => n.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SimultaneousManualAndScheduledChecksSubmitOneAlert()
    {
        var fixture = new Fixture(4500m);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Notifications.Setup(n => n.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return release.Task; });
        var manual = fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scheduled = fixture.Processor.CheckDueTracksAsync(CancellationToken.None);
        var secondManual = fixture.Processor.CheckSingleTrackAsync(fixture.Track.TrackingId, fixture.User.Email);
        release.SetResult(MessageSid);
        await Task.WhenAll(manual, scheduled, secondManual).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Notifications.Verify(n => n.SendPriceAlertAsync(fixture.User, fixture.Track, 4500m, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class Fixture
    {
        public InMemoryTrackingStore Tracks { get; } = new();
        public ApplicationUser User { get; } = new() { Email = "shopper@example.com", WhatsAppNumber = "+923001234567" };
        public TrackingItem Track { get; }
        public ManualClock Clock { get; } = new();
        public Mock<IPriceScraper> Scraper { get; } = new();
        public Mock<IWhatsAppNotificationService> Notifications { get; } = new();
        public PriceTrackingProcessor Processor { get; }

        public Fixture(decimal? price)
        {
            var accounts = new InMemoryAccountStore();
            accounts.Add(User);
            Track = Tracks.Create(User.Email, new CreateTrackViewModel
            {
                ProductUrl = "https://example.com/product", TargetPrice = 5000, CheckIntervalMinutes = 5
            });
            Clock.Now = Track.CreatedAt.AddMinutes(5);
            Scraper.Setup(service => service.GetCurrentPriceAsync(Track.ProductUrl, It.IsAny<CancellationToken>())).ReturnsAsync(price);
            Notifications.Setup(service => service.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>())).ReturnsAsync(MessageSid);
            Processor = new PriceTrackingProcessor(Tracks, accounts, Scraper.Object, Notifications.Object, Clock, NullLogger<PriceTrackingProcessor>.Instance);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
