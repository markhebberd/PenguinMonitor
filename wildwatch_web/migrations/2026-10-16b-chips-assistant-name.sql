-- An assistant who isn't a user can be typed by name in NestCheck, like chipper_name.
-- Stored only when assistant_id is NULL; shown on the bird page's chip details.
--
-- Additive and nullable. Apply BEFORE deploying the code — the snapshot selects assistant_name.

ALTER TABLE penguin_chips ADD COLUMN assistant_name varchar(80) DEFAULT NULL AFTER assistant_id;
