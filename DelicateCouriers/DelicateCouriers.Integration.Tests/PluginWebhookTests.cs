using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Jobs;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The order webhook, with the signature check it stands or falls on.
///
/// The endpoint is unauthenticated apart from the HMAC, so anyone who can reach it and guess a
/// store id is one forged signature away from injecting deliveries onto a merchant's account.
/// These tests are about refusing, more than about accepting.
/// </summary>
public class PluginWebhookTests : IClassFixture<PluginWebhookTests.Host>
{
    private const string Secret = "a-merchant-webhook-secret";
    private readonly Host _host;

    public PluginWebhookTests(Host host)
    {
        _host = host;
        _host.Queue.Reset();
    }

    private static string Body(long wooOrderId = 49905, string status = "processing") =>
        JsonSerializer.Serialize(new
        {
            @event = "order.updated",
            woo_order_id = wooOrderId,
            order_number = wooOrderId.ToString(),
            status,
            shipping_total = 172.50m,
            total_weight = 4m,
            store_id = "1",
            customer = new { name = "Jane Smith", email = "jane@example.com", phone = "0821234567" },
            shipping_address = new
            {
                street = "12 Oak Street",
                suburb = "Centurion",
                city = "Pretoria",
                state = "Gauteng",
                postcode = "0157",
                country = "ZA"
            }
        });

    private static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private async Task<HttpResponseMessage> PostAsync(
        string body, string? signature, string storeId = "1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/plugin/order")
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Store-ID", storeId);
        if (signature is not null) request.Headers.Add("X-Plugin-Signature", signature);
        return await _host.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_correctly_signed_order_books()
    {
        var body = Body();
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("queued", json.RootElement.GetProperty("message").GetString());

        // Queued with the exact bytes that were signed, so the job books what the merchant
        // sent rather than something re-serialised on the way through.
        var queued = Assert.Single(_host.Queue.Items);
        Assert.Equal(1, queued.StoreId);
        Assert.Equal(body, queued.RawPayload);
    }

    [Fact]
    public async Task A_forged_signature_is_refused_and_books_nothing()
    {
        var body = Body();
        var response = await PostAsync(body, Sign(body, "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Queue.Items);
    }

    [Fact]
    public async Task A_signature_over_different_bytes_is_refused()
    {
        // Signed correctly, but for a different order. Signing the body is the whole point:
        // a replayed signature must not carry a new payload.
        var response = await PostAsync(Body(wooOrderId: 50000), Sign(Body(wooOrderId: 49905), Secret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Queue.Items);
    }

    [Fact]
    public async Task A_missing_signature_header_is_refused()
    {
        var response = await PostAsync(Body(), signature: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Queue.Items);
    }

    [Fact]
    public async Task An_unknown_store_is_refused_before_any_signature_work()
    {
        var body = Body();
        var response = await PostAsync(body, Sign(body, Secret), storeId: "999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_merchant_with_no_secret_configured_is_a_loud_configuration_error()
    {
        // Never "allow because nothing was configured". Store 2 exists but has no secret.
        var body = Body();
        var response = await PostAsync(body, Sign(body, Secret), storeId: "2");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(_host.Queue.Items);
    }

    [Fact]
    public async Task Queues_a_collect_order_too_and_lets_the_rules_decide_behind_the_webhook()
    {
        // The webhook's job is "signed, valid, ours" — not "should this be delivered". Making
        // that call here would mean two places that decide, which is one too many.
        var body = Body().Replace("\"status\":\"processing\"", "\"status\":\"processing\",\"fulfillment_type\":\"collect\"");
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_host.Queue.Items);
    }

    // ── test host ───────────────────────────────────────────────────────────

    public sealed record QueuedOrder(int StoreId, string RawPayload);

    public sealed class RecordingQueue : IOrderQueue
    {
        public List<QueuedOrder> Items { get; } = new();

        public void Reset() => Items.Clear();

        public void Enqueue(int storeId, string rawPayload) =>
            Items.Add(new QueuedOrder(storeId, rawPayload));
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        public RecordingQueue Queue { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrderQueue>();
                services.AddSingleton<IOrderQueue>(Queue);

                services.RemoveAll<IMerchantDirectory>();
                services.AddSingleton<IMerchantDirectory, ConfiguredMerchantDirectory>();
                services.AddSingleton<IOptions<MerchantDirectoryOptions>>(
                    Options.Create(new MerchantDirectoryOptions
                    {
                        Stores = new List<Merchant>
                        {
                            new()
                            {
                                StoreId = 1,
                                Name = "Honey Bee Baker",
                                EngineAccountRef = "store-1",
                                WebhookSecret = Secret,
                                DefaultPackageTypeId = Guid.Empty.ToString(),
                                Collection = new MerchantAddress
                                {
                                    Formatted = "Honey Bee Baker, Menlyn",
                                    Latitude = -25.7826,
                                    Longitude = 28.2755
                                }
                            },
                            // Deliberately has no WebhookSecret.
                            new()
                            {
                                StoreId = 2,
                                Name = "Misconfigured Store",
                                EngineAccountRef = "store-2"
                            }
                        }
                    }));
            });
        }
    }
}
