using Hangfire.Dashboard;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

/// <summary>
/// Hangfire dashboard authorisation: requires a valid Supabase-issued JWT
/// (in the Authorization header or the ?access_token=... query string) and
/// the user's role must be Admin or SuperAdmin.
///
/// Tokens are validated against the live Supabase JWKS (asymmetric ES256
/// keys) — same source-of-truth used by the main JwtBearer middleware in
/// Program.cs. The legacy SymmetricSecurityKey(SUPABASE_JWT_SECRET) flow
/// no longer works because Supabase rotated to asymmetric signing keys.
/// </summary>
public class JwtDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly IConfiguration _configuration;
    private readonly IConfigurationManager<JsonWebKeySet> _jwksConfigManager;

    public JwtDashboardAuthorizationFilter(
        IConfiguration configuration,
        IConfigurationManager<JsonWebKeySet> jwksConfigManager)
    {
        _configuration = configuration;
        _jwksConfigManager = jwksConfigManager;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        var token = httpContext.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var authHeader = httpContext.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                token = authHeader["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(token)) return false;

        var supabaseUrl = (Environment.GetEnvironmentVariable("NEXT_PUBLIC_SUPABASE_URL")
                           ?? _configuration["Supabase:Url"])?.TrimEnd('/');
        if (string.IsNullOrEmpty(supabaseUrl)) return false;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = (_, _, kid, _) =>
                {
                    var jwks = _jwksConfigManager.GetConfigurationAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var match = jwks.GetSigningKeys().Where(k => k.KeyId == kid).ToList();
                    if (match.Count == 0)
                    {
                        _jwksConfigManager.RequestRefresh();
                        jwks = _jwksConfigManager.GetConfigurationAsync(CancellationToken.None)
                            .GetAwaiter().GetResult();
                        match = jwks.GetSigningKeys().Where(k => k.KeyId == kid).ToList();
                    }
                    return match;
                },
                ValidateIssuer = true,
                ValidIssuer = $"{supabaseUrl}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 }
            }, out SecurityToken validatedToken);

            var jwt = (JwtSecurityToken)validatedToken;

            // Prefer top-level "app_role" (set by the Supabase Custom Access
            // Token Hook). Fall back to digging into app_metadata for the
            // case where the hook isn't installed in this environment.
            var appRole = jwt.Claims.FirstOrDefault(c => c.Type == "app_role")?.Value
                          ?? jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value
                          ?? ExtractFromAppMetadata(jwt, "app_role");

            return string.Equals(appRole, "Admin", StringComparison.Ordinal)
                   || string.Equals(appRole, "SuperAdmin", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractFromAppMetadata(JwtSecurityToken jwt, string key)
    {
        var raw = jwt.Claims.FirstOrDefault(c => c.Type == "app_metadata")?.Value;
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.ToString() : null;
        }
        catch { return null; }
    }
}
