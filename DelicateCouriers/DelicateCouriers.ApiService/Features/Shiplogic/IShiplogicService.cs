using DelicateCouriers.Features.Shiplogic.DTOs;

namespace DelicateCouriers.Features.Shiplogic;

/// <summary>
/// Service interface for interacting with Shiplogic REST API
/// This defines all operations we can perform with Shiplogic courier services
/// </summary>
public interface IShiplogicService
{
    /// <summary>
    /// Create a new shipment booking in Shiplogic
    /// </summary>
    /// <param name="bearerToken">Tenant's Shiplogic bearer token</param>
    /// <param name="request">Shipment details (addresses, contacts, parcels)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created shipment with consignment ID and tracking number</returns>
    Task<CreateShipmentResponse> CreateShipmentAsync(string bearerToken, CreateShipmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve the shipping label PDF for a shipment
    /// </summary>
    /// <param name="bearerToken">Tenant's Shiplogic bearer token</param>
    /// <param name="consignmentId">Shiplogic consignment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PDF label as byte array</returns>
    Task<byte[]> GetLabelAsync(string bearerToken, string consignmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch tracking updates for shipments
    /// </summary>
    /// <param name="bearerToken">Tenant's Shiplogic bearer token</param>
    /// <param name="consignmentIds">Optional list of specific consignment IDs to track</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tracking information with events</returns>
    Task<TrackingResponse> GetTrackingUpdatesAsync(string bearerToken, List<string>? consignmentIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get shipping rate quotes for a delivery
    /// </summary>
    /// <param name="bearerToken">Tenant's Shiplogic bearer token</param>
    /// <param name="collectionAddress">Collection address</param>
    /// <param name="deliveryAddress">Delivery address</param>
    /// <param name="parcels">List of parcels to ship</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available quotes with rates and ETAs</returns>
    Task<RateResponseDto> GetRatesAsync(string bearerToken, AddressDto collectionAddress, AddressDto deliveryAddress, List<ParcelDto> parcels, string? serviceLevelCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GetRatesAsync"/> but also returns raw diagnostic
    /// information (request JSON, HTTP status code, raw response body). Used by
    /// the debug-gated path on the public rates endpoint to surface what
    /// Shiplogic actually returned without leaking internals on normal calls.
    /// </summary>
    Task<(RateResponseDto Rates, ShiplogicDiagnostics Diagnostics)> GetRatesWithDiagnosticsAsync(string bearerToken, AddressDto collectionAddress, AddressDto deliveryAddress, List<ParcelDto> parcels, string? serviceLevelCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel an existing shipment
    /// </summary>
    /// <param name="bearerToken">Tenant's Shiplogic bearer token</param>
    /// <param name="trackingReference">Shiplogic short tracking reference (e.g. "RPLNWM")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cancellation successful</returns>
    Task<bool> CancelShipmentAsync(string bearerToken, string trackingReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve shipping label PDF from Shiplogic
    /// GET /shipments/{shipmentId}/label
    /// </summary>
    /// <param name="bearerToken">Per-tenant Shiplogic bearer token</param>
    /// <param name="shipmentId">Shiplogic shipment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PDF file as byte array</returns>
    Task<byte[]?> GetShipmentLabelAsync(string bearerToken, int shipmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort lookup: does Shiplogic already have a shipment for this
    /// customer reference? Used as a cross-system idempotency guard so a
    /// crash between a successful Shiplogic create and the local DB save
    /// does not produce a duplicate consignment on the next Hangfire retry.
    /// Returns null when no shipment is found OR when the lookup itself
    /// fails (network / 4xx / 5xx) — the caller treats null as "go ahead
    /// and create", since a false negative just means we proceed as we did
    /// before this guard existed.
    /// </summary>
    Task<CreateShipmentResponse?> FindShipmentByCustomerReferenceAsync(string bearerToken, string customerReference, CancellationToken cancellationToken = default);
}