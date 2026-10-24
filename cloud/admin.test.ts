import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { lookupName } from "./names";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
function identity(email: string | null = "Owner@Example.test") {
  if (!email) return {};
  const user = JSON.stringify({ id: email, email, name: "Owner" }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time,
    "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
const fn = (kind: "q" | "m", name: string, args: unknown = null, email?: string | null) => fetch(`${base}/_bedrock/${kind}/${name}`, {
  method: "POST", headers: { ...identity(email), origin: base, "content-type": "application/json" }, body: JSON.stringify(args) });
const value = async <T = any>(response: Response | Promise<Response>): Promise<T> => ((await (await response).json()) as { value: T }).value;
const tokenOf = (link: string) => /^\/connect#(cab_[A-Za-z0-9_-]{43})$/.exec(link)![1]!;
const call = (path: string, bearer: string, options: RequestInit = {}) =>
  fetch(base + path, { ...options, headers: { ...options.headers, authorization: `Bearer ${bearer}`, origin: base } });

beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-admin-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});

test("every admin function requires a signed-in creator", async () => {
  const calls: [kind: "q" | "m", name: string, args: unknown][] = [["q", "adminOverview", null], ["q", "adminUser", { id: "x" }],
    ["m", "createUser", { name: "x" }], ["m", "newConnectLink", { id: "x" }], ["m", "pairingCode", { id: "x" }],
    ["m", "revokeAccess", { id: "x" }], ["m", "deleteCloudFiles", { id: "x" }]];
  for (const [kind, name, args] of calls) {
    const anonymous = await fn(kind, name, args, null), guest = await fn(kind, name, args, "guest@example.test");
    expect([anonymous.status, guest.status]).toEqual([401, 403]);
    expect((await guest.json() as { error: { code: string } }).error.code).toBe("FORBIDDEN");
  }
  expect((await value(fn("q", "adminOverview"))).users).toEqual([]);
});
test("createUser returns a one-time link and code, and the overview never repeats them", async () => {
  expect((await fn("m", "createUser", { name: " " })).status).toBe(400);
  const created = await value(fn("m", "createUser", { name: "Sam" }));
  expect(created.user.name).toBe("Sam");
  expect(created.code).toMatch(/^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  const token = tokenOf(created.link);
  const overview = await value(fn("q", "adminOverview"));
  expect(JSON.stringify(overview)).not.toContain(token);
  expect(JSON.stringify(overview)).not.toContain(created.code);
  expect(overview.users[0]).toMatchObject({ name: "Sam", status: "unconnected", connections: 1, games: 0, lastSeen: null });
  expect(overview.totals).toMatchObject({ users: 1, activeThisWeek: 0, games: 0 });
  expect((await fn("q", "adminUser", { id: "missing" })).status).toBe(404);
});
test("progress, presence names and access changes show up in the admin", async () => {
  const { user, link } = await value(fn("m", "createUser", { name: "Marius" }));
  const token = tokenOf(link);
  await call("/api/apps/1245620/progress", token, { method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ deviceId: "deck", deviceName: "Deck", achievements: { A: 1, B: 2 }, playtimeMinutes: 90 }) });
  await call("/api/presence", token, { method: "PUT", headers: { "content-type": "application/json" },
    body: JSON.stringify({ accountId: 7, appId: "1245620", name: "ELDEN RING" }) });
  const overview = await value(fn("q", "adminOverview"));
  expect(overview.users.find((row: { id: string }) => row.id === user.id)).toMatchObject({ status: "active", games: 1, achievements: 2, playtimeMinutes: 90 });
  expect(overview.playing).toEqual([{ userId: user.id, userName: "Marius", appId: "1245620", name: "ELDEN RING" }]);
  expect(overview.totals).toMatchObject({ users: 2, activeThisWeek: 1, games: 1, achievements: 2, playtimeMinutes: 90 });
  const detail = await value(fn("q", "adminUser", { id: user.id }));
  expect(detail.games).toEqual([{ appid: "1245620", name: "ELDEN RING", achievements: 2, playtimeMinutes: 90, save: null, archive: null, cloud: null }]);
  expect(await (await call("/api/app-names?ids=1245620,620", token)).json()).toEqual({ names: { "1245620": "ELDEN RING" } });
  expect((await call("/api/app-names?ids=abc", token)).status).toBe(400);
  expect((await fetch(base + "/api/app-names?ids=1")).status).toBe(401);
  expect(await value(fn("m", "revokeAccess", { id: user.id }))).toEqual({ ok: true });
  expect((await call("/api/me", token)).status).toBe(401);
  expect((await value(fn("q", "adminUser", { id: user.id }))).user).toMatchObject({ status: "revoked", connections: 0 });
  const next = tokenOf((await value(fn("m", "newConnectLink", { id: user.id }))).link);
  expect((await call("/api/me", next)).status).toBe(200);
  expect(await value(fn("m", "deleteCloudFiles", { id: user.id }))).toEqual({ deleted: 0 });
});
test("/admin and /connect serve the web app", async () => {
  for (const path of ["/admin", "/connect", "/"]) {
    const response = await fetch(base + path);
    expect([response.status, response.headers.get("content-type")?.split(";")[0]]).toEqual([200, "text/html"]);
    expect(await response.text()).toContain('<div id="root">');
  }
});
test("store name lookups tolerate failures", async () => {
  const reply = (body: unknown, status = 200) => (async () => Response.json(body, { status })) as unknown as typeof fetch;
  expect(await lookupName("620", reply({ "620": { success: true, data: { name: " Portal 2 " } } }))).toBe("Portal 2");
  expect(await lookupName("620", reply({ "620": { success: false } }))).toBeNull();
  expect(await lookupName("620", reply({}, 429))).toBeNull();
  expect(await lookupName("620", (async () => { throw new Error("offline"); }) as unknown as typeof fetch)).toBeNull();
});
