import { sqliteTable, text, integer, primaryKey } from "bedrock";

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
