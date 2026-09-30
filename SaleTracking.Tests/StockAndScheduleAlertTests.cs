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

public class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

public class StockAndScheduleAlertTests
{
    private (PriceTrackingProcessor processor,
        ITrackingStore tracks,
        IAccountStore accounts,
        InMemoryInAppNotificationService inApp,
        Mock<IProductScraper> scraper,
        TimeProvider timeProvider)
        CreateProcessor(DateTimeOffset? now = null)
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var inApp = new InMemoryInAppNotificationService();
        var scraper = new Mock<IProductScraper>();
        var timeProvider = new TestTimeProvider(now ?? new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var whatsApp = new Mock<IWhatsAppNotificationService>();
        var email = new Mock<IEmailNotificationService>();

        whatsApp.Setup(w => w.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("whatsapp-msg-123");
        email.Setup(e => e.SendPriceAlertEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("email-msg-456");

        var processor = new PriceTrackingProcessor(
            tracks, accounts, scraper.Object, whatsApp.Object,
            timeProvider, NullLogger<PriceTrackingProcessor>.Instance,
            email.Object, inApp);

        return (processor, tracks, accounts, inApp, scraper, timeProvider);
    }

    [Fact]
    public async Task InStockAlert_WhenProductInStock_FiresAlert_EvenWithoutTargetPrice()
    {
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor();
        var user = new ApplicationUser
        {
            Email = "stockuser@example.com",
            Name = "Stock User",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item-instock",
            CheckType = "InStock",
            TargetPrice = null
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(null, true));

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("back in stock", message, StringComparison.OrdinalIgnoreCase);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(1, unread);
        var notifs = await inApp.GetNotificationsAsync(user.Email);
        Assert.Contains("[Stock Alert]", notifs[0].Message);

        var updated = tracks.GetById(track.TrackingId, user.Email);
        Assert.NotNull(updated);
        Assert.True(updated.IsNotified);
    }

    [Fact]
    public async Task TargetPriceAlert_WhenPriceBelowTarget_FiresAlertWithPriceAlertMessage()
    {
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor();
        var user = new ApplicationUser
        {
            Email = "priceuser@example.com",
            Name = "Price User",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item-price",
            CheckType = "TargetPrice",
            TargetPrice = 2000m
        });

        // Scraper reports out of stock, but price check alerts anyway based on price threshold
        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(1800m, false));

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("Target threshold met", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(1, unread);

        var notifs = await inApp.GetNotificationsAsync(user.Email);
        Assert.Contains("[Price Alert]", notifs[0].Message);
    }

    [Fact]
    public async Task InStockAlert_WhenProductOutOfStock_DoesNotAlert()
    {
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor();
        var user = new ApplicationUser
        {
            Email = "outofstock@example.com",
            Name = "Out Of Stock User",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item-outofstock",
            CheckType = "InStock",
            TargetPrice = null
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(1200m, false));

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("Out of stock", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(0, unread);

        var updated = tracks.GetById(track.TrackingId, user.Email);
        Assert.NotNull(updated);
        Assert.False(updated.IsNotified);
    }

    [Fact]
    public async Task BothAlert_WhenPriceBelowTargetAndInStock_FiresAlert()
    {
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor();
        var user = new ApplicationUser
        {
            Email = "bothuser@example.com",
            Name = "Both User",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item-both",
            CheckType = "Both",
            TargetPrice = 3000m
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(2500m, true));

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("Target threshold met", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(1, unread);
        var notifs = await inApp.GetNotificationsAsync(user.Email);
        Assert.Contains("[Both: Price & Stock]", notifs[0].Message);
    }

    [Fact]
    public async Task BothAlert_WhenPriceBelowTargetButOutOfStock_DoesNotAlert()
    {
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor();
        var user = new ApplicationUser
        {
            Email = "bothuser2@example.com",
            Name = "Both User 2",
            DefaultNotificationPreference = "InApp"
        };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item-both2",
            CheckType = "Both",
            TargetPrice = 3000m
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(2500m, false));

        var (success, price, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task ScheduleTime_WhenStartTimeInFuture_DoesNotAlert()
    {
        // Current time is 10:00 AM on 2026-09-28
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor(now);

        var user = new ApplicationUser { Email = "sched@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/future-start",
            CheckType = "TargetPrice",
            TargetPrice = 5000m,
            StartDate = new DateOnly(2026, 9, 28),
            StartTime = new TimeOnly(14, 0) // Starts at 2:00 PM today
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(4000m, true));

        var (success, _, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("outside this tracker's schedule", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task ScheduleTime_WhenStartTimePassed_AlertsSuccessfully()
    {
        // Current time is 15:00 (3:00 PM) on 2026-09-28
        var now = new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor(now);

        var user = new ApplicationUser { Email = "sched2@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/past-start",
            CheckType = "TargetPrice",
            TargetPrice = 5000m,
            StartDate = new DateOnly(2026, 9, 28),
            StartTime = new TimeOnly(14, 0) // Started at 2:00 PM today
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(4000m, true));

        var (success, _, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("Target threshold met", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(1, unread);
    }

    [Fact]
    public async Task ScheduleTime_WhenEndTimePassed_DoesNotAlert()
    {
        // Current time is 18:00 (6:00 PM) on 2026-09-28
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var (processor, tracks, accounts, inApp, scraper, _) = CreateProcessor(now);

        var user = new ApplicationUser { Email = "sched3@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var track = tracks.Create(user.Email, new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/past-end",
            CheckType = "TargetPrice",
            TargetPrice = 5000m,
            EndDate = new DateOnly(2026, 9, 28),
            EndTime = new TimeOnly(17, 0) // Ended at 5:00 PM today
        });

        scraper.Setup(s => s.ScrapeProductAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductScrapeResult(4000m, true));

        var (success, _, message) = await processor.CheckSingleTrackAsync(track.TrackingId, user.Email);

        Assert.True(success);
        Assert.Contains("outside this tracker's schedule", message);
        var unread = await inApp.GetUnreadCountAsync(user.Email);
        Assert.Equal(0, unread);
    }

    [Fact]
    public void CreateController_AllowsEmptyTargetPrice_ForInStockCheck()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "stockval@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/instock-product",
            CheckType = "InStock",
            TargetPrice = null
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CreateController.Index), redirect.ActionName);
        var created = Assert.Single(tracks.GetForUser(user.Email));
        Assert.Equal("InStock", created.CheckType);
        Assert.Equal(0m, created.TargetPrice);
    }

    [Fact]
    public void CreateController_RequiresTargetPrice_ForPriceAlert()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "priceval@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/price-product",
            CheckType = "TargetPrice",
            TargetPrice = null
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(CreateTrackViewModel.TargetPrice)));
    }

    [Fact]
    public void CreateController_Validates_EndTimeBeforeStartTime_OnSameDay()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "timeval@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/time-product",
            CheckType = "TargetPrice",
            TargetPrice = 1000m,
            StartDate = today,
            StartTime = new TimeOnly(15, 0),
            EndDate = today,
            EndTime = new TimeOnly(10, 0) // End time earlier than start time
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(CreateTrackViewModel.EndTime)));
    }

    [Fact]
    public void CreateController_NewSale_WithReturnUrl_RedirectsToReturnUrl()
    {
        var tracks = new InMemoryTrackingStore();
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "return@example.com", DefaultNotificationPreference = "InApp" };
        accounts.Add(user);

        var urlHelperMock = new Mock<IUrlHelper>();
        urlHelperMock.Setup(u => u.IsLocalUrl("/Dashboard")).Returns(true);

        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
                }
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>()),
            Url = urlHelperMock.Object
        };

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item",
            CheckType = "TargetPrice",
            TargetPrice = 5000m
        }, returnUrl: "/Dashboard");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Dashboard", redirect.Url);
    }

    [Fact]
    public async Task SettingsController_Index_WithReturnUrl_RedirectsToReturnUrl()
    {
        var user = new ApplicationUser { Email = "settingsuser@example.com", Name = "Settings User" };
        var accounts = new InMemoryAccountStore();
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();

        var urlHelperMock = new Mock<IUrlHelper>();
        urlHelperMock.Setup(u => u.IsLocalUrl("/Dashboard")).Returns(true);

        var authServiceMock = new Mock<Microsoft.AspNetCore.Authentication.IAuthenticationService>();
        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(Microsoft.AspNetCore.Authentication.IAuthenticationService)))
            .Returns(authServiceMock.Object);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.Name) }, "TestAuth")),
            RequestServices = serviceProviderMock.Object
        };

        var controller = new SettingsController(accounts, tracks)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>()),
            Url = urlHelperMock.Object
        };

        var result = await controller.Index(new SettingsViewModel
        {
            Name = "Updated Name",
            Email = user.Email,
            WhatsAppNumber = "03001234567",
            DefaultCheckIntervalMinutes = 10,
            DefaultNotificationPreference = "WhatsApp"
        }, returnUrl: "/Dashboard");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Dashboard", redirect.Url);
    }

    [Fact]
    public void SettingsController_ChangePassword_WithReturnUrl_RedirectsToReturnUrl()
    {
        var user = new ApplicationUser
        {
            Email = "pwduser@example.com",
            Name = "Pwd User",
            PasswordHash = PasswordSecurity.Hash("OldPass@123")
        };
        var accounts = new InMemoryAccountStore();
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();

        var urlHelperMock = new Mock<IUrlHelper>();
        urlHelperMock.Setup(u => u.IsLocalUrl("/Dashboard")).Returns(true);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
        };

        var controller = new SettingsController(accounts, tracks)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>()),
            Url = urlHelperMock.Object
        };

        var result = controller.ChangePassword("OldPass@123", "NewPass@123", "NewPass@123", returnUrl: "/Dashboard");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Dashboard", redirect.Url);
    }
}
