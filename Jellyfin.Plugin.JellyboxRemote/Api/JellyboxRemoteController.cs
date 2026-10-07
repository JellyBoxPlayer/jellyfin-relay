using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyboxRemote.Api;

public sealed record SeatView(string SeatId, string Label, string Url);

public sealed record StatusView(
    string State,
    string? Message,
    string? Notice,
    bool Linked,
    PairingView? Pairing,
    IReadOnlyList<SeatView> Seats);

public sealed record InfoView(string? Url, string? Fingerprint, bool Connected, string? Reason);

[ApiController]
[Route("JellyboxRemote")]
public class JellyboxRemoteController(RelayStatusStore status, PairingService pairing, SeatService seats) : ControllerBase
{
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<StatusView> GetStatus() => Status();

    [HttpPost("Pairing")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<StatusView>> StartPairing()
    {
        await pairing.StartAsync().ConfigureAwait(false);
        return Status();
    }

    [HttpDelete("Pairing")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<StatusView> CancelPairing()
    {
        pairing.Cancel();
        return Status();
    }

    [HttpPost("LogOut")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<StatusView>> LogOut()
    {
        seats.Forget();
        await pairing.LogOutAsync().ConfigureAwait(false);
        return Status();
    }

    [HttpDelete("Seats/{seatId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<StatusView>> ReleaseSeat(string seatId, CancellationToken cancellationToken)
    {
        await seats.ReleaseAsync(seatId, cancellationToken).ConfigureAwait(false);
        return Status();
    }

    [HttpGet("Info")]
    [Authorize]
    public async Task<ActionResult<InfoView>> GetInfo(CancellationToken cancellationToken)
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return new InfoView(null, status.Fingerprint, status.State == "Connected", "unknown_user");
        }

        var seat = await seats.ForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return new InfoView(seat.Url, status.Fingerprint, status.State == "Connected", seat.Reason);
    }

    private StatusView Status()
    {
        var linked = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.Token);
        var held = seats.Seats.Select(s => new SeatView(s.SeatId, s.Label, s.Url)).ToList();
        return new StatusView(status.State, status.Message, status.Notice, linked, pairing.Current, held);
    }
}
