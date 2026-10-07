using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Jellyfin.Plugin.JellyboxRemote.Cloud;

internal static class CloudAddress
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? cloud)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out cloud)
            && (cloud.Scheme == Uri.UriSchemeHttps || (cloud.Scheme == Uri.UriSchemeHttp && IsLocal(cloud.Host))))
        {
            return true;
        }

        cloud = null;
        return false;
    }

    private static bool IsLocal(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal)
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }

            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal);
    }

    public static Uri Relay(Uri cloud) => new UriBuilder(cloud)
    {
        Scheme = cloud.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = cloud.AbsolutePath.TrimEnd('/') + "/relay/agent",
        Query = string.Empty,
    }.Uri;
}
