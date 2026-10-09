using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<MerchantDirectoryOptions>(
    builder.Configuration.GetSection(MerchantDirectoryOptions.SectionName));
builder.Services.AddSingleton<IMerchantDirectory, ConfiguredMerchantDirectory>();

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
