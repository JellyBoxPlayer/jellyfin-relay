using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed record TunnelOptions(
    Uri RelayUrl,
    string Token,
    string ServerId,
    string ServerType,
    string Version,
    Uri Target,
    X509Certificate2 Certificate);
