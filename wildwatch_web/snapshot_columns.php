<?php
/**
 * Shared SELECT column lists for the box/bird tables.
 *
 * snapshot.php (full sync -> IndexedDB, used by wildwatch + nestcheck sync) and
 * bird-detail.php (live, scoped fetch -> nestcheck bird-panel WebView) both build their
 * queries from these constants. Because both return identically-shaped rows, the client
 * assembly (queryBirdDetailInner / computeBoxFamilies) produces the identical panel whether
 * it runs over a full mem or a scoped mem. Change a column here and both stay in sync.
 *
 * Aliases (o. / ps.) match the joins used in both files. The incremental snapshot path adds
 * ', o.updated_at' to the observations list; nothing else differs.
 *
 * observer_id is the id only — names come from the snapshot's `observers` list (getObservers),
 * so a name isn't repeated on every one of a colony's tens of thousands of rows.
 */

const SNAP_COLS_OBS  = 'o.observation_id, o.location_id, o.observation_time_utc, o.adults, o.eggs, o.chicks, o.breeding_status, o.gate_status, o.notes, o.no_scan, o.fledged_unchipped, o.failed_eggs, o.dead_chicks, o.is_deleted, o.observer_id';
const SNAP_COLS_SCAN = 'ps.scan_id, ps.observation_id, ps.pit_id, ps.is_deleted as scan_deleted';
// sex_guess_m / sex_guess_f are the weighted field-sex evidence for the bird — "probably" 2,
// "maybe" 1 — summed over its WHOLE biometric history, which is why they are computed here and not
// on the client: nestcheck holds only the current round's biometrics, so a tally it worked out
// itself would be a fraction of the real one and the "enough guesses to sex it" prompt would never
// reach the score. Correlated subqueries on an indexed peng_num: 26 ms for all 1043 birds.
// Qualified with the table name rather than an alias, because every caller says a bare FROM penguins.
const SNAP_COLS_PENG = 'peng_num, chipped_as_adult, sex, is_dead, death_date, chick_size_code, alert, notes,
    (SELECT COALESCE(SUM(CASE WHEN UPPER(b.observed_sex) IN (\'PM\',\'M\') THEN 2 WHEN UPPER(b.observed_sex) = \'MM\' THEN 1 ELSE 0 END), 0)
       FROM penguin_biometric_data b
      WHERE b.peng_num = penguins.peng_num AND (b.is_deleted = FALSE OR b.is_deleted IS NULL)) AS sex_guess_m,
    (SELECT COALESCE(SUM(CASE WHEN UPPER(b.observed_sex) IN (\'PF\',\'F\') THEN 2 WHEN UPPER(b.observed_sex) = \'MF\' THEN 1 ELSE 0 END), 0)
       FROM penguin_biometric_data b
      WHERE b.peng_num = penguins.peng_num AND (b.is_deleted = FALSE OR b.is_deleted IS NULL)) AS sex_guess_f';
// chip_by is no longer a stored column — it's derived from chipper_id (the FK) so the client keeps
// receiving the chipper's acronym without depending on a denormalised field. A correlated subquery
// (like sex_guess on penguins) means every caller resolves it with no extra join.
const SNAP_COLS_CHIP = 'pit_id, peng_num, chip_date, is_active, chip_box, location_id, chipper_id, assistant_id, solo, (SELECT chip_acronym FROM users WHERE id = chipper_id) AS chip_by';
// Same list, aliased — the incremental query joins and needs the pc. prefix.
const SNAP_COLS_CHIP_P = 'pc.pit_id, pc.peng_num, pc.chip_date, pc.is_active, pc.chip_box, pc.location_id, pc.chipper_id, pc.assistant_id, pc.solo, (SELECT chip_acronym FROM users WHERE id = pc.chipper_id) AS chip_by';
// pit_id / latitude / longitude / accuracy / scan_time_utc ARE the box tag: nestcheck builds its
// box_tags store straight off these rows rather than calling boxtags.php, so a tag, a moved fix or a
// cleared tag reaches the phone on the one feed. boxtags.php still takes the writes.
const SNAP_COLS_LOC  = 'location_id, location_name, persistent_notes, watched, pit_id, latitude, longitude, accuracy, scan_time_utc';
const SNAP_COLS_BIO  = 'biometric_id, peng_num, observation_id, observation_date, sex, observed_sex, weight, flipper_length, body_length, beak_length, is_moulting, disposition_aggressive, disposition_passive, notes, is_deleted';

// Human-verified breeding truth (single table). Reviewer names come from the observer joins
// (oa/oc) so the client shows "accepted by <name>" without an observers table. chicks is a JSON
// array of peng_nums (prefix-stripped per element in getVerificationData). Alias v = breeding_verifications.
// The day's note — one row per colony per NZ date. Alias d = day_notes. updated_at rides along
// so the client can tell an edited note from an unchanged one on an incremental snapshot.
const SNAP_COLS_DAYNOTE = 'd.day_note_id, d.note_date, d.note, d.observer_id, d.scribe_id, d.updated_at';

const SNAP_COLS_VER = 'v.verification_id, v.observation_id, v.adults_verdict, v.male_peng_num, v.female_peng_num, v.adults_reviewed_by, oa.f_name AS adults_reviewed_by_name, v.adults_reviewed_at, v.adults_note, v.chicks_verdict, v.chicks, v.dead_eggs, v.dead_chicks, v.fledged_unchipped, v.chicks_reviewed_by, oc.f_name AS chicks_reviewed_by_name, v.chicks_reviewed_at, v.chicks_note, v.created_at, v.updated_at';

/** Every observer, id → name, so the client can name whoever recorded an observation from
 *  observations.observer_id alone — the id is on the row, the name is looked up once rather
 *  than repeated on tens of thousands of rows. A few dozen rows, and not colony-scoped (an
 *  observer works across colonies), so it rides every payload in FULL and the client replaces
 *  its store wholesale: that is how a rename or a removed account reaches the cache.
 *  Name only — no email, no hash. */
function getObservers($pdo) {
    // surname and active ride along so the client can show a full name and offer only
    // current people in a picker, while still resolving the id on an old row to whoever it was.
    // role rides along so the client can keep service accounts (the API user) out of the
    // people pickers, while still resolving their id if an old row happens to reference one.
    // Soft-deleted users are returned FLAGGED, not omitted: the client hides them from pickers
    // but still needs their name for the history that points at them.
    // chip_acronym and falcon_id ride along for nestcheck: it picks who chipped a bird from this
    // list, and only someone with a permit id may be picked, so the flag has to travel with the name.
    return ['observers' => $pdo->query("SELECT id AS observer_id, f_name AS observer_name, surname, active, role, chip_acronym, falcon_id, (deleted_at IS NOT NULL) AS deleted FROM users ORDER BY f_name, surname")->fetchAll()];
}
