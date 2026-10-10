using System.Text.Json;
using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Merchants;
using Hangfire;

namespace DelicateCouriers.Integration.Jobs;

/// <summary>
/// Booking one order, retryably.
///
/// The retry schedule is the important part and it is deliberate: ten attempts spread across
/// roughly twenty-four hours. A courier platform that gives up after five minutes turns a
/// deploy or a brief outage into orders that were accepted and never booked, and the merchant
/// finds out from the customer.
///
/// What makes a day of retries safe rather than dangerous is the idempotency key. Every
/// attempt sends the same <c>WC-{wooOrderId}</c>, so the Engine settles duplicates at the
/// point where the booking is created rather than leaving ten attempts to race.
/// </summary>
public sealed class BookOrderJob
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IMerchantDirectory _merchants;
    private readonly OrderBookingService _bookings;
    private readonly ILogger<BookOrderJob> _logger;

    public BookOrderJob(
        IMerchantDirectory merchants,
        OrderBookingService bookings,
        ILogger<BookOrderJob> logger)
    {
        _merchants = merchants;
        _bookings = bookings;
        _logger = logger;
    }

    /// <summary>
    /// Attempt the booking.
    ///
    /// Returning means "do not try again", and it is the right answer for everything we have
    /// decided about: booked, already booked, skipped by the rules, or refused for a reason
    /// that will not change. Throwing means "try again", and is reserved for the Engine being
    /// briefly unreachable — which is the only failure another attempt can fix.
    /// </summary>
    [AutomaticRetry(
        Attempts = 10,
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        DelaysInSeconds = new[] { 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200, 14400 })]
    public async Task RunAsync(int storeId, string rawPayload, CancellationToken cancellationToken)
    {
        var merchant = _merchants.Find(storeId);
        if (merchant is null)
        {
            // The store was removed between the webhook and this attempt. Nothing to retry
            // towards, and throwing would only fill the failed queue with something nobody
            // can act on.
            _logger.LogWarning("Dropping queued order for unknown store {StoreId}", storeId);
            return;
        }

        PluginOrderPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PluginOrderPayload>(rawPayload, Json);
        }
        catch (JsonException ex)
        {
            // It was valid JSON when the webhook accepted it, so this is a bug rather than a
            // bad caller — but retrying the same bytes ten times will not fix it.
            _logger.LogError(ex, "Dropping queued order for store {StoreId}: payload no longer parses", storeId);
            return;
        }

        if (payload is null)
        {
            _logger.LogError("Dropping queued order for store {StoreId}: empty payload", storeId);
            return;
        }

        var result = await _bookings.BookAsync(merchant, payload, cancellationToken);

        _logger.LogInformation(
            "Booking job finished for WooOrderID {WooOrderId}: {Outcome} ({Reason}) reference={Reference}",
            payload.WooOrderId, result.Outcome, result.Reason, result.BookingReference);

        // Anything that reaches here is settled. A transient failure never gets this far —
        // BookAsync lets it escape precisely so this method throws and Hangfire reschedules.
    }
}
