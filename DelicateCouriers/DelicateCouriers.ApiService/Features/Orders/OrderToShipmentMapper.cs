using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Shiplogic.DTOs;
using DelicateCouriers.ApiService.Features.Packaging;
using DelicateCouriers.ApiService.Infrastructure;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;

namespace DelicateCouriers.ApiService.Features.Orders;

/// <summary>
/// Maps WooCommerce orders to Shiplogic shipment requests
/// </summary>
public class OrderToShipmentMapper
{
    private readonly PackageMappingService _packageMappingService;
    private readonly IGeocoder _geocoder;
    private readonly ILogger<OrderToShipmentMapper> _logger;

    // Valid Shiplogic service level codes
    private static readonly HashSet<string> ValidServiceLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "STD", "LSF", "LOF", "ECO", "LSE", "LSX", "NFS", "NOF", "NFE", "RFS", "ROF", "RFE",
        // SPX — Shiplogic "Special Express" ad-hoc trip; used for
        // special_trip orders quoted by the WooCommerce plugin (v2.7.0+).
        "SPX"
    };

    public OrderToShipmentMapper(PackageMappingService packageMappingService, IGeocoder geocoder, ILogger<OrderToShipmentMapper> logger)
    {
        _packageMappingService = packageMappingService;
        _geocoder = geocoder;
        _logger = logger;
    }

    /// <summary>
    /// Convert a WooCommerce order into a Shiplogic shipment request
    /// </summary>
    public async Task<CreateShipmentRequest> MapOrderToShipmentAsync(Order order, Store store)
    {
        // Entry breadcrumb — earlier production logs went silent between
        // the orchestration "Starting shipment creation" line and the
        // "Shipment timing" log below, so we couldn't tell whether the
        // mapper had even been entered. Log immediately on entry, BEFORE
        // any work that could throw, so the next deploy makes the failure
        // location obvious.
        _logger.LogInformation(
            "MapOrderToShipmentAsync entered for OrderID: {OrderId}. StoreNull={StoreNull}, StoreID={StoreId}, LineItemCount={LineItems}",
            order?.OrderID,
            store == null,
            store?.StoreID,
            order?.LineItems?.Count ?? 0);

        if (store == null)
        {
            throw new InvalidOperationException(
                $"Cannot map Order {order?.OrderID} to a Shiplogic shipment — the Store reference is null. " +
                "This usually means the order's Store was filtered out by the tenant query filter.");
        }

        ValidateStoreConfiguration(store);

        // Shiplogic rejects bookings whose delivery address lacks a postal
        // code (BadRequest: "delivery postal_code is required"). Fail fast
        // with a clear, actionable message instead — an empty postal code
        // here almost always means the order is not actually a courier
        // delivery (e.g. a Shopify pickup order that was misclassified) or
        // the customer's address is incomplete. Special trips are exempt:
        // they book as SPX ad-hoc trips routed by the customer lat/lng
        // captured at checkout, so a postal code is not required.
        if (string.IsNullOrWhiteSpace(order.ShippingPostalCode) &&
            !string.Equals(order.FulfillmentType, "special_trip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Order {order.OrderID} (#{order.WooOrderNumber}) has no delivery postal code — " +
                "cannot book a Shiplogic shipment. Check whether this is really a delivery order " +
                "(pickup/collection orders must not book shipments) or ask the customer for a complete address.");
        }

        var serviceLevel = GetValidServiceLevel(store.DefaultServiceLevel);

        // Special-trip orders (plugin v2.7.0+ fallback rate) are booked as
        // Shiplogic SPX ad-hoc trips, with the amount quoted to the customer
        // carried as the shipment's declared value.
        var isSpecialTrip = string.Equals(order.FulfillmentType, "special_trip", StringComparison.OrdinalIgnoreCase);
        if (isSpecialTrip)
        {
            serviceLevel = "SPX";
            _logger.LogInformation(
                "Order {OrderId} is a special trip — booking as SPX with declared value {DeclaredValue}",
                order.OrderID, order.SpecialTripQuotedAmount);
        }
        var deliveryDate = order.RequestedDeliveryDate?.Date ?? DateTime.UtcNow.AddDays(1).Date;

        if (deliveryDate < DateTime.UtcNow.Date)
        {
            deliveryDate = DateTime.UtcNow.AddDays(1).Date;
        }

        // Collection date from plugin, fallback to delivery date (same-day model)
        var collectionDate = order.RequestedCollectionDate?.Date ?? deliveryDate;

        // Parse time windows sent by the plugin
        var (deliveryAfter, deliveryBefore) = ParseTimeRange(order.RequestedDeliveryTime);
        var (collectionAfter, collectionBefore) = ParseTimeRange(order.RequestedCollectionTime);

        // Couriers can't schedule against a zero-width window (e.g.
        // "09:00"-"09:00"), which is exactly what we get when the
        // merchant's WooCommerce plugin reports a single time instead of
        // a range. A customer picking a single DELIVERY time means
        // "deliver BY that time", so we treat it as a 30-minute slot
        // ENDING at the chosen time (08:00 → 07:30-08:00). Delivery is
        // resolved FIRST so the effective window (and the collection
        // default derived from it) never carries a zero-width range
        // into the request.
        (deliveryAfter, deliveryBefore) = SlotEndingAt(deliveryAfter, deliveryBefore);

        // Resolve the effective delivery window (requested slot, else
        // store defaults) so the collection default below can key off it.
        var effectiveDeliveryAfter = deliveryAfter ?? store.DeliveryTimeFrom ?? "08:00";
        var effectiveDeliveryBefore = deliveryBefore ?? store.DeliveryTimeTo ?? "17:00";

        // Default collection window: same day as delivery, running from
        // 120 to 90 minutes before the END of the delivery window (the
        // customer's chosen time). E.g. chosen 16:30 → delivery 16:00-16:30,
        // collection 14:30-15:00. Applies whenever the order didn't carry
        // an explicit collection time from the merchant's checkout.
        if (collectionAfter == null)
        {
            collectionAfter = SubtractMinutes(effectiveDeliveryBefore, 120) ?? store.CollectionTimeFrom ?? "08:00";
            collectionBefore = SubtractMinutes(effectiveDeliveryBefore, 90) ?? effectiveDeliveryAfter;

            // Underflow guard: SubtractMinutes clamps to 00:00, so a very
            // early delivery end (before ~01:30) collapses both edges to
            // 00:00. In that case fall back to [00:00, delivery start] so
            // the collection window stays valid and never extends past the
            // start of the delivery window.
            if (string.CompareOrdinal(collectionAfter, collectionBefore) >= 0)
            {
                collectionAfter = "00:00";
                collectionBefore = effectiveDeliveryAfter;
            }
        }

        (collectionAfter, collectionBefore) = WidenIfZeroWidth(collectionAfter, collectionBefore);

        _logger.LogInformation("Shipment timing for Order {OrderId}: DeliveryDate={DeliveryDate}, DeliveryWindow={DeliveryAfter}-{DeliveryBefore}, CollectionDate={CollectionDate}, CollectionWindow={CollectionAfter}-{CollectionBefore}",
                                order.OrderID, deliveryDate.ToString("yyyy-MM-dd"),
                                deliveryAfter ?? "default", deliveryBefore ?? "default",
                                collectionDate.ToString("yyyy-MM-dd"),
                                collectionAfter ?? "default", collectionBefore ?? "default"
        );

        // Map parcels using package type configuration
        var parcels = await MapToParcelsAsync(order, store.StoreID);

        // Resolve delivery lat/lng ourselves (cached geocoder), mirroring the
        // rates flow. Shiplogic's server-side geocoder struggles with vague
        // ZA addresses (unnumbered plot/farm descriptions), which can stall shipment
        // creation until our HTTP timeout fires. Best-effort: booking still
        // proceeds without coordinates if the lookup fails.
        var deliveryAddress = MapDeliveryAddress(order);

        // Special trips carry customer coordinates geocoded by the plugin at
        // checkout — prefer those (they match the exact quoted route) and
        // skip the platform-side geocode round-trip.
        if (isSpecialTrip && order.SpecialTripCustomerLat.HasValue && order.SpecialTripCustomerLng.HasValue)
        {
            deliveryAddress.Latitude = order.SpecialTripCustomerLat;
            deliveryAddress.Longitude = order.SpecialTripCustomerLng;
        }
        else
        try
        {
            var deliveryGeo = await _geocoder.GeocodeAsync(new GeocodeQuery(
                Street: deliveryAddress.Street,
                Suburb: deliveryAddress.Suburb,
                City: deliveryAddress.City,
                Province: order.ShippingProvince,
                PostalCode: deliveryAddress.PostalCode,
                Country: deliveryAddress.Country ?? "ZA"), CancellationToken.None);
            if (deliveryGeo != null)
            {
                deliveryAddress.Latitude = deliveryGeo.Latitude;
                deliveryAddress.Longitude = deliveryGeo.Longitude;
                _logger.LogInformation(
                    "Geocoded delivery address for Order {OrderId}: lat={Lat}, lng={Lng} (provider={Provider})",
                    order.OrderID, deliveryGeo.Latitude, deliveryGeo.Longitude, deliveryGeo.Provider);
            }
            else
            {
                _logger.LogWarning("Could not geocode delivery address for Order {OrderId}; booking without delivery coordinates.", order.OrderID);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delivery geocode lookup failed for Order {OrderId}; booking without delivery coordinates.", order.OrderID);
        }

        return new CreateShipmentRequest
        {
            AccountId = store.ShiplogicAccountId,
            ProviderId = store.ShiplogicProviderId,
            CollectionAddress = MapCollectionAddress(store),
            CollectionContact = MapCollectionContact(store),
            DeliveryAddress = deliveryAddress,
            DeliveryContact = MapDeliveryContact(order),
            Parcels = parcels,
            ServiceLevelCode = serviceLevel,
            MuteNotifications = false,
            CustomerReference = $"WC-{order.WooOrderID}",
            CollectionMinDate = collectionDate,
            CollectionAfter = collectionAfter ?? store.CollectionTimeFrom ?? "08:00",
            CollectionBefore = collectionBefore ?? store.CollectionTimeTo ?? "16:00",
            DeliveryMinDate = deliveryDate,
            DeliveryAfter = effectiveDeliveryAfter,
            DeliveryBefore = effectiveDeliveryBefore,
            // The quoted special-trip amount rides along as declared value so
            // the merchant's Shiplogic account bills/insures the ad-hoc trip
            // at what the customer actually paid. Null for normal orders.
            DeclaredValue = isSpecialTrip ? order.SpecialTripQuotedAmount : null
        };
    }

    /// <summary>
    /// Parse a time range string like "10:30 AM - 12:30 PM" or "10:30 - 12:30"
    /// Returns (startTime, endTime) in 24h "HH:mm" format, or (null, null) if unparseable
    /// </summary>
    private static (string? start, string? end) ParseTimeRange(string? timeRange)
    {
        if (string.IsNullOrWhiteSpace(timeRange))

            return (null, null);

        // Split on common delimiters: " - ", " – ", "-", "–"
        var separators = new[] { " - ", " – ", " to ", "-", "–" };
        string[]? parts = null;

        foreach (var sep in separators)
        {
            if (timeRange.Contains(sep))
            {
                parts = timeRange.Split(new[] { sep }, 2, StringSplitOptions.TrimEntries);
                break;
            }
        }

        if (parts == null || parts.Length < 2)
        {
            // Single time value (e.g., "14:30")
            var single = ParseTimeTo24H(timeRange.Trim());
            return (single, single);
        }

        var start = ParseTimeTo24H(parts[0]);
        var end = ParseTimeTo24H(parts[1]);

        return (start, end);
    }

    /// <summary>
    /// Parse time string to 24h format "HH:mm"
    /// Handles: "10:30 AM", "2:30 PM", "14:30", "8:30"
    /// </summary>
    private static string? ParseTimeTo24H(string time)
    {
        if (string.IsNullOrWhiteSpace(time))
            return null;

        // Try standard DateTime parsing (handles "10:30 AM", "2:30 PM", etc.)
        if (DateTime.TryParse(time.Trim(), out var parsed))
        {
            return parsed.ToString("HH:mm");
        }

        // Try plain HH:mm format
        if (TimeSpan.TryParse(time.Trim(), out var ts))
        {
            return ts.ToString(@"hh\:mm");
        }

        return null;
    }

    /// <summary>
    /// Subtract minutes from a "HH:mm" time string
    /// </summary>
    private static string? SubtractMinutes(string? time, int minutes)
    {
        if (string.IsNullOrWhiteSpace(time))

            return null;

        if (TimeSpan.TryParse(time, out var ts))
        {
            var result = ts.Subtract(TimeSpan.FromMinutes(minutes));

            if (result < TimeSpan.Zero)
                result = TimeSpan.Zero;

            return result.ToString(@"hh\:mm");
        }

        return null;
    }

    /// <summary>
    /// Convert a zero-width DELIVERY window (customer picked a single
    /// time, e.g. "08:00") into a 30-minute slot ENDING at that time
    /// (07:30-08:00). If the chosen time is too close to midnight to
    /// pull the start back (e.g. "00:00"), push the end forward 30
    /// minutes instead so `after &lt; before` always holds.
    /// </summary>
    private static (string? After, string? Before) SlotEndingAt(string? after, string? before)
    {
        if (after == null || after != before)
            return (after, before);

        var pulledStart = SubtractMinutes(before, 30);
        if (pulledStart != null && pulledStart != before)
            return (pulledStart, before);

        // Start-of-day case (chosen time 00:00): push the end forward.
        var pushedEnd = AddMinutes(after, 30);
        if (pushedEnd != null && pushedEnd != after)
            return (after, pushedEnd);

        return (after, before);
    }

    /// <summary>
    /// Widen a zero-width time window (after == before) to a 2-hour
    /// window. Prefers pushing `before` forward by 2h; if that would
    /// overflow past 23:59 (start was too close to end-of-day), pulls
    /// `after` back by 2h instead. Either way the result satisfies
    /// `after &lt; before` so Shiplogic will accept it.
    /// </summary>
    private static (string? After, string? Before) WidenIfZeroWidth(string? after, string? before)
    {
        if (after == null || after != before)
            return (after, before);

        var pushedEnd = AddMinutes(after, 120);
        if (pushedEnd != null && pushedEnd != after)
            return (after, pushedEnd);

        // End-of-day case: pull start earlier instead.
        var pulledStart = SubtractMinutes(before, 120);
        if (pulledStart != null && pulledStart != before)
            return (pulledStart, before);

        // Shouldn't happen for a well-formed HH:mm, but stay defensive.
        return (after, before);
    }

    /// <summary>
    /// Add minutes to a "HH:mm" time string. Caps at 23:59 so we never
    /// roll the window into the next day.
    /// </summary>
    private static string? AddMinutes(string? time, int minutes)
    {
        if (string.IsNullOrWhiteSpace(time))
            return null;

        if (TimeSpan.TryParse(time, out var ts))
        {
            var result = ts.Add(TimeSpan.FromMinutes(minutes));
            var cap = new TimeSpan(23, 59, 0);
            if (result > cap) result = cap;
            return result.ToString(@"hh\:mm");
        }

        return null;
    }

    /// <summary>
    /// Map order line items to parcels using package type configuration
    /// </summary>
    private async Task<List<ParcelDto>> MapToParcelsAsync(Order order, int storeId)
    {
        var parcels = new List<ParcelDto>();

        if (order.LineItems != null && order.LineItems.Any())
        {
            foreach (var lineItem in order.LineItems)
            {
                // Find matching package type for this product
                var packageType = await _packageMappingService.GetPackageTypeForProductAsync(storeId, lineItem.ProductName);

                // Calculate weight
                decimal itemWeight;
                if (lineItem.TotalWeight.HasValue && lineItem.TotalWeight.Value > 0)
                {
                    itemWeight = lineItem.TotalWeight.Value;
                }
                else if (lineItem.WeightPerUnit.HasValue && lineItem.WeightPerUnit.Value > 0)
                {
                    itemWeight = lineItem.WeightPerUnit.Value * lineItem.Quantity;
                }
                else if (packageType != null)
                {
                    itemWeight = packageType.DefaultWeightKg * lineItem.Quantity;
                }
                else
                {
                    itemWeight = 0.5m * lineItem.Quantity;
                }

                if (itemWeight < 0.1m)
                {
                    itemWeight = 0.5m;
                }

                // Use package type dimensions or fallback to estimates
                int length, width, height;
                string description;

                if (packageType != null)
                {
                    length = packageType.LengthCm;
                    width = packageType.WidthCm;
                    height = packageType.HeightCm;
                    description = packageType.Name; // e.g., "Medium Cheesecake Box"

                    _logger.LogInformation("Product '{ProductName}' mapped to package '{PackageName}' ({L}x{W}x{H}cm)", lineItem.ProductName, packageType.Name, length, width, height);
                }
                else
                {
                    // Fallback to weight-based estimation
                    (length, width, height) = EstimateDimensions(itemWeight);
                    description = lineItem.ProductName;

                    _logger.LogWarning("No package type found for product '{ProductName}', using estimated dimensions", lineItem.ProductName);
                }

                parcels.Add(new ParcelDto
                {
                    ParcelCount = lineItem.Quantity,
                    Weight = itemWeight,
                    Length = length,
                    Width = width,
                    Height = height,
                    Packaging = packageType?.Name ?? "Custom parcel",  // PACKAGE TYPE (e.g., "Med White Cake Box")
                    Description = lineItem.ProductName                  // PARCEL CATEGORY (e.g., "Single Tier Cake")
                });
            }
        }

        // Fallback if no line items
        if (!parcels.Any())
        {
            var defaultPackage = await _packageMappingService.GetDefaultPackageTypeAsync(storeId);

            parcels.Add(new ParcelDto
            {
                ParcelCount = 1,
                Weight = defaultPackage?.DefaultWeightKg ?? 0.5m,
                Length = defaultPackage?.LengthCm ?? 25,
                Width = defaultPackage?.WidthCm ?? 20,
                Height = defaultPackage?.HeightCm ?? 10,
                Packaging = defaultPackage?.Name ?? "Custom parcel",
                Description = $"Order #{order.WooOrderNumber}"
            });
        }

        return parcels;
    }

    /// <summary>
    /// Estimate dimensions based on weight (fallback when no package type configured)
    /// </summary>
    private static (int length, int width, int height) EstimateDimensions(decimal weight)
    {
        return weight switch
        {
            <= 1m => (25, 20, 10),
            <= 3m => (30, 25, 15),
            _ => (40, 30, 20)
        };
    }

    private static string GetValidServiceLevel(string? storeServiceLevel)
    {
        if (!string.IsNullOrWhiteSpace(storeServiceLevel) && ValidServiceLevels.Contains(storeServiceLevel))
        {
            return storeServiceLevel.ToUpperInvariant();
        }

        return storeServiceLevel?.ToUpperInvariant() switch
        {
            "ECO" => "STD",
            "STANDARD" => "STD",
            "EXPRESS" => "LSX",
            "OVERNIGHT" => "LOF",
            _ => "STD"
        };
    }

    private static void ValidateStoreConfiguration(Store store)
    {
        var missingFields = new List<string>();

        if (string.IsNullOrWhiteSpace(store.CollectionAddressLine1))
            missingFields.Add("CollectionAddressLine1");
        if (string.IsNullOrWhiteSpace(store.CollectionCity))
            missingFields.Add("CollectionCity");
        if (string.IsNullOrWhiteSpace(store.CollectionPostalCode))
            missingFields.Add("CollectionPostalCode");
        if (string.IsNullOrWhiteSpace(store.CollectionContactName))
            missingFields.Add("CollectionContactName");
        if (string.IsNullOrWhiteSpace(store.CollectionContactPhone))
            missingFields.Add("CollectionContactPhone");
        if (store.ShiplogicAccountId == 0)
            missingFields.Add("ShiplogicAccountId");
        if (store.ShiplogicProviderId == 0)
            missingFields.Add("ShiplogicProviderId");
        if (!store.CollectionLatitude.HasValue || !store.CollectionLongitude.HasValue)
            missingFields.Add("CollectionLatitude/CollectionLongitude");

        if (missingFields.Any())
        {
            throw new InvalidOperationException(
                $"Store '{store.StoreName}' is missing required configuration: {string.Join(", ", missingFields)}.");
        }
    }

    private static AddressDto MapCollectionAddress(Store store)
    {
        // Combine line1 + line2 the same way MapDeliveryAddress already does
        // for orders. Previously line2 (e.g. building name "Stocks Centre")
        // was silently dropped from the Shiplogic payload, leading to driver
        // confusion and failed pickups.
        var street = string.IsNullOrWhiteSpace(store.CollectionAddressLine2)
            ? (store.CollectionAddressLine1 ?? "")
            : $"{store.CollectionAddressLine1} {store.CollectionAddressLine2}".Trim();

        return new AddressDto
        {
            Type = "business",
            Company = store.CollectionCompanyName ?? store.StoreName,
            Street = street,
            // Treat empty/whitespace as null so the DTO's WhenWritingNull
            // rule omits `local_area` from the JSON entirely. Sending an
            // empty `"local_area": ""` makes some Shiplogic accounts reject
            // the payload.
            Suburb = string.IsNullOrWhiteSpace(store.CollectionSuburb) ? null : store.CollectionSuburb,
            City = store.CollectionCity ?? "",
            PostalCode = store.CollectionPostalCode!,
            Country = CountryNormalizer.ToIsoCode(store.CollectionCountry),
            Province = MapProvinceToFullName(store.CollectionProvince),
            Latitude = store.CollectionLatitude,
            Longitude = store.CollectionLongitude
        };
    }

    private static ContactDto MapCollectionContact(Store store)
    {
        return new ContactDto
        {
            Name = store.CollectionContactName!,
            Mobile = store.CollectionContactPhone!,
            Email = string.IsNullOrWhiteSpace(store.CollectionContactEmail) ? null : store.CollectionContactEmail
        };
    }

    private static AddressDto MapDeliveryAddress(Order order)
    {
        return new AddressDto
        {
            Type = "residential",
            Company = null,
            Street = $"{order.ShippingAddressLine1} {order.ShippingAddressLine2}".Trim(),
            // Use the dedicated ShippingSuburb column populated by the
            // WooCommerce plugin (from billing/shipping address_2, the
            // conventional ZA placement). Empty/whitespace becomes null
            // so the DTO drops `local_area` from the JSON entirely
            // instead of sending `"local_area": ""`.
            Suburb = string.IsNullOrWhiteSpace(order.ShippingSuburb) ? null : order.ShippingSuburb,
            City = order.ShippingCity,
            PostalCode = order.ShippingPostalCode,
            Country = CountryNormalizer.ToIsoCode(order.ShippingCountry),
            Province = MapProvinceToFullName(order.ShippingProvince)
        };
    }

    private static string MapProvinceToFullName(string? province)
    {
        if (string.IsNullOrWhiteSpace(province))
            return "Gauteng";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "EC", "Eastern Cape" }, { "FS", "Free State" }, { "GP", "Gauteng" },
            { "KZN", "KwaZulu-Natal" }, { "LP", "Limpopo" }, { "MP", "Mpumalanga" },
            { "NW", "North West" }, { "NC", "Northern Cape" }, { "WC", "Western Cape" }
        };

        return map.TryGetValue(province.Trim(), out var full) ? full : province;
    }

    private static ContactDto MapDeliveryContact(Order order)
    {
        // Shiplogic rejects shipments where the delivery contact mobile is
        // blank. WooCommerce sometimes hands us an order with an empty
        // billing/shipping phone — rather than have the entire booking fail
        // we substitute a clearly-fake placeholder so Shiplogic accepts the
        // request. The courier driver will fall back to the customer's
        // email or the order notes for contact.
        var mobile = string.IsNullOrWhiteSpace(order.CustomerPhone) ? "0000000000" : order.CustomerPhone;
        var name   = string.IsNullOrWhiteSpace(order.CustomerName)  ? "Customer"   : order.CustomerName;
        return new ContactDto
        {
            Name = name,
            Mobile = mobile,
            Email = string.IsNullOrWhiteSpace(order.CustomerEmail) ? null : order.CustomerEmail
        };
    }

}