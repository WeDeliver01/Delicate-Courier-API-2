using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DelicateCouriers.ApiService.Features.Shopify
{
    /// <summary>
    /// Service for interacting with the Shopify Admin REST API.
    /// Mirrors the structure and error-handling style of WooCommerceService:
    /// every public method is wrapped in try/catch, logs failures, and returns
    /// a soft failure (empty list / null / false / IsConnected=false) instead
    /// of throwing — so callers (controllers, Hangfire jobs) don't need to
    /// re-implement defensive plumbing.
    /// </summary>
    public class ShopifyService : IShopifyService
    {
        private const string ShopifyApiVersion = ShopifyApiConstants.ApiVersion;

        // Name shown to the merchant in Shopify Admin → Settings → Shipping
        // for the live carrier-service rate option.
        private const string CarrierServiceName = "Delicate Couriers";

        // Order webhook topics the ingestion pipeline (ShopifyWebhookController
        // → ShopifyOrderIngestionService) knows how to process. Keep in sync
        // with ShopifyWebhookController.ProcessableTopics.
        private static readonly string[] OrderWebhookTopics =
        {
            "orders/create",
            "orders/updated",
            "orders/cancelled"
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ShopifyService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ShopifyService(
            IHttpClientFactory httpClientFactory,
            AppDbContext context,
            IConfiguration configuration,
            ILogger<ShopifyService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Register (or update) the Shopify Carrier Service so our live-rate
        /// callback (POST /api/shopify/rates/{storeId}) appears as a shipping
        /// option at the merchant's checkout. Idempotent: if a carrier service
        /// with our name / callback already exists, we PUT-update it instead of
        /// creating a duplicate.
        /// </summary>
        public async Task<ShopifyCarrierServiceResult> RegisterCarrierServiceAsync(int storeId)
        {
            try
            {
                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return new ShopifyCarrierServiceResult { Success = false, Message = "Store not found" };
                }

                if (string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return new ShopifyCarrierServiceResult
                    {
                        Success = false,
                        Message = "Store is not configured for Shopify (missing URL or access token)"
                    };
                }

                var apiBase = _configuration["PublicUrls:ApiBaseUrl"]?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(apiBase))
                {
                    return new ShopifyCarrierServiceResult { Success = false, Message = "PublicUrls:ApiBaseUrl is not configured" };
                }

                var callbackUrl = $"{apiBase}/api/shopify/rates/{storeId}";

                using var client = CreateAuthenticatedClient(store);

                // 1. Look for an existing carrier service we own (matched by
                //    name or callback URL) so we update rather than duplicate.
                long? existingId = null;
                var listResponse = await client.GetAsync($"/admin/api/{ShopifyApiVersion}/carrier_services.json");
                if (listResponse.IsSuccessStatusCode)
                {
                    var listBody = await listResponse.Content.ReadAsStringAsync();
                    var wrapper = JsonSerializer.Deserialize<CarrierServicesWrapper>(listBody, JsonOptions);
                    var match = wrapper?.CarrierServices?.FirstOrDefault(cs =>
                        string.Equals(cs.CallbackUrl?.TrimEnd('/'), callbackUrl, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(cs.Name, CarrierServiceName, StringComparison.OrdinalIgnoreCase));
                    existingId = match?.Id;
                }

                var payload = new
                {
                    carrier_service = new
                    {
                        name = CarrierServiceName,
                        callback_url = callbackUrl,
                        service_discovery = true,
                        format = "json"
                    }
                };

                var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                HttpResponseMessage response = existingId.HasValue
                    ? await client.PutAsync($"/admin/api/{ShopifyApiVersion}/carrier_services/{existingId.Value}.json", httpContent)
                    : await client.PostAsync($"/admin/api/{ShopifyApiVersion}/carrier_services.json", httpContent);

                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed to register Shopify carrier service for StoreID {StoreId}. Status: {Status}, Body: {Body}",
                        storeId, response.StatusCode, responseBody);
                    return new ShopifyCarrierServiceResult
                    {
                        Success = false,
                        Message = $"Shopify rejected carrier service registration ({(int)response.StatusCode}). " +
                                  "The store's access token likely lacks the Carrier Service scope, " +
                                  "or live rates are not enabled on this Shopify plan.",
                        CallbackUrl = callbackUrl
                    };
                }

                _logger.LogInformation(
                    "Registered Shopify carrier service for StoreID {StoreId} → {CallbackUrl} ({Action}). Raw Shopify response: {Body}",
                    storeId, callbackUrl, existingId.HasValue ? "updated" : "created", responseBody);

                return new ShopifyCarrierServiceResult
                {
                    Success = true,
                    Message = existingId.HasValue ? "Carrier service updated" : "Carrier service created",
                    CallbackUrl = callbackUrl
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception registering Shopify carrier service for StoreID {StoreId}", storeId);
                return new ShopifyCarrierServiceResult { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// List the carrier services registered on a Shopify store (read-only).
        /// Useful for verifying the Delicate Couriers carrier service is present
        /// and pointing at the expected live-rate callback.
        /// </summary>
        public async Task<ShopifyCarrierServiceListResult> ListCarrierServicesAsync(int storeId)
        {
            try
            {
                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return new ShopifyCarrierServiceListResult { Success = false, Message = "Store not found" };
                }

                if (string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return new ShopifyCarrierServiceListResult
                    {
                        Success = false,
                        Message = "Store is not configured for Shopify (missing URL or access token)"
                    };
                }

                using var client = CreateAuthenticatedClient(store);

                var response = await client.GetAsync($"/admin/api/{ShopifyApiVersion}/carrier_services.json");
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed to list Shopify carrier services for StoreID {StoreId}. Status: {Status}, Body: {Body}",
                        storeId, response.StatusCode, body);
                    return new ShopifyCarrierServiceListResult
                    {
                        Success = false,
                        Message = $"Shopify rejected listing carrier services ({(int)response.StatusCode}). " +
                                  "The store's access token likely lacks the Carrier Service scope, " +
                                  "or live rates are not enabled on this Shopify plan."
                    };
                }

                var wrapper = JsonSerializer.Deserialize<CarrierServicesWrapper>(body, JsonOptions);
                var items = wrapper?.CarrierServices?.Select(cs => new ShopifyCarrierServiceItem
                {
                    Id = cs.Id,
                    Name = cs.Name,
                    CallbackUrl = cs.CallbackUrl,
                    Active = cs.Active
                }).ToList() ?? new List<ShopifyCarrierServiceItem>();

                return new ShopifyCarrierServiceListResult
                {
                    Success = true,
                    Message = $"Found {items.Count} carrier service(s).",
                    CarrierServices = items
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception listing Shopify carrier services for StoreID {StoreId}", storeId);
                return new ShopifyCarrierServiceListResult { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Subscribe (or re-point) the store to the order webhooks that drive the
        /// ingestion pipeline: orders/create, orders/updated, orders/cancelled.
        /// All point at our existing receiver (POST /api/webhooks/shopify/order).
        /// Idempotent: lists existing webhooks first and only creates the ones
        /// that are missing; if a webhook for the topic exists but points at a
        /// different address we PUT-update it instead of creating a duplicate.
        ///
        /// SIGNATURE-SECRET CONSTRAINT: webhooks created via the Admin API are
        /// HMAC-signed by Shopify using the *app's API secret key*, while the
        /// receiver (ShopifyWebhookController) verifies against the store's
        /// ShopifyWebhookSecret. For these subscriptions to pass verification the
        /// store's webhook secret must be that same app API secret. We don't have
        /// it here (the access token is all we store), so we surface a warning
        /// when the secret is missing rather than silently subscribing to
        /// webhooks that will be rejected with 401.
        /// </summary>
        public async Task<ShopifyWebhookSubscriptionResult> SubscribeOrderWebhooksAsync(int storeId)
        {
            var result = new ShopifyWebhookSubscriptionResult();
            try
            {
                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    result.Message = "Store not found";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    result.Message = "Store is not configured for Shopify (missing URL or access token)";
                    return result;
                }

                var apiBase = _configuration["PublicUrls:ApiBaseUrl"]?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(apiBase))
                {
                    result.Message = "PublicUrls:ApiBaseUrl is not configured";
                    return result;
                }

                var callbackUrl = $"{apiBase}/api/webhooks/shopify/order";
                result.CallbackUrl = callbackUrl;
                result.WebhookSecretConfigured = !string.IsNullOrWhiteSpace(store.ShopifyWebhookSecret);

                using var client = CreateAuthenticatedClient(store);

                // 1. List ALL existing webhooks (following Shopify's Link-header
                //    pagination) so reconciliation is correct even when a store
                //    has more than one page of app webhooks. Without this, a
                //    subscription living on a later page would be missed and we'd
                //    create a duplicate.
                var existing = new List<WebhookDto>();
                var nextPath = $"/admin/api/{ShopifyApiVersion}/webhooks.json?limit=250";
                while (!string.IsNullOrEmpty(nextPath))
                {
                    var listResponse = await client.GetAsync(nextPath);
                    if (!listResponse.IsSuccessStatusCode)
                    {
                        var body = await listResponse.Content.ReadAsStringAsync();
                        _logger.LogWarning(
                            "Failed to list Shopify webhooks for StoreID {StoreId}. Status: {Status}, Body: {Body}",
                            storeId, listResponse.StatusCode, body);
                        result.Message = DescribeWebhookFailure(listResponse.StatusCode, "list webhooks");
                        return result;
                    }

                    var listBody = await listResponse.Content.ReadAsStringAsync();
                    var wrapper = JsonSerializer.Deserialize<WebhooksWrapper>(listBody, JsonOptions);
                    if (wrapper?.Webhooks != null) existing.AddRange(wrapper.Webhooks);

                    nextPath = ExtractNextPath(listResponse, store);
                }

                // 2. Reconcile each topic.
                foreach (var topic in OrderWebhookTopics)
                {
                    var match = existing.FirstOrDefault(w =>
                        string.Equals(w.Topic, topic, StringComparison.OrdinalIgnoreCase));

                    // Already subscribed at our address → no-op.
                    if (match != null &&
                        string.Equals(match.Address?.TrimEnd('/'), callbackUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        result.AlreadyPresent.Add(topic);
                        continue;
                    }

                    var payload = new
                    {
                        webhook = new
                        {
                            topic,
                            address = callbackUrl,
                            format = "json"
                        }
                    };
                    var httpContent = new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

                    HttpResponseMessage response = match != null
                        ? await client.PutAsync($"/admin/api/{ShopifyApiVersion}/webhooks/{match.Id}.json", httpContent)
                        : await client.PostAsync($"/admin/api/{ShopifyApiVersion}/webhooks.json", httpContent);

                    if (response.IsSuccessStatusCode)
                    {
                        if (match != null) result.Updated.Add(topic);
                        else result.Created.Add(topic);
                    }
                    else
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning(
                            "Failed to subscribe Shopify webhook {Topic} for StoreID {StoreId}. Status: {Status}, Body: {Body}",
                            topic, storeId, response.StatusCode, body);
                        result.Failed.Add(topic);
                    }
                }

                var parts = new List<string>();
                if (result.Created.Count > 0) parts.Add($"created {result.Created.Count}");
                if (result.Updated.Count > 0) parts.Add($"updated {result.Updated.Count}");
                if (result.AlreadyPresent.Count > 0) parts.Add($"already present {result.AlreadyPresent.Count}");
                if (result.Failed.Count > 0) parts.Add($"failed {result.Failed.Count}");

                var allTopicsSubscribed = result.Failed.Count == 0;

                // Subscribing the topics is only half the job: every delivery is
                // HMAC-signed by Shopify and the receiver verifies it against the
                // store's webhook secret. With NO secret on file, every delivery is
                // GUARANTEED to be rejected (401), so this is a hard failure of the
                // end-to-end objective — not a soft warning. We deliberately do not
                // report unqualified success in that case.
                //
                // Note on a *wrong* (present but mismatched) secret: this platform
                // uses a per-store Shopify custom app, so the signing secret is that
                // store's own custom-app API secret, entered by hand on the Stores
                // page. There is no global app API secret in our config to compare
                // against, and Shopify never exposes a store's API secret over the
                // Admin API, so a mismatch cannot be detected here — it surfaces as
                // 401s in the receiver logs. We therefore enforce only what we can
                // verify: that a secret is present.
                result.Success = allTopicsSubscribed && result.WebhookSecretConfigured;

                if (!allTopicsSubscribed)
                {
                    result.Message =
                        $"Some webhooks could not be subscribed ({string.Join(", ", parts)}). " +
                        "Common causes: the access token lacks the read_orders scope, the token is " +
                        "invalid/expired, or Shopify is rate-limiting — see the server logs for the exact status.";
                }
                else if (!result.WebhookSecretConfigured)
                {
                    result.Message =
                        $"Webhooks were subscribed ({string.Join(", ", parts)}), but this store has NO webhook " +
                        "secret on file — every delivery will be rejected (401) until the store's Shopify " +
                        "custom-app API secret is saved as its webhook secret on the Stores page. Setup is not " +
                        "complete until then.";
                }
                else
                {
                    result.Message = $"Order webhooks subscribed ({string.Join(", ", parts)}).";
                }

                _logger.LogInformation(
                    "Subscribed Shopify order webhooks for StoreID {StoreId} → {CallbackUrl}. {Summary}",
                    storeId, callbackUrl, string.Join(", ", parts));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception subscribing Shopify order webhooks for StoreID {StoreId}", storeId);
                result.Success = false;
                result.Message = $"Error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Run the complete Shopify store setup in one call: test the connection,
        /// then (only if it succeeds) register the carrier service and subscribe
        /// the order webhooks. Each step is idempotent and its individual result
        /// is preserved on the aggregate. A failed connection short-circuits the
        /// remaining steps because they would fail anyway with a bad token/URL.
        /// </summary>
        public async Task<ShopifyFullRegistrationResult> FullRegistrationAsync(int storeId)
        {
            var result = new ShopifyFullRegistrationResult();

            try
            {
                // 1. Connection test — gates the rest.
                result.Connection = await TestConnectionAsync(storeId);
                if (!result.Connection.IsConnected)
                {
                    result.Success = false;
                    result.Message =
                        $"Connection test failed, so carrier service and webhooks were skipped: {result.Connection.Message}";
                    return result;
                }

                // 2. Carrier service (live checkout rates).
                result.CarrierService = await RegisterCarrierServiceAsync(storeId);

                // 3. Order webhooks (order ingestion).
                result.Webhooks = await SubscribeOrderWebhooksAsync(storeId);

                result.Success = result.CarrierService.Success && result.Webhooks.Success;

                var carrier = result.CarrierService.Success ? "carrier service OK" : "carrier service FAILED";
                var hooks = result.Webhooks.Success ? "webhooks OK" : "webhooks FAILED";
                result.Message = result.Success
                    ? $"Full registration complete: connection OK, {carrier}, {hooks}."
                    : $"Full registration partially failed: connection OK, {carrier}, {hooks}. " +
                      "See each step's result for details.";

                _logger.LogInformation(
                    "Shopify full registration for StoreID {StoreId}: success={Success} ({Carrier}, {Hooks})",
                    storeId, result.Success, carrier, hooks);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during Shopify full registration for StoreID {StoreId}", storeId);
                result.Success = false;
                result.Message = $"Error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// List active Shopify-platform stores (with tenant name + a flag for
        /// whether an access token is on file) for the SuperAdmin carrier-service
        /// UI. Never returns secret values.
        /// </summary>
        public async Task<List<ShopifyStoreSummary>> ListShopifyStoresAsync()
        {
            return await _context.Stores
                .AsNoTracking()
                .Where(s => s.IsActive && s.Platform == "shopify")
                .OrderBy(s => s.StoreName)
                .Select(s => new ShopifyStoreSummary
                {
                    StoreID = s.StoreID,
                    StoreName = s.StoreName,
                    TenantID = s.TenantID,
                    TenantName = s.Tenant != null ? s.Tenant.TenantName : string.Empty,
                    ShopifyStoreUrl = s.ShopifyStoreUrl,
                    HasAccessToken = !string.IsNullOrWhiteSpace(s.ShopifyAccessToken),
                    IsActive = s.IsActive
                })
                .ToListAsync();
        }

        public async Task<ShopifyConnectionResponse> TestConnectionAsync(int storeId)
        {
            try
            {
                _logger.LogInformation("Testing Shopify connection for StoreID: {StoreId}", storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return new ShopifyConnectionResponse
                    {
                        IsConnected = false,
                        Message = "Store not found"
                    };
                }

                if (string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return new ShopifyConnectionResponse
                    {
                        IsConnected = false,
                        Message = "Store is not configured for Shopify (missing URL or access token)",
                        StoreUrl = store.ShopifyStoreUrl
                    };
                }

                using var client = CreateAuthenticatedClient(store);
                var response = await client.GetAsync($"/admin/api/{ShopifyApiVersion}/shop.json");

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully connected to Shopify store {StoreId}", storeId);

                    return new ShopifyConnectionResponse
                    {
                        IsConnected = true,
                        Message = "Connection successful",
                        StoreUrl = store.ShopifyStoreUrl,
                        ShopifyApiVersion = ShopifyApiVersion
                    };
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to connect to Shopify store {StoreId}. Status: {Status}, Error: {Error}",
                    storeId, response.StatusCode, errorBody);

                return new ShopifyConnectionResponse
                {
                    IsConnected = false,
                    Message = $"Connection failed: {response.StatusCode}",
                    StoreUrl = store.ShopifyStoreUrl
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception testing Shopify connection for StoreID: {StoreId}", storeId);

                return new ShopifyConnectionResponse
                {
                    IsConnected = false,
                    Message = $"Connection error: {ex.Message}"
                };
            }
        }

        public async Task<ShopifyOrdersResponse> FetchOrdersAsync(FetchOrdersRequest request)
        {
            var emptyResponse = new ShopifyOrdersResponse
            {
                StoreID = request.StoreID,
                Count = 0,
                Orders = new List<ShopifyOrderDto>()
            };

            try
            {
                _logger.LogInformation("Fetching Shopify orders for StoreID: {StoreId}, Page: {Page}, Status: {Status}",
                    request.StoreID, request.Page, request.Status);

                var store = await GetStoreAsync(request.StoreID);
                if (store == null || string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    _logger.LogWarning("Store {StoreId} not found or not configured for Shopify when fetching orders", request.StoreID);
                    return emptyResponse;
                }

                using var client = CreateAuthenticatedClient(store);

                var queryParams = new List<string>
                {
                    $"limit={Math.Clamp(request.PerPage, 1, 250)}", // Shopify max is 250
                    $"status={(string.IsNullOrEmpty(request.Status) ? "any" : request.Status)}"
                };

                if (request.After.HasValue)
                {
                    queryParams.Add($"created_at_min={request.After.Value:yyyy-MM-ddTHH:mm:ssZ}");
                }
                if (request.Before.HasValue)
                {
                    queryParams.Add($"created_at_max={request.Before.Value:yyyy-MM-ddTHH:mm:ssZ}");
                }

                var url = $"/admin/api/{ShopifyApiVersion}/orders.json?{string.Join("&", queryParams)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to fetch Shopify orders for StoreID: {StoreId}. Status: {Status}, Error: {Error}",
                        request.StoreID, response.StatusCode, errorBody);
                    return emptyResponse;
                }

                var content = await response.Content.ReadAsStringAsync();
                var wrapper = JsonSerializer.Deserialize<ShopifyOrdersWrapper>(content, JsonOptions);
                var orders = wrapper?.Orders ?? new List<ShopifyOrderDto>();

                _logger.LogInformation("Successfully fetched {Count} Shopify orders for StoreID: {StoreId}", orders.Count, request.StoreID);

                return new ShopifyOrdersResponse
                {
                    StoreID = request.StoreID,
                    Count = orders.Count,
                    Orders = orders
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception fetching Shopify orders for StoreID: {StoreId}", request.StoreID);
                return emptyResponse;
            }
        }

        public async Task<ShopifyOrderDto?> GetOrderAsync(int storeId, string shopifyOrderId)
        {
            try
            {
                _logger.LogInformation("Fetching Shopify order {OrderId} from StoreID: {StoreId}", shopifyOrderId, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null || string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return null;
                }

                using var client = CreateAuthenticatedClient(store);
                var response = await client.GetAsync($"/admin/api/{ShopifyApiVersion}/orders/{shopifyOrderId}.json");

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to fetch Shopify order {OrderId} for StoreID: {StoreId}. Status: {Status}, Error: {Error}",
                        shopifyOrderId, storeId, response.StatusCode, errorBody);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var wrapper = JsonSerializer.Deserialize<ShopifyOrderWrapper>(content, JsonOptions);
                return wrapper?.Order;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception fetching Shopify order {OrderId} from StoreID: {StoreId}", shopifyOrderId, storeId);
                return null;
            }
        }

        public async Task<bool> UpdateOrderStatusAsync(int storeId, string shopifyOrderId, string status)
        {
            try
            {
                _logger.LogInformation("Updating Shopify order {OrderId} status to {Status} in StoreID: {StoreId}", shopifyOrderId, status, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null || string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return false;
                }

                using var client = CreateAuthenticatedClient(store);

                // Shopify's Order resource doesn't expose a single "status" property
                // (it has financial_status + fulfillment_status). The closest generic
                // mutation that works via PUT /orders/{id}.json is updating note/tags,
                // which we use here as a status marker. Concrete fulfillment workflows
                // should be expressed via AddTrackingInfoAsync (which creates a
                // fulfillment) — this method just records the status change on the
                // order itself.
                var payload = new
                {
                    order = new
                    {
                        id = shopifyOrderId,
                        tags = $"delicate-status:{status}",
                        note = $"Status updated to {status} by Delicate Couriers"
                    }
                };

                var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await client.PutAsync($"/admin/api/{ShopifyApiVersion}/orders/{shopifyOrderId}.json", httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to update Shopify order {OrderId} in StoreID: {StoreId}. Status: {Status}, Error: {Error}",
                        shopifyOrderId, storeId, response.StatusCode, errorBody);
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception updating Shopify order {OrderId} status in StoreID: {StoreId}", shopifyOrderId, storeId);
                return false;
            }
        }

        public async Task<bool> AddTrackingInfoAsync(int storeId, string shopifyOrderId, string trackingNumber, string courierName)
        {
            try
            {
                _logger.LogInformation("Adding tracking info to Shopify order {OrderId} in StoreID: {StoreId}", shopifyOrderId, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null || string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return false;
                }

                using var client = CreateAuthenticatedClient(store);

                // Shopify tracking is recorded by creating a Fulfillment for the order.
                // We let Shopify pick a default location/line items by omitting them —
                // for the skeleton call this is the simplest valid payload. A future
                // enhancement should look up the order's fulfillment_orders and pass
                // line_items_by_fulfillment_order explicitly.
                var payload = new
                {
                    fulfillment = new
                    {
                        message = $"Shipped via {courierName}",
                        notify_customer = true,
                        tracking_info = new
                        {
                            number = trackingNumber,
                            company = courierName
                        },
                        line_items_by_fulfillment_order = new[]
                        {
                            new { fulfillment_order_id = (long?)null }
                        }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Per task spec, POST to /fulfillments.json (the 2024-01 fulfillment endpoint).
                var response = await client.PostAsync($"/admin/api/{ShopifyApiVersion}/fulfillments.json", httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to add tracking info to Shopify order {OrderId} in StoreID: {StoreId}. Status: {Status}, Error: {Error}",
                        shopifyOrderId, storeId, response.StatusCode, errorBody);
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception adding tracking info to Shopify order {OrderId} in StoreID: {StoreId}", shopifyOrderId, storeId);
                return false;
            }
        }

        public async Task<List<ShopifyOrderDto>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000)
        {
            var allOrders = new List<ShopifyOrderDto>();

            try
            {
                _logger.LogInformation("Starting bulk Shopify order fetch for StoreID: {StoreId}, MaxOrders: {MaxOrders}", storeId, maxOrders);

                var store = await GetStoreAsync(storeId);
                if (store == null || string.IsNullOrWhiteSpace(store.ShopifyStoreUrl) || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    _logger.LogWarning("Store {StoreId} not found or not configured for Shopify when bulk fetching orders", storeId);
                    return allOrders;
                }

                using var client = CreateAuthenticatedClient(store);

                // Build the first page URL. For subsequent pages we follow the
                // Link header's rel="next" cursor (page_info=...). When using
                // page_info, all other filters MUST be omitted per Shopify's docs.
                const int perPage = 250; // Shopify's max page size.
                var queryParams = new List<string>
                {
                    $"limit={perPage}",
                    $"status={(string.IsNullOrEmpty(status) ? "any" : status)}"
                };
                if (after.HasValue) queryParams.Add($"created_at_min={after.Value:yyyy-MM-ddTHH:mm:ssZ}");
                if (before.HasValue) queryParams.Add($"created_at_max={before.Value:yyyy-MM-ddTHH:mm:ssZ}");

                var nextUrl = $"/admin/api/{ShopifyApiVersion}/orders.json?{string.Join("&", queryParams)}";
                var pagesFetched = 0;

                while (!string.IsNullOrEmpty(nextUrl) && allOrders.Count < maxOrders)
                {
                    var response = await client.GetAsync(nextUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Bulk fetch failed at page {Page} for StoreID: {StoreId}. Status: {Status}, Error: {Error}",
                            pagesFetched + 1, storeId, response.StatusCode, errorBody);
                        break;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var wrapper = JsonSerializer.Deserialize<ShopifyOrdersWrapper>(content, JsonOptions);
                    var pageOrders = wrapper?.Orders ?? new List<ShopifyOrderDto>();

                    if (pageOrders.Count == 0)
                    {
                        break;
                    }

                    // Respect maxOrders precisely.
                    var remaining = maxOrders - allOrders.Count;
                    if (pageOrders.Count > remaining)
                    {
                        allOrders.AddRange(pageOrders.GetRange(0, remaining));
                    }
                    else
                    {
                        allOrders.AddRange(pageOrders);
                    }

                    pagesFetched++;
                    _logger.LogInformation("Fetched Shopify page {Page}: {Count} orders (Total so far: {Total})",
                        pagesFetched, pageOrders.Count, allOrders.Count);

                    // Find the next cursor from the Link header (rel="next").
                    nextUrl = ExtractNextPath(response, store);

                    // Safety: don't loop forever even if Shopify keeps handing us cursors.
                    if (pagesFetched > 1000)
                    {
                        _logger.LogWarning("Reached maximum page limit (1000) for StoreID: {StoreId}", storeId);
                        break;
                    }
                }

                _logger.LogInformation("Bulk Shopify order fetch completed for StoreID: {StoreId}. Total orders: {Total}", storeId, allOrders.Count);
                return allOrders;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during bulk Shopify order fetch for StoreID: {StoreId}", storeId);
                return allOrders; // Return whatever we managed to fetch.
            }
        }

        /// <summary>
        /// Build an authenticated Shopify Admin REST client for the given store.
        /// </summary>
        private HttpClient CreateAuthenticatedClient(Store store)
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(NormaliseStoreBaseUrl(store.ShopifyStoreUrl!));
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", store.ShopifyAccessToken);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        /// <summary>
        /// Shopify accepts both "https://shop.myshopify.com" and bare
        /// "shop.myshopify.com" in our config. Normalise to a https:// scheme
        /// with no trailing path so HttpClient.BaseAddress is happy.
        /// </summary>
        private static string NormaliseStoreBaseUrl(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}";
            }

            return trimmed;
        }

        /// <summary>
        /// Pull the rel="next" URL out of Shopify's Link header and return it
        /// as a relative path (so it works against the same BaseAddress).
        /// Returns null when there is no next page.
        /// </summary>
        private static string? ExtractNextPath(HttpResponseMessage response, Store store)
        {
            if (!response.Headers.TryGetValues("Link", out var linkValues))
            {
                return null;
            }

            // Header format: <https://shop.myshopify.com/admin/api/2024-01/orders.json?page_info=XXX&limit=250>; rel="next", <...>; rel="previous"
            var combined = string.Join(",", linkValues);
            var match = Regex.Match(combined, @"<(?<url>[^>]+)>;\s*rel=""next""", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            var fullUrl = match.Groups["url"].Value;

            if (Uri.TryCreate(fullUrl, UriKind.Absolute, out var nextUri))
            {
                return nextUri.PathAndQuery;
            }

            return fullUrl;
        }

        /// <summary>
        /// Fetch the active store row. Read-only — better for concurrent reads.
        /// </summary>
        private async Task<Store?> GetStoreAsync(int storeId)
        {
            return await _context.Stores
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive);
        }

        // ---- Internal Shopify response envelopes ----
        // Shopify wraps single resources and collections in an outer property.

        private sealed class ShopifyOrderWrapper
        {
            [JsonPropertyName("order")]
            public ShopifyOrderDto? Order { get; set; }
        }

        private sealed class ShopifyOrdersWrapper
        {
            [JsonPropertyName("orders")]
            public List<ShopifyOrderDto> Orders { get; set; } = new();
        }

        private sealed class CarrierServicesWrapper
        {
            [JsonPropertyName("carrier_services")]
            public List<CarrierServiceDto> CarrierServices { get; set; } = new();
        }

        private sealed class CarrierServiceDto
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("callback_url")]
            public string? CallbackUrl { get; set; }

            [JsonPropertyName("active")]
            public bool Active { get; set; }
        }

        /// <summary>
        /// Turn a failed Shopify webhook Admin-API response into a human-readable
        /// cause instead of always blaming a missing scope.
        /// </summary>
        private static string DescribeWebhookFailure(System.Net.HttpStatusCode status, string action)
        {
            var code = (int)status;
            var cause = code switch
            {
                401 => "the store's access token is invalid or expired",
                403 => "the access token lacks the required scope (read_orders / write_orders)",
                404 => "the store or resource was not found",
                429 => "Shopify is rate-limiting the request — try again shortly",
                >= 500 => "Shopify returned a server error — try again shortly",
                _ => "Shopify rejected the request"
            };
            return $"Failed to {action} ({code}): {cause}.";
        }

        private sealed class WebhooksWrapper
        {
            [JsonPropertyName("webhooks")]
            public List<WebhookDto> Webhooks { get; set; } = new();
        }

        private sealed class WebhookDto
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }

            [JsonPropertyName("topic")]
            public string? Topic { get; set; }

            [JsonPropertyName("address")]
            public string? Address { get; set; }
        }
    }
}
