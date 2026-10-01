using DelicateCouriers.ApiService.Infrastructure.Services;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace DelicateCouriers.ApiService.Infrastructure.Filters;

/// <summary>
/// Hangfire state-election filter that records a SystemEvent every time a
/// background job transitions into the Failed state. This gives SuperAdmin
/// visibility into job exhaustion (the silent-hang class of issue that
/// was previously only visible by trawling Hangfire's UI).
/// </summary>
public class HangfireSystemEventFilter : JobFilterAttribute, IApplyStateFilter
{
    private readonly IServiceProvider _serviceProvider;

    public HangfireSystemEventFilter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is not FailedState failed) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var logger = scope.ServiceProvider.GetService<ISystemEventLogger>();
            if (logger == null) return;

            var jobMethod = $"{context.BackgroundJob.Job?.Type?.Name}.{context.BackgroundJob.Job?.Method?.Name}";

            // Fire-and-forget; logger swallows its own errors.
            _ = logger.LogAsync(new SystemEventEntry
            {
                EventType = "job.failed",
                ActorKind = "Job",
                ActorLabel = "Hangfire",
                EntityType = "HangfireJob",
                EntityRef = context.BackgroundJob.Id,
                Message = $"Background job {jobMethod} failed: {failed.Exception?.Message ?? "unknown error"}",
                Details = new
                {
                    jobId = context.BackgroundJob.Id,
                    jobMethod,
                    exceptionType = failed.Exception?.GetType().FullName,
                    exceptionMessage = failed.Exception?.Message,
                    failedAt = failed.FailedAt,
                },
            });
        }
        catch
        {
            // Never bubble — observability must not break Hangfire state transitions.
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op.
    }
}
