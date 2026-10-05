using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyboxRemote.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

public sealed class TunnelTests : IAsyncLifetime
{
    private static readonly byte[] Big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

    private readonly TaskCompletionSource _hangAborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TunnelStatus> _statuses = [];
    private FakeRelay _relay = null!;
    private IHost _upstream = null!;
    private Task _run = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _relay = await FakeRelay.StartAsync();
        _upstream = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(UpstreamAsync)))
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run;
        await _relay.DisposeAsync();
        await _upstream.StopAsync();
        _upstream.Dispose();
    }

    [Fact]
    public void Frames_round_trip()
    {
        var encoded = Frame.Encode(FrameType.Data, 4_000_000_000, "payload"u8);
        Assert.True(Frame.TryDecode(encoded, out var frame));
        Assert.Equal(FrameType.Data, frame.Type);
        Assert.Equal(4_000_000_000u, frame.Stream);
        Assert.Equal("payload", Encoding.UTF8.GetString(frame.Payload.Span));

        Assert.True(Frame.TryDecode(Frame.EncodeWindow(7, 65_536), out var window));
        Assert.Equal(65_536, window.WindowBytes);

        Assert.False(Frame.TryDecode(new byte[] { 9, 0, 0, 0, 1 }, out _));
        Assert.False(Frame.TryDecode(new byte[] { 1, 0 }, out _));
    }

    [Fact]
    public async Task Connects_with_its_identity_and_reports_the_address()
    {
        Start();
        await _relay.NextConnectionAsync();

        Assert.Contains("server_id=srv%201", _relay.Queries[0], StringComparison.Ordinal);
        Assert.Contains("server_type=jellyfin", _relay.Queries[0], StringComparison.Ordinal);
        await Eventually(() => _statuses.Any(s => s is { State: TunnelState.Connected, Url: "https://relay.test/r/key" }));
    }

    [Fact]
    public async Task A_get_reaches_the_server_under_its_base_path()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await relay.RequestAsync(1, "GET", "/echo?limit=5&x=a%20b", ("x-emby-token", "abc"), ("host", "relay.test"), ("connection", "keep-alive"));
        await relay.SendAsync(Frame.Encode(FrameType.End, 1));

        var (status, headers, body) = await relay.ReadResponseAsync(1);
        Assert.Equal(200, status);
        Assert.StartsWith("application/json", headers["content-type"], StringComparison.Ordinal);

        using var echo = JsonDocument.Parse(body);
        Assert.Equal("/jf/echo", echo.RootElement.GetProperty("path").GetString());
        Assert.Equal("?limit=5&x=a%20b", echo.RootElement.GetProperty("query").GetString());
        Assert.Equal("abc", echo.RootElement.GetProperty("token").GetString());
        Assert.NotEqual("relay.test", echo.RootElement.GetProperty("host").GetString());
    }

    [Fact]
    public async Task An_upload_arrives_intact_and_is_granted_back()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();
        var upload = RandomNumberGenerator.GetBytes(600 * 1024);

        await relay.RequestAsync(1, "POST", "/hash", ("content-type", "application/octet-stream"));
        var window = Frame.InitialWindow;
        var granted = 0;
        for (var offset = 0; offset < upload.Length;)
        {
            if (window == 0)
            {
                var grant = await relay.ReceiveAsync();
                Assert.Equal(FrameType.Window, grant.Type);
                window += grant.WindowBytes;
                granted += grant.WindowBytes;
                continue;
            }

            var size = Math.Min(Math.Min(window, Frame.MaxData), upload.Length - offset);
            await relay.SendAsync(Frame.Encode(FrameType.Data, 1, upload.AsSpan(offset, size)));
            offset += size;
            window -= size;
        }

        await relay.SendAsync(Frame.Encode(FrameType.End, 1));

        var (status, _, body) = await relay.ReadResponseAsync(1);
        Assert.Equal(200, status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(upload)), Encoding.UTF8.GetString(body));
        Assert.True(granted > 0);
    }

    [Fact]
    public async Task A_large_response_never_exceeds_the_window()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await relay.RequestAsync(1, "GET", "/big");
        await relay.SendAsync(Frame.Encode(FrameType.End, 1));

        var head = await relay.ReceiveAsync();
        Assert.Equal(FrameType.Response, head.Type);

        var received = new MemoryStream();
        var inFlight = 0;
        var peak = 0;
        while (true)
        {
            var frame = await relay.ReceiveAsync();
            if (frame.Type == FrameType.End)
            {
                break;
            }

            inFlight += frame.Payload.Length;
            peak = Math.Max(peak, inFlight);
            Assert.True(inFlight <= Frame.InitialWindow, $"{inFlight} bytes unacknowledged");
            received.Write(frame.Payload.Span);

            if (inFlight >= Frame.InitialWindow)
            {
                await relay.SendAsync(Frame.EncodeWindow(1, inFlight));
                inFlight = 0;
            }
        }

        Assert.Equal(Big, received.ToArray());
        Assert.Equal(Frame.InitialWindow, peak);
    }

    [Fact]
    public async Task A_range_response_keeps_its_status_and_length()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await relay.RequestAsync(1, "GET", "/range", ("range", "bytes=10-19"));
        await relay.SendAsync(Frame.Encode(FrameType.End, 1));

        var (status, headers, body) = await relay.ReadResponseAsync(1);
        Assert.Equal(206, status);
        Assert.Equal("10", headers["content-length"]);
        Assert.Equal("bytes 10-19/100", headers["content-range"]);
        Assert.Equal("0123456789", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_reset_cancels_the_upstream_request()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await relay.RequestAsync(1, "GET", "/hang");
        await relay.SendAsync(Frame.Encode(FrameType.End, 1));
        var head = await relay.ReceiveAsync();
        Assert.Equal(FrameType.Response, head.Type);

        await relay.SendAsync(Frame.Encode(FrameType.Reset, 1));
        await _hangAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await relay.SendAsync(Frame.Encode(FrameType.Reset, 1));
        await relay.RequestAsync(2, "GET", "/range");
        await relay.SendAsync(Frame.Encode(FrameType.End, 2));
        var (status, _, _) = await relay.ReadResponseAsync(2);
        Assert.Equal(206, status);
    }

    [Fact]
    public async Task An_unreachable_server_answers_502()
    {
        Start(new HttpClient(new FailingHandler()));
        var relay = await _relay.NextConnectionAsync();

        await relay.RequestAsync(1, "GET", "/echo");
        await relay.SendAsync(Frame.Encode(FrameType.End, 1));

        var (status, headers, body) = await relay.ReadResponseAsync(1);
        Assert.Equal(502, status);
        Assert.Equal(body.Length.ToString(CultureInfo.InvariantCulture), headers["content-length"]);
        Assert.Contains("unreachable", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_request_is_reset()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await relay.SendAsync(Frame.Encode(FrameType.Request, 1, "not json"u8));
        var frame = await relay.ReceiveAsync();
        Assert.Equal(FrameType.Reset, frame.Type);
        Assert.Equal(1u, frame.Stream);
    }

    [Fact]
    public async Task It_reconnects_when_the_relay_goes_away()
    {
        Start();
        var first = await _relay.NextConnectionAsync();
        first.Close();

        var second = await _relay.NextConnectionAsync();
        Assert.Equal(2, _relay.Connects);

        await second.RequestAsync(1, "GET", "/range");
        await second.SendAsync(Frame.Encode(FrameType.End, 1));
        var (status, _, _) = await second.ReadResponseAsync(1);
        Assert.Equal(206, status);
    }

    private void Start(HttpClient? upstream = null)
    {
        var options = new TunnelOptions(
            new Uri("ws://relay.test/relay/agent"),
            "token",
            "srv 1",
            "jellyfin",
            "test",
            new Uri("http://upstream.test/jf/"));

        var client = upstream ?? new HttpClient(_upstream.GetTestServer().CreateHandler());
        var runner = new TunnelRunner(options, client, NullLogger.Instance, s => { lock (_statuses) { _statuses.Add(s); } }, _relay.Connector)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(20),
        };

        _run = runner.RunAsync(_stop.Token);
    }

    private async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (_statuses)
            {
                if (condition())
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail("condition never became true");
    }

    private async Task UpstreamAsync(HttpContext context)
    {
        var request = context.Request;
        switch (request.Path.Value)
        {
            case "/jf/echo":
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    path = request.Path.Value,
                    query = request.QueryString.Value,
                    token = request.Headers["x-emby-token"].ToString(),
                    host = request.Host.Value,
                });
                break;

            case "/jf/hash":
                using (var buffer = new MemoryStream())
                {
                    await request.Body.CopyToAsync(buffer);
                    await context.Response.WriteAsync(Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
                }

                break;

            case "/jf/big":
                context.Response.ContentLength = Big.Length;
                await context.Response.Body.WriteAsync(Big);
                break;

            case "/jf/range":
                context.Response.StatusCode = 206;
                context.Response.ContentLength = 10;
                context.Response.Headers.ContentRange = "bytes 10-19/100";
                await context.Response.WriteAsync("0123456789");
                break;

            case "/jf/hang":
                await context.Response.Body.WriteAsync(new byte[] { 1 });
                await context.Response.Body.FlushAsync();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    _hangAborted.TrySetResult();
                }

                break;

            default:
                context.Response.StatusCode = 404;
                break;
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
