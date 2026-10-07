using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyboxRemote.Cloud;

internal sealed record PairingCode(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("verify_url")] string VerifyUrl,
    [property: JsonPropertyName("verify_url_complete")] string VerifyUrlComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

internal enum CollectOutcome
{
    Pending,
    Linked,
    Expired,
}

internal sealed record CollectResult(CollectOutcome Outcome, string? Token = null);

internal sealed class PairingClient(HttpClient http, Uri cloud)
{
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<PairingCode> StartAsync(string serverId, string serverType, string serverName, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["server_id"] = serverId,
            ["server_type"] = serverType,
            ["server_name"] = serverName,
        };

        using var response = await http.PostAsJsonAsync(Endpoint("api/v1/pairings"), body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException("This cloud address does not offer remote access.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingCode>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The cloud sent an empty answer.");
    }

    public async Task<CollectResult> CollectAsync(string secret, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string> { ["secret"] = secret };
        using var response = await http.PostAsJsonAsync(Endpoint("api/v1/pairings/collect"), body, cancellationToken).ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var linked = await response.Content.ReadFromJsonAsync<Linked>(cancellationToken).ConfigureAwait(false);
                return string.IsNullOrEmpty(linked?.Token)
                    ? new CollectResult(CollectOutcome.Expired)
                    : new CollectResult(CollectOutcome.Linked, linked.Token);
            case HttpStatusCode.Accepted:
                return new CollectResult(CollectOutcome.Pending);
            case HttpStatusCode.Gone:
                return new CollectResult(CollectOutcome.Expired);
            default:
                throw new HttpRequestException("The cloud answered " + (int)response.StatusCode + ".");
        }
    }

    public async Task SignOutAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint("api/v1/sessions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<string?> WaitForTokenAsync(PairingCode code, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(code.Interval) < MinimumInterval ? MinimumInterval : TimeSpan.FromSeconds(code.Interval);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await CollectAsync(code.Secret, cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case CollectOutcome.Linked:
                        return result.Token;
                    case CollectOutcome.Expired:
                        return null;
                }
            }
            catch (HttpRequestException)
            {
            }
        }

        return null;
    }

    private Uri Endpoint(string path) => new(new Uri(cloud.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), path);

    private sealed record Linked([property: JsonPropertyName("token")] string? Token);
}
