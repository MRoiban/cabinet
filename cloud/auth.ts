import { and, eq, isNull, BedrockError, type FunctionContext } from "bedrock";
import { tokens, users } from "./schema";

export function hashToken(token: string): string { return new Bun.CryptoHasher("sha256").update(token).digest("hex"); }
export function newToken(ctx: FunctionContext, userId: string): string {
  const token = "cab_" + Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
  ctx.db.insert(tokens).values({ hash: hashToken(token), userId, createdAt: Date.now() }).run();
  return token;
}
export function authenticate(ctx: FunctionContext) {
  const match = /^Bearer (cab_[A-Za-z0-9_-]{43})$/.exec(ctx.request.headers.get("authorization") ?? "");
  const token = match ? ctx.db.select().from(tokens).where(and(eq(tokens.hash, hashToken(match[1]!)), isNull(tokens.revokedAt))).get() : undefined;
  const user = token ? ctx.db.select().from(users).where(eq(users.id, token.userId)).get() : undefined;
  if (!user) throw new BedrockError("UNAUTHENTICATED", "unauthorized", "Connect Cabinet using an active connect link.");
  ctx.db.update(users).set({ lastSeen: Date.now() }).where(eq(users.id, user.id)).run();
  return user;
}
export function creator(ctx: FunctionContext): boolean {
  // The daemon verifies the session and signs identity, including on public pebbles.
  const creators: unknown = JSON.parse(process.env.BEDROCK_CREATORS ?? "[]");
  return !!ctx.user?.email && Array.isArray(creators) && creators.some(email => typeof email === "string" && email.toLowerCase() === ctx.user!.email!.toLowerCase());
}
