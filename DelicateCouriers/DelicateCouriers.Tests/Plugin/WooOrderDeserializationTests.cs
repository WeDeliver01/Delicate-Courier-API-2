using System.Text.Json;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using Xunit;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Regression tests for the production incident where every shipment booking
/// was withheld: Woo REST order fetches were failing to deserialise because
/// line_items[].meta_data[].value / display_value can be JSON objects or
/// arrays (product add-on / upload plugins), while the DTO typed them as
/// strings. That crash made GetOrderAsync return null, which the
/// WooFulfillmentVerifier treats as LookupFailed — its fail-safe then blocked
/// ALL bookings, including plain delivery orders.
/// </summary>
public class WooOrderDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string OrderJsonWithStructuredMeta = """
    {
      "id": 50046,
      "number": "50046",
      "status": "pending",
      "currency": "ZAR",
      "date_created": "2026-07-12T14:56:40",
      "total": "828.00",
      "billing": { "first_name": "Test", "last_name": "Customer", "email": "t@example.com" },
      "shipping": { "address_1": "1 Test Rd", "city": "Centurion", "postcode": "0157", "country": "ZA" },
      "line_items": [
        {
          "id": 991,
          "name": "Cake",
          "product_id": 123,
          "quantity": 1,
          "total": "700.00",
          "meta_data": [
            { "id": 1, "key": "plain", "value": "just a string", "display_key": "Plain", "display_value": "just a string" },
            { "id": 2, "key": "numeric", "value": 42, "display_key": "Numeric", "display_value": 42 },
            { "id": 3, "key": "pewc_uploads", "value": { "file": "cake.png", "url": "https://x/y.png" }, "display_key": "Uploads", "display_value": { "html": "<a>cake.png</a>" } },
            { "id": 4, "key": "array_meta", "value": ["a", "b"], "display_key": "Array", "display_value": ["a", "b"] }
          ]
        }
      ],
      "meta_data": [
        { "key": "date_picker", "value": "11/07/2026" },
        { "key": "structured", "value": { "nested": true } }
      ],
      "shipping_lines": [
        { "id": 55, "method_id": "flat_rate", "method_title": "Collect from 198 Watermeyer St", "total": "95.00" }
      ]
    }
    """;

    [Fact]
    public void Order_with_object_and_array_line_item_meta_values_deserialises()
    {
        var order = JsonSerializer.Deserialize<WooCommerceOrder>(OrderJsonWithStructuredMeta, Options);

        Assert.NotNull(order);
        Assert.Equal(50046, order!.Id);

        // Shipping lines — the part the fulfillment verifier depends on —
        // must survive structured meta elsewhere in the payload.
        Assert.NotNull(order.ShippingLines);
        var line = Assert.Single(order.ShippingLines!);
        Assert.Equal("flat_rate", line.MethodId);
        Assert.Contains("Collect", line.MethodTitle);

        var meta = order.LineItems[0].MetaData!;
        Assert.Equal(4, meta.Count);
        Assert.Equal("just a string", meta[0].StringValue);
        Assert.Null(meta[1].StringValue);                 // number → not a string
        Assert.Null(meta[2].StringValue);                 // object → not a string
        Assert.Null(meta[3].StringValue);                 // array → not a string
        Assert.Equal("just a string", meta[0].DisplayStringValue);
        Assert.Null(meta[2].DisplayStringValue);
    }

    [Fact]
    public void Line_item_meta_can_be_reserialised_for_variation_details()
    {
        var order = JsonSerializer.Deserialize<WooCommerceOrder>(OrderJsonWithStructuredMeta, Options);

        // WebhookService serialises MetaData into VariationDetails — must not throw.
        var json = JsonSerializer.Serialize(order!.LineItems[0].MetaData);
        Assert.Contains("pewc_uploads", json);
        Assert.Contains("cake.png", json);
    }
}
