using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyboxRemote.Api;

public sealed record StatusView(string State, string? Url, string? Message, bool Linked, PairingView? Pairing);

public sealed record InfoView(string? Url, string? Fingerprint, bool Connected);

[ApiController]
[Route("JellyboxRemote")]
public class JellyboxRemoteController(RelayStatusStore status, PairingService pairing) : ControllerBase
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
        await pairing.LogOutAsync().ConfigureAwait(false);
        return Status();
    }

    [HttpGet("Info")]
    [Authorize]
    public ActionResult<InfoView> GetInfo() =>
        new InfoView(status.Url, status.Fingerprint, status.State == "Connected");

    private StatusView Status()
    {
        var linked = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.Token);
        return new StatusView(status.State, status.Url, status.Message, linked, pairing.Current);
    }
}
