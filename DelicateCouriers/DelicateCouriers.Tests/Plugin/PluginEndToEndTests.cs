using System.Text;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// End-to-end harness covering the WooCommerce plugin → platform round-trip.
///
/// What this replaces: a live smoke test against a real staging WooCommerce
/// store. That test caught HMAC-encoding / header-casing / JSON-whitespace
/// regressions only after deploying ZIPs to merchants. Here we drive the
/// real <see cref="PluginWebhookController"/> + <see cref="PluginWebhookService"/>
/// in-process with a faithful in-process simulation of the v2.0.0 plugin
/// (see <see cref="MockWooCommercePlugin"/>), so every contract break shows
/// up at <c>dotnet test</c> time.
///
/// Each test:
///   1. Spins up a fresh InMemory <see cref="AppDbContext"/> and seeds a
///      Tenant + Store with a known WebhookSecret.
///   2. Builds a payload the way the PHP plugin would (snake_case JSON).
///   3. HMAC-signs it with the store's secret and POSTs it to the controller
///      method directly, with the same headers the plugin sets.
///   4. Asserts the controller persisted an Order with correct line items,
///      addresses, totals, status mapping, and that the shipment job was
///      enqueued (or not, depending on order status).
/// </summary>
public class PluginEndToEndTests
{
    private const string WebhookSecret = "test-webhook-secret-32-chars-min!!";
    private const int TenantId = 1;
    private const int StoreId = 42;

    [Fact]
    public async Task Happy_path_creates_order_with_line_items_and_queues_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var fakeOrder = NewFakeOrder(status: "processing");
        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            fakeOrder, StoreId.ToString(), WebhookSecret);

        var result = await harness.Post(signed);

        Assert.IsType<OkObjectResult>(result);

        // Order was persisted with the right shape.
        var saved = await ctx.Orders
            .IgnoreQueryFilters()
            .Include(o => o.LineItems)
            .SingleAsync();

        Assert.Equal(fakeOrder.Id.ToString(), saved.WooOrderID);
        Assert.Equal(fakeOrder.Number, saved.WooOrderNumber);
        Assert.Equal(TenantId, saved.TenantID);
        Assert.Equal(StoreId, saved.StoreID);
        Assert.Equal("Processing", saved.OrderStatus);                  // status mapping
        Assert.Equal(fakeOrder.CustomerName, saved.CustomerName);
        Assert.Equal(fakeOrder.CustomerEmail, saved.CustomerEmail);
        Assert.Equal(fakeOrder.ShippingStreet, saved.ShippingAddressLine1);
        Assert.Equal(fakeOrder.ShippingCity, saved.ShippingCity);
        Assert.Equal(fakeOrder.ShippingState, saved.ShippingProvince);
        Assert.Equal(fakeOrder.ShippingPostcode, saved.ShippingPostalCode);
        Assert.Equal(fakeOrder.ShippingCountry, saved.ShippingCountry);
        Assert.Equal(fakeOrder.Total, saved.OrderTotal);

        Assert.Equal(2, saved.LineItems.Count);
        var first = saved.LineItems.Single(li => li.WooLineItemID == 1);
        Assert.Equal("Widget", first.ProductName);
        Assert.Equal("SKU-W", first.ProductSKU);
        Assert.Equal(2, first.Quantity);
        Assert.Equal(200m, first.LineTotal);
        Assert.Equal(100m, first.UnitPrice);                            // 200 / 2

        // Shipment job was enqueued because status was "processing".
        Assert.Single(harness.Jobs.Enqueued);
    }

    [Fact]
    public async Task Terminal_status_order_is_persisted_but_no_shipment_job_is_queued()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        // The pipeline books by default for any non-terminal status
        // (see PluginWebhookService.ShouldAutoCreateShipment); only terminal
        // statuses (cancelled/refunded/failed) are persisted without booking.
        var fakeOrder = NewFakeOrder(status: "cancelled");
        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            fakeOrder, StoreId.ToString(), WebhookSecret);

        var result = await harness.Post(signed);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(ctx.Orders.IgnoreQueryFilters());
        Assert.Empty(harness.Jobs.Enqueued);
    }

    [Fact]
    public async Task Replayed_identical_webhook_is_idempotent()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var fakeOrder = NewFakeOrder(status: "processing");
        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            fakeOrder, StoreId.ToString(), WebhookSecret);

        await harness.Post(signed);
        await harness.Post(signed);

        // Unique index on (StoreID, WooOrderID) — only ONE order, never two.
        var orders = await ctx.Orders.IgnoreQueryFilters().ToListAsync();
        Assert.Single(orders);
    }

    [Fact]
    public async Task Status_transition_from_terminal_to_processing_queues_the_shipment_job()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        // First push: cancelled (terminal) → persisted, no job.
        var cancelledOrder = NewFakeOrder(status: "cancelled");
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            cancelledOrder, StoreId.ToString(), WebhookSecret));
        Assert.Empty(harness.Jobs.Enqueued);

        // Second push: same order, now processing → job enqueued.
        var paidOrder = NewFakeOrder(status: "processing");
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            paidOrder, StoreId.ToString(), WebhookSecret));

        Assert.Single(harness.Jobs.Enqueued);
        var saved = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Processing", saved.OrderStatus);
    }

    [Fact]
    public async Task Wrong_signature_is_rejected_with_401()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), StoreId.ToString(), WebhookSecret);

        // Tamper: sign with the wrong secret.
        var headers = signed.Headers.ToDictionary(k => k.Key, v => v.Value);
        headers["X-Plugin-Signature"] = MockWooCommercePlugin.ComputeHmacBase64(
            signed.RawBody, "wrong-secret");

        var result = await harness.Post(new MockWooCommercePlugin.SignedRequest(
            signed.RawBody, headers["X-Plugin-Signature"], headers));

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(ctx.Orders.IgnoreQueryFilters());
        Assert.Empty(harness.Jobs.Enqueued);
    }

    [Fact]
    public async Task Tampered_body_after_signing_is_rejected_with_401()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), StoreId.ToString(), WebhookSecret);

        // Attacker rewrites the total but cannot re-sign.
        var tamperedBody = signed.RawBody.Replace("\"total\":300", "\"total\":1");
        Assert.NotEqual(signed.RawBody, tamperedBody);

        var result = await harness.Post(new MockWooCommercePlugin.SignedRequest(
            tamperedBody, signed.Signature, signed.Headers));

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(ctx.Orders.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Missing_signature_header_is_rejected_with_400()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), StoreId.ToString(), WebhookSecret);

        var headers = signed.Headers
            .Where(h => h.Key != "X-Plugin-Signature")
            .ToDictionary(k => k.Key, v => v.Value);

        var result = await harness.Post(new MockWooCommercePlugin.SignedRequest(
            signed.RawBody, "", headers));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Unknown_store_id_is_rejected_with_404()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), "999999", WebhookSecret);

        var result = await harness.Post(signed);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Inactive_store_is_rejected_with_404()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, isActive: false);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), StoreId.ToString(), WebhookSecret);

        var result = await harness.Post(signed);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_with_400()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        const string junk = "{ this is not json ";
        var sig = MockWooCommercePlugin.ComputeHmacBase64(junk, WebhookSecret);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["X-Store-ID"] = StoreId.ToString(),
            ["X-Plugin-Signature"] = sig,
        };

        var result = await harness.Post(new MockWooCommercePlugin.SignedRequest(
            junk, sig, headers));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("x-store-id", "x-plugin-signature")]            // lower-case
    [InlineData("X-STORE-ID", "X-PLUGIN-SIGNATURE")]            // upper-case
    [InlineData("X-Store-Id", "X-Plugin-Signature")]            // mixed
    public async Task Header_name_casing_is_accepted(string storeHeader, string sigHeader)
    {
        // Real-world reverse proxies (nginx, Cloudfront, Cloudflare) sometimes
        // normalize header casing. ASP.NET's HeaderDictionary is already
        // case-insensitive, but lock it in with a regression test.
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            NewFakeOrder(), StoreId.ToString(), WebhookSecret);

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            [storeHeader] = StoreId.ToString(),
            [sigHeader] = signed.Signature,
        };

        var result = await harness.Post(new MockWooCommercePlugin.SignedRequest(
            signed.RawBody, signed.Signature, headers));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Php_resync_behaviour_suite_passes()
    {
        // Drives the REAL plugin's dcp_sync_order() under the PHP stub
        // harness (Plugins/WooCommerce/tests/resync_behaviour_test.php) and
        // asserts the $is_resync paths: an already-synced order re-sends on
        // the completed hook, and failed resyncs never add duplicate order
        // notes. This is the plugin-side half of the "#50829 completed but
        // never booked" protection.
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(
            repoRoot, "Plugins", "WooCommerce", "tests", "resync_behaviour_test.php");
        Assert.True(File.Exists(script), $"PHP test script not found at {script}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "php",
            ArgumentList = { script },
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        System.Diagnostics.Process proc;
        try
        {
            proc = System.Diagnostics.Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // PHP not installed in this environment (e.g. a stripped CI
            // container). The suite still runs everywhere PHP exists — the
            // dev workspace and the Plugin Tests workflow both have it.
            return;
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0,
            $"PHP resync behaviour suite failed (exit {proc.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Plugins")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir != null, "Could not locate repo root (no 'Plugins' directory upward of test bin).");
        return dir!.FullName;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"plugin-e2e-{Guid.NewGuid():N}")
            // InMemory raises a noisy warning about transactions; suppress it.
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        // Pass httpContextAccessor=null so BypassTenantFilter returns true,
        // matching how the real webhook pipeline executes (no auth).
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static void SeedStore(AppDbContext ctx, bool isActive = true)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Test Tenant",
            TenantAPIKey = "test-api-key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Test Store",
            WooCommerceURL = "https://store.example.com",
            WebhookSecret = WebhookSecret,
            IsActive = isActive,
            CreatedBy = "tests",
        });
        ctx.SaveChanges();
    }

    private static TestHarness NewHarness(AppDbContext ctx)
    {
        var signatureValidator = new WebhookSignatureValidator(
            NullLogger<WebhookSignatureValidator>.Instance);
        var jobClient = new FakeBackgroundJobClient();
        // Store seeded by these tests has no Woo REST consumer key/secret, so
        // the fulfillment verifier returns Unverifiable and the legacy
        // booking behaviour under test here is unchanged.
        var verifier = new WooFulfillmentVerifier(
            new FakeWooCommerceService(), NullLogger<WooFulfillmentVerifier>.Instance);
        var service = new PluginWebhookService(
            ctx, jobClient, NullLogger<PluginWebhookService>.Instance,
            verifier, new FakeSystemEventLogger(),
            new DelicateCouriers.Tests.Shopify.FakeGeocoder(null));
        var controller = new PluginWebhookController(
            service, signatureValidator, ctx,
            NullLogger<PluginWebhookController>.Instance,
            new FakeSystemEventLogger());
        return new TestHarness(controller, jobClient);
    }

    [Fact]
    public async Task Special_trip_order_persists_quote_fields_and_queues_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var fakeOrder = NewFakeOrder(status: "processing");
        fakeOrder.FulfillmentType = "special_trip";
        fakeOrder.SpecialTrip = new FakeSpecialTrip
        {
            QuotedAmount = 486.20m,
            DistanceKm = 44.2m,
            CustomerLat = -26.1076,
            CustomerLng = 28.0567,
        };

        var signed = MockWooCommercePlugin.BuildSignedOrderPush(
            fakeOrder, StoreId.ToString(), WebhookSecret);

        var result = await harness.Post(signed);
        Assert.IsType<OkObjectResult>(result);

        var saved = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("special_trip", saved.FulfillmentType);
        Assert.Equal(486.20m, saved.SpecialTripQuotedAmount);
        Assert.Equal(44.2m, saved.SpecialTripDistanceKm);
        Assert.Equal(-26.1076, saved.SpecialTripCustomerLat);
        Assert.Equal(28.0567, saved.SpecialTripCustomerLng);

        // special_trip is NOT collect — the shipment job must still be queued
        // (booked as a Shiplogic SPX ad-hoc trip downstream).
        Assert.Single(harness.Jobs.Enqueued);
    }

    [Fact]
    public async Task Special_trip_block_alone_triggers_update_when_status_and_fulfillment_unchanged()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        // First push: special_trip order without quote data yet.
        var first = NewFakeOrder(status: "processing");
        first.FulfillmentType = "special_trip";
        first.SpecialTrip = null;
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            first, StoreId.ToString(), WebhookSecret));

        // Second push: same status + fulfillment, but the quote block arrives.
        var second = NewFakeOrder(status: "processing");
        second.FulfillmentType = "special_trip";
        second.SpecialTrip = new FakeSpecialTrip
        {
            QuotedAmount = 250m,
            DistanceKm = 20.5m,
            CustomerLat = -26.1,
            CustomerLng = 28.05,
        };
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            second, StoreId.ToString(), WebhookSecret));

        var saved = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(250m, saved.SpecialTripQuotedAmount);
        Assert.Equal(20.5m, saved.SpecialTripDistanceKm);
        Assert.Equal(-26.1, saved.SpecialTripCustomerLat);
        Assert.Equal(28.05, saved.SpecialTripCustomerLng);
    }

    [Fact]
    public async Task Update_webhook_without_special_trip_block_keeps_existing_quote()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var harness = NewHarness(ctx);

        var fakeOrder = NewFakeOrder(status: "processing");
        fakeOrder.FulfillmentType = "special_trip";
        fakeOrder.SpecialTrip = new FakeSpecialTrip { QuotedAmount = 100m, DistanceKm = 10m };
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            fakeOrder, StoreId.ToString(), WebhookSecret));

        // Second push (e.g. a status transition) WITHOUT the block — the
        // stored quote must survive.
        var followUp = NewFakeOrder(status: "completed");
        followUp.FulfillmentType = "special_trip";
        followUp.SpecialTrip = null;
        await harness.Post(MockWooCommercePlugin.BuildSignedOrderPush(
            followUp, StoreId.ToString(), WebhookSecret));

        var saved = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(100m, saved.SpecialTripQuotedAmount);
        Assert.Equal(10m, saved.SpecialTripDistanceKm);
    }

    private static FakeWooOrder NewFakeOrder(string status = "processing") => new()
    {
        Id = 12345,
        Number = "ORD-2025-001",
        Status = status,
        Total = 300m,
        ShippingTotal = 50m,
        LineItems =
        {
            new FakeWooLineItem
            {
                Id = 1, ProductId = 101, Name = "Widget",
                Quantity = 2, LineTotal = 200m, Sku = "SKU-W", WeightKg = 1m,
            },
            new FakeWooLineItem
            {
                Id = 2, ProductId = 102, Name = "Gadget",
                Quantity = 1, LineTotal = 100m, Sku = "SKU-G", WeightKg = 0.5m,
            },
        },
    };

    private sealed class TestHarness
    {
        private readonly PluginWebhookController _controller;
        public FakeBackgroundJobClient Jobs { get; }

        public TestHarness(PluginWebhookController controller, FakeBackgroundJobClient jobs)
        {
            _controller = controller;
            Jobs = jobs;
        }

        /// <summary>
        /// Invoke the controller's POST action with a real HttpContext built
        /// from the signed request — same code path the real plugin hits.
        /// </summary>
        public async Task<IActionResult> Post(MockWooCommercePlugin.SignedRequest req)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = "POST";
            http.Request.Path = "/api/webhooks/plugin/order";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(req.RawBody));
            http.Request.ContentLength = http.Request.Body.Length;
            foreach (var (k, v) in req.Headers)
            {
                http.Request.Headers[k] = v;
            }
            _controller.ControllerContext = new ControllerContext { HttpContext = http };
            return await _controller.ReceivePluginOrder();
        }
    }
}
