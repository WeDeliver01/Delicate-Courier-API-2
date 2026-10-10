using Hangfire;

namespace DelicateCouriers.Integration.Jobs;

/// <summary>
/// Where an accepted order waits to be booked.
///
/// The webhook must answer quickly — a shopper's checkout is on the other end of it — and
/// booking is a call to another system that may be slow or briefly down. Those two facts are
/// irreconcilable in one request, so the webhook's job ends at "we have this, signed and
/// valid", and the booking happens behind it.
///
/// The queue has to be durable. An in-memory one loses every pending order on a restart, and
/// an order the plugin was told we accepted and then never booked is invisible: nobody is
/// waiting for it, nothing alarms, and it surfaces weeks later as a customer asking where
/// their cake is.
/// </summary>
public interface IOrderQueue
{
    /// <summary>
    /// Queue a signed, validated payload for booking. The raw body is stored with the job, so
    /// a restart loses nothing and the job books exactly what the merchant sent.
    /// </summary>
    void Enqueue(int storeId, string rawPayload);
}

public sealed class HangfireOrderQueue : IOrderQueue
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireOrderQueue(IBackgroundJobClient jobs) => _jobs = jobs;

    public void Enqueue(int storeId, string rawPayload) =>
        _jobs.Enqueue<BookOrderJob>(job => job.RunAsync(storeId, rawPayload, CancellationToken.None));
}
