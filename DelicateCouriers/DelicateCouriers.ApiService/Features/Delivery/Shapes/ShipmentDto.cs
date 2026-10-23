namespace DelicateCouriers.ApiService.Features.Shiplogic.DTOs
{
    /// <summary>
    /// Result for shipment list query
    /// </summary>
    public class ShipmentListResult
    {
        public List<ShipmentDto> Shipments { get; set; } = new();
        public int Total { get; set; }
    }

    /// <summary>
    /// DTO for shipment data
    /// </summary>
    public class ShipmentDto
    {
        public int ShipmentId { get; set; }
        public int OrderId { get; set; }
        public string TrackingNumber { get; set; } = string.Empty;
        public string ConsignmentId { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public string CourierName { get; set; } = string.Empty;
        public string CourierService { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal ShippingCost { get; set; }
        public DateTime? EstimatedDeliveryDate { get; set; }
        public DateTime CreatedOn { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result for dashboard stats
    /// </summary>
    public class ShipmentStatsResult
    {
        public int Total { get; set; }
        public int Pending { get; set; }
        public int InTransit { get; set; }
        public int Delivered { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// Detailed shipment information
    /// </summary>
    public class ShipmentDetailDto
    {
        public int ShipmentId { get; set; }
        public int OrderId { get; set; }
        public string TrackingNumber { get; set; } = string.Empty;
        public string ConsignmentId { get; set; } = string.Empty;
        public string CourierName { get; set; } = string.Empty;
        public string CourierService { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal ShippingCost { get; set; }
        public DateTime? EstimatedDeliveryDate { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }
        // Requested delivery/collection schedule
        public DateTime? RequestedDeliveryDate { get; set; }
        public string? RequestedDeliveryTime { get; set; }
        public DateTime? RequestedCollectionDate { get; set; }
        public string? RequestedCollectionTime { get; set; }
        public string? Occasion { get; set; }
        public DateTime CreatedOn { get; set; }

        // Order info
        public string OrderNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;

        // Address
        public AddressDto DeliveryAddress { get; set; } = new();

        // Tenant/Store
        public string TenantName { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;

        // Label
        public bool HasLabel { get; set; }
        public string? LabelUrl { get; set; }

        // Tracking
        public List<TrackingEventDto> TrackingEvents { get; set; } = new();
    }

    public class AddressDto
    {
        public string Line1 { get; set; } = string.Empty;
        public string? Line2 { get; set; }
        public string City { get; set; } = string.Empty;
        public string? Province { get; set; }
        public string PostalCode { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }

    public class TrackingEventDto
    {
        public DateTime EventDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Location { get; set; }
        public string? Description { get; set; }
    }
}
