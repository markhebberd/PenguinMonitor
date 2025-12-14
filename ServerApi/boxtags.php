<?php
/**
 * BoxTags REST API
 *
 * Endpoints:
 *   GET    /penguin-api/boxtags.php           - Get all box tags
 *   GET    /penguin-api/boxtags.php?box_id=X  - Get single box tag
 *   POST   /penguin-api/boxtags.php           - Create or update box tag (JSON body)
 *   DELETE /penguin-api/boxtags.php?box_id=X  - Delete box tag
 *
 * All requests require X-API-Key header
 */

require_once 'config.php';

setHeaders();
validateApiKey();

$method = $_SERVER['REQUEST_METHOD'];
$pdo = getDbConnection();

switch ($method) {
    case 'GET':
        handleGet($pdo);
        break;
    case 'POST':
        handlePost($pdo);
        break;
    case 'DELETE':
        handleDelete($pdo);
        break;
    default:
        http_response_code(405);
        echo json_encode(['success' => false, 'error' => 'Method not allowed']);
}

/**
 * GET - Retrieve box tags
 */
function handleGet($pdo) {
    $boxId = $_GET['box_id'] ?? null;

    if ($boxId) {
        // Get single box tag
        $stmt = $pdo->prepare("SELECT * FROM box_tags WHERE box_id = ?");
        $stmt->execute([$boxId]);
        $row = $stmt->fetch();

        if ($row) {
            echo json_encode([
                'success' => true,
                'data' => formatBoxTag($row)
            ]);
        } else {
            http_response_code(404);
            echo json_encode(['success' => false, 'error' => 'Box tag not found']);
        }
    } else {
        // Get all box tags
        $stmt = $pdo->query("SELECT * FROM box_tags ORDER BY box_id");
        $rows = $stmt->fetchAll();

        $data = [];
        foreach ($rows as $row) {
            $data[$row['box_id']] = formatBoxTag($row);
        }

        echo json_encode([
            'success' => true,
            'data' => $data,
            'count' => count($data)
        ]);
    }
}

/**
 * POST - Create or update box tag
 */
function handlePost($pdo) {
    $input = json_decode(file_get_contents('php://input'), true);

    if (!$input) {
        http_response_code(400);
        echo json_encode(['success' => false, 'error' => 'Invalid JSON body']);
        return;
    }

    // Validate required fields
    $boxId = $input['BoxID'] ?? $input['box_id'] ?? null;
    $tagNumber = $input['TagNumber'] ?? $input['tag_number'] ?? null;
    $scanTimeUtc = $input['ScanTimeUTC'] ?? $input['scan_time_utc'] ?? null;

    if (!$boxId || !$tagNumber) {
        http_response_code(400);
        echo json_encode(['success' => false, 'error' => 'BoxID and TagNumber are required']);
        return;
    }

    // Parse scan time
    if ($scanTimeUtc) {
        $scanTime = date('Y-m-d H:i:s', strtotime($scanTimeUtc));
    } else {
        $scanTime = date('Y-m-d H:i:s');
    }

    // Optional fields
    $latitude = $input['Latitude'] ?? $input['latitude'] ?? null;
    $longitude = $input['Longitude'] ?? $input['longitude'] ?? null;
    $accuracy = $input['Accuracy'] ?? $input['accuracy'] ?? null;

    // Upsert (INSERT ... ON DUPLICATE KEY UPDATE)
    $sql = "INSERT INTO box_tags (box_id, tag_number, scan_time_utc, latitude, longitude, accuracy)
            VALUES (?, ?, ?, ?, ?, ?)
            ON DUPLICATE KEY UPDATE
                tag_number = VALUES(tag_number),
                scan_time_utc = VALUES(scan_time_utc),
                latitude = VALUES(latitude),
                longitude = VALUES(longitude),
                accuracy = VALUES(accuracy)";

    $stmt = $pdo->prepare($sql);
    $stmt->execute([$boxId, $tagNumber, $scanTime, $latitude, $longitude, $accuracy]);

    echo json_encode([
        'success' => true,
        'message' => 'Box tag saved',
        'box_id' => $boxId
    ]);
}

/**
 * DELETE - Remove box tag
 */
function handleDelete($pdo) {
    $boxId = $_GET['box_id'] ?? null;

    if (!$boxId) {
        http_response_code(400);
        echo json_encode(['success' => false, 'error' => 'box_id parameter required']);
        return;
    }

    $stmt = $pdo->prepare("DELETE FROM box_tags WHERE box_id = ?");
    $stmt->execute([$boxId]);

    if ($stmt->rowCount() > 0) {
        echo json_encode([
            'success' => true,
            'message' => 'Box tag deleted',
            'box_id' => $boxId
        ]);
    } else {
        http_response_code(404);
        echo json_encode(['success' => false, 'error' => 'Box tag not found']);
    }
}

/**
 * Format database row to match Android model
 */
function formatBoxTag($row) {
    return [
        'BoxID' => $row['box_id'],
        'TagNumber' => $row['tag_number'],
        'ScanTimeUTC' => date('c', strtotime($row['scan_time_utc'])),  // ISO 8601 format
        'Latitude' => $row['latitude'] !== null ? (float)$row['latitude'] : 0,
        'Longitude' => $row['longitude'] !== null ? (float)$row['longitude'] : 0,
        'Accuracy' => $row['accuracy'] !== null ? (float)$row['accuracy'] : -1
    ];
}
?>
