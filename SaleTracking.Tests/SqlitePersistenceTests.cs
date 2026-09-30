using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saletrack-sqlite-" + Guid.NewGuid());
    private string FilePath => Path.Combine(directory, "test.db");

    private (SqliteDatabase database, SqliteAccountStore accounts, SqliteTrackingStore tracks) Open()
    {
        var database = new SqliteDatabase(FilePath);
        database.Initialize();
        return (database, new SqliteAccountStore(database), new SqliteTrackingStore(database));
    }

    [Fact]
    public void RegistrationVerificationAndProfileSurviveNewStoreInstances()
    {
        var (_, accounts, _) = Open();
        var user = Customer();
        user.VerificationCode = "123456";
        user.CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        user.IsWhatsAppVerified = false;
        Assert.True(accounts.Add(user));
        var restarted = Open().accounts;
        Assert.Equal(user.Id, restarted.FindByEmail(user.Email.ToUpperInvariant())!.Id);
        Assert.False(restarted.TryVerifyWhatsApp(user.Email, "000000", DateTimeOffset.UtcNow));
        Assert.True(restarted.TryVerifyWhatsApp(user.Email, "123456", DateTimeOffset.UtcNow));
        Assert.False(restarted.TryVerifyWhatsApp(user.Email, "123456", DateTimeOffset.UtcNow));
        var verified = Open().accounts.FindByEmail(user.Email)!;
        Assert.True(verified.IsWhatsAppVerified);
        Assert.Null(verified.VerificationCode);
        Assert.Null(verified.CodeExpiresAt);
        Assert.True(accounts.UpdateProfile(user.Email, "Saved Name", "renamed@example.com", "+923111234567", 15, "Both"));
        var saved = Open().accounts.FindByEmail("renamed@example.com")!;
        Assert.Equal(user.Id, saved.Id);
        Assert.Equal("Saved Name", saved.Name);
        Assert.Equal("+923111234567", saved.WhatsAppNumber);
        Assert.Equal(15, saved.DefaultCheckIntervalMinutes);
        Assert.Equal("Both", saved.DefaultNotificationPreference);
        Assert.True(PasswordSecurity.Verify("DatabaseTest123!", saved.PasswordHash));
    }

    [Fact]
    public void ExpiredOrMissingVerificationExpiryCannotVerifyAnAccount()
    {
        var (_, accounts, _) = Open();
        var expired = Customer();
        expired.IsWhatsAppVerified = false;
        expired.VerificationCode = "123456";
        expired.CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        accounts.Add(expired);
        Assert.False(accounts.TryVerifyWhatsApp(expired.Email, "123456", DateTimeOffset.UtcNow));
        Assert.False(Open().accounts.FindByEmail(expired.Email)!.IsWhatsAppVerified);
    }

    [Fact]
    public void TrackFieldsMoneyAndAlertReceiptSurviveRestart()
    {
        var (_, accounts, tracks) = Open();
        var user = Customer();
        accounts.Add(user);
        var model = Model();
        model.TargetPrice = 12345678.123456789m;
        model.StartDate = new DateOnly(2026, 9, 22);
        model.EndDate = new DateOnly(2026, 10, 5);
        model.DisableAfterNotification = true;
        var track = tracks.Create(user.Email, model);
        var checkedAt = DateTimeOffset.UtcNow.AddTicks(127);
        tracks.RecordPriceCheck(track.TrackingId, 99999.99999999m, checkedAt);
        tracks.RecordAlertFailure(track.TrackingId, "Temporary connection failure");
        Assert.Equal("Temporary connection failure", Open().tracks.GetById(track.TrackingId, user.Email)!.LastAlertError);
        tracks.MarkNotified(track.TrackingId, checkedAt, "confirmed-message-id");
        var stored = Open().tracks.GetById(track.TrackingId, user.Email)!;
        Assert.Equal(model.TargetPrice, stored.TargetPrice);
        Assert.Equal(99999.99999999m, stored.CurrentPrice);
        Assert.Equal(track.CreatedAt.UtcTicks, stored.CreatedAt.UtcTicks);
        Assert.Equal(checkedAt, stored.LastCheckedAt);
        Assert.Equal(model.StartDate, stored.StartDate);
        Assert.Equal(model.EndDate, stored.EndDate);
        Assert.Equal(model.WhatsAppNumber, stored.TargetWhatsAppNumber);
        Assert.True(stored.IsNotified);
        Assert.False(stored.IsActive);
        Assert.Null(stored.LastAlertError);
        Assert.Equal("confirmed-message-id", stored.NotificationMessageSid);
        Assert.Equal(checkedAt, stored.NotifiedAt);
    }

    [Fact]
    public void EmailChangeRetainsOwnershipAndDuplicateEmailRollsBack()
    {
        var (_, accounts, tracks) = Open();
        accounts.Add(Customer());
        accounts.Add(Customer("other@example.com"));
        var track = tracks.Create("customer@example.com", Model());
        Assert.False(accounts.UpdateProfile("customer@example.com", "Changed", "OTHER@example.com", "", 5, "WhatsApp"));
        Assert.Equal("Test Customer", accounts.FindByEmail("customer@example.com")!.Name);
        Assert.True(accounts.UpdateProfile("customer@example.com", "Changed", "renamed@example.com", "+923001234567", 5, "WhatsApp"));
        // Stable owner ID makes this atomic with the single user row update.
        Assert.Null(tracks.GetById(track.TrackingId, "customer@example.com"));
        Assert.Equal("renamed@example.com", Open().tracks.GetById(track.TrackingId, "renamed@example.com")!.OwnerEmail);
        Assert.Empty(tracks.GetForUser("other@example.com"));
        Assert.False(tracks.Delete(track.TrackingId, "other@example.com"));
        Assert.False(tracks.Toggle(track.TrackingId, "other@example.com"));
        Assert.False(tracks.Update(track.TrackingId, "other@example.com", Edit(track)));
    }

    [Fact]
    public void DueScheduleAndExpiredPauseAreSaved()
    {
        var (_, accounts, tracks) = Open();
        accounts.Add(Customer());
        var track = tracks.Create("customer@example.com", Model());
        Assert.Empty(tracks.GetDueTracks(track.CreatedAt.AddMinutes(5).AddTicks(-1)));
        Assert.Single(tracks.GetDueTracks(track.CreatedAt.AddMinutes(5)));
        tracks.RecordPriceCheck(track.TrackingId, null, track.CreatedAt.AddMinutes(5), "No price");
        Assert.Empty(Open().tracks.GetDueTracks(track.CreatedAt.AddMinutes(9)));
        Assert.Single(tracks.GetDueTracks(track.CreatedAt.AddMinutes(10)));
        var edit = Edit(track);
        edit.EndDate = DateOnly.FromDateTime(track.CreatedAt.UtcDateTime).AddDays(-1);
        tracks.Update(track.TrackingId, track.OwnerEmail, edit);
        Assert.Empty(tracks.GetDueTracks(track.CreatedAt.AddMinutes(10)));
        Assert.False(Open().tracks.GetById(track.TrackingId, track.OwnerEmail)!.IsActive);
    }

    [Fact]
    public void EditingPausingAndDeletingPersistAndOrphansCannotBeCreated()
    {
        var (_, accounts, tracks) = Open();
        Assert.Throws<InvalidOperationException>(() => tracks.Create("missing@example.com", Model()));
        accounts.Add(Customer());
        var track = tracks.Create("customer@example.com", Model());
        var edit = Edit(track);
        edit.ProductUrl = "https://example.com/updated";
        edit.TargetPrice = 333.99m;
        edit.CheckIntervalMinutes = 60;
        Assert.True(tracks.Update(track.TrackingId, track.OwnerEmail, edit));
        Assert.True(tracks.Toggle(track.TrackingId, track.OwnerEmail));
        var stored = Open().tracks.GetById(track.TrackingId, track.OwnerEmail)!;
        Assert.False(stored.IsActive);
        Assert.Equal(edit.ProductUrl, stored.ProductUrl);
        Assert.Equal(edit.TargetPrice, stored.TargetPrice);
        Assert.Equal(60, stored.CheckIntervalMinutes);
        Assert.True(tracks.Delete(track.TrackingId, track.OwnerEmail));
        Assert.Empty(Open().tracks.GetForUser(track.OwnerEmail));
    }

    [Fact]
    public void LegacyAdministratorIsImportedOnceAndNeverOverwritesDatabaseChanges()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy-admin.json");
        var admin = new ApplicationUser { Email = "admin@example.com", Name = "Original Admin", IsAdmin = true,
            IsWhatsAppVerified = true, PasswordHash = PasswordSecurity.Hash("ImportedAdmin123!") };
        File.WriteAllText(path, JsonSerializer.Serialize(admin));
        var database = new SqliteDatabase(FilePath);
        database.Initialize(path);
        var accounts = new SqliteAccountStore(database);
        Assert.Equal(admin.Id, accounts.FindByEmail(admin.Email)!.Id);
        Assert.True(accounts.IsConfigured);
        accounts.UpdatePassword(admin.Email, PasswordSecurity.Hash("ChangedAdmin123!"));
        // A stale or damaged former file must not reset the new account on the next startup.
        File.WriteAllText(path, "broken old file");
        new SqliteDatabase(FilePath).Initialize(path);
        Assert.True(PasswordSecurity.Verify("ChangedAdmin123!", Open().accounts.FindByEmail(admin.Email)!.PasswordHash));
    }

    [Fact]
    public async Task PartialDeliveryReceiptsSurviveRestartAndOnlyFailedChannelRetries()
    {
        var (_, accounts, tracks) = Open();
        var user = Customer();
        user.DefaultNotificationPreference = "Both";
        accounts.Add(user);
        var model = Model();
        model.DisableAfterNotification = true;
        var track = tracks.Create(user.Email, model);
        var scraper = new Mock<IPriceScraper>();
        scraper.SetReturnsDefault(Task.FromResult<decimal?>(100m));
        var whatsApp = new Mock<IWhatsAppNotificationService>();
        whatsApp.SetReturnsDefault(Task.FromResult("WA-SAVED"));
        var email = new Mock<IEmailNotificationService>();
        email.SetupSequence(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP rejected")).ReturnsAsync("EMAIL-SAVED");
        var first = new PriceTrackingProcessor(tracks, accounts, scraper.Object, whatsApp.Object,
            TimeProvider.System, NullLogger<PriceTrackingProcessor>.Instance, email.Object);
        Assert.False((await first.CheckSingleTrackAsync(track.TrackingId, user.Email)).success);
        var restarted = Open();
        var pending = restarted.tracks.GetById(track.TrackingId, user.Email)!;
        Assert.True(pending.IsActive);
        Assert.False(pending.IsNotified);
        Assert.Contains("SMTP rejected", pending.LastAlertError);
        Assert.Equal("WA-SAVED", restarted.tracks.GetNotificationReceipts(track.TrackingId)["WhatsApp"]);
        var second = new PriceTrackingProcessor(restarted.tracks, restarted.accounts, scraper.Object, whatsApp.Object,
            TimeProvider.System, NullLogger<PriceTrackingProcessor>.Instance, email.Object);
        Assert.True((await second.CheckSingleTrackAsync(track.TrackingId, user.Email)).success);
        var done = Open().tracks.GetById(track.TrackingId, user.Email)!;
        Assert.True(done.IsNotified);
        Assert.False(done.IsActive);
        Assert.Null(done.LastAlertError);
        Assert.Contains("EMAIL-SAVED", done.NotificationMessageSid);
        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        restarted.tracks.Delete(track.TrackingId, user.Email);
        Assert.Empty(restarted.tracks.GetNotificationReceipts(track.TrackingId));
    }

    [Fact]
    public void DueDatesUseTheSameTimezoneAsAlertScheduling()
    {
        var (database, accounts, _) = Open();
        var schedule = new TrackingSchedule(TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"));
        var tracks = new SqliteTrackingStore(database, schedule);
        var user = Customer();
        accounts.Add(user);
        var localDate = schedule.Today(DateTimeOffset.UtcNow).AddDays(1);
        var model = Model();
        model.StartDate = localDate;
        var track = tracks.Create(user.Email, model);
        // 01:00 Pakistan is still the previous UTC date.
        var now = new DateTimeOffset(localDate.ToDateTime(new TimeOnly(1, 0)), TimeSpan.FromHours(5));
        Assert.Contains(tracks.GetDueTracks(now), t => t.TrackingId == track.TrackingId);
    }

    [Fact]
    public async Task WorkerDoesNotResendAnAlertAfterRestart()
    {
        var (_, accounts, tracks) = Open();
        var user = Customer();
        accounts.Add(user);
        var track = tracks.Create(user.Email, Model());
        var scraper = new Mock<IPriceScraper>();
        scraper.Setup(s => s.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(100m);
        var sender = new Mock<IWhatsAppNotificationService>();
        sender.Setup(s => s.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync("saved-receipt");
        var first = Processor(tracks, accounts, scraper.Object, sender.Object);
        Assert.True((await first.CheckSingleTrackAsync(track.TrackingId, user.Email)).success);
        var restarted = Open();
        var second = Processor(restarted.tracks, restarted.accounts, scraper.Object, sender.Object);
        Assert.True((await second.CheckSingleTrackAsync(track.TrackingId, user.Email)).success);
        sender.Verify(s => s.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), 100m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("paused")]
    public async Task ChangesDuringPriceFetchAreReadFromDatabaseBeforeSending(string change)
    {
        var (_, accounts, tracks) = Open();
        accounts.Add(Customer());
        var track = tracks.Create("customer@example.com", Model());
        var scraper = new Mock<IPriceScraper>();
        scraper.Setup(s => s.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (change == "paused") tracks.Toggle(track.TrackingId, track.OwnerEmail);
                else { var edit = Edit(track); edit.ProductUrl = "https://example.com/other"; tracks.Update(track.TrackingId, track.OwnerEmail, edit); }
            }).ReturnsAsync(100m);
        var sender = new Mock<IWhatsAppNotificationService>();
        await Processor(tracks, accounts, scraper.Object, sender.Object).CheckSingleTrackAsync(track.TrackingId, track.OwnerEmail);
        sender.Verify(s => s.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ParallelDatabaseWritersDoNotLoseTracksOrPartialProfiles()
    {
        var (_, accounts, tracks) = Open();
        accounts.Add(Customer());
        Parallel.For(0, 16, i =>
        {
            tracks.Create("customer@example.com", Model());
            accounts.UpdateProfile("customer@example.com", "User " + i, "customer@example.com", "+923001234567", 5, "WhatsApp");
        });
        Assert.Equal(16, Open().tracks.GetForUser("customer@example.com").Count);
    }

    private static PriceTrackingProcessor Processor(ITrackingStore tracks, IAccountStore accounts, IPriceScraper scraper, IWhatsAppNotificationService sender) =>
        new(tracks, accounts, scraper, sender, TimeProvider.System, NullLogger<PriceTrackingProcessor>.Instance);

    private static ApplicationUser Customer(string email = "customer@example.com") => new()
    {
        Email = email, Name = "Test Customer", WhatsAppNumber = "+923001234567", IsWhatsAppVerified = true,
        PasswordHash = PasswordSecurity.Hash("DatabaseTest123!")
    };
    private static CreateTrackViewModel Model() => new()
    { ProductUrl = "https://example.com/product", TargetPrice = 5000m, CheckIntervalMinutes = 5, WhatsAppNumber = "+923001234567" };
    private static EditTrackViewModel Edit(TrackingItem track) => new()
    { TrackingId = track.TrackingId, ProductUrl = track.ProductUrl, TargetPrice = track.TargetPrice, CheckIntervalMinutes = track.CheckIntervalMinutes, IsActive = track.IsActive };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
