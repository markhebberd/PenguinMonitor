<?php
/**
 * Audited CRUD API with session-based observer auth and role-based access.
 *
 * POST ?action=login     - {email, password} -> token, role
 * POST ?action=register  - {name, email, password} (restricted emails only)
 *
 * All other actions require Authorization: Bearer <token>
 * GET    ?action=list&table=X[&field=val]
 * GET    ?action=get&table=X&id=N
 * GET    ?action=history&table=X&id=N  - audit log for a record
 * POST   ?action=create&table=X  (JSON body) [editor+]
 * POST   ?action=update&table=X&id=N  (JSON body) [editor+]
 * POST   ?action=delete&table=X&id=N  [editor+, soft delete for observations]
 */
require_once 'config.php';

header('Content-Type: application/json');
header('Cache-Control: no-cache');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type, Authorization');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }

$pdo = getDbConnection();

// Ensure sessions table
$pdo->exec("CREATE TABLE IF NOT EXISTS sessions (
    token VARCHAR(64) PRIMARY KEY,
    observer_id INT NOT NULL,
    expires_at DATETIME NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (observer_id) REFERENCES users(id)
)");

$action = $_GET['action'] ?? '';

if ($action === 'login') { handleLogin($pdo); exit; }
if ($action === 'register') { handleRegister($pdo); exit; }
// Set-password flow (unauthenticated by design: the emailed token IS the credential)
if ($action === 'request_password_reset') { handleRequestPasswordReset($pdo); exit; }
if ($action === 'check_reset_token') { handleCheckResetToken($pdo); exit; }
if ($action === 'reset_password') { handleResetPassword($pdo); exit; }

$observer = authenticate($pdo);
if (!$observer) { http_response_code(401); echo json_encode(['error' => 'Not authenticated']); exit; }

// FM dates are a Tarakohe (PT) field-book concept — the date_mappings table has no colony
// column, so every FM endpoint gates on the caller's active colony instead. Non-PT colonies
// get an empty set (reads) or a refusal (writes). colony_id defaults to 1 (Tarakohe).
function wwFmDatesApply($pdo): bool {
    return getColonyPrefix($pdo, (int)($_GET['colony_id'] ?? 1)) === 'PT';
}

// Season field-monitoring dates — read (GET) and write (POST)
if ($action === 'season_fm_dates' && $_SERVER['REQUEST_METHOD'] === 'GET') {
    if (!wwFmDatesApply($pdo)) { echo json_encode([]); exit; }
    $seasonInput = $_GET['season'] ?? '';
    $season = strlen($seasonInput) === 2 ? 2000 + intval($seasonInput) : intval($seasonInput);
    if (!$season) { echo json_encode(['error' => 'season required']); exit; }
    $stmt = $pdo->prepare("SELECT date_number, actual_date, partial_monitor FROM date_mappings WHERE season_year = ? ORDER BY date_number");
    $stmt->execute([$season]);
    echo json_encode($stmt->fetchAll());
    exit;
}

// All registered FM dates across every season — lets the app flag FM dates app-wide
if ($action === 'all_fm_dates' && $_SERVER['REQUEST_METHOD'] === 'GET') {
    if (!wwFmDatesApply($pdo)) { echo json_encode([]); exit; }
    $stmt = $pdo->query("SELECT season_year, date_number, actual_date, partial_monitor FROM date_mappings ORDER BY actual_date");
    echo json_encode($stmt->fetchAll());
    exit;
}

if ($action === 'change_password') { handleChangePassword($pdo, $observer); exit; }

/** The peng_num the next penguins-create in this colony will assign (prefixed, e.g. "PT1004").
 *  Single source of truth: the create path AND the preview endpoint both use this. */
function wwNextPengNum($pdo, int $colonyId): string {
    $stmt = $pdo->prepare("SELECT MAX(CAST(REGEXP_REPLACE(peng_num, '^[A-Z]+', '') AS UNSIGNED)) FROM penguins WHERE colony_id = ?");
    $stmt->execute([$colonyId]);
    return getColonyPrefix($pdo, $colonyId) . (string)((int)$stmt->fetchColumn() + 1);
}

// Preview of the number the next penguins-create will assign.
if ($action === 'next_peng_num' && $_SERVER['REQUEST_METHOD'] === 'GET') {
    echo json_encode(['success' => true, 'peng_num' => wwNextPengNum($pdo, (int)($_GET['colony_id'] ?? 1))]);
    exit;
}

/** The peng_num to give a new penguin. Creates send the number the device predicted ($req):
 *  honour it if still free, else fall back to the next number in sequence — renaming an existing
 *  bird is easy on wildwatch, so there's no need for the old out-of-band +100 park. No request
 *  (or an unparseable one) => next in sequence. Returns prefixed. */
function wwResolvePengNum($pdo, int $colonyId, string $req): string {
    $req = trim($req);
    if ($req !== '' && preg_match('/^([A-Z]*)(\d+)$/', strtoupper($req), $m)) {
        $prefix = $m[1] !== '' ? $m[1] : getColonyPrefix($pdo, $colonyId);
        $exists = $pdo->prepare("SELECT 1 FROM penguins WHERE peng_num = ?");
        $exists->execute([$prefix . (int)$m[2]]);
        if (!$exists->fetchColumn()) return $prefix . (int)$m[2];
    }
    return wwNextPengNum($pdo, $colonyId);
}

/**
 * Create a chipped bird — penguin + chip + biometrics — in ONE transaction.
 *
 * The field app queues chippings while offline and replays them on the next sync. Doing that
 * as three separate creates meant a connection drop between them left half a bird behind, and
 * the replay then made a SECOND penguin for the same PIT. So: all-or-nothing, and idempotent —
 * pit_id is the natural key, so a replay of a chipping that already landed just returns the
 * peng_num it landed as. Retrying is therefore always safe, however the first attempt died.
 */
if ($action === 'create_chipped_bird' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    $cbRole = $observer['role'] ?? 'viewer';
    if ($cbRole !== 'admin' && $cbRole !== 'editor') { http_response_code(403); echo json_encode(['error'=>'Editors only']); exit; }

    $in = json_decode(file_get_contents('php://input'), true);
    if (!$in || !is_array($in)) { http_response_code(400); echo json_encode(['error'=>'JSON body required']); exit; }
    $pit = ww_pit(trim((string)($in['pit_id'] ?? '')));
    if ($pit === '') { http_response_code(400); echo json_encode(['error'=>'pit_id required']); exit; }
    // The tag is its 15 digits. A client sending the reader's "LA" form is normalized above, so
    // the idempotency check below sees the same key however the chipping was captured.
    if (!preg_match('/^\d{15}$/', $pit)) {
        http_response_code(400); echo json_encode(['error'=>'pit_id must be the 15-digit ISO tag number']); exit;
    }

    $cid = (int)($_GET['colony_id'] ?? 1);
    requireColonyAccess($pdo, $observer, $cid, true); // the new bird is stamped with this colony
    $viewPrefix = getColonyPrefix($pdo, $cid);
    $pdo->beginTransaction();
    try {
        // Idempotency: this PIT is already chipped, so the bird exists — hand back its number
        // rather than minting a duplicate. Covers "insert landed, response never arrived".
        $dup = $pdo->prepare("SELECT peng_num FROM penguin_chips WHERE pit_id = ?");
        $dup->execute([wwResolvePit($pdo, $pit) ?? $pit]);
        if ($existing = $dup->fetchColumn()) {
            $pdo->commit();
            echo json_encode(['success'=>true, 'replayed'=>true, 'peng_num'=>displayPengNum($existing, $viewPrefix)]);
            exit;
        }

        $pengNum = wwResolvePengNum($pdo, $cid, (string)($in['requested_peng_num'] ?? ''));
        $obsId = $observer['observer_id'];
        $reason = $in['_reason'] ?? 'Offline chipping synced from NestCheck';

        $penguin = ['peng_num'=>$pengNum, 'colony_id'=>$cid, 'chipped_as_adult'=>!empty($in['chipped_as_adult']) ? 1 : 0];
        if (!empty($in['chick_size_code'])) $penguin['chick_size_code'] = $in['chick_size_code'];
        wwAuditedInsert($pdo, 'penguins', $penguin, $obsId, $reason);

        $chip = [
            'peng_num'  => $pengNum,
            'pit_id'    => $pit,
            'chip_date' => $in['chip_date'] ?? date('Y-m-d'),
            'is_active' => 1,
        ];
        foreach (['chip_box'] as $f) if (isset($in[$f])) $chip[$f] = $in[$f]; // chip_by is retired
        // Who chipped it, as a user. An explicit id wins; otherwise derive it from the acronym an
        // older client (or the web add-penguin form) signs with, so the chipper still lands as an id.
        $asChipUser = function ($v) use ($pdo) {
            if ($v === null || $v === '' || (int)$v === 0) return null;
            $c = $pdo->prepare("SELECT id FROM users WHERE id = ?");
            $c->execute([(int)$v]);
            return $c->fetchColumn() ? (int)$v : null;
        };
        $chip['chipper_id'] = $asChipUser($in['chipper_id'] ?? null);
        if ($chip['chipper_id'] === null && !empty($in['chip_by'])) {
            $byAcr = $pdo->prepare("SELECT id FROM users WHERE chip_acronym = ?");
            $byAcr->execute([trim($in['chip_by'])]);
            $chip['chipper_id'] = ($hit = $byAcr->fetchColumn()) ? (int)$hit : null;
        }
        $chip['assistant_id'] = $asChipUser($in['assistant_id'] ?? null);
        wwAuditedInsert($pdo, 'penguin_chips', $chip, $obsId, $reason);

        $bio = [];
        foreach (['weight', 'flipper_length', 'observed_sex', 'notes'] as $f) {
            if (isset($in[$f]) && $in[$f] !== '') $bio[$f] = $in[$f];
        }
        if ($bio) {
            $bio['peng_num'] = $pengNum;
            $bio['observation_date'] = $in['observation_date'] ?? ($in['chip_date'] ?? date('Y-m-d'));
            wwAuditedInsert($pdo, 'penguin_biometric_data', stripRetiredColumns('penguin_biometric_data', $bio), $obsId, $reason);
        }

        $pdo->commit();
        echo json_encode(['success'=>true, 'peng_num'=>displayPengNum($pengNum, $viewPrefix)]);
    } catch (Exception $e) {
        $pdo->rollBack(); // nothing partial survives — the app can safely retry the whole thing
        http_response_code(400);
        echo json_encode(['success'=>false, 'error'=>$e->getMessage()]);
    }
    exit;
}

/**
 * Record a verdict on one half of a breeding verification, anchored to a clutch's first-egg
 * observation. Each half (adults / offspring) is accepted, rejected, or cleared independently;
 * the row is upserted through the audited gateway. Clearing the last remaining verdict deletes
 * the row (chk_bv_reviewed requires at least one).
 *
 * ACCEPT snapshots the algorithm's detected data as the fixture + drift baseline; REJECT records
 * a note (required) and holds no data for that half. Body:
 *   { observation_id, half:'adults'|'chicks', verdict:'accepted'|'rejected'|'clear', note?,
 *     accept adults: male_peng_num, female_peng_num
 *     accept chicks: chicks:[peng_num...], dead_eggs, dead_chicks, fledged_unchipped }
 * peng_nums arrive display-stripped and are re-prefixed with dbPengNum. male/female are FK'd; the
 * chicks list is stored as a JSON array of prefixed peng_nums (renumber-maintained in db_write.php).
 */
if ($action === 'save_verification' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    $svRole = $observer['role'] ?? 'viewer';
    if ($svRole !== 'admin' && $svRole !== 'editor') { http_response_code(403); echo json_encode(['error'=>'Editors only']); exit; }
    $in = json_decode(file_get_contents('php://input'), true);
    if (!is_array($in)) { http_response_code(400); echo json_encode(['error'=>'JSON body required']); exit; }
    $obsId   = (int)($in['observation_id'] ?? 0);
    $half    = $in['half'] ?? '';
    $verdict = $in['verdict'] ?? '';
    if (!$obsId || !in_array($half, ['adults','chicks'], true) || !in_array($verdict, ['accepted','rejected','clear'], true)) {
        http_response_code(400); echo json_encode(['error'=>'observation_id, half and verdict required']); exit;
    }
    $note = trim((string)($in['note'] ?? ''));
    if ($verdict === 'rejected' && $note === '') { http_response_code(400); echo json_encode(['error'=>'A reason is required to reject']); exit; }

    // Colony access via the anchor observation.
    $cs = $pdo->prepare("SELECT ol.colony_id FROM observations o JOIN observation_locations ol ON ol.location_id = o.location_id WHERE o.observation_id = ?");
    $cs->execute([$obsId]);
    $cid = $cs->fetchColumn();
    if ($cid === false) { http_response_code(404); echo json_encode(['error'=>'Observation not found']); exit; }
    requireColonyAccess($pdo, $observer, (int)$cid, true);
    $oid = $observer['observer_id'];
    $now = date('Y-m-d H:i:s');
    $pref = function($p) use ($pdo, $cid) { return ($p === null || $p === '') ? null : dbPengNum($pdo, (int)$cid, (string)$p); };
    $clear = $verdict === 'clear';

    // The half's column set. Accept snapshots the detected data; reject holds a note only; clear nulls all.
    if ($half === 'adults') {
        $male = $verdict === 'accepted' ? $pref($in['male_peng_num'] ?? null) : null;
        $female = $verdict === 'accepted' ? $pref($in['female_peng_num'] ?? null) : null;
        if ($verdict === 'accepted' && $male !== null && $female !== null && $male === $female) { http_response_code(400); echo json_encode(['error'=>'A bird cannot be both parents']); exit; }
        $fields = $clear
            ? ['adults_verdict'=>null,'male_peng_num'=>null,'female_peng_num'=>null,'adults_reviewed_by'=>null,'adults_reviewed_at'=>null,'adults_note'=>null]
            : ['adults_verdict'=>$verdict,'male_peng_num'=>$male,'female_peng_num'=>$female,'adults_reviewed_by'=>$oid,'adults_reviewed_at'=>$now,'adults_note'=>$note !== '' ? $note : null];
    } else {
        $chicksJson = null;
        if ($verdict === 'accepted') {
            $list = array_values(array_filter(array_map($pref, is_array($in['chicks'] ?? null) ? $in['chicks'] : [])));
            $chicksJson = json_encode($list);
        }
        $fields = $clear
            ? ['chicks_verdict'=>null,'chicks'=>null,'dead_eggs'=>0,'dead_chicks'=>0,'fledged_unchipped'=>0,'chicks_reviewed_by'=>null,'chicks_reviewed_at'=>null,'chicks_note'=>null]
            : ['chicks_verdict'=>$verdict,'chicks'=>$chicksJson,
               'dead_eggs'=>$verdict==='accepted'?(int)($in['dead_eggs']??0):0,'dead_chicks'=>$verdict==='accepted'?(int)($in['dead_chicks']??0):0,'fledged_unchipped'=>$verdict==='accepted'?(int)($in['fledged_unchipped']??0):0,
               'chicks_reviewed_by'=>$oid,'chicks_reviewed_at'=>$now,'chicks_note'=>$note !== '' ? $note : null];
    }

    try {
        $pdo->beginTransaction();
        $ex = $pdo->prepare("SELECT * FROM breeding_verifications WHERE observation_id = ?");
        $ex->execute([$obsId]);
        $existing = $ex->fetch();
        $otherVerdict = $existing ? ($half === 'adults' ? $existing['chicks_verdict'] : $existing['adults_verdict']) : null;

        if ($clear && $existing && $otherVerdict === null) {
            wwAuditedDelete($pdo, 'breeding_verifications', (int)$existing['verification_id'], $oid, 'Cleared verification');
        } elseif ($clear && !$existing) {
            // Nothing to clear.
        } elseif ($existing) {
            wwAuditedUpdate($pdo, 'breeding_verifications', (int)$existing['verification_id'], $fields, $oid);
        } else {
            wwAuditedInsert($pdo, 'breeding_verifications', array_merge(['observation_id'=>$obsId], $fields), $oid);
        }
        $pdo->commit();
        echo json_encode(['success'=>true]);
    } catch (Exception $e) {
        if ($pdo->inTransaction()) $pdo->rollBack();
        http_response_code(400); echo json_encode(['error'=>$e->getMessage()]);
    }
    exit;
}

// The day's note: one free-text line per colony per NZ date, saved from the day view.
// Keyed by (colony_id, note_date) rather than an id, because that is how the caller knows it —
// it is looking at a day, not at a row. Blank text deletes: "no note" has one representation.
if ($action === 'save_day_note' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    $dnRole = $observer['role'] ?? 'viewer';
    if ($dnRole !== 'admin' && $dnRole !== 'editor') { http_response_code(403); echo json_encode(['error'=>'Editors only']); exit; }
    $in = json_decode(file_get_contents('php://input'), true);
    if (!is_array($in)) { http_response_code(400); echo json_encode(['error'=>'JSON body required']); exit; }
    $date = (string)($in['date'] ?? '');
    if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) { http_response_code(400); echo json_encode(['error'=>'date required (YYYY-MM-DD)']); exit; }
    $colonyId = (int)($in['colony_id'] ?? $_GET['colony_id'] ?? 1);
    requireColonyAccess($pdo, $observer, $colonyId, true);
    // Collapse whitespace so a note pasted over two lines still fits the column and reads as one line.
    $clean = fn($v, $max) => mb_substr(trim(preg_replace('/\s+/', ' ', (string)$v)), 0, $max);
    $note = $clean($in['note'] ?? '', 255);
    // observer_id/scribe_id are only touched when the caller sends them, so a client that
    // knows nothing about the people fields (posting just a note) can't blank them. '' / 0 /
    // null all mean "nobody recorded".
    $sentObserver = array_key_exists('observer_id', $in);
    $sentScribe = array_key_exists('scribe_id', $in);
    $asUserId = function ($v) use ($pdo) {
        if ($v === null || $v === '' || (int)$v === 0) return null;
        $chk = $pdo->prepare("SELECT id FROM users WHERE id = ?");
        $chk->execute([(int)$v]);
        if (!$chk->fetchColumn()) throw new RuntimeException("No such user: $v");
        return (int)$v;
    };
    $oid = $observer['observer_id'];

    try {
        $observerId = $sentObserver ? $asUserId($in['observer_id']) : null;
        $scribeId = $sentScribe ? $asUserId($in['scribe_id']) : null;

        $pdo->beginTransaction();
        $ex = $pdo->prepare("SELECT day_note_id, note, observer_id, scribe_id FROM day_notes WHERE colony_id = ? AND note_date = ?");
        $ex->execute([$colonyId, $date]);
        $row = $ex->fetch(PDO::FETCH_ASSOC);
        $existingId = $row['day_note_id'] ?? null;

        // What the row will hold once this write lands — whichever fields weren't sent keep
        // their stored value. Nothing left in any of the three means the day has nothing to
        // say: delete, so "no record for this day" has one representation.
        $finalNote     = $note;
        $finalObserver = $sentObserver ? $observerId : (isset($row['observer_id']) ? (int)$row['observer_id'] : null);
        $finalScribe = $sentScribe ? $scribeId : (isset($row['scribe_id']) ? (int)$row['scribe_id'] : null);
        $allBlank = $finalNote === '' && $finalObserver === null && $finalScribe === null;

        $fields = ['note' => $finalNote === '' ? null : $finalNote];
        if ($sentObserver) $fields['observer_id'] = $observerId;
        if ($sentScribe) $fields['scribe_id'] = $scribeId;

        if ($allBlank) {
            if ($existingId) wwAuditedDelete($pdo, 'day_notes', (int)$existingId, $oid, 'Cleared day note');
        } elseif ($existingId) {
            wwAuditedUpdate($pdo, 'day_notes', (int)$existingId, $fields, $oid);
        } else {
            // (colony_id, note_date) is UNIQUE. Two saves for the same day can be in flight at
            // once — one client double-submitting, or two people editing the same day — and the
            // loser of that race must not surface a constraint violation to a field worker.
            // Treat a duplicate as what it is: the row now exists, so update it.
            try {
                wwAuditedInsert($pdo, 'day_notes', ['colony_id'=>$colonyId, 'note_date'=>$date] + $fields, $oid);
            } catch (PDOException $e) {
                if ($e->getCode() !== '23000') throw $e;
                $again = $pdo->prepare("SELECT day_note_id FROM day_notes WHERE colony_id = ? AND note_date = ?");
                $again->execute([$colonyId, $date]);
                $raced = $again->fetchColumn();
                if (!$raced) throw $e;
                wwAuditedUpdate($pdo, 'day_notes', (int)$raced, $fields, $oid, 'Concurrent save for the same day');
            }
        }
        $pdo->commit();
        echo json_encode(['success'=>true, 'note'=>$finalNote, 'observer_id'=>$finalObserver, 'scribe_id'=>$finalScribe]);
    } catch (Exception $e) {
        if ($pdo->inTransaction()) $pdo->rollBack();
        http_response_code(400); echo json_encode(['error'=>$e->getMessage()]);
    }
    exit;
}

$table = $_GET['table'] ?? '';
$id = $_GET['id'] ?? null;

// Tables this generic endpoint exposes. Their primary keys come from the data gateway, so the
// two can't drift apart — a table here that the gateway won't write is rejected at startup.
$tables = array_intersect_key(WW_TABLE_KEYS, array_flip([
    'observations', 'penguins', 'penguin_scans', 'penguin_biometric_data', 'penguin_chips', 'observation_locations',
    // Breeding verification is written via save_verification, day notes via save_day_note (both
    // above); generic access here is for history reads only.
    'breeding_verifications', 'day_notes',
]));

if ($action === 'history') { handleHistory($pdo, $table, $id); exit; }
if ($action === 'me') { echo json_encode(['name'=>$observer['observer_name'], 'role'=>$observer['role'] ?? 'viewer', 'is_mirror'=>(defined('IS_MIRROR') && IS_MIRROR)]); exit; }

// Season field-monitoring dates — write (POST) requires auth
if ($action === 'season_fm_dates') {
    if (!wwFmDatesApply($pdo)) { http_response_code(403); echo json_encode(['error'=>'FM dates only apply to colony PT']); exit; }
    $seasonInput = $_GET['season'] ?? '';
    $season = strlen($seasonInput) === 2 ? 2000 + intval($seasonInput) : intval($seasonInput);
    if (!$season) { echo json_encode(['error' => 'season required']); exit; }
    // $canWrite isn't computed until the CRUD section below, so check the role here.
    $fmRole = $observer['role'] ?? 'viewer';
    if ($fmRole !== 'admin' && $fmRole !== 'editor') { http_response_code(403); echo json_encode(['error'=>'Write access required']); exit; }

    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input || !is_array($input)) { http_response_code(400); echo json_encode(['error'=>'JSON array required']); exit; }

    $pdo->beginTransaction();
    try {
        $count = wwAuditedReplaceSeason($pdo, $season, $input, $observer['observer_id']);
        $pdo->commit();
        echo json_encode(['success'=>true, 'count'=>$count]);
    } catch (Exception $e) {
        $pdo->rollBack();
        http_response_code(400); echo json_encode(['error'=>$e->getMessage()]);
    }
    exit;
}

if (!in_array($action, ['list','get','create','update','delete'])) { echo json_encode(['error'=>'Invalid action']); exit; }
if (!isset($tables[$table])) { echo json_encode(['error'=>'Invalid table']); exit; }

$pk = $tables[$table];
$role = $observer['role'] ?? 'viewer';

// API role: can only read penguins, read/write observation_locations
$apiWriteTables = ['observation_locations'];
$apiReadTables = ['penguins', 'penguin_chips', 'observation_locations'];

$canRead = ($role !== 'api') || in_array($table, $apiReadTables);
$canWrite = ($role === 'admin' || $role === 'editor') || ($role === 'api' && in_array($table, $apiWriteTables));

switch ($action) {
    case 'list':
    case 'get':
        if (!$canRead) { http_response_code(403); echo json_encode(['error'=>'Access denied']); break; }
        $action === 'list' ? handleList($pdo, $table) : handleGet($pdo, $table, $pk, $id);
        break;
    case 'create':
    case 'update':
    case 'delete':
        if (!$canWrite) { http_response_code(403); echo json_encode(['error'=>'Editors only']); break; }
        requireWriteColony($pdo, $observer, $table, $id, json_decode(file_get_contents('php://input'), true) ?: []);
        if ($action === 'create') handleCreate($pdo, $table, $pk, $observer);
        elseif ($action === 'update') handleUpdate($pdo, $table, $pk, $id, $observer);
        else handleDelete($pdo, $table, $pk, $id, $observer);
        break;
}

/**
 * Resolve the colony a CRUD write targets and require edit access to it. Colony-scoped
 * tables only (observations/scans/biometrics via the observation's location, and
 * observation_locations directly). Global tables (penguins, penguin_chips) aren't
 * colony-scoped, so they keep the global-role gate already applied above.
 */
function requireWriteColony($pdo, $observer, $table, $id, $input) {
    $col = function($sql, $arg) use ($pdo) { $s = $pdo->prepare($sql); $s->execute([$arg]); return $s->fetchColumn(); };
    $colonyId = null;
    if ($table === 'observation_locations') {
        $colonyId = $id ? $col("SELECT colony_id FROM observation_locations WHERE location_id = ?", $id)
                        : ($input['colony_id'] ?? null);
    } elseif ($table === 'observations') {
        $locId = $id ? $col("SELECT location_id FROM observations WHERE observation_id = ?", $id)
                     : ($input['location_id'] ?? null);
        if ($locId) $colonyId = $col("SELECT colony_id FROM observation_locations WHERE location_id = ?", $locId);
    } elseif ($table === 'penguin_scans' || $table === 'penguin_biometric_data') {
        $pk = $table === 'penguin_scans' ? 'scan_id' : 'biometric_id';
        $obsId = $id ? $col("SELECT observation_id FROM $table WHERE $pk = ?", $id)
                     : ($input['observation_id'] ?? null);
        if ($obsId) {
            $locId = $col("SELECT location_id FROM observations WHERE observation_id = ?", $obsId);
            if ($locId) $colonyId = $col("SELECT colony_id FROM observation_locations WHERE location_id = ?", $locId);
        }
    } elseif ($table === 'day_notes') {
        // Carries its colony directly — no observation to resolve through.
        $colonyId = $id ? $col("SELECT colony_id FROM day_notes WHERE day_note_id = ?", $id)
                        : ($input['colony_id'] ?? null);
    } elseif ($table === 'breeding_verifications') {
        // Anchored to an observation; resolve its colony (used by the history read path).
        $obsId = $id ? $col("SELECT observation_id FROM breeding_verifications WHERE verification_id = ?", $id)
                     : ($input['observation_id'] ?? null);
        if ($obsId) {
            $locId = $col("SELECT location_id FROM observations WHERE observation_id = ?", $obsId);
            if ($locId) $colonyId = $col("SELECT colony_id FROM observation_locations WHERE location_id = ?", $locId);
        }
    }
    if ($colonyId !== null) requireColonyAccess($pdo, $observer, (int)$colonyId, true);
}

function handleLogin($pdo) {
    $input = json_decode(file_get_contents('php://input'), true);
    $email = $input['email'] ?? '';
    $password = $input['password'] ?? '';

    $stmt = $pdo->prepare("SELECT *, id AS observer_id, f_name AS observer_name FROM users WHERE email = ?");
    $stmt->execute([$email]);
    $rows = $stmt->fetchAll();

    $observer = null;
    foreach ($rows as $row) {
        if (password_verify($password, $row['passphrase_hash'])) { $observer = $row; break; }
    }
    if (!$observer) { http_response_code(401); echo json_encode(['error'=>'Invalid credentials']); return; }

    $token = wwSessionCreate($pdo, $observer['observer_id']);

    echo json_encode([
        'token'=>$token,
        'observer_id'=>$observer['observer_id'],
        'name'=>$observer['observer_name'],
        'email'=>$observer['email'],
        'role'=>$observer['role'] ?? 'viewer',
        'expires'=>$expires
    ]);
}

function handleRegister($pdo) {
    $allowed = ['markhebberd@gmail.com', 'bdot@snotch.com'];
    $input = json_decode(file_get_contents('php://input'), true);
    $name = trim($input['name'] ?? '');
    $email = trim($input['email'] ?? '');
    $password = $input['password'] ?? '';

    if (!in_array(strtolower($email), $allowed)) {
        http_response_code(403); echo json_encode(['error'=>'Registration not allowed for this email']); return;
    }
    if (empty($name) || empty($email)) {
        http_response_code(400); echo json_encode(['error'=>'Name, email, and password required']); return;
    }
    if ($pwProblem = wwPasswordProblem($password, [$name, $email])) {
        http_response_code(400); echo json_encode(['error'=>$pwProblem]); return;
    }

    $hash = password_hash($password, PASSWORD_BCRYPT);
    try {
        // Self-signup was the one account creation that left no audit trail.
        $pdo->beginTransaction();
        $newId = wwAuditedInsertSelf($pdo, 'users',
            ['f_name' => $name, 'email' => $email, 'passphrase_hash' => $hash, 'role' => 'editor'],
            'Self-signup');
        $pdo->commit();
        echo json_encode(['success'=>true, 'observer_id'=>$newId]);
    } catch (Exception $e) {
        if ($pdo->inTransaction()) $pdo->rollBack();
        http_response_code(409); echo json_encode(['error'=>'Name or email already exists']);
    }
}

/** A repeat forgot-password request inside this window sends nothing at all. */
const RESET_EMAIL_COOLDOWN_MIN = 5;

/** Forgot password: email a 1-hour set-password link. Always answers success so the
 *  endpoint can't be used to probe which emails have accounts. */
function handleRequestPasswordReset($pdo) {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $email = trim($input['email'] ?? '');
    if ($email !== '') {
        $stmt = $pdo->prepare("SELECT *, id AS observer_id, f_name AS observer_name FROM users WHERE email = ?");
        $stmt->execute([$email]);
        foreach ($stmt->fetchAll() as $observer) {
            // Asking twice used to send a second email AND kill the first link, so whichever
            // message they opened first said "invalid or expired". Inside the cooldown we leave
            // the live link alone and send nothing: the mail already in their inbox still works.
            if (hasRecentResetLink($pdo, $observer['observer_id'])) continue;
            // Replace any outstanding reset links (invites keep their longer validity)
            wwPasswordResetsInvalidate($pdo, $observer['observer_id'], 'reset');
            sendPasswordSetupEmail($pdo, $observer, 'reset');
        }
    }
    echo json_encode(['success' => true, 'message' => 'If that email has an account, a reset link has been sent.']);
}

/** True when a reset link mailed within the cooldown is still unused and unexpired. */
function hasRecentResetLink($pdo, $observerId) {
    $stmt = $pdo->prepare("SELECT 1 FROM password_resets
        WHERE observer_id = ? AND purpose = 'reset' AND used_at IS NULL
          AND expires_at > NOW() AND created_at > DATE_SUB(NOW(), INTERVAL ? MINUTE) LIMIT 1");
    $stmt->execute([$observerId, RESET_EMAIL_COOLDOWN_MIN]);
    return (bool) $stmt->fetchColumn();
}

/** Look up a live (unused, unexpired) set-password token. */
function findResetToken($pdo, $token) {
    if ($token === '') return null;
    $stmt = $pdo->prepare("SELECT pr.*, o.f_name AS observer_name, o.email FROM password_resets pr
        JOIN users o ON o.id = pr.observer_id
        WHERE pr.token_hash = ? AND pr.used_at IS NULL AND pr.expires_at > NOW()");
    $stmt->execute([hash('sha256', $token)]);
    return $stmt->fetch() ?: null;
}

/** Validate an emailed token so the set-password page can greet the user. */
function handleCheckResetToken($pdo) {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $row = findResetToken($pdo, (string)($input['token'] ?? ''));
    if (!$row) { http_response_code(400); echo json_encode(['error' => 'This link is invalid or has expired.']); return; }
    echo json_encode(['valid' => true, 'observer_name' => $row['observer_name'], 'purpose' => $row['purpose']]);
}

/** Set a new password via an emailed token, then log the user straight in. All other
 *  sessions are dropped (a reset must lock out whoever had the old password). */
function handleResetPassword($pdo) {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
    $row = findResetToken($pdo, (string)($input['token'] ?? ''));
    $newPass = (string)($input['password'] ?? '');
    if (!$row) { http_response_code(400); echo json_encode(['error' => 'This link is invalid or has expired.']); return; }
    if ($pwProblem = wwPasswordProblem($newPass, [$row['observer_name'] ?? '', $row['email'] ?? ''])) {
        http_response_code(400); echo json_encode(['error' => $pwProblem]); return;
    }

    $hash = password_hash($newPass, PASSWORD_BCRYPT);
    // The observer is their own actor: they authenticated with the emailed token, not a session.
    wwAuditedUpdate($pdo, 'users', $row['observer_id'], ['passphrase_hash' => $hash],
        $row['observer_id'], 'Password set via ' . $row['purpose'] . ' link');
    wwPasswordResetConsume($pdo, $row['token_hash']);
    wwSessionsDeleteForObserver($pdo, $row['observer_id']);

    $stmt = $pdo->prepare("SELECT *, id AS observer_id, f_name AS observer_name FROM users WHERE id = ?");
    $stmt->execute([$row['observer_id']]);
    $observer = $stmt->fetch();
    $token = wwSessionCreate($pdo, $observer['observer_id']);
    echo json_encode([
        'token' => $token,
        'observer_id' => $observer['observer_id'],
        'name' => $observer['observer_name'],
        'email' => $observer['email'],
        'role' => $observer['role'] ?? 'viewer',
        'expires' => $expires
    ]);
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

/** Tables where a delete is a flag, not a removal. Their rows must not come back from a list. */
const WW_SOFT_DELETE_TABLES = ['observations', 'penguin_scans', 'penguin_biometric_data'];

function handleList($pdo, $table) {
    $where = []; $params = [];
    // A soft-deleted row is deleted as far as every caller is concerned. Without this the phone
    // re-downloads one on its next sync and shows a record the web already deleted — worse after
    // a cache clear, which is exactly when someone is trying to make a stale record go away.
    if (in_array($table, WW_SOFT_DELETE_TABLES) && ($_GET['include_deleted'] ?? '') !== '1')
        $where[] = "(is_deleted = FALSE OR is_deleted IS NULL)";
    foreach ($_GET as $k => $v) {
        if (in_array($k, ['action','table','colony_id','include_deleted'])) continue;
        // nz_date=YYYY-MM-DD on observations: rows whose NZ day (fixed +12,
        // matching the app's toNzDateStr bucketing) is that date.
        if ($k === 'nz_date' && $table === 'observations') {
            $dayStartUtc = strtotime($v . ' 00:00:00 UTC') - 12 * 3600;
            $where[] = "observation_time_utc >= ?"; $params[] = gmdate('Y-m-d H:i:s', $dayStartUtc);
            $where[] = "observation_time_utc < ?";  $params[] = gmdate('Y-m-d H:i:s', $dayStartUtc + 24 * 3600);
            continue;
        }
        if ($k === 'peng_num' && !preg_match('/^[A-Z]/', $v)) {
            $cid = (int)($_GET['colony_id'] ?? 1);
            $v = dbPengNum($pdo, $cid, $v);
        }
        // A caller filtering by tag may still spell it the reader's way ("LA" + 15 digits) —
        // and the row may still be stored that way, so match it to what is actually there.
        if ($k === 'pit_id') $v = wwResolvePit($pdo, $v) ?? ww_pit($v);
        $where[] = "$k = ?"; $params[] = $v;
    }
    $sql = "SELECT * FROM $table";
    if ($where) $sql .= " WHERE " . implode(' AND ', $where);
    $limit = ($table === 'observation_locations' || $table === 'penguins' || $table === 'penguin_chips' || $table === 'penguin_biometric_data' || isset($_GET['nz_date'])) ? 5000 : 100;
    $sql .= " ORDER BY 1 DESC LIMIT $limit";
    $stmt = $pdo->prepare($sql); $stmt->execute($params);
    $rows = $stmt->fetchAll();
    if (in_array($table, ['penguins', 'penguin_chips', 'penguin_biometric_data']))
        stripPengPrefix($rows, getColonyPrefix($pdo, (int)($_GET['colony_id'] ?? 1)));
    echo json_encode($rows);
}

function handleGet($pdo, $table, $pk, $id) {
    if (!$id) { echo json_encode(['error'=>'id required']); return; }
    if ($pk === 'pit_id') $id = wwResolvePit($pdo, $id) ?? $id;
    if ($table === 'penguins' && !preg_match('/^[A-Z]/', $id)) {
        $cid = (int)($_GET['colony_id'] ?? 1);
        $id = dbPengNum($pdo, $cid, $id);
    }
    $stmt = $pdo->prepare("SELECT * FROM $table WHERE $pk = ?"); $stmt->execute([$id]);
    $row = $stmt->fetch();
    if (!$row) { http_response_code(404); echo json_encode(['error'=>'Not found']); return; }
    if (isset($row['peng_num'])) $row['peng_num'] = displayPengNum($row['peng_num'], getColonyPrefix($pdo, (int)($_GET['colony_id'] ?? 1)));
    echo json_encode($row);
}

/**
 * Drop retired columns from a write payload so older clients (e.g. a nestcheck build
 * still sending removed biometric condition fields) don't error after the columns are
 * dropped from the table.
 */
function stripRetiredColumns($table, $input) {
    $retired = [
        'penguin_biometric_data' => ['condition_underweight', 'condition_dog_attacked', 'condition_attacked', 'condition_dead'],
        'penguins' => ['life_stage', 'is_dead'], // is_dead is a generated column (derived from death_date)
        'penguin_chips' => ['chip_by'], // retired: chip_by is derived from chipper_id in the snapshot
    ];
    if (isset($retired[$table]) && is_array($input)) {
        foreach ($retired[$table] as $col) unset($input[$col]);
    }
    return $input;
}

/**
 * Accept a field under the name an older client knows it by. Nestcheck ships the moulting flag
 * as condition_moulting (the name its other condition flags use); the column here has always
 * been is_moulting, so those writes failed outright and the bird stayed queued on the phone.
 * Aliasing it server-side means installed apps recover on their next sync, without a release.
 */
function renameLegacyColumns($table, $input) {
    $aliases = [
        'penguin_biometric_data' => ['condition_moulting' => 'is_moulting'],
    ];
    if (!isset($aliases[$table]) || !is_array($input)) return $input;
    foreach ($aliases[$table] as $old => $new) {
        if (array_key_exists($old, $input)) {
            if (!array_key_exists($new, $input)) $input[$new] = $input[$old];
            unset($input[$old]);
        }
    }
    return $input;
}

function handleCreate($pdo, $table, $pk, $observer) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) { http_response_code(400); echo json_encode(['error'=>'JSON body required']); return; }
    // An older client (or the web add-penguin form) may still send a chip_by acronym; capture it
    // before stripRetiredColumns drops it, so chipper_id can be derived from it below.
    $incomingChipBy = ($table === 'penguin_chips') ? ($input['chip_by'] ?? null) : null;
    // Before the duplicate checks below read pit_id: they must compare the stored form, or a
    // client sending "LA" + 15 digits looks like a new chip and gets a duplicate-key error.
    $input = wwNormalizePit($table, renameLegacyColumns($table, stripRetiredColumns($table, $input)));
    // Must come off before $input is used as the column list, or _reason becomes a column.
    $reason = $input['_reason'] ?? null;
    unset($input['_reason']);

    $cid = (int)($_GET['colony_id'] ?? 1);
    $viewPrefix = getColonyPrefix($pdo, $cid);
    $pdo->beginTransaction();
    try {
        // A scan references a chip row, so it must carry that row's pit_id exactly — resolve the
        // tag the caller sent (either form, a stale cache or an older app) to the stored one.
        if ($table === 'penguin_scans' && isset($input['pit_id'])) {
            if ($resolved = wwResolvePit($pdo, $input['pit_id'])) $input['pit_id'] = $resolved;
        }

        // Prevent duplicate penguin scans for same observation
        if ($table === 'penguin_scans' && isset($input['observation_id']) && isset($input['pit_id'])) {
            $dup = $pdo->prepare("SELECT scan_id FROM penguin_scans WHERE observation_id = ? AND pit_id = ? AND (is_deleted = FALSE OR is_deleted IS NULL)");
            $dup->execute([$input['observation_id'], $input['pit_id']]);
            if ($dup->fetch()) {
                $pdo->rollBack();
                echo json_encode(['success' => false, 'error' => 'Penguin already scanned for this observation']);
                return;
            }
        }

        // Prevent duplicate chips — including one stored in the pre-2026-08-17 prefixed form,
        // which is the same physical tag and must not become a second chip row.
        if ($table === 'penguin_chips' && isset($input['pit_id'])) {
            $dup = $pdo->prepare("SELECT peng_num FROM penguin_chips WHERE pit_id = ?");
            $dup->execute([wwResolvePit($pdo, $input['pit_id']) ?? $input['pit_id']]);
            $existing = $dup->fetch();
            if ($existing) {
                $pdo->rollBack();
                echo json_encode(['success' => false, 'error' => "pit_id already assigned to penguin #" . displayPengNum($existing['peng_num'], $viewPrefix), 'peng_num' => displayPengNum($existing['peng_num'], $viewPrefix)]);
                return;
            }
        }

        // chip_by is retired (stripped above). An older client or the web add-penguin form may have
        // sent an acronym instead of an id — derive chipper_id from it so the chipper still lands.
        if ($table === 'penguin_chips' && empty($input['chipper_id']) && !empty($incomingChipBy)) {
            $byAcr = $pdo->prepare("SELECT id FROM users WHERE chip_acronym = ?");
            $byAcr->execute([trim((string)$incomingChipBy)]);
            if ($hit = $byAcr->fetchColumn()) $input['chipper_id'] = (int)$hit;
        }

        // Auto-generate peng_num for new penguins (next number in the requested colony, or the
        // device-predicted number honoured when free — see wwResolvePengNum).
        if ($table === 'penguins' && !isset($input['peng_num'])) {
            $req = (string)($input['requested_peng_num'] ?? '');
            unset($input['requested_peng_num']);
            $input['peng_num'] = wwResolvePengNum($pdo, $cid, $req);
        }
        // New penguins are stamped with their home colony
        if ($table === 'penguins' && !isset($input['colony_id'])) {
            $input['colony_id'] = $cid;
        }
        // Prepend colony prefix to bare peng_num on penguin/chip/bio creates
        if (in_array($table, ['penguins', 'penguin_chips', 'penguin_biometric_data']) && isset($input['peng_num'])) {
            $input['peng_num'] = dbPengNum($pdo, $cid, $input['peng_num']);
        }
        $newId = wwAuditedInsert($pdo, $table, $input, $observer['observer_id'], $reason);
        // Natural-key tables (penguins, penguin_chips) have no auto-increment id to return.
        $keyCol = WW_NATURAL_KEYS[$table] ?? null;
        $recordId = ($keyCol !== null && isset($input[$keyCol])) ? $input[$keyCol] : $newId;
        $pdo->commit();
        $result = ['success'=>true, 'id'=>$recordId];
        // Return the full inserted row so callers get auto-generated fields (e.g. peng_num)
        $row = $pdo->prepare("SELECT * FROM $table WHERE $pk = ?");
        $row->execute([$recordId]);
        $inserted = $row->fetch();
        if ($inserted) {
            if (isset($inserted['peng_num'])) $inserted['peng_num'] = displayPengNum($inserted['peng_num'], $viewPrefix);
            $result = array_merge($result, $inserted);
        }
        if (isset($result['id']) && is_string($result['id'])) $result['id'] = displayPengNum($result['id'], $viewPrefix);
        echo json_encode($result);
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(400); echo json_encode(['error'=>$e->getMessage()]); }
}

function handleUpdate($pdo, $table, $pk, $id, $observer) {
    if (!$id) { http_response_code(400); echo json_encode(['error'=>'id required']); return; }
    // Prepend colony prefix for penguin-keyed tables
    if ($table === 'penguins' && !preg_match('/^[A-Z]/', $id)) {
        $cid = (int)($_GET['colony_id'] ?? 1);
        $id = dbPengNum($pdo, $cid, $id);
    }
    if ($pk === 'pit_id') $id = wwResolvePit($pdo, $id) ?? $id;
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) { http_response_code(400); echo json_encode(['error'=>'JSON body required']); return; }
    $input = wwNormalizePit($table, renameLegacyColumns($table, stripRetiredColumns($table, $input)));
    // Same as create: a body peng_num arrives display-stripped ("1039"). Written bare it matches no
    // penguin, and the FK rejects the whole update — the phone resends peng_num on every bio edit.
    if (in_array($table, ['penguins', 'penguin_chips', 'penguin_biometric_data']) && isset($input['peng_num'])) {
        $input['peng_num'] = dbPengNum($pdo, (int)($_GET['colony_id'] ?? 1), (string)$input['peng_num']);
    }

    $stmt = $pdo->prepare("SELECT * FROM $table WHERE $pk = ?"); $stmt->execute([$id]);
    $old = $stmt->fetch();
    if (!$old) { http_response_code(404); echo json_encode(['error'=>'Not found']); return; }

    $reason = $input['_reason'] ?? null;
    $fields = $input; unset($fields['_reason']);

    $pdo->beginTransaction();
    try {
        $changed = wwAuditedUpdate($pdo, $table, $id, $fields, $observer['observer_id'], $reason);
        $pdo->commit();
        echo json_encode(['success'=>true, 'changed'=>$changed]);
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(400); echo json_encode(['error'=>$e->getMessage()]); }
}

function handleDelete($pdo, $table, $pk, $id, $observer) {
    if (!$id) { http_response_code(400); echo json_encode(['error'=>'id required']); return; }
    if ($pk === 'pit_id') $id = wwResolvePit($pdo, $id) ?? $id;
    $body = json_decode(file_get_contents('php://input'), true) ?? [];
    $stmt = $pdo->prepare("SELECT * FROM $table WHERE $pk = ?"); $stmt->execute([$id]);
    $old = $stmt->fetch();
    if (!$old) { http_response_code(404); echo json_encode(['error'=>'Not found']); return; }

    $pdo->beginTransaction();
    try {
        $reason = $body['_reason'] ?? null;
        // Deleting an observation takes its scans and biometrics with it — each audited in its
        // own right, so the history names the birds that went, not just the parent row.
        if ($table === 'observations') {
            wwAuditedDeleteObservationChildren($pdo, $id, $observer['observer_id'], $reason);
        }
        wwAuditedDelete($pdo, $table, $id, $observer['observer_id'], $reason);
        $pdo->commit();
        echo json_encode(['success'=>true]);
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(400); echo json_encode(['error'=>$e->getMessage()]); }
}

function handleHistory($pdo, $table, $id) {
    if (!$table || !$id) { echo json_encode(['error'=>'table and id required']); return; }
    if ($table === 'penguin_chips') $id = wwResolvePit($pdo, $id) ?? ww_pit($id);

    if ($table === 'observations') {
        // Get scan IDs belonging to this observation (current + from audit log)
        $scanStmt = $pdo->prepare("SELECT scan_id FROM penguin_scans WHERE observation_id = ?");
        $scanStmt->execute([$id]);
        $scanIds = $scanStmt->fetchAll(PDO::FETCH_COLUMN);

        // Also find scans referenced in audit log for this observation (including ones since
        // deleted). JSON_VALUE matches whether the id was encoded as a number or a string; the
        // old LIKE '%"observation_id":123%' also matched 1230, 1234, ...
        $auditStmt = $pdo->prepare("SELECT record_id FROM audit_log
            WHERE table_name = 'penguin_scans' AND JSON_VALUE(changed_fields, '$.observation_id') = ?");
        $auditStmt->execute([(string)(int)$id]);
        foreach ($auditStmt->fetchAll(PDO::FETCH_COLUMN) as $sid) $scanIds[] = $sid;
        $scanIds = array_unique($scanIds);

        if (!empty($scanIds)) {
            $placeholders = implode(',', array_fill(0, count($scanIds), '?'));
            $stmt = $pdo->prepare("SELECT a.*, o.f_name AS observer_name FROM audit_log a JOIN users o ON a.observer_id = o.id
                WHERE (a.table_name = ? AND a.record_id = ?)
                   OR (a.table_name = 'penguin_scans' AND a.record_id IN ($placeholders))
                ORDER BY a.change_timestamp DESC LIMIT 100");
            $stmt->execute(array_merge([$table, $id], $scanIds));
        } else {
            $stmt = $pdo->prepare("SELECT a.*, o.f_name AS observer_name FROM audit_log a JOIN users o ON a.observer_id = o.id WHERE a.table_name = ? AND a.record_id = ? ORDER BY a.change_timestamp DESC LIMIT 50");
            $stmt->execute([$table, $id]);
        }

        // Enrich scan entries with penguin info
        $results = $stmt->fetchAll();
        foreach ($results as &$entry) {
            if ($entry['table_name'] === 'penguin_scans') {
                $fields = json_decode($entry['changed_fields'], true);
                // Audit rows written before the prefix was dropped still name the tag as "LA"+15.
                $pitId = $fields['pit_id'] ?? $fields['peng_num'] ?? null;
                if ($pitId) {
                    $pStmt = $pdo->prepare("SELECT p.peng_num, pc.pit_id, p.sex FROM penguin_chips pc JOIN penguins p ON pc.peng_num = p.peng_num WHERE pc.pit_id = ? OR p.peng_num = ?");
                    $pStmt->execute([ww_pit($pitId), $pitId]);
                    $penguin = $pStmt->fetch();
                    if ($penguin) $entry['penguin_info'] = $penguin;
                }
            }
        }
        echo json_encode($results);
    } else {
        $stmt = $pdo->prepare("SELECT a.*, o.f_name AS observer_name FROM audit_log a JOIN users o ON a.observer_id = o.id WHERE a.table_name = ? AND a.record_id = ? ORDER BY a.change_timestamp DESC LIMIT 50");
        $stmt->execute([$table, $id]);
        echo json_encode($stmt->fetchAll());
    }
}

function handleChangePassword($pdo, $observer) {
    $input = json_decode(file_get_contents('php://input'), true);
    $current = $input['current_password'] ?? '';
    $newPass = $input['new_password'] ?? '';

    if (!password_verify($current, $observer['passphrase_hash'])) {
        http_response_code(401); echo json_encode(['error'=>'Current password incorrect']); return;
    }
    if ($pwProblem = wwPasswordProblem($newPass, [$observer['observer_name'] ?? '', $observer['surname'] ?? '', $observer['email'] ?? ''])) {
        http_response_code(400); echo json_encode(['error'=>$pwProblem]); return;
    }

    // Changing your own password left no audit trail. The hash is redacted by the gateway.
    $hash = password_hash($newPass, PASSWORD_BCRYPT);
    $pdo->beginTransaction();
    try {
        wwAuditedUpdate($pdo, 'users', $observer['observer_id'], ['passphrase_hash' => $hash],
            $observer['observer_id'], 'Password changed by user');
        $pdo->commit();
    } catch (Exception $e) { $pdo->rollBack(); http_response_code(500); echo json_encode(['error'=>$e->getMessage()]); return; }

    echo json_encode(['success'=>true]);
}
