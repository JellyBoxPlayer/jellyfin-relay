using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyboxRemote.Cloud;

namespace Jellyfin.Plugin.JellyboxRemote.Tests;

public sealed class PairingTests
{
    private static readonly Uri Cloud = new("https://cloud.test/base/");

    [Fact]
    public async Task Starting_sends_the_server_and_returns_the_code()
    {
        var cloud = new FakeCloud(_ => Json(HttpStatusCode.Created, """
            {"code":"ABCD-2345","secret":"mps_x","verify_url":"https://cloud.test/pair",
             "verify_url_complete":"https://cloud.test/pair?code=ABCD-2345","expires_in":600,"interval":3}
            """));

        var code = await Client(cloud).StartAsync("srv", "jellyfin", "Living room", CancellationToken.None);

        Assert.Equal("ABCD-2345", code.Code);
        Assert.Equal("mps_x", code.Secret);
        var (path, body) = Assert.Single(cloud.Requests);
        Assert.Equal("/base/api/v1/pairings", path);
        Assert.Equal("Living room", body.GetProperty("server_name").GetString());
        Assert.Equal("srv", body.GetProperty("server_id").GetString());
    }

    [Fact]
    public async Task Waiting_keeps_asking_until_the_owner_approves()
    {
        var answers = new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Accepted, """{"status":"pending"}"""),
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            Json(HttpStatusCode.OK, """{"token":"mib_token","name":"Living room (Jellyfin)"}"""),
        ]);
        var cloud = new FakeCloud(_ => answers.Dequeue());

        var token = await Client(cloud).WaitForTokenAsync(Code(), CancellationToken.None);

        Assert.Equal("mib_token", token);
        Assert.Equal(3, cloud.Requests.Count);
        Assert.All(cloud.Requests, r => Assert.Equal("mps_x", r.Body.GetProperty("secret").GetString()));
    }

    [Fact]
    public async Task An_expired_code_stops_the_wait()
    {
        var cloud = new FakeCloud(_ => Json(HttpStatusCode.Gone, """{"error":{"code":"expired"}}"""));

        Assert.Null(await Client(cloud).WaitForTokenAsync(Code(), CancellationToken.None));
        Assert.Single(cloud.Requests);
    }

    [Fact]
    public async Task A_cloud_without_remote_access_says_so()
    {
        var cloud = new FakeCloud(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(cloud).StartAsync("srv", "jellyfin", "x", CancellationToken.None));
        Assert.Contains("remote access", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://cloud.jellybox.app", "wss://cloud.jellybox.app/relay/agent")]
    [InlineData("http://192.168.1.5:4000/", "ws://192.168.1.5:4000/relay/agent")]
    public void The_relay_lives_under_the_cloud_address(string cloud, string relay)
    {
        Assert.True(CloudAddress.TryParse(cloud, out var parsed));
        Assert.Equal(new Uri(relay), CloudAddress.Relay(parsed));
    }

    [Fact]
    public void Only_web_addresses_count_as_a_cloud()
    {
        Assert.False(CloudAddress.TryParse("cloud.jellybox.app", out _));
        Assert.False(CloudAddress.TryParse("ftp://cloud.jellybox.app", out _));
    }

    private static PairingClient Client(FakeCloud cloud) =>
        new(new HttpClient(cloud), Cloud) { MinimumInterval = TimeSpan.FromMilliseconds(10) };

    private static PairingCode Code() => new("ABCD-2345", "mps_x", "u", "u", 30, 0);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeCloud(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, JsonDocument.Parse(body).RootElement.Clone()));
            return answer(request);
        }
    }
}
