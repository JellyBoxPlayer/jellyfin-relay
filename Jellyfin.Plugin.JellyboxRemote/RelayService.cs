using System.Net;
using Jellyfin.Plugin.JellyboxRemote.Cloud;
using Jellyfin.Plugin.JellyboxRemote.Tunnel;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote;

internal sealed class RelayService(IServerApplicationHost host, RelayStatusStore status, ILogger<RelayService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        };
        using var upstream = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, BasePluginConfiguration configuration) => changed.TrySetResult();

            plugin.ConfigurationChanged += OnChanged;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var tunnel = Start(plugin, upstream, run.Token);
            try
            {
                await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                plugin.ConfigurationChanged -= OnChanged;
                await run.CancelAsync().ConfigureAwait(false);
                await tunnel.ConfigureAwait(false);
            }
        }
    }

    private Task Start(Plugin plugin, HttpClient upstream, CancellationToken cancellationToken)
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
            new Uri(host.GetLocalApiUrl("127.0.0.1", Uri.UriSchemeHttp, host.HttpPort)));

        return new TunnelRunner(options, upstream, logger, status.Report).RunAsync(cancellationToken);
    }
}
