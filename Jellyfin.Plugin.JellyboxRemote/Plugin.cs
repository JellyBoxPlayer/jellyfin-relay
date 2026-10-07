using System.Globalization;
using Jellyfin.Plugin.JellyboxRemote.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyboxRemote;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Token = new TokenStore(Path.Combine(DataFolderPath, "token"));
        if (Configuration.Token.Length > 0)
        {
            Token.Set(Configuration.Token);
            Configuration.Token = string.Empty;
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public TokenStore Token { get; }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration ours && ours.Token.Trim().Length > 0)
        {
            Token.Set(ours.Token);
            ours.Token = string.Empty;
        }

        base.UpdateConfiguration(configuration);
    }

    public override string Name => "JellyBox Remote Access";

    public override string Description => "Reach this server from JellyBox anywhere, without port forwarding.";

    public override Guid Id => Guid.Parse("5f0c3d2e-8b1a-4c6e-9f47-2a61d3b8e9c4");

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
        },
    ];
}
