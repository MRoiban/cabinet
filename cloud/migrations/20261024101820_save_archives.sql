CREATE TABLE `save_archives` (
	`id` text PRIMARY KEY NOT NULL,
	`user_id` text NOT NULL,
	`appid` text NOT NULL,
	`file_id` text NOT NULL,
	`sha256` text NOT NULL,
	`content_hash` text NOT NULL,
	`size` integer NOT NULL,
	`files` integer NOT NULL,
	`bytes` integer NOT NULL,
	`created_at` integer NOT NULL,
	`device_id` text NOT NULL,
	`device_name` text NOT NULL,
	`base_hash` text,
	`deleted` integer DEFAULT 0 NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
CREATE INDEX `save_archives_user_app_created` ON `save_archives` (`user_id`,`appid`,`created_at`);
