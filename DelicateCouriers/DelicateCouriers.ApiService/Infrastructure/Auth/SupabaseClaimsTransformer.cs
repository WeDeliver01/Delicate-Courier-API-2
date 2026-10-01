using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Infrastructure.Auth;

/// <summary>
/// Lifts Supabase-specific JWT claims into the shape the rest of the codebase
/// already expects:
///
///   - <c>app_metadata.app_role</c>   -> ClaimTypes.Role + "role" (used by [Authorize(Roles=...)] and IsInRole)
///   - <c>app_metadata.tenant_id</c>  -> "tenant_id" + "TenantId" (used by AppDbContext.CurrentTenantId)
///   - <c>email</c>                   -> ClaimTypes.Email (used by audit logging)
///   - <c>sub</c>                     -> ClaimTypes.NameIdentifier (used by /api/auth/me lookup)
///
/// The Custom Access Token Hook (configured in Supabase) is supposed to write
/// <c>app_role</c> and <c>tenant_id</c> as top-level claims directly. We still
/// fall back to scraping <c>app_metadata</c> here so the system keeps working
/// if the hook is ever disabled or temporarily misconfigured.
/// </summary>
public class SupabaseClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        // Avoid double-transforming the same principal (TransformAsync can be
        // called more than once per request in some pipelines).
        if (identity.HasClaim(c => c.Type == "__supabase_transformed"))
            return Task.FromResult(principal);

        // 1) Resolve role
        var role = identity.FindFirst("app_role")?.Value
                   ?? ExtractFromAppMetadata(identity, "app_role");
        if (!string.IsNullOrEmpty(role))
        {
            // Add as both Role and "role" so IsInRole and string-based lookups both work.
            if (!identity.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            if (!identity.HasClaim(c => c.Type == "role" && c.Value == role))
                identity.AddClaim(new Claim("role", role));
        }

        // 2) Resolve tenant_id
        var tenantId = identity.FindFirst("tenant_id")?.Value
                       ?? ExtractFromAppMetadata(identity, "tenant_id");
        if (!string.IsNullOrEmpty(tenantId))
        {
            // AppDbContext checks both forms; emit both for safety.
            if (!identity.HasClaim(c => c.Type == "tenant_id"))
                identity.AddClaim(new Claim("tenant_id", tenantId));
            if (!identity.HasClaim(c => c.Type == "TenantId"))
                identity.AddClaim(new Claim("TenantId", tenantId));
        }

        // 3) NameIdentifier from sub (Supabase user id)
        var sub = identity.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub) && !identity.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        }

        // 4) Email
        var email = identity.FindFirst("email")?.Value;
        if (!string.IsNullOrEmpty(email) && !identity.HasClaim(c => c.Type == ClaimTypes.Email))
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
        }

        identity.AddClaim(new Claim("__supabase_transformed", "1"));
        return Task.FromResult(principal);
    }

    private static string? ExtractFromAppMetadata(ClaimsIdentity identity, string key)
    {
        var raw = identity.FindFirst("app_metadata")?.Value;
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty(key, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => v.ToString()
            };
        }
        catch { return null; }
    }
}
