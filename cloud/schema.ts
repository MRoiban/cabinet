import { sqliteTable, text, integer, primaryKey, index } from "bedrock";

export const users = sqliteTable("users", {
  id: text("id").primaryKey(), name: text("name").notNull(),
  createdAt: integer("created_at").notNull(), lastSeen: integer("last_seen"),
});
export const tokens = sqliteTable("tokens", {
  hash: text("hash").primaryKey(), userId: text("user_id").notNull().references(() => users.id),
  createdAt: integer("created_at").notNull(), revokedAt: integer("revoked_at"),
});
export const apps = sqliteTable("apps", {
  userId: text("user_id").notNull().references(() => users.id), appid: text("appid").notNull(),
  achievements: text("achievements", { mode: "json" }).$type<Record<string, number>>().notNull(),
  stats: text("stats", { mode: "json" }).$type<Record<string, number>>().notNull(),
  statsUpdatedAt: integer("stats_updated_at").notNull(),
  playtime: text("playtime", { mode: "json" }).$type<Record<string, number>>().notNull(),
}, t => [primaryKey({ columns: [t.userId, t.appid] })]);
export const devices = sqliteTable("devices", {
  userId: text("user_id").notNull().references(() => users.id), deviceId: text("device_id").notNull(),
  name: text("name").notNull(),
}, t => [primaryKey({ columns: [t.userId, t.deviceId] })]);
export const saves = sqliteTable("saves", {
  userId: text("user_id").notNull().references(() => users.id), appid: text("appid").notNull(),
  fileId: text("file_id").notNull(), sha256: text("sha256").notNull(), size: integer("size").notNull(),
  updatedAt: integer("updated_at").notNull(), deviceId: text("device_id").notNull(), deviceName: text("device_name").notNull(),
}, t => [primaryKey({ columns: [t.userId, t.appid] })]);

export const presence = sqliteTable("presence", {
  userId: text("user_id").notNull().references(() => users.id),
  accountId: integer("account_id").notNull(), appId: text("app_id").notNull(),
  name: text("name").notNull(), updatedAt: integer("updated_at").notNull(),
}, t => [primaryKey({ columns: [t.userId, t.accountId] })]);

export const cloudFiles = sqliteTable("cloud_files", {
  userId: text("user_id").notNull().references(() => users.id), path: text("path").notNull(),
  fileId: text("file_id").notNull(), sha256: text("sha256").notNull(), size: integer("size").notNull(),
  updatedAt: integer("updated_at").notNull(),
}, t => [primaryKey({ columns: [t.userId, t.path] })]);

export const pairingCodes = sqliteTable("pairing_codes", {
  hash: text("hash").primaryKey(), userId: text("user_id").notNull().references(() => users.id),
  createdAt: integer("created_at").notNull(), expiresAt: integer("expires_at").notNull(), usedAt: integer("used_at"),
});

export const appNames = sqliteTable("app_names", {
  appid: text("appid").primaryKey(), name: text("name").notNull(), updatedAt: integer("updated_at").notNull(),
});

// Format-2 save archives: every version is a row; the newest is current (docs/save-sync.md).
export const saveArchives = sqliteTable("save_archives", {
  id: text("id").primaryKey(), userId: text("user_id").notNull().references(() => users.id), appid: text("appid").notNull(),
  fileId: text("file_id").notNull(), sha256: text("sha256").notNull(), contentHash: text("content_hash").notNull(),
  size: integer("size").notNull(), files: integer("files").notNull(), bytes: integer("bytes").notNull(),
  createdAt: integer("created_at").notNull(), deviceId: text("device_id").notNull(), deviceName: text("device_name").notNull(),
  baseHash: text("base_hash"), deleted: integer("deleted").notNull().default(0),
}, t => [index("save_archives_user_app_created").on(t.userId, t.appid, t.createdAt)]);

// When a game first reached Cabinet and when it was last played, recorded from presence,
// progress and CloudRedirect uploads. Games older than this table fall back to derived times.
export const appActivity = sqliteTable("app_activity", {
  userId: text("user_id").notNull().references(() => users.id), appid: text("appid").notNull(),
  firstSeen: integer("first_seen").notNull(), lastPlayed: integer("last_played"),
}, t => [primaryKey({ columns: [t.userId, t.appid] })]);

// Steam's public achievement list per game (IPlayerService/GetGameAchievements), cached weekly.
export const achievementSchemas = sqliteTable("achievement_schemas", {
  appid: text("appid").primaryKey(),
  achievements: text("achievements", { mode: "json" }).$type<SchemaAchievement[]>().notNull(),
  fetchedAt: integer("fetched_at").notNull(),
});
export type SchemaAchievement = { id: string; name: string; description: string; icon: string | null; iconGray: string | null; hidden: boolean; percent: number | null };
