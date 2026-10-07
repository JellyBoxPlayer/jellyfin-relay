using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class TunnelPipe(uint id, TunnelSession session, Uri target, X509Certificate2 certificate, ILogger logger)
    : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly MuxStream _app = new(id, session);
    private readonly CancellationTokenSource _cancel = new();

    public void OnData(ReadOnlyMemory<byte> data)
    {
        if (!_app.Deliver(data))
        {
            session.Send(Frame.Encode(FrameType.Reset, id, "flow control window exceeded"u8));
            Cancel();
        }
    }

    public void OnEnd() => _app.Finish();

    public void OnWindow(int bytes) => _app.Grant(bytes);

    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _app.Finish();
    }

    public void Dispose() => _cancel.Dispose();

    public async Task RunAsync()
    {
        var token = _cancel.Token;
        try
        {
            var tls = new SslStream(_app, leaveInnerStreamOpen: true);
            await using (tls.ConfigureAwait(false))
            {
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshake.CancelAfter(HandshakeTimeout);
                    var options = new SslServerAuthenticationOptions { ServerCertificate = certificate };
                    await tls.AuthenticateAsServerAsync(options, handshake.Token).ConfigureAwait(false);
                }

                using var upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(target.Host, target.Port, token).ConfigureAwait(false);
                var server = upstream.GetStream();

                var fromApp = CopyThenAsync(tls, server, () => upstream.Client.Shutdown(SocketShutdown.Send), token);
                var toApp = CopyThenAsync(server, tls, () => tls.ShutdownAsync(), token);
                await Task.WhenAll(fromApp, toApp).ConfigureAwait(false);
            }

            session.Send(Frame.Encode(FrameType.End, id));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug("Tunnel stream {Id} failed: {Message}", id, e.Message);
            session.Send(Frame.Encode(FrameType.Reset, id));
        }
        finally
        {
            session.Forget(id);
            Dispose();
        }
    }

    private static Task CopyThenAsync(Stream from, Stream to, Action then, CancellationToken token) =>
        CopyThenAsync(from, to, () =>
        {
            then();
            return Task.CompletedTask;
        }, token);

    private static async Task CopyThenAsync(Stream from, Stream to, Func<Task> then, CancellationToken token)
    {
        try
        {
            await from.CopyToAsync(to, token).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        await then().ConfigureAwait(false);
    }
}
