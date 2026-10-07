using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jellyfin.Plugin.JellyboxRemote.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

public sealed class TunnelTests : IAsyncLifetime
{
    private static readonly byte[] Big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

    private readonly TaskCompletionSource _hangAborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TunnelStatus> _statuses = [];
    private readonly string _state = Path.Combine(Path.GetTempPath(), "jellybox-tunnel-" + Guid.NewGuid());
    private FakeRelay _relay = null!;
    private WebApplication _upstream = null!;
    private Uri _target = null!;
    private X509Certificate2 _certificate = null!;
    private Task _run = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _relay = await FakeRelay.StartAsync();
        _certificate = AgentCertificate.LoadOrCreate(Path.Combine(_state, "tunnel.pfx"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _upstream = builder.Build();
        _upstream.Run(UpstreamAsync);
        await _upstream.StartAsync();
        var address = _upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _target = new Uri(address);
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run;
        await _relay.DisposeAsync();
        await _upstream.DisposeAsync();
        _certificate.Dispose();
        Directory.Delete(_state, recursive: true);
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

        Assert.False(Frame.TryDecode(new byte[] { 1, 0, 0, 0, 1 }, out _));
        Assert.False(Frame.TryDecode(new byte[] { 3, 0 }, out _));
    }

    [Fact]
    public void The_certificate_is_kept_across_restarts()
    {
        using var again = AgentCertificate.LoadOrCreate(Path.Combine(_state, "tunnel.pfx"));
        Assert.Equal(AgentCertificate.Fingerprint(_certificate), AgentCertificate.Fingerprint(again));
        Assert.Equal(64, AgentCertificate.Fingerprint(again).Length);
    }

    [Fact]
    public async Task Connects_with_its_identity_and_fingerprint_and_reports_the_address()
    {
        Start();
        await _relay.NextConnectionAsync();

        var query = _relay.Queries[0];
        Assert.Contains("server_id=srv%201", query, StringComparison.Ordinal);
        Assert.Contains("server_type=jellyfin", query, StringComparison.Ordinal);
        Assert.Contains("fingerprint=" + AgentCertificate.Fingerprint(_certificate), query, StringComparison.Ordinal);
        await Eventually(() => _statuses.Any(s => s is { State: TunnelState.Connected, Key: "serverkey" }));
    }

    [Fact]
    public async Task The_app_speaks_tls_with_the_agent_and_reaches_the_server()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await using var tls = await relay.OpenTlsAsync(_certificate);
        var (status, body) = await HttpAsync(tls, "GET", "/echo?limit=5");

        Assert.Equal(200, status);
        Assert.Equal("/echo?limit=5", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_different_certificate_is_not_accepted_by_a_pinned_app()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();
        using var other = AgentCertificate.LoadOrCreate(Path.Combine(_state, "other.pfx"));

        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => relay.OpenTlsAsync(other));
    }

    [Fact]
    public async Task A_large_response_arrives_whole_within_the_window()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();

        await using var tls = await relay.OpenTlsAsync(_certificate);
        var (status, body) = await HttpAsync(tls, "GET", "/big");

        Assert.Equal(200, status);
        Assert.Equal(Big, body);
    }

    [Fact]
    public async Task An_upload_arrives_intact()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();
        var upload = RandomNumberGenerator.GetBytes(600 * 1024);

        await using var tls = await relay.OpenTlsAsync(_certificate);
        var (status, body) = await HttpAsync(tls, "POST", "/hash", upload);

        Assert.Equal(200, status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(upload)), Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task The_app_hanging_up_ends_the_server_connection()
    {
        Start();
        var relay = await _relay.NextConnectionAsync();
        var app = await relay.OpenAsync();
        var tls = new SslStream(app, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("key.tunnel.test");
        await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /hang HTTP/1.1\r\nHost: x\r\n\r\n"));
        await tls.ReadAtLeastAsync(new byte[256], 1);

        await app.ResetAsync();

        await _hangAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_unreachable_server_resets_the_stream()
    {
        _target = new Uri("http://127.0.0.1:1");
        Start();
        var relay = await _relay.NextConnectionAsync();
        var app = await relay.OpenAsync();
        var tls = new SslStream(app, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("key.tunnel.test");

        Assert.True(await app.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task It_reconnects_when_the_relay_goes_away()
    {
        Start();
        var first = await _relay.NextConnectionAsync();
        first.Close();

        var second = await _relay.NextConnectionAsync();
        Assert.Equal(2, _relay.Connects);

        await using var tls = await second.OpenTlsAsync(_certificate);
        var (status, _) = await HttpAsync(tls, "GET", "/echo");
        Assert.Equal(200, status);
    }

    private void Start()
    {
        var options = new TunnelOptions(
            new Uri("ws://relay.test/relay/agent"),
            "token",
            "srv 1",
            "jellyfin",
            "test",
            _target,
            _certificate);

        var runner = new TunnelRunner(options, NullLogger.Instance, s => { lock (_statuses) { _statuses.Add(s); } }, _relay.Connector)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(20),
        };

        _run = runner.RunAsync(_stop.Token);
    }

    private static async Task<(int Status, byte[] Body)> HttpAsync(Stream stream, string method, string path, byte[]? body = null)
    {
        var head = $"{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: {body?.Length ?? 0}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (body is not null)
        {
            await stream.WriteAsync(body);
        }

        using var response = new MemoryStream();
        await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(20));
        var bytes = response.ToArray();
        var split = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        var headers = Encoding.ASCII.GetString(bytes, 0, split);
        var status = int.Parse(headers.Split(' ')[1], CultureInfo.InvariantCulture);
        return (status, bytes[(split + 4)..]);
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

    private static Task WriteAsync(HttpContext context, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength = bytes.Length;
        return context.Response.Body.WriteAsync(bytes).AsTask();
    }

    private async Task UpstreamAsync(HttpContext context)
    {
        var request = context.Request;
        switch (request.Path.Value)
        {
            case "/echo":
                await WriteAsync(context, request.Path.Value + request.QueryString.Value);
                break;

            case "/hash":
                using (var buffer = new MemoryStream())
                {
                    await request.Body.CopyToAsync(buffer);
                    await WriteAsync(context, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
                }

                break;

            case "/big":
                context.Response.ContentLength = Big.Length;
                await context.Response.Body.WriteAsync(Big);
                break;

            case "/hang":
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
}
