namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal static class Headers
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "proxy-connection",
        "te", "trailer", "transfer-encoding", "upgrade", "host",
    };

    public static bool Forwardable(string name) => !HopByHop.Contains(name);
}
