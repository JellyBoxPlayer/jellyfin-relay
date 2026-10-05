using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyboxRemote.Cloud;

internal static class CloudAddress
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? cloud)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out cloud)
            && (cloud.Scheme == Uri.UriSchemeHttps || cloud.Scheme == Uri.UriSchemeHttp))
        {
            return true;
        }

        cloud = null;
        return false;
    }

    public static Uri Relay(Uri cloud) => new UriBuilder(cloud)
    {
        Scheme = cloud.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = cloud.AbsolutePath.TrimEnd('/') + "/relay/agent",
        Query = string.Empty,
    }.Uri;
}
