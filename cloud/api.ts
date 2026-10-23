import { and, eq, lt, gte, BedrockError, type FunctionContext, type RouteHandler } from "bedrock";
import { authenticate } from "./auth";
import { apps, devices, saves, users, presence } from "./schema";
import { emptyState, merge, progress, type AppState, type SaveMeta } from "./merge";
import { accountId, presenceBody, appId, invalid, jsonBody, label, timestamp } from "./validation";
import { MAX_SAVE, saveZips, withInternalStorage } from "./storage";

const appKey = (userId: string, appid: string) => and(eq(apps.userId, userId), eq(apps.appid, appid));
const saveKey = (userId: string, appid: string) => and(eq(saves.userId, userId), eq(saves.appid, appid));
export function saveMeta(row: typeof saves.$inferSelect): SaveMeta {
  const { sha256, size, updatedAt, deviceId, deviceName } = row;
  return { sha256, size, updatedAt, deviceId, deviceName };
}
export function appState(ctx: FunctionContext, userId: string, appid: string): AppState {
  const row = ctx.db.select().from(apps).where(appKey(userId, appid)).get();
  const save = ctx.db.select().from(saves).where(saveKey(userId, appid)).get();
  const state = row ? { achievements: row.achievements, stats: row.stats, statsUpdatedAt: row.statsUpdatedAt,
    playtime: row.playtime, playtimeTotal: Object.values(row.playtime).reduce((a, b) => a + b, 0), save: null } : emptyState();
  return { ...state, save: save ? saveMeta(save) : null };
}
export function allStates(ctx: FunctionContext, userId: string): Record<string, AppState> {
  const ids = new Set([
    ...ctx.db.select().from(apps).where(eq(apps.userId, userId)).all().map(row => row.appid),
    ...ctx.db.select().from(saves).where(eq(saves.userId, userId)).all().map(row => row.appid),
  ]);
  return Object.fromEntries([...ids].map(id => [id, appState(ctx, userId, id)]));
}
export function api(handler: (request: Request, ctx: FunctionContext, userId: string) => Response | Promise<Response>): RouteHandler {
  return async (request, _server, ctx) => {
    try {
      const user = authenticate(ctx);
      const response = await handler(request, ctx, user.id);
      response.headers.set("cache-control", "no-store");
      return response;
    } catch (error) {
      if (!(error instanceof BedrockError)) throw error;
      const status = error.code === "UNAUTHENTICATED" ? 401 : error.code === "INVALID_ARGS" ? 400 : 500;
      if (status === 500) throw error;
      return Response.json({ error: status === 401 ? "unauthorized" : error.message }, { status, headers: { "cache-control": "no-store" } });
    }
  };
}
export const apiRoutes: Record<string, RouteHandler> = {
  "PUT /api/presence": api(async (request, ctx, id) => {
    const values = { ...presenceBody(await jsonBody(request)), userId: id, updatedAt: Date.now() };
    ctx.db.insert(presence).values(values).onConflictDoUpdate({ target: [presence.userId, presence.accountId], set: values }).run();
    return Response.json({ accountId: values.accountId, appId: values.appId, name: values.name, updatedAt: values.updatedAt });
  }),
  "DELETE /api/presence": api((request, ctx, id) => {
    const raw = new URL(request.url).searchParams.get("accountId") ?? "";
    if ((!/^\d{1,10}$/.test(raw) || raw.trim() !== raw)) invalid("Invalid Steam accountId.");
    ctx.db.delete(presence).where(and(eq(presence.userId, id), eq(presence.accountId, accountId(Number(raw))))).run();
    return Response.json({ ok: true });
  }),
  "GET /api/presence": api((_request, ctx) => {
    const cutoff = Date.now() - 3 * 60 * 1000;
    ctx.db.delete(presence).where(lt(presence.updatedAt, cutoff)).run();
    const rows = ctx.db.select().from(presence).where(gte(presence.updatedAt, cutoff)).all();
    // Multiple token-users may share a Steam account; its freshest report wins.
    const players = new Map<number, { accountId: number; appId: string; name: string; updatedAt: number }>();
    for (const row of rows) {
      const { accountId, appId, name, updatedAt } = row;
      if (!players.has(accountId) || players.get(accountId)!.updatedAt < updatedAt) players.set(accountId, { accountId, appId, name, updatedAt });
    }
    return Response.json({ players: [...players.values()] });
  }),
  "GET /api/me": api((_request, ctx, id) => {
    const user = ctx.db.select().from(users).where(eq(users.id, id)).get()!;
    return Response.json({ user: { id, name: user.name } });
  }),
  "GET /api/state": api((_request, ctx, id) => Response.json({ apps: allStates(ctx, id) })),
  "POST /api/apps/:appid/progress": api(async (request, ctx, id) => {
    const appid = appId(new URL(request.url).pathname);
    const pushed = progress(await jsonBody(request));
    const state = merge(appState(ctx, id, appid), pushed);
    const values = { userId: id, appid, achievements: state.achievements, stats: state.stats,
      statsUpdatedAt: state.statsUpdatedAt, playtime: state.playtime };
    ctx.db.insert(apps).values(values).onConflictDoUpdate({ target: [apps.userId, apps.appid], set: values }).run();
    ctx.db.insert(devices).values({ userId: id, deviceId: pushed.deviceId, name: pushed.deviceName })
      .onConflictDoUpdate({ target: [devices.userId, devices.deviceId], set: { name: pushed.deviceName } }).run();
    return Response.json(state);
  }),
  "PUT /api/apps/:appid/save": api(async (request, ctx, id) => {
    const url = new URL(request.url), appid = appId(url.pathname);
    const deviceId = label(url.searchParams.get("deviceId"));
    const rawTime = url.searchParams.get("updatedAt") ?? "";
    if (!/^\d+$/.test(rawTime)) invalid("Invalid updatedAt.");
    const updatedAt = timestamp(Number(rawTime));
    const sha256 = url.searchParams.get("sha256") ?? "";
    if (!/^[a-fA-F0-9]{64}$/.test(sha256)) invalid("Invalid SHA-256.");
    if (request.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase() !== "application/zip") invalid("Expected application/zip.");
    if (Number(request.headers.get("content-length")) > MAX_SAVE) invalid("Save exceeds 90 MiB.");
    const current = ctx.db.select().from(saves).where(saveKey(id, appid)).get();
    // Equal timestamps cannot replace a snapshot: only a newer snapshot wins.
    if (current && updatedAt <= current.updatedAt) return Response.json({ current: saveMeta(current) }, { status: 409 });
    const body = await request.blob();
    if (!body.size || body.size > MAX_SAVE) invalid("Save must be between 1 byte and 90 MiB.");
    const actual = new Bun.CryptoHasher("sha256").update(await body.arrayBuffer()).digest("hex");
    if (actual !== sha256.toLowerCase()) invalid("SHA-256 does not match the upload.");
    const device = ctx.db.select().from(devices).where(and(eq(devices.userId, id), eq(devices.deviceId, deviceId))).get();
    return withInternalStorage(ctx, async () => {
      const file = await ctx.storage.put(saveZips, body, { name: `${appid}.zip`, mime: "application/zip" });
      const values = { userId: id, appid, fileId: file.id, sha256: actual, size: file.size,
        updatedAt, deviceId, deviceName: device?.name ?? deviceId };
      ctx.db.insert(saves).values(values).onConflictDoUpdate({ target: [saves.userId, saves.appid], set: values }).run();
      if (current) await ctx.storage.delete(saveZips, current.fileId);
      return Response.json(saveMeta(values));
    });
  }),
  "GET /api/apps/:appid/save": api(async (request, ctx, id) => {
    const appid = appId(new URL(request.url).pathname);
    const save = ctx.db.select().from(saves).where(saveKey(id, appid)).get();
    if (!save) return Response.json({ error: "not_found" }, { status: 404 });
    return withInternalStorage(ctx, async () => {
      const file = await ctx.storage.get(saveZips, save.fileId);
      // Capture the snapshot before the next serialized upload can delete its file.
      return new Response(await file.arrayBuffer(), {
        headers: { "content-type": "application/zip", etag: save.sha256, "content-length": String(save.size) },
      });
    });
  }),
};
