using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SaleTracking.Models;
using SaleTracking.Services;
using Microsoft.Data.Sqlite;

namespace SaleTracking.Tests;

public sealed class AdminAccountTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saletrack-admin-tests-" + Guid.NewGuid());
    private string AccountFile => Path.Combine(directory, "saletracking.db");
    private SqliteAccountStore OpenStore()
    {
        var database = new SqliteDatabase(AccountFile);
        database.Initialize();
        return new SqliteAccountStore(database);
    }

    [Fact]
    public void AdminSurvivesRestartWithSameIdentityAndUpdatedCredentials()
    {
        var accounts = OpenStore();
        var user = Admin();
        Assert.True(accounts.TryCreate(user));
        Assert.False(accounts.TryCreate(Admin()));
        Assert.True(accounts.UpdateProfile(user.Email, "Updated", "new@example.com", "+923001234567", 30, "WhatsApp"));
        var hash = PasswordSecurity.Hash("Replacement123!");
        Assert.True(accounts.UpdatePassword("new@example.com", hash));
        var restarted = OpenStore();
        Assert.Null(restarted.FindByEmail(user.Email));
        var restored = Assert.IsType<ApplicationUser>(restarted.FindByEmail("new@example.com"));
        Assert.Equal(user.Id, restored.Id);
        Assert.True(restored.IsAdmin);
        Assert.Equal("Updated", restored.Name);
        Assert.Equal(30, restored.DefaultCheckIntervalMinutes);
        Assert.True(PasswordSecurity.Verify("Replacement123!", restored.PasswordHash));
        Assert.DoesNotContain("Replacement123!", restored.PasswordHash);
        Assert.True(AccountClaims.Create(restored).IsInRole("Admin"));
    }

    [Fact]
    public void RegistrationAndProfileChangesCannotAcquireOrReplaceAdmin()
    {
        var accounts = OpenStore();
        var admin = Admin();
        Assert.False(accounts.Add(admin));
        Assert.True(accounts.TryCreate(admin));
        Assert.False(accounts.Add(new ApplicationUser { Email = admin.Email.ToUpperInvariant() }));
        Assert.True(accounts.Add(new ApplicationUser { Email = "customer@example.com" }));
        Assert.False(accounts.UpdateProfile("customer@example.com", "Stolen", admin.Email, "", 1, "WhatsApp"));
        Assert.False(accounts.UpdateProfile(admin.Email, "Changed", "customer@example.com", "", 1, "WhatsApp"));
        Assert.False(AccountClaims.Create(accounts.FindByEmail("customer@example.com")!).IsInRole("Admin"));
        Assert.Equal(admin.Id, accounts.FindByEmail(admin.Email)!.Id);
    }

    [Fact]
    public void ConcurrentFirstSetupCreatesOnlyOneAdmin()
    {
        var accounts = OpenStore();
        var successes = 0;
        Parallel.For(0, 8, _ => { if (accounts.TryCreate(Admin())) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
    }

    [Fact]
    public void DamagedFileDoesNotReopenAdminSetup()
    {
        Directory.CreateDirectory(directory);
        var legacyFile = Path.Combine(directory, "admin.json");
        File.WriteAllText(legacyFile, "{}");
        Assert.Throws<InvalidDataException>(() => new SqliteDatabase(AccountFile).Initialize(legacyFile));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", false, true)]
    [InlineData("::1", "localhost", false, true)]
    [InlineData("192.168.1.20", "localhost", false, false)]
    [InlineData("127.0.0.1", "public.example.com", false, false)]
    [InlineData("127.0.0.1", "localhost", true, false)]
    public void SetupIsRestrictedToDirectLocalRequests(string ip, string host, bool forwarded, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Host = new HostString(host);
        if (forwarded) context.Request.Headers["X-Forwarded-For"] = "192.168.1.20";
        Assert.Equal(expected, AdminSetupAccess.IsLocal(context));
    }

    private static ApplicationUser Admin() => new()
    {
        Name = "Admin", Email = "admin@example.com", IsAdmin = true, IsWhatsAppVerified = true,
        PasswordHash = PasswordSecurity.Hash("Initial123!")
    };

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
