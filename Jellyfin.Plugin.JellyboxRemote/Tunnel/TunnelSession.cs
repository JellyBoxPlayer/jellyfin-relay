using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class TunnelSession(WebSocket socket, HttpClient client, Uri target, ILogger logger, Action<string> welcomed)
{
    public const int ReplacedCloseStatus = 4000;

    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, TunnelStream> _streams = new();

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
                welcomed(root.GetProperty("url").GetString() ?? string.Empty);
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
            case FrameType.Request when stream is null:
                Open(frame);
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

    private void Open(Frame frame)
    {
        RequestHead? head = null;
        try
        {
            head = JsonSerializer.Deserialize<RequestHead>(frame.Payload.Span);
        }
        catch (JsonException)
        {
        }

        if (head is null || string.IsNullOrEmpty(head.Method) || head.Path is null || head.Headers is null)
        {
            Send(Frame.Encode(FrameType.Reset, frame.Stream, "malformed request"u8));
            return;
        }

        var stream = new TunnelStream(frame.Stream, head, this, client, target);
        _streams[frame.Stream] = stream;
        _ = Task.Run(stream.RunAsync, CancellationToken.None);
    }
}
