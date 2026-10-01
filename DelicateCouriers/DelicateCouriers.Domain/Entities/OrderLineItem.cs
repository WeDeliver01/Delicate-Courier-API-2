namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a single line item (product) in a WooCommerce order
/// Each order can have multiple line items
/// Example: Order #123 contains 2x Blue T-Shirt (size M) + 1x Red Cap
/// </summary>
public class OrderLineItem
{
    /// <summary>
    /// Unique identifier for this line item
    /// </summary>
    public int OrderLineItemID { get; set; }

    /// <summary>
    /// Foreign key to the parent order
    /// </summary>
    public int OrderID { get; set; }

    /// <summary>
    /// Line item ID from WooCommerce
    /// </summary>
    public int WooLineItemID { get; set; }

    /// <summary>
    /// Product name
    /// Example: "Organic Honey - 500ml"
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// Product SKU (Stock Keeping Unit)
    /// Example: "HON-ORG-500"
    /// </summary>
    public string? ProductSKU { get; set; }

    /// <summary>
    /// WooCommerce product ID
    /// </summary>
    public int? WooProductID { get; set; }

    /// <summary>
    /// Quantity ordered
    /// Example: 3 (customer ordered 3 jars)
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Weight per unit in kilograms (CRITICAL for shipping)
    /// Example: 0.6 (each jar weighs 600 grams)
    /// This comes from WooCommerce product weight field
    /// </summary>
    public decimal? WeightPerUnit { get; set; }

    /// <summary>
    /// Total weight for this line item (WeightPerUnit × Quantity)
    /// Example: 1.8kg (3 jars × 0.6kg each)
    /// </summary>
    public decimal? TotalWeight { get; set; }

    /// <summary>
    /// Price per unit (before quantity multiplication)
    /// Example: 89.99
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Total price for this line item (UnitPrice × Quantity)
    /// Example: 269.97 (3 × 89.99)
    /// </summary>
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Tax amount for this line item
    /// </summary>
    public decimal TaxAmount { get; set; }

    /// <summary>
    /// Product variation details (e.g., "Size: Large, Color: Blue")
    /// Stored as JSON string from WooCommerce
    /// </summary>
    public string? VariationDetails { get; set; }

    /// <summary>
    /// When this line item was created in our system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who/what created this line item (usually "System" from webhook)
    /// </summary>
    public string CreatedBy { get; set; } = "System";

    // Navigation property - link back to parent order
    public Order Order { get; set; } = null!;
}