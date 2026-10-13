import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq } from "bedrock";
import { saves, tokens } from "./schema";
import { hashToken } from "./auth";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string;
let token: string, userId: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
function identity(email = "owner@example.test") {
  const user = JSON.stringify({ id: "owner", email, name: "Owner" }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time,
    "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
async function admin(action: string, extra: Record<string, string> = {}, email?: string) {
  return fetch(base + "/admin", { method: "POST", headers: { ...identity(email), origin: base },
    body: new URLSearchParams({ action, ...extra }), redirect: "manual" });
}
async function create(name: string): Promise<string> {
  const response = await admin("create", { name });
  if (response.status !== 200) throw new Error("Admin creation failed.");
  const html = await response.text();
  const value = /\/connect#(cab_[A-Za-z0-9_-]{43})/.exec(html)?.[1];
  if (!value) throw new Error("Admin did not display its one-time link.");
  return value;
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
test("admin requires signed creator identity and protects writes with Origin", async () => {
  expect((await fetch(base + "/admin", { redirect: "manual" })).status).toBe(302);
  expect((await fetch(base + "/admin", { headers: identity("guest@example.test") })).status).toBe(403);
  expect((await fetch(base + "/admin", { method: "POST", headers: identity(), body: "action=create&name=bad" })).status).toBe(403);
  const html = await (await fetch(base + "/admin", { headers: identity() })).text();
  expect(html.includes("cab_" )).toBe(false);
  const stored = running.db.select().from(tokens).where(eq(tokens.hash, hashToken(token))).get();
  expect(stored?.hash.length).toBe(64);
});
test("API missing, invalid and revoked tokens return exact unauthorized shape", async () => {
  for (const response of [await fetch(base + "/api/me"), await call("/api/me", {}, "invalid")]) {
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ error: "unauthorized" });
  }
  const disposable = await create("Revoked");
  const me = await (await call("/api/me", {}, disposable)).json() as { user: { id: string } };
  await admin("revoke", { userId: me.user.id });
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
    playtime: { one: 30, two: 40 }, playtimeTotal: 70, save: null });
  const other = await create("Other");
  expect(await (await call("/api/state", {}, other)).json()).toEqual({ apps: {} });
  expect((await call("/api/apps/123/save", {}, other)).status).toBe(404);
});
test("validation rejects malformed appids and JSON", async () => {
  for (const id of ["abc", "12345678901", "-1"]) expect((await call(`/api/apps/${id}/save`)).status).toBe(400);
  for (const body of ["{", "[]", '{"deviceId":"a"}']) expect((await call("/api/apps/1/progress", { method: "POST", body })).status).toBe(400);
});
test("save upload/download, conflict, replacement and private bucket", async () => {
  const zip = new Uint8Array(22); zip.set([0x50, 0x4b, 0x05, 0x06]);
  const hash = new Bun.CryptoHasher("sha256").update(zip).digest("hex");
  const upload = (time: number, sha = hash) => call(`/api/apps/123/save?deviceId=one&updatedAt=${time}&sha256=${sha}`, {
    method: "PUT", headers: { "content-type": "application/zip" }, body: zip,
  });
  expect((await call("/api/apps/987/save")).status).toBe(404);
  expect((await upload(500, "0".repeat(64))).status).toBe(400);
  const response = await upload(500);
  expect(response.status).toBe(200);
  const meta = await response.json();
  expect(meta).toEqual({ sha256: hash, size: 22, updatedAt: 500, deviceId: "one", deviceName: "Desktop one" });
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
  expect((await upload(501)).status).toBe(200);
  expect(running.db.select().from(saves).where(eq(saves.userId, userId)).get()?.fileId !== row.fileId).toBe(true);
});
test("regeneration invalidates old token and keeps user state", async () => {
  const response = await admin("regenerate", { userId });
  const next = /\/connect#(cab_[A-Za-z0-9_-]{43})/.exec(await response.text())?.[1];
  if (!next) throw new Error("Missing regenerated connect link.");
  expect((await call("/api/me")).status).toBe(401);
  token = next;
  expect((await call("/api/me")).status).toBe(200);
  const state = await (await call("/api/state")).json() as { apps: Record<string, unknown> };
  expect(Object.keys(state.apps)).toEqual(["123"]);
});
