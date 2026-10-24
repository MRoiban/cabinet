import { and, eq, gte, isNull, mutation, query, BedrockError, type FunctionContext } from "bedrock";
import { creator, newToken } from "./auth";
import { presence, tokens, users } from "./schema";
import { allStates } from "./api";
import { CLOUD_QUOTA, cloudApps, deleteAll, usage } from "./cloud";
import { namesFor } from "./names";
import { activityFor } from "./activity";
import { createCode } from "./pairing";
import { args, label } from "./validation";

export type UserStatus = "active" | "unconnected" | "revoked";
const PLAYING_WINDOW = 3 * 60 * 1000;

function gate(ctx: FunctionContext) {
  if (!ctx.user) throw new BedrockError("UNAUTHENTICATED", "Sign in to manage Cabinet.", "Sign in with a Bedrock creator account.");
  if (!creator(ctx)) throw new BedrockError("FORBIDDEN", "This account can't manage Cabinet.", "Sign in with a Bedrock creator account.");
}
function find(ctx: FunctionContext, id: string) {
  const user = ctx.db.select().from(users).where(eq(users.id, id)).get();
  if (!user) throw new BedrockError("USER_NOT_FOUND", "This user no longer exists.", "Reload the user list.");
  return user;
}
const activeTokens = (id: string) => and(eq(tokens.userId, id), isNull(tokens.revokedAt));
// The client prefixes location.origin; the raw token appears only in this one response.
const connectLink = (token: string) => `/connect#${token}`;

function summary(ctx: FunctionContext, user: typeof users.$inferSelect) {
  const connections = ctx.db.select().from(tokens).where(activeTokens(user.id)).all().length;
  const status: UserStatus = !connections ? "revoked" : user.lastSeen ? "active" : "unconnected";
  const states = allStates(ctx, user.id), cloud = cloudApps(ctx, user.id);
  const ids = new Set([...Object.keys(states), ...Object.keys(cloud)]);
  return {
    id: user.id, name: user.name, createdAt: user.createdAt, lastSeen: user.lastSeen, status, connections,
    games: ids.size, achievements: Object.values(states).reduce((sum, state) => sum + Object.keys(state.achievements).length, 0),
    playtimeMinutes: Object.values(states).reduce((sum, state) => sum + state.playtimeTotal, 0),
    cloud: usage(ctx, user.id), states, cloudApps: cloud, appids: [...ids],
  };
}

export const adminQueries = {
  adminOverview: query(ctx => {
    gate(ctx);
    const rank = { active: 0, unconnected: 1, revoked: 2 };
    const rows = ctx.db.select().from(users).all().map(user => summary(ctx, user))
      .sort((a, b) => rank[a.status] - rank[b.status] || (b.lastSeen ?? 0) - (a.lastSeen ?? 0) || a.name.localeCompare(b.name));
    const week = Date.now() - 7 * 24 * 60 * 60 * 1000;
    const names = Object.fromEntries(rows.map(row => [row.id, row.name]));
    const live = ctx.db.select().from(presence).where(gte(presence.updatedAt, Date.now() - PLAYING_WINDOW)).all();
    return {
      users: rows.map(({ states: _s, cloudApps: _c, appids: _a, ...row }) => row),
      totals: {
        users: rows.length, activeThisWeek: rows.filter(row => (row.lastSeen ?? 0) >= week).length,
        games: new Set(rows.flatMap(row => row.appids)).size, achievements: rows.reduce((sum, row) => sum + row.achievements, 0),
        playtimeMinutes: rows.reduce((sum, row) => sum + row.playtimeMinutes, 0),
        cloud: { files: rows.reduce((sum, row) => sum + row.cloud.files, 0), bytes: rows.reduce((sum, row) => sum + row.cloud.bytes, 0) },
      },
      playing: live.map(row => ({ userId: row.userId, userName: names[row.userId] ?? "", appId: row.appId, name: row.name })),
      quota: CLOUD_QUOTA,
    };
  }),
  adminUser: query(args(body => ({ id: label(body.id) })), (ctx, { id }) => {
    gate(ctx);
    const { states, cloudApps: cloud, appids, ...user } = summary(ctx, find(ctx, id));
    const names = namesFor(ctx, appids), activity = activityFor(ctx, id);
    const games = appids.map(appid => {
      const state = states[appid], save = state?.save ?? null, archive = state?.archive ?? null, files = cloud[appid] ?? null;
      // Newest real save: CloudRedirect files, a format-2 archive, or a legacy ZIP that isn't empty.
      const lastSaved = Math.max(files?.updatedAt ?? 0, archive && !archive.deleted ? archive.createdAt : 0, save && !save.empty ? save.updatedAt : 0) || null;
      const lastUnlock = Math.max(0, ...Object.values(state?.achievements ?? {})) * 1000;
      return {
        appid, name: names[appid] ?? null, achievements: Object.keys(state?.achievements ?? {}).length,
        playtimeMinutes: state?.playtimeTotal ?? 0, save, archive, cloud: files,
        lastPlayed: Math.max(activity[appid]?.lastPlayed ?? 0, lastUnlock, lastSaved ?? 0) || null,
        lastSaved, addedAt: activity[appid]?.firstSeen ?? null,
      };
    }).sort((a, b) => (b.lastPlayed ?? 0) - (a.lastPlayed ?? 0) || b.playtimeMinutes - a.playtimeMinutes);
    return { user, games, cloud: user.cloud, cloudGames: Object.keys(cloud).length, quota: CLOUD_QUOTA };
  }),
};

const byId = args(body => ({ id: label(body.id) }));
function revokeAll(ctx: FunctionContext, id: string) {
  ctx.db.update(tokens).set({ revokedAt: Date.now() }).where(activeTokens(id)).run();
}
export const adminMutations = {
  createUser: mutation(args(body => ({ name: label(body.name).trim() })), (ctx, { name }) => {
    gate(ctx);
    const user = { id: crypto.randomUUID(), name, createdAt: Date.now(), lastSeen: null };
    ctx.db.insert(users).values(user).run();
    return { user, link: connectLink(newToken(ctx, user.id)), ...createCode(ctx, user.id) };
  }),
  newConnectLink: mutation(byId, (ctx, { id }) => {
    gate(ctx); find(ctx, id); revokeAll(ctx, id);
    return { link: connectLink(newToken(ctx, id)) };
  }),
  pairingCode: mutation(byId, (ctx, { id }) => { gate(ctx); find(ctx, id); return createCode(ctx, id); }),
  revokeAccess: mutation(byId, (ctx, { id }) => { gate(ctx); find(ctx, id); revokeAll(ctx, id); return { ok: true }; }),
  deleteCloudFiles: mutation(byId, async (ctx, { id }) => { gate(ctx); find(ctx, id); return { deleted: await deleteAll(ctx, id) }; }),
};
