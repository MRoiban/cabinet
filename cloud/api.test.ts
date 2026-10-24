import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq } from "bedrock";
import { saves, tokens, presence } from "./schema";
import { hashToken } from "./auth";
import { sha, zipOf } from "./test-zip";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string;
let token: string, userId: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
function identity(email = "owner@example.test") {
  const user = JSON.stringify({ id: "owner", email, name: "Owner" }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time,
    "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
// Calls a Bedrock query ("q") or mutation ("m") as a signed-in user.
const fn = (kind: "q" | "m", name: string, args: unknown = null, email?: string) => fetch(`${base}/_bedrock/${kind}/${name}`, {
  method: "POST", headers: { ...identity(email), origin: base, "content-type": "application/json" }, body: JSON.stringify(args) });
const value = async <T = any>(response: Response | Promise<Response>): Promise<T> => ((await (await response).json()) as { value: T }).value;
const tokenOf = (link: string) => /^\/connect#(cab_[A-Za-z0-9_-]{43})$/.exec(link)![1]!;
async function create(name: string): Promise<string> {
  return tokenOf((await value<{ link: string }>(fn("m", "createUser", { name }))).link);
}
function call(path: string, options: RequestInit = {}, bearer = token) {
  const headers = new Headers(options.headers);
  headers.set("authorization", `Bearer ${bearer}`);
  return fetch(base + path, { ...options, headers });
}
beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
  token = await create("Test user");
  const me = await (await call("/api/me")).json() as { user: { id: string } };
  userId = me.user.id;
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});
test("tokens are stored only as SHA-256 hashes", () => {
  const stored = running.db.select().from(tokens).where(eq(tokens.hash, hashToken(token))).get();
  expect(stored?.hash.length).toBe(64);
  expect(JSON.stringify(running.db.select().from(tokens).all())).not.toContain("cab_");
});
test("API missing, invalid and revoked tokens return exact unauthorized shape", async () => {
  for (const response of [await fetch(base + "/api/me"), await call("/api/me", {}, "invalid")]) {
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ error: "unauthorized" });
  }
  const disposable = await create("Revoked");
  const me = await (await call("/api/me", {}, disposable)).json() as { user: { id: string } };
  expect(await value(fn("m", "revokeAccess", { id: me.user.id }))).toEqual({ ok: true });
  expect((await call("/api/me", {}, disposable)).status).toBe(401);
});
test("HTTP progress merges and user isolation", async () => {
  const push = (deviceId: string, minutes: number, time: number) => call("/api/apps/123/progress", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ deviceId, deviceName: "Desktop " + deviceId, achievements: { A: time },
      stats: { score: time }, statsUpdatedAt: time, playtimeMinutes: minutes }),
  });
  expect((await push("one", 30, 200)).status).toBe(200);
  await push("two", 40, 100);
  const response = await push("one", 20, 300);
  expect(await response.json()).toEqual({ achievements: { A: 100 }, stats: { score: 300 }, statsUpdatedAt: 300,
    playtime: { one: 30, two: 40 }, playtimeTotal: 70, save: null, archive: null });
  const other = await create("Other");
  expect(await (await call("/api/state", {}, other)).json()).toEqual({ apps: {} });
  expect((await call("/api/apps/123/save", {}, other)).status).toBe(404);
});
test("validation rejects malformed appids and JSON", async () => {
  for (const id of ["abc", "12345678901", "-1"]) expect((await call(`/api/apps/${id}/save`)).status).toBe(400);
  for (const body of ["{", "[]", '{"deviceId":"a"}']) expect((await call("/api/apps/1/progress", { method: "POST", body })).status).toBe(400);
});
test("save upload/download, conflict, replacement and private bucket", async () => {
  const zip = zipOf([{ name: "slot.dat", data: "save-A" }]);
  const hash = sha(zip);
  const upload = (time: number, body = zip, sha256 = sha(body), extra = "") => call(`/api/apps/123/save?deviceId=one&updatedAt=${time}&sha256=${sha256}${extra}`, {
    method: "PUT", headers: { "content-type": "application/zip" }, body,
  });
  expect((await call("/api/apps/987/save")).status).toBe(404);
  expect((await upload(500, zip, "0".repeat(64))).status).toBe(400);
  const response = await upload(500);
  expect(response.status).toBe(200);
  const meta = await response.json();
  expect(meta).toEqual({ sha256: hash, size: zip.length, updatedAt: 500, deviceId: "one", deviceName: "Desktop one", empty: false });
  for (const time of [499, 500]) {
    const conflict = await upload(time);
    expect(conflict.status).toBe(409);
    expect(await conflict.json()).toEqual({ current: meta });
  }
  const download = await call("/api/apps/123/save");
  expect(download.headers.get("etag")).toBe(hash);
  expect(new Uint8Array(await download.arrayBuffer())).toEqual(zip);
  const row = running.db.select().from(saves).where(eq(saves.userId, userId)).get()!;
  for (const method of ["GET", "DELETE"]) expect((await fetch(`${base}/_bedrock/files/saves/${row.fileId}`, { method, headers: identity() })).status).toBe(403);
  expect((await fetch(`${base}/_bedrock/files/saves`, { method: "POST", body: zip, headers: { "content-type": "application/zip", "x-bedrock-file-name": "x.zip", ...identity() } })).status).toBe(403);
  // The 22-byte empty ZIP the old client uploaded for empty OnlineFix Saves folders, and a ZIP
  // holding only a folder entry, are refused and never replace the stored backup.
  const empty = new Uint8Array(22); empty.set([0x50, 0x4b, 0x05, 0x06]);
  for (const body of [empty, zipOf([{ name: "Saves/", data: "" }])]) {
    const refused = await upload(600, body);
    expect(refused.status).toBe(422);
    expect(await refused.json()).toEqual({ error: "empty_archive" });
  }
  expect((await upload(600, new TextEncoder().encode("not a zip at all, definitely"))).status).toBe(400);
  expect(running.db.select().from(saves).where(eq(saves.userId, userId)).get()?.fileId).toBe(row.fileId);
  expect(new Uint8Array(await (await call("/api/apps/123/save")).arrayBuffer())).toEqual(zip);
  expect((await upload(501)).status).toBe(200);
  expect(running.db.select().from(saves).where(eq(saves.userId, userId)).get()?.fileId !== row.fileId).toBe(true);
  // An explicit allowEmpty=1 is the only way to store an empty legacy archive.
  const allowed = await upload(700, empty, sha(empty), "&allowEmpty=1");
  expect(allowed.status).toBe(200);
  expect((await allowed.json()).empty).toBe(true);
});
test("regeneration invalidates old token and keeps user state", async () => {
  const next = tokenOf((await value<{ link: string }>(fn("m", "newConnectLink", { id: userId }))).link);
  expect((await call("/api/me")).status).toBe(401);
  token = next;
  expect((await call("/api/me")).status).toBe(200);
  const state = await (await call("/api/state")).json() as { apps: Record<string, unknown> };
  expect(Object.keys(state.apps)).toEqual(["123"]);
});

test("presence shares fresh reports, upserts and isolates deletion", async () => {
  const other = await create("Presence friend");
  const put = (bearer: string, accountId: number, name: string) => call("/api/presence", {
    method: "PUT", headers: { origin: base, "content-type": "application/json" },
    body: JSON.stringify({ accountId, appId: "123", name }),
  }, bearer);
  expect((await put(token, 1234, "First game")).status).toBe(200);
  expect((await put(token, 1234, "Second game")).status).toBe(200);
  expect((await put(other, 4294967295, "Friend game")).status).toBe(200);
  const read = async () => (await (await call("/api/presence")).json() as { players: { accountId: number; name: string; updatedAt: number }[] }).players;
  const players = await read();
  expect(players.length).toBe(2);
  expect(players.find(p => p.accountId === 1234)?.name).toBe("Second game");
  expect(players.every(p => p.updatedAt >= Date.now() - 10000 && !("userId" in p))).toBe(true);
  expect((await call("/api/presence?accountId=1234", { method: "DELETE", headers: { origin: base } }, other)).status).toBe(200);
  expect((await read()).length).toBe(2);
  await call("/api/presence?accountId=1234", { method: "DELETE", headers: { origin: base } });
  expect((await read()).length).toBe(1);
  running.db.update(presence).set({ updatedAt: Date.now() - 180001 }).run();
  expect(await read()).toEqual([]);
  expect(running.db.select().from(presence).all()).toEqual([]);
});
test("presence validates identifiers, names, authentication and deletion", async () => {
  for (const change of [
    { accountId: 0 }, { accountId: -1 }, { accountId: 4294967296 }, { accountId: 1.5 }, { accountId: "1" },
    { appId: "" }, { appId: "abc" }, { appId: "123\n" }, { appId: "12345678901" }, { appId: 123 },
    { name: "" }, { name: " " }, { name: "x".repeat(101) }, { name: "a\n" }, { name: "a\u007f" }, { name: "a\u0085" },
  ]) expect((await call("/api/presence", { method: "PUT", headers: { origin: base },
    body: JSON.stringify({ accountId: 1, appId: "123", name: "Game", ...change }) })).status).toBe(400);
  for (const account of ["", "0", "-1", "1.5", "4294967296", "abc"])
    expect((await call(`/api/presence?accountId=${account}`, { method: "DELETE", headers: { origin: base } })).status).toBe(400);
  for (const method of ["GET", "PUT", "DELETE"])
    expect((await call("/api/presence", { method, headers: { origin: base } }, "invalid")).status).toBe(401);
});
