using Android.Content;
using Android.OS;
using PenguinMonitor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;

namespace PenguinMonitor.Services
{
    public class DataStorageService
    {
        private const string APP_SETTINGS_FILENAME = "app_settings.json";
        private const string COLONY_STATE_FILENAME = "colony_state.json";
        internal const string REMOTE_BIRD_DATA_FILENAME = "remotePenguinData.json";
        internal const string BOX_NOTES_FILENAME = "boxNotes.json";
        internal const string BREEDING_DATES_FILENAME = "predictedDates.json";

        private static readonly HttpClient _httpClient = Http.CreateClient(TimeSpan.FromSeconds(15));
        private const int RETRY_MAX_SECONDS = 30;
        private const int RETRY_DELAY_MS = 5000;

        private static async Task<T> WithRetry<T>(Func<Task<T>> action, string label, Action<string>? onStatus = null, Func<bool>? isCancelled = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(RETRY_MAX_SECONDS);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    onStatus?.Invoke(attempt > 0 ? $"{label} (attempt {attempt + 1})..." : $"{label}...");
                    var result = await action();
                    return result;
                }
                catch (UpgradeRequiredException) { throw; }   // thirty seconds of retries won't update the app
                catch (Exception ex)
                {
                    if (DateTime.UtcNow >= deadline || (isCancelled?.Invoke() == true))
                    {
                        onStatus?.Invoke($"{label} ✗");
                        throw new Exception($"{label} failed after {attempt + 1} attempts: {ex.Message}");
                    }
                    onStatus?.Invoke($"{label} (attempt {attempt + 1} failed, retrying)...");
                    await Task.Delay(Math.Min(RETRY_DELAY_MS, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                }
            }
        }
        internal const string WILDWATCH_BASE_URL = "https://wildwatch.co.nz/penguin-api";
        private const string USERS_FILENAME = "wildwatch_users.json";

        internal const string WILDWATCH_API_URL = "https://wildwatch.co.nz/api/crud.php";
        internal const string WILDWATCH_SYNC_URL = "https://wildwatch.co.nz/api/sync.php";
        internal const string WILDWATCH_API_KEY = "b30181424b2d70102fb90a32af6c013e63e7b0d49ae466ebf90aa0f969ddbe02";
        internal const string WILDWATCH_EVENTS_URL = "https://wildwatch.co.nz/api/events.php";
        internal const string WILDWATCH_REPORTS_URL = "https://wildwatch.co.nz/api/reports.php";

        // ===== Background Polling =====

        private static string _lastWatermark = "";
        private static System.Timers.Timer? _pollTimer;
        private static bool _pollingSyncing = false;

        internal enum PollResult { Failed, NoChanges, Changed }

        /// <summary>
        /// Check events.php for changes since last watermark.
        /// </summary>
        internal async Task<PollResult> CheckForChangesAsync(string token)
        {
            try
            {
                var url = string.IsNullOrEmpty(_lastWatermark)
                    ? WILDWATCH_EVENTS_URL
                    : $"{WILDWATCH_EVENTS_URL}?wm={Uri.EscapeDataString(_lastWatermark)}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Bearer {token}");
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return PollResult.Failed;
                var json = await response.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (data == null) return PollResult.Failed;
                if (data.ContainsKey("wm")) _lastWatermark = data["wm"]?.ToString() ?? "";
                bool changed = data.ContainsKey("changed") && data["changed"]?.ToString() == "True";
                return changed ? PollResult.Changed : PollResult.NoChanges;
            }
            catch { return PollResult.Failed; }
        }

        /// <summary>
        /// Start background polling every 5 minutes. Calls onChanged on the main thread when new data detected.
        /// </summary>
        /// <summary>
        /// Start background polling every 5 minutes.
        /// onChecked is called on every successful poll (changed or not).
        /// onChanged is called only when server has new data.
        /// </summary>
        internal void StartBackgroundPolling(string token, Func<Task> onChanged, Action? onChecked = null, Func<Task>? onPendingUpload = null)
        {
            StopBackgroundPolling();
            _pollTimer = new System.Timers.Timer(15 * 1000); // 15 seconds (testing)
            _pollTimer.Elapsed += async (s, e) =>
            {
                if (_pollingSyncing || string.IsNullOrEmpty(token)) return;
                try
                {
                    var result = await CheckForChangesAsync(token);
                    if (result != PollResult.Failed)
                    {
                        onChecked?.Invoke();
                        if (result == PollResult.Changed)
                        {
                            _pollingSyncing = true;
                            await onChanged();
                            _pollingSyncing = false;
                        }
                        else if (onPendingUpload != null)
                        {
                            await onPendingUpload();
                        }
                    }
                }
                catch { _pollingSyncing = false; }
            };
            _pollTimer.AutoReset = true;
            _pollTimer.Start();

            // Fetch initial watermark silently
            _ = CheckForChangesAsync(token);
        }

        internal void StopBackgroundPolling()
        {
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        // ===== Sync Result =====

        public class SyncResult
        {
            public Dictionary<string, BoxTag>? BoxTags { get; set; }
            public BoxTagService.SyncResult? TagSyncResult { get; set; }
            /// <summary>Entries in the scanner's bird cache — one per chip, so a rechipped bird
            /// counts twice. This is the number that matters to a scan.</summary>
            public int BirdCount { get; set; }
            /// <summary>Distinct birds behind those chips. Fewer than BirdCount by the number of
            /// rechippings, and the honest answer to "how many birds does the phone know".</summary>
            public int ChippedBirdCount { get; set; }
            public int BoxCount { get; set; }
            public int Uploaded { get; set; }
            public int UploadErrors { get; set; }
            /// <summary>What the server refused, box by box. An observation whose scan was rejected
            /// still lands, so the box stops being pending — a bare count here is a bird's scan
            /// disappearing with nobody told which bird, or which box.</summary>
            public List<string> UploadErrorDetails { get; set; } = new List<string>();
            public int Reconciled { get; set; } // pending boxes matched to identical server data before upload
            public int BiometricCount { get; set; }
            public int BiometricsUploaded { get; set; }
            public int BiometricUploadErrors { get; set; }
            /// <summary>Why each biometric upload failed, in the server's words. A failed biometric
            /// stays queued, so the sync is not a clean one and must not report itself as such.</summary>
            public List<string> BiometricErrors { get; set; } = new List<string>();
            /// <summary>Fields the server sent that this build couldn't read, by path. The data that
            /// did parse is kept — this says what was dropped, so a payload change shows up as a
            /// named gap instead of a download that mysteriously fails.</summary>
            public HashSet<string> PayloadWarnings { get; set; } = new HashSet<string>();
            public string? Error { get; set; }
            /// <summary>Non-fatal notes from the offline-chip flush (e.g. a bird the server
            /// rejected). Kept out of Error so the sync still counts as successful — a queued-chip
            /// problem must not read as a failed download or stop background polling.</summary>
            public List<string>? ChipWarnings { get; set; }
            public bool AuthFailed { get; set; }
            /// <summary>
            /// Server-detected conflicts: box already has today's data.
            /// </summary>
            public List<SyncConflict>? Conflicts { get; set; }
        }

        /// <summary>The queued observation a server reply is about. Several days can be queued for
        /// one box after a spell out of signal, so the day the server names picks the right one;
        /// clearing another day's round would leave it to upload a second time. Falls back to the
        /// box's oldest queued round when the server didn't say (older server).</summary>
        private static BoxObservation? MatchPending(ColonyState colonyState, string boxName, string? nzDate)
        {
            var forBox = colonyState.PendingObservations
                .Where(p => p.BoxName == boxName && p.IsPendingUpload)
                .OrderBy(p => p.WhenDataCollectedUtc);
            if (!string.IsNullOrEmpty(nzDate) && DateTime.TryParse(nzDate, out var d))
            {
                var exact = forBox.FirstOrDefault(p => MainActivity.ToNzTime(p.WhenDataCollectedUtc).Date == d.Date);
                if (exact != null) return exact;
            }
            return forBox.FirstOrDefault();
        }

        private static string? CreatedNzDate(Dictionary<string, object> created) =>
            created.TryGetValue("nz_date", out var v) ? v?.ToString() : null;

        /// <summary>The NZ day a conflict's rejected observation was made on — read from the copy of
        /// it the server echoed back, so the right queued round is the one shown and replaced.</summary>
        internal static string? ConflictNzDate(SyncConflict conflict)
        {
            if (conflict.incoming == null) return null;
            if (!conflict.incoming.TryGetValue("observation_time_utc", out var t) || t == null) return null;
            return DateTime.TryParse(t.ToString(), null,
                       System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)
                ? MainActivity.ToNzTime(utc).ToString("yyyy-MM-dd") : null;
        }

        /// <summary>The queued round a conflict is about, picked by box and day.</summary>
        internal static BoxObservation? PendingForConflict(ColonyState colonyState, SyncConflict conflict) =>
            MatchPending(colonyState, conflict.box_name ?? "", ConflictNzDate(conflict));

        /// <summary>Does this box hold local work for that day that the server hasn't got? Either
        /// queued for upload, or a draft the box hasn't been locked on yet. Both must survive a
        /// download: a draft is invisible once the server's copy takes its place in TodayBoxes, and
        /// since drafts are never uploaded, overwriting one silently threw the edit away.</summary>
        private static bool HasUnsentLocalEdit(ColonyState colonyState, string boxName, DateTime nzDate) =>
            colonyState.PendingObservations.Any(p => p.BoxName == boxName
                && (p.IsPendingUpload || p.IsDraft)
                && MainActivity.ToNzTime(p.WhenDataCollectedUtc).Date == nzDate);

        /// <summary>Soft-delete one observation on the server, the same call wildwatch's own Delete
        /// button makes (crud.php action=delete, POST, optional _reason in the body). The server
        /// takes the observation's scans and biometrics with it, auditing each in its own right, so
        /// the history names the birds that went rather than just the parent row.
        ///
        /// Returns null on success, or a message worth showing. Needs the server: there is no queue
        /// for this, because a destructive action waiting invisibly for signal is worse than being
        /// told plainly that it didn't happen.</summary>
        internal async Task<string?> DeleteObservationAsync(int observationId, string? reason,
            AppSettings appSettings)
        {
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) return "Not logged in.";
            if (appSettings.SelectedColonyId <= 0) return "No colony selected.";
            try
            {
                var url = $"{WILDWATCH_API_URL}?action=delete&table=observations"
                        + $"&id={observationId}&colony_id={appSettings.SelectedColonyId}";
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bearer {token}");
                if (!string.IsNullOrWhiteSpace(reason))
                    req.Content = new StringContent(
                        JsonConvert.SerializeObject(new { _reason = reason }),
                        System.Text.Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                // 403 is the one worth naming: the server allows this to editors and admins only,
                // and "failed" would send someone looking for a fault that isn't there.
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return "Deleting an observation needs editor access — ask an admin on wildwatch.";
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return "Session expired. Please log in again.";
                if (Http.IsUpgradeRequired(resp)) return ServerMessage(body, 426);
                if (!resp.IsSuccessStatusCode) return $"Server refused it (HTTP {(int)resp.StatusCode}).";

                var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                if (parsed != null && parsed.TryGetValue("error", out var err))
                    return err?.ToString() ?? "Server refused it.";
                if (parsed == null || !parsed.ContainsKey("success")) return "Unexpected reply from the server.";
                return null;
            }
            catch (Exception ex) { return $"No connection — {ex.Message}"; }
        }

        /// <summary>A lat/long/accuracy as MySQL sends it — a decimal string, or null for "never
        /// recorded", which a 0 would read as the equator.</summary>
        private static double? ParseCoord(string? s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        // The colony to READ, falling back to Tarakohe when none is chosen yet. Writes send
        // SelectedColonyId as-is instead: with none chosen the server refuses and the work stays
        // queued, where a colony-1 fallback would file it under Port Tarakohe.
        private static int ColonyIdOf(AppSettings s) => s.SelectedColonyId > 0 ? s.SelectedColonyId : 1;

        // Observation time for upload. If the observation has no valid timestamp
        // (e.g. a chick added with no scan to seed one), default to now.
        private static string ObsTimeUtc(BoxObservation o)
        {
            var t = o.WhenDataCollectedUtc;
            if (t == default || t.Year < 2000) t = DateTime.UtcNow;
            return t.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        // Full tag identity: upper-cased and reduced to its ISO digits, so a tag scanned today
        // and the same tag stored by an older build ("LA"+15) compare equal. Free-text manual
        // entry was removed, so every scan is a full tag (scanner or search) — we compare the
        // whole number, not a tail.
        public static string PitFull(string? id) => BluetoothManager.BareEid((id ?? "").ToUpperInvariant());

        // A content fingerprint of an observation (ignores time/observer/id): counts, statuses,
        // notes, no-scan count, and the exact set of scanned birds. Two observations with the
        // same signature carry the same data. Shared by the download-first reconcile and the
        // conflict-dialog guard so "identical" means the same thing in both.
        public static string BoxSignature(BoxObservation o)
        {
            string N(string? s) => (s ?? "").Trim();
            bool IsNoScan(string? id) => (id ?? "").StartsWith("NOSCAN", StringComparison.OrdinalIgnoreCase);
            int noScan = o.ScannedIds.Count(s => IsNoScan(s.BirdId));
            var pits = o.ScannedIds.Where(s => !IsNoScan(s.BirdId)).Select(s => PitFull(s.BirdId)).OrderBy(x => x, StringComparer.Ordinal);
            // Losses count as content: without them an edit that only recorded a failed egg looked
            // identical to the server row, so the reconcile adopted the server copy and the loss was
            // never uploaded. Null and 0 compare equal — "not recorded" and "none" are the same box.
            return $"{o.Adults}|{o.Eggs}|{o.Chicks}|{N(o.BreedingStatus).ToUpperInvariant()}|{N(o.GateStatus).ToUpperInvariant()}|{N(o.Notes)}|{noScan}|{string.Join(",", pits)}|{o.FailedEggs ?? 0}|{o.DeadChicks ?? 0}";
        }

        // Download-first reconcile: before uploading, pull current server state and drop any
        // pending box whose data already matches the server. On a patchy connection a write can
        // commit server-side while the reply is lost, leaving the box queued; without this it
        // would re-upload and the server would report its own saved row back as a "replace"
        // conflict. Adopting the server row (with its observation_id) resolves it silently and
        // stops the box re-uploading. Returns the number reconciled.
        internal async Task<int> ReconcilePendingBeforeUpload(ColonyState colonyState, AppSettings appSettings, string token)
        {
            var pending = colonyState.PendingObservations
                .Where(p => p.IsPendingUpload && !string.IsNullOrEmpty(p.BoxName)).ToList();
            if (pending.Count == 0) return 0;

            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{WILDWATCH_SYNC_URL}?colony_id={(appSettings.SelectedColonyId > 0 ? appSettings.SelectedColonyId : 1)}");
            req.Headers.Add("Authorization", $"Bearer {token}");
            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return 0;
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{")) return 0;
            var serverState = LenientParse<SyncResponse>(json, "reconcile", new HashSet<string>());
            if (serverState?.boxes == null) return 0;

            var nzToday = MainActivity.NzToday;
            int reconciled = 0;
            foreach (var p in pending)
            {
                if (!serverState.boxes.TryGetValue(p.BoxName!, out var b)) continue;
                var serverObs = BoxObservation.FromServerData(b.observation_id, b.location_id, b.observation_time_utc,
                    b.adults, b.eggs, b.chicks, b.breeding_status, b.gate_status, b.notes ?? "", b.monitor_filename,
                    b.observer_name, b.failed_eggs, b.dead_chicks);
                serverObs.BoxName = p.BoxName;
                if (b.scans != null) foreach (var s in b.scans) serverObs.ScannedIds.Add(new ScanRecord { BirdId = s.pit_id ?? "" });
                for (int ns = 0; ns < b.no_scan; ns++) serverObs.ScannedIds.Add(new ScanRecord { BirdId = $"NOSCAN_{ns + 1}" });
                // Only a server row for the same NZ day can be the twin of a pending box.
                if (MainActivity.ToNzTime(serverObs.WhenDataCollectedUtc).Date != nzToday) continue;
                if (BoxSignature(serverObs) != BoxSignature(p)) continue; // genuinely different — leave it to upload/conflict

                colonyState.PendingObservations.Remove(p);
                serverObs.IsPendingUpload = false;
                colonyState.TodayBoxes[p.BoxName!] = serverObs; // adopt the server copy incl. its observation_id
                reconciled++;
            }
            return reconciled;
        }

        // ===== Main Sync: Upload pending, download fresh state =====

        // No boxTags in: box tags are no longer merged with a local copy, they are read off the
        // locations in the one feed, so what the caller happens to be holding can't matter.
        internal async Task<SyncResult> SyncWithServer(Android.Content.Context context, ColonyState colonyState, AppSettings appSettings, ICollection<string>? validBoxIds = null, Action<int, string>? onLineProgress = null, Func<bool>? isCancelled = null)
        {
            var result = new SyncResult();
            try
            {
                var token = appSettings.AuthToken;
                if (string.IsNullOrEmpty(token))
                {
                    result.Error = "Not logged in. Tap 'Login' to connect your Wildwatch account.";
                    result.AuthFailed = true;
                    return result;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Fetch colony data (always, so box sets stay current)
                {
                    try
                    {
                        var colonyRequest = new HttpRequestMessage(HttpMethod.Get, $"{WILDWATCH_BASE_URL}/colonies.php");
                        colonyRequest.Headers.Add("Authorization", $"Bearer {token}");
                        var colonyResponse = await _httpClient.SendAsync(colonyRequest);
                        // The first request of every sync, so a build the server has retired stops
                        // here with the server's own words, before any upload is tried: the queues
                        // are left exactly as they are for the updated app to send.
                        if (Http.IsUpgradeRequired(colonyResponse))
                        {
                            result.Error = ServerMessage(await colonyResponse.Content.ReadAsStringAsync(), 426);
                            return result;
                        }
                        if (colonyResponse.IsSuccessStatusCode)
                        {
                            var colonyJson = await colonyResponse.Content.ReadAsStringAsync();
                            var colonies = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(colonyJson);
                            if (colonies != null && colonies.Count > 0)
                            {
                                var match = colonies.FirstOrDefault(c => Convert.ToInt32(c["colony_id"]) == appSettings.SelectedColonyId) ?? colonies[0];
                                appSettings.SelectedColonyId = Convert.ToInt32(match["colony_id"]);
                                appSettings.SelectedColonyName = match["colony_name"]?.ToString() ?? "";
                                appSettings.SelectedColonyPrefix = match.TryGetValue("colony_prefix", out var cpfx) ? cpfx?.ToString() ?? "" : "";
                                appSettings.AllBoxSetsString = match["location_sets_string"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(appSettings.BoxSetString))
                                    appSettings.BoxSetString = "All";
                            }
                            else
                            {
                                // No permissions — clear colony
                                appSettings.SelectedColonyId = 0;
                                appSettings.SelectedColonyName = "";
                                appSettings.AllBoxSetsString = "";
                            }
                        }
                    }
                    catch { }
                }

                // No colony resolved after the fetch above — the user has no colony access, or a
                // first-run fetch failed before one was ever set. Never fall back to colony 1: that
                // would file this user's work under Tarakohe. Stop before any upload or download
                // targets the wrong colony; unsynced work stays queued for when a colony is known.
                if (appSettings.SelectedColonyId <= 0)
                {
                    bool hasWork = colonyState.PendingObservations.Any(p => p.IsPendingUpload)
                                   || colonyState.PendingBiometricCount > 0
                                   || LoadQueuedChips(context).Count > 0;
                    result.Error = hasWork
                        ? "No colony selected — your unsynced work is kept safe and will upload once you have colony access."
                        : "No colony selected. Log in and sync to load your colony.";
                    return result;
                }

                // The colony's prefix may have only just arrived (first sync after an upgrade, or a
                // phone that never had one), and every upload below sends peng numbers the server
                // now refuses bare — so unsent work written by the old build is converted first.
                MigratePengNumsToFull(context, colonyState, appSettings);

                // Step 0: Download-first reconcile — drop pending boxes whose data already
                // matches the server (a lost reply on a patchy connection re-queues an
                // observation the server already saved), so we never re-upload them or get
                // asked to replace identical data.
                result.Reconciled = await ReconcilePendingBeforeUpload(colonyState, appSettings, token);

                // Step 0b: birds chipped while offline go up BEFORE the observations that scanned
                // them. The server resolves each scan against the chips it knows: a pit_id it has
                // never seen is dropped from the observation with an error, and since the
                // observation itself lands, the box stops being pending and that scan is gone for
                // good. Creating the bird first is what makes its scan resolvable.
                var chipWarnings = await FlushQueuedChips(context, appSettings);
                if (chipWarnings.Count > 0) result.ChipWarnings = chipWarnings;

                // Step 1: Upload ALL pending observations — server detects conflicts
                var pendingBoxes = colonyState.PendingObservations
                    .Where(p => p.IsPendingUpload && !string.IsNullOrEmpty(p.BoxName))
                    .Select(p => (object)BuildObservationPayload(p))
                    .ToList();

                if (pendingBoxes.Count > 0)
                {
                    var uploadBody = JsonConvert.SerializeObject(BuildUploadBody(colonyState, pendingBoxes));
                    var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{WILDWATCH_SYNC_URL}?action=upload&colony_id={appSettings.SelectedColonyId}");
                    uploadRequest.Headers.Add("Authorization", $"Bearer {token}");
                    uploadRequest.Content = new StringContent(uploadBody, Encoding.UTF8, "application/json");

                    var uploadResponse = await _httpClient.SendAsync(uploadRequest);
                    if (uploadResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        result.Error = "Session expired. Please log in again.";
                        result.AuthFailed = true;
                        return result;
                    }

                    var uploadJson = await uploadResponse.Content.ReadAsStringAsync();
                    // A refused upload (no colony, no access) answers with an {"error"} body: say so
                    // and leave everything queued, rather than reading it as "nothing was created".
                    if (!uploadResponse.IsSuccessStatusCode)
                        throw new Exception($"Upload refused: {ServerMessage(uploadJson, (int)uploadResponse.StatusCode)}. Nothing was lost — still queued on this phone.");
                    // A gateway error page or an empty body here used to surface as a raw JSON
                    // parser message with nothing to act on; name the responder instead.
                    if (string.IsNullOrWhiteSpace(uploadJson) || !uploadJson.TrimStart().StartsWith("{"))
                        throw new Exception($"Upload: {ServerMessage(uploadJson, (int)uploadResponse.StatusCode)}");
                    var uploadResult = JsonConvert.DeserializeObject<Dictionary<string, object>>(uploadJson);

                    if (uploadResult != null && uploadResult.ContainsKey("created"))
                    {
                        var created = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(uploadResult["created"].ToString());
                        result.Uploaded = created?.Count ?? 0;

                        // Remove successfully uploaded observations from pending
                        foreach (var c in created ?? new())
                        {
                            var boxName = c["box_name"]?.ToString();
                            if (boxName != null)
                            {
                                var match = MatchPending(colonyState, boxName, CreatedNzDate(c));
                                if (match != null)
                                    colonyState.PendingObservations.Remove(match);
                            }
                        }
                    }
                    if (uploadResult != null && uploadResult.ContainsKey("errors"))
                    {
                        var errors = JsonConvert.DeserializeObject<List<object>>(uploadResult["errors"].ToString());
                        result.UploadErrors = errors?.Count ?? 0;
                        result.UploadErrorDetails.AddRange(DescribeUploadErrors(uploadResult["errors"]));
                    }
                    // Server-detected conflicts (box already has today's data)
                    if (uploadResult != null && uploadResult.ContainsKey("conflicts"))
                    {
                        result.Conflicts = JsonConvert.DeserializeObject<List<SyncConflict>>(uploadResult["conflicts"].ToString());
                    }
                }

                // Step 1b: Upload any pending biometric edits (independent of pending observations)
                await UploadPendingBiometrics(colonyState, token, result, appSettings.SelectedColonyId);

                // Step 1c: watched flags are kept locally and pushed here (offline-safe queue)
                var localBoxNotes = LoadBoxNotesFromDisk(context);
                await UploadPendingWatchedFlags(context, appSettings, localBoxNotes);

                // Step 1d: the day's note, if a change to it never reached the server (set while
                // offline, or refused). Riding along on an observation upload isn't enough — that
                // only fills a day with no note, so a corrected label would never land.
                await FlushPendingDayNote(context, colonyState, appSettings);

                // Step 2: Fetch + process in parallel — each task reports its own progress
                var nzToday = MainActivity.NzToday;
                bool authFailed = false;

                // Boxes: fetch, parse, update colony state
                // One incremental feed replaces the three fetches that used to be here (boxes,
                // penguins, today's biometrics). Those pulled a fixed slice of the colony every
                // time, which is why an edit or a delete outside that slice never reached the
                // phone. This pulls what changed since the last watermark and applies it — see
                // SYNC-INCREMENTAL.md.
                Task boxesTask = WithRetry(async () =>
                {
                    var db = SnapshotSyncService.Load(context);
                    var applied = await SnapshotSyncService.SyncAsync(_httpClient, context, db, token, ColonyIdOf(appSettings));
                    if (!applied.Ok)
                    {
                        if (applied.UpgradeRequired) throw new UpgradeRequiredException(applied.Error ?? "Update the app");
                        if (applied.Error?.Contains("401") == true) { authFailed = true; return 0; }
                        throw new Exception(applied.Error);
                    }
                    foreach (var w in applied.PayloadWarnings) result.PayloadWarnings.Add(w);

                    // Views the screens read, rebuilt from the rows — one copy of the truth.
                    SnapshotSyncService.DeriveViews(db, colonyState, nzToday, MainActivity.ToNzTime,
                        (cs, box, day) => HasUnsentLocalEdit(cs, box, day));

                    var birds = SnapshotSyncService.DeriveBirds(db);
                    // ~300 KB of indented JSON for two thousand birds, and on a quiet sync it would
                    // be byte-for-byte what is already there. Written only when the payload actually
                    // moved a bird — or when the file is missing, which is a first run.
                    var birdPath = Path.Combine(context.FilesDir?.AbsolutePath ?? "", REMOTE_BIRD_DATA_FILENAME);
                    if (applied.BirdsChanged || !File.Exists(birdPath))
                        File.WriteAllText(birdPath, JsonConvert.SerializeObject(birds, Formatting.Indented));
                    // Keyed per chip, because a chip is what the scanner reads — so a rechipped bird
                    // is two entries. Reporting that count as "birds" overstated the colony by the
                    // number of rechippings; say both, since both are the answer to a real question.
                    result.BirdCount = birds.Count;
                    result.ChippedBirdCount = birds.Values.Select(b => b.PengNum).Distinct().Count();

                    // Box notes: server text, with a locally-edited note or watched flag that hasn't
                    // been accepted yet left standing.
                    var boxNotes = new Dictionary<string, BoxNoteData>();
                    foreach (var loc in db.Locations.Values)
                    {
                        var bn = new BoxNoteData {
                            LocationId = loc.location_id, BoxName = loc.location_name ?? "",
                            PersistentNotes = loc.persistent_notes ?? "", Watched = loc.watched == 1,
                        };
                        if (localBoxNotes.TryGetValue(bn.BoxName, out var prior))
                        {
                            if (prior.WatchedPendingUpload) { bn.Watched = prior.Watched; bn.WatchedPendingUpload = true; }
                            if (prior.NotesPendingUpload) { bn.PersistentNotes = prior.PersistentNotes; bn.NotesPendingUpload = true; }
                        }
                        boxNotes[bn.BoxName] = bn;
                    }
                    File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, BOX_NOTES_FILENAME),
                        JsonConvert.SerializeObject(boxNotes, Formatting.Indented));

                    // People for the day pickers, and this user as the server sees them.
                    var users = db.Observers.Select(kv => new SyncUser { id = kv.Key, name = kv.Value }).ToList();
                    if (applied.Users != null && applied.Users.Count > 0) users = applied.Users;
                    File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, USERS_FILENAME),
                        JsonConvert.SerializeObject(users, Formatting.Indented));
                    // This user's own role, found by matching the id the server calls "me" against
                    // the observers list that rides every payload — so what the phone offers and
                    // what crud.php will accept are decided from the same place.
                    var myRole = applied.Me != null
                        ? users.FirstOrDefault(u => u.id == applied.Me.observer_id)?.role ?? ""
                        : appSettings.ObserverRole;
                    if (applied.Me != null &&
                        (appSettings.ObserverChipAcronym != (applied.Me.chip_acronym ?? "")
                         || appSettings.ObserverFalconId != (applied.Me.falcon_id ?? "")
                         || appSettings.ObserverRole != myRole))
                    {
                        appSettings.ObserverChipAcronym = applied.Me.chip_acronym ?? "";
                        appSettings.ObserverFalconId = applied.Me.falcon_id ?? "";
                        appSettings.ObserverRole = myRole;
                        saveApplicationSettings(appSettings);
                    }

                    // Box tags: pit_id and the stored fix are columns on the location row, so they
                    // come down with the locations above. This was the last download still on its
                    // own endpoint; boxtags.php now only takes the writes.
                    var tags = new Dictionary<string, BoxTag>();
                    foreach (var loc in db.Locations.Values)
                    {
                        var box = loc.location_name ?? "";
                        if (box.Length == 0) continue;
                        if (validBoxIds != null && !validBoxIds.Contains(box)) continue;
                        // A tagged box, or one whose position was recorded \u2014 the same pair
                        // boxtags.php returned, so nothing appears or vanishes with the change.
                        if (string.IsNullOrEmpty(loc.pit_id) && loc.latitude == null) continue;
                        tags[box] = new BoxTag
                        {
                            BoxID = box,
                            TagNumber = loc.pit_id ?? "",
                            ScanTimeUTC = SnapshotSyncService.ParseUtc(loc.scan_time_utc),
                            Latitude = ParseCoord(loc.latitude) ?? 0,
                            Longitude = ParseCoord(loc.longitude) ?? 0,
                            Accuracy = (float)(ParseCoord(loc.accuracy) ?? -1),
                        };
                    }
                    BoxTagService.SaveBoxTags(tags, context.FilesDir?.AbsolutePath ?? "");
                    result.BoxTags = tags;
                    result.TagSyncResult = new BoxTagService.SyncResult { Tags = tags, ApiAvailable = true };

                    colonyState.LastSyncedUtc = DateTime.UtcNow;
                    result.BoxCount = colonyState.TodayBoxes.Count;
                    result.BiometricCount = colonyState.TodayBiometrics.Count;
                    onLineProgress?.Invoke(0, $"{applied} \u2713");
                    onLineProgress?.Invoke(1, $"{result.BirdCount} chips \u00b7 {result.ChippedBirdCount} birds \u2713");
                    onLineProgress?.Invoke(2, $"{tags.Count} box tags \u2713");
                    return result.BoxCount;
                }, "Sync", s => onLineProgress?.Invoke(0, s), isCancelled);

                Task birdsTask = Task.CompletedTask;
                Task bioTask = Task.CompletedTask;

                // Breeding dates: fetch per-box current-clutch predictions from wildwatch
                // (which now owns the estimator). Non-critical — failures don't fail the sync.
                Task datesTask = WithRetry(async () =>
                {
                    int cid = appSettings.SelectedColonyId > 0 ? appSettings.SelectedColonyId : 1;
                    var req = new HttpRequestMessage(HttpMethod.Get, $"{WILDWATCH_REPORTS_URL}?report=breeding_dates&colony_id={cid}");
                    req.Headers.Add("Authorization", $"Bearer {token}");
                    var resp = await _httpClient.SendAsync(req);
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { authFailed = true; return 0; }
                    resp.EnsureSuccessStatusCode();
                    var datesJson = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(datesJson) || !datesJson.TrimStart().StartsWith("{"))
                        throw new Exception("Breeding dates API: expected JSON object");
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, BoxPredictedDates>>(datesJson);
                    File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, BREEDING_DATES_FILENAME), datesJson);
                    return parsed?.Count ?? 0;
                }, "Breeding dates", null, isCancelled);

                // Wait for all — don't throw if individual tasks fail
                try { await Task.WhenAll(boxesTask, birdsTask, bioTask, datesTask); } catch { }

                // Save colony state after all tasks complete (boxes task already mutated it)
                SaveColonyState(context, colonyState);

                if (authFailed) { result.Error = "Session expired. Please log in again."; result.AuthFailed = true; return result; }
                if (boxesTask.IsFaulted)
                {
                    // Tags come down with the boxes now, so they fail with them. Left blank the line
                    // would read as "no tags in this colony" rather than "not fetched".
                    onLineProgress?.Invoke(2, "Box tags ✗");
                    result.Error = boxesTask.Exception?.InnerException is UpgradeRequiredException upgrade
                        ? upgrade.Message
                        : $"Boxes: {boxesTask.Exception?.InnerException?.Message ?? "Failed"}";
                    return result;
                }
                if (birdsTask.IsFaulted)
                    result.Error = $"Penguin data: {birdsTask.Exception?.InnerException?.Message ?? "Failed"}";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Upload confirmed edits after user approval.
        /// </summary>
        /// <returns>How many boxes landed, and — when the server refused or couldn't be reached —
        /// why, in words to show the person who pressed Replace. Refused boxes stay queued.</returns>
        internal async Task<(int uploaded, string? error)> UploadConfirmedEdits(ColonyState colonyState, AppSettings appSettings,
            List<(string boxName, string? nzDate)> confirmedBoxes)
        {
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) return (0, "Not logged in");
            // Never confirm-upload without a colony — stays queued until one is resolved.
            if (appSettings.SelectedColonyId <= 0) return (0, "No colony selected");

            var uploads = new List<object>();
            foreach (var (boxName, nzDate) in confirmedBoxes)
            {
                // The day that was confirmed, not just the box: a replace aimed at one round must
                // not send a different day's round in its place.
                var pending = MatchPending(colonyState, boxName, nzDate);
                if (pending == null) continue;

                var scans = new List<object>();
                foreach (var scan in pending.ScannedIds)
                {
                    scans.Add(new {
                        pit_id = scan.BirdId,
                        scan_time_utc = scan.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        latitude = scan.Latitude, longitude = scan.Longitude, accuracy = scan.Accuracy,
                    });
                }
                uploads.Add(new {
                    box_name = pending.BoxName,
                    observation_id = pending.ObservationId,
                    observation_time_utc = ObsTimeUtc(pending),
                    adults = pending.Adults, eggs = pending.Eggs, chicks = pending.Chicks,
                    breeding_status = pending.BreedingStatus, gate_status = pending.GateStatus,
                    notes = pending.Notes, failed_eggs = pending.FailedEggs, dead_chicks = pending.DeadChicks, scans = scans,
                });
            }

            if (uploads.Count == 0) return (0, null);

            var body = JsonConvert.SerializeObject(new { daily_label = colonyState.DailyLabel,
                daily_observer_id = colonyState.DailyObserverId, daily_scribe_id = colonyState.DailyScribeId,
                observations = uploads });
            // colony_id is what the server looks box names up in — without it a replaced Ngawhiti box
            // landed in Port Tarakohe as a new box of the same name.
            var request = new HttpRequestMessage(HttpMethod.Post, $"{WILDWATCH_SYNC_URL}?action=confirm&colony_id={appSettings.SelectedColonyId}");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string json;
            try
            {
                response = await _httpClient.SendAsync(request);
                json = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) { return (0, $"Couldn't reach the server: {ex.Message}"); }
            // Refused, or nothing usable came back (gateway page, empty body): report why rather
            // than throwing — this runs inside a dialog flow whose continuation must still fire.
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{"))
                return (0, ServerMessage(json, (int)response.StatusCode));
            var uploadResult = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

            int uploaded = 0;
            if (uploadResult != null && uploadResult.ContainsKey("created"))
            {
                var created = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(uploadResult["created"].ToString());
                foreach (var c in created ?? new())
                {
                    var boxName = c["box_name"]?.ToString();
                    if (boxName != null)
                    {
                        var match = MatchPending(colonyState, boxName, CreatedNzDate(c));
                        if (match != null)
                        {
                            colonyState.PendingObservations.Remove(match);
                            match.IsPendingUpload = false;
                            var nzDate = MainActivity.ToNzTime(match.WhenDataCollectedUtc).Date;
                            if (nzDate == MainActivity.NzToday)
                                colonyState.TodayBoxes[boxName] = match;
                        }
                        uploaded++;
                    }
                }
            }
            return (uploaded, null);
        }

        /// <summary>One observation in the shape sync.php's upload action accepts. Shared with the
        /// manual JSON export so a hand-delivered file is byte-for-byte what a sync would have
        /// sent — an export that drifted from the wire format would be useless at the far end.</summary>
        internal static Dictionary<string, object?> BuildObservationPayload(BoxObservation obs)
        {
            var scans = obs.ScannedIds.Select(scan => (object)new {
                pit_id = scan.BirdId,
                scan_time_utc = scan.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                latitude = scan.Latitude, longitude = scan.Longitude, accuracy = scan.Accuracy,
            }).ToList();
            var payload = new Dictionary<string, object?>
            {
                ["box_name"] = obs.BoxName,
                ["observation_time_utc"] = ObsTimeUtc(obs),
                ["adults"] = obs.Adults, ["eggs"] = obs.Eggs, ["chicks"] = obs.Chicks,
                ["breeding_status"] = obs.BreedingStatus, ["gate_status"] = obs.GateStatus,
                ["notes"] = obs.Notes, ["failed_eggs"] = obs.FailedEggs, ["dead_chicks"] = obs.DeadChicks,
                ["scans"] = scans,
            };
            if (obs.ConfirmedAgainstObsId.HasValue)
                payload["expected_observation_id"] = obs.ConfirmedAgainstObsId.Value;
            return payload;
        }

        /// <summary>The upload envelope: the day's label and people, wrapped around the observations.</summary>
        internal static object BuildUploadBody(ColonyState colonyState, List<object> observations) => new {
            daily_label = colonyState.DailyLabel,
            daily_observer_id = colonyState.DailyObserverId,
            daily_scribe_id = colonyState.DailyScribeId,
            observations,
        };

        /// <summary>
        /// Upload-only: send pending observations to server, check for conflicts. No download.
        /// </summary>
        internal async Task<SyncResult> UploadPendingOnly(ColonyState colonyState, AppSettings appSettings)
        {
            var result = new SyncResult();
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) { result.Error = "Not logged in"; result.AuthFailed = true; return result; }
            // Never upload to the colony-1 fallback — keep it queued until a colony is resolved.
            if (appSettings.SelectedColonyId <= 0) { result.Error = "No colony selected — kept local until you sync with colony access."; return result; }
            if (colonyState.PendingObservations.Count(p => p.IsPendingUpload) == 0) return result;

            var pendingBoxes = colonyState.PendingObservations
                .Where(p => p.IsPendingUpload && !string.IsNullOrEmpty(p.BoxName))
                .Select(p => (object)BuildObservationPayload(p))
                .ToList();

            var uploadBody = JsonConvert.SerializeObject(BuildUploadBody(colonyState, pendingBoxes));
            var request = new HttpRequestMessage(HttpMethod.Post, $"{WILDWATCH_SYNC_URL}?action=upload&colony_id={appSettings.SelectedColonyId}");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Content = new StringContent(uploadBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { result.Error = "Session expired"; result.AuthFailed = true; return result; }

            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(json) || !json.TrimStart().StartsWith("{"))
            { result.Error = $"Upload refused: {ServerMessage(json, (int)response.StatusCode)}. Still queued on this phone."; return result; }

            var uploadResult = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (uploadResult != null && uploadResult.ContainsKey("created"))
            {
                var created = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(uploadResult["created"].ToString());
                result.Uploaded = created?.Count ?? 0;
                foreach (var c in created ?? new())
                {
                    var boxName = c["box_name"]?.ToString();
                    if (boxName != null)
                    {
                        var match = MatchPending(colonyState, boxName, CreatedNzDate(c));
                        if (match != null)
                        {
                            colonyState.PendingObservations.Remove(match);
                            // Move to TodayBoxes so it stays visible locally
                            match.IsPendingUpload = false;
                            var nzDate = MainActivity.ToNzTime(match.WhenDataCollectedUtc).Date;
                            if (nzDate == MainActivity.NzToday)
                                colonyState.TodayBoxes[boxName] = match;
                        }
                    }
                }
            }
            if (uploadResult != null && uploadResult.ContainsKey("errors"))
            {
                var errors = JsonConvert.DeserializeObject<List<object>>(uploadResult["errors"].ToString());
                result.UploadErrors = errors?.Count ?? 0;
                result.UploadErrorDetails.AddRange(DescribeUploadErrors(uploadResult["errors"]));
            }
            if (uploadResult != null && uploadResult.ContainsKey("conflicts"))
                result.Conflicts = JsonConvert.DeserializeObject<List<SyncConflict>>(uploadResult["conflicts"].ToString());

            return result;
        }

        // ===== Biometrics: cache + queue =====

        /// <summary>Build a BiometricRecord from a crud.php penguin_biometric_data row.</summary>
        private static BiometricRecord BiometricRecordFromRow(Dictionary<string, object> row, string dateFallback)
        {
            string? S(string k) => row.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
            bool B(string k) { var s = S(k); return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase); }
            int? I(string k) { var s = S(k); return int.TryParse(s, out var n) ? n : (int?)null; }
            return new BiometricRecord
            {
                PengNum = S("peng_num") ?? "",
                ObservationDate = S("observation_date") ?? dateFallback,
                Weight = S("weight"),
                FlipperLength = S("flipper_length"),
                ObservedSex = S("observed_sex"),
                // Wildwatch's column is is_moulting — not the condition_* family the other flags use.
                ConditionMoulting = B("is_moulting"),
                ConditionTicks = B("condition_ticks"),
                ConditionDead = B("condition_dead"),
                DispositionPassive = B("disposition_passive"),
                DispositionAggressive = B("disposition_aggressive"),
                Notes = S("notes"),
                BiometricId = I("biometric_id"),
                IsPendingUpload = false,
            };
        }

        // Where a field arrives as a type the model doesn't expect. Recorded per parse so a payload
        // change is reported rather than absorbed; the containing record still loads.
        private sealed class SkippedField
        {
            public string Path = "";
            public string Got = "";
            public override string ToString() => $"{Path} ({Got})";
        }

        /// <summary>Takes any JSON token where a string is expected, and consumes it whole.
        ///
        /// Consuming it is the point. Newtonsoft's Error event can swallow a type mismatch, but the
        /// reader is left standing in the middle of the value it choked on, and the whole enclosing
        /// object is abandoned — the payload comes back empty instead of throwing, which is worse.
        /// A converter reads the token to its end, so parsing carries on with the next field.
        ///
        /// An object gets its "note"/"name"/"value" read out where it has one, since a field being
        /// promoted from a string to a record around that string is how this keeps happening.</summary>
        private sealed class TolerantStringConverter : JsonConverter<string?>
        {
            private readonly ICollection<SkippedField> _skipped;
            public TolerantStringConverter(ICollection<SkippedField> skipped) { _skipped = skipped; }
            public override bool CanWrite => false;
            public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer) => throw new NotSupportedException();
            public override string? ReadJson(JsonReader reader, Type objectType, string? existing, bool hasExisting, JsonSerializer serializer)
            {
                switch (reader.TokenType)
                {
                    case JsonToken.Null:
                        return null;
                    case JsonToken.String:
                        return reader.Value?.ToString();
                    case JsonToken.StartObject:
                    case JsonToken.StartArray:
                    {
                        var path = reader.Path;
                        var token = Newtonsoft.Json.Linq.JToken.ReadFrom(reader);
                        string? salvaged = null;
                        if (token is Newtonsoft.Json.Linq.JObject obj)
                            foreach (var name in new[] { "note", "name", "value", "text" })
                                if (obj[name] is Newtonsoft.Json.Linq.JValue v && v.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                { salvaged = (string?)v; break; }
                        _skipped.Add(new SkippedField { Path = path, Got = salvaged == null ? token.Type.ToString() : $"{token.Type}, read its text" });
                        return salvaged;
                    }
                    default:
                        return reader.Value?.ToString();   // number, bool, date — the app wants text
                }
            }
        }

        /// <summary>The same for whole numbers: a null or a surprise shape leaves the field at its
        /// default rather than taking the record down with it.</summary>
        private sealed class TolerantIntConverter : JsonConverter
        {
            private readonly ICollection<SkippedField> _skipped;
            public TolerantIntConverter(ICollection<SkippedField> skipped) { _skipped = skipped; }
            public override bool CanConvert(Type t) => t == typeof(int) || t == typeof(int?);
            public override bool CanWrite => false;
            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();
            public override object? ReadJson(JsonReader reader, Type objectType, object? existing, JsonSerializer serializer)
            {
                bool nullable = objectType == typeof(int?);
                switch (reader.TokenType)
                {
                    case JsonToken.Null:
                        return nullable ? (int?)null : 0;
                    case JsonToken.Integer:
                        return Convert.ToInt32(reader.Value);
                    case JsonToken.Float:
                        return (int)Convert.ToDouble(reader.Value);
                    case JsonToken.String:
                        return int.TryParse(reader.Value?.ToString(), out var n) ? n : (nullable ? (int?)null : 0);
                    case JsonToken.Boolean:
                        return Convert.ToBoolean(reader.Value) ? 1 : 0;
                    default:
                    {
                        var path = reader.Path;
                        var token = Newtonsoft.Json.Linq.JToken.ReadFrom(reader);
                        _skipped.Add(new SkippedField { Path = path, Got = token.Type.ToString() });
                        return nullable ? (int?)null : 0;
                    }
                }
            }
        }

        /// <summary>Parse a server payload without letting one unexpected field cost the whole thing.
        ///
        /// Strict deserialisation is all-or-nothing: one field arriving as the wrong JSON type threw,
        /// and the entire box download failed — 153 boxes lost to one bad string, retried for thirty
        /// seconds, and reported as a partial sync with no clue which field. A field the phone can't
        /// read should cost that field, not the round. What was skipped is collected so this is
        /// visible rather than silent — the sync names it, and the paths go to the log.</summary>
        private static T? LenientParse<T>(string json, string label, ICollection<string> problems)
        {
            var skipped = new List<SkippedField>();
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new TolerantStringConverter(skipped));
            settings.Converters.Add(new TolerantIntConverter(skipped));
            // Last resort for a shape the converters don't cover (an object where a list belongs).
            // It abandons the rest of that record, so it must stay the exception, not the mechanism.
            settings.Error = (sender, args) =>
            {
                skipped.Add(new SkippedField { Path = args.ErrorContext.Path, Got = "unreadable" });
                args.ErrorContext.Handled = true;
            };
            var parsed = JsonConvert.DeserializeObject<T>(json, settings);
            foreach (var s in skipped)
            {
                problems.Add($"{label}: {s}");
                System.Diagnostics.Debug.WriteLine($"LenientParse {label} skipped {s}");
            }
            return parsed;
        }

        /// <summary>Read sync.php's per-observation "errors" into lines a person can act on —
        /// "Box 12: Unknown pit_id: …" — rather than the count that hid them.</summary>
        private static List<string> DescribeUploadErrors(object? errorsNode)
        {
            var lines = new List<string>();
            try
            {
                var rows = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(errorsNode?.ToString() ?? "[]");
                foreach (var r in rows ?? new())
                {
                    var box = r.TryGetValue("box", out var b) ? b?.ToString() : null;
                    var why = r.TryGetValue("error", out var e) ? e?.ToString() : null;
                    lines.Add(string.IsNullOrEmpty(box) ? (why ?? "rejected") : $"Box {box}: {why ?? "rejected"}");
                }
            }
            catch { }
            return lines;
        }

        /// <summary>The server's own words for a failed request: its "error" field where the body is
        /// JSON, otherwise the status and a short snippet of whatever came back instead.</summary>
        internal static string ServerMessage(string? body, int statusCode)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{"))
                {
                    var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                    if (obj != null && obj.TryGetValue("error", out var e) && e != null)
                        return e.ToString() ?? $"HTTP {statusCode}";
                }
            }
            catch { }
            var snippet = (body ?? "").Trim();
            if (snippet.Length > 120) snippet = snippet.Substring(0, 120) + "…";
            return string.IsNullOrEmpty(snippet) ? $"HTTP {statusCode}" : $"HTTP {statusCode}: {snippet}";
        }

        private static int? ExtractBiometricId(string createResponseJson)
        {
            try
            {
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(createResponseJson);
                if (obj == null) return null;
                foreach (var key in new[] { "biometric_id", "id" })
                    if (obj.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var n)) return n;
            }
            catch { }
            return null;
        }

        /// <summary>Upload all pending biometric edits via crud.php (create or update). Mutates colonyState.</summary>
        private async Task UploadPendingBiometrics(ColonyState colonyState, string token, SyncResult result, int colonyId)
        {
            foreach (var bio in colonyState.TodayBiometrics.Values.Where(b => b.IsPendingUpload).ToList())
            {
                try
                {
                    var fields = new Dictionary<string, object>
                    {
                        ["peng_num"] = bio.PengNum,
                        ["observation_date"] = bio.ObservationDate,
                    };
                    if (!string.IsNullOrEmpty(bio.Weight)) fields["weight"] = bio.Weight;
                    if (!string.IsNullOrEmpty(bio.FlipperLength)) fields["flipper_length"] = bio.FlipperLength;
                    if (!string.IsNullOrEmpty(bio.ObservedSex)) fields["observed_sex"] = bio.ObservedSex;
                    // The server's column is is_moulting; a payload naming it condition_moulting was
                    // rejected outright, so every moulting bird stayed queued. Always sent (unlike
                    // the flags below) so unticking it clears the flag on the next upload.
                    // Send 0/1, not a JSON bool: PDO binds a PHP `false` as '' and the tinyint
                    // column rejects it, which used to sink the whole biometric (sex guess and all).
                    fields["is_moulting"] = bio.ConditionMoulting ? 1 : 0;
                    // Ticks and the dispositions are checkboxes on the form too, so like moulting they
                    // always go up — unticking one has to clear it on the server.
                    fields["condition_ticks"] = bio.ConditionTicks ? 1 : 0;
                    fields["disposition_passive"] = bio.DispositionPassive ? 1 : 0;
                    fields["disposition_aggressive"] = bio.DispositionAggressive ? 1 : 0;
                    if (!string.IsNullOrEmpty(bio.Notes)) fields["notes"] = bio.Notes;
                    // Dead is not a biometric column — the flag was retired in favour of a death
                    // date on the bird itself, and is written separately below.

                    var url = bio.BiometricId.HasValue
                        ? $"{WILDWATCH_API_URL}?action=update&table=penguin_biometric_data&id={bio.BiometricId.Value}&colony_id={colonyId}"
                        : $"{WILDWATCH_API_URL}?action=create&table=penguin_biometric_data&colony_id={colonyId}";
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("Authorization", $"Bearer {token}");
                    req.Content = new StringContent(JsonConvert.SerializeObject(fields), Encoding.UTF8, "application/json");
                    var resp = await _httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Keep what the server said: a rejected field silently re-queues the bird
                        // every sync, and without the reason there's nothing to go on in the field.
                        result.BiometricUploadErrors++;
                        result.BiometricErrors.Add($"#{bio.PengNum}: {ServerMessage(await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode)}");
                        continue;
                    }

                    // Capture the new id on create so a later edit updates instead of duplicating
                    if (!bio.BiometricId.HasValue)
                        bio.BiometricId = ExtractBiometricId(await resp.Content.ReadAsStringAsync());
                    bio.IsPendingUpload = false;
                    result.BiometricsUploaded++;

                    if (bio.ConditionDead)
                    {
                        var deathError = await MarkPenguinDead(bio.PengNum, bio.ObservationDate, token, colonyId);
                        if (deathError != null)
                        {
                            // The biometric itself is saved; only the death date didn't land. Count
                            // it so the sync reads as partial rather than reporting a clean run.
                            result.BiometricUploadErrors++;
                            result.BiometricErrors.Add($"#{bio.PengNum} death date: {deathError}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.BiometricUploadErrors++;
                    result.BiometricErrors.Add($"#{bio.PengNum}: {ex.Message}");
                }
            }
        }

        /// <summary>Record a bird as dead on the date it was found. Death lives on the penguin, not on
        /// the visit: penguins.death_date, stamped 02:00 UTC — 2pm NZ, wildwatch's convention, so a
        /// same-day morning scan still reads as before the death. A bird that already has a death
        /// date keeps it; the first record of a death is the one that counts, and a later visit to
        /// the same carcass must not move the date. Returns null on success, else why it failed.</summary>
        private async Task<string?> MarkPenguinDead(string pengNum, string observationDate, string token, int colonyId)
        {
            if (string.IsNullOrEmpty(pengNum)) return "no penguin number";

            var getReq = new HttpRequestMessage(HttpMethod.Get,
                $"{WILDWATCH_API_URL}?action=get&table=penguins&id={Uri.EscapeDataString(pengNum)}&colony_id={colonyId}");
            getReq.Headers.Add("Authorization", $"Bearer {token}");
            var getResp = await _httpClient.SendAsync(getReq);
            var getBody = await getResp.Content.ReadAsStringAsync();
            if (!getResp.IsSuccessStatusCode) return ServerMessage(getBody, (int)getResp.StatusCode);
            try
            {
                var row = JsonConvert.DeserializeObject<Dictionary<string, object>>(getBody);
                if (row != null && row.TryGetValue("death_date", out var dd)
                    && dd != null && !string.IsNullOrWhiteSpace(dd.ToString()))
                    return null;   // already recorded dead — leave the original date alone
            }
            catch { }

            var fields = new Dictionary<string, object>
            {
                ["death_date"] = $"{observationDate} 02:00:00",
                ["_reason"] = "Recorded dead in nestcheck",
            };
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{WILDWATCH_API_URL}?action=update&table=penguins&id={Uri.EscapeDataString(pengNum)}&colony_id={colonyId}");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(fields), Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(req);
            return resp.IsSuccessStatusCode ? null
                : ServerMessage(await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
        }

        /// <summary>Settle the truth sex on a penguin (penguins.sex) once enough field guesses have
        /// piled up. Value is "m" or "f". This is the confirmed sex, not an observation. Returns null
        /// on success, else why it failed.</summary>
        public async Task<string?> ApplyPenguinSex(string pengNum, string sex, string token, int colonyId)
        {
            if (string.IsNullOrEmpty(pengNum)) return "no penguin number";
            var fields = new Dictionary<string, object>
            {
                ["sex"] = sex,
                ["_reason"] = "Sexed from field guesses in nestcheck",
            };
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{WILDWATCH_API_URL}?action=update&table=penguins&id={Uri.EscapeDataString(pengNum)}&colony_id={colonyId}");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(fields), Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(req);
            return resp.IsSuccessStatusCode ? null
                : ServerMessage(await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
        }

        /// <summary>Upload only pending biometrics (used for prompt background flush after a save).</summary>
        internal async Task<SyncResult> UploadPendingBiometricsOnly(ColonyState colonyState, AppSettings appSettings)
        {
            var result = new SyncResult();
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) { result.Error = "Not logged in"; result.AuthFailed = true; return result; }
            // Never upload to the colony-1 fallback — biometrics stay queued until a colony is resolved.
            if (appSettings.SelectedColonyId <= 0) { result.Error = "No colony selected — kept local until you sync with colony access."; return result; }
            await UploadPendingBiometrics(colonyState, token, result, appSettings.SelectedColonyId);
            return result;
        }

        public class SyncConflict
        {
            public string? box_name { get; set; }
            public SyncConflictObs? server { get; set; }
            public Dictionary<string, object>? incoming { get; set; }
        }
        public class SyncConflictObs
        {
            public int observation_id { get; set; }
            public string? observation_time_utc { get; set; }
            public string? observer_name { get; set; }
            public int adults { get; set; }
            public int eggs { get; set; }
            public int chicks { get; set; }
            public int no_scan { get; set; }
            public int? failed_eggs { get; set; }
            public int? dead_chicks { get; set; }
            public string? breeding_status { get; set; }
            public string? gate_status { get; set; }
            public string? notes { get; set; }
            public string? monitor_filename { get; set; }
            public List<SyncScan>? scans { get; set; }
        }

        // ===== JSON models for sync.php response =====
        //
        // Only what the pre-upload reconcile reads. sync.php's download was retired when the
        // snapshot feed took over — boxes for today is all that is still asked of it, and every
        // other group it sends (previous, locations, users, observer) now arrives on the feed.
        // Newtonsoft ignores the keys we no longer declare.

        private class SyncResponse
        {
            public Dictionary<string, SyncBox>? boxes { get; set; }
        }
        private class SyncBox
        {
            public int observation_id { get; set; }
            public int location_id { get; set; }
            public string? observation_time_utc { get; set; }
            public string? monitor_filename { get; set; }
            public string? observer_name { get; set; }
            public int adults { get; set; }
            public int eggs { get; set; }
            public int chicks { get; set; }
            public int no_scan { get; set; }
            public int? failed_eggs { get; set; }
            public int? dead_chicks { get; set; }
            public string? breeding_status { get; set; }
            public string? gate_status { get; set; }
            public string? notes { get; set; }
            public List<SyncScan>? scans { get; set; }
        }
        public class SyncScan
        {
            public string? pit_id { get; set; }
            public string? scan_time_utc { get; set; }
            public string? peng_num { get; set; }
            public string? sex { get; set; }
        }
        /// <summary>A person who can be named as the day's observer or scribe. Active users only.
        /// falcon_id is the chipper/permit id; only users who have one may be picked as the chipper.
        /// (Requires sync.php to include falcon_id in the users payload — else it's null for all.)</summary>
        public class SyncUser
        {
            public int id { get; set; }
            public string? name { get; set; }
            public string? f_name { get; set; }
            public string? surname { get; set; }
            /// <summary>Initials this person signs a chipping with (users.chip_acronym).</summary>
            public string? chip_acronym { get; set; }
            public string? falcon_id { get; set; }
            /// <summary>users.role — admin, editor, viewer or api. Carried so the phone can tell what
            /// this user is allowed to do without asking, and hide what crud.php would refuse.</summary>
            public string? role { get; set; }
        }

        // ===== Colony State persistence =====

        public static void SaveColonyState(Android.Content.Context context, ColonyState state)
        {
            try
            {
                if (string.IsNullOrEmpty(context.FilesDir?.AbsolutePath)) return;
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var path = Path.Combine(context.FilesDir.AbsolutePath, COLONY_STATE_FILENAME);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                // Keep the copy we're about to replace. This file is the only home of a day's
                // unsent work, and starting from an empty state loses a round nobody can redo.
                try { if (File.Exists(path)) File.Copy(path, path + ".bak", true); } catch { }
                File.Move(tempPath, path, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveColonyState failed: {ex.Message}");
            }
        }

        public static ColonyState LoadColonyState(Android.Content.Context context)
        {
            var dir = context.FilesDir?.AbsolutePath;
            var path = Path.Combine(dir ?? "", COLONY_STATE_FILENAME);
            ColonyState? Read(string p)
            {
                try
                {
                    if (!File.Exists(p)) return null;
                    return JsonConvert.DeserializeObject<ColonyState>(File.ReadAllText(p));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadColonyState {p}: {ex.Message}");
                    return null;
                }
            }

            var state = Read(path);
            if (state != null) { state.MigrateBiometricKeys(); state.MigrateTagPrefixes(); return state; }

            // The live file is unreadable. Fall back to the previous copy rather than opening on an
            // empty colony, and keep the bad one — an unsent observation is recoverable from a file
            // on disk, never from a state we deleted.
            var fallback = Read(path + ".bak");
            if (fallback != null)
            {
                try { if (File.Exists(path)) File.Copy(path, path + ".corrupt", true); } catch { }
                System.Diagnostics.Debug.WriteLine("LoadColonyState: recovered from .bak");
                fallback.MigrateBiometricKeys();
                fallback.MigrateTagPrefixes();
                return fallback;
            }
            return new ColonyState();
        }

        // ===== App Settings =====

        public static void saveApplicationSettings(AppSettings appSettings)
        {
            try
            {
                string saveTo = Path.Combine(appSettings.filesDir, APP_SETTINGS_FILENAME);
                string tempFile = saveTo + ".tmp";
                var appSettingsJson = JsonConvert.SerializeObject(appSettings, Formatting.Indented);
                File.Delete(tempFile);
                File.WriteAllText(tempFile, appSettingsJson);
                AppSettings g = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(tempFile));
                if (g.IsBlueToothEnabled != null)
                {
                    File.Move(tempFile, saveTo, true);
                    return;
                }
            }
            catch { }
        }

        public static AppSettings loadAppSettingsFromDir(string filesDir)
        {
            string appSettingsPath = Path.Combine(filesDir, APP_SETTINGS_FILENAME);
            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(appSettingsPath));
            }
            catch
            {
                return new AppSettings(filesDir);
            }
        }

        // ===== Remote Data Loaders (unchanged) =====

        public async Task<Dictionary<string, PenguinData>?> loadRemotePengInfoFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string remoteBirdPath = Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BIRD_DATA_FILENAME);
                var remoteBirdJson = File.ReadAllText(remoteBirdPath);
                return JsonConvert.DeserializeObject<Dictionary<string, PenguinData>>(remoteBirdJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote penguin data: {ex.Message}");
                return null;
            }
        }


        public async Task<Dictionary<string, BoxPredictedDates>?> loadBreedingDatesFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string breedingDatesPath = Path.Combine(context.FilesDir?.AbsolutePath, BREEDING_DATES_FILENAME);
                var breedingDatesJson = File.ReadAllText(breedingDatesPath);
                return JsonConvert.DeserializeObject<Dictionary<string, BoxPredictedDates>>(breedingDatesJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load predicted breeding data: {ex.Message}");
                return null;
            }
        }

        // ===== peng_num: bare → full, once, on upgrade =====

        /// <summary>The in-progress chipping form, saved so a process kill doesn't lose it.</summary>
        internal const string PENDING_CHIP_FILENAME = "pendingChip.json";

        /// <summary>Convert what an older build left on the phone from bare peng numbers ("1039") to
        /// the full form the API now speaks and insists on ("PT1039").
        ///
        /// Only unsent work is converted in place — queued biometrics, queued birds' requested
        /// numbers, a half-finished chipping form. The server now refuses a bare number on a write,
        /// so a queue left bare would never drain. The downloaded caches are rebuilt instead (LocalDb
        /// SchemaVersion forces a full pull); the bird cache is converted too, only so scanning and
        /// rechipping work offline in the gap before that pull lands.
        ///
        /// A bare number is taken to be the current colony's: it was the only colony the old API
        /// handed out bare, and these queues are cleared or blocked on a colony switch. With no
        /// prefix known yet nothing is touched and the flag stays down, so it runs on the first
        /// sync that learns one. Returns true when it converted this time round.</summary>
        internal bool MigratePengNumsToFull(Context context, ColonyState colonyState, AppSettings appSettings)
        {
            var prefix = appSettings.SelectedColonyPrefix ?? "";
            if (colonyState.PengNumFormat >= ColonyState.PengNumFormatFull || prefix.Length == 0) return false;
            var dir = context.FilesDir?.AbsolutePath ?? "";

            var queue = LoadQueuedChips(context);
            if (queue.Count > 0)
            {
                foreach (var q in queue)
                {
                    q.RequestedPengNum = PengNums.Full(q.RequestedPengNum, prefix);
                    q.RechipPengNum = PengNums.Full(q.RechipPengNum, prefix);
                }
                SaveQueuedChips(context, queue);
            }

            try
            {
                var formPath = Path.Combine(dir, PENDING_CHIP_FILENAME);
                if (File.Exists(formPath))
                {
                    var form = JsonConvert.DeserializeObject<PendingChipState>(File.ReadAllText(formPath));
                    if (form != null)
                    {
                        form.RequestedPengNum = PengNums.Full(form.RequestedPengNum, prefix);
                        form.RechipPengNum = PengNums.Full(form.RechipPengNum, prefix);
                        File.WriteAllText(formPath, JsonConvert.SerializeObject(form));
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MigratePengNums form: {ex.Message}"); }

            try
            {
                var birdPath = Path.Combine(dir, REMOTE_BIRD_DATA_FILENAME);
                if (File.Exists(birdPath))
                {
                    var birds = JsonConvert.DeserializeObject<Dictionary<string, PenguinData>>(File.ReadAllText(birdPath));
                    if (birds != null)
                    {
                        foreach (var b in birds.Values) b.PengNum = PengNums.Full(b.PengNum, prefix);
                        File.WriteAllText(birdPath, JsonConvert.SerializeObject(birds, Formatting.Indented));
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MigratePengNums birds: {ex.Message}"); }

            // Last, so the flag only goes up once everything above has been written.
            colonyState.MigratePengNumsToFull(prefix);
            SaveColonyState(context, colonyState);
            return true;
        }

        // ===== Offline chip queue =====
        internal const string QUEUED_CHIPS_FILENAME = "queuedChips.json";

        internal List<PendingChipState> LoadQueuedChips(Android.Content.Context context)
        {
            try
            {
                var p = Path.Combine(context.FilesDir?.AbsolutePath, QUEUED_CHIPS_FILENAME);
                if (File.Exists(p))
                    return JsonConvert.DeserializeObject<List<PendingChipState>>(File.ReadAllText(p)) ?? new List<PendingChipState>();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadQueuedChips: {ex.Message}"); }
            return new List<PendingChipState>();
        }

        internal void SaveQueuedChips(Android.Content.Context context, List<PendingChipState> queue)
        {
            try { File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, QUEUED_CHIPS_FILENAME), JsonConvert.SerializeObject(queue)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SaveQueuedChips: {ex.Message}"); }
        }

        // Only one flush at a time. A manual sync and a background sync can overlap, and two
        // flushes reading the same queue file would POST the same birds twice.
        private static readonly SemaphoreSlim _chipFlushLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Upload birds chipped while offline. Each queued entry goes up as ONE atomic
        /// create_chipped_bird call (penguin + chip + biometrics in a single server transaction),
        /// so a connection drop mid-flush can never leave half a bird behind. The server keys on
        /// pit_id, so replaying an entry whose first attempt actually landed just returns the
        /// peng_num it landed as — retrying is always safe.
        ///
        /// Connectivity failures keep the entry queued for the next sync. Only a definitive
        /// server rejection drops it, and never silently: the reason comes back as a warning.
        /// </summary>
        internal async Task<List<string>> FlushQueuedChips(Android.Content.Context context, AppSettings appSettings)
        {
            var warnings = new List<string>();
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) return warnings;
            // Never flush chips to the colony-1 fallback — a bird chipped here must not land in
            // Tarakohe. Keep them queued until a real colony is resolved.
            if (appSettings.SelectedColonyId <= 0) return warnings;
            if (LoadQueuedChips(context).Count == 0) return warnings;
            if (!await _chipFlushLock.WaitAsync(0)) return warnings; // another sync is already flushing

            try
            {
                var queue = LoadQueuedChips(context); // re-read under the lock
                var colonyId = appSettings.SelectedColonyId;

                foreach (var q in queue.ToList())
                {
                    // A 400 drops the bird from the queue, and the server 400s a bare peng_num. One still
                    // bare here was queued by an older build with no colony prefix learnt since to
                    // complete it — keep it (and everything after it, to hold the order) for a later
                    // sync rather than lose a bird to a number format.
                    var requested = PengNums.Full(q.RequestedPengNum, appSettings.SelectedColonyPrefix);
                    if (requested.Length > 0 && !char.IsLetter(requested[0])) break;

                    var chipDate = MainActivity.ToNzTime(q.CreatedUtc).ToString("yyyy-MM-dd");
                    var fields = new Dictionary<string, object>
                    {
                        ["pit_id"] = q.FullPitId,
                        ["chipped_as_adult"] = q.IsChick ? 0 : 1,
                        ["chip_date"] = chipDate,
                        ["observation_date"] = chipDate,
                        ["chip_box"] = q.ChipBox,
                    };
                    if (q.ChipperId > 0) fields["chipper_id"] = q.ChipperId;
                    else if (!string.IsNullOrEmpty(q.ChipBy)) fields["chip_by"] = q.ChipBy;   // legacy queued item (pre-dropdown)
                    if (q.AssistantId > 0) fields["assistant_id"] = q.AssistantId;
                    if (!string.IsNullOrEmpty(q.ChickSizeCode)) fields["chick_size_code"] = q.ChickSizeCode;
                    if (!string.IsNullOrEmpty(requested)) fields["requested_peng_num"] = requested;
                    if (!string.IsNullOrEmpty(q.Weight)) fields["weight"] = q.Weight;
                    if (!string.IsNullOrEmpty(q.Flipper)) fields["flipper_length"] = q.Flipper;
                    if (!string.IsNullOrEmpty(q.SexCode)) fields["observed_sex"] = q.SexCode;
                    if (!string.IsNullOrEmpty(q.Notes)) fields["notes"] = q.Notes;

                    System.Net.HttpStatusCode status;
                    Dictionary<string, object>? body;
                    string raw = "";
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post,
                            $"{WILDWATCH_BASE_URL}/crud.php?action=create_chipped_bird&colony_id={colonyId}");
                        req.Headers.Add("Authorization", $"Bearer {token}");
                        req.Content = new StringContent(JsonConvert.SerializeObject(fields), Encoding.UTF8, "application/json");
                        var resp = await _httpClient.SendAsync(req);
                        status = resp.StatusCode;
                        raw = await resp.Content.ReadAsStringAsync();
                        body = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
                    }
                    catch (Exception ex)
                    {
                        // Connectivity, or a response we can't parse (proxy/HTML error page). The
                        // transaction rolled back either way — stop; everything left stays queued
                        // and replays safely on the next sync.
                        System.Diagnostics.Debug.WriteLine($"FlushQueuedChips: {ex.Message}");
                        break;
                    }

                    var pengNum = body != null && body.ContainsKey("peng_num") ? body["peng_num"]?.ToString() : null;
                    if (string.IsNullOrEmpty(pengNum))
                    {
                        // Not a per-bird problem: expired session, lost editor rights, server
                        // fault. Retrying WILL help, so keep the queue intact — dropping birds
                        // because a token went stale would be real field data lost.
                        if (status != System.Net.HttpStatusCode.BadRequest)
                        {
                            // A retired build: the birds stay queued for the updated app, and the
                            // chipper is told why they aren't going up rather than left guessing.
                            if ((int)status == 426) warnings.Add($"Queued birds not sent — {ServerMessage(raw, 426)}");
                            System.Diagnostics.Debug.WriteLine($"FlushQueuedChips: {(int)status}, keeping queue");
                            break;
                        }

                        // 400: the server rolled back and definitively refused this bird.
                        // Retrying can't help, so drop it — but never silently: say enough
                        // that the bird can be re-entered by hand.
                        var why = body?.GetValueOrDefault("error")?.ToString() ?? "unknown error";
                        var who = string.IsNullOrEmpty(requested) ? q.FullPitId : requested;
                        warnings.Add($"Queued bird {who} (PIT {q.FullPitId}, box {q.BoxName}) rejected: {why}");
                    }
                    else if (!string.IsNullOrEmpty(requested) && !string.Equals(pengNum, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        // The predicted number was taken, so the server gave the next free one.
                        // Surface it — the field notes say one number and the database another.
                        warnings.Add($"Bird written down as {requested} synced as {pengNum} (number was taken — rename on wildwatch).");
                    }

                    queue.Remove(q);
                    SaveQueuedChips(context, queue);
                }
            }
            finally { _chipFlushLock.Release(); }

            return warnings;
        }

        /// <summary>
        /// Push local box edits — the watched flag and the persistent note — to the server. Both are
        /// kept locally and synced; a failed push just stays pending for the next sync (offline-safe).
        /// </summary>
        internal async Task UploadPendingWatchedFlags(Android.Content.Context context, AppSettings appSettings, Dictionary<string, BoxNoteData>? boxNotes = null)
        {
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) return;
            var notes = boxNotes ?? LoadBoxNotesFromDisk(context);
            var colonyId = appSettings.SelectedColonyId > 0 ? appSettings.SelectedColonyId : 1;
            bool changed = false;
            foreach (var n in notes.Values.Where(n => (n.WatchedPendingUpload || n.NotesPendingUpload) && n.LocationId > 0).ToList())
            {
                try
                {
                    // One update carries whichever of the two is outstanding; a field that isn't
                    // pending is left out so it can't overwrite a change made on the website.
                    var fields = new Dictionary<string, object>();
                    if (n.WatchedPendingUpload) fields["watched"] = n.Watched ? 1 : 0;
                    if (n.NotesPendingUpload) fields["persistent_notes"] = n.PersistentNotes ?? "";
                    var req = new HttpRequestMessage(HttpMethod.Post,
                        $"{WILDWATCH_BASE_URL}/crud.php?action=update&table=observation_locations&id={n.LocationId}&colony_id={colonyId}");
                    req.Headers.Add("Authorization", $"Bearer {token}");
                    req.Content = new StringContent(JsonConvert.SerializeObject(fields), Encoding.UTF8, "application/json");
                    var resp = await _httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        n.WatchedPendingUpload = false;
                        n.NotesPendingUpload = false;
                        changed = true;
                    }
                }
                catch { /* stays pending */ }
            }
            if (changed) SaveBoxNotesToDisk(context, notes);
        }

        public void SaveBoxNotesToDisk(Android.Content.Context context, Dictionary<string, BoxNoteData> boxNotes)
        {
            try
            {
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, BOX_NOTES_FILENAME),
                    JsonConvert.SerializeObject(boxNotes, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save box notes: {ex.Message}");
            }
        }

        public Dictionary<string, BoxNoteData> LoadBoxNotesFromDisk(Android.Content.Context context)
        {
            try
            {
                string path = Path.Combine(context.FilesDir?.AbsolutePath, BOX_NOTES_FILENAME);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<Dictionary<string, BoxNoteData>>(json) ?? new Dictionary<string, BoxNoteData>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load box notes: {ex.Message}");
            }
            return new Dictionary<string, BoxNoteData>();
        }

        /// <summary>Cached list of people who can be the day's observer or scribe. Empty until
        /// the first sync; the pickers then fall back to "not recorded".</summary>
        internal static List<SyncUser> LoadUsers(Context context)
        {
            try
            {
                var path = Path.Combine(context.FilesDir?.AbsolutePath, USERS_FILENAME);
                if (!File.Exists(path)) return new List<SyncUser>();
                return JsonConvert.DeserializeObject<List<SyncUser>>(File.ReadAllText(path)) ?? new List<SyncUser>();
            }
            catch { return new List<SyncUser>(); }
        }

        // Push the day's note (the phone's "Daily label") straight to the day_notes table for a
        // colony + NZ date. Unlike the daily_label carried on an observation upload — which only
        // fills a day that has no note yet — this upserts, so re-setting the label actually changes
        // it. Needs editor/admin role server-side; a viewer gets 403 and we just keep it local.
        internal async Task<bool> SaveDayNoteAsync(int colonyId, string nzDate, string note, string token,
                                                   int observerId = 0, int scribeId = 0)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{WILDWATCH_API_URL}?action=save_day_note&colony_id={colonyId}");
                request.Headers.Add("Authorization", $"Bearer {token}");
                var body = JsonConvert.SerializeObject(new { colony_id = colonyId, date = nzDate, note,
                    observer_id = observerId == 0 ? (int?)null : observerId,
                    scribe_id = scribeId == 0 ? (int?)null : scribeId });
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save day note: {ex.Message}");
                return false;
            }
        }

        /// <summary>Retry a day note whose server save didn't land (set while offline, or refused).
        /// The label is otherwise local-only: it rides along on an observation upload, but that
        /// only fills a day with no note, so a correction to an existing one would never arrive.</summary>
        private async Task FlushPendingDayNote(Android.Content.Context context, ColonyState colonyState, AppSettings appSettings)
        {
            var token = appSettings.AuthToken;
            if (string.IsNullOrEmpty(token)) return;
            int colonyId = appSettings.SelectedColonyId;
            bool changed = false;

            // Earlier days first — each goes to the date it was written on, not today.
            foreach (var note in colonyState.PendingDayNotes.ToList())
            {
                if (string.IsNullOrEmpty(note.NzDate)) { colonyState.PendingDayNotes.Remove(note); changed = true; continue; }
                if (await SaveDayNoteAsync(colonyId, note.NzDate, note.Note, token, note.ObserverId, note.ScribeId))
                {
                    colonyState.PendingDayNotes.Remove(note);
                    changed = true;
                }
            }

            if (colonyState.DailyLabelPendingUpload)
            {
                if (string.IsNullOrEmpty(colonyState.DailyLabelDate))
                {
                    colonyState.DailyLabelPendingUpload = false;
                    changed = true;
                }
                else if (await SaveDayNoteAsync(colonyId, colonyState.DailyLabelDate, colonyState.DailyLabel,
                             token, colonyState.DailyObserverId, colonyState.DailyScribeId))
                {
                    colonyState.DailyLabelPendingUpload = false;
                    changed = true;
                }
            }

            if (changed) SaveColonyState(context, colonyState);
        }

        internal async Task<bool> UpdateBoxNotesAsync(int locationId, string notes, string token)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{WILDWATCH_API_URL}?action=update&table=observation_locations&id={locationId}");
                request.Headers.Add("Authorization", $"Bearer {token}");
                var body = JsonConvert.SerializeObject(new { persistent_notes = notes });
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update box notes: {ex.Message}");
                return false;
            }
        }

        // Full reset: remove every file and subdirectory in the app's internal storage.
        // Used on logout so no cached colony/penguin data, box tags, notes, or settings
        // survive until the next login.
        public void ClearInternalStorageData(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir) || !Directory.Exists(filesDir)) return;
                foreach (var f in Directory.GetFiles(filesDir))
                {
                    try { File.Delete(f); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clear: {f}: {ex.Message}"); }
                }
                foreach (var d in Directory.GetDirectories(filesDir))
                {
                    try { Directory.Delete(d, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clear: {d}: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear data: {ex.Message}");
            }
        }

        // ===== Breeding Status (uses server previous data instead of monitor history) =====

        internal static string GetBoxBreedingStatusString(string boxName, BoxObservation? currentBox, BoxObservation? previousBox)
        {
            // Use the previous observation from server to determine breeding status
            var obs = currentBox ?? previousBox;
            if (obs == null) return "";

            if (obs.BreedingStatus == "ABN") return "Abandoned";
            if (!string.IsNullOrEmpty(obs.BreedingStatus)) return obs.BreedingStatus;
            return "";
        }

        public static string breedingDateStatus(int daysSinceLaid)
        {
            DateTime estHatch = MainActivity.NzToday.AddDays(38 - daysSinceLaid);
            if (estHatch.AddDays(3) >= MainActivity.NzToday)
                return "Hatch" + getDateString(estHatch);

            DateTime estPG = MainActivity.NzToday.AddDays(52 - daysSinceLaid);
            if (estPG.AddDays(3) >= MainActivity.NzToday)
                return "PG" + getDateString(estPG);

            DateTime chipStart = MainActivity.NzToday.AddDays(80 - daysSinceLaid);
            if (chipStart.AddDays(3) >= MainActivity.NzToday)
                return "Chip" + getDateString(chipStart);

            DateTime estFledge = MainActivity.NzToday.AddDays(87 - daysSinceLaid);
            if (estFledge.AddDays(3) >= MainActivity.NzToday)
                return "Fledge" + getDateString(estFledge);
            return "";
        }

        private static string getDateString(DateTime expectedDate)
        {
            DateTime today = MainActivity.NzToday;
            if (expectedDate.Date.Equals(today)) return " today";
            if ((expectedDate.Date - today).TotalDays == 1 && expectedDate > today) return " tomorrow";
            if ((today - expectedDate.Date).TotalDays == 1) return " yesterday";
            if (expectedDate > today)
                return " " + Math.Ceiling((expectedDate - today).TotalDays) + " days";
            return " " + Math.Ceiling((today - expectedDate).TotalDays) + " days ago";
        }
    }
}
