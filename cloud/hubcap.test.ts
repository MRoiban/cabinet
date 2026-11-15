import { afterAll, beforeAll, beforeEach, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { sql } from "bedrock";
import { JOB_RETENTION, REFETCH_WINDOW, STATS_TTL, prune, upstream } from "./hubcap";
import { sha, zipOf } from "./test-zip";

// A fake Hubcap: every upstream request the relay makes lands here. The real Hubcap is never called.
type FakeKey = { usage: number; limit: number; can?: boolean; expires?: string; reject?: boolean; throttle?: boolean };
const fake = {
  keys: new Map<string, FakeKey>(),
  manifests: new Map<string, Uint8Array<ArrayBuffer> | "missing">(),
  downloads: [] as { key: string; appid: string }[],
  statusCalls: 0,
  hold: null as Promise<void> | null,
};
const key = (c: string) => "smm_" + c.repeat(96);
const K1 = key("1"), K2 = key("2"), K3 = key("3");
const ALL_KEYS = [K1, K2, K3];

async function fakeHubcap(url: string, init: RequestInit): Promise<Response> {
  const u = new URL(url);
  expect(u.origin).toBe("https://hubcapmanifest.com");
  if (u.pathname === "/api/v1/user/stats") {
    const k = fake.keys.get(u.searchParams.get("api_key") ?? "");
    if (!k || k.reject) return Response.json({ detail: "Invalid API key" }, { status: 401 });
    return Response.json({ user_id: "u", daily_usage: k.usage, daily_limit: k.limit, can_make_requests: k.can ?? k.usage < k.limit,
      ...(k.expires ? { api_key_expires_at: k.expires } : {}) });
  }
  const status = /^\/api\/v1\/status\/(\d+)$/.exec(u.pathname);
  if (status) {
    fake.statusCalls++;
    const k = fake.keys.get(new Headers(init.headers).get("authorization")?.replace(/^Bearer /, "") ?? "");
    if (!k || k.reject) return new Response("no", { status: 401 });
    const m = fake.manifests.get(status[1]!);
    return Response.json({ status: "ok", manifest_file_exists: !!m && m !== "missing" });
  }
  const manifest = /^\/api\/v1\/manifest\/(\d+)$/.exec(u.pathname);
  if (manifest) {
    const apiKey = u.searchParams.get("api_key") ?? "", k = fake.keys.get(apiKey);
    if (!k || k.reject) return new Response("no", { status: 401 });
    if (k.throttle || k.usage >= k.limit) return new Response("limit", { status: 429 });
    if (fake.hold) await fake.hold;
    const m = fake.manifests.get(manifest[1]!);
    if (!m || m === "missing") return new Response("none", { status: 404 });
    k.usage++;
    fake.downloads.push({ key: apiKey, appid: manifest[1]! });
    // The title in Content-Disposition is not a safe file name; the relay must ignore it.
    return new Response(m, { headers: { "content-type": "application/zip", "content-disposition": 'attachment; filename="Game: Sub/../x.zip"' } });
  }
  return new Response("unexpected", { status: 500 });
}

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string, alex: string, sam: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
const transcript: string[] = []; // every response body and console line, checked for keys at the end
const originalConsole = { log: console.log, warn: console.warn, error: console.error, info: console.info };
const originalFetch = upstream.fetch;

function identity(email = "owner@example.test") {
  const user = JSON.stringify({ id: email, email, name: email }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time, "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
async function fn(kind: "m" | "q", name: string, body: unknown, email: string | null = "owner@example.test") {
  const response = await fetch(`${base}/_bedrock/${kind}/${name}`, { method: "POST",
    headers: { ...(email ? identity(email) : {}), origin: base, "content-type": "application/json" }, body: JSON.stringify(body) });
  const text = await response.text();
  transcript.push(text);
  return { status: response.status, body: JSON.parse(text) };
}
const admin = async (name: string, body: unknown = null) => {
  const result = await fn(name === "adminHubcap" ? "q" : "m", name, body);
  if (!result.body.ok) throw new Error(`${name}: ${JSON.stringify(result.body.error)}`);
  return result.body.value;
};
async function create(name: string): Promise<string> {
  const link = (await admin("createUser", { name })).link as string;
  return /^\/connect#(cab_[A-Za-z0-9_-]{43})$/.exec(link)![1]!;
}
async function call(path: string, options: RequestInit = {}, bearer: string | null = alex) {
  const headers = new Headers(options.headers);
  if (bearer) headers.set("authorization", `Bearer ${bearer}`);
  headers.set("origin", base);
  const response = await fetch(base + path, { ...options, headers });
  const text = await response.text();
  transcript.push(text);
  return { status: response.status, body: text ? JSON.parse(text) : null };
}
const ask = (appid: string, stale?: string, bearer = alex) =>
  call(`/api/apps/${appid}/hubcap${stale ? `?stale=${stale}` : ""}`, { method: "POST" }, bearer);
const look = (appid: string) => call(`/api/apps/${appid}/hubcap`);
async function settle(appid: string) {
  for (let i = 0; i < 200; i++) {
    const { body } = await look(appid);
    if (body.state !== "queued" && body.state !== "running") return body;
    await Bun.sleep(25);
  }
  throw new Error(`job for ${appid} did not finish`);
}
async function until(check: () => Promise<boolean> | boolean) {
  for (let i = 0; i < 200; i++) { if (await check()) return; await Bun.sleep(25); }
  throw new Error("condition not reached");
}

const lua = (appid: string, manifest: string) =>
  `addappid(${appid})\naddappid(${Number(appid) + 1}, 1, "${"ab".repeat(32)}")\nsetManifestid(${Number(appid) + 1}, "${manifest}")\n`;
const pack = (appid: string, manifest: string) => zipOf([{ name: `${appid}.lua`, data: lua(appid, manifest), deflate: true },
  { name: `${Number(appid) + 1}_${manifest}.manifest`, data: `manifest ${manifest}` }]);

/** Replaces the pool with these keys and their fake Hubcap accounts; stats are checked right away. */
async function pool(keys: [string, FakeKey][]) {
  for (const row of (await admin("adminHubcap")).keys) await admin("removeHubcapKey", { id: row.id });
  fake.keys = new Map(keys.map(([k, v]) => [k, { ...v }]));
  for (const [k] of keys) await admin("addHubcapKey", { key: k, label: `key ${k[4]}` });
  expect((await call("/api/admin/hubcap/check", { method: "POST", headers: identity() }, null)).status).toBe(200);
}

beforeAll(async () => {
  for (const level of ["log", "warn", "error", "info"] as const)
    console[level] = (...values: unknown[]) => { transcript.push(values.map(String).join(" ")); originalConsole[level](...values); };
  upstream.fetch = fakeHubcap;
  temp = await mkdtemp(join(tmpdir(), "cabinet-hubcap-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
  alex = await create("Alex");
  sam = await create("Sam");
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  Object.assign(console, originalConsole);
  upstream.fetch = originalFetch;
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});
beforeEach(() => { fake.downloads = []; fake.statusCalls = 0; fake.hold = null; });

test("without keys the relay is off, and every route needs a Cabinet token", async () => {
  expect((await call("/api/hubcap", {}, null)).status).toBe(401);
  expect((await call("/api/apps/10/hubcap", { method: "POST" }, null)).status).toBe(401);
  expect((await call("/api/apps/10/hubcap", {}, null)).status).toBe(401);
  expect((await call("/api/hubcap")).body).toEqual({ enabled: false });
  expect((await ask("10")).body).toEqual({ state: "failed", error: "relay_disabled" });
  expect((await look("10")).status).toBe(404);
  expect((await ask("abc")).status).toBe(400);
  expect((await ask("10", "nothex")).status).toBe(400);
});

test("only a creator manages keys, and a key is never shown again after saving", async () => {
  for (const [email, status] of [[null, 401], ["someone@example.test", 403]] as const) {
    expect((await fn("q", "adminHubcap", null, email)).status).toBe(status);
    expect((await fn("m", "addHubcapKey", { key: K1 }, email)).status).toBe(status);
    expect((await call("/api/admin/hubcap/check", { method: "POST", headers: email ? identity(email) : {} }, null)).status).toBe(status);
  }
  // A Cabinet bearer token is not a creator session either.
  expect((await call("/api/admin/hubcap/check", { method: "POST" })).status).toBe(401);
  const bad = await fn("m", "addHubcapKey", { key: "smm_" + "A".repeat(96) });
  expect(bad.body.ok).toBe(false);
  expect(JSON.stringify(bad.body)).not.toContain("AAAA");
  await pool([[K1, { usage: 2, limit: 10 }]]);
  const dup = await fn("m", "addHubcapKey", { key: K1 });
  expect(dup.body.error.code).toBe("KEY_CONFLICT");
  const view = await admin("adminHubcap");
  expect(view.enabled).toBe(true);
  expect(view.keys).toEqual([expect.objectContaining({ label: "key 1", mask: `smm_…${K1.slice(-4)}`, state: "usable",
    stats: { dailyUsage: 2, dailyLimit: 10, canMakeRequests: true, expiresAt: null } })]);
  // Relabel and replace.
  const id = view.keys[0].id;
  expect((await admin("updateHubcapKey", { id, label: "Owner" })).label).toBe("Owner");
  fake.keys.set(K2, { usage: 0, limit: 10 });
  const replaced = await admin("updateHubcapKey", { id, key: K2 });
  expect(replaced).toEqual(expect.objectContaining({ id, label: "Owner", mask: `smm_…${K2.slice(-4)}`, checkedAt: null }));
  expect((await call("/api/hubcap")).body).toEqual({ enabled: true });
});

test("a request is fetched once, validated and stored as the server's shared copy", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  const zip = pack("1000", "111");
  fake.manifests.set("1000", zip);
  const first = await ask("1000");
  expect(["queued", "running", "ready"]).toContain(first.body.state);
  const done = await settle("1000");
  expect(done).toEqual({ state: "ready", meta: { appid: "1000", sha256: sha(zip), size: zip.length, pins: { "1001": "111" },
    source: "Hubcap (Cabinet)", uploadedAt: expect.any(Number), uploader: "Cabinet Cloud" } });
  expect(fake.downloads).toEqual([{ key: K1, appid: "1000" }]);
  // The shared Lua cache now serves it to everyone, uploader shown as the server.
  expect((await call("/api/apps/1000/lua/meta", {}, sam)).body).toEqual(done.meta);
  // Asking again (no stale copy, or a stale one someone already replaced) answers at once.
  expect(await ask("1000", undefined, sam).then(r => r.body)).toEqual(done);
  expect(await ask("1000", "f".repeat(64)).then(r => r.body)).toEqual(done);
  expect(fake.downloads).toHaveLength(1);
});

test("requests for the same app join one job and cost one download", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  fake.manifests.set("2000", pack("2000", "222"));
  let release!: () => void;
  fake.hold = new Promise(resolve => { release = resolve; });
  await ask("2000");
  await until(async () => (await look("2000")).body.state === "running");
  expect((await ask("2000", undefined, sam)).body).toEqual({ state: "running" });
  expect((await ask("2000")).body).toEqual({ state: "running" }); // the same user again: still one requester row
  const job = (await admin("adminHubcap")).jobs.find((j: { appid: string }) => j.appid === "2000");
  expect(job).toEqual(expect.objectContaining({ state: "running", requesters: ["Alex", "Sam"] }));
  release();
  expect((await settle("2000")).state).toBe("ready");
  expect(fake.downloads.filter(d => d.appid === "2000")).toHaveLength(1);
});

test("the queue runs one job at a time, first in first out", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  for (const appid of ["3000", "3100", "3200", "3300"]) fake.manifests.set(appid, pack(appid, "3"));
  let release!: () => void;
  fake.hold = new Promise(resolve => { release = resolve; });
  await ask("3000");
  await until(async () => (await look("3000")).body.state === "running");
  expect((await ask("3200")).body).toEqual({ state: "queued", position: 1 });
  expect((await ask("3100", undefined, sam)).body).toEqual({ state: "queued", position: 2 });
  expect((await ask("3300")).body).toEqual({ state: "queued", position: 3 });
  expect((await look("3300")).body).toEqual({ state: "queued", position: 3 });
  release();
  for (const appid of ["3000", "3200", "3100", "3300"]) expect((await settle(appid)).state).toBe("ready");
  expect(fake.downloads.map(d => d.appid)).toEqual(["3000", "3200", "3100", "3300"]);
});

test("an app is not downloaded again within 30 minutes; the same build is upstream_not_newer", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  const zip = pack("4000", "444");
  fake.manifests.set("4000", zip);
  await ask("4000");
  const ready = await settle("4000");
  // The client found that copy stale, but it was fetched minutes ago: nothing newer to ask for.
  expect((await ask("4000", sha(zip))).body).toEqual({ state: "upstream_not_newer", meta: ready.meta });
  expect(fake.downloads).toHaveLength(1);
  // After the window, Hubcap is asked again; the same bytes mean it has nothing newer.
  running.db.run(sql`update hubcap_jobs set downloaded_at = ${Date.now() - REFETCH_WINDOW - 1000} where appid = '4000'`);
  expect((await ask("4000", sha(zip))).body.state).toBe("queued");
  expect(await settle("4000")).toEqual({ state: "upstream_not_newer", meta: ready.meta });
  expect(fake.downloads).toHaveLength(2);
  // A newer build on Hubcap replaces the copy (retention handled by the Lua cache).
  running.db.run(sql`update hubcap_jobs set downloaded_at = ${Date.now() - REFETCH_WINDOW - 1000} where appid = '4000'`);
  const newer = pack("4000", "445");
  fake.manifests.set("4000", newer);
  await ask("4000", sha(zip));
  expect(await settle("4000")).toEqual({ state: "ready", meta: expect.objectContaining({ sha256: sha(newer), pins: { "4001": "445" } }) });
});

test("an upload already newer than the stale copy is served without a fetch", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  const mine = pack("5000", "555");
  const put = await call(`/api/apps/5000/lua?sha256=${sha(mine)}&source=Ryuu&deviceId=pc`, { method: "PUT",
    headers: { "content-type": "application/zip" }, body: mine });
  expect(put.status).toBe(200);
  const answer = await ask("5000", "e".repeat(64));
  expect(answer.body).toEqual({ state: "ready", meta: expect.objectContaining({ sha256: sha(mine), uploader: "Alex", source: "Ryuu" }) });
  expect(fake.downloads).toHaveLength(0);
  expect(fake.statusCalls).toBe(0);
});

test("no manifest on Hubcap fails without spending a download", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  fake.manifests.set("6000", "missing");
  await ask("6000");
  expect(await settle("6000")).toEqual({ state: "failed", error: "no_manifest" });
  expect(fake.statusCalls).toBe(1);
  expect(fake.downloads).toHaveLength(0);
  expect(fake.keys.get(K1)!.usage).toBe(0);
  // Not asked again within the window.
  expect((await ask("6000")).body).toEqual({ state: "failed", error: "no_manifest" });
  expect(fake.statusCalls).toBe(1);
});

test("Hubcap's package goes through the Lua cache's own upload validation", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  // Declares another app: the PUT route refuses it, so must the relay.
  const wrong = zipOf([{ name: "7000.lua", data: lua("7999", "1") }]);
  const put = await call(`/api/apps/7000/lua?sha256=${sha(wrong)}&source=x&deviceId=pc`, { method: "PUT",
    headers: { "content-type": "application/zip" }, body: wrong });
  expect(put).toEqual({ status: 400, body: { error: "Lua does not actively declare this app." } });
  fake.manifests.set("7000", wrong);
  await ask("7000");
  expect(await settle("7000")).toEqual({ state: "failed", error: "invalid_package" });
  expect((await call("/api/apps/7000/lua/meta")).status).toBe(404);
  // An unsafe path, refused by the same shared check.
  fake.manifests.set("7100", zipOf([{ name: "7100.lua", data: lua("7100", "1") }, { name: "../evil.txt", data: "x" }]));
  await ask("7100");
  expect(await settle("7100")).toEqual({ state: "failed", error: "invalid_package" });
});

test("the key with the most remaining quota is used", async () => {
  await pool([[K1, { usage: 5, limit: 10 }], [K2, { usage: 1, limit: 10 }], [K3, { usage: 0, limit: 3 }]]);
  fake.manifests.set("8000", pack("8000", "8"));
  await ask("8000");
  expect((await settle("8000")).state).toBe("ready");
  expect(fake.downloads).toEqual([{ key: K2, appid: "8000" }]);
  // Stats are re-read after the download.
  const k2 = (await admin("adminHubcap")).keys.find((k: { mask: string }) => k.mask.endsWith(K2.slice(-4)));
  expect(k2.stats.dailyUsage).toBe(2);
});

test("a 429 rotates the same job to the next key and marks the first exhausted", async () => {
  await pool([[K1, { usage: 0, limit: 10 }], [K2, { usage: 5, limit: 10 }]]);
  fake.keys.get(K1)!.throttle = true; // Hubcap says limit reached although the stats looked fine
  fake.manifests.set("9000", pack("9000", "9"));
  await ask("9000");
  expect((await settle("9000")).state).toBe("ready");
  expect(fake.downloads).toEqual([{ key: K2, appid: "9000" }]);
  const keys = (await admin("adminHubcap")).keys;
  expect(keys.find((k: { mask: string }) => k.mask.endsWith(K1.slice(-4)))).toEqual(expect.objectContaining({ state: "exhausted", lastError: "quota_exhausted" }));
});

test("a rejected key is disabled and skipped", async () => {
  await pool([[K1, { usage: 0, limit: 10, reject: true }], [K2, { usage: 9, limit: 10 }]]);
  const view = await admin("adminHubcap");
  expect(view.keys.find((k: { mask: string }) => k.mask.endsWith(K1.slice(-4)))).toEqual(expect.objectContaining({ state: "disabled", disabled: "rejected", lastError: "key_rejected" }));
  fake.manifests.set("9100", pack("9100", "9"));
  await ask("9100");
  expect((await settle("9100")).state).toBe("ready");
  expect(fake.downloads).toEqual([{ key: K2, appid: "9100" }]);
  // A key rejected only at download time is disabled too, and the job moves on.
  await pool([[K1, { usage: 0, limit: 10 }], [K2, { usage: 5, limit: 10 }]]);
  fake.keys.get(K1)!.reject = true;
  fake.manifests.set("9200", pack("9200", "9"));
  await ask("9200");
  expect((await settle("9200")).state).toBe("ready");
  expect(fake.downloads.at(-1)).toEqual({ key: K2, appid: "9200" });
  expect((await admin("adminHubcap")).keys.find((k: { mask: string }) => k.mask.endsWith(K1.slice(-4))).state).toBe("disabled");
});

test("with every key exhausted, requests fail fast until stats allow requests again", async () => {
  await pool([[K1, { usage: 10, limit: 10 }], [K2, { usage: 3, limit: 3 }]]);
  fake.manifests.set("9300", pack("9300", "9"));
  expect((await call("/api/hubcap")).body).toEqual({ enabled: false });
  expect((await ask("9300")).body).toEqual({ state: "failed", error: "quota_exhausted" });
  expect((await look("9300")).status).toBe(404); // no job was queued
  expect((await admin("adminHubcap")).failure).toBe("quota_exhausted");
  // A new day: the cached stats are re-checked only once they are older than STATS_TTL.
  fake.keys.get(K2)!.usage = 0;
  expect((await ask("9300")).body.state).toBe("failed");
  running.db.run(sql`update hubcap_keys set stats_at = ${Date.now() - STATS_TTL - 1000}`);
  expect((await ask("9300")).body.state).toBe("queued");
  expect((await settle("9300")).state).toBe("ready");
  expect(fake.downloads).toEqual([{ key: K2, appid: "9300" }]);
  // Every key exhausted mid-job (429s): the job fails with quota_exhausted.
  await pool([[K1, { usage: 0, limit: 10, throttle: true }], [K2, { usage: 0, limit: 10, throttle: true }]]);
  fake.manifests.set("9400", pack("9400", "9"));
  await ask("9400");
  expect(await settle("9400")).toEqual({ state: "failed", error: "quota_exhausted" });
  expect((await ask("9500")).body).toEqual({ state: "failed", error: "quota_exhausted" });
});

test("expired and rejected pools fail with their own codes", async () => {
  await pool([[K1, { usage: 0, limit: 10, expires: "2020-01-01T00:00:00Z" }]]);
  expect((await ask("9600")).body).toEqual({ state: "failed", error: "key_expired" });
  await pool([[K1, { usage: 0, limit: 10, reject: true }]]);
  expect((await ask("9600")).body).toEqual({ state: "failed", error: "key_rejected" });
});

test("finished jobs are pruned after a day", async () => {
  running.db.run(sql`insert into hubcap_jobs (id, appid, seq, state, created_at, finished_at) values ('old', '1', 0, 'failed', 0, ${Date.now() - JOB_RETENTION - 1})`);
  running.db.run(sql`insert into hubcap_requesters (job_id, user_id, requested_at) select 'old', id, 0 from users limit 1`);
  prune({ db: running.db } as never);
  expect(running.db.all(sql`select id from hubcap_jobs where id = 'old'`)).toEqual([]);
  expect(running.db.all(sql`select job_id from hubcap_requesters where job_id = 'old'`)).toEqual([]);
});

test("a job left running by a stopped worker is queued again on restart", async () => {
  await pool([[K1, { usage: 0, limit: 10 }]]);
  fake.manifests.set("9700", pack("9700", "97"));
  await running.stop();
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0, cron: false });
  base = `http://127.0.0.1:${running.server.port}`;
  // Stand in for a crash mid-job: insert it as running, then restart again.
  running.db.run(sql`insert into hubcap_jobs (id, appid, seq, state, created_at, started_at) values ('crashed', '9700', 100000, 'running', ${Date.now()}, ${Date.now()})`);
  await running.stop();
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0, cron: false });
  base = `http://127.0.0.1:${running.server.port}`;
  expect((await settle("9700")).state).toBe("ready");
  expect(fake.downloads).toEqual([{ key: K1, appid: "9700" }]);
});

test("no key ever appears in a response or a log line", () => {
  const all = transcript.join("\n");
  for (const k of ALL_KEYS) {
    expect(all).not.toContain(k);
    expect(all).not.toContain(k.slice(4, 40));
  }
  expect(all).not.toContain("api_key=");
});
