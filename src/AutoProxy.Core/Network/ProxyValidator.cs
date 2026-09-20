using System.Net;
using System.Text.RegularExpressions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Network;

public static class ProxyValidator
{
    private static readonly Regex HostNameRegex =
        new(@"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$", RegexOptions.Compiled);

    public static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host.Length > 253) return false;

        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                   ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        return HostNameRegex.IsMatch(host) && !host.Contains("://");
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>
    /// Formats a proxy endpoint for use in URIs, the Windows registry and
    /// netsh. IPv6 literal hosts are wrapped in square brackets so they are
    /// unambiguous from the port separator.
    /// </summary>
    public static string FormatAddress(string host, int port)
    {
        var h = (host ?? string.Empty).Trim();
        if (IPAddress.TryParse(h, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            h = $"[{h}]";
        }

        return $"{h}:{port}";
    }

    public static string? Validate(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            return "Proxy name is required.";
        if (!IsValidHost(profile.Host))
            return "Host must be a valid hostname or IP address.";
        if (!IsValidPort(profile.Port))
            return "Port must be between 1 and 65535.";
        return null;
    }

    public static string SanitizeForLog(string host)
    {
        host = (host ?? string.Empty).Trim();
        return host.Length > 128 ? host[..128] : host;
    }
}