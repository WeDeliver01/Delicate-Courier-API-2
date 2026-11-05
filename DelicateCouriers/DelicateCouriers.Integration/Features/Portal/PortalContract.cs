using System.Text.Json.Serialization;

namespace DelicateCouriers.Integration.Features.Portal;

/// <summary>
/// What the portal sends, in the portal's own terms.
///
/// Shaped after the ShipLogic request it replaces — collection and delivery, each an address
/// and a contact, plus parcels — so swapping the backend is a change of service, not a rewrite
/// of everything that calls it. <c>OrderToShipmentMapper</c> on the portal side keeps building
/// roughly what it always built.
///
/// The Engine's own vocabulary stays on this side of the wire. The portal asking for a
/// delivery should not have to know that the Engine prices a loop of drops, catalogues package
/// types, or wants coordinates on every address — those are this service's problem, and
/// teaching them to a second codebase would mean two places to fix when the Engine changes.
/// </summary>
public sealed record PortalDeliveryRequest
{
    /// <summary>The merchant's own identifier, which the Engine resolves to an account.</summary>
    [JsonPropertyName("accountRef")] public string AccountRef { get; init; } = string.Empty;

    /// <summary>
    /// The portal's reference for this job, used to recognise a repeat.
    ///
    /// Also the idempotency key unless one is given separately: sent twice, the Engine returns
    /// the booking it already made rather than sending a second driver.
    /// </summary>
    [JsonPropertyName("customerReference")] public string CustomerReference { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; init; }

    /// <summary>Which service the customer is paying for. Defaults to the standard one.</summary>
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = "standard";

    [JsonPropertyName("collection")] public PortalStop Collection { get; init; } = new();
    [JsonPropertyName("delivery")] public PortalStop Delivery { get; init; } = new();
    [JsonPropertyName("parcels")] public List<PortalParcel> Parcels { get; init; } = new();

    /// <summary>
    /// Refuse rather than book above this, in cents.
    ///
    /// Null means no ceiling, which is right when an operator is booking by hand and has
    /// already agreed the price. It is not right when a customer was quoted at a checkout.
    /// </summary>
    [JsonPropertyName("maxTotalCents")] public long? MaxTotalCents { get; init; }

    /// <summary>
    /// The day and time the customer chose, which a slotted service level needs.
    ///
    /// Standard and Express are booked into a window. Leaving this out is what made every
    /// Woo order fail: the Engine was sent no slot and refused, correctly, and the only thing
    /// visible downstream was "request failed validation".
    /// </summary>
    [JsonPropertyName("slot")] public PortalSlot? Slot { get; init; }
}

/// <summary>
/// When the customer wants it. `date` is `YYYY-MM-DD`; `time` is `HH:MM` on a 24-hour clock
/// and is the START of the window they were promised.
///
/// No window key: the Engine owns its window names and resolves the time against them, so
/// nothing on this side has to learn them or be changed when it adds one.
/// </summary>
public sealed record PortalSlot
{
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("time")] public string? Time { get; init; }
}

/// <summary>One end of the job: where, and who to ask for.</summary>
public sealed record PortalStop
{
    /// <summary>
    /// The address as one line. Used to geocode when no coordinates are supplied, so the
    /// portal does not need a maps key of its own.
    /// </summary>
    [JsonPropertyName("formatted")] public string Formatted { get; init; } = string.Empty;

    [JsonPropertyName("line1")] public string? Line1 { get; init; }
    [JsonPropertyName("suburb")] public string? Suburb { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; init; }
    [JsonPropertyName("country")] public string Country { get; init; } = "ZA";

    /// <summary>
    /// Supply these when they are known and trusted — a merchant's own premises, say.
    /// Leaving them null has this service geocode the address instead, which is a network
    /// call and can fail; sending a wrong pin is worse, because it prices silently.
    /// </summary>
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }

    [JsonPropertyName("contactName")] public string ContactName { get; init; } = string.Empty;
    [JsonPropertyName("contactPhone")] public string ContactPhone { get; init; } = string.Empty;
    [JsonPropertyName("contactEmail")] public string? ContactEmail { get; init; }
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }

    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}

public sealed record PortalParcel
{
    /// <summary>
    /// The Engine catalogue entry this is priced as.
    ///
    /// Required, and not derivable from dimensions: the Engine prices by catalogued type, and
    /// nothing in a length and a weight says whether a box of cupcakes is handled like a box
    /// of books.
    /// </summary>
    [JsonPropertyName("packageTypeId")] public string PackageTypeId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;
    [JsonPropertyName("weightKg")] public decimal? WeightKg { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>
/// What came back, in terms the portal already stores.
///
/// <see cref="ConsignmentId"/> and <see cref="TrackingNumber"/> are named for the columns they
/// land in rather than for the Engine's words, because the portal's database has held those
/// names since it was a ShipLogic client and renaming them buys nothing.
/// </summary>
public sealed record PortalDeliveryResult
{
    [JsonPropertyName("consignmentId")] public string ConsignmentId { get; init; } = string.Empty;
    [JsonPropertyName("trackingNumber")] public string TrackingNumber { get; init; } = string.Empty;
    [JsonPropertyName("reference")] public string Reference { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }

    /// <summary>
    /// Who is carrying it.
    ///
    /// Always us now. The portal's CourierName column exists because shipments used to be
    /// resold through a third party whose branch did the collecting. After the decoupling
    /// there is no third party, and saying so plainly beats leaving a column that quietly
    /// means something it no longer means.
    /// </summary>
    [JsonPropertyName("courierName")] public string CourierName { get; init; } = "Delicate Courier";

    /// <summary>The service actually booked. The portal stores it as CourierService.</summary>
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = string.Empty;

    /// <summary>
    /// True when the Engine recognised this as something it had already booked.
    ///
    /// Worth surfacing rather than hiding: a caller that retried after a timeout wants to know
    /// it did not just book a second collection.
    /// </summary>
    [JsonPropertyName("replayed")] public bool Replayed { get; init; }
}

/// <summary>A price, without committing to it.</summary>
public sealed record PortalQuoteResult
{
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "ZAR";
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = string.Empty;
}
