using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Test double for <see cref="IWooCommerceService"/>. Only
/// <see cref="GetOrderAsync"/> is meaningful — set <see cref="OrderToReturn"/>
/// (null simulates a lookup failure, matching the real service which
/// swallows all errors and returns null). <see cref="GetOrderCallCount"/>
/// lets tests assert whether a REST round-trip actually happened.
/// </summary>
public sealed class FakeWooCommerceService : IWooCommerceService
{
    public WooCommerceOrder? OrderToReturn { get; set; }
    public int GetOrderCallCount { get; private set; }

    public Task<WooCommerceOrder?> GetOrderAsync(int storeId, int wooOrderId)
    {
        GetOrderCallCount++;
        return Task.FromResult(OrderToReturn);
    }

    public Task<WooCommerceConnectionResponse> TestConnectionAsync(int storeId)
        => throw new NotSupportedException();

    public Task<List<WooCommerceOrder>> FetchOrdersAsync(FetchOrdersRequest request)
        => throw new NotSupportedException();

    public Task<bool> UpdateOrderStatusAsync(int storeId, int wooOrderId, string status)
        => throw new NotSupportedException();

    public Task<bool> AddTrackingInfoAsync(int storeId, int wooOrderId, string trackingNumber, string courierName)
        => throw new NotSupportedException();

    public Task<bool> AddOrderNoteAsync(int storeId, int wooOrderId, string note, bool customerNote = false)
        => throw new NotSupportedException();

    public Task<bool> PushShipmentMetaAsync(int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta)
        => throw new NotSupportedException();

    /// <summary>Recorded (wooOrderId, meta, note) tuples from meta pushes.</summary>
    public List<(int WooOrderId, IReadOnlyDictionary<string, string?> Meta, string? Note)> MetaPushes { get; } = new();

    public Task<PushShipmentMetaResult> PushShipmentMetaDetailedAsync(
        int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta,
        string? note = null, bool noteIsCustomerNote = false)
    {
        MetaPushes.Add((wooOrderId, meta, note));
        return Task.FromResult(new PushShipmentMetaResult { Success = true, Path = "fake", KeysSent = meta?.Count ?? 0 });
    }

    public Task<List<WooCommerceOrder>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000)
        => throw new NotSupportedException();
}
