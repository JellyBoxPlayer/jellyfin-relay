namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed record TunnelOptions(
    Uri RelayUrl,
    string Token,
    string ServerId,
    string ServerType,
    string Version,
    Uri Target);
