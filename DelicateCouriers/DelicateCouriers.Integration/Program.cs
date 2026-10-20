using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Features.Admin;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Jobs;
using DelicateCouriers.Integration.Labels;
using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Storefront;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Merchants live in a table rather than a settings file, so onboarding one is somebody doing
// their job rather than a developer and a deploy, and every change leaves a record.
var merchantConnection = builder.Configuration.GetConnectionString("Merchants")
                         ?? Environment.GetEnvironmentVariable("MERCHANTS_CONNECTION")
                         ?? builder.Configuration.GetConnectionString("Hangfire")
                         ?? Environment.GetEnvironmentVariable("HANGFIRE_CONNECTION");

if (!string.IsNullOrWhiteSpace(merchantConnection))
{
    builder.Services.AddDbContext<MerchantDbContext>(o => o.UseNpgsql(merchantConnection));
    builder.Services.AddSingleton<DbMerchantDirectory>();
    builder.Services.AddSingleton<IMerchantDirectory>(sp => sp.GetRequiredService<DbMerchantDirectory>());
}
else if (builder.Environment.IsEnvironment("Testing"))
{
    // Tests supply their own directory.
}
else
{
    throw new InvalidOperationException(
        "No merchant storage configured. Set ConnectionStrings:Merchants (or MERCHANTS_CONNECTION).");
}

builder.Services.AddSingleton(builder.Configuration
    .GetSection(AdminOptions.SectionName)
    .Get<AdminOptions>() is { } admin && !string.IsNullOrWhiteSpace(admin.ApiKey)
    ? admin
    : new AdminOptions { ApiKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY") ?? string.Empty });
builder.Services.AddScoped<OrderBookingService>();
builder.Services.AddScoped<BookOrderJob>();
builder.Services.AddSingleton<ILabelRenderer, LabelRenderer>();

var engineOptions = builder.Configuration
    .GetSection(EngineOptions.SectionName)
    .Get<EngineOptions>() ?? new EngineOptions();

// The credential is a secret and belongs in the environment, not in a committed settings file.
// Configuration still wins if it is set, so a developer can point at a local Engine.
engineOptions.ServiceCredential = string.IsNullOrWhiteSpace(engineOptions.ServiceCredential)
    ? Environment.GetEnvironmentVariable("ENGINE_SERVICE_CREDENTIAL") ?? string.Empty
    : engineOptions.ServiceCredential;

builder.Services.AddSingleton(engineOptions);

builder.Services
    .AddHttpClient<IEngineClient, EngineClient>(http => EngineClient.Configure(http, engineOptions));

// Geocoding is a public Engine endpoint, so it carries no credential: a separate client keeps
// the service credential off a request that does not need it.
builder.Services.AddHttpClient<IEngineGeocoder, EngineGeocoder>(http =>
{
    http.BaseAddress = new Uri(engineOptions.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(engineOptions.TimeoutSeconds);
});

// Each merchant's own storefront, read to verify what the shopper chose. One client for all
// of them: the base address differs per merchant, so it is set per request rather than here.
builder.Services.AddHttpClient<IStorefront, WooStorefront>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
});

// The order queue must survive a restart, so it is Postgres-backed. Refusing to start without
// it is deliberate: the alternative is a service that looks healthy, accepts orders and
// quietly drops them, which is the worst of the available failures.
var hangfireConnection = builder.Configuration.GetConnectionString("Hangfire")
                         ?? Environment.GetEnvironmentVariable("HANGFIRE_CONNECTION");

if (!string.IsNullOrWhiteSpace(hangfireConnection))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(hangfireConnection)));
    builder.Services.AddHangfireServer();
    builder.Services.AddSingleton<IOrderQueue, HangfireOrderQueue>();
}
else if (builder.Environment.IsEnvironment("Testing"))
{
    // Tests supply their own queue; see the webhook tests.
}
else
{
    throw new InvalidOperationException(
        "No queue storage configured. Set ConnectionStrings:Hangfire (or HANGFIRE_CONNECTION) — "
        + "orders accepted without a durable queue are lost on restart.");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();

/// <summary>Named so an integration test host can reference this assembly.</summary>
public partial class Program;
