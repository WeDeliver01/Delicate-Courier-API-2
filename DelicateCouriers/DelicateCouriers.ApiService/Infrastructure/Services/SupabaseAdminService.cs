using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Infrastructure.Services;

/// <summary>
/// Thin wrapper over the Supabase Admin API for provisioning and managing
/// users. Authenticates with the project service-role key (kept server-side
/// only — must never be exposed to the frontend).
///
/// We deliberately store tenant_id and app_role inside the user's
/// <c>app_metadata</c> rather than <c>user_metadata</c>: Supabase only
/// allows the service role to write app_metadata, so a malicious user
/// cannot re-tenant or self-promote by hitting the user-update endpoint
/// from the browser.
/// </summary>
public class SupabaseAdminService
{
    private readonly HttpClient _http;
    private readonly ILogger<SupabaseAdminService> _logger;

    public SupabaseAdminService(HttpClient http, IConfiguration configuration, ILogger<SupabaseAdminService> logger)
    {
        _logger = logger;

        var url = (configuration["Supabase:Url"]
                   ?? Environment.GetEnvironmentVariable("NEXT_PUBLIC_SUPABASE_URL")
                   ?? throw new InvalidOperationException("NEXT_PUBLIC_SUPABASE_URL not configured")).TrimEnd('/');

        var serviceKey = configuration["Supabase:ServiceRoleKey"]
                         ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
                         ?? throw new InvalidOperationException("SUPABASE_SERVICE_ROLE_KEY not configured");

        _http = http;
        _http.BaseAddress = new Uri(url + "/auth/v1/admin/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Supabase Admin API requires both the apikey header and a bearer token.
        _http.DefaultRequestHeaders.Add("apikey", serviceKey);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
    }

    /// <summary>
    /// Create a new Supabase Auth user with email + password and pre-stamped
    /// app_metadata. Email is auto-confirmed because an admin is provisioning
    /// the user — they don't need to click a confirmation link to log in.
    /// </summary>
    public async Task<Guid> CreateUserAsync(string email, string password, int tenantId, string role, CancellationToken ct = default)
    {
        var payload = new
        {
            email,
            password,
            email_confirm = true,
            app_metadata = new Dictionary<string, object>
            {
                ["tenant_id"] = tenantId,
                ["app_role"] = role,
            },
        };

        using var resp = await _http.PostAsJsonAsync("users", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Supabase create user failed: {Status} {Body}", resp.StatusCode, body);
            throw new InvalidOperationException($"Supabase create user failed ({(int)resp.StatusCode}): {body}");
        }

        var result = await resp.Content.ReadFromJsonAsync<SupabaseUserDto>(cancellationToken: ct)
                     ?? throw new InvalidOperationException("Supabase returned empty body for create user");
        return result.Id;
    }

    /// <summary>
    /// Update app_metadata (tenant_id / app_role) for an existing Supabase user.
    /// Used when an admin changes a user's role or moves them between tenants.
    /// </summary>
    public async Task UpdateAppMetadataAsync(Guid supabaseUserId, IDictionary<string, object?> metadata, CancellationToken ct = default)
    {
        var payload = new { app_metadata = metadata };
        using var resp = await _http.PutAsJsonAsync($"users/{supabaseUserId}", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Supabase update user failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>Disable / re-enable a user by banning them indefinitely or clearing the ban.</summary>
    public async Task SetUserBannedAsync(Guid supabaseUserId, bool banned, CancellationToken ct = default)
    {
        var payload = new { ban_duration = banned ? "876000h" : "none" };
        using var resp = await _http.PutAsJsonAsync($"users/{supabaseUserId}", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Supabase ban toggle failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>Permanently delete a user from Supabase Auth.</summary>
    public async Task DeleteUserAsync(Guid supabaseUserId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"users/{supabaseUserId}", ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Supabase delete user failed ({(int)resp.StatusCode}): {body}");
        }
    }

    private sealed class SupabaseUserDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }
    }
}
