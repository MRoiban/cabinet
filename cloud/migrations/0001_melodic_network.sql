CREATE TABLE `presence` (
	`user_id` text NOT NULL,
	`account_id` integer NOT NULL,
	`app_id` text NOT NULL,
	`name` text NOT NULL,
	`updated_at` integer NOT NULL,
	PRIMARY KEY(`user_id`, `account_id`),
	FOREIGN KEY (`user_id`) REFERENCES `users`(`id`) ON UPDATE no action ON DELETE no action
);
