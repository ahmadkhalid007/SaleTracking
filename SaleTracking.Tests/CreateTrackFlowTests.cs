using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class CreateTrackFlowTests
{
    [Fact]
    public void CreateTrackUsesOwnerNumberAndSchedulesWithoutSending()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "ali@example.com", WhatsAppNumber = "03001234567" };
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();
        var notifications = new Mock<IWhatsAppNotificationService>();
        var controller = new CreateController(tracks, accounts, notifications.Object);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());
        controller.ModelState.SetModelValue(nameof(CreateTrackViewModel.WhatsAppNumber), "+923211112233", "+923211112233");

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/product", TargetPrice = 5000,
            CheckIntervalMinutes = 5, WhatsAppNumber = "+923211112233"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var track = Assert.Single(tracks.GetForUser(user.Email));
        Assert.Equal(user.Email, track.OwnerEmail);
        Assert.Equal("+923001234567", track.TargetWhatsAppNumber);
        Assert.Equal("Track created successfully", controller.TempData["SuccessMessage"]);
        Assert.Equal(track.TrackingId, controller.TempData["TrackingId"]);
        Assert.Null(track.LastCheckedAt);
        Assert.False(track.IsNotified);
        Assert.Empty(tracks.GetDueTracks(track.CreatedAt));
        notifications.Verify(service => service.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void CreatingTrackRequiresExistingAccount()
    {
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, new InMemoryAccountStore(), Mock.Of<IWhatsAppNotificationService>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var result = Assert.IsType<RedirectToActionResult>(controller.NewSale(new CreateTrackViewModel()));
        Assert.Equal("Login", result.ActionName);
        Assert.Empty(tracks.GetForUser(""));
    }

    [Fact]
    public void CreateTrack_WhenUserWhatsAppNumberEmpty_UsesAndSavesModelWhatsAppNumber()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "emptyphone@example.com", WhatsAppNumber = "" };
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/product",
            TargetPrice = 2500,
            CheckIntervalMinutes = 1,
            WhatsAppNumber = "03129876543"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var track = Assert.Single(tracks.GetForUser(user.Email));
        Assert.Equal("+923129876543", track.TargetWhatsAppNumber);
        Assert.Equal("+923129876543", user.WhatsAppNumber);
    }

    [Fact]
    public void CreateTrack_NormalizesUrlWithoutScheme()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "urltest@example.com", WhatsAppNumber = "+923001234567" };
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "www.daraz.pk/products/item-123.html",
            TargetPrice = 1200,
            CheckIntervalMinutes = 5
        });

        Assert.IsType<RedirectToActionResult>(result);
        var track = Assert.Single(tracks.GetForUser(user.Email));
        Assert.Equal("https://www.daraz.pk/products/item-123.html", track.ProductUrl);
    }

    [Fact]
    public void CreateTrack_RejectsPastEndDate()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "datetest@example.com", WhatsAppNumber = "+923001234567" };
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, user.Email) }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());

        var result = controller.NewSale(new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/product",
            TargetPrice = 1000,
            CheckIntervalMinutes = 5,
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2)
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(CreateTrackViewModel.EndDate)));
    }

    [Fact]
    public void CreateTrack_MissingAccountRedirectsToLoginWithoutRecreatingIt()
    {
        var accounts = new InMemoryAccountStore(); // Empty in-memory accounts store
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Auto SeedTest"),
                new Claim(ClaimTypes.Email, "autoseed@example.com")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());

        var getResult = Assert.IsType<RedirectToActionResult>(controller.NewSale());
        Assert.Equal("Login", getResult.ActionName);
        Assert.Null(accounts.FindByEmail("autoseed@example.com"));
    }
}
