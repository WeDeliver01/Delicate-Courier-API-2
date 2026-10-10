using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
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
        _host.Engine.Reset();
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
        Assert.Equal("booked", json.RootElement.GetProperty("message").GetString());

        var sent = Assert.Single(_host.Engine.Booked);
        Assert.Equal("WC-49905", sent.IdempotencyKey);
    }

    [Fact]
    public async Task A_forged_signature_is_refused_and_books_nothing()
    {
        var body = Body();
        var response = await PostAsync(body, Sign(body, "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Engine.Booked);
    }

    [Fact]
    public async Task A_signature_over_different_bytes_is_refused()
    {
        // Signed correctly, but for a different order. Signing the body is the whole point:
        // a replayed signature must not carry a new payload.
        var response = await PostAsync(Body(wooOrderId: 50000), Sign(Body(wooOrderId: 49905), Secret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Engine.Booked);
    }

    [Fact]
    public async Task A_missing_signature_header_is_refused()
    {
        var response = await PostAsync(Body(), signature: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Engine.Booked);
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
        Assert.Empty(_host.Engine.Booked);
    }

    [Fact]
    public async Task A_collect_order_answers_success_without_booking()
    {
        // A skip is a correct outcome, not a failure: telling the plugin otherwise invites a
        // retry of something we have already decided not to do.
        var body = Body().Replace("\"status\":\"processing\"", "\"status\":\"processing\",\"fulfillment_type\":\"collect\"");
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("collect", json.RootElement.GetProperty("message").GetString());
        Assert.Empty(_host.Engine.Booked);
    }

    // ── test host ───────────────────────────────────────────────────────────

    public sealed class FakeEngine : IEngineClient
    {
        public List<EngineBookingRequest> Booked { get; } = new();

        public void Reset() => Booked.Clear();

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
        {
            Booked.Add(request);
            return Task.FromResult(new EngineBookingResponse
            {
                Booking = new EngineBooking
                {
                    Reference = "BK-260101-0001",
                    Shipments = new List<EngineShipment> { new() { Waybill = "DC-260101-00001" } }
                }
            });
        }

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
            => Task.FromResult<EngineBooking?>(null);
    }

    public sealed class AlwaysResolves : IEngineGeocoder
    {
        public Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<ResolvedPlace?>(
                new ResolvedPlace("12 Oak Street, Centurion", -25.8603, 28.1894, "Centurion", "Pretoria", "0157"));
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        public FakeEngine Engine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEngineClient>();
                services.RemoveAll<IEngineGeocoder>();
                services.AddSingleton<IEngineClient>(Engine);
                services.AddSingleton<IEngineGeocoder, AlwaysResolves>();

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
