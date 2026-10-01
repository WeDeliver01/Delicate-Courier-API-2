using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Service for processing WooCommerce webhooks
    /// Validates, parses, and stores orders from webhook events
    /// </summary>
    public class WebhookService
    {
        private readonly AppDbContext _context;
        private readonly WebhookSignatureValidator _signatureValidator;
        private readonly ILogger<WebhookService> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient; //HangFire Background jobs
        private readonly ISystemEventLogger _systemEvents;

        public WebhookService(AppDbContext context, WebhookSignatureValidator signatureValidator, ILogger<WebhookService> logger, IBackgroundJobClient backgroundJobClient, ISystemEventLogger systemEvents)
        {
            _context = context;
            _signatureValidator = signatureValidator;
            _logger = logger;
            _backgroundJobClient = backgroundJobClient;
            _systemEvents = systemEvents;
        }

        /// <summary>
        /// Process incoming order webhook from WooCommerce
        /// Validates signature, parses order, stores in database
        /// </summary>
        public async Task<WebhookResponse> ProcessOrderWebhookAsync(string payload, string signature, string webhookSource, string topic)
        {
            ParsedWebhookOrder? parsedOrder = null;

            try
            {
                _logger.LogInformation("Processing WooCommerce webhook. Topic: {Topic}, Source: {Source}", topic, webhookSource);

                // Step 1: Find the store by webhook source URL
                var store = await GetStoreByUrlAsync(webhookSource);
                if (store == null)
                {
                    _logger.LogWarning("Webhook rejected: Store not found for URL {Source}", webhookSource);

                    return new WebhookResponse
                    {
                        Success = false,
                        Message = "Store not found"
                    };
                }

                // Step 2: Validate webhook signature. Always required — without this
                // anyone who can reach the webhook URL can forge orders for the store.
                if (string.IsNullOrEmpty(store.WebhookSecret))
                {
                    _logger.LogError("Webhook rejected: Store {StoreId} has no WebhookSecret configured", store.StoreID);
                    return new WebhookResponse
                    {
                        Success = false,
                        Message = "Store webhook secret not configured"
                    };
                }

                if (!_signatureValidator.ValidateSignature(payload, signature, store.WebhookSecret))
                {
                    _logger.LogWarning("Webhook rejected: Invalid signature for StoreID {StoreId}", store.StoreID);
                    return new WebhookResponse
                    {
                        Success = false,
                        Message = "Invalid signature"
                    };
                }

                // Step 3: Parse the WooCommerce order from JSON
                var wooOrder = JsonSerializer.Deserialize<WooCommerceOrder>(payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (wooOrder == null)
                {
                    _logger.LogError("Failed to parse webhook payload for StoreID {StoreId}", store.StoreID);

                    return new WebhookResponse
                    {
                        Success = false,
                        Message = "Invalid order data"
                    };
                }

                // Step 4: Check if order already exists
                var existingOrder = await _context.Orders.Include(o => o.LineItems)
                                          .Include(o => o.Shipment)
                                          .FirstOrDefaultAsync(o => o.StoreID == store.StoreID && o.WooOrderID == wooOrder.Id.ToString());

                if (existingOrder != null)
                {
                    // Check if we should UPDATE the existing order (status changed or missing data)
                    var newStatus = MapWooCommerceStatus(wooOrder.Status);
                    var hasIncompleteData = string.IsNullOrEmpty(existingOrder.CustomerName) || existingOrder.LineItems.Count == 0;
                    var statusChanged = existingOrder.OrderStatus != newStatus;

                    if (hasIncompleteData || statusChanged)
                    {
                        _logger.LogInformation("Order {OrderId} exists but needs update. HasIncompleteData: {Incomplete}, StatusChanged: {StatusChanged} ({OldStatus} -> {NewStatus})",
                            wooOrder.Id, hasIncompleteData, statusChanged, existingOrder.OrderStatus, newStatus);

                        // Update the existing order with new data
                        parsedOrder = ParseWooCommerceOrder(wooOrder);

                        await UpdateExistingOrderAsync(existingOrder, parsedOrder, wooOrder);

                        _logger.LogInformation("Updated Order {OrderId} with fresh data. LineItems: {Count}", existingOrder.OrderID, existingOrder.LineItems.Count);
                    }
                    else
                    {
                        _logger.LogInformation("Order {OrderId} already exists and is complete. No update needed.", wooOrder.Id);
                    }

                    // ALWAYS check enqueue regardless of whether we just updated. This
                    // covers the case where a prior webhook delivery saved the order
                    // row but failed to enqueue the shipment job (e.g. Hangfire storage
                    // blip). On WooCommerce's retry the order would otherwise hit the
                    // "already complete, skip" path and the shipment would never be
                    // booked. The Hangfire job itself is idempotent — if a shipment
                    // already exists it short-circuits — so re-enqueueing is safe.
                    if (ShouldAutoCreateShipment(store, wooOrder) && existingOrder.Shipment == null)
                    {
                        _logger.LogInformation("Order {OrderId} (status: {Status}) has no shipment yet — queueing shipment creation.", existingOrder.OrderID, wooOrder.Status);

                        _backgroundJobClient.Enqueue<ShipmentOrchestrationService>(
                            service => service.CreateShipmentForOrderAsync(existingOrder.OrderID));
                    }

                    return new WebhookResponse
                    {
                        Success = true,
                        Message = hasIncompleteData || statusChanged ? "Order updated successfully" : "Order already processed",
                        OrderId = existingOrder.OrderID,
                        OrderNumber = existingOrder.WooOrderNumber
                    };
                }

                // Step 5: Parse and create new order
                parsedOrder = ParseWooCommerceOrder(wooOrder);
                var order = await CreateOrderAsync(store, parsedOrder, wooOrder);

                _logger.LogInformation("Successfully processed webhook. Created OrderID: {OrderId} from WooCommerce Order: {WooOrderId}", order.OrderID, wooOrder.Id);

                await _systemEvents.LogAsync(new SystemEventEntry
                {
                    EventType = "order.received",
                    ActorKind = "Webhook",
                    ActorLabel = $"WooCommerce {store.StoreName}",
                    TenantId = store.TenantID,
                    EntityType = "Order",
                    EntityRef = $"#{order.WooOrderNumber}",
                    Message = $"Order #{order.WooOrderNumber} received from WooCommerce ({wooOrder.Status})",
                    Details = new
                    {
                        orderId = order.OrderID,
                        wooOrderId = wooOrder.Id,
                        wooOrderNumber = order.WooOrderNumber,
                        storeId = store.StoreID,
                        status = wooOrder.Status,
                        total = order.OrderTotal,
                        customer = order.CustomerName,
                    },
                });

                // ==================== AUTO-CREATE SHIPMENT ====================
                // Check if order is paid and should auto-create shipment
                if (ShouldAutoCreateShipment(store, wooOrder))
                {
                    _logger.LogInformation("Order {OrderId} is paid (status: {Status}) - queueing shipment creation", order.OrderID, wooOrder.Status);

                    // Queue background job to create shipment (non-blocking)
                    _backgroundJobClient.Enqueue<ShipmentOrchestrationService>(
                        service => service.CreateShipmentForOrderAsync(order.OrderID));

                    _logger.LogInformation("Shipment creation job queued for Order {OrderId}", order.OrderID);
                }
                // ==================== END AUTO-CREATE SHIPMENT ====================

                return new WebhookResponse
                {
                    Success = true,
                    Message = "Order created successfully",
                    OrderId = order.OrderID,
                    OrderNumber = order.WooOrderNumber
                };
            }
            catch
            {
                // Re-throw so the controller returns 5xx — WooCommerce's
                // built-in webhook delivery retries 5xx but NOT 4xx, and we
                // never want to silently drop an inbound order because of a
                // transient backend hiccup. The controller logs the
                // exception, so we deliberately don't log it again here.
                throw;
            }
        }

        /// <summary>
        /// Decide whether the incoming WooCommerce order should be booked.
        ///
        /// We book by default — any order WooCommerce sends us is something
        /// the merchant wants shipped. The only exception is terminal states
        /// (cancelled / refunded / failed / trash) where a shipment would
        /// obviously be wrong. This matches the plugin-webhook behaviour and
        /// guarantees that no inbound order is ever silently dropped.
        /// </summary>
        private bool ShouldAutoCreateShipment(Store store, WooCommerceOrder wooOrder)
        {
            var terminal = new[] { "cancelled", "canceled", "refunded", "failed", "trash" };
            var normalised = wooOrder.Status?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalised) && terminal.Contains(normalised))
            {
                _logger.LogInformation("Order {OrderId} status is terminal ('{Status}') - skipping shipment creation", wooOrder.Id, wooOrder.Status);
                return false;
            }

            // Per-store override (see Store.BookingTriggerStatuses): when set,
            // only the listed statuses may auto-book for that store. Stores
            // without an override keep the book-by-default behaviour.
            if (!BookingStatusGate.IsAllowed(store, normalised, out var allowed))
            {
                _logger.LogInformation(
                    "Skipping shipment creation for Store {StoreId}: status '{Status}' is not in the store's booking-trigger list [{Allowed}]. WooOrderID: {WooOrderId}",
                    store.StoreID, wooOrder.Status ?? "(blank)", allowed, wooOrder.Id);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parse WooCommerce order into our simplified structure
        /// </summary>
        private ParsedWebhookOrder ParseWooCommerceOrder(WooCommerceOrder wooOrder)
        {
            // Combine shipping name
            var customerName = $"{wooOrder.Shipping.FirstName} {wooOrder.Shipping.LastName}".Trim();

            if (string.IsNullOrEmpty(customerName))
            {
                customerName = $"{wooOrder.Billing.FirstName} {wooOrder.Billing.LastName}".Trim();
            }

            // Build full shipping address
            var addressParts = new List<string>();

            if (!string.IsNullOrEmpty(wooOrder.Shipping.Address1))
                addressParts.Add(wooOrder.Shipping.Address1);

            if (!string.IsNullOrEmpty(wooOrder.Shipping.Address2))
                addressParts.Add(wooOrder.Shipping.Address2);

            var shippingAddress = string.Join(", ", addressParts);
            if (string.IsNullOrEmpty(shippingAddress))
            {
                // Fallback to billing address
                if (!string.IsNullOrEmpty(wooOrder.Billing.Address1))
                    addressParts.Add(wooOrder.Billing.Address1);

                if (!string.IsNullOrEmpty(wooOrder.Billing.Address2))
                    addressParts.Add(wooOrder.Billing.Address2);
                shippingAddress = string.Join(", ", addressParts);
            }

            // Create items summary
            var itemsSummary = string.Join("; ", wooOrder.LineItems.Select(i => $"{i.Name} (x{i.Quantity})"));

            return new ParsedWebhookOrder
            {
                WooCommerceOrderId = wooOrder.Id,
                OrderNumber = wooOrder.Number,
                Status = wooOrder.Status,
                TotalAmount = decimal.TryParse(wooOrder.Total, out var total) ? total : 0,
                Currency = wooOrder.Currency,
                // Woo's `date_created` is site-local wall-clock time whose
                // zone varies per merchant; `date_created_gmt` is always UTC
                // and always present in Woo REST v3, so prefer it. The
                // fallback keeps the historical treat-as-UTC behaviour.
                OrderDate = wooOrder.DateCreatedGmt.HasValue
                    ? DateTime.SpecifyKind(wooOrder.DateCreatedGmt.Value, DateTimeKind.Utc)
                    : DateTime.SpecifyKind(wooOrder.DateCreated, DateTimeKind.Utc),
                CustomerName = customerName,
                CustomerEmail = wooOrder.Billing.Email,
                CustomerPhone = wooOrder.Billing.Phone,
                ShippingAddress = shippingAddress,
                ShippingCity = wooOrder.Shipping.City ?? wooOrder.Billing.City,
                ShippingProvince = wooOrder.Shipping.State ?? wooOrder.Billing.State,
                ShippingPostalCode = wooOrder.Shipping.Postcode ?? wooOrder.Billing.Postcode,
                ShippingCountry = wooOrder.Shipping.Country ?? wooOrder.Billing.Country,
                TotalItems = wooOrder.LineItems.Sum(i => i.Quantity),
                ItemsSummary = itemsSummary
            };
        }

        /// <summary>
        /// Create Order entity in database with line items
        /// </summary>
        private async Task<Order> CreateOrderAsync(Store store, ParsedWebhookOrder parsedOrder, WooCommerceOrder wooOrder)
        {
            var order = new Order
            {
                TenantID = store.TenantID,
                StoreID = store.StoreID,
                WooOrderID = parsedOrder.WooCommerceOrderId.ToString(),
                WooOrderNumber = parsedOrder.OrderNumber,
                CustomerName = parsedOrder.CustomerName,
                CustomerEmail = parsedOrder.CustomerEmail,
                CustomerPhone = parsedOrder.CustomerPhone,
                // Split the combined address into Line1 and Line2
                ShippingAddressLine1 = parsedOrder.ShippingAddress.Length > 100 ? parsedOrder.ShippingAddress.Substring(0, 100) : parsedOrder.ShippingAddress,
                ShippingAddressLine2 = parsedOrder.ShippingAddress.Length > 100 ? parsedOrder.ShippingAddress.Substring(100) : null,
                ShippingCity = parsedOrder.ShippingCity,
                ShippingProvince = parsedOrder.ShippingProvince,
                ShippingPostalCode = parsedOrder.ShippingPostalCode,
                ShippingCountry = parsedOrder.ShippingCountry,
                OrderTotal = parsedOrder.TotalAmount,
                OrderStatus = MapWooCommerceStatus(parsedOrder.Status),
                OrderDate = parsedOrder.OrderDate,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = "WooCommerceWebhook"
            };

            // Add line items
            foreach (var wooLineItem in wooOrder.LineItems)
            {
                var lineItem = new OrderLineItem
                {
                    WooLineItemID = wooLineItem.Id,
                    ProductName = wooLineItem.Name,
                    ProductSKU = wooLineItem.Sku,
                    WooProductID = wooLineItem.ProductId,
                    Quantity = wooLineItem.Quantity,

                    // Parse weight - WooCommerce might send it or it might be null
                    WeightPerUnit = wooLineItem.Weight,

                    // Calculate total weight (WeightPerUnit × Quantity)
                    TotalWeight = wooLineItem.Weight.HasValue ? wooLineItem.Weight.Value * wooLineItem.Quantity : null,

                    // Parse prices (WooCommerce sends as strings)
                    UnitPrice = decimal.TryParse(wooLineItem.Subtotal, out var subtotal) ? subtotal / wooLineItem.Quantity : 0,

                    LineTotal = decimal.TryParse(wooLineItem.Total, out var total) ? total : 0,

                    TaxAmount = decimal.TryParse(wooLineItem.TotalTax, out var tax) ? tax : 0,

                    // Store variation details as JSON if present
                    VariationDetails = wooLineItem.MetaData != null && wooLineItem.MetaData.Any() ? JsonSerializer.Serialize(wooLineItem.MetaData) : null,

                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = "WooCommerceWebhook"
                };

                order.LineItems.Add(lineItem);
            }

            _context.Orders.Add(order);

            await _context.SaveChangesAsync();

            _logger.LogInformation("Created Order {OrderId} with {LineItemCount} line items. Total weight: {TotalWeight}kg",
                                    order.OrderID,
                                    order.LineItems.Count,
                                    order.LineItems.Sum(li => li.TotalWeight ?? 0)
            );

            return order;
        }


        /// <summary>
        /// Update existing order with fresh data from WooCommerce
        /// </summary>
        private async Task UpdateExistingOrderAsync(Order existingOrder, ParsedWebhookOrder parsedOrder, WooCommerceOrder wooOrder)
        {
            // Update order fields
            existingOrder.WooOrderNumber = parsedOrder.OrderNumber;
            existingOrder.CustomerName = parsedOrder.CustomerName;
            existingOrder.CustomerEmail = parsedOrder.CustomerEmail;
            existingOrder.CustomerPhone = parsedOrder.CustomerPhone;
            existingOrder.ShippingAddressLine1 = parsedOrder.ShippingAddress.Length > 100 ? parsedOrder.ShippingAddress.Substring(0, 100) : parsedOrder.ShippingAddress;
            existingOrder.ShippingAddressLine2 = parsedOrder.ShippingAddress.Length > 100 ? parsedOrder.ShippingAddress.Substring(100) : null;
            existingOrder.ShippingCity = parsedOrder.ShippingCity;
            existingOrder.ShippingProvince = parsedOrder.ShippingProvince;
            existingOrder.ShippingPostalCode = parsedOrder.ShippingPostalCode;
            existingOrder.ShippingCountry = parsedOrder.ShippingCountry;
            existingOrder.OrderTotal = parsedOrder.TotalAmount;
            existingOrder.OrderStatus = MapWooCommerceStatus(parsedOrder.Status);
            existingOrder.OrderDate = parsedOrder.OrderDate;
            existingOrder.ChangedOn = DateTime.UtcNow;
            existingOrder.ChangedBy = "WooCommerceWebhook";

            // Remove old line items and add new ones
            if (existingOrder.LineItems.Any())
            {
                _context.OrderLineItems.RemoveRange(existingOrder.LineItems);
            }

            foreach (var wooLineItem in wooOrder.LineItems)
            {
                var lineItem = new OrderLineItem
                {
                    OrderID = existingOrder.OrderID,
                    WooLineItemID = wooLineItem.Id,
                    ProductName = wooLineItem.Name,
                    ProductSKU = wooLineItem.Sku,
                    WooProductID = wooLineItem.ProductId,
                    Quantity = wooLineItem.Quantity,
                    WeightPerUnit = wooLineItem.Weight,
                    TotalWeight = wooLineItem.Weight.HasValue ? wooLineItem.Weight.Value * wooLineItem.Quantity : null,
                    UnitPrice = decimal.TryParse(wooLineItem.Subtotal, out var subtotal) ? subtotal / wooLineItem.Quantity : 0,
                    LineTotal = decimal.TryParse(wooLineItem.Total, out var total) ? total : 0,
                    TaxAmount = decimal.TryParse(wooLineItem.TotalTax, out var tax) ? tax : 0,
                    VariationDetails = wooLineItem.MetaData != null && wooLineItem.MetaData.Any() ? JsonSerializer.Serialize(wooLineItem.MetaData) : null,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = "WooCommerceWebhook"
                };

                existingOrder.LineItems.Add(lineItem);
            }

            await _context.SaveChangesAsync();
        }


        /// <summary>
        /// Map WooCommerce status to our order status
        /// WooCommerce: pending, processing, on-hold, completed, cancelled, refunded, failed
        /// Our System: Pending, Processing, Completed, Cancelled
        /// </summary>
        private string MapWooCommerceStatus(string wooStatus)
        {
            return wooStatus.ToLower() switch
            {
                "pending" => "Pending",
                "on-hold" => "Pending",
                "processing" => "Processing",
                "completed" => "Completed",
                "cancelled" => "Cancelled",
                "refunded" => "Cancelled",
                "failed" => "Cancelled",
                _ => "Pending"
            };
        }

        /// <summary>
        /// Find store by WooCommerce URL
        /// </summary>
        //private async Task<Store?> GetStoreByUrlAsync(string webhookSource)
        //{
        //    // Extract domain from webhook source
        //    // WooCommerce sends full URL like: https://store.com/wp-json/...
        //    var uri = new Uri(webhookSource);
        //    var baseUrl = $"{uri.Scheme}://{uri.Host}";

        //    return await _context.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.WooCommerceURL.StartsWith(baseUrl) && s.IsActive);
        //}


        /// <summary>
        /// Find store by WooCommerce URL
        /// </summary>
        private async Task<Store?> GetStoreByUrlAsync(string webhookSource)
        {
            // Webhook source is mandatory — the WebhookController rejects requests
            // missing it before this method is called. Defence in depth: refuse to
            // fall back to "first active store" because that lets a request without
            // a source header create orders for an arbitrary tenant.
            if (string.IsNullOrEmpty(webhookSource))
            {
                _logger.LogWarning("Webhook source header is empty — cannot identify store");
                return null;
            }

            // Extract domain from webhook source
            // WooCommerce sends full URL like: https://store.com/wp-json/...
            if (!Uri.TryCreate(webhookSource, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Webhook source header is not a valid URI: {Source}", webhookSource);
                return null;
            }
            var baseUrl = $"{uri.Scheme}://{uri.Host}";

            // Webhook context has no JWT; AppDbContext bypasses the tenant filter,
            // so this query freely sees stores across tenants. IgnoreQueryFilters
            // is added defensively in case the filter ever applies.
            return await _context.Stores
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.WooCommerceURL.StartsWith(baseUrl) && s.IsActive);
        }
    }
}