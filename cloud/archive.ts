import { and, desc, eq, BedrockError, detached, type DetachedContext, type DetachedRouteDefinition, type FunctionContext } from "bedrock";
import { authenticate } from "./auth";
import { devices, saveArchives } from "./schema";
import { MAX_SAVE, archiveZips, withInternalStorage } from "./storage";
import { appId, invalid, label } from "./validation";
import { centralDirectory, readEntry } from "./zip";

// Format-2 save archives (docs/save-sync.md). Old clients only know /save, so they never see these.
export const KEEP_VERSIONS = 10;
export const MANIFEST_NAME = "cabinet-save.json";
const MAX_MANIFEST = 1024 * 1024;
const MAX_FILES = 10000;
const MAX_EXPANDED = 512 * 1024 * 1024;
export const EMPTY_HASH = new Bun.CryptoHasher("sha256").update("").digest("hex");

type Row = typeof saveArchives.$inferSelect;
export interface ArchiveMeta {
  id: string; sha256: string; contentHash: string; size: number; files: number; bytes: number;
  createdAt: number; deviceId: string; deviceName: string; deleted: boolean;
}
export function archiveMeta(row: Row): ArchiveMeta {
  const { id, sha256, contentHash, size, files, bytes, createdAt, deviceId, deviceName } = row;
  return { id, sha256, contentHash, size, files, bytes, createdAt, deviceId, deviceName, deleted: row.deleted === 1 };
}
const appVersions = (userId: string, appid: string) => and(eq(saveArchives.userId, userId), eq(saveArchives.appid, appid));
export function versions(ctx: FunctionContext, userId: string, appid: string): Row[] {
  // createdAt is strictly increasing per user and app (see PUT), so it orders versions.
  return ctx.db.select().from(saveArchives).where(appVersions(userId, appid)).orderBy(desc(saveArchives.createdAt)).all();
}
export function newestArchive(ctx: FunctionContext, userId: string, appid: string): ArchiveMeta | null {
  const row = ctx.db.select().from(saveArchives).where(appVersions(userId, appid))
    .orderBy(desc(saveArchives.createdAt)).limit(1).get();
  return row ? archiveMeta(row) : null;
}

const RESERVED = /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$/i;
/** The archive path rules from docs/save-sync.md: relative, "/"-separated, Windows-safe segments. */
export function safePath(path: unknown): string {
  if (typeof path !== "string" || !path || Buffer.byteLength(path) > 1024) invalid("Invalid archive path.");
  for (const segment of path.split("/")) {
    if (!segment || segment === "." || segment === ".." || /[\u0000-\u001f\u007f<>:"|?*\\]/.test(segment)
      || /[. ]$/.test(segment) || RESERVED.test(segment) || Buffer.byteLength(segment) > 255) invalid("Invalid archive path.");
  }
  return path;
}
const nonNegative = (value: unknown, what: string) => {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) invalid(`Invalid ${what}.`);
  return value;
};

export interface ArchiveManifest { contentHash: string; files: number; bytes: number }
/** Validates a format-2 archive end to end without extracting save files. */
export function inspectArchive(bytes: Uint8Array, appid: string): ArchiveManifest {
  const entries = centralDirectory(bytes);
  const first = entries[0];
  if (!first || first.name !== MANIFEST_NAME || first.localOffset !== 0) invalid("The first entry must be cabinet-save.json.");
  let manifest: Record<string, unknown>;
  try {
    const value: unknown = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(readEntry(bytes, first, MAX_MANIFEST)));
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error();
    manifest = value as Record<string, unknown>;
  } catch (error) {
    if (error instanceof BedrockError) throw error;
    invalid("cabinet-save.json is not a JSON object.");
  }
  if (manifest.format !== "cabinet-save" || manifest.version !== 2) invalid("Unsupported archive format.");
  if (manifest.gameId !== `steam:${appid}`) invalid("Archive gameId does not match the app.");
  if (!Array.isArray(manifest.roots) || manifest.roots.length > 256) invalid("Invalid archive roots.");
  const rootIds = new Set<string>();
  const roots = manifest.roots.map(root => {
    if (!root || typeof root !== "object") invalid("Invalid archive root.");
    const { id, source } = root as Record<string, unknown>;
    if (typeof id !== "string" || !id || id.length > 512 || /[\u0000-\u001f\u007f]/.test(id) || rootIds.has(id)) invalid("Invalid archive root id.");
    if (typeof source !== "string" || !/^[a-z]{1,32}$/.test(source)) invalid("Invalid archive root source.");
    rootIds.add(id);
    return id;
  });
  if (!Array.isArray(manifest.files) || manifest.files.length > MAX_FILES) invalid("Invalid archive file list.");
  const lines: string[] = [], expected = new Map<string, number>(), seen = new Set<string>();
  let bytesTotal = 0, previous: { root: number; path: string } | null = null;
  for (const file of manifest.files) {
    if (!file || typeof file !== "object") invalid("Invalid archive file.");
    const f = file as Record<string, unknown>;
    const root = nonNegative(f.root, "root index"), path = safePath(f.path), size = nonNegative(f.size, "file size");
    if (root >= roots.length) invalid("Archive file refers to an unknown root.");
    if (typeof f.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(f.sha256)) invalid("Invalid file SHA-256.");
    if (previous && (root < previous.root || (root === previous.root && !(previous.path < path)))) invalid("Archive files must be sorted and unique.");
    const key = `${root}/${path.toLowerCase()}`;
    if (seen.has(key)) invalid("Archive files must be unique.");
    seen.add(key); previous = { root, path };
    bytesTotal += size;
    if (bytesTotal > MAX_EXPANDED) invalid("Expanded save is too large.");
    lines.push(`${roots[root]}\t${path}\t${size}\t${f.sha256}\n`);
    expected.set(`roots/${root}/${path}`, size);
  }
  const contentHash = new Bun.CryptoHasher("sha256").update(lines.join("")).digest("hex");
  if (manifest.contentHash !== contentHash) invalid("Archive contentHash does not match its file list.");
  if (entries.length !== expected.size + 1) invalid("Archive entries do not match its file list.");
  for (const entry of entries.slice(1)) {
    const size = expected.get(entry.name);
    if (size === undefined || size !== entry.size) invalid("Archive entries do not match its file list.");
    expected.delete(entry.name);
  }
  return { contentHash, files: manifest.files.length, bytes: bytesTotal };
}

async function limitedBody(request: Request): Promise<Uint8Array<ArrayBuffer>> {
  if (Number(request.headers.get("content-length")) > MAX_SAVE) invalid("Save exceeds 90 MiB.");
  const reader = request.body?.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  while (reader) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > MAX_SAVE) { await reader.cancel(); invalid("Save exceeds 90 MiB."); }
    chunks.push(value);
  }
  return Buffer.concat(chunks) as Uint8Array<ArrayBuffer>;
}
const hex64 = (value: string | null, what: string, optional = false) => {
  const v = value ?? "";
  if (optional && v === "") return "";
  if (!/^[a-fA-F0-9]{64}$/.test(v)) invalid(`Invalid ${what}.`);
  return v.toLowerCase();
};
const integer = (value: string | null, what: string) => {
  if (!/^\d{1,15}$/.test(value ?? "")) invalid(`Invalid ${what}.`);
  return Number(value);
};
const json = (body: unknown, status = 200) => Response.json(body, { status, headers: { "cache-control": "no-store" } });

function route(handler: (request: Request, ctx: DetachedContext) => Promise<Response>): DetachedRouteDefinition {
  return detached(async (request, _server, ctx) => {
    try {
      const response = await handler(request, ctx);
      response.headers.set("cache-control", "no-store");
      return response;
    } catch (error) {
      if (!(error instanceof BedrockError)) throw error;
      if (error.code === "UNAUTHENTICATED") return json({ error: "unauthorized" }, 401);
      if (error.code === "INVALID_ARGS") return json({ error: error.message }, 400);
      throw error;
    }
  });
}

export const archiveRoutes: Record<string, DetachedRouteDefinition> = {
  "PUT /api/apps/:appid/archive": route(async (request, ctx) => {
    const url = new URL(request.url), q = url.searchParams, appid = appId(url.pathname);
    await ctx.write(slot => authenticate(slot).id);
    const deviceId = label(q.get("deviceId"));
    const sha256 = hex64(q.get("sha256"), "SHA-256"), contentHash = hex64(q.get("contentHash"), "contentHash");
    const files = integer(q.get("files"), "files"), bytes = integer(q.get("bytes"), "bytes");
    const base = hex64(q.get("base"), "base", true);
    const intent = q.get("intent");
    if (intent !== null && intent !== "delete") invalid("Invalid intent.");
    if (request.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase() !== "application/zip") invalid("Expected application/zip.");
    const body = await limitedBody(request);
    if (new Bun.CryptoHasher("sha256").update(body).digest("hex") !== sha256) invalid("SHA-256 does not match the upload.");
    const manifest = inspectArchive(body, appid);
    if (manifest.contentHash !== contentHash) invalid("contentHash does not match the archive.");
    if (manifest.files !== files || manifest.bytes !== bytes) invalid("files/bytes do not match the archive.");
    // Never let an empty scan replace a backup; only an explicit delete records emptiness.
    if (files === 0 && intent !== "delete") return json({ error: "empty_archive" }, 422);
    if (files !== 0 && intent === "delete") invalid("A delete must not contain files.");
    const result = await ctx.write(async slot => {
      const userId = authenticate(slot).id;
      const all = versions(slot, userId, appid), current = all[0];
      if (current && current.contentHash === contentHash) return { status: 200, body: archiveMeta(current) };
      if (current && current.contentHash !== base) return { status: 409, body: { current: archiveMeta(current) } };
      if (!current && intent === "delete") return { status: 404, body: { error: "not_found" } };
      const device = slot.db.select().from(devices).where(and(eq(devices.userId, userId), eq(devices.deviceId, deviceId))).get();
      return withInternalStorage(slot, async () => {
        const file = await slot.storage.put(archiveZips, new Blob([body]), { name: `${appid}.zip`, mime: "application/zip" });
        const values = { id: crypto.randomUUID(), userId, appid, fileId: file.id, sha256, contentHash, size: body.length,
          files, bytes, createdAt: Math.max(Date.now(), (current?.createdAt ?? 0) + 1), deviceId, deviceName: device?.name ?? deviceId,
          baseHash: base || null, deleted: intent === "delete" ? 1 : 0 };
        slot.db.insert(saveArchives).values(values).run();
        // Retention: newest KEEP_VERSIONS stay; blob deletion runs after the commit.
        for (const old of all.slice(KEEP_VERSIONS - 1)) {
          slot.db.delete(saveArchives).where(eq(saveArchives.id, old.id)).run();
          await slot.storage.delete(archiveZips, old.fileId);
        }
        const row = slot.db.select().from(saveArchives).where(eq(saveArchives.id, values.id)).get()!;
        return { status: 200, body: archiveMeta(row) };
      });
    });
    return json(result.body, result.status);
  }),
  "GET /api/apps/:appid/archive": route(async (request, ctx) => {
    const url = new URL(request.url), appid = appId(url.pathname), id = url.searchParams.get("id");
    if (id !== null && !/^[0-9a-f-]{36}$/.test(id)) invalid("Invalid archive id.");
    const found = await ctx.write(async slot => {
      const userId = authenticate(slot).id;
      const row = id === null ? versions(slot, userId, appid)[0]
        : slot.db.select().from(saveArchives).where(and(appVersions(userId, appid), eq(saveArchives.id, id))).get();
      if (!row) return null;
      // Capture the bytes in the slot: retention may delete this blob later.
      return { row, bytes: await withInternalStorage(slot, async () => (await slot.storage.get(archiveZips, row.fileId)).arrayBuffer()) };
    });
    if (!found) return json({ error: "not_found" }, 404);
    return new Response(found.bytes, { headers: { "content-type": "application/zip", etag: found.row.sha256,
      "content-length": String(found.row.size), "x-cabinet-archive-id": found.row.id } });
  }),
  "GET /api/apps/:appid/archives": route(async (request, ctx) => {
    const appid = appId(new URL(request.url).pathname);
    const rows = await ctx.write(slot => versions(slot, authenticate(slot).id, appid));
    return json({ versions: rows.map(archiveMeta) });
  }),
};
