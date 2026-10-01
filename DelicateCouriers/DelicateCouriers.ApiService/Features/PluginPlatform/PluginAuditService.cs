using System.Text.Json;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>Append-only audit trail for plugin-platform actions (audit_logs table).</summary>
public class PluginAuditService
{
    private readonly AppDbContext _context;
    private readonly ILogger<PluginAuditService> _logger;

    public PluginAuditService(AppDbContext context, ILogger<PluginAuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Write an audit row as part of the caller's next SaveChanges (call
    /// SaveAsync to flush immediately). Never throws — auditing must not
    /// break the underlying operation.
    /// </summary>
    public void Append(string action, string actor, string? entityType = null, string? entityRef = null, object? details = null, string? ip = null)
    {
        try
        {
            _context.PluginAuditLogs.Add(new PluginAuditLog
            {
                Action = action,
                Actor = Truncate(actor, 255),
                EntityType = entityType,
                EntityRef = Truncate(entityRef, 255),
                DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
                IpAddress = Truncate(ip, 45),
                CreatedOn = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin audit append failed for action {Action}", action);
        }
    }

    private static string? Truncate(string? v, int max)
        => v is null ? null : (v.Length <= max ? v : v[..max]);
}
