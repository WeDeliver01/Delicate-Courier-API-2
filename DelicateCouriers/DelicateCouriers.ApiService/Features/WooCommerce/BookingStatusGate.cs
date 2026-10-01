using System;
using System.Linq;
using DelicateCouriers.Domain.Entities;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Per-store order-status gate for automatic shipment booking.
    ///
    /// <see cref="Store.BookingTriggerStatuses"/> (comma-separated, e.g.
    /// "completed") restricts which WooCommerce statuses may enqueue a
    /// booking for that store only. NULL/empty keeps the platform default
    /// (book on any non-terminal status), so no other store or tenant is
    /// affected by one store's override.
    ///
    /// Used by both webhook intake paths (custom plugin + standard Woo
    /// REST webhooks) so the rule holds no matter how the order arrives.
    /// </summary>
    public static class BookingStatusGate
    {
        /// <summary>
        /// Returns true when the (already normalised: trimmed, lowercase)
        /// order status may trigger an automatic booking for this store.
        /// <paramref name="allowedForLogging"/> carries the store's
        /// configured list for log messages.
        /// </summary>
        public static bool IsAllowed(Store store, string? normalisedStatus, out string allowedForLogging)
        {
            allowedForLogging = string.Empty;

            if (string.IsNullOrWhiteSpace(store.BookingTriggerStatuses))
            {
                return true; // no override — platform default applies
            }

            var allowed = store.BookingTriggerStatuses
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToArray();

            allowedForLogging = string.Join(", ", allowed);

            if (allowed.Length == 0)
            {
                return true; // malformed config (only commas/whitespace) — fail open to default
            }

            // Blank/unknown status cannot match an explicit allow-list.
            return !string.IsNullOrEmpty(normalisedStatus) && allowed.Contains(normalisedStatus);
        }
    }
}
