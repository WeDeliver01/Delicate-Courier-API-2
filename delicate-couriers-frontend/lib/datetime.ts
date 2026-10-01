// Shared date/time formatters. Everything in the app renders timestamps
// in the tenant's local timezone (South Africa / Africa/Johannesburg) so
// operators don't have to mentally translate UTC. The full UTC ISO string
// is exposed via `isoTitle` for tooltip use, which support can paste into
// log queries.

const TZ = 'Africa/Johannesburg';

const dateTimeFmt = new Intl.DateTimeFormat('en-ZA', {
  timeZone: TZ,
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

const dateFmt = new Intl.DateTimeFormat('en-ZA', {
  timeZone: TZ,
  day: '2-digit',
  month: 'short',
  year: 'numeric',
});

const dateLongFmt = new Intl.DateTimeFormat('en-ZA', {
  timeZone: TZ,
  weekday: 'short',
  day: '2-digit',
  month: 'short',
  year: 'numeric',
});

function parse(value: string | Date | null | undefined): Date | null {
  if (value == null) return null;
  const d = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(d.getTime())) return null;
  return d;
}

/** "20 May 2026, 14:32" in Africa/Johannesburg, or `fallback` when unparseable. */
export function formatDateTime(value: string | Date | null | undefined, fallback = '—'): string {
  const d = parse(value);
  if (!d) return fallback;
  // en-ZA renders as "20 May 2026, 14:32" — join with a comma for consistency
  // across browsers (some omit it, some include it).
  const formatted = dateTimeFmt.format(d);
  return formatted.replace(',', ',').replace(/\s+/g, ' ').trim();
}

/** "20 May 2026" date only in Africa/Johannesburg. */
export function formatDate(value: string | Date | null | undefined, fallback = '—'): string {
  const d = parse(value);
  if (!d) return fallback;
  return dateFmt.format(d);
}

/** "Wed, 20 May 2026" — used on shipment detail schedule rows. */
export function formatDateLong(value: string | Date | null | undefined, fallback = '—'): string {
  const d = parse(value);
  if (!d) return fallback;
  return dateLongFmt.format(d);
}

/**
 * Date + time when the value carries a meaningful (non-midnight) time-of-day
 * component in Africa/Johannesburg; otherwise date-only. Useful for fields
 * like `requestedDeliveryDate` where a user may have picked a delivery date
 * with or without a specific time slot — we don't want to render a misleading
 * "00:00" against a date the user only set as a calendar day.
 */
export function formatDateMaybeTime(value: string | Date | null | undefined, fallback = '—'): string {
  const d = parse(value);
  if (!d) return fallback;
  // Inspect hours/minutes *in the display timezone*, not UTC — a value
  // stored as 22:00Z is 00:00 Africa/Johannesburg the next day and should
  // be treated as a date-only entry.
  const parts = dateTimeFmt.formatToParts(d);
  const hour = Number(parts.find((p) => p.type === 'hour')?.value ?? '0');
  const minute = Number(parts.find((p) => p.type === 'minute')?.value ?? '0');
  return hour === 0 && minute === 0 ? dateFmt.format(d) : dateTimeFmt.format(d);
}

/** Full UTC ISO string, intended for the `title` attribute (hover tooltip). */
export function isoTitle(value: string | Date | null | undefined): string {
  const d = parse(value);
  if (!d) return '';
  return `${d.toISOString()} (UTC)`;
}
