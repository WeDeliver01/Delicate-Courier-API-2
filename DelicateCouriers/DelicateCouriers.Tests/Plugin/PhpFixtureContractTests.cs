using System.Text;
using System.Text.Json;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Byte-for-byte contract test between the real PHP plugin's
/// <c>wp_json_encode()</c> output and the .NET platform's signature
/// verification + payload deserialisation.
///
/// The companion script <c>Plugins/WooCommerce/tests/build_fixtures.php</c>
/// signs each fixture using the SAME code path as
/// <c>dcp_platform_post_signed()</c>:
///   <code>base64_encode( hash_hmac( 'sha256', wp_json_encode( $payload ), $secret, true ) )</code>
/// The resulting raw body + signature pair is committed under
/// <c>Plugin/Fixtures/</c>. This test feeds those bytes — unmodified —
/// through <see cref="WebhookSignatureValidator"/> and then through the
/// same <see cref="JsonSerializerOptions"/> the live
/// <c>PluginWebhookController</c> uses, asserting:
///
///   1. The signature verifies (i.e. PHP's bytes and the bytes the
///      validator sees agree on every byte, including <c>\/</c> escapes
///      and <c>\u00e9</c>-style unicode escapes).
///   2. The deserialised <see cref="PluginWebhookPayload"/> preserves every
///      field — no silent loss when PHP omits trailing zeros, escapes
///      slashes, or sends optional fields as JSON <c>null</c>.
///
/// What this catches that <see cref="PluginEndToEndTests"/> does not:
/// PluginEndToEndTests round-trips bytes that .NET both produces and
/// verifies, so encoding drift between PHP's <c>wp_json_encode</c> and
/// .NET's <c>System.Text.Json</c> is invisible to it. This test pins the
/// actual PHP-emitted bytes.
/// </summary>
public class PhpFixtureContractTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Plugin", "Fixtures");

    private static readonly JsonSerializerOptions ControllerJsonOptions = new()
    {
        // Matches PluginWebhookController.ReceivePluginOrder().
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Every fixture must round-trip: PHP's exact bytes hash to PHP's exact
    /// signature using .NET's HMAC, with PHP's exact secret.
    /// </summary>
    [Theory]
    [InlineData("ascii_happy_path")]
    [InlineData("unicode_customer_name")]
    [InlineData("decimal_trailing_zero")]
    [InlineData("nullable_optional_fields")]
    public void Php_signed_body_is_accepted_by_dotnet_validator(string fixture)
    {
        var (rawBody, signature, secret) = LoadFixture(fixture);

        var validator = new WebhookSignatureValidator(
            NullLogger<WebhookSignatureValidator>.Instance);

        Assert.True(
            validator.ValidateSignature(rawBody, signature, secret),
            $"Signature on PHP fixture '{fixture}' did not verify. " +
            "This means PHP's wp_json_encode() bytes diverged from what the " +
            ".NET HMAC sees, OR the fixture file was edited after signing.");
    }

    [Fact]
    public void Ascii_happy_path_deserialises_every_field()
    {
        var (rawBody, _, _) = LoadFixture("ascii_happy_path");
        var p = Deserialize(rawBody);

        Assert.Equal("order.created", p.Event);
        Assert.Equal(12345, p.WooOrderId);
        Assert.Equal("ORD-2025-001", p.OrderNumber);
        Assert.Equal("processing", p.Status);
        Assert.Equal(300m, p.Total);
        Assert.Equal(50m, p.ShippingTotal);
        Assert.Equal("ZAR", p.Currency);
        Assert.Equal("Credit Card", p.PaymentMethod);
        Assert.Equal("2026-05-01 10:00:00", p.DateCreated);
        Assert.Equal("", p.CustomerNote);

        Assert.Equal("Jane Customer", p.Customer.Name);
        Assert.Equal("jane@example.com", p.Customer.Email);
        Assert.Equal("+27820000000", p.Customer.Phone);

        Assert.Equal("12 Test Avenue", p.ShippingAddress.Street);
        Assert.Equal("Johannesburg", p.ShippingAddress.City);
        Assert.Equal("Gauteng", p.ShippingAddress.State);
        Assert.Equal("2000", p.ShippingAddress.Postcode);
        Assert.Equal("ZA", p.ShippingAddress.Country);

        Assert.Equal(2, p.LineItems.Count);
        Assert.Equal(1, p.LineItems[0].Id);
        Assert.Equal(101, p.LineItems[0].ProductId);
        Assert.Equal("Widget", p.LineItems[0].Name);
        Assert.Equal(2, p.LineItems[0].Quantity);
        Assert.Equal(200m, p.LineItems[0].Price);
        Assert.Equal("SKU-W", p.LineItems[0].Sku);

        Assert.Equal(2.5m, p.TotalWeight);

        // PHP json_encode escapes forward slashes as `\/`. The .NET
        // deserialiser must collapse them back to `/`.
        Assert.Equal("https://store.example.com", p.StoreUrl);
        Assert.Equal("42", p.StoreId);

        Assert.Equal("2026-05-10", p.DeliveryDate);
        Assert.Equal("14:00", p.DeliveryTime);
        Assert.Equal("2026-05-10", p.CollectionDate);
        Assert.Equal("12:30", p.CollectionTime);
        Assert.Equal("Birthday", p.Occasion);
    }

    [Fact]
    public void Unicode_customer_name_survives_php_unicode_escaping()
    {
        var (rawBody, _, _) = LoadFixture("unicode_customer_name");

        // Sanity-check the wire format: PHP's default flags=0 emits
        // \uXXXX escapes for non-ASCII. If anyone ever enables
        // JSON_UNESCAPED_UNICODE in the plugin, this assertion will trip.
        Assert.Contains("\\u00e9", rawBody);    // é in Renée
        Assert.Contains("\\u00fc", rawBody);    // ü in Müller
        Assert.Contains("\\u4f60", rawBody);    // 你

        var p = Deserialize(rawBody);
        Assert.Equal("Renée Müller 你好", p.Customer.Name);
    }

    [Fact]
    public void Decimal_with_trailing_zero_deserialises_without_loss()
    {
        var (rawBody, _, _) = LoadFixture("decimal_trailing_zero");

        // PHP floatval('3.50') -> 3.5, json_encode emits `3.5` (no
        // trailing zero). The platform's `decimal` deserialiser must
        // still produce 3.5m, not 0 and not throw.
        Assert.Contains("\"total\":3.5", rawBody);
        Assert.Contains("\"shipping_total\":0.1", rawBody);
        Assert.Contains("\"total_weight\":1", rawBody);     // 1.0 -> bare `1`
        Assert.Contains("\"price\":3.4", rawBody);

        var p = Deserialize(rawBody);
        Assert.Equal(3.5m, p.Total);
        Assert.Equal(0.1m, p.ShippingTotal);
        Assert.Equal(1m, p.TotalWeight);
        Assert.Single(p.LineItems);
        Assert.Equal(3.4m, p.LineItems[0].Price);
    }

    /// <summary>
    /// Closes the loop end-to-end: take the PHP-emitted bytes for the
    /// happy-path fixture, deserialise via the controller's options, and
    /// run the resulting <see cref="PluginWebhookPayload"/> through the
    /// real <see cref="PluginWebhookService"/>. If anything between the
    /// PHP JSON shape and the service's reads (field name, type, casing)
    /// were to drift, an Order would either fail to persist or come out
    /// missing fields.
    /// </summary>
    [Fact]
    public async Task Ascii_happy_path_persists_through_real_service()
    {
        var (rawBody, _, _) = LoadFixture("ascii_happy_path");
        var payload = Deserialize(rawBody);

        var ctxOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"phpfixture-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var ctx = new AppDbContext(ctxOptions, httpContextAccessor: null);
        ctx.Tenants.Add(new Tenant
        {
            TenantID = 1, TenantName = "T", TenantAPIKey = "k", CreatedBy = "t",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = 42, TenantID = 1, StoreName = "S",
            WooCommerceURL = "https://store.example.com",
            WebhookSecret = "test-webhook-secret-32-chars-min!!",
            IsActive = true, CreatedBy = "t",
        });
        await ctx.SaveChangesAsync();

        var jobs = new FakeBackgroundJobClient();
        // Seeded store has no Woo REST credentials → verifier is Unverifiable
        // → legacy booking behaviour under test is unchanged.
        var service = new PluginWebhookService(
            ctx, jobs, NullLogger<PluginWebhookService>.Instance,
            new WooFulfillmentVerifier(new FakeWooCommerceService(), NullLogger<WooFulfillmentVerifier>.Instance),
            new FakeSystemEventLogger(),
            new DelicateCouriers.Tests.Shopify.FakeGeocoder(null));

        var resp = await service.ProcessPluginOrderAsync(42, payload);
        Assert.True(resp.Success, resp.Message);

        var saved = await ctx.Orders
            .IgnoreQueryFilters()
            .Include(o => o.LineItems)
            .SingleAsync();

        Assert.Equal("12345", saved.WooOrderID);
        Assert.Equal("ORD-2025-001", saved.WooOrderNumber);
        Assert.Equal("Processing", saved.OrderStatus);
        Assert.Equal(300m, saved.OrderTotal);
        Assert.Equal("Jane Customer", saved.CustomerName);
        Assert.Equal("12 Test Avenue", saved.ShippingAddressLine1);
        Assert.Equal(2, saved.LineItems.Count);
        Assert.Single(jobs.Enqueued);   // status=processing -> shipment queued
    }

    [Fact]
    public void Nullable_optional_fields_deserialise_as_null()
    {
        var (rawBody, _, _) = LoadFixture("nullable_optional_fields");

        // Sanity-check the wire format: nulls really are JSON nulls, not
        // empty strings, so the C# `string?` properties end up null.
        Assert.Contains("\"customer_note\":null", rawBody);
        Assert.Contains("\"delivery_date\":null", rawBody);
        Assert.Contains("\"delivery_time\":null", rawBody);
        Assert.Contains("\"collection_date\":null", rawBody);
        Assert.Contains("\"collection_time\":null", rawBody);
        Assert.Contains("\"occasion\":null", rawBody);

        var p = Deserialize(rawBody);
        Assert.Null(p.CustomerNote);
        Assert.Null(p.DeliveryDate);
        Assert.Null(p.DeliveryTime);
        Assert.Null(p.CollectionDate);
        Assert.Null(p.CollectionTime);
        Assert.Null(p.Occasion);

        // Non-nullable fields are still populated.
        Assert.Equal("Jane Customer", p.Customer.Name);
        Assert.Equal(2, p.LineItems.Count);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static PluginWebhookPayload Deserialize(string rawBody)
    {
        var p = JsonSerializer.Deserialize<PluginWebhookPayload>(
            rawBody, ControllerJsonOptions);
        Assert.NotNull(p);
        return p!;
    }

    /// <summary>
    /// Load the (rawBody, signature, secret) tuple for a fixture, reading
    /// the body as the EXACT UTF-8 bytes PHP wrote — no normalisation, no
    /// BOM-stripping — so the HMAC is computed over the same bytes the PHP
    /// plugin would have transmitted.
    /// </summary>
    private static (string RawBody, string Signature, string Secret) LoadFixture(string name)
    {
        var bodyPath = Path.Combine(FixtureDir, name + ".json");
        var sigPath = Path.Combine(FixtureDir, name + ".sig");
        var secretPath = Path.Combine(FixtureDir, "webhook_secret.txt");

        Assert.True(File.Exists(bodyPath),
            $"Missing fixture body '{bodyPath}'. Regenerate via " +
            "`php Plugins/WooCommerce/tests/build_fixtures.php`.");

        var rawBody = Encoding.UTF8.GetString(File.ReadAllBytes(bodyPath));
        var signature = File.ReadAllText(sigPath).Trim();
        var secret = File.ReadAllText(secretPath).Trim();
        return (rawBody, signature, secret);
    }
}
