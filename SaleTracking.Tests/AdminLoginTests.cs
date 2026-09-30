using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Models;
using SaleTracking.Services;
using Microsoft.Data.Sqlite;

namespace SaleTracking.Tests;

public sealed class AdminLoginTests
{
    [Fact]
    public void SavedAdministratorStillGetsAnAdminLoginPageAndOldSetupLinkRedirects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saletrack-login-" + Guid.NewGuid());
        try
        {
            var database = new SqliteDatabase(Path.Combine(directory, "saletracking.db"));
            database.Initialize();
            var saved = new SqliteAccountStore(database);
            var admin = User(true);
            saved.TryCreate(admin);
            var (controller, _) = Controller(saved);
            var view = Assert.IsType<ViewResult>(controller.AdminLogin(saved));
            Assert.Equal("Login", view.ViewName);
            Assert.True((bool)controller.ViewData["IsAdminLogin"]!);
            var redirect = Assert.IsType<RedirectToActionResult>(controller.AdminSetup(saved));
            Assert.Equal("AdminLogin", redirect.ActionName);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AdminLoginGoesDirectlyToSenderSettings()
    {
        var accounts = new InMemoryAccountStore();
        accounts.Add(User(true));
        var (controller, authentication) = Controller(accounts);
        var result = Assert.IsType<RedirectToActionResult>(await controller.AdminLogin(new LoginViewModel
        { Email = "admin@example.com", Password = "TestPassword123!", RememberMe = true }));
        Assert.Equal("Settings", result.ControllerName);
        Assert.Equal("whatsapp-sender", result.Fragment);
        authentication.Verify(service => service.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(),
            It.Is<ClaimsPrincipal>(principal => principal.IsInRole("Admin")),
            It.Is<AuthenticationProperties>(properties => properties.IsPersistent)), Times.Once);
    }

    [Theory]
    [InlineData(false, "TestPassword123!")]
    [InlineData(true, "WrongPassword123!")]
    public async Task AdminLoginRejectsCustomerAccountsAndWrongPasswords(bool isAdmin, string password)
    {
        var accounts = new InMemoryAccountStore();
        accounts.Add(User(isAdmin));
        var (controller, authentication) = Controller(accounts);
        var view = Assert.IsType<ViewResult>(await controller.AdminLogin(new LoginViewModel
        { Email = "admin@example.com", Password = password }));
        Assert.Equal("Login", view.ViewName);
        Assert.False(controller.ModelState.IsValid);
        authentication.Verify(service => service.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>()), Times.Never);
    }

    private static ApplicationUser User(bool isAdmin) => new()
    {
        Email = "admin@example.com", Name = "Test Admin", IsAdmin = isAdmin, IsWhatsAppVerified = true,
        PasswordHash = PasswordSecurity.Hash("TestPassword123!")
    };

    private static (AccountController, Mock<IAuthenticationService>) Controller(IAccountStore accounts)
    {
        var authentication = new Mock<IAuthenticationService>();
        var urlHelperFactory = new Mock<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory>();
        var urlHelper = new Mock<IUrlHelper>();
        urlHelperFactory.Setup(f => f.GetUrlHelper(It.IsAny<ActionContext>())).Returns(urlHelper.Object);
        var services = new ServiceCollection();
        services.AddSingleton(authentication.Object);
        services.AddSingleton(urlHelperFactory.Object);
        services.AddSingleton<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>(Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
        services.AddSingleton<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory, Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionaryFactory>();
        var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        var controller = new AccountController(accounts, Mock.Of<IWhatsAppVerificationService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>())
        };
        return (controller, authentication);
    }
}
