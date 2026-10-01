using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Orders;
using DelicateCouriers.ApiService.Features.Packaging;
using DelicateCouriers.ApiService.Features.Shiplogic;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Infrastructure.Auth;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Features.Reports;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Tenants;
using DelicateCouriers.Features.Users;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Polly;
using Polly.Extensions.Http;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ====== DATABASE ======
var rawConnectionString = Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(rawConnectionString))
{
    rawConnectionString = builder.Configuration.GetConnectionString("delicatedb");
}
var connectionString = NormalizePostgresConnectionString(rawConnectionString);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
    });
});

builder.Services.AddHttpContextAccessor();

// ====== HANGFIRE ======
builder.Services.AddHangfire((sp, config) =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings()
          .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString))
          // Records every Failed-state transition to the SystemEvent log so
          // SuperAdmin sees job exhaustion without trawling the dashboard.
          .UseFilter(new DelicateCouriers.ApiService.Infrastructure.Filters.HangfireSystemEventFilter(sp));
});

if (!string.Equals(Environment.GetEnvironmentVariable("ENABLE_BACKGROUND_WORKER"), "false", StringComparison.OrdinalIgnoreCase))
builder.Services.AddHangfireServer(options =>
{
    // Each Hangfire worker holds a long-lived Postgres connection while it
    // polls + processes jobs. The Supabase pooler caps **the entire project**
    // at 15 simultaneous clients in session mode, and we also need headroom
    // for the EF pool used by the API request path. Keep this small.
    options.WorkerCount = 5;
    options.ServerName = "DelicateCouriers-BackgroundWorker";
    options.Queues = new[] { "webhooks", "default", "low" };
});

// ====== CONTROLLERS, SERVICES ======
builder.Services.AddControllers();

// Supabase admin API client (used by AdminUsersController + UserService for
// provisioning users and syncing app_metadata when role/tenant changes).
builder.Services.AddHttpClient<SupabaseAdminService>();

//registering WooCommerceService
builder.Services.AddHttpClient<IWooCommerceService, WooCommerceService>()
                .AddPolicyHandler(GetRetryPolicy())
                .AddPolicyHandler(GetCircuitBreakerPolicy());
builder.Services.AddScoped<IWooCommerceService, WooCommerceService>();
builder.Services.AddScoped<IWooFulfillmentVerifier, WooFulfillmentVerifier>();

// registering ShopifyService (Admin API client + carrier-service registration)
builder.Services.AddHttpClient<DelicateCouriers.ApiService.Features.Shopify.IShopifyService, DelicateCouriers.ApiService.Features.Shopify.ShopifyService>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.Shopify.IShopifyService, DelicateCouriers.ApiService.Features.Shopify.ShopifyService>();

// Shopify order ingestion (webhook → WooCommerce pipeline) + tracking write-back (Hangfire job)
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.Shopify.ShopifyOrderIngestionService>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.Shopify.ShopifyTrackingWriteback>();

builder.Services.AddSingleton<WooCommerceRateLimiter>();

builder.Services.AddScoped<WebhookSignatureValidator>();
builder.Services.AddScoped<PluginWebhookService>();
builder.Services.AddScoped<WooStatusReconciliationService>();
builder.Services.AddScoped<WebhookService>();

builder.Services.Configure<ShiplogicSettings>(builder.Configuration.GetSection(ShiplogicSettings.SectionName));

var shiplogicBaseUrl = builder.Configuration["Shiplogic:ApiBaseUrl"]
    ?? "https://api.shiplogic.com";
if (!shiplogicBaseUrl.EndsWith('/')) shiplogicBaseUrl += "/";
builder.Services.AddHttpClient<IShiplogicService, ShiplogicService>(client =>
{
    client.BaseAddress = new Uri(shiplogicBaseUrl);
    // Shipment creation can be slow on Shiplogic's side (their geocoder is
    // invoked synchronously for addresses without lat/lng), and this timeout
    // spans the whole Polly retry pipeline. 30s produced spurious
    // "Request timed out" booking failures — see order WC-49905.
    client.Timeout = TimeSpan.FromSeconds(100);
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy());

// ====== GEOCODING ======
// Shiplogic /v2/rates returns rates:null when its server-side geocoder can't
// resolve our address. We side-step that by geocoding ourselves (with a DB
// cache) and passing explicit lat/lng on every address. Default to Google
// when GOOGLE_MAPS_API_KEY is configured and working; otherwise fall back to
// the free Nominatim service. Both implementations are wrapped in the same
// read-through cache (GeocodeCache table).
builder.Services.AddHttpClient<NominatimGeocoder>();
var googleMapsApiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
if (!string.IsNullOrWhiteSpace(googleMapsApiKey))
{
    builder.Services.AddHttpClient<GoogleGeocoder>();
    builder.Services.AddScoped<GoogleGeocoder>(sp => new GoogleGeocoder(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GoogleGeocoder)),
        sp.GetRequiredService<ILogger<GoogleGeocoder>>(),
        googleMapsApiKey));
}
// Driving distance (Google Distance Matrix) — powers the Shopify
// "Special Trip" fallback rate. A factory rather than a singleton client:
// each merchant supplies their OWN Google key (Store.GoogleMapsApiKey) so
// their Distance Matrix usage bills to them, not the platform.
builder.Services.AddHttpClient(nameof(DelicateCouriers.ApiService.Infrastructure.Distance.GoogleDistanceMatrixService));
builder.Services.AddSingleton<
    DelicateCouriers.ApiService.Infrastructure.Distance.IDrivingDistanceServiceFactory,
    DelicateCouriers.ApiService.Infrastructure.Distance.GoogleDistanceMatrixServiceFactory>();
builder.Services.AddSingleton<
    DelicateCouriers.Features.Shipping.SpecialTrip.ISpecialTripQuoter,
    DelicateCouriers.Features.Shipping.SpecialTrip.SpecialTripQuoter>();
builder.Services.AddScoped<GeocodeCacheStore>();
builder.Services.AddScoped<IGeocoder>(sp =>
{
    var providers = new List<IGeocoder>();
    if (!string.IsNullOrWhiteSpace(googleMapsApiKey))
    {
        providers.Add(sp.GetRequiredService<GoogleGeocoder>());
    }
    providers.Add(sp.GetRequiredService<NominatimGeocoder>());
    IGeocoder inner = providers.Count == 1
        ? providers[0]
        : new ChainGeocoder(providers, sp.GetRequiredService<ILogger<ChainGeocoder>>());
    return new CachedGeocoder(inner, sp.GetRequiredService<GeocodeCacheStore>(), sp.GetRequiredService<ILogger<CachedGeocoder>>());
});

builder.Services.AddScoped<ShipmentOrchestrationService>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.Shipments.UnbookedOrderSentinel>();
builder.Services.AddScoped<LabelService>();
builder.Services.AddScoped<ShipmentService>();
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<PackageMappingService>();
builder.Services.AddScoped<OrderToShipmentMapper>();

// ====== PLUGIN PLATFORM ======
// Release distribution, licensing, staged rollouts, telemetry and feature
// flags for the WordPress/WooCommerce plugins (Features/PluginPlatform).
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.PluginPlatform.IPluginPackageStorage,
    DelicateCouriers.ApiService.Features.PluginPlatform.DbPluginPackageStorage>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.PluginPlatform.PluginUpdateService>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.PluginPlatform.PluginLicenseService>();
builder.Services.AddScoped<DelicateCouriers.ApiService.Features.PluginPlatform.PluginAuditService>();
builder.Services.AddSingleton<DelicateCouriers.ApiService.Features.PluginPlatform.PluginDownloadTokenService>();
builder.Services.AddSingleton<DelicateCouriers.ApiService.Features.PluginPlatform.PluginReplayGuard>();

// SystemEvent log writer. Singleton because it manages its own DbContext
// scope per call (so it's safe to invoke from background jobs and Hangfire
// state filters that don't have an ambient request scope).
builder.Services.AddSingleton<ISystemEventLogger, SystemEventLogger>();
builder.Services.AddHttpClient(nameof(DelicateCouriers.ApiService.Features.Notifications.AdminEmailSender));
builder.Services.AddSingleton<DelicateCouriers.ApiService.Features.Notifications.IAdminEmailSender, DelicateCouriers.ApiService.Features.Notifications.AdminEmailSender>();

// ====== DATA PROTECTION ======
// Key ring persists in the same Postgres database via
// Microsoft.AspNetCore.DataProtection.EntityFrameworkCore — using the
// filesystem would lose keys on every fresh Replit deploy and brick every
// merchant secret. The application name is pinned so a sibling app on the
// same DB can't accidentally produce the same protector instance.
// Application name defaults to the content-root path (stable on Replit
// deploys); the key ring lives in the Postgres DataProtectionKeys table so
// it survives fresh deploys and is shared across any future replicas.
builder.Services
    .AddDataProtection()
    .PersistKeysToDbContext<DelicateCouriers.ApiService.Data.AppDbContext>();

// Lifts Supabase JWT custom claims (app_metadata, app_role, tenant_id, sub)
// into the standard claim shapes the rest of the app expects.
builder.Services.AddScoped<IClaimsTransformation, SupabaseClaimsTransformer>();

// ====== CACHE ======
builder.Services.AddDistributedMemoryCache();
builder.Services.AddMemoryCache();

// ====== SWAGGER ======
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Delicate Couriers API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT (Supabase access token). Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ====== CORS ======
var allowedOrigins = builder.Configuration["CORS:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(o => o.Trim())
    .Where(o => o.Length > 0)
    .ToArray()
    ?? new[] {
        "http://localhost:3000",
        "http://localhost:5000",
        "https://app2.delicatecourier.co.za",
        "https://app2-dev.delicatecourier.co.za",
        "https://app2-staging.delicatecourier.co.za",
        "https://delicate-couriers-backend-test.replit.app"
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ====== SUPABASE JWT AUTH ======
// Validates HS256 access tokens issued by Supabase. The signing key is the
// project's JWT secret (Settings → API → "JWT Secret" in the Supabase
// dashboard). Issuer is "<project-url>/auth/v1", audience is "authenticated".
//
// All custom claims (tenant_id, app_role) are normalised by
// SupabaseClaimsTransformer (registered above) into the shapes the rest of
// the codebase already expects.
var supabaseUrl = (Environment.GetEnvironmentVariable("NEXT_PUBLIC_SUPABASE_URL")
                   ?? builder.Configuration["Supabase:Url"])?.TrimEnd('/');

if (string.IsNullOrWhiteSpace(supabaseUrl))
    throw new InvalidOperationException("NEXT_PUBLIC_SUPABASE_URL is not configured.");

// Supabase signs access tokens with asymmetric ES256 keys; the public JWKS
// is at <project-url>/auth/v1/.well-known/jwks.json. We deliberately do NOT
// use the JwtBearer `Authority` option here — Supabase's OIDC discovery doc
// (/.well-known/openid-configuration) advertises non-standard endpoint paths
// (oauth/token, oauth/userinfo) which Microsoft.IdentityModel's
// OpenIdConnectConfigurationRetriever silently fails to materialise into a
// usable SigningKeys collection. The symptom is every request returning
// 401 with "The signature key was not found" even though the JWKS endpoint
// itself is fine. Wiring a ConfigurationManager<JsonWebKeySet> straight at
// the JWKS URL bypasses OIDC discovery entirely and is the recommended
// pattern for Supabase + Microsoft.IdentityModel.
var jwksUri = $"{supabaseUrl}/auth/v1/.well-known/jwks.json";
var jwksConfigManager = new Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Tokens.JsonWebKeySet>(
    jwksUri,
    new JwksRetriever(),
    new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever())
{
    // Refresh JWKS daily; allow on-demand refresh every 5 min if a kid lookup fails.
    AutomaticRefreshInterval = TimeSpan.FromHours(24),
    RefreshInterval = TimeSpan.FromMinutes(5),
};

// Share the JWKS manager with anything that validates tokens out-of-band
// (currently the Hangfire dashboard filter).
builder.Services.AddSingleton<Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Tokens.JsonWebKeySet>>(jwksConfigManager);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Audience = "authenticated";
        options.MapInboundClaims = false; // keep "sub" as "sub" (don't auto-rename to NameIdentifier)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // 30s skew to absorb minor clock drift between Supabase and our host.
            ClockSkew = TimeSpan.FromSeconds(30),
            // Use the canonical "role" / nameid claim names so [Authorize(Roles=...)] works.
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier,
            // Pin to ES256 — Supabase's JWKS only serves an EC P-256 key, and
            // pinning prevents algorithm-confusion attacks if a future JWKS
            // ever serves a symmetric (HS256) key alongside it.
            ValidAlgorithms = new[] { Microsoft.IdentityModel.Tokens.SecurityAlgorithms.EcdsaSha256 },
            // Resolve signing keys straight from the JWKS endpoint. If the
            // requested kid is not in the cache, force a refresh (handles
            // Supabase rotating keys at runtime).
            IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
            {
                var jwks = jwksConfigManager.GetConfigurationAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                var match = jwks.GetSigningKeys().Where(k => k.KeyId == kid).ToList();
                if (match.Count == 0)
                {
                    jwksConfigManager.RequestRefresh();
                    jwks = jwksConfigManager.GetConfigurationAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    match = jwks.GetSigningKeys().Where(k => k.KeyId == kid).ToList();
                }
                return match;
            }
        };

        // Surface JWKS / validation failures to the application logger so
        // future regressions show up loudly instead of as silent 401s.
        options.Events = new JwtBearerEvents
        {
            // Third-party webhooks (Shiplogic, WooCommerce plugin) authenticate
            // via their own shared-secret headers — Shiplogic sends its "Auth
            // key" verbatim in the Authorization header, which makes JwtBearer
            // try to parse a non-JWT string and throw IDX14100. Suppress the
            // handler entirely for those paths so the bearer middleware never
            // touches the request and we don't pollute the logs with bogus
            // "Failed to validate the token" warnings. The controllers have
            // their own [AllowAnonymous] + shared-secret enforcement.
            OnMessageReceived = ctx =>
            {
                var path = ctx.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/api/webhooks/shiplogic/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/webhooks/plugin/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/webhooks/woocommerce/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.NoResult();
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                var logger = ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtBearer");
                logger.LogWarning(ctx.Exception,
                    "JWT auth failed for {Method} {Path}: {Message}",
                    ctx.Request.Method, ctx.Request.Path, ctx.Exception.Message);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ====== RATE LIMITING ======
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/alive", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("health");
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("AuthIp", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("WebhookIp", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1000,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// ====== HEALTH CHECKS ======
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: new[] { "ready" });

var app = builder.Build();

// Global exception handler — must be the outermost middleware so it can
// observe failures from anything downstream (auth, rate limiter, controllers).
// Without this, ASP.NET in Production returns an empty 500 with no body and
// the stack trace is silently dropped, which makes prod debugging impossible.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feat = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("UnhandledException");
    logger.LogError(feat?.Error,
        "Unhandled exception on {Method} {Path}{Query}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : string.Empty);

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync("{\"error\":\"internal_server_error\"}");
}));

app.UseCors("AllowFrontend");

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Delicate Couriers API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new JwtDashboardAuthorizationFilter(app.Configuration, jwksConfigManager) },
    DashboardTitle = "Delicate Couriers - Background Jobs",
    StatsPollingInterval = 5000,
    DisplayStorageConnectionString = false
});

app.MapControllers();

// ====== RECURRING JOBS ======
// DAA remote monitoring was removed (2026-07-13). Deregister the recurring
// sweep job so Hangfire stops firing it against the now-deleted job type.
RecurringJob.RemoveIfExists("daa-pull-sweep");

// Platform-side status re-sync for stores whose installed Woo plugin never
// re-sends status changes (older builds sync each order once). Polls the
// store's Woo REST API for unshipped orders on trigger-status stores and
// books once the trigger status is reached. See WooStatusReconciliationService.
RecurringJob.AddOrUpdate<DelicateCouriers.ApiService.Features.WooCommerce.WooStatusReconciliationService>(
    "woo-status-reconciliation",
    service => service.RunAsync(),
    "*/10 * * * *"); // every 10 minutes

// Safety net for "completed but never booked" orders (#50829): every 15
// minutes, flag Completed orders with no shipment (grace period applies)
// via an `order.completed_without_booking` system event + error log.
RecurringJob.AddOrUpdate<DelicateCouriers.ApiService.Features.Shipments.UnbookedOrderSentinel>(
    "unbooked-completed-orders-sentinel",
    sentinel => sentinel.RunAsync(),
    "*/15 * * * *");


static string NormalizePostgresConnectionString(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        throw new InvalidOperationException(
            "Database connection string is not configured. Set ConnectionStrings__delicatedb.");
    }

    var trimmed = raw.Trim();
    if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return trimmed;
    }

    var uri = new Uri(trimmed);
    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var database = uri.AbsolutePath.TrimStart('/');
    var port = uri.Port > 0 ? uri.Port : 5432;

    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    var sslMode = query["sslmode"] ?? query["ssl_mode"] ?? "Prefer";
    sslMode = sslMode.ToLowerInvariant() switch
    {
        "disable" => "Disable",
        "allow" => "Allow",
        "prefer" => "Prefer",
        "require" => "Require",
        "verify-ca" => "VerifyCA",
        "verify-full" => "VerifyFull",
        _ => "Prefer"
    };

    var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = port,
        Database = database,
        Username = username,
        Password = password,
        SslMode = Enum.Parse<Npgsql.SslMode>(sslMode),
        // Supabase's session-mode pooler hard-caps the whole project at 15
        // simultaneous clients (`EMAXCONNSESSION`). We open this connection
        // string in two pools — EF (web request path) and Hangfire (job
        // workers) — and prod + a dev backend may share the same pooler, so
        // each pool needs to stay small. 5 here × 2 pools = 10 max from one
        // process, leaving room for the other process plus Supabase Studio.
        MaxPoolSize = 5,
    };

    return connBuilder.ConnectionString;
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions.HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryCount, _) =>
            {
                Console.WriteLine($"Retry {retryCount} after {timespan.TotalSeconds}s due to: {outcome.Result?.StatusCode}");
            });
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    // Threshold is intentionally generous: this breaker is a singleton on the
    // HttpClient handler, so its failure counter accumulates across every job
    // in the process. A single poison order that 5xx's repeatedly would trip
    // a low threshold and starve every other tenant's bookings for the open
    // window. 10 keeps the safety net but stops one bad order from
    // monopolising the breaker.
    return HttpPolicyExtensions.HandleTransientHttpError()
        .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 10,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) =>
            {
                Console.WriteLine($"Circuit breaker opened for {duration.TotalSeconds}s due to: {outcome.Result?.StatusCode}");
            },
            onReset: () => Console.WriteLine("Circuit breaker reset"));
}

// ====== STARTUP MIGRATIONS + SEED ======
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        dbContext.Database.Migrate();
        Console.WriteLine("Database migrations applied successfully");

        // Ensure the geocoding cache table exists. Lives outside EF migrations
        // so we don't have to coordinate snapshot updates for an auxiliary
        // operational table.
        await DelicateCouriers.ApiService.Infrastructure.Geocoding.GeocodeCacheStore
            .EnsureTableAsync(dbContext, CancellationToken.None);
        Console.WriteLine("GeocodeCache table ensured");

        if (!dbContext.Tenants.IgnoreQueryFilters().Any(t => t.TenantID == 1))
        {
            dbContext.Tenants.Add(new DelicateCouriers.Domain.Entities.Tenant
            {
                TenantName = "Delicate Couriers",
                TenantAPIKey = "DEFAULT-PLATFORM-KEY",
                IsActive = true,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = "system"
            });
            dbContext.SaveChanges();
            Console.WriteLine("Seeded default platform tenant");
        }

        // Seed the bootstrap SuperAdmin into Supabase + local profile if both
        // ADMIN_EMAIL/ADMIN_PASSWORD are set and the user doesn't yet exist.
        // This is idempotent and safe to leave on indefinitely.
        var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL")?.Trim().ToLowerInvariant();
        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            if (!dbContext.User.IgnoreQueryFilters().Any(u => u.Email == adminEmail))
            {
                try
                {
                    var supabaseAdmin = scope.ServiceProvider.GetRequiredService<SupabaseAdminService>();
                    var supabaseId = await supabaseAdmin.CreateUserAsync(adminEmail, adminPassword, tenantId: 1, role: "SuperAdmin");
                    dbContext.User.Add(new DelicateCouriers.Domain.Entities.User
                    {
                        SupabaseUserId = supabaseId,
                        TenantID = 1,
                        Email = adminEmail,
                        FirstName = "Super",
                        LastName = "Admin",
                        Role = "SuperAdmin",
                        IsActive = true,
                        CreatedOn = DateTime.UtcNow,
                        CreatedBy = "system"
                    });
                    dbContext.SaveChanges();
                    Console.WriteLine($"Seeded bootstrap SuperAdmin in Supabase + local DB: {adminEmail}");
                }
                catch (Exception ex)
                {
                    // Most common cause: user already exists in Supabase from a
                    // previous run but the local row was wiped. Log and move on
                    // — re-seeding the local row in that case requires manual
                    // intervention so we don't accidentally clobber a real user.
                    Console.WriteLine($"SuperAdmin seed skipped: {ex.Message}");
                }
            }
        }

        // Back-fill the global default packaging catalogue into every existing
        // store. Idempotent and additive — only inserts templates that aren't
        // already present (matched by Name, case-insensitive).
        try
        {
            var storeIds = dbContext.Stores.IgnoreQueryFilters().Select(s => s.StoreID).ToList();
            var totalSeeded = 0;
            foreach (var sid in storeIds)
            {
                totalSeeded += await DelicateCouriers.ApiService.Features.Packaging.DefaultPackageTypes
                    .EnsureForStoreAsync(dbContext, sid, createdBy: "system");
            }
            if (totalSeeded > 0)
            {
                await dbContext.SaveChangesAsync();
                Console.WriteLine($"Back-filled {totalSeeded} default package types across {storeIds.Count} store(s)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Default package types back-fill skipped: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Migration error: {ex.Message}");
        throw;
    }
}

app.Run();

/// <summary>
/// Minimal IConfigurationRetriever for a JWKS endpoint. Used by the
/// ConfigurationManager wired up in the JwtBearer setup above. Microsoft's
/// own JsonWebKeySetConfigurationRetriever was added in IdentityModel 8.x;
/// we run on 7.1.2 here, so we ship a tiny equivalent.
/// </summary>
public class JwksRetriever : Microsoft.IdentityModel.Protocols.IConfigurationRetriever<Microsoft.IdentityModel.Tokens.JsonWebKeySet>
{
    public async Task<Microsoft.IdentityModel.Tokens.JsonWebKeySet> GetConfigurationAsync(
        string address,
        Microsoft.IdentityModel.Protocols.IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        return new Microsoft.IdentityModel.Tokens.JsonWebKeySet(json);
    }
}
