<?php
require_once 'config.php';
setHeaders();
$observer = requireAuth();
wwRequireFullPengClient();

$pdo = getDbConnection();
$colonyId = (int)($_GET['colony_id'] ?? 1);
requireColonyAccess($pdo, $observer, $colonyId); // view access
$date = $_GET['date'] ?? '';
if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) {
    echo json_encode(['error' => 'date required (YYYY-MM-DD)']);
    exit;
}

// NZ date D covers UTC range: (D-1)T12:00 to DT12:00 — fixed +12 (NZST), matching the
// app's date bucketing so an observation appears on exactly one day
$utcStart = date('Y-m-d 12:00:00', strtotime($date) - 86400);
$utcEnd = date('Y-m-d 12:00:00', strtotime($date));
$stmt = $pdo->prepare("SELECT o.observation_id, o.observation_time_utc, o.adults, o.eggs, o.chicks,
    o.breeding_status, o.gate_status, o.notes, o.observer_id,
    ol.location_name AS box_name, ob.f_name AS observer_name
    FROM observations o
    JOIN observation_locations ol ON o.location_id = ol.location_id
    LEFT JOIN users ob ON o.observer_id = ob.id
    WHERE o.observation_time_utc >= ? AND o.observation_time_utc < ?
    AND o.is_deleted = FALSE
    ORDER BY ol.location_name + 0, o.observation_time_utc");
$stmt->execute([$utcStart, $utcEnd]);
$observations = $stmt->fetchAll();

// Scans for these observations
$obsIds = array_column($observations, 'observation_id');
$scans = [];
if (!empty($obsIds)) {
    $ph = implode(',', array_fill(0, count($obsIds), '?'));
    $stmt = $pdo->prepare("SELECT ps.observation_id, pc.peng_num, ps.pit_id, p.sex, p.chipped_as_adult, pc.chip_date, p.chick_size_code
        FROM penguin_scans ps
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
        JOIN penguins p ON pc.peng_num = p.peng_num
        WHERE ps.observation_id IN ($ph) AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
        GROUP BY ps.observation_id, pc.peng_num");
    $stmt->execute(array_values($obsIds));
    foreach ($stmt->fetchAll() as $row) {
        $scans[$row['observation_id']][] = $row;
    }
}

// Attach scans to observations
foreach ($observations as &$obs) {
    $obs['scans'] = $scans[$obs['observation_id']] ?? [];
}

// Chippings on this date
$stmt = $pdo->prepare("SELECT pc.pit_id, pc.peng_num, pc.chip_box, p.sex, p.chipped_as_adult, p.chick_size_code,
    u.chip_acronym AS chip_by
    FROM penguin_chips pc
    JOIN penguins p ON pc.peng_num = p.peng_num
    LEFT JOIN users u ON u.id = pc.chipper_id
    WHERE pc.chip_date = ?
    ORDER BY pc.chip_box + 0");
$stmt->execute([$date]);
$chippings = $stmt->fetchAll();

// The day's note and who was out — one row for this colony on this date, or none.
$noteStmt = $pdo->prepare("SELECT note, observer_id, scribe_id FROM day_notes WHERE colony_id = ? AND note_date = ?");
$noteStmt->execute([$colonyId, $date]);
$dayRow = $noteStmt->fetch(PDO::FETCH_ASSOC) ?: [];

echo json_encode([
    'date' => $date,
    'day_note' => $dayRow['note'] ?? null,
    'day_observer_id' => isset($dayRow['observer_id']) ? (int)$dayRow['observer_id'] : null,
    'day_scribe_id' => isset($dayRow['scribe_id']) ? (int)$dayRow['scribe_id'] : null,
    'observations' => $observations,
    'chippings' => $chippings,
]);
