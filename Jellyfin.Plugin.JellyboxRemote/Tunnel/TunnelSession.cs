using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class TunnelSession(
    WebSocket socket,
    Uri target,
    X509Certificate2 certificate,
    ILogger logger,
    Action<string> welcomed)
{
    public const int ReplacedCloseStatus = 4000;

    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, TunnelPipe> _streams = new();

    public bool Replaced => (int?)socket.CloseStatus == ReplacedCloseStatus;

    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public void Forget(uint id) => _streams.TryRemove(id, out _);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = WriteAsync(session.Token);
        try
        {
            await ReadAsync(session.Token).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            foreach (var stream in _streams.Values)
            {
                stream.Cancel();
            }

            _outbox.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
            }
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(Frame.MaxData + Frame.HeaderSize);
        while (true)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(Frame.MaxData), cancellationToken).ConfigureAwait(false);
                buffer.Advance(result.Count);
            }
            while (!result.EndOfMessage);

            switch (result.MessageType)
            {
                case WebSocketMessageType.Close:
                    return;
                case WebSocketMessageType.Text:
                    OnText(buffer.WrittenSpan);
                    break;
                default:
                    if (Frame.TryDecode(buffer.WrittenSpan.ToArray(), out var frame))
                    {
                        Dispatch(frame);
                    }

                    break;
            }
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnText(ReadOnlySpan<byte> text)
    {
        try
        {
            using var message = JsonDocument.Parse(text.ToArray());
            var root = message.RootElement;
            if (root.GetProperty("type").GetString() == "welcome")
            {
                welcomed(root.GetProperty("key").GetString() ?? string.Empty);
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogDebug("Ignoring relay message it could not read");
        }
    }

    private void Dispatch(Frame frame)
    {
        _streams.TryGetValue(frame.Stream, out var stream);
        switch (frame.Type)
        {
            case FrameType.Connect when stream is null:
                Open(frame.Stream, Encoding.UTF8.GetString(frame.Payload.Span));
                break;
            case FrameType.Data:
                stream?.OnData(frame.Payload);
                break;
            case FrameType.End:
                stream?.OnEnd();
                break;
            case FrameType.Window when frame.Payload.Length >= 4:
                stream?.OnWindow(frame.WindowBytes);
                break;
            case FrameType.Reset:
                if (_streams.TryRemove(frame.Stream, out var reset))
                {
                    reset.Cancel();
                }

                break;
        }
    }

    private void Open(uint id, string clientIp)
    {
        var pipe = new TunnelPipe(id, this, target, certificate, clientIp.Length == 0 ? "unknown" : clientIp, logger);
        _streams[id] = pipe;
        _ = Task.Run(pipe.RunAsync, CancellationToken.None);
    }
}
