CREATE TABLE `hubcap_keys` (
	`id` text PRIMARY KEY NOT NULL,
	`sealed` text NOT NULL,
	`digest` text NOT NULL,
	`hint` text NOT NULL,
	`label` text,
	`added_at` integer NOT NULL,
	`daily_usage` integer,
	`daily_limit` integer,
	`can_make_requests` integer,
	`expires_at` text,
	`stats_at` integer DEFAULT 0 NOT NULL,
	`exhausted` integer DEFAULT 0 NOT NULL,
	`disabled` text,
	`last_error` text,
	`last_error_at` integer,
	`last_used_at` integer
);
CREATE UNIQUE INDEX `hubcap_keys_digest_unique` ON `hubcap_keys` (`digest`);
CREATE TABLE `hubcap_jobs` (
	`id` text PRIMARY KEY NOT NULL,
	`appid` text NOT NULL,
	`seq` integer NOT NULL,
	`state` text NOT NULL,
	`stale` text,
	`error` text,
	`sha256` text,
	`created_at` integer NOT NULL,
	`started_at` integer,
	`finished_at` integer,
	`downloaded_at` integer
);
CREATE INDEX `hubcap_jobs_app` ON `hubcap_jobs` (`appid`,`seq`);
CREATE INDEX `hubcap_jobs_state` ON `hubcap_jobs` (`state`,`seq`);
CREATE TABLE `hubcap_requesters` (
	`job_id` text NOT NULL,
	`user_id` text NOT NULL,
	`requested_at` integer NOT NULL,
	PRIMARY KEY(`job_id`, `user_id`),
	FOREIGN KEY (`job_id`) REFERENCES `hubcap_jobs`(`id`) ON UPDATE no action ON DELETE cascade,
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
CREATE TABLE `__new_lua_cache` (
	`id` text PRIMARY KEY NOT NULL,
	`appid` text NOT NULL,
	`file_id` text NOT NULL,
	`sha256` text NOT NULL,
	`size` integer NOT NULL,
	`pins` text NOT NULL,
	`source` text NOT NULL,
	`uploaded_at` integer NOT NULL,
	`user_id` text,
	`device_id` text NOT NULL,
	`device_name` text NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
INSERT INTO `__new_lua_cache`("id", "appid", "file_id", "sha256", "size", "pins", "source", "uploaded_at", "user_id", "device_id", "device_name") SELECT "id", "appid", "file_id", "sha256", "size", "pins", "source", "uploaded_at", "user_id", "device_id", "device_name" FROM `lua_cache`;
DROP TABLE `lua_cache`;
ALTER TABLE `__new_lua_cache` RENAME TO `lua_cache`;
CREATE INDEX `lua_cache_app_uploaded` ON `lua_cache` (`appid`,`uploaded_at`);
