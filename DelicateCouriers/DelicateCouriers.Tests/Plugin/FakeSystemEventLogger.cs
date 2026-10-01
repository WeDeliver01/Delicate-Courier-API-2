using DelicateCouriers.ApiService.Infrastructure.Services;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// No-op <see cref="ISystemEventLogger"/> for tests. The real logger spins up
/// its own scoped DbContext via IServiceScopeFactory which isn't available in
/// these in-process controller tests; observability is best-effort so swapping
/// in a recording stub keeps the controllers under test without side effects.
/// </summary>
public sealed class FakeSystemEventLogger : ISystemEventLogger
{
    public List<SystemEventEntry> Events { get; } = new();

    public Task LogAsync(SystemEventEntry entry, CancellationToken ct = default)
    {
        Events.Add(entry);
        return Task.CompletedTask;
    }
}
