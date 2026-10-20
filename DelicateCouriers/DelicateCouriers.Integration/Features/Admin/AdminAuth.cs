using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DelicateCouriers.Integration.Features.Admin;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// The key that opens the admin API. From the environment, not a committed settings file.
    ///
    /// A shared key, not a login: it identifies the console rather than a person, so the
    /// change log records which key was used and not who used it. That is enough while this
    /// is operated by a small team, and it is the thing to replace when it is not — the audit
    /// trail is only as good as the identity behind it.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Guards the merchant admin API.
///
/// The endpoints behind this can change where a parcel is collected from, which Engine account
/// a merchant's deliveries are billed to, and the secret their webhooks are signed with. An
/// unauthenticated admin API would be worse than the settings file it replaces: at least a
/// file needs access to the server.
/// </summary>
public sealed class AdminKeyFilter : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Admin-Key";

    private readonly AdminOptions _options;
    private readonly ILogger<AdminKeyFilter> _logger;

    public AdminKeyFilter(AdminOptions options, ILogger<AdminKeyFilter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            // Never open by default. A missing key means the admin API is closed, not public.
            _logger.LogError("Admin API refused: no admin key configured");
            context.Result = new ObjectResult(new { error = "Admin API not configured" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return Task.CompletedTask;
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!Matches(provided, _options.ApiKey))
        {
            _logger.LogWarning("Admin API refused: bad or missing key");
            context.Result = new UnauthorizedObjectResult(new { error = "Unauthorized" });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Constant-time, and over a fixed-length digest of each side.
    ///
    /// Comparing the raw bytes would return early on a length mismatch, which leaks the
    /// length of the key one request at a time. Hashing first makes both sides 32 bytes
    /// whatever was sent.
    /// </summary>
    private static bool Matches(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>Put this on a controller to require the admin key.</summary>
public sealed class RequiresAdminKeyAttribute : TypeFilterAttribute
{
    public RequiresAdminKeyAttribute() : base(typeof(AdminKeyFilter)) { }
}
