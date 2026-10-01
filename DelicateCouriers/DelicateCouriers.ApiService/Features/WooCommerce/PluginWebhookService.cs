using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Service for processing webhooks from the Delicate Courier WooCommerce Plugin
    /// Handles the custom plugin payload format
    /// </summary>
    public class PluginWebhookService
    {
        private readonly AppDbContext _context;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<PluginWebhookService> _logger;
        private readonly IWooFulfillmentVerifier _fulfillmentVerifier;
        private readonly ISystemEventLogger _systemEvents;
        private readonly Infrastructure.Geocoding.IGeocoder _geocoder;

        public PluginWebhookService(
            AppDbContext context,
            IBackgroundJobClient backgroundJobClient,
            ILogger<PluginWebhookService> logger,
            IWooFulfillmentVerifier fulfillmentVerifier,
            ISystemEventLogger systemEvents,
            Infrastructure.Geocoding.IGeocoder geocoder)
        {
            _context = context;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _fulfillmentVerifier = fulfillmentVerifier;
            _systemEvents = systemEvents;
            _geocoder = geocoder;
        }

        /// <summary>
        /// Process order from plugin webhook
        /// Creates or updates order, triggers shipment if ready
        /// </summary>
        public async Task<WebhookResponse> ProcessPluginOrderAsync(int storeId, PluginWebhookPayload payload)
        {
            // NOTE: this method intentionally does NOT swallow exceptions.
            // Validation failures (store not found, etc.) return Success=false
            // so the controller can map them to 4xx. UNEXPECTED exceptions
            // (DB outage, code bug, etc.) propagate to the controller's
            // outer catch which returns 500, so the plugin's bounded
            // retry-on-5xx will re-deliver the order and we never silently
            // drop a webhook because of a transient backend hiccup.
            // Step 1: Validate store exists and is active
            var store = await _context.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive);

            if (store == null)
            {
                _logger.LogWarning("Plugin webhook rejected: Store {StoreId} not found or inactive", storeId);

                return new WebhookResponse
                {
                    Success = false,
                    Message = $"Store {storeId} not found or inactive"
                };
            }

            _logger.LogInformation("Processing plugin order for Store: {StoreName} (ID: {StoreId}), TenantID: {TenantId}",
                                    store.StoreName,
                                    storeId,
                                    store.TenantID
            );

            // Step 2: Check if order already exists
            var existingOrder = await _context.Orders.Include(o => o.LineItems)
                                .Include(o => o.Shipment)
                                .FirstOrDefaultAsync(o => o.StoreID == storeId && o.WooOrderID == payload.WooOrderId.ToString());

            if (existingOrder != null)
            {
                return await HandleExistingOrderAsync(store, existingOrder, payload);
            }

            // Step 3: Create new order
            var order = await CreateOrderFromPluginAsync(store, payload);

            _logger.LogInformation("Created Order {OrderId} from plugin webhook. WooOrderID: {WooOrderId}, Status: {Status}",
                                    order.OrderID,
                                    payload.WooOrderId,
                                    payload.Status
            );

            // Step 4: Queue shipment creation if order is not in a terminal state
            // AND the store's Woo REST API confirms the customer didn't choose
            // a collection/pickup shipping method (see VerifyAndGateCollectionAsync).
            if (ShouldAutoCreateShipment(store, payload) && await VerifyAndGateCollectionAsync(store, order))
            {
                _logger.LogInformation("Order {OrderId} (status: {Status}) — queueing shipment creation",
                                        order.OrderID,
                                        payload.Status
                );

                _backgroundJobClient.Enqueue<ShipmentOrchestrationService>(service => service.CreateShipmentForOrderAsync(order.OrderID));
            }

            return new WebhookResponse
            {
                Success = true,
                Message = "Order created successfully",
                OrderId = order.OrderID,
                OrderNumber = order.WooOrderNumber
            };
        }

        /// <summary>
        /// Handle update to existing order
        /// </summary>
        private async Task<WebhookResponse> HandleExistingOrderAsync(Store store, Order existingOrder, PluginWebhookPayload payload)
        {
            var newStatus = MapPluginStatus(payload.Status);
            var hasIncompleteData = string.IsNullOrEmpty(existingOrder.CustomerName) || !existingOrder.LineItems.Any();
            var statusChanged = existingOrder.OrderStatus != newStatus;
            // Fulfillment_type must trigger a refresh too. Without this, a
            // customer (or merchant) flipping delivery→collect on an existing
            // order — without changing status — would never persist the new
            // value, and the Layer-2 guard in CreateShipmentForOrderAsync
            // would read stale "delivery" and book a Shiplogic shipment for
            // a collect order. Compare normalised (trim + lowercase) so
            // whitespace/casing churn from WC doesn't cause spurious updates.
            var incomingFulfillment = string.IsNullOrWhiteSpace(payload.FulfillmentType) ? null : payload.FulfillmentType.Trim().ToLowerInvariant();
            var fulfillmentChanged = !string.Equals(existingOrder.FulfillmentType, incomingFulfillment, StringComparison.Ordinal);
            // A special_trip block must also trigger a refresh: a webhook can
            // carry new/late-arriving quote data (amount, distance, customer
            // coordinates) while status and fulfillment are unchanged. Only
            // trigger when something actually differs to avoid spurious writes.
            var specialTripChanged = payload.SpecialTrip != null &&
                (existingOrder.SpecialTripQuotedAmount != payload.SpecialTrip.QuotedAmount ||
                 existingOrder.SpecialTripDistanceKm != payload.SpecialTrip.DistanceKm ||
                 existingOrder.SpecialTripCustomerLat != payload.SpecialTrip.CustomerLat ||
                 existingOrder.SpecialTripCustomerLng != payload.SpecialTrip.CustomerLng);

            if (hasIncompleteData || statusChanged || fulfillmentChanged || specialTripChanged)
            {
                _logger.LogInformation("Updating existing Order {OrderId}. HasIncompleteData: {Incomplete}, StatusChanged: {StatusChanged} ({OldStatus} -> {NewStatus}), FulfillmentChanged: {FulfillmentChanged} ({OldFulfillment} -> {NewFulfillment})",
                                        existingOrder.OrderID,
                                        hasIncompleteData,
                                        statusChanged,
                                        existingOrder.OrderStatus,
                                        newStatus,
                                        fulfillmentChanged,
                                        existingOrder.FulfillmentType ?? "(null)",
                                        incomingFulfillment ?? "(null)"
                );

                await UpdateExistingOrderAsync(existingOrder, payload);
            }
            else
            {
                _logger.LogInformation("Order {OrderId} already exists and is complete. No update needed.", existingOrder.OrderID);
            }

            // ALWAYS check enqueue regardless of whether we just updated. This
            // covers the case where a prior webhook delivery saved the order
            // row but failed to enqueue the shipment job (e.g. Hangfire storage
            // blip). On the plugin's retry the order would otherwise hit the
            // "already complete, skip" path and the shipment would never be
            // booked. The Hangfire job itself is idempotent — if a shipment
            // already exists it short-circuits — so re-enqueueing is safe.
            if (ShouldAutoCreateShipment(store, payload) && existingOrder.Shipment == null
                && await VerifyAndGateCollectionAsync(store, existingOrder))
            {
                _logger.LogInformation("Order {OrderId} (status: {Status}) has no shipment yet — queueing shipment creation.",
                                        existingOrder.OrderID,
                                        payload.Status
                );

                _backgroundJobClient.Enqueue<ShipmentOrchestrationService>(service => service.CreateShipmentForOrderAsync(existingOrder.OrderID));
            }

            return new WebhookResponse
            {
                Success = true,
                Message = hasIncompleteData || statusChanged || fulfillmentChanged ? "Order updated successfully" : "Order already processed",
                OrderId = existingOrder.OrderID,
                OrderNumber = existingOrder.WooOrderNumber
            };
        }

        /// <summary>
        /// Create new order from plugin payload
        /// </summary>
        private async Task<Order> CreateOrderFromPluginAsync(Store store, PluginWebhookPayload payload)
        {
            var order = new Order
            {
                TenantID = store.TenantID,
                StoreID = store.StoreID,
                WooOrderID = payload.WooOrderId.ToString(),
                WooOrderNumber = payload.OrderNumber,
                CustomerName = payload.Customer.Name,
                CustomerEmail = payload.Customer.Email,
                CustomerPhone = payload.Customer.Phone,
                ShippingAddressLine1 = payload.ShippingAddress.Street.Length > 100 ? payload.ShippingAddress.Street.Substring(0, 100) : payload.ShippingAddress.Street,
                ShippingAddressLine2 = payload.ShippingAddress.Street.Length > 100 ? payload.ShippingAddress.Street.Substring(100) : null,
                ShippingSuburb = string.IsNullOrWhiteSpace(payload.ShippingAddress.Suburb) ? null : payload.ShippingAddress.Suburb!.Trim(),
                ShippingCity = payload.ShippingAddress.City,
                ShippingProvince = payload.ShippingAddress.State,
                ShippingPostalCode = payload.ShippingAddress.Postcode,
                ShippingCountry = payload.ShippingAddress.Country,
                RequestedDeliveryDate = !string.IsNullOrEmpty(payload.DeliveryDate) && DateTime.TryParse(payload.DeliveryDate, out var parsedDate) ? DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc) : null,
                RequestedDeliveryTime = payload.DeliveryTime,
                RequestedCollectionDate = !string.IsNullOrEmpty(payload.CollectionDate) && DateTime.TryParse(payload.CollectionDate, out var parsedCollDate) ? DateTime.SpecifyKind(parsedCollDate, DateTimeKind.Utc) : null,
                RequestedCollectionTime = payload.CollectionTime,
                Occasion = payload.Occasion,
                OrderTotal = payload.Total,
                OrderStatus = MapPluginStatus(payload.Status),
                // Persist `fulfillment_type` on the order row so the
                // shipment-orchestration pipeline can refuse to book a
                // Shiplogic shipment for a collect order regardless of
                // which trigger path queued the job (webhook, manual
                // admin button, retry queue, status-transition hook).
                // The upstream gate in ShouldAutoCreateShipment already
                // covers the webhook path; persisting the value here is
                // what lets the downstream Layer-2 guard work.
                FulfillmentType = string.IsNullOrWhiteSpace(payload.FulfillmentType) ? null : payload.FulfillmentType.Trim().ToLowerInvariant(),
                // Special-trip quote (plugin v2.7.0+). Null when absent.
                SpecialTripQuotedAmount = payload.SpecialTrip?.QuotedAmount,
                SpecialTripDistanceKm = payload.SpecialTrip?.DistanceKm,
                SpecialTripCustomerLat = payload.SpecialTrip?.CustomerLat,
                SpecialTripCustomerLng = payload.SpecialTrip?.CustomerLng,
                // Woo plugins send `date_created` as bare wall-clock time in
                // the WordPress site's configured timezone (differs per
                // merchant: some UTC, some SAST). StoreLocalTime picks the
                // interpretation closest to the webhook arrival time. Shopify
                // sends ISO-8601 with an explicit offset, which is honoured.
                OrderDate = Infrastructure.StoreLocalTime.ParseToUtc(
                    payload.DateCreated, receivedAtUtc: DateTime.UtcNow, fallbackUtc: DateTime.UtcNow),
                CreatedOn = DateTime.UtcNow,
                CreatedBy = "PluginWebhook"
            };

            // Add line items
            foreach (var item in payload.LineItems)
            {
                var lineItem = new OrderLineItem
                {
                    WooLineItemID = item.Id,
                    ProductName = item.Name,
                    ProductSKU = item.Sku,
                    WooProductID = item.ProductId,
                    Quantity = item.Quantity,
                    UnitPrice = item.Quantity > 0 ? item.Price / item.Quantity : item.Price,
                    LineTotal = item.Price,
                    // Calculate weight per item from total weight
                    WeightPerUnit = payload.LineItems.Sum(i => i.Quantity) > 0 ? payload.TotalWeight / payload.LineItems.Sum(i => i.Quantity) : 1,
                    TotalWeight = payload.LineItems.Sum(i => i.Quantity) > 0 ? (payload.TotalWeight / payload.LineItems.Sum(i => i.Quantity)) * item.Quantity : item.Quantity,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = "PluginWebhook"
                };

                order.LineItems.Add(lineItem);
            }

            _context.Orders.Add(order);

            await _context.SaveChangesAsync();

            _logger.LogInformation("Created Order {OrderId} with {LineItemCount} line items. Total: {Total}, Weight: {Weight}kg",
                                    order.OrderID,
                                    order.LineItems.Count,
                                    payload.Total,
                                    payload.TotalWeight
                                  );

            return order;
        }

        /// <summary>
        /// Update existing order with plugin payload data
        /// </summary>
        private async Task UpdateExistingOrderAsync(Order existingOrder, PluginWebhookPayload payload)
        {
            existingOrder.WooOrderNumber = payload.OrderNumber;
            existingOrder.CustomerName = payload.Customer.Name;
            existingOrder.CustomerEmail = payload.Customer.Email;
            existingOrder.CustomerPhone = payload.Customer.Phone;
            existingOrder.ShippingAddressLine1 = payload.ShippingAddress.Street.Length > 100 ? payload.ShippingAddress.Street.Substring(0, 100) : payload.ShippingAddress.Street;
            existingOrder.ShippingAddressLine2 = payload.ShippingAddress.Street.Length > 100 ? payload.ShippingAddress.Street.Substring(100) : null;
            existingOrder.ShippingSuburb = string.IsNullOrWhiteSpace(payload.ShippingAddress.Suburb) ? null : payload.ShippingAddress.Suburb!.Trim();
            existingOrder.ShippingCity = payload.ShippingAddress.City;
            existingOrder.ShippingProvince = payload.ShippingAddress.State;
            existingOrder.ShippingPostalCode = payload.ShippingAddress.Postcode;
            existingOrder.ShippingCountry = payload.ShippingAddress.Country;
            existingOrder.RequestedDeliveryDate = !string.IsNullOrEmpty(payload.DeliveryDate) && DateTime.TryParse(payload.DeliveryDate, out var parsedDate) ? DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc) : null;
            existingOrder.RequestedDeliveryTime = payload.DeliveryTime;
            existingOrder.RequestedCollectionDate = !string.IsNullOrEmpty(payload.CollectionDate) && DateTime.TryParse(payload.CollectionDate, out var parsedCollDate) ? DateTime.SpecifyKind(parsedCollDate, DateTimeKind.Utc) : null;
            existingOrder.RequestedCollectionTime = payload.CollectionTime;
            existingOrder.Occasion = payload.Occasion;
            existingOrder.OrderTotal = payload.Total;
            existingOrder.OrderStatus = MapPluginStatus(payload.Status);
            // Refresh fulfillment_type on every plugin push. A customer
            // editing their order in WooCommerce admin (or a merchant
            // switching the fulfillment dropdown after the fact) should
            // see the new choice persisted so the Layer-2 guard in
            // CreateShipmentForOrderAsync picks it up on the next book
            // attempt. Lower-cased + trimmed for stable comparison.
            existingOrder.FulfillmentType = string.IsNullOrWhiteSpace(payload.FulfillmentType) ? null : payload.FulfillmentType.Trim().ToLowerInvariant();
            // Refresh the special-trip quote alongside fulfillment_type, but
            // never wipe an existing quote with null — a follow-up webhook
            // from an older plugin build (or a non-special-trip event) must
            // not destroy the amount the customer actually paid.
            if (payload.SpecialTrip != null)
            {
                existingOrder.SpecialTripQuotedAmount = payload.SpecialTrip.QuotedAmount;
                existingOrder.SpecialTripDistanceKm = payload.SpecialTrip.DistanceKm;
                existingOrder.SpecialTripCustomerLat = payload.SpecialTrip.CustomerLat;
                existingOrder.SpecialTripCustomerLng = payload.SpecialTrip.CustomerLng;
            }
            existingOrder.ChangedOn = DateTime.UtcNow;
            existingOrder.ChangedBy = "PluginWebhook";

            // Remove old line items and add new ones
            if (existingOrder.LineItems.Any())
            {
                _context.OrderLineItems.RemoveRange(existingOrder.LineItems);
            }

            foreach (var item in payload.LineItems)
            {
                var lineItem = new OrderLineItem
                {
                    OrderID = existingOrder.OrderID,
                    WooLineItemID = item.Id,
                    ProductName = item.Name,
                    ProductSKU = item.Sku,
                    WooProductID = item.ProductId,
                    Quantity = item.Quantity,
                    UnitPrice = item.Quantity > 0 ? item.Price / item.Quantity : item.Price,
                    LineTotal = item.Price,
                    WeightPerUnit = payload.LineItems.Sum(i => i.Quantity) > 0 ? payload.TotalWeight / payload.LineItems.Sum(i => i.Quantity) : 1,
                    TotalWeight = payload.LineItems.Sum(i => i.Quantity) > 0 ? (payload.TotalWeight / payload.LineItems.Sum(i => i.Quantity)) * item.Quantity : item.Quantity,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = "PluginWebhook"
                };

                existingOrder.LineItems.Add(lineItem);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated Order {OrderId} with {LineItemCount} line items", existingOrder.OrderID, existingOrder.LineItems.Count);
        }

        /// <summary>
        /// Decide whether the incoming order should be booked as a shipment.
        ///
        /// Two gates:
        /// 1. Fulfillment type — if the customer chose in-person collection
        ///    at checkout (`fulfillment_type == "collect"`), there is no
        ///    delivery to book and we must NOT create a Shiplogic shipment.
        ///    Without this gate the platform would queue a courier booking
        ///    for orders the customer is picking up themselves and the
        ///    merchant would be billed for a real shipment that should
        ///    never have happened. `special_trip` is collection-flow on
        ///    the merchant side but intentionally still booked through
        ///    Shiplogic as an ad-hoc job, so it falls through.
        ///    `fulfillment_type` defaults to null on older plugin versions
        ///    that don't send the field — those keep the old delivery-only
        ///    behaviour, so no existing merchant is affected by this gate.
        /// 2. Order status — previously this gated on "processing" or
        ///    "completed" only, which silently dropped any order that
        ///    arrived in another status (e.g. "on-hold", "pending") even
        ///    though the merchant clearly pushed it at us. We now BOOK
        ///    BY DEFAULT and only skip the terminal states where a
        ///    shipment would obviously be wrong (cancelled/refunded/
        ///    failed). Anything else — including blank/unknown statuses
        ///    — gets queued, so no plugin push is ever silently swallowed.
        ///
        /// A third gate — Woo REST shipping-line verification — lives in
        /// <see cref="VerifyAndGateCollectionAsync"/> below and runs only
        /// when this method has already said "book".
        /// </summary>
        private bool ShouldAutoCreateShipment(Store store, PluginWebhookPayload payload)
        {
            if (string.Equals(payload.FulfillmentType, "collect", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Skipping Shiplogic shipment creation for collect order. WooOrderID: {WooOrderId}",
                    payload.WooOrderId);
                return false;
            }

            var terminal = new[] { "cancelled", "canceled", "refunded", "failed", "trash" };
            var normalised = payload.Status?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalised) && terminal.Contains(normalised))
            {
                _logger.LogInformation("Skipping shipment creation: order status '{Status}' is terminal", payload.Status);
                return false;
            }

            // Per-store override (e.g. Baked By Nataleen, store 7): when the
            // store lists explicit trigger statuses, ONLY those statuses may
            // book. The order is still saved either way — a later webhook
            // (the plugin fires on every status transition) re-evaluates this
            // gate via HandleExistingOrderAsync and books once the status
            // reaches a listed one. Stores without an override keep the
            // platform default: book on any non-terminal status.
            if (!BookingStatusGate.IsAllowed(store, normalised, out var allowed))
            {
                _logger.LogInformation(
                    "Skipping shipment creation for Store {StoreId}: status '{Status}' is not in the store's booking-trigger list [{Allowed}]. WooOrderID: {WooOrderId}",
                    store.StoreID, payload.Status ?? "(blank)", allowed, payload.WooOrderId);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Gate 3 — Woo REST shipping-line verification (backend-only).
        ///
        /// Some stores (Baked By Nataleen, store #7) sell paid collection
        /// points built as `flat_rate` shipping methods whose only collection
        /// signal is the merchant-written LABEL ("Collect from 1 Clifford
        /// road… R95.00"). The plugin's heuristic only inspects the method
        /// ID, so those orders arrive tagged `delivery` — and order #49906
        /// was wrongly booked as a real Shiplogic shipment because of it.
        /// The store's plugin cannot be updated, so the platform verifies the
        /// chosen shipping method itself via the store's Woo REST credentials.
        ///
        /// Returns TRUE when booking may proceed. Behaviour by outcome:
        ///  * Collection      — order reclassified to `collect`, method info
        ///                      persisted, system event emitted, NO booking.
        ///  * ConfirmedDelivery — method info persisted for audit, booking
        ///                      proceeds exactly as before.
        ///  * Unverifiable    — store has no Woo REST credentials; keep the
        ///                      legacy behaviour (book) so stores that never
        ///                      configured REST access are unaffected.
        ///  * LookupFailed    — FAIL SAFE: do NOT book (never risk shipping a
        ///                      collection order), log loudly and emit an
        ///                      `order.fulfillment_verification_failed` system
        ///                      event so an admin reviews + books manually.
        /// </summary>
        private async Task<bool> VerifyAndGateCollectionAsync(Store store, Order order)
        {
            var verification = await _fulfillmentVerifier.VerifyAsync(store, order.WooOrderID);

            switch (verification.Outcome)
            {
                case FulfillmentVerificationOutcome.Collection:
                    order.FulfillmentType = "collect";
                    order.ShippingMethodId = verification.ShippingMethodId;
                    order.ShippingMethodTitle = verification.ShippingMethodTitle;
                    order.ChangedOn = DateTime.UtcNow;
                    order.ChangedBy = "FulfillmentVerification";
                    await _context.SaveChangesAsync();

                    _logger.LogInformation(
                        "Order {OrderId} (WooOrderID {WooOrderId}) reclassified as COLLECT — customer chose collection shipping method '{Title}'. No shipment will be booked.",
                        order.OrderID, order.WooOrderID, verification.ShippingMethodTitle ?? verification.ShippingMethodId ?? "(unknown)");

                    await _systemEvents.LogAsync(new SystemEventEntry
                    {
                        EventType = "order.reclassified_collect",
                        ActorKind = "System",
                        ActorLabel = "PluginWebhookService",
                        TenantId = order.TenantID,
                        EntityType = "Order",
                        EntityRef = $"#{order.WooOrderNumber}",
                        Message = $"Order #{order.WooOrderNumber} reclassified as collection: customer chose shipping method \"{verification.ShippingMethodTitle ?? verification.ShippingMethodId}\". No shipment booked.",
                        Details = new
                        {
                            orderId = order.OrderID,
                            wooOrderId = order.WooOrderID,
                            storeId = order.StoreID,
                            shippingMethodId = verification.ShippingMethodId,
                            shippingMethodTitle = verification.ShippingMethodTitle,
                        },
                    });
                    return false;

                case FulfillmentVerificationOutcome.ConfirmedDelivery:
                    // Persist the verified method for auditability and so the
                    // downstream pre-booking guard can re-check without
                    // another REST round-trip.
                    order.ShippingMethodId = verification.ShippingMethodId;
                    order.ShippingMethodTitle = verification.ShippingMethodTitle;

                    // Special trip: the platform's rates endpoint serves the
                    // distance-priced fallback as a NORMAL-looking rate (the
                    // v2.9.x plugin can't flag it), so the order arrives
                    // tagged `delivery`. Recognize it here by the method
                    // label and reclassify so booking goes out as an SPX
                    // ad-hoc trip instead of a standard shipment.
                    await TryReclassifySpecialTripAsync(store, order, verification.ShippingMethodTitle);

                    await _context.SaveChangesAsync();
                    return true;

                case FulfillmentVerificationOutcome.Unverifiable:
                    return true;

                case FulfillmentVerificationOutcome.LookupFailed:
                default:
                    _logger.LogError(
                        "FULFILLMENT VERIFICATION FAILED for Order {OrderId} (WooOrderID {WooOrderId}, Store {StoreId}) — " +
                        "could not confirm via Woo REST whether the customer chose delivery or collection. " +
                        "FAILING SAFE: no shipment booked. An admin must review the order and book manually if it is a genuine delivery.",
                        order.OrderID, order.WooOrderID, order.StoreID);

                    await _systemEvents.LogAsync(new SystemEventEntry
                    {
                        EventType = "order.fulfillment_verification_failed",
                        ActorKind = "System",
                        ActorLabel = "PluginWebhookService",
                        TenantId = order.TenantID,
                        EntityType = "Order",
                        EntityRef = $"#{order.WooOrderNumber}",
                        Message = $"NEEDS REVIEW: could not verify shipping method for Order #{order.WooOrderNumber} (Woo REST lookup failed). Shipment booking withheld — review the order and book manually if it is a genuine delivery.",
                        Details = new
                        {
                            orderId = order.OrderID,
                            wooOrderId = order.WooOrderID,
                            storeId = order.StoreID,
                        },
                    });
                    return false;
            }
        }

        /// <summary>
        /// If the chosen shipping method's label is one of OUR backend-served
        /// "Special Trip Request (X km)" fallback rates, reclassify the order
        /// as a special trip: parse the distance off the label, re-derive the
        /// pre-markup quoted amount from the store's configured pricing, and
        /// resolve the customer's coordinates from our geocode cache (warm
        /// from rate time). Best-effort — booking still proceeds even when
        /// geocoding fails (the shipment mapper geocodes again as fallback).
        /// </summary>
        private async Task TryReclassifySpecialTripAsync(Store store, Order order, string? shippingMethodTitle)
        {
            if (string.IsNullOrWhiteSpace(shippingMethodTitle))
                return;

            // Match the label this store's special-trip rate is served with
            // (custom CheckoutRateLabel or the default). With a custom label
            // the store's NORMAL rates carry the same name — those have no
            // " (X km)" suffix, so the distance-parse gate below keeps them
            // from being reclassified.
            var labelPrefix = DelicateCouriers.Features.Shipping.GetRates.GetRatesController
                .EffectiveSpecialTripLabelPrefix(store);
            if (!shippingMethodTitle.Contains(labelPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(order.FulfillmentType, "special_trip", StringComparison.OrdinalIgnoreCase))
                return; // already reclassified on an earlier webhook

            var distanceKm = ParseDistanceKmFromTitle(shippingMethodTitle);
            var quotedAmount = distanceKm is decimal km && km > 0
                ? DelicateCouriers.Features.Shipping.SpecialTrip.SpecialTripQuoter.ComputeAmount(store, km)
                : null;

            // Only reclassify when the label carries a parsable, priceable
            // distance. Our backend-served labels always do; a merchant-made
            // method that merely CONTAINS the phrase must not turn into an
            // unpriced SPX booking.
            if (quotedAmount is null)
            {
                _logger.LogWarning(
                    "Order {OrderId}: shipping method \"{Title}\" looks like a Special Trip but has no parsable distance / store pricing — leaving fulfillment as-is.",
                    order.OrderID, shippingMethodTitle);
                return;
            }

            order.FulfillmentType = "special_trip";
            order.SpecialTripDistanceKm = distanceKm;
            order.SpecialTripQuotedAmount = quotedAmount;

            try
            {
                var geo = await _geocoder.GeocodeAsync(new Infrastructure.Geocoding.GeocodeQuery(
                    Street: order.ShippingAddressLine1,
                    Suburb: order.ShippingSuburb ?? order.ShippingAddressLine2,
                    City: order.ShippingCity,
                    Province: order.ShippingProvince,
                    PostalCode: order.ShippingPostalCode,
                    Country: Infrastructure.CountryNormalizer.ToIsoCode(order.ShippingCountry)),
                    CancellationToken.None);
                if (geo != null)
                {
                    order.SpecialTripCustomerLat = geo.Latitude;
                    order.SpecialTripCustomerLng = geo.Longitude;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Special-trip reclassification: geocode failed for Order {OrderId}; mapper will geocode at booking time.",
                    order.OrderID);
            }

            _logger.LogInformation(
                "Order {OrderId} (Woo #{WooOrderNumber}) reclassified as SPECIAL TRIP from method label \"{Title}\": {Km} km, quoted R{Amount} (pre-markup).",
                order.OrderID, order.WooOrderNumber, shippingMethodTitle,
                distanceKm?.ToString("0.#") ?? "?", quotedAmount?.ToString("0.00") ?? "?");

            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "order.special_trip_detected",
                ActorKind = "System",
                ActorLabel = "PluginWebhookService",
                TenantId = order.TenantID,
                EntityType = "Order",
                EntityRef = $"#{order.WooOrderNumber}",
                Message = $"Order #{order.WooOrderNumber} recognized as a Special Trip (\"{shippingMethodTitle}\") — will book as an SPX ad-hoc trip.",
                Details = new
                {
                    orderId = order.OrderID,
                    wooOrderId = order.WooOrderID,
                    storeId = order.StoreID,
                    distanceKm,
                    quotedAmount,
                },
            });
        }

        /// <summary>
        /// Pull the one-way distance out of a "Special Trip Request (12.3 km)"
        /// label. Returns null when the label carries no parsable distance.
        /// </summary>
        public static decimal? ParseDistanceKmFromTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                title, @"\((\d+(?:[.,]\d+)?)\s*km\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var raw = match.Groups[1].Value.Replace(',', '.');
            return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var km) ? km : null;
        }

        /// <summary>
        /// Map WooCommerce/plugin status to our internal status
        /// </summary>
        private string MapPluginStatus(string status)
        {
            return status?.ToLower() switch
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
    }
}