-- A chipper who isn't a user (no account, or no permit id synced) can be typed by name in NestCheck.
-- Stored here only when chipper_id is NULL; chipper_id stays the normal path. Displays fall back to
-- it wherever the chipper's acronym shows (chip_by in snapshot_columns.php and the list views).
--
-- Additive and nullable. Apply BEFORE deploying the code — the views select pc.chipper_name.

ALTER TABLE penguin_chips ADD COLUMN chipper_name varchar(80) DEFAULT NULL AFTER chipper_id;
