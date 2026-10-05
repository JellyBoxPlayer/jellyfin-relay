using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyboxRemote.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultRelayUrl = "wss://cloud.jellybox.app/relay/agent";

    public string Token { get; set; } = string.Empty;

    public string RelayUrl { get; set; } = DefaultRelayUrl;
}
