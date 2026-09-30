using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class DashboardAndSettingsTests
{
    private (DashboardController controller, InMemoryTrackingStore store, TempDataDictionary tempData) CreateDashboardController(string userEmail, string userName = "Test User")
    {
        var store = new InMemoryTrackingStore();
        var controller = new DashboardController(store);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.Email, userEmail)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var urlHelperMock = new Mock<IUrlHelper>();
        urlHelperMock.Setup(u => u.IsLocalUrl(It.IsAny<string>())).Returns(true);

        var httpContext = new DefaultHttpContext { User = principal };
        var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = tempData;
        controller.Url = urlHelperMock.Object;

        return (controller, store, tempData);
    }

    private (SettingsController controller, InMemoryAccountStore accounts, InMemoryTrackingStore tracks, TempDataDictionary tempData) CreateSettingsController(ApplicationUser user)
    {
        var accounts = new InMemoryAccountStore();
        accounts.Add(user);

        var tracks = new InMemoryTrackingStore();
        var controller = new SettingsController(accounts, tracks);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var authServiceMock = new Mock<Microsoft.AspNetCore.Authentication.IAuthenticationService>();
        var urlHelperMock = new Mock<IUrlHelper>();
        var urlHelperFactoryMock = new Mock<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory>();
        urlHelperFactoryMock.Setup(f => f.GetUrlHelper(It.IsAny<ActionContext>())).Returns(urlHelperMock.Object);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(s => s.GetService(typeof(Microsoft.AspNetCore.Authentication.IAuthenticationService)))
            .Returns(authServiceMock.Object);
        serviceProviderMock
            .Setup(s => s.GetService(typeof(Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory)))
            .Returns(urlHelperFactoryMock.Object);

        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = serviceProviderMock.Object
        };
        var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = tempData;
        controller.Url = urlHelperMock.Object;


        return (controller, accounts, tracks, tempData);
    }

    [Fact]
    public void Dashboard_Index_ReturnsOnlyCurrentUserTracks()
    {
        var (controller, store, _) = CreateDashboardController("user1@example.com");

        store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 50,
            CheckIntervalMinutes = 5
        });

        store.Create("user2@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item2",
            TargetPrice = 100,
            CheckIntervalMinutes = 10
        });

        var result = Assert.IsType<ViewResult>(controller.Index());
        var model = Assert.IsAssignableFrom<IReadOnlyCollection<TrackingItem>>(result.Model);

        Assert.Single(model);
        Assert.Equal("https://example.com/item1", model.First().ProductUrl);
    }

    [Fact]
    public void Dashboard_TrackedProducts_ReturnsOnlyCurrentUserTracks()
    {
        var (controller, store, _) = CreateDashboardController("user1@example.com");

        store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 50,
            CheckIntervalMinutes = 5
        });

        store.Create("user2@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item2",
            TargetPrice = 100,
            CheckIntervalMinutes = 10
        });

        var result = Assert.IsType<ViewResult>(controller.TrackedProducts());
        var model = Assert.IsAssignableFrom<IReadOnlyCollection<TrackingItem>>(result.Model);

        Assert.Single(model);
        Assert.Equal("https://example.com/item1", model.First().ProductUrl);
    }

    [Fact]
    public void Dashboard_Products_RedirectsToTrackedProducts()
    {
        var (controller, _, _) = CreateDashboardController("user1@example.com");

        var result = Assert.IsType<RedirectToActionResult>(controller.Products());
        Assert.Equal(nameof(DashboardController.TrackedProducts), result.ActionName);
    }

    [Fact]
    public void Dashboard_Toggle_TogglesIsActiveAndSetsFeedback()
    {
        var (controller, store, tempData) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 50,
            CheckIntervalMinutes = 5
        });

        Assert.True(track.IsActive);

        var result = controller.Toggle(track.TrackingId);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DashboardController.Index), redirect.ActionName);

        Assert.False(track.IsActive);
        Assert.Contains("paused", tempData["SuccessMessage"]?.ToString());

        controller.Toggle(track.TrackingId);
        Assert.True(track.IsActive);
        Assert.Contains("resumed", tempData["SuccessMessage"]?.ToString());
    }

    [Fact]
    public void Dashboard_Details_ReturnsTrackForOwner()
    {
        var (controller, store, _) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 45.99m,
            CheckIntervalMinutes = 15
        });

        var result = Assert.IsType<ViewResult>(controller.Details(track.TrackingId));
        var model = Assert.IsType<TrackingItem>(result.Model);

        Assert.Equal(track.TrackingId, model.TrackingId);
        Assert.Equal(45.99m, model.TargetPrice);
    }

    [Fact]
    public void Dashboard_Edit_UpdatesTrackingParameters()
    {
        var (controller, store, tempData) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/old",
            TargetPrice = 100,
            CheckIntervalMinutes = 1
        });

        var editModel = new EditTrackViewModel
        {
            TrackingId = track.TrackingId,
            ProductUrl = "https://example.com/new-product",
            TargetPrice = 85.50m,
            CheckIntervalMinutes = 30,
            DisableAfterNotification = true,
            IsActive = true
        };

        var result = controller.Edit(track.TrackingId, editModel);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DashboardController.Index), redirect.ActionName);

        var updated = store.GetById(track.TrackingId, "user1@example.com");
        Assert.NotNull(updated);
        Assert.Equal("https://example.com/new-product", updated.ProductUrl);
        Assert.Equal(85.50m, updated.TargetPrice);
        Assert.Equal(30, updated.CheckIntervalMinutes);
        Assert.True(updated.DisableAfterNotification);
        Assert.Contains("successfully updated", tempData["SuccessMessage"]?.ToString());
    }

    [Fact]
    public void Dashboard_Delete_RemovesTrackFromStore()
    {
        var (controller, store, tempData) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/delete-me",
            TargetPrice = 20,
            CheckIntervalMinutes = 10
        });

        var result = controller.Delete(track.TrackingId);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DashboardController.Index), redirect.ActionName);

        Assert.Null(store.GetById(track.TrackingId, "user1@example.com"));
        Assert.Contains("successfully deleted", tempData["SuccessMessage"]?.ToString());
    }

    [Fact]
    public void Settings_Index_GET_PopulatesCurrentPreferences()
    {
        var user = new ApplicationUser
        {
            Name = "John Doe",
            Email = "john@example.com",
            WhatsAppNumber = "+1234567890",
            DefaultCheckIntervalMinutes = 15,
            DefaultNotificationPreference = "Email"
        };

        var (controller, _, _, _) = CreateSettingsController(user);

        var result = Assert.IsType<ViewResult>(controller.Index());
        var model = Assert.IsType<SettingsViewModel>(result.Model);

        Assert.Equal("John Doe", model.Name);
        Assert.Equal("john@example.com", model.Email);
        Assert.Equal("+1234567890", model.WhatsAppNumber);
        Assert.Equal(15, model.DefaultCheckIntervalMinutes);
        Assert.Equal("Email", model.DefaultNotificationPreference);
    }

    [Fact]
    public async Task Settings_Index_POST_UpdatesProfileAndPreferences()
    {
        var user = new ApplicationUser
        {
            Name = "John Doe",
            Email = "john@example.com",
            WhatsAppNumber = "+1234567890",
            PasswordHash = PasswordSecurity.Hash("CurrentPassword123!"),
            DefaultCheckIntervalMinutes = 1,
            DefaultNotificationPreference = "WhatsApp"
        };

        var (controller, accounts, _, tempData) = CreateSettingsController(user);

        var postModel = new SettingsViewModel
        {
            Name = "Johnathan Doe",
            Email = "john@example.com",
            WhatsAppNumber = "+1987654321",
            DefaultCheckIntervalMinutes = 30,
            DefaultNotificationPreference = "Both"
        };

        var result = await controller.Index(postModel);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SettingsController.Index), redirect.ActionName);

        var updatedUser = accounts.FindByEmail("john@example.com");
        Assert.NotNull(updatedUser);
        Assert.Equal("Johnathan Doe", updatedUser.Name);
        Assert.Equal("+1987654321", updatedUser.WhatsAppNumber);
        Assert.Equal(30, updatedUser.DefaultCheckIntervalMinutes);
        Assert.Equal("Both", updatedUser.DefaultNotificationPreference);
        Assert.Contains("updated successfully", tempData["SuccessMessage"]?.ToString());
    }

    [Fact]
    public async Task Settings_Index_POST_UpdatesEmailAndMigratesUserTracks()
    {
        var user = new ApplicationUser
        {
            Name = "Alice",
            Email = "alice.old@example.com",
            WhatsAppNumber = "+1234567890",
            PasswordHash = PasswordSecurity.Hash("Password123!")
        };

        var (controller, accounts, tracks, _) = CreateSettingsController(user);

        var track = tracks.Create("alice.old@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/tracked",
            TargetPrice = 100
        });

        var postModel = new SettingsViewModel
        {
            Name = "Alice",
            Email = "alice.new@example.com",
            WhatsAppNumber = "+1234567890",
            DefaultCheckIntervalMinutes = 5,
            DefaultNotificationPreference = "WhatsApp"
        };

        var result = await controller.Index(postModel);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SettingsController.Index), redirect.ActionName);

        Assert.Null(accounts.FindByEmail("alice.old@example.com"));
        var newAccount = accounts.FindByEmail("alice.new@example.com");
        Assert.NotNull(newAccount);

        // Verify track migrated
        var migratedTracks = tracks.GetForUser("alice.new@example.com");
        Assert.Single(migratedTracks);
        Assert.Equal(track.TrackingId, migratedTracks.First().TrackingId);
    }

    [Fact]
    public async Task Settings_Index_POST_ValidatesCurrentPasswordWhenChangingPassword()
    {
        var user = new ApplicationUser
        {
            Name = "Bob",
            Email = "bob@example.com",
            WhatsAppNumber = "+1234567890",
            PasswordHash = PasswordSecurity.Hash("CorrectOldPassword123!")
        };

        var (controller, accounts, _, _) = CreateSettingsController(user);

        // Attempt change with wrong current password
        var invalidModel = new SettingsViewModel
        {
            Name = "Bob",
            Email = "bob@example.com",
            WhatsAppNumber = "+1234567890",
            CurrentPassword = "WrongPassword!",
            NewPassword = "NewPassword1234!",
            ConfirmNewPassword = "NewPassword1234!"
        };

        var invalidResult = await controller.Index(invalidModel);
        Assert.IsType<ViewResult>(invalidResult);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(SettingsViewModel.CurrentPassword)));

        // Attempt change with correct current password
        controller.ModelState.Clear();
        var validModel = new SettingsViewModel
        {
            Name = "Bob",
            Email = "bob@example.com",
            WhatsAppNumber = "+1234567890",
            CurrentPassword = "CorrectOldPassword123!",
            NewPassword = "NewPassword1234!",
            ConfirmNewPassword = "NewPassword1234!"
        };

        var validResult = await controller.Index(validModel);
        var redirect = Assert.IsType<RedirectToActionResult>(validResult);
        Assert.Equal(nameof(SettingsController.Index), redirect.ActionName);

        var updatedUser = accounts.FindByEmail("bob@example.com");
        Assert.NotNull(updatedUser);
        Assert.True(PasswordSecurity.Verify("NewPassword1234!", updatedUser.PasswordHash));
    }

    [Fact]
    public async Task Settings_Index_POST_BlankPasswordDoesNotBlockSavingProfile()
    {
        var user = new ApplicationUser
        {
            Name = "Dave",
            Email = "dave@example.com",
            WhatsAppNumber = "03001234567",
            PasswordHash = PasswordSecurity.Hash("OriginalPassword123!")
        };

        var (controller, accounts, _, tempData) = CreateSettingsController(user);

        var model = new SettingsViewModel
        {
            Name = "David Updated",
            Email = "dave@example.com",
            WhatsAppNumber = "03001234567",
            DefaultCheckIntervalMinutes = 10,
            DefaultNotificationPreference = "Email",
            CurrentPassword = null,
            NewPassword = null,
            ConfirmNewPassword = null
        };

        var result = await controller.Index(model);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SettingsController.Index), redirect.ActionName);

        var updated = accounts.FindByEmail("dave@example.com");
        Assert.NotNull(updated);
        Assert.Equal("David Updated", updated.Name);
        Assert.Equal("+923001234567", updated.WhatsAppNumber);
        Assert.Equal(10, updated.DefaultCheckIntervalMinutes);
        Assert.Equal("Email", updated.DefaultNotificationPreference);
        Assert.True(PasswordSecurity.Verify("OriginalPassword123!", updated.PasswordHash));
    }


    [Fact]
    public void Create_NewSale_UsesUserDefaultCheckInterval()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser
        {
            Name = "Carol",
            Email = "carol@example.com",
            DefaultCheckIntervalMinutes = 15,
            WhatsAppNumber = "+923001234567"
        };
        accounts.Add(user);

        var tracks = new InMemoryTrackingStore();
        var whatsAppMock = new Mock<IWhatsAppNotificationService>();
        var controller = new CreateController(tracks, accounts, whatsAppMock.Object);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "Carol"),
            new Claim(ClaimTypes.Email, "carol@example.com")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var result = Assert.IsType<ViewResult>(controller.NewSale());
        var model = Assert.IsType<CreateTrackViewModel>(result.Model);

        Assert.Equal(15, model.CheckIntervalMinutes);
        Assert.Equal("+923001234567", model.WhatsAppNumber);
    }

    [Fact]
    public async Task Dashboard_CheckNow_ExecutesCheckAndSetsFeedback()
    {
        var (controller, store, tempData) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 5000,
            CheckIntervalMinutes = 10
        });

        var accounts = new InMemoryAccountStore();
        accounts.Add(new ApplicationUser { Email = "user1@example.com", WhatsAppNumber = "+923001234567" });

        var scraperMock = new Mock<IPriceScraper>();
        scraperMock.Setup(s => s.GetCurrentPriceAsync(track.ProductUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4500m);

        var notificationsMock = new Mock<IWhatsAppNotificationService>();
        notificationsMock.Setup(n => n.SendPriceAlertAsync(It.IsAny<ApplicationUser>(), It.IsAny<TrackingItem>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SM_TEST");

        var processor = new PriceTrackingProcessor(
            store, accounts, scraperMock.Object, notificationsMock.Object,
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PriceTrackingProcessor>.Instance);

        var result = await controller.CheckNow(track.TrackingId, processor, "/Dashboard/TrackedProducts");
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Dashboard/TrackedProducts", redirect.Url);

        var updated = store.GetById(track.TrackingId, "user1@example.com");
        Assert.NotNull(updated);
        Assert.Equal(4500m, updated.CurrentPrice);
        Assert.Contains("Price checked", tempData["SuccessMessage"]?.ToString());
    }

    [Fact]
    public async Task Dashboard_CheckNowRejectsExternalReturnUrl()
    {
        var (controller, store, _) = CreateDashboardController("user1@example.com");
        var helper = new Mock<IUrlHelper>();
        helper.Setup(h => h.IsLocalUrl(It.IsAny<string>())).Returns(false);
        controller.Url = helper.Object;
        var processor = new PriceTrackingProcessor(store, new InMemoryAccountStore(), Mock.Of<IPriceScraper>(),
            Mock.Of<IWhatsAppNotificationService>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PriceTrackingProcessor>.Instance);
        var result = await controller.CheckNow("missing", processor, "https://external.example/");
        Assert.Equal("Details", Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public void Dashboard_Edit_RejectsPastEndDate()
    {
        var (controller, store, _) = CreateDashboardController("user1@example.com");

        var track = store.Create("user1@example.com", new CreateTrackViewModel
        {
            ProductUrl = "https://example.com/item1",
            TargetPrice = 5000,
            CheckIntervalMinutes = 10
        });

        var editModel = new EditTrackViewModel
        {
            TrackingId = track.TrackingId,
            ProductUrl = track.ProductUrl,
            TargetPrice = 4500,
            CheckIntervalMinutes = 5,
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)
        };

        var result = controller.Edit(track.TrackingId, editModel);
        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(EditTrackViewModel.EndDate)));
    }
}

