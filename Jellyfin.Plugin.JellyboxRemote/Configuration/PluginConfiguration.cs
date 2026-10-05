using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyboxRemote.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultCloudUrl = "https://cloud.jellybox.app";

    public string Token { get; set; } = string.Empty;

    public string CloudUrl { get; set; } = DefaultCloudUrl;
}
