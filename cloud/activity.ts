import { and, eq, sql, type FunctionContext } from "bedrock";
import { appActivity } from "./schema";

// First sight inserts the row; `played` also moves lastPlayed forward (never back).
export function touch(ctx: FunctionContext, userId: string, appid: string, played: boolean, now = Date.now()) {
  ctx.db.insert(appActivity).values({ userId, appid, firstSeen: now, lastPlayed: played ? now : null })
    .onConflictDoUpdate({ target: [appActivity.userId, appActivity.appid],
      set: played ? { lastPlayed: sql`max(coalesce(${appActivity.lastPlayed}, 0), ${now})` } : { userId } }).run();
}
export function activityFor(ctx: FunctionContext, userId: string): Record<string, { firstSeen: number; lastPlayed: number | null }> {
  const rows = ctx.db.select().from(appActivity).where(eq(appActivity.userId, userId)).all();
  return Object.fromEntries(rows.map(row => [row.appid, { firstSeen: row.firstSeen, lastPlayed: row.lastPlayed }]));
}
export const activityKey = (userId: string, appid: string) => and(eq(appActivity.userId, userId), eq(appActivity.appid, appid));
// CloudRedirect keys files as <accountId>/<appId>/…
export const appOfCloudPath = (path: string) => /^\d+\/(\d{1,10})\//.exec(path)?.[1] ?? null;
