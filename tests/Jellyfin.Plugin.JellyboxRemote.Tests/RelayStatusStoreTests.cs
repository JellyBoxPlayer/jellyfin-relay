using Jellyfin.Plugin.JellyboxRemote.Tunnel;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

public sealed class RelayStatusStoreTests
{
    [Fact]
    public void The_address_survives_a_dropped_tunnel_but_not_a_log_out()
    {
        var store = new RelayStatusStore();
        store.Report(new TunnelStatus(TunnelState.Connected, "https://relay.test/r/key"));

        store.Report(new TunnelStatus(TunnelState.Disconnected, Message: "connection closed"));
        Assert.Equal("https://relay.test/r/key", store.Url);

        store.Report(new TunnelStatus(TunnelState.Connecting));
        Assert.Equal("https://relay.test/r/key", store.Url);

        store.Report(new TunnelStatus(TunnelState.NotConfigured));
        Assert.Null(store.Url);
    }
}
