import { and, asc, eq, gt, sql, BedrockError, detached, type DetachedContext, type DetachedRouteDefinition, type FunctionContext } from "bedrock";
import { authenticate } from "./auth";
import { cloudFiles } from "./schema";
import { MAX_CLOUD_FILE, cloudBlobs, withInternalStorage } from "./storage";
import { invalid } from "./validation";
import { appOfCloudPath, touch } from "./activity";

export const CLOUD_QUOTA = 10 * 1024 * 1024 * 1024;
const PAGE = 1000;
const FILES = "/api/cloud/files/";

type Row = typeof cloudFiles.$inferSelect;
const info = (row: Pick<Row, "path" | "size" | "sha256" | "updatedAt">) =>
  ({ path: row.path, size: row.size, sha256: row.sha256, modifiedTime: Math.floor(row.updatedAt / 1000) });
const fileKey = (userId: string, path: string) => and(eq(cloudFiles.userId, userId), eq(cloudFiles.path, path));

// Paths are CloudRedirect's own relative keys; each URL segment is encoded separately
// so a decoded "/" can never smuggle in an extra segment.
export function cloudPath(pathname: string): string {
  if (!pathname.startsWith(FILES)) invalid("Invalid path.");
  const segments = pathname.slice(FILES.length).split("/").map(segment => {
    let decoded = "";
    try { decoded = decodeURIComponent(segment); } catch { invalid("Invalid path encoding."); }
    if (!decoded || decoded === "." || decoded === ".." || /[\\/\u0000-\u001f\u007f]/.test(decoded) || Buffer.byteLength(decoded) > 255) invalid("Invalid path segment.");
    return decoded;
  });
  const path = segments.join("/");
  if (Buffer.byteLength(path) > 1024) invalid("Path is too long.");
  return path;
}

export function usage(ctx: FunctionContext, userId: string): { files: number; bytes: number } {
  const row = ctx.db.select({ files: sql<number>`count(*)`, bytes: sql<number>`coalesce(sum(${cloudFiles.size}), 0)` })
    .from(cloudFiles).where(eq(cloudFiles.userId, userId)).get();
  return { files: row?.files ?? 0, bytes: row?.bytes ?? 0 };
}

// CloudRedirect keys files as <accountId>/<appId>/…; group a user's files by that app id.
const rest = sql`substr(${cloudFiles.path}, instr(${cloudFiles.path}, '/') + 1)`;
const appOfPath = sql<string>`substr(${rest}, 1, instr(${rest}, '/') - 1)`;
export function cloudApps(ctx: FunctionContext, userId?: string): Record<string, { files: number; bytes: number; updatedAt: number }> {
  const rows = ctx.db.select({ appid: appOfPath, files: sql<number>`count(*)`, bytes: sql<number>`coalesce(sum(${cloudFiles.size}), 0)`,
    updatedAt: sql<number>`coalesce(max(${cloudFiles.updatedAt}), 0)` })
    .from(cloudFiles).where(userId === undefined ? undefined : eq(cloudFiles.userId, userId)).groupBy(appOfPath).all();
  return Object.fromEntries(rows.filter(row => /^\d{1,10}$/.test(row.appid)).map(({ appid, files, bytes, updatedAt }) => [appid, { files, bytes, updatedAt }]));
}

export async function deleteAll(ctx: FunctionContext, userId: string): Promise<number> {
  const rows = ctx.db.select().from(cloudFiles).where(eq(cloudFiles.userId, userId)).all();
  ctx.db.delete(cloudFiles).where(eq(cloudFiles.userId, userId)).run();
  await withInternalStorage(ctx, async () => { for (const row of rows) await ctx.storage.delete(cloudBlobs, row.fileId); });
  return rows.length;
}

async function limitedBody(request: Request): Promise<Uint8Array<ArrayBuffer>> {
  if (Number(request.headers.get("content-length")) > MAX_CLOUD_FILE) invalid("File exceeds 90 MiB.");
  const reader = request.body?.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  while (reader) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > MAX_CLOUD_FILE) { await reader.cancel(); invalid("File exceeds 90 MiB."); }
    chunks.push(value);
  }
  return Buffer.concat(chunks) as Uint8Array<ArrayBuffer>;
}

function failure(error: unknown): Response {
  if (!(error instanceof BedrockError)) throw error;
  const status = { UNAUTHENTICATED: 401, INVALID_ARGS: 400, QUOTA_EXCEEDED: 507 }[error.code];
  if (!status) throw error;
  const message = status === 401 ? "unauthorized" : status === 507 ? "quota_exceeded" : error.message;
  return Response.json({ error: message }, { status, headers: { "cache-control": "no-store" } });
}

// File bodies are read and hashed outside the serialized queue; only database and
// local storage work runs in short read/write slots.
function cloud(handler: (request: Request, ctx: DetachedContext) => Promise<Response>): DetachedRouteDefinition {
  return detached(async (request, _server, ctx) => {
    try {
      const response = await handler(request, ctx);
      response.headers.set("cache-control", "no-store");
      return response;
    } catch (error) { return failure(error); }
  });
}
const userOf = (ctx: DetachedContext) => ctx.write(slot => authenticate(slot).id);
const notFound = () => Response.json({ error: "not_found" }, { status: 404 });

export const cloudRoutes: Record<string, DetachedRouteDefinition> = {
  "PUT /api/cloud/files/*": cloud(async (request, ctx) => {
    const path = cloudPath(new URL(request.url).pathname);
    await userOf(ctx);
    const declared = request.headers.get("x-content-sha256");
    if (declared !== null && !/^[a-fA-F0-9]{64}$/.test(declared)) invalid("Invalid X-Content-SHA256.");
    const body = await limitedBody(request);
    const sha256 = new Bun.CryptoHasher("sha256").update(body).digest("hex");
    if (declared !== null && declared.toLowerCase() !== sha256) invalid("SHA-256 does not match the upload.");
    // Authenticate again in the write slot: a token revoked mid-upload must not land a file.
    const stored = await ctx.write(async slot => {
      const userId = authenticate(slot).id;
      const current = slot.db.select().from(cloudFiles).where(fileKey(userId, path)).get();
      if (usage(slot, userId).bytes - (current?.size ?? 0) + body.length > CLOUD_QUOTA)
        throw new BedrockError("QUOTA_EXCEEDED", "quota_exceeded", "Delete cloud files from /admin or raise CLOUD_QUOTA.");
      return withInternalStorage(slot, async () => {
        const file = await slot.storage.put(cloudBlobs, new Blob([body]), { name: path.split("/").pop()!, mime: "application/octet-stream" });
        const values = { userId, path, fileId: file.id, sha256, size: body.length, updatedAt: Date.now() };
        slot.db.insert(cloudFiles).values(values).onConflictDoUpdate({ target: [cloudFiles.userId, cloudFiles.path], set: values }).run();
        // Steam uploads cloud files when a game exits.
        const appid = appOfCloudPath(path);
        if (appid) touch(slot, userId, appid, true, values.updatedAt);
        if (current) await slot.storage.delete(cloudBlobs, current.fileId);
        return values;
      });
    });
    return Response.json(info(stored));
  }),
  "GET /api/cloud/files/*": cloud(async (request, ctx) => {
    const path = cloudPath(new URL(request.url).pathname);
    const head = request.method === "HEAD";
    const found = await ctx.write(async slot => {
      const row = slot.db.select().from(cloudFiles).where(fileKey(authenticate(slot).id, path)).get();
      if (!row) return null;
      // Capture the bytes inside the slot: a later replacement deletes this file.
      const bytes = head ? null : await withInternalStorage(slot, async () => (await slot.storage.get(cloudBlobs, row.fileId)).arrayBuffer());
      return { row, bytes };
    });
    if (!found) return head ? new Response(null, { status: 404 }) : notFound();
    const { row, bytes } = found;
    return new Response(bytes, { headers: { "content-type": "application/octet-stream", "content-length": String(row.size),
      etag: `"${row.sha256}"`, "last-modified": new Date(row.updatedAt).toUTCString() } });
  }),
  "DELETE /api/cloud/files/*": cloud(async (request, ctx) => {
    const path = cloudPath(new URL(request.url).pathname);
    await ctx.write(async slot => {
      const userId = authenticate(slot).id;
      const row = slot.db.select().from(cloudFiles).where(fileKey(userId, path)).get();
      if (!row) return;
      slot.db.delete(cloudFiles).where(fileKey(userId, path)).run();
      await withInternalStorage(slot, () => slot.storage.delete(cloudBlobs, row.fileId));
    });
    return Response.json({ ok: true });
  }),
  "GET /api/cloud/list": cloud(async (request, ctx) => {
    const params = new URL(request.url).searchParams;
    const prefix = params.get("prefix") ?? "", cursor = params.get("cursor");
    if (Buffer.byteLength(prefix) > 1024 || (cursor !== null && Buffer.byteLength(cursor) > 1024)) invalid("Prefix or cursor is too long.");
    const rows = await ctx.write(slot => {
      const userId = authenticate(slot).id;
      return slot.db.select().from(cloudFiles).where(and(eq(cloudFiles.userId, userId),
        sql`substr(${cloudFiles.path}, 1, length(${prefix})) = ${prefix}`,
        cursor === null ? undefined : gt(cloudFiles.path, cursor))).orderBy(asc(cloudFiles.path)).limit(PAGE + 1).all();
    });
    const page = rows.slice(0, PAGE);
    return Response.json({ files: page.map(info), next: rows.length > PAGE ? page[page.length - 1]!.path : null });
  }),
  "GET /api/cloud/usage": cloud(async (_request, ctx) =>
    Response.json({ ...await ctx.write(slot => usage(slot, authenticate(slot).id)), quota: CLOUD_QUOTA })),
  "GET /api/cloud/apps": cloud(async (_request, ctx) =>
    Response.json({ apps: Object.fromEntries(Object.entries(await ctx.write(slot => cloudApps(slot, authenticate(slot).id)))
      .map(([appid, { files, bytes }]) => [appid, { files, bytes }])) })),
};
