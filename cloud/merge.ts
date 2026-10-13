import { label, numericMap, record, timestamp, invalid } from "./validation";

export interface SaveMeta { sha256: string; size: number; updatedAt: number; deviceId: string; deviceName: string }
export interface AppState {
  achievements: Record<string, number>; stats: Record<string, number>; statsUpdatedAt: number;
  playtime: Record<string, number>; playtimeTotal: number; save: SaveMeta | null;
}
export interface Progress {
  deviceId: string; deviceName: string; achievements: Record<string, number>;
  stats?: Record<string, number>; statsUpdatedAt?: number; playtimeMinutes: number;
}
export function progress(value: unknown): Progress {
  const body = record(value);
  const minutes = body.playtimeMinutes;
  if (typeof minutes !== "number" || !Number.isFinite(minutes) || minutes < 0 || minutes > Number.MAX_SAFE_INTEGER) invalid("Invalid playtime.");
  const result: Progress = {
    deviceId: label(body.deviceId), deviceName: label(body.deviceName),
    achievements: numericMap(body.achievements, true), playtimeMinutes: minutes,
  };
  if (body.stats !== undefined || body.statsUpdatedAt !== undefined) {
    if (body.stats === undefined || body.statsUpdatedAt === undefined) invalid("Provide stats and statsUpdatedAt together.");
    result.stats = numericMap(body.stats);
    result.statsUpdatedAt = timestamp(body.statsUpdatedAt);
  }
  return result;
}
export function emptyState(): AppState {
  return { achievements: {}, stats: {}, statsUpdatedAt: 0, playtime: {}, playtimeTotal: 0, save: null };
}
export function merge(stored: AppState, pushed: Progress): AppState {
  const achievements = new Map(Object.entries(stored.achievements));
  for (const [key, time] of Object.entries(pushed.achievements)) achievements.set(key, Math.min(achievements.get(key) ?? time, time));
  const playtime = new Map(Object.entries(stored.playtime));
  playtime.set(pushed.deviceId, Math.max(playtime.get(pushed.deviceId) ?? 0, pushed.playtimeMinutes));
  const newer = pushed.stats !== undefined && pushed.statsUpdatedAt! > stored.statsUpdatedAt;
  const total = [...playtime.values()].reduce((a, b) => a + b, 0);
  if (!Number.isFinite(total) || total > Number.MAX_SAFE_INTEGER) invalid("Playtime total is too large.");
  return {
    achievements: Object.fromEntries(achievements), stats: newer ? pushed.stats! : stored.stats,
    statsUpdatedAt: newer ? pushed.statsUpdatedAt! : stored.statsUpdatedAt,
    playtime: Object.fromEntries(playtime), playtimeTotal: total, save: stored.save,
  };
}
