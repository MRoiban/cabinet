import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq } from "bedrock";
import { cloudFiles } from "./schema";
import { cloudPath } from "./cloud";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string, alice: string, bob: string;
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
const put = (path: string, body: string, headers: Record<string, string> = {}, bearer = alice) =>
  call(`/api/cloud/files/${path}`, { method: "PUT", body, headers: { "content-type": "application/octet-stream", ...headers } }, bearer);
const sha = (value: string) => new Bun.CryptoHasher("sha256").update(value).digest("hex");
const userIdOf = async (bearer: string) => ((await (await call("/api/me", {}, bearer)).json()) as { user: { id: string } }).user.id;
const storedFiles = () => (running.db.$client.query("select count(*) as n from _bedrock_files where bucket = 'cloud'").get() as { n: number }).n;

beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-cloud-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
  alice = await create("Alice");
  bob = await create("Bob");
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});

test("cloud paths decode per segment and reject traversal", () => {
  expect(cloudPath("/api/cloud/files/12/480/blobs/save%20one.dat")).toBe("12/480/blobs/save one.dat");
  for (const bad of ["", "a//b", "a/%2E%2E/b", "a/./b", "a%2Fb", "a%5Cb", "a%00", "a/%E0%A4%A", "x".repeat(256), ("a/").repeat(600) + "a"])
    expect(() => cloudPath("/api/cloud/files/" + bad)).toThrow();
});
test("cloud files require an active token", async () => {
  for (const [path, method] of [["/api/cloud/files/1/2/a", "GET"], ["/api/cloud/files/1/2/a", "PUT"], ["/api/cloud/list", "GET"], ["/api/cloud/usage", "GET"]] as const) {
    const response = await fetch(base + path, { method, ...(method === "PUT" ? { body: "x" } : {}) });
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ error: "unauthorized" });
  }
});
test("put, get, head, replace and delete one file", async () => {
  const path = "7/480/blobs/game.sav";
  expect((await call(`/api/cloud/files/${path}`)).status).toBe(404);
  expect((await call(`/api/cloud/files/${path}`, { method: "HEAD" })).status).toBe(404);
  expect((await put(path, "first", { "x-content-sha256": "0".repeat(64) })).status).toBe(400);
  expect((await put(path, "first", { "x-content-sha256": "nope" })).status).toBe(400);
  const stored = await put(path, "first", { "x-content-sha256": sha("first") });
  expect(stored.status).toBe(200);
  const meta = await stored.json() as { path: string; size: number; sha256: string; modifiedTime: number };
  expect(meta).toMatchObject({ path, size: 5, sha256: sha("first") });
  expect(Math.abs(meta.modifiedTime - Date.now() / 1000)).toBeLessThan(5);
  const got = await call(`/api/cloud/files/${path}`);
  expect(got.headers.get("etag")).toBe(`"${sha("first")}"`);
  expect(await got.text()).toBe("first");
  const head = await call(`/api/cloud/files/${path}`, { method: "HEAD" });
  expect([head.status, head.headers.get("content-length"), await head.text()]).toEqual([200, "5", ""]);
  expect((await put(path, "second!")).status).toBe(200);
  expect(await (await call(`/api/cloud/files/${path}`)).text()).toBe("second!");
  expect(storedFiles()).toBe(1);
  expect(await (await call("/api/cloud/usage")).json()).toEqual({ files: 1, bytes: 7, quota: 10 * 1024 ** 3 });
  for (let i = 0; i < 2; i++) expect(await (await call(`/api/cloud/files/${path}`, { method: "DELETE" })).json()).toEqual({ ok: true });
  expect((await call(`/api/cloud/files/${path}`)).status).toBe(404);
  expect(storedFiles()).toBe(0);
});
test("users are isolated and the internal bucket is unreachable", async () => {
  expect((await put("7/480/blobs/mine", "alice")).status).toBe(200);
  expect((await call("/api/cloud/files/7/480/blobs/mine", {}, bob)).status).toBe(404);
  expect(await (await call("/api/cloud/list", {}, bob)).json()).toEqual({ files: [], next: null });
  expect((await call("/api/cloud/files/7/480/blobs/mine", { method: "DELETE" }, bob)).status).toBe(200);
  expect(await (await call("/api/cloud/files/7/480/blobs/mine")).text()).toBe("alice");
  const row = running.db.select().from(cloudFiles).where(eq(cloudFiles.userId, await userIdOf(alice))).get()!;
  for (const method of ["GET", "DELETE"]) expect((await fetch(`${base}/_bedrock/files/cloud/${row.fileId}`, { method, headers: identity() })).status).toBe(403);
});
test("list filters by prefix and paginates in path order", async () => {
  for (let i = 0; i < 1001; i++) running.db.insert(cloudFiles).values({ userId: await userIdOf(bob), path: `9/10/blobs/${String(i).padStart(4, "0")}`,
    fileId: crypto.randomUUID(), sha256: sha(String(i)), size: 1, updatedAt: 1000 }).run();
  expect((await put("9/11/blobs/other", "x", {}, bob)).status).toBe(200);
  const first = await (await call("/api/cloud/list?prefix=9%2F10%2F", {}, bob)).json() as { files: { path: string }[]; next: string };
  expect([first.files.length, first.files[0]!.path, first.next]).toEqual([1000, "9/10/blobs/0000", "9/10/blobs/0999"]);
  const second = await (await call(`/api/cloud/list?prefix=9%2F10%2F&cursor=${encodeURIComponent(first.next)}`, {}, bob)).json();
  expect(second).toEqual({ files: [{ path: "9/10/blobs/1000", size: 1, sha256: sha("1000"), modifiedTime: 1 }], next: null });
  const other = await (await call("/api/cloud/list?prefix=9%2F11", {}, bob)).json() as { files: { path: string }[] };
  expect(other.files.map(file => file.path)).toEqual(["9/11/blobs/other"]);
});
test("admin shows usage per game and deletes one user's cloud files", async () => {
  const id = await userIdOf(alice);
  const overview = await value(fn("q", "adminOverview"));
  expect(overview.users.find((user: { id: string }) => user.id === id).cloud).toEqual({ files: 1, bytes: 5 });
  const detail = await value(fn("q", "adminUser", { id }));
  expect([detail.cloud, detail.cloudGames, detail.games]).toEqual([{ files: 1, bytes: 5 }, 1,
    [{ appid: "480", name: null, achievements: 0, playtimeMinutes: 0, save: null, cloud: { files: 1, bytes: 5 } }]]);
  expect(await (await call("/api/cloud/apps")).json()).toEqual({ apps: { "480": { files: 1, bytes: 5 } } });
  expect(await value(fn("m", "deleteCloudFiles", { id }))).toEqual({ deleted: 1 });
  expect(await (await call("/api/cloud/usage")).json()).toMatchObject({ files: 0, bytes: 0 });
  expect((await call("/api/cloud/list", {}, bob)).status).toBe(200);
  expect(((await (await call("/api/cloud/usage", {}, bob)).json()) as { files: number }).files).toBe(1002);
});
