using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DelicateCouriers.Integration.Labels;

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

    /// <summary>
    /// The secret the Engine signs its outbound deliveries with, from the subscription we
    /// registered there. Separate from <see cref="ServiceCredential"/> on purpose: verifying
    /// what arrives needs no ability to book.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Raised when the Engine answered, but not with success. Carries the Engine's own stable
/// error code so callers can branch on the fact rather than on a message.
/// </summary>
public sealed class EngineCallException : Exception
{
    public EngineCallException(
        HttpStatusCode status, string code, string message, string? requestId, string? detail = null)
        : base(message)
    {
        Status = status;
        Code = code;
        RequestId = requestId;
        Detail = detail;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
    public string? RequestId { get; }

    /// <summary>The Engine's own account of what was wrong, for the log. Never for the caller.</summary>
    public string? Detail { get; }

    /// <summary>
    /// Worth trying again later. A 5xx or a timeout is the Engine having a bad moment; a 4xx
    /// is us having sent something it will refuse just as firmly on the tenth attempt.
    /// </summary>
    public bool IsTransient => (int)Status >= 500 || Status == HttpStatusCode.RequestTimeout;
}

/// <summary>
/// Whether this service can actually act for a merchant's Engine account.
///
/// Two things have to exist in the Engine before a merchant can book, and neither is created
/// by onboarding them here: a mapping from their reference to an Engine account, and a grant
/// of that account to this service's credential. Both are deliberately super_admin-only — a
/// credential cannot grant itself authority — so this service can detect them but not fix
/// them. Detecting them at onboarding is the difference between an operator seeing a problem
/// on the merchant's own page and a shopper's first order failing at checkout.
/// </summary>
public enum EngineReadiness
{
    /// <summary>The Engine accepted a call made for this merchant.</summary>
    Ready,

    /// <summary>No account is mapped to this merchant's reference.</summary>
    NoAccountMapping,

    /// <summary>The account exists but this credential was never granted it.</summary>
    NotGrantedToCredential,

    /// <summary>The Engine knows the account and it is not active.</summary>
    AccountNotActive,

    /// <summary>The credential itself was rejected.</summary>
    CredentialRejected,

    /// <summary>The Engine could not be reached, or answered in a way not understood.</summary>
    Unknown,
}

public sealed record EngineAccountStatus(EngineReadiness Readiness, string Detail)
{
    public bool CanBook => Readiness == EngineReadiness.Ready;
}

public interface IEngineClient
{
    /// <summary>
    /// Ask the Engine whether this service may act for a merchant, without booking anything.
    ///
    /// Uses an ordinary read, because the point is to exercise the same authorisation path a
    /// real order takes. A merchant with nothing booked answers "nothing found", which is the
    /// success case here.
    /// </summary>
    Task<EngineAccountStatus> CheckAccountAsync(
        string accountRef,
        CancellationToken cancellationToken);

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

    /// <summary>One booking by its Engine id.</summary>
    Task<EngineBooking?> GetBookingAsync(
        string accountRef,
        string bookingId,
        CancellationToken cancellationToken);

    /// <summary>The waybill for a shipment, as data to be laid out here.</summary>
    Task<WaybillDocument> GetWaybillAsync(
        string accountRef,
        string shipmentId,
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

    public async Task<EngineBooking?> GetBookingAsync(
        string accountRef,
        string bookingId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"v1/service/bookings/{Uri.EscapeDataString(bookingId)}");
        message.Headers.Add(AccountRefHeader, accountRef);

        using var response = await _http.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, accountRef, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<EngineBooking>(Json, cancellationToken);
    }

    public async Task<WaybillDocument> GetWaybillAsync(
        string accountRef,
        string shipmentId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"v1/service/shipments/{Uri.EscapeDataString(shipmentId)}/waybill");
        message.Headers.Add(AccountRefHeader, accountRef);

        using var response = await _http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, accountRef, cancellationToken);
        }

        var waybill = await response.Content.ReadFromJsonAsync<WaybillDocument>(Json, cancellationToken);
        if (waybill is null)
        {
            throw new EngineCallException(
                response.StatusCode, "empty_response", "the Engine returned an empty waybill", null);
        }
        return waybill;
    }

    /// <summary>
    /// Turn a failed response into something with a code on it.
    ///
    /// The Engine's error body is a documented shape, but a proxy or a crash can produce
    /// something else entirely, so a body that will not parse must not itself become the
    /// failure — the status code is still useful on its own.
    /// </summary>
    public async Task<EngineAccountStatus> CheckAccountAsync(
        string accountRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountRef))
        {
            return new EngineAccountStatus(
                EngineReadiness.NoAccountMapping,
                "This merchant has no Engine account reference set.");
        }

        try
        {
            // A reference no booking will ever use, so a hit is impossible and the answer is
            // always about authorisation rather than data.
            await FindBookingByCustomerReferenceAsync(
                accountRef, "engine-readiness-probe", cancellationToken);

            return new EngineAccountStatus(
                EngineReadiness.Ready,
                "The Engine accepts calls for this merchant.");
        }
        catch (EngineCallException ex)
        {
            return Classify(ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engine readiness check failed for accountRef={AccountRef}", accountRef);
            return new EngineAccountStatus(
                EngineReadiness.Unknown,
                "The Engine could not be reached: " + ex.Message);
        }
    }

    /// <summary>
    /// Works out which of the Engine's refusals this is.
    ///
    /// On the message, not the code, because the Engine answers all of these with "forbidden".
    /// That is matching on prose and will break if the wording changes, so an unrecognised
    /// refusal reports Unknown and carries the Engine's own text rather than guessing. The
    /// durable fix is distinct codes in the Engine; this reads them until there are.
    /// </summary>
    private static EngineAccountStatus Classify(EngineCallException ex)
    {
        if (ex.Status == HttpStatusCode.Unauthorized)
        {
            return new EngineAccountStatus(
                EngineReadiness.CredentialRejected,
                "The Engine rejected this service's credential.");
        }

        var message = ex.Message ?? string.Empty;

        if (message.Contains("no account is mapped", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineAccountStatus(
                EngineReadiness.NoAccountMapping,
                "No Engine account is mapped to this merchant's reference.");
        }

        if (message.Contains("may not act for that account", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineAccountStatus(
                EngineReadiness.NotGrantedToCredential,
                "The Engine account exists, but this service was never granted it.");
        }

        if (message.Contains("account is not active", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineAccountStatus(
                EngineReadiness.AccountNotActive,
                "The Engine account is not active.");
        }

        if (message.Contains("unknown account", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineAccountStatus(
                EngineReadiness.NoAccountMapping,
                "The mapped Engine account no longer exists.");
        }

        return new EngineAccountStatus(
            EngineReadiness.Unknown,
            $"The Engine refused the call ({(int)ex.Status} {ex.Code}): {message}");
    }

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
            error?.RequestId,
            error?.Details?.ToString());
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
