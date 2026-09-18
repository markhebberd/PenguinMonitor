<?php
/**
 * Payload contract check — run by deploy.sh after the symlink flip, before the release is kept.
 *
 * Nestcheck deserialises sync.php and penguins.php into typed C# classes. A field that arrives as
 * the wrong JSON type doesn't degrade — Newtonsoft throws, the whole download fails, and the phone
 * retries for thirty seconds and gives up. That is invisible from the server: the request is a 200.
 * It happened on 2026-07-28, when previous-visit boxes started sending the day's whole record where
 * the phone expects the note as a string, and only showed up in the field.
 *
 * So: fetch the real payloads as a real user and assert the shape the app declares. A mismatch
 * fails the deploy, which rolls back.
 *
 * Three payloads, because there are three readers. snapshot.php?scope=field is what nestcheck
 * downloads — checked both cold (no watermark, whole set) and warm (watermarked, bird tables as a
 * delta), since those are separate branches and only the cold one used to be exercised at all. The
 * full snapshot.php is the website's cache. sync.php and penguins.php are what the legacy v37 app
 * still parses, plus sync.php's boxes, which nestcheck reads for the pre-upload reconcile.
 *
 * The contracts below mirror nestcheck's LocalDb row classes and DataStorageService.SyncBox /
 * SyncScan / SyncUser. When a field is added to one of those, add it here.
 *
 * Types: 'int' and 'string' are strict — the C# property is non-nullable, so a JSON null throws on
 * the phone. 'int?' / 'string?' accept null. Missing keys are allowed (the app leaves the default);
 * extra keys are ignored, as Newtonsoft ignores them.
 *
 * Usage: php contract_check.php [colony_id]   (CLI only; exits non-zero on any breach)
 */
if (PHP_SAPI !== 'cli') { http_response_code(404); exit; }

require_once __DIR__ . '/config.php';

const WW_HOST = 'wildwatch.co.nz';

$BOX = [
    'observation_id' => 'int',   'location_id' => 'int',
    'observation_time_utc' => 'string?', 'monitor_filename' => 'string?', 'day_note' => 'string?',
    'day_observer' => 'string?', 'day_scribe' => 'string?',
    'day_observer_id' => 'int?', 'day_scribe_id' => 'int?',
    'observer_name' => 'string?',
    'adults' => 'int', 'eggs' => 'int', 'chicks' => 'int', 'no_scan' => 'int',
    'failed_eggs' => 'int?', 'dead_chicks' => 'int?',
    'breeding_status' => 'string?', 'gate_status' => 'string?', 'notes' => 'string?',
];
$SCAN = [
    'pit_id' => 'string?', 'scan_time_utc' => 'string?', 'peng_num' => 'string?', 'sex' => 'string?',
    'chick_size_code' => 'string?', 'chipped_as_adult' => 'int?', 'alert' => 'int?',
];
$LOCATION = ['location_id' => 'int', 'location_name' => 'string?', 'persistent_notes' => 'string?', 'watched' => 'int'];
$USER     = ['id' => 'int', 'name' => 'string?', 'f_name' => 'string?', 'surname' => 'string?',
             'chip_acronym' => 'string?', 'falcon_id' => 'string?'];
$OBSERVER = ['observer_id' => 'int', 'name' => 'string?', 'chip_acronym' => 'string?', 'falcon_id' => 'string?'];
$PENGUIN  = ['peng_num' => 'string?', 'pit_id' => 'string?', 'sex' => 'string?', 'is_dead' => 'int?',
             'chip_date' => 'string?', 'chipped_as_adult' => 'int?', 'chick_size_code' => 'string?',
             'sex_guess_m' => 'int?', 'sex_guess_f' => 'int?', 'alert' => 'int?'];

$breaches = [];

function ok($value, string $type): bool {
    $nullable = str_ends_with($type, '?');
    if ($value === null) return $nullable;
    return match (rtrim($type, '?')) {
        // json_decode gives ints for whole numbers; the phone parses "3" into an int too, so a
        // numeric string passes. An array or object never does — that is the failure being caught.
        'int'    => is_int($value) || (is_string($value) && preg_match('/^-?\d+$/', $value) === 1),
        'string' => is_string($value) || is_int($value) || is_float($value) || is_bool($value),
        default  => false,
    };
}

function describe($value): string {
    if (is_array($value)) return array_is_list($value) ? 'array' : 'object';
    return gettype($value);
}

function check(string $where, $row, array $contract): void {
    global $breaches;
    if (!is_array($row)) { $breaches[] = "$where: expected an object, got " . describe($row); return; }
    foreach ($contract as $field => $type) {
        if (!array_key_exists($field, $row)) continue;          // absent is fine; wrong type is not
        if (!ok($row[$field], $type))
            $breaches[] = "$where.$field: expected $type, got " . describe($row[$field]);
    }
}

/** GET an endpoint on this host, resolved to the local nginx so the check hits THIS release. */
function fetchJson(string $path, string $token): array {
    $ch = curl_init("https://" . WW_HOST . $path);
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_RESOLVE => [WW_HOST . ':443:127.0.0.1'],
        // Speaks the current bird-number contract, as the apps do; without it every endpoint answers 426.
        CURLOPT_HTTPHEADER => ["Authorization: Bearer $token", "X-Peng-Format: full"],
        CURLOPT_TIMEOUT => 30,
    ]);
    $body = curl_exec($ch);
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $err = curl_error($ch);
    curl_close($ch);
    if ($body === false) { fwrite(STDERR, "contract: $path request failed: $err\n"); exit(1); }
    if ($status !== 200) { fwrite(STDERR, "contract: $path returned $status\n"); exit(1); }
    // snapshot.php gzips the full payload itself, with Content-Encoding: identity so nothing
    // unpacks it on the way. Clients have to know; so does this check.
    if (substr($body, 0, 2) === "\x1f\x8b") $body = gzdecode($body);
    $decoded = json_decode($body, true);
    if ($decoded === null) { fwrite(STDERR, "contract: $path is not JSON: " . substr($body, 0, 200) . "\n"); exit(1); }
    return $decoded;
}

$colonyId = (int)($argv[1] ?? 1);
$pdo = getDbConnection();

// A real session for a real user, because that is what the phone has and permissions shape the
// payload. Two minutes, and removed in the finally below however this ends.
$who = $pdo->query("SELECT id FROM users WHERE role = 'admin' AND active = 1 AND deleted_at IS NULL ORDER BY id LIMIT 1")->fetchColumn();
if (!$who) { fwrite(STDERR, "contract: no active admin to check as\n"); exit(1); }
$token = wwSessionCreateShortLived($pdo, (int)$who, 2);

try {
    $sync = fetchJson("/api/sync.php?colony_id=$colonyId", $token);

    check('sync.observer', $sync['observer'] ?? [], $OBSERVER);
    foreach (['boxes', 'previous'] as $group) {
        foreach (($sync[$group] ?? []) as $name => $box) {
            check("sync.$group.$name", $box, $BOX);
            foreach ($box['scans'] ?? [] as $i => $scan) check("sync.$group.$name.scans[$i]", $scan, $SCAN);
        }
    }
    foreach (($sync['locations'] ?? []) as $i => $loc) check("sync.locations[$i]", $loc, $LOCATION);
    foreach (($sync['users'] ?? []) as $i => $u)      check("sync.users[$i]", $u, $USER);

    // A colony with no round today has no previous-visit boxes, and that is exactly the branch
    // that broke — say so rather than passing quietly on an untested half of the payload.
    $prevCount = count($sync['previous'] ?? []);
    if ($prevCount === 0) echo "contract: note — no previous-visit boxes in this payload to check\n";

    foreach (fetchJson("/api/penguins.php", $token) as $i => $p) check("penguins[$i]", $p, $PENGUIN);

    // The incremental feed nestcheck now downloads from (LocalDb / SnapshotSyncService). Checked
    // on the full payload, which is what a phone with no watermark asks for, and where every table
    // is populated — an incremental one can legitimately be empty and prove nothing.
    $snap = fetchJson("/api/snapshot.php?colony_id=$colonyId", $token);
    $SNAP = [
        'observations' => ['observation_id' => 'int', 'location_id' => 'int', 'observation_time_utc' => 'string?',
                           'adults' => 'int', 'eggs' => 'int', 'chicks' => 'int', 'no_scan' => 'int',
                           'failed_eggs' => 'int?', 'dead_chicks' => 'int?', 'breeding_status' => 'string?',
                           'gate_status' => 'string?', 'notes' => 'string?', 'observer_id' => 'int?', 'is_deleted' => 'int'],
        'scans'        => ['scan_id' => 'int', 'observation_id' => 'int', 'pit_id' => 'string?', 'scan_deleted' => 'int?'],
        // sex_guess_m/f decide whether the phone offers to sex a bird. Absent or wrong-typed, the
        // score reads as 0 and the prompt never fires for any bird — silent, and it has happened.
        'penguins'     => ['peng_num' => 'string', 'chipped_as_adult' => 'int?', 'sex' => 'string?', 'is_dead' => 'int?',
                           'death_date' => 'string?', 'chick_size_code' => 'string?', 'alert' => 'int?', 'notes' => 'string?',
                           'sex_guess_m' => 'int?', 'sex_guess_f' => 'int?'],
        'chips'        => ['pit_id' => 'string', 'peng_num' => 'string?', 'chip_date' => 'string?', 'is_active' => 'int?', 'chip_box' => 'string?'],
        'locations'    => ['location_id' => 'int', 'location_name' => 'string?', 'persistent_notes' => 'string?', 'watched' => 'int'],
        'biometrics'   => ['biometric_id' => 'int', 'peng_num' => 'string?', 'observation_date' => 'string?',
                           'weight' => 'string?', 'flipper_length' => 'string?', 'observed_sex' => 'string?',
                           'is_moulting' => 'int?', 'disposition_passive' => 'int?',
                           'disposition_aggressive' => 'int?', 'notes' => 'string?', 'is_deleted' => 'int'],
        'day_notes'    => ['note_date' => 'string', 'note' => 'string?', 'observer_id' => 'int?', 'scribe_id' => 'int?'],
        // chip_acronym and falcon_id decide who may be named as a chipper on the phone; without
        // them the picker is empty, which is a silent failure rather than a loud one. role is how
        // the phone knows whether to offer deleting an observation — crud.php allows that to
        // editors and admins only, so without it the control just isn't there.
        'observers'    => ['observer_id' => 'int', 'observer_name' => 'string?', 'surname' => 'string?',
                           'chip_acronym' => 'string?', 'falcon_id' => 'string?', 'role' => 'string?'],
    ];
    // The location row carries the box tag as well — nestcheck builds its box_tags store from
    // these columns rather than calling boxtags.php, so a wrong type here empties the tag map.
    $SNAP['locations'] += ['pit_id' => 'string?', 'scan_time_utc' => 'string?',
                           'latitude' => 'string?', 'longitude' => 'string?', 'accuracy' => 'string?'];

    foreach ($SNAP as $group => $contract) {
        $rows = $snap[$group] ?? null;
        if (!is_array($rows) || !$rows) { $breaches[] = "snapshot.$group: missing or empty"; continue; }
        // Braces, or "$group[$i]" reads as an offset INTO $group rather than a name with an index.
        foreach ($rows as $i => $row) check("snapshot.{$group}[{$i}]", $row, $contract);
    }
    check('snapshot.me', $snap['me'] ?? [], ['observer_id' => 'int', 'name' => 'string?', 'chip_acronym' => 'string?', 'falcon_id' => 'string?']);
    // No watermark means the phone can't record where it got to, so it would either replay
    // everything forever or lose whatever arrived in between.
    if (empty($snap['snapshot_time'])) $breaches[] = 'snapshot.snapshot_time: missing';

    // scope=field is the payload nestcheck ACTUALLY downloads — the full one above is the
    // website's. Same row shapes, different set of rows, and its own branch in snapshot.php, so
    // checking only the full payload left the phone's own feed untested. An empty group is not a
    // breach here: a colony with no round today legitimately has no observations, scans or
    // biometrics, and the bird tables come as a delta once the phone has a watermark.
    $field = fetchJson("/api/snapshot.php?colony_id=$colonyId&scope=field", $token);
    foreach ($SNAP as $group => $contract) {
        $rows = $field[$group] ?? null;
        if (!is_array($rows)) { $breaches[] = "field.$group: missing"; continue; }
        if (!$rows) { echo "contract: note — field.$group is empty in this payload\n"; continue; }
        foreach ($rows as $i => $row) check("field.{$group}[{$i}]", $row, $contract);
    }
    check('field.me', $field['me'] ?? [], ['observer_id' => 'int', 'name' => 'string?', 'chip_acronym' => 'string?', 'falcon_id' => 'string?']);
    if (empty($field['snapshot_time'])) $breaches[] = 'field.snapshot_time: missing';
    // The phone rebuilds its bird stores on true and merges on false. Absent reads as true (an
    // older server always sent the whole set), so a non-boolean here is the dangerous case: it
    // would be read as "full set" and clear the stores before applying a delta of a few rows.
    if (array_key_exists('birds_full', $field) && !is_bool($field['birds_full']))
        $breaches[] = 'field.birds_full: expected a boolean, got ' . describe($field['birds_full']);
    if (($field['scope'] ?? null) !== 'field') $breaches[] = 'field.scope: expected "field"';

    // And the delta branch, asked for as the phone asks: same shapes, watermark in hand. Its bird
    // tables are normally empty, which is the entire point — the check is that they parse and that
    // birds_full says so, not that they carry rows.
    $delta = fetchJson("/api/snapshot.php?colony_id=$colonyId&scope=field&since="
        . urlencode($field['snapshot_time'] ?? '2000-01-01 00:00:00'), $token);
    foreach ($SNAP as $group => $contract) {
        foreach (($delta[$group] ?? []) as $i => $row) check("delta.{$group}[{$i}]", $row, $contract);
    }
    if (empty($delta['snapshot_time'])) $breaches[] = 'delta.snapshot_time: missing';
    if (($delta['birds_full'] ?? null) !== false)
        $breaches[] = 'delta.birds_full: expected false for a watermarked field payload, got '
                    . describe($delta['birds_full'] ?? null);
} finally {
    wwSessionDelete($pdo, $token);
}

if ($breaches) {
    fwrite(STDERR, "CONTRACT FAILED — nestcheck would not parse this payload:\n");
    foreach (array_slice($breaches, 0, 20) as $b) fwrite(STDERR, "  $b\n");
    if (count($breaches) > 20) fwrite(STDERR, "  ... and " . (count($breaches) - 20) . " more\n");
    exit(1);
}

echo "contract OK: " . count($sync['boxes'] ?? []) . " boxes, $prevCount previous, "
   . count($sync['locations'] ?? []) . " locations, " . count($sync['users'] ?? []) . " users\n";
