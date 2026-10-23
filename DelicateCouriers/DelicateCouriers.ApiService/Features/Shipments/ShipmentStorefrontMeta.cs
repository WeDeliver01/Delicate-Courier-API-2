using System.Globalization;
using DelicateCouriers.Domain.Entities;

namespace DelicateCouriers.ApiService.Features.Shipments;

/// <summary>
/// Builds the <c>_dcp_*</c> meta-data payload we push onto a WooCommerce
/// order so the plugin's "Shipment &amp; Tracking" metabox can render the
/// live shipment state (tracking number, courier, status, courier rate,
/// Shiplogic consignment id, tracking URL). Centralised here so the
/// booking-time push (ShipmentOrchestrationService) and the
/// status-update push (ShiplogicTrackingWebhookController) emit the
/// same keys in the same format.
/// </summary>
public static class ShipmentStorefrontMeta
{
    /// <summary>
    /// Our own public tracking page.
    ///
    /// Was Shiplogic's. A customer following a link to a carrier we no longer use would
    /// have been told their parcel does not exist, which is a worse answer than none.
    /// </summary>
    public const string TrackingUrlFormat =
        "https://delicatecourier.co.za/track?waybill={0}";

    /// <summary>
    /// Returns the customer-friendly label for a shipment status.
    /// so the metabox and the per-event tracking history stay in sync.
    /// </summary>
    public static string StatusLabel(string? status) => status?.ToLowerInvariant() switch
    {
        "submitted" => "Shipment submitted",
        "collection-assigned" => "Collection assigned to driver",
        "out-for-collection" => "Driver out for collection",
        "collected" => "Parcel collected",
        "at-hub" => "Parcel at hub",
        "in-transit" => "Parcel in transit",
        "out-for-delivery" => "Out for delivery",
        "delivered" => "Parcel delivered",
        "failed-collection" => "Collection failed",
        "failed-delivery" => "Delivery failed",
        "cancelled" => "Shipment cancelled",
        null or "" => "Pending",
        _ => char.ToUpperInvariant(status[0]) + status[1..],
    };

    /// <summary>
    /// Assemble the meta-dict to PUT onto the WooCommerce order. Keys that
    /// don't yet have a value are still emitted (as empty strings) so the
    /// plugin metabox can distinguish "not yet pushed" from "explicitly
    /// cleared" — empty strings render as "—" in the panel.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Build(Shipment shipment)
    {
        var tracking = shipment.TrackingNumber ?? string.Empty;
        var trackingUrl = !string.IsNullOrWhiteSpace(tracking)
            ? string.Format(CultureInfo.InvariantCulture, TrackingUrlFormat, Uri.EscapeDataString(tracking))
            : string.Empty;

        var courier = string.IsNullOrWhiteSpace(shipment.CourierName)
            ? "Delicate Courier"
            : shipment.CourierName;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["_dcp_tracking_number"] = tracking,
            ["_dcp_consignment_id"] = shipment.ConsignmentID ?? string.Empty,
            ["_dcp_courier_name"] = courier,
            ["_dcp_shipment_status"] = shipment.ShipmentStatus ?? string.Empty,
            ["_dcp_shipment_status_label"] = StatusLabel(shipment.ShipmentStatus),
            ["_dcp_courier_rate"] = shipment.ShippingCost.ToString("F2", CultureInfo.InvariantCulture),
            ["_dcp_tracking_url"] = trackingUrl,
            ["_dcp_shipment_id"] = shipment.ShipmentID.ToString(CultureInfo.InvariantCulture),
            ["_dcp_synced_at"] = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
        };
    }
}
