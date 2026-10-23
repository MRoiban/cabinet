import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq } from "bedrock";
import { pairingCodes } from "./schema";
import { newCode, normalizeCode } from "./pairing";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string, alice: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
function identity() {
  const user = JSON.stringify({ id: "owner", email: "owner@example.test", name: "Owner" }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time,
    "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
// Calls a Bedrock query ("q") or mutation ("m") as a signed-in user.
const fn = (kind: "q" | "m", name: string, args: unknown = null) => fetch(`${base}/_bedrock/${kind}/${name}`, {
  method: "POST", headers: { ...identity(), origin: base, "content-type": "application/json" }, body: JSON.stringify(args) });
const value = async <T = any>(response: Response | Promise<Response>): Promise<T> => ((await (await response).json()) as { value: T }).value;
const tokenOf = (link: string) => /^\/connect#(cab_[A-Za-z0-9_-]{43})$/.exec(link)![1]!;
async function create(name: string): Promise<string> {
  return tokenOf((await value<{ link: string }>(fn("m", "createUser", { name }))).link);
}
function call(path: string, options: RequestInit = {}, bearer = alice) {
  const headers = new Headers(options.headers);
  headers.set("authorization", `Bearer ${bearer}`);
  return fetch(base + path, { ...options, headers });
}
const userIdOf = async (bearer: string) => ((await (await call("/api/me", {}, bearer)).json()) as { user: { id: string } }).user.id;

beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-pair-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
  alice = await create("Alice");
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});
const pair = (code: unknown, extra: Record<string, unknown> = {}) => fetch(base + "/api/pair", { method: "POST",
  headers: { "content-type": "application/json" }, body: JSON.stringify({ code, ...extra }) });
const issue = async (bearer = alice) => (await (await call("/api/pairing-codes", { method: "POST" }, bearer)).json()) as { code: string; expiresAt: number };

test("codes use the readable alphabet and normalize forgiving input", () => {
  for (let i = 0; i < 200; i++) expect(newCode()).toMatch(/^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$/);
  expect(normalizeCode(" abcd-efgh-jkmn ")).toBe("ABCDEFGHJKMN");
  expect(normalizeCode("o0il-1234-5678")).toBe("0011" + "12345678");
  for (const bad of ["ABCD-EFGH-JKM", "ABCD-EFGH-JKMNP", "ABCD-EFGH-JKMU", "ABCD_EFGH_JKMN", 123, "A".repeat(41)])
    expect(() => normalizeCode(bad)).toThrow();
});
test("a code pairs a device once with its own token", async () => {
  expect((await fetch(base + "/api/pairing-codes", { method: "POST" })).status).toBe(401);
  const { code, expiresAt } = await issue();
  expect(expiresAt - Date.now()).toBeGreaterThan(14 * 60 * 1000);
  const paired = await pair(code.toLowerCase(), { deviceName: "steamdeck" });
  expect(paired.status).toBe(200);
  const { token, user } = await paired.json() as { token: string; user: { name: string } };
  expect(token).toMatch(/^cab_[A-Za-z0-9_-]{43}$/);
  expect(user.name).toBe("Alice");
  expect(((await (await call("/api/me", {}, token)).json()) as { user: { name: string } }).user.name).toBe("Alice");
  expect((await call("/api/me")).status).toBe(200);
  const again = await pair(code);
  expect([again.status, await again.json()]).toEqual([401, { error: "invalid_code" }]);
});
test("expired, unknown and malformed codes are rejected", async () => {
  const { code } = await issue();
  running.db.update(pairingCodes).set({ expiresAt: Date.now() - 1 }).run();
  expect((await pair(code)).status).toBe(401);
  expect((await pair("ZZZZ-ZZZZ-ZZZZ")).status).toBe(401);
  expect((await pair("nope")).status).toBe(400);
  expect((await pair((await issue()).code, { deviceName: "" })).status).toBe(400);
});
test("admin issues a code once and a new connect link revokes paired devices", async () => {
  const id = ((await (await call("/api/me")).json()) as { user: { id: string } }).user.id;
  const { code, expiresAt } = await value<{ code: string; expiresAt: number }>(fn("m", "pairingCode", { id }));
  expect(expiresAt - Date.now()).toBeGreaterThan(14 * 60 * 1000);
  const before = await value(fn("q", "adminUser", { id }));
  expect(JSON.stringify(before)).not.toContain(code);
  const { token } = await (await pair(code)).json() as { token: string };
  expect((await value(fn("q", "adminUser", { id }))).user.connections).toBe(before.user.connections + 1);
  const next = tokenOf((await value<{ link: string }>(fn("m", "newConnectLink", { id }))).link);
  expect((await call("/api/me", {}, token)).status).toBe(401);
  expect((await value(fn("q", "adminUser", { id }))).user.connections).toBe(1);
  alice = next;
});
test("the connect page is the web app", async () => {
  const response = await fetch(base + "/connect");
  expect([response.status, response.headers.get("content-type")?.split(";")[0]]).toEqual([200, "text/html"]);
  expect(await response.text()).toContain('<div id="root">');
});
test("repeated failures trip the server-wide brake", async () => {
  let last = 0;
  for (let i = 0; i < 31; i++) last = (await pair("ZZZZ-ZZZZ-ZZZY")).status;
  expect(last).toBe(429);
});
