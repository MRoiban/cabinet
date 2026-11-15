import { desc, eq, and, type DetachedRouteDefinition, type FunctionContext } from "bedrock";
import { authenticate } from "./auth";
import { devices, luaCache, users } from "./schema";
import { MAX_LUA_ARCHIVE, luaZips, withInternalStorage } from "./storage";
import { hex64, json, limitedBody, route } from "./archive";
import { appId, invalid, label } from "./validation";
import { centralDirectory, readEntry, type CentralEntry } from "./zip";

// Shared Lua config cache (docs/cloud-api.md, "Lua config cache"). A game's manifest package, as one
// LuaTools user downloaded and installed it, is kept for every user of this server. The newest row
// per app is current; a couple of previous versions stay downloadable by SHA-256.
export const KEEP_LUA_VERSIONS = 3;
// The entry limits mirror LuaTools' ManifestContentPolicy, which validates the package again on
// install: the server only refuses what no client would ever accept.
export const MAX_LUA = 2 * 1024 * 1024;
export const MAX_MANIFEST = 64 * 1024 * 1024;
export const MAX_DOC = 256 * 1024;
export const MAX_EXPANDED = 256 * 1024 * 1024;
export const MAX_ENTRIES = 4096;

type Row = typeof luaCache.$inferSelect;
/** What every user of the server may see. The uploader's user id and device stay private. */
export interface LuaMeta {
  appid: string; sha256: string; size: number; pins: Record<string, string>;
  source: string; uploadedAt: number; uploader: string;
}
/** The uploader shown for packages the server fetched itself (the Hubcap relay), which no user uploaded. */
export const SERVER_UPLOADER = "Cabinet Cloud";
export function luaMeta(ctx: FunctionContext, row: Row): LuaMeta {
  const { appid, sha256, size, pins, source, uploadedAt } = row;
  const uploader = row.userId === null ? SERVER_UPLOADER
    : ctx.db.select().from(users).where(eq(users.id, row.userId)).get()?.name ?? "";
  return { appid, sha256, size, pins, source, uploadedAt, uploader };
}

/** Who stored a version: a user's upload (their device), or the server itself (userId null). */
export interface LuaUploader { userId: string | null; deviceId: string; deviceName: string }
/**
 * Makes a package validated by {@link inspectLua} the app's current version, keeping the newest
 * {@link KEEP_LUA_VERSIONS}. The same package as the current one changes nothing (`stored` false).
 * Runs inside a write slot; old blobs are deleted after the commit.
 */
export async function storeLua(slot: FunctionContext, appid: string, body: Uint8Array, sha256: string,
  pins: Record<string, string>, source: string, uploader: LuaUploader): Promise<{ meta: LuaMeta; stored: boolean }> {
  const all = luaVersions(slot, appid), current = all[0];
  if (current && current.sha256 === sha256) return { meta: luaMeta(slot, current), stored: false };
  return withInternalStorage(slot, async () => {
    const file = await slot.storage.put(luaZips, new Blob([body as Uint8Array<ArrayBuffer>]), { name: `${appid}.zip`, mime: "application/zip" });
    const values = { id: crypto.randomUUID(), appid, fileId: file.id, sha256, size: body.length, pins, source,
      uploadedAt: Math.max(Date.now(), (current?.uploadedAt ?? 0) + 1), ...uploader };
    slot.db.insert(luaCache).values(values).run();
    // Retention: the newest KEEP_LUA_VERSIONS stay; blob deletion runs after the commit.
    for (const old of all.slice(KEEP_LUA_VERSIONS - 1)) {
      slot.db.delete(luaCache).where(eq(luaCache.id, old.id)).run();
      await slot.storage.delete(luaZips, old.fileId);
    }
    return { meta: luaMeta(slot, values), stored: true };
  });
}
export function luaVersions(ctx: FunctionContext, appid: string): Row[] {
  // uploadedAt is strictly increasing per app (see PUT), so it orders versions.
  return ctx.db.select().from(luaCache).where(eq(luaCache.appid, appid)).orderBy(desc(luaCache.uploadedAt)).all();
}

const RESERVED = /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i;
const MANIFEST_FILE = /^[1-9][0-9]{0,9}_[0-9]{1,20}\.manifest$/;
/** ManifestContentPolicy.ValidateRelativePath: relative, no drive or ADS colon, Windows-safe segments. */
function entryPath(name: string): string {
  const path = name.replace(/\\/g, "/"), relative = path.endsWith("/") ? path.slice(0, -1) : path;
  if (!relative || relative.length > 240 || relative.startsWith("/") || relative.includes(":")) invalid("Unsafe archive path.");
  for (const part of relative.split("/")) {
    if (!part || part === "." || part === ".." || /[. ]$/.test(part) || /[\u0000-\u001f<>"|?*]/.test(part)
      || RESERVED.test(part.split(".")[0]!)) invalid("Unsafe archive path.");
  }
  return path;
}
const isDoc = (name: string) => /\.(txt|md)$/i.test(name);

// ManifestContentPolicy's declaration grammar: double or single quotes, an optional size, an optional
// ';' and an optional trailing "-- comment", which is ignored.
const PIN = /^setManifestid[ \t]*\([ \t]*([1-9][0-9]{0,9})[ \t]*,[ \t]*(?:"([0-9]{1,20})"|'([0-9]{1,20})')[ \t]*(?:,[ \t]*[0-9]{1,20}[ \t]*)?\)[ \t]*;?[ \t]*(?:--.*)?$/i;
const STATEMENT = /^(addappid|setManifestid|addtoken)[ \t]*\(/i;
const ADDAPPID = /^addappid[ \t]*\([ \t]*([1-9][0-9]{0,9})[ \t]*[,)]/i;
/**
 * Depot → manifest pins of the lua's ACTIVE, full-line `setManifestid` declarations, read exactly as
 * LuaTools' cache check reads them. Commented-out lines and trailing comments never count, so a pin
 * hidden in a comment cannot vouch for a different active pin. Refused: a depot pinned more than once,
 * and a lua without an active `addappid(<appid>)` (stricter than LuaTools' install policy, because a
 * shared copy comes from another user).
 */
export function luaPins(text: string, appid: string): Record<string, string> {
  const pins: Record<string, string> = {};
  let declared = false;
  for (const raw of text.replace(/^﻿/, "").split(/\r\n|\r|\n/)) {
    const line = raw.trim();
    if (line.includes("[[") || /\[=+\[/.test(line)) invalid("Lua block strings and comments are not allowed.");
    if (!line || line.startsWith("--")) continue;
    if (!STATEMENT.test(line)) invalid("Lua may contain only addappid, addtoken and setManifestid declarations.");
    const app = ADDAPPID.exec(line);
    if (app && Number(app[1]) === Number(appid)) declared = true;
    if (!/^setManifestid/i.test(line)) continue;
    const pin = PIN.exec(line);
    if (!pin) invalid("Invalid setManifestid declaration.");
    const depot = pin[1]!;
    if (depot in pins) invalid("A depot is pinned more than once.");
    pins[depot] = pin[2] ?? pin[3]!;
  }
  if (!declared) invalid("Lua does not actively declare this app.");
  return Object.fromEntries(Object.keys(pins).sort((a, b) => Number(a) - Number(b)).map(depot => [depot, pins[depot]!]));
}

/** Validates a manifest package (one .lua, Steam .manifest files, small .txt/.md docs) and reads its pins. */
export function inspectLua(bytes: Uint8Array, appid: string): { pins: Record<string, string>; manifests: string[] } {
  const entries = centralDirectory(bytes);
  if (!entries.length || entries.length > MAX_ENTRIES) invalid("Archive entry count exceeds its limit.");
  const names = new Set<string>(), targets = new Set<string>(), manifests: string[] = [];
  let total = 0, lua: CentralEntry | null = null;
  for (const entry of entries) {
    if (entry.flags & 0x1) invalid("Encrypted ZIP entries are not supported.");
    const path = entryPath(entry.name);
    if (names.has(path.toLowerCase())) invalid("Duplicate archive entry.");
    names.add(path.toLowerCase());
    if (entry.size > Math.max(1024 * 1024, entry.compressedSize * 200)) invalid("Archive entry exceeds expansion limits.");
    total += entry.size;
    if (total > MAX_EXPANDED) invalid("Archive exceeds its expansion limit.");
    if (path.endsWith("/")) { if (entry.size !== 0) invalid("Archive directory contains data."); continue; }
    const file = path.slice(path.lastIndexOf("/") + 1);
    if (isDoc(file)) { if (entry.size > MAX_DOC) invalid("Documentation exceeds its size limit."); continue; }
    if (targets.has(file.toLowerCase())) invalid("Duplicate manifest install target.");
    targets.add(file.toLowerCase());
    if (/\.lua$/i.test(file)) {
      if (lua) invalid("A package may contain only one Lua file.");
      if (entry.size > MAX_LUA) invalid("Lua exceeds its size limit.");
      lua = entry;
    } else if (/\.manifest$/i.test(file)) {
      if (!MANIFEST_FILE.test(file) || entry.size > MAX_MANIFEST) invalid("Invalid Steam manifest entry.");
      manifests.push(file.toLowerCase());
    } else invalid("A package may contain only Lua data, Steam manifests and plain text documentation.");
  }
  if (!lua) invalid("The package contains no Lua file.");
  let text: string;
  try { text = new TextDecoder("utf-8", { fatal: true }).decode(readEntry(bytes, lua, MAX_LUA)); }
  catch (error) { if (error instanceof TypeError) invalid("Lua is not UTF-8."); throw error; }
  return { pins: luaPins(text, appid), manifests: manifests.sort() };
}

/**
 * The build a validated package describes: its active pins plus the `<depot>_<manifest>.manifest` files
 * it carries. Two packages with the same build differ only in ways a client's freshness check ignores.
 */
export function sameBuild(a: { pins: Record<string, string>; manifests: string[] }, b: { pins: Record<string, string>; manifests: string[] }): boolean {
  return JSON.stringify(a.pins) === JSON.stringify(b.pins) && JSON.stringify(a.manifests) === JSON.stringify(b.manifests);
}

export const luaRoutes: Record<string, DetachedRouteDefinition> = {
  "PUT /api/apps/:appid/lua": route(async (request, ctx) => {
    const url = new URL(request.url), q = url.searchParams, appid = appId(url.pathname);
    // Authenticate before reading up to 90 MiB from an anonymous caller.
    await ctx.write(slot => authenticate(slot).id);
    const sha256 = hex64(q.get("sha256"), "SHA-256");
    const source = label(q.get("source"), 64), deviceId = label(q.get("deviceId"));
    if (request.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase() !== "application/zip") invalid("Expected application/zip.");
    const body = await limitedBody(request, MAX_LUA_ARCHIVE, "Lua package exceeds 90 MiB.");
    if (new Bun.CryptoHasher("sha256").update(body).digest("hex") !== sha256) invalid("SHA-256 does not match the upload.");
    const { pins } = inspectLua(body, appid);
    const { meta } = await ctx.write(async slot => {
      const user = authenticate(slot);
      // The same package again (another user installing the cached copy's source) changes nothing.
      const device = slot.db.select().from(devices).where(and(eq(devices.userId, user.id), eq(devices.deviceId, deviceId))).get();
      return storeLua(slot, appid, body, sha256, pins, source, { userId: user.id, deviceId, deviceName: device?.name ?? deviceId });
    });
    return json(meta);
  }),
  "GET /api/apps/:appid/lua/meta": route(async (request, ctx) => {
    const appid = appId(new URL(request.url).pathname);
    const meta = await ctx.write(slot => {
      authenticate(slot);
      const row = luaVersions(slot, appid)[0];
      return row ? luaMeta(slot, row) : null;
    });
    return meta ? json(meta) : json({ error: "not_found" }, 404);
  }),
  "GET /api/apps/:appid/lua": route(async (request, ctx) => {
    const url = new URL(request.url), appid = appId(url.pathname);
    const wanted = hex64(url.searchParams.get("sha256"), "SHA-256", true);
    const found = await ctx.write(async slot => {
      authenticate(slot);
      const all = luaVersions(slot, appid);
      const row = wanted ? all.find(version => version.sha256 === wanted) : all[0];
      if (!row) return null;
      // Capture the bytes in the slot: retention may delete this blob later.
      return { row, bytes: await withInternalStorage(slot, async () => (await slot.storage.get(luaZips, row.fileId)).arrayBuffer()) };
    });
    if (!found) return json({ error: "not_found" }, 404);
    return new Response(found.bytes, { headers: { "content-type": "application/zip", etag: found.row.sha256,
      "content-length": String(found.row.size) } });
  }),
};
