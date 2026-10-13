import { bucket, type FunctionContext } from "bedrock";

export const MAX_SAVE = 90 * 1024 * 1024;
const internal = new WeakSet<FunctionContext>();
export const saveZips = bucket("saves", { maxSize: MAX_SAVE, accept: ["application/zip"], access: ctx => internal.has(ctx) });
export async function withSaveStorage<T>(ctx: FunctionContext, action: () => Promise<T>): Promise<T> {
  internal.add(ctx);
  const identity = ctx.user;
  // Null ownership prevents Bedrock's generic owner-delete shortcut exposing saves.
  ctx.user = null;
  try { return await action(); } finally { internal.delete(ctx); ctx.user = identity; }
}
