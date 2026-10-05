namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal enum TunnelState
{
    NotConfigured,
    Connecting,
    Connected,
    TokenRefused,
    Disconnected,
}

internal sealed record TunnelStatus(TunnelState State, string? Url = null, string? Message = null);
