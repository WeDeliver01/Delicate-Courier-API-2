using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Service for interacting with WooCommerce REST API
    /// Handles authentication, API calls, retries, and error handling
    /// Thread-safe and designed for concurrent usage (100-200 users)
    /// </summary>
    public class WooCommerceService : IWooCommerceService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppDbContext _context;
        private readonly ILogger<WooCommerceService> _logger;
        private readonly WooCommerceRateLimiter _rateLimiter;

        public WooCommerceService(IHttpClientFactory httpClientFactory, AppDbContext context, ILogger<WooCommerceService> logger, WooCommerceRateLimiter rateLimiter)
        {
            _httpClientFactory = httpClientFactory;
            _context = context;
            _logger = logger;
            _rateLimiter = rateLimiter;
        }

        /// <summary>
        /// Test connection to WooCommerce store
        /// Attempts to fetch store information to verify credentials work
        /// </summary>
        public async Task<WooCommerceConnectionResponse> TestConnectionAsync(int storeId)
        {
            try
            {
                _logger.LogInformation("Testing WooCommerce connection for StoreID: {StoreId}", storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return new WooCommerceConnectionResponse
                    {
                        IsConnected = false,
                        Message = "Store not found"
                    };
                }

                // Enforce rate limiting
                await _rateLimiter.WaitIfNeededAsync(storeId);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                // Test connection by fetching system status
                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Get, "/wp-json/wc/v3/system_status", null, store.WooConsumerKey, store.WooConsumerSecret);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var systemStatus = JsonSerializer.Deserialize<JsonElement>(content);

                    var version = systemStatus.GetProperty("environment").GetProperty("version").GetString();

                    _logger.LogInformation("Successfully connected to WooCommerce store {StoreId}, Version: {Version}", storeId, version);

                    return new WooCommerceConnectionResponse
                    {
                        IsConnected = true,
                        Message = "Connection successful",
                        StoreUrl = store.WooCommerceURL,
                        WooCommerceVersion = version
                    };
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    _logger.LogWarning("Failed to connect to WooCommerce store {StoreId}. Status: {Status}, Error: {Error}", storeId, response.StatusCode, errorContent);

                    return new WooCommerceConnectionResponse
                    {
                        IsConnected = false,
                        Message = $"Connection failed: {response.StatusCode}",
                        StoreUrl = store.WooCommerceURL
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception testing WooCommerce connection for StoreID: {StoreId}", storeId);

                return new WooCommerceConnectionResponse
                {
                    IsConnected = false,
                    Message = $"Connection error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Fetch orders from WooCommerce with filtering
        /// Supports pagination for handling large order volumes
        /// </summary>
        public async Task<List<WooCommerceOrder>> FetchOrdersAsync(FetchOrdersRequest request)
        {
            try
            {
                _logger.LogInformation("Fetching orders from StoreID: {StoreId}, Page: {Page}, Status: {Status}", request.StoreID, request.Page, request.Status);

                var store = await GetStoreAsync(request.StoreID);

                if (store == null)
                {
                    _logger.LogWarning("Store {StoreId} not found when fetching orders", request.StoreID);

                    return new List<WooCommerceOrder>();
                }

                // Enforce rate limiting
                await _rateLimiter.WaitIfNeededAsync(request.StoreID);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                // Build query parameters
                var queryParams = new List<string>
                {
                    $"page={request.Page}",
                    $"per_page={request.PerPage}"
                };

                if (!string.IsNullOrEmpty(request.Status))
                {
                    queryParams.Add($"status={request.Status}");
                }

                if (request.After.HasValue)
                {
                    queryParams.Add($"after={request.After.Value:yyyy-MM-ddTHH:mm:ss}");
                }

                if (request.Before.HasValue)
                {
                    queryParams.Add($"before={request.Before.Value:yyyy-MM-ddTHH:mm:ss}");
                }

                var queryString = string.Join("&", queryParams);
                var url = $"/wp-json/wc/v3/orders?{queryString}";

                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Get, url, null, store.WooConsumerKey, store.WooConsumerSecret);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var orders = JsonSerializer.Deserialize<List<WooCommerceOrder>>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<WooCommerceOrder>();

                    _logger.LogInformation("Successfully fetched {Count} orders from StoreID: {StoreId}", orders.Count, request.StoreID);

                    return orders;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    _logger.LogError("Failed to fetch orders from StoreID: {StoreId}. Status: {Status}, Error: {Error}", request.StoreID, response.StatusCode, errorContent);

                    return new List<WooCommerceOrder>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception fetching orders from StoreID: {StoreId}", request.StoreID);

                return new List<WooCommerceOrder>();
            }
        }

        /// <summary>
        /// Get a specific order by WooCommerce order ID
        /// </summary>
        public async Task<WooCommerceOrder?> GetOrderAsync(int storeId, int wooOrderId)
        {
            try
            {
                _logger.LogInformation("Fetching order {OrderId} from StoreID: {StoreId}", wooOrderId, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return null;
                }

                // Enforce rate limiting
                await _rateLimiter.WaitIfNeededAsync(storeId);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);
                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Get, $"/wp-json/wc/v3/orders/{wooOrderId}", null, store.WooConsumerKey, store.WooConsumerSecret);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var order = JsonSerializer.Deserialize<WooCommerceOrder>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return order;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception fetching order {OrderId} from StoreID: {StoreId}", wooOrderId, storeId);

                return null;
            }
        }

        /// <summary>
        /// Update order status in WooCommerce
        /// </summary>
        public async Task<bool> UpdateOrderStatusAsync(int storeId, int wooOrderId, string status)
        {
            try
            {
                _logger.LogInformation("Updating order {OrderId} status to {Status} in StoreID: {StoreId}", wooOrderId, status, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return false;
                }

                // Enforce rate limiting
                await _rateLimiter.WaitIfNeededAsync(storeId);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                var updateData = new { status };
                var jsonContent = JsonSerializer.Serialize(updateData);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Put, $"/wp-json/wc/v3/orders/{wooOrderId}", httpContent, store.WooConsumerKey, store.WooConsumerSecret);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception updating order {OrderId} status in StoreID: {StoreId}", wooOrderId, storeId);

                return false;
            }
        }

        /// <summary>
        /// Add tracking information to WooCommerce order as order note
        /// </summary>
        public async Task<bool> AddTrackingInfoAsync(int storeId, int wooOrderId, string trackingNumber, string courierName)
        {
            try
            {
                _logger.LogInformation("Adding tracking info to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return false;
                }

                // Enforce rate limiting
                await _rateLimiter.WaitIfNeededAsync(storeId);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                // INTERNAL note only (customer_note = false). Previously this
                // posted as a customer-facing note, which made WooCommerce
                // fire its "A note has been added to your order" email on
                // every booking — and once more on each Shiplogic status
                // transition that wrote tracking info back, so a single order
                // could trigger 4–5 redundant emails to the buyer.
                //
                // The tracking number + URL + courier are already written to
                // the order as `_dcp_tracking_number`, `_dcp_tracking_url`,
                // `_dcp_courier_name`, etc., and surface inside the standard
                // WooCommerce order emails (processing / completed) via the
                // merchant's existing email templates — so the buyer still
                // gets the info, just in the email they're already expecting
                // instead of an extra "note added" notification.
                var noteData = new
                {
                    note = $"Delicate Courier: shipment booked via {courierName}. Tracking number: {trackingNumber}",
                    customer_note = false
                };

                var jsonContent = JsonSerializer.Serialize(noteData);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Post, $"/wp-json/wc/v3/orders/{wooOrderId}/notes", httpContent, store.WooConsumerKey, store.WooConsumerSecret);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception adding tracking info to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);

                return false;
            }
        }

        /// <summary>
        /// Add a plain order note to a WooCommerce order. Internal by default
        /// (customer_note = false) so WooCommerce doesn't email the buyer.
        /// </summary>
        public async Task<bool> AddOrderNoteAsync(int storeId, int wooOrderId, string note, bool customerNote = false)
        {
            try
            {
                _logger.LogInformation("Adding order note to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    return false;
                }

                await _rateLimiter.WaitIfNeededAsync(storeId);

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                var noteData = new
                {
                    note,
                    customer_note = customerNote
                };

                var jsonContent = JsonSerializer.Serialize(noteData);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Post, $"/wp-json/wc/v3/orders/{wooOrderId}/notes", httpContent, store.WooConsumerKey, store.WooConsumerSecret);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception adding order note to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);

                return false;
            }
        }

        /// <summary>
        /// Upsert <c>_dcp_*</c> meta fields on a WooCommerce order so the
        /// plugin metabox can render the live "Shipment &amp; Tracking" panel
        /// (tracking number, courier, status, courier rate, Shiplogic
        /// consignment id, etc.). Idempotent. Thin wrapper around
        /// <see cref="PushShipmentMetaDetailedAsync"/> — kept for callers
        /// that don't care about which path succeeded.
        /// </summary>
        public async Task<bool> PushShipmentMetaAsync(int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta)
        {
            var result = await PushShipmentMetaDetailedAsync(storeId, wooOrderId, meta);
            return result.Success;
        }

        /// <summary>
        /// Same as <see cref="PushShipmentMetaAsync"/> but reports which path
        /// (plugin route vs WC REST) actually wrote, plus the count WP
        /// confirms it saved. The manual Resync endpoint uses this so the
        /// operator can tell at a glance whether the plugin route is engaged.
        /// </summary>
        public async Task<PushShipmentMetaResult> PushShipmentMetaDetailedAsync(
            int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta,
            string? note = null, bool noteIsCustomerNote = false)
        {
            if ((meta == null || meta.Count == 0) && string.IsNullOrWhiteSpace(note))
            {
                return new PushShipmentMetaResult
                {
                    Success = true,
                    Path = "none",
                    KeysSent = 0,
                    Summary = "No meta keys or note to push (skipped).",
                };
            }
            meta ??= new Dictionary<string, string?>();

            try
            {
                _logger.LogInformation(
                    "Pushing {Count} shipment meta key(s) to WooCommerce order {OrderId} in StoreID {StoreId}: {Keys}",
                    meta.Count, wooOrderId, storeId, string.Join(",", meta.Keys));

                var store = await GetStoreAsync(storeId);
                if (store == null)
                {
                    _logger.LogWarning("PushShipmentMetaAsync: Store {StoreId} not found.", storeId);
                    return new PushShipmentMetaResult
                    {
                        Success = false,
                        Path = "none",
                        KeysSent = meta.Count,
                        Summary = $"Store {storeId} not found.",
                    };
                }

                await _rateLimiter.WaitIfNeededAsync(storeId);

                // Preferred path: signed plugin endpoint. Bypasses the WC REST
                // permission model and (crucially) the WC REST behaviour of
                // silently dropping protected underscore-prefixed meta on
                // update. The plugin's handler runs as PHP inside WP and
                // writes via `update_meta_data()`.
                if (!string.IsNullOrWhiteSpace(store.WebhookSecret)
                    && !string.IsNullOrWhiteSpace(store.WooCommerceURL))
                {
                    var pluginAttempt = await TryPushMetaViaPluginAsync(store, wooOrderId, meta, note, noteIsCustomerNote);
                    if (pluginAttempt.HttpStatus > 0)
                    {
                        if (pluginAttempt.Success)
                        {
                            _logger.LogInformation(
                                "Pushed shipment meta to WC order {OrderId} (Store {StoreId}) via plugin endpoint (updated={Updated} preserved={Preserved} skipped={Skipped} note={Note}).",
                                wooOrderId, storeId, pluginAttempt.KeysUpdated,
                                pluginAttempt.PreservedKeys?.Count ?? 0,
                                pluginAttempt.SkippedKeys?.Count ?? 0,
                                pluginAttempt.NoteAdded);

                            // Build a richer summary that exposes the
                            // skipped/preserved-key signals — those are
                            // exactly the diagnostics that distinguish a
                            // green-but-empty-metabox from a real save.
                            var extras = new List<string>();
                            if ((pluginAttempt.SkippedKeys?.Count ?? 0) > 0)
                                extras.Add($"skipped {pluginAttempt.SkippedKeys!.Count} ({string.Join(",", pluginAttempt.SkippedKeys)})");
                            if ((pluginAttempt.PreservedKeys?.Count ?? 0) > 0)
                                extras.Add($"preserved {pluginAttempt.PreservedKeys!.Count} ({string.Join(",", pluginAttempt.PreservedKeys)})");
                            if (pluginAttempt.NoteAdded == true)
                                extras.Add("note added");
                            var extrasText = extras.Count > 0 ? " — " + string.Join("; ", extras) : string.Empty;

                            return new PushShipmentMetaResult
                            {
                                Success = true,
                                Path = "plugin",
                                KeysSent = meta.Count,
                                KeysUpdated = pluginAttempt.KeysUpdated,
                                HttpStatus = pluginAttempt.HttpStatus,
                                ResponseBody = pluginAttempt.Body,
                                AcceptedKeys = pluginAttempt.AcceptedKeys,
                                SkippedKeys = pluginAttempt.SkippedKeys,
                                PreservedKeys = pluginAttempt.PreservedKeys,
                                NoteAdded = pluginAttempt.NoteAdded,
                                Summary = $"Plugin route: sent {meta.Count}, WP saved {pluginAttempt.KeysUpdated ?? 0}{extrasText}.",
                            };
                        }
                        // The plugin route ANSWERED but rejected. Surface that
                        // to the caller verbatim — do NOT silently fall back
                        // to WC REST, because the plugin's reason (e.g. bad
                        // signature, wrong store id) is the actionable
                        // diagnostic the operator needs to see.
                        _logger.LogWarning(
                            "Plugin meta-receiver rejected push for WC order {OrderId} (Store {StoreId}). Status={Status}, Body={Body}",
                            wooOrderId, storeId, pluginAttempt.HttpStatus, pluginAttempt.Body);
                        return new PushShipmentMetaResult
                        {
                            Success = false,
                            Path = "plugin",
                            KeysSent = meta.Count,
                            HttpStatus = pluginAttempt.HttpStatus,
                            ResponseBody = pluginAttempt.Body,
                            Summary = $"Plugin route rejected (HTTP {pluginAttempt.HttpStatus}): {Trim(pluginAttempt.Body, 200)}",
                        };
                    }
                    // Transport error → plugin unreachable / wrong URL /
                    // route not registered (older plugin version). Fall
                    // through to WC REST below.
                    _logger.LogInformation(
                        "Plugin meta-receiver unreachable for Store {StoreId} ({Reason}); falling back to WC REST API.",
                        storeId, pluginAttempt.Body ?? "n/a");
                }

                var client = CreateAuthenticatedClient(store.WooCommerceURL, store.WooConsumerKey, store.WooConsumerSecret);

                var metaArray = meta
                    .Select(kvp => new { key = kvp.Key, value = (object?)kvp.Value })
                    .ToArray();

                var payload = new { meta_data = metaArray };
                var json = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await SendWithAuthFallbackAsync(client, HttpMethod.Put, $"/wp-json/wc/v3/orders/{wooOrderId}", httpContent, store.WooConsumerKey, store.WooConsumerSecret);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Pushed shipment meta to WC order {OrderId} (Store {StoreId}) via WC REST. Status: {Status}",
                        wooOrderId, storeId, response.StatusCode);
                    return new PushShipmentMetaResult
                    {
                        Success = true,
                        Path = "wc-rest",
                        KeysSent = meta.Count,
                        // WC REST returns 200 without telling us per-key
                        // whether each meta actually landed. It is known to
                        // silently drop protected (underscore-prefixed) meta
                        // when the API key's owner lacks `edit_shop_orders`,
                        // so a 200 here is NOT a guarantee the metabox will
                        // populate — install the plugin v2.4.0+ to get a
                        // real updated-count via the plugin route.
                        KeysUpdated = null,
                        HttpStatus = (int)response.StatusCode,
                        Summary = $"WC REST: sent {meta.Count} (WC does not confirm per-key save).",
                    };
                }

                _logger.LogWarning(
                    "WC REST rejected shipment meta push for WC order {OrderId} (Store {StoreId}). Status: {Status}, Body: {Body}",
                    wooOrderId, storeId, response.StatusCode, responseBody);
                return new PushShipmentMetaResult
                {
                    Success = false,
                    Path = "wc-rest",
                    KeysSent = meta.Count,
                    HttpStatus = (int)response.StatusCode,
                    ResponseBody = Trim(responseBody, 500),
                    Summary = $"WC REST rejected (HTTP {(int)response.StatusCode}): {Trim(responseBody, 200)}",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception pushing shipment meta to WC order {OrderId} (Store {StoreId})",
                    wooOrderId, storeId);
                return new PushShipmentMetaResult
                {
                    Success = false,
                    Path = "none",
                    KeysSent = meta.Count,
                    Summary = $"Exception: {ex.Message}",
                };
            }
        }

        private static string Trim(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Internal: POST shipment meta to the Delicate Courier plugin's
        /// signed REST route. Returns a richer struct than a bool so the
        /// caller can distinguish "route answered with N updated keys" from
        /// "route rejected" from "transport failure / route not present".
        /// </summary>
        private async Task<PluginPushAttempt> TryPushMetaViaPluginAsync(
            Domain.Entities.Store store,
            int wooOrderId,
            IReadOnlyDictionary<string, string?> meta,
            string? note,
            bool noteIsCustomerNote)
        {
            var siteUrl = (store.WooCommerceURL ?? string.Empty).TrimEnd('/');
            var url = siteUrl + "/wp-json/delicate-courier/v1/shipment-meta";

            var payload = new Dictionary<string, object?>
            {
                ["wc_order_id"] = wooOrderId,
                ["meta"] = meta.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            };
            if (!string.IsNullOrWhiteSpace(note))
            {
                payload["note"] = note;
                payload["note_is_customer_note"] = noteIsCustomerNote;
            }
            var json = JsonSerializer.Serialize(payload);
            var bodyBytes = Encoding.UTF8.GetBytes(json);

            using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(store.WebhookSecret!));
            var sig = Convert.ToBase64String(hmac.ComputeHash(bodyBytes));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("X-Plugin-Signature", sig);
                request.Headers.TryAddWithoutValidation("X-Store-ID", store.StoreID.ToString());
                request.Headers.UserAgent.ParseAdd("DelicateCouriersApi/MetaPush");

                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                var response = await http.SendAsync(request);

                // HttpClient does NOT re-POST bodies across redirects, so a
                // site that 301s (e.g. non-www → www) would silently kill the
                // push: the redirect response comes back as a "rejected"
                // plugin answer. Follow one same-scheme redirect manually,
                // re-signing headers on the new request. (Root cause of the
                // Store 7 / bakedbynataleen.co.za missing-tracking incident.)
                if ((int)response.StatusCode is 301 or 302 or 307 or 308
                    && response.Headers.Location != null)
                {
                    var location = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(new Uri(url), response.Headers.Location);
                    if (location.Scheme == Uri.UriSchemeHttps)
                    {
                        _logger.LogInformation(
                            "Plugin meta push for Store {StoreId} redirected ({Status}) to {Location}; re-posting.",
                            store.StoreID, (int)response.StatusCode, location);
                        response.Dispose();
                        using var redirectRequest = new HttpRequestMessage(HttpMethod.Post, location)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json"),
                        };
                        redirectRequest.Headers.TryAddWithoutValidation("X-Plugin-Signature", sig);
                        redirectRequest.Headers.TryAddWithoutValidation("X-Store-ID", store.StoreID.ToString());
                        redirectRequest.Headers.UserAgent.ParseAdd("DelicateCouriersApi/MetaPush");
                        response = await http.SendAsync(redirectRequest);
                    }
                }

                string body;
                int httpStatus;
                bool isSuccess;
                using (response)
                {
                    body = Trim(await response.Content.ReadAsStringAsync(), 500);
                    httpStatus = (int)response.StatusCode;
                    isSuccess = response.IsSuccessStatusCode;
                }

                int? updated = null;
                List<string>? accepted = null;
                List<string>? skipped = null;
                List<string>? preserved = null;
                bool? noteAdded = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.Number)
                        updated = u.GetInt32();
                    accepted = ReadStringArray(root, "accepted_keys");
                    skipped = ReadStringArray(root, "skipped");
                    preserved = ReadStringArray(root, "preserved_keys");
                    if (root.TryGetProperty("note_added", out var n) && (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False))
                        noteAdded = n.GetBoolean();
                }
                catch (JsonException) { /* non-JSON body is fine — pure diag */ }

                return new PluginPushAttempt
                {
                    HttpStatus = httpStatus,
                    Success = isSuccess,
                    KeysUpdated = updated,
                    AcceptedKeys = accepted,
                    SkippedKeys = skipped,
                    PreservedKeys = preserved,
                    NoteAdded = noteAdded,
                    Body = body,
                };
            }
            catch (HttpRequestException ex)
            {
                return new PluginPushAttempt { HttpStatus = 0, Success = false, Body = $"transport: {ex.Message}" };
            }
            catch (TaskCanceledException ex)
            {
                return new PluginPushAttempt { HttpStatus = 0, Success = false, Body = $"timeout: {ex.Message}" };
            }
        }

        private sealed class PluginPushAttempt
        {
            public int HttpStatus { get; init; }
            public bool Success { get; init; }
            public int? KeysUpdated { get; init; }
            public List<string>? AcceptedKeys { get; init; }
            public List<string>? SkippedKeys { get; init; }
            public List<string>? PreservedKeys { get; init; }
            public bool? NoteAdded { get; init; }
            public string? Body { get; init; }
        }

        private static List<string>? ReadStringArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }

        /// <summary>
        /// Fetch all orders in bulk with automatic pagination
        /// Efficiently handles 1000s of orders by fetching in batches
        /// Respects rate limits automatically
        /// </summary>
        /// <param name="storeId">Store ID from database</param>
        /// <param name="status">Optional status filter (processing, completed, etc.)</param>
        /// <param name="after">Optional date filter - orders after this date</param>
        /// <param name="before">Optional date filter - orders before this date</param>
        /// <param name="maxOrders">Maximum number of orders to fetch (safety limit)</param>
        /// <returns>List of all orders matching criteria</returns>
        public async Task<List<WooCommerceOrder>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000)
        {
            var allOrders = new List<WooCommerceOrder>();
            var currentPage = 1;
            var perPage = 100; // WooCommerce max is 100 per page
            var hasMorePages = true;

            try
            {
                _logger.LogInformation("Starting bulk order fetch for StoreID: {StoreId}, MaxOrders: {MaxOrders}", storeId, maxOrders);

                while (hasMorePages && allOrders.Count < maxOrders)
                {
                    var request = new FetchOrdersRequest
                    {
                        StoreID = storeId,
                        Status = status,
                        After = after,
                        Before = before,
                        Page = currentPage,
                        PerPage = perPage
                    };

                    var orders = await FetchOrdersAsync(request);

                    if (orders.Count == 0)
                    {
                        // No more orders to fetch
                        hasMorePages = false;
                    }
                    else
                    {
                        allOrders.AddRange(orders);

                        _logger.LogInformation("Fetched page {Page}: {Count} orders (Total so far: {Total})", currentPage, orders.Count, allOrders.Count);

                        // Check if we got less than perPage, which means last page
                        if (orders.Count < perPage)
                        {
                            hasMorePages = false;
                        }

                        currentPage++;

                        // Safety check to prevent infinite loops
                        if (currentPage > 1000)
                        {
                            _logger.LogWarning("Reached maximum page limit (1000) for StoreID: {StoreId}", storeId);
                            
                            hasMorePages = false;
                        }
                    }
                }

                _logger.LogInformation("Bulk order fetch completed for StoreID: {StoreId}. Total orders: {Total}", storeId, allOrders.Count);

                return allOrders;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during bulk order fetch for StoreID: {StoreId}", storeId);

                return allOrders; // Return what we've fetched so far
            }
        }

        /// <summary>
        /// Append WooCommerce query-string credentials to a request path.
        /// Some WordPress hosts (e.g. LiteSpeed/Apache without the
        /// HTTP_AUTHORIZATION rewrite) strip the Basic Authorization header
        /// before it reaches WooCommerce, producing 401s even with valid
        /// keys — observed on bakedbynataleen.co.za. WooCommerce officially
        /// supports consumer_key/consumer_secret as query parameters over
        /// HTTPS, so we send both auth forms on every call.
        /// </summary>
        /// <summary>
        /// Send a Woo REST request using the client's Basic auth header first;
        /// if the host rejects it with 401/403 (some WordPress hosts strip the
        /// Authorization header — observed on merchant sites), retry once with
        /// WooCommerce's supported consumer_key/consumer_secret query-string
        /// auth. The fallback is HTTPS-only so credentials never travel in a
        /// plaintext URL.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithAuthFallbackAsync(
            HttpClient client, HttpMethod method, string path, HttpContent? content,
            string consumerKey, string consumerSecret)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            var response = await client.SendAsync(request);

            if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                && string.Equals(client.BaseAddress?.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Woo REST call {Method} {Path} rejected with {Status} using Basic auth; retrying with query-string credentials (host likely strips the Authorization header).",
                    method, path, (int)response.StatusCode);
                response.Dispose();
                using var retry = new HttpRequestMessage(method, WithAuthQuery(path, consumerKey, consumerSecret)) { Content = content };
                response = await client.SendAsync(retry);
            }

            return response;
        }

        private static string WithAuthQuery(string path, string consumerKey, string consumerSecret)
        {
            var sep = path.Contains('?') ? '&' : '?';
            return $"{path}{sep}consumer_key={Uri.EscapeDataString(consumerKey)}&consumer_secret={Uri.EscapeDataString(consumerSecret)}";
        }

        /// <summary>
        /// Create HTTP client with Basic Auth for WooCommerce.
        /// Uses HttpClientFactory for connection pooling (handles concurrent requests efficiently).
        /// </summary>
        private HttpClient CreateAuthenticatedClient(string baseUrl, string consumerKey, string consumerSecret)
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);

            // WooCommerce uses HTTP Basic Authentication
            var authString = $"{consumerKey}:{consumerSecret}";
            var encodedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(authString));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encodedAuth);

            return client;
        }

        /// <summary>
        /// Get store from database with caching consideration for future optimization
        /// </summary>
        private async Task<Domain.Entities.Store?> GetStoreAsync(int storeId)
        {
            // Read-only, better performance for concurrent reads
            return await _context.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive); 
        }
    }
}