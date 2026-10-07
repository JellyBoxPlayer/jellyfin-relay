using Jellyfin.Plugin.JellyboxRemote.Cloud;
using Jellyfin.Plugin.JellyboxRemote.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyboxRemote;

public sealed record SeatLookup(string? Url, string? Reason);

public sealed class SeatService(IServerApplicationHost host, IUserManager users, ILogger<SeatService> logger) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<SeatAssignment> Seats => Plugin.Instance?.Configuration.Seats ?? [];

    public async Task<SeatLookup> ForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrWhiteSpace(plugin.Configuration.Token))
        {
            return new SeatLookup(null, "unlinked");
        }

        var user = users.GetUserById(userId);
        if (user is null)
        {
            return new SeatLookup(null, "unknown_user");
        }

        if (string.IsNullOrEmpty(user.Password))
        {
            return new SeatLookup(null, "no_password");
        }

        var existing = plugin.Configuration.Seats.FirstOrDefault(s => s.UserId == userId.ToString("N"));
        if (existing is not null)
        {
            return new SeatLookup(existing.Url, null);
        }

        if (!CloudAddress.TryParse(plugin.Configuration.CloudUrl, out var cloud))
        {
            return new SeatLookup(null, "cloud_unreachable");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raced = plugin.Configuration.Seats.FirstOrDefault(s => s.UserId == userId.ToString("N"));
            if (raced is not null)
            {
                return new SeatLookup(raced.Url, null);
            }

            var client = new PairingClient(Http, cloud);
            var result = await client.RequestSeatAsync(
                plugin.Configuration.Token.Trim(),
                host.SystemId,
                userId.ToString("N"),
                user.Username,
                cancellationToken).ConfigureAwait(false);

            if (result.Seat is null)
            {
                return new SeatLookup(null, result.Reason);
            }

            var configuration = plugin.Configuration;
            configuration.Seats.Add(new SeatAssignment
            {
                UserId = userId.ToString("N"),
                SeatId = result.Seat.Id,
                Url = result.Seat.Url,
                Label = result.Seat.Label,
            });
            plugin.SaveConfiguration();
            logger.LogInformation("Seat granted to {User}", user.Username);
            return new SeatLookup(result.Seat.Url, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Could not ask Jellybox Cloud for a seat: {Message}", e.Message);
            return new SeatLookup(null, "cloud_unreachable");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(string seatId, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var configuration = plugin.Configuration;
        var seat = configuration.Seats.FirstOrDefault(s => s.SeatId == seatId);
        if (seat is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(configuration.Token) && CloudAddress.TryParse(configuration.CloudUrl, out var cloud))
        {
            try
            {
                await new PairingClient(Http, cloud).ReleaseSeatAsync(configuration.Token.Trim(), seatId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Could not tell Jellybox Cloud the seat was released: {Message}", e.Message);
            }
        }

        configuration.Seats.Remove(seat);
        plugin.SaveConfiguration();
    }

    public void Dispose() => _gate.Dispose();

    public void Forget()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || plugin.Configuration.Seats.Count == 0)
        {
            return;
        }

        plugin.Configuration.Seats.Clear();
        plugin.SaveConfiguration();
    }
}
