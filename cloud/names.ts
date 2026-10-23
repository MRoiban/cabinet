import { inArray, job, type FunctionContext } from "bedrock";
import { api } from "./api";
import { cloudApps } from "./cloud";
import { appNames, apps, saves } from "./schema";
import { invalid } from "./validation";

const PER_RUN = 5;

// Presence reports carry the game's real name, so every heartbeat refreshes it.
export function rememberName(ctx: FunctionContext, appid: string, name: string) {
  const values = { appid, name, updatedAt: Date.now() };
  ctx.db.insert(appNames).values(values).onConflictDoUpdate({ target: appNames.appid, set: values }).run();
}
export function namesFor(ctx: FunctionContext, ids: readonly string[]): Record<string, string> {
  if (!ids.length) return {};
  const rows = ctx.db.select().from(appNames).where(inArray(appNames.appid, [...new Set(ids)])).all();
  return Object.fromEntries(rows.map(row => [row.appid, row.name]));
}

// Store lookups tolerate rate limits and delisted apps: a failure is retried on a later run.
export async function lookupName(appid: string, fetcher: typeof fetch = fetch): Promise<string | null> {
  try {
    const response = await fetcher(`https://store.steampowered.com/api/appdetails?appids=${appid}&filters=basic`, { signal: AbortSignal.timeout(10_000) });
    if (!response.ok) return null;
    const body = await response.json() as Record<string, { success?: boolean; data?: { name?: unknown } }>;
    const name = body[appid]?.success ? body[appid]?.data?.name : null;
    return typeof name === "string" && name.trim() && name.length <= 200 ? name.trim() : null;
  } catch { return null; }
}

export const nameJobs = {
  appNames: job("17 * * * *", async ctx => {
    const missing = await ctx.read(slot => {
      const known = new Set(slot.db.select({ appid: appNames.appid }).from(appNames).all().map(row => row.appid));
      const ids = new Set([...slot.db.selectDistinct({ appid: apps.appid }).from(apps).all().map(row => row.appid),
        ...slot.db.selectDistinct({ appid: saves.appid }).from(saves).all().map(row => row.appid), ...Object.keys(cloudApps(slot))]);
      return [...ids].filter(id => !known.has(id)).sort(() => Math.random() - 0.5).slice(0, PER_RUN);
    });
    for (const appid of missing) {
      const name = await lookupName(appid);
      if (name) await ctx.write(slot => rememberName(slot, appid, name));
    }
  }, { transaction: false }),
};

export const nameRoutes = {
  "GET /api/app-names": api((request, ctx) => {
    const ids = (new URL(request.url).searchParams.get("ids") ?? "").split(",").filter(Boolean);
    if (ids.length > 500 || ids.some(id => !/^\d{1,10}$/.test(id))) invalid("Expected up to 500 comma-separated appids.");
    return Response.json({ names: namesFor(ctx, ids) });
  }),
};
