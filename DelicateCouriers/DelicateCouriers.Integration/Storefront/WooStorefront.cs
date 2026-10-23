using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DelicateCouriers.Integration.Merchants;

namespace DelicateCouriers.Integration.Storefront;

/// <summary>What the shopper actually chose at checkout, as WooCommerce recorded it.</summary>
public sealed record ChosenShippingMethod(string? MethodId, string? MethodTitle);

/// <summary>The storefront could not be asked, and that is a decision in itself.</summary>
public sealed class StorefrontLookupException : Exception
{
    public StorefrontLookupException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What to tell a merchant's shop about one of its orders.</summary>
/// <param name="Status">The WooCommerce order status to move to, or null to leave it alone.</param>
/// <param name="CustomerNote">A note the customer sees, or null for none.</param>
/// <param name="ShipmentStatus">The courier status written to order meta.</param>
public sealed record StorefrontOrderUpdate(
    string? Status,
    string? CustomerNote,
    string ShipmentStatus,
    string Waybill);

public interface IStorefront
{
    /// <summary>
    /// Push a delivery update onto the merchant's order. Throws
    /// <see cref="StorefrontLookupException"/> when the shop cannot be reached or refuses.
    /// </summary>
    Task UpdateOrderAsync(
        Merchant merchant, long wooOrderId, StorefrontOrderUpdate update,
        CancellationToken cancellationToken);

    /// <summary>
    /// The shipping method on an order, or null when this merchant cannot be asked — no REST
    /// credentials means there is nothing to check, which is different from a check that
    /// failed. Throws <see cref="StorefrontLookupException"/> when the lookup itself fails.
    /// </summary>
    Task<ChosenShippingMethod?> GetShippingMethodAsync(
        Merchant merchant, long wooOrderId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads an order back from the merchant's own WooCommerce over its REST API.
///
/// Needed because the plugin's payload cannot be trusted to say how an order is being
/// fulfilled: its heuristic only looks at the shipping method's id, and a merchant who sells
/// collection as an ordinary flat-rate method defeats that by construction. The method the
/// shopper chose is on the order itself, so that is what gets read.
/// </summary>
public sealed class WooStorefront : IStorefront
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<WooStorefront> _logger;

    public WooStorefront(HttpClient http, ILogger<WooStorefront> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ChosenShippingMethod?> GetShippingMethodAsync(
        Merchant merchant, long wooOrderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(merchant.WooCommerceUrl)
            || string.IsNullOrWhiteSpace(merchant.WooConsumerKey)
            || string.IsNullOrWhiteSpace(merchant.WooConsumerSecret))
        {
            // Not reachable over REST. Nothing to verify, so nothing is withheld.
            return null;
        }

        var url = $"{merchant.WooCommerceUrl.TrimEnd('/')}/wp-json/wc/v3/orders/{wooOrderId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Basic auth over TLS is WooCommerce's documented scheme for server-to-server calls.
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{merchant.WooConsumerKey}:{merchant.WooConsumerSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StorefrontLookupException(
                $"Could not reach {merchant.Name}'s storefront to verify order {wooOrderId}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new StorefrontLookupException(
                    $"{merchant.Name}'s storefront answered {(int)response.StatusCode} "
                    + $"for order {wooOrderId}");
            }

            WooOrder? order;
            try
            {
                order = await response.Content.ReadFromJsonAsync<WooOrder>(Json, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new StorefrontLookupException(
                    $"{merchant.Name}'s storefront returned something unreadable for order {wooOrderId}", ex);
            }

            var line = order?.ShippingLines?.FirstOrDefault();
            _logger.LogInformation(
                "Order {WooOrderId} shipping method: id={MethodId} title={MethodTitle}",
                wooOrderId, line?.MethodId, line?.MethodTitle);

            return new ChosenShippingMethod(line?.MethodId, line?.MethodTitle);
        }
    }

    public async Task UpdateOrderAsync(
        Merchant merchant,
        long wooOrderId,
        StorefrontOrderUpdate update,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(merchant.WooCommerceUrl)
            || string.IsNullOrWhiteSpace(merchant.WooConsumerKey)
            || string.IsNullOrWhiteSpace(merchant.WooConsumerSecret))
        {
            // Nothing to push to. Not a failure: plenty of merchants are read-only to us.
            return;
        }

        // Meta first, status second. If the status write is what fails, the order still
        // carries the courier status the merchant's own templates read, and a retry is safe
        // because both writes are idempotent.
        var body = new Dictionary<string, object?>
        {
            ["meta_data"] = new[]
            {
                new { key = "_dcp_shipment_status", value = update.ShipmentStatus },
                new { key = "_dcp_waybill", value = update.Waybill }
            }
        };
        if (!string.IsNullOrWhiteSpace(update.Status)) body["status"] = update.Status;

        await SendAsync(merchant, $"orders/{wooOrderId}", HttpMethod.Put, body, cancellationToken);

        if (!string.IsNullOrWhiteSpace(update.CustomerNote))
        {
            await SendAsync(
                merchant,
                $"orders/{wooOrderId}/notes",
                HttpMethod.Post,
                new { note = update.CustomerNote, customer_note = true },
                cancellationToken);
        }
    }

    private async Task SendAsync(
        Merchant merchant,
        string path,
        HttpMethod method,
        object body,
        CancellationToken cancellationToken)
    {
        var url = $"{merchant.WooCommerceUrl!.TrimEnd('/')}/wp-json/wc/v3/{path}";
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{merchant.WooConsumerKey}:{merchant.WooConsumerSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StorefrontLookupException(
                $"Could not reach {merchant.Name}'s storefront to update order", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new StorefrontLookupException(
                    $"{merchant.Name}'s storefront answered {(int)response.StatusCode} "
                    + $"when updating {path}");
            }
        }
    }

    private sealed record WooOrder
    {
        [JsonPropertyName("shipping_lines")]
        public List<WooShippingLine>? ShippingLines { get; init; }
    }

    private sealed record WooShippingLine
    {
        [JsonPropertyName("method_id")] public string? MethodId { get; init; }
        [JsonPropertyName("method_title")] public string? MethodTitle { get; init; }
    }
}

/// <summary>
/// Does a chosen shipping method mean the customer is collecting?
///
/// A substring match on the method's id or the label the merchant wrote. Crude on purpose: the
/// labels are free text written by merchants ("Collect from 1 Clifford road, Irene… R95.00"),
/// there is no structured signal to use instead, and the cost of the two mistakes is not
/// symmetric. Refusing to book a delivery that was really a delivery is a phone call;
/// delivering an order the customer is about to collect is a wasted trip and a confused
/// customer.
/// </summary>
public static class Fulfillment
{
    private static readonly string[] CollectionKeywords = { "collect", "pickup", "pick up", "pick-up" };

    public static bool IsCollection(ChosenShippingMethod? method) =>
        method is not null
        && (ContainsKeyword(method.MethodId) || ContainsKeyword(method.MethodTitle));

    private static bool ContainsKeyword(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && CollectionKeywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which of a merchant's service levels the shopper actually paid for.
    ///
    /// The plugin names each rate <c>delicate_courier_platform_&lt;code&gt;</c>, lowercased, and
    /// WooCommerce hands that id back on the order. Matching on it is how a shopper who chose
    /// Express gets an Express booking rather than whatever the merchant happens to list first.
    ///
    /// Returns null when the id matches nothing the merchant offers — a merchant may have
    /// removed a level between the shopper choosing it and the order arriving, and guessing
    /// which of the remaining ones they meant would quietly change what was sold.
    /// </summary>
    public static MerchantServiceLevel? MatchServiceLevel(
        Merchant merchant, ChosenShippingMethod? method)
    {
        var id = method?.MethodId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        return merchant.ServiceLevels.FirstOrDefault(level =>
            !string.IsNullOrWhiteSpace(level.CheckoutCode)
            && id.EndsWith("_" + level.CheckoutCode, StringComparison.OrdinalIgnoreCase));
    }
}
