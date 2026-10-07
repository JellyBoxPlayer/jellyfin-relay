using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyboxRemote.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultCloudUrl = "https://cloud.jellybox.app";

    public string Token { get; set; } = string.Empty;

    public string CloudUrl { get; set; } = DefaultCloudUrl;

    public List<SeatAssignment> Seats { get; set; } = [];
}

public class SeatAssignment
{
    public string UserId { get; set; } = string.Empty;

    public string SeatId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}
