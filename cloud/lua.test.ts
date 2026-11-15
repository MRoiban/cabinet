import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq, sql } from "bedrock";
import { luaCache } from "./schema";
import { KEEP_LUA_VERSIONS, MAX_DOC, MAX_LUA, luaPins } from "./lua";
import { limitedBody } from "./archive";
import { MAX_LUA_ARCHIVE } from "./storage";
import { sha, zipOf, type TestEntry } from "./test-zip";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string, alex: string, sam: string;
const previousSecret = process.env.BEDROCK_IDENTITY_SECRET, previousCreators = process.env.BEDROCK_CREATORS;
const secret = crypto.randomUUID();
function identity() {
  const user = JSON.stringify({ id: "owner", email: "owner@example.test", name: "Owner" }), time = String(Date.now());
  return { "x-bedrock-user": user, "x-bedrock-user-ts": time, "x-bedrock-signature": createHmac("sha256", secret).update(`${user}\n${time}`).digest("hex") };
}
async function create(name: string): Promise<string> {
  const response = await fetch(`${base}/_bedrock/m/createUser`, { method: "POST",
    headers: { ...identity(), origin: base, "content-type": "application/json" }, body: JSON.stringify({ name }) });
  const link = ((await response.json()) as { value: { link: string } }).value.link;
  return /^\/connect#(cab_[A-Za-z0-9_-]{43})$/.exec(link)![1]!;
}
function call(path: string, options: RequestInit = {}, bearer = alex) {
  const headers = new Headers(options.headers);
  headers.set("authorization", `Bearer ${bearer}`);
  headers.set("origin", base);
  return fetch(base + path, { ...options, headers });
}
const lua = (appid: string, manifest: string, extra = "") =>
  `addappid(${appid})\naddappid(${Number(appid) + 1}, 1, "${"ab".repeat(32)}") -- Content\nsetManifestid(${Number(appid) + 1}, "${manifest}", 1234)\n${extra}`;
const pack = (appid: string, manifest: string, more: TestEntry[] = []) =>
  zipOf([{ name: `${appid}.lua`, data: lua(appid, manifest), deflate: true },
    { name: `${Number(appid) + 1}_${manifest}.manifest`, data: `manifest ${manifest}` }, ...more]);
function put(appid: string, zip: Uint8Array<ArrayBuffer>, query: Record<string, string> = {}, bearer = alex) {
  const params = new URLSearchParams({ sha256: sha(zip), source: "Morrenus", deviceId: "thor", ...query });
  return call(`/api/apps/${appid}/lua?${params}`, { method: "PUT", headers: { "content-type": "application/zip" }, body: zip }, bearer);
}
const blobs = () => (running.db.all(sql`select count(*) as n from _bedrock_files where bucket = 'lua'`) as { n: number }[])[0]!.n;

beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-lua-test-"));
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
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});

test("an upload is shared with every user of the server", async () => {
  expect((await call("/api/apps/1245620/lua/meta")).status).toBe(404);
  expect(await (await call("/api/apps/1245620/lua")).json()).toEqual({ error: "not_found" });
  const zip = pack("1245620", "7290000000000000001");
  const response = await put("1245620", zip);
  expect(response.status).toBe(200);
  const meta = await response.json();
  expect(meta).toEqual({ appid: "1245620", sha256: sha(zip), size: zip.length, pins: { "1245621": "7290000000000000001" },
    source: "Morrenus", uploadedAt: expect.any(Number), uploader: "Alex" });
  // Another user reads the same meta and bytes.
  expect(await (await call("/api/apps/1245620/lua/meta", {}, sam)).json()).toEqual(meta);
  const download = await call("/api/apps/1245620/lua", {}, sam);
  expect(download.status).toBe(200);
  expect(download.headers.get("etag")).toBe(sha(zip));
  expect(download.headers.get("content-type")).toBe("application/zip");
  expect(download.headers.get("cache-control")).toBe("no-store");
  expect(new Uint8Array(await download.arrayBuffer())).toEqual(zip);
  // The uploader's identity beyond the display name stays private.
  expect(JSON.stringify(meta)).not.toContain("thor");
  for (const path of ["/api/apps/1245620/lua/meta", "/api/apps/1245620/lua"]) {
    expect((await fetch(base + path)).status).toBe(401);
    expect((await call(path, {}, "cab_" + "x".repeat(43))).status).toBe(401);
  }
  expect((await put("1245620", zip, {}, "cab_" + "x".repeat(43))).status).toBe(401);
});

test("the same package again is a no-op that returns the current meta", async () => {
  const before = await (await call("/api/apps/1245620/lua/meta")).json();
  const again = await put("1245620", pack("1245620", "7290000000000000001"), { source: "Other source" }, sam);
  expect(again.status).toBe(200);
  expect(await again.json()).toEqual(before);
  expect(running.db.select().from(luaCache).where(eq(luaCache.appid, "1245620")).all().length).toBe(1);
});

test("a newer package replaces the current one; the newest three are kept", async () => {
  const zips: Uint8Array<ArrayBuffer>[] = [];
  for (let i = 0; i < KEEP_LUA_VERSIONS + 2; i++) {
    const zip = pack("440", `10${i}`);
    zips.push(zip);
    const response = await put("440", zip, { source: `source ${i}` }, i % 2 ? sam : alex);
    expect(response.status).toBe(200);
  }
  const meta = await (await call("/api/apps/440/lua/meta")).json() as Record<string, unknown>;
  expect(meta).toMatchObject({ sha256: sha(zips.at(-1)!), pins: { "441": `10${KEEP_LUA_VERSIONS + 1}` }, source: `source ${KEEP_LUA_VERSIONS + 1}`, uploader: "Alex" });
  expect(running.db.select().from(luaCache).where(eq(luaCache.appid, "440")).all().length).toBe(KEEP_LUA_VERSIONS);
  expect(blobs()).toBe(running.db.select().from(luaCache).all().length);
  // A retained previous version is still downloadable by hash; an evicted one is gone.
  const previous = zips.at(-2)!;
  const old = await call(`/api/apps/440/lua?sha256=${sha(previous)}`);
  expect(old.status).toBe(200);
  expect(new Uint8Array(await old.arrayBuffer())).toEqual(previous);
  expect((await call(`/api/apps/440/lua?sha256=${sha(zips[0]!)}`)).status).toBe(404);
  expect((await call("/api/apps/440/lua?sha256=nope")).status).toBe(400);
});

test("pins come only from active, full-line declarations in the install policy's grammar", () => {
  const text = "\uFEFFaddappid(10) -- Game\r\n-- setManifestid(11, \"1\")\r\nsetManifestid(11, '2', 99); -- note\r\n-- setManifestid(12, \"3\")\r\nsetManifestid(13, \"4\")\r\naddtoken(10, \"5\")\r\n\r\n-- a comment\r\n";
  expect(luaPins(text, "10")).toEqual({ "11": "2", "13": "4" });
  expect(luaPins("addappid(10)\n", "10")).toEqual({});
  expect(() => luaPins("addappid(11)\n", "10")).toThrow("does not actively declare");
  expect(() => luaPins("addappid(10)\nos.execute('x')\n", "10")).toThrow();
  expect(() => luaPins("addappid(10)\n--[[ x\n", "10")).toThrow();
  expect(() => luaPins("addappid(10)\nsetManifestid(11, \"2\") os.exit()\n", "10")).toThrow("Invalid setManifestid");
});

test("a pin hidden in a comment cannot vouch for a different active pin", () => {
  // Single-quoted stale pin with a "current" double-quoted pin in its trailing comment.
  expect(luaPins("addappid(10)\nsetManifestid(481,'1111') -- setManifestid(481,\"2222\")\n", "10")).toEqual({ "481": "1111" });
  // Stale pin followed by a commented-out "current" one.
  expect(luaPins("addappid(10)\nsetManifestid(481, '1111')\n-- setManifestid(481, \"2222\")\n", "10")).toEqual({ "481": "1111" });
});

test("ambiguous or undeclared luas are refused", () => {
  expect(() => luaPins("addappid(10)\nsetManifestid(11, \"1\")\nsetManifestid(11, \"2\")\n", "10")).toThrow("more than once");
  expect(() => luaPins("addappid(10)\nsetManifestid(11, \"1\")\nsetManifestid(11, '1')\n", "10")).toThrow("more than once");
  // Only commented out: LuaTools' install policy would accept it, a shared copy may not.
  expect(() => luaPins("-- addappid(10)\naddappid(11, 1, \"aa\")\n", "10")).toThrow("does not actively declare");
});

test("malformed packages and parameters are refused", async () => {
  const good = pack("70", "1");
  const bad: [string, Uint8Array<ArrayBuffer>][] = [
    ["not a zip", new TextEncoder().encode(lua("70", "1")) as Uint8Array<ArrayBuffer>],
    ["no lua", zipOf([{ name: "71_1.manifest", data: "m" }])],
    ["two luas", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "x/71.lua", data: lua("70", "1") }])],
    ["other app", zipOf([{ name: "70.lua", data: lua("80", "1") }])],
    ["code", zipOf([{ name: "70.lua", data: lua("70", "1", "os.execute('calc')") }])],
    ["executable", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "setup.exe", data: "MZ" }])],
    ["dll", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "x.dll", data: "MZ" }])],
    ["manifest name", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "evil.manifest", data: "m" }])],
    ["traversal", zipOf([{ name: "../70.lua", data: lua("70", "1") }])],
    ["absolute", zipOf([{ name: "/70.lua", data: lua("70", "1") }])],
    ["drive", zipOf([{ name: "C:/70.lua", data: lua("70", "1") }])],
    ["reserved", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "CON.txt", data: "x" }])],
    ["duplicate", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "a.txt", data: "x" }, { name: "A.txt", data: "y" }])],
    ["duplicate target", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "71_1.manifest", data: "m" }, { name: "d/71_1.manifest", data: "m" }])],
    ["big doc", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "readme.txt", data: "x".repeat(MAX_DOC + 1), deflate: true }])],
    ["big lua", zipOf([{ name: "70.lua", data: lua("70", "1", "-- " + "x".repeat(MAX_LUA)), deflate: true }])],
    ["bomb", zipOf([{ name: "70.lua", data: lua("70", "1") }, { name: "71_2.manifest", data: new Uint8Array(4 * 1024 * 1024), deflate: true }])],
    ["latin-1 lua", zipOf([{ name: "70.lua", data: new Uint8Array([...new TextEncoder().encode(lua("70", "1")), 0x2d, 0x2d, 0xe9]) }])],
    ["EOCD count", zipOf([{ name: "70.lua", data: lua("70", "1") }], { eocdCount: 2 })],
    ["pinned twice", zipOf([{ name: "70.lua", data: lua("70", "1", "setManifestid(71, \"2\")") }])],
    ["app only commented", zipOf([{ name: "70.lua", data: `-- addappid(70)\naddappid(71, 1, "${"ab".repeat(32)}")\n` }])],
  ];
  for (const [why, zip] of bad) {
    const response = await put("70", zip);
    if (response.status !== 400) throw new Error(`${why}: expected 400, got ${response.status}`);
  }
  // Query values must describe the body.
  expect((await put("70", good, { sha256: sha("other") })).status).toBe(400);
  expect((await put("70", good, { source: "" })).status).toBe(400);
  expect((await put("70", good, { source: "x".repeat(65) })).status).toBe(400);
  expect((await put("70", good, { deviceId: "" })).status).toBe(400);
  expect((await put("7O", good)).status).toBe(400);
  expect((await call("/api/apps/70/lua?sha256=" + sha(good) + "&source=s&deviceId=d", { method: "PUT", headers: { "content-type": "application/octet-stream" }, body: good })).status).toBe(400);
  expect(running.db.select().from(luaCache).where(eq(luaCache.appid, "70")).all().length).toBe(0);
  // Directories, documentation and a Windows-style separator are fine.
  const ok = zipOf([{ name: "pkg/", data: "" }, { name: "pkg\\70.lua", data: lua("70", "1") }, { name: "readme.md", data: "hi" }, { name: "71_1.manifest", data: "m" }]);
  expect((await put("70", ok)).status).toBe(200);
});

test("bodies over 90 MiB are refused before they are buffered", async () => {
  const request = new Request("http://localhost/", { method: "PUT", body: new Uint8Array(MAX_LUA_ARCHIVE + 1) });
  await expect(limitedBody(request, MAX_LUA_ARCHIVE, "too large")).rejects.toThrow("too large");
});
