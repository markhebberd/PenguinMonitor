<?php
/**
 * Admin API: user management + monitor sync
 * All actions require admin role.
 *
 * GET  ?action=users           - list all observers
 * POST ?action=update_user     - update observer {observer_id, role, observer_name, email}
 */
require_once 'config.php';
header('Content-Type: application/json');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type, Authorization');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }

$pdo = getDbConnection();

// Auth (header or query param for downloads)
$header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
if (!preg_match('/^Bearer\s+(.+)$/i', $header, $m)) {
    if (!empty($_GET['token'])) { $m = [null, $_GET['token']]; }
    else { http_response_code(401); echo json_encode(['error'=>'Auth required']); exit; }
}
$stmt = $pdo->prepare("SELECT o.*, o.id AS observer_id, o.f_name AS observer_name FROM sessions s JOIN users o ON s.observer_id = o.id WHERE s.token = ? AND s.expires_at > NOW()");
$stmt->execute([$m[1]]);
$observer = $stmt->fetch();
if (!$observer) { http_response_code(401); echo json_encode(['error'=>'Invalid token']); exit; }
if (($observer['role'] ?? '') !== 'admin') { http_response_code(403); echo json_encode(['error'=>'Admin required']); exit; }

$action = $_GET['action'] ?? '';

// Read-only SQL console. Available to all admins (role gate enforced above).
if ($action === 'sql') {
    $input = json_decode(file_get_contents('php://input'), true) ?? [];
    $sql = trim($input['sql'] ?? '');
    if ($sql === '') { echo json_encode(['error' => 'Empty query']); exit; }

    // Strip a single trailing semicolon; reject anything that looks like stacked statements.
    $sql = rtrim($sql, "; \t\n\r");
    if (strpos($sql, ';') !== false) { echo json_encode(['error' => 'Only a single statement is allowed']); exit; }

    // UX guardrail so mistakes fail fast. NOT the security boundary — that is the
    // SELECT-only grant on DB_RO_USER, which makes writes impossible regardless.
    if (!preg_match('/^(SELECT|WITH|SHOW|EXPLAIN|DESCRIBE|DESC)\b/i', $sql)) {
        echo json_encode(['error' => 'Only SELECT / WITH / SHOW / EXPLAIN / DESCRIBE queries are allowed']); exit;
    }

    // Auto-cap row-returning queries; fetch one extra to detect truncation.
    $limit = 1000; $capped = false;
    if (preg_match('/^(SELECT|WITH)\b/i', $sql) && !preg_match('/\bLIMIT\s+\d/i', $sql)) {
        $sql .= " LIMIT " . ($limit + 1); $capped = true;
    }

    try {
        $ro = getReadOnlyDbConnection();
        $t0 = microtime(true);
        $stmt = $ro->query($sql);
        $rows = $stmt->fetchAll(PDO::FETCH_ASSOC);
        $ms = (int) round((microtime(true) - $t0) * 1000);
        $truncated = false;
        if ($capped && count($rows) > $limit) { $rows = array_slice($rows, 0, $limit); $truncated = true; }
        $columns = $rows ? array_keys($rows[0]) : [];
        // Not audited: reads change nothing, and the Database tab's automated browsing was
        // flooding the log. The SELECT-only grant on DB_RO_USER is the security boundary.
        echo json_encode(['success' => true, 'columns' => $columns, 'rows' => $rows, 'rowCount' => count($rows), 'truncated' => $truncated, 'ms' => $ms]);
    } catch (PDOException $e) {
        echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}

if ($action === 'duplicate_scans') {
    // Duplicates by pit_id
    $byPit = $pdo->query("
        SELECT ps.observation_id, ps.pit_id, COUNT(*) as cnt, MIN(ps.scan_id) as keep_id,
            ol.location_name as box_name, pc.peng_num,
            DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) as obs_date,
            'pit_id' as dup_type
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        LEFT JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 1
        WHERE (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
        GROUP BY ps.observation_id, ps.pit_id
        HAVING cnt > 1
    ")->fetchAll();

    // Duplicates by peng_num (different pit_ids mapping to same penguin — exclude exact dups already found)
    $byPeng = $pdo->query("
        SELECT ps.observation_id, pc.peng_num, COUNT(*) as cnt, MIN(ps.scan_id) as keep_id,
            ol.location_name as box_name,
            DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) as obs_date,
            GROUP_CONCAT(DISTINCT ps.pit_id) as pit_ids,
            'peng_num' as dup_type
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 1
        WHERE (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
        GROUP BY ps.observation_id, pc.peng_num
        HAVING cnt > 1 AND COUNT(DISTINCT ps.pit_id) > 1
    ")->fetchAll();

    echo json_encode(array_merge($byPit, $byPeng));
    exit;
}

// NOTE: cleanup_duplicate_scans was intentionally removed. Duplicate scans are
// preserved as evidence of data-entry errors and must not be bulk-deleted.

if ($action === 'same_gender_conflicts') {
    // Find observations where 2+ penguins of the same sex were scanned at the same box on the same day
    $stmt = $pdo->query("
        SELECT ol.location_name AS box_name,
            DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) AS obs_date,
            p.sex,
            COUNT(DISTINCT pc.peng_num) AS cnt,
            GROUP_CONCAT(DISTINCT pc.peng_num ORDER BY pc.peng_num) AS peng_nums
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 1
        JOIN penguins p ON pc.peng_num = p.peng_num
        WHERE o.is_deleted = FALSE
          AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
          AND p.sex IS NOT NULL AND p.sex != ''
        GROUP BY ol.location_name, DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')), p.sex
        HAVING cnt > 1
        ORDER BY obs_date DESC, ol.location_name + 0
    ");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'duplicate_observations') {
    $stmt = $pdo->query("
        SELECT ol.location_name as box_name,
            DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) as obs_date,
            COUNT(*) as cnt,
            GROUP_CONCAT(o.observation_id ORDER BY o.observation_time_utc DESC) as obs_ids,
            GROUP_CONCAT(COALESCE(ob.f_name,'?') ORDER BY o.observation_time_utc DESC) as observers
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        LEFT JOIN users ob ON o.observer_id = ob.id
        WHERE o.is_deleted = FALSE
        GROUP BY ol.location_name, DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00'))
        HAVING cnt > 1
        ORDER BY obs_date DESC, ol.location_name + 0
    ");
    echo json_encode($stmt->fetchAll());
    exit;
}

// ---- Data-integrity checks (whole-DB versions of the CSV-import validations) ----
define('WW_NZ', "DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00'))");

// A penguin scanned at two different boxes on the same day — can't be two places at once.
if ($action === 'bird_two_boxes') {
    echo json_encode($pdo->query("
        SELECT pc.peng_num, " . WW_NZ . " AS obs_date,
            COUNT(DISTINCT ol.location_name) AS box_count,
            GROUP_CONCAT(DISTINCT ol.location_name ORDER BY ol.location_name + 0) AS boxes
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
        WHERE o.is_deleted = FALSE AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
        GROUP BY pc.peng_num, " . WW_NZ . "
        HAVING box_count > 1
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

// A scan dated before the bird's chip was fitted — impossible.
if ($action === 'scan_before_chip') {
    echo json_encode($pdo->query("
        SELECT pc.peng_num, pc.chip_date, ol.location_name AS box_name, " . WW_NZ . " AS obs_date
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
        WHERE o.is_deleted = FALSE AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
          AND pc.chip_date IS NOT NULL AND " . WW_NZ . " < pc.chip_date
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

// Birds scanned AFTER their recorded death date — the death date or the scan is wrong.
if ($action === 'dead_scanned') {
    echo json_encode($pdo->query("
        SELECT p.peng_num, DATE(CONVERT_TZ(p.death_date, '+00:00', '+12:00')) AS death_date,
            MAX(" . WW_NZ . ") AS last_scan, COUNT(*) AS scan_count
        FROM penguins p
        JOIN penguin_chips pc ON pc.peng_num = p.peng_num
        JOIN penguin_scans ps ON ps.pit_id = pc.pit_id
        JOIN observations o ON ps.observation_id = o.observation_id
        WHERE p.death_date IS NOT NULL AND o.is_deleted = FALSE
          AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
          AND o.observation_time_utc > p.death_date
        GROUP BY p.peng_num, p.death_date
        ORDER BY last_scan DESC
        LIMIT 500")->fetchAll());
    exit;
}

// Improbable counts for a little-penguin box: adults > 2 or eggs + chicks > 2.
if ($action === 'improbable_counts') {
    echo json_encode($pdo->query("
        SELECT ol.location_name AS box_name, " . WW_NZ . " AS obs_date,
            o.adults, o.eggs, o.chicks, o.no_scan
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE o.is_deleted = FALSE AND (o.adults > 2 OR (o.eggs + o.chicks) > 2)
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

// Observations dated in the future (NZ) — almost always a typo.
if ($action === 'future_observations') {
    echo json_encode($pdo->query("
        SELECT ol.location_name AS box_name, " . WW_NZ . " AS obs_date,
            COALESCE(ob.f_name, '?') AS observer
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        LEFT JOIN users ob ON o.observer_id = ob.id
        WHERE o.is_deleted = FALSE
          AND " . WW_NZ . " > DATE(CONVERT_TZ(NOW(), '+00:00', '+12:00'))
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

// Scans recorded via a retired (inactive) chip AFTER the bird got a newer active one.
if ($action === 'retired_tag_scans') {
    echo json_encode($pdo->query("
        SELECT pc.peng_num, ps.pit_id, ol.location_name AS box_name,
            " . WW_NZ . " AS obs_date, a.chip_date AS active_chip_date
        FROM penguin_scans ps
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id AND pc.is_active = 0
        JOIN penguin_chips a ON a.peng_num = pc.peng_num AND a.is_active = 1
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE o.is_deleted = FALSE AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
          AND a.chip_date IS NOT NULL AND " . WW_NZ . " > a.chip_date
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

// Chicks chipped in a box, then chicks recorded there within the following month but with no
// scans on that observation — the chicks are chippable, so zero scans is a likely missed scan.
if ($action === 'chicks_no_scan') {
    echo json_encode($pdo->query("
        SELECT ol.location_name AS box_name, " . WW_NZ . " AS obs_date, o.chicks,
            (SELECT COUNT(DISTINCT pp.peng_num)
               FROM penguin_chips c JOIN penguins pp ON c.peng_num = pp.peng_num
               WHERE (c.location_id = o.location_id OR c.chip_box = ol.location_name)
                 AND pp.chipped_as_adult = 0
                 AND c.chip_date < " . WW_NZ . "
                 AND c.chip_date >= DATE_SUB(" . WW_NZ . ", INTERVAL 31 DAY)
            ) AS chicks_chipped
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE o.is_deleted = FALSE AND o.chicks > 0
          AND NOT EXISTS (SELECT 1 FROM penguin_scans ps
                          WHERE ps.observation_id = o.observation_id
                            AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL))
        HAVING chicks_chipped >= 2
        ORDER BY obs_date DESC
        LIMIT 500")->fetchAll());
    exit;
}

if ($action === 'cleanup_duplicate_observations') {
    // Keep the most recent observation per box per day, soft-delete the rest
    $stmt = $pdo->query("
        SELECT ol.location_name as box_name, o.location_id,
            DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) as obs_date,
            MAX(o.observation_id) as keep_id
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE o.is_deleted = FALSE
        GROUP BY o.location_id, DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00'))
        HAVING COUNT(*) > 1
    ");
    $groups = $stmt->fetchAll();
    $deleted = 0;
    $pdo->beginTransaction();
    try {
        foreach ($groups as $g) {
            // The losers of each duplicate group, deleted one at a time so each observation and
            // each of its scans/biometrics lands in the audit log as its own entry.
            $dupes = $pdo->prepare("SELECT observation_id FROM observations
                WHERE location_id = ? AND is_deleted = FALSE
                  AND DATE(CONVERT_TZ(observation_time_utc, '+00:00', '+12:00')) = ? AND observation_id != ?");
            $dupes->execute([$g['location_id'], $g['obs_date'], $g['keep_id']]);
            foreach ($dupes->fetchAll(PDO::FETCH_COLUMN) as $dupId) {
                wwAuditedDeleteObservationChildren($pdo, $dupId, $observer['observer_id'], 'Duplicate observation cleanup');
                wwAuditedDelete($pdo, 'observations', $dupId, $observer['observer_id'], 'Duplicate observation cleanup');
                $deleted++;
            }
        }
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]); exit; }
    echo json_encode(['duplicate_groups' => count($groups), 'observations_deleted' => $deleted]);
    exit;
}

if ($action === 'recent_changes') {
    $days = min(30, max(1, (int)($_GET['days'] ?? 7)));
    $stmt = $pdo->prepare("SELECT a.*,
        o.f_name AS observer_name,
        DATE(CONVERT_TZ(a.change_timestamp, '+00:00', '+12:00')) as nz_date,
        DATE_FORMAT(CONVERT_TZ(a.change_timestamp, '+00:00', '+12:00'), '%H:%i') as nz_time,
        CASE WHEN a.table_name = 'observations' THEN
            (SELECT ol.location_name FROM observations obs JOIN observation_locations ol ON obs.location_id = ol.location_id WHERE obs.observation_id = a.record_id LIMIT 1)
        END as box_name
        FROM audit_log a
        LEFT JOIN users o ON a.observer_id = o.id
        WHERE a.change_timestamp >= DATE_SUB(NOW(), INTERVAL ? DAY)
          AND a.action <> 'SELECT' AND a.table_name <> '__sql_console'
        ORDER BY a.change_timestamp DESC");
    $stmt->execute([$days]);
    $rows = $stmt->fetchAll();

    // The audit row for an observation records a scan COUNT, which tells a reader nothing about
    // WHICH birds were there. Attach the observation's scans and no-scan count so the panel can
    // draw the same penguin minis as the box view. These are the observation's CURRENT birds,
    // not its birds as at the change — the audit log doesn't store per-scan history.
    $obsIds = array_values(array_unique(array_map(
        fn($r) => (int)$r['record_id'],
        array_filter($rows, fn($r) => $r['table_name'] === 'observations'))));
    if ($obsIds) {
        $ph = implode(',', array_fill(0, count($obsIds), '?'));
        $s = $pdo->prepare(
            "SELECT ps.observation_id, ps.pit_id, pc.peng_num, pc.chip_date, p.sex, p.chipped_as_adult, p.chick_size_code
               FROM penguin_scans ps
               LEFT JOIN penguin_chips pc ON pc.pit_id = ps.pit_id
               LEFT JOIN penguins p ON p.peng_num = pc.peng_num
              WHERE ps.observation_id IN ($ph) AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
              ORDER BY ps.scan_id");
        $s->execute($obsIds);
        $scansByObs = [];
        foreach ($s->fetchAll() as $sc) {
            $oid = (int)$sc['observation_id'];
            unset($sc['observation_id']);
            $scansByObs[$oid][] = $sc;
        }
        $m = $pdo->prepare("SELECT observation_id, no_scan, observation_time_utc FROM observations WHERE observation_id IN ($ph)");
        $m->execute($obsIds);
        $obsMeta = [];
        foreach ($m->fetchAll() as $o) $obsMeta[(int)$o['observation_id']] = $o;
        foreach ($rows as &$r) {
            if ($r['table_name'] !== 'observations') continue;
            $oid = (int)$r['record_id'];
            $r['obs_scans'] = $scansByObs[$oid] ?? [];
            $r['obs_no_scan'] = (int)($obsMeta[$oid]['no_scan'] ?? 0);
            $r['obs_time'] = $obsMeta[$oid]['observation_time_utc'] ?? null;
        }
        unset($r);
    }
    echo json_encode($rows);
    exit;
}

if ($action === 'users') {
    $stmt = $pdo->query("SELECT id AS observer_id, f_name AS observer_name, surname, falcon_id, chip_acronym, active, email, role, created_at FROM users WHERE deleted_at IS NULL ORDER BY active DESC, f_name, surname");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'update_user') {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input || !$input['observer_id']) { http_response_code(400); echo json_encode(['error'=>'observer_id required']); exit; }
    // The API still speaks observer_name; the column behind it is users.f_name.
    $fields = [];
    foreach (['role' => 'role', 'observer_name' => 'f_name', 'surname' => 'surname', 'falcon_id' => 'falcon_id',
              'chip_acronym' => 'chip_acronym', 'active' => 'active', 'email' => 'email'] as $in => $col) {
        if (isset($input[$in])) $fields[$col] = $col === 'active' ? (int)(bool)$input[$in] : $input[$in];
    }
    if (empty($fields)) { echo json_encode(['success'=>true]); exit; }

    // The API service account authenticates by api_key and is hidden from the people pickers on
    // the strength of role='api'. The admin dropdown only offers viewer/editor/admin, so it
    // renders that row as "Viewer" — one careless change would break the legacy auth path AND
    // put the account back in every picker. Refuse it, and validate the role either way, since
    // this endpoint previously accepted any string at all.
    if (array_key_exists('role', $fields)) {
        $cur = $pdo->prepare("SELECT role FROM users WHERE id = ?");
        $cur->execute([$input['observer_id']]);
        $curRole = $cur->fetchColumn();
        if ($curRole === 'api' && $fields['role'] !== 'api') {
            http_response_code(409);
            echo json_encode(['error'=>"The API service account's role cannot be changed — it authenticates by API key and is hidden from people pickers on the strength of it."]);
            exit;
        }
        if ($curRole !== 'api' && !in_array($fields['role'], ['viewer', 'editor', 'admin'], true)) {
            http_response_code(400); echo json_encode(['error'=>'Invalid role']); exit;
        }
    }

    // surname is NOT NULL (blank is ''), so it can take part in the (f_name, surname) key.
    // falcon_id and email blank out to NULL — for email that is what lets any number of
    // login-less users coexist under a UNIQUE key that still catches a real duplicate.
    if (array_key_exists('surname', $fields)) $fields['surname'] = trim((string)$fields['surname']);
    // Acronyms are stored uppercase so the list reads consistently; the key is already
    // case-insensitive under this collation, so this is presentation, not enforcement.
    if (array_key_exists('chip_acronym', $fields) && $fields['chip_acronym'] !== null) {
        $fields['chip_acronym'] = mb_strtoupper(trim((string)$fields['chip_acronym']));
    }
    foreach (['falcon_id', 'chip_acronym', 'email'] as $opt) {
        if (array_key_exists($opt, $fields)) {
            $fields[$opt] = trim((string)$fields[$opt]);
            if ($fields[$opt] === '') $fields[$opt] = null;
        }
    }

    // Answer the ways an edit can legitimately fail with a sentence, rather than letting a raw
    // SQL constraint error surface as a 500. Identity is the (f_name, surname) pair, so either
    // half changing has to be checked against the other half as it will be after the write.
    if (array_key_exists('f_name', $fields) || array_key_exists('surname', $fields)) {
        $cur = $pdo->prepare("SELECT f_name, surname FROM users WHERE id = ?");
        $cur->execute([$input['observer_id']]);
        $now = $cur->fetch(PDO::FETCH_ASSOC) ?: ['f_name'=>'', 'surname'=>''];
        $newFirst = array_key_exists('f_name', $fields) ? trim((string)$fields['f_name']) : $now['f_name'];
        $newLast  = array_key_exists('surname', $fields) ? $fields['surname'] : $now['surname'];
        if ($newFirst === '') { http_response_code(400); echo json_encode(['error'=>'Name cannot be empty']); exit; }
        if (array_key_exists('f_name', $fields)) $fields['f_name'] = $newFirst;
        $dup = $pdo->prepare("SELECT id FROM users WHERE f_name = ? AND surname = ? AND id <> ?");
        $dup->execute([$newFirst, $newLast, $input['observer_id']]);
        if ($dup->fetchColumn()) { http_response_code(409); echo json_encode(['error'=>'Another user is already named "' . trim("$newFirst $newLast") . '"']); exit; }
    }
    if (!empty($fields['email'])) {
        $dupe = $pdo->prepare("SELECT f_name, surname FROM users WHERE email = ? AND id <> ?");
        $dupe->execute([$fields['email'], $input['observer_id']]);
        if ($e2 = $dupe->fetch(PDO::FETCH_ASSOC)) {
            http_response_code(409);
            echo json_encode(['error'=>"{$fields['email']} is already the address for " . trim("{$e2['f_name']} {$e2['surname']}")]); exit;
        }
    }
    if (!empty($fields['chip_acronym'])) {
        $dupa = $pdo->prepare("SELECT f_name, surname FROM users WHERE chip_acronym = ? AND id <> ?");
        $dupa->execute([$fields['chip_acronym'], $input['observer_id']]);
        if ($a2 = $dupa->fetch(PDO::FETCH_ASSOC)) {
            http_response_code(409);
            echo json_encode(['error'=>"Chip acronym \"{$fields['chip_acronym']}\" already belongs to " . trim("{$a2['f_name']} {$a2['surname']}")]); exit;
        }
    }

    $pdo->beginTransaction();
    try {
        wwAuditedUpdate($pdo, 'users', $input['observer_id'], $fields, $observer['observer_id']);
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error'=>$e->getMessage()]); exit; }
    echo json_encode(['success'=>true]);
    exit;
}

/**
 * Soft-delete a user: hide them from the admin list and every people picker, without erasing
 * the row that ten tables and audit_log point at. Refused while the account owns data — the
 * response names what and how much, so "why can't I delete this" is answerable from the UI.
 */
if ($action === 'delete_user' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $target = (int)($input['observer_id'] ?? 0);
    if (!$target) { http_response_code(400); echo json_encode(['error'=>'observer_id required']); exit; }
    if ($target === (int)$observer['observer_id']) { http_response_code(400); echo json_encode(['error'=>'You cannot delete your own account']); exit; }

    $who = $pdo->prepare("SELECT f_name, surname, api_key, deleted_at FROM users WHERE id = ?");
    $who->execute([$target]);
    $row = $who->fetch(PDO::FETCH_ASSOC);
    if (!$row) { http_response_code(404); echo json_encode(['error'=>'No such user']); exit; }
    if ($row['deleted_at'] !== null) { echo json_encode(['success'=>true, 'already'=>true]); exit; }
    $name = trim($row['f_name'] . ' ' . $row['surname']);

    // Every place a user can own data. audit_log is deliberately absent: it records what they
    // did, not data they own, and blocking on it would make deletion impossible in practice.
    $blockers = [
        ['penguin_chips',          'chipper_id',          'chipper on %d chip%s'],
        ['penguin_chips',          'assistant_id',        'assistant on %d chip%s'],
        ['observations',           'observer_id',         'recorded %d observation%s'],
        ['observations',           'deleted_by',          'deleted %d observation%s'],
        ['day_notes',              'observer_id',         'observer on %d day%s'],
        ['day_notes',              'scribe_id',         'scribe on %d day%s'],
        ['penguin_scans',          'deleted_by',          'deleted %d scan%s'],
        ['penguin_biometric_data', 'deleted_by',          'deleted %d trait record%s'],
        ['breeding_verifications', 'adults_reviewed_by',  'verified adults on %d clutch%s'],
        ['breeding_verifications', 'chicks_reviewed_by',  'verified chicks on %d clutch%s'],
    ];
    $found = [];
    foreach ($blockers as [$table, $col, $fmt]) {
        $c = $pdo->prepare("SELECT COUNT(*) FROM `$table` WHERE `$col` = ?");
        $c->execute([$target]);
        $n = (int)$c->fetchColumn();
        if ($n > 0) $found[] = sprintf($fmt, $n, $n === 1 ? '' : (str_contains($fmt, 'clutch%s') ? 'es' : 's'));
    }
    if ($row['api_key'] !== null) $found[] = 'holds an API key';

    if ($found) {
        http_response_code(409);
        echo json_encode(['error' => "$name still has data attached — " . implode(', ', $found) . ". Deactivate instead of deleting.",
                          'blockers' => $found]);
        exit;
    }

    $pdo->beginTransaction();
    try {
        // active=0 alongside deleted_at so anything still reading `active` also sees them gone.
        wwAuditedUpdate($pdo, 'users', $target, ['deleted_at' => date('Y-m-d H:i:s'), 'active' => 0],
            $observer['observer_id'], 'User deleted (soft) — no data attached');
        // A hidden account must not keep working from a browser that is already logged in.
        wwSessionsDeleteForObserver($pdo, $target);
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error'=>$e->getMessage()]); exit; }
    echo json_encode(['success'=>true, 'observer_id'=>$target, 'name'=>$name]);
    exit;
}

if ($action === 'create_user') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $name = trim($input['observer_name'] ?? '');
    $surname = trim($input['surname'] ?? '');
    $falconId = trim($input['falcon_id'] ?? '');
    $chipAcronym = mb_strtoupper(trim($input['chip_acronym'] ?? ''));
    $email = trim($input['email'] ?? '');
    $role = $input['role'] ?? 'viewer';
    $password = (string)($input['password'] ?? '');
    // Three ways to create an account:
    //   password           -> they can log in straight away (no email needed at all)
    //   email, no password -> invite: unguessable placeholder hash, they set their own via the link
    //   neither            -> a person with no login yet. Field volunteers often have no email
    //                         address; the row exists so work can be attributed to them, and an
    //                         admin can set a password later. Same unguessable placeholder hash,
    //                         so "no login yet" is not "logs in with a blank password".
    $invite = $password === '' && $email !== '';
    $noLogin = $password === '' && $email === '';
    if ($name === '') { http_response_code(400); echo json_encode(['error'=>'A first name is required']); exit; }
    if ($password !== '' && ($pwProblem = wwPasswordProblem($password, [$name, $surname, $email]))) { http_response_code(400); echo json_encode(['error'=>$pwProblem]); exit; }
    if (!in_array($role, ['viewer', 'editor', 'admin'], true)) { http_response_code(400); echo json_encode(['error'=>'Invalid role']); exit; }
    // Identity is the pair. Surname is stored '' rather than NULL precisely so this key bites
    // for two people sharing a first name and neither having a surname recorded.
    $dup = $pdo->prepare("SELECT id FROM users WHERE f_name = ? AND surname = ?");
    $dup->execute([$name, $surname]);
    if ($dup->fetch()) { http_response_code(409); echo json_encode(['error'=>'A user named "' . trim("$name $surname") . '" already exists']); exit; }
    if ($email !== '') {
        $dupe = $pdo->prepare("SELECT f_name, surname FROM users WHERE email = ?");
        $dupe->execute([$email]);
        if ($e2 = $dupe->fetch(PDO::FETCH_ASSOC)) {
            http_response_code(409);
            echo json_encode(['error'=>"$email is already the address for " . trim("{$e2['f_name']} {$e2['surname']}")]); exit;
        }
    }
    if ($chipAcronym !== '') {
        $dupa = $pdo->prepare("SELECT f_name, surname FROM users WHERE chip_acronym = ?");
        $dupa->execute([$chipAcronym]);
        if ($a2 = $dupa->fetch(PDO::FETCH_ASSOC)) {
            http_response_code(409);
            echo json_encode(['error'=>"Chip acronym \"$chipAcronym\" already belongs to " . trim("{$a2['f_name']} {$a2['surname']}")]); exit;
        }
    }
    $hash = password_hash($password !== '' ? $password : bin2hex(random_bytes(32)), PASSWORD_BCRYPT);
    $pdo->beginTransaction();
    try {
        $id = (int)wwAuditedInsert($pdo, 'users',
            ['f_name' => $name, 'surname' => $surname, 'falcon_id' => $falconId !== '' ? $falconId : null,
             'chip_acronym' => $chipAcronym !== '' ? $chipAcronym : null,
             'email' => $email !== '' ? $email : null, 'passphrase_hash' => $hash, 'role' => $role],
            $observer['observer_id'],
            $invite ? 'Created with email invite' : ($noLogin ? 'Created without a login (no email, no password)' : 'Created with password'));
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error'=>$e->getMessage()]); exit; }
    $emailSent = $invite && sendPasswordSetupEmail($pdo, ['observer_id'=>$id, 'observer_name'=>$name, 'email'=>$email], 'invite');
    echo json_encode(['observer_id'=>$id, 'observer_name'=>$name, 'surname'=>$surname, 'falcon_id'=>$falconId, 'chip_acronym'=>$chipAcronym, 'active'=>1, 'email'=>$email, 'role'=>$role, 'created_at'=>date('Y-m-d H:i:s'), 'invited'=>$invite, 'no_login'=>$noLogin, 'email_sent'=>$emailSent]);
    exit;
}

// Backup inventory: nightly offsite job status (written by offsite-backup.sh) plus
// the local staged dumps it keeps for 14 days on this server.
if ($action === 'backups') {
    $out = ['status' => null, 'local' => []];
    $statusFile = '/var/backups/offsite/status.json';
    if (is_readable($statusFile)) $out['status'] = json_decode((string)file_get_contents($statusFile), true);
    foreach (glob('/var/backups/offsite/db/*.sql.gz') ?: [] as $f) {
        $out['local'][] = ['name' => basename($f), 'bytes' => filesize($f), 'mtime' => gmdate('Y-m-d H:i:s', filemtime($f))];
    }
    usort($out['local'], fn($a, $b) => strcmp($b['name'], $a['name']));

    // Live offsite verification: list devian's backup dir over ssh on every load, so
    // the panel proves the remote copies exist NOW (status.json is only a snapshot
    // from the last run — a dead cron would keep it saying "success" forever). The
    // key is restricted on devian to a forced find-listing command; it can't do
    // anything else.
    $out['remote'] = ['ok' => false, 'error' => '', 'files' => [], 'checked_at' => gmdate('Y-m-d H:i:s')];
    $lines = []; $rc = 1;
    exec('ssh -i /var/www/wildwatch/shared/ssh/id_devian_check -p 43322'
        . ' -o BatchMode=yes -o ConnectTimeout=8'
        . ' -o UserKnownHostsFile=/var/www/wildwatch/shared/ssh/known_hosts -o StrictHostKeyChecking=accept-new'
        . ' mark@baluga.myqnapcloud.com list 2>&1', $lines, $rc);
    if ($rc === 0) {
        foreach ($lines as $ln) {
            $p = explode("\t", trim($ln));
            if (count($p) !== 3) continue;
            $out['remote']['files'][] = [
                'name' => basename($p[0]),
                'kind' => (strpos($p[0], '/monthly/') !== false) ? 'monthly' : 'daily',
                'bytes' => (int)$p[1],
                'mtime' => gmdate('Y-m-d H:i:s', (int)(float)$p[2]),
            ];
        }
        $out['remote']['ok'] = true;
    } else {
        $out['remote']['error'] = 'ssh exit ' . $rc . ': ' . implode(' ', array_slice($lines, 0, 2));
    }
    echo json_encode($out);
    exit;
}

// Email a set-password link to an existing user (invite resend / reset on their behalf)
if ($action === 'send_reset') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $id = (int)($input['observer_id'] ?? 0);
    $chk = $pdo->prepare("SELECT id AS observer_id, f_name AS observer_name, email FROM users WHERE id = ?");
    $chk->execute([$id]);
    $row = $chk->fetch();
    if (!$row) { http_response_code(404); echo json_encode(['error'=>'User not found']); exit; }
    if (empty($row['email'])) { http_response_code(400); echo json_encode(['error'=>'User has no email address']); exit; }
    wwPasswordResetsInvalidate($pdo, $id);
    $ok = sendPasswordSetupEmail($pdo, $row, 'invite'); // 7-day link: admin-sent, so give the recipient time
    if (!$ok) { http_response_code(500); echo json_encode(['error'=>'Mail could not be sent']); exit; }
    echo json_encode(['success'=>true, 'email'=>$row['email']]);
    exit;
}

if ($action === 'reset_password') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $id = (int)($input['observer_id'] ?? 0);
    $password = (string)($input['password'] ?? '');
    if (!$id) { http_response_code(400); echo json_encode(['error'=>'observer_id required']); exit; }
    $chk = $pdo->prepare("SELECT f_name AS observer_name, surname, email FROM users WHERE id = ?");
    $chk->execute([$id]);
    $row = $chk->fetch();
    if (!$row) { http_response_code(404); echo json_encode(['error'=>'User not found']); exit; }
    if ($pwProblem = wwPasswordProblem($password, [$row['observer_name'], $row['surname'] ?? '', $row['email'] ?? ''])) { http_response_code(400); echo json_encode(['error'=>$pwProblem]); exit; }
    // The gateway redacts passphrase_hash — the log records that it was reset, never the value.
    $hash = password_hash($password, PASSWORD_BCRYPT);
    $pdo->beginTransaction();
    try {
        wwAuditedUpdate($pdo, 'users', $id, ['passphrase_hash' => $hash], $observer['observer_id'], 'Password reset by admin');
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error'=>$e->getMessage()]); exit; }
    echo json_encode(['success'=>true, 'observer_name'=>$row['observer_name']]);
    exit;
}

// Preview or delete observations from a specific date
if ($action === 'preview_date' || $action === 'delete_date') {
    $body = json_decode(file_get_contents('php://input'), true) ?? [];
    $date = $body['date'] ?? $_POST['date'] ?? $_GET['date'] ?? '';
    if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) { echo json_encode(['error' => 'date required (YYYY-MM-DD)']); exit; }

    $colonyId = 1;
    // Fixed +12 (NZST) window matching the app's date bucketing — the old NZDT-widened
    // start (11:00) would have deleted an extra hour belonging to the previous NZ day.
    $utcStart = date('Y-m-d 12:00:00', strtotime($date) - 86400);
    $utcEnd = date('Y-m-d 12:00:00', strtotime($date));

    $stmt = $pdo->prepare("SELECT o.observation_id, o.observation_time_utc, o.adults, o.eggs, o.chicks,
        o.breeding_status, o.notes, ol.location_name AS box_name,
        (SELECT COUNT(*) FROM penguin_scans ps WHERE ps.observation_id = o.observation_id AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)) as scan_count
        FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE o.observation_time_utc >= ? AND o.observation_time_utc < ? AND o.is_deleted = FALSE
        ORDER BY ol.location_name + 0");
    $stmt->execute([$utcStart, $utcEnd]);
    $observations = $stmt->fetchAll();

    $totals = ['boxes' => count($observations), 'adults' => 0, 'eggs' => 0, 'chicks' => 0, 'scans' => 0,
        'with_breeding' => 0, 'without_breeding' => 0];
    foreach ($observations as $o) {
        $totals['adults'] += (int)$o['adults'];
        $totals['eggs'] += (int)$o['eggs'];
        $totals['chicks'] += (int)$o['chicks'];
        $totals['scans'] += (int)$o['scan_count'];
        if (!empty($o['breeding_status'])) $totals['with_breeding']++;
        else $totals['without_breeding']++;
    }

    if ($action === 'preview_date') {
        // The day's note, so the preview says what the day was before you delete it.
        $noteStmt = $pdo->prepare("SELECT note FROM day_notes WHERE colony_id = ? AND note_date = ?");
        $noteStmt->execute([$colonyId, $date]);
        $dayNote = $noteStmt->fetchColumn();
        echo json_encode(['date' => $date, 'day_note' => $dayNote === false ? null : $dayNote,
            'totals' => $totals, 'observations' => $observations]);
        exit;
    }

    // Soft delete with audit
    $reason = $body['_reason'] ?? null;

    $pdo->beginTransaction();
    try {
        $obsIds = array_column($observations, 'observation_id');
        $deleted = 0;
        $oid = $observer['observer_id'];
        foreach ($obsIds as $obsId) {
            // Children first, each audited in its own right, then the observation itself — whose
            // audit entry carries the full row rather than a {date, bulk_delete} placeholder.
            wwAuditedDeleteObservationChildren($pdo, $obsId, $oid, $reason);
            wwAuditedDelete($pdo, 'observations', $obsId, $oid, $reason);
            $deleted++;
        }
        $pdo->commit();
        echo json_encode(['success' => true, 'deleted' => $deleted, 'date' => $date]);
    } catch (Exception $e) {
        $pdo->rollBack();
        echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}


if ($action === 'export_nestcheck_zip') {
    $colonyId = $_GET['colony'] ?? 1;

    // Get all observation dates (NZ time)
    $stmt = $pdo->prepare("
        SELECT DISTINCT DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) AS obs_date
        FROM observations o
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE ol.colony_id = ? AND o.is_deleted = FALSE
        ORDER BY obs_date
    ");
    $stmt->execute([$colonyId]);
    $dates = $stmt->fetchAll(PDO::FETCH_COLUMN);

    // Build a MonitorDetails JSON for each date
    $tmpFile = tempnam(sys_get_temp_dir(), 'nestcheck_export_');
    $zip = new ZipArchive();
    $zip->open($tmpFile, ZipArchive::CREATE | ZipArchive::OVERWRITE);

    foreach ($dates as $date) {
        $stmt = $pdo->prepare("
            SELECT ol.location_name AS box_name,
                o.observation_time_utc, o.adults, o.eggs, o.chicks,
                o.breeding_status, o.gate_status, o.notes,
                o.observation_id
            FROM observations o
            JOIN observation_locations ol ON o.location_id = ol.location_id
            WHERE ol.colony_id = ? AND o.is_deleted = FALSE
              AND DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) = ?
            ORDER BY ol.location_name + 0, ol.location_name
        ");
        $stmt->execute([$colonyId, $date]);
        $obs = $stmt->fetchAll();

        $boxData = [];
        foreach ($obs as $row) {
            $scans = $pdo->prepare("SELECT pit_id, scan_time_utc, latitude, longitude, accuracy FROM penguin_scans WHERE observation_id = ? AND (is_deleted = FALSE OR is_deleted IS NULL)");
            $scans->execute([$row['observation_id']]);
            $scanRecords = [];
            foreach ($scans->fetchAll() as $scan) {
                $scanRecords[] = [
                    'BirdId' => $scan['pit_id'],
                    'Timestamp' => $scan['scan_time_utc'] ? date('c', strtotime($scan['scan_time_utc'])) : date('c'),
                    'Latitude' => (float)($scan['latitude'] ?? 0),
                    'Longitude' => (float)($scan['longitude'] ?? 0),
                    'Accuracy' => (float)($scan['accuracy'] ?? 0),
                ];
            }
            $boxData[$row['box_name']] = [
                'ScannedIds' => $scanRecords,
                'Adults' => (int)$row['adults'],
                'Eggs' => (int)$row['eggs'],
                'Chicks' => (int)$row['chicks'],
                'GateStatus' => $row['gate_status'],
                'Notes' => $row['notes'] ?? '',
                'whenDataCollectedUtc' => date('c', strtotime($row['observation_time_utc'])),
                'BreedingChance' => $row['breeding_status'],
            ];
        }

        $nzDate = date('d M Y', strtotime($date));
        $monitor = [
            'LastSaved' => date('c', strtotime($date . ' 23:59:59')),
            'filename' => "PenguinMonitor $nzDate",
            'BoxData' => (object)$boxData,
        ];

        $json = json_encode($monitor, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE);
        $zip->addFromString("Nestcheck $date.json", $json);
    }

    $zip->close();

    header('Content-Type: application/zip');
    header('Content-Disposition: attachment; filename="nestcheck-export-' . date('Y-m-d') . '.zip"');
    header('Content-Length: ' . filesize($tmpFile));
    header_remove('Access-Control-Allow-Origin');
    readfile($tmpFile);
    unlink($tmpFile);
    exit;
}

// --- Remove Penguin ---

if ($action === 'preview_penguin_delete') {
    $pengNum = $_GET['peng_num'] ?? '';
    if (!$pengNum) { http_response_code(400); echo json_encode(['error' => 'peng_num required']); exit; }

    // Same resolution as bird.php: a prefixed input matches as-is ("NI7"), a bare number
    // resolves within the viewing colony ("319" → "PT319"). Exact matches only — a
    // wrong-colony guess on a delete screen would be far worse than not-found.
    $viewPrefix = getColonyPrefix($pdo, (int)($_GET['colony_id'] ?? 1));
    $peng = $pdo->prepare("SELECT * FROM penguins WHERE peng_num = ? OR peng_num = ? LIMIT 1");
    $peng->execute([$pengNum, $viewPrefix . $pengNum]);
    $penguin = $peng->fetch();
    if (!$penguin) { http_response_code(404); echo json_encode(['error' => 'Penguin not found']); exit; }
    $pid = $penguin['peng_num'];  // full prefixed PK — the delete action must be sent this

    $chips = $pdo->prepare("SELECT * FROM penguin_chips WHERE peng_num = ?");
    $chips->execute([$pid]);
    $chipList = $chips->fetchAll();

    $pitIds = array_column($chipList, 'pit_id');
    $scans = [];
    $scansSoftDeleted = 0;
    if (!empty($pitIds)) {
        // All scans, including already-soft-deleted ones: the delete hard-purges those too,
        // so the preview must count exactly what the delete will touch.
        $ph = implode(',', array_fill(0, count($pitIds), '?'));
        $scanStmt = $pdo->prepare("SELECT ps.*, o.observation_time_utc, ol.location_name AS box_name
            FROM penguin_scans ps
            JOIN observations o ON ps.observation_id = o.observation_id
            JOIN observation_locations ol ON o.location_id = ol.location_id
            WHERE ps.pit_id IN ($ph)
            ORDER BY o.observation_time_utc DESC");
        $scanStmt->execute($pitIds);
        $allScans = $scanStmt->fetchAll();
        $scans = array_values(array_filter($allScans, fn($s) => empty($s['is_deleted'])));
        $scansSoftDeleted = count($allScans) - count($scans);
    }

    $bio = $pdo->prepare("SELECT * FROM penguin_biometric_data WHERE peng_num = ?");
    $bio->execute([$pid]);
    $allBio = $bio->fetchAll();
    $bioData = array_values(array_filter($allBio, fn($b) => empty($b['is_deleted'])));

    echo json_encode([
        'penguin' => $penguin,
        'chips' => $chipList,
        'scans' => $scans,
        'biometrics' => $bioData,
        'scan_count' => count($scans),
        'scans_soft_deleted' => $scansSoftDeleted,
        'bio_soft_deleted' => count($allBio) - count($bioData),
    ]);
    exit;
}

/**
 * The renumber plan that closes the gap left at $gapNumber in colony $colonyId: the unbroken run
 * of penguins immediately above the gap that were FIRST chipped within the last 7 days, each
 * shifted down by one. Stops at the first number that is missing or not a recent-chip candidate —
 * an established or unchipped bird must never be renumbered. Returns [] when nothing shifts.
 */
function computeCompactionPlan(PDO $pdo, int $colonyId, int $gapNumber): array {
    $prefix = getColonyPrefix($pdo, $colonyId);
    $cutoff = date('Y-m-d', strtotime('-7 days'));
    $info = $pdo->prepare(
        "SELECT (SELECT MIN(chip_date) FROM penguin_chips WHERE peng_num = p.peng_num) AS first_chip,
            (SELECT COUNT(*) FROM penguin_chips WHERE peng_num = p.peng_num) AS chips,
            (SELECT COUNT(*) FROM penguin_biometric_data WHERE peng_num = p.peng_num AND (is_deleted = 0 OR is_deleted IS NULL)) AS biometrics,
            (SELECT COUNT(*) FROM penguin_scans ps JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
               WHERE pc.peng_num = p.peng_num AND (ps.is_deleted = 0 OR ps.is_deleted IS NULL)) AS scans
         FROM penguins p WHERE p.colony_id = ? AND p.peng_num = ?");
    $plan = [];
    for ($k = $gapNumber + 1; ; $k++) {
        $pn = $prefix . $k;
        $info->execute([$colonyId, $pn]);
        $r = $info->fetch(PDO::FETCH_ASSOC);
        if (!$r) break;                                                     // no penguin at this number
        if ($r['first_chip'] === null || $r['first_chip'] < $cutoff) break; // not a ≤7-day candidate
        $plan[] = [
            'from' => $pn, 'to' => $prefix . ($k - 1),
            'chips' => (int)$r['chips'], 'scans' => (int)$r['scans'], 'biometrics' => (int)$r['biometrics'],
            'first_chip' => $r['first_chip'],
        ];
    }
    return $plan;
}

/** Numeric suffix of a peng_num ("PT1009" -> 1009). */
function pengNumSuffix(string $pengNum): int {
    return (int)preg_replace('/^\D+/', '', $pengNum);
}

if ($action === 'compact_numbering') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $colonyId = (int)($input['colony_id'] ?? 0);
    $gap = (int)($input['gap'] ?? 0);
    if (!$colonyId || !$gap) { http_response_code(400); echo json_encode(['error' => 'colony_id and gap required']); exit; }
    $prefix = getColonyPrefix($pdo, $colonyId);

    $pdo->beginTransaction();
    try {
        // Recompute server-side — a client-supplied plan is never trusted for a primary-key rename.
        $plan = computeCompactionPlan($pdo, $colonyId, $gap);
        if (empty($plan)) { $pdo->commit(); echo json_encode(['success' => true, 'renumbered' => 0, 'applied' => []]); exit; }

        // The gap slot must be vacant, or there is nothing to compact into (e.g. it was re-chipped).
        $occ = $pdo->prepare("SELECT 1 FROM penguins WHERE peng_num = ?");
        $occ->execute([$prefix . $gap]);
        if ($occ->fetchColumn()) { $pdo->rollBack(); http_response_code(409); echo json_encode(['error' => "$prefix$gap is occupied — nothing to compact into"]); exit; }

        $reason = "Compaction: close gap at $prefix$gap";
        $applied = [];
        foreach ($plan as $step) {   // ascending order — each target was vacated by the previous step
            $moved = wwAuditedRenumberPenguin($pdo, $step['from'], $step['to'], $observer['observer_id'], $reason);
            $applied[] = ['from' => $step['from'], 'to' => $step['to']] + $moved;
        }
        $pdo->commit();
        echo json_encode(['success' => true, 'renumbered' => count($applied), 'applied' => $applied]);
    } catch (Exception $e) {
        $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}

if ($action === 'delete_penguin') {
    $input = json_decode(file_get_contents('php://input'), true);
    $pengNum = $input['peng_num'] ?? '';
    if (!$pengNum) { http_response_code(400); echo json_encode(['error' => 'peng_num required']); exit; }

    // Capture colony + number before deletion, so the response can offer to close the gap.
    $meta = $pdo->prepare("SELECT colony_id FROM penguins WHERE peng_num = ?");
    $meta->execute([$pengNum]);
    $delColonyId = (int)($meta->fetchColumn() ?: 0);
    $delNumber = pengNumSuffix($pengNum);

    $pdo->beginTransaction();
    try {
        $oid = $observer['observer_id'];

        $reason = $input['reason'] ?? 'Admin delete';

        // Get pit_ids for this penguin
        $chips = $pdo->prepare("SELECT pit_id FROM penguin_chips WHERE peng_num = ?");
        $chips->execute([$pengNum]);
        $pitIds = array_column($chips->fetchAll(), 'pit_id');

        // Every row is hard-deleted (as before) but audited one by one, each entry carrying the
        // whole row — so a penguin removed in error is reconstructable from the audit log alone.
        $scansDeleted = 0;
        if (!empty($pitIds)) {
            $ph = implode(',', array_fill(0, count($pitIds), '?'));
            $scanStmt = $pdo->prepare("SELECT scan_id FROM penguin_scans WHERE pit_id IN ($ph)");
            $scanStmt->execute($pitIds);
            foreach ($scanStmt->fetchAll(PDO::FETCH_COLUMN) as $scanId) {
                wwAuditedDelete($pdo, 'penguin_scans', $scanId, $oid, $reason, true);
                $scansDeleted++;
            }
        }

        $bioStmt = $pdo->prepare("SELECT biometric_id FROM penguin_biometric_data WHERE peng_num = ?");
        $bioStmt->execute([$pengNum]);
        foreach ($bioStmt->fetchAll(PDO::FETCH_COLUMN) as $bioId) wwAuditedDelete($pdo, 'penguin_biometric_data', $bioId, $oid, $reason, true);

        foreach ($pitIds as $pitId) wwAuditedDelete($pdo, 'penguin_chips', $pitId, $oid, $reason);

        wwAuditedDelete($pdo, 'penguins', $pengNum, $oid, $reason);

        $pdo->commit();

        // The deletion may have left a fillable gap. Offer the renumber plan so the admin can
        // decide — nothing is renumbered here; that needs a separate confirmed compact_numbering.
        $compaction = null;
        if ($delColonyId && $delNumber) {
            $plan = computeCompactionPlan($pdo, $delColonyId, $delNumber);
            if (!empty($plan)) {
                $prefix = getColonyPrefix($pdo, $delColonyId);
                $compaction = ['colony_id' => $delColonyId, 'gap' => $delNumber, 'gap_peng' => $prefix . $delNumber, 'plan' => $plan];
            }
        }
        echo json_encode(['success' => true, 'scans_deleted' => $scansDeleted, 'chips_deleted' => count($pitIds), 'compaction' => $compaction]);
    } catch (Exception $e) {
        $pdo->rollBack();
        http_response_code(500);
        echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}

// --- Renumber / swap penguins ---

/** Resolve admin input to a penguin row, same rules as preview_penguin_delete: a prefixed input
 *  matches as-is ("NI7"), a bare number resolves within the viewing colony ("319" -> "PT319").
 *  Exact matches only. Returns the row or null. */
function resolvePenguin(PDO $pdo, string $raw, int $viewColonyId): ?array {
    $viewPrefix = getColonyPrefix($pdo, $viewColonyId);
    $sel = $pdo->prepare("SELECT * FROM penguins WHERE peng_num = ? OR peng_num = ? LIMIT 1");
    $sel->execute([$raw, $viewPrefix . $raw]);
    return $sel->fetch(PDO::FETCH_ASSOC) ?: null;
}

if ($action === 'rename_penguin') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $fromRaw = trim($input['from'] ?? '');
    $toRaw = strtoupper(trim($input['to'] ?? ''));
    if ($fromRaw === '' || $toRaw === '') { http_response_code(400); echo json_encode(['error' => 'from and to required']); exit; }

    $penguin = resolvePenguin($pdo, $fromRaw, (int)($input['colony_id'] ?? 1));
    if (!$penguin) { http_response_code(404); echo json_encode(['error' => "Penguin $fromRaw not found"]); exit; }
    $from = $penguin['peng_num'];

    // The new number stays in the bird's own colony: a bare number gets that colony's prefix, a
    // prefixed one must match it — renaming PT319 to NI5 would contradict the row's colony_id.
    $prefix = getColonyPrefix($pdo, (int)$penguin['colony_id']);
    $to = preg_match('/^[0-9]+$/', $toRaw) ? $prefix . $toRaw : $toRaw;
    if (!preg_match('/^' . preg_quote($prefix, '/') . '[0-9]+$/', $to)) {
        http_response_code(400); echo json_encode(['error' => "New number must be $prefix + digits (this bird is in the $prefix colony)"]); exit;
    }
    if ($to === $from) { http_response_code(400); echo json_encode(['error' => "$from already has that number"]); exit; }

    $pdo->beginTransaction();
    try {
        $reason = trim($input['reason'] ?? '') ?: "Admin rename $from -> $to";
        $moved = wwAuditedRenumberPenguin($pdo, $from, $to, $observer['observer_id'], $reason);
        $pdo->commit();
        echo json_encode(['success' => true, 'from' => $from, 'to' => $to] + $moved);
    } catch (Exception $e) {
        $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}

if ($action === 'swap_penguins') {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $aRaw = trim($input['a'] ?? '');
    $bRaw = trim($input['b'] ?? '');
    if ($aRaw === '' || $bRaw === '') { http_response_code(400); echo json_encode(['error' => 'a and b required']); exit; }

    $colonyId = (int)($input['colony_id'] ?? 1);
    $pa = resolvePenguin($pdo, $aRaw, $colonyId);
    if (!$pa) { http_response_code(404); echo json_encode(['error' => "Penguin $aRaw not found"]); exit; }
    $pb = resolvePenguin($pdo, $bRaw, $colonyId);
    if (!$pb) { http_response_code(404); echo json_encode(['error' => "Penguin $bRaw not found"]); exit; }
    if ($pa['colony_id'] !== $pb['colony_id']) {
        // Swapping numbers across colonies would put each bird's prefix at odds with its colony_id.
        http_response_code(400); echo json_encode(['error' => "{$pa['peng_num']} and {$pb['peng_num']} are in different colonies"]); exit;
    }

    $pdo->beginTransaction();
    try {
        $reason = trim($input['reason'] ?? '') ?: "Admin swap {$pa['peng_num']} <-> {$pb['peng_num']}";
        $moved = wwAuditedSwapPenguins($pdo, $pa['peng_num'], $pb['peng_num'], $observer['observer_id'], $reason);
        $pdo->commit();
        echo json_encode(['success' => true, 'a' => $pa['peng_num'], 'b' => $pb['peng_num'], 'moved' => $moved]);
    } catch (Exception $e) {
        $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]);
    }
    exit;
}

// --- Regions & Colonies management ---

if ($action === 'regions') {
    $stmt = $pdo->query("SELECT r.*, (SELECT COUNT(*) FROM colonies c WHERE c.region_id = r.region_id) as colony_count FROM regions r ORDER BY r.region_name");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'save_region') {
    $input = json_decode(file_get_contents('php://input'), true);
    $name = trim($input['region_name'] ?? '');
    if (!$name) { http_response_code(400); echo json_encode(['error' => 'region_name required']); exit; }
    $id = $input['region_id'] ?? null;
    if ($id) {
        wwAuditedUpdate($pdo, 'regions', $id, ['region_name' => $name], $observer['observer_id']);
    } else {
        $id = wwAuditedInsert($pdo, 'regions', ['region_name' => $name], $observer['observer_id']);
    }
    echo json_encode(['success' => true, 'region_id' => (int)$id]);
    exit;
}

if ($action === 'colonies') {
    $stmt = $pdo->query("SELECT c.*, r.region_name FROM colonies c JOIN regions r ON c.region_id = r.region_id ORDER BY r.region_name, c.colony_name");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'save_colony') {
    $input = json_decode(file_get_contents('php://input'), true);
    $name = trim($input['colony_name'] ?? '');
    $regionId = (int)($input['region_id'] ?? 0);
    $locationSets = trim($input['location_sets_string'] ?? '');
    // Locations excluded from Full Monitor detection (comma-separated). Only overwrite
    // when the key is present so callers that don't send it keep the existing value.
    $hasFmExcluded = is_array($input) && array_key_exists('fm_excluded_boxes', $input);
    $fmExcluded = trim($input['fm_excluded_boxes'] ?? '');
    if (!$name || !$regionId) { http_response_code(400); echo json_encode(['error' => 'colony_name and region_id required']); exit; }
    // colony_prefix numbers the colony's birds (e.g. "RH" => RH1). Only overwrite when the key is
    // present. The rest of the code assumes 2–4 letters, so enforce that (or blank).
    $hasPrefix = is_array($input) && array_key_exists('colony_prefix', $input);
    $prefix = strtoupper(preg_replace('/[^A-Za-z]/', '', (string)($input['colony_prefix'] ?? '')));
    if ($hasPrefix && $prefix !== '' && (strlen($prefix) < 2 || strlen($prefix) > 4)) {
        http_response_code(400); echo json_encode(['error' => 'Prefix must be 2–4 letters, or blank']); exit;
    }
    $id = $input['colony_id'] ?? null;
    // A prefix must be unique: peng_num is a global key, so two colonies sharing "RH" would collide.
    if ($hasPrefix && $prefix !== '') {
        $dup = $pdo->prepare("SELECT colony_name FROM colonies WHERE colony_prefix = ? AND colony_id <> ?");
        $dup->execute([$prefix, (int)($id ?? 0)]);
        if ($other = $dup->fetchColumn()) {
            http_response_code(409); echo json_encode(['error' => "Prefix $prefix is already used by $other"]); exit;
        }
    }
    if ($id) {
        // Changing a prefix once the colony has birds would orphan their numbers — block it.
        if ($hasPrefix) {
            $cur = $pdo->prepare("SELECT colony_prefix FROM colonies WHERE colony_id = ?");
            $cur->execute([$id]);
            if ($prefix !== (string)($cur->fetchColumn() ?: '')) {
                $pc = $pdo->prepare("SELECT COUNT(*) FROM penguins WHERE colony_id = ?");
                $pc->execute([$id]);
                if ((int)$pc->fetchColumn() > 0) {
                    http_response_code(409);
                    echo json_encode(['error' => "Can't change the prefix of a colony that already has birds — their numbers would no longer match."]);
                    exit;
                }
            }
        }
        $fields = ['colony_name' => $name, 'region_id' => $regionId, 'location_sets_string' => $locationSets];
        if ($hasFmExcluded) $fields['fm_excluded_boxes'] = $fmExcluded;
        if ($hasPrefix) $fields['colony_prefix'] = $prefix;
        wwAuditedUpdate($pdo, 'colonies', $id, $fields, $observer['observer_id']);
    } else {
        $id = wwAuditedInsert($pdo, 'colonies', ['colony_name' => $name, 'region_id' => $regionId,
            'location_sets_string' => $locationSets, 'colony_prefix' => $prefix,
            'fm_excluded_boxes' => $hasFmExcluded ? $fmExcluded : '0,AA,AB,AC'], $observer['observer_id']);
    }
    echo json_encode(['success' => true, 'colony_id' => (int)$id]);
    exit;
}

if ($action === 'colony_box_names') {
    $colonyId = (int)($_GET['colony_id'] ?? 0);
    if (!$colonyId) { echo json_encode([]); exit; }
    $stmt = $pdo->prepare("SELECT location_name FROM observation_locations WHERE colony_id = ?");
    $stmt->execute([$colonyId]);
    echo json_encode($stmt->fetchAll(PDO::FETCH_COLUMN));
    exit;
}

if ($action === 'create_colony_boxes') {
    // Materialise a colony's box-sets into observation_locations rows.
    $input = json_decode(file_get_contents('php://input'), true);
    $colonyId = (int)($input['colony_id'] ?? 0);
    $names = $input['box_names'] ?? [];
    if (!$colonyId || !is_array($names) || empty($names)) { http_response_code(400); echo json_encode(['error' => 'colony_id and box_names required']); exit; }
    // Idempotent: skip names this colony already has, audit the ones we actually create.
    $exists = $pdo->prepare("SELECT location_id FROM observation_locations WHERE colony_id = ? AND location_name = ?");
    $created = 0;
    $pdo->beginTransaction();
    try {
        foreach ($names as $name) {
            $name = trim((string)$name);
            if ($name === '') continue;
            $exists->execute([$colonyId, $name]);
            if ($exists->fetchColumn() !== false) continue;
            wwAuditedInsert($pdo, 'observation_locations',
                ['colony_id' => $colonyId, 'location_name' => $name, 'location_type' => 'box'],
                $observer['observer_id'], 'Colony box-set materialisation');
            $created++;
        }
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]); exit; }
    echo json_encode(['success' => true, 'created' => $created, 'requested' => count($names)]);
    exit;
}

if ($action === 'colony_permissions') {
    $stmt = $pdo->query("SELECT cp.permission_id, cp.colony_id, cp.observer_id, cp.role,
            c.colony_name, o.f_name AS observer_name
        FROM colony_permissions cp
        JOIN colonies c ON cp.colony_id = c.colony_id
        JOIN users o ON cp.observer_id = o.id
        ORDER BY c.colony_name, o.f_name");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'save_colony_permission') {
    // Grant/change/revoke a user's access to a colony. role 'view' | 'edit', or '' to revoke.
    $input = json_decode(file_get_contents('php://input'), true);
    $colonyId = (int)($input['colony_id'] ?? 0);
    $observerId = (int)($input['observer_id'] ?? 0);
    $role = trim($input['role'] ?? '');
    if (!$colonyId || !$observerId) { http_response_code(400); echo json_encode(['error' => 'colony_id and observer_id required']); exit; }

    $pdo->beginTransaction();
    try {
        if ($role === '' || $role === 'none') {
            $sel = $pdo->prepare("SELECT permission_id FROM colony_permissions WHERE colony_id = ? AND observer_id = ?");
            $sel->execute([$colonyId, $observerId]);
            $permId = $sel->fetchColumn();
            if ($permId !== false) wwAuditedDelete($pdo, 'colony_permissions', $permId, $observer['observer_id'], 'Access revoked');
        } else {
            if (!in_array($role, ['view', 'edit'], true)) { $pdo->rollBack(); http_response_code(400); echo json_encode(['error' => "role must be 'view', 'edit', or empty to revoke"]); exit; }
            wwAuditedUpsert($pdo, 'colony_permissions', ['colony_id', 'observer_id'],
                ['colony_id' => $colonyId, 'observer_id' => $observerId, 'role' => $role], $observer['observer_id']);
        }
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error' => $e->getMessage()]); exit; }
    echo json_encode(['success' => true]);
    exit;
}

// ============ Monitor CSV import ============
// Two-phase: analyze (no writes) then commit. Both call ww_parseImportCsv so the
// analysis page reflects exactly what the import will write. Row shape from the
// monitor sheet: Date, Box, Adults, Eggs, Chicks, Bird-1, Sex-1, .. Bird-N, Sex-N, Notes.
//   - "Decom" in the Adults cell => observation with breeding_status DCM, zero counts.
//   - Bird cells hold chip/PIT numbers; matched last-8 against penguin_chips.pit_id.
//     A bird cell reading "no scan" is an unscanned adult (counts toward no_scan).
//   - Each Sex-N pairs with Bird-N: a chick size code (BC/LC/SC, validated against a
//     chick chipped in this box on this date) and/or an observed sex (M/UM/UF/F -> a
//     penguin_biometric_data sighting). penguins.sex is never modified.
//   - Adults exceeding entered birds implies no-scans, confirmed by the user at commit.
//   - Unmatched chips are reported, never auto-created; their scan is skipped.

function ww_normHeader($h) { return preg_replace('/[^a-z0-9]/', '', strtolower(trim((string)$h))); }

// Last-8 uppercased alphanumerics of a chip/tag number — same key importMonitor() uses.
function ww_chipKey($raw) { return strtoupper(substr(preg_replace('/[^A-Za-z0-9]/', '', (string)$raw), -8)); }

// Digits only — for near-match on hand-typed chip numbers.
function ww_digits($s) { return preg_replace('/\D+/', '', (string)$s); }

// All digit strings one edit away (substitution / adjacent transposition / insertion / deletion).
// Used to suggest the intended bird when a typed chip matches nothing — the classic transcription slip.
function ww_editNeighbors($d) {
    $out = []; $n = strlen($d);
    for ($i = 0; $i < $n; $i++) {
        $out[substr($d, 0, $i) . substr($d, $i + 1)] = true;                        // deletion
        for ($c = 0; $c <= 9; $c++) if ($d[$i] !== (string)$c)
            $out[substr($d, 0, $i) . $c . substr($d, $i + 1)] = true;               // substitution
        if ($i + 1 < $n && $d[$i] !== $d[$i + 1])
            $out[substr($d, 0, $i) . $d[$i + 1] . $d[$i] . substr($d, $i + 2)] = true; // transposition
    }
    for ($i = 0; $i <= $n; $i++) for ($c = 0; $c <= 9; $c++)
        $out[substr($d, 0, $i) . $c . substr($d, $i)] = true;                       // insertion
    return array_keys($out);
}

/**
 * Parse + validate a monitor CSV against a colony. Pure analysis: NO DB writes.
 * Returns the structure the analysis page renders and the commit step replays.
 */
function ww_parseImportCsv($pdo, $csv, $colonyId, $observerId, $filename) {
    $R = [
        'ok' => true, 'error' => null,
        'colony_id' => $colonyId, 'colony_name' => null, 'filename' => $filename,
        'headers' => null, 'rows' => [],
        'unmatched_chips' => [], 'unknown_boxes' => [],
        'date_min' => null, 'date_max' => null,
        'totals' => [
            'rows' => 0, 'importable' => 0, 'flagged' => 0, 'duplicates' => 0, 'conflicts' => 0, 'error_rows' => 0,
            'decom' => 0, 'boxes' => 0, 'boxes_in_colony' => 0, 'boxes_missing' => 0,
            'adults' => 0, 'eggs' => 0, 'chicks' => 0,
            'no_scan' => 0, 'scans_matched' => 0, 'scans_unmatched' => 0,
            'biometrics' => 0, 'noscan_confirm' => 0,
        ],
    ];

    $cstmt = $pdo->prepare("SELECT colony_name FROM colonies WHERE colony_id = ?");
    $cstmt->execute([$colonyId]);
    $colonyName = $cstmt->fetchColumn();
    if ($colonyName === false) { $R['ok'] = false; $R['error'] = "Colony $colonyId not found"; return $R; }
    $R['colony_name'] = $colonyName;

    // Parse through a temp stream so quoted fields / embedded newlines are handled.
    $fh = fopen('php://temp', 'r+');
    fwrite($fh, $csv);
    rewind($fh);
    $records = [];
    while (($row = fgetcsv($fh)) !== false) $records[] = $row;
    fclose($fh);
    if (count($records) < 2) { $R['ok'] = false; $R['error'] = 'CSV has no data rows'; return $R; }

    $header = array_shift($records);
    $headerCount = count($header);
    // Column layout: Date, Box, Adults, Eggs, Chicks, Bird-1, Sex-1, Bird-2, Sex-2, ..., Notes.
    // Each Bird-N carries a chip / "no scan" / (empty); its paired Sex-N holds either a chick
    // size code (BC/LC/SC[+sex]) or an observed sex (M/UM/UF/F). No standalone "No scan" column
    // any more — an unscanned adult is a bird cell reading "no scan".
    $idx = []; $birdCols = []; $ignoredCols = [];
    $birdColByNum = []; $sexColByNum = [];
    foreach ($header as $i => $h) {
        $n = ww_normHeader($h);
        if ($n === 'date') $idx['date'] = $i;
        elseif ($n === 'box') $idx['box'] = $i;
        elseif ($n === 'adults' || $n === 'adult') $idx['adults'] = $i;
        elseif ($n === 'eggs' || $n === 'egg') $idx['eggs'] = $i;
        elseif ($n === 'chicks' || $n === 'chick') $idx['chicks'] = $i;
        elseif ($n === 'notes' || $n === 'note') $idx['notes'] = $i;
        elseif (preg_match('/^sex(\d+)$/', $n, $m)) $sexColByNum[(int)$m[1]] = $i;
        elseif (preg_match('/^bird(\d+)$/', $n, $m)) { $birdColByNum[(int)$m[1]] = $i; $birdCols[] = $i; }
        elseif (strpos($n, 'bird') === 0) $birdCols[] = $i; // unnumbered bird column
        elseif ($n !== '') $ignoredCols[] = trim((string)$h);
    }
    // Pair each numbered bird column with its like-numbered sex column: birdColIndex => sexColIndex.
    $sexColByBird = [];
    foreach ($birdColByNum as $num => $bi) if (isset($sexColByNum[$num])) $sexColByBird[$bi] = $sexColByNum[$num];
    $missing = [];
    foreach (['date', 'box', 'adults'] as $req) if (!isset($idx[$req])) $missing[] = $req;
    if ($missing) { $R['ok'] = false; $R['error'] = 'Missing required column(s): ' . implode(', ', $missing); return $R; }
    $R['headers'] = [
        'date' => $header[$idx['date']], 'box' => $header[$idx['box']], 'adults' => $header[$idx['adults']],
        'eggs' => isset($idx['eggs']) ? $header[$idx['eggs']] : null,
        'chicks' => isset($idx['chicks']) ? $header[$idx['chicks']] : null,
        'no_scan' => isset($idx['no_scan']) ? $header[$idx['no_scan']] : null,
        'notes' => isset($idx['notes']) ? $header[$idx['notes']] : null,
        'bird_columns' => array_values(array_map(function ($i) use ($header) { return $header[$i]; }, $birdCols)),
    ];

    // Lookups: this colony's locations, and every known chip.
    $locLookup = [];
    $lstmt = $pdo->prepare("SELECT location_id, location_name FROM observation_locations WHERE colony_id = ?");
    $lstmt->execute([$colonyId]);
    foreach ($lstmt->fetchAll() as $l) $locLookup[strtoupper($l['location_name'])] = (int)$l['location_id'];

    // Chip resolution scoped to THIS colony. Key = last-8 of pit_id; a CSV bird number is the
    // same 8-digit subset. A cell resolves only if the subset maps to exactly one bird (peng_num)
    // whose home colony is this one. Zero / many / another-colony all flag and skip the scan.
    $byKeyColony = [];   // key => ['pengs'=>[peng=>1], 'pit'=>first pit, 'active'=>active pit]
    $byKeyAny = [];      // key => [peng=>colony_id]  (all colonies — to spot foreign birds)
    $pengMeta = [];      // peng_num => ['sex'=>M/F/'', 'dead'=>bool]  (this colony)
    $pengFirstChip = []; // peng_num => earliest chip_date (YYYY-MM-DD)
    $pengActiveKey = []; // peng_num => last-8 of its active chip (to spot retired-tag scans)
    $pengChickSize = []; // peng_num => recorded chick_size_code (BC/LC/SC or '')
    $pengChips = [];     // peng_num => [['date'=>YYYY-MM-DD, 'box'=>UPPER], ...]  (chipping events)
    $pengAdult = [];     // peng_num => chipped_as_adult (bool)
    foreach ($pdo->query("SELECT pc.pit_id, pc.peng_num, pc.is_active, pc.chip_date, pc.chip_box, p.colony_id, p.sex, p.is_dead, p.chick_size_code, p.chipped_as_adult
        FROM penguin_chips pc JOIN penguins p ON pc.peng_num = p.peng_num")->fetchAll() as $c) {
        $k = ww_chipKey($c['pit_id']);
        $byKeyAny[$k][$c['peng_num']] = (int)$c['colony_id'];
        if ((int)$c['colony_id'] !== $colonyId) continue;
        if (!isset($byKeyColony[$k])) $byKeyColony[$k] = ['pengs' => [], 'pit' => null, 'active' => null];
        $byKeyColony[$k]['pengs'][$c['peng_num']] = true;
        if ($byKeyColony[$k]['pit'] === null) $byKeyColony[$k]['pit'] = $c['pit_id'];
        if ($c['is_active']) { if ($byKeyColony[$k]['active'] === null) $byKeyColony[$k]['active'] = $c['pit_id']; $pengActiveKey[$c['peng_num']] = $k; }
        $pengMeta[$c['peng_num']] = ['sex' => strtoupper((string)($c['sex'] ?? '')), 'dead' => !empty($c['is_dead'])];
        $pengChickSize[$c['peng_num']] = strtoupper((string)($c['chick_size_code'] ?? ''));
        $pengAdult[$c['peng_num']] = !empty($c['chipped_as_adult']);
        if (!empty($c['chip_date'])) {
            $pengChips[$c['peng_num']][] = ['date' => substr($c['chip_date'], 0, 10), 'box' => strtoupper(trim((string)($c['chip_box'] ?? '')))];
            if (!isset($pengFirstChip[$c['peng_num']]) || $c['chip_date'] < $pengFirstChip[$c['peng_num']])
                $pengFirstChip[$c['peng_num']] = substr($c['chip_date'], 0, 10);
        }
    }
    $prefix = getColonyPrefix($pdo, $colonyId);

    // Biometric sex guesses so a same-sex-pair check can use suspected sex too.
    // observed_sex codes: PM/MM/M => male, PF/MF/F => female, U ignored.
    $pengGuess = [];     // peng_num => ['m'=>n, 'f'=>n]
    foreach ($pdo->query("SELECT b.peng_num, b.observed_sex FROM penguin_biometric_data b
        JOIN penguins p ON b.peng_num = p.peng_num
        WHERE p.colony_id = " . (int)$colonyId . " AND (b.is_deleted = 0 OR b.is_deleted IS NULL)")->fetchAll() as $b) {
        $s = strtoupper((string)($b['observed_sex'] ?? ''));
        if ($s === 'PM' || $s === 'MM' || $s === 'M') $pengGuess[$b['peng_num']]['m'] = ($pengGuess[$b['peng_num']]['m'] ?? 0) + 1;
        elseif ($s === 'PF' || $s === 'MF' || $s === 'F') $pengGuess[$b['peng_num']]['f'] = ($pengGuess[$b['peng_num']]['f'] ?? 0) + 1;
    }
    // Effective sex per bird: confirmed if known, else the biometric lean (flagged unconfirmed).
    $effSex = function ($peng) use ($pengMeta, $pengGuess) {
        $s = $pengMeta[$peng]['sex'] ?? '';
        if ($s === 'M' || $s === 'F') return [$s, true];
        $g = $pengGuess[$peng] ?? [];
        $m = $g['m'] ?? 0; $f = $g['f'] ?? 0;
        if ($m > $f) return ['M', false];
        if ($f > $m) return ['F', false];
        return ['', false];
    };

    // First & last date each bird was ever seen in each box, to flag a bird whose ONLY sighting
    // in a box is this date — nothing before or after. Keyed location_id|peng_num => [min,max].
    $seenInBox = [];
    foreach ($pdo->query("SELECT o.location_id, o.observation_time_utc, pc.peng_num
        FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
        WHERE ol.colony_id = " . (int)$colonyId . " AND o.is_deleted = FALSE AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)")->fetchAll() as $h) {
        $nz = substr($h['observation_time_utc'], 0, 10); // day-level is enough for a before/after test
        $key = $h['location_id'] . '|' . $h['peng_num'];
        if (!isset($seenInBox[$key])) $seenInBox[$key] = ['min' => $nz, 'max' => $nz];
        else { if ($nz < $seenInBox[$key]['min']) $seenInBox[$key]['min'] = $nz; if ($nz > $seenInBox[$key]['max']) $seenInBox[$key]['max'] = $nz; }
    }

    // Existing data on the same NZ day for a box (ANY observer, any time that day) —
    // used to auto-skip matching rows and to block conflicting ones with a field diff.
    $dayObsStmt = $pdo->prepare("SELECT observation_id, adults, eggs, chicks, no_scan, breeding_status
        FROM observations WHERE location_id = ? AND observation_time_utc >= ? AND observation_time_utc < ? AND is_deleted = FALSE
        ORDER BY observation_time_utc LIMIT 1");
    $dayScansStmt = $pdo->prepare("SELECT pit_id FROM penguin_scans WHERE observation_id = ? AND (is_deleted = 0 OR is_deleted IS NULL)");
    // Most recent existing observation in a box before the import instant — the row-click target.
    $prevStmt = $pdo->prepare("SELECT observation_time_utc FROM observations WHERE location_id = ? AND is_deleted = FALSE AND observation_time_utc < ? ORDER BY observation_time_utc DESC LIMIT 1");

    $parseInt = function ($v) {
        $v = trim((string)$v);
        if ($v === '') return [0, true];
        if (!preg_match('/^-?\d+$/', $v)) return [0, false];
        return [(int)$v, true];
    };

    // Parse a Sex-N cell into [sizeCode, observedSex, unknownToken].
    //   - Optional leading chick size code BC/LC/SC  -> validated against chick_size_code.
    //   - Remaining token is an observed sex, mapped to the biometric encoding:
    //       M->M, F->F (confirmed/legacy), UM->MM (maybe male), UF->MF (maybe female), U->U.
    //   observedSex '' = none; null = an unrecognised token (flagged, not stored).
    $parseSexCell = function ($raw) {
        $s = strtoupper(preg_replace('/[^A-Za-z]/', '', (string)$raw));
        $size = '';
        if (preg_match('/^(BC|LC|SC)(.*)$/', $s, $m)) { $size = $m[1]; $s = $m[2]; }
        static $sexMap = ['M' => 'M', 'F' => 'F', 'UM' => 'MM', 'UF' => 'MF', 'U' => 'U'];
        $obs = $s === '' ? '' : ($sexMap[$s] ?? null);
        return [$size, $obs, $s];
    };

    // A scanned bird counts as a chick (against the Chicks column, not Adults) if it was chipped
    // as a chick and this observation is within ~3 months of that chipping — i.e. still a chick.
    $isChick = function ($peng, $obsDate) use ($pengAdult, $pengFirstChip) {
        if ($peng === null || $peng === '' || $obsDate === null) return false;
        if (!empty($pengAdult[$peng])) return false;      // chipped as an adult
        $cd = $pengFirstChip[$peng] ?? null;
        if ($cd === null) return false;
        return $obsDate >= $cd && $obsDate <= date('Y-m-d', strtotime($cd . ' +3 months'));
    };

    $unmatched = [];      // chipKey => ['chip'=>original, 'reason'=>why, 'count'=>n, 'boxes'=>[...]]
    $unknownBoxes = [];
    $seenBoxes = [];
    $distinctDates = [];
    $refDate = null;      // first data row's date — the sheet's expected single date
    $birdBoxes = [];      // "date|chipKey" => [['i'=>rowIndex, 'box'=>name], ...] — a bird can't be in two boxes at once
    $R['file_flags'] = [];
    $tzNz = new DateTimeZone('Pacific/Auckland');
    $tzUtc = new DateTimeZone('UTC');
    $today = (new DateTime('now', $tzNz))->format('Y-m-d');
    $prevBirdDigits = [];  // bird column index => previous row's digits (fill-drag detection)
    $prevDate = null;
    $sheetSeen = [];       // "locId|date" => first line seen (within-sheet duplicate box)
    $lineNo = 1;          // header consumed as line 1

    foreach ($records as $rec) {
        $lineNo++;
        if (!count(array_filter($rec, function ($v) { return trim((string)$v) !== ''; }))) continue; // blank line

        $R['totals']['rows']++;
        $box = trim((string)($rec[$idx['box']] ?? ''));
        $dateRaw = trim((string)($rec[$idx['date']] ?? ''));
        $adultsRaw = trim((string)($rec[$idx['adults']] ?? ''));
        $notes = isset($idx['notes']) ? trim((string)($rec[$idx['notes']] ?? '')) : '';
        $errors = []; $warnings = [];
        if (count($rec) !== $headerCount) $warnings[] = 'row has ' . count($rec) . " columns, expected $headerCount — misaligned?";

        // Box -> location
        $locId = null;
        if ($box === '') { $errors[] = 'Missing box'; }
        else {
            $locId = $locLookup[strtoupper($box)] ?? null;
            if ($locId === null) {
                $errors[] = "Unknown box '$box' — not a location in $colonyName";
                if (!in_array($box, $unknownBoxes, true)) $unknownBoxes[] = $box;
            }
            if (!in_array($box, $seenBoxes, true)) $seenBoxes[] = $box;
        }

        // Date -> observation_time. Year-first ONLY (YYYY-MM-DD, or / . space separators) so
        // DD/MM vs MM/DD can never be misread. Anything else is rejected and the row skipped.
        $obsDate = null;
        if (preg_match('/^(\d{4})[-\/. ](\d{1,2})[-\/. ](\d{1,2})$/', $dateRaw, $dm)
            && checkdate((int)$dm[2], (int)$dm[3], (int)$dm[1]))
            $obsDate = sprintf('%04d-%02d-%02d', (int)$dm[1], (int)$dm[2], (int)$dm[3]);
        if ($obsDate === null) $errors[] = "Invalid date '$dateRaw' (expected YYYY-MM-DD)";
        $obsTime = null;
        if ($obsDate) {
            $nzDt = new DateTime($obsDate . ' 14:00:00', $tzNz);
            $nzDt->setTimezone($tzUtc);
            $obsTime = $nzDt->format('Y-m-d H:i:s');
            $distinctDates[$obsDate] = true;
            if ($obsDate > $today) $warnings[] = "date $obsDate is in the future";
            if ($R['date_min'] === null || $obsDate < $R['date_min']) $R['date_min'] = $obsDate;
            if ($R['date_max'] === null || $obsDate > $R['date_max']) $R['date_max'] = $obsDate;
        }

        // Counts / Decom
        $isDecom = (bool)preg_match('/decom/i', $adultsRaw);
        $adults = 0; $eggs = 0; $chicks = 0; $noScan = 0; $breeding = null; $countsOk = true;
        if ($isDecom) {
            $breeding = 'DCM';
        } else {
            list($adults, $aOk) = $parseInt($adultsRaw);
            if (!$aOk) { $errors[] = "Adults '$adultsRaw' is not a number"; $countsOk = false; }
            if (isset($idx['eggs'])) { list($eggs, $ok) = $parseInt($rec[$idx['eggs']] ?? ''); if (!$ok) { $errors[] = 'Eggs is not a number'; $countsOk = false; } }
            if (isset($idx['chicks'])) { list($chicks, $ok) = $parseInt($rec[$idx['chicks']] ?? ''); if (!$ok) { $errors[] = 'Chicks is not a number'; $countsOk = false; } }
            if ($countsOk && $adults < 0) { $errors[] = 'Adults is negative'; $countsOk = false; }
        }

        // Column shift: an 8-digit chip-like value where a count/note belongs — the row slid sideways.
        foreach (['eggs' => 'Eggs', 'chicks' => 'Chicks', 'notes' => 'Notes'] as $ck => $lbl) {
            if (!isset($idx[$ck])) continue;
            $cell = trim((string)($rec[$idx[$ck]] ?? ''));
            if (preg_match('/^\d{8}$/', $cell)) $warnings[] = "$lbl column has a chip-like value ($cell) — shifted row?";
        }

        // Bird cells -> scans. A filled cell is a scan regardless of whether the chip resolves.
        // $miniPengs collects display peng_nums a flag refers to, so the report can show their minis.
        $scans = []; $unmatchedHere = []; $rowKeys = []; $rowSeen = []; $miniPengs = []; $bios = [];
        $addMini = function ($peng) use (&$miniPengs, $prefix) {
            $d = displayPengNum($peng, $prefix);
            if (!in_array($d, $miniPengs, true)) $miniPengs[] = $d;
        };
        if (!$isDecom) {
            foreach ($birdCols as $bi) {
                $val = trim((string)($rec[$bi] ?? ''));
                $sexRaw = isset($sexColByBird[$bi]) ? trim((string)($rec[$sexColByBird[$bi]] ?? '')) : '';
                if ($val === '') continue;
                // Inline "no scan" marker: an adult present but not scanned this visit. Its paired
                // Sex cell can't be attached to a bird, so it's ignored.
                if (preg_replace('/[^a-z]/', '', strtolower($val)) === 'noscan') { $noScan++; continue; }
                $key = ww_chipKey($val);
                // Same chip listed twice in one box — almost always a copy-paste. Flag, scan once.
                if (isset($rowSeen[$key])) {
                    $warnings[] = "chip $val listed more than once in this box";
                    $c2 = $byKeyColony[$key] ?? null;
                    if ($c2 && count($c2['pengs']) === 1) $addMini(array_key_first($c2['pengs']));
                    continue;
                }
                $rowSeen[$key] = true; $rowKeys[] = $key;
                $col = $byKeyColony[$key] ?? null;
                $nPeng = $col ? count($col['pengs']) : 0;
                if ($nPeng === 1) {
                    $pgR = array_key_first($col['pengs']);
                    $scans[] = ['pit_id' => $col['active'] ?? $col['pit'], 'chip' => $val, 'key' => $key, 'peng_num' => $pgR];
                    // Paired Sex cell: a chick size code (validated) and/or an observed sex (biometric).
                    if ($sexRaw !== '') {
                        list($size, $obsSex) = $parseSexCell($sexRaw);
                        if ($size !== '') {
                            // Validate the chick size code against the bird's recorded chick_size_code.
                            // Only a genuine conflict is flagged; a match — or no recorded size — passes
                            // quietly (chip_box is too unreliable to demand a same-box chipping).
                            $recorded = $pengChickSize[$pgR] ?? '';
                            if ($recorded !== '' && $recorded !== $size) {
                                $warnings[] = "chick size $size ≠ recorded $recorded for #" . displayPengNum($pgR, $prefix);
                                $addMini($pgR);
                            }
                        }
                        if ($obsSex === null) { $warnings[] = "unrecognised sex '$sexRaw' for #" . displayPengNum($pgR, $prefix); $addMini($pgR); }
                        elseif ($obsSex !== '') $bios[] = ['peng_num' => $pgR, 'observed_sex' => $obsSex];
                    }
                } else {
                    if ($nPeng > 1) $reason = "last-8 matches $nPeng birds in $colonyName";
                    elseif (isset($byKeyAny[$key])) $reason = 'belongs to another colony';
                    else $reason = "no bird in $colonyName";
                    $u = ['chip' => $val, 'reason' => $reason, 'suggest' => null, 'suggest_peng' => null, 'suggest_key' => null];
                    // Transcription near-match: a single colony bird one edit away from the typed number.
                    if ($nPeng === 0 && !isset($byKeyAny[$key])) {
                        $cand = [];
                        foreach (ww_editNeighbors(ww_digits($val)) as $nb)
                            if (isset($byKeyColony[$nb])) foreach (array_keys($byKeyColony[$nb]['pengs']) as $pg) $cand[$pg] = $nb;
                        if (count($cand) === 1) { $pg = array_key_first($cand); $u['suggest'] = displayPengNum($pg, $prefix); $u['suggest_peng'] = $pg; $u['suggest_key'] = $cand[$pg]; }
                    }
                    $unmatchedHere[] = $u;
                    if (!isset($unmatched[$key])) $unmatched[$key] = ['chip' => $val, 'reason' => $reason, 'suggest' => $u['suggest'], 'count' => 0, 'boxes' => []];
                    $unmatched[$key]['count']++;
                    if ($box !== '' && !in_array($box, $unmatched[$key]['boxes'], true)) $unmatched[$key]['boxes'][] = $box;
                }
            }
        }

        // ---- Flags: interesting/problematic but still importable (never exclude the row) ----
        // Split scanned birds into adults vs chicks (a chick is chipped-as-chick and still <3 months
        // old) so chicks count against the Chicks column, not the adult balance.
        $chickBirds = 0; $adultBirds = count($unmatchedHere); // unmatched -> assume adult
        foreach ($scans as $sc) { if ($isChick($sc['peng_num'], $obsDate)) $chickBirds++; else $adultBirds++; }
        $birdsListed = count($scans) + count($unmatchedHere);
        $needNoScanConfirm = 0; // implied no-scans (adults not accounted for) — created automatically
        if (!$isDecom) {
            // 1. Improbable counts for a little-penguin box.
            if ($adults > 2) $warnings[] = "adults = $adults (>2)";
            if (($eggs + $chicks) > 2) $warnings[] = 'eggs + chicks = ' . ($eggs + $chicks) . ' (>2)';
            // 2. Adult balance: extra adults with no bird cell are unscanned — a no-scan is
            //    created automatically for the difference on import; the row is flagged so the
            //    reviewer sees it. Chicks excluded. (The reverse — more birds entered than the
            //    Adults count — is common and harmless, so it isn't flagged.)
            if ($countsOk && $adults > $adultBirds + $noScan) {
                $needNoScanConfirm = $adults - $adultBirds - $noScan;
                $noScan += $needNoScanConfirm;
                $warnings[] = "adults ($adults) > scanned ($adultBirds) — $needNoScanConfirm no-scan(s) will be created for this date";
            }
        }
        // 5. Chips that didn't resolve to exactly one colony bird (with a "did you mean" near-match).
        foreach ($unmatchedHere as $u) {
            $msg = "chip {$u['chip']}: {$u['reason']}";
            if (!empty($u['suggest'])) { $msg .= " — did you mean #{$u['suggest']} ({$u['suggest_key']})?"; $addMini($u['suggest_peng']); }
            $warnings[] = $msg;
        }
        // Per-scanned-bird checks.
        foreach ($scans as $sc) {
            $pg = $sc['peng_num'];
            // Retired tag: typed an old (inactive) chip when the bird has a newer active one.
            if (isset($pengActiveKey[$pg]) && $pengActiveKey[$pg] !== $sc['key']) {
                $warnings[] = "chip {$sc['chip']} is a retired tag for #" . displayPengNum($pg, $prefix) . " (active {$pengActiveKey[$pg]})";
                $addMini($pg);
            }
            // Scan dated before the bird was chipped — impossible, so the row is an error (skipped).
            if ($obsDate && isset($pengFirstChip[$pg]) && $obsDate < $pengFirstChip[$pg]) {
                $errors[] = "scanned $obsDate, before #" . displayPengNum($pg, $prefix) . " was chipped ({$pengFirstChip[$pg]})";
                $addMini($pg);
            }
            // Bird recorded dead in the database.
            if (!empty($pengMeta[$pg]['dead'])) { $warnings[] = 'recorded dead: #' . displayPengNum($pg, $prefix); $addMini($pg); }
        }
        // Two adults of the same sex (confirmed or suspected) — unusual for a breeding pair.
        if (!$isDecom && count($scans) >= 2) {
            $sexes = []; $known = 0; $anyGuess = false;
            foreach ($scans as $sc) {
                list($sx, $conf) = $effSex($sc['peng_num']);
                if ($sx !== '') { $sexes[$sx] = true; $known++; if (!$conf) $anyGuess = true; }
            }
            if ($known >= 2 && count($sexes) === 1) {
                $warnings[] = 'two ' . (array_key_first($sexes) === 'M' ? 'male' : 'female') . ' adults' . ($anyGuess ? ' (incl. suspected)' : '');
                foreach ($scans as $sc) $addMini($sc['peng_num']);
            }
        }
        // 3. Bird recorded in this box for the first time (no prior scan or chipping here).
        if ($obsDate && $locId !== null) {
            foreach ($scans as $sc) {
                $s = $seenInBox[$locId . '|' . $sc['peng_num']] ?? null;
                $seenBefore = $s && ($s['min'] < $obsDate || $s['max'] > $obsDate);
                // Being chipped in this box also counts as having been here before.
                if (!$seenBefore)
                    foreach ($pengChips[$sc['peng_num']] ?? [] as $ce)
                        if ($ce['box'] === strtoupper($box)) { $seenBefore = true; break; }
                if (!$seenBefore) {
                    $warnings[] = 'Never seen in box ' . $box . ':';
                    $addMini($sc['peng_num']);
                }
            }
        }
        // Spreadsheet fill-down: a bird number exactly +1 from the row above (same column),
        // or a date exactly one day later — drag-handle artifacts, not real observations.
        $curBirdDigits = [];
        foreach ($birdCols as $bi) {
            $dg = ww_digits(trim((string)($rec[$bi] ?? '')));
            if ($dg === '') continue;
            $curBirdDigits[$bi] = $dg;
            if (isset($prevBirdDigits[$bi]) && strlen($dg) === 8 && strlen($prevBirdDigits[$bi]) === 8
                && (int)$dg === (int)$prevBirdDigits[$bi] + 1)
                $warnings[] = "chip $dg is +1 from the row above — spreadsheet fill-down?";
        }
        if ($obsDate && $prevDate) {
            $pd = date_create($prevDate);
            if ($pd && $pd->modify('+1 day')->format('Y-m-d') === $obsDate) $warnings[] = 'date is +1 day from the row above — fill-down?';
        }
        $prevBirdDigits = $curBirdDigits;
        if ($obsDate) $prevDate = $obsDate;

        // 6. Date consistency — a monitor sheet should be one day.
        if ($obsDate) {
            if ($refDate === null) $refDate = $obsDate;
            elseif ($obsDate !== $refDate) $warnings[] = "date $obsDate differs from $refDate";
        }

        // Existing-data check: any observation for this box on this NZ day (any observer,
        // any time). If the sheet row MATCHES it (counts, status, scanned birds) it's a
        // duplicate — warned and auto-skipped. If it DIFFERS, the row is a conflict:
        // skipped by default (fix the sheet or the stored observation until they agree),
        // or imported as a SECOND observation when the user ticks the override.
        $duplicate = false; $conflict = null;
        if ($locId !== null && $obsDate !== null) {
            $dayStart = new DateTime($obsDate . ' 00:00:00', $tzNz);
            $dayStartUtc = (clone $dayStart)->setTimezone($tzUtc)->format('Y-m-d H:i:s');
            $dayEndUtc = $dayStart->modify('+1 day')->setTimezone($tzUtc)->format('Y-m-d H:i:s');
            $dayObsStmt->execute([$locId, $dayStartUtc, $dayEndUtc]);
            $ex = $dayObsStmt->fetch();
            if ($ex) {
                $diffs = [];
                foreach ([['adults', $adults], ['eggs', $eggs], ['chicks', $chicks], ['no_scan', $noScan]] as $pair) {
                    if ((int)$ex[$pair[0]] !== (int)$pair[1]) $diffs[] = "{$pair[0]}: sheet {$pair[1]} ≠ existing " . (int)$ex[$pair[0]];
                }
                $exBs = strtoupper(trim((string)($ex['breeding_status'] ?? '')));
                $shBs = strtoupper(trim((string)($breeding ?? '')));
                if ($exBs !== $shBs) $diffs[] = 'status: sheet ' . ($shBs !== '' ? $shBs : '(none)') . ' ≠ existing ' . ($exBs !== '' ? $exBs : '(none)');
                $dayScansStmt->execute([$ex['observation_id']]);
                $exPits = array_map(fn($p) => substr($p, -8), $dayScansStmt->fetchAll(PDO::FETCH_COLUMN));
                $shPits = array_map(fn($sc) => substr($sc['pit_id'], -8), $scans);
                $onlyExisting = array_diff($exPits, $shPits);
                $onlySheet = array_diff($shPits, $exPits);
                if ($onlyExisting) $diffs[] = 'birds only in existing: ' . implode(', ', $onlyExisting);
                if ($onlySheet) $diffs[] = 'birds only in sheet: ' . implode(', ', $onlySheet);
                if ($diffs) {
                    $conflict = "Box already has data on $obsDate and the sheet DIFFERS: " . implode('; ', $diffs)
                        . ". Fix the sheet (or edit the stored observation) until they match — or tick “Import conflicting rows” to add this as a second observation.";
                } else {
                    $duplicate = true;
                    $warnings[] = "Box already has a matching observation on $obsDate — skipped (already recorded)";
                }
            }
        }
        // Within-sheet duplicate: same box+date appears earlier in THIS file. Skip the repeat
        // (the DB check can't catch it — neither row is in the DB yet at analyse time).
        $sheetDupOf = null;
        if ($locId !== null && $obsDate !== null) {
            $sk = $locId . '|' . $obsDate;
            if (isset($sheetSeen[$sk])) $sheetDupOf = $sheetSeen[$sk];
            else $sheetSeen[$sk] = $lineNo;
        }
        // Row-click target: most recent existing observation in this box before the import.
        $prevObs = null;
        if ($locId !== null && $obsTime !== null) {
            $prevStmt->execute([$locId, $obsTime]);
            $pv = $prevStmt->fetchColumn();
            if ($pv !== false) $prevObs = $pv;
        }

        if ($sheetDupOf !== null) $warnings[] = "duplicate of line $sheetDupOf in this sheet (same box + date)";
        $status = count($errors) ? 'error'
            : ($conflict !== null ? 'conflict'
            : (($duplicate || $sheetDupOf !== null) ? 'duplicate' : 'ok'));
        $R['rows'][] = [
            'line' => $lineNo, 'box' => $box, 'date' => $obsDate, 'location_id' => $locId,
            'adults' => $adults, 'eggs' => $eggs, 'chicks' => $chicks, 'no_scan' => $noScan,
            'confirm_no_scan' => $needNoScanConfirm, 'bios' => $bios,
            'breeding_status' => $breeding, 'is_decom' => $isDecom, 'notes' => $notes,
            'obs_time' => $obsTime, 'prev_obs' => $prevObs, 'scans' => $scans, 'unmatched' => $unmatchedHere,
            'errors' => $errors, 'warnings' => $warnings, 'conflict' => $conflict, 'mini_pengs' => $miniPengs, 'status' => $status,
        ];
        // Track each scanned chip's box per day so we can flag a bird appearing in two boxes.
        if ($obsDate && $rowKeys) {
            $ri = count($R['rows']) - 1;
            foreach ($rowKeys as $k) $birdBoxes[$obsDate . '|' . $k][] = ['i' => $ri, 'box' => $box];
        }

        if ($status === 'error') { $R['totals']['error_rows']++; continue; }
        if ($status === 'conflict') { $R['totals']['conflicts']++; continue; }
        if ($status === 'duplicate') { $R['totals']['duplicates']++; continue; }
        // Importable row — tally what would actually be written.
        $R['totals']['importable']++;
        if (count($warnings)) $R['totals']['flagged']++;
        if ($isDecom) $R['totals']['decom']++;
        $R['totals']['adults'] += $adults; $R['totals']['eggs'] += $eggs; $R['totals']['chicks'] += $chicks;
        $R['totals']['no_scan'] += $noScan; $R['totals']['scans_matched'] += count($scans);
        $R['totals']['scans_unmatched'] += count($unmatchedHere);
        $R['totals']['biometrics'] += count($bios);
        if ($needNoScanConfirm > 0) $R['totals']['noscan_confirm']++;
    }

    if (count($distinctDates) > 1) {
        $dl = array_keys($distinctDates); sort($dl);
        $R['file_flags'][] = 'Sheet spans multiple dates: ' . implode(', ', $dl);
    }
    if ($ignoredCols) $R['file_flags'][] = 'Ignored columns (not imported): ' . implode(', ', $ignoredCols);

    // Coverage: colony boxes absent from this sheet (is it a full monitor or a partial?).
    $present = [];
    foreach ($seenBoxes as $b) $present[strtoupper($b)] = true;
    $missing = [];
    foreach ($locLookup as $up => $lid) if (!isset($present[$up])) $missing[] = $up;
    sort($missing, SORT_NATURAL);
    $R['totals']['boxes_in_colony'] = count($locLookup);
    $R['totals']['boxes_missing'] = count($missing);
    $R['coverage_missing'] = $missing;
    if ($missing) $R['file_flags'][] = count($missing) . ' of ' . count($locLookup) . ' colony boxes not in this sheet';

    // A bird scanned in two different boxes on the same day can't be right — flag every box.
    foreach ($birdBoxes as $dk => $entries) {
        $boxes = [];
        foreach ($entries as $e) if (!in_array($e['box'], $boxes, true)) $boxes[] = $e['box'];
        if (count($boxes) < 2) continue;
        $chip = substr($dk, strpos($dk, '|') + 1);
        $cc = $byKeyColony[$chip] ?? null;
        $disp = ($cc && count($cc['pengs']) === 1) ? displayPengNum(array_key_first($cc['pengs']), $prefix) : null;
        foreach ($entries as $e) {
            $others = [];
            foreach ($boxes as $b) if ($b !== $e['box']) $others[] = $b;
            $row = &$R['rows'][$e['i']];
            $had = count($row['warnings']) > 0;
            $row['warnings'][] = "chip $chip also in box " . implode(', ', $others) . ' this day';
            if ($disp !== null && !in_array($disp, $row['mini_pengs'], true)) $row['mini_pengs'][] = $disp;
            if ($row['status'] === 'ok' && !$had) $R['totals']['flagged']++;
            unset($row);
        }
    }

    $R['totals']['boxes'] = count($seenBoxes);
    $R['unknown_boxes'] = $unknownBoxes;
    $R['unmatched_chips'] = array_values($unmatched);
    usort($R['unmatched_chips'], function ($a, $b) { return $b['count'] - $a['count']; });
    return $R;
}

// Read {csv, filename, colony_id} from the request body.
// Parse an OLD NestCheck (v37-era) monitor JSON export into a preview structure the same
// analyze/commit flow can consume. The file is a JSON array (or single object) of MonitorDetails:
//   { IsDeleted, LastSaved, filename, BoxData: { "<box>": {
//        ScannedIds:[{BirdId,Timestamp,...}], Adults, Eggs, Chicks, GateStatus, Notes,
//        whenDataCollectedUtc, BreedingChance } } }
// Each box entry becomes one observation; scans resolve by last-8 chip match, colony-scoped.
function ww_parseImportJson($pdo, $jsonText, $colonyId, $observerId, $filename) {
    $R = [
        'ok' => true, 'error' => null,
        'colony_id' => $colonyId, 'colony_name' => null, 'filename' => $filename,
        'rows' => [], 'unmatched_chips' => [], 'unknown_boxes' => [],
        'date_min' => null, 'date_max' => null,
        'totals' => [
            'monitors' => 0, 'observations' => 0, 'importable' => 0, 'duplicates' => 0, 'error_rows' => 0,
            'boxes' => 0, 'adults' => 0, 'eggs' => 0, 'chicks' => 0,
            'scans_matched' => 0, 'scans_unmatched' => 0, 'box_tags_skipped' => 0,
        ],
    ];

    $cstmt = $pdo->prepare("SELECT colony_name FROM colonies WHERE colony_id = ?");
    $cstmt->execute([$colonyId]);
    $colonyName = $cstmt->fetchColumn();
    if ($colonyName === false) { $R['ok'] = false; $R['error'] = "Colony $colonyId not found"; return $R; }
    $R['colony_name'] = $colonyName;

    $data = json_decode($jsonText, true);
    if (!is_array($data)) { $R['ok'] = false; $R['error'] = 'Not valid JSON'; return $R; }
    if (isset($data['BoxData']) || isset($data['filename'])) $data = [$data]; // single monitor object
    $monitors = array_values(array_filter($data, function ($m) { return is_array($m) && isset($m['BoxData']); }));
    if (!$monitors) { $R['ok'] = false; $R['error'] = 'No monitor records found (expected objects with a "BoxData" map)'; return $R; }

    // Colony locations: UPPER(name) => location_id.
    $locLookup = [];
    $lstmt = $pdo->prepare("SELECT location_id, location_name FROM observation_locations WHERE colony_id = ?");
    $lstmt->execute([$colonyId]);
    foreach ($lstmt->fetchAll() as $l) $locLookup[strtoupper($l['location_name'])] = (int)$l['location_id'];

    // Chip resolution scoped to this colony: last-8 of pit_id => the bird (active pit preferred).
    // Carries peng_num/sex/chick_size so the preview can render the same bird minis as the day view.
    $chipMap = [];
    $chstmt = $pdo->prepare("SELECT pc.pit_id, pc.is_active, pc.peng_num, p.sex, p.chick_size_code FROM penguin_chips pc JOIN penguins p ON pc.peng_num = p.peng_num WHERE p.colony_id = ?");
    $chstmt->execute([$colonyId]);
    foreach ($chstmt->fetchAll() as $c) {
        $k = ww_chipKey($c['pit_id']);
        if (!isset($chipMap[$k]) || !empty($c['is_active'])) $chipMap[$k] = [
            'pit_id' => $c['pit_id'], 'peng_num' => $c['peng_num'],
            'sex' => $c['sex'], 'chick_size_code' => $c['chick_size_code'],
        ];
    }

    $nz = new DateTimeZone('Pacific/Auckland');
    $utc = new DateTimeZone('UTC');
    $unmatched = []; $unknownBoxes = []; $seen = [];
    $dupStmt = $pdo->prepare("SELECT 1 FROM observations WHERE location_id = ? AND observation_time_utc >= ? AND observation_time_utc < ? LIMIT 1");

    foreach ($monitors as $monitor) {
        if (!empty($monitor['IsDeleted'])) continue;
        $R['totals']['monitors']++;
        $mfile = basename((string)($monitor['filename'] ?? $filename));
        $lastSaved = $monitor['LastSaved'] ?? null;
        foreach (($monitor['BoxData'] ?? []) as $boxName => $bd) {
            if (!is_array($bd)) continue;
            $R['totals']['observations']++; $R['totals']['boxes']++;
            $boxKey = strtoupper(trim((string)$boxName));
            $locId = $locLookup[$boxKey] ?? null;

            $raw = $bd['whenDataCollectedUtc'] ?? $lastSaved ?? null;
            $ts = $raw ? strtotime((string)$raw) : false;
            $obsTime = $ts !== false ? gmdate('Y-m-d H:i:s', $ts) : null;
            $nzDay = null;
            if ($ts !== false) { $dt = new DateTime('@' . $ts); $dt->setTimezone($nz); $nzDay = $dt->format('Y-m-d'); }

            $adults = (int)($bd['Adults'] ?? 0);
            $eggs = (int)($bd['Eggs'] ?? 0);
            $chicks = (int)($bd['Chicks'] ?? 0);
            $breeding = trim((string)($bd['BreedingChance'] ?? '')); $breeding = $breeding === '' ? null : $breeding;
            $gate = trim((string)($bd['GateStatus'] ?? '')); $gate = $gate === '' ? null : $gate;
            $notes = (string)($bd['Notes'] ?? '');

            // Resolve scans. Box tags (short-8 starts "9130", or the 9000250 tag series) are
            // dropped. The file may spell a tag either way — with the reader's letter prefix, as
            // every export before 2026-08-17 did, or as the bare ISO digits — so judge it stripped.
            $scans = []; $rowUnmatched = []; $boxTags = 0;
            foreach (($bd['ScannedIds'] ?? []) as $sc) {
                $bird = (string)($sc['BirdId'] ?? '');
                if ($bird === '') continue;
                $clean = strtoupper(preg_replace('/[^A-Za-z0-9]/', '', $bird));
                $short8 = strlen($clean) >= 8 ? substr($clean, -8) : $clean;
                $bare = (string)ww_pit($clean);
                if (substr($short8, 0, 4) === '9130' || strpos($bare, '9000250') === 0) { $boxTags++; continue; }
                if (isset($chipMap[$short8])) $scans[] = $chipMap[$short8];
                else { $rowUnmatched[] = $short8; $unmatched[$short8] = ($unmatched[$short8] ?? 0) + 1; }
            }
            $R['totals']['scans_matched'] += count($scans);
            $R['totals']['scans_unmatched'] += count($rowUnmatched);
            $R['totals']['box_tags_skipped'] += $boxTags;

            $status = 'ok'; $error = null;
            if ($locId === null) { $status = 'error'; $error = 'Unknown box'; if (!in_array($boxKey, $unknownBoxes, true)) $unknownBoxes[] = $boxKey; }
            elseif ($obsTime === null) { $status = 'error'; $error = 'Unreadable date'; }
            else {
                $sk = $locId . '|' . $nzDay;
                if (isset($seen[$sk])) $status = 'duplicate';
                else {
                    $startUtc = (new DateTime($nzDay . ' 00:00:00', $nz))->setTimezone($utc)->format('Y-m-d H:i:s');
                    $endUtc = (new DateTime($nzDay . ' 00:00:00', $nz))->modify('+1 day')->setTimezone($utc)->format('Y-m-d H:i:s');
                    $dupStmt->execute([$locId, $startUtc, $endUtc]);
                    if ($dupStmt->fetchColumn()) $status = 'duplicate';
                }
                $seen[$sk] = true;
            }

            if ($status === 'ok') {
                $R['totals']['importable']++;
                $R['totals']['adults'] += $adults; $R['totals']['eggs'] += $eggs; $R['totals']['chicks'] += $chicks;
                if ($nzDay) {
                    if ($R['date_min'] === null || $nzDay < $R['date_min']) $R['date_min'] = $nzDay;
                    if ($R['date_max'] === null || $nzDay > $R['date_max']) $R['date_max'] = $nzDay;
                }
            } elseif ($status === 'duplicate') $R['totals']['duplicates']++;
            else $R['totals']['error_rows']++;

            $R['rows'][] = [
                'monitor' => $mfile, 'box' => (string)$boxName, 'date' => $nzDay, 'obs_time' => $obsTime,
                'location_id' => $locId, 'adults' => $adults, 'eggs' => $eggs, 'chicks' => $chicks,
                'breeding_status' => $breeding, 'gate_status' => $gate, 'notes' => $notes,
                'scans' => $scans, 'unmatched' => $rowUnmatched, 'box_tags' => $boxTags,
                'status' => $status, 'error' => $error,
            ];
        }
    }

    ksort($unmatched);
    foreach ($unmatched as $chip => $count) $R['unmatched_chips'][] = ['chip' => (string)$chip, 'count' => $count];
    $R['unknown_boxes'] = $unknownBoxes;
    return $R;
}

function ww_importJsonInput() {
    $in = json_decode(file_get_contents('php://input'), true) ?? [];
    $json = (string)($in['json'] ?? '');
    $filename = basename(trim((string)($in['filename'] ?? 'import.json')));
    $colonyId = (int)($in['colony_id'] ?? 0);
    return [$json, $filename, $colonyId];
}

function ww_importInput() {
    $in = json_decode(file_get_contents('php://input'), true) ?? [];
    $csv = (string)($in['csv'] ?? '');
    // Keep the filename as-is (incl. extension) — it becomes the day's monitor label, e.g. "FM-19 2024.csv".
    $filename = basename(trim((string)($in['filename'] ?? 'import.csv')));
    $colonyId = (int)($in['colony_id'] ?? 0);
    return [$csv, $filename, $colonyId];
}

if ($action === 'import_csv_analyze') {
    list($csv, $filename, $colonyId) = ww_importInput();
    if ($csv === '') { http_response_code(400); echo json_encode(['error' => 'No CSV supplied']); exit; }
    if (!$colonyId) { http_response_code(400); echo json_encode(['error' => 'colony_id required']); exit; }
    echo json_encode(ww_parseImportCsv($pdo, $csv, $colonyId, (int)$observer['observer_id'], $filename));
    exit;
}

if ($action === 'import_csv_commit') {
    list($csv, $filename, $colonyId) = ww_importInput();
    if ($csv === '') { http_response_code(400); echo json_encode(['error' => 'No CSV supplied']); exit; }
    if (!$colonyId) { http_response_code(400); echo json_encode(['error' => 'colony_id required']); exit; }
    $observerId = (int)$observer['observer_id'];
    // User override: also import rows that CONFLICT with same-day existing data, as
    // second observations for that box+day. Off by default.
    $inBody = json_decode(file_get_contents('php://input'), true) ?? [];
    $importConflicts = !empty($inBody['import_conflicts']);

    // Re-parse so what we write is exactly what was validated (DB may have shifted since analyze).
    $A = ww_parseImportCsv($pdo, $csv, $colonyId, $observerId, $filename);
    if (!$A['ok']) { http_response_code(400); echo json_encode(['error' => $A['error']]); exit; }

    $imported = 0; $scans = 0; $bios = 0; $skippedDup = 0; $skippedErr = 0; $skippedConflicts = 0; $importedConflicts = 0;
    try {
        $pdo->beginTransaction();
        // Skip a biometric if this bird already has a live one on this date (idempotent re-imports).
        $dupBio = $pdo->prepare("SELECT 1 FROM penguin_biometric_data WHERE peng_num = ? AND observation_date = ? AND (is_deleted = 0 OR is_deleted IS NULL) LIMIT 1");
        foreach ($A['rows'] as $row) {
            if ($row['status'] === 'error') { $skippedErr++; continue; }
            if ($row['status'] === 'duplicate') { $skippedDup++; continue; }
            if ($row['status'] === 'conflict') {
                if (!$importConflicts) { $skippedConflicts++; continue; }
                $importedConflicts++;
            }
            // Every insert goes through the same audited path as a manual crud write.
            $obsId = wwAuditedInsert($pdo, 'observations', [
                'location_id' => $row['location_id'], 'observer_id' => $observerId, 'observation_time_utc' => $row['obs_time'],
                'adults' => $row['adults'], 'eggs' => $row['eggs'], 'chicks' => $row['chicks'], 'no_scan' => $row['no_scan'],
                'breeding_status' => $row['breeding_status'], 'notes' => $row['notes'],
            ], $observerId);
            $imported++;
            foreach ($row['scans'] as $sc) {
                wwAuditedInsert($pdo, 'penguin_scans', ['observation_id' => $obsId, 'pit_id' => $sc['pit_id'], 'scan_time_utc' => $row['obs_time']], $observerId);
                $scans++;
            }
            foreach ($row['bios'] ?? [] as $b) {
                $dupBio->execute([$b['peng_num'], $row['date']]);
                if ($dupBio->fetchColumn()) continue;
                wwAuditedInsert($pdo, 'penguin_biometric_data', ['peng_num' => $b['peng_num'], 'observation_id' => $obsId, 'observation_date' => $row['date'], 'observed_sex' => $b['observed_sex']], $observerId);
                $bios++;
            }
        }
        $pdo->commit();
    } catch (Exception $e) {
        if ($pdo->inTransaction()) $pdo->rollBack();
        http_response_code(500);
        echo json_encode(['error' => 'Import failed: ' . $e->getMessage()]);
        exit;
    }

    echo json_encode([
        'success' => true, 'filename' => $A['filename'], 'colony_id' => $colonyId, 'colony_name' => $A['colony_name'],
        'imported' => $imported, 'scans' => $scans, 'biometrics' => $bios, 'skipped_duplicates' => $skippedDup, 'skipped_errors' => $skippedErr,
        'skipped_conflicts' => $skippedConflicts, 'imported_conflicts' => $importedConflicts,
        'unmatched_chips' => $A['unmatched_chips'],
    ]);
    exit;
}

if ($action === 'import_json_analyze') {
    list($json, $filename, $colonyId) = ww_importJsonInput();
    if ($json === '') { http_response_code(400); echo json_encode(['error' => 'No JSON supplied']); exit; }
    if (!$colonyId) { http_response_code(400); echo json_encode(['error' => 'colony_id required']); exit; }
    echo json_encode(ww_parseImportJson($pdo, $json, $colonyId, (int)$observer['observer_id'], $filename));
    exit;
}

if ($action === 'import_json_commit') {
    list($json, $filename, $colonyId) = ww_importJsonInput();
    if ($json === '') { http_response_code(400); echo json_encode(['error' => 'No JSON supplied']); exit; }
    if (!$colonyId) { http_response_code(400); echo json_encode(['error' => 'colony_id required']); exit; }
    $observerId = (int)$observer['observer_id'];
    // Re-parse so what we write is exactly what was validated (DB may have shifted since analyze).
    $A = ww_parseImportJson($pdo, $json, $colonyId, $observerId, $filename);
    if (!$A['ok']) { http_response_code(400); echo json_encode(['error' => $A['error']]); exit; }

    $imported = 0; $scans = 0; $skippedDup = 0; $skippedErr = 0; $dayNotes = 0;
    try {
        $pdo->beginTransaction();
        // A nestcheck export's monitor filename is the closest thing the file has to a note on
        // the day ("PenguinMonitor 20 May 26 FM Marian"). Cleaned of its wrapper and date, it
        // names the day it imported into — but only where that day has no note yet.
        $noteFor = [];
        foreach ($A['rows'] as $row) {
            if ($row['status'] !== 'ok' || !$row['date']) continue;
            $clean = ww_cleanMonitorLabel((string)($row['monitor'] ?? ''));
            if ($clean !== '' && !isset($noteFor[$row['date']])) $noteFor[$row['date']] = $clean;
        }
        foreach ($A['rows'] as $row) {
            if ($row['status'] === 'error') { $skippedErr++; continue; }
            if ($row['status'] === 'duplicate') { $skippedDup++; continue; }
            $obsId = wwAuditedInsert($pdo, 'observations', [
                'location_id' => $row['location_id'], 'observer_id' => $observerId, 'observation_time_utc' => $row['obs_time'],
                'adults' => $row['adults'], 'eggs' => $row['eggs'], 'chicks' => $row['chicks'],
                'breeding_status' => $row['breeding_status'], 'gate_status' => $row['gate_status'], 'notes' => $row['notes'],
            ], $observerId);
            $imported++;
            foreach ($row['scans'] as $sc) {
                wwAuditedInsert($pdo, 'penguin_scans', ['observation_id' => $obsId, 'pit_id' => $sc['pit_id'], 'scan_time_utc' => $row['obs_time']], $observerId);
                $scans++;
            }
        }
        foreach ($noteFor as $noteDate => $note) {
            if (wwFillDayNote($pdo, $colonyId, $noteDate, $note, $observerId, 'json_import')) $dayNotes++;
        }
        $pdo->commit();
    } catch (Exception $e) {
        if ($pdo->inTransaction()) $pdo->rollBack();
        http_response_code(500);
        echo json_encode(['error' => 'Import failed: ' . $e->getMessage()]);
        exit;
    }

    echo json_encode([
        'success' => true, 'filename' => $A['filename'], 'colony_id' => $colonyId, 'colony_name' => $A['colony_name'],
        'imported' => $imported, 'scans' => $scans, 'skipped_duplicates' => $skippedDup, 'skipped_errors' => $skippedErr,
        'day_notes' => $dayNotes, 'unmatched_chips' => $A['unmatched_chips'],
    ]);
    exit;
}

echo json_encode(['error'=>'Unknown action']);
