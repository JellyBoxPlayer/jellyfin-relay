using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
        var welcome = Encoding.UTF8.GetBytes("""{"type":"welcome","url":"https://relay.test/r/key"}""");
        await socket.SendAsync(welcome, WebSocketMessageType.Text, true, CancellationToken.None);

        var connection = new RelayConnection(socket);
        await _connections.Writer.WriteAsync(connection);
        await connection.Closed.Task;
    }
}

internal sealed class RelayConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TaskCompletionSource Closed { get; } = new();

    public async Task SendAsync(byte[] frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task RequestAsync(uint stream, string method, string path, params (string Name, string Value)[] headers)
    {
        var head = new { method, path, headers = headers.Select(h => new[] { h.Name, h.Value }) };
        return SendAsync(Frame.Encode(FrameType.Request, stream, JsonSerializer.SerializeToUtf8Bytes(head)));
    }

    public async Task<Frame> ReceiveAsync()
    {
        var buffer = new byte[1024 * 1024];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), timeout.Token);
            total += result.Count;
        }
        while (!result.EndOfMessage);

        Assert.True(Frame.TryDecode(buffer.AsMemory(0, total).ToArray(), out var frame));
        return frame;
    }

    public async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> ReadResponseAsync(uint stream, bool grant = true)
    {
        Frame head;
        do
        {
            head = await ReceiveAsync();
        }
        while (head.Type == FrameType.Window || head.Stream != stream);

        Assert.Equal(FrameType.Response, head.Type);
        Assert.Equal(stream, head.Stream);
        using var json = JsonDocument.Parse(head.Payload);
        var status = json.RootElement.GetProperty("status").GetInt32();
        var headers = json.RootElement.GetProperty("headers").EnumerateArray()
            .ToDictionary(h => h[0].GetString()!, h => h[1].GetString()!);

        var body = new MemoryStream();
        while (true)
        {
            var frame = await ReceiveAsync();
            if (frame.Stream != stream)
            {
                continue;
            }

            if (frame.Type == FrameType.End)
            {
                break;
            }

            Assert.Equal(FrameType.Data, frame.Type);
            body.Write(frame.Payload.Span);
            if (grant)
            {
                await SendAsync(Frame.EncodeWindow(stream, frame.Payload.Length));
            }
        }

        return (status, headers, body.ToArray());
    }

    public void Close() => Closed.TrySetResult();
}
