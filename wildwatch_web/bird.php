<?php
require_once 'config.php';
header('Content-Type: application/json');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Headers: Content-Type, Authorization');
header('Cache-Control: no-cache');
if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') { http_response_code(200); exit; }
requireAuth();
wwRequireFullPengClient();

$pdo = getDbConnection();
$colonyId = (int)($_GET['colony_id'] ?? 1);
$viewPrefix = getColonyPrefix($pdo, $colonyId);
$num = $_GET['num'] ?? '';
$tag = $_GET['tag'] ?? '';
if (empty($num) && empty($tag)) { echo json_encode(['error'=>'num or tag required']); exit; }

if (!empty($num)) {
    // Exact match only: prefixed input as-is ("NI7" from anywhere), or a bare
    // number resolved within the viewing colony ("706" → "PT706"). No fuzzy
    // suffix matching — a wrong-colony guess is worse than not-found.
    $stmt = $pdo->prepare("SELECT * FROM penguins WHERE peng_num = ? OR peng_num = ? LIMIT 1");
    $stmt->execute([$num, $viewPrefix . $num]);
    $penguin = $stmt->fetch();
} else {
    $stmt = $pdo->prepare("SELECT p.* FROM penguins p JOIN penguin_chips pc ON p.peng_num = pc.peng_num WHERE pc.pit_id = ? OR pc.pit_id LIKE ?");
    $stmt->execute([$tag, '%'.$tag]);
    $penguin = $stmt->fetch();
}
if (!$penguin) { echo json_encode(['error'=>'penguin not found']); exit; }

$pid = $penguin['peng_num'];

// Chips
$chipsStmt = $pdo->prepare("SELECT pc.pit_id, pc.chip_date, pc.is_active, pc.chip_box, pc.solo,
        u.chip_acronym AS chip_by
    FROM penguin_chips pc LEFT JOIN users u ON u.id = pc.chipper_id
    WHERE pc.peng_num = ? ORDER BY pc.chip_date");
$chipsStmt->execute([$pid]);
$penguin['chips'] = $chipsStmt->fetchAll();

// Biometrics (fast — no observation joins)
$stmt = $pdo->prepare("SELECT * FROM penguin_biometric_data WHERE peng_num = ? ORDER BY observation_date DESC");
$stmt->execute([$pid]);
$biometrics = $stmt->fetchAll();

// Quick mode: return penguin + chips + biometrics only (no sightings/partners)
if (isset($_GET['quick'])) {
    echo json_encode([
        'penguin' => $penguin,
        'biometrics' => $biometrics,
        'sightings' => [],
        'partners' => [],
        'breeding_stats' => [],
    ]);
    exit;
}

// Unified sightings (shared function)
$result = getSightings($pdo, $pid, null, $colonyId);
$sightings = $result['sightings'];

// Partners (from sightings seen_with, with full observation context)
$partners = [];
foreach ($sightings as $s) {
    foreach ($s['seen_with'] as $sw) {
        $pnum = $sw['peng_num'];
        if (!isset($partners[$pnum])) {
            $partners[$pnum] = ['peng_num'=>$pnum, 'pit_id'=>$sw['pit_id'], 'sex'=>$sw['sex'],
                'chipped_as_adult'=>$sw['chipped_as_adult'], 'chip_date'=>$sw['chip_date'],
                'chick_size_code'=>$sw['chick_size_code'] ?? null,
                'sightings'=>[]];
        }
        // Include full observation context + other penguins seen
        $others = array_filter($s['seen_with'], function($o) use ($pnum) { return $o['peng_num'] !== $pnum; });
        $partners[$pnum]['sightings'][] = [
            'box'=>$s['box'], 'date'=>$s['date'],
            'adults'=>$s['adults'], 'eggs'=>$s['eggs'], 'chicks'=>$s['chicks'],
            'breeding_status'=>$s['breeding_status'], 'notes'=>$s['notes'],
            'also_seen'=>array_values($others),
        ];
    }
}
usort($partners, function($a,$b){ return count($b['sightings'])-count($a['sightings']); });

// Breeding stats from sightings
$breedingStats = [];
foreach ($sightings as $s) {
    $d = new DateTime($s['date']);
    $m = (int)$d->format('n');
    $y = (int)$d->format('Y');
    $seasonYear = $m >= 4 ? $y : $y - 1;
    $season = $seasonYear . '/' . substr($seasonYear + 1, -2);

    if (!isset($breedingStats[$season])) {
        $breedingStats[$season] = ['season'=>$season, 'scans'=>0, 'boxes'=>[], 'max_eggs'=>0, 'max_chicks'=>0, 'statuses'=>[]];
    }
    $bs = &$breedingStats[$season];
    $bs['scans']++;
    if (!in_array($s['box'], $bs['boxes'])) $bs['boxes'][] = $s['box'];
    if ($s['eggs'] > $bs['max_eggs']) $bs['max_eggs'] = $s['eggs'];
    if ($s['chicks'] > $bs['max_chicks']) $bs['max_chicks'] = $s['chicks'];
    if ($s['breeding_status'] && !in_array($s['breeding_status'], $bs['statuses'])) $bs['statuses'][] = $s['breeding_status'];
}
krsort($breedingStats);

echo json_encode([
    'penguin' => $penguin,
    'sightings' => $sightings,
    'biometrics' => $biometrics,
    'partners' => array_values($partners),
    'breeding_stats' => array_values($breedingStats),
]);
