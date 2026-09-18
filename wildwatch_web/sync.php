<?php
/**
 * NestCheck app sync endpoint.
 *
 * GET  /penguin-api/sync.php              - Download latest observation per box with scans
 * POST /penguin-api/sync.php?action=upload - Upload dirty observations from app
 *
 * All requests require Bearer token (session auth) or X-API-Key (observer API key).
 */
require_once 'config.php';

header('Content-Type: application/json');
header('Cache-Control: no-cache');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type, Authorization');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }

$pdo = getDbConnection();
$observer = authenticate($pdo);
if (!$observer) { http_response_code(401); echo json_encode(['error' => 'Not authenticated']); exit; }

$action = $_GET['action'] ?? '';
$colonyId = (int)($_GET['colony_id'] ?? 1);

if ($_SERVER['REQUEST_METHOD'] === 'GET' && $action === '') {
    requireColonyAccess($pdo, $observer, $colonyId);        // view
    handleDownload($pdo, $colonyId, $observer);
} elseif ($_SERVER['REQUEST_METHOD'] === 'POST' && ($action === 'upload' || $action === 'confirm')) {
    // Boxes are looked up by name within this colony and an unknown name becomes a new box in it.
    $colonyId = wwRequireColonyId();
    requireColonyAccess($pdo, $observer, $colonyId, true);  // edit
    handleUpload($pdo, $colonyId, $observer);
} else {
    echo json_encode(['error' => 'Unknown action']);
}

function authenticate($pdo) {
    $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';

    // Try Bearer token (session auth)
    if (preg_match('/^Bearer\s+(.+)$/i', $header, $m)) {
        $stmt = $pdo->prepare("SELECT o.*, o.id AS observer_id, o.f_name AS observer_name FROM sessions s JOIN users o ON s.observer_id = o.id WHERE s.token = ? AND s.expires_at > NOW()");
        $stmt->execute([$m[1]]);
        $result = $stmt->fetch();
        if ($result) return $result;
    }

    return null;
}

/**
 * GET: Download latest observation per box with scans.
 */
/** The colony's day notes as note_date => [note, observer, scribe], for stamping onto each
 *  observation below. Note stays a plain string in the payload; the people ride beside it. */
function dayNoteMap($pdo, $colonyId): array {
    // The people are FKs to users; resolve them to display names here so the phone can show
    // "who was out" without carrying a copy of the user table.
    $s = $pdo->prepare("SELECT d.note_date, d.note, d.observer_id, d.scribe_id,
            TRIM(CONCAT(uo.f_name, ' ', uo.surname)) AS observer_name,
            TRIM(CONCAT(ur.f_name, ' ', ur.surname)) AS scribe_name
        FROM day_notes d
        LEFT JOIN users uo ON uo.id = d.observer_id
        LEFT JOIN users ur ON ur.id = d.scribe_id
        WHERE d.colony_id = ?");
    $s->execute([$colonyId]);
    $map = [];
    foreach ($s->fetchAll() as $r) $map[$r['note_date']] = [
        'note' => $r['note'],
        'observer_id' => $r['observer_id'] !== null ? (int)$r['observer_id'] : null,
        'scribe_id' => $r['scribe_id'] !== null ? (int)$r['scribe_id'] : null,
        'observer' => $r['observer_name'] ?: null,
        'scribe' => $r['scribe_name'] ?: null,
    ];
    return $map;
}

/** NZ calendar date of a UTC observation time — the key day notes are filed under. */
function nzDateOf(string $utc): string {
    return (new DateTime($utc, new DateTimeZone('UTC')))->setTimezone(new DateTimeZone('Pacific/Auckland'))->format('Y-m-d');
}

/**
 * The day an observation belongs to, bucketed the way the queries bucket it: a fixed +12, which
 * is what DATE(CONVERT_TZ(observation_time_utc,'+00:00','+12:00')) does and what the phone does.
 * Pacific/Auckland is +13 in daylight time, so using it to look up a +12-bucketed row put a round
 * made just after midnight on the wrong day — and a duplicate observation with it.
 */
function nzObsDay(string $utc): string {
    return gmdate('Y-m-d', strtotime($utc . ' UTC') + 12 * 3600);
}

function handleDownload($pdo, $colonyId, $observer) {
    // Latest observation per box, plus second-most-recent for boxes where latest is today
    $nzToday = (new DateTime('now', new DateTimeZone('Pacific/Auckland')))->format('Y-m-d');

    // First: get latest per box
    $stmt = $pdo->prepare("
        SELECT o.observation_id, o.location_id, ol.location_name AS box_name,
            o.observation_time_utc, o.observer_id,
            ob.f_name AS observer_name,
            o.adults, o.eggs, o.chicks, o.no_scan, o.failed_eggs, o.dead_chicks, o.breeding_status, o.gate_status, o.notes
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        LEFT JOIN users ob ON o.observer_id = ob.id
        WHERE ol.colony_id = ? AND o.is_deleted = FALSE
        AND o.observation_id = (
            SELECT o2.observation_id FROM observations o2
            WHERE o2.location_id = o.location_id AND o2.is_deleted = FALSE
            ORDER BY o2.observation_time_utc DESC LIMIT 1
        )
        ORDER BY ol.location_name + 0, ol.location_name
    ");
    $stmt->execute([$colonyId]);
    $latestObs = $stmt->fetchAll();

    // Find boxes where latest is from today — fetch their second-most-recent
    $todayBoxLocationIds = [];
    foreach ($latestObs as $obs) {
        $obsNzDate = (new DateTime($obs['observation_time_utc'], new DateTimeZone('UTC')))->setTimezone(new DateTimeZone('Pacific/Auckland'))->format('Y-m-d');
        if ($obsNzDate === $nzToday) $todayBoxLocationIds[] = $obs['location_id'];
    }

    $previousObs = [];
    if (!empty($todayBoxLocationIds)) {
        $ph = implode(',', array_fill(0, count($todayBoxLocationIds), '?'));
        $prevStmt = $pdo->prepare("
            SELECT o.observation_id, o.location_id, ol.location_name AS box_name,
                o.observation_time_utc, o.observer_id,
                ob.f_name AS observer_name,
                o.adults, o.eggs, o.chicks, o.no_scan, o.failed_eggs, o.dead_chicks, o.breeding_status, o.gate_status, o.notes
            FROM observations o
            JOIN observation_locations ol ON o.location_id = ol.location_id
            LEFT JOIN users ob ON o.observer_id = ob.id
            WHERE ol.colony_id = ? AND o.is_deleted = FALSE
            AND o.location_id IN ($ph)
            AND o.observation_id = (
                SELECT o3.observation_id FROM observations o3
                WHERE o3.location_id = o.location_id AND o3.is_deleted = FALSE
                ORDER BY o3.observation_time_utc DESC LIMIT 1 OFFSET 1
            )
        ");
        $prevStmt->execute(array_merge([$colonyId], $todayBoxLocationIds));
        $previousObs = $prevStmt->fetchAll();
    }

    $observations = array_merge($latestObs, $previousObs);

    // Batch fetch scans for all these observations
    $obsIds = array_column($observations, 'observation_id');
    $scansByObs = [];
    if (!empty($obsIds)) {
        $ph = implode(',', array_fill(0, count($obsIds), '?'));
        $scanStmt = $pdo->prepare("SELECT ps.observation_id, ps.pit_id, ps.scan_time_utc,
            pc.peng_num, p.sex, p.chick_size_code, p.chipped_as_adult, p.alert
            FROM penguin_scans ps
            LEFT JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 1
            LEFT JOIN penguins p ON pc.peng_num = p.peng_num
            WHERE ps.observation_id IN ($ph) AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)");
        $scanStmt->execute(array_values($obsIds));
        foreach ($scanStmt->fetchAll() as $scan) {
            $scansByObs[$scan['observation_id']][] = [
                'pit_id' => $scan['pit_id'],
                'scan_time_utc' => $scan['scan_time_utc'],
                'peng_num' => displayPengNum($scan['peng_num'] ?? '', getColonyPrefix($pdo, $colonyId)),
                'sex' => $scan['sex'],
                'chick_size_code' => $scan['chick_size_code'],
                'chipped_as_adult' => $scan['chipped_as_adult'],
                'alert' => (int)($scan['alert'] ?? 0),
            ];
        }
    }

    // Build response: latest per box + previous for today's boxes.
    // Each observation carries its day's note. `monitor_filename` is the same value under the
    // name the installed app still deserialises — the per-observation column is gone; drop the
    // alias once nestcheck ships a release that reads `day_note`.
    $dayNotes = dayNoteMap($pdo, $colonyId);
    $boxes = [];
    $previous = [];
    $latestIds = [];
    foreach ($latestObs as $obs) {
        $boxName = $obs['box_name'];
        $day = $dayNotes[nzDateOf($obs['observation_time_utc'])] ?? null;
        $note = $day['note'] ?? null;
        $boxes[$boxName] = [
            'observation_id' => (int)$obs['observation_id'],
            'location_id' => (int)$obs['location_id'],
            'observation_time_utc' => $obs['observation_time_utc'],
            'day_note' => $note,
            'day_observer' => $day['observer'] ?? null,
            'day_scribe' => $day['scribe'] ?? null,
            'day_observer_id' => $day['observer_id'] ?? null,
            'day_scribe_id' => $day['scribe_id'] ?? null,
            'monitor_filename' => $note,
            'observer_name' => $obs['observer_name'],
            'adults' => (int)$obs['adults'],
            'eggs' => (int)$obs['eggs'],
            'chicks' => (int)$obs['chicks'],
            'no_scan' => (int)($obs['no_scan'] ?? 0),
            // Null means nobody recorded either way; 0 means checked and none lost. Casting a
            // null to int here would turn "not recorded" into "checked, none" on every phone.
            'failed_eggs' => $obs['failed_eggs'] === null ? null : (int)$obs['failed_eggs'],
            'dead_chicks' => $obs['dead_chicks'] === null ? null : (int)$obs['dead_chicks'],
            'breeding_status' => $obs['breeding_status'],
            'gate_status' => $obs['gate_status'],
            'notes' => $obs['notes'],
            'scans' => $scansByObs[$obs['observation_id']] ?? [],
        ];
    }
    foreach ($previousObs as $obs) {
        $boxName = $obs['box_name'];
        // The day's entry is a record (note + who was out); the note is the string field of it.
        // Emitting the whole record here made day_note/monitor_filename an object, and the phone
        // types monitor_filename as a string — the parse threw and the entire box download failed.
        $day = $dayNotes[nzDateOf($obs['observation_time_utc'])] ?? null;
        $note = $day['note'] ?? null;
        $previous[$boxName] = [
            'observation_id' => (int)$obs['observation_id'],
            'location_id' => (int)$obs['location_id'],
            'observation_time_utc' => $obs['observation_time_utc'],
            'day_note' => $note,
            'day_observer' => $day['observer'] ?? null,
            'day_scribe' => $day['scribe'] ?? null,
            'day_observer_id' => $day['observer_id'] ?? null,
            'day_scribe_id' => $day['scribe_id'] ?? null,
            'monitor_filename' => $note,
            'observer_name' => $obs['observer_name'],
            'adults' => (int)$obs['adults'],
            'eggs' => (int)$obs['eggs'],
            'chicks' => (int)$obs['chicks'],
            'no_scan' => (int)($obs['no_scan'] ?? 0),
            // Null means nobody recorded either way; 0 means checked and none lost. Casting a
            // null to int here would turn "not recorded" into "checked, none" on every phone.
            'failed_eggs' => $obs['failed_eggs'] === null ? null : (int)$obs['failed_eggs'],
            'dead_chicks' => $obs['dead_chicks'] === null ? null : (int)$obs['dead_chicks'],
            'breeding_status' => $obs['breeding_status'],
            'gate_status' => $obs['gate_status'],
            'notes' => $obs['notes'],
            'scans' => $scansByObs[$obs['observation_id']] ?? [],
        ];
    }

    // Locations
    $locStmt = $pdo->prepare("SELECT location_id, location_name, persistent_notes, watched FROM observation_locations WHERE colony_id = ?");
    $locStmt->execute([$colonyId]);

    // Everyone who can be named as the day's observer or scribe. Active only — the phone is
    // picking who is out today, and a deactivated person is by definition not. Names are
    // pre-joined so the app never has to compose them.
    $userStmt = $pdo->query("SELECT id, TRIM(CONCAT(f_name, ' ', surname)) AS name, f_name, surname, chip_acronym, falcon_id
        FROM users WHERE active = 1 AND deleted_at IS NULL ORDER BY f_name, surname");

    echo json_encode([
        'snapshot_time' => date('c'),
        // chip_acronym rides along so the phone signs a chipping with the initials the chip
        // record expects ("BS"), not the login's display name ("Britta") — which is what put 26
        // rows of "Britta" in penguin_chips.chip_by. Refreshed every sync, so assigning or
        // changing an acronym in the admin screen reaches the phone without a re-login.
        'observer' => ['observer_id' => (int)$observer['observer_id'], 'name' => $observer['observer_name'],
                       'chip_acronym' => $observer['chip_acronym'] ?? null,
                       'falcon_id' => $observer['falcon_id'] ?? null],
        'boxes' => (object)$boxes,
        'previous' => (object)$previous,
        'locations' => $locStmt->fetchAll(),
        'users' => $userStmt->fetchAll(),
    ]);
}

/**
 * POST ?action=upload: Upload dirty observations from app.
 * If a box already has today's observation on the server, returns it as a conflict
 * instead of overwriting. Use ?action=confirm to force-replace conflicts.
 */
// Normalise an incoming ISO-8601 datetime (e.g. "2026-07-03T10:30:00Z") to MySQL DATETIME
// format. Missing / unparseable / out-of-range values (e.g. "0001-01-01...") fall back.
function normalizeDateTime($val, $fallback) {
    if (empty($val)) return $fallback;
    try {
        $dt = new DateTime($val);
        $y = (int)$dt->format('Y');
        if ($y < 1000 || $y > 9999) return $fallback;
        return $dt->format('Y-m-d H:i:s');
    } catch (Exception $e) {
        return $fallback;
    }
}

function handleUpload($pdo, $colonyId, $observer) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input || !isset($input['observations'])) {
        http_response_code(400);
        echo json_encode(['error' => 'JSON body with observations array required']);
        return;
    }

    // Whole-request intent: 'confirm' means the person already agreed to replace what's there.
    // Per-observation it is decided again below — one box earning a replace must never hand that
    // permission to the boxes after it in the same payload.
    $replaceAll = ($_GET['action'] === 'confirm');
    $dailyLabel = $input['daily_label'] ?? '';
    $dailyObserverId = (int)($input['daily_observer_id'] ?? 0) ?: null;
    $dailyScribeId = (int)($input['daily_scribe_id'] ?? 0) ?: null;
    $observerId = (int)$observer['observer_id'];
    $created = [];
    $conflicts = [];
    $errors = [];
    $labelDates = [];   // NZ dates this payload wrote to — the days $dailyLabel describes

    $nzToday = (new DateTime('now', new DateTimeZone('Pacific/Auckland')))->format('Y-m-d');

    // Build chip lookup for validating pit_ids
    $chipLookup = [];
    foreach ($pdo->query("SELECT pit_id FROM penguin_chips")->fetchAll() as $c) {
        $chipLookup[strtoupper($c['pit_id'])] = $c['pit_id'];
        $chipLookup[strtoupper(substr($c['pit_id'], -8))] = $c['pit_id'];
    }

    // Batch-fetch scans and day notes for conflict display
    $conflictDayNotes = dayNoteMap($pdo, $colonyId);
    $viewPrefix = getColonyPrefix($pdo, $colonyId);
    $fetchScansForObs = function($obsId) use ($pdo, $chipLookup, $viewPrefix) {
        $s = $pdo->prepare("SELECT ps.pit_id, ps.scan_time_utc, pc.peng_num, p.sex, p.chick_size_code, p.alert
            FROM penguin_scans ps LEFT JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 1
            LEFT JOIN penguins p ON pc.peng_num = p.peng_num WHERE ps.observation_id = ? AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)");
        $s->execute([$obsId]);
        $rows = $s->fetchAll();
        stripPengPrefix($rows, $viewPrefix);
        return $rows;
    };

    $pdo->beginTransaction();
    try {
        foreach ($input['observations'] as $obs) {
            $boxName = $obs['box_name'] ?? null;
            if ($boxName === null || $boxName === '') { $errors[] = ['error' => 'Missing box_name', 'obs' => $obs]; continue; }

            // Look up location_id
            $locStmt = $pdo->prepare("SELECT location_id FROM observation_locations WHERE colony_id = ? AND location_name = ?");
            $locStmt->execute([$colonyId, $boxName]);
            $locationId = $locStmt->fetchColumn();
            if (!$locationId) {
                // A box the server has never seen. Audited like any other row — a new box
                // appearing in a colony should never be silent.
                $locationId = wwAuditedInsert($pdo, 'observation_locations',
                    ['colony_id' => $colonyId, 'location_name' => $boxName, 'location_type' => 'box'],
                    $observerId, 'Created by nestcheck sync');
            }

            $obsTime = normalizeDateTime($obs['observation_time_utc'] ?? null, date('Y-m-d H:i:s'));
            // The day this observation was MADE, which is not necessarily today: a phone out of
            // signal for three days uploads three days of rounds at once. Matching those against
            // today's rows let an old round duplicate its own day, and let "Replace" overwrite
            // today's observation with three-day-old counts.
            $obsNzDate = nzObsDay($obsTime);
            $forceReplace = $replaceAll;   // this observation's own decision, reset every time

            // Check if this box already has an observation for that day on the server
            if (!$forceReplace) {
                $existingStmt = $pdo->prepare("SELECT o.observation_id, o.observation_time_utc, o.adults, o.eggs, o.chicks, o.no_scan,
                    o.breeding_status, o.gate_status, o.notes, ob.f_name AS observer_name
                    FROM observations o LEFT JOIN users ob ON o.observer_id = ob.id
                    WHERE o.location_id = ? AND o.is_deleted = FALSE
                    AND DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) = ?
                    ORDER BY o.observation_time_utc DESC LIMIT 1");
                $existingStmt->execute([$locationId, $obsNzDate]);
                $existing = $existingStmt->fetch();

                if ($existing) {
                    // Optimistic concurrency: if client confirmed against this exact observation, auto-replace
                    $expectedObsId = $obs['expected_observation_id'] ?? null;
                    if ($expectedObsId !== null && (int)$expectedObsId === (int)$existing['observation_id']) {
                        $forceReplace = true;
                        // Fall through to the force-replace logic below
                    } else {
                        // Conflict: server data differs from what client expected
                        $conflicts[] = [
                            'box_name' => $boxName,
                            'server' => [
                                'observation_id' => (int)$existing['observation_id'],
                                'observation_time_utc' => $existing['observation_time_utc'],
                                'observer_name' => $existing['observer_name'],
                                'adults' => (int)$existing['adults'],
                                'eggs' => (int)$existing['eggs'],
                                'chicks' => (int)$existing['chicks'],
                                'no_scan' => (int)($existing['no_scan'] ?? 0),
                                'breeding_status' => $existing['breeding_status'],
                                'gate_status' => $existing['gate_status'],
                                'notes' => $existing['notes'],
                                'day_note' => $conflictDayNotes[nzDateOf($existing['observation_time_utc'])]['note'] ?? null,
                                'monitor_filename' => $conflictDayNotes[nzDateOf($existing['observation_time_utc'])]['note'] ?? null,
                                'scans' => $fetchScansForObs($existing['observation_id']),
                            ],
                            'incoming' => $obs,
                        ];
                        continue; // Skip — don't write
                    }
                }
            }

            // Count no-scan placeholders
            $noScanCount = 0;
            foreach ($obs['scans'] ?? [] as $sc) {
                if (str_starts_with(strtoupper($sc['pit_id'] ?? ''), 'NOSCAN')) $noScanCount++;
            }

            $obsRow = [
                'location_id' => $locationId, 'observer_id' => $observerId, 'observation_time_utc' => $obsTime,
                'adults' => (int)($obs['adults'] ?? 0), 'eggs' => (int)($obs['eggs'] ?? 0), 'chicks' => (int)($obs['chicks'] ?? 0),
                'breeding_status' => $obs['breeding_status'] ?? null, 'gate_status' => $obs['gate_status'] ?? null,
                'notes' => $obs['notes'] ?? '', 'no_scan' => $noScanCount,
            ];
            // Only overwrite when the phone actually sent the key — an older build that doesn't
            // know these fields must not blank what someone entered on the website.
            foreach (['failed_eggs', 'dead_chicks'] as $lossField) {
                if (array_key_exists($lossField, $obs)) {
                    $obsRow[$lossField] = $obs[$lossField] === null ? null : (int)$obs[$lossField];
                }
            }
            // The day the phone's label describes — collected here, written once after the loop.
            $labelDates[nzDateOf($obsTime)] = true;

            // Create, or force-replace in place. wwAuditedUpdate logs only the fields that actually
            // differ, so a re-sync reads as "what changed" rather than a re-print of the row.
            $existingId = null;
            $oldScans = [];        // pit_id => scan_id, the birds already on this observation
            if ($forceReplace) {
                $existingStmt = $pdo->prepare("SELECT observation_id FROM observations
                    WHERE location_id = ? AND is_deleted = FALSE
                    AND DATE(CONVERT_TZ(observation_time_utc, '+00:00', '+12:00')) = ?
                    ORDER BY observation_time_utc DESC LIMIT 1");
                $existingStmt->execute([$locationId, $obsNzDate]);   // the incoming day, not today
                $existingId = $existingStmt->fetchColumn() ?: null;
            }

            if ($existingId) {
                wwAuditedUpdate($pdo, 'observations', $existingId, $obsRow, $observerId, 'nestcheck_sync');
                $observationId = $existingId;
                // Old scans are kept, not wiped: the loop below adds only birds that weren't
                // already here, and removes only those the phone no longer reports. Blanket
                // delete-and-recreate churned scan_ids and hid who actually arrived or left.
                $oldScanStmt = $pdo->prepare("SELECT scan_id, pit_id FROM penguin_scans WHERE observation_id = ? AND (is_deleted = FALSE OR is_deleted IS NULL)");
                $oldScanStmt->execute([$observationId]);
                foreach ($oldScanStmt->fetchAll(PDO::FETCH_ASSOC) as $os) $oldScans[$os['pit_id']] = (int)$os['scan_id'];
            } else {
                $observationId = wwAuditedInsert($pdo, 'observations', $obsRow, $observerId, 'nestcheck_sync');
            }

            // Create penguin scans. Each insert is audited (wwAuditedInsert) so the observation's
            // change history names the bird that arrived, instead of only moving a scan count.
            $scansCreated = 0;
            $seenPits = [];
            foreach ($obs['scans'] ?? [] as $scan) {
                $pitId = $scan['pit_id'] ?? '';
                if (empty($pitId)) continue;

                $scanTime = normalizeDateTime($scan['scan_time_utc'] ?? null, $obsTime);
                $lat = isset($scan['latitude']) && $scan['latitude'] != 0 ? $scan['latitude'] : null;
                $lon = isset($scan['longitude']) && $scan['longitude'] != 0 ? $scan['longitude'] : null;
                $acc = isset($scan['accuracy']) && $scan['accuracy'] > 0 ? $scan['accuracy'] : null;

                // No-scan placeholder — skip DB, these are app-only placeholders
                if (str_starts_with(strtoupper($pitId), 'NOSCAN')) continue;

                // Resolve to the pit_id as stored. The reader hands the app "LA" + 15 digits and
                // older builds upload it that way, so normalize first — then the whole-tag key
                // hits and the last-8 fallback is left for genuinely short ids.
                $cleanId = strtoupper(preg_replace('/[^A-Za-z0-9]/', '', (string)ww_pit($pitId)));
                $fullPitId = $chipLookup[$cleanId] ?? $chipLookup[substr($cleanId, -8)] ?? null;
                if (!$fullPitId) { $errors[] = ['error' => "Unknown pit_id: $pitId", 'box' => $boxName]; continue; }

                // Skip duplicate pit_id for same observation — within this payload, or already stored
                if (isset($seenPits[$fullPitId])) continue;
                $seenPits[$fullPitId] = true;
                if (isset($oldScans[$fullPitId])) continue;   // bird already on this observation

                wwAuditedInsert($pdo, 'penguin_scans', [
                    'observation_id' => (int)$observationId, 'pit_id' => $fullPitId, 'scan_time_utc' => $scanTime,
                    'latitude' => $lat, 'longitude' => $lon, 'accuracy' => $acc,
                ], $observerId);
                $scansCreated++;
            }

            // Birds the phone no longer reports on this observation — soft-delete, and audit each
            // so the history reads "<bird> removed" rather than a silently shrinking count.
            $scansRemoved = 0;
            foreach ($oldScans as $pit => $scanId) {
                if (isset($seenPits[$pit])) continue;
                wwAuditedDelete($pdo, 'penguin_scans', $scanId, $observerId, 'nestcheck_sync');
                $scansRemoved++;
            }
            $scanTotal = count($seenPits);

            // nz_date names WHICH day landed. With several days uploaded at once the box name alone
            // doesn't say which queued round this reply is for, so the phone can't tell which one
            // to clear — and clearing the wrong one leaves a round to upload twice. Sent in the
            // phone's own day convention (real NZ time), not the +12 the row lookup above needs.
            $created[] = ['box_name' => $boxName, 'nz_date' => nzDateOf($obsTime),
                          'observation_id' => (int)$observationId, 'scans' => $scanTotal,
                          'scans_added' => $scansCreated, 'scans_removed' => $scansRemoved];
        }

        // The phone's "Daily label" is the day's note, and who was observing/recording rides with
        // it. It fills a day that has none rather than overwriting: by the time a second sync
        // arrives the note may have been corrected in the web day view, and a re-sync of the same
        // label should not undo that edit.
        foreach (array_keys($labelDates) as $noteDate) {
            wwFillDayNote($pdo, $colonyId, $noteDate, $dailyLabel, $observerId, 'nestcheck_sync', $dailyObserverId, $dailyScribeId);
        }

        $pdo->commit();
        $response = ['success' => true, 'created' => $created, 'errors' => $errors];
        if (!empty($conflicts)) $response['conflicts'] = $conflicts;
        @file_put_contents(__DIR__ . '/sync_debug.log', date('Y-m-d H:i:s') . ' ' . json_encode($response) . "\n", FILE_APPEND);
        echo json_encode($response);
    } catch (Exception $e) {
        $pdo->rollBack();
        http_response_code(500);
        echo json_encode(['error' => $e->getMessage()]);
    }
}
