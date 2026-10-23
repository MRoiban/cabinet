import { and, eq, gt, isNull, lt, BedrockError, type FunctionContext, type RouteHandler } from "bedrock";
import { hashToken, newToken } from "./auth";
import { api } from "./api";
import { pairingCodes, users } from "./schema";
import { invalid, jsonBody, label } from "./validation";

export const PAIRING_TTL = 15 * 60 * 1000;
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const FAILURE_LIMIT = 30;
// Server-wide, in memory: 60-bit codes that expire in 15 minutes need only a coarse brake.
const failures: number[] = [];

export function newCode(): string {
  // 256 is a multiple of 32, so masking each byte keeps every character equally likely.
  const raw = [...crypto.getRandomValues(new Uint8Array(12))].map(byte => ALPHABET[byte & 31]).join("");
  return `${raw.slice(0, 4)}-${raw.slice(4, 8)}-${raw.slice(8)}`;
}
export function normalizeCode(value: unknown): string {
  if (typeof value !== "string" || value.length > 40) invalid("Invalid pairing code.");
  const code = value.toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (!/^[0-9A-HJKMNP-TV-Z]{12}$/.test(code)) invalid("Invalid pairing code.");
  return code;
}
export function createCode(ctx: FunctionContext, userId: string): { code: string; expiresAt: number } {
  const code = newCode(), now = Date.now();
  ctx.db.delete(pairingCodes).where(lt(pairingCodes.expiresAt, now)).run();
  ctx.db.insert(pairingCodes).values({ hash: hashToken(normalizeCode(code)), userId, createdAt: now, expiresAt: now + PAIRING_TTL }).run();
  return { code, expiresAt: now + PAIRING_TTL };
}
const reply = (status: number, body: unknown) => Response.json(body, { status, headers: { "cache-control": "no-store" } });

export const pairingRoutes: Record<string, RouteHandler> = {
  "POST /api/pairing-codes": api((_request, ctx, id) => Response.json(createCode(ctx, id))),
  "POST /api/pair": async (request, _server, ctx) => {
    const now = Date.now();
    while (failures.length && failures[0]! < now - 60_000) failures.shift();
    if (failures.length >= FAILURE_LIMIT) return reply(429, { error: "too_many_attempts" });
    try {
      const body = await jsonBody(request);
      const code = normalizeCode(body.code);
      if (body.deviceName !== undefined) label(body.deviceName, 100);
      const row = ctx.db.select().from(pairingCodes).where(and(eq(pairingCodes.hash, hashToken(code)),
        isNull(pairingCodes.usedAt), gt(pairingCodes.expiresAt, now))).get();
      if (!row) { failures.push(now); return reply(401, { error: "invalid_code" }); }
      ctx.db.update(pairingCodes).set({ usedAt: now }).where(eq(pairingCodes.hash, row.hash)).run();
      const user = ctx.db.select().from(users).where(eq(users.id, row.userId)).get()!;
      return reply(200, { token: newToken(ctx, user.id), user: { id: user.id, name: user.name } });
    } catch (error) {
      if (error instanceof BedrockError && error.code === "INVALID_ARGS") return reply(400, { error: error.message });
      throw error;
    }
  },
};
