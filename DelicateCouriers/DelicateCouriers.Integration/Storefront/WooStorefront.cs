using System.Net.Http.Headers;
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

public interface IStorefront
{
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
}
