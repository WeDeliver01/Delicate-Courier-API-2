using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

public class ShippingRateDto
{
    // Shiplogic /v2/rates returns service-level fields nested inside a
    // `service_level` object (id, code, name, description, dates...), not
    // as flat top-level properties. The previous flat mappings silently
    // produced null/0 values which caused the controller's STD filter to
    // discard every rate. We deserialize the nested object and expose the
    // most commonly needed fields as flat helpers so callers don't have to
    // change.
    [JsonPropertyName("service_level")]
    public ServiceLevelDto? ServiceLevel { get; set; }

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("estimated_delivery_days")]
    public int? EstimatedDeliveryDays { get; set; }

    [JsonPropertyName("collection_branch_id")]
    public int? CollectionBranchId { get; set; }

    [JsonPropertyName("collection_branch_name")]
    public string? CollectionBranchName { get; set; }

    [JsonPropertyName("delivery_branch_id")]
    public int? DeliveryBranchId { get; set; }

    [JsonPropertyName("delivery_branch_name")]
    public string? DeliveryBranchName { get; set; }

    // Flat helpers — read from nested service_level. Existing controller
    // code uses these names; keeping them stable avoids a wider refactor.
    [JsonIgnore]
    public int ServiceLevelId => ServiceLevel?.Id ?? 0;

    [JsonIgnore]
    public string? ServiceLevelCode => ServiceLevel?.Code;

    [JsonIgnore]
    public string? ServiceLevelName => ServiceLevel?.Name;

    [JsonIgnore]
    public string Courier => CollectionBranchName ?? "Unknown";

    [JsonIgnore]
    public DateTime? EstimatedDeliveryDate => EstimatedDeliveryDays.HasValue ? DateTime.UtcNow.AddDays(EstimatedDeliveryDays.Value) : null;
}

public class ServiceLevelDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("delivery_date_from")]
    public DateTimeOffset? DeliveryDateFrom { get; set; }

    [JsonPropertyName("delivery_date_to")]
    public DateTimeOffset? DeliveryDateTo { get; set; }

    [JsonPropertyName("collection_date")]
    public DateTimeOffset? CollectionDate { get; set; }
}
