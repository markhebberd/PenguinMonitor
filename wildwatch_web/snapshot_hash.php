<?php
/**
 * Integrity hashes for the client cache — a diagnostic net for the incremental sync.
 *
 * The incremental snapshot can silently strand an edit in a client's cache: a row that changed
 * server-side but that the delta query failed to re-send (e.g. an in-place chip_box edit — chips
 * have no updated_at — or the v22 penguin alert flag). The row COUNT is unchanged, so the existing
 * _counts check can't see it. These hashes let the client notice its cache disagrees with the
 * server, re-sync, and — the point — report exactly which row/field drifted, so we can find the
 * incremental method that mishandled it.
 *
 * The hash is over the EXACT rows the server sends for a colony view (full set, full bird numbers),
 * in a canonical form the client reproduces byte-for-byte:
 *   row      = implode("\x1f", each HASH column as a string, null => "")
 *   table    = sha1( implode("\x1e", rows sorted ascending by primary key) )
 * FLOAT columns are deliberately excluded (lat/long/accuracy, biometric measurements) — they don't
 * serialise identically across PHP and JS, and no stranded-edit bug has ever been in one. Every
 * hashed column below is a string or integer.
 *
 * Result is cached per colony keyed on MAX(audit_log.id): changes are far rarer than syncs, so on
 * the common no-change path this is a single MAX(id) lookup and a cached blob — no table scan.
 */

// Curated, float-free columns per table, in a fixed order. The client MUST hash the same columns
// in the same order. Keep this list and the client's HASH_COLS (localdb) in lockstep.
const WW_HASH_COLS = [
    'penguins'     => ['pk' => 'peng_num',      'cols' => ['peng_num','chipped_as_adult','sex','is_dead','death_date','chick_size_code','alert','notes','sex_guess_m','sex_guess_f']],
    'chips'        => ['pk' => 'pit_id',        'cols' => ['pit_id','peng_num','chip_date','is_active','chip_box','location_id','chipper_id','assistant_id','solo']],
    'observations' => ['pk' => 'observation_id','cols' => ['observation_id','location_id','observation_time_utc','adults','eggs','chicks','breeding_status','gate_status','notes','no_scan','fledged_unchipped','failed_eggs','dead_chicks','is_deleted','observer_id']],
    'scans'        => ['pk' => 'scan_id',       'cols' => ['scan_id','observation_id','pit_id']],
    'locations'    => ['pk' => 'location_id',   'cols' => ['location_id','location_name','persistent_notes','watched','pit_id','scan_time_utc']],
    'biometrics'   => ['pk' => 'biometric_id',  'cols' => ['biometric_id','peng_num','observation_id','observation_date','sex','observed_sex','is_moulting','disposition_aggressive','disposition_passive','notes','is_deleted']],
];

/** Canonical hash of one table's rows (assoc arrays), matching the client's serialisation. */
function wwHashRows(array $rows, array $spec): string {
    $lines = [];
    foreach ($rows as $r) {
        $parts = [];
        foreach ($spec['cols'] as $c) $parts[] = (string)($r[$c] ?? '');
        $lines[(string)($r[$spec['pk']] ?? '')] = implode("\x1f", $parts);
    }
    ksort($lines, SORT_STRING); // client sorts pk lexicographically to match
    return sha1(implode("\x1e", array_values($lines)));
}

/** Build the full per-colony row set for each hashed table, exactly as the snapshot sends it
 *  (full bird numbers, no stripping), and hash each. (Separate curated SELECTs so the hash is self-contained and
 *  can't silently drift when SNAP_COLS_* change.) */
function wwComputeSnapshotHashes(PDO $pdo, int $colonyId): array {
    $out = [];
    // Hash each table then free its rows before loading the next, so peak memory is one table's
    // worth, not all six at once — the difference between fitting under the pool limit and OOMing.
    $do = function(string $key, string $sql, array $params) use ($pdo, &$out) {
        if ($params) { $st = $pdo->prepare($sql); $st->execute($params); }
        else { $st = $pdo->query($sql); }
        $rows = $st->fetchAll();
        $out[$key] = wwHashRows($rows, WW_HASH_COLS[$key]);
        unset($rows);
    };

    $do('penguins', "SELECT peng_num, chipped_as_adult, sex, is_dead, death_date, chick_size_code, alert, notes,
        (SELECT COALESCE(SUM(CASE WHEN UPPER(b.observed_sex) IN ('PM','M') THEN 2 WHEN UPPER(b.observed_sex)='MM' THEN 1 ELSE 0 END),0)
           FROM penguin_biometric_data b WHERE b.peng_num = penguins.peng_num AND (b.is_deleted=FALSE OR b.is_deleted IS NULL)) AS sex_guess_m,
        (SELECT COALESCE(SUM(CASE WHEN UPPER(b.observed_sex) IN ('PF','F') THEN 2 WHEN UPPER(b.observed_sex)='MF' THEN 1 ELSE 0 END),0)
           FROM penguin_biometric_data b WHERE b.peng_num = penguins.peng_num AND (b.is_deleted=FALSE OR b.is_deleted IS NULL)) AS sex_guess_f
        FROM penguins", []);

    $do('chips', "SELECT pit_id, peng_num, chip_date, is_active, chip_box, location_id, chipper_id, assistant_id, solo FROM penguin_chips", []);

    $do('observations', "SELECT o.observation_id, o.location_id, o.observation_time_utc, o.adults, o.eggs, o.chicks, o.breeding_status, o.gate_status, o.notes, o.no_scan, o.fledged_unchipped, o.failed_eggs, o.dead_chicks, o.is_deleted, o.observer_id
        FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id WHERE ol.colony_id = ?", [$colonyId]);

    $do('scans', "SELECT ps.scan_id, ps.observation_id, ps.pit_id FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE ol.colony_id = ? AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)", [$colonyId]);

    $do('locations', "SELECT location_id, location_name, persistent_notes, watched, pit_id, scan_time_utc
        FROM observation_locations WHERE colony_id = ?", [$colonyId]);

    $do('biometrics', "SELECT biometric_id, peng_num, observation_id, observation_date, sex, observed_sex, is_moulting, disposition_aggressive, disposition_passive, notes, is_deleted FROM penguin_biometric_data", []);

    return $out;
}

/** Per-colony hashes, cached to a working file keyed on MAX(audit_log.id). Recomputes only when a
 *  write has happened since (rare); otherwise one MAX(id) lookup and the cached blob. Never fatal —
 *  a diagnostic must not be able to break a sync, so any failure just returns null (no _hashes). */
function wwSnapshotHashes(PDO $pdo, int $colonyId): ?array {
    try {
        $auditId = (int)$pdo->query("SELECT COALESCE(MAX(audit_id),0) FROM audit_log")->fetchColumn();
        $file = __DIR__ . "/hash-cache-{$colonyId}.json";
        // NB: snapshot.php installs a set_error_handler that throws on ANY warning, and PHP 8's @
        // does NOT stop a custom handler firing — so file ops must be guarded, never @-suppressed,
        // or a missing/unwritable cache file throws and nulls the whole result.
        if (is_file($file) && is_readable($file)) {
            $cached = json_decode((string)file_get_contents($file), true);
            if (is_array($cached) && ($cached['audit_id'] ?? -1) === $auditId && isset($cached['hashes'])) {
                return $cached['hashes'];
            }
        }
        $hashes = wwComputeSnapshotHashes($pdo, $colonyId);
        // Best-effort cache write — a failure here must neither throw nor lose the fresh hashes.
        if (is_writable(dirname($file)) || (is_file($file) && is_writable($file))) {
            file_put_contents($file, json_encode(['audit_id' => $auditId, 'hashes' => $hashes]), LOCK_EX);
        }
        return $hashes;
    } catch (Throwable $e) {
        return null;
    }
}
