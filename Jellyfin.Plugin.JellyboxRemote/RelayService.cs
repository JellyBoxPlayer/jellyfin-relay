using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.JellyboxRemote.Cloud;
using Jellyfin.Plugin.JellyboxRemote.Tunnel;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote;

internal sealed class RelayService(
    IServerApplicationHost host,
    IConfigurationManager configurationManager,
    RelayStatusStore status,
    ILogger<RelayService> logger) : BackgroundService
{
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        using var certificate = AgentCertificate.LoadOrCreate(Path.Combine(plugin.DataFolderPath, "tunnel.pfx"));
        status.Fingerprint = AgentCertificate.Fingerprint(certificate);

        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, BasePluginConfiguration configuration) => changed.TrySetResult();
            void OnNetworkChanged(object? sender, ConfigurationUpdateEventArgs e)
            {
                if (e.Key == "network")
                {
                    changed.TrySetResult();
                }
            }

            plugin.ConfigurationChanged += OnChanged;
            configurationManager.NamedConfigurationUpdated += OnNetworkChanged;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var remoteAllowed = RemoteAccessAllowed();
            var tunnel = remoteAllowed ? Start(plugin, certificate, run.Token) : Task.CompletedTask;
            try
            {
                await changed.Task.WaitAsync(remoteAllowed ? Timeout.InfiniteTimeSpan : Recheck, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                plugin.ConfigurationChanged -= OnChanged;
                configurationManager.NamedConfigurationUpdated -= OnNetworkChanged;
                await run.CancelAsync().ConfigureAwait(false);
                await tunnel.ConfigureAwait(false);
            }
        }
    }

    private bool RemoteAccessAllowed()
    {
        var network = configurationManager.GetNetworkConfiguration();
        if (!network.EnableRemoteAccess)
        {
            status.Report(new TunnelStatus(
                TunnelState.Disconnected,
                Message: "Jellyfin's remote access is off (Dashboard → Networking → Allow remote connections). Turn it on to use the tunnel."));
            return false;
        }

        if (!network.KnownProxies.Contains("127.0.0.1", StringComparer.Ordinal))
        {
            network.KnownProxies = [.. network.KnownProxies, "127.0.0.1"];
            configurationManager.SaveConfiguration("network", network);
            status.Notice = "Restart Jellyfin once: the plugin added itself as a known proxy, so Jellyfin can tell listeners' real addresses apart from local ones.";
            logger.LogInformation("Added 127.0.0.1 to Jellyfin's known proxies; a restart applies it");
        }

        return true;
    }

    private Task Start(Plugin plugin, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var configuration = plugin.Configuration;
        var token = configuration.Token.Trim();
        if (token.Length == 0)
        {
            status.Report(new TunnelStatus(TunnelState.NotConfigured));
            return Task.CompletedTask;
        }

        if (!CloudAddress.TryParse(configuration.CloudUrl, out var cloud))
        {
            status.Report(new TunnelStatus(TunnelState.Disconnected, Message: "The cloud address is not a valid URL."));
            return Task.CompletedTask;
        }

        var options = new TunnelOptions(
            CloudAddress.Relay(cloud),
            token,
            host.SystemId,
            "jellyfin",
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0",
            new Uri(host.GetLocalApiUrl("127.0.0.1", Uri.UriSchemeHttp, host.HttpPort)),
            certificate);

        return new TunnelRunner(options, logger, status.Report).RunAsync(cancellationToken);
    }
}
