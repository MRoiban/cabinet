import { and, asc, desc, eq, inArray, isNotNull, lt, mutation, query, service, BedrockError, detached,
  type DetachedContext, type DetachedRouteDefinition, type FunctionContext, type ServiceContext } from "bedrock";
import { authenticate, creator } from "./auth";
import { hubcapJobs, hubcapKeys, hubcapRequesters, users, type HubcapState } from "./schema";
import { hex64, json, limitedBody, route } from "./archive";
import { inspectLua, luaMeta, luaVersions, sameBuild, storeLua, SERVER_UPLOADER, type LuaMeta } from "./lua";
import { digestKey, openKey, sealKey, secretState, SecretNotSet } from "./hubcap-crypto";
import { namesFor } from "./names";
import { MAX_LUA_ARCHIVE, luaZips, withInternalStorage } from "./storage";
import { appId, args, invalid, record } from "./validation";

// Hubcap relay (docs/cloud-api.md, "Hubcap relay"). The server owner keeps a pool of Hubcap API keys
// here; any Cabinet user can ask for a game's Lua config, which the server fetches from Hubcap ONCE
// into the shared Lua cache (lua.ts). One worker runs the queue, one job at a time, FIFO.
//
// A key never leaves this module except toward Hubcap itself: no response, admin view, log line or
// error carries it (admin views get a mask). Keys are encrypted at rest (hubcap-crypto.ts). Upstream
// failures are recorded as short codes only, never as exception text, because a fetch error can quote
// the request URL and the manifest URL holds the key.

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
/** How long a client request waits for a stats re-check of a pool that looks dead before answering. */
export const REVIVE_WAIT = 2_000;
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
type LiveKey = KeyRow & { key: string };
type JobRow = typeof hubcapJobs.$inferSelect;
type IO = Pick<DetachedContext, "read" | "write">;

// ── Key pool ────────────────────────────────────────────────────────────────

const expired = (row: KeyRow, now: number) => !!row.expiresAt && Date.parse(row.expiresAt) <= now;
/** A key the worker may download with. A key not checked yet counts: the worker checks it first. */
export function usable(row: KeyRow, now = Date.now()): boolean {
  return !row.disabled && !row.exhausted && row.canMakeRequests !== 0 && !expired(row, now);
}
/** The relay can fetch: the key secret is set and at least one key is usable. */
export const relayEnabled = (rows: readonly KeyRow[], now = Date.now()) => secretState() === "ok" && rows.some(row => usable(row, now));
const remaining = (row: KeyRow) => row.statsAt && row.dailyLimit !== null ? row.dailyLimit - (row.dailyUsage ?? 0) : Number.MAX_SAFE_INTEGER;
/** The usable key with the most remaining daily quota (least recently used on a tie). */
export function pickKey<T extends KeyRow>(rows: readonly T[], now = Date.now()): T | null {
  return [...rows].filter(row => usable(row, now))
    .sort((a, b) => remaining(b) - remaining(a) || (a.lastUsedAt ?? 0) - (b.lastUsedAt ?? 0) || a.addedAt - b.addedAt)[0] ?? null;
}
/** Why no key is usable: out of quota beats an expired key beats a rejected one. */
export function poolFailure(rows: readonly KeyRow[], now = Date.now()): RelayError {
  const readable = rows.filter(row => row.disabled !== "unreadable");
  if (secretState() !== "ok" || !readable.length) return "relay_disabled";
  if (readable.some(row => !row.disabled && !expired(row, now) && (row.exhausted || row.canMakeRequests === 0))) return "quota_exhausted";
  if (readable.some(row => row.disabled === "expired" || expired(row, now))) return "key_expired";
  return "key_rejected";
}
export const maskKey = (hint: string) => `smm_…${hint}`;

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

/**
 * GET /api/v1/status/{appid} (free): whether Hubcap has a manifest for the app. A 401 rejects the key; a
 * 403 is only `forbidden` (a blanket block such as a challenge page looks the same), confirmed by stats.
 */
export async function fetchStatus(key: string, appid: string, signal?: AbortSignal): Promise<"exists" | "missing" | "rejected" | "forbidden" | "quota" | "unavailable"> {
  try {
    const response = await upstream.fetch(`${HUBCAP_ORIGIN}/api/v1/status/${appid}`, {
      headers: { authorization: `Bearer ${key}` }, signal: timeout(QUICK_TIMEOUT, signal), redirect: "error" });
    const kind = response.status === 401 ? "rejected" : response.status === 403 ? "forbidden" : response.status === 429 ? "quota"
      : response.status === 404 ? "missing" : !response.ok ? "unavailable" : null;
    if (kind) { await response.body?.cancel(); return kind; }
    const exists = (await readJson(response))?.manifest_file_exists;
    return exists === true ? "exists" : exists === false ? "missing" : "unavailable";
  } catch { return "unavailable"; }
}

type DownloadResult = { kind: "ok"; bytes: Uint8Array } | { kind: "rejected" | "forbidden" | "quota" | "missing" | "unavailable" | "broken" | "too_large" };
/**
 * GET /api/v1/manifest/{appid} (counts against the key's daily limit). `spent` runs as soon as a 200
 * arrives, before the body is read: from then on the download counts even if the body fails (`broken`).
 * Content-Disposition is ignored.
 */
export async function fetchManifest(key: string, appid: string, spent: () => Promise<void>, signal?: AbortSignal): Promise<DownloadResult> {
  let response: Response;
  try {
    response = await upstream.fetch(`${HUBCAP_ORIGIN}/api/v1/manifest/${appid}?${keyQuery(key)}`, { signal: timeout(DOWNLOAD_TIMEOUT, signal), redirect: "error" });
  } catch { return { kind: "unavailable" }; }
  const kind = response.status === 401 ? "rejected" : response.status === 403 ? "forbidden" : response.status === 429 ? "quota"
    : response.status === 404 ? "missing" : !response.ok ? "unavailable" : null;
  if (kind) { await response.body?.cancel().catch(() => {}); return { kind }; }
  await spent();
  try { return { kind: "ok", bytes: await limitedBody(response, MAX_LUA_ARCHIVE, "too large") }; }
  catch (error) { return { kind: error instanceof BedrockError ? "too_large" : "broken" }; }
}

/** Records what a stats check said about a key, unless the key was replaced or removed meanwhile. */
function applyStats(slot: FunctionContext, row: KeyRow, result: StatsResult) {
  const now = Date.now(), same = and(eq(hubcapKeys.id, row.id), eq(hubcapKeys.sealed, row.sealed));
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
  slot.db.update(hubcapKeys).set(change).where(and(eq(hubcapKeys.id, row.id), eq(hubcapKeys.sealed, row.sealed))).run();
}
const allKeys = (slot: FunctionContext) => slot.db.select().from(hubcapKeys).orderBy(asc(hubcapKeys.addedAt)).all();

/**
 * The pool with each key decrypted. A row the current secret cannot open is marked disabled
 * ("unreadable", e.g. after the secret changed) and left out until a later check opens it again.
 */
async function liveKeys(io: IO): Promise<LiveKey[]> {
  const rows = await io.read(allKeys), live: LiveKey[] = [];
  for (const row of rows) {
    const key = await openKey(row.sealed);
    if (key) {
      if (row.disabled === "unreadable") {
        await io.write(slot => markKey(slot, row, { disabled: null, statsAt: 0 }));
        live.push({ ...row, disabled: null, statsAt: 0, key });
      } else live.push({ ...row, key });
    } else if (row.disabled !== "unreadable" && secretState() === "ok") {
      await io.write(slot => markKey(slot, row, { disabled: "unreadable", lastError: "key_unreadable", lastErrorAt: Date.now() }));
    }
  }
  return live;
}

/**
 * Re-checks the stats of keys whose cached stats are older than {@link STATS_TTL} (or every key with
 * `force`, or only `ids`). Network calls run outside the write slots. Stats calls are free.
 */
export async function refreshKeys(io: IO, options: { force?: boolean; ids?: string[]; signal?: AbortSignal | undefined } = {}): Promise<void> {
  const now = Date.now();
  const due = (await liveKeys(io)).filter(row => (options.ids ? options.ids.includes(row.id) : true)
    && (options.force || now - row.statsAt >= STATS_TTL));
  for (const row of due) {
    const result = await fetchStats(row.key, options.signal);
    await io.write(slot => applyStats(slot, row, result));
  }
}

/**
 * A 403 from a status or manifest call may be a blanket block (a challenge page) rather than a dead key:
 * ask that key's stats. Disabled only if stats also refuse it; true when the key was disabled.
 */
async function confirmForbidden(io: IO, key: LiveKey, signal?: AbortSignal): Promise<boolean> {
  const result = await fetchStats(key.key, signal);
  await io.write(slot => {
    applyStats(slot, key, result);
    if (result.kind !== "rejected") markKey(slot, key, { lastError: "hubcap_forbidden", lastErrorAt: Date.now() });
  });
  return result.kind === "rejected";
}

// Stats re-checks a client request asks for run on the worker, one at a time: requests share the same
// in-flight check and wait at most REVIVE_WAIT for it, then answer from what is known.
let revival: { promise: Promise<void>; resolve: () => void } | null = null;
function requestRevival(): Promise<void> {
  if (!revival) {
    let resolve!: () => void;
    revival = { promise: new Promise<void>(done => { resolve = done; }), resolve };
    wake();
  }
  return revival.promise;
}
async function revive(): Promise<void> {
  await Promise.race([requestRevival(), Bun.sleep(REVIVE_WAIT)]);
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
  if (!relayEnabled(keys, now))
    return { status: { state: "failed", error: poolFailure(keys, now) },
      poolDue: secretState() === "ok" && keys.some(row => now - row.statsAt >= STATS_TTL) };
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

/** The current cached version's bytes and build, read outside the write queue. */
async function currentBuild(io: IO, appid: string) {
  const current = await io.read(async slot => {
    const row = luaVersions(slot, appid)[0];
    if (!row) return null;
    const bytes = await withInternalStorage(slot, async () => new Uint8Array(await (await slot.storage.get(luaZips, row.fileId)).arrayBuffer()));
    return { sha256: row.sha256, bytes };
  });
  if (!current) return null;
  try { return { sha256: current.sha256, build: inspectLua(current.bytes, appid) }; }
  catch { return { sha256: current.sha256, build: null }; }
}

/** One job: rules first, then the free status check, then a download with key rotation, then storage. */
export async function runJob(io: IO, job: JobRow, signal?: AbortSignal): Promise<void> {
  const done = (decision: Decision) => io.write(slot => finish(slot, job.id, decision));
  const early = await io.read(slot => decideWithoutFetch(slot, job.appid, job.stale, Date.now(), job.id));
  if (early) return done(early);
  if (secretState() !== "ok") return done({ state: "failed", error: "relay_disabled" });
  await refreshKeys(io, { signal });

  // 1. Free status check: every key that isn't disabled may answer (usable ones first). A key that is
  //    throttled, blocked or unreachable doesn't settle it; the job fails only after every key was tried.
  let exists = false, transient = false;
  const candidates = (await liveKeys(io)).filter(row => !row.disabled).sort((a, b) => Number(usable(b)) - Number(usable(a)));
  for (const key of candidates) {
    const status = await fetchStatus(key.key, job.appid, signal);
    if (signal?.aborted) return;
    const now = Date.now();
    if (status === "missing") return done({ state: "failed", error: "no_manifest" });
    if (status === "exists") { exists = true; break; }
    if (status === "rejected") await io.write(slot => markKey(slot, key, { disabled: "rejected", lastError: "key_rejected", lastErrorAt: now }));
    else if (status === "quota") await io.write(slot => markKey(slot, key, { exhausted: 1, lastError: "quota_exhausted", lastErrorAt: now }));
    else if (status === "forbidden") { if (!await confirmForbidden(io, key, signal)) transient = true; }
    else { transient = true; await io.write(slot => markKey(slot, key, { lastError: "hubcap_unavailable", lastErrorAt: now })); }
  }
  if (!exists) return done({ state: "failed", error: transient ? "hubcap_unavailable" : poolFailure(await io.read(allKeys)) });

  // 2. Download with the usable key holding the most quota; rotate on 429, rejection, a block or an
  //    outage before the download started. A key that failed transiently is not retried in this job.
  let bytes: Uint8Array | null = null;
  const skip = new Set<string>();
  transient = false;
  while (!bytes) {
    await refreshKeys(io, { signal });
    const live = await liveKeys(io), key = pickKey(live.filter(row => !skip.has(row.id)));
    if (!key) return done({ state: "failed", error: transient ? "hubcap_unavailable" : poolFailure(await io.read(allKeys)) });
    const result = await fetchManifest(key.key, job.appid, () => io.write(slot => {
      markKey(slot, key, { lastUsedAt: Date.now() });
      slot.db.update(hubcapJobs).set({ downloadedAt: Date.now() }).where(eq(hubcapJobs.id, job.id)).run();
    }), signal);
    if (signal?.aborted) return;
    const now = Date.now();
    switch (result.kind) {
      case "ok": bytes = result.bytes; break;
      case "rejected": await io.write(slot => markKey(slot, key, { disabled: "rejected", lastError: "key_rejected", lastErrorAt: now })); break;
      case "quota": await io.write(slot => markKey(slot, key, { exhausted: 1, lastError: "quota_exhausted", lastErrorAt: now })); break;
      case "forbidden": if (!await confirmForbidden(io, key, signal)) { skip.add(key.id); transient = true; } break;
      case "unavailable":
        skip.add(key.id); transient = true;
        await io.write(slot => markKey(slot, key, { lastError: "hubcap_unavailable", lastErrorAt: now }));
        break;
      case "missing": return done({ state: "failed", error: "no_manifest" });
      // After a 200 the download counted: don't spend another key's quota on it within the window.
      case "broken":
        await io.write(slot => markKey(slot, key, { lastError: "hubcap_unavailable", lastErrorAt: now }));
        return done({ state: "failed", error: "hubcap_unavailable" });
      case "too_large":
        await io.write(slot => markKey(slot, key, { lastError: "invalid_package", lastErrorAt: now }));
        return done({ state: "failed", error: "invalid_package" });
    }
    if (bytes) await refreshKeys(io, { ids: [key.id], force: true, signal });
  }

  // 3. The Lua cache's own upload validation.
  let fetched: ReturnType<typeof inspectLua>;
  try { fetched = inspectLua(bytes, job.appid); }
  catch (error) {
    if (error instanceof BedrockError && error.code === "INVALID_ARGS") return done({ state: "failed", error: "invalid_package" });
    throw error;
  }
  const sha256 = new Bun.CryptoHasher("sha256").update(bytes).digest("hex");
  // 4. Hubcap still on the build the cache has (same pins and manifest files, other bytes): nothing newer,
  //    and the current entry stays. Otherwise the package becomes the shared cache's current version.
  const current = await currentBuild(io, job.appid);
  if (current && (current.sha256 === sha256 || (current.build && sameBuild(current.build, fetched))))
    return done({ state: "upstream_not_newer", sha256: current.sha256 });
  await io.write(async slot => {
    const { stored, meta } = await storeLua(slot, job.appid, bytes, sha256, fetched.pins, RELAY_SOURCE,
      { userId: null, deviceId: "server", deviceName: SERVER_UPLOADER });
    finish(slot, job.id, { state: stored ? "ready" : "upstream_not_newer", sha256: meta.sha256 });
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
      const asked = revival;
      if (asked) {
        // Client requests found the pool dead: one shared re-check of the keys whose stats are due.
        try { if (secretState() === "ok") await refreshKeys(ctx, { signal: ctx.signal }); }
        finally { if (revival === asked) revival = null; asked.resolve(); }
      }
      job = await ctx.write(claimNext);
      if (job) { await runJob(ctx, job, ctx.signal); continue; }
      // Keys added or replaced in the admin get their first stats check while the queue is empty.
      if (secretState() === "ok" && (await ctx.read(allKeys)).some(row => row.statsAt === 0 || row.disabled === "unreadable"))
        await refreshKeys(ctx, { signal: ctx.signal });
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

export const hubcapRoutes: Record<string, DetachedRouteDefinition> = {
  "GET /api/hubcap": route(async (_request, ctx) => {
    const first = await ctx.write(slot => {
      authenticate(slot);
      const keys = allKeys(slot), now = Date.now();
      return { enabled: relayEnabled(keys, now), due: secretState() === "ok" && keys.some(row => now - row.statsAt >= STATS_TTL) };
    });
    if (first.enabled || !first.due) return json({ enabled: first.enabled });
    await revive();
    return json({ enabled: await ctx.read(slot => relayEnabled(allKeys(slot))) });
  }),
  "POST /api/apps/:appid/hubcap": route(async (request, ctx) => {
    const url = new URL(request.url), appid = appId(url.pathname);
    const stale = hex64(url.searchParams.get("stale"), "stale SHA-256", true) || null;
    let result = await ctx.write(slot => enqueue(slot, appid, stale));
    if (result.poolDue) {
      await revive();
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
    if (secretState() !== "ok") return json({ error: "secret_not_set" }, 409);
    await refreshKeys(ctx, { force: true });
    return json({ ok: true });
  }),
};

// ── Admin functions ─────────────────────────────────────────────────────────

function gate(ctx: FunctionContext) {
  if (!ctx.user) throw new BedrockError("UNAUTHENTICATED", "Sign in to manage Cabinet.", "Sign in with a Bedrock creator account.");
  if (!creator(ctx)) throw new BedrockError("FORBIDDEN", "This account can't manage Cabinet.", "Sign in with a Bedrock creator account.");
}
function requireSecret() {
  if (secretState() !== "ok") throw new BedrockError("SECRET_NOT_SET", "The server secret HUBCAP_KEY_SECRET is not set, so keys can't be stored safely.",
    "Set it with `bedrock secrets set cabinet HUBCAP_KEY_SECRET --restart` (see cloud/README.md).", undefined, { status: 409 });
}
function keyView(row: KeyRow, now = Date.now()) {
  return {
    id: row.id, label: row.label, mask: maskKey(row.hint), addedAt: row.addedAt,
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
function refuseDuplicate(slot: FunctionContext, digest: string, except?: string) {
  const other = slot.db.select({ id: hubcapKeys.id }).from(hubcapKeys).where(eq(hubcapKeys.digest, digest)).get();
  if (other && other.id !== except) throw new BedrockError("KEY_CONFLICT", "This key is already in the pool.", "Each key can be added once.");
}
/** The stored form of a key: encrypted, its digest, and the 4 characters the admin may see. */
async function seal(key: string) {
  try { return { sealed: await sealKey(key), digest: await digestKey(key), hint: key.slice(-4) }; }
  catch (error) { if (error instanceof SecretNotSet) requireSecret(); throw error; }
}
const FRESH_KEY = { dailyUsage: null, dailyLimit: null, canMakeRequests: null, expiresAt: null, statsAt: 0, exhausted: 0,
  disabled: null, lastError: null, lastErrorAt: null };

export const hubcapQueries = {
  adminHubcap: query(ctx => {
    gate(ctx);
    const now = Date.now(), keys = allKeys(ctx), enabled = relayEnabled(keys, now);
    const jobs = ctx.db.select().from(hubcapJobs).orderBy(desc(hubcapJobs.seq)).limit(200).all();
    const requesters = jobs.length ? ctx.db.select({ jobId: hubcapRequesters.jobId, name: users.name, at: hubcapRequesters.requestedAt })
      .from(hubcapRequesters).innerJoin(users, eq(users.id, hubcapRequesters.userId))
      .where(inArray(hubcapRequesters.jobId, jobs.map(job => job.id))).orderBy(asc(hubcapRequesters.requestedAt)).all() : [];
    const names = namesFor(ctx, jobs.map(job => job.appid));
    return {
      secret: secretState(), enabled, failure: enabled ? null : poolFailure(keys, now),
      keys: keys.map(row => keyView(row, now)),
      jobs: jobs.map(job => ({ id: job.id, appid: job.appid, name: names[job.appid] ?? null,
        ...(({ state, position, error }) => ({ state, position: position ?? null, error: error ?? null }))(jobStatus(ctx, job)),
        createdAt: job.createdAt, startedAt: job.startedAt, finishedAt: job.finishedAt, downloaded: job.downloadedAt !== null,
        requesters: requesters.filter(r => r.jobId === job.id).map(r => r.name) })),
    };
  }),
};

export const hubcapMutations = {
  addHubcapKey: mutation(args(body => ({ key: keyArg(body.key), label: labelArg(body.label) })), async (ctx, { key, label }) => {
    gate(ctx); requireSecret();
    const stored = await seal(key);
    refuseDuplicate(ctx, stored.digest);
    const row = { ...FRESH_KEY, ...stored, id: crypto.randomUUID(), label, addedAt: Date.now(), lastUsedAt: null };
    ctx.db.insert(hubcapKeys).values(row).run();
    wake();
    return keyView(row);
  }),
  updateHubcapKey: mutation(args(body => ({ id: typeof body.id === "string" ? body.id : invalid("Invalid key id."),
    key: body.key === undefined || body.key === "" ? undefined : keyArg(body.key),
    label: body.label === undefined ? undefined : labelArg(body.label) })), async (ctx, { id, key, label }) => {
    gate(ctx); findKey(ctx, id);
    let replacement = {};
    if (key !== undefined) {
      requireSecret();
      const stored = await seal(key);
      refuseDuplicate(ctx, stored.digest, id);
      replacement = { ...FRESH_KEY, ...stored };
    }
    ctx.db.update(hubcapKeys).set({ ...replacement, ...(label !== undefined ? { label } : {}) }).where(eq(hubcapKeys.id, id)).run();
    if (key !== undefined) wake();
    return keyView(findKey(ctx, id));
  }),
  removeHubcapKey: mutation(args(body => ({ id: typeof body.id === "string" ? body.id : invalid("Invalid key id.") })), (ctx, { id }) => {
    gate(ctx); findKey(ctx, id);
    ctx.db.delete(hubcapKeys).where(eq(hubcapKeys.id, id)).run();
    return { ok: true };
  }),
};
