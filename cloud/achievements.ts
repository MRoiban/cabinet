import { eq, detached, type DetachedRouteDefinition } from "bedrock";
import { creator } from "./auth";
import { appState } from "./api";
import { namesFor } from "./names";
import { achievementSchemas, users, type SchemaAchievement } from "./schema";

const FRESH = 7 * 24 * 60 * 60 * 1000;
const MAX_ACHIEVEMENTS = 5000;
const ICON = /^[0-9a-f]{40}\.(jpg|png)$/;
const text = (value: unknown, max: number) => typeof value === "string" ? value.slice(0, max) : "";

// Steam's keyless list of a game's achievements, the one the store and client use. Display data only.
export async function fetchSchema(appid: string, fetcher: typeof fetch = fetch): Promise<SchemaAchievement[] | null> {
  try {
    const response = await fetcher(`https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid=${appid}&language=english`,
      { signal: AbortSignal.timeout(10_000) });
    if (!response.ok) return null;
    const list = ((await response.json()) as { response?: { achievements?: unknown } }).response?.achievements;
    if (!Array.isArray(list)) return null;
    return list.slice(0, MAX_ACHIEVEMENTS).flatMap(raw => {
      const item = raw as Record<string, unknown>, id = text(item.internal_name, 200);
      if (!id) return [];
      const percent = Number(item.player_percent_unlocked);
      return [{ id, name: text(item.localized_name, 200) || id, description: text(item.localized_desc, 500),
        icon: ICON.test(String(item.icon)) ? String(item.icon) : null, iconGray: ICON.test(String(item.icon_gray)) ? String(item.icon_gray) : null,
        hidden: item.hidden === true, percent: Number.isFinite(percent) && percent >= 0 && percent <= 100 ? percent : null }];
    });
  } catch { return null; }
}

const json = (body: unknown, status = 200) => Response.json(body, { status, headers: { "cache-control": "no-store" } });

export function achievementsRoute(fetcher: typeof fetch = fetch): DetachedRouteDefinition {
  return detached(async (request, _server, ctx) => {
    const url = new URL(request.url), userId = url.searchParams.get("user") ?? "", appid = url.searchParams.get("appid") ?? "";
    if (!/^[0-9a-f-]{36}$/.test(userId) || !/^\d{1,10}$/.test(appid)) return json({ error: "invalid_args" }, 400);
    const first = await ctx.read(slot => {
      if (!slot.user) return { status: 401 as const };
      if (!creator(slot)) return { status: 403 as const };
      if (!slot.db.select({ id: users.id }).from(users).where(eq(users.id, userId)).get()) return { status: 404 as const };
      return { status: 200 as const, cached: slot.db.select().from(achievementSchemas).where(eq(achievementSchemas.appid, appid)).get() ?? null };
    });
    if (first.status !== 200) return json({ error: { 401: "unauthorized", 403: "forbidden", 404: "not_found" }[first.status] }, first.status);
    let cached = first.cached;
    // Network I/O stays outside the serialized slots; a failed refresh keeps the last good list.
    if (!cached || Date.now() - cached.fetchedAt > FRESH) {
      const fetched = await fetchSchema(appid, fetcher);
      if (fetched) {
        const row = { appid, achievements: fetched, fetchedAt: Date.now() };
        await ctx.write(slot => slot.db.insert(achievementSchemas).values(row)
          .onConflictDoUpdate({ target: achievementSchemas.appid, set: row }).run());
        cached = row;
      }
    }
    const { unlocked, name } = await ctx.read(slot => ({
      unlocked: appState(slot, userId, appid).achievements, name: namesFor(slot, [appid])[appid] ?? null }));
    const known = new Set(cached?.achievements.map(item => item.id));
    const achievements = [
      ...(cached?.achievements ?? []).map(item => ({ ...item, unlockedAt: unlocked[item.id] ? unlocked[item.id]! * 1000 : null })),
      // Unlocks Steam doesn't list (renamed, removed, or no list available) still show by their id.
      ...Object.entries(unlocked).filter(([id]) => !known.has(id)).map(([id, time]) => ({
        id, name: id, description: "", icon: null, iconGray: null, hidden: false, percent: null, unlockedAt: time * 1000 })),
    ];
    return json({ appid, name, listed: !!cached, fetchedAt: cached?.fetchedAt ?? null, achievements });
  });
}

export const achievementRoutes = { "GET /api/admin/achievements": achievementsRoute() };
