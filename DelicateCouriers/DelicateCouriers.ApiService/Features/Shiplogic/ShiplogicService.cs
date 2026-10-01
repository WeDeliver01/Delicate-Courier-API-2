using System.Net.Http.Headers;
using System.Net.Http.Json;
using DelicateCouriers.Features.Shiplogic.DTOs;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace DelicateCouriers.Features.Shiplogic;

public class ShiplogicService : IShiplogicService
{
    private readonly HttpClient _httpClient;

    // Shared client for downloading presigned label URLs (no auth header;
    // S3 presigned URLs reject requests carrying Authorization).
    private static readonly HttpClient _labelDownloadClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ShiplogicSettings _settings;
    private readonly ILogger<ShiplogicService> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    // NOTE: The HttpClient-level circuit breaker is configured in Program.cs and
    // attached to the IShiplogicService typed-client pipeline as a singleton, so
    // its failure counter accumulates across calls and actually trips. A per-
    // instance breaker here would never trip (ShiplogicService is transient via
    // AddHttpClient + SetBearerToken mutates a shared header, so it can't be
    // promoted to singleton), so we rely on the outer one only.

    public ShiplogicService(HttpClient httpClient, IOptions<ShiplogicSettings> settings, ILogger<ShiplogicService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_settings.ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        //_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken); /Removed to per-tenant basis/
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Retry policy: exponential backoff
        _retryPolicy =  Policy<HttpResponseMessage>.Handle<HttpRequestException>()
                       .OrResult(r => !r.IsSuccessStatusCode && (int)r.StatusCode >= 500)
                       .WaitAndRetryAsync(_settings.MaxRetryAttempts, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                                         onRetry: (outcome, timespan, retryCount, context) =>
                                         {
                                             _logger.LogWarning("Shiplogic API request failed. Retry {RetryCount} after {Delay}s. Status: {StatusCode}", retryCount, timespan.TotalSeconds, outcome.Result?.StatusCode);
                                         }
                       );

        // (Inner per-instance circuit breaker removed — see field comment above.)
    }

    /// <summary>
    /// Sets the bearer token for the current request
    /// Must be called before making any API calls
    /// </summary>
    private void SetBearerToken(string bearerToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    //public async Task<CreateShipmentResponse> CreateShipmentAsync(string bearerToken, CreateShipmentRequest request, CancellationToken cancellationToken = default)
    //{
    //    _logger.LogInformation("Creating shipment in Shiplogic. Reference: {CustomerReference}", request.CustomerReference);

    //    // Set the bearer token (authorization) for this request
    //    SetBearerToken(bearerToken);

    //    try
    //    {
    //        var response = await _circuitBreakerPolicy.ExecuteAsync(() =>
    //            _retryPolicy.ExecuteAsync(async () =>
    //            {
    //                var httpResponse = await _httpClient.PostAsJsonAsync("/shipments", request, cancellationToken);
    //                httpResponse.EnsureSuccessStatusCode();
    //                return httpResponse;
    //            })
    //        );

    //        var shipment = await response.Content.ReadFromJsonAsync<CreateShipmentResponse>(cancellationToken);

    //        if (shipment == null)
    //        {
    //            throw new InvalidOperationException("Failed to deserialize shipment response");
    //        }

    //        _logger.LogInformation("Shipment created successfully. ConsignmentId: {ConsignmentId}, TrackingNumber: {TrackingNumber}", shipment.ConsignmentId, shipment.TrackingNumber);

    //        return shipment;
    //    }
    //    catch (HttpRequestException ex)
    //    {
    //        _logger.LogError(ex, "HTTP error creating shipment in Shiplogic");

    //        throw new InvalidOperationException("Failed to create shipment in Shiplogic", ex);
    //    }
    //    catch (BrokenCircuitException<HttpResponseMessage> ex)
    //    {
    //        _logger.LogError(ex, "Circuit breaker open - Shiplogic service unavailable");

    //        throw new InvalidOperationException("Shiplogic service is temporarily unavailable", ex);
    //    }
    //}

    public async Task<CreateShipmentResponse> CreateShipmentAsync(string bearerToken, CreateShipmentRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("=== SHIPLOGIC CREATE SHIPMENT START ===");
        _logger.LogInformation("========================================");

        // Log authentication
        _logger.LogInformation("Bearer Token (first 20 chars): {Token}...", bearerToken.Substring(0, Math.Min(20, bearerToken.Length)));
        _logger.LogInformation("Base URL: {BaseUrl}", _httpClient.BaseAddress);

        // Log the FULL request payload as JSON
        var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });
        _logger.LogInformation("=== FULL REQUEST PAYLOAD ===");
        _logger.LogInformation("{RequestJson}", requestJson);

        // Log individual fields for easier debugging
        _logger.LogInformation("=== REQUEST DETAILS ===");
        _logger.LogInformation("Customer Reference: {CustomerReference}", request.CustomerReference);
        _logger.LogInformation("Service Level Code: {ServiceLevelCode}", request.ServiceLevelCode);
        _logger.LogInformation("Mute Notifications: {MuteNotifications}", request.MuteNotifications);

        // Collection Address
        _logger.LogInformation("=== COLLECTION ADDRESS ===");
        _logger.LogInformation("Type: {Type}", request.CollectionAddress?.Type);
        _logger.LogInformation("Company: {Company}", request.CollectionAddress?.Company);
        _logger.LogInformation("Street: {Street}", request.CollectionAddress?.Street);
        _logger.LogInformation("Suburb: {Suburb}", request.CollectionAddress?.Suburb);
        _logger.LogInformation("City: {City}", request.CollectionAddress?.City);
        _logger.LogInformation("Province/Zone: {Zone}", request.CollectionAddress?.Province);
        _logger.LogInformation("PostalCode: {PostalCode}", request.CollectionAddress?.PostalCode);
        _logger.LogInformation("Country: {Country}", request.CollectionAddress?.Country);
        _logger.LogInformation("=== COLLECTION CONTACT ===");
        _logger.LogInformation("Contact Name: {Name}", request.CollectionContact?.Name);
        _logger.LogInformation("Contact Mobile: {Mobile}", request.CollectionContact?.Mobile);
        _logger.LogInformation("Contact Email: {Email}", request.CollectionContact?.Email);

        // Delivery Address
        _logger.LogInformation("=== DELIVERY ADDRESS ===");
        _logger.LogInformation("Type: {Type}", request.DeliveryAddress?.Type);
        _logger.LogInformation("Company: {Company}", request.DeliveryAddress?.Company);
        _logger.LogInformation("Street: {Street}", request.DeliveryAddress?.Street);
        _logger.LogInformation("Suburb: {Suburb}", request.DeliveryAddress?.Suburb);
        _logger.LogInformation("City: {City}", request.DeliveryAddress?.City);
        _logger.LogInformation("Province/Zone: {Zone}", request.DeliveryAddress?.Province);
        _logger.LogInformation("PostalCode: {PostalCode}", request.DeliveryAddress?.PostalCode);
        _logger.LogInformation("Country: {Country}", request.DeliveryAddress?.Country);
        _logger.LogInformation("=== DELIVERY CONTACT ===");
        _logger.LogInformation("Contact Name: {Name}", request.DeliveryContact?.Name);
        _logger.LogInformation("Contact Mobile: {Mobile}", request.DeliveryContact?.Mobile);
        _logger.LogInformation("Contact Email: {Email}", request.DeliveryContact?.Email);

        // Parcels
        _logger.LogInformation("=== PARCELS ({Count} total) ===", request.Parcels?.Count ?? 0);
        if (request.Parcels != null)
        {
            for (int i = 0; i < request.Parcels.Count; i++)
            {
                var parcel = request.Parcels[i];

                _logger.LogInformation("Parcel {Index}: Length={Length}cm, Width={Width}cm, Height={Height}cm, Weight={Weight}kg, Description={Desc}", i + 1, parcel.Length, parcel.Width, parcel.Height, parcel.Weight, parcel.Description);
            }
        }

        // Set the bearer token
        SetBearerToken(bearerToken);

        // Make the API call using the DI-managed HttpClient so we get connection
        // pooling + the configured Polly retry/circuit-breaker policies and don't
        // leak sockets by newing up an HttpClient per request.
        try
        {
            _logger.LogInformation("=== SENDING HTTP REQUEST ===");
            _logger.LogInformation("POST {Url}/shipments", _httpClient.BaseAddress);

            var httpResponse = await _httpClient.PostAsJsonAsync("/shipments", request, cancellationToken);

            _logger.LogInformation("=== HTTP RESPONSE RECEIVED ===");
            _logger.LogInformation("Status Code: {StatusCode} ({StatusCodeInt})",
                httpResponse.StatusCode, (int)httpResponse.StatusCode);
            _logger.LogInformation("Reason Phrase: {ReasonPhrase}", httpResponse.ReasonPhrase);

            // ALWAYS read the response body
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation("=== RESPONSE BODY ===");
            _logger.LogInformation("{ResponseBody}", responseBody);

            // Log response headers
            _logger.LogInformation("=== RESPONSE HEADERS ===");
            foreach (var header in httpResponse.Headers)
            {
                _logger.LogInformation("{Header}: {Value}", header.Key, string.Join(", ", header.Value));
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("========================================");
                _logger.LogError("=== SHIPLOGIC API ERROR ===");
                _logger.LogError("Status: {StatusCode}", httpResponse.StatusCode);
                _logger.LogError("Body: {Body}", responseBody);
                _logger.LogError("========================================");

                throw new HttpRequestException($"Shiplogic API returned {httpResponse.StatusCode}: {responseBody}");
            }

            // Deserialize successful response
            var shipment = System.Text.Json.JsonSerializer.Deserialize<CreateShipmentResponse>(responseBody,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (shipment == null)
            {
                _logger.LogError("Failed to deserialize response: {Body}", responseBody);
                throw new InvalidOperationException("Failed to deserialize shipment response");
            }

            _logger.LogInformation("========================================");
            _logger.LogInformation("=== SHIPLOGIC API SUCCESS ===");
            _logger.LogInformation("Shipment ID: {ShipmentId}", shipment.ShipmentId);
            _logger.LogInformation("Consignment ID: {ConsignmentId}", shipment.ConsignmentId);
            _logger.LogInformation("Tracking Number: {TrackingNumber}", shipment.TrackingNumber);
            _logger.LogInformation("Rate: {Rate}", shipment.Rate);
            _logger.LogInformation("========================================");

            return shipment;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "=== HTTP REQUEST EXCEPTION ===");
            _logger.LogError("Message: {Message}", ex.Message);
            _logger.LogError("Status Code: {StatusCode}", ex.StatusCode);
            throw new InvalidOperationException($"Failed to create shipment: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "=== REQUEST TIMEOUT/CANCELLED ===");
            throw new InvalidOperationException("Request timed out or was cancelled", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "=== UNEXPECTED EXCEPTION ===");
            _logger.LogError("Type: {Type}", ex.GetType().Name);
            _logger.LogError("Message: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<byte[]> GetLabelAsync(string bearerToken, string consignmentId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching label for consignment {ConsignmentId}", consignmentId);

        // Set the bearer token (authorization) for this request
        SetBearerToken(bearerToken);

        try
        {
            // Shiplogic v2: GET /v2/shipments/label?id={shipment_id} returns JSON
            // with a short-lived signed URL to the label PDF.
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync($"/v2/shipments/label?id={Uri.EscapeDataString(consignmentId)}", cancellationToken);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            });

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            string? labelUrl = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                    labelUrl = urlProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(labelUrl))
                throw new InvalidOperationException($"Shiplogic label response for consignment {consignmentId} contained no URL");

            // The signed URL must be fetched WITHOUT the Authorization header
            // (S3 presigned URLs reject extra auth), so use a bare request.
            var labelBytes = await _labelDownloadClient.GetByteArrayAsync(labelUrl, cancellationToken);

            _logger.LogInformation("Label retrieved successfully. Size: {Size} bytes", labelBytes.Length);

            return labelBytes;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching label for consignment {ConsignmentId}", consignmentId);

            throw new InvalidOperationException($"Failed to fetch label for consignment {consignmentId}", ex);
        }
        catch (BrokenCircuitException<HttpResponseMessage> ex)
        {
            _logger.LogError(ex, "Circuit breaker open - Shiplogic service unavailable");

            throw new InvalidOperationException("Shiplogic service is temporarily unavailable", ex);
        }
    }

    public async Task<TrackingResponse> GetTrackingUpdatesAsync(string bearerToken, List<string>? consignmentIds = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching tracking updates from Shiplogic");

        // Set the bearer token (authorization) for this request
        SetBearerToken(bearerToken);

        try
        {
            var url = "/v2/tracking/shipments";

            // Add query parameters if specific consignment IDs provided
            if (consignmentIds != null && consignmentIds.Any())
            {
                var queryString = string.Join("&", consignmentIds.Select(id => $"consignment_ids[]={id}"));
                url += $"?{queryString}";
            }

            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync(url, cancellationToken);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            });

            var tracking = await response.Content.ReadFromJsonAsync<TrackingResponse>(cancellationToken);

            if (tracking == null)
            {
                throw new InvalidOperationException("Failed to deserialize tracking response");
            }

            _logger.LogInformation("Retrieved tracking for {Count} shipments", tracking.Shipments.Count);

            return tracking;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching tracking updates");

            throw new InvalidOperationException("Failed to fetch tracking updates from Shiplogic", ex);
        }
        catch (BrokenCircuitException<HttpResponseMessage> ex)
        {
            _logger.LogError(ex, "Circuit breaker open - Shiplogic service unavailable");

            throw new InvalidOperationException("Shiplogic service is temporarily unavailable", ex);
        }
    }

    public async Task<RateResponseDto> GetRatesAsync(string bearerToken, AddressDto collectionAddress, AddressDto deliveryAddress, List<ParcelDto> parcels, string? serviceLevelCode = null, CancellationToken cancellationToken = default)
    {
        var (rates, _) = await GetRatesWithDiagnosticsAsync(bearerToken, collectionAddress, deliveryAddress, parcels, serviceLevelCode, cancellationToken);
        return rates;
    }

    public async Task<(RateResponseDto Rates, ShiplogicDiagnostics Diagnostics)> GetRatesWithDiagnosticsAsync(string bearerToken, AddressDto collectionAddress, AddressDto deliveryAddress, List<ParcelDto> parcels, string? serviceLevelCode = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting GetRatesAsync. ServiceLevel: {ServiceLevel}", serviceLevelCode);

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            _logger.LogError("Bearer token is missing!");

            throw new ArgumentException("Bearer token is required", nameof(bearerToken));
        }

        // Build request payload. Send service_level_code at the top level
        // to match the working v1 WordPress plugin payload (see
        // Plugins/WooCommerce/.../delicate-courier-platform.php — the
        // 'service_level_code' key is always present in the body, even
        // when the option is empty). Empirically, omitting this field
        // causes Shiplogic to return HTTP 200 with rates:null on some
        // account/provider/route combinations. We still filter the
        // returned rates client-side below in case Shiplogic ignores
        // the field for a given provider.
        var request = new
        {
            account_id = collectionAddress.AccountId,
            provider_id = collectionAddress.ProviderId,
            service_level_code = serviceLevelCode ?? string.Empty,
            collection_address = collectionAddress,
            delivery_address = deliveryAddress,
            parcels = parcels
        };

        var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        _logger.LogInformation("Shiplogic /v2/rates REQUEST payload: {RequestJson}", requestJson);

        var diagnostics = new ShiplogicDiagnostics
        {
            Endpoint = "POST /v2/rates",
            RequestJson = requestJson
        };

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/v2/rates", request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            diagnostics.StatusCode = (int)response.StatusCode;
            diagnostics.RawResponseBody = responseBody;
            _logger.LogInformation("Shiplogic response status: {StatusCode}, body: {ResponseBody}", response.StatusCode, responseBody);

            response.EnsureSuccessStatusCode();

            RateResponseDto? rates;
            try
            {
                rates = System.Text.Json.JsonSerializer.Deserialize<RateResponseDto>(responseBody);
            }
            catch (System.Text.Json.JsonException jex)
            {
                _logger.LogError(jex, "Failed to deserialize Shiplogic rates from response body: {ResponseBody}", responseBody);
                throw new ShiplogicCallException("Failed to deserialize rates from Shiplogic", jex, diagnostics);
            }

            if (rates == null)
            {
                _logger.LogError("Shiplogic returned a null rates payload. Body: {ResponseBody}", responseBody);
                throw new ShiplogicCallException(
                    "Failed to deserialize rates from Shiplogic",
                    new InvalidOperationException("Deserializer returned null"),
                    diagnostics);
            }

            // Shiplogic /v2/rates may return {"rates": null} on a 200 response when
            // no service level is available for the route (rather than an empty
            // array). System.Text.Json overwrites the property initializer with
            // null, so guard against it before any List operations.
            if (rates.Rates == null)
            {
                _logger.LogWarning(
                    "Shiplogic returned HTTP 200 but rates:null. " +
                    "Likely causes: provider not configured for this route, " +
                    "country code mismatch between collection/delivery addresses, " +
                    "or the bearer token's account is not authorised for the requested provider. " +
                    "Response body: {ResponseBody}",
                    responseBody);
                rates.Rates = new List<ShippingRateDto>();
            }

            // Filter to the requested service level in C# (Shiplogic v2/rates
            // returns every service level for the account; the old query
            // parameter is silently ignored). Case-insensitive match on code.
            if (!string.IsNullOrWhiteSpace(serviceLevelCode))
            {
                var before = rates.Rates.Count;
                rates.Rates = rates.Rates
                    .Where(r => string.Equals(r.ServiceLevelCode, serviceLevelCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _logger.LogInformation(
                    "Filtered Shiplogic rates by service_level_code '{Code}': {After}/{Before} kept",
                    serviceLevelCode, rates.Rates.Count, before);
            }

            _logger.LogInformation("Successfully retrieved {Count} rates from Shiplogic", rates.Rates.Count);
            return (rates, diagnostics);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request to Shiplogic failed.");
            // Wrap, but include diagnostics so the controller can surface them
            // on the debug-gated path.
            throw new ShiplogicCallException("Failed to fetch rates from Shiplogic", ex, diagnostics);
        }
    }

    public async Task<bool> CancelShipmentAsync(string bearerToken, string trackingReference, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling shipment {TrackingReference} in Shiplogic", trackingReference);

        // Set the bearer token (authorization) for this request
        SetBearerToken(bearerToken);

        try
        {
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                // Shiplogic's documented cancel endpoint is POST /v2/shipments/cancel
                // with the short tracking reference in the body.
                // (POST /v2/shipments/{id}/cancel returns 404 "Unhandled resource
                // path" — verified against the live API on 2026-07-09.)
                var httpResponse = await _httpClient.PostAsJsonAsync(
                    "/v2/shipments/cancel",
                    new { tracking_reference = trackingReference },
                    cancellationToken);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            });

            _logger.LogInformation("Shipment {TrackingReference} cancelled successfully", trackingReference);

            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error cancelling shipment {TrackingReference}", trackingReference);

            return false;
        }
        catch (BrokenCircuitException<HttpResponseMessage> ex)
        {
            _logger.LogError(ex, "Circuit breaker open - Shiplogic service unavailable");

            return false;
        }
    }

    /// <summary>
    /// Retrieve shipping label PDF from Shiplogic
    /// Downloads the label as a PDF file that can be printed
    /// </summary>
    public async Task<byte[]?> GetShipmentLabelAsync(string bearerToken, int shipmentId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Retrieving label for Shiplogic shipment ID: {ShipmentId}", shipmentId);

        SetBearerToken(bearerToken);

        try
        {
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync($"/shipments/{shipmentId}/label", cancellationToken);
                httpResponse.EnsureSuccessStatusCode();

                return httpResponse;
            });

            // Read the PDF bytes
            var pdfBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            _logger.LogInformation("Successfully retrieved label for shipment {ShipmentId}. Size: {Size} bytes", shipmentId, pdfBytes.Length);

            return pdfBytes;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error retrieving label for shipment {ShipmentId}. Status: {StatusCode}", shipmentId, ex.StatusCode);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve label for shipment {ShipmentId}", shipmentId);

            return null;
        }
    }

    /// <summary>
    /// Best-effort lookup-by-customer-reference. Calls Shiplogic
    /// <c>GET /shipments?customer_reference={ref}</c> and returns the first
    /// match if one exists. This is the cross-system idempotency guard for
    /// the narrow crash window between a successful Shiplogic create and
    /// the local DB save — on retry we check Shiplogic first, and if the
    /// shipment is already there we reuse it instead of double-booking.
    ///
    /// Returns null on no-match, on any HTTP error (4xx/5xx), or on JSON
    /// parse failure. The caller treats null as "go ahead and create",
    /// because a false negative just degrades to the pre-guard behaviour.
    /// We deliberately bypass the retry/circuit-breaker policies here so
    /// a Shiplogic outage on this lookup doesn't block the create attempt
    /// that follows — the create call has its own retry policy.
    /// </summary>
    public async Task<CreateShipmentResponse?> FindShipmentByCustomerReferenceAsync(
        string bearerToken,
        string customerReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerReference))
        {
            return null;
        }

        SetBearerToken(bearerToken);

        try
        {
            var encoded = Uri.EscapeDataString(customerReference);
            var url = $"/shipments?customer_reference={encoded}";

            _logger.LogInformation(
                "Shiplogic idempotency lookup: GET {Url}", url);

            var httpResponse = await _httpClient.GetAsync(url, cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Shiplogic idempotency lookup returned {StatusCode} for {Reference} — treating as not found.",
                    httpResponse.StatusCode, customerReference);
                return null;
            }

            var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            // Shiplogic's list endpoint shape is not contractually fixed in
            // our code base — accept several common shapes defensively, but
            // we ONLY accept arrays (a list response). A bare single-object
            // response is rejected because Shiplogic might be ignoring our
            // query parameter entirely and just returning "the first
            // shipment in the account" — accepting that would attach a
            // totally unrelated shipment to this order.
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            System.Text.Json.JsonElement? candidates = null;

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                candidates = root;
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var key in new[] { "shipments", "data", "results" })
                {
                    if (root.TryGetProperty(key, out var arr)
                        && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        candidates = arr;
                        break;
                    }
                }
            }

            if (candidates == null || candidates.Value.GetArrayLength() == 0)
            {
                _logger.LogInformation(
                    "Shiplogic idempotency lookup: no existing shipment for {Reference}.",
                    customerReference);
                return null;
            }

            // CRITICAL: only accept a match whose customer_reference field
            // EQUALS what we asked for. This is the false-positive guard:
            // if Shiplogic silently ignored the query string and returned
            // an unfiltered list, the first row's reference will not match
            // and we will correctly fall through to create.
            foreach (var element in candidates.Value.EnumerateArray())
            {
                if (!element.TryGetProperty("customer_reference", out var refProp)
                    || refProp.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                var elementRef = refProp.GetString();
                if (!string.Equals(elementRef, customerReference, StringComparison.Ordinal))
                {
                    continue;
                }

                var match = System.Text.Json.JsonSerializer.Deserialize<CreateShipmentResponse>(element.GetRawText());
                if (match == null || match.ShipmentId == 0)
                {
                    continue;
                }

                _logger.LogWarning(
                    "Shiplogic idempotency lookup HIT: existing ShipmentId={ShipmentId} Tracking={Tracking} for {Reference} — will reuse, not create.",
                    match.ShipmentId, match.TrackingNumber, customerReference);

                return match;
            }

            _logger.LogInformation(
                "Shiplogic idempotency lookup: list returned but no row's customer_reference matched {Reference}. Treating as no-match and proceeding to create.",
                customerReference);
            return null;
        }
        catch (Exception ex)
        {
            // Any failure here is non-fatal: log and fall through to create.
            _logger.LogWarning(ex,
                "Shiplogic idempotency lookup failed for {Reference} — proceeding to create (lookup is best-effort).",
                customerReference);
            return null;
        }
    }
}