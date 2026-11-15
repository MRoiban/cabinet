CREATE TABLE `lua_cache` (
	`id` text PRIMARY KEY NOT NULL,
	`appid` text NOT NULL,
	`file_id` text NOT NULL,
	`sha256` text NOT NULL,
	`size` integer NOT NULL,
	`pins` text NOT NULL,
	`source` text NOT NULL,
	`uploaded_at` integer NOT NULL,
	`user_id` text NOT NULL,
	`device_id` text NOT NULL,
	`device_name` text NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
CREATE INDEX `lua_cache_app_uploaded` ON `lua_cache` (`appid`,`uploaded_at`);
