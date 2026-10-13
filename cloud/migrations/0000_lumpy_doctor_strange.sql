CREATE TABLE `users` (
	`id` text PRIMARY KEY NOT NULL,
	`name` text NOT NULL,
	`created_at` integer NOT NULL,
	`last_seen` integer
);
--> statement-breakpoint
CREATE TABLE `tokens` (
	`hash` text PRIMARY KEY NOT NULL,
	`user_id` text NOT NULL,
	`created_at` integer NOT NULL,
	`revoked_at` integer,
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE TABLE `apps` (
	`user_id` text NOT NULL,
	`appid` text NOT NULL,
	`achievements` text NOT NULL,
	`stats` text NOT NULL,
	`stats_updated_at` integer NOT NULL,
	`playtime` text NOT NULL,
	PRIMARY KEY(`user_id`, `appid`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE TABLE `devices` (
	`user_id` text NOT NULL,
	`device_id` text NOT NULL,
	`name` text NOT NULL,
	PRIMARY KEY(`user_id`, `device_id`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE TABLE `saves` (
	`user_id` text NOT NULL,
	`appid` text NOT NULL,
	`file_id` text NOT NULL,
	`sha256` text NOT NULL,
	`size` integer NOT NULL,
	`updated_at` integer NOT NULL,
	`device_id` text NOT NULL,
	`device_name` text NOT NULL,
	PRIMARY KEY(`user_id`, `appid`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
