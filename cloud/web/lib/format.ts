export const PAIRING_TTL = 15 * 60 * 1000;
const NOW_WINDOW = 3 * 60 * 1000;

export const artUrl = (appid: string) => `https://cdn.cloudflare.steamstatic.com/steam/apps/${appid}/header.jpg`;
export const appName = (appid: string, name?: string | null) => name || `App ${appid}`;

export function bytes(value: number, locale = "en"): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let index = 0;
  // Binary steps, labelled the way Windows and Steam do (10 GiB reads "10 GB").
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  const digits = index === 0 || value >= 100 ? 0 : 1;
  return `${value.toLocaleString(locale, { maximumFractionDigits: digits })} ${units[index]}`;
}
export const count = (value: number, locale = "en") => value.toLocaleString(locale);
export const hours = (minutes: number, locale = "en") =>
  minutes < 60 && minutes > 0 ? `${minutes} min` : `${Math.round(minutes / 60).toLocaleString(locale)} h`;
export const plural = (n: number, one: string, many: string, locale = "en") => `${count(n, locale)} ${n === 1 ? one : many}`;

export function ago(time: number | null, locale = "en", labels = { now: "Now", never: "Never" }): string {
  if (!time) return labels.never;
  const elapsed = Date.now() - time;
  if (elapsed < NOW_WINDOW) return labels.now;
  const format = new Intl.RelativeTimeFormat(locale, { numeric: "auto" });
  const steps: [Intl.RelativeTimeFormatUnit, number][] = [["minute", 60], ["hour", 24], ["day", 7], ["week", 4.35], ["month", 12], ["year", Infinity]];
  let value = elapsed / 60_000;
  for (const [unit, size] of steps) {
    if (Math.abs(value) < size) return format.format(-Math.round(value), unit);
    value /= size;
  }
  return format.format(-Math.round(value), "year");
}
export const shortDate = (time: number, locale = "en-GB") =>
  new Date(time).toLocaleDateString(locale, { day: "numeric", month: "short" });

const AVATARS = ["blue-600", "orange-400", "green-400", "purple-400", "cyan-400", "magenta-400", "yellow-600", "red-400"];
export function avatarColor(seed: string, muted = false): string {
  if (muted) return "var(--onyx-palette-base-700)";
  let hash = 0;
  for (const char of seed) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return `var(--onyx-palette-${AVATARS[hash % AVATARS.length]})`;
}
