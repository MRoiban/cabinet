-- Backfill app_activity for games synced before it existed: last played is the newest known
-- event (achievement unlock, non-empty save, archive or CloudRedirect upload); added is the
-- oldest one, but never before the user was created (older unlocks were imported, not added).
INSERT OR IGNORE INTO `app_activity` (`user_id`, `appid`, `first_seen`, `last_played`)
SELECT e.`user_id`, e.`appid`, max(u.`created_at`, min(e.`t`)), max(e.`t`)
FROM (
  SELECT a.`user_id`, a.`appid`, CAST(j.`value` AS INTEGER) * 1000 AS `t`
    FROM `apps` a, json_each(a.`achievements`) j WHERE CAST(j.`value` AS INTEGER) > 0
  UNION ALL SELECT `user_id`, `appid`, `updated_at` FROM `saves` WHERE `size` > 22
  UNION ALL SELECT `user_id`, `appid`, `created_at` FROM `save_archives` WHERE `deleted` = 0
  UNION ALL SELECT `user_id`, substr(substr(`path`, instr(`path`, '/') + 1), 1, instr(substr(`path`, instr(`path`, '/') + 1), '/') - 1), `updated_at`
    FROM `cloud_files`
) e JOIN `users` u ON u.`id` = e.`user_id`
WHERE e.`appid` GLOB '[0-9]*' AND e.`appid` NOT GLOB '*[^0-9]*'
GROUP BY e.`user_id`, e.`appid`;
-- Games with no dated event at all: added when the user was created, never played on record.
INSERT OR IGNORE INTO `app_activity` (`user_id`, `appid`, `first_seen`, `last_played`)
SELECT a.`user_id`, a.`appid`, u.`created_at`, NULL FROM `apps` a JOIN `users` u ON u.`id` = a.`user_id`;
INSERT OR IGNORE INTO `app_activity` (`user_id`, `appid`, `first_seen`, `last_played`)
SELECT s.`user_id`, s.`appid`, u.`created_at`, NULL FROM `saves` s JOIN `users` u ON u.`id` = s.`user_id`;
