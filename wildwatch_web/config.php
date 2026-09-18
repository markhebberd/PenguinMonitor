<?php
/**
 * Shared config + helpers for the BoxTags/Wildwatch API.
 *
 * Real credentials (DB_*, API_KEY) live in secrets.php, which is git-ignored.
 * On a new server: copy secrets.php.sample -> secrets.php and fill in real values.
 */

require_once __DIR__ . '/secrets.php';   // defines DB_HOST, DB_NAME, DB_USER, DB_PASS, API_KEY

// This server is a read-only backup MIRROR only if its secrets.php declares it. Production
// never defines IS_MIRROR, so it defaults to false. Used to expose the backup-status admin
// view (status.php + the "Backup" admin tab) only on the mirror.
if (!defined('IS_MIRROR')) define('IS_MIRROR', false);

// CORS settings (adjust for production)
define('ALLOWED_ORIGIN', '*');  // In production, set to your specific domain

// ============ Colony penguin number prefix ============
// Penguins are numbered per-colony with a 2-4 letter prefix (e.g. PT319, NI1); the
// prefixed value is the DB primary key, and penguins.colony_id records the home
// colony. Bare numbers are the PT (Tarakohe) standard: only PT's prefix is stripped
// for display when viewing PT ("PT319" → "319"). All other colonies keep their
// prefix even at home (NI birds always show "NI1"), so a bare number always means
// a PT bird and NI706 never collides with PT's 706.
const BARE_NUMBER_PREFIXES = ['PT'];

/** Strip the viewing colony's prefix for display, but only for colonies whose local
 *  standard is bare numbers (PT). Everything else keeps its prefix. */
function displayPengNum(string $pengNum, string $viewPrefix): string {
    if (!in_array($viewPrefix, BARE_NUMBER_PREFIXES, true)) return $pengNum;
    $len = strlen($viewPrefix);
    if ($len > 0 && strncmp($pengNum, $viewPrefix, $len) === 0 && ctype_digit(substr($pengNum, $len))) {
        return substr($pengNum, $len);
    }
    return $pengNum;
}

/** Get colony prefix for a colony_id. Cached per-request. */
function getColonyPrefix($pdo, int $colonyId): string {
    static $cache = [];
    if (!isset($cache[$colonyId])) {
        $stmt = $pdo->prepare("SELECT colony_prefix FROM colonies WHERE colony_id = ?");
        $stmt->execute([$colonyId]);
        $cache[$colonyId] = $stmt->fetchColumn() ?: '';
    }
    return $cache[$colonyId];
}

/** Prepend colony prefix to a bare peng_num (e.g. "319" → "PT319"). Skips if already prefixed. */
function dbPengNum($pdo, int $colonyId, string $pengNum): string {
    if (preg_match('/^[A-Z]{2,4}/', $pengNum)) return $pengNum; // already prefixed
    return getColonyPrefix($pdo, $colonyId) . $pengNum;
}

/**
 * Every endpoint that carries bird numbers speaks them in full ("PT1039"), in and out; only the
 * clients shorten them, and only on screen. Clients built before that change still expect the
 * viewing colony's prefix stripped and would send bare numbers back, so they are turned away
 * with a message to update rather than half-working. Call after authentication, so an
 * unauthenticated probe (deploy/mirror health checks) still gets its 401.
 */
function wwRequireFullPengClient(): void {
    if (($_SERVER['HTTP_X_PENG_FORMAT'] ?? '') === 'full') return;
    http_response_code(426);
    echo json_encode(['error' => 'This app version is out of date — update NestCheck from the Play Store (or reload wildwatch) to keep syncing. Nothing on this device has been lost.']);
    exit;
}

/** A bird number a client is WRITING: must be the full stored form. A bare one is refused —
 *  guessing its colony is how a Ngawhiti bird once got filed under Tarakohe's number. */
function wwFullPengNum($pengNum): string {
    $p = strtoupper(trim((string)$pengNum));
    if (!preg_match('/^[A-Z]{2,4}\d+$/', $p)) {
        http_response_code(400);
        echo json_encode(['error' => "Bird number '$pengNum' must include its colony prefix (e.g. PT1039)"]);
        exit;
    }
    return $p;
}


/**
 * The stored form of a PIT/tag number: the 15 digits of the ISO tag, nothing else.
 *
 * Readers (the HR5) hand over the tag as "LA" + 15 digits, and every row written before
 * 2026-08-17 was stored that way. But those two letters are the reader's manufacturer
 * code, not part of the ISO number — they are not on the chip label, so a person entering
 * a tag by hand cannot know them and used to have to invent them. The digits are the
 * identity; the prefix is transport.
 *
 * Every write path normalizes through here, so a field app still sending the "LA" form
 * (any version, indefinitely) lands the same row as a new one. Anything that isn't a
 * recognisable tag is passed through untouched rather than mangled — NOSCAN placeholders
 * and hand-typed oddities must survive to be seen, not be silently rewritten.
 */
function ww_pit($v) {
    if ($v === null) return null;
    $s = strtoupper(preg_replace('/[^A-Za-z0-9]/', '', (string)$v));
    if (preg_match('/^[A-Z]+(\d{15})$/', $s, $m)) return $m[1];
    return preg_match('/^\d{15}$/', $s) ? $s : $v;
}

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
                PDO::MYSQL_ATTR_INIT_COMMAND => "SET SESSION wait_timeout=30, time_zone='+00:00'"  // Keep session alive, force UTC
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
 * Read-only DB connection for the admin SQL console. Uses a MySQL user granted
 * only SELECT (see secrets.php), so it physically cannot write, drop, or write
 * files regardless of the query — the security boundary is the grant, not any
 * string checks upstream.
 */
function getReadOnlyDbConnection($attemptsRemaining = 2) {
    try {
        $pdo = new PDO(
            "mysql:host=" . DB_HOST . ";dbname=" . DB_NAME . ";charset=utf8mb4",
            DB_RO_USER,
            DB_RO_PASS,
            [
                PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
                PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
                PDO::ATTR_EMULATE_PREPARES => false,
                PDO::ATTR_PERSISTENT => false,
                PDO::ATTR_TIMEOUT => 5,
                PDO::MYSQL_ATTR_INIT_COMMAND => "SET SESSION wait_timeout=30, time_zone='+00:00'"
            ]
        );
        // Best-effort per-query time cap so a runaway console query self-kills.
        // MariaDB: max_statement_time (seconds). MySQL: max_execution_time (ms).
        try { $pdo->exec("SET SESSION max_statement_time=15"); }
        catch (PDOException $e) { try { $pdo->exec("SET SESSION max_execution_time=15000"); } catch (PDOException $e2) {} }
        return $pdo;
    } catch (PDOException $e) {
        if ($attemptsRemaining > 0 && (
            strpos($e->getMessage(), 'gone away') !== false ||
            strpos($e->getMessage(), 'timeout') !== false ||
            strpos($e->getMessage(), 'Lost connection') !== false
        )) {
            usleep(300000);
            return getReadOnlyDbConnection($attemptsRemaining - 1);
        }
        error_log("Read-only DB connection failed: " . $e->getMessage());
        http_response_code(500);
        echo json_encode(['success' => false, 'error' => 'Read-only database connection failed']);
        exit;
    }
}

/** Sliding session: extend a valid token to 30 days from now, at most once per day. */
function touchSession($pdo, $token) {
    wwSessionTouch($pdo, $token);
}

/**
 * Require auth for read-only endpoints. Accepts Bearer token, API key, or
 * observer api_key (GET only). Returns observer row or true.
 * Used by penguins.php and boxtags.php for legacy app (v37) compatibility.
 */
function requireReadAuth($pdo = null) {
    if (!$pdo) $pdo = getDbConnection();
    // Bearer token — full access
    $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
    if (preg_match('/^Bearer\s+(.+)$/i', $header, $m)) {
        $stmt = $pdo->prepare("SELECT o.*, o.id AS observer_id, o.f_name AS observer_name FROM sessions s JOIN users o ON s.observer_id = o.id WHERE s.token = ? AND s.expires_at > NOW()");
        $stmt->execute([$m[1]]);
        $result = $stmt->fetch();
        if ($result) { touchSession($pdo, $m[1]); return $result; }
    }
    // API key — read-only (GET requests only)
    if ($_SERVER['REQUEST_METHOD'] === 'GET') {
        // Case-insensitive header lookup: php-fpm's getallheaders() normalises the
        // name to 'X-Api-Key', whereas Apache preserved the client's 'X-API-Key'.
        $headers = array_change_key_case(getallheaders(), CASE_LOWER);
        $apiKey = $headers['x-api-key'] ?? '';
        if (!empty($apiKey)) {
            // Check global API key
            if ($apiKey === API_KEY) return true;
            // Check per-observer api_key (legacy app uses this for boxtags)
            $stmt = $pdo->prepare("SELECT *, id AS observer_id, f_name AS observer_name FROM users WHERE api_key = ?");
            $stmt->execute([$apiKey]);
            $result = $stmt->fetch();
            if ($result) return $result;
        }
    }
    http_response_code(401);
    echo json_encode(['success' => false, 'error' => 'Authentication required']);
    exit;
}

/**
 * Require session-based auth (Bearer token). Returns observer row or exits 401.
 */
function requireAuth($pdo = null) {
    if (!$pdo) $pdo = getDbConnection();
    $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
    if (preg_match('/^Bearer\s+(.+)$/i', $header, $m)) {
        $stmt = $pdo->prepare("SELECT o.*, o.id AS observer_id, o.f_name AS observer_name FROM sessions s JOIN users o ON s.observer_id = o.id WHERE s.token = ? AND s.expires_at > NOW()");
        $stmt->execute([$m[1]]);
        $result = $stmt->fetch();
        if ($result) { touchSession($pdo, $m[1]); return $result; }
    }
    http_response_code(401);
    echo json_encode(['success' => false, 'error' => 'Authentication required']);
    exit;
}

/**
 * Per-colony access check. Global 'admin' has full access to every colony; everyone
 * else needs a colony_permissions row for that colony. Within a granted colony,
 * role 'edit' (or 'admin') may write, 'view' is read-only.
 *
 * Returns the effective per-colony role ('admin' | 'edit' | 'view'), or sends 403 + exits.
 */
/**
 * The colony a write belongs to, from ?colony_id= (or the body's colony_id). A write that decides
 * where data lands — box by name, bird number prefix, day note, FM dates — must say which colony;
 * defaulting a missing one to Port Tarakohe once filed a Ngawhiti N4 as a new box there.
 */
function wwRequireColonyId(?array $body = null): int {
    $cid = (int)($body['colony_id'] ?? $_GET['colony_id'] ?? 0);
    if ($cid <= 0) { http_response_code(400); echo json_encode(['error' => 'colony_id required']); exit; }
    return $cid;
}

function requireColonyAccess($pdo, $observer, $colonyId, $needWrite = false) {
    // Global API key (requireReadAuth returns `true`, not an observer row) is the trusted
    // app identity used by nestcheck — treat it as full access so field sync never breaks.
    if (!is_array($observer)) return 'admin';
    if (($observer['role'] ?? '') === 'admin') return 'admin';   // app admin: all colonies, read+write
    $stmt = $pdo->prepare("SELECT role FROM colony_permissions WHERE colony_id = ? AND observer_id = ?");
    $stmt->execute([$colonyId, $observer['observer_id']]);
    $role = $stmt->fetchColumn();
    if ($role === false) {
        http_response_code(403);
        echo json_encode(['success' => false, 'error' => 'No access to this colony']);
        exit;
    }
    if ($needWrite && !in_array($role, ['edit', 'admin'], true)) {
        http_response_code(403);
        echo json_encode(['success' => false, 'error' => 'View-only access to this colony']);
        exit;
    }
    return $role;
}

/**
 * Record a server free-space sample into disk_history (auto-creates the table,
 * prunes samples older than 400 days). Returns free MB, or null on failure.
 * $dir selects the filesystem to measure (defaults to this api directory,
 * matching server_stats.php / disk_check.php).
 */
function recordDiskSample($pdo, $dir = null) {
    if ($dir === null) $dir = __DIR__;
    $pdo->exec("CREATE TABLE IF NOT EXISTS disk_history (
        id INT AUTO_INCREMENT PRIMARY KEY,
        recorded_at DATETIME NOT NULL,
        disk_free_mb INT NOT NULL,
        INDEX idx_recorded_at (recorded_at)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    $free = @disk_free_space($dir);
    if ($free === false) return null;
    $freeMb = (int)round($free / 1048576);
    wwDiskHistoryRecord($pdo, $freeMb);
    return $freeMb;
}

/**
 * Simple HTML mail from the send-only no-reply account (rspamd DKIM-signs it,
 * Postfix relays out — same path as the disk alert).
 */
function sendWildwatchMail($to, $subject, $html) {
    $from = 'no-reply@wildwatch.co.nz';
    $headers = "From: Wildwatch <$from>\r\n"
        . "Reply-To: $from\r\n"
        . "MIME-Version: 1.0\r\n"
        . "Content-Type: text/html; charset=UTF-8";
    // Raw non-ASCII in a header (e.g. the em-dash in the invite subject) makes Postfix require
    // SMTPUTF8, which plenty of receiving MTAs don't offer — so the message BOUNCES and never
    // arrives. RFC 2047-encode the subject to keep the header pure ASCII.
    return @mail($to, wwEncodeMailHeader($subject), $html, $headers, "-f$from");
}

/** RFC 2047 encoded-word for a header value that contains non-ASCII; pass ASCII through. */
function wwEncodeMailHeader($s) {
    return preg_match('/[\x80-\xFF]/', $s) ? '=?UTF-8?B?' . base64_encode($s) . '?=' : $s;
}

/**
 * Create a one-time set-password token for an observer and return the RAW token
 * (only the sha256 is stored). $purpose: 'invite' (new user) or 'reset'.
 */
function createPasswordResetToken($pdo, $observerId, $purpose, $ttlSeconds) {
    return wwPasswordResetCreate($pdo, $observerId, $purpose, $ttlSeconds);
}

/**
 * Email an observer a set-password link. $purpose 'invite' welcomes a new user
 * (7-day link); 'reset' is the forgot-password flow (1-hour link).
 * Returns true when the mail was accepted for delivery.
 */
function sendPasswordSetupEmail($pdo, $observer, $purpose) {
    if (empty($observer['email'])) return false;
    $ttl = $purpose === 'invite' ? 7 * 86400 : 3600;
    $token = createPasswordResetToken($pdo, $observer['observer_id'], $purpose, $ttl);
    $link = 'https://wildwatch.co.nz/?setpw=' . $token;
    $name = htmlspecialchars($observer['observer_name']);
    if ($purpose === 'invite') {
        $subject = 'Welcome to Wildwatch — set your password';
        $intro = "An account has been created for you on <b>Wildwatch</b> (wildwatch.co.nz), the penguin colony monitoring site.";
        $validity = 'This link is valid for 7 days.';
    } else {
        $subject = 'Wildwatch password reset';
        $intro = "A password reset was requested for your <b>Wildwatch</b> account. If this wasn't you, you can ignore this email — your password is unchanged.";
        $validity = 'This link is valid for 1 hour.';
    }
    $html = "<div style=\"font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:15px;color:#333;max-width:520px\">"
        . "<h2 style=\"color:#1a5276\">Wildwatch</h2>"
        . "<p>Hi $name,</p><p>$intro</p>"
        . "<p><a href=\"$link\" style=\"display:inline-block;background:#1a5276;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none\">Set your password</a></p>"
        . "<p style=\"font-size:13px;color:#666\">Or paste this link into your browser:<br>$link</p>"
        . "<p style=\"font-size:13px;color:#666\">$validity</p></div>";
    return sendWildwatchMail($observer['email'], $subject, $html);
}

/**
 * Check recent disk_history for a linear descent that would hit zero within 24 hours.
 * If detected and the descent spans >= 45 minutes, email an alert with an SVG graph.
 *
 * $asOfUtc: if set, pretend "now" is this UTC datetime (for testing).
 * $recipients: array of email addresses to notify.
 * Returns the detection result array or null.
 */
function checkDiskDescentAlert($pdo, $asOfUtc = null, $recipients = ['markhebberd@gmail.com', 'bdot@snotch.com']) {
    $nowUtc = $asOfUtc ?? gmdate('Y-m-d H:i:s');

    // Fetch last 24h of samples relative to $nowUtc
    $stmt = $pdo->prepare("SELECT UNIX_TIMESTAMP(recorded_at) AS t, disk_free_mb
        FROM disk_history
        WHERE recorded_at >= ? - INTERVAL 1 DAY AND recorded_at <= ?
        ORDER BY recorded_at");
    $stmt->execute([$nowUtc, $nowUtc]);
    $points = $stmt->fetchAll();
    if (count($points) < 4) return null;

    // Convert to arrays
    $ts = array_map(fn($p) => (int)$p['t'], $points);
    $mb = array_map(fn($p) => (int)$p['disk_free_mb'], $points);

    // --- Same algorithm as JS: walk backwards requiring each step to descend ---
    $end = count($points) - 1;
    $minPts = 4;

    // Compute tail rate from last minPts points
    $tailDrop = $mb[$end - $minPts + 1] - $mb[$end];
    $tailSpan = $ts[$end] - $ts[$end - $minPts + 1];
    if ($tailSpan <= 0 || $tailDrop <= 0) return null;
    $steepestRate = $tailDrop / $tailSpan; // MB per second
    $minRate = $steepestRate * 0.25;

    $descentStart = $end;
    for ($i = $end; $i > 0; $i--) {
        $drop = $mb[$i - 1] - $mb[$i];
        $dt = $ts[$i] - $ts[$i - 1];
        if ($dt <= 0) break;
        $rate = $drop / $dt;
        if ($rate < $minRate) break;
        $descentStart = $i - 1;
    }
    $n = $end - $descentStart + 1;
    if ($n < $minPts) return null;

    // Linear regression on the descent segment
    $xs = array_slice($ts, $descentStart, $n);
    $ys = array_slice($mb, $descentStart, $n);
    $sx = $sy = $sxx = $sxy = $syy = 0;
    for ($i = 0; $i < $n; $i++) {
        $sx += $xs[$i]; $sy += $ys[$i];
        $sxx += $xs[$i] * $xs[$i]; $sxy += $xs[$i] * $ys[$i]; $syy += $ys[$i] * $ys[$i];
    }
    $denom = $n * $sxx - $sx * $sx;
    if ($denom == 0) return null;
    $slope = ($n * $sxy - $sx * $sy) / $denom;
    $intercept = ($sy - $slope * $sx) / $n;
    $ssTot = $syy - $sy * $sy / $n;
    $ssRes = 0;
    for ($i = 0; $i < $n; $i++) $ssRes += ($ys[$i] - ($slope * $xs[$i] + $intercept)) ** 2;
    $r2 = $ssTot == 0 ? 1 : 1 - $ssRes / $ssTot;

    if ($slope >= 0 || $r2 < 0.95) return null;

    $zeroTime = -$intercept / $slope; // unix timestamp when it hits 0
    $durationMin = ($ts[$end] - $ts[$descentStart]) / 60;
    $slopeMbPerMin = $slope * 60;
    $hoursToZero = ($zeroTime - $ts[$end]) / 3600;

    // Only alert if > 40 min descent and hits zero within 24 hours
    if ($durationMin <= 40 || $hoursToZero > 24 || $hoursToZero < 0) return null;

    $result = [
        'slope_mb_per_min' => round($slopeMbPerMin, 1),
        'gb_per_hr' => round($slopeMbPerMin * 60 / 1024, 2),
        'duration_min' => round($durationMin),
        'r2' => round($r2, 3),
        'zero_time_utc' => gmdate('Y-m-d H:i:s', (int)$zeroTime),
        'zero_time_nz' => (new DateTime('@' . (int)$zeroTime))->setTimezone(new DateTimeZone('Pacific/Auckland'))->format('H:i D j M T'),
        'hours_to_zero' => round($hoursToZero, 1),
        'current_free_mb' => $mb[$end],
    ];

    // Build PNG graph
    $pngData = buildDescentPng($ts, $mb, $descentStart, $end, $slope, $intercept, (int)$zeroTime);

    // Build multipart MIME email with inline PNG
    $nowNz = (new DateTime($nowUtc, new DateTimeZone('UTC')))->setTimezone(new DateTimeZone('Pacific/Auckland'))->format('H:i D j M Y T');
    $freeGb = round($result['current_free_mb'] / 1024, 1);
    $subject = "DISK ALERT - wildwatch.co.nz hits zero in {$result['hours_to_zero']}h";
    $from = 'no-reply@wildwatch.co.nz';
    $boundary = '----=_DiskAlert_' . md5(uniqid());
    $cid = 'disk-graph@wildwatch.co.nz';

    $html = "<html><body style='font-family:sans-serif;max-width:700px'>"
        . "<h2 style='color:#D32F2F;margin:0 0 12px 0'>Disk space running out</h2>"
        . "<table style='border-collapse:collapse;font-size:14px'>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>Drop rate</td><td><strong>" . abs($result['slope_mb_per_min']) . " MB/min</strong> (" . abs($result['gb_per_hr']) . " GB/hr)</td></tr>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>Duration</td><td>{$result['duration_min']} min</td></tr>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>R&sup2;</td><td>{$result['r2']}</td></tr>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>Current free</td><td>{$freeGb} GB</td></tr>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>Hits zero at</td><td style='color:#D32F2F;font-weight:bold'>{$result['zero_time_nz']}</td></tr>"
        . "<tr><td style='padding:4px 12px 4px 0;color:#666'>Alert sent</td><td>{$nowNz}</td></tr>"
        . "</table>"
        . "<div style='margin:16px 0'><img src='cid:{$cid}' width='600' height='260' style='border:1px solid #ddd;border-radius:4px' alt='Disk space graph'/></div>"
        . "<p style='font-size:12px;color:#999'><a href='https://wildwatch.co.nz/#admin'>View live graph</a></p>"
        . "</body></html>";

    $body = "--{$boundary}\r\n"
        . "Content-Type: text/html; charset=UTF-8\r\n"
        . "Content-Transfer-Encoding: 7bit\r\n\r\n"
        . $html . "\r\n"
        . "--{$boundary}\r\n"
        . "Content-Type: image/png; name=\"disk-graph.png\"\r\n"
        . "Content-Transfer-Encoding: base64\r\n"
        . "Content-ID: <{$cid}>\r\n"
        . "Content-Disposition: inline; filename=\"disk-graph.png\"\r\n\r\n"
        . chunk_split(base64_encode($pngData)) . "\r\n"
        . "--{$boundary}--";

    $headers = "From: $from\r\n"
        . "Reply-To: $from\r\n"
        . "MIME-Version: 1.0\r\n"
        . "Content-Type: multipart/related; boundary=\"{$boundary}\"";

    $to = implode(', ', $recipients);
    $ok = @mail($to, $subject, $body, $headers, "-f$from");

    $result['emailed'] = $recipients;
    $result['mail_result'] = $ok;
    return $result;
}

/**
 * Generate a PNG chart of disk history with descent highlighted using GD.
 */
function buildDescentPng($ts, $mb, $descentStart, $end, $slope, $intercept, $zeroTime) {
    $w = 600; $h = 260;
    $pad = ['t' => 20, 'r' => 20, 'b' => 40, 'l' => 55];
    $pw = $w - $pad['l'] - $pad['r'];
    $ph = $h - $pad['t'] - $pad['b'];

    $tMin = $ts[0];
    $tMax = max($ts[$end], $zeroTime);
    $mbMax = max($mb);
    $mbMin = 0;
    $tRange = $tMax - $tMin ?: 1;
    $mbRange = $mbMax - $mbMin ?: 1;

    $xOf = fn($t) => (int)($pad['l'] + ($t - $tMin) / $tRange * $pw);
    $yOf = fn($v) => (int)($pad['t'] + (1 - ($v - $mbMin) / $mbRange) * $ph);

    $img = imagecreatetruecolor($w, $h);
    $white = imagecolorallocate($img, 255, 255, 255);
    $blue = imagecolorallocate($img, 33, 150, 243);
    $orange = imagecolorallocate($img, 255, 87, 34);
    $red = imagecolorallocate($img, 211, 47, 47);
    $gray = imagecolorallocate($img, 150, 150, 150);
    $gridCol = imagecolorallocate($img, 232, 236, 239);
    $black = imagecolorallocate($img, 0, 0, 0);

    imagefill($img, 0, 0, $white);
    imagesetthickness($img, 1);

    // Grid lines + Y labels
    for ($i = 0; $i <= 4; $i++) {
        $v = $mbMin + $mbRange * $i / 4;
        $py = $yOf($v);
        imagedashedline($img, $pad['l'], $py, $w - $pad['r'], $py, $gridCol);
        $label = round($v / 1024) . ' GB';
        imagestring($img, 2, $pad['l'] - 45, $py - 7, $label, $gray);
    }

    // X labels (NZ 24h time)
    $tz = new DateTimeZone('Pacific/Auckland');
    for ($i = 0; $i <= 6; $i++) {
        $t = $tMin + ($tRange * $i / 6);
        $px = $xOf($t);
        $label = (new DateTime('@' . (int)$t))->setTimezone($tz)->format('H:i');
        imagestring($img, 2, $px - 12, $h - 30, $label, $gray);
    }

    // Blue line (all points)
    imagesetthickness($img, 2);
    for ($i = 1; $i <= $end; $i++) {
        imageline($img, $xOf($ts[$i-1]), $yOf($mb[$i-1]), $xOf($ts[$i]), $yOf($mb[$i]), $blue);
    }

    // Orange descent segment (thicker)
    imagesetthickness($img, 3);
    for ($i = $descentStart + 1; $i <= $end; $i++) {
        imageline($img, $xOf($ts[$i-1]), $yOf($mb[$i-1]), $xOf($ts[$i]), $yOf($mb[$i]), $orange);
    }
    // Orange dots
    for ($i = $descentStart; $i <= $end; $i++) {
        imagefilledellipse($img, $xOf($ts[$i]), $yOf($mb[$i]), 6, 6, $orange);
    }

    // Red dashed extrapolation line
    imagesetthickness($img, 2);
    $extX1 = $xOf($ts[$end]); $extY1 = $yOf($mb[$end]);
    $extX2 = $xOf($zeroTime); $extY2 = $yOf(0);
    // Draw dashed line manually
    $dashLen = 8; $gapLen = 5;
    $dx = $extX2 - $extX1; $dy = $extY2 - $extY1;
    $lineLen = sqrt($dx * $dx + $dy * $dy);
    if ($lineLen > 0) {
        $ux = $dx / $lineLen; $uy = $dy / $lineLen;
        $drawn = 0; $drawing = true;
        while ($drawn < $lineLen) {
            $segLen = $drawing ? $dashLen : $gapLen;
            $segEnd = min($drawn + $segLen, $lineLen);
            if ($drawing) {
                imageline($img,
                    (int)($extX1 + $ux * $drawn), (int)($extY1 + $uy * $drawn),
                    (int)($extX1 + $ux * $segEnd), (int)($extY1 + $uy * $segEnd), $red);
            }
            $drawn = $segEnd;
            $drawing = !$drawing;
        }
    }

    // Zero reference line (dashed red)
    $zeroY = $yOf(0);
    imagesetthickness($img, 1);
    for ($px = $pad['l']; $px < $w - $pad['r']; $px += 10) {
        imageline($img, $px, $zeroY, min($px + 5, $w - $pad['r']), $zeroY, $red);
    }

    ob_start();
    imagepng($img);
    $data = ob_get_clean();
    imagedestroy($img);
    return $data;
}

/**
 * Set CORS and JSON headers
 */
function setHeaders() {
    header('Content-Type: application/json');
    header('Cache-Control: no-cache'); // API responses must never be served stale from browser cache
    header('Access-Control-Allow-Origin: ' . ALLOWED_ORIGIN);
    header('Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS');
    header('Access-Control-Allow-Headers: Content-Type, Authorization, X-API-Key');

    // Handle preflight requests
    if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
        http_response_code(200);
        exit;
    }
}
/**
 * Fetch CSV from Google Sheets using curl (file_get_contents fails on this host)
 */
function fetchGoogleSheet($gid) {
    $url = "https://docs.google.com/spreadsheets/d/1A2j56iz0_VNHiWNJORAzGDqTbZsEd76j-YI_gQZsDEE/export?format=csv&gid=$gid";
    $ch = curl_init($url);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_FOLLOWLOCATION, true);
    curl_setopt($ch, CURLOPT_TIMEOUT, 60);
    $csv = curl_exec($ch);
    $code = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    return ($code === 200 && $csv) ? $csv : null;
}

/**
 * Get all penguin sightings: scans + chip events, deduplicated by date+box.
 * Filter by penguin OR box (or both). Returns array sorted newest first.
 * Each entry: peng_num, pit_id, sex, chipped_as_adult, chick_size_code, chip_date,
 *   date, box, source, adults, eggs, chicks, breeding_status, notes, seen_with[],
 *   is_chipped_here, chip_by, scan_count.
 */
function getSightings($pdo, $pengNum = null, $boxName = null, $colonyId = 1) {
    // Build scan query with optional filters
    $where = ['o.is_deleted = FALSE'];
    $params = [];
    if ($pengNum) { $where[] = 'pc.peng_num = ?'; $params[] = $pengNum; }
    if ($boxName) { $where[] = 'ol.location_name = ?'; $params[] = $boxName; $where[] = 'ol.colony_id = ?'; $params[] = $colonyId; }
    $whereStr = implode(' AND ', $where);

    $stmt = $pdo->prepare("SELECT ps.pit_id, pc.peng_num, p.sex, p.is_dead, p.chipped_as_adult, p.chick_size_code,
        pc.chip_date, o.observation_id, ol.location_name AS box_name, o.observation_time_utc,
        o.adults, o.eggs, o.chicks, o.breeding_status, o.notes
        FROM penguin_scans ps
        JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
        JOIN penguins p ON pc.peng_num = p.peng_num
        JOIN observations o ON ps.observation_id = o.observation_id
        JOIN observation_locations ol ON o.location_id = ol.location_id
        WHERE $whereStr AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)
        ORDER BY o.observation_time_utc DESC");
    $stmt->execute($params);
    $scans = $stmt->fetchAll();

    // Co-scanned birds per observation (penguin queries only — box view has scans inline)
    $coScans = [];
    if ($pengNum) {
    $obsIds = array_values(array_unique(array_column($scans, 'observation_id')));
    if (!empty($obsIds)) {
        $ph = implode(',', array_fill(0, count($obsIds), '?'));
        $coStmt = $pdo->prepare("SELECT ps.observation_id, pc.peng_num, p.sex, p.chipped_as_adult, pc.pit_id, pc.chip_date, p.chick_size_code
            FROM penguin_scans ps USE INDEX (idx_observation)
            JOIN penguin_chips pc ON ps.pit_id = pc.pit_id
            JOIN penguins p ON pc.peng_num = p.peng_num
            WHERE ps.observation_id IN ($ph) AND (ps.is_deleted = FALSE OR ps.is_deleted IS NULL)" . ($pengNum ? " AND pc.peng_num != ?" : ""));
        $params = $obsIds;
        if ($pengNum) $params[] = $pengNum;
        $coStmt->execute(array_values($params));
        foreach ($coStmt->fetchAll() as $row) {
            $coScans[$row['observation_id']][] = $row;
        }
    }
    }

    // Build penguin map and sighting list
    $penguins = []; // peng_num => summary
    $sightings = []; // deduped by peng+date+box
    foreach ($scans as $s) {
        $pnum = $s['peng_num'];
        $date = (new DateTime($s['observation_time_utc'], new DateTimeZone('UTC')))->setTimezone(new DateTimeZone('Pacific/Auckland'))->format('Y-m-d');
        $key = $pnum . '|' . $date . '|' . $s['box_name'];

        if (!isset($penguins[$pnum])) {
            $penguins[$pnum] = [
                'peng_num' => $pnum, 'pit_id' => $s['pit_id'], 'sex' => $s['sex'],
                'is_dead' => $s['is_dead'], 'chipped_as_adult' => $s['chipped_as_adult'],
                'chick_size_code' => $s['chick_size_code'], 'chip_date' => $s['chip_date'],
                'scan_count' => 0, 'last_seen' => $s['observation_time_utc'], 'is_chipped_here' => false,
            ];
        }
        $penguins[$pnum]['scan_count']++;

        if (!isset($sightings[$key])) {
            $sightings[$key] = [
                'peng_num' => $pnum, 'date' => $s['observation_time_utc'], 'box' => $s['box_name'],
                'source' => 'scan', 'adults' => (int)$s['adults'], 'eggs' => (int)$s['eggs'],
                'chicks' => (int)$s['chicks'], 'breeding_status' => $s['breeding_status'],
                'notes' => $s['notes'], 'seen_with' => $coScans[$s['observation_id']] ?? [],
            ];
        }
    }

    // Add chip events
    $chipWhere = [];
    $chipParams = [];
    if ($pengNum) { $chipWhere[] = 'pc.peng_num = ?'; $chipParams[] = $pengNum; }
    if ($boxName) { $chipWhere[] = 'pc.chip_box = ?'; $chipParams[] = $boxName; }
    if (!empty($chipWhere)) {
        $chipStmt = $pdo->prepare("SELECT pc.pit_id, pc.peng_num, p.sex, p.is_dead, p.chipped_as_adult, p.chick_size_code,
            pc.chip_date, pc.chip_box, u.chip_acronym AS chip_by
            FROM penguin_chips pc JOIN penguins p ON pc.peng_num = p.peng_num
            LEFT JOIN users u ON u.id = pc.chipper_id
            WHERE " . implode(' AND ', $chipWhere) . " ORDER BY pc.chip_date");
        $chipStmt->execute($chipParams);
        // A chip is a rechip if it isn't the penguin's first (chips come date-ascending).
        $seenChipPeng = [];
        foreach ($chipStmt->fetchAll() as $c) {
            $pnum = $c['peng_num'];
            $isRechip = isset($seenChipPeng[$pnum]);
            $seenChipPeng[$pnum] = true;
            if (!isset($penguins[$pnum])) {
                $penguins[$pnum] = [
                    'peng_num' => $pnum, 'pit_id' => $c['pit_id'], 'sex' => $c['sex'],
                    'is_dead' => $c['is_dead'], 'chipped_as_adult' => $c['chipped_as_adult'],
                    'chick_size_code' => $c['chick_size_code'], 'chip_date' => $c['chip_date'],
                    'scan_count' => 0, 'last_seen' => $c['chip_date'], 'is_chipped_here' => false,
                ];
            }
            if ($boxName && $c['chip_box'] === $boxName) {
                $penguins[$pnum]['is_chipped_here'] = true;
                $penguins[$pnum]['chip_by'] = $c['chip_by'];
            }

            if ($c['chip_box'] && $c['chip_date']) {
                // Skip chip sighting if penguin was already scanned that day (any box)
                $alreadyScanned = false;
                foreach ($sightings as $sk => $sv) {
                    if (str_starts_with($sk, $pnum . '|' . $c['chip_date'] . '|')) { $alreadyScanned = true; break; }
                }
                if (!$alreadyScanned) {
                    $key = $pnum . '|' . $c['chip_date'] . '|' . $c['chip_box'];
                    if (!isset($sightings[$key])) {
                        $verb = $isRechip ? 'Rechipped by ' : 'Chipped by ';
                        $chipper = $verb . ($c['chip_by'] ?: '?');
                        $sightings[$key] = [
                            'peng_num' => $pnum, 'pit_id' => $c['pit_id'], 'date' => $c['chip_date'], 'box' => $c['chip_box'],
                            'source' => 'chip', 'adults' => 0, 'eggs' => 0, 'chicks' => 0,
                            'breeding_status' => null, 'notes' => $chipper,
                            'chip_by' => $c['chip_by'], 'is_rechip' => $isRechip,
                            'seen_with' => [],
                        ];
                    }
                }
            }
        }
    }

    usort($sightings, function($a, $b) { return strcmp($b['date'], $a['date']); });
    return ['penguins' => array_values($penguins), 'sightings' => array_values($sightings)];
}

// ============ Day notes ============

/**
 * An import's monitor filename reduced to the part a person actually wrote — the day note it
 * should become — or '' if none of it was. Used by the JSON import (admin.php) and by the
 * one-off backfill of the old observations.monitor_filename column.
 *
 * The shapes this handles, from the values that column accumulated:
 *   sheet-import-2021-04-07              spreadsheet importer's own filename  -> ''
 *   web-entry, bdot@snotch.com           web form's who-typed-it stamp        -> ''
 *   PenguinMonitor 20 May 26 FM Marian   nestcheck export                     -> 'FM Marian'
 *   PenguinMonitor 251019 150843         export named for a bare timestamp    -> ''
 *   FM 13-2024-Jun 26.csv                old field-monitor sheet              -> 'FM 13'
 *   Mark and Flo (no bdot)               typed into the app's Daily label     -> unchanged
 *
 * Abbreviations are left exactly as written: this expands no FM, GR or BC into a guess at what
 * the person meant.
 */
function ww_cleanMonitorLabel(string $raw): string {
    $s = trim($raw);
    if ($s === '') return '';

    // Machine provenance, not a note: neither says anything about the day itself.
    if (preg_match('/^sheet-import\b/i', $s)) return '';
    if (preg_match('/^web-entry\b/i', $s)) return '';

    // The nestcheck export wrapper and the file extension carry no meaning.
    $s = preg_replace('/^PenguinMonitor\s*/i', '', $s);
    $s = preg_replace('/\.csv$/i', '', $s);

    // A leading date is the day's own date, restated. Forms present in the data:
    //   03.06.26 / 13.06.2026                 dotted
    //   20 May 26 / 8 June 26 / 2 May 2026    spelled
    //   250923 / 20250820 / 0902              compact — "260131FM" has no separator after it
    $mon = '(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?';
    $leading = [
        '/^\d{1,2}[.\/-]\d{1,2}[.\/-]\d{2,4}\s*/',
        '/^\d{1,2}\s+' . $mon . '\s+\d{2,4}\s*/i',
        '/^(\d{8}|\d{6}|\d{4})(?!\d)\s*/',
    ];
    // Twice: "250922 015545 GR b" is a date followed by a time.
    for ($pass = 0; $pass < 2; $pass++) {
        foreach ($leading as $p) {
            $next = preg_replace($p, '', $s, 1);
            if ($next !== $s) { $s = $next; break; }
        }
    }

    // The old FM sheets put the date at the end: "FM 13-2024-Jun 26", "FM 20 - 10 Sep 2024".
    $s = preg_replace('/\s*-\s*\d{4}-' . $mon . '\s+\d{1,2}$/i', '', $s);
    $s = preg_replace('/\s*-\s*\d{1,2}\s+' . $mon . '\s+\d{4}$/i', '', $s);

    return trim(preg_replace('/\s+/', ' ', $s), " \t\n\r-–—,·");
}

/**
 * Give a day the note $note if it has none. Never overwrites: a note already there was typed by
 * a person (or corrected by one after an earlier import), and an import arriving later should
 * not silently replace that. Returns true if a note was written. Caller owns the transaction.
 */
function wwFillDayNote($pdo, int $colonyId, string $noteDate, string $note, $observerId, $reason = null,
                       ?int $dayObserverId = null, ?int $dayScribeId = null): bool {
    $note = mb_substr(trim(preg_replace('/\s+/', ' ', $note)), 0, 255);
    if ($note === '' && $dayObserverId === null && $dayScribeId === null) return false;
    $ex = $pdo->prepare("SELECT day_note_id FROM day_notes WHERE colony_id = ? AND note_date = ?");
    $ex->execute([$colonyId, $noteDate]);
    if ($ex->fetchColumn()) return false;
    // A phone sending an id for a user that has since been deleted must not abort the whole
    // upload — the observations matter more than the attribution, so drop an unknown id.
    $known = function ($id) use ($pdo) {
        if ($id === null) return null;
        $c = $pdo->prepare("SELECT id FROM users WHERE id = ?");
        $c->execute([$id]);
        return $c->fetchColumn() ? $id : null;
    };
    wwAuditedInsert($pdo, 'day_notes', [
        'colony_id' => $colonyId, 'note_date' => $noteDate,
        'note'        => $note === '' ? null : $note,
        'observer_id' => $known($dayObserverId),
        'scribe_id' => $known($dayScribeId),
    ], $observerId, $reason);
    return true;
}

/**
 * Password policy for NEWLY SET passwords — the one place the rules live, so every set/reset
 * path enforces the same thing and the client can mirror it. Returns null if acceptable, or a
 * short human sentence naming the first problem (safe to show to the person choosing it).
 *
 * This gates only WRITES. Login still verifies whatever hash is stored, so existing accounts
 * with a weak old password keep working until they next change it — a policy change must never
 * lock people out, only stop new weak passwords going in.
 *
 * The client mirrors these exact rules in passwordProblem() (App.tsx) to colour the field and
 * gate the button; this function is the authority, since the client check is only advisory.
 *
 * $identity: any strings tied to the person — first name, surname, email. Each is tokenised
 * (email split at '@' and on separators, names on whitespace) and any token of 3+ chars may
 * not appear in the password. That is what stops "florence" for Florence, or "munoz" from
 * florence-munoz@hotmail.fr.
 */
function wwPasswordProblem(string $pw, array $identity = []): ?string {
    if (strlen($pw) < 8) return 'Use at least 8 characters.';

    // At least 4 distinct characters, checked BEFORE the length exemption below — otherwise
    // "000000000000" is 12 long, skips the class rule, and passes. Counts by byte, which is
    // fine here: a repeated multibyte char repeats its bytes too, so it still fails.
    if (count(array_unique(str_split($pw))) < 4) return 'Use at least 4 different characters.';

    // A long passphrase earns an exemption from the character-mix rule — "correcthorsestaple"
    // is stronger than "Pw2!x" and we should not punish it. Short ones need variety.
    if (strlen($pw) < 12) {
        $classes = (preg_match('/[a-z]/', $pw) ? 1 : 0)
                 + (preg_match('/[A-Z]/', $pw) ? 1 : 0)
                 + (preg_match('/[0-9]/', $pw) ? 1 : 0)
                 + (preg_match('/[^a-zA-Z0-9]/', $pw) ? 1 : 0);
        if ($classes < 2) return 'Mix at least two of: lower case, upper case, numbers, symbols — or make it 12+ characters.';
    }

    $lp = mb_strtolower($pw);

    // Not the person's own name or email. Tokenise (email at '@' then on separators, names on
    // separators), drop anything under 3 chars, and keep name and email tokens apart so the
    // message can name the exact thing that matched rather than a vague "name or email".
    $nameTokens = $emailTokens = [];
    foreach ($identity as $raw) {
        $raw = mb_strtolower(trim((string)$raw));
        if ($raw === '') continue;
        $isEmail = strpos($raw, '@') !== false;
        if ($isEmail) $raw = substr($raw, 0, strpos($raw, '@'));
        foreach (preg_split('/[^\p{L}\p{N}]+/u', $raw) as $tok) {
            // 4+ so a short whole name ("Amy", "Flo", "Lee") doesn't ban ordinary words that
            // merely contain it ("creamy", "flourish"). Real names are longer and still caught.
            if (mb_strlen($tok) < 4) continue;
            if ($isEmail) $emailTokens[$tok] = 1; else $nameTokens[$tok] = 1;
        }
    }
    foreach (array_keys($nameTokens)  as $tok) if (mb_strpos($lp, $tok) !== false) return 'Do not put your name in your password.';
    foreach (array_keys($emailTokens) as $tok) if (mb_strpos($lp, $tok) !== false) return 'Do not put your email address in your password.';

    // Base stems of the passwords a wordlist tries first, plus this project's own words. Each
    // is matched as a SUBSTRING and through common character swaps, so a stem catches its
    // suffixed AND leet-spelled variants — "Wildwatch1", "password2026", "P@ssw0rd", "W1ldw4tch"
    // are exactly what people reach for once a bare word is refused. Every stem is 6+ chars, so
    // even with the swaps this won't snag an ordinary password. A backstop, not the main rules.
    $weak = ['password','123456','1234567','12345678','qwerty','azerty','iloveyou',
             'letmein','motdepasse','changeme','welcome','monkey','dragon','sunshine',
             'wildwatch','nestcheck','penguin','manchot','korora'];
    // letter -> itself plus the digits/symbols people substitute for it
    $swap = ['a'=>'a4@','b'=>'b8','c'=>'c(','e'=>'e3','g'=>'g9','i'=>'i1!|','l'=>'l1|',
             'o'=>'o0','s'=>'s5$','t'=>'t7+','z'=>'z2'];
    foreach ($weak as $w) {
        $re = '';
        foreach (str_split($w) as $ch) $re .= isset($swap[$ch]) ? '['.preg_quote($swap[$ch], '/').']' : preg_quote($ch, '/');
        if (preg_match('/'.$re.'/i', $pw)) return 'That password is too easy to guess — choose something less common.';
    }

    return null;
}

// The audited write primitives (wwAuditedInsert / Update / Delete / Upsert) live in db_write.php,
// the one gateway between the clients and the data tables. Every endpoint gets them via config.php.
require_once __DIR__ . '/db_write.php';
?>
