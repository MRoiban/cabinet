import { and, asc, desc, eq, inArray, isNotNull, lt, mutation, query, service, BedrockError, detached,
  type DetachedContext, type DetachedRouteDefinition, type FunctionContext, type ServiceContext } from "bedrock";
import { authenticate, creator } from "./auth";
import { hubcapJobs, hubcapKeys, hubcapRequesters, users, type HubcapState } from "./schema";
import { hex64, json, limitedBody, route } from "./archive";
import { inspectLua, luaMeta, luaVersions, storeLua, SERVER_UPLOADER, type LuaMeta } from "./lua";
import { namesFor } from "./names";
import { MAX_LUA_ARCHIVE } from "./storage";
import { appId, args, invalid, record } from "./validation";

// Hubcap relay (docs/cloud-api.md, "Hubcap relay"). The server owner keeps a pool of Hubcap API keys
// here; any Cabinet user can ask for a game's Lua config, which the server fetches from Hubcap ONCE
// into the shared Lua cache (lua.ts). One worker runs the queue, one job at a time, FIFO.
//
// A key never leaves this module except toward Hubcap itself: no response, admin view, log line or
// error carries it (admin views get a mask). Upstream failures are recorded as short codes only, never
// as exception text, because a fetch error can quote the request URL and the manifest URL holds the key.

export const HUBCAP_ORIGIN = "https://hubcapmanifest.com";
export const KEY_FORMAT = /^smm_[0-9a-f]{96}$/;
/** The source recorded on packages the relay stores. */
export const RELAY_SOURCE = "Hubcap (Cabinet)";
/** Cached key stats are re-checked at most this often (and after every download). */
export const STATS_TTL = 5 * 60_000;
/** An app is downloaded from Hubcap at most once per this window. */
export const REFETCH_WINDOW = 30 * 60_000;
/** Finished jobs are kept this long (the GET status, the admin list), then pruned. */
export const JOB_RETENTION = 24 * 60 * 60_000;
const IDLE_POLL = 10_000;
const PRUNE_EVERY = 10 * 60_000;
const QUICK_TIMEOUT = 15_000;
const DOWNLOAD_TIMEOUT = 5 * 60_000;
const MAX_STATS = 64 * 1024;

export type RelayError = "no_manifest" | "quota_exhausted" | "key_expired" | "key_rejected" | "relay_disabled"
  | "hubcap_unavailable" | "invalid_package";
export interface RelayStatus { state: HubcapState; position?: number; error?: RelayError; meta?: LuaMeta }

/** Where upstream requests go. Tests replace `fetch` with a fake Hubcap; nothing else changes it. */
export const upstream = { fetch: (url: string, init: RequestInit): Promise<Response> => fetch(url, init) };

type KeyRow = typeof hubcapKeys.$inferSelect;
type JobRow = typeof hubcapJobs.$inferSelect;
type IO = Pick<DetachedContext, "read" | "write">;

// ── Key pool ────────────────────────────────────────────────────────────────

const expired = (row: KeyRow, now: number) => !!row.expiresAt && Date.parse(row.expiresAt) <= now;
/** A key the worker may download with. A key not checked yet counts: the worker checks it first. */
export function usable(row: KeyRow, now = Date.now()): boolean {
  return !row.disabled && !row.exhausted && row.canMakeRequests !== 0 && !expired(row, now);
}
const remaining = (row: KeyRow) => row.statsAt && row.dailyLimit !== null ? row.dailyLimit - (row.dailyUsage ?? 0) : Number.MAX_SAFE_INTEGER;
/** The usable key with the most remaining daily quota (least recently used on a tie). */
export function pickKey(rows: readonly KeyRow[], now = Date.now()): KeyRow | null {
  return [...rows].filter(row => usable(row, now))
    .sort((a, b) => remaining(b) - remaining(a) || (a.lastUsedAt ?? 0) - (b.lastUsedAt ?? 0) || a.addedAt - b.addedAt)[0] ?? null;
}
/** Why no key is usable: out of quota beats an expired key beats a rejected one. */
export function poolFailure(rows: readonly KeyRow[], now = Date.now()): RelayError {
  if (!rows.length) return "relay_disabled";
  if (rows.some(row => !row.disabled && !expired(row, now) && (row.exhausted || row.canMakeRequests === 0))) return "quota_exhausted";
  if (rows.some(row => row.disabled === "expired" || expired(row, now))) return "key_expired";
  return "key_rejected";
}
export function maskKey(key: string): string { return `smm_…${key.slice(-4)}`; }

type Stats = { dailyUsage: number; dailyLimit: number; canMakeRequests: boolean; expiresAt: string | null };
type StatsResult = { kind: "ok"; stats: Stats } | { kind: "rejected" } | { kind: "unavailable" };

const timeout = (ms: number, signal?: AbortSignal) => signal ? AbortSignal.any([signal, AbortSignal.timeout(ms)]) : AbortSignal.timeout(ms);
const keyQuery = (key: string) => `api_key=${encodeURIComponent(key)}`;

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  try { return record(JSON.parse(new TextDecoder().decode(await limitedBody(response, MAX_STATS)))); } catch { return null; }
}
/** GET /api/v1/user/stats (free). */
export async function fetchStats(key: string, signal?: AbortSignal): Promise<StatsResult> {
  try {
    const response = await upstream.fetch(`${HUBCAP_ORIGIN}/api/v1/user/stats?${keyQuery(key)}`, { signal: timeout(QUICK_TIMEOUT, signal), redirect: "error" });
    if (response.status === 401 || response.status === 403) { await response.body?.cancel(); return { kind: "rejected" }; }
    if (!response.ok) { await response.body?.cancel(); return { kind: "unavailable" }; }
    const body = await readJson(response);
    const usage = body?.daily_usage, limit = body?.daily_limit, can = body?.can_make_requests, expires = body?.api_key_expires_at;
    if (!Number.isSafeInteger(usage) || (usage as number) < 0 || !Number.isSafeInteger(limit) || (limit as number) <= 0
      || typeof can !== "boolean" || (expires !== undefined && expires !== null && (typeof expires !== "string" || expires.length > 64)))
      return { kind: "unavailable" };
    return { kind: "ok", stats: { dailyUsage: usage as number, dailyLimit: limit as number, canMakeRequests: can, expiresAt: (expires as string | null | undefined) ?? null } };
  } catch { return { kind: "unavailable" }; }
}

/** GET /api/v1/status/{appid} (free): whether Hubcap has a manifest for the app. */
export async function fetchStatus(key: string, appid: string, signal?: AbortSignal): Promise<"exists" | "missing" | "rejected" | "unavailable"> {
  try {
    const response = await upstream.fetch(`${HUBCAP_ORIGIN}/api/v1/status/${appid}`, {
      headers: { authorization: `Bearer ${key}` }, signal: timeout(QUICK_TIMEOUT, signal), redirect: "error" });
    if (response.status === 401 || response.status === 403) { await response.body?.cancel(); return "rejected"; }
    if (response.status === 404) { await response.body?.cancel(); return "missing"; }
    if (!response.ok) { await response.body?.cancel(); return "unavailable"; }
    const exists = (await readJson(response))?.manifest_file_exists;
    return exists === true ? "exists" : exists === false ? "missing" : "unavailable";
  } catch { return "unavailable"; }
}

type DownloadResult = { kind: "ok"; bytes: Uint8Array } | { kind: "rejected" | "quota" | "missing" | "unavailable" | "too_large" };
/** GET /api/v1/manifest/{appid} (counts against the key's daily limit). Content-Disposition is ignored. */
export async function fetchManifest(key: string, appid: string, signal?: AbortSignal): Promise<DownloadResult> {
  try {
    const response = await upstream.fetch(`${HUBCAP_ORIGIN}/api/v1/manifest/${appid}?${keyQuery(key)}`, { signal: timeout(DOWNLOAD_TIMEOUT, signal), redirect: "error" });
    const kind = response.status === 401 || response.status === 403 ? "rejected" : response.status === 429 ? "quota"
      : response.status === 404 ? "missing" : !response.ok ? "unavailable" : null;
    if (kind) { await response.body?.cancel(); return { kind }; }
    try { return { kind: "ok", bytes: await limitedBody(response, MAX_LUA_ARCHIVE, "too large") }; }
    catch (error) { if (error instanceof BedrockError) return { kind: "too_large" }; throw error; }
  } catch { return { kind: "unavailable" }; }
}

/** Records what a stats check said about a key, unless the key was replaced or removed meanwhile. */
function applyStats(slot: FunctionContext, row: KeyRow, result: StatsResult) {
  const now = Date.now(), same = and(eq(hubcapKeys.id, row.id), eq(hubcapKeys.key, row.key));
  if (result.kind === "ok") {
    const { dailyUsage, dailyLimit, canMakeRequests, expiresAt } = result.stats;
    const gone = !!expiresAt && Date.parse(expiresAt) <= now;
    slot.db.update(hubcapKeys).set({ dailyUsage, dailyLimit, canMakeRequests: canMakeRequests ? 1 : 0, expiresAt, statsAt: now,
      exhausted: canMakeRequests ? 0 : 1, disabled: gone ? "expired" : null,
      ...(gone ? { lastError: "key_expired", lastErrorAt: now } : !canMakeRequests ? { lastError: "quota_exhausted", lastErrorAt: now } : {}) })
      .where(same).run();
  } else if (result.kind === "rejected") {
    slot.db.update(hubcapKeys).set({ statsAt: now, disabled: "rejected", lastError: "key_rejected", lastErrorAt: now }).where(same).run();
  } else {
    slot.db.update(hubcapKeys).set({ statsAt: now, lastError: "stats_unavailable", lastErrorAt: now }).where(same).run();
  }
}
function markKey(slot: FunctionContext, row: KeyRow, change: Partial<KeyRow>) {
  slot.db.update(hubcapKeys).set(change).where(and(eq(hubcapKeys.id, row.id), eq(hubcapKeys.key, row.key))).run();
}
const allKeys = (slot: FunctionContext) => slot.db.select().from(hubcapKeys).orderBy(asc(hubcapKeys.addedAt)).all();

/**
 * Re-checks the stats of keys whose cached stats are older than {@link STATS_TTL} (or every key with
 * `force`, or only `ids`). Network calls run outside the write slots. Stats calls are free.
 */
export async function refreshKeys(io: IO, options: { force?: boolean; ids?: string[]; signal?: AbortSignal | undefined } = {}): Promise<void> {
  const now = Date.now();
  const due = (await io.read(allKeys)).filter(row => (options.ids ? options.ids.includes(row.id) : true)
    && (options.force || now - row.statsAt >= STATS_TTL));
  for (const row of due) {
    const result = await fetchStats(row.key, options.signal);
    await io.write(slot => applyStats(slot, row, result));
  }
}

// ── Jobs ────────────────────────────────────────────────────────────────────

const ACTIVE: HubcapState[] = ["queued", "running"];
const activeJob = (slot: FunctionContext, appid: string) => slot.db.select().from(hubcapJobs)
  .where(and(eq(hubcapJobs.appid, appid), inArray(hubcapJobs.state, ACTIVE))).get();

function metaFor(slot: FunctionContext, appid: string, sha256: string | null): LuaMeta | undefined {
  const all = luaVersions(slot, appid);
  const row = all.find(version => version.sha256 === sha256) ?? all[0];
  return row ? luaMeta(slot, row) : undefined;
}
export function jobStatus(slot: FunctionContext, job: JobRow): RelayStatus {
  if (job.state === "queued") {
    const ahead = slot.db.select({ id: hubcapJobs.id }).from(hubcapJobs)
      .where(and(eq(hubcapJobs.state, "queued"), lt(hubcapJobs.seq, job.seq))).all().length;
    return { state: "queued", position: ahead + 1 };
  }
  if (job.state === "running") return { state: "running" };
  if (job.state === "failed") return { state: "failed", error: (job.error ?? "hubcap_unavailable") as RelayError };
  const meta = metaFor(slot, job.appid, job.sha256);
  // The cached copy can only be gone if an admin removed it; report that rather than a broken ready.
  return meta ? { state: job.state, meta } : { state: "failed", error: "hubcap_unavailable" };
}

type Decision = { state: "ready" | "upstream_not_newer"; sha256: string } | { state: "failed"; error: RelayError };
/**
 * The answers that need no Hubcap call: another user already refreshed the copy the client found
 * stale (or there was none), or the app was fetched less than {@link REFETCH_WINDOW} ago.
 */
export function decideWithoutFetch(slot: FunctionContext, appid: string, stale: string | null, now = Date.now(), exclude?: string): Decision | null {
  const current = luaVersions(slot, appid)[0];
  if (current && current.sha256 !== stale) return { state: "ready", sha256: current.sha256 };
  const recent = slot.db.select().from(hubcapJobs).where(and(eq(hubcapJobs.appid, appid), isNotNull(hubcapJobs.finishedAt)))
    .orderBy(desc(hubcapJobs.finishedAt)).all().filter(job => job.id !== exclude);
  const fetched = recent.find(job => job.downloadedAt !== null && now - job.downloadedAt < REFETCH_WINDOW);
  if (fetched) return current ? { state: "upstream_not_newer", sha256: current.sha256 }
    : { state: "failed", error: (fetched.error ?? "hubcap_unavailable") as RelayError };
  // "No manifest" costs nothing to learn, but is not asked again within the window either.
  const missing = recent.find(job => job.error === "no_manifest" && now - job.finishedAt! < REFETCH_WINDOW);
  return missing ? { state: "failed", error: "no_manifest" } : null;
}
function decisionStatus(slot: FunctionContext, appid: string, decision: Decision): RelayStatus {
  if (decision.state === "failed") return { state: "failed", error: decision.error };
  return { state: decision.state, meta: metaFor(slot, appid, decision.sha256)! };
}

type Enqueued = { status: RelayStatus; created?: boolean; poolDue?: boolean };
/** POST: join the app's active job, answer without fetching, fail fast on a dead pool, or queue a job. */
function enqueue(slot: FunctionContext, appid: string, stale: string | null): Enqueued {
  const user = authenticate(slot), now = Date.now();
  const join = (jobId: string) => slot.db.insert(hubcapRequesters).values({ jobId, userId: user.id, requestedAt: now }).onConflictDoNothing().run();
  const active = activeJob(slot, appid);
  if (active) { join(active.id); return { status: jobStatus(slot, active) }; }
  const decision = decideWithoutFetch(slot, appid, stale, now);
  if (decision) return { status: decisionStatus(slot, appid, decision) };
  const keys = allKeys(slot);
  if (!keys.some(row => usable(row, now)))
    return { status: { state: "failed", error: poolFailure(keys, now) }, poolDue: keys.some(row => now - row.statsAt >= STATS_TTL) };
  const last = slot.db.select({ seq: hubcapJobs.seq }).from(hubcapJobs).orderBy(desc(hubcapJobs.seq)).limit(1).get();
  const job = { id: crypto.randomUUID(), appid, seq: (last?.seq ?? 0) + 1, state: "queued" as const, stale, createdAt: now };
  slot.db.insert(hubcapJobs).values(job).run();
  join(job.id);
  return { status: jobStatus(slot, slot.db.select().from(hubcapJobs).where(eq(hubcapJobs.id, job.id)).get()!), created: true };
}

function finish(slot: FunctionContext, jobId: string, decision: Decision) {
  slot.db.update(hubcapJobs).set({ state: decision.state, finishedAt: Date.now(),
    error: decision.state === "failed" ? decision.error : null, sha256: decision.state === "failed" ? null : decision.sha256 })
    .where(eq(hubcapJobs.id, jobId)).run();
}

/** One job: rules first, then the free status check, then a download with key rotation, then storage. */
export async function runJob(io: IO, job: JobRow, signal?: AbortSignal): Promise<void> {
  const done = (decision: Decision) => io.write(slot => finish(slot, job.id, decision));
  const early = await io.read(slot => decideWithoutFetch(slot, job.appid, job.stale, Date.now(), job.id));
  if (early) return done(early);

  // 1. Free status check, with any key that isn't disabled (usable ones first).
  let exists = false;
  await refreshKeys(io, { signal });
  for (const row of (await io.read(allKeys)).filter(row => !row.disabled).sort((a, b) => Number(usable(b)) - Number(usable(a)))) {
    const status = await fetchStatus(row.key, job.appid, signal);
    if (signal?.aborted) return;
    if (status === "rejected") { await io.write(slot => markKey(slot, row, { disabled: "rejected", lastError: "key_rejected", lastErrorAt: Date.now() })); continue; }
    if (status === "unavailable") {
      await io.write(slot => markKey(slot, row, { lastError: "hubcap_unavailable", lastErrorAt: Date.now() }));
      return done({ state: "failed", error: "hubcap_unavailable" });
    }
    if (status === "missing") return done({ state: "failed", error: "no_manifest" });
    exists = true;
    break;
  }
  if (!exists) return done({ state: "failed", error: poolFailure(await io.read(allKeys)) });

  // 2. Download with the usable key holding the most quota; rotate on 429 / rejection.
  let bytes: Uint8Array | null = null;
  for (let attempt = 0; !bytes; attempt++) {
    await refreshKeys(io, { signal });
    const rows = await io.read(allKeys), key = pickKey(rows);
    if (!key || attempt > rows.length * 2) return done({ state: "failed", error: poolFailure(rows) });
    const result = await fetchManifest(key.key, job.appid, signal);
    if (signal?.aborted) return;
    const now = Date.now();
    if (result.kind === "rejected") { await io.write(slot => markKey(slot, key, { disabled: "rejected", lastError: "key_rejected", lastErrorAt: now })); continue; }
    if (result.kind === "quota") { await io.write(slot => markKey(slot, key, { exhausted: 1, lastError: "quota_exhausted", lastErrorAt: now })); continue; }
    if (result.kind === "missing") return done({ state: "failed", error: "no_manifest" });
    if (result.kind !== "ok") {
      await io.write(slot => markKey(slot, key, { lastError: result.kind === "too_large" ? "invalid_package" : "hubcap_unavailable", lastErrorAt: now, lastUsedAt: now }));
      // A body that arrived (even too large) was a spent download.
      if (result.kind === "too_large") await io.write(slot => slot.db.update(hubcapJobs).set({ downloadedAt: now }).where(eq(hubcapJobs.id, job.id)).run());
      return done({ state: "failed", error: result.kind === "too_large" ? "invalid_package" : "hubcap_unavailable" });
    }
    bytes = result.bytes;
    await io.write(slot => {
      markKey(slot, key, { lastUsedAt: now });
      slot.db.update(hubcapJobs).set({ downloadedAt: now }).where(eq(hubcapJobs.id, job.id)).run();
    });
    await refreshKeys(io, { ids: [key.id], force: true, signal });
  }

  // 3. The Lua cache's own upload validation, then the shared cache (or "Hubcap has nothing newer").
  let pins: Record<string, string>;
  try { pins = inspectLua(bytes, job.appid).pins; }
  catch (error) {
    if (error instanceof BedrockError && error.code === "INVALID_ARGS") return done({ state: "failed", error: "invalid_package" });
    throw error;
  }
  const sha256 = new Bun.CryptoHasher("sha256").update(bytes).digest("hex");
  await io.write(async slot => {
    const { stored } = await storeLua(slot, job.appid, bytes, sha256, pins, RELAY_SOURCE,
      { userId: null, deviceId: "server", deviceName: SERVER_UPLOADER });
    finish(slot, job.id, { state: stored ? "ready" : "upstream_not_newer", sha256 });
  });
}

// ── Worker ──────────────────────────────────────────────────────────────────

let woken = false, waiter: (() => void) | null = null;
/** Nudges the worker after a new job or key; without it the worker still polls every few seconds. */
export function wake() { woken = true; waiter?.(); }
function sleep(ms: number, signal: AbortSignal): Promise<void> {
  if (woken || signal.aborted) return Promise.resolve();
  return new Promise(resolve => {
    const done = () => { clearTimeout(timer); signal.removeEventListener("abort", done); waiter = null; resolve(); };
    const timer = setTimeout(done, ms);
    signal.addEventListener("abort", done);
    waiter = done;
  });
}

function claimNext(slot: FunctionContext): JobRow | null {
  const job = slot.db.select().from(hubcapJobs).where(eq(hubcapJobs.state, "queued")).orderBy(asc(hubcapJobs.seq)).limit(1).get();
  if (!job) return null;
  slot.db.update(hubcapJobs).set({ state: "running", startedAt: Date.now() }).where(eq(hubcapJobs.id, job.id)).run();
  return { ...job, state: "running" };
}
export function prune(slot: FunctionContext, now = Date.now()) {
  const old = and(isNotNull(hubcapJobs.finishedAt), lt(hubcapJobs.finishedAt, now - JOB_RETENTION));
  const ids = slot.db.select({ id: hubcapJobs.id }).from(hubcapJobs).where(old).all().map(job => job.id);
  if (!ids.length) return;
  slot.db.delete(hubcapRequesters).where(inArray(hubcapRequesters.jobId, ids)).run();
  slot.db.delete(hubcapJobs).where(inArray(hubcapJobs.id, ids)).run();
}
/** Restart safety: a job a stopped worker left running is queued again (it keeps its place). */
export function requeueRunning(slot: FunctionContext) {
  slot.db.update(hubcapJobs).set({ state: "queued", startedAt: null }).where(eq(hubcapJobs.state, "running")).run();
}

async function worker(ctx: ServiceContext) {
  let pruned = 0;
  while (!ctx.signal.aborted) {
    let job: JobRow | null = null;
    try {
      if (Date.now() - pruned > PRUNE_EVERY) { await ctx.write(slot => prune(slot)); pruned = Date.now(); }
      woken = false; // a wake from here on is seen by the next sleep
      job = await ctx.write(claimNext);
      if (job) { await runJob(ctx, job, ctx.signal); continue; }
      // Keys added or replaced in the admin get their first stats check while the queue is empty.
      if ((await ctx.read(allKeys)).some(row => row.statsAt === 0)) await refreshKeys(ctx, { signal: ctx.signal });
    } catch (error) {
      // A code only: an exception could quote an upstream URL, and the manifest URL holds a key.
      ctx.log(`Hubcap relay: worker error (${error instanceof Error ? error.name : "unknown"})`);
      const failed = job;
      if (failed && !ctx.signal.aborted)
        await ctx.write(slot => finish(slot, failed.id, { state: "failed", error: "hubcap_unavailable" })).catch(() => {});
      if (!failed) await sleep(IDLE_POLL, ctx.signal);
      continue;
    }
    await sleep(IDLE_POLL, ctx.signal);
  }
}

export const hubcapServices = {
  // Exclusive: never two workers across a deploy handoff, so the queue really runs one job at a time.
  hubcapRelay: service({
    exclusive: true, stopTimeout: 10_000,
    async start(ctx) {
      await ctx.write(requeueRunning);
      return { done: worker(ctx) };
    },
    async stop(value) { await value.done; },
  }),
};

// ── Client routes ───────────────────────────────────────────────────────────

/** Re-checks a pool that looks dead, at most every STATS_TTL, before answering from it. */
async function revive(io: IO) { await refreshKeys(io); }

export const hubcapRoutes: Record<string, DetachedRouteDefinition> = {
  "GET /api/hubcap": route(async (_request, ctx) => {
    const first = await ctx.write(slot => {
      authenticate(slot);
      const keys = allKeys(slot), now = Date.now();
      return { enabled: keys.some(row => usable(row, now)), due: keys.some(row => now - row.statsAt >= STATS_TTL) };
    });
    if (first.enabled || !first.due) return json({ enabled: first.enabled });
    await revive(ctx);
    return json({ enabled: await ctx.read(slot => allKeys(slot).some(row => usable(row))) });
  }),
  "POST /api/apps/:appid/hubcap": route(async (request, ctx) => {
    const url = new URL(request.url), appid = appId(url.pathname);
    const stale = hex64(url.searchParams.get("stale"), "stale SHA-256", true) || null;
    let result = await ctx.write(slot => enqueue(slot, appid, stale));
    if (result.poolDue) {
      await revive(ctx);
      result = await ctx.write(slot => enqueue(slot, appid, stale));
    }
    if (result.created) wake();
    return json(result.status);
  }),
  "GET /api/apps/:appid/hubcap": route(async (request, ctx) => {
    const appid = appId(new URL(request.url).pathname);
    const status = await ctx.write(slot => {
      authenticate(slot);
      const job = activeJob(slot, appid) ?? slot.db.select().from(hubcapJobs).where(eq(hubcapJobs.appid, appid)).orderBy(desc(hubcapJobs.seq)).limit(1).get();
      return job ? jobStatus(slot, job) : null;
    });
    return status ? json(status) : json({ error: "not_found" }, 404);
  }),
  // Creator session only: re-check every key's stats now (the admin's "Check keys").
  "POST /api/admin/hubcap/check": detached(async (_request, _server, ctx) => {
    const gate = await ctx.read(slot => !slot.user ? 401 : !creator(slot) ? 403 : 200);
    if (gate !== 200) return json({ error: gate === 401 ? "unauthorized" : "forbidden" }, gate);
    await refreshKeys(ctx, { force: true });
    return json({ ok: true });
  }),
};

// ── Admin functions ─────────────────────────────────────────────────────────

function gate(ctx: FunctionContext) {
  if (!ctx.user) throw new BedrockError("UNAUTHENTICATED", "Sign in to manage Cabinet.", "Sign in with a Bedrock creator account.");
  if (!creator(ctx)) throw new BedrockError("FORBIDDEN", "This account can't manage Cabinet.", "Sign in with a Bedrock creator account.");
}
function keyView(row: KeyRow, now = Date.now()) {
  return {
    id: row.id, label: row.label, mask: maskKey(row.key), addedAt: row.addedAt,
    state: row.disabled ? "disabled" as const : usable(row, now) ? "usable" as const : "exhausted" as const,
    disabled: row.disabled, checkedAt: row.statsAt || null,
    stats: row.statsAt && row.dailyLimit !== null ? { dailyUsage: row.dailyUsage ?? 0, dailyLimit: row.dailyLimit,
      canMakeRequests: row.canMakeRequests === 1, expiresAt: row.expiresAt } : null,
    lastError: row.lastError, lastErrorAt: row.lastErrorAt, lastUsedAt: row.lastUsedAt,
  };
}
function keyArg(value: unknown): string {
  // Never echo the value: a near-miss is still most of a real key.
  if (typeof value !== "string" || !KEY_FORMAT.test(value.trim())) invalid("A Hubcap key is smm_ followed by 96 lowercase hex characters.");
  return value.trim();
}
function labelArg(value: unknown): string | null {
  if (value === undefined || value === null || value === "") return null;
  if (typeof value !== "string" || value.trim().length > 64 || /[\u0000-\u001f]/.test(value)) invalid("A label is at most 64 characters.");
  return value.trim() || null;
}
function findKey(slot: FunctionContext, id: string): KeyRow {
  const row = slot.db.select().from(hubcapKeys).where(eq(hubcapKeys.id, id)).get();
  if (!row) throw new BedrockError("KEY_NOT_FOUND", "This key no longer exists.", "Reload the key list.");
  return row;
}
function refuseDuplicate(slot: FunctionContext, key: string, except?: string) {
  const other = slot.db.select({ id: hubcapKeys.id }).from(hubcapKeys).where(eq(hubcapKeys.key, key)).get();
  if (other && other.id !== except) throw new BedrockError("KEY_CONFLICT", "This key is already in the pool.", "Each key can be added once.");
}
const FRESH_KEY = { dailyUsage: null, dailyLimit: null, canMakeRequests: null, expiresAt: null, statsAt: 0, exhausted: 0,
  disabled: null, lastError: null, lastErrorAt: null };

export const hubcapQueries = {
  adminHubcap: query(ctx => {
    gate(ctx);
    const now = Date.now(), keys = allKeys(ctx);
    const jobs = ctx.db.select().from(hubcapJobs).orderBy(desc(hubcapJobs.seq)).limit(200).all();
    const requesters = jobs.length ? ctx.db.select({ jobId: hubcapRequesters.jobId, name: users.name, at: hubcapRequesters.requestedAt })
      .from(hubcapRequesters).innerJoin(users, eq(users.id, hubcapRequesters.userId))
      .where(inArray(hubcapRequesters.jobId, jobs.map(job => job.id))).orderBy(asc(hubcapRequesters.requestedAt)).all() : [];
    const names = namesFor(ctx, jobs.map(job => job.appid));
    return {
      enabled: keys.some(row => usable(row, now)), failure: keys.some(row => usable(row, now)) ? null : poolFailure(keys, now),
      keys: keys.map(row => keyView(row, now)),
      jobs: jobs.map(job => ({ id: job.id, appid: job.appid, name: names[job.appid] ?? null,
        ...(({ state, position, error }) => ({ state, position: position ?? null, error: error ?? null }))(jobStatus(ctx, job)),
        createdAt: job.createdAt, startedAt: job.startedAt, finishedAt: job.finishedAt, downloaded: job.downloadedAt !== null,
        requesters: requesters.filter(r => r.jobId === job.id).map(r => r.name) })),
    };
  }),
};

export const hubcapMutations = {
  addHubcapKey: mutation(args(body => ({ key: keyArg(body.key), label: labelArg(body.label) })), (ctx, { key, label }) => {
    gate(ctx); refuseDuplicate(ctx, key);
    const row = { ...FRESH_KEY, id: crypto.randomUUID(), key, label, addedAt: Date.now(), lastUsedAt: null };
    ctx.db.insert(hubcapKeys).values(row).run();
    wake();
    return keyView(row);
  }),
  updateHubcapKey: mutation(args(body => ({ id: typeof body.id === "string" ? body.id : invalid("Invalid key id."),
    key: body.key === undefined || body.key === "" ? undefined : keyArg(body.key),
    label: body.label === undefined ? undefined : labelArg(body.label) })), (ctx, { id, key, label }) => {
    gate(ctx); findKey(ctx, id);
    if (key !== undefined) refuseDuplicate(ctx, key, id);
    ctx.db.update(hubcapKeys).set({ ...(key !== undefined ? { ...FRESH_KEY, key } : {}), ...(label !== undefined ? { label } : {}) })
      .where(eq(hubcapKeys.id, id)).run();
    if (key !== undefined) wake();
    return keyView(findKey(ctx, id));
  }),
  removeHubcapKey: mutation(args(body => ({ id: typeof body.id === "string" ? body.id : invalid("Invalid key id.") })), (ctx, { id }) => {
    gate(ctx); findKey(ctx, id);
    ctx.db.delete(hubcapKeys).where(eq(hubcapKeys.id, id)).run();
    return { ok: true };
  }),
};
