import { bucket, type FunctionContext } from "bedrock";

export const MAX_SAVE = 90 * 1024 * 1024;
export const MAX_CLOUD_FILE = 90 * 1024 * 1024;
const internal = new WeakSet<FunctionContext>();
export const saveZips = bucket("saves", { maxSize: MAX_SAVE, accept: ["application/zip"], access: ctx => internal.has(ctx) });
// Format-2 archives get their own internal bucket so legacy and versioned saves stay distinguishable.
export const archiveZips = bucket("archives", { maxSize: MAX_SAVE, accept: ["application/zip"], access: ctx => internal.has(ctx) });
export const cloudBlobs = bucket("cloud", { maxSize: MAX_CLOUD_FILE, access: ctx => internal.has(ctx) });
export async function withInternalStorage<T>(ctx: FunctionContext, action: () => Promise<T>): Promise<T> {
  internal.add(ctx);
  const identity = ctx.user;
  // Null ownership prevents Bedrock's generic owner-delete shortcut exposing internal files.
  ctx.user = null;
  try { return await action(); } finally { internal.delete(ctx); ctx.user = identity; }
}
