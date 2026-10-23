using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Delivery;

/// <summary>
/// How this portal gets things delivered, now that it is not a ShipLogic client.
///
/// Deliberately the same shape as <c>IShiplogicService</c> was: book, find an existing
/// booking, fetch a label, read status. Everything that called the old service calls this
/// with the same questions, so replacing the courier was a change of supplier rather than a
/// rewrite of the portal.
///
/// Calls go to the Delicate Integration service, not to the Engine. That service holds the
/// Engine credential — one place to rotate it, one grant list to audit — and owns the mapping
/// into the Engine's own model. This portal never learns how the Engine represents a job,
/// which is what keeps the Engine's contract in one codebase instead of two.
/// </summary>
public interface IDeliveryService
{
    /// <summary>Price it without committing.</summary>
    Task<DeliveryQuote> QuoteAsync(DeliveryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Book it. Safe to retry with the same customer reference: the booking already made comes
    /// back rather than a second one being created.
    /// </summary>
    Task<DeliveryBooking> BookAsync(DeliveryRequest request, CancellationToken cancellationToken = default);

    /// <summary>The booking already made against this reference, or null.</summary>
    Task<DeliveryBooking?> FindByReferenceAsync(
        string accountRef, string customerReference, CancellationToken cancellationToken = default);

    /// <summary>One booking, for a status refresh. Null when it is not there.</summary>
    Task<DeliveryBooking?> GetAsync(
        string accountRef, string bookingId, CancellationToken cancellationToken = default);

    /// <summary>The waybill PDF, or null when there is not one yet.</summary>
    Task<byte[]?> GetLabelAsync(
        string accountRef, string shipmentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The integration service answered, but not with success.
///
/// Carries the stable code rather than only a message, so a caller can branch on the fact.
/// <see cref="IsTransient"/> is the difference between retrying and telling someone: a 5xx is
/// a bad moment, a 4xx will be refused just as firmly on the tenth attempt.
/// </summary>
public sealed class DeliveryException : Exception
{
    public DeliveryException(HttpStatusCode status, string code, string message, bool transient)
        : base(message)
    {
        Status = status;
        Code = code;
        IsTransient = transient;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
    public bool IsTransient { get; }
}

public sealed class DeliveryService : IDeliveryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(HttpClient http, ILogger<DeliveryService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<DeliveryQuote> QuoteAsync(
        DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/portal/quotes", request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<DeliveryQuote>(Json, cancellationToken)
               ?? throw new DeliveryException(
                   response.StatusCode, "empty_response", "The integration service returned no quote", false);
    }

    public async Task<DeliveryBooking> BookAsync(
        DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/portal/bookings", request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);

        var booking = await response.Content.ReadFromJsonAsync<DeliveryBooking>(Json, cancellationToken)
                      ?? throw new DeliveryException(
                          response.StatusCode, "empty_response",
                          "The integration service returned no booking", false);

        _logger.LogInformation(
            "Booked {Reference} as consignment {ConsignmentId} (replayed={Replayed})",
            booking.Reference, booking.ConsignmentId, booking.Replayed);

        return booking;
    }

    public Task<DeliveryBooking?> FindByReferenceAsync(
        string accountRef, string customerReference, CancellationToken cancellationToken = default) =>
        GetOrNullAsync(
            $"api/portal/bookings/by-reference?accountRef={Uri.EscapeDataString(accountRef)}"
            + $"&customerReference={Uri.EscapeDataString(customerReference)}",
            cancellationToken);

    public Task<DeliveryBooking?> GetAsync(
        string accountRef, string bookingId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync(
            $"api/portal/bookings/{Uri.EscapeDataString(bookingId)}"
            + $"?accountRef={Uri.EscapeDataString(accountRef)}",
            cancellationToken);

    public async Task<byte[]?> GetLabelAsync(
        string accountRef, string shipmentId, CancellationToken cancellationToken = default)
    {
        var url = $"api/portal/shipments/{Uri.EscapeDataString(shipmentId)}/waybill"
                  + $"?accountRef={Uri.EscapeDataString(accountRef)}";

        using var response = await _http.GetAsync(url, cancellationToken);

        // No label yet is an ordinary state, not a failure: a booking exists before anything
        // is printed for it.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<DeliveryBooking?> GetOrNullAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);

        // Nothing booked yet is the ordinary case on these paths. It is the answer, not a fault.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<DeliveryBooking>(Json, cancellationToken);
    }

    private async Task<DeliveryException> ToExceptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        IntegrationError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<IntegrationError>(Json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Integration error body was not the standard shape");
        }

        var code = string.IsNullOrWhiteSpace(error?.Code) ? "integration_error" : error!.Code!;
        var transient = error?.Transient ?? (int)response.StatusCode >= 500;

        _logger.LogWarning(
            "Delivery call failed: {Status} {Code} {Message}",
            (int)response.StatusCode, code, error?.Error);

        return new DeliveryException(
            response.StatusCode,
            code,
            error?.Error ?? $"The integration service returned {(int)response.StatusCode}",
            transient);
    }

    private sealed record IntegrationError
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("transient")] public bool? Transient { get; init; }
    }

    /// <summary>
    /// Applied once at registration, so every request carries the key.
    ///
    /// The admin key reaches the integration service over loopback: both run on the same
    /// machine, and nginx answers 404 for /api/admin and /api/portal on the public host, so
    /// this traffic never leaves the box.
    /// </summary>
    public static void Configure(HttpClient http, IConfiguration configuration)
    {
        var baseUrl = configuration["Integration:ApiUrl"]
                      ?? Environment.GetEnvironmentVariable("INTEGRATION_API_URL")
                      ?? "http://127.0.0.1:8096";

        var key = configuration["Integration:AdminKey"]
                  ?? Environment.GetEnvironmentVariable("INTEGRATION_ADMIN_KEY")
                  ?? string.Empty;

        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Add("X-Admin-Key", key);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
