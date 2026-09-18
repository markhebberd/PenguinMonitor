-- Retire penguin_biometric_data.condition_healthy and condition_ticks.
--
-- Neither flag has ever been set: condition_healthy was a website-only chip (added 2026-07-06),
-- and nestcheck's Ticks checkbox was hidden for most of the column's life. The code no longer
-- reads or writes either (snapshot columns, hash columns, website flag chips, nestcheck form),
-- and crud.php strips both from incoming writes so phones still on v39.69 or older keep uploading.
--
-- Safe to drop — verified no information is lost (2026-09-18):
--   SELECT condition_healthy, COUNT(*) FROM penguin_biometric_data GROUP BY 1;  -- NULL 11965, 0 3
--   SELECT condition_ticks,   COUNT(*) FROM penguin_biometric_data GROUP BY 1;  -- NULL 11956, 0 12
--   audit_log (from 2026-06-28) never records either flag as 1.
-- Values backed up to ~/condition_flags_backup.tsv on the VPS before the drop.
--
-- Apply the CODE change (deploy) first so nothing references the columns, THEN run this.

ALTER TABLE penguin_biometric_data DROP COLUMN condition_healthy, DROP COLUMN condition_ticks;
