<?php
/**
 * Live, scoped bird detail — the data behind the Wildwatch bird panel, for ONE bird,
 * without a full sync. Returns the SAME table shape as snapshot.php (observations, scans,
 * penguins, chips, locations, biometrics, edit_counts) but scoped to the boxes this bird
 * has been seen in, so the client's queryBirdDetailInner / computeBoxFamilies produce the
 * identical panel. Used by the nestcheck WebView modal.
 *
 * GET /penguin-api/bird-detail.php?peng_num=226&colony_id=1   (Authorization: Bearer <token>)
 *
 * Scope closure (see snapshot_columns.php for why the row shape must match):
 *   L    = location_ids the bird was scanned in / chipped in (this colony)
 *   O1   = every observation in those boxes (full history, incl. deleted) — computeBoxFamilies
 *          needs the whole box, not just where this bird appears
 *   PENG = this bird + everyone chipped in L + everyone scanned in O1 (co-scans, chipped chicks)
 *   scans= all non-deleted scans in O1, plus every scan by a PENG pit anywhere in the colony
 *          (so chicks chipped here get the same scan_count/seasons the full app shows)
 */
ini_set('display_errors', 0);
set_exception_handler(function($e){ http_response_code(500); echo json_encode(['error' => $e->getMessage(), 'line' => $e->getLine()]); exit; });
set_error_handler(function($errno, $errstr, $errfile, $errline){ throw new ErrorException($errstr, 0, $errno, $errfile, $errline); });
require_once 'config.php';
require_once 'snapshot_columns.php';
header('Content-Type: application/json');
header('Cache-Control: no-cache');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Headers: Content-Type, Authorization');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }

$observer = requireAuth();
wwRequireFullPengClient();
$pdo = getDbConnection();
$colonyId = (int)($_GET['colony_id'] ?? 1);
requireColonyAccess($pdo, $observer, $colonyId);

$reqPeng = trim($_GET['peng_num'] ?? '');
if ($reqPeng === '') { http_response_code(400); echo json_encode(['error' => 'peng_num required']); exit; }
$target = dbPengNum($pdo, $colonyId, $reqPeng);  // prefixed DB primary key, e.g. "PT226"

/** Run $sql, return the first column of every row as an int array. */
$intCol = function(string $sql, array $params = []) use ($pdo): array {
    $s = $pdo->prepare($sql); $s->execute($params);
    return array_map('intval', $s->fetchAll(PDO::FETCH_COLUMN));
};
/** Run $sql, return the first column of every row as a string array. */
$strCol = function(string $sql, array $params = []) use ($pdo): array {
    $s = $pdo->prepare($sql); $s->execute($params);
    return $s->fetchAll(PDO::FETCH_COLUMN);
};
/** Placeholder list "?,?,?" for an IN clause. */
$ph = fn(array $a) => implode(',', array_fill(0, count($a), '?'));

// --- L: the bird's boxes (location_ids), this colony -----------------------------------
// From its scans, its chips' location_id, and legacy chips that only carry a box NAME.
$L = $intCol(
    "SELECT o.location_id FROM penguin_scans ps
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
       WHERE ps.pit_id IN (SELECT pit_id FROM penguin_chips WHERE peng_num = ?) AND ol.colony_id = ?
     UNION
     SELECT ol.location_id FROM penguin_chips pc
        JOIN observation_locations ol ON ol.colony_id = ?
          AND (ol.location_id = pc.location_id OR ol.location_name = pc.chip_box)
       WHERE pc.peng_num = ?",
    [$target, $colonyId, $colonyId, $target]
);

$O1 = $L ? $intCol("SELECT observation_id FROM observations WHERE location_id IN ({$ph($L)})", $L) : [];

// --- PENG: bird + chipped-in-L + scanned-in-O1 -----------------------------------------
$pengSet = [$target => true];
if ($L) {
    foreach ($strCol("SELECT DISTINCT peng_num FROM penguin_chips WHERE location_id IN ({$ph($L)})", $L) as $p) $pengSet[$p] = true;
}
if ($O1) {
    foreach ($strCol(
        "SELECT DISTINCT pc.peng_num FROM penguin_scans ps
            JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
           WHERE ps.observation_id IN ({$ph($O1)})", $O1) as $p) $pengSet[$p] = true;
}
$PENG = array_keys($pengSet);

// pit_ids of every PENG bird (for the colony-wide scan sweep below)
$pengPits = $strCol("SELECT pit_id FROM penguin_chips WHERE peng_num IN ({$ph($PENG)})", $PENG);

// --- scans: all non-deleted scans in O1, plus every PENG-pit scan in this colony --------
$scanWhere = [];
$scanParams = [];
if ($O1) { $scanWhere[] = "o.observation_id IN ({$ph($O1)})"; $scanParams = array_merge($scanParams, $O1); }
if ($pengPits) { $scanWhere[] = "ps.pit_id IN ({$ph($pengPits)})"; $scanParams = array_merge($scanParams, $pengPits); }
$scans = [];
if ($scanWhere) {
    $s = $pdo->prepare(
        "SELECT " . SNAP_COLS_SCAN . " FROM penguin_scans ps
            JOIN observations o ON ps.observation_id = o.observation_id
            JOIN observation_locations ol ON o.location_id = ol.location_id
           WHERE ol.colony_id = ? AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
             AND (" . implode(' OR ', $scanWhere) . ")");
    $s->execute(array_merge([$colonyId], $scanParams));
    $scans = $s->fetchAll();
}

// --- O: O1 (full box history) + observations reached by the PENG-pit scans -------------
$obsSet = array_fill_keys($O1, true);
foreach ($scans as $sc) $obsSet[(int)$sc['observation_id']] = true;
$O = array_keys($obsSet);

$observations = [];
if ($O) {
    $s = $pdo->prepare("SELECT " . SNAP_COLS_OBS . " FROM observations o WHERE o.observation_id IN ({$ph($O)})");
    $s->execute($O);
    $observations = $s->fetchAll();
}

// --- penguins / chips / biometrics / locations / edit_counts ---------------------------
$s = $pdo->prepare("SELECT " . SNAP_COLS_PENG . " FROM penguins WHERE peng_num IN ({$ph($PENG)})");
$s->execute($PENG); $penguins = $s->fetchAll();

$s = $pdo->prepare("SELECT " . SNAP_COLS_CHIP . " FROM penguin_chips WHERE peng_num IN ({$ph($PENG)})");
$s->execute($PENG); $chips = $s->fetchAll();

// Biometrics: only the target bird's are rendered in the panel.
$s = $pdo->prepare("SELECT " . SNAP_COLS_BIO . " FROM penguin_biometric_data WHERE peng_num = ?");
$s->execute([$target]); $biometrics = $s->fetchAll();

// Locations: the whole colony's box list (small; matches the full snapshot so name/id
// lookups behave identically).
$s = $pdo->prepare("SELECT " . SNAP_COLS_LOC . " FROM observation_locations WHERE colony_id = ?");
$s->execute([$colonyId]); $locations = $s->fetchAll();

$editCounts = [];
if ($O) {
    $s = $pdo->prepare("SELECT record_id, COUNT(*) as c FROM audit_log
        WHERE table_name = 'observations' AND action = 'UPDATE' AND record_id IN ({$ph($O)}) GROUP BY record_id");
    $s->execute($O);
    foreach ($s->fetchAll() as $row) $editCounts[(int)$row['record_id']] = (int)$row['c'];
}

echo json_encode([
    'bird_detail' => true,
    'peng_num'    => $target,
    'observations' => $observations,
    'scans'        => $scans,
    'penguins'     => $penguins,
    'chips'        => $chips,
    'locations'    => $locations,
    'biometrics'   => $biometrics,
    'edit_counts'  => $editCounts,
] + getObservers($pdo));
