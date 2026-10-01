namespace DelicateCouriers.ApiService.Features.WooCommerce.DTOs
{
    /// <summary>
    /// Request received from WooCommerce webhook
    /// Contains headers and payload
    /// </summary>
    public class WebhookRequest
    {
        public string Signature { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response sent back to WooCommerce after processing webhook
    /// </summary>
    public class WebhookResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? OrderId { get; set; }
        public string? OrderNumber { get; set; }
    }

    /// <summary>
    /// Parsed order data from webhook for storage
    /// Maps WooCommerce order to our Order entity
    /// </summary>
    public class ParsedWebhookOrder
    {
        public int WooCommerceOrderId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }

        // Customer shipping info
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;
        public string ShippingAddress { get; set; } = string.Empty;
        public string ShippingCity { get; set; } = string.Empty;
        public string ShippingProvince { get; set; } = string.Empty;
        public string ShippingPostalCode { get; set; } = string.Empty;
        public string ShippingCountry { get; set; } = string.Empty;

        // Line items summary
        public int TotalItems { get; set; }
        public string ItemsSummary { get; set; } = string.Empty;
    }
}