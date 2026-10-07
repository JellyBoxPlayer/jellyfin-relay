namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal enum TunnelState
{
    NotConfigured,
    Connecting,
    Connected,
    TokenRefused,
    Disconnected,
}

internal sealed record TunnelStatus(TunnelState State, string? Key = null, string? Message = null);
