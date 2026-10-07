using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Jellyfin.Plugin.JellyboxRemote.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

internal sealed class FakeRelay : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Channel<RelayConnection> _connections = Channel.CreateUnbounded<RelayConnection>();

    private FakeRelay(IHost host) => _host = host;

    public int Connects { get; private set; }

    public List<string> Queries { get; } = [];

    public static async Task<FakeRelay> StartAsync()
    {
        FakeRelay? relay = null;
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            {
                app.UseWebSockets();
                app.Run(context => relay!.AcceptAsync(context));
            }))
            .StartAsync();
        relay = new FakeRelay(host);
        return relay;
    }

    public RelayConnector Connector => async (uri, token, cancellationToken) =>
    {
        var client = _host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        return await client.ConnectAsync(uri, cancellationToken);
    };

    public async Task<RelayConnection> NextConnectionAsync() =>
        await _connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task AcceptAsync(HttpContext context)
    {
        Connects++;
        Queries.Add(context.Request.QueryString.Value ?? string.Empty);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var welcome = Encoding.UTF8.GetBytes("""{"type":"welcome","url":"https://key.tunnel.test"}""");
        await socket.SendAsync(welcome, WebSocketMessageType.Text, true, CancellationToken.None);

        var connection = new RelayConnection(socket);
        await _connections.Writer.WriteAsync(connection);
        await connection.RunAsync();
    }
}

internal sealed class RelayConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, AppStream> _streams = new();
    private readonly Channel<Frame> _unrouted = Channel.CreateUnbounded<Frame>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _next = 1;

    public void Close() => _closed.TrySetResult();

    public async Task SendAsync(byte[] frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<AppStream> OpenAsync()
    {
        var id = _next++;
        var stream = new AppStream(id, this);
        _streams[id] = stream;
        await SendAsync(Frame.Encode(FrameType.Connect, id));
        return stream;
    }

    public async Task<SslStream> OpenTlsAsync(X509Certificate2? expected = null)
    {
        var tls = new SslStream(await OpenAsync(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            expected is null || certificate?.GetRawCertData().AsSpan().SequenceEqual(expected.RawData) == true);
        await tls.AuthenticateAsClientAsync("key.tunnel.test").WaitAsync(TimeSpan.FromSeconds(10));
        return tls;
    }

    public async Task<Frame> NextUnroutedAsync() =>
        await _unrouted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task RunAsync()
    {
        var reading = ReadAsync();
        await Task.WhenAny(reading, _closed.Task);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[1024 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var total = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
                total += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close || !Frame.TryDecode(buffer.AsMemory(0, total).ToArray(), out var frame))
            {
                continue;
            }

            if (_streams.TryGetValue(frame.Stream, out var stream))
            {
                stream.Receive(frame);
            }
            else
            {
                await _unrouted.Writer.WriteAsync(frame);
            }
        }
    }
}

internal sealed class AppStream(uint id, RelayConnection relay) : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly FlowWindow _window = new(Frame.InitialWindow);
    private ReadOnlyMemory<byte> _current;

    public TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Acknowledged { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Receive(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameType.Data:
                _incoming.Writer.TryWrite(frame.Payload.ToArray());
                break;
            case FrameType.Window:
                Acknowledged += frame.WindowBytes;
                _window.Grant(frame.WindowBytes);
                break;
            case FrameType.End:
                Ended.TrySetResult(false);
                _incoming.Writer.TryComplete();
                break;
            case FrameType.Reset:
                Ended.TrySetResult(true);
                _incoming.Writer.TryComplete();
                break;
        }
    }

    public Task HangUpAsync() => relay.SendAsync(Frame.Encode(FrameType.End, id));

    public Task ResetAsync() => relay.SendAsync(Frame.Encode(FrameType.Reset, id));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            _incoming.Reader.TryRead(out var chunk);
            _current = chunk;
            await relay.SendAsync(Frame.EncodeWindow(id, chunk!.Length));
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var allowed = await _window.TakeAsync(Math.Min(buffer.Length - offset, Frame.MaxData), cancellationToken);
            await relay.SendAsync(Frame.Encode(FrameType.Data, id, buffer.Span.Slice(offset, allowed)));
            offset += allowed;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
