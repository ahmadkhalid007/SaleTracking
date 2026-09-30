using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class BugRegressionTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("recreated")]
    [InlineData("unverified")]
    [InlineData("legacy")]
    [InlineData("valid")]
    [InlineData("admin")]
    [InlineData("forged-admin")]
    [InlineData("missing-admin-role")]
    public async Task CookieMustMatchTheCurrentVerifiedAccount(string state)
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "test@example.com", IsWhatsAppVerified = state != "unverified",
            IsAdmin = state is "admin" or "missing-admin-role" };
        if (state != "missing") accounts.Add(user);
        var claims = new List<Claim> { new(ClaimTypes.Email, user.Email) };
        if (state != "legacy") claims.Add(new(ClaimTypes.NameIdentifier,
            state == "recreated" ? Guid.NewGuid().ToString() : user.Id.ToString()));
        if (state is "admin" or "forged-admin") claims.Add(new(ClaimTypes.Role, "Admin"));
        var authentication = new Mock<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme.Name)), scheme.Name);
        var validation = new CookieValidatePrincipalContext(context, scheme, new CookieAuthenticationOptions(), ticket);

        await new AccountCookieEvents(accounts).ValidatePrincipal(validation);

        Assert.Equal(state is "valid" or "admin", validation.Principal is not null);
        authentication.Verify(a => a.SignOutAsync(context, scheme.Name, It.IsAny<AuthenticationProperties>()),
            state is "valid" or "admin" ? Times.Never() : Times.Once());
    }

    [Fact]
    public void InvalidTrackDoesNotSaveThePhoneNumber()
    {
        var (controller, user, tracks) = CreateForm();
        var model = ValidTrack();
        model.EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        Assert.IsType<ViewResult>(controller.NewSale(model));
        Assert.Empty(user.WhatsAppNumber);
        Assert.Empty(tracks.GetForUser(user.Email));
    }

    [Theory]
    [InlineData("ftp://example.com/product")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("https://user:password@example.com/product")]
    [InlineData("not a product link")]
    public void InvalidProductUrlDoesNotCreateTrack(string url)
    {
        var (controller, user, tracks) = CreateForm();
        var model = ValidTrack();
        model.ProductUrl = url;
        Assert.IsType<ViewResult>(controller.NewSale(model));
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(tracks.GetForUser(user.Email));
    }

    [Theory]
    [InlineData("www.daraz.pk/products/item.html", "https://www.daraz.pk/products/item.html")]
    [InlineData(" https://example.com/product ", "https://example.com/product")]
    public void SchemeOptionalUrlPassesValidationAndIsNormalized(string input, string expected)
    {
        Assert.True(new ProductUrlAttribute().IsValid(input));
        var (controller, user, tracks) = CreateForm();
        var model = ValidTrack();
        model.ProductUrl = input;
        Assert.IsType<RedirectToActionResult>(controller.NewSale(model));
        Assert.Equal(expected, Assert.Single(tracks.GetForUser(user.Email)).ProductUrl);
    }

    [Fact]
    public void DuplicateEmailDoesNotPartiallyChangeProfile()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "first@example.com", Name = "Original", WhatsAppNumber = "+923001234567" };
        accounts.Add(user);
        accounts.Add(new ApplicationUser { Email = "other@example.com" });
        Assert.False(accounts.UpdateProfile(user.Email, "Changed", "other@example.com", "+923111234567", 30, "Both"));
        Assert.Equal("Original", user.Name);
        Assert.Equal("+923001234567", user.WhatsAppNumber);
        Assert.Equal(1, user.DefaultCheckIntervalMinutes);
    }

    [Fact]
    public async Task SettingsWithMissingEmailShowsValidationInsteadOfCrashing()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "test@example.com", Name = "Original", WhatsAppNumber = "+923001234567" };
        accounts.Add(user);
        var controller = new SettingsController(accounts, new InMemoryTrackingStore());
        controller.ControllerContext = new ControllerContext { HttpContext = UserContext(user.Email) };
        controller.ModelState.AddModelError("Email", "Required");
        var result = await controller.Index(new SettingsViewModel { Email = null!, WhatsAppNumber = user.WhatsAppNumber });
        Assert.IsType<ViewResult>(result);
        Assert.Equal("Original", user.Name);
    }

    [Fact]
    public void SettingsDoesNotCreateAnAccountFromAStaleCookie()
    {
        var accounts = new InMemoryAccountStore();
        var controller = new SettingsController(accounts, new InMemoryTrackingStore());
        controller.ControllerContext = new ControllerContext { HttpContext = UserContext("test@example.com") };
        Assert.IsType<RedirectToActionResult>(controller.Index());
        Assert.Null(accounts.FindByEmail("test@example.com"));
    }

    private static (CreateController, ApplicationUser, InMemoryTrackingStore) CreateForm()
    {
        var accounts = new InMemoryAccountStore();
        var user = new ApplicationUser { Email = "test@example.com" };
        accounts.Add(user);
        var tracks = new InMemoryTrackingStore();
        var controller = new CreateController(tracks, accounts, Mock.Of<IWhatsAppNotificationService>());
        var context = UserContext(user.Email);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());
        return (controller, user, tracks);
    }

    private static DefaultHttpContext UserContext(string email) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], "test"))
    };

    private static CreateTrackViewModel ValidTrack() => new()
    {
        ProductUrl = "https://example.com/product", TargetPrice = 1000, WhatsAppNumber = "03001234567"
    };
}
