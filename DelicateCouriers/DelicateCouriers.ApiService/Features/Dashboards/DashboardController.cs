using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.ApiService.Features.Dashboards;

/// <summary>
/// Hangfire dashboard helper. The previous "mint a backend JWT and redirect
/// with it in the URL" flow has been removed because Supabase JWTs are
/// minted by Supabase, not by us. To open the dashboard, sign in to the
/// frontend as Admin/SuperAdmin and navigate to /hangfire — the same
/// Supabase access token is forwarded by the Hangfire dashboard filter.
/// </summary>
[ApiController]
[Route("admin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class DashboardController : ControllerBase
{
    /// <summary>
    /// Bounces the caller to /hangfire. Only callable by an already-authenticated
    /// Admin/SuperAdmin (enforced by the [Authorize] attribute above), so it
    /// never escalates privilege.
    /// </summary>
    [HttpGet("hangfire-login")]
    public IActionResult HangfireLogin([FromServices] IConfiguration config)
    {
        var apiBaseUrl = config["PublicUrls:ApiBaseUrl"]?.TrimEnd('/')
                         ?? $"{Request.Scheme}://{Request.Host}";
        return Redirect($"{apiBaseUrl}/hangfire");
    }
}
