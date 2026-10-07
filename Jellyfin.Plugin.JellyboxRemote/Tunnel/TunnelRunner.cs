using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class TokenRefusedException : Exception
{
}

internal delegate Task<WebSocket> RelayConnector(Uri relay, string token, CancellationToken cancellationToken);

internal sealed class TunnelRunner(
    TunnelOptions options,
    ILogger logger,
    Action<TunnelStatus> report,
    RelayConnector? connect = null)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);

    private readonly RelayConnector _connect = connect ?? ConnectAsync;

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            report(new TunnelStatus(TunnelState.Connecting));
            var started = DateTime.UtcNow;
            TimeSpan wait;

            try
            {
                using var socket = await _connect(RelayUri(), options.Token, cancellationToken).ConfigureAwait(false);
                var session = new TunnelSession(socket, options.Target, options.Certificate, logger, key =>
                {
                    logger.LogInformation("JellyBox relay connected as server {Key}", key);
                    report(new TunnelStatus(TunnelState.Connected, key));
                });

                await session.RunAsync(cancellationToken).ConfigureAwait(false);

                if (DateTime.UtcNow - started > StableConnection)
                {
                    backoff = InitialBackoff;
                }

                wait = session.Replaced ? MaxBackoff : backoff;
                var reason = session.Replaced ? "another copy of this server connected to the relay" : "connection closed";
                logger.LogWarning("JellyBox relay disconnected: {Reason}", reason);
                report(new TunnelStatus(TunnelState.Disconnected, Message: reason));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TokenRefusedException)
            {
                logger.LogWarning("The JellyBox relay refused the token; check it on the plugin page");
                report(new TunnelStatus(TunnelState.TokenRefused));
                wait = MaxBackoff;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
            {
                logger.LogWarning("JellyBox relay connection failed: {Message}", e.Message);
                report(new TunnelStatus(TunnelState.Disconnected, Message: e.Message));
                wait = backoff;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

            try
            {
                await Task.Delay(Jitter(wait), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Uri RelayUri()
    {
        var query = $"server_id={Uri.EscapeDataString(options.ServerId)}" +
                    $"&server_type={Uri.EscapeDataString(options.ServerType)}" +
                    $"&version={Uri.EscapeDataString(options.Version)}" +
                    $"&fingerprint={AgentCertificate.Fingerprint(options.Certificate)}";
        return new UriBuilder(options.RelayUrl) { Query = query }.Uri;
    }

    private static TimeSpan Jitter(TimeSpan wait) => wait * (0.5 + (Random.Shared.NextDouble() * 0.5));

    private static async Task<WebSocket> ConnectAsync(Uri relay, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(relay, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            socket.Dispose();
            throw new TokenRefusedException();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
