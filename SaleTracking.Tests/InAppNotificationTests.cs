using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class InAppNotificationTests
{
    private static (PriceTrackingProcessor processor, InMemoryTrackingStore tracks, InMemoryAccountStore accounts,
        InMemoryInAppNotificationService inApp, Mock<IPriceScraper> scraper, Mock<IWhatsAppNotificationService> whatsApp, Mock<IEmailNotificationService> email)
        CreateProcessorWithInApp()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var inApp = new InMemoryInAppNotificationService();
        var scraper = new Mock<IPriceScraper>();
        var whatsApp = new Mock<IWhatsAppNotificationService>();
        var email = new Mock<IEmailNotificationService>();

        whatsApp.Setup(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("WA-MSG-001");

        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("EMAIL-MSG-001");

        var processor = new PriceTrackingProcessor(
            tracks, accounts, scraper.Object, whatsApp.Object, TimeProvider.System,
            NullLogger<PriceTrackingProcessor>.Instance, email.Object, inApp);

        return (processor, tracks, accounts, inApp, scraper, whatsApp, email);
    }

    [Fact]
    public async Task InMemoryInAppNotificationService_BasicCrud_WorksCorrectly()
    {
        var service = new InMemoryInAppNotificationService();
        var email = "user@test.com";

        var id = await service.CreateNotificationAsync(email, "trk-1", "Bag", "https://example.com/bag", 4500m, 5000m, "Price met!");
        Assert.True(id > 0);

        var unread = await service.GetUnreadCountAsync(email);
        Assert.Equal(1, unread);

        var list = await service.GetNotificationsAsync(email);
        Assert.Single(list);
        Assert.Equal("trk-1", list[0].TrackingId);
        Assert.False(list[0].IsRead);

        var marked = await service.MarkAsReadAsync(id, email);
        Assert.True(marked);

        unread = await service.GetUnreadCountAsync(email);
        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task SqliteInAppNotificationService_Crud_WorksCorrectly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"saletrack_test_{Guid.NewGuid():N}.db");
        try
        {
            var db = new SqliteDatabase(dbPath);
            db.Initialize();
            var service = new SqliteInAppNotificationService(db);
            var email = "testuser@example.com";

            var notifId = await service.CreateNotificationAsync(email, "trk-sq-1", "Shoes", "https://example.com/shoes", 3000m, 3500m, "Price dropped!");
            Assert.True(notifId > 0);

            var count = await service.GetUnreadCountAsync(email);
            Assert.Equal(1, count);

            var items = await service.GetNotificationsAsync(email);
            Assert.Single(items);
            Assert.Equal("Shoes", items[0].ProductName);
            Assert.Equal(3000m, items[0].CurrentPrice);
            Assert.Equal(3500m, items[0].TargetPrice);
            Assert.False(items[0].IsRead);

            var markResult = await service.MarkAllAsReadAsync(email);
            Assert.True(markResult);

            count = await service.GetUnreadCountAsync(email);
            Assert.Equal(0, count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { /* ignore temp cleanup */ }
            }
        }
    }

    [Fact]
    public async Task CheckSingleTrackAsync_WhenPreferenceIsInApp_CreatesInAppNotificationOnly()
    {
        var (processor, tracks, accounts, inApp, scraper, whatsApp, email) = CreateProcessorWithInApp();

        var user = new ApplicationUser
        {
            Name = "InApp User",
            Email = "inapp@example.com",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 5000m
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4500m);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Equal(4500m, price);
        Assert.Contains("In-App alert posted to website", message);

        var notifs = await inApp.GetNotificationsAsync(user.Email);
        Assert.Single(notifs);
        Assert.Equal(track.TrackingId, notifs[0].TrackingId);
        Assert.Equal(4500m, notifs[0].CurrentPrice);

        // Verify neither WhatsApp nor Email were contacted
        whatsApp.Verify(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
        email.Verify(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify track is marked as notified
        var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
        Assert.NotNull(updatedTrack);
        Assert.True(updatedTrack.IsNotified);
    }

    [Theory]
    [InlineData(5000, 5000, true)]   // Exactly equal to target
    [InlineData(5000, 4999, true)]   // Slightly below target
    [InlineData(5000, 100, true)]    // Far below target ("jitni bhi kam ho")
    [InlineData(5000, 5001, false)]  // Above target -> no alert
    public async Task CheckSingleTrackAsync_TargetPriceThreshold_TriggersAlertAccurately(decimal target, decimal scraped, bool shouldAlert)
    {
        var (processor, tracks, accounts, inApp, scraper, _, _) = CreateProcessorWithInApp();

        var user = new ApplicationUser
        {
            Name = "Threshold User",
            Email = "threshold@example.com",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/target-test",
            TargetPrice = target
        });

        scraper.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scraped);

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        var unread = await inApp.GetUnreadCountAsync(user.Email);

        if (shouldAlert)
        {
            Assert.Equal(1, unread);
            Assert.Contains("Target threshold met", message);
            var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
            Assert.NotNull(updatedTrack);
            Assert.True(updatedTrack.IsNotified);
        }
        else
        {
            Assert.Equal(0, unread);
            var updatedTrack = tracks.GetById(track.TrackingId, user.Email);
            Assert.NotNull(updatedTrack);
            Assert.False(updatedTrack.IsNotified);
        }
    }

    [Fact]
    public async Task CreateController_NewSale_TriggersImmediateAlert_WhenCurrentPriceIsEqualOrLower()
    {
        var (processor, tracks, accounts, inApp, scraper, _, _) = CreateProcessorWithInApp();

        var user = new ApplicationUser
        {
            Name = "Instant Checker",
            Email = "instant@example.com",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        scraper.Setup(s => s.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m); // Scraped price is 3000

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>(), processor)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Email, user.Email)
                    }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/instant-deal",
            TargetPrice = 4000m // Target price is 4000 (current 3000 <= 4000)
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CreateController.Index), redirect.ActionName);

        // Verify alert was created in in-app store immediately!
        var count = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(1, count);

        // Verify success message indicates target met and alert dispatched
        var successMsg = controller.TempData["SuccessMessage"]?.ToString();
        Assert.NotNull(successMsg);
        Assert.Contains("In-App alert posted to website", successMsg);
    }

    [Fact]
    public void CreateController_NewSale_AllowsEmptyPhone_WhenPreferenceIsInApp()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();

        var user = new ApplicationUser
        {
            Name = "InApp Only",
            Email = "inapponly@example.com",
            WhatsAppNumber = "", // Empty phone number
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Email, user.Email)
                    }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/gadget",
            TargetPrice = 2500m,
            WhatsAppNumber = "" // No phone provided
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CreateController.Index), redirect.ActionName);

        // Should have created the track without model errors
        Assert.True(controller.ModelState.IsValid);
        Assert.Single(tracks.GetForUser(user.Email));
    }

    [Fact]
    public async Task NotificationsController_ApiEndpoints_WorkAsExpected()
    {
        var inApp = new InMemoryInAppNotificationService();
        var userEmail = "apiuser@example.com";

        var id1 = await inApp.CreateNotificationAsync(userEmail, "trk-a", "Item A", "https://example.com/a", 100m, 120m, "Drop A");
        var id2 = await inApp.CreateNotificationAsync(userEmail, "trk-b", "Item B", "https://example.com/b", 200m, 250m, "Drop B");

        var controller = new NotificationsController(inApp)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Email, userEmail)
                    }, "TestAuth"))
                }
            }
        };

        // Test Recent
        var recentResult = await controller.Recent();
        var jsonResult = Assert.IsType<JsonResult>(recentResult);
        Assert.NotNull(jsonResult.Value);

        // Test MarkRead
        var markResult = await controller.MarkRead(new NotificationsController.MarkReadModel { Id = id1 });
        Assert.IsType<JsonResult>(markResult);
        var unread = await inApp.GetUnreadCountAsync(userEmail);
        Assert.Equal(1, unread);

        // Test MarkAllRead
        var markAllResult = await controller.MarkAllRead();
        Assert.IsType<JsonResult>(markAllResult);
        unread = await inApp.GetUnreadCountAsync(userEmail);
        Assert.Equal(0, unread);
    }
}
