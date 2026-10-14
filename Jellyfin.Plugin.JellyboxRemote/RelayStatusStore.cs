using Jellyfin.Plugin.JellyboxRemote.Tunnel;

namespace Jellyfin.Plugin.JellyboxRemote;

public sealed class RelayStatusStore
{
    private volatile TunnelStatus _current = new(TunnelState.NotConfigured);

    public string State => _current.State.ToString();

    public string? Key => _current.Key;

    public string? Message => _current.Message;

    public string? Fingerprint { get; internal set; }

    public RelayNotice? Notice { get; private set; }

    internal void ReportNotice(RelayNotice notice) => Notice = notice;

    internal void Report(TunnelStatus status)
    {
        if (status.State is TunnelState.Connected or TunnelState.NotConfigured)
        {
            Notice = null;
        }

        var previous = _current;
        var stillLinked = status.State is TunnelState.Connecting or TunnelState.Disconnected;
        _current = stillLinked && status.Key is null && previous.Key is not null
            ? status with { Key = previous.Key }
            : status;
    }
}
