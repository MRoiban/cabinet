CREATE TABLE `cloud_files` (
	`user_id` text NOT NULL,
	`path` text NOT NULL,
	`file_id` text NOT NULL,
	`sha256` text NOT NULL,
	`size` integer NOT NULL,
	`updated_at` integer NOT NULL,
	PRIMARY KEY(`user_id`, `path`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
