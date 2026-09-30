using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaleTracking.Models;
using SaleTracking.Services;

namespace SaleTracking.Tests;

public class WhatsAppWebNotificationServiceTests
{
    private const string MessageSid = "true_923001234567@c.us_TEST_MESSAGE";

    [Fact]
    public async Task SendsTextToOwnersCurrentNumberWithStableAlertReference()
    {
        var settings = ConfiguredOptions();
        using var handler = new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:3210/api/messages", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(settings.ApiKey, request.Headers.Authorization.Parameter);
            using var fields = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("+923001234567", fields.RootElement.GetProperty("to").GetString());
            var message = fields.RootElement.GetProperty("message").GetString();
            Assert.Contains("TRK-TEST", message);
            Assert.Contains("4,500.00", message);
            Assert.Contains("5,000.00", message);
            Assert.Contains("https://example.com/product", message);
            Assert.StartsWith("TRK-TEST:", fields.RootElement.GetProperty("idempotencyKey").GetString());
            return JsonResponse(HttpStatusCode.OK, $"{{\"success\":true,\"messageId\":\"{MessageSid}\"}}");
        });
        using var client = new HttpClient(handler);
        var service = CreateService(client, settings);
        var track = Track();
        track.TargetWhatsAppNumber = "+923211112233"; // An old or tampered per-track number must not be used.
        var result = await service.SendPriceAlertAsync(Owner(), track, 4500m);
        Assert.Equal(MessageSid, result);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresAreNotReportedAsSuccess(int status)
    {
        using var handler = new StubHandler(_ => Task.FromResult(JsonResponse((HttpStatusCode)status, "{}")));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).SendPriceAlertAsync(Owner(), Track(), 4500m));
    }

    [Theory]
    [InlineData("{\"success\":false,\"code\":\"disconnected\"}")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"success\":true,\"messageId\":\"\"}")]
    [InlineData("not-json")]
    public async Task MissingConfirmationIsNotReportedAsSuccess(string body)
    {
        using var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, body)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).SendPriceAlertAsync(Owner(), Track(), 4500m));
    }

    [Fact]
    public async Task MissingConfigurationNeverSendsOrClaimsSuccess()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var client = new HttpClient(handler);
        var service = CreateService(client, new WhatsAppWebOptions { TokenFile = "not-a-real-token-file" });
        Assert.False(service.IsConfigured);
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => service.SendPriceAlertAsync(Owner(), Track(), 4500m));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DifferentAccountCannotReceiveTracksAlert()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var client = new HttpClient(handler);
        var owner = Owner();
        owner.Email = "someone-else@example.com";
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).SendPriceAlertAsync(owner, Track(), 4500m));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("03001234567", "+923001234567")]
    [InlineData("923001234567", "+923001234567")]
    [InlineData("+92 300-1234567", "+923001234567")]
    [InlineData("+14155552671", "+14155552671")]
    public void NormalizesRegisteredPhoneNumbers(string input, string expected) => Assert.Equal(expected, WhatsAppPhoneNumber.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("+92300123+4567")]
    public void RejectsInvalidPhoneNumbers(string input) => Assert.Throws<WhatsAppNotificationException>(() => WhatsAppPhoneNumber.Normalize(input));

    [Theory]
    [InlineData("http://example.com:3210")]
    [InlineData("http://127.0.0.1:3210?token=wrong")]
    [InlineData("http://user:pass@127.0.0.1:3210")]
    public void RejectsNonLocalOrAmbiguousBridgeAddresses(string address)
    {
        var options = ConfiguredOptions();
        options.BridgeUrl = address;
        using var client = new HttpClient();
        Assert.False(CreateService(client, options).IsConfigured);
    }

    [Fact]
    public async Task ConnectionStatusRequiresActualReadyResponse()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("/api/status", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"state\":\"qr\",\"connected\":false}"));
        });
        using var client = new HttpClient(handler);
        var status = await CreateService(client, ConfiguredOptions()).GetConnectionStatusAsync();
        Assert.Equal("qr", status.State);
        Assert.False(status.Connected);
    }

    [Fact]
    public async Task OfflineBridgeReturnsStatusAndDoesNotCrashPage()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var client = new HttpClient(handler);
        var service = CreateService(client, ConfiguredOptions());
        Assert.Equal("offline", (await service.GetConnectionStatusAsync()).State);
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => service.SendPriceAlertAsync(Owner(), Track(), 4500));
    }

    [Fact]
    public async Task RetryReusesSameAlertReferenceEvenIfCurrentPriceChanges()
    {
        var keys = new List<string?>();
        using var handler = new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            keys.Add(payload.RootElement.GetProperty("idempotencyKey").GetString());
            return JsonResponse(HttpStatusCode.OK, $"{{\"success\":true,\"messageId\":\"{MessageSid}\"}}");
        });
        using var client = new HttpClient(handler);
        var service = CreateService(client, ConfiguredOptions());
        var track = Track();
        await service.SendPriceAlertAsync(Owner(), track, 4500);
        await service.SendPriceAlertAsync(Owner(), track, 4400);
        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public async Task ShutdownCancellationIsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new StubHandler(_ => throw new OperationCanceledException(cancellation.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(client, ConfiguredOptions()).SendPriceAlertAsync(Owner(), Track(), 4500, cancellation.Token));
    }

    [Fact]
    public async Task AdminStatusUsesProtectedEndpointAndDropsQrAfterReady()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("/api/connection", request.RequestUri!.AbsolutePath);
            Assert.Equal(ConfiguredOptions().ApiKey, request.Headers.Authorization!.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                "{\"state\":\"ready\",\"connected\":true,\"senderNumber\":\"+923001234567\",\"qr\":\"data:image/png;base64,YWJj\"}"));
        });
        using var client = new HttpClient(handler);
        var status = await CreateService(client, ConfiguredOptions()).GetAdminStatusAsync();
        Assert.True(status.Connected);
        Assert.Null(status.Qr);
        Assert.Equal("+923001234567", status.SenderNumber);
    }

    [Theory]
    [InlineData("data:image/png;base64,YWJj", true)]
    [InlineData("https://example.com/qr.png", false)]
    [InlineData("data:image/svg+xml;base64,YWJj", false)]
    public async Task AdminStatusRelaysOnlyInlinePngQr(string qr, bool accepted)
    {
        using var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { state = "qr", connected = false, senderNumber = "stale-number", qr }))));
        using var client = new HttpClient(handler);
        var status = await CreateService(client, ConfiguredOptions()).GetAdminStatusAsync();
        Assert.False(status.Connected);
        Assert.Null(status.SenderNumber);
        Assert.Equal(accepted ? qr : null, status.Qr);
    }

    [Fact]
    public async Task ReconnectUsesAuthenticatedPostAndReportsFailure()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/reconnect", request.RequestUri!.AbsolutePath);
            Assert.NotNull(request.Headers.Authorization);
            return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).ReconnectAsync());
    }

    [Fact]
    public async Task OldBridgeWithoutConnectionEndpointIsIdentifiedInsteadOfShowingGenericFailure()
    {
        using var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, "{\"error\":\"Not found.\"}")));
        using var client = new HttpClient(handler);
        var status = await CreateService(client, ConfiguredOptions()).GetAdminStatusAsync();
        Assert.Equal("bridge_outdated", status.State);
        Assert.False(status.Connected);
        Assert.Null(status.Qr);
    }

    [Fact]
    public async Task DisconnectUsesAuthenticatedPost()
    {
        var options = ConfiguredOptions();
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/disconnect", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(options.ApiKey, request.Headers.Authorization.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"success\":true}"));
        });
        using var client = new HttpClient(handler);
        await CreateService(client, options).DisconnectAsync();
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task FailedDisconnectIsNotReportedAsSuccess(int status)
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(JsonResponse((HttpStatusCode)status, "{}"))));
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).DisconnectAsync());
    }

    [Fact]
    public async Task OldBridgeReturnsActionableDisconnectError()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, "{}"))));
        var error = await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).DisconnectAsync());
        Assert.Contains("Restart SaleTrack", error.Message);
    }

    [Fact]
    public async Task OfflineDisconnectDoesNotCrashAndExternalCancellationPropagates()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var error = await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, ConfiguredOptions()).DisconnectAsync());
        Assert.Contains("not confirmed", error.Message);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelledClient = new HttpClient(new StubHandler(_ => throw new OperationCanceledException(cancellation.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(cancelledClient, ConfiguredOptions()).DisconnectAsync(cancellation.Token));
    }

    [Fact]
    public async Task DisabledConnectionDoesNotAttemptDisconnect()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);
        var options = ConfiguredOptions();
        options.Enabled = false;
        await Assert.ThrowsAsync<WhatsAppNotificationException>(() => CreateService(client, options).DisconnectAsync());
        Assert.Equal(0, handler.Calls);
    }

    private static WhatsAppWebOptions ConfiguredOptions() => new()
    {
        Enabled = true, ApiKey = "test-key-with-at-least-32-characters", BridgeUrl = "http://127.0.0.1:3210"
    };
    private static ApplicationUser Owner() => new() { Email = "owner@example.com", WhatsAppNumber = "03001234567" };
    private static TrackingItem Track() => new() { TrackingId = "TRK-TEST", OwnerEmail = "owner@example.com", TargetPrice = 5000m, ProductUrl = "https://example.com/product" };
    private static WhatsAppWebNotificationService CreateService(HttpClient client, WhatsAppWebOptions settings) =>
        new(client, Options.Create(settings), NullLogger<WhatsAppWebNotificationService>.Instance);
    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request);
        }
    }
}
