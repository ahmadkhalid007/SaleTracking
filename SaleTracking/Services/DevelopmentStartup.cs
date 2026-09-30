using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace SaleTracking.Services;

public static class DevelopmentStartup
{
    public static string? FindPortConflict(string? urls)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var address in (urls ?? "http://localhost:5000").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Port == 0) continue;
            if (listeners.Any(endpoint => endpoint.Port == uri.Port &&
                (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))))
                return $"SaleTracking cannot start at {address}: port {uri.Port} is already in use. " +
                    "Stop the existing instance before starting another, or select a different port in Properties/launchSettings.json. " +
                    "No background alert workers were started by this launch.";
        }
        return null;
    }

    public static bool IsAddressInUse(Exception exception) =>
        exception is AddressInUseException ||
        exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ||
        exception.InnerException is { } inner && IsAddressInUse(inner);
}
