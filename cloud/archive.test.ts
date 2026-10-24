import { afterAll, beforeAll, expect, test } from "bun:test";
import { createHmac } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startPebble } from "bedrock/server";
import { eq, sql } from "bedrock";
import { saveArchives } from "./schema";
import { EMPTY_HASH, KEEP_VERSIONS, safePath } from "./archive";
import { archiveOf, sha, zipOf, type Archive } from "./test-zip";

let running: Awaited<ReturnType<typeof startPebble>>, temp: string, base: string, token: string, other: string;
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
function call(path: string, options: RequestInit = {}, bearer = token) {
  const headers = new Headers(options.headers);
  headers.set("authorization", `Bearer ${bearer}`);
  headers.set("origin", base);
  return fetch(base + path, { ...options, headers });
}
function put(appid: string, archive: Archive, query: Record<string, string> = {}, bearer = token) {
  const params = new URLSearchParams({ deviceId: "thor", sha256: archive.sha256, contentHash: archive.contentHash,
    files: String(archive.files), bytes: String(archive.bytes), base: "", ...query });
  return call(`/api/apps/${appid}/archive?${params}`, { method: "PUT", headers: { "content-type": "application/zip" }, body: archive.zip }, bearer);
}
const blobs = () => (running.db.all(sql`select count(*) as n from _bedrock_files where bucket = 'archives'`) as { n: number }[])[0]!.n;
const save = (data: string) => archiveOf("1139980", [{ root: 0, path: "File_1/SaveFile.save", data }, { root: 0, path: "keybinds.kbsd", data: "keys" }]);

beforeAll(async () => {
  temp = await mkdtemp(join(tmpdir(), "cabinet-archive-test-"));
  process.env.BEDROCK_IDENTITY_SECRET = secret;
  process.env.BEDROCK_CREATORS = '["owner@example.test"]';
  running = await startPebble({ dir: import.meta.dir, dataDir: temp, port: 0 });
  base = `http://127.0.0.1:${running.server.port}`;
  token = await create("Alex");
  other = await create("Other");
});
afterAll(async () => {
  await running?.stop();
  if (temp) await rm(temp, { recursive: true, force: true });
  if (previousSecret === undefined) delete process.env.BEDROCK_IDENTITY_SECRET; else process.env.BEDROCK_IDENTITY_SECRET = previousSecret;
  if (previousCreators === undefined) delete process.env.BEDROCK_CREATORS; else process.env.BEDROCK_CREATORS = previousCreators;
});

test("format-2 upload, download, history, state and user isolation", async () => {
  expect((await call("/api/apps/1139980/archive")).status).toBe(404);
  const first = save("day 1");
  const response = await put("1139980", first);
  expect(response.status).toBe(200);
  const meta = await response.json() as Record<string, unknown>;
  expect(meta).toEqual({ id: expect.any(String), sha256: first.sha256, contentHash: first.contentHash, size: first.zip.length,
    files: 2, bytes: 9, createdAt: expect.any(Number), deviceId: "thor", deviceName: "thor", deleted: false });
  const download = await call("/api/apps/1139980/archive");
  expect(download.headers.get("etag")).toBe(first.sha256);
  expect(download.headers.get("x-cabinet-archive-id")).toBe(meta.id as string);
  expect(new Uint8Array(await download.arrayBuffer())).toEqual(first.zip);
  const state = await (await call("/api/state")).json() as { apps: Record<string, { archive: unknown; save: unknown }> };
  expect(state.apps["1139980"]!.archive).toEqual(meta);
  expect(state.apps["1139980"]!.save).toBeNull();
  // Another user sees nothing and cannot read by version id.
  expect((await call("/api/apps/1139980/archive", {}, other)).status).toBe(404);
  expect((await call(`/api/apps/1139980/archive?id=${meta.id}`, {}, other)).status).toBe(404);
  expect(await (await call("/api/apps/1139980/archives", {}, other)).json()).toEqual({ versions: [] });
  expect((await call("/api/apps/1139980/archive", {}, "cab_" + "x".repeat(43))).status).toBe(401);

  const second = save("day 2");
  const next = await put("1139980", second, { base: first.contentHash });
  expect(next.status).toBe(200);
  const history = await (await call("/api/apps/1139980/archives")).json() as { versions: { id: string; contentHash: string }[] };
  expect(history.versions.map(v => v.contentHash)).toEqual([second.contentHash, first.contentHash]);
  // An older version stays downloadable by id.
  expect(new Uint8Array(await (await call(`/api/apps/1139980/archive?id=${meta.id}`)).arrayBuffer())).toEqual(first.zip);
});

test("idempotent retry and 409 for divergent bases", async () => {
  const current = (await (await call("/api/apps/1139980/archives")).json() as { versions: { id: string; contentHash: string }[] }).versions[0]!;
  // The same content again (a retry after a lost response) returns the current version, no new row.
  const again = save("day 2");
  const retry = await put("1139980", again, { base: "0".repeat(64) });
  expect(retry.status).toBe(200);
  expect((await retry.json() as { id: string }).id).toBe(current.id);
  // A device that never saw "day 2" must not overwrite it.
  for (const baseHash of ["", sha("something else")]) {
    const conflict = await put("1139980", save("laptop"), { base: baseHash });
    expect(conflict.status).toBe(409);
    expect((await conflict.json() as { current: { id: string } }).current.id).toBe(current.id);
  }
  expect((await (await call("/api/apps/1139980/archives")).json() as { versions: unknown[] }).versions.length).toBe(2);
});

test("empty scans are refused and keep the existing archive; deletes are tombstones", async () => {
  const before = await (await call("/api/apps/1139980/archives")).json() as { versions: { id: string; contentHash: string }[] };
  const empty = archiveOf("1139980", []);
  expect(empty.contentHash).toBe(EMPTY_HASH);
  for (const baseHash of ["", before.versions[0]!.contentHash]) {
    const refused = await put("1139980", empty, { base: baseHash });
    expect(refused.status).toBe(422);
    expect(await refused.json()).toEqual({ error: "empty_archive" });
  }
  expect(await (await call("/api/apps/1139980/archives")).json()).toEqual(before);
  // Nothing to delete for a game without archives, and a delete must not carry files.
  expect((await put("42", archiveOf("42", []), { intent: "delete" })).status).toBe(404);
  expect((await put("1139980", save("x"), { intent: "delete", base: before.versions[0]!.contentHash })).status).toBe(400);
  expect((await put("1139980", empty, { intent: "remove" })).status).toBe(400);
  // A stale delete conflicts like any other write.
  expect((await put("1139980", empty, { intent: "delete", base: sha("stale") })).status).toBe(409);
  const tomb = await put("1139980", empty, { intent: "delete", base: before.versions[0]!.contentHash });
  expect(tomb.status).toBe(200);
  expect(await tomb.json()).toMatchObject({ deleted: true, files: 0, bytes: 0, contentHash: EMPTY_HASH });
  const after = await (await call("/api/apps/1139980/archives")).json() as { versions: unknown[] };
  expect(after.versions.length).toBe(3);
  expect(after.versions.slice(1)).toEqual(before.versions);
  // Restoring after the delete: a new upload based on the tombstone works.
  expect((await put("1139980", save("day 3"), { base: EMPTY_HASH })).status).toBe(200);
});

test("retention keeps the newest versions and deletes old blobs", async () => {
  let previous = "";
  for (let i = 0; i < KEEP_VERSIONS + 3; i++) {
    const archive = archiveOf("777", [{ root: 0, path: "slot.sav", data: `v${i}` }]);
    expect((await put("777", archive, { base: previous })).status).toBe(200);
    previous = archive.contentHash;
  }
  const rows = running.db.select().from(saveArchives).where(eq(saveArchives.appid, "777")).all();
  expect(rows.length).toBe(KEEP_VERSIONS);
  const history = await (await call("/api/apps/777/archives")).json() as { versions: { contentHash: string }[] };
  expect(history.versions[0]!.contentHash).toBe(previous);
  const total = running.db.select().from(saveArchives).all().length;
  expect(blobs()).toBe(total);
});

test("malformed format-2 archives are refused", async () => {
  const ok = save("valid");
  const bad: [string, Archive][] = [
    ["manifest not first", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { entries: e => [e[1]!, e[0]!] })],
    ["wrong gameId", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { tamper: m => { m.gameId = "steam:6"; } })],
    ["newer version", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { tamper: m => { m.version = 3; } })],
    ["unsorted", archiveOf("5", [{ root: 0, path: "b", data: "1" }, { root: 0, path: "a", data: "2" }])],
    ["duplicate", archiveOf("5", [{ root: 0, path: "a", data: "1" }, { root: 0, path: "a", data: "2" }])],
    ["case duplicate", archiveOf("5", [{ root: 0, path: "A", data: "1" }, { root: 0, path: "a", data: "2" }])],
    ["contentHash mismatch", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { tamper: m => { m.contentHash = sha("x"); } })],
    ["size lies", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { tamper: m => { m.files[0].size = 2; m.contentHash = sha(`${m.roots[0].id}\ta\t2\t${m.files[0].sha256}\n`); } })],
    ["EOCD count", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { eocdCount: 1 })],
    ["extra entry", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { entries: e => [...e, { name: "roots/0/b", data: "x" }] })],
    ["missing entry", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { entries: e => [e[0]!, { name: "roots/0/z", data: "1" }] })],
    ["unknown root", archiveOf("5", [{ root: 1, path: "a", data: "1" }])],
    ["duplicate root id", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { roots: [{ id: "r", source: "manifest" }, { id: "r", source: "override" }] })],
    ["not json", archiveOf("5", [], { entries: e => [{ name: "cabinet-save.json", data: "{nope" }, ...e.slice(1)] })],
  ];
  for (const path of ["../x", "a/../b", "C:/x", "/abs", "a//b", "a\\b", "CON", "aux.txt", "dot.", "space ", "a/./b", "nul\u0001"])
    bad.push([`path ${JSON.stringify(path)}`, archiveOf("5", [{ root: 0, path, data: "1" }])]);
  for (const [why, archive] of bad) {
    const response = await put("5", archive);
    if (response.status !== 400) throw new Error(`${why}: expected 400, got ${response.status}`);
  }
  // Query values must describe the body.
  const valid = archiveOf("5", [{ root: 0, path: "a", data: "1" }]);
  expect((await put("5", valid, { files: "2" })).status).toBe(400);
  expect((await put("5", valid, { bytes: "9" })).status).toBe(400);
  expect((await put("5", valid, { contentHash: ok.contentHash })).status).toBe(400);
  expect((await put("5", valid, { sha256: ok.sha256 })).status).toBe(400);
  expect((await put("5", { ...valid, zip: zipOf([{ name: "slot.dat", data: "legacy" }]), sha256: sha(zipOf([{ name: "slot.dat", data: "legacy" }])) })).status).toBe(400);
  expect((await call("/api/apps/5/archive?deviceId=a&sha256=x", { method: "PUT", body: "x" })).status).toBe(400);
  // .NET Framework cannot write stored entries; a deflated manifest is accepted.
  expect((await put("5", archiveOf("5", [{ root: 0, path: "a", data: "1" }], { deflateManifest: true }))).status).toBe(200);
  expect(running.db.select().from(saveArchives).where(eq(saveArchives.appid, "5")).all().length).toBe(1);
});

test("archive path rules", () => {
  for (const path of ["File_1/SaveFile-22-10-2026-18-31-54.save", "keybinds.kbsd", "a b/c.d", "Été/sauvegarde"]) expect(safePath(path)).toBe(path);
});
