using Jellyfin.Plugin.JellyboxRemote.Cloud;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote;

public sealed record PairingView(string State, string? Code, string? VerifyUrl, string? VerifyUrlComplete, string? Message);

public sealed class PairingService(IServerApplicationHost host, ILogger<PairingService> logger) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _gate = new();
    private CancellationTokenSource? _running;
    private PairingView? _current;

    public PairingView? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public async Task<PairingView> StartAsync()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded.");
        if (!CloudAddress.TryParse(plugin.Configuration.CloudUrl, out var cloud))
        {
            return Show(new PairingView("Failed", null, null, null, "The cloud address is not a valid URL."));
        }

        var run = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _running;
            _running = run;
        }

        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
            previous.Dispose();
        }

        var client = new PairingClient(Http, cloud);
        PairingCode code;
        try
        {
            var name = string.IsNullOrWhiteSpace(host.FriendlyName) ? "Jellyfin" : host.FriendlyName;
            code = await client.StartAsync(host.SystemId, "jellyfin", name, run.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Could not get a pairing code from {Cloud}: {Message}", cloud, e.Message);
            return Show(new PairingView("Failed", null, null, null, "Could not reach Jellybox Cloud: " + e.Message));
        }

        var view = Show(new PairingView("Waiting", code.Code, code.VerifyUrl, code.VerifyUrlComplete, null));
        _ = Task.Run(() => WaitAsync(client, code, plugin, run.Token), CancellationToken.None);
        return view;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _running?.Cancel();
            _current = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }

    private async Task WaitAsync(PairingClient client, PairingCode code, Plugin plugin, CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = await client.WaitForTokenAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token is null)
        {
            Show(new PairingView("Expired", null, null, null, "The code expired before it was entered."));
            return;
        }

        var configuration = plugin.Configuration;
        configuration.Token = token;
        plugin.UpdateConfiguration(configuration);
        logger.LogInformation("Linked this server to Jellybox Cloud");
        Cancel();
    }

    private PairingView Show(PairingView view)
    {
        lock (_gate)
        {
            _current = view;
        }

        return view;
    }
}
