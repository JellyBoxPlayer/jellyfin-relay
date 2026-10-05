using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyboxRemote.Api;

[ApiController]
[Route("JellyboxRemote")]
[Authorize(Policy = "RequiresElevation")]
public class JellyboxRemoteController(RelayStatusStore status) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<RelayStatusStore> GetStatus() => status;
}
