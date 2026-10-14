using Jellyfin.Plugin.JellyboxRemote.Tunnel;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

public sealed class RelayStatusStoreTests
{
    [Fact]
    public void The_key_survives_a_dropped_tunnel_but_not_a_log_out()
    {
        var store = new RelayStatusStore();
        store.Report(new TunnelStatus(TunnelState.Connected, "serverkey"));

        store.Report(new TunnelStatus(TunnelState.Disconnected, Message: "connection closed"));
        Assert.Equal("serverkey", store.Key);

        store.Report(new TunnelStatus(TunnelState.Connecting));
        Assert.Equal("serverkey", store.Key);

        store.Report(new TunnelStatus(TunnelState.NotConfigured));
        Assert.Null(store.Key);
    }

    [Fact]
    public void A_notice_lasts_until_the_next_connection_or_a_log_out()
    {
        var store = new RelayStatusStore();
        var notice = new RelayNotice("update_available", "warning", "Update the plugin.");
        store.Report(new TunnelStatus(TunnelState.Connected, "serverkey"));
        store.ReportNotice(notice);

        store.Report(new TunnelStatus(TunnelState.Disconnected));
        Assert.Equal(notice, store.Notice);

        store.Report(new TunnelStatus(TunnelState.Connected, "serverkey"));
        Assert.Null(store.Notice);

        store.ReportNotice(notice);
        store.Report(new TunnelStatus(TunnelState.NotConfigured));
        Assert.Null(store.Notice);
    }
}
