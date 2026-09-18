-- PenguinMonitor database schema — the live shape of wildwatch_nestcheck.
--
-- wildwatch_web/migrations/ remains the record of how the schema got here; this file is
-- what it adds up to. Regenerated from production 2026-07-28 with the hand-written notes
-- carried over. Table charsets are kept: the peng_num foreign keys only form because those
-- columns are latin1.

-- People with a login. Every other table's observer_id / deleted_by / reviewed_by column is
-- an FK to users.id — those column names were left alone when the table was renamed, so the
-- API keeps publishing observer_id / observer_name and installed nestcheck builds keep working.
CREATE TABLE IF NOT EXISTS users (
    id int(11) NOT NULL AUTO_INCREMENT,
    f_name varchar(100) NOT NULL,
    -- NOT NULL (blank is '') so the UNIQUE pair below bites for two people who share a first
    -- name and have no surname recorded — SQL would treat two NULLs as distinct.
    surname varchar(100) NOT NULL DEFAULT '',
    falcon_id varchar(64) DEFAULT NULL, -- identifier for this person in Falcon; free-form, not validated here
    -- The initials a chipping is signed with (penguin_chips.chip_by): 'BS', 'AL'. UNIQUE so an
    -- acronym identifies one person, NULLable because most users never chip.
    chip_acronym varchar(10) DEFAULT NULL,
    active tinyint(1) NOT NULL DEFAULT 1, -- deactivated accounts are kept, not deleted (they own observations)
    deleted_at timestamp NULL DEFAULT NULL,
    -- NULL when the person has no email (field volunteers often don't, and login is by email
    -- so they simply can't use one). NULLs are distinct under UNIQUE, which is what allows any
    -- number of them while still rejecting a genuinely duplicated address.
    email varchar(255) DEFAULT NULL,
    passphrase_hash varchar(255) NOT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    role varchar(20) DEFAULT 'viewer',
    api_key varchar(64) DEFAULT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uniq_user_name (f_name,surname),
    UNIQUE KEY api_key (api_key),
    UNIQUE KEY uniq_user_email (email),
    UNIQUE KEY uniq_user_chip_acronym (chip_acronym)
) ENGINE=InnoDB AUTO_INCREMENT=34 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS regions (
    region_id int(11) NOT NULL AUTO_INCREMENT,
    region_name varchar(100) NOT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    PRIMARY KEY (region_id),
    UNIQUE KEY region_name (region_name)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS colonies (
    colony_id int(11) NOT NULL AUTO_INCREMENT,
    region_id int(11) NOT NULL,
    colony_name varchar(100) NOT NULL,
    colony_prefix varchar(4) DEFAULT NULL,
    location_sets_string text DEFAULT NULL,
    fm_excluded_boxes varchar(255) NOT NULL DEFAULT '0,AA,AB,AC', -- locations excluded from Full Monitor detection
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    PRIMARY KEY (colony_id),
    UNIQUE KEY region_id (region_id,colony_name),
    CONSTRAINT colonies_ibfk_1 FOREIGN KEY (region_id) REFERENCES regions (region_id)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS colony_permissions (
    permission_id int(11) NOT NULL AUTO_INCREMENT,
    colony_id int(11) NOT NULL,
    observer_id int(11) NOT NULL,
    role varchar(20) NOT NULL DEFAULT 'view',
    created_at timestamp NULL DEFAULT current_timestamp(),
    PRIMARY KEY (permission_id),
    UNIQUE KEY colony_id (colony_id,observer_id),
    KEY idx_observer (observer_id),
    CONSTRAINT colony_permissions_ibfk_1 FOREIGN KEY (colony_id) REFERENCES colonies (colony_id),
    CONSTRAINT colony_permissions_ibfk_2 FOREIGN KEY (observer_id) REFERENCES users (id)
) ENGINE=InnoDB AUTO_INCREMENT=18 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS observation_locations (
    location_id int(11) NOT NULL AUTO_INCREMENT,
    colony_id int(11) NOT NULL,
    location_name varchar(50) NOT NULL,
    location_type varchar(20) DEFAULT 'box',
    persistent_notes text DEFAULT NULL,
    watched tinyint(1) NOT NULL DEFAULT 0,
    pit_id varchar(50) DEFAULT NULL,
    scan_time_utc datetime DEFAULT NULL,
    latitude double DEFAULT NULL,
    longitude double DEFAULT NULL,
    accuracy float DEFAULT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    PRIMARY KEY (location_id),
    UNIQUE KEY colony_id (colony_id,location_name),
    UNIQUE KEY colony_id_2 (colony_id,pit_id),
    KEY idx_location_type (location_type),
    CONSTRAINT observation_locations_ibfk_1 FOREIGN KEY (colony_id) REFERENCES colonies (colony_id)
) ENGINE=InnoDB AUTO_INCREMENT=610212 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS observations (
    observation_id int(11) NOT NULL AUTO_INCREMENT,
    location_id int(11) NOT NULL,
    observer_id int(11) NOT NULL,
    observation_time_utc datetime NOT NULL,
    adults int(11) DEFAULT 0,
    eggs int(11) DEFAULT 0,
    chicks int(11) DEFAULT 0,
    breeding_status varchar(50) DEFAULT NULL,
    gate_status varchar(50) DEFAULT NULL,
    notes text DEFAULT NULL,
    is_deleted tinyint(1) DEFAULT 0,
    deletion_reason text DEFAULT NULL,
    deleted_at timestamp NULL DEFAULT NULL,
    deleted_by int(11) DEFAULT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    no_scan int(11) DEFAULT 0,
    fledged_unchipped int(11) DEFAULT 0,
    -- End-of-life seen on this visit, for what the counts alone can't show (a failed egg
    -- replaced within the same observation). NULL = nothing recorded, 0 = looked and nothing
    -- failed, n = n failed here. Never filled in on most observations.
    failed_eggs int(11) DEFAULT NULL,
    dead_chicks int(11) DEFAULT NULL,
    PRIMARY KEY (observation_id),
    KEY deleted_by (deleted_by),
    KEY idx_obs_time (observation_time_utc),
    KEY idx_loc_time (location_id,observation_time_utc),
    KEY idx_observer (observer_id),
    KEY idx_deleted (is_deleted),
    CONSTRAINT observations_ibfk_1 FOREIGN KEY (location_id) REFERENCES observation_locations (location_id),
    CONSTRAINT observations_ibfk_2 FOREIGN KEY (observer_id) REFERENCES users (id),
    CONSTRAINT observations_ibfk_3 FOREIGN KEY (deleted_by) REFERENCES users (id)
) ENGINE=InnoDB AUTO_INCREMENT=382472 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

-- One free-text note per colony per monitoring day — what the day's work was, in a person's
-- words ("Full monitor with Mark"). note_date is an NZ calendar date and is deliberately not
-- an FK: a note can outlive, or precede, the observations it describes.
-- Authorship and edit history live in audit_log (see migrations/2026-07-24c-day-notes.sql).
CREATE TABLE IF NOT EXISTS day_notes (
    day_note_id int(11) NOT NULL AUTO_INCREMENT,
    colony_id int(11) NOT NULL,
    note_date date NOT NULL,
    note varchar(255) DEFAULT NULL, -- what happened that day
    observer_id int(11) DEFAULT NULL, -- who was looking in the boxes
    scribe_id int(11) DEFAULT NULL, -- who was working the phone
    created_at timestamp NOT NULL DEFAULT current_timestamp(),
    updated_at timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    PRIMARY KEY (day_note_id),
    UNIQUE KEY uniq_colony_date (colony_id,note_date),
    KEY idx_note_date (note_date),
    KEY fk_dn_observer (observer_id),
    KEY fk_dn_scribe (scribe_id),
    -- An all-blank row is deleted instead (crud.php save_day_note), so "nothing recorded for
    -- this day" has exactly one representation.
    CONSTRAINT fk_dn_colony FOREIGN KEY (colony_id) REFERENCES colonies (colony_id),
    CONSTRAINT fk_dn_observer FOREIGN KEY (observer_id) REFERENCES users (id),
    CONSTRAINT fk_dn_scribe FOREIGN KEY (scribe_id) REFERENCES users (id),
    CONSTRAINT chk_dn_any CHECK (char_length(trim(coalesce(note,''))) > 0 or observer_id is not null or scribe_id is not null)
) ENGINE=InnoDB AUTO_INCREMENT=100 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS date_mappings (
    season_year int(11) NOT NULL,
    date_number int(11) NOT NULL,
    actual_date date NOT NULL,
    partial_monitor tinyint(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (season_year,date_number)
) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS penguins (
    peng_num varchar(20) NOT NULL,
    colony_id int(11) NOT NULL DEFAULT 1,
    chipped_as_adult tinyint(1) DEFAULT 0,
    sex varchar(10) DEFAULT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    updated_at timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    chick_size_code varchar(10) DEFAULT NULL,
    -- Scanning this bird in the field raises an alert, as an unsexed adult does. Set by hand.
    alert tinyint(1) NOT NULL DEFAULT 0,
    notes text DEFAULT NULL,
    death_date datetime DEFAULT NULL, -- 2pm NZ (02:00 UTC) on the death date; NULL = alive
    is_dead tinyint(1) GENERATED ALWAYS AS (death_date is not null) STORED,
    PRIMARY KEY (peng_num),
    KEY idx_penguins_colony (colony_id)
) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS penguin_chips (
    peng_num varchar(20) DEFAULT NULL,
    pit_id varchar(17) NOT NULL,
    chip_date date DEFAULT NULL,
    is_active tinyint(1) DEFAULT 1,
    created_at timestamp NULL DEFAULT current_timestamp(),
    chip_box varchar(20) DEFAULT NULL,
    chip_by varchar(50) DEFAULT NULL,
    chipper_id int(11) DEFAULT NULL,
    assistant_id int(11) DEFAULT NULL,
    solo varchar(50) DEFAULT NULL,
    location_id int(11) DEFAULT NULL,
    PRIMARY KEY (pit_id),
    UNIQUE KEY chip_number (pit_id),
    KEY idx_chip_number (pit_id),
    KEY fk_chips_peng (peng_num),
    KEY idx_chip_location (location_id),
    KEY fk_chips_chipper (chipper_id),
    KEY fk_chips_assistant (assistant_id),
    CONSTRAINT fk_chips_assistant FOREIGN KEY (assistant_id) REFERENCES users (id),
    CONSTRAINT fk_chips_chipper FOREIGN KEY (chipper_id) REFERENCES users (id),
    CONSTRAINT fk_chips_peng FOREIGN KEY (peng_num) REFERENCES penguins (peng_num) ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS penguin_scans (
    scan_id int(11) NOT NULL AUTO_INCREMENT,
    observation_id int(11) NOT NULL,
    pit_id varchar(17) DEFAULT NULL,
    scan_time_utc datetime NOT NULL,
    latitude double DEFAULT NULL,
    longitude double DEFAULT NULL,
    accuracy float DEFAULT NULL,
    is_deleted tinyint(1) DEFAULT 0,
    deleted_at timestamp NULL DEFAULT NULL,
    deleted_by int(11) DEFAULT NULL,
    PRIMARY KEY (scan_id),
    KEY idx_observation (observation_id),
    KEY fk_scans_chip (pit_id),
    KEY idx_deleted (is_deleted),
    KEY deleted_by (deleted_by),
    CONSTRAINT fk_scans_chip FOREIGN KEY (pit_id) REFERENCES penguin_chips (pit_id),
    CONSTRAINT fk_scans_obs FOREIGN KEY (observation_id) REFERENCES observations (observation_id) ON DELETE CASCADE,
    CONSTRAINT penguin_scans_ibfk_1 FOREIGN KEY (observation_id) REFERENCES observations (observation_id)
) ENGINE=InnoDB AUTO_INCREMENT=177164 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS penguin_biometric_data (
    biometric_id int(11) NOT NULL AUTO_INCREMENT,
    peng_num varchar(20) DEFAULT NULL,
    observation_id int(11) DEFAULT NULL,
    observation_date date NOT NULL,
    observed_sex varchar(2) DEFAULT NULL,
    sex varchar(10) DEFAULT NULL,
    weight decimal(6,2) DEFAULT NULL,
    flipper_length decimal(5,2) DEFAULT NULL,
    body_length decimal(5,2) DEFAULT NULL,
    beak_length decimal(5,2) DEFAULT NULL,
    notes text DEFAULT NULL,
    is_deleted tinyint(1) DEFAULT 0,
    deleted_at timestamp NULL DEFAULT NULL,
    deleted_by int(11) DEFAULT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    is_moulting tinyint(1) DEFAULT NULL,
    disposition_aggressive tinyint(1) DEFAULT NULL,
    disposition_passive tinyint(1) DEFAULT NULL,
    PRIMARY KEY (biometric_id),
    KEY idx_penguin_date (observation_date),
    KEY fk_bio_peng (peng_num),
    KEY deleted_by (deleted_by),
    KEY penguin_biometric_data_ibfk_2 (observation_id),
    CONSTRAINT fk_bio_peng FOREIGN KEY (peng_num) REFERENCES penguins (peng_num) ON UPDATE CASCADE,
    CONSTRAINT penguin_biometric_data_ibfk_2 FOREIGN KEY (observation_id) REFERENCES observations (observation_id),
    CONSTRAINT penguin_biometric_data_ibfk_3 FOREIGN KEY (deleted_by) REFERENCES users (id),
    CONSTRAINT penguin_biometric_data_ibfk_4 FOREIGN KEY (deleted_by) REFERENCES users (id)
) ENGINE=InnoDB AUTO_INCREMENT=135124 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS breeding_verifications (
    verification_id int(11) NOT NULL AUTO_INCREMENT,
    observation_id int(11) NOT NULL,
    adults_verdict enum('accepted','rejected') DEFAULT NULL,
    male_peng_num varchar(20) CHARACTER SET latin1 COLLATE latin1_swedish_ci DEFAULT NULL,
    female_peng_num varchar(20) CHARACTER SET latin1 COLLATE latin1_swedish_ci DEFAULT NULL,
    adults_reviewed_by int(11) DEFAULT NULL,
    adults_reviewed_at timestamp NULL DEFAULT NULL,
    adults_note varchar(500) DEFAULT NULL,
    chicks_verdict enum('accepted','rejected') DEFAULT NULL,
    chicks longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(chicks)),
    dead_eggs tinyint(3) unsigned NOT NULL DEFAULT 0,
    dead_chicks tinyint(3) unsigned NOT NULL DEFAULT 0,
    fledged_unchipped tinyint(3) unsigned NOT NULL DEFAULT 0,
    chicks_reviewed_by int(11) DEFAULT NULL,
    chicks_reviewed_at timestamp NULL DEFAULT NULL,
    chicks_note varchar(500) DEFAULT NULL,
    created_at timestamp NOT NULL DEFAULT current_timestamp(),
    updated_at timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
    PRIMARY KEY (verification_id),
    UNIQUE KEY uniq_clutch (observation_id),
    KEY idx_male (male_peng_num),
    KEY idx_female (female_peng_num),
    KEY idx_adults_by (adults_reviewed_by),
    KEY idx_chicks_by (chicks_reviewed_by),
    CONSTRAINT fk_bv_adults_by FOREIGN KEY (adults_reviewed_by) REFERENCES users (id),
    CONSTRAINT fk_bv_chicks_by FOREIGN KEY (chicks_reviewed_by) REFERENCES users (id),
    CONSTRAINT fk_bv_female FOREIGN KEY (female_peng_num) REFERENCES penguins (peng_num) ON UPDATE CASCADE,
    CONSTRAINT fk_bv_male FOREIGN KEY (male_peng_num) REFERENCES penguins (peng_num) ON UPDATE CASCADE,
    CONSTRAINT fk_bv_obs FOREIGN KEY (observation_id) REFERENCES observations (observation_id),
    CONSTRAINT chk_bv_counts CHECK (dead_eggs <= 10 and dead_chicks <= 10 and fledged_unchipped <= 10),
    CONSTRAINT chk_bv_adults_stamp CHECK (adults_verdict is null = (adults_reviewed_by is null) and adults_reviewed_by is null = (adults_reviewed_at is null)),
    CONSTRAINT chk_bv_chicks_stamp CHECK (chicks_verdict is null = (chicks_reviewed_by is null) and chicks_reviewed_by is null = (chicks_reviewed_at is null)),
    CONSTRAINT chk_bv_adults_reason CHECK (adults_verdict <> 'rejected' or adults_note is not null and char_length(trim(adults_note)) > 0),
    CONSTRAINT chk_bv_chicks_reason CHECK (chicks_verdict <> 'rejected' or chicks_note is not null and char_length(trim(chicks_note)) > 0),
    CONSTRAINT chk_bv_reviewed CHECK (adults_verdict is not null or chicks_verdict is not null)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS validation_dismissals (
    id int(11) NOT NULL AUTO_INCREMENT,
    colony_id int(11) NOT NULL,
    error_type varchar(40) NOT NULL,
    error_key varchar(255) NOT NULL,
    content_hash varchar(64) NOT NULL,
    reason varchar(255) DEFAULT NULL,
    dismissed_by int(11) DEFAULT NULL,
    dismissed_at timestamp NOT NULL DEFAULT current_timestamp(),
    PRIMARY KEY (id),
    UNIQUE KEY uniq_dismissal (colony_id,error_type,error_key),
    KEY idx_colony (colony_id)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS audit_log (
    audit_id int(11) NOT NULL AUTO_INCREMENT,
    table_name varchar(50) NOT NULL,
    record_id varchar(50) NOT NULL,
    action varchar(20) NOT NULL,
    observer_id int(11) NOT NULL,
    changed_fields longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(changed_fields)),
    change_reason text DEFAULT NULL,
    change_timestamp timestamp NULL DEFAULT current_timestamp(),
    PRIMARY KEY (audit_id),
    KEY idx_record (table_name,record_id),
    KEY idx_observer (observer_id),
    KEY idx_timestamp (change_timestamp),
    CONSTRAINT audit_log_ibfk_1 FOREIGN KEY (observer_id) REFERENCES users (id)
) ENGINE=InnoDB AUTO_INCREMENT=11985 DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS sessions (
    token varchar(64) NOT NULL,
    observer_id int(11) NOT NULL,
    expires_at datetime NOT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    PRIMARY KEY (token),
    KEY observer_id (observer_id),
    CONSTRAINT sessions_ibfk_1 FOREIGN KEY (observer_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;

CREATE TABLE IF NOT EXISTS password_resets (
    token_hash char(64) NOT NULL,
    observer_id int(11) NOT NULL,
    purpose varchar(10) NOT NULL DEFAULT 'reset',
    expires_at datetime NOT NULL,
    used_at datetime DEFAULT NULL,
    created_at timestamp NULL DEFAULT current_timestamp(),
    PRIMARY KEY (token_hash),
    KEY observer_id (observer_id),
    CONSTRAINT password_resets_ibfk_1 FOREIGN KEY (observer_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS disk_history (
    id int(11) NOT NULL AUTO_INCREMENT,
    recorded_at datetime NOT NULL,
    disk_free_mb int(11) NOT NULL,
    PRIMARY KEY (id),
    KEY idx_recorded_at (recorded_at)
) ENGINE=InnoDB AUTO_INCREMENT=4147 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
