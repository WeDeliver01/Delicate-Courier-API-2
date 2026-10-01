using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Stores.DTOs;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Stores
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class StoresController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StoresController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public StoresController(AppDbContext context, ILogger<StoresController> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        // GET: api/stores
        [HttpGet]
        public async Task<ActionResult<IEnumerable<StoreResponse>>> GetStores()
        {
            var stores = await _context.Stores.Include(s => s.Tenant).OrderByDescending(s => s.CreatedOn).Select(s => MapToStoreResponse(s)).ToListAsync();

            return Ok(stores);
        }

        // GET: api/stores/{id}
        [HttpGet("{id}")]
        public async Task<ActionResult<StoreResponse>> GetStore(int id)
        {
            var store = await _context.Stores.Include(s => s.Tenant).FirstOrDefaultAsync(s => s.StoreID == id);

            if (store == null)
            {
                return NotFound($"Store with ID {id} not found");
            }

            return Ok(MapToStoreResponse(store));
        }

        // POST: api/stores
        [HttpPost]
        public async Task<ActionResult<StoreResponse>> CreateStore([FromBody] CreateStoreRequest request)
        {
            var userEmail = GetUserEmailFromToken();

            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized("Invalid token");
            }

            // TenantID must be provided in the request
            if (request.TenantID <= 0)
            {
                return BadRequest("TenantID is required");
            }

            var tenantId = request.TenantID;

            // Verify the tenant exists
            var tenantExists = await _context.Tenants.AnyAsync(t => t.TenantID == tenantId);
            if (!tenantExists)
            {
                return BadRequest($"Tenant with ID {tenantId} does not exist");
            }

            // Platform-specific credential validation
            var platform = string.IsNullOrWhiteSpace(request.Platform)
                ? "woocommerce"
                : request.Platform.Trim().ToLowerInvariant();

            if (platform != "woocommerce" && platform != "shopify")
            {
                return BadRequest("Invalid platform. Must be 'woocommerce' or 'shopify'");
            }

            if (platform == "woocommerce")
            {
                if (string.IsNullOrWhiteSpace(request.WooCommerceURL)
                    || string.IsNullOrWhiteSpace(request.WooConsumerKey)
                    || string.IsNullOrWhiteSpace(request.WooConsumerSecret))
                {
                    return BadRequest("WooCommerce URL, Consumer Key, and Consumer Secret are required for WooCommerce stores");
                }

                if (!Uri.TryCreate(request.WooCommerceURL, UriKind.Absolute, out _))
                {
                    return BadRequest("Invalid WooCommerce URL format");
                }

                var existingStore = await _context.Stores.FirstOrDefaultAsync(s => s.WooCommerceURL == request.WooCommerceURL && s.TenantID == tenantId);

                if (existingStore != null)
                {
                    return Conflict("A store with this URL already exists for this tenant");
                }
            }
            else // shopify
            {
                if (string.IsNullOrWhiteSpace(request.ShopifyStoreUrl)
                    || string.IsNullOrWhiteSpace(request.ShopifyAccessToken))
                {
                    return BadRequest("Shopify Store URL and Access Token are required for Shopify stores");
                }
            }

            var store = new Store
            {
                TenantID = tenantId,  // Now uses the request's TenantID
                StoreName = request.StoreName,
                Platform = platform,
                WooCommerceURL = platform == "woocommerce" ? request.WooCommerceURL!.TrimEnd('/') : null,
                WooConsumerKey = platform == "woocommerce" ? request.WooConsumerKey : null,
                WooConsumerSecret = platform == "woocommerce" ? request.WooConsumerSecret : null,
                WebhookSecret = platform == "woocommerce" ? (request.WebhookSecret ?? string.Empty) : string.Empty,
                ShopifyStoreUrl = platform == "shopify" ? request.ShopifyStoreUrl : null,
                ShopifyAccessToken = platform == "shopify" ? request.ShopifyAccessToken : null,
                ShopifyWebhookSecret = platform == "shopify" ? request.ShopifyWebhookSecret : null,
                IsActive = true,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = userEmail,

                // Collection Address
                CollectionAddressLine1 = request.CollectionAddressLine1,
                CollectionAddressLine2 = request.CollectionAddressLine2,
                CollectionCity = request.CollectionCity,
                CollectionProvince = request.CollectionProvince,
                CollectionPostalCode = request.CollectionPostalCode,
                CollectionCountry = request.CollectionCountry,

                // Collection Contact
                CollectionContactName = request.CollectionContactName,
                CollectionContactPhone = request.CollectionContactPhone,
                CollectionContactEmail = request.CollectionContactEmail,
                CollectionCompanyName = request.CollectionCompanyName,

                // Shiplogic Configuration
                ShiplogicProviderId = request.ShiplogicProviderId,
                ShiplogicAccountId = request.ShiplogicAccountId,
                DefaultServiceLevel = request.DefaultServiceLevel,

                // Special Trip fallback
                SpecialTripCostPerKm = request.SpecialTripCostPerKm,
                SpecialTripMinFee = request.SpecialTripMinFee,
                SpecialTripMaxKm = request.SpecialTripMaxKm,
                GoogleMapsApiKey = string.IsNullOrWhiteSpace(request.GoogleMapsApiKey) ? null : request.GoogleMapsApiKey.Trim(),
                CheckoutRateLabel = string.IsNullOrWhiteSpace(request.CheckoutRateLabel) ? null : request.CheckoutRateLabel.Trim()
            };

            _context.Stores.Add(store);
            await _context.SaveChangesAsync();

            // Seed the global default packaging catalogue into the new store.
            // Idempotent and additive — see DefaultPackageTypes for details.
            var seeded = await DelicateCouriers.ApiService.Features.Packaging.DefaultPackageTypes
                .EnsureForStoreAsync(_context, store.StoreID, createdBy: userEmail ?? "system");
            if (seeded > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Seeded {Count} default package types for new StoreID {StoreID}", seeded, store.StoreID);
            }

            // Reload with tenant for response
            await _context.Entry(store).Reference(s => s.Tenant).LoadAsync();

            _logger.LogInformation("Store '{StoreName}' created for TenantID: {TenantID} by {User}", store.StoreName, store.TenantID, userEmail);

            return CreatedAtAction(nameof(GetStore), new { id = store.StoreID }, MapToStoreResponse(store));
        }


        // PUT: api/stores/{id}
        [HttpPut("{id}")]
        public async Task<ActionResult<StoreResponse>> UpdateStore(int id, [FromBody] UpdateStoreRequest request)
        {
            var userEmail = GetUserEmailFromToken();

            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized("Invalid token");
            }

            var store = await _context.Stores.Include(s => s.Tenant).FirstOrDefaultAsync(s => s.StoreID == id);

            if (store == null)
            {
                return NotFound($"Store with ID {id} not found");
            }

            // Update basic fields
            if (!string.IsNullOrEmpty(request.StoreName))
                store.StoreName = request.StoreName;

            // Apply Platform change (validated below against the effective post-update credentials)
            if (!string.IsNullOrWhiteSpace(request.Platform))
            {
                var requestedPlatform = request.Platform.Trim().ToLowerInvariant();
                if (requestedPlatform != "woocommerce" && requestedPlatform != "shopify")
                {
                    return BadRequest("Invalid platform. Must be 'woocommerce' or 'shopify'");
                }
                store.Platform = requestedPlatform;
            }

            //validate woocommerce url
            if (!string.IsNullOrEmpty(request.WooCommerceURL))
            {
                if (!Uri.TryCreate(request.WooCommerceURL, UriKind.Absolute, out _))
                    return BadRequest("Invalid WooCommerce URL format");

                store.WooCommerceURL = request.WooCommerceURL.TrimEnd('/');
            }

            if (!string.IsNullOrEmpty(request.WooConsumerKey))
                store.WooConsumerKey = request.WooConsumerKey;

            if (!string.IsNullOrEmpty(request.WooConsumerSecret))
                store.WooConsumerSecret = request.WooConsumerSecret;

            if (!string.IsNullOrEmpty(request.WebhookSecret))
                store.WebhookSecret = request.WebhookSecret;

            // Shopify credential updates
            if (request.ShopifyStoreUrl != null)
                store.ShopifyStoreUrl = request.ShopifyStoreUrl;

            if (request.ShopifyAccessToken != null)
                store.ShopifyAccessToken = request.ShopifyAccessToken;

            if (request.ShopifyWebhookSecret != null)
                store.ShopifyWebhookSecret = request.ShopifyWebhookSecret;

            if (request.IsActive.HasValue)
                store.IsActive = request.IsActive.Value;

            // Update Collection Address (allow setting to null/empty)
            if (request.CollectionAddressLine1 != null)
                store.CollectionAddressLine1 = request.CollectionAddressLine1;
            if (request.CollectionAddressLine2 != null)
                store.CollectionAddressLine2 = request.CollectionAddressLine2;
            if (request.CollectionCity != null)
                store.CollectionCity = request.CollectionCity;
            if (request.CollectionProvince != null)
                store.CollectionProvince = request.CollectionProvince;
            if (request.CollectionPostalCode != null)
                store.CollectionPostalCode = request.CollectionPostalCode;
            if (request.CollectionCountry != null)
                store.CollectionCountry = request.CollectionCountry;

            // Update Collection Contact
            if (request.CollectionContactName != null)
                store.CollectionContactName = request.CollectionContactName;
            if (request.CollectionContactPhone != null)
                store.CollectionContactPhone = request.CollectionContactPhone;
            if (request.CollectionContactEmail != null)
                store.CollectionContactEmail = request.CollectionContactEmail;
            if (request.CollectionCompanyName != null)
                store.CollectionCompanyName = request.CollectionCompanyName;

            // Update Shiplogic Configuration
            if (request.ShiplogicProviderId.HasValue)
                store.ShiplogicProviderId = request.ShiplogicProviderId.Value;
            if (request.ShiplogicAccountId.HasValue)
                store.ShiplogicAccountId = request.ShiplogicAccountId.Value;
            if (request.DefaultServiceLevel != null)
                store.DefaultServiceLevel = request.DefaultServiceLevel;

            // Update Special Trip fallback config. Null = unchanged;
            // send SpecialTripCostPerKm = 0 to disable the fallback.
            if (request.SpecialTripCostPerKm.HasValue)
                store.SpecialTripCostPerKm = request.SpecialTripCostPerKm.Value <= 0 ? null : request.SpecialTripCostPerKm;
            if (request.SpecialTripMinFee.HasValue)
                store.SpecialTripMinFee = request.SpecialTripMinFee;
            if (request.SpecialTripMaxKm.HasValue)
                store.SpecialTripMaxKm = request.SpecialTripMaxKm;
            // Merchant Google Maps key: null = leave unchanged, "" = clear.
            if (request.GoogleMapsApiKey != null)
                store.GoogleMapsApiKey = string.IsNullOrWhiteSpace(request.GoogleMapsApiKey) ? null : request.GoogleMapsApiKey.Trim();
            // Checkout rate label: null = leave unchanged, "" = clear (back to defaults).
            if (request.CheckoutRateLabel != null)
                store.CheckoutRateLabel = string.IsNullOrWhiteSpace(request.CheckoutRateLabel) ? null : request.CheckoutRateLabel.Trim();

            // Validate the effective post-update credentials match the (possibly new) platform
            var effectivePlatform = string.IsNullOrWhiteSpace(store.Platform)
                ? "woocommerce"
                : store.Platform.Trim().ToLowerInvariant();

            if (effectivePlatform != "woocommerce" && effectivePlatform != "shopify")
            {
                return BadRequest("Invalid platform. Must be 'woocommerce' or 'shopify'");
            }

            if (effectivePlatform == "woocommerce")
            {
                if (string.IsNullOrWhiteSpace(store.WooCommerceURL)
                    || string.IsNullOrWhiteSpace(store.WooConsumerKey)
                    || string.IsNullOrWhiteSpace(store.WooConsumerSecret))
                {
                    return BadRequest("WooCommerce URL, Consumer Key, and Consumer Secret are required for WooCommerce stores");
                }
            }
            else // shopify
            {
                if (string.IsNullOrWhiteSpace(store.ShopifyStoreUrl)
                    || string.IsNullOrWhiteSpace(store.ShopifyAccessToken))
                {
                    return BadRequest("Shopify Store URL and Access Token are required for Shopify stores");
                }
            }

            store.ChangedOn = DateTime.UtcNow;
            store.ChangedBy = userEmail;

            await _context.SaveChangesAsync();

            return Ok(MapToStoreResponse(store));
        }

        // DELETE: api/stores/{id}
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteStore(int id)
        {
            var userEmail = GetUserEmailFromToken();

            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized("Invalid token");
            }

            var store = await _context.Stores.FirstOrDefaultAsync(s => s.StoreID == id);

            if (store == null)
            {
                return NotFound($"Store with ID {id} not found");
            }

            store.IsActive = false;
            store.ChangedOn = DateTime.UtcNow;
            store.ChangedBy = userEmail;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // Helper: Map Store entity to StoreResponse DTO
        private static StoreResponse MapToStoreResponse(Store s)
        {
            return new StoreResponse
            {
                StoreID = s.StoreID,
                TenantID = s.TenantID,
                TenantName = s.Tenant?.TenantName ?? "",
                StoreName = s.StoreName,
                WooCommerceURL = s.WooCommerceURL,
                IsActive = s.IsActive,
                CreatedOn = s.CreatedOn,
                CreatedBy = s.CreatedBy,
                ChangedOn = s.ChangedOn,
                ChangedBy = s.ChangedBy,

                // Credential flags
                HasWooConsumerKey = !string.IsNullOrEmpty(s.WooConsumerKey),
                HasWooConsumerSecret = !string.IsNullOrEmpty(s.WooConsumerSecret),
                HasWebhookSecret = !string.IsNullOrEmpty(s.WebhookSecret),

                // Actual credentials
                WooConsumerKey = s.WooConsumerKey,
                WooConsumerSecret = s.WooConsumerSecret,
                WebhookSecret = s.WebhookSecret,

                // Collection Address
                CollectionAddressLine1 = s.CollectionAddressLine1,
                CollectionAddressLine2 = s.CollectionAddressLine2,
                CollectionCity = s.CollectionCity,
                CollectionProvince = s.CollectionProvince,
                CollectionPostalCode = s.CollectionPostalCode,
                CollectionCountry = s.CollectionCountry,

                // Collection Contact
                CollectionContactName = s.CollectionContactName,
                CollectionContactPhone = s.CollectionContactPhone,
                CollectionContactEmail = s.CollectionContactEmail,
                CollectionCompanyName = s.CollectionCompanyName,

                // Shiplogic Configuration
                ShiplogicProviderId = s.ShiplogicProviderId,
                ShiplogicAccountId = s.ShiplogicAccountId,
                DefaultServiceLevel = s.DefaultServiceLevel,

                // Special Trip fallback
                SpecialTripCostPerKm = s.SpecialTripCostPerKm,
                SpecialTripMinFee = s.SpecialTripMinFee,
                SpecialTripMaxKm = s.SpecialTripMaxKm,
                HasGoogleMapsApiKey = !string.IsNullOrWhiteSpace(s.GoogleMapsApiKey),
                CheckoutRateLabel = s.CheckoutRateLabel
            };
        }

        private int? GetTenantIdFromToken()
        {
            var tenantIdClaim = User.FindFirst("TenantId")?.Value;

            if (int.TryParse(tenantIdClaim, out int tenantId))

                return tenantId;

            return null;
        }

        private string? GetUserEmailFromToken()
        {
            return User.FindFirst(ClaimTypes.Email)?.Value;
        }

        // Repurposed (2026-05-20): previously sent a fake WooCommerce-native
        // webhook to the legacy /api/webhooks/woocommerce/order endpoint at the
        // (DNS-less) webhooks2.* host. The current production path is the
        // Delicate Courier WooCommerce plugin posting to /api/webhooks/plugin/order
        // with X-Store-ID + X-Plugin-Signature (HMAC-SHA256 base64 over raw body).
        // This endpoint now fires a synthetic payload through that real plugin
        // path via loopback, so a green result means the store's WebhookSecret
        // matches what the plugin would sign with end-to-end.
        [HttpPost("{storeId}/test-webhook")]
        public async Task<IActionResult> TestWebhookConnection(int storeId)
        {
            try
            {
                var store = await _context.Stores.Include(s => s.Tenant).FirstOrDefaultAsync(s => s.StoreID == storeId);

                if (store == null)
                    return NotFound(new { success = false, message = "Store not found" });

                if (string.IsNullOrWhiteSpace(store.WebhookSecret))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Store has no Webhook Secret configured — set one on the store before testing."
                    });
                }

                // Unique WooOrderId per click so the platform's dedupe (by WooOrderId)
                // doesn't silently return "order already exists" on the second test.
                // Kept inside int32 to match the column type.
                var suffix = Random.Shared.Next(100, 1000);
                var unique = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % int.MaxValue) ^ suffix);

                var testPayload = new PluginWebhookPayload
                {
                    Event = "order.created",
                    WooOrderId = unique,
                    OrderNumber = $"TEST-{suffix}",
                    Status = "processing",
                    Total = 100.00m,
                    ShippingTotal = 50.00m,
                    Currency = "ZAR",
                    PaymentMethod = "test",
                    DateCreated = DateTime.UtcNow.ToString("o"),
                    StoreId = store.StoreID.ToString(),
                    StoreUrl = store.WooCommerceURL ?? string.Empty,
                    Customer = new PluginCustomer
                    {
                        Name = "Test Customer",
                        Email = "test@example.com",
                        Phone = "0821234567",
                    },
                    ShippingAddress = new PluginShippingAddress
                    {
                        Street = "123 Test Street",
                        Suburb = "Centurion",
                        City = "Pretoria",
                        State = "Gauteng",
                        Postcode = "0001",
                        Country = "ZA",
                    },
                    LineItems = new List<PluginLineItem>
                    {
                        new() { Id = 1, ProductId = 1, Name = "Test Product", Quantity = 1, Price = 100.00m, Sku = "TEST-SKU" }
                    },
                    TotalWeight = 0.5m,
                };

                var jsonPayload = JsonSerializer.Serialize(testPayload);
                var signature = GenerateWebhookSignature(jsonPayload, store.WebhookSecret);

                // Hit the local plugin webhook via loopback so we exercise the
                // real controller (signature validation, rate limit, processing)
                // without depending on public DNS for the api2.* hostname.
                var webhookUrl = "http://127.0.0.1:8080/api/webhooks/plugin/order";

                var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                content.Headers.Add("X-Store-ID", store.StoreID.ToString());
                content.Headers.Add("X-Plugin-Signature", signature);

                var response = await httpClient.PostAsync(webhookUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Plugin test webhook accepted for Store {StoreId} (synthetic WooOrderId={WooOrderId})",
                        storeId, unique);

                    var publicUrl = (_configuration["PublicUrls:ApiBaseUrl"] ?? string.Empty).TrimEnd('/');
                    return Ok(new
                    {
                        success = true,
                        message = $"Plugin webhook test passed. Synthetic order {testPayload.OrderNumber} (WooOrderId {unique}) accepted by the platform.",
                        details = new
                        {
                            publicWebhookUrl = string.IsNullOrEmpty(publicUrl)
                                ? "/api/webhooks/plugin/order"
                                : $"{publicUrl}/api/webhooks/plugin/order",
                            wooOrderId = unique,
                            orderNumber = testPayload.OrderNumber,
                            responseStatus = (int)response.StatusCode,
                            responseBody,
                        }
                    });
                }

                _logger.LogWarning(
                    "Plugin test webhook rejected for Store {StoreId}: {Status} {Body}",
                    storeId, (int)response.StatusCode, responseBody);

                var hint = (int)response.StatusCode switch
                {
                    401 => "Signature mismatch — the platform-side WebhookSecret on this store does not match what would be used to sign the request. Re-save the store's Webhook Secret and retry.",
                    404 => "Store not found by the plugin webhook endpoint — verify the store is Active.",
                    400 => "Request was malformed. Check the response body below for details.",
                    _ => "See response body below.",
                };

                return BadRequest(new
                {
                    success = false,
                    message = $"Plugin webhook test failed (HTTP {(int)response.StatusCode}). {hint}",
                    details = new
                    {
                        statusCode = (int)response.StatusCode,
                        error = responseBody,
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing plugin webhook for store {StoreId}", storeId);

                return StatusCode(500, new
                {
                    success = false,
                    message = "Error testing plugin webhook",
                    error = ex.Message
                });
            }
        }

        private string GenerateWebhookSignature(string payload, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));

            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

            return Convert.ToBase64String(hash);
        }
    }
}