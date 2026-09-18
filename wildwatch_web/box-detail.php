<?php
/**
 * Live, scoped box detail — the data behind the Wildwatch box panel (breeding history +
 * observations), for ONE box, without a full sync. Returns the SAME table shape as
 * snapshot.php, scoped to this box, so the client's queryBoxDetailInner / computeBoxFamilies
 * produce the identical panel. Used by the nestcheck box-panel WebView modal.
 *
 * GET /penguin-api/box-detail.php?box=5&colony_id=1   (Authorization: Bearer <token>)
 *
 * Scope closure — MIRRORS bird-detail.php (keep the two in sync; the shared row shape lives in
 * snapshot_columns.php). Here L is the single box:
 *   L    = this box's location_id
 *   O1   = every observation in the box (full history, incl. deleted) — computeBoxFamilies needs it
 *   PENG = everyone chipped in L + everyone scanned in O1 (co-scans, chipped chicks)
 *   scans= all non-deleted scans in O1, plus every scan by a PENG pit anywhere in the colony
 *          (so a chick's colony-wide hasReturned / scan counts match the full app)
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

$reqBox = trim($_GET['box'] ?? '');
if ($reqBox === '') { http_response_code(400); echo json_encode(['error' => 'box required']); exit; }

$intCol = function(string $sql, array $params = []) use ($pdo): array {
    $s = $pdo->prepare($sql); $s->execute($params);
    return array_map('intval', $s->fetchAll(PDO::FETCH_COLUMN));
};
$strCol = function(string $sql, array $params = []) use ($pdo): array {
    $s = $pdo->prepare($sql); $s->execute($params);
    return $s->fetchAll(PDO::FETCH_COLUMN);
};
$ph = fn(array $a) => implode(',', array_fill(0, count($a), '?'));

// L = this box's location_id in this colony.
$L = $intCol("SELECT location_id FROM observation_locations WHERE colony_id = ? AND location_name = ?", [$colonyId, $reqBox]);
$O1 = $L ? $intCol("SELECT observation_id FROM observations WHERE location_id IN ({$ph($L)})", $L) : [];

// PENG: chipped in L + scanned in O1.
$pengSet = [];
if ($L)  foreach ($strCol("SELECT DISTINCT peng_num FROM penguin_chips WHERE location_id IN ({$ph($L)})", $L) as $p) $pengSet[$p] = true;
if ($O1) foreach ($strCol("SELECT DISTINCT pc.peng_num FROM penguin_scans ps JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
                             WHERE ps.observation_id IN ({$ph($O1)})", $O1) as $p) $pengSet[$p] = true;
$PENG = array_keys($pengSet);

$pengPits = $PENG ? $strCol("SELECT pit_id FROM penguin_chips WHERE peng_num IN ({$ph($PENG)})", $PENG) : [];

// scans: all non-deleted scans in O1, plus every PENG-pit scan in this colony.
$scanWhere = []; $scanParams = [];
if ($O1)       { $scanWhere[] = "o.observation_id IN ({$ph($O1)})";  $scanParams = array_merge($scanParams, $O1); }
if ($pengPits) { $scanWhere[] = "ps.pit_id IN ({$ph($pengPits)})";   $scanParams = array_merge($scanParams, $pengPits); }
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

// O = O1 (full box history) + observations reached by the PENG-pit scans.
$obsSet = array_fill_keys($O1, true);
foreach ($scans as $sc) $obsSet[(int)$sc['observation_id']] = true;
$O = array_keys($obsSet);

$observations = [];
if ($O) { $s = $pdo->prepare("SELECT " . SNAP_COLS_OBS . " FROM observations o WHERE o.observation_id IN ({$ph($O)})"); $s->execute($O); $observations = $s->fetchAll(); }
$penguins = [];
if ($PENG) { $s = $pdo->prepare("SELECT " . SNAP_COLS_PENG . " FROM penguins WHERE peng_num IN ({$ph($PENG)})"); $s->execute($PENG); $penguins = $s->fetchAll(); }
$chips = [];
if ($PENG) { $s = $pdo->prepare("SELECT " . SNAP_COLS_CHIP . " FROM penguin_chips WHERE peng_num IN ({$ph($PENG)})"); $s->execute($PENG); $chips = $s->fetchAll(); }

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
    'box_detail'   => true,
    'box'          => $reqBox,
    'observations' => $observations,
    'scans'        => $scans,
    'penguins'     => $penguins,
    'chips'        => $chips,
    'locations'    => $locations,
    'biometrics'   => [],
    'edit_counts'  => $editCounts,
] + getObservers($pdo));
