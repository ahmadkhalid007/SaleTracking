using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaleTracking.Controllers;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public sealed class WhatsAppControllerTests
{
    [Fact]
    public void DisconnectRequiresAdminAndAntiforgeryProtectedPost()
    {
        Assert.Equal("Admin", typeof(WhatsAppController).GetCustomAttribute<AuthorizeAttribute>()?.Roles);
        var action = typeof(WhatsAppController).GetMethod(nameof(WhatsAppController.Disconnect))!;
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task SuccessfulDisconnectCallsConnectionService()
    {
        var connection = new Mock<IWhatsAppConnectionService>();
        connection.Setup(service => service.DisconnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var controller = new WhatsAppController(connection.Object);
        Assert.IsType<OkObjectResult>(await controller.Disconnect(CancellationToken.None));
        connection.Verify(service => service.DisconnectAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task FailedDisconnectReturnsServiceError()
    {
        var connection = new Mock<IWhatsAppConnectionService>();
        connection.Setup(service => service.DisconnectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WhatsAppNotificationException("Disconnect was not confirmed."));
        var result = Assert.IsType<ObjectResult>(await new WhatsAppController(connection.Object).Disconnect(CancellationToken.None));
        Assert.Equal(503, result.StatusCode);
    }
}
