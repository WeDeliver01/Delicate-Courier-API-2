using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DelicateCouriers.Integration.Engine;

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>Base address, e.g. https://api2.delicatecourier.co.za</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The service credential issued by the Engine (`dsk_&lt;keyId&gt;_&lt;secret&gt;`).
    ///
    /// A secret, so it belongs in the environment rather than in appsettings committed to a
    /// repository. The Engine stores only a hash of it and can revoke it instantly, which is
    /// why rotating is cheap and leaving it in a config file is not.
    /// </summary>
    public string ServiceCredential { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Raised when the Engine answered, but not with success. Carries the Engine's own stable
/// error code so callers can branch on the fact rather than on a message.
/// </summary>
public sealed class EngineCallException : Exception
{
    public EngineCallException(HttpStatusCode status, string code, string message, string? requestId)
        : base(message)
    {
        Status = status;
        Code = code;
        RequestId = requestId;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
    public string? RequestId { get; }

    /// <summary>
    /// Worth trying again later. A 5xx or a timeout is the Engine having a bad moment; a 4xx
    /// is us having sent something it will refuse just as firmly on the tenth attempt.
    /// </summary>
    public bool IsTransient => (int)Status >= 500 || Status == HttpStatusCode.RequestTimeout;
}

public interface IEngineClient
{
    /// <summary>
    /// Price a job without booking it. <paramref name="accountRef"/> is the merchant's own
    /// identifier for itself, which the Engine resolves to an account.
    /// </summary>
    Task<EngineQuote> CreateQuoteAsync(
        string accountRef,
        EngineQuoteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Price and book in one call. Safe to retry with the same idempotency key: the Engine
    /// returns the booking it already made rather than making a second one.
    /// </summary>
    Task<EngineBookingResponse> CreateBookingAsync(
        string accountRef,
        EngineBookingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// The booking already recorded against this reference, or null.
    ///
    /// This is what closes the crash window between a successful booking and this service
    /// recording it: on the next attempt we ask before we book.
    /// </summary>
    Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
        string accountRef,
        string customerReference,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one place that talks to the Delicate Engine.
///
/// Authentication is a service credential rather than a user's token: this process acts for a
/// merchant and has no human behind it. The account it acts for travels in `X-Account-Ref` as
/// the merchant's own identifier, so this service never has to store Engine uuids.
/// </summary>
public sealed class EngineClient : IEngineClient
{
    public const string AccountRefHeader = "X-Account-Ref";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<EngineClient> _logger;

    public EngineClient(HttpClient http, ILogger<EngineClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<EngineQuote> CreateQuoteAsync(
        string accountRef,
        EngineQuoteRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/service/quotes")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        message.Headers.Add(AccountRefHeader, accountRef);

        using var response = await _http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, accountRef, cancellationToken);
        }

        var quote = await response.Content.ReadFromJsonAsync<EngineQuote>(Json, cancellationToken);
        if (quote is null)
        {
            throw new EngineCallException(
                response.StatusCode, "empty_response", "the Engine returned an empty quote", null);
        }
        return quote;
    }

    public async Task<EngineBookingResponse> CreateBookingAsync(
        string accountRef,
        EngineBookingRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/service/bookings")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        message.Headers.Add(AccountRefHeader, accountRef);

        using var response = await _http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, accountRef, cancellationToken);
        }

        var booked =
            await response.Content.ReadFromJsonAsync<EngineBookingResponse>(Json, cancellationToken);
        if (booked is null)
        {
            throw new EngineCallException(
                response.StatusCode, "empty_response", "the Engine returned an empty booking", null);
        }
        return booked;
    }

    public async Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
        string accountRef,
        string customerReference,
        CancellationToken cancellationToken)
    {
        var url = "v1/service/bookings/by-customer-reference?customerReference="
                  + Uri.EscapeDataString(customerReference);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Add(AccountRefHeader, accountRef);

        using var response = await _http.SendAsync(message, cancellationToken);

        // Nothing booked yet is the ordinary case, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, accountRef, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<EngineBooking>(Json, cancellationToken);
    }

    /// <summary>
    /// Turn a failed response into something with a code on it.
    ///
    /// The Engine's error body is a documented shape, but a proxy or a crash can produce
    /// something else entirely, so a body that will not parse must not itself become the
    /// failure — the status code is still useful on its own.
    /// </summary>
    private async Task<EngineCallException> ToExceptionAsync(
        HttpResponseMessage response,
        string accountRef,
        CancellationToken cancellationToken)
    {
        EngineError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<EngineError>(Json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Engine error body could not be parsed as the standard shape");
        }

        var code = string.IsNullOrWhiteSpace(error?.Code) ? "engine_error" : error!.Code;
        _logger.LogWarning(
            "Engine call failed: {Status} {Code} accountRef={AccountRef} requestId={RequestId}",
            (int)response.StatusCode, code, accountRef, error?.RequestId);

        return new EngineCallException(
            response.StatusCode,
            code,
            error?.Message ?? $"the Engine returned {(int)response.StatusCode}",
            error?.RequestId);
    }

    /// <summary>Applied once at registration; every request carries the credential.</summary>
    public static void Configure(HttpClient http, EngineOptions options)
    {
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ServiceCredential);
    }
}
