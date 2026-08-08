/**
 * Every date and number is formatted here, on the client, in the resolved locale.
 *
 * The server returns ISO-8601 UTC and raw numbers and never formats anything for display. That one
 * rule removes a whole category of bug and makes "hace 3 días" free.
 */

const relativeFormatters = new Map<string, Intl.RelativeTimeFormat>();
const dateFormatters = new Map<string, Intl.DateTimeFormat>();

const DIVISIONS: { amount: number; unit: Intl.RelativeTimeFormatUnit }[] = [
  { amount: 60, unit: 'second' },
  { amount: 60, unit: 'minute' },
  { amount: 24, unit: 'hour' },
  { amount: 7, unit: 'day' },
  { amount: 4.34524, unit: 'week' },
  { amount: 12, unit: 'month' },
  { amount: Number.POSITIVE_INFINITY, unit: 'year' },
];

export function formatRelative(iso: string, locale: string): string {
  const formatter = relativeFormatters.get(locale)
    ?? new Intl.RelativeTimeFormat(locale, { numeric: 'auto', style: 'long' });
  relativeFormatters.set(locale, formatter);

  let duration = (new Date(iso).getTime() - Date.now()) / 1000;

  for (const division of DIVISIONS) {
    if (Math.abs(duration) < division.amount) {
      return formatter.format(Math.round(duration), division.unit);
    }
    duration /= division.amount;
  }

  return formatDate(iso, locale);
}

export function formatDate(iso: string, locale: string): string {
  const formatter = dateFormatters.get(locale)
    ?? new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
  dateFormatters.set(locale, formatter);

  return formatter.format(new Date(iso));
}

export function formatBytes(bytes: number, locale: string): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value)} ${units[unit]}`;
}

export function formatNumber(value: number, locale: string): string {
  return new Intl.NumberFormat(locale).format(value);
}
