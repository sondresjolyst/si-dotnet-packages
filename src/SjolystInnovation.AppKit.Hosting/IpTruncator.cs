using System.Net;
using System.Net.Sockets;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>
/// Shortens client IP addresses before an app stores them. Enough to show who accepted the terms and to
/// spot fraud, without pointing at one household.
/// </summary>
public static class IpTruncator
{
    /// <summary>
    /// Truncates an IPv4 address to its /24 network and an IPv6 address to its /48 network, by zeroing the
    /// rest. An IPv4-mapped IPv6 address such as <c>::ffff:203.0.113.7</c>, which Kestrel reports for an
    /// IPv4 client on a dual-stack socket, is mapped back to IPv4 first, so it is truncated as IPv4. An
    /// IPv6 scope id is dropped. Returns an empty string for null, empty, white space or text that
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> rejects, which includes an IPv4 address with a port.
    /// </summary>
    /// <param name="ip">The address text, for example from <c>HttpContext.Connection.RemoteIpAddress</c>.</param>
    /// <returns>The truncated address, or an empty string when <paramref name="ip"/> is not an IP address.</returns>
    public static string Truncate(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return string.Empty;
        }

        return Truncate(address);
    }

    /// <summary>Truncates an address as <see cref="Truncate(string?)"/> does. Returns an empty string for null.</summary>
    /// <param name="address">The address, for example <c>HttpContext.Connection.RemoteIpAddress</c>.</param>
    /// <returns>The truncated address, or an empty string when <paramref name="address"/> is null.</returns>
    public static string Truncate(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                bytes[3] = 0;
                break;
            case AddressFamily.InterNetworkV6:
                Array.Clear(bytes, 6, bytes.Length - 6);
                break;
            default:
                return string.Empty;
        }

        return new IPAddress(bytes).ToString();
    }
}
