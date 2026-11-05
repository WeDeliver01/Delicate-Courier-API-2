using System.Globalization;
using DelicateCouriers.Integration.Engine;

namespace DelicateCouriers.Integration.Bookings;

/// <summary>
/// Turns the delivery date and time a WooCommerce order carries into the slot the Engine books.
///
/// This exists because we were dropping both. The order has had them all along -- the shopper
/// picked them at checkout -- and the booking request was built without ever looking, so every
/// Standard order was refused by the Engine for having no slot, and the only thing anybody saw
/// was "request failed validation".
///
/// Both values arrive as free text from whichever checkout plugin the merchant uses, so the
/// parsing is deliberately forgiving: the Delicate two-step checkout, WP Time Slots Booking and
/// WAPF each write a different shape, and a strict parser here means a dropped order rather
/// than a loud error.
/// </summary>
public static class DeliverySlotMapper
{
    /// <summary>
    /// The slot to send, or null when the order carries no usable date.
    ///
    /// Null is a real answer, not a failure: an on-demand service level needs no slot, and the
    /// Engine refuses a Standard booking without one in terms that name the missing field.
    /// Inventing a date here would book somebody's cake for a day nobody chose.
    /// </summary>
    public static EngineSlot? FromOrder(string? deliveryDate, string? deliveryTime)
    {
        var date = ParseDate(deliveryDate);
        if (date is null) return null;

        // The time is optional on purpose. A date with no time still books: the Engine picks
        // the window, which is better than refusing an order we could have delivered.
        return new EngineSlot { Date = date, Time = ParseStartTime(deliveryTime) };
    }

    /// <summary>
    /// Normalise to `YYYY-MM-DD`.
    ///
    /// Day-first is assumed for slash and dash formats, because this is South Africa and
    /// 06/11 means the sixth of November. Getting that backwards would deliver five months
    /// late, so ISO is tried first and the ambiguous forms are read with an explicit
    /// day-first culture rather than the machine's locale, which is not ours to depend on.
    /// </summary>
    public static string? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        string[] formats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy",
            "dd-MM-yy", "dd/MM/yy", "d-M-yy", "d/M/yy",
            "d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "dd MMM yyyy"
        };
        var za = new CultureInfo("en-ZA");
        if (DateTime.TryParseExact(text, formats, za, DateTimeStyles.None, out var exact))
        {
            return exact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // A date with a time stuck on the end, or some shape the list above missed. Still
        // day-first: a loose parse under the machine's own culture is how 06/11 becomes June.
        if (DateTime.TryParse(text, za, DateTimeStyles.None, out var loose))
        {
            return loose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>
    /// The start of the chosen window, as `HH:MM`.
    ///
    /// Time slots arrive as ranges far more often than as instants -- "09:00 - 11:00", "9am to
    /// 11am" -- and the start is the part that matters: it is when the shopper was told we
    /// would be there, and it is what decides which Engine window the order lands in.
    /// </summary>
    public static string? ParseStartTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        foreach (var separator in new[] { " - ", " – ", " — ", " to ", "-", "–", "—" })
        {
            var at = text.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            // Only split when something precedes it, so a stray leading dash is not a split.
            if (at > 0)
            {
                text = text[..at].Trim();
                break;
            }
        }
        return ToTwentyFourHour(text);
    }

    private static string? ToTwentyFourHour(string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        var invariant = CultureInfo.InvariantCulture;

        // "9am" and "9 am" are not times to DateTime.TryParse; "9:00 am" is.
        var padded = time.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(
            padded, @"^(\d{1,2})\s*([ap]\.?m\.?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            padded = $"{match.Groups[1].Value}:00 {match.Groups[2].Value}";
        }

        if (DateTime.TryParse(padded, invariant, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("HH:mm", invariant);
        }
        if (TimeSpan.TryParse(padded, invariant, out var span) && span < TimeSpan.FromDays(1))
        {
            return $"{span.Hours:D2}:{span.Minutes:D2}";
        }
        return null;
    }
}
