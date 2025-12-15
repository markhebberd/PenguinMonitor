<?php
/**
 * Database Configuration for BoxTags API
 *
 * INSTRUCTIONS:
 * 1. Copy this file to your cPanel server at: public_html/penguin-api/config.php
 * 2. Update the database credentials below
 * 3. Generate a secure API key (32+ characters) and set it below
 * 4. Ensure this file is NOT accessible directly from web (or move outside public_html)
 */

// Database credentials - UPDATE THESE
define('DB_HOST', 'localhost');
define('DB_NAME', 'wildwatch_nestcheck');
define('DB_USER', 'wildwatch_nestcheck_api');       
define('DB_PASS', 'notARealPassword');             // see public_html/penguin-api/config.php for this value or to reset it. 

// see public_html/penguin-api/config.php for this value or to reset it. 
define('API_KEY', 'notARealPassword');

// CORS settings (adjust for production)
define('ALLOWED_ORIGIN', '*');  // In production, set to your specific domain

/**
 * Get database connection with retry logic for shared hosting
 *
 * @param int $attemptsRemaining Number of retry attempts remaining
 * @return PDO Database connection
 */
function getDbConnection($attemptsRemaining = 4) {
    try {
        $pdo = new PDO(
            "mysql:host=" . DB_HOST . ";dbname=" . DB_NAME . ";charset=utf8mb4",
            DB_USER,
            DB_PASS,
            [
                PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
                PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
                PDO::ATTR_EMULATE_PREPARES => false,
                // Shared hosting optimizations
                PDO::ATTR_PERSISTENT => false,  // Avoid stale connections on shared hosting
                PDO::ATTR_TIMEOUT => 5,          // Connection timeout (5 seconds)
                PDO::MYSQL_ATTR_INIT_COMMAND => "SET SESSION wait_timeout=30"  // Keep session alive
            ]
        );
        return $pdo;
    } catch (PDOException $e) {
        // Retry if MySQL connection timed out (common on cheap shared hosting)
        if ($attemptsRemaining > 0 && (
            strpos($e->getMessage(), 'gone away') !== false ||
            strpos($e->getMessage(), 'timeout') !== false ||
            strpos($e->getMessage(), 'Lost connection') !== false
        )) {
            $attemptNumber = 5 - $attemptsRemaining;
            error_log("Database connection attempt {$attemptNumber} failed, retrying... ({$attemptsRemaining} attempts remaining)");
            usleep(500000);  // Wait 500ms before retry
            return getDbConnection($attemptsRemaining - 1);
        }

        // Log the actual error for debugging (visible in PHP error logs)
        error_log("Database connection failed after all retries: " . $e->getMessage());

        http_response_code(500);
        echo json_encode(['success' => false, 'error' => 'Database connection failed']);
        exit;
    }
}

/**
 * Validate API key from request header
 */
function validateApiKey() {
    $headers = getallheaders();
    $apiKey = $headers['X-API-Key'] ?? $headers['x-api-key'] ?? '';

    if ($apiKey !== API_KEY) {
        http_response_code(401);
        echo json_encode(['success' => false, 'error' => 'Invalid API key']);
        exit;
    }
}

/**
 * Set CORS and JSON headers
 */
function setHeaders() {
    header('Content-Type: application/json');
    header('Access-Control-Allow-Origin: ' . ALLOWED_ORIGIN);
    header('Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS');
    header('Access-Control-Allow-Headers: Content-Type, X-API-Key');

    // Handle preflight requests
    if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
        http_response_code(200);
        exit;
    }
}
?>
