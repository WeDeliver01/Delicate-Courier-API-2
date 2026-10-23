namespace DelicateCouriers.ApiService.Features.Shiplogic.DTOs
{
    /// <summary>
    /// Summary of an order for selection/display
    /// </summary>
    public class OrderSummaryDto
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public decimal OrderTotal { get; set; }
        public DateTime CreatedOn { get; set; }
    }
}
