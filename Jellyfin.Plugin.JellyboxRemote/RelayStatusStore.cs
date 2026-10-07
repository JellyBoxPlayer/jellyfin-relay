using Jellyfin.Plugin.JellyboxRemote.Tunnel;

namespace Jellyfin.Plugin.JellyboxRemote;

public sealed class RelayStatusStore
{
    private volatile TunnelStatus _current = new(TunnelState.NotConfigured);

    public string State => _current.State.ToString();

    public string? Key => _current.Key;

    public string? Message => _current.Message;

    public string? Fingerprint { get; internal set; }

    internal void Report(TunnelStatus status)
    {
        var previous = _current;
        var stillLinked = status.State is TunnelState.Connecting or TunnelState.Disconnected;
        _current = stillLinked && status.Key is null && previous.Key is not null
            ? status with { Key = previous.Key }
            : status;
    }
}
