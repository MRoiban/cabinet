CREATE TABLE `app_activity` (
	`user_id` text NOT NULL,
	`appid` text NOT NULL,
	`first_seen` integer NOT NULL,
	`last_played` integer,
	PRIMARY KEY(`user_id`, `appid`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
CREATE TABLE `achievement_schemas` (
	`appid` text PRIMARY KEY NOT NULL,
	`achievements` text NOT NULL,
	`fetched_at` integer NOT NULL
);
