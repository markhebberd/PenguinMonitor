<?php
require_once 'config.php';
header('Content-Type: application/json');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Headers: Content-Type, Authorization, X-API-Key');
header('Cache-Control: no-cache');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }
$observer = requireReadAuth();
wwRequireFullPengClient();
$pdo = getDbConnection();
$view = $_GET['view'] ?? 'overview';
$colonyId = (int)($_GET['colony_id'] ?? 1);
requireColonyAccess($pdo, $observer, $colonyId); // view access (global API key / admin = all)
switch ($view) {
    case 'timeline': handleTimeline($pdo, $colonyId); break;
    case 'box': handleBox($pdo, $colonyId, $_GET['name'] ?? ''); break;
    case 'overview': default: handleOverview($pdo, $colonyId);
}

function handleTimeline($pdo, $colonyId) {
    // One entry per NZ day, carrying that day's note. Days used to be split by
    // monitor_filename and then re-merged largest-wins; the note is per-day, so there is
    // nothing left to split on.
    $sql = "SELECT DATE(o.observation_time_utc + INTERVAL 12 HOUR) AS nz_date, ol.location_name AS box_name,
                o.adults, o.eggs, o.chicks, o.breeding_status, o.gate_status, o.notes
            FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id
            WHERE ol.colony_id = ? AND o.is_deleted = FALSE ORDER BY o.observation_time_utc ASC";
    $stmt = $pdo->prepare($sql); $stmt->execute([$colonyId]); $rows = $stmt->fetchAll();

    $noteStmt = $pdo->prepare("SELECT note_date, note FROM day_notes WHERE colony_id = ?");
    $noteStmt->execute([$colonyId]);
    $notes = array_column($noteStmt->fetchAll(), 'note', 'note_date');

    $byDate = [];
    foreach ($rows as $row) {
        $date = $row['nz_date'];
        if (!isset($byDate[$date])) $byDate[$date] = ['date' => $date, 'note' => $notes[$date] ?? null, 'boxes' => []];
        $byDate[$date]['boxes'][$row['box_name']] = ['a'=>(int)$row['adults'],'e'=>(int)$row['eggs'],'c'=>(int)$row['chicks'],'s'=>$row['breeding_status'],'g'=>$row['gate_status'],'n'=>$row['notes']];
    }
    echo json_encode(array_values($byDate));
}

function handleBox($pdo, $colonyId, $boxName) {
    if (empty($boxName)) { echo json_encode(['error'=>'name required']); return; }
    $sql = "SELECT o.observation_id, o.observation_time_utc, o.adults, o.eggs, o.chicks, o.no_scan, o.breeding_status, o.gate_status, o.notes
            FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id
            WHERE ol.colony_id = ? AND ol.location_name = ? AND o.is_deleted = FALSE ORDER BY o.observation_time_utc DESC";
    $stmt = $pdo->prepare($sql); $stmt->execute([$colonyId, $boxName]); $observations = $stmt->fetchAll();

    $obsIds = array_column($observations, 'observation_id');

    // Batch fetch edit counts
    $editCounts = [];
    if (!empty($obsIds)) {
        $ph = implode(',', array_fill(0, count($obsIds), '?'));
        $ec = $pdo->prepare("SELECT record_id, COUNT(*) as c FROM audit_log WHERE table_name = 'observations' AND action = 'UPDATE' AND record_id IN ($ph) GROUP BY record_id");
        $ec->execute(array_values($obsIds));
        foreach ($ec->fetchAll() as $row) $editCounts[$row['record_id']] = (int)$row['c'];
    }
    foreach ($observations as &$obs) {
        $obs['edit_count'] = $editCounts[$obs['observation_id']] ?? 0;
    }
    unset($obs);

    // Batch fetch all scans for all observations in one query.
    $scansByObs = [];
    if (!empty($obsIds)) {
        $ph = implode(',', array_fill(0, count($obsIds), '?'));
        $s = $pdo->prepare("SELECT ps.observation_id, ps.scan_id, ps.pit_id, pc.peng_num, p.sex, p.is_dead, p.chipped_as_adult, p.chick_size_code, pc.chip_date FROM penguin_scans ps LEFT JOIN penguin_chips pc ON ps.pit_id = pc.pit_id LEFT JOIN penguins p ON pc.peng_num = p.peng_num WHERE ps.observation_id IN ($ph) AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)");
        $s->execute(array_values($obsIds));
        foreach ($s->fetchAll() as $scan) {
            $scansByObs[$scan['observation_id']][] = $scan;
        }
    }
    foreach ($observations as &$obs) {
        $obs['scans'] = $scansByObs[$obs['observation_id']] ?? [];
    }

    $l = $pdo->prepare("SELECT location_id, location_name, persistent_notes, pit_id, latitude, longitude, accuracy FROM observation_locations WHERE colony_id = ? AND location_name = ?");
    $l->execute([$colonyId, $boxName]);

    // Build allPenguins from already-fetched scans (peng_num already stripped above)
    // + chipped-here birds
    $allPenguins = [];
    foreach ($scansByObs as $scans) {
        foreach ($scans as $scan) {
            $pnum = $scan['peng_num'] ?? null;
            if (!$pnum) continue;
            if (!isset($allPenguins[$pnum])) {
                $allPenguins[$pnum] = ['peng_num'=>$pnum, 'pit_id'=>$scan['pit_id'], 'sex'=>$scan['sex'],
                    'is_dead'=>$scan['is_dead'], 'chipped_as_adult'=>$scan['chipped_as_adult'],
                    'chick_size_code'=>$scan['chick_size_code'], 'chip_date'=>$scan['chip_date'],
                    'scan_count'=>0, 'last_seen'=>null, 'is_chipped_here'=>false];
            }
            $allPenguins[$pnum]['scan_count']++;
        }
    }
    // Add birds chipped in this box
    $chipStmt = $pdo->prepare("SELECT pc.peng_num, pc.pit_id, pc.chip_date, p.sex, p.is_dead, p.chipped_as_adult, p.chick_size_code,
            u.chip_acronym AS chip_by
        FROM penguin_chips pc JOIN penguins p ON pc.peng_num = p.peng_num
        LEFT JOIN users u ON u.id = pc.chipper_id WHERE pc.chip_box = ?");
    $chipStmt->execute([$boxName]);
    foreach ($chipStmt->fetchAll() as $c) {
        $pnum = $c['peng_num'];
        if (!isset($allPenguins[$pnum])) {
            $allPenguins[$pnum] = ['peng_num'=>$pnum, 'pit_id'=>$c['pit_id'], 'sex'=>$c['sex'],
                'is_dead'=>$c['is_dead'], 'chipped_as_adult'=>$c['chipped_as_adult'],
                'chick_size_code'=>$c['chick_size_code'], 'chip_date'=>$c['chip_date'], 'chip_by'=>$c['chip_by'],
                'scan_count'=>0, 'last_seen'=>$c['chip_date'], 'is_chipped_here'=>true];
        } else {
            $allPenguins[$pnum]['is_chipped_here'] = true;
            $allPenguins[$pnum]['chip_by'] = $c['chip_by'];
            if (empty($allPenguins[$pnum]['chip_date'])) $allPenguins[$pnum]['chip_date'] = $c['chip_date'];
        }
    }
    $allPenguins = array_values($allPenguins);

    // Count deleted observations
    $delStmt = $pdo->prepare("SELECT COUNT(*) as c FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id WHERE ol.colony_id = ? AND ol.location_name = ? AND o.is_deleted = TRUE");
    $delStmt->execute([$colonyId, $boxName]);
    $deletedCount = (int)$delStmt->fetch()['c'];

    // Optionally include deleted observations
    $deleted = [];
    if ($deletedCount > 0 && isset($_GET['include_deleted'])) {
        $delObs = $pdo->prepare("SELECT o.observation_id, o.observation_time_utc, o.adults, o.eggs, o.chicks, o.breeding_status, o.notes, o.deleted_at, ob.f_name as deleted_by_name,
            (SELECT a.change_reason FROM audit_log a WHERE a.table_name = 'observations' AND a.record_id = o.observation_id AND a.action = 'DELETE' ORDER BY a.change_timestamp DESC LIMIT 1) as delete_reason
            FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id LEFT JOIN users ob ON o.deleted_by = ob.id
            WHERE ol.colony_id = ? AND ol.location_name = ? AND o.is_deleted = TRUE ORDER BY o.observation_time_utc DESC");
        $delObs->execute([$colonyId, $boxName]);
        $deleted = $delObs->fetchAll();
    }

    echo json_encode(['location'=>$l->fetch(), 'observations'=>$observations, 'all_penguins'=>$allPenguins, 'deleted_count'=>$deletedCount, 'deleted'=>$deleted]);
}

function handleOverview($pdo, $colonyId) {
    $now = new DateTime();
    $year = (int)$now->format('n') >= 4 ? (int)$now->format('Y') : (int)$now->format('Y') - 1;
    $seasonStart = "$year-04-01 00:00:00";

    $stmt = $pdo->prepare("SELECT COUNT(*) as c FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id WHERE ol.colony_id = ? AND o.is_deleted = FALSE AND o.observation_time_utc >= ?");
    $stmt->execute([$colonyId, $seasonStart]); $seasonObs = (int)$stmt->fetch()['c'];

    $stmt = $pdo->prepare("SELECT COUNT(DISTINCT pc.peng_num) as c FROM penguin_scans ps JOIN penguin_chips pc ON ps.pit_id = pc.pit_id JOIN observations o ON ps.observation_id = o.observation_id JOIN observation_locations ol ON o.location_id = ol.location_id WHERE ol.colony_id = ? AND o.is_deleted = FALSE AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL) AND o.observation_time_utc >= ?");
    $stmt->execute([$colonyId, $seasonStart]); $seasonPenguins = (int)$stmt->fetch()['c'];

    // Latest data per box
    $sql = "SELECT ol.location_name, o.breeding_status, o.adults, o.eggs, o.chicks
            FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id
            WHERE ol.colony_id = ? AND o.is_deleted = FALSE ORDER BY o.observation_time_utc DESC";
    $stmt = $pdo->prepare($sql); $stmt->execute([$colonyId]); $rows = $stmt->fetchAll();

    $boxInfo = []; // name => {s, a, e, c}
    $statusCounts = ['BR'=>0,'CON'=>0,'POT'=>0,'UNL'=>0,'NO'=>0,'ABN'=>0,'DCM'=>0];
    $totalEggs = 0; $totalChicks = 0;

    foreach ($rows as $row) {
        $name = $row['location_name'];
        if (!isset($boxInfo[$name])) {
            $s = $row['breeding_status'] ?: '';
            $boxInfo[$name] = ['s'=>$s, 'a'=>(int)$row['adults'], 'e'=>(int)$row['eggs'], 'c'=>(int)$row['chicks']];
            if (isset($statusCounts[$s])) $statusCounts[$s]++;
            $totalEggs += (int)$row['eggs'];
            $totalChicks += (int)$row['chicks'];
        } elseif ($boxInfo[$name]['s'] === '' && $row['breeding_status']) {
            $boxInfo[$name]['s'] = $row['breeding_status'];
            $statusCounts[$row['breeding_status']] = ($statusCounts[$row['breeding_status']] ?? 0) + 1;
        }
    }

    $stmt = $pdo->prepare("SELECT COUNT(*) as c FROM observation_locations WHERE colony_id = ?");
    $stmt->execute([$colonyId]); $totalBoxes = (int)$stmt->fetch()['c'];

    // Distinct dates with activity (observations + chippings)
    $stmt = $pdo->prepare("SELECT DISTINCT d FROM (
        SELECT DATE(CONVERT_TZ(o.observation_time_utc, '+00:00', '+12:00')) as d
        FROM observations o JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE ol.colony_id = ? AND o.is_deleted = FALSE
        UNION
        SELECT pc.chip_date as d FROM penguin_chips pc
        JOIN observation_locations ol2 ON pc.location_id = ol2.location_id
        WHERE ol2.colony_id = ? AND pc.chip_date IS NOT NULL
    ) dates ORDER BY d DESC");
    $stmt->execute([$colonyId, $colonyId]);
    $dates = $stmt->fetchAll(PDO::FETCH_COLUMN);

    echo json_encode([
        'total_boxes' => $totalBoxes,
        'season_observations' => $seasonObs,
        'season_penguins' => $seasonPenguins,
        'season_start' => $seasonStart,
        'status_counts' => $statusCounts,
        'total_eggs' => $totalEggs,
        'total_chicks' => $totalChicks,
        'box_info' => $boxInfo,
        'observation_dates' => $dates,
    ]);
}
