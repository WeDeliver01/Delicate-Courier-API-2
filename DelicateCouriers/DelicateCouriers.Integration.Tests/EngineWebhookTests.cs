using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Labels;
using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Status;
using DelicateCouriers.Integration.Storefront;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The Engine telling us a shipment moved, and that reaching the merchant's order.
///
/// This endpoint can move an order to "completed", which fires a merchant's fulfilment emails
/// and touches their accounting. So most of these are about refusing, and about not acting on
/// things we are not sure of.
/// </summary>
public class EngineWebhookTests : IClassFixture<EngineWebhookTests.Host>
{
    private const string Secret = "whsec_subscription_secret";
    private const string AccountId = "11111111-1111-4111-8111-111111111111";

    private readonly Host _host;

    public EngineWebhookTests(Host host)
    {
        _host = host;
        _host.Storefront.Reset();
        _host.Engine.CustomerReference = "WC-49905";
    }

    private static string Body(string to = "delivered", string? accountId = AccountId) =>
        JsonSerializer.Serialize(new
        {
            id = "7f1a2b3c-0000-4000-8000-000000000001",
            type = "shipment.status_changed",
            occurredAt = "2026-01-02T10:00:00Z",
            accountId,
            data = new
            {
                shipmentId = "22222222-2222-4222-8222-222222222222",
                bookingId = "33333333-3333-4333-8333-333333333333",
                waybill = "DC-260101-00001",
                from = "in_transit",
                to,
                note = (string?)null
            }
        });

    private static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private async Task<HttpResponseMessage> PostAsync(string body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/engine/status")
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null) request.Headers.Add("X-Delicate-Signature", signature);
        return await _host.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_delivered_shipment_completes_the_merchants_order()
    {
        var body = Body("delivered");
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var update = Assert.Single(_host.Storefront.Updates);
        Assert.Equal(49905, update.WooOrderId);
        Assert.Equal("completed", update.Update.Status);
        Assert.Equal("delivered", update.Update.ShipmentStatus);
        Assert.Contains("delivered", update.Update.CustomerNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DC-260101-00001", update.Update.CustomerNote!);
    }

    [Fact]
    public async Task Progress_short_of_delivery_never_touches_the_order_status()
    {
        // Moving an order to completed early fires the merchant's fulfilment emails and their
        // accounting. Collection is progress a customer can watch, not a finished order.
        var body = Body("collected");
        await PostAsync(body, Sign(body, Secret));

        var update = Assert.Single(_host.Storefront.Updates);
        Assert.Null(update.Update.Status);
        Assert.Equal("collected", update.Update.ShipmentStatus);
    }

    [Fact]
    public async Task Most_statuses_pass_quietly_without_a_customer_note()
    {
        // One delivery must not become five emails.
        var body = Body("in_transit");
        await PostAsync(body, Sign(body, Secret));

        var update = Assert.Single(_host.Storefront.Updates);
        Assert.Null(update.Update.CustomerNote);
        Assert.Equal("in-transit", update.Update.ShipmentStatus);
    }

    [Fact]
    public async Task A_forged_signature_changes_nothing()
    {
        var body = Body();
        var response = await PostAsync(body, Sign(body, "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Storefront.Updates);
    }

    [Fact]
    public async Task A_missing_signature_changes_nothing()
    {
        var response = await PostAsync(Body(), signature: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Storefront.Updates);
    }

    [Fact]
    public async Task An_unknown_engine_status_is_accepted_and_ignored()
    {
        // Answering anything but 200 makes the Engine retry something we have decided not to
        // act on. Inventing a merchant-visible word would break automations keyed on the old
        // values, which is worse than saying nothing.
        var body = Body("teleported");
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_host.Storefront.Updates);
    }

    [Fact]
    public async Task An_account_with_no_merchant_mapped_is_accepted_and_ignored()
    {
        var body = Body(accountId: "99999999-9999-4999-8999-999999999999");
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_host.Storefront.Updates);
    }

    [Fact]
    public async Task A_booking_that_is_not_a_woocommerce_order_is_left_alone()
    {
        // The same Engine account can be booked through the portal by a person. Those
        // bookings have no Woo order to update, and must not be guessed at.
        _host.Engine.CustomerReference = null;

        var body = Body();
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_host.Storefront.Updates);
    }

    [Fact]
    public async Task A_storefront_that_refuses_does_not_make_the_engine_retry_everyone()
    {
        // The Engine redelivers to every subscriber on a failure, so one broken shop must not
        // cause a storm. The failure is logged and swallowed; the answer stays 200.
        _host.Storefront.Throw = new StorefrontLookupException("shop is down");

        var body = Body();
        var response = await PostAsync(body, Sign(body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("booked", "submitted")]
    [InlineData("collected", "collected")]
    [InlineData("out_for_delivery", "out-for-delivery")]
    [InlineData("delivered", "delivered")]
    [InlineData("failed", "delivery-failed")]
    [InlineData("cancelled", "cancelled")]
    public void The_status_map_is_the_single_translation_point(string engine, string merchantVisible)
    {
        Assert.Equal(merchantVisible, ShipmentStatusMap.ToMerchantVisible(engine));
    }

    [Fact]
    public void Only_delivery_completes_an_order()
    {
        Assert.Equal("completed", ShipmentStatusMap.ToWooOrderStatus("delivered"));
        foreach (var other in new[] { "booked", "collected", "in_transit", "failed", "cancelled" })
        {
            Assert.Null(ShipmentStatusMap.ToWooOrderStatus(other));
        }
    }

    // ── test host ───────────────────────────────────────────────────────────

    public sealed record StorefrontCall(long WooOrderId, StorefrontOrderUpdate Update);

    public sealed class RecordingStorefront : IStorefront
    {
        public List<StorefrontCall> Updates { get; } = new();
        public Exception? Throw { get; set; }

        public void Reset()
        {
            Updates.Clear();
            Throw = null;
        }

        public Task<ChosenShippingMethod?> GetShippingMethodAsync(
            Merchant merchant, long wooOrderId, CancellationToken cancellationToken)
            => Task.FromResult<ChosenShippingMethod?>(null);

        public Task UpdateOrderAsync(
            Merchant merchant, long wooOrderId, StorefrontOrderUpdate update,
            CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            Updates.Add(new StorefrontCall(wooOrderId, update));
            return Task.CompletedTask;
        }
    }

    public sealed class StubEngine : IEngineClient
    {
        public string? CustomerReference { get; set; } = "WC-49905";

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EngineBooking?> GetBookingAsync(
            string accountRef, string bookingId, CancellationToken cancellationToken)
            => Task.FromResult<EngineBooking?>(new EngineBooking
            {
                Id = bookingId,
                Reference = "BK-260101-0001",
                CustomerReference = CustomerReference
            });

        public Task<WaybillDocument> GetWaybillAsync(
            string accountRef, string shipmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        public RecordingStorefront Storefront { get; } = new();
        public StubEngine Engine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorefront>();
                services.RemoveAll<IEngineClient>();
                services.RemoveAll<EngineOptions>();
                services.AddSingleton<IStorefront>(Storefront);
                services.AddSingleton<IEngineClient>(Engine);
                services.AddSingleton(new EngineOptions
                {
                    BaseUrl = "http://engine.test",
                    WebhookSecret = Secret
                });

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
                                EngineAccountId = AccountId,
                                WooCommerceUrl = "https://honeybee.example",
                                WooConsumerKey = "ck",
                                WooConsumerSecret = "cs"
                            }
                        }
                    }));
            });
        }
    }
}
