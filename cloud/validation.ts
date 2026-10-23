import { BedrockError } from "bedrock";

export function invalid(message: string): never {
  throw new BedrockError("INVALID_ARGS", message, "Follow docs/cloud-api.md.");
}
export function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) invalid("Expected a JSON object.");
  return value as Record<string, unknown>;
}
export function label(value: unknown, max = 200): string {
  if (typeof value !== "string" || !value.trim() || value.length > max || /[\u0000-\u001f]/.test(value)) invalid("Invalid name or device identifier.");
  return value;
}
export function timestamp(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) invalid("Expected a nonnegative integer timestamp.");
  return value;
}
export function numericMap(value: unknown, times = false): Record<string, number> {
  const entries = Object.entries(record(value));
  if (entries.length > 10000) invalid("Too many entries.");
  for (const [key, n] of entries) {
    label(key);
    if (typeof n !== "number" || !Number.isFinite(n)) invalid("Expected finite numeric values.");
    if (times) timestamp(n);
  }
  return Object.fromEntries(entries) as Record<string, number>;
}
export function appId(path: string): string {
  const id = path.split("/")[3] ?? "";
  if (!/^\d{1,10}$/.test(id)) invalid("Invalid appid.");
  return id;
}
export async function smallBody(request: Request): Promise<string> {
  const limit = 1024 * 1024;
  if (Number(request.headers.get("content-length")) > limit) invalid("Request is too large.");
  const reader = request.body?.getReader();
  if (!reader) return "";
  const chunks: Uint8Array[] = [];
  let size = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) { await reader.cancel(); invalid("Request is too large."); }
    chunks.push(value);
  }
  return new TextDecoder().decode(Buffer.concat(chunks));
}
export async function jsonBody(request: Request): Promise<Record<string, unknown>> {
  const body = await smallBody(request);
  let value: unknown;
  try { value = JSON.parse(body); } catch { invalid("Malformed JSON."); }
  return record(value);
}

export function accountId(value: unknown): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value <= 0 || value > 0xffffffff) invalid("Invalid Steam accountId.");
  return value;
}
export function presenceBody(body: Record<string, unknown>) {
  const id = accountId(body.accountId);
  if (typeof body.appId !== "string" || (!/^\d{1,10}$/.test(body.appId) || body.appId.trim() !== body.appId)) invalid("Invalid appId.");
  const name = label(body.name, 100);
  if (/[\u007f-\u009f]/.test(name)) invalid("Invalid game name.");
  return { accountId: id, appId: body.appId, name };
}

// A minimal Standard Schema: Bedrock validates function args with it before the handler runs.
export function args<T>(parse: (value: Record<string, unknown>) => T) {
  return { "~standard": { version: 1 as const, vendor: "cabinet", types: undefined as unknown as { input: unknown; output: T },
    validate(value: unknown) {
      try { return { value: parse(record(value)) }; }
      catch (error) { if (error instanceof BedrockError) return { issues: [{ message: error.message }] }; throw error; }
    } } };
}
