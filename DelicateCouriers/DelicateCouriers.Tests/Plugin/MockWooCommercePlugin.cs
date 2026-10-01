using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// In-process simulation of the v2.0.0 Delicate Courier WooCommerce plugin.
///
/// Mirrors the exact wire contract documented in the CONTRACT FREEZE block of
/// <c>Plugins/WooCommerce/Default Plugin/delicate-courier-platform.php</c>:
///
///   * snake_case JSON body matching the C# PluginWebhookPayload DTO
///   * HMAC-SHA256( rawBody, webhook_secret ) base64 in X-Plugin-Signature
///   * Numeric Store ID in X-Store-ID
///   * No Authorization header — signature IS the auth
///   * Content-Type: application/json
///   * User-Agent: DelicateCourierPlatform/&lt;version&gt; WordPress/&lt;wp ver&gt;
///
/// We sign the bytes we are about to send, exactly like the PHP plugin signs
/// whatever <c>wp_json_encode()</c> produced. The bytes never differ between
/// signing and transmission, so any difference in JSON whitespace / slash
/// escaping is irrelevant to the round-trip — what matters is that the
/// receiver re-hashes the EXACT raw body it received, which it does.
/// </summary>
public static class MockWooCommercePlugin
{
    public const string PluginVersion = "2.0.0";
    public const string WordPressVersion = "6.6.2";

    /// <summary>
    /// Match WordPress's <c>wp_json_encode()</c> defaults closely enough that
    /// the produced body looks like what the real plugin would send:
    /// compact (no whitespace) and snake_case fields.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Mirror PHP's default of not encoding forward slashes — we never
        // emit any anyway, but be explicit.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record SignedRequest(
        string RawBody,
        string Signature,
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// Build a fully populated order payload from a fake WooCommerce order,
    /// serialize it, and HMAC-sign it with the store's webhook secret.
    /// </summary>
    public static SignedRequest BuildSignedOrderPush(
        FakeWooOrder order,
        string storeId,
        string webhookSecret)
    {
        var payload = BuildPayload(order, storeId);
        var rawBody = JsonSerializer.Serialize(payload, JsonOptions);
        var signature = ComputeHmacBase64(rawBody, webhookSecret);

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
            ["X-Store-ID"] = storeId,
            ["X-Plugin-Signature"] = signature,
            ["User-Agent"] = $"DelicateCourierPlatform/{PluginVersion} WordPress/{WordPressVersion}",
        };

        return new SignedRequest(rawBody, signature, headers);
    }

    /// <summary>Sign an arbitrary raw body — used for negative tests.</summary>
    public static string ComputeHmacBase64(string rawBody, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
    }

    private static PluginOrderPayload BuildPayload(FakeWooOrder order, string storeId) => new()
    {
        Event = "order.created",
        WooOrderId = order.Id,
        OrderNumber = order.Number,
        Status = order.Status,
        Total = order.Total,
        ShippingTotal = order.ShippingTotal,
        Currency = order.Currency,
        PaymentMethod = order.PaymentMethod,
        DateCreated = order.DateCreated.ToString("yyyy-MM-dd HH:mm:ss"),
        CustomerNote = order.CustomerNote,
        Customer = new PluginCustomer
        {
            Name = order.CustomerName,
            Email = order.CustomerEmail,
            Phone = order.CustomerPhone,
        },
        ShippingAddress = new PluginShippingAddress
        {
            Street = order.ShippingStreet,
            City = order.ShippingCity,
            State = order.ShippingState,
            Postcode = order.ShippingPostcode,
            Country = order.ShippingCountry,
        },
        LineItems = order.LineItems
            .Select(li => new PluginLineItem
            {
                Id = li.Id,
                ProductId = li.ProductId,
                Name = li.Name,
                Quantity = li.Quantity,
                Price = li.LineTotal,
                Sku = li.Sku,
            })
            .ToList(),
        TotalWeight = order.LineItems.Sum(li => Math.Max(li.WeightKg, 1m) * li.Quantity),
        StoreUrl = order.StoreUrl,
        StoreId = storeId,
        DeliveryDate = order.DeliveryDate,
        DeliveryTime = order.DeliveryTime,
        CollectionDate = order.CollectionDate,
        CollectionTime = order.CollectionTime,
        Occasion = order.Occasion,
        FulfillmentType = order.FulfillmentType,
        SpecialTrip = order.SpecialTrip == null ? null : new PluginSpecialTrip
        {
            QuotedAmount = order.SpecialTrip.QuotedAmount,
            DistanceKm = order.SpecialTrip.DistanceKm,
            CustomerLat = order.SpecialTrip.CustomerLat,
            CustomerLng = order.SpecialTrip.CustomerLng,
        },
    };

    // ----- Wire DTO (snake_case via JsonOptions.PropertyNamingPolicy) -----

    private sealed class PluginOrderPayload
    {
        public string Event { get; set; } = "";
        public int WooOrderId { get; set; }
        public string OrderNumber { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Total { get; set; }
        public decimal ShippingTotal { get; set; }
        public string Currency { get; set; } = "";
        public string PaymentMethod { get; set; } = "";
        public string DateCreated { get; set; } = "";
        public string? CustomerNote { get; set; }
        public PluginCustomer Customer { get; set; } = new();
        public PluginShippingAddress ShippingAddress { get; set; } = new();
        public List<PluginLineItem> LineItems { get; set; } = new();
        public decimal TotalWeight { get; set; }
        public string StoreUrl { get; set; } = "";
        public string StoreId { get; set; } = "";
        public string? DeliveryDate { get; set; }
        public string? DeliveryTime { get; set; }
        public string? CollectionDate { get; set; }
        public string? CollectionTime { get; set; }
        public string? Occasion { get; set; }
        public string? FulfillmentType { get; set; }
        public PluginSpecialTrip? SpecialTrip { get; set; }
    }

    private sealed class PluginSpecialTrip
    {
        public decimal QuotedAmount { get; set; }
        public decimal DistanceKm { get; set; }
        public double? CustomerLat { get; set; }
        public double? CustomerLng { get; set; }
    }

    private sealed class PluginCustomer
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
    }

    private sealed class PluginShippingAddress
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string Postcode { get; set; } = "";
        public string Country { get; set; } = "";
    }

    private sealed class PluginLineItem
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Sku { get; set; } = "";
    }
}

/// <summary>
/// Plain-data fixture mimicking the inputs <c>dcp_build_order_payload()</c>
/// reads from a real WooCommerce order. Tests construct one of these and let
/// <see cref="MockWooCommercePlugin"/> turn it into a signed HTTP request.
/// </summary>
public sealed class FakeWooOrder
{
    public int Id { get; set; } = 12345;
    public string Number { get; set; } = "ORD-2025-001";
    public string Status { get; set; } = "processing";
    public decimal Total { get; set; }
    public decimal ShippingTotal { get; set; }
    public string Currency { get; set; } = "ZAR";
    public string PaymentMethod { get; set; } = "card";
    public DateTime DateCreated { get; set; } = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
    public string? CustomerNote { get; set; }

    public string CustomerName { get; set; } = "Jane Customer";
    public string CustomerEmail { get; set; } = "jane@example.com";
    public string CustomerPhone { get; set; } = "+27820000000";

    public string ShippingStreet { get; set; } = "12 Test Avenue";
    public string ShippingCity { get; set; } = "Johannesburg";
    public string ShippingState { get; set; } = "Gauteng";
    public string ShippingPostcode { get; set; } = "2000";
    public string ShippingCountry { get; set; } = "ZA";

    public string StoreUrl { get; set; } = "https://store.example.com";

    public string? DeliveryDate { get; set; }
    public string? DeliveryTime { get; set; }
    public string? CollectionDate { get; set; }
    public string? CollectionTime { get; set; }
    public string? Occasion { get; set; }

    public string? FulfillmentType { get; set; }
    public FakeSpecialTrip? SpecialTrip { get; set; }

    public List<FakeWooLineItem> LineItems { get; set; } = new();
}

/// <summary>
/// Mirrors the plugin v2.7.0 `special_trip` payload block (quote produced by
/// the "Special Trip Request" fallback rate).
/// </summary>
public sealed class FakeSpecialTrip
{
    public decimal QuotedAmount { get; set; }
    public decimal DistanceKm { get; set; }
    public double? CustomerLat { get; set; }
    public double? CustomerLng { get; set; }
}

public sealed class FakeWooLineItem
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal LineTotal { get; set; }
    public string Sku { get; set; } = "";
    public decimal WeightKg { get; set; } = 1m;
}
