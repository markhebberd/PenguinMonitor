using Android.Animation;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Locations;
using Android.Media;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.Animations;
using Android.Views.InputMethods;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PenguinMonitor.Models;
using PenguinMonitor.Services;
using PenguinMonitor.UI.Factories;
using PenguinMonitor.UI.Gestures;
using PenguinMonitor.UI.Utils;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PenguinMonitor
{
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        Theme = "@android:style/Theme.NoTitleBar",
        ScreenOrientation = Android.Content.PM.ScreenOrientation.Portrait,
        WindowSoftInputMode = SoftInput.AdjustResize
    )]
    [IntentFilter(new[] { Android.Content.Intent.ActionView },
        Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
        DataMimeType = "application/json")]
    [IntentFilter(new[] { Android.Content.Intent.ActionView },
        Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
        DataMimeType = "*/*",
        DataPathPattern = ".*\\.json")]
    [IntentFilter(new[] { Android.Content.Intent.ActionView },
        Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
        DataScheme = "nestcheck", DataHost = "auth")]
    public class MainActivity : Activity, ILocationListener
    {
        private string? _versionCache;
        private string version => _versionCache ??= PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName!;
        private static readonly TimeZoneInfo NzTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        internal static DateTime ToNzTime(DateTime dt) => TimeZoneInfo.ConvertTimeFromUtc(
            dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc), NzTimeZone);
        internal static DateTime NzNow => ToNzTime(DateTime.UtcNow);
        internal static DateTime NzToday => NzNow.Date;
        // Bluetooth manager
        private BluetoothManager? _bluetoothManager;

        // GPS components
        private LocationManager? _locationManager;
        private Location? _currentLocation;
        private float _gpsAccuracy = -1;
        // Best (most accurate) fix recorded during an Edit Box Tags capture session
        private Location? _bestTagCaptureLocation;

        // Held scans — penguin chips scanned while no box is unlocked
        private readonly List<ScanRecord> _heldScans = new();
        private AlertDialog? _heldScansDialog;
        // Set when the user scans another box's tag before locking the current box.
        // Short-lived bridge for KNOWN birds only: scans made after the next box tag but before the
        // previous box was locked are held here and replayed into the right box on lock. A new/unknown
        // tag is never held — it's diverted straight to the chipping workflow (DivertNewBirdFromHeldQueue).
        private string? _pendingBoxTagNavigation;
        // Once a pending navigation exists, scanning a different box tag freezes the recording:
        // the destination is fixed (first tag wins) and no further scans are queued.
        private bool _pendingScanQueueFrozen;

        // Suppress ALL sync while data-entry dialogs are open
        private bool _dialogActive;
        private void SetDialogActive(bool active)
        {
            _dialogActive = active;
            if (active)
                _dataStorageService.StopBackgroundPolling();
            else
            {
                var token = _appSettings?.AuthToken;
                if (!string.IsNullOrEmpty(token))
                    StartPolling(token);
            }
        }

        // Status refresh (updates "sync:Xs ago" display)
        private Handler? _statusRefreshHandler;
        private Java.Lang.Runnable? _statusRefreshRunnable;
        private DateTime _lastSyncCheckUtc = DateTime.MinValue;
        // A cold start adopts the server's current change-watermark and reports "no changes",
        // so anything edited while the app was closed is never pulled by the incremental
        // poller. Force one full sync on launch when the last full sync is older than this.
        private const double SyncStaleMinutes = 5;
        // A change the poller detected but DoSilentSync had to defer (a box was unlocked or a
        // dialog was open) — the watermark already advanced past it, so it must be retried on
        // the next poll once unblocked, or the change is lost silently.
        private bool _pendingSilentSync;

        // Single place the background poller is (re)started, so the retry-on-unblock logic
        // below can't drift between call sites.
        private void StartPolling(string token)
        {
            _dataStorageService.StartBackgroundPolling(token,
                () => DoSilentSync(token),
                () =>
                {
                    _lastSyncCheckUtc = DateTime.UtcNow;
                    RunOnUiThread(() => UpdateStatusText());
                    // Retry a sync the guard deferred, now that we may be unblocked.
                    if (_pendingSilentSync && !_dialogActive && _isBoxLocked)
                        _ = DoSilentSync(token);
                },
                async () => { if (_colonyState?.PendingUploadCount > 0) { RunOnUiThread(() => TryBackgroundUpload()); } });
        }

        // UI Components
        private ScrollView? _rootScrollView;
        private TextView? _statusText; // scanner and GPS status

        private LinearLayout? _topButtonLayout; //Clear, bird stats and save/load. 

        private Button? _prevBoxButton;
        private Button? _selectBoxButton;
        private Button? _nextBoxButton;

        private LinearLayout? _settingsCard;
        private LinearLayout _overviewFiltersLayout;
        private CheckBox? _isBluetoothEnabledCheckBox;
        private Button? _syncButton;
        private TextView? _interestingBoxTextView;
        private TextView? _dupPenguinWarningView;
        private TextView? _stickyBulb1;
        private TextView? _stickyBulb2;
        private LinearLayout? _stickyNoteBar;
        private CheckBox _setTimeActiveSessionCheckBox;
        private TextView _boxSavedTimeTextView;
        // Trails the filter sentence with the count of boxes currently shown, e.g. "(74)".
        private TextView? _boxCountLabel;

        // Single box data 
        private bool _isBoxLocked;
        private bool _dataChangedSinceUnlock;
        private bool _suppressDataChanged;
        private bool _suppressColonySwitch;
        // Set while RebuildSettingsCard() re-creates the settings card (which runs after every sync).
        // The card holds the "Connect scanner" checkbox, and setting its initial .Checked re-fires the
        // CheckedChange handler — which would dispose and reconnect the live scanner on every sync,
        // bouncing the Bluetooth connection. Suppressed on rebuild so the connection is left alone; the
        // startup createSettingsCard (not a rebuild) still fires and makes the first connect.
        private bool _suppressBtCheckbox;
        // Track server observation IDs that user already confirmed locally (fallback for non-optimistic paths)
        private Dictionary<string, int> _confirmedAgainstServerObsId = new();
        private LinearLayout? _singleBoxDataOuterLayout;
        private LinearLayout? _singleBoxDataTitleLayout;
        private LinearLayout _singleBoxDataContentLayout;
        private LinearLayout _boxNavigationButtonsLayout;
        private TextView? _dataCardTitleText;
        private ImageView? _dataCardLockIconView;
        private TextView? _discardButton;
        private TextView? _watchedToggle;   // header watched flag — shown while the box is locked
        // Demo PIT used by the Settings "Rechip / new penguin" button — never sent to the server
        private const string PLACEHOLDER_PIT = "LA000000000000000";
        /// <summary>The demo PIT, however it is spelled — a form saved by an older build still
        /// carries the reader's "LA" prefix, and a test chip that stops being recognised as one
        /// is a fake bird saved to the server.</summary>
        private static bool IsPlaceholderPit(string? pit) =>
            DataStorageService.PitFull(pit) == DataStorageService.PitFull(PLACEHOLDER_PIT);
        // While the new-bird dialog is open with the placeholder, a real scan lands here
        private Action<string>? _newBirdScanCapture;

        // ===== Pending chip workflow persistence (survives Android killing the app) =====
        private const string PENDING_CHIP_FILENAME = "pendingChip.json";
        // Set while the new-bird dialog is open; OnPause invokes it to snapshot the form
        private Action? _pendingChipCapture;
        private void SavePendingChip(PendingChipState st)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(FilesDir!.AbsolutePath, PENDING_CHIP_FILENAME), JsonConvert.SerializeObject(st)); } catch { }
        }
        private PendingChipState? LoadPendingChip()
        {
            try
            {
                var p = System.IO.Path.Combine(FilesDir!.AbsolutePath, PENDING_CHIP_FILENAME);
                return System.IO.File.Exists(p) ? JsonConvert.DeserializeObject<PendingChipState>(System.IO.File.ReadAllText(p)) : null;
            }
            catch { return null; }
        }
        private void ClearPendingChip()
        {
            try
            {
                var p = System.IO.Path.Combine(FilesDir!.AbsolutePath, PENDING_CHIP_FILENAME);
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            }
            catch { }
        }
        private Button? _deleteBoxTagButton;

        private TextView? _noColonyBanner;   // blocks data entry while no box sets string is loaded
        private TextView? _webviewButton;   // opens the wildwatch box panel for the current nest
        private LinearLayout? _prevObsSummaryLayout;
        private TextView? _stickyNoteBelowPrev;   // sticky note shown under the expanded prev-obs card
        private TextView? _prevObsHeaderText;
        private LinearLayout? _prevObsDetailLayout;
        // Which box the expanded previous-obs detail belongs to — auto-collapses on box change.
        private string _prevObsExpandedForBox = "";
        // --- Edit Box Tags mode ---------------------------------------------------------
        // Opening a box in tag mode changes nothing and starts nothing — no GPS, no listening.
        // The user taps "Set up this box" to open a capture session, which walks two steps:
        //   1. get a GPS fix taken AT the box    2. scan the box tag (optional)
        // Both are held as pending state and written together by one Save. Cancel discards
        // the lot. There is no lock/unlock in this mode.
        private const int LOCATION_PERMISSION_REQUEST = 3;
        private bool _tagCaptureActive;
        private string _tagCaptureBoxName = "";
        private string? _pendingTagId;        // tag scanned this session, not yet written
        private string? _pendingTagFromBox;   // box the pending tag has to be taken off first
        private bool _pendingTagRemoval;      // clear this box's existing tag on save
        // "Set location" opens a confirm dialog rather than another screen: the dialog is what
        // guarantees the phone is on the nest before a single reading counts, which is the whole
        // reason the placement step exists. Recording starts on confirm and runs until saved.
        private bool _tagGpsMeasuring;
        private int _tagGpsElapsedSeconds;
        private Handler? _tagGpsHandler;
        private Java.Lang.Runnable? _tagGpsRunnable;
        private LinearLayout? _tagModeContentLayout;
        private LinearLayout? _tagModeLastObsCard;   // last known contents: black = today, orange = older
        private Button? _tagModeDeleteObsButton;     // editors/admins only — hidden for viewers
        private TextView? _tagModeTagText;
        private TextView? _tagModePositionText;
        private Button? _tagModeSetButton;           // "Set location"
        private Button? _tagModeSetTagButton;        // "Set tag" — never needs a GPS reading
        private LinearLayout? _tagModeCapturePanel;  // live capture UI, shown only while capturing
        private TextView? _tagModeCaptureTitle;
        private TextView? _tagModeStep1Text;         // step 1 — GPS
        private Button? _tagModeMeasureAgainButton;
        private TextView? _tagModeStep2Text;         // step 2 — box tag
        private Button? _tagModeTagActionButton;     // inline: clear a scanned tag / remove the stored one
        private Button? _tagModeSaveButton;
        private Button? _tagModeCancelButton;
        private TextView? _tagModeBanner;            // full-width "BOX TAGS MODE" bar at the top
        // Only ever one of these two occupies the bottom of the screen, so there is exactly one
        // primary action in thumb reach at a time: Save while capturing, Finish otherwise.
        private LinearLayout? _tagModeFinishBar;
        private LinearLayout? _tagModeCaptureBar;
        private View? _tagModeBottomSpacer;          // keeps scrolled content clear of the finish bar
        // Mode chrome: purple, deliberately unlike the black/orange/green/red/blue data language.
        private static readonly Color TAG_MODE_PURPLE = Color.ParseColor("#6A1B9A");
        private static readonly Color TAG_MODE_WASH = Color.ParseColor("#F3E5F5");

        private List<LinearLayout?> _scannedIdsLayout;
        private EditText? _penguinSearchEditText;

        /// <summary>
        /// Adjust a count field by delta, never below zero. TryParse rather than Parse because
        /// the user can clear the field entirely — an empty box must read as 0, not throw and
        /// take down the scan (or queued chip) that was trying to bump it.
        /// </summary>
        private void BumpCount(List<EditText?>? field, int delta)
        {
            var view = field != null && field.Count > 0 ? field[0] : null;
            if (view == null) return;
            int.TryParse(view.Text ?? "0", out int n);
            view.Text = Math.Max(0, n + delta).ToString();
        }

        private List<EditText?> _adultsEditText;
        private List<EditText?> _eggsEditText;
        private List<EditText?> _chicksEditText;
        private List<Spinner?> _breedingChanceSpinner;
        private List<Spinner?> _gateStatusSpinner;
        private List<EditText?> _notesEditText;
        private bool _shouldAutoDownloadBirdStats;
        private bool _isHistoricalView;
        private string _historicalFilename = "";
        private Dictionary<string, BoxObservation> _historicalBoxes = new();
        private TextView? _appTitleText;
        private Button? _exitHistoricalButton;
        private LinearLayout? _titleCard;
        private ImageButton? _expandSettingsButton;
        private Dictionary<string, BoxNoteData> _boxNotes = new Dictionary<string, BoxNoteData>();

        public UIFactory.selectedPage selectedPage;
        private readonly (string Text, UIFactory.selectedPage Page)[] _menuItems = new[]
        {
            ("⚙️ Settings",      UIFactory.selectedPage.Settings),
            ("📦 Single box data",  UIFactory.selectedPage.BoxDataSingle),
            ("📊 Today's statistics", UIFactory.selectedPage.BoxOverview),
         };

        // Add gesture detection components
        private GestureDetector? _gestureDetector;
        private float _lastTouchDownY = 0;
        private bool _isAnimating = false;

        // Services
        public UIFactory? _uiFactory;
        private DataStorageService _dataStorageService = new DataStorageService();

        // Data storage
        private int _currentBoxIndex = 1;
        private string _currentBoxName = "";
        private Dictionary<string, int> _boxNamesAndIndexes;
        private ColonyState _colonyState = new ColonyState();
        private AppSettings _appSettings;
        private Dictionary<string, PenguinData>? _remotePenguinData;
        private Dictionary<string, BoxPredictedDates>? _remoteBreedingDates;
        private Dictionary<string, BoxTag> _boxTags;

        // High value confirmation tracking - reset on each entry
        private DateTime doNotDisplayBefore;

        // Vibration and sound components
        private Vibrator? _vibrator;
        private MediaPlayer? _alertMediaPlayer;

        //multibox View
        private LinearLayout? _multiBoxViewCard;
        private LinearLayout? _breedingDatesCard;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestPermissions();
            LoadFromAppDataDir();
            HandleAuthDeepLink(Intent);
            CreateUI();
            UpdateStatusText();
            _statusRefreshHandler = new Handler(Looper.MainLooper);
            _statusRefreshRunnable = new Java.Lang.Runnable(() =>
            {
                UpdateStatusText();
                _statusRefreshHandler?.PostDelayed(_statusRefreshRunnable, 1000);
            });
            _statusRefreshHandler.PostDelayed(_statusRefreshRunnable, 1000);
            HandleIncomingJsonIntent();
            if (_shouldAutoDownloadBirdStats)
            {
                new Handler(Looper.MainLooper).PostDelayed(() =>
                {
                    try { StartSync(silent: true); } catch { }
                }, 1500);
            }
            // Pre-warm the embed WebView (boots the wildwatch embed app + colony sync in the
            // background) so the first bird/box panel open is instant. Delayed so it doesn't
            // compete with startup work.
            new Handler(Looper.MainLooper).PostDelayed(() => WarmEmbedWebView(), 4000);

            // Reopen an unfinished chipping form — the workflow can take ~15 minutes and
            // Android may kill the backgrounded app mid-bird. The saved PIT and form values
            // put the user back exactly where they were.
            new Handler(Looper.MainLooper).PostDelayed(() => TryRestorePendingChip(0), 2500);
        }

        /// <summary>
        /// Reopen a chipping form that a process-kill interrupted, on the box it was started on.
        /// The box list loads asynchronously, so wait for it: opening the form early would land
        /// the bird's counts on whatever box happened to be showing. Retries for ~10s, then gives
        /// up on the jump and says so rather than silently using the wrong box.
        /// </summary>
        private void TryRestorePendingChip(int attempt)
        {
            var pending = LoadPendingChip();
            if (pending == null || string.IsNullOrEmpty(pending.FullPitId)) return;

            bool needsJump = !string.IsNullOrEmpty(pending.BoxName) && pending.BoxName != _currentBoxName;
            bool boxKnown = _boxNamesAndIndexes != null && _boxNamesAndIndexes.ContainsKey(pending.BoxName ?? "");
            if (needsJump && !boxKnown)
            {
                if (attempt < 20) // ~10s of 500ms retries while the box list loads
                {
                    new Handler(Looper.MainLooper).PostDelayed(() => TryRestorePendingChip(attempt + 1), 500);
                    return;
                }
                // Box list is loaded but doesn't have it (different box set/colony), or it never
                // loaded. Restore the form — the PIT and the field values are the precious part —
                // but be loud that its counts will land on the box now showing.
                Toast.MakeText(this, $"Box {pending.BoxName} not loaded — restoring chip form on {_currentBoxName}", ToastLength.Long)?.Show();
            }
            else if (needsJump)
            {
                JumpToBox(pending.BoxName);
            }

            var sid = pending.FullPitId.Length >= 8 ? pending.FullPitId.Substring(pending.FullPitId.Length - 8) : pending.FullPitId;
            (string box, bool decrementAdult)? cleanup = pending.ScanCleanup ? (pending.ScanCleanupBox, pending.ScanCleanupDecrement) : null;
            ShowNewBirdDialog(sid, pending.FullPitId, scanCleanup: cleanup, restore: pending);
        }
        protected override void OnResume()
        {
            base.OnResume();
            _statusRefreshHandler?.PostDelayed(_statusRefreshRunnable, 1000);
            var token = _appSettings?.AuthToken;
            if (!string.IsNullOrEmpty(token) && _colonyState?.LastSyncedUtc > DateTime.MinValue)
            {
                StartPolling(token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await _dataStorageService.CheckForChangesAsync(token);
                        if (result != DataStorageService.PollResult.Failed)
                        {
                            _lastSyncCheckUtc = DateTime.UtcNow;
                            RunOnUiThread(() => { UpdateStatusText(); DrawPageLayouts(); });
                        }
                        if (result == DataStorageService.PollResult.Changed) await DoSilentSync(token);
                    }
                    catch { }
                });
            }
            UpdateStatusText();
        }

        protected override void OnPause()
        {
            base.OnPause();
            _statusRefreshHandler?.RemoveCallbacks(_statusRefreshRunnable);
            _dataStorageService.StopBackgroundPolling();
            // A backgrounded app may be killed by Android at any time: capture any unlocked
            // edits as a local draft (stays local — not flagged for upload until box lock),
            // and snapshot an in-progress chipping form (PIT included).
            try { if (!_isBoxLocked && !_appSettings.EditBoxTagsMode && !_isHistoricalView) SaveCurrentBoxData(); } catch { }
            try { _pendingChipCapture?.Invoke(); } catch { }
        }

        /// <summary>
        /// Silent background sync — download only, no UI dialogs, no upload.
        /// </summary>
        private async Task ApplyPostSync(DataStorageService.SyncResult result)
        {
            _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
            _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
            _boxNotes = _dataStorageService.LoadBoxNotesFromDisk(this);
            if (result.BoxTags != null) _boxTags = result.BoxTags;
            _lastSyncCheckUtc = DateTime.UtcNow;
        }

        private async Task DoSilentSync(string token)
        {
            // Don't clobber in-progress editing, but remember that a sync is owed — the poller
            // retries it once the box is locked / the dialog closes (otherwise the change the
            // poller already consumed from the watermark is lost for good).
            if (_dialogActive || !_isBoxLocked) { _pendingSilentSync = true; return; }
            _pendingSilentSync = false;
            try
            {
                var result = await _dataStorageService.SyncWithServer(this, _colonyState, _appSettings, _boxNamesAndIndexes?.Keys);
                await ApplyPostSync(result);
                new Handler(Looper.MainLooper).Post(() =>
                {
                    CreateBoxSetsDictionary();
                    RebuildSettingsCard();   // pick up the freshly-synced people list without a restart
                    if (_isBoxLocked)
                        DrawPageLayouts();
                    // Refresh the previous-obs card explicitly — a locked redraw can skip the
                    // box-card content path, leaving the breeding string / summary stale.
                    UpdatePreviousObsSummary();
                    UpdateStatusText();

                    // A queued bird the server refused is gone from the queue for good, and one
                    // given a different number won't match the field notes. Neither can be
                    // left to a toast the user may never see — make it dismiss-to-acknowledge.
                    if (result.ChipWarnings?.Count > 0)
                    {
                        new AlertDialog.Builder(this)
                            .SetTitle("Queued birds — check")
                            .SetMessage(string.Join("\n\n", result.ChipWarnings))
                            .SetPositiveButton("OK", (s, e) => { })
                            .SetCancelable(false)
                            .Show();
                    }
                });
            }
            catch (Exception ex)
            {
                new Handler(Looper.MainLooper).Post(() =>
                    Toast.MakeText(this, $"Background sync error: {ex.Message}", ToastLength.Short)?.Show());
            }
        }

        private void RequestPermissions()
        {
            InitializeVibrationAndSound();
            var permissions = new List<string>();

            // Always request READ_EXTERNAL_STORAGE for Android 6-12 (API 23-32)
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M && 
                Android.OS.Build.VERSION.SdkInt <= Android.OS.BuildVersionCodes.S) // Changed from < R to <= S
            {
                permissions.Add(Android.Manifest.Permission.ReadExternalStorage);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                permissions.AddRange(new[]
                {
                    Android.Manifest.Permission.BluetoothConnect,
                    Android.Manifest.Permission.BluetoothScan
                });
            }

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
            {
                // Android 13+ doesn't need WRITE_EXTERNAL_STORAGE for Downloads folder
            }
            else
            {
                permissions.Add(Android.Manifest.Permission.WriteExternalStorage);
            }

            permissions.AddRange(new[]
            {
                Android.Manifest.Permission.AccessFineLocation,
                Android.Manifest.Permission.AccessCoarseLocation,
                Android.Manifest.Permission.Internet
            });

            if (OperatingSystem.IsAndroidVersionAtLeast(23) && permissions.Count > 0)
            {
                // Check which permissions are not granted using native .NET Android API
                var permissionsToRequest = permissions.Where(p => 
                    CheckSelfPermission(p) != Android.Content.PM.Permission.Granted).ToArray();

                if (permissionsToRequest.Length > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Requesting permissions: {string.Join(", ", permissionsToRequest)}");
                    // Use native .NET Android API instead of AndroidX
                    RequestPermissions(permissionsToRequest, 1);
                }
                else
                {
                    // All permissions already granted
                    System.Diagnostics.Debug.WriteLine("All permissions already granted");
                    if (_appSettings?.IsBlueToothEnabled == true) InitializeGPS();
                }
            }
            else
            {
                // Pre-Android 6 or no permissions needed
                if (_appSettings?.IsBlueToothEnabled == true) InitializeGPS();
            }
        }
        private void InitializeVibrationAndSound()
        {
            try
            {
                // Initialize vibrator
                _vibrator = (Vibrator?)GetSystemService(VibratorService);

                // Initialize alert sound (using system notification sound)
                var notificationUri = Android.Media.RingtoneManager.GetDefaultUri(Android.Media.RingtoneType.Notification);
                if (notificationUri != null)
                {
                    var audioAttributesBuilder = new AudioAttributes.Builder();
                    var audioAttributes = audioAttributesBuilder?.SetUsage(AudioUsageKind.Alarm)
                                                                ?.SetContentType(AudioContentType.Sonification)
                                                                ?.Build();

                    _alertMediaPlayer = MediaPlayer.Create(this, notificationUri);
                    if (_alertMediaPlayer != null && audioAttributes != null)
                    {
                        _alertMediaPlayer.SetAudioAttributes(audioAttributes);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize vibration/sound: {ex.Message}");
            }
        }
        private void InitializeGPS()
        {
            _locationManager = (LocationManager?)GetSystemService(LocationService);
            if (_locationManager?.IsProviderEnabled(LocationManager.GpsProvider) != true &&
                _locationManager?.IsProviderEnabled(LocationManager.NetworkProvider) != true)
            {
                Toast.MakeText(this, "Please enable location services for accurate positioning", ToastLength.Short)?.Show();
                return;
            }
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
            {
                _locationManager?.RequestLocationUpdates(LocationManager.GpsProvider, 1000, 1, this);
                _locationManager?.RequestLocationUpdates(LocationManager.NetworkProvider, 1000, 1, this);
            }
        }
        public void OnLocationChanged(Location location) // required by ILocationListener
        {
            // Only accept if more accurate than current fix (avoids Network provider overwriting GPS)
            if (_currentLocation == null || location.Accuracy <= _currentLocation.Accuracy
                || (DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(_currentLocation.Time).UtcDateTime).TotalSeconds > 10)
            {
                _currentLocation = location;
                _gpsAccuracy = location.Accuracy;
                UpdateStatusText();
            }

            // Only collect during the measuring window. Once it closes, the reading shown is the
            // one that will be saved — letting later fixes keep changing it would mean the panel
            // said "measured" while the value underneath quietly moved.
            if (_tagCaptureActive && _tagGpsMeasuring)
            {
                if (_bestTagCaptureLocation == null || location.Accuracy < _bestTagCaptureLocation.Accuracy)
                    _bestTagCaptureLocation = location;
                RunOnUiThread(UpdateTagCaptureSteps);
            }
        }
        public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { } // required by ILocationListener
        public void OnProviderDisabled(string provider) { } // required by ILocationListener
        public void OnProviderEnabled(string provider) { } // required by ILocationListener
        private void InitializeBluetooth()
        {
            // Always retire the previous manager first. Re-init paths (settings redraw
            // re-firing the checkbox, exit-historical, restart) otherwise orphan a live
            // manager whose reconnect loop runs forever — even with the scanner disabled.
            _bluetoothManager?.Dispose();
            _bluetoothManager = null;
            MigrateLegacyScanner();
            var addresses = _appSettings.RememberedScanners.Where(s => s.Enabled).Select(s => s.Address).ToList();
            if (addresses.Count == 0)
            {
                UpdateStatusText("No scanner selected — choose one in Settings");
                return;
            }
            _bluetoothManager = new BluetoothManager();
            _bluetoothManager.StatusChanged += OnBluetoothStatusChanged;
            _bluetoothManager.EidDataReceived += OnEidDataReceived;
            if (_appSettings.IsBlueToothEnabled)
                _ = _bluetoothManager.ConnectAsync(addresses);
        }

        // One-time move of the old single SelectedBluetoothDevice into the remembered-scanners list.
        private void MigrateLegacyScanner()
        {
            if (_appSettings.RememberedScanners.Count == 0 && !string.IsNullOrEmpty(_appSettings.SelectedBluetoothDevice))
            {
                var addr = _appSettings.SelectedBluetoothDevice!;
                var name = (BluetoothManager.GetPairedDevices().FirstOrDefault(d => d.Address == addr).Name ?? addr).Trim();
                _appSettings.RememberedScanners.Add(new RememberedScanner { Address = addr, Name = name, Enabled = true });
                DataStorageService.saveApplicationSettings(_appSettings);
            }
        }

        // Add (or re-enable) a scanner the user picked from discovery, then reconnect.
        private void RememberScanner(string address, string name)
        {
            name = name?.Trim() ?? "";
            var existing = _appSettings.RememberedScanners.FirstOrDefault(s => s.Address == address);
            if (existing == null)
                _appSettings.RememberedScanners.Add(new RememberedScanner { Address = address, Name = name, Enabled = true });
            else
            {
                existing.Enabled = true;
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
            }
            _appSettings.SelectedBluetoothDevice = address; // keep legacy field pointing at the latest pick
            DataStorageService.saveApplicationSettings(_appSettings);
            RestartBluetooth();
        }

        // Restart the Bluetooth connection against the current enabled-scanner list.
        private void RestartBluetooth()
        {
            _bluetoothManager?.Dispose();
            _bluetoothManager = null;
            InitializeBluetooth();
        }

        // Edit a remembered scanner's nickname (blank clears it → falls back to the device name).
        private void ShowScannerNicknameDialog(RememberedScanner scanner, Action onDone)
        {
            var input = new EditText(this) { Text = scanner.Nickname };
            input.Hint = string.IsNullOrWhiteSpace(scanner.Name) ? scanner.Address : scanner.Name;
            input.SetSelectAllOnFocus(true);
            new AlertDialog.Builder(this)
                .SetTitle("Scanner nickname")
                .SetMessage($"{scanner.Name} ({scanner.Address})")
                .SetView(input)
                .SetPositiveButton("Save", (s, e) =>
                {
                    scanner.Nickname = input.Text?.Trim() ?? "";
                    DataStorageService.saveApplicationSettings(_appSettings);
                    onDone();
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }
        private void OnBluetoothStatusChanged(string status)
        {
            RunOnUiThread(() => UpdateStatusText(status));
        }
        public void OnSwipePrevious()
        {
            // If viewing single box data, navigate to newer historical data
            // Navigate to previous box
            if (!_isBoxLocked)
            {
                Toast.MakeText(this, "Please lock the current box before navigating", ToastLength.Short)?.Show();
                return;
            }
            if (_currentBoxIndex > 1)
            {
                NavigateToBox(_currentBoxIndex - 1, () => _currentBoxIndex > 1);
            }
            else
            {
                Toast.MakeText(this, "Already at first box", ToastLength.Short)?.Show();
            }
        }
        public void OnSwipeNext()
        {
            // Navigate to next box
            if (!_isBoxLocked)
            {
                Toast.MakeText(this, "Please lock the current box before navigating", ToastLength.Short)?.Show();
                return;
            }
            if (_currentBoxIndex < _boxNamesAndIndexes.Count)
            {
                NavigateToBox(_currentBoxIndex + 1, () => _currentBoxIndex < _boxNamesAndIndexes.Count);
            }
            else
            {
                Toast.MakeText(this, "Already at last box", ToastLength.Short)?.Show();
            }
        }
        private void OnEidDataReceived(string eidData)
        {
            RunOnUiThread(() => HandleEidData(eidData));
        }

        /// <summary>A bird-cache key that is a whole tag — its 15 ISO digits — rather than the
        /// short 8-digit form some lookups fall back to.</summary>
        private static bool IsFullPitKey(string? key) => key?.Length == 15;

        private void HandleEidData(string eidData)
        {
            // Already stripped of the reader's prefix on the way in (BluetoothManager.BareEid).
            // Stripped again because everything below compares tags by string, and that has to
            // hold no matter what a future caller hands this method.
            var cleanEid = BluetoothManager.BareEid(new String(eidData.Where(char.IsLetterOrDigit).ToArray()));

            // Paused: no colony/box list loaded — nothing may be recorded.
            if (string.IsNullOrWhiteSpace(_appSettings.AllBoxSetsString))
            {
                Toast.MakeText(this, "⛔ No colony loaded — log in and sync first", ToastLength.Short)?.Show();
                return;
            }

            // Guard: only complete 15-digit EIDs may be recorded. A partial read (e.g. the
            // scanner waking mid-transmission) would fail the 9000250 box-tag prefix test
            // and be silently recorded as a penguin scan.
            if (!BluetoothManager.IsCompleteEid(cleanEid))
            {
                Toast.MakeText(this, $"⚠️ Partial scan ignored ({cleanEid.Length} chars) — please scan again", ToastLength.Short)?.Show();
                return;
            }

            // The new-bird dialog (opened with the demo placeholder PIT) accepts a real
            // scanned chip in its place instead of routing the scan to the box.
            if (_newBirdScanCapture != null)
            {
                _newBirdScanCapture(cleanEid);
                return;
            }

            // Box tag mode: route all scans to HandleBoxTagScan
            if (_appSettings.EditBoxTagsMode)
            {
                HandleBoxTagScan(cleanEid);
                return;
            }

            bool isBoxTag = BoxTagService.IsBoxTag(cleanEid);

            if (isBoxTag)
            {
                // Box tag scanned — navigate to box and flush any held scans
                HandleBoxTagScan(cleanEid);
                // Don't flush while a "forgot to lock" navigation is pending — those held scans
                // belong to the pending box and are replayed once the current box is locked.
                if (_heldScans.Count > 0 && _pendingBoxTagNavigation == null)
                    FlushHeldScansToCurrentBox();
                return;
            }

            if (_pendingBoxTagNavigation != null)
            {
                // The user scanned another box's tag before locking the current box.
                // Record penguin scans so they can be replayed into the pending box once it's locked —
                // unless the queue was frozen by a further box-tag scan, in which case ignore them.
                if (_pendingScanQueueFrozen)
                {
                    TriggerAlert();
                    Toast.MakeText(this, $"⚠️ Scan ignored — lock Box {_currentBoxName} first", ToastLength.Short)?.Show();
                    return;
                }
                // A new bird is never held — divert it straight to the chipping workflow.
                if (DivertNewBirdFromHeldQueue(cleanEid)) return;
                if (!_heldScans.Any(s => s.BirdId == cleanEid))
                {
                    _heldScans.Add(new ScanRecord
                    {
                        BirdId = cleanEid,
                        Timestamp = DateTime.UtcNow,
                        Latitude = _currentLocation?.Latitude ?? 0,
                        Longitude = _currentLocation?.Longitude ?? 0,
                        Accuracy = _currentLocation?.Accuracy ?? -1
                    });
                }
                Toast.MakeText(this, $"Held — lock Box {_currentBoxName} to continue to Box {_pendingBoxTagNavigation}", ToastLength.Short)?.Show();
                return;
            }

            if (_isBoxLocked)
            {
                // A new bird is never held — divert it straight to the chipping workflow.
                if (DivertNewBirdFromHeldQueue(cleanEid)) return;
                // Penguin chip scanned while locked — hold it, don't add to any box
                if (_heldScans.Any(s => s.BirdId == cleanEid))
                {
                    Toast.MakeText(this, $"Already held — {cleanEid}", ToastLength.Short)?.Show();
                    return;
                }
                var scanRecord = new ScanRecord
                {
                    BirdId = cleanEid,
                    Timestamp = DateTime.UtcNow,
                    Latitude = _currentLocation?.Latitude ?? 0,
                    Longitude = _currentLocation?.Longitude ?? 0,
                    Accuracy = _currentLocation?.Accuracy ?? -1
                };
                _heldScans.Add(scanRecord);
                TriggerAlert();
                ShowHeldScansDialog();
                return;
            }

            // Box is unlocked — normal scan flow
            if (selectedPage != UIFactory.selectedPage.BoxDataSingle)
                selectedPage = UIFactory.selectedPage.BoxDataSingle;

            // Auto-expand the box card if it's collapsed so the scan is visible
            if (_singleBoxDataContentLayout != null && _singleBoxDataContentLayout.Visibility != ViewStates.Visible)
            {
                _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                ScrollToTop();
            }

            AddScannedId(eidData, 0);
            _dataChangedSinceUnlock = true;
            DrawPageLayouts();
        }

        // A valid pit the colony data doesn't recognise is a NEW bird: it belongs in the chipping
        // workflow, not the short-lived held-scan queue (which only carries KNOWN birds to replay
        // into the right box when the previous nest wasn't locked yet). Holding a new bird meant it
        // rode into the flush as an unknown scan and, past the first, was silently dropped at sync.
        // Divert it straight to the new-bird dialog and return true; false = a known bird to hold.
        // When colony data isn't loaded we can't classify, so treat it as known and hold as before.
        private bool DivertNewBirdFromHeldQueue(string cleanEid)
        {
            if (_remotePenguinData == null) return false;
            var key = cleanEid.ToUpper();
            var shortKey = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8).ToUpper() : key;
            if (_remotePenguinData.ContainsKey(key) || _remotePenguinData.ContainsKey(shortKey)) return false;
            var shortId = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8) : cleanEid;
            TriggerAlert();                          // buzz — the scribe needs to notice a new bird
            ShowNewBirdDialog(shortId, cleanEid);    // never held — chip it now
            return true;
        }

        private void FlushHeldScansToCurrentBox()
        {
            if (_heldScans.Count == 0) return;

            _isBoxLocked = false;
            selectedPage = UIFactory.selectedPage.BoxDataSingle;
            if (_singleBoxDataContentLayout != null)
                _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
            if (_boxNavigationButtonsLayout != null)
                _boxNavigationButtonsLayout.Visibility = ViewStates.Visible;

            var boxData = _colonyState.GetTodayForBox(_currentBoxName) ?? new BoxObservation { BoxName = _currentBoxName };
            int added = 0;
            foreach (var scan in _heldScans)
            {
                if (!boxData.ScannedIds.Any(s => s.BirdId == scan.BirdId))
                {
                    boxData.ScannedIds.Add(scan);
                    // Each flushed scan is a bird present — bump the adult or chick count
                    var (_, _, isChick, _) = LookupPenguinLabel(scan.BirdId);
                    if (isChick) boxData.Chicks++;
                    else boxData.Adults++;
                    added++;
                }
            }
            if (added > 0)
            {
                boxData.WhenDataCollectedUtc = DateTime.UtcNow;
                boxData.ObserverName = _appSettings.ObserverName;
                _colonyState.SaveBoxObservation(_currentBoxName, boxData); // draft until the box is locked
                SaveToAppDataDir();
            }

            // Held scans are KNOWN birds only — a new/unknown tag is diverted to the chipping
            // workflow at scan time and never held — so the flush just replays them. No unknown-scan
            // handling here: the old code processed only the first unknown and dropped the rest.
            Toast.MakeText(this, $"📥 {added} scan{(added != 1 ? "s" : "")} added to Box {_currentBoxName}", ToastLength.Short)?.Show();
            _heldScans.Clear();
            _heldScansDialog?.Dismiss();
            _heldScansDialog = null;
            SetDialogActive(false);
            _dataChangedSinceUnlock = true;
            DrawPageLayouts();
            ScrollToTop();
        }

        private void ShowHeldScansDialog()
        {
            try { _heldScansDialog?.Dismiss(); } catch { }
            _heldScansDialog = null;
            SetDialogActive(true);

            var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            layout.SetPadding(24, 16, 24, 16);
            layout.SetBackgroundColor(Color.White);

            // Header: "Waiting:" followed by penguin mini badges
            var headerFlow = new PenguinMonitor.UI.FlowLayout(this);
            var density = Resources?.DisplayMetrics?.Density ?? 2;
            headerFlow.HorizontalSpacing = (int)(4 * density);
            headerFlow.VerticalSpacing = (int)(4 * density);
            var waitLabel = new TextView(this) { Text = "Waiting:", TextSize = 30 };
            waitLabel.SetTextColor(Color.Black);
            waitLabel.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            headerFlow.AddView(waitLabel);
            foreach (var scan in _heldScans)
                headerFlow.AddView(CreateScanBadge(scan.BirdId, textSize: 18));
            layout.AddView(headerFlow);

            var info = new TextView(this)
            {
                Text = "Scan a box tag or select a box below.",
                TextSize = 12
            };
            info.SetTextColor(Color.Black);
            info.SetPadding(0, 8, 0, 12);
            layout.AddView(info);

            var boxLabel = new TextView(this) { Text = "Assign to box:", TextSize = 13 };
            boxLabel.SetTextColor(Color.Black);
            boxLabel.SetPadding(0, 16, 0, 8);
            layout.AddView(boxLabel);

            var boxRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            boxRow.SetPadding(0, 0, 0, 0);

            var btnMargin = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            btnMargin.SetMargins((int)(4 * density), 0, (int)(4 * density), 0);

            var currentBtn = _uiFactory.CreateStyledButton($"Box {_currentBoxName}", UIFactory.PRIMARY_BLUE);
            currentBtn.Click += (s, e) =>
            {
                _isBoxLocked = false;
                FlushHeldScansToCurrentBox();
            };
            boxRow.AddView(currentBtn, btnMargin);

            var otherBtn = _uiFactory.CreateStyledButton("Select", UIFactory.PRIMARY_BLUE);
            otherBtn.Click += (s, e) =>
            {
                var input = new EditText(this) { Hint = "Box number", InputType = Android.Text.InputTypes.ClassText };
                new AlertDialog.Builder(this)
                    .SetTitle("Assign to box")
                    .SetView(input)
                    .SetPositiveButton("OK", (s2, e2) =>
                    {
                        var boxName = input.Text?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(boxName) && _boxNamesAndIndexes.ContainsKey(boxName))
                        {
                            _currentBoxIndex = _boxNamesAndIndexes[boxName];
                            _currentBoxName = boxName;
                            _isBoxLocked = false;
                            FlushHeldScansToCurrentBox();
                        }
                        else
                        {
                            Toast.MakeText(this, $"Box {boxName} not found", ToastLength.Short)?.Show();
                        }
                    })
                    .SetNegativeButton("Cancel", (s2, e2) => { })
                    .Show();
            };
            boxRow.AddView(otherBtn, btnMargin);
            layout.AddView(boxRow);

            _heldScansDialog = new AlertDialog.Builder(this)
                .SetView(layout)
                .SetNegativeButton("Discard all", (s, e) =>
                {
                    _heldScans.Clear();
                    _heldScansDialog = null;
                    SetDialogActive(false);
                })
                .SetCancelable(false)
                .Create();

            _heldScansDialog.Show();
        }
        private static string FormatAccuracy(float accuracy)
        {
            if (accuracy >= 100) return $"{(int)accuracy}m";
            if (accuracy >= 10) return $"{accuracy:F0}m";
            return $"{accuracy:F1}m";
        }

        private static string FormatSyncAgo(DateTime lastSyncUtc)
        {
            if (lastSyncUtc <= DateTime.MinValue) return "never";
            var ago = DateTime.UtcNow - lastSyncUtc;
            if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s ago";
            if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
            if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
            return $"{(int)ago.TotalDays}d ago";
        }

        // Counting the queue touches a file, and the status line redraws on every scan and redraw,
        // so the answer is held for a few seconds. It only has to be current enough to read.
        private int _unsentCache;
        private DateTime _unsentCacheAtUtc;

        /// <summary>Everything on this phone the server hasn't got: boxes, bird details, birds
        /// chipped out of signal, box notes, watched flags and day notes. One number, because the
        /// question someone actually has before walking away from a colony is "is it all in?".</summary>
        private int UnsentTotal()
        {
            if ((DateTime.UtcNow - _unsentCacheAtUtc).TotalSeconds < 3) return _unsentCache;
            int n = 0;
            try
            {
                n += _colonyState.PendingUploadCount;
                n += _colonyState.PendingBiometricCount;
                n += _colonyState.PendingDayNotes.Count + (_colonyState.DailyLabelPendingUpload ? 1 : 0);
                n += _boxNotes.Values.Count(b => b.NotesPendingUpload || b.WatchedPendingUpload);
                n += _dataStorageService.LoadQueuedChips(this).Count;
            }
            catch { }
            _unsentCache = n;
            _unsentCacheAtUtc = DateTime.UtcNow;
            return n;
        }

        /// <summary>The button's label, longest first — FitLabel picks the most descriptive one the
        /// button's own width allows. It reads as the state it's in: what is waiting, or how long
        /// ago everything went in. "Sync" alone told you nothing you didn't already know.</summary>
        private string[] SyncButtonVariants()
        {
            if (_isDownloadingCsvData) return new[] { "Syncing…", "Sync…" };
            if (!_appSettings.IsAuthenticated) return new[] { "Log in to sync", "Log in", "Sync" };

            int unsent = UnsentTotal();
            if (unsent > 0) return new[] { $"Send {unsent} unsent", $"Send {unsent}", $"{unsent}↑" };

            var lastFull = _colonyState?.LastSyncedUtc ?? DateTime.MinValue;
            var mostRecent = _lastSyncCheckUtc > lastFull ? _lastSyncCheckUtc : lastFull;
            if (mostRecent == DateTime.MinValue || mostRecent == default) return new[] { "Sync now", "Sync" };
            return new[] { $"All in · {FormatSyncAgo(mostRecent)}", $"In · {FormatSyncAgo(mostRecent)}", "Sync" };
        }

        private void UpdateStatusText(string? bluetoothStatus = null)
        {
            // BT status
            string bt;
            if (_bluetoothManager == null)
                bt = "BT:off";
            else if (_bluetoothManager.IsConnected)
            {
                var nick = _appSettings.RememberedScanners
                    .FirstOrDefault(s => s.Address == _bluetoothManager.ConnectedDeviceAddress)?.DisplayName
                    ?? _bluetoothManager.ConnectedDeviceName;
                bt = string.IsNullOrWhiteSpace(nick) ? "BT🔗" : $"BT🔗 ({nick})";
            }
            else if (_bluetoothManager.IsConnecting)
                bt = "BT🔄";
            else
                bt = bluetoothStatus != null && bluetoothStatus.Contains("Retry") ? "BT🔄" : "BT:off";

            // GPS status
            string gps;
            if (_gpsAccuracy > 0)
                gps = $"📍{FormatAccuracy(_gpsAccuracy)}";
            else
                gps = "GPS:off";

            // Sync status — use most recent of full sync or successful poll check
            string sync;
            if (_colonyState != null && _colonyState.PendingUploadCount > 0)
            {
                var pendingNames = string.Join(",", _colonyState.PendingObservations.Where(p => p.IsPendingUpload).Select(p => p.BoxName));
                sync = $"Uploading:{_colonyState.PendingUploadCount} [{pendingNames}]";
            }
            else
            {
                var lastFull = _colonyState?.LastSyncedUtc ?? DateTime.MinValue;
                var mostRecent = _lastSyncCheckUtc > lastFull ? _lastSyncCheckUtc : lastFull;
                sync = $"sync:{FormatSyncAgo(mostRecent)}";
            }

            RunOnUiThread(() =>
            {
                if (_statusText != null)
                {
                    _statusText.Text = $"{bt}  {gps}  {sync}";

                    if (_bluetoothManager?.IsConnected == true && _gpsAccuracy > 0)
                        _statusText.SetTextColor(UIFactory.SUCCESS_GREEN);
                    else if (_bluetoothManager?.IsConnected == true)
                        _statusText.SetTextColor(UIFactory.WARNING_YELLOW);
                    else
                        _statusText.SetTextColor(Color.Black);
                }

                // The button carries the same truth as the status line, in the place someone is
                // already looking before they press it — and turns amber while work is unsent, so
                // "is it all in?" is answerable across the shed.
                if (_syncButton != null && !_isDownloadingCsvData)
                {
                    var variants = SyncButtonVariants();
                    FitLabelNow(_syncButton, variants);
                    _syncButton.Background = _uiFactory.CreateRoundedBackground(
                        UnsentTotal() > 0 ? UIFactory.WARNING_YELLOW : UIFactory.PRIMARY_BLUE, 8);
                }
            });
        }
        private void LoadJsonDataFromFile()
        {
            try
            {
                // Check permissions first
                if (!CheckExternalStoragePermissions())
                {
                    var sdkVersion = (int)Android.OS.Build.VERSION.SdkInt;

                    if (OperatingSystem.IsAndroidVersionAtLeast(30)) // Android 11+
                    {
                        Toast.MakeText(this, "⚠️ Android 11+ detected!\n\nFor file access, please:\n1. Go to Settings > Apps > PenguinMonitor\n2. Enable 'All files access'", ToastLength.Long)?.Show();

                        // Try to open the manage storage settings
                        try
                        {
                            var intent = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission);
                            intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                            StartActivity(intent);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to open storage settings: {ex.Message}");
                        }
                        return;
                    }
                    else
                    {
                        Toast.MakeText(this, "Storage permission required to load files. Please grant permission and try again.", ToastLength.Long)?.Show();

                        // Request permission if not granted (Android 6-10)
                        if (OperatingSystem.IsAndroidVersionAtLeast(23))
                        {
                            RequestPermissions(new string[] { Android.Manifest.Permission.ReadExternalStorage }, 2);
                        }
                        return;
                    }
                }
                var downloadsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
                if (string.IsNullOrEmpty(downloadsPath))
                {
                    Toast.MakeText(this, "Downloads directory not accessible", ToastLength.Long)?.Show();
                    return;
                }

                // Force media scanner to update Downloads folder
                try
                {
                    var intent = new Intent(Intent.ActionMediaScannerScanFile);
                    intent.SetData(Android.Net.Uri.FromFile(new Java.IO.File(downloadsPath)));
                    SendBroadcast(intent);

                    // Also try scanning the entire Downloads directory
                    var mediaScanIntent = new Intent(Intent.ActionMediaMounted);
                    mediaScanIntent.SetData(Android.Net.Uri.FromFile(new Java.IO.File(downloadsPath)));
                    SendBroadcast(mediaScanIntent);

                    // Give it a moment to scan
                    System.Threading.Thread.Sleep(500);
                }
                catch (Exception scanEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Media scanner failed: {scanEx.Message}");
                }

                // Debug: Show what files are actually detected
                var allFiles = Directory.GetFiles(downloadsPath, "*", SearchOption.TopDirectoryOnly);
                System.Diagnostics.Debug.WriteLine($"Downloads path: {downloadsPath}");
                System.Diagnostics.Debug.WriteLine($"Total files found: {allFiles.Length}");

                var debugInfo = new System.Text.StringBuilder();
                debugInfo.AppendLine($"📂 Downloads: {allFiles.Length} files found");
                debugInfo.AppendLine($"🤖 Android API: {(int)Android.OS.Build.VERSION.SdkInt}");

                foreach (var file in allFiles.Take(10)) // Show first 10 files
                {
                    var fileInfo = new FileInfo(file);
                    System.Diagnostics.Debug.WriteLine($"File: {fileInfo.Name}, Size: {fileInfo.Length}, Created: {fileInfo.CreationTime}, LastWrite: {fileInfo.LastWriteTime}");
                    debugInfo.AppendLine($"• {fileInfo.Name} ({fileInfo.Length / 1024}KB)");
                }

                if (allFiles.Length > 10)
                {
                    debugInfo.AppendLine($"... and {allFiles.Length - 10} more files");
                }

                // Check permissions
                var hasReadPermission = CheckExternalStoragePermissions();
                debugInfo.AppendLine($"📋 Read Permission: {(hasReadPermission ? "✅ Granted" : "❌ Denied")}");

                // Toast the debug info to user
                Toast.MakeText(this, debugInfo.ToString(), ToastLength.Long)?.Show();

                // Look for JSON files (try multiple patterns)
                var jsonFiles = allFiles.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToArray();

                var files = jsonFiles
                    .OrderByDescending(f => File.GetLastWriteTime(f)) // Use LastWriteTime instead of CreationTime
                    .ToArray();

                if (files.Length == 0)
                {
                    var message = $"No JSON files found.\n\n" +
                                 $"📂 Total files: {allFiles.Length}\n" +
                                 $"📋 Permissions: {(hasReadPermission ? "✅" : "❌")}\n" +
                                 $"🤖 Android API: {(int)Android.OS.Build.VERSION.SdkInt}\n" +
                                 $"📁 Path: {downloadsPath}";

                    Toast.MakeText(this, message, ToastLength.Long)?.Show();
                    return;
                }
                // Show file selection dialog
                ShowFileSelectionDialog(files);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to browse files: {ex.Message}", ToastLength.Long)?.Show();
                System.Diagnostics.Debug.WriteLine($"LoadJsonDataFromFile error: {ex}");
            }
        }
        private void ShowFileSelectionDialog(string[] files)
        {
            var fileNames = files.Select(f => 
            {
                var fileName = System.IO.Path.GetFileName(f);
                var fileInfo = new FileInfo(f);
                var fileSize = fileInfo.Length / 1024; // Size in KB
                var creationTime = fileInfo.CreationTime.ToString("MMM dd, HH:mm");
                return $"{fileName}\n{creationTime} • {fileSize} KB";
            }).ToArray();

            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Select JSON File to Load");
            
            builder.SetItems(fileNames, (sender, args) =>
            {
                var selectedFile = files[args.Which];
                var fileName = System.IO.Path.GetFileName(selectedFile);
                
                LoadJsonFileData(selectedFile);
            });

            builder.SetNegativeButton("Cancel", (sender, args) => { });
            
            var dialog = builder.Create();
            dialog?.Show();
        }
        private void LoadJsonFileData(string filePath)
        {
            var json = File.ReadAllText(filePath);
            LoadJsonData(json, System.IO.Path.GetFileName(filePath));
        }

        private void ExitHistoricalView()
        {
            _isHistoricalView = false;
            _historicalFilename = "";
            _historicalBoxes.Clear();

            // Re-enable bluetooth/GPS if setting is on
            if (_appSettings?.IsBlueToothEnabled == true)
            {
                InitializeBluetooth();
                InitializeGPS();
            }

            _isBoxLocked = true;
            CreateBoxSetsDictionary();
            if (_boxNamesAndIndexes.Count > 0)
                JumpToBox(_boxNamesAndIndexes.First().Key);
            DrawPageLayouts();
            UpdateStatusText();
        }

        /// <summary>
        /// Get the box observation to display for a given box name.
        /// In historical view, returns from _historicalBoxes. Otherwise from colony state.
        /// </summary>
        private BoxObservation? GetDisplayBoxData(string boxName)
        {
            if (_isHistoricalView)
                return _historicalBoxes.TryGetValue(boxName, out var hb) ? hb : null;
            return _colonyState.GetTodayForBox(boxName);
        }

        private void LoadJsonData(string json, string filename="")
        {
            try
            {
                var loadedData = JToken.Parse(json);

                int boxCount = 0;
                int birdCount = 0;

                if (loadedData == null)
                {
                    Toast.MakeText(this, "❌ Invalid JSON file format", ToastLength.Long)?.Show();
                    return;
                }
                if (loadedData["BoxData"] == null)
                {
                    Toast.MakeText(this, "❌ No box data found in JSON file", ToastLength.Long)?.Show();
                    return;
                }

                _historicalFilename = filename.Replace("PenguinMonitor", "").Replace("Nestcheck", "").Trim();
                _isHistoricalView = true;
                _historicalBoxes.Clear();

                // Disable bluetooth/GPS temporarily
                _bluetoothManager?.Dispose();
                _bluetoothManager = null;
                _locationManager?.RemoveUpdates(this);
                _gpsAccuracy = -1;

                var boxDatas = loadedData["BoxData"] as JObject;
                foreach (var boxItem in boxDatas)
                {
                    string boxName = boxItem.Key;
                    var dataNode = boxItem.Value;
                    var boxData = new BoxObservation
                    {
                        Adults = dataNode["Adults"]?.Value<int>() ?? 0,
                        Eggs = dataNode["Eggs"]?.Value<int>() ?? 0,
                        Chicks = dataNode["Chicks"]?.Value<int>() ?? 0,
                        GateStatus = (dataNode["GateStatus"]?.Value<string>() ?? "").Replace("gate up", "Gate up").Replace("regate", "Regate"),
                        BreedingStatus = dataNode["BreedingChance"]?.Value<string>(),
                        Notes = dataNode["Notes"]?.Value<string>() ?? "",
                        WhenDataCollectedUtc = dataNode["whenDataCollectedUtc"]?.Value<DateTime>().ToUniversalTime() ?? DateTime.MinValue.ToUniversalTime(),
                        IsPendingUpload = false,
                        BoxName = boxName,
                    };
                    var scannedIdsNode = dataNode["ScannedIds"];
                    if (scannedIdsNode != null)
                    {
                        foreach (var scanItem in scannedIdsNode)
                        {
                            boxData.ScannedIds.Add(new ScanRecord
                            {
                                BirdId = scanItem?["BirdId"]?.Value<string>() ?? "",
                                Timestamp = scanItem?["Timestamp"]?.Value<DateTime>() ?? DateTime.UtcNow,
                                Latitude = scanItem?["Latitude"]?.Value<double>() ?? 0,
                                Longitude = scanItem?["Longitude"]?.Value<double>() ?? 0,
                                Accuracy = scanItem?["Accuracy"]?.Value<float>() ?? -1
                            });
                            birdCount++;
                        }
                    }
                    _historicalBoxes[boxName] = boxData;
                    boxCount++;
                }

                _isBoxLocked = true;
                DrawPageLayouts();
                UpdateStatusText();

                Toast.MakeText(this, $"📂 Viewing {boxCount} boxes, {birdCount} birds", ToastLength.Short)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to load JSON: {ex.Message}", ToastLength.Long)?.Show();
            }
        }
        private void ShowBoxDataSummary()
        {
            if (_colonyState.TodayBoxes.Count == 0 && _colonyState.PendingObservations.Count == 0 && _colonyState.PreviousBoxes.Count == 0)
                Toast.MakeText(this, "No boxes with data", ToastLength.Short)?.Show();
            else
                ShowConfirmationDialog(
                    "📊 Today's Statistics",
                    GetSummaryText(),
                    ("OK", () => { }),
                    null);
        }
        private string GetSummaryText()
        {
            var todayBoxes = _isHistoricalView
                ? _historicalBoxes
                : new Dictionary<string, BoxObservation>(_colonyState.TodayBoxes);
            if (!_isHistoricalView)
            {
                var nzToday = NzToday;
                foreach (var obs in _colonyState.PendingObservations)
                {
                    if (!string.IsNullOrEmpty(obs.BoxName) && ToNzTime(obs.WhenDataCollectedUtc).Date == nzToday)
                        todayBoxes[obs.BoxName] = obs;
                }
            }

            var totalScannedBirds = todayBoxes.Values.Sum(box => box.ScannedIds.Count(s => !s.BirdId.StartsWith("NOSCAN_")));
            var totalNoScans = todayBoxes.Values.Sum(box => box.ScannedIds.Count(s => s.BirdId.StartsWith("NOSCAN_")));
            var totalAdults = todayBoxes.Values.Sum(box => box.Adults);
            var totalEggs = todayBoxes.Values.Sum(box => box.Eggs);
            var totalChicks = todayBoxes.Values.Sum(box => box.Chicks);
            var gateUpCount = todayBoxes.Values.Count(box => box.GateStatus == "Gate up");
            var regateCount = todayBoxes.Values.Count(box => box.GateStatus == "Regate");
            var adultMismatch = totalAdults - totalNoScans - totalScannedBirds;
            var totalFailedEggs = todayBoxes.Values.Sum(box => box.FailedEggs ?? 0);
            var totalDeadChicks = todayBoxes.Values.Sum(box => box.DeadChicks ?? 0);

            // New eggs and new chicks are both counted box by box against that box's own last
            // visit, then added up. A colony-wide "today minus last time" would net one box's new
            // egg off against another's loss and report nothing laid on a day two clutches started.
            // Historical view has no matching previous set, so both stay plain totals.
            //
            // A hatch shows up only on the chicks line, which is the honest reading of it: the egg
            // count goes down, so it contributes no new egg, and the chick it became is the new
            // thing in the box.
            int newEggs = 0, newChicks = 0;
            if (!_isHistoricalView)
                foreach (var kv in todayBoxes)
                {
                    _colonyState.PreviousBoxes.TryGetValue(kv.Key, out var prev);
                    newEggs += Math.Max(0, kv.Value.Eggs - (prev?.Eggs ?? 0));
                    newChicks += Math.Max(0, kv.Value.Chicks - (prev?.Chicks ?? 0));
                }

            var pending = _colonyState.PendingUploadCount;

            string summary = $"📦 {todayBoxes.Count} boxes today\n" +
                         (pending > 0 ? $"⏳ {pending} boxes pending upload\n" : "") +
                         $"🐧 {totalScannedBirds} bird scans" + (totalNoScans > 0 ? $" + {totalNoScans} no-scan" : "") + "\n" +
                         $"👥 {totalAdults} adults\n" +
                         $"🥚 {totalEggs} eggs" + (newEggs > 0 ? $" - {newEggs} new" : "") + "\n" +
                         $"🐣 {totalChicks} chicks" + (newChicks > 0 ? $" - {newChicks} new" : "") + "\n" +
                         (totalFailedEggs > 0 ? $"🥚✗ {totalFailedEggs} failed egg{(totalFailedEggs != 1 ? "s" : "")}\n" : "") +
                         (totalDeadChicks > 0 ? $"🐣✗ {totalDeadChicks} dead chick{(totalDeadChicks != 1 ? "s" : "")}\n" : "") +
                         $"🚪 Gate: {gateUpCount} up, {regateCount} regate";
            return summary;
        }
        private bool _isDownloadingCsvData = false;
        private void OnSyncClick(object? sender, EventArgs e) => StartSync(silent: false);

        private void StartSync(bool silent = false)
        {
            if (_isDownloadingCsvData)
                return;

            _isDownloadingCsvData = true;
            UpdateStatusText("Syncing...");
            if (_syncButton != null)
            {
                _refitSyncLabel?.Invoke();
                _syncButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.WARNING_YELLOW, 8);
                _syncButton.Enabled = false;
            }

            // Show sync dialog — transitions from progress to results
            var progressMessages = new[] { "📦 Boxes...", "🐧 Penguins...", "📍 Tags...", "🌐 Web view..." };
            var cancelled = false;
            AlertDialog? syncDialog = null;

            if (!silent)
            {
                syncDialog = new AlertDialog.Builder(this)
                    .SetTitle("Syncing")
                    .SetMessage(string.Join("\n", progressMessages))
                    .SetNegativeButton("Cancel", (s, e) => { cancelled = true; })
                    .SetCancelable(false)
                    .Create();
                syncDialog?.Show();

                // Manual sync also clears + re-syncs the embed web view's cached colony data.
                RefreshEmbedWebView(status =>
                {
                    progressMessages[3] = $"🌐 {status}";
                    RunOnUiThread(() => syncDialog?.SetMessage(string.Join("\n", progressMessages)));
                });
            }

            _ = Task.Run(async () =>
            {
                DataStorageService.SyncResult result;
                try
                {
                    result = await _dataStorageService.SyncWithServer(this, _colonyState, _appSettings, _boxNamesAndIndexes?.Keys,
                        onLineProgress: (lineIndex, status) =>
                        {
                            if (lineIndex >= 0 && lineIndex < progressMessages.Length)
                            {
                                var icon = lineIndex == 0 ? "📦" : lineIndex == 1 ? "🐧" : "📍";
                                progressMessages[lineIndex] = $"{icon} {status}";
                            }
                            RunOnUiThread(() => syncDialog?.SetMessage(string.Join("\n", progressMessages)));
                        },
                        isCancelled: () => cancelled);

                    await ApplyPostSync(result);
                }
                catch (Exception ex)
                {
                    result = new DataStorageService.SyncResult { Error = ex.Message };
                }

                new Handler(Looper.MainLooper).Post(() =>
                {
                    _isDownloadingCsvData = false;
                    if (_syncButton != null)
                    {
                        _refitSyncLabel?.Invoke();
                        _syncButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 8);
                        _syncButton.Enabled = true;
                    }
                    CreateBoxSetsDictionary();
                    RebuildSettingsCard();   // pick up the freshly-synced people list without a restart
                    DrawPageLayouts();
                    // Refresh the previous-obs card explicitly so an updated breeding string
                    // (or summary) from this sync shows without a manual collapse/expand.
                    UpdatePreviousObsSummary();

                    if (_colonyState.GetTodayForBox(_currentBoxName) != null)
                        buildScannedIdsLayout(_colonyState.GetTodayForBox(_currentBoxName).ScannedIds);

                    // Start background polling after successful sync
                    if (result.Error == null && !result.AuthFailed)
                    {
                        var token = _appSettings?.AuthToken;
                        if (!string.IsNullOrEmpty(token))
                            StartPolling(token);
                    }

                    if (syncDialog != null)
                    {
                        if (cancelled)
                        {
                            syncDialog.Dismiss();
                        }
                        else
                        {
                            // A biometric that didn't upload is still queued — that's a partial sync,
                            // not a clean one, and saying "Synced" over it loses the bird's record.
                            bool hasErrors = !string.IsNullOrEmpty(result.Error) || result.TagSyncResult?.Error != null
                                || result.UploadErrors > 0 || result.BiometricUploadErrors > 0
                                || result.PayloadWarnings.Count > 0;
                            // Chip warnings aren't failures — the sync worked, but a queued bird
                            // needs a human look (rejected, or given a different number).
                            bool hasChipWarnings = result.ChipWarnings?.Count > 0;
                            syncDialog.SetTitle(hasErrors ? "Sync — Partial" : hasChipWarnings ? "Synced — check birds" : "Synced");
                            if (hasErrors || hasChipWarnings)
                            {
                                var details = new List<string>(progressMessages);
                                if (!string.IsNullOrEmpty(result.Error)) details.Add($"Error: {result.Error}");
                                if (result.TagSyncResult?.Error != null) details.Add($"Tags: {result.TagSyncResult.Error}");
                                if (result.UploadErrors > 0)
                                {
                                    details.Add($"Upload errors: {result.UploadErrors}");
                                    details.AddRange(result.UploadErrorDetails.Take(5));
                                }
                                if (result.BiometricUploadErrors > 0)
                                {
                                    details.Add($"Bird details not uploaded: {result.BiometricUploadErrors}");
                                    details.AddRange(result.BiometricErrors.Take(3));
                                }
                                // The rest of the payload still loaded — say what didn't, so a server
                                // change reads as a named gap rather than data quietly going missing.
                                if (result.PayloadWarnings.Count > 0)
                                {
                                    details.Add($"Server sent {result.PayloadWarnings.Count} field(s) this app couldn't read:");
                                    details.AddRange(result.PayloadWarnings.Take(3));
                                }
                                if (hasChipWarnings) details.AddRange(result.ChipWarnings!);
                                syncDialog.SetMessage(string.Join("\n", details));
                            }
                            var okBtn = syncDialog.GetButton((int)Android.Content.DialogButtonType.Negative);
                            if (okBtn != null) okBtn.Text = "OK";
                        }
                    }

                    if (result.Conflicts != null && result.Conflicts.Count > 0)
                    {
                        syncDialog?.Dismiss();
                        ShowConflictDialog(result.Conflicts);
                    }

                    if (result.AuthFailed)
                    {
                        syncDialog?.Dismiss();
                        ShowLoginPrompt();
                    }
                });
            });
        }


        /// <summary>
        /// Build a styled comparison dialog with server (orange) and local (black) cards.
        /// Uses app design language: card backgrounds, styled buttons.
        /// </summary>
        private void ShowComparisonDialog(string title, BoxObservation serverObs, BoxObservation localObs, Action onReplace, Action onDiscard)
        {
            SetDialogActive(true);
            var outerCard = _uiFactory.CreateCard(padding: 16);
            outerCard.SetGravity(GravityFlags.Top);

            // Title
            var titleView = new TextView(this) { Text = title, TextSize = 20 };
            titleView.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            titleView.SetTextColor(UIFactory.TEXT_PRIMARY);
            titleView.SetPadding(0, 0, 0, 12);
            outerCard.AddView(titleView);

            // Server version (orange card)
            var serverLabel = new TextView(this) { Text = "Current (server)", TextSize = 12 };
            serverLabel.SetTextColor(UIFactory.TEXT_SECONDARY);
            outerCard.AddView(serverLabel);

            var serverCard = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            serverCard.SetPadding(12, 8, 12, 8);
            serverCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4, borderColour: UIFactory.WARNING_YELLOW);
            var serverParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            serverParams.SetMargins(0, 4, 0, 8);
            serverCard.LayoutParameters = serverParams;
            serverCard.AddView(BuildObsDetailView(serverObs, showBoxLink: false));
            outerCard.AddView(serverCard);

            // Arrow
            var arrow = new TextView(this) { Text = "▼", TextSize = 18, Gravity = GravityFlags.Center };
            arrow.SetTextColor(UIFactory.TEXT_SECONDARY);
            arrow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            outerCard.AddView(arrow);

            // Local version (black card)
            var localLabel = new TextView(this) { Text = "Your edit", TextSize = 12 };
            localLabel.SetTextColor(UIFactory.TEXT_SECONDARY);
            localLabel.SetPadding(0, 4, 0, 0);
            outerCard.AddView(localLabel);

            var localCard = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            localCard.SetPadding(12, 8, 12, 8);
            localCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4, borderColour: Color.Black);
            var localParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            localParams.SetMargins(0, 4, 0, 16);
            localCard.LayoutParameters = localParams;
            localCard.AddView(BuildObsDetailView(localObs, showBoxLink: false));
            outerCard.AddView(localCard);

            // Button row
            var buttonRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            buttonRow.SetGravity(GravityFlags.Center);
            var buttonRowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            buttonRow.LayoutParameters = buttonRowParams;

            var discardButton = _uiFactory.CreateStyledButton("Discard", UIFactory.DANGER_RED);
            discardButton.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            ((LinearLayout.LayoutParams)discardButton.LayoutParameters).SetMargins(0, 0, 8, 0);

            var replaceButton = _uiFactory.CreateStyledButton("Replace", UIFactory.PRIMARY_BLUE);
            replaceButton.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            ((LinearLayout.LayoutParams)replaceButton.LayoutParameters).SetMargins(8, 0, 0, 0);

            buttonRow.AddView(discardButton);
            buttonRow.AddView(replaceButton);
            outerCard.AddView(buttonRow);

            var scrollView = new ScrollView(this);
            scrollView.AddView(outerCard);

            var dialog = new AlertDialog.Builder(this)
                .SetView(scrollView)
                .SetCancelable(false)
                .Create();

            discardButton.Click += (s, e) => { dialog?.Dismiss(); SetDialogActive(false); onDiscard(); };
            replaceButton.Click += (s, e) => { dialog?.Dismiss(); SetDialogActive(false); onReplace(); };
            dialog?.Show();
        }

        // True when a server observation carries the same data as our pending one. Uses the same
        // signature as the download-first reconcile (DataStorageService.BoxSignature) so the two
        // agree on what "identical" means — build a BoxObservation from the server row and compare.
        private bool ObsContentEqual(DataStorageService.SyncConflictObs server, BoxObservation local)
        {
            var s = BoxObservation.FromServerData(server.observation_id, 0, server.observation_time_utc ?? "",
                server.adults, server.eggs, server.chicks, server.breeding_status, server.gate_status,
                server.notes ?? "", server.monitor_filename, server.observer_name,
                server.failed_eggs, server.dead_chicks);
            if (server.scans != null)
                foreach (var sc in server.scans) s.ScannedIds.Add(new ScanRecord { BirdId = DataStorageService.PitFull(sc.pit_id) });
            for (int ns = 0; ns < server.no_scan; ns++) s.ScannedIds.Add(new ScanRecord { BirdId = $"NOSCAN_{ns + 1}" });
            return DataStorageService.BoxSignature(s) == DataStorageService.BoxSignature(local);
        }

        // Adopt the server's copy of a box (data already equals ours): drop the pending upload
        // and store the server row — crucially with its observation_id — so it's no longer
        // re-uploaded and future edits confirm against it.
        private void AdoptServerObs(string boxName, DataStorageService.SyncConflictObs server)
        {
            _colonyState.PendingObservations.RemoveAll(p => p.BoxName == boxName && p.IsPendingUpload);
            var restored = BoxObservation.FromServerData(
                server.observation_id, 0, server.observation_time_utc ?? "",
                server.adults, server.eggs, server.chicks,
                server.breeding_status, server.gate_status, server.notes ?? "",
                server.monitor_filename, server.observer_name, server.failed_eggs, server.dead_chicks);
            restored.BoxName = boxName;
            if (server.scans != null)
                foreach (var scan in server.scans)
                    restored.ScannedIds.Add(new ScanRecord { BirdId = DataStorageService.PitFull(scan.pit_id) });
            for (int ns = 0; ns < server.no_scan; ns++)
                restored.ScannedIds.Add(new ScanRecord { BirdId = $"NOSCAN_{ns + 1}" });
            restored.IsPendingUpload = false;
            if (MainActivity.ToNzTime(restored.WhenDataCollectedUtc).Date == NzToday)
                _colonyState.TodayBoxes[boxName] = restored;
        }

        private void ShowConflictDialog(List<DataStorageService.SyncConflict> conflicts)
        {
            // Filter out conflicts where server data hasn't changed since local confirmation
            conflicts = conflicts.Where(c =>
            {
                var boxName = c.box_name ?? "";
                if (_confirmedAgainstServerObsId.TryGetValue(boxName, out int confirmedId)
                    && c.server != null && c.server.observation_id == confirmedId)
                {
                    _confirmedAgainstServerObsId.Remove(boxName);
                    return false;
                }
                return true;
            }).ToList();

            // Deduplicate by box name — keep only the latest conflict per box
            var seen = new HashSet<string>();
            conflicts = conflicts.Where(c => seen.Add(c.box_name ?? "")).ToList();

            // Never ask to replace data with an identical copy. On a patchy connection a write
            // can commit server-side while the reply is lost, so the box stays queued and
            // re-uploads; the server then reports its own just-saved row as a "conflict". When
            // the server's data matches our pending data exactly, adopt the server copy (records
            // its observation_id and clears the pending box), so it resolves silently instead of
            // prompting — and never re-conflicts, since the box now knows the server id.
            bool resolvedAny = false;
            var stillConflicting = new List<DataStorageService.SyncConflict>();
            foreach (var c in conflicts)
            {
                var boxName = c.box_name ?? "";
                var localPending = DataStorageService.PendingForConflict(_colonyState, c);
                if (c.server != null && localPending != null && ObsContentEqual(c.server, localPending))
                {
                    AdoptServerObs(boxName, c.server);
                    resolvedAny = true;
                }
                else stillConflicting.Add(c);
            }
            if (resolvedAny)
            {
                DataStorageService.SaveColonyState(this, _colonyState);
                UpdateSyncButtonLabel();
                DrawPageLayouts();
            }
            conflicts = stillConflicting;

            if (conflicts.Count == 0) return;

            // Show one dialog per conflict (sequential)
            void ShowNext(int idx)
            {
                if (idx >= conflicts.Count) return;
                var conflict = conflicts[idx];
                var boxName = conflict.box_name ?? "?";
                var server = conflict.server;

                BoxObservation serverObs = null;
                if (server != null)
                {
                    serverObs = BoxObservation.FromServerData(
                        server.observation_id, 0, server.observation_time_utc ?? "",
                        server.adults, server.eggs, server.chicks,
                        server.breeding_status, server.gate_status, server.notes ?? "", server.monitor_filename,
                        server.observer_name, server.failed_eggs, server.dead_chicks);
                    serverObs.BoxName = boxName;
                    if (server.scans != null)
                        foreach (var scan in server.scans)
                            serverObs.ScannedIds.Add(new ScanRecord { BirdId = DataStorageService.PitFull(scan.pit_id) });
                    for (int ns = 0; ns < server.no_scan; ns++)
                        serverObs.ScannedIds.Add(new ScanRecord { BirdId = $"NOSCAN_{ns + 1}" });
                }

                var local = DataStorageService.PendingForConflict(_colonyState, conflict);
                if (serverObs == null || local == null) { ShowNext(idx + 1); return; }
                var conflictNzDate = DataStorageService.ConflictNzDate(conflict);

                ShowComparisonDialog($"Confirm edit: Box {boxName}", serverObs, local,
                    onReplace: () =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var (_, error) = await _dataStorageService.UploadConfirmedEdits(_colonyState, _appSettings,
                                new List<(string, string?)> { (boxName, conflictNzDate) });
                            new Handler(Looper.MainLooper).Post(() =>
                            {
                                DataStorageService.SaveColonyState(this, _colonyState);
                                UpdateSyncButtonLabel();
                                DrawPageLayouts();
                                if (error == null) { ShowNext(idx + 1); return; }
                                // The replace didn't land — say why before moving on; the box stays queued.
                                new AlertDialog.Builder(this)
                                    .SetTitle($"Box {boxName} not replaced")
                                    .SetMessage($"{error}\n\nYour entry is still on this phone and will be offered again next sync.")
                                    .SetCancelable(false)
                                    .SetPositiveButton("OK", (s3, e3) => ShowNext(idx + 1))
                                    .Show();
                            });
                        });
                    },
                    onDiscard: () =>
                    {
                        // Only the round this conflict is about. A box can have other days queued
                        // behind it after a spell offline, and those weren't offered up for discard.
                        _colonyState.PendingObservations.Remove(local);
                        if (server != null)
                        {
                            var restored = BoxObservation.FromServerData(
                                server.observation_id, 0, server.observation_time_utc ?? "",
                                server.adults, server.eggs, server.chicks,
                                server.breeding_status, server.gate_status,
                                server.notes ?? "", server.monitor_filename, server.observer_name,
                                server.failed_eggs, server.dead_chicks);
                            restored.BoxName = boxName;
                            if (server.scans != null)
                                foreach (var scan in server.scans)
                                    restored.ScannedIds.Add(new ScanRecord { BirdId = DataStorageService.PitFull(scan.pit_id) });
                            for (int ns = 0; ns < server.no_scan; ns++)
                                restored.ScannedIds.Add(new ScanRecord { BirdId = $"NOSCAN_{ns + 1}" });
                            restored.IsPendingUpload = false;
                            // Today's board only takes today's data — an older day's row belongs
                            // in the previous-visit card, not in the box you're standing at.
                            if (ToNzTime(restored.WhenDataCollectedUtc).Date == NzToday)
                                _colonyState.TodayBoxes[boxName] = restored;
                            else
                                _colonyState.PreviousBoxes[boxName] = restored;
                        }
                        DataStorageService.SaveColonyState(this, _colonyState);
                        UpdateSyncButtonLabel();
                        DrawPageLayouts();
                        ShowNext(idx + 1);
                    });
            }
            ShowNext(0);
        }

        private void ShowLoginPrompt()
        {
            new AlertDialog.Builder(this)
                .SetTitle("Login Required")
                .SetMessage("Connect your Wildwatch account to sync data.")
                .SetPositiveButton("Login", (s, e) => {
                    StartActivity(new Android.Content.Intent(Android.Content.Intent.ActionView,
                        Android.Net.Uri.Parse($"{DataStorageService.WILDWATCH_BASE_URL}/auth.php")));
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }
        private void CreateUI()
        {
            _uiFactory = new UIFactory(this);
            selectedPage = UIFactory.selectedPage.BoxDataSingle;
            _isBoxLocked = true;
            _rootScrollView = new ScrollView(this);
            _rootScrollView.SetBackgroundColor(UIFactory.LIGHT_GRAY);

            // Initialize gesture detector and apply to ScrollView
            _gestureDetector = new GestureDetector(this, new SwipeGestureDetector(this));
            _rootScrollView.Touch += OnScrollViewTouch;

            var parentLinearLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            // Box tags mode banner — first thing on the page so the mode is unmistakable.
            _tagModeBanner = new TextView(this)
            {
                Text = "✏️ BOX TAGS MODE",
                TextSize = 15,
                Gravity = GravityFlags.Center,
            };
            _tagModeBanner.SetTextColor(Color.White);
            _tagModeBanner.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _tagModeBanner.SetPadding(16, 14, 16, 14);
            _tagModeBanner.Background = _uiFactory.CreateRoundedBackground(TAG_MODE_PURPLE, 8);
            var bannerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            bannerParams.SetMargins(0, 0, 0, 10);
            _tagModeBanner.LayoutParameters = bannerParams;
            _tagModeBanner.Visibility = ViewStates.Gone;
            parentLinearLayout.AddView(_tagModeBanner);

            var headerStatusSettingsCard = _uiFactory.CreateCard(padding: 12);
            headerStatusSettingsCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4);            

            _titleCard = _uiFactory.CreateCard(Android.Widget.Orientation.Horizontal, padding: 0, borderWidth: 0);
            var titleCard = _titleCard;
            titleCard.SetGravity(GravityFlags.Center);

            _expandSettingsButton = new ImageButton(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            };
            var expandSettingsImageButton = _expandSettingsButton;
            expandSettingsImageButton.SetImageResource(Resource.Drawable.unfold);
            expandSettingsImageButton.SetBackgroundColor(Color.Transparent);
            expandSettingsImageButton.Click += (s,e) => {
                // Settings is unreachable in box tags mode — the only way out is the
                // "Finish editing box details" bar, so the mode can't be left half-done.
                if (_appSettings.EditBoxTagsMode) return;
                if (_settingsCard.Visibility == ViewStates.Gone)
                {
                    _settingsCard.Visibility = ViewStates.Visible;
                    expandSettingsImageButton.SetImageResource(Resource.Drawable.fold);
                }
                else
                {
                    _settingsCard.Visibility = ViewStates.Gone;
                    expandSettingsImageButton.SetImageResource(Resource.Drawable.unfold);
                }
            };
            titleCard.AddView(expandSettingsImageButton);

            // Add a spacer that expands to fill available space
            var spacer = new View(this);
            spacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 3f);
            titleCard.AddView(spacer);

            ImageView iconView = new ImageView(this);
            iconView.SetPadding(0, 0, 0, 0);
            iconView.SetImageResource(Resource.Mipmap.appicon);
            iconView.ScaleX = iconView.ScaleY = 0.8f;
            titleCard.AddView(iconView);

            var spacer1 = new View(this);
            spacer1.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            titleCard.AddView(spacer1);

            _appTitleText = new TextView(this)
            {
                Text = "Penguin Nestcheck",
                TextSize = 28,
                Gravity = GravityFlags.Center
            };
            _appTitleText.SetPadding(0, 0, 50, 0);
            _appTitleText.SetTextColor(UIFactory.PRIMARY_BLUE);
            _appTitleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            titleCard.AddView(_appTitleText);

            var spacer2 = new View(this);
            spacer2.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 7f);
            titleCard.AddView(spacer2);

            headerStatusSettingsCard.AddView(titleCard);

            _statusText = new TextView(this)
            {
                TextSize = 13,
                Gravity = GravityFlags.Center
            };
            _statusText.SetTextColor(Color.Black);
            var statusParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            statusParams.SetMargins(0, -12, 0, 0);
            _statusText.LayoutParameters = statusParams;

            headerStatusSettingsCard.AddView(_statusText);

            // Exit historical view button (hidden by default)
            _exitHistoricalButton = new Button(this) { Text = "Exit Historical View", TextSize = 14 };
            _exitHistoricalButton.SetTextColor(Color.Black);
            _exitHistoricalButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _exitHistoricalButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.WARNING_YELLOW, 8);
            _exitHistoricalButton.SetAllCaps(false);
            var exitHistParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            exitHistParams.SetMargins(8, 4, 8, 4);
            _exitHistoricalButton.LayoutParameters = exitHistParams;
            _exitHistoricalButton.Visibility = ViewStates.Gone;
            _exitHistoricalButton.Click += (s, e) => ExitHistoricalView();
            headerStatusSettingsCard.AddView(_exitHistoricalButton);

            //Settings Card
            createSettingsCard();
            headerStatusSettingsCard.AddView(_settingsCard);
            CreateBoxSetsDictionary();

            parentLinearLayout.AddView(headerStatusSettingsCard);

           // Data card
            CreateBoxDataCard();

            //Create Multi box view card
            _multiBoxViewCard = _uiFactory.CreateCard();
            _multiBoxViewCard.Visibility = ViewStates.Visible;

            //Create Breeding dates card
            _breedingDatesCard = _uiFactory.CreateCard();
            _breedingDatesCard.Visibility = ViewStates.Visible;

            // Blocking banner: shown when no colony/box list is loaded — data entry pauses
            // until login + sync deliver the colony's box sets string.
            _noColonyBanner = new TextView(this)
            {
                Text = "⛔ No colony loaded\n\nLog in and sync to load your colony's boxes.\nData entry is paused until then.",
                TextSize = 16,
                Gravity = GravityFlags.Center,
            };
            _noColonyBanner.SetTextColor(UIFactory.DANGER_RED);
            _noColonyBanner.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _noColonyBanner.SetPadding(24, 48, 24, 48);
            _noColonyBanner.Visibility = ViewStates.Gone;
            parentLinearLayout.AddView(_noColonyBanner);

            parentLinearLayout.AddView(_singleBoxDataOuterLayout);
            parentLinearLayout.AddView(_multiBoxViewCard);
            parentLinearLayout.AddView(_breedingDatesCard);

            // Keeps the last card scrollable clear of the fixed finish bar in tag mode
            _tagModeBottomSpacer = new View(this);
            _tagModeBottomSpacer.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, (int)(140 * (Resources?.DisplayMetrics?.Density ?? 2)));
            _tagModeBottomSpacer.Visibility = ViewStates.Gone;
            parentLinearLayout.AddView(_tagModeBottomSpacer);

            _rootScrollView.AddView(parentLinearLayout);

            // The finish bar floats over the scroll view so it's on screen no matter where
            // the user has scrolled to — in tag mode there is always a visible way out.
            var rootFrame = new FrameLayout(this);
            rootFrame.AddView(_rootScrollView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            _tagModeFinishBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _tagModeFinishBar.SetPadding(16, 12, 16, 12);
            _tagModeFinishBar.Background = _uiFactory.CreateRoundedBackground(TAG_MODE_PURPLE, 0);
            // "box tags" until the page also set positions and deleted observations. "Box details"
            // matches the button that opens it, which already said that.
            var finishButton = _uiFactory.CreateStyledButton("✓  Finish editing box details", Color.White);
            finishButton.TextSize = 18;
            finishButton.SetPadding(24, 28, 24, 28);
            finishButton.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            finishButton.Click += (s, e) => FinishBoxTagsMode();
            _tagModeFinishBar.AddView(finishButton);
            _tagModeFinishBar.Visibility = ViewStates.Gone;
            var finishBarParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            finishBarParams.Gravity = GravityFlags.Bottom;
            rootFrame.AddView(_tagModeFinishBar, finishBarParams);

            // Capture actions take over the bottom of the screen while a session is open. They
            // live here rather than in the scrolling card because the user is holding the phone
            // flat on the box while it measures — they can't go hunting for a button.
            _tagModeCaptureBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            _tagModeCaptureBar.SetPadding(12, 10, 12, 10);
            _tagModeCaptureBar.Background = _uiFactory.CreateRoundedBackground(TAG_MODE_PURPLE, 0);

            _tagModeCancelButton = _uiFactory.CreateStyledButton("Cancel", UIFactory.LIGHTER_GRAY);
            _tagModeCancelButton.TextSize = 15;
            _tagModeCancelButton.SetPadding(8, 26, 8, 26);
            var cancelBarParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            cancelBarParams.SetMargins(0, 0, 6, 0);
            _tagModeCancelButton.LayoutParameters = cancelBarParams;
            _tagModeCancelButton.Click += (s, e) =>
            {
                EndTagCapture();
                Toast.MakeText(this, "Cancelled — nothing changed", ToastLength.Short)?.Show();
            };
            _tagModeCaptureBar.AddView(_tagModeCancelButton);

            // Affirmative action on the right and twice the width — the one you reach for
            _tagModeSaveButton = _uiFactory.CreateStyledButton("✓  Save", UIFactory.SUCCESS_GREEN);
            _tagModeSaveButton.TextSize = 17;
            _tagModeSaveButton.SetPadding(8, 26, 8, 26);
            var saveBarParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2f);
            saveBarParams.SetMargins(6, 0, 0, 0);
            _tagModeSaveButton.LayoutParameters = saveBarParams;
            _tagModeSaveButton.Click += (s, e) =>
            {
                if (!_tagCaptureActive) return;
                SaveTagCapture();
            };
            _tagModeCaptureBar.AddView(_tagModeSaveButton);

            _tagModeCaptureBar.Visibility = ViewStates.Gone;
            var captureBarParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            captureBarParams.Gravity = GravityFlags.Bottom;
            rootFrame.AddView(_tagModeCaptureBar, captureBarParams);

            SetContentView(rootFrame);

            _rootScrollView.SetOnApplyWindowInsetsListener(new ViewInsetsListener());
            _tagModeFinishBar.SetOnApplyWindowInsetsListener(new BottomInsetListener(12));
            _tagModeCaptureBar.SetOnApplyWindowInsetsListener(new BottomInsetListener(10));

           JumpToBox(_boxNamesAndIndexes.First().Key); //Contains DrawPageLayouts()
        }
        /// <summary>
        /// designed to create _boxNamesAndIndexes to map box names to indexes which can be used 
        /// to navigate boxes
        /// 
        /// example 1 BoxSetString value: {1-150,AA-AC}
        /// example 2 BoxSetString value: {N1-N6}
        /// example 3 BoxSetString value: {1-150,AA-AC},{N1-N6}
        /// </summary>
        private void CreateBoxSetsDictionary()
        {
            string setString;
            if (string.IsNullOrWhiteSpace(_appSettings.AllBoxSetsString))
            {
                // No sets string (not logged in / colony not resolved yet): no boxes.
                // Never invent another colony's boxes here.
                _appSettings.BoxSetString = "All";
            }
            // A chosen subset from a previous colony must not survive a colony change
            // (a stale PT "1-150" on Ngawhiti is how phantom boxes get created). The
            // selected set must be one of the current colony's sets, else fall back to All.
            if (!string.IsNullOrWhiteSpace(_appSettings.BoxSetString) && _appSettings.BoxSetString.ToLower() != "all")
            {
                var validSets = (_appSettings.AllBoxSetsString ?? "").Split(new string[] { "},{", "{", "}" }, StringSplitOptions.RemoveEmptyEntries);
                if (!validSets.Contains(_appSettings.BoxSetString))
                    _appSettings.BoxSetString = "All";
            }
            setString = string.IsNullOrWhiteSpace(_appSettings.BoxSetString) || _appSettings.BoxSetString.ToLower() == "all"
                ? _appSettings.AllBoxSetsString : _appSettings.BoxSetString;

            _boxNamesAndIndexes = new Dictionary<string, int>();
            if (!string.IsNullOrWhiteSpace(setString))
            {
                _boxNamesAndIndexes.Clear();
                int currentIndex = 1;

                foreach (string boxSetString in setString.Split(new string[] { "}{", "},{" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Remove curly braces
                    string cleanedSet = boxSetString.Trim('{', '}');

                    foreach (string boxSetPart in cleanedSet.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmedPart = boxSetPart.Trim();

                        if (trimmedPart.Contains('-'))
                        {
                            // Handle ranges like "1-150", "AA-AC", "N1-N6"
                            var rangeParts = trimmedPart.Split('-');
                            if (rangeParts.Length == 2)
                            {
                                string start = rangeParts[0].Trim();
                                string end = rangeParts[1].Trim();

                                // Check if it's a numeric range (e.g., "1-150")
                                if (int.TryParse(start, out int startNum) && int.TryParse(end, out int endNum))
                                {
                                    for (int i = startNum; i <= endNum; i++)
                                    {
                                        _boxNamesAndIndexes[i.ToString()] = currentIndex++;
                                    }
                                }
                                else
                                {
                                    // Handle alphanumeric ranges (e.g., "AA-AC", "N1-N6")
                                    var expandedRange = ExpandAlphanumericRange(start, end);
                                    foreach (string boxName in expandedRange)
                                    {
                                        _boxNamesAndIndexes[boxName.ToUpper()] = currentIndex++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Single box name/number
                            _boxNamesAndIndexes[trimmedPart.ToUpper()] = currentIndex++;
                        }
                    }
                }
            }
            if (_boxNamesAndIndexes.Count > 1000)
                _boxNamesAndIndexes = _boxNamesAndIndexes.Take(1000).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (_boxNamesAndIndexes.Count == 0)
                _boxNamesAndIndexes.Add("fake", 1);

            // Snap the current box into the (possibly rebuilt) list — a box carried over
            // from another colony/set must not stay current, or scans and tag edits get
            // recorded against a box this colony doesn't have.
            if (!_boxNamesAndIndexes.ContainsKey(_currentBoxName))
            {
                _currentBoxName = _boxNamesAndIndexes.First().Key;
                _currentBoxIndex = _boxNamesAndIndexes[_currentBoxName];
            }
        }

        /// <summary>
        /// Expands alphanumeric ranges like "AA-AC" or "N1-N6"
        /// </summary>
        private List<string> ExpandAlphanumericRange(string start, string end)
        {
            var result = new List<string>();

            // Extract prefix and numeric suffix
            var startMatch = Regex.Match(start, @"^([A-Za-z]*)(\d*)$");
            var endMatch = Regex.Match(end, @"^([A-Za-z]*)(\d*)$");

            if (!startMatch.Success || !endMatch.Success)
            {
                // If pattern doesn't match, just add both as individual items
                result.Add(start);
                result.Add(end);
                return result;
            }

            string startPrefix = startMatch.Groups[1].Value;
            string endPrefix = endMatch.Groups[1].Value;
            string startNumStr = startMatch.Groups[2].Value;
            string endNumStr = endMatch.Groups[2].Value;

            // Case 1: Pure alphabetic range (e.g., "AA-AC")
            if (string.IsNullOrEmpty(startNumStr) && string.IsNullOrEmpty(endNumStr) &&
                startPrefix.Length == endPrefix.Length)
            {
                result.AddRange(ExpandAlphabeticRange(startPrefix, endPrefix));
            }
            // Case 2: Same prefix with numeric range (e.g., "N1-N6")
            else if (startPrefix == endPrefix &&
                     int.TryParse(startNumStr, out int startNum) &&
                     int.TryParse(endNumStr, out int endNum))
            {
                for (int i = startNum; i <= endNum; i++)
                {
                    result.Add(startPrefix + i.ToString());
                }
            }
            else
            {
                // Fallback: add both as individual items
                result.Add(start);
                result.Add(end);
            }

            return result;
        }

        /// <summary>
        /// Expands purely alphabetic ranges like "AA-AC"
        /// </summary>
        private List<string> ExpandAlphabeticRange(string start, string end)
        {
            var result = new List<string>();

            if (start.Length != end.Length)
            {
                result.Add(start);
                result.Add(end);
                return result;
            }

            // Convert to base-26 numbers for easier iteration
            int startValue = AlphaToNumber(start);
            int endValue = AlphaToNumber(end);

            for (int i = startValue; i <= endValue; i++)
            {
                result.Add(NumberToAlpha(i, start.Length));
            }

            return result;
        }

        /// <summary>
        /// Convert alphabetic string to number (A=0, B=1, ..., Z=25, AA=26, etc.)
        /// </summary>
        private int AlphaToNumber(string alpha)
        {
            int result = 0;
            for (int i = 0; i < alpha.Length; i++)
            {
                result = result * 26 + (char.ToUpper(alpha[i]) - 'A');
            }
            return result;
        }

        /// <summary>
        /// Convert number back to alphabetic string of specified length
        /// </summary>
        private string NumberToAlpha(int number, int length)
        {
            string result = "";
            for (int i = 0; i < length; i++)
            {
                result = (char)('A' + (number % 26)) + result;
                number /= 26;
            }
            return result;
        }
        private void createMultiBoxViewCard()
        {
            _multiBoxViewCard.RemoveAllViews();

            var OverviewHeaderCard = _uiFactory.CreateCard(
                Android.Widget.Orientation.Vertical,
                borderWidth: _appSettings.ActiveSessionTimeStampActive ? 6 : 4,
                borderColour: _appSettings.ActiveSessionTimeStampActive ? UIFactory.DANGER_RED : null);
            OverviewHeaderCard.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            LinearLayout headerTitle = new LinearLayout(this);

            var showFiltersButton = new ImageButton(this);
            showFiltersButton.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            showFiltersButton.SetPadding(0, 0, 0, 0);
            showFiltersButton.SetImageResource(_appSettings.ShowMultiboxFilterCard ? Resource.Drawable.fold : Resource.Drawable.unfold);
            showFiltersButton.SetBackgroundColor(Color.Transparent);
            showFiltersButton.Click += (sender, e) =>
            {
                _appSettings.ShowMultiboxFilterCard = !_appSettings.ShowMultiboxFilterCard;
                DrawPageLayouts();
            };
            headerTitle.AddView(showFiltersButton);

            TextView multiBoxTitle = new TextView(this)
            {
                Text = "Overview",
                TextSize = 30,
                Gravity = GravityFlags.Left
            };
            multiBoxTitle.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            multiBoxTitle.SetTextColor(Color.Black);
            multiBoxTitle.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            multiBoxTitle.SetPadding(0, 0, 0, 0);
            headerTitle.AddView(multiBoxTitle);

            // Add a spacer that expands to fill available space
            var spacer = new View(this);
            spacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            headerTitle.AddView(spacer);

            TextView timeTV = new TextView(this)
            {
                TextSize = 14,
            };
            var pending = _colonyState.PendingUploadCount;
            timeTV.Text = !string.IsNullOrEmpty(_colonyState.DailyLabel) ? _colonyState.DailyLabel : "";
            var lastFullSync = _colonyState.LastSyncedUtc;
            var mostRecentCheck = _lastSyncCheckUtc > lastFullSync ? _lastSyncCheckUtc : lastFullSync;
            if (mostRecentCheck > DateTime.MinValue)
            {
                var syncNz = ToNzTime(mostRecentCheck);
                var syncLabel = syncNz.Date == NzToday ? $"{syncNz:HH:mm}" : $"{syncNz:d MMM HH:mm}";
                timeTV.Text += $"\nSynced {syncLabel}";
            }
            if (pending > 0)
                timeTV.Text += $"\n⏳ {pending} pending upload";

            timeTV.Text = timeTV.Text.Trim();
            timeTV.SetTextColor(Color.Black);
            timeTV.SetPadding(0, 0, 0, 0);
            timeTV.Gravity = GravityFlags.Right | GravityFlags.Top;
            timeTV.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent);
            headerTitle.AddView(timeTV);

            OverviewHeaderCard.AddView(headerTitle);

            // Per-box scan/count mismatch warnings — right under header
            {
                var todayBoxes2 = new Dictionary<string, BoxObservation>(_colonyState.TodayBoxes);
                var nzToday2 = NzToday;
                foreach (var obs2 in _colonyState.PendingObservations)
                    if (!string.IsNullOrEmpty(obs2.BoxName) && ToNzTime(obs2.WhenDataCollectedUtc).Date == nzToday2)
                        todayBoxes2[obs2.BoxName] = obs2;
                var mismatchLines = new List<string>();
                foreach (var bn in _boxNamesAndIndexes.Keys)
                {
                    if (!todayBoxes2.TryGetValue(bn, out var boxObs2)) continue;
                    var problem = GetBoxScanMismatch(boxObs2);
                    if (problem != null)
                        mismatchLines.Add($"⚠ Box {bn}: {problem}");
                    var drop = GetOffspringDropWarning(bn, boxObs2);
                    if (drop != null)
                        mismatchLines.Add($"⚠ Box {bn}: {drop}");
                }
                if (mismatchLines.Count > 0)
                {
                    var mismatchWarn = new TextView(this)
                    {
                        Text = string.Join("\n", mismatchLines),
                        TextSize = 13
                    };
                    mismatchWarn.SetTextColor(UIFactory.DANGER_RED);
                    mismatchWarn.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                    mismatchWarn.SetPadding(12, 4, 12, 4);
                    OverviewHeaderCard.AddView(mismatchWarn);
                }
            }

            // Duplicate penguin warnings — right under header
            {
                var penguinBoxes2 = new Dictionary<string, List<string>>();
                foreach (var bn in _boxNamesAndIndexes.Keys)
                {
                    var obs2 = _colonyState.GetTodayForBox(bn);
                    if (obs2 == null) continue;
                    foreach (var scan in obs2.ScannedIds)
                    {
                        if (scan.BirdId.StartsWith("NOSCAN_")) continue;
                        var key = scan.BirdId.ToUpper();
                        if (!penguinBoxes2.ContainsKey(key)) penguinBoxes2[key] = new List<string>();
                        if (!penguinBoxes2[key].Contains(bn)) penguinBoxes2[key].Add(bn);
                    }
                }
                foreach (var dup in penguinBoxes2.Where(kvp => kvp.Value.Count > 1))
                {
                    var (dupLabel, _, _, _) = LookupPenguinLabel(dup.Key);
                    var warn = new TextView(this)
                    {
                        Text = $"⚠ {dupLabel} in Box {string.Join(" & Box ", dup.Value)}",
                        TextSize = 13
                    };
                    warn.SetTextColor(UIFactory.DANGER_RED);
                    warn.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                    warn.SetPadding(12, 4, 12, 4);
                    OverviewHeaderCard.AddView(warn);
                }
            }

            _overviewFiltersLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            // Filter sentence: "Show [all/filters] except [none/filters]"
            var filterSentenceLayout = new LinearLayout(this);
            filterSentenceLayout.SetGravity(GravityFlags.Center);
            var sentenceParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            sentenceParams.SetMargins(8, 12, 8, 12);
            filterSentenceLayout.LayoutParameters = sentenceParams;

            var showLabel = new TextView(this) { Text = "Show ", TextSize = 18 };
            showLabel.SetTextColor(Color.Black);
            filterSentenceLayout.AddView(showLabel);

            string showText = GetShowFilterText();
            var showButton = new TextView(this) { Text = showText, TextSize = 18 };
            showButton.SetTextColor(UIFactory.PRIMARY_BLUE);
            showButton.SetTypeface(null, TypefaceStyle.Bold);
            showButton.PaintFlags = showButton.PaintFlags | Android.Graphics.PaintFlags.UnderlineText;
            showButton.Click += (s, e) =>
            {
                _appSettings.ShowFiltersVisible = !_appSettings.ShowFiltersVisible;
                _appSettings.HideFiltersVisible = false;
                DrawPageLayouts();
            };
            filterSentenceLayout.AddView(showButton);

            if (showText != "none")
            {
                var exceptLabel = new TextView(this) { Text = " except ", TextSize = 18 };
                exceptLabel.SetTextColor(Color.Black);
                filterSentenceLayout.AddView(exceptLabel);

                string hideText = GetHideFilterText();
                var hideButton = new TextView(this) { Text = hideText, TextSize = 18 };
                hideButton.SetTextColor(UIFactory.DANGER_RED);
                hideButton.SetTypeface(null, TypefaceStyle.Bold);
                hideButton.PaintFlags = hideButton.PaintFlags | Android.Graphics.PaintFlags.UnderlineText;
                hideButton.Click += (s, e) =>
                {
                    _appSettings.HideFiltersVisible = !_appSettings.HideFiltersVisible;
                    _appSettings.ShowFiltersVisible = false;
                    DrawPageLayouts();
                };
                filterSentenceLayout.AddView(hideButton);
            }

            // Count of boxes currently shown, filled in after the box loop below.
            _boxCountLabel = new TextView(this) { Text = "", TextSize = 18 };
            _boxCountLabel.SetTextColor(UIFactory.TEXT_SECONDARY);
            filterSentenceLayout.AddView(_boxCountLabel);

            _overviewFiltersLayout.AddView(filterSentenceLayout);

            // Show filters checkboxes layout
            var showFiltersCheckboxLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            showFiltersCheckboxLayout.Visibility = _appSettings.ShowFiltersVisible ? ViewStates.Visible : ViewStates.Gone;

            TextView showBoxesTitle = new TextView(this)
            {
                Text = "Show boxes matching:",
                TextSize = 14,
                Gravity = GravityFlags.Center,
            };
            showBoxesTitle.SetTextColor(UIFactory.TEXT_SECONDARY);
            showFiltersCheckboxLayout.AddView(showBoxesTitle);

            var showFlow = new PenguinMonitor.UI.FlowLayout(this);
            showFlow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            foreach (var (text, getter, setter) in new (string, Func<bool>, Action<bool>)[] {
                ("All", () => _appSettings.ShowAllBoxesInMultiBoxView, v => { _appSettings.ShowAllBoxesInMultiBoxView = v; }),
                ("With data", () => _appSettings.ShowBoxesWithDataInMultiBoxView, v => { _appSettings.ShowBoxesWithDataInMultiBoxView = v; if (v) { _appSettings.ShowAllBoxesInMultiBoxView = false; _appSettings.HideBoxesWithDataInMultiBoxView = false; } }),
                ("NO", () => _appSettings.ShowNoBoxesInMultiBoxView, v => { _appSettings.ShowNoBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("UNL", () => _appSettings.ShowUnlikleyBoxesInMultiBoxView, v => { _appSettings.ShowUnlikleyBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("POT", () => _appSettings.ShowPotentialBoxesInMultiBoxView, v => { _appSettings.ShowPotentialBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("CON", () => _appSettings.ShowConfidentBoxesInMultiBoxView, v => { _appSettings.ShowConfidentBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("BR", () => _appSettings.ShowBreedingBoxesInMultiBoxView, v => { _appSettings.ShowBreedingBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("ABN", () => _appSettings.ShowABNBoxesInMultiboxView, v => { _appSettings.ShowABNBoxesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("DCM", () => _appSettings.ShowDCMBoxesInMultiboxView, v => { _appSettings.ShowDCMBoxesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("Notes", () => _appSettings.showBoxesWithNotesInMultiboxView, v => { _appSettings.showBoxesWithNotesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("Box notes", () => _appSettings.ShowInterestingBoxesInMultiBoxView, v => { _appSettings.ShowInterestingBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("1 egg", () => _appSettings.ShowSingleEggBoxesInMultiboxView, v => { _appSettings.ShowSingleEggBoxesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("2 egg", () => _appSettings.ShowDoubleEggBoxesInMultiboxView, v => { _appSettings.ShowDoubleEggBoxesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("No scan", () => _appSettings.ShowNoScanBoxesInMultiboxView, v => { _appSettings.ShowNoScanBoxesInMultiboxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("Watched", () => _appSettings.ShowWatchedBoxesInMultiBoxView, v => { _appSettings.ShowWatchedBoxesInMultiBoxView = v; if (v) _appSettings.ShowAllBoxesInMultiBoxView = false; }),
                ("Chip only", () => _appSettings.ShowChipOnlyBoxesInMultiBoxView, v => { _appSettings.ShowChipOnlyBoxesInMultiBoxView = v; if (v) { _appSettings.ShowAllBoxesInMultiBoxView = false; RefreshChipOnlyBoxes(redrawOnChange: true); } }),
            })
            {
                var cb = new CheckBox(this) { Text = text, Checked = getter() };
                cb.SetTextColor(Color.Black);
                cb.Click += (s, e) => { setter(cb.Checked); DrawPageLayouts(); };
                showFlow.AddView(cb);
            }
            showFiltersCheckboxLayout.AddView(showFlow);

            _overviewFiltersLayout.AddView(showFiltersCheckboxLayout);

            // Hide filters checkboxes layout
            var hideFiltersCheckboxLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            hideFiltersCheckboxLayout.Visibility = _appSettings.HideFiltersVisible ? ViewStates.Visible : ViewStates.Gone;

            TextView hideBoxesTitle = new TextView(this)
            {
                Text = "Hide boxes matching:",
                TextSize = 14,
                Gravity = GravityFlags.Center,
            };
            hideBoxesTitle.SetTextColor(UIFactory.TEXT_SECONDARY);
            hideFiltersCheckboxLayout.AddView(hideBoxesTitle);

            var hideFlow = new PenguinMonitor.UI.FlowLayout(this);
            hideFlow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            foreach (var (text, getter, setter) in new (string, Func<bool>, Action<bool>)[] {
                ("With data", () => _appSettings.HideBoxesWithDataInMultiBoxView, v => { _appSettings.HideBoxesWithDataInMultiBoxView = v; if (v) _appSettings.ShowBoxesWithDataInMultiBoxView = false; }),
                ("NO", () => _appSettings.HideNoBoxesInMultiBoxView, v => _appSettings.HideNoBoxesInMultiBoxView = v),
                ("UNL", () => _appSettings.HideUnlikelyBoxesInMultiBoxView, v => _appSettings.HideUnlikelyBoxesInMultiBoxView = v),
                ("POT", () => _appSettings.HidePotentialBoxesInMultiBoxView, v => _appSettings.HidePotentialBoxesInMultiBoxView = v),
                ("CON", () => _appSettings.HideConfidentBoxesInMultiBoxView, v => _appSettings.HideConfidentBoxesInMultiBoxView = v),
                ("BR", () => _appSettings.HideBreedingBoxesInMultiBoxView, v => _appSettings.HideBreedingBoxesInMultiBoxView = v),
                ("ABN", () => _appSettings.HideABNInMultiBoxView, v => _appSettings.HideABNInMultiBoxView = v),
                ("DCM", () => _appSettings.HideDCMInMultiBoxView, v => _appSettings.HideDCMInMultiBoxView = v),
                ("Notes", () => _appSettings.HideBoxesWithNotesInMultiboxView, v => _appSettings.HideBoxesWithNotesInMultiboxView = v),
                ("Box notes", () => _appSettings.HideInterestingBoxesInMultiBoxView, v => _appSettings.HideInterestingBoxesInMultiBoxView = v),
                ("< current", () => _appSettings.HideBeforeCurrentInMultiBoxView, v => _appSettings.HideBeforeCurrentInMultiBoxView = v),
                ("1 egg", () => _appSettings.HideSingleEggBoxesInMultiboxView, v => _appSettings.HideSingleEggBoxesInMultiboxView = v),
                ("2 egg", () => _appSettings.HideDoubleEggBoxesInMultiboxView, v => _appSettings.HideDoubleEggBoxesInMultiboxView = v),
            })
            {
                var cb = new CheckBox(this) { Text = text, Checked = getter() };
                cb.SetTextColor(Color.Black);
                cb.Click += (s, e) => { setter(cb.Checked); DrawPageLayouts(); };
                hideFlow.AddView(cb);
            }
            hideFiltersCheckboxLayout.AddView(hideFlow);

            _overviewFiltersLayout.AddView(hideFiltersCheckboxLayout);

            // Done button (visible when either filter panel is open)
            if (_appSettings.ShowFiltersVisible || _appSettings.HideFiltersVisible)
            {
                var doneButton = _uiFactory.CreateStyledButton("Hide filters", UIFactory.SUCCESS_GREEN);
                var navDensity = Resources?.DisplayMetrics?.Density ?? 2;
                doneButton.SetPadding((int)(12 * navDensity), (int)(8 * navDensity), (int)(12 * navDensity), (int)(8 * navDensity));
                var doneParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                doneParams.SetMargins(16, 8, 16, 8);
                doneButton.LayoutParameters = doneParams;
                doneButton.Click += (s, e) =>
                {
                    _appSettings.ShowFiltersVisible = false;
                    _appSettings.HideFiltersVisible = false;
                    DrawPageLayouts();
                };
                _overviewFiltersLayout.AddView(doneButton);
            }

            // Today's statistics summary
            var statsText = new TextView(this) { TextFormatted = CrossedLosses(GetSummaryText()), TextSize = 14 };
            statsText.SetTextColor(Color.Black);
            statsText.SetPadding(12, 8, 12, 8);
            _overviewFiltersLayout.AddView(statsText);

            OverviewHeaderCard.AddView(_overviewFiltersLayout);
            _multiBoxViewCard.AddView(OverviewHeaderCard);

            _overviewFiltersLayout.Visibility = _appSettings.ShowMultiboxFilterCard ? ViewStates.Visible : ViewStates.Gone;

            int boxesPerRow = 3;
            LinearLayout? currentRow = null;

            int visibleBoxCount = 0;
            foreach (string boxName in _boxNamesAndIndexes.Keys)
            {
                if (visibleBoxCount % boxesPerRow == 0)
                {
                    currentRow = new LinearLayout(this);
                    currentRow.SetPadding(0, 0, 0, 0);

                    var rowParams = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent,
                        ViewGroup.LayoutParams.WrapContent);
                    currentRow.LayoutParameters = rowParams;
                    currentRow.SetGravity(GravityFlags.Center);

                    _multiBoxViewCard.AddView(currentRow);
                }
                // Get the most recent data for this box
                BoxObservation mostRecentBoxData = new BoxObservation();
                BoxObservation? previousBoxData = null;
                if (!_isHistoricalView)
                    _colonyState.PreviousBoxes.TryGetValue(boxName, out previousBoxData);
                if (previousBoxData != null)
                    mostRecentBoxData = previousBoxData;

                var currentBoxData = GetDisplayBoxData(boxName);
                bool currentBoxDataFound = currentBoxData != null;
                if (currentBoxDataFound && currentBoxData != null)
                    mostRecentBoxData = currentBoxData;

                bool hasBoxNotes = _boxNotes.TryGetValue(boxName, out var boxNoteForFilter) && !string.IsNullOrWhiteSpace(boxNoteForFilter.PersistentNotes);
                bool showBox = _appSettings.ShowAllBoxesInMultiBoxView
                            || _appSettings.ShowBoxesWithDataInMultiBoxView && (GetDisplayBoxData(boxName) != null)
                            || _appSettings.ShowBreedingBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("BR")
                            || _appSettings.ShowConfidentBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("CON")
                            || _appSettings.ShowPotentialBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("POT")
                            || _appSettings.ShowUnlikleyBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("UNL")
                            || _appSettings.ShowNoBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("NO")
                            || _appSettings.ShowBoxesWithNotesInMultiboxView && mostRecentBoxData != null && !String.IsNullOrWhiteSpace(mostRecentBoxData.Notes)
                            || _appSettings.ShowInterestingBoxesInMultiBoxView && hasBoxNotes
                            || _appSettings.ShowSingleEggBoxesInMultiboxView && (mostRecentBoxData.Eggs == 1)
                            || _appSettings.ShowDoubleEggBoxesInMultiboxView && (mostRecentBoxData.Eggs == 2)
                            || _appSettings.ShowDCMBoxesInMultiboxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("DCM")
                            || _appSettings.ShowABNBoxesInMultiboxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("ABN")
                            || _appSettings.ShowNoScanBoxesInMultiboxView && mostRecentBoxData.ScannedIds.Any(s => s.BirdId.StartsWith("NOSCAN_"))
                            || _appSettings.ShowWatchedBoxesInMultiBoxView && boxNoteForFilter != null && boxNoteForFilter.Watched
                            || _appSettings.ShowChipOnlyBoxesInMultiBoxView && _chipOnlyBoxes.Contains(boxName);

                bool hideBoxWithData = _appSettings.HideBoxesWithDataInMultiBoxView && (GetDisplayBoxData(boxName) != null);
                bool hideDCM = _appSettings.HideDCMInMultiBoxView && ((mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus == "DCM"));
                bool hideABN = _appSettings.HideABNInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("ABN");
                bool hideBeforeCurrent = _appSettings.HideBeforeCurrentInMultiBoxView && _currentBoxIndex > _boxNamesAndIndexes[boxName];
                bool hideNo = _appSettings.HideNoBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("NO");
                bool hideUnlikely = _appSettings.HideUnlikelyBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("UNL");
                bool hidePotential = _appSettings.HidePotentialBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("POT");
                bool hideConfident = _appSettings.HideConfidentBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("CON");
                bool hideBreeding = _appSettings.HideBreedingBoxesInMultiBoxView && mostRecentBoxData.BreedingStatus != null && mostRecentBoxData.BreedingStatus.Equals("BR");
                bool hideNotes = _appSettings.HideBoxesWithNotesInMultiboxView && mostRecentBoxData != null && !String.IsNullOrWhiteSpace(mostRecentBoxData.Notes);
                bool hideInteresting = _appSettings.HideInterestingBoxesInMultiBoxView && hasBoxNotes;
                bool hideSingleEgg = _appSettings.HideSingleEggBoxesInMultiboxView && mostRecentBoxData.Eggs == 1;
                bool hideDoubleEgg = _appSettings.HideDoubleEggBoxesInMultiboxView && mostRecentBoxData.Eggs == 2;

                // In Edit Box Tags mode, show all boxes regardless of filters
                bool shouldShow = _appSettings.EditBoxTagsMode ||
                    (showBox && !hideBoxWithData && !hideDCM && !hideABN && !hideBeforeCurrent && !hideNo && !hideUnlikely && !hidePotential && !hideConfident && !hideBreeding && !hideNotes && !hideInteresting && !hideSingleEgg && !hideDoubleEgg);

                if (shouldShow)
                {
                    View? card;
                    if (currentBoxDataFound)
                        card = CreateBoxSummaryCard(boxName, currentBoxData, _boxNamesAndIndexes[boxName] == _currentBoxIndex, previousBoxData);
                    else
                        card = CreateBoxSummaryCard(boxName, null, _boxNamesAndIndexes[boxName] == _currentBoxIndex, previousBoxData);
                    currentRow?.AddView(card);
                    visibleBoxCount++;
                }
            }
            if (visibleBoxCount == 0)
            {
                var empty = new TextView(this) { Text = "No boxes to show." };
                _multiBoxViewCard.AddView(empty);
            }
            if (_boxCountLabel != null) _boxCountLabel.Text = $" ({visibleBoxCount})";
        }
        private void createBreedingDatesCard()
        {
            _breedingDatesCard.RemoveAllViews();
            var breedingDatesContent = createBreedingDatesTimelineSection();
            _breedingDatesCard.AddView(breedingDatesContent);
        }
        private View? CreateBoxSummaryCard(string boxName, BoxObservation? thisBoxData, bool selected, BoxObservation? previousBoxData)
        {
            bool currentExists = thisBoxData != null;
            if (!currentExists && previousBoxData != null)
                thisBoxData = previousBoxData;

            var boxOverviewCard = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            boxOverviewCard.SetPadding(5, 5, 5, 5);

            int minWidth = Resources.DisplayMetrics.WidthPixels / 5;
            var cardParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            cardParams.SetMargins(5, 5, 5, 5);
            boxOverviewCard.LayoutParameters = cardParams;
            boxOverviewCard.SetMinimumWidth(minWidth);

            boxOverviewCard.Click += (sender, e) =>
            {
                JumpToBox(boxName);
                ScrollToTop();
            };

            // Border: today=solid black, old data=orange border, loss=red, changed=blue, default=grey
            bool differenceFound = false;
            if (currentExists && (thisBoxData?.IsPendingUpload == true || thisBoxData?.IsDraft == true))
            {
                // Unsynced today's edit (queued OR an uncommitted draft) — orange border
                boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.WARNING_YELLOW, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else if (currentExists)
            {
                // Today's synced data — solid black border
                // Same rule as the header warning, so the two agree: a drop stops reading as a
                // loss once the chip window has closed and the chicks are expected to be leaving.
                if (previousBoxData != null && thisBoxData != null
                    && GetOffspringDropWarning(boxName, thisBoxData) != null)
                {
                    differenceFound = true;
                    boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.DANGER_RED, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
                }
                else if (previousBoxData != null && thisBoxData != null
                    && (thisBoxData.Eggs != previousBoxData.Eggs || thisBoxData.Chicks != previousBoxData.Chicks))
                {
                    differenceFound = true;
                    boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.PRIMARY_BLUE, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
                }
                else
                {
                    boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4, borderColour: Color.Black, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
                }
            }
            else if (thisBoxData != null)
            {
                // Old/previous data only — orange border
                boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4, borderColour: UIFactory.WARNING_YELLOW, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else
            {
                // No data — thin grey
                boxOverviewCard.Background = _uiFactory.CreateCardBackground(borderWidth: 3, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }

            // Pale green background for boxes with no-scan penguins (chipping candidates)
            bool hasNoScan = thisBoxData != null && thisBoxData.ScannedIds.Any(s => s.BirdId.StartsWith("NOSCAN_"));
            if (hasNoScan && !selected)
            {
                ((Android.Graphics.Drawables.GradientDrawable)boxOverviewCard.Background).SetColor(SCAN_CHIPPED_TODAY_BG);
            }

            // Pale red background for boxes whose scans don't match the recorded counts (overrides green)
            if (currentExists && !selected && thisBoxData != null && GetBoxScanMismatch(thisBoxData) != null)
            {
                ((Android.Graphics.Drawables.GradientDrawable)boxOverviewCard.Background).SetColor(BOX_MISMATCH_BG);
            }

            var title = new TextView(this)
            {
                Text = $"Box {boxName}",
                Gravity = GravityFlags.Center,
                TextSize = 18
            };
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            title.SetTextColor(Color.Black);
            boxOverviewCard.AddView(title);

            if (thisBoxData == null)
                return boxOverviewCard;

            var summary = new TextView(this)
            {
                // Losses recorded on this visit sit with the live counts, so a box whose eggs
                // dropped explains itself on the overview instead of only in the box.
                Text = $"{string.Concat(Enumerable.Repeat("🐧", thisBoxData.Adults))}" +
                    $"{string.Concat(Enumerable.Repeat("🥚", thisBoxData.Eggs))}" +
                    $"{string.Concat(Enumerable.Repeat("🐣", thisBoxData.Chicks))}" +
                    $"{string.Concat(Enumerable.Repeat("🥚✗", thisBoxData.FailedEggs ?? 0))}" +
                    $"{string.Concat(Enumerable.Repeat("🐣✗", thisBoxData.DeadChicks ?? 0))}",
                Gravity = GravityFlags.Center,
                TextSize = 14
            };
            if (differenceFound && previousBoxData != null)
            {
                var prevEggs = previousBoxData.Eggs;
                var prevChicks = previousBoxData.Chicks;
                if (prevChicks + prevEggs > 0 && (thisBoxData.Eggs != prevEggs || thisBoxData.Chicks != prevChicks))
                    summary.Text += $"({string.Concat(Enumerable.Repeat("🥚", prevEggs))}{string.Concat(Enumerable.Repeat("🐣", prevChicks))})";
            }

            // Show breeding status once
            var status = thisBoxData.BreedingStatus;
            if (!string.IsNullOrEmpty(status) && (status != "BR" || (thisBoxData.Chicks + thisBoxData.Eggs == 0)))
                summary.Text += $" {status}";

            if (_remoteBreedingDates != null && _remoteBreedingDates.ContainsKey(boxName))
                summary.Text += "\n" + _remoteBreedingDates[boxName].breedingDateStatus();

            // Last, once every append is in: setting Text after this would drop the crossed icons.
            summary.TextFormatted = CrossedLosses(summary.Text);
            summary.SetTextColor(Color.Black);

            string gateStatus = thisBoxData.GateStatus;
            string notes = string.IsNullOrWhiteSpace(thisBoxData.Notes) ? "" : "notes";
            bool hasBoxNotesForCard = _boxNotes.TryGetValue(boxName, out var cardBoxNote) && !string.IsNullOrWhiteSpace(cardBoxNote.PersistentNotes);
            notes += hasBoxNotesForCard ? $" ({cardBoxNote.PersistentNotes})" : "";
            // NRF percentage removed (requires monitor history)
            string lineThreeStatusText = "";
            if (!string.IsNullOrWhiteSpace(gateStatus) && !string.IsNullOrWhiteSpace(notes))
                lineThreeStatusText = gateStatus + " & " + notes;
            else
                lineThreeStatusText = gateStatus + notes;
            var gate_and_notes = new TextView(this)
            {
                Text = lineThreeStatusText,
                Gravity = GravityFlags.Center,
                TextSize = 14
            };
            gate_and_notes.SetTextColor(Color.Black);

            if(!string.IsNullOrEmpty(summary.Text)) boxOverviewCard.AddView(summary);
            if(!string.IsNullOrEmpty(gate_and_notes.Text)) boxOverviewCard.AddView(gate_and_notes);
            return boxOverviewCard;
        }

        /// <summary>
        /// Creates the breeding dates timeline section showing upcoming milestones grouped by date
        /// </summary>
        private LinearLayout createBreedingDatesTimelineSection()
        {
            var container = _uiFactory.CreateCard(Android.Widget.Orientation.Vertical);

            // Header with title
            var headerRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            var title = new TextView(this)
            {
                Text = "Next breeding dates",
                TextSize = 30,
                Gravity = GravityFlags.Left
            };
            title.SetTextColor(Color.Black);
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            headerRow.AddView(title);
            container.AddView(headerRow);

            // Filter checkboxes row
            var filtersRow = new PenguinMonitor.UI.FlowLayout(this);
            filtersRow.SetPadding(0, 8, 0, 8);
            filtersRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            foreach (var (text, getter, setter) in new (string, Func<bool>, Action<bool>)[] {
                ("Hatch", () => _appSettings.ShowHatchingDatesInTimeline, v => _appSettings.ShowHatchingDatesInTimeline = v),
                ("PG", () => _appSettings.ShowPGDatesInTimeline, v => _appSettings.ShowPGDatesInTimeline = v),
                ("Chip", () => _appSettings.ShowChippingDatesInTimeline, v => _appSettings.ShowChippingDatesInTimeline = v),
                ("Fledge", () => _appSettings.ShowFledgingDatesInTimeline, v => _appSettings.ShowFledgingDatesInTimeline = v),
            })
            {
                var cb = new CheckBox(this) { Text = text, Checked = getter() };
                cb.SetTextColor(Color.Black);
                cb.Click += (s, e) => { setter(cb.Checked); DrawPageLayouts(); };
                filtersRow.AddView(cb);
            }

            container.AddView(filtersRow);

            // Collect all breeding dates from local calculations
            var milestones = new List<(DateTime date, string boxName, string milestone)>();

            // Use remote breeding dates if available
            if (_remoteBreedingDates != null)
            {
                foreach (string boxName in _boxNamesAndIndexes.Keys)
                {
                    if (!_remoteBreedingDates.ContainsKey(boxName)) continue;
                    var bd = _remoteBreedingDates[boxName];
                    if (_appSettings.ShowHatchingDatesInTimeline && !string.IsNullOrEmpty(bd.estHatchDate))
                        milestones.Add((DateTime.Parse(bd.estHatchDate), boxName, "Hatches"));
                    if (_appSettings.ShowPGDatesInTimeline && !string.IsNullOrEmpty(bd.estPGDate))
                        milestones.Add((DateTime.Parse(bd.estPGDate), boxName, "PG"));
                    if (_appSettings.ShowChippingDatesInTimeline && !string.IsNullOrEmpty(bd.chipWindowStart))
                        milestones.Add((DateTime.Parse(bd.chipWindowStart), boxName, "Chipping starts"));
                    if (_appSettings.ShowFledgingDatesInTimeline && !string.IsNullOrEmpty(bd.estFledgeDate))
                        milestones.Add((DateTime.Parse(bd.estFledgeDate), boxName, "Fledges"));
                }
            }

            // Filter to only future dates (including today), then sort
            var today = NzToday;
            var sortedMilestones = milestones
                .Where(m => m.date >= today)
                .OrderBy(m => m.date)
                .ToList();

            // Group by date and render
            var groupedByDate = sortedMilestones.GroupBy(m => m.date.Date);

            foreach (var dateGroup in groupedByDate)
            {
                var dateHeader = new TextView(this)
                {
                    Text = dateGroup.Key.ToString("d MMM"),
                    TextSize = 18,
                    Gravity = GravityFlags.Left
                };
                dateHeader.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
                dateHeader.SetPadding(0, 16, 0, 4);
                dateHeader.SetTextColor(Color.Black);

                container.AddView(dateHeader);

                foreach (var milestone in dateGroup.OrderBy(m => m.boxName))
                {
                    var entryRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
                    entryRow.SetPadding(24, 2, 0, 2);

                    var entryText = new TextView(this)
                    {
                        Text = $"Box {milestone.boxName}: {milestone.milestone}",
                        TextSize = 16
                    };
                    entryText.SetTextColor(Color.Black);

                    // Make clickable to jump to box
                    entryRow.Click += (s, e) =>
                    {
                        JumpToBox(milestone.boxName);
                        ScrollToTop();
                    };

                    entryRow.AddView(entryText);
                    container.AddView(entryRow);
                }
            }

            if (milestones.Count == 0)
            {
                var noDataText = new TextView(this)
                {
                    Text = "No breeding dates available. Dates are calculated from boxes with eggs/chicks.",
                    TextSize = 14
                };
                noDataText.SetTextColor(UIFactory.TEXT_SECONDARY);
                noDataText.SetPadding(0, 16, 0, 8);
                container.AddView(noDataText);
            }

            return container;
        }

        /// <summary>
        private void ScrollToTop()
        {
            if (_rootScrollView == null) return;
            _rootScrollView.Post(() =>
            {
                var animator = ObjectAnimator.OfInt(_rootScrollView, "scrollY", _rootScrollView.ScrollY, 0);
                animator.SetDuration(750); // millis
                animator.SetInterpolator(new DecelerateInterpolator());
                animator.Start();
            });
        }
        private void AnimateHistoricalDataTransition(bool isSwipingToOlder)
        {
            if (_singleBoxDataContentLayout == null || _isAnimating) return;

            _isAnimating = true;

            // Slide distance
            int slideDistance = _singleBoxDataContentLayout.Width;
            // Swipe LEFT (to older): current exits RIGHT, new enters from LEFT
            // Swipe RIGHT (to newer): current exits LEFT, new enters from RIGHT
            int slideOutX = isSwipingToOlder ? slideDistance : -slideDistance;
            int slideInX = isSwipingToOlder ? -slideDistance : slideDistance;

            // Haptic feedback
            _vibrator?.Vibrate(50);

            // Phase 1: Slide out current content
            var slideOut = ObjectAnimator.OfFloat(_singleBoxDataContentLayout, "translationX", 0, slideOutX);
            slideOut?.SetDuration(200);
            slideOut?.SetInterpolator(new AccelerateInterpolator());

            if (slideOut != null)
            {
                slideOut.AnimationEnd += (s, e) =>
                {
                    // Phase 2: Update content (happens off-screen)
                    DrawPageLayouts();

                    // Phase 3: Slide in new content
                    if (_singleBoxDataContentLayout != null)
                    {
                        _singleBoxDataContentLayout.TranslationX = slideInX;
                        var slideIn = ObjectAnimator.OfFloat(_singleBoxDataContentLayout, "translationX", slideInX, 0);
                        slideIn?.SetDuration(250);
                        slideIn?.SetInterpolator(new DecelerateInterpolator());

                        if (slideIn != null)
                        {
                            slideIn.AnimationEnd += (s2, e2) =>
                            {
                                _isAnimating = false;
                            };

                            slideIn.Start();
                        }
                    }
                };

                slideOut.Start();
            }
        }
        // Rebuild the settings card in place so a completed sync's fresh data — the people list
        // especially — shows without an app restart. createSettingsCard reads the caches at build
        // time and is otherwise only called once at startup, so the Observer/Scribe pickers would
        // stay on "Sync to load people" until relaunch.
        /// <summary>Save today's day note — label, observer and scribe — and push it to the server.
        ///
        /// The row is written locally first and flagged pending, so a change made out of signal is
        /// retried by every later sync rather than living in a view that the next redraw discards.
        /// Callers set the fields on _colonyState; this decides the date, the queueing and the toast.
        /// </summary>
        private void SaveDayPeople() => PersistDayNote(null);

        private void PersistDayNote(string? okMessage)
        {
            var dateStr = NzToday.ToString("yyyy-MM-dd");
            _colonyState.DailyLabelDate = dateStr;
            // Queued until the server confirms it, so a note set out of signal isn't left behind
            // on the phone — every sync retries it.
            _colonyState.DailyLabelPendingUpload = true;
            DataStorageService.SaveColonyState(this, _colonyState);

            var label = _colonyState.DailyLabel ?? "";
            var observerId = _colonyState.DailyObserverId;
            var scribeId = _colonyState.DailyScribeId;
            if (!_appSettings.IsAuthenticated)
            {
                Toast.MakeText(this, okMessage != null ? "Daily label set" : "Saved — will sync", ToastLength.Short)?.Show();
                return;
            }

            var colonyId = CurrentColonyIdOrDefault();
            var token = _appSettings.AuthToken;
            _ = Task.Run(async () =>
            {
                bool ok = await _dataStorageService.SaveDayNoteAsync(colonyId, dateStr, label, token, observerId, scribeId);
                if (ok)
                {
                    _colonyState.DailyLabelPendingUpload = false;
                    DataStorageService.SaveColonyState(this, _colonyState);
                }
                new Handler(Looper.MainLooper).Post(() =>
                    Toast.MakeText(this, ok ? (okMessage ?? "Who was out — saved") : "Set locally — will sync", ToastLength.Short)?.Show());
            });
        }

        private void RebuildSettingsCard()
        {
            var parent = _settingsCard?.Parent as ViewGroup;
            if (parent == null) return;
            int idx = parent.IndexOfChild(_settingsCard);
            var wasVisible = _settingsCard.Visibility;
            parent.RemoveView(_settingsCard);
            _suppressBtCheckbox = true;    // the re-created checkbox must not reconnect the scanner
            createSettingsCard();
            _suppressBtCheckbox = false;
            _settingsCard.Visibility = wasVisible;
            parent.AddView(_settingsCard, idx);
        }
        private void createSettingsCard()
        {
            _settingsCard = _uiFactory.CreateCard(borderWidth: 8);
            _settingsCard.Visibility = ViewStates.Gone;

            TextView versionText = new TextView(this)
            {
                Text = "Version: " + version
                ,
                Gravity = GravityFlags.CenterHorizontal
            };
            versionText.SetTextColor(Color.Black);
            _settingsCard.AddView(versionText);

            _isBluetoothEnabledCheckBox = new CheckBox(this)
            {
                Text = "Connect scanner",
            };
            _isBluetoothEnabledCheckBox.SetTextColor(Color.Black);
            _isBluetoothEnabledCheckBox.CheckedChange += (s, e) =>
            {
                // A settings-card rebuild (after every sync) re-fires this; ignore it so we don't
                // dispose and reconnect the live scanner. Only a real user toggle touches Bluetooth.
                if (_suppressBtCheckbox) return;
                if (_isBluetoothEnabledCheckBox.Checked)
                {
                    _appSettings.IsBlueToothEnabled = true;   // must be set before InitializeBluetooth (it gates connect on this)
                    InitializeBluetooth();
                    InitializeGPS();
                }
                else
                {
                    _appSettings.IsBlueToothEnabled = false;
                    _bluetoothManager?.Dispose();
                    _bluetoothManager = null;
                    _locationManager?.RemoveUpdates(this);
                    _gpsAccuracy = -1;
                    UpdateStatusText("Bluetooth & GPS Disabled");
                }
            };
            _isBluetoothEnabledCheckBox.Checked = _appSettings.IsBlueToothEnabled;
            // Daily label
            var dailyLabelLayout = new LinearLayout(this);
            dailyLabelLayout.SetPadding(8, 8, 8, 8);
            var dailyLabelTitle = new TextView(this) { Text = "Daily label:", TextSize = 14 };
            dailyLabelTitle.SetTextColor(Color.Black);
            dailyLabelTitle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            dailyLabelLayout.AddView(dailyLabelTitle);

            var dailyLabelInput = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapSentences,
                Hint = "",
                Text = _colonyState.DailyLabel ?? "",
                TextSize = 14,
            };
            dailyLabelInput.SetTextColor(UIFactory.TEXT_PRIMARY);
            dailyLabelInput.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            dailyLabelInput.SetPadding(12, 8, 12, 8);
            dailyLabelInput.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            dailyLabelInput.Focusable = true;
            dailyLabelInput.FocusableInTouchMode = true;
            dailyLabelInput.Clickable = true;
            dailyLabelInput.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            dailyLabelLayout.AddView(dailyLabelInput);

            // Who was out today. Both are people from the user table, not free text, so the day's
            // record links to the same person the web admin screen manages. One row per colony per
            // day holds the label and both people.
            //
            // Each pick saves ITSELF, the moment it is made — it is not left waiting for the Set
            // button. RebuildSettingsCard() runs after every sync and re-creates these spinners
            // from _colonyState, so a selection sitting only in the view was wiped by the next
            // sync: you chose the observer, the phone synced, the spinner snapped back to blank,
            // and Set then sent null. That is what put "note, observer_id: null, scribe_id: null"
            // in the day_notes audit trail on days somebody had plainly filled both in.
            var dayPeopleLayout = new LinearLayout(this);
            dayPeopleLayout.SetPadding(8, 0, 8, 8);
            var dayUsers = DataStorageService.LoadUsers(this);
            // Index 0 is "not recorded", so a spinner position maps to dayUsers[pos - 1].
            Spinner MakePersonSpinner(int selectedId, string emptyLabel = "")
            {
                // Index 0 is the unset option (users.id 0). Blank for the observer; the scribe
                // labels it "Solo" — one person doing both roles, i.e. no separate scribe.
                var labels = new List<string> { emptyLabel };
                labels.AddRange(dayUsers.Select(u => u.name ?? ""));
                var spinner = _uiFactory.CreateDropdownSpinner();
                spinner.SetPadding(8, 4, 8, 4);
                // SimpleSpinnerItem for the closed view (plain text, no radio); the checked
                // SimpleSpinnerDropDownItem is only for the open list.
                var ad = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, labels);
                ad.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
                spinner.Adapter = ad;
                var idx = dayUsers.FindIndex(u => u.id == selectedId);
                spinner.SetSelection(idx >= 0 ? idx + 1 : 0);
                spinner.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
                return spinner;
            }
            var observerSpinner = MakePersonSpinner(_colonyState.DailyObserverId);
            var scribeSpinner = MakePersonSpinner(_colonyState.DailyScribeId, "Solo");
            // Selected spinner position -> users.id, 0 for "not recorded".
            int SelectedUserId(Spinner s) => s.SelectedItemPosition > 0 && s.SelectedItemPosition <= dayUsers.Count
                ? dayUsers[s.SelectedItemPosition - 1].id : 0;

            // Attaching an adapter fires ItemSelected once for the initial position, so the
            // handlers go on AFTER the spinners are built and are deaf to that first shot — the
            // same reason the scanner checkbox is suppressed across a rebuild.
            void OnPersonPicked(Spinner spinner, bool isObserver)
            {
                // The position the card was built showing. Anything else is a person choosing.
                // Comparing ids instead would misfire on someone whose account has since been
                // deactivated: they aren't in the picker, so it opens at "not recorded", and the
                // first callback would look like a change and overwrite them with nobody.
                int builtAt = spinner.SelectedItemPosition;
                spinner.ItemSelected += (s, e) =>
                {
                    if (e.Position == builtAt) return;
                    builtAt = e.Position;
                    var picked = SelectedUserId(spinner);
                    if (isObserver) _colonyState.DailyObserverId = picked;
                    else _colonyState.DailyScribeId = picked;
                    SaveDayPeople();
                };
            }
            OnPersonPicked(observerSpinner, true);
            OnPersonPicked(scribeSpinner, false);
            // Each spinner sits under its own label so a blank picker is still identifiable.
            LinearLayout LabelledPerson(string label, Spinner sp)
            {
                var col = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
                var colParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
                colParams.SetMargins(4, 0, 4, 0);   // a little gap between the two selectors
                col.LayoutParameters = colParams;
                var lbl = new TextView(this) { Text = label, TextSize = 13 };
                lbl.SetTextColor(Color.Black);
                lbl.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                col.AddView(lbl);
                sp.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                col.AddView(sp);
                return col;
            }
            dayPeopleLayout.AddView(LabelledPerson("Observer", observerSpinner));
            dayPeopleLayout.AddView(LabelledPerson("Scribe", scribeSpinner));
            if (dayUsers.Count == 0)
            {
                var noUsers = new TextView(this) { Text = "Sync to load people", TextSize = 12 };
                noUsers.SetTextColor(UIFactory.TEXT_SECONDARY);
                dayPeopleLayout.AddView(noUsers);
            }

            var setLabelButton = new Button(this) { Text = "Set", TextSize = 12 };
            setLabelButton.SetTextColor(Color.White);
            setLabelButton.SetPadding(16, 8, 16, 8);
            setLabelButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.SUCCESS_GREEN, 8);
            setLabelButton.SetAllCaps(false);
            setLabelButton.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            setLabelButton.Click += (s, e) =>
            {
                // Hide keyboard
                var imm = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
                imm?.HideSoftInputFromWindow(dailyLabelInput.WindowToken, 0);

                // The daily label is the colony's day note (one per colony per day) — no longer a
                // per-observation filename — and observer/scribe are fields of that same row. The
                // spinners have already written themselves to the state; the typed label is the
                // only thing this button still carries in.
                _colonyState.DailyLabel = dailyLabelInput.Text?.Trim() ?? "";
                _colonyState.DailyObserverId = SelectedUserId(observerSpinner);
                _colonyState.DailyScribeId = SelectedUserId(scribeSpinner);
                PersistDayNote("Daily label saved");
            };
            dailyLabelLayout.AddView(setLabelButton);

            // Enter Edit Box Tags mode. There's no matching exit button here — leaving is done
            // with the "Finish editing box details" bar, and settings is locked out while in the mode.
            Button editBoxTagsButton = _uiFactory.CreateStyledButton("Edit box details", UIFactory.SUCCESS_GREEN);
            editBoxTagsButton.Click += (s, e) =>
            {
                // Entering would force the box locked, dropping whatever is half-typed in it
                if (!_isBoxLocked)
                {
                    Toast.MakeText(this, $"Lock Box {_currentBoxName} first", ToastLength.Short)?.Show();
                    return;
                }
                _appSettings.EditBoxTagsMode = true;
                _tagCaptureActive = false;
                _tagCaptureBoxName = "";
                _bestTagCaptureLocation = null;
                _isBoxLocked = true;
                if (_settingsCard != null) _settingsCard.Visibility = ViewStates.Gone;
                if (_expandSettingsButton != null) _expandSettingsButton.SetImageResource(Resource.Drawable.unfold);
                selectedPage = UIFactory.selectedPage.BoxDataSingle;
                DrawPageLayouts();
                ScrollToTop();
            };
            // Region & Colony dropdowns (horizontal)
            var regionColonyLayout = new LinearLayout(this);
            regionColonyLayout.SetPadding(8, 8, 8, 8);

            // Same fixed dropdowns as everywhere else (drag-to-open, popup below, solid highlight).
            var regionSpinner = _uiFactory.CreateDropdownSpinner();
            regionSpinner.SetPadding(8, 4, 8, 4);
            regionSpinner.Prompt = "Region";
            var regionParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            regionParams.SetMargins(4, 0, 4, 0);   // a little gap between the two selectors
            regionSpinner.LayoutParameters = regionParams;
            regionColonyLayout.AddView(regionSpinner);

            var colonySpinner = _uiFactory.CreateDropdownSpinner();
            colonySpinner.SetPadding(8, 4, 8, 4);
            colonySpinner.Prompt = "Colony";
            var colonyParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            colonyParams.SetMargins(4, 0, 4, 0);
            colonySpinner.LayoutParameters = colonyParams;
            regionColonyLayout.AddView(colonySpinner);


            // Show saved colony immediately (before server fetch)
            if (!string.IsNullOrEmpty(_appSettings.SelectedColonyName))
            {
                var savedColony = new[] { _appSettings.SelectedColonyName };
                colonySpinner.Adapter = _uiFactory.CreateSpinnerAdapter(savedColony);
            }

            // Fetch colonies from server and populate dropdowns
            new Thread(async () =>
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{DataStorageService.WILDWATCH_BASE_URL}/colonies.php");
                    if (_appSettings.IsAuthenticated)
                        request.Headers.Add("Authorization", $"Bearer {_appSettings.AuthToken}");
                    var response = await Http.CreateClient(TimeSpan.FromSeconds(10)).SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    var allColonies = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(json) ?? new();

                    RunOnUiThread(() =>
                    {
                        if (allColonies.Count == 0)
                        {
                            Toast.MakeText(this, "No colonies available", ToastLength.Short)?.Show();
                            return;
                        }

                        // First run (or a fresh login) has no colony picked, which leaves the app
                        // on the "no colony loaded" banner until someone works the dropdowns.
                        // Port Tarakohe is the home colony, so default to it when the user is
                        // allowed it — colonies.php only ever returns colonies they can access,
                        // so its presence in this list IS the permission check.
                        // Only when nothing is chosen: a colony the user picked is never overridden,
                        // even if its box sets haven't downloaded yet.
                        if (_appSettings.SelectedColonyId <= 0)
                        {
                            var home = allColonies.FirstOrDefault(c =>
                                    string.Equals(c.TryGetValue("colony_prefix", out var p) ? p?.ToString() : "",
                                                  "PT", StringComparison.OrdinalIgnoreCase))
                                ?? allColonies.FirstOrDefault(c =>
                                    (c["colony_name"]?.ToString() ?? "").Contains("Tarakohe", StringComparison.OrdinalIgnoreCase));
                            if (home != null)
                            {
                                _appSettings.SelectedColonyId = Convert.ToInt32(home["colony_id"]);
                                _appSettings.SelectedColonyName = home["colony_name"]?.ToString() ?? "";
                                _appSettings.SelectedColonyPrefix = home.TryGetValue("colony_prefix", out var hp) ? hp?.ToString() ?? "" : "";
                                _appSettings.AllBoxSetsString = home["location_sets_string"]?.ToString() ?? "";
                                _appSettings.BoxSetString = "All";
                                DataStorageService.saveApplicationSettings(_appSettings);

                                // No cached state to clear — nothing was loaded for any colony yet.
                                CreateBoxSetsDictionary();
                                if (_boxNamesAndIndexes.Count > 0)
                                {
                                    var firstBox = _boxNamesAndIndexes.First().Key;
                                    _currentBoxName = firstBox;
                                    _currentBoxIndex = _boxNamesAndIndexes[firstBox];
                                    _isBoxLocked = true;
                                }
                                DrawPageLayouts();
                                StartSync();
                            }
                        }

                        // Build region list
                        var regions = allColonies.Select(c => c["region_name"]?.ToString() ?? "").Distinct().ToList();
                        regionSpinner.Adapter = _uiFactory.CreateSpinnerAdapter(regions);

                        // When region changes, update colony spinner
                        regionSpinner.ItemSelected += (s, e) =>
                        {
                            var selectedRegion = regions[e.Position];
                            var coloniesInRegion = allColonies.Where(c => c["region_name"]?.ToString() == selectedRegion).ToList();
                            var colonyNames = coloniesInRegion.Select(c => c["colony_name"]?.ToString() ?? "").ToList();
                            colonySpinner.Adapter = _uiFactory.CreateSpinnerAdapter(colonyNames);

                            // Pre-select current colony if in this region
                            var currentIdx = colonyNames.IndexOf(_appSettings.SelectedColonyName);
                            if (currentIdx >= 0) colonySpinner.SetSelection(currentIdx, false);
                        };

                        // When colony changes, apply it
                        colonySpinner.ItemSelected += (s, e) =>
                        {
                            if (_suppressColonySwitch) return;
                            var selectedRegion = regionSpinner.SelectedItem?.ToString() ?? "";
                            var coloniesInRegion = allColonies.Where(c => c["region_name"]?.ToString() == selectedRegion).ToList();
                            if (e.Position >= coloniesInRegion.Count) return;

                            var selected = coloniesInRegion[e.Position];
                            var newId = Convert.ToInt32(selected["colony_id"]);
                            if (newId == _appSettings.SelectedColonyId) return;

                            void ApplySwitch()
                            {
                                _appSettings.SelectedColonyId = newId;
                                _appSettings.SelectedColonyName = selected["colony_name"]?.ToString() ?? "";
                                _appSettings.SelectedColonyPrefix = selected.TryGetValue("colony_prefix", out var cpfx) ? cpfx?.ToString() ?? "" : "";
                                _appSettings.AllBoxSetsString = selected["location_sets_string"]?.ToString() ?? "";
                                _appSettings.BoxSetString = "All";
                                DataStorageService.saveApplicationSettings(_appSettings);

                                // Reset the single-colony cached state so the new colony's sync repopulates cleanly
                                _colonyState.TodayBoxes.Clear();
                                _colonyState.PreviousBoxes.Clear();
                                _colonyState.TodayBiometrics.Clear();
                                _colonyState.PendingObservations.Clear();
                                _colonyState.LastSyncedUtc = DateTime.MinValue;
                                DataStorageService.SaveColonyState(this, _colonyState);

                                CreateBoxSetsDictionary();
                                if (_boxNamesAndIndexes.Count > 0)
                                {
                                    var first = _boxNamesAndIndexes.First().Key;
                                    _currentBoxName = first;
                                    _currentBoxIndex = _boxNamesAndIndexes[first];
                                    _isBoxLocked = true;
                                }
                                DrawPageLayouts();
                                StartSync();               // download the new colony's data
                                WarmEmbedWebView();         // re-point the warm embed at the new colony
                            }

                            void RevertColonySpinner()
                            {
                                _suppressColonySwitch = true;
                                var curIdx = coloniesInRegion.FindIndex(c => Convert.ToInt32(c["colony_id"]) == _appSettings.SelectedColonyId);
                                if (curIdx >= 0) colonySpinner.SetSelection(curIdx, false);
                                _suppressColonySwitch = false;
                            }

                            // Unsynced changes belong to the current colony and must not upload to another,
                            // so normally you must sync first. But a change that CAN'T upload (e.g. one that
                            // silently keeps failing) would otherwise trap you here forever — so also offer
                            // to discard it and switch.
                            // Unsynced work belongs to the CURRENT colony and must never upload to
                            // another — colony is only read at upload time, so switching first would
                            // file it under the new one. UnsentTotal counts every queue (chips, day
                            // notes, watched flags), not just observations/biometrics: a bird chipped
                            // here would otherwise land in the colony you switched to.
                            int unsynced = UnsentTotal();
                            int unsyncedChips = _dataStorageService.LoadQueuedChips(this).Count;
                            if (unsynced > 0)
                            {
                                var builder = new AlertDialog.Builder(this)
                                    .SetTitle("Unsynced changes")
                                    .SetCancelable(false)
                                    .SetPositiveButton("Sync now", (s2, e2) => { RevertColonySpinner(); StartSync(); })
                                    .SetNegativeButton("Cancel", (s2, e2) => RevertColonySpinner());
                                if (unsyncedChips > 0)
                                {
                                    // A queued chip is a tag physically in a bird — sync-only, never
                                    // discarded. No "discard" path while chips are waiting.
                                    builder.SetMessage($"{unsynced} change{(unsynced == 1 ? "" : "s")} for {_appSettings.SelectedColonyName} haven't uploaded yet, including {unsyncedChips} bird{(unsyncedChips == 1 ? "" : "s")} chipped offline. Sync first to switch to {selected["colony_name"]} — chipped birds can't be discarded.");
                                }
                                else
                                {
                                    builder.SetMessage($"{unsynced} change{(unsynced == 1 ? "" : "s")} for {_appSettings.SelectedColonyName} haven't uploaded yet. Sync first, or discard {(unsynced == 1 ? "it" : "them")} to switch to {selected["colony_name"]}.")
                                        .SetNeutralButton("Discard & switch", (s2, e2) =>
                                        {
                                            _colonyState.PendingObservations.RemoveAll(p => p.IsPendingUpload);
                                            foreach (var b in _colonyState.TodayBiometrics.Values) b.IsPendingUpload = false;
                                            _colonyState.PendingDayNotes.Clear();
                                            _colonyState.DailyLabelPendingUpload = false;
                                            DataStorageService.SaveColonyState(this, _colonyState);
                                            // Box-note / watched edits are keyed to the old colony's
                                            // locations; clear their pending flags too so they don't
                                            // upload to the colony we're switching to.
                                            foreach (var n in _boxNotes.Values) { n.NotesPendingUpload = false; n.WatchedPendingUpload = false; }
                                            _dataStorageService.SaveBoxNotesToDisk(this, _boxNotes);
                                            ApplySwitch();
                                        });
                                }
                                builder.Show();
                                return;
                            }

                            ApplySwitch();
                        };

                        // Pre-select current region
                        var currentRegion = allColonies.FirstOrDefault(c => c["colony_name"]?.ToString() == _appSettings.SelectedColonyName);
                        if (currentRegion != null)
                        {
                            var regionIdx = regions.IndexOf(currentRegion["region_name"]?.ToString() ?? "");
                            if (regionIdx >= 0) regionSpinner.SetSelection(regionIdx, false);
                        }
                    });
                }
                catch { }
            }).Start();


            // Daily label warning

            // Logout/Login button
            View authButton;
            if (_appSettings?.IsAuthenticated == true)
            {
                var logoutButton = _uiFactory.CreateStyledButton($"Logout {_appSettings.ObserverName}", UIFactory.DANGER_RED);
                logoutButton.Click += (s, e) =>
                {
                    int pending = UnsentTotal();   // every queue — incl. chips, day notes, watched flags
                    int pendingChips = _dataStorageService.LoadQueuedChips(this).Count;
                    var chipWarn = pendingChips > 0
                        ? $" This includes {pendingChips} bird{(pendingChips == 1 ? "" : "s")} chipped offline — losing {(pendingChips == 1 ? "it" : "them")} leaves a tag with no record of the bird it's in."
                        : "";
                    var message = pending > 0
                        ? $"Logout {_appSettings.ObserverName}?\n\n⚠️ You have {pending} unsynced record{(pending == 1 ? "" : "s")} that will be PERMANENTLY LOST. Sync first to keep them.{chipWarn}\n\nAll local data will be cleared and you'll need to log in again."
                        : $"Logout {_appSettings.ObserverName}?\n\nAll local data will be cleared and you'll need to log in again to sync.";
                    new AlertDialog.Builder(this)
                        .SetTitle("Logout")
                        .SetMessage(message)
                        .SetPositiveButton("Logout & Clear", (s2, e2) => PerformFullLogout())
                        .SetNegativeButton("Cancel", (s2, e2) => { })
                        .Show();
                };
                var logoutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                logoutParams.SetMargins(8, 8, 8, 8);
                logoutButton.LayoutParameters = logoutParams;
                authButton = logoutButton;
            }
            else
            {
                var loginButton = _uiFactory.CreateStyledButton("Login to Wildwatch", UIFactory.PRIMARY_BLUE);
                loginButton.Click += (s, e) => ShowLoginPrompt();
                var loginParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                loginParams.SetMargins(8, 8, 8, 8);
                loginButton.LayoutParameters = loginParams;
                authButton = loginButton;
            }

            // === Add all elements in order ===
            // 1. Region/Colony (top)
            _settingsCard.AddView(regionColonyLayout);
            // 2. Daily label
            _settingsCard.AddView(dayPeopleLayout);
            _settingsCard.AddView(dailyLabelLayout);
            // 3. Bluetooth enable + Scan BT are added together as a row below

            // Scanner device picker
            // Scanner selection — remembered scanners (enable / nickname / delete) + discovery
            var scannerSection = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            scannerSection.SetPadding(8, 0, 8, 8);

            IDisposable? discoveryHandle = null;
            var scanButton = _uiFactory.CreateStyledButton("Scan BT", UIFactory.PRIMARY_BLUE);
            var rememberedContainer = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var deviceListLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            // Rebuild the list of remembered scanners
            void RefreshRemembered()
            {
                rememberedContainer.RemoveAllViews();
                var scanners = _appSettings.RememberedScanners;
                if (scanners.Count == 0)
                {
                    var none = new TextView(this) { Text = "No scanners remembered — tap Scan BT to add one", TextSize = 13 };
                    none.SetTextColor(UIFactory.TEXT_SECONDARY);
                    rememberedContainer.AddView(none);
                    return;
                }
                if (scanners.Count(s => s.Enabled) > 1)
                {
                    var hint = new TextView(this) { Text = "Enabled scanners are tried top-to-bottom until one connects.", TextSize = 12 };
                    hint.SetTextColor(UIFactory.TEXT_SECONDARY);
                    rememberedContainer.AddView(hint);
                }
                foreach (var scanner in scanners.ToList())
                {
                    var sc = scanner;
                    var row = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    var rowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                    rowParams.SetMargins(0, 4, 0, 4);
                    row.LayoutParameters = rowParams;

                    var cb = new CheckBox(this) { Checked = sc.Enabled };
                    cb.SetTextColor(Color.Black);
                    cb.CheckedChange += (s2, e2) =>
                    {
                        if (sc.Enabled == e2.IsChecked) return;
                        sc.Enabled = e2.IsChecked;
                        DataStorageService.saveApplicationSettings(_appSettings);
                        RestartBluetooth();
                    };
                    row.AddView(cb);

                    var nameTv = new TextView(this) { Text = sc.DisplayName, TextSize = 14 };
                    nameTv.SetTextColor(sc.Enabled ? Color.Black : UIFactory.TEXT_SECONDARY);
                    nameTv.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                    row.AddView(nameTv);

                    Button chip(string text, Color bg)
                    {
                        var b = new Button(this) { Text = text, TextSize = 12 };
                        b.SetAllCaps(false);
                        b.SetTextColor(Color.White);
                        b.SetPadding(16, 6, 16, 6);
                        b.Background = _uiFactory.CreateRoundedBackground(bg, 8);
                        var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                        p.SetMargins(4, 0, 0, 0);
                        b.LayoutParameters = p;
                        return b;
                    }

                    // Increase preference — move up so it's tried earlier (wide tap target)
                    var upBtn = chip("^", UIFactory.PRIMARY_BLUE);
                    upBtn.SetPadding(96, 6, 96, 6);   // ~5x wider than a normal chip
                    if (_appSettings.RememberedScanners.IndexOf(sc) <= 0) { upBtn.Enabled = false; upBtn.Alpha = 0.4f; }
                    upBtn.Click += (s2, e2) =>
                    {
                        var i = _appSettings.RememberedScanners.IndexOf(sc);
                        if (i <= 0) return;
                        _appSettings.RememberedScanners.RemoveAt(i);
                        _appSettings.RememberedScanners.Insert(i - 1, sc);
                        DataStorageService.saveApplicationSettings(_appSettings);
                        RestartBluetooth();
                        RefreshRemembered();
                    };
                    row.AddView(upBtn);

                    var renameBtn = chip("Rename", UIFactory.PRIMARY_BLUE);
                    renameBtn.Click += (s2, e2) => ShowScannerNicknameDialog(sc, RefreshRemembered);
                    row.AddView(renameBtn);

                    var delBtn = chip("Delete", UIFactory.DANGER_RED);
                    delBtn.Click += (s2, e2) => ShowConfirmationDialog("Delete scanner",
                        $"Remove {sc.DisplayName} from remembered scanners?",
                        ("Delete", () =>
                        {
                            _appSettings.RememberedScanners.Remove(sc);
                            DataStorageService.saveApplicationSettings(_appSettings);
                            RestartBluetooth();
                            RefreshRemembered();
                        }),
                        ("Cancel", () => { }));
                    row.AddView(delBtn);

                    rememberedContainer.AddView(row);
                }
            }
            RefreshRemembered();

            scanButton.Click += (s, e) =>
            {
                // Android 12+ needs BLUETOOTH_SCAN/CONNECT granted at runtime — re-request if missing
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    var missing = new[] { Android.Manifest.Permission.BluetoothScan, Android.Manifest.Permission.BluetoothConnect }
                        .Where(p => CheckSelfPermission(p) != Android.Content.PM.Permission.Granted).ToArray();
                    if (missing.Length > 0)
                    {
                        RequestPermissions(missing, 1);
                        Toast.MakeText(this, "Allow Bluetooth, then tap 'Scan BT' again", ToastLength.Long)?.Show();
                        return;
                    }
                }

                deviceListLayout.Visibility = Android.Views.ViewStates.Visible;
                discoveryHandle?.Dispose();
                deviceListLayout.RemoveAllViews();
                scanButton.Text = "Scanning...";
                scanButton.Enabled = false;
                var foundAddresses = new HashSet<string>();

                discoveryHandle = BluetoothManager.StartDiscovery(this, (address, name) =>
                {
                    RunOnUiThread(() =>
                    {
                        if (!foundAddresses.Add(address)) return;
                        var btn = _uiFactory.CreateStyledButton($"{name}\n{address}", UIFactory.SUCCESS_GREEN);
                        btn.SetAllCaps(false);
                        btn.Click += (_, _) =>
                        {
                            discoveryHandle?.Dispose();
                            deviceListLayout.RemoveAllViews();
                            deviceListLayout.Visibility = Android.Views.ViewStates.Gone;
                            scanButton.Text = "Scan BT";
                            scanButton.Enabled = true;
                            RememberScanner(address, name);   // adds/enables + reconnects
                            RefreshRemembered();
                            Toast.MakeText(this, $"Added {name}", ToastLength.Short)?.Show();
                        };
                        var btnParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                        btnParams.SetMargins(0, 4, 0, 4);
                        btn.LayoutParameters = btnParams;
                        deviceListLayout.AddView(btn);
                    });
                }, () =>
                {
                    RunOnUiThread(() =>
                    {
                        scanButton.Text = "Scan BT";
                        scanButton.Enabled = true;
                    });
                });
            };

            deviceListLayout.Visibility = Android.Views.ViewStates.Gone;

            // "Enable BT & GPS" checkbox with "Scan BT" to its right (checkbox text keeps full size — it wraps rather than shrinks)
            var btRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            btRow.SetGravity(GravityFlags.CenterVertical);
            btRow.SetPadding(8, 4, 8, 4);
            _isBluetoothEnabledCheckBox.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            btRow.AddView(_isBluetoothEnabledCheckBox);
            var scanBtnParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            scanBtnParams.SetMargins(8, 0, 0, 0);
            scanButton.LayoutParameters = scanBtnParams;
            btRow.AddView(scanButton);
            _settingsCard.AddView(btRow);

            scannerSection.AddView(rememberedContainer);
            scannerSection.AddView(deviceListLayout);
            _settingsCard.AddView(scannerSection);

            // 4. Box Tags + Sync + Logout/Login (horizontal row)
            _syncButton = _uiFactory.CreateStyledButton("Force Sync", UIFactory.PRIMARY_BLUE);
            _syncButton.Click += OnSyncClick;

            // Escape hatch for a sync that won't go through: today's data as a file the observer
            // can hand over by any means, so a day's work is never trapped on the phone.
            var exportJsonButton = _uiFactory.CreateStyledButton("Export todays json", UIFactory.PRIMARY_BLUE);
            exportJsonButton.Click += (s, e) => ExportTodayJson();

            // One row of four when the short labels fit it, two rows of two when they don't —
            // and in either case each button keeps the longest wording its own width allows.
            // Buttons share the width equally rather than sizing to their label: wrapping flow
            // left ragged gaps and made the shortest actions the hardest to hit.
            var density = Resources?.DisplayMetrics?.Density ?? 2;
            var actionButtons = new (Button btn, string[] variants)[]
            {
                (editBoxTagsButton, new[] { "Edit box details", "Box detail", "Boxes" }),
                (_syncButton,       new[] { "Force Sync", "Sync" }),
                (exportJsonButton,  new[] { "Export todays json", "Export json", "Json" }),
                (authButton as Button ?? new Button(this),
                                    _appSettings?.IsAuthenticated == true
                                        ? new[] { $"Logout {_appSettings.ObserverName}", "Logout" }
                                        : new[] { "Login to Wildwatch", "Login" }),
            };
            foreach (var (btn, _) in actionButtons)
            {
                btn.SetPadding((int)(8 * density), (int)(6 * density), (int)(8 * density), (int)(6 * density));
                btn.SetMinimumWidth(0);
                // One line only. A Button left to wrap turns a label too wide for its column into
                // two or three stacked lines, and the whole row grows to match. The fitter picks a
                // label that fits, so nothing is cut off by holding it to one line.
                btn.SetMaxLines(1);
                // The theme gives every Button a 48dp minHeight and font padding on top of
                // whatever padding is set, which is what made these so tall. Cleared, then held
                // to 40dp so they stay a reasonable target for a cold thumb.
                btn.SetMinHeight(0);
                btn.SetMinimumHeight((int)(40 * density));
                btn.SetIncludeFontPadding(false);
            }

            // Estimated only — the card's exact inner width isn't known until layout. It decides
            // one row versus two; the per-button fitter below then settles the wording against the
            // real width, so an estimate that's slightly out costs a shorter label, not a clipped one.
            var usableWidth = (Resources?.DisplayMetrics?.WidthPixels ?? (int)(360 * density)) - (int)(56 * density);
            bool ShortestFitsAcross(int columns)
            {
                var per = usableWidth / (float)columns - 6 * density;
                foreach (var (btn, variants) in actionButtons)
                {
                    var shortest = variants[variants.Length - 1];
                    var need = (btn.Paint?.MeasureText(shortest) ?? 0) + btn.PaddingLeft + btn.PaddingRight;
                    if (need > per) return false;
                }
                return true;
            }
            int columnCount = ShortestFitsAcross(4) ? 4 : 2;

            var actionRows = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var actionRowsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            actionRowsParams.SetMargins(0, 4, 0, 4);
            actionRows.LayoutParameters = actionRowsParams;

            LinearLayout currentRow = null!;
            for (int i = 0; i < actionButtons.Length; i++)
            {
                if (i % columnCount == 0)
                {
                    currentRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
                    var rowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                    rowParams.SetMargins(0, 0, 0, (int)(6 * density));
                    currentRow.LayoutParameters = rowParams;
                    actionRows.AddView(currentRow);
                }
                var v = actionButtons[i].btn;
                var p = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                bool firstInRow = i % columnCount == 0;
                p.SetMargins(firstInRow ? 0 : (int)(3 * density), 0, (int)(3 * density), 0);
                v.LayoutParameters = p;
                currentRow.AddView(v);
            }
            _settingsCard.AddView(actionRows);

            // Longest first: each button keeps the most descriptive label its own width allows.
            foreach (var (btn, variants) in actionButtons) FitLabel(btn, variants);
            _refitSyncLabel = () => _syncButton.Post(() => FitLabelNow(_syncButton, SyncButtonVariants()));

            // "Force Sync" is gone because there is nothing left to force: the download is the whole
            // field set every time, so a forced sync and an ordinary one do the same work. What's
            // left is the cache itself going wrong — a shape change, or a file that won't read.
            // Long-press throws it away and pulls again. Unsent work is untouched: it lives in the
            // upload queue, not the cache.
            _syncButton.LongClick += (s, e) =>
            {
                new AlertDialog.Builder(this)
                    .SetTitle("Refetch everything?")
                    .SetMessage("Throws away the downloaded copy of the colony and pulls it again.\n\n"
                              + "Anything waiting to upload is kept. Only worth doing if the boxes on screen look wrong.")
                    .SetNegativeButton("Cancel", (s2, e2) => { })
                    .SetPositiveButton("Refetch", (s2, e2) =>
                    {
                        var db = SnapshotSyncService.Load(this);
                        db.Reset();
                        SnapshotSyncService.Save(this, db);
                        StartSync(silent: false);
                    })
                    .Show();
            };
        }

        private void OnScrollViewTouch(object? sender, View.TouchEventArgs e)
        {
            if (e.Event?.Action == MotionEventActions.Down)
            {
                _lastTouchDownY = e.Event.GetY();
            }

            if (_gestureDetector != null && e.Event != null)
            {
                _gestureDetector.OnTouchEvent(e.Event);
            }
            e.Handled = false; // Allow scrolling to continue
        }
        private LinearLayout CreateStyledButtonLayout(params (string text, EventHandler handler, Color color)[] buttons)
        {
            var layout = new LinearLayout(this);
            for (int i = 0; i < buttons.Length; i++)
            {
                var (text, handler, color) = buttons[i];
                var button = _uiFactory.CreateStyledButton(text, color);
                button.Click += handler;

                var buttonParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
                if (i > 0) buttonParams.SetMargins(8, 0, 0, 0);
                button.LayoutParameters = buttonParams;

                layout.AddView(button);
            }
            return layout;
        }
        public bool IsTouchInContentArea(float touchY)
        {
            if (_singleBoxDataContentLayout == null) return false;

            int[] contentLocation = new int[2];
            _singleBoxDataContentLayout.GetLocationOnScreen(contentLocation);

            int contentTop = contentLocation[1] - (_rootScrollView?.ScrollY ?? 0);
            int contentBottom = contentTop + _singleBoxDataContentLayout.Height;

            return touchY >= contentTop && touchY <= contentBottom;
        }
        private LinearLayout CreateNavigationLayout()
        {
            var layout = new LinearLayout(this);
            layout.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            var density = Resources?.DisplayMetrics?.Density ?? 2;
            _prevBoxButton = _uiFactory.CreateStyledButton("← Prev", UIFactory.PRIMARY_BLUE);
            _prevBoxButton.Click += OnPrevBoxClick;
            _prevBoxButton.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));
            var buttonParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            buttonParams.SetMargins(8, 8, 8, 8);
            _prevBoxButton.LayoutParameters = buttonParams;

            _selectBoxButton = _uiFactory.CreateStyledButton("Select", UIFactory.PRIMARY_BLUE);
            _selectBoxButton.Click += (s,e) => ShowBoxJumpDialog();
            _selectBoxButton.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));
            _selectBoxButton.LayoutParameters = buttonParams;

            _nextBoxButton = _uiFactory.CreateStyledButton("Next →", UIFactory.PRIMARY_BLUE);
            _nextBoxButton.Click += OnNextBoxClick;
            _nextBoxButton.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));
            _nextBoxButton.LayoutParameters = buttonParams;

            layout.AddView(_prevBoxButton);
            layout.AddView(_selectBoxButton);
            layout.AddView(_nextBoxButton);

            return layout;
        }
        private string GetShowFilterText()
        {
            if (_appSettings.ShowAllBoxesInMultiBoxView)
                return "all";

            var filters = new List<string>();
            if (_appSettings.ShowBoxesWithDataInMultiBoxView) filters.Add("with data");
            if (_appSettings.ShowNoBoxesInMultiBoxView) filters.Add("NO");
            if (_appSettings.ShowUnlikleyBoxesInMultiBoxView) filters.Add("UNL");
            if (_appSettings.ShowPotentialBoxesInMultiBoxView) filters.Add("POT");
            if (_appSettings.ShowConfidentBoxesInMultiBoxView) filters.Add("CON");
            if (_appSettings.ShowBreedingBoxesInMultiBoxView) filters.Add("BR");
            if (_appSettings.ShowDCMBoxesInMultiboxView) filters.Add("DCM");
            if (_appSettings.ShowABNBoxesInMultiboxView) filters.Add("ABN");
            if (_appSettings.showBoxesWithNotesInMultiboxView) filters.Add("notes");
            if (_appSettings.ShowInterestingBoxesInMultiBoxView) filters.Add("box notes");
            if (_appSettings.ShowSingleEggBoxesInMultiboxView) filters.Add("1 egg");
            if (_appSettings.ShowDoubleEggBoxesInMultiboxView) filters.Add("2 egg");
            if (_appSettings.ShowNoScanBoxesInMultiboxView) filters.Add("no scan");
            if (_appSettings.ShowWatchedBoxesInMultiBoxView) filters.Add("watched");
            if (_appSettings.ShowChipOnlyBoxesInMultiBoxView) filters.Add("chip only");

            return filters.Count > 0 ? string.Join(", ", filters) : "none";
        }

        private string GetHideFilterText()
        {
            var filters = new List<string>();
            if (_appSettings.HideBoxesWithDataInMultiBoxView) filters.Add("with data");
            if (_appSettings.HideNoBoxesInMultiBoxView) filters.Add("NO");
            if (_appSettings.HideUnlikelyBoxesInMultiBoxView) filters.Add("UNL");
            if (_appSettings.HidePotentialBoxesInMultiBoxView) filters.Add("POT");
            if (_appSettings.HideConfidentBoxesInMultiBoxView) filters.Add("CON");
            if (_appSettings.HideBreedingBoxesInMultiBoxView) filters.Add("BR");
            if (_appSettings.HideDCMInMultiBoxView) filters.Add("DCM");
            if (_appSettings.HideABNInMultiBoxView) filters.Add("ABN");
            if (_appSettings.HideBoxesWithNotesInMultiboxView) filters.Add("notes");
            if (_appSettings.HideInterestingBoxesInMultiBoxView) filters.Add("box notes");
            if (_appSettings.HideSingleEggBoxesInMultiboxView) filters.Add("1 egg");
            if (_appSettings.HideDoubleEggBoxesInMultiboxView) filters.Add("2 egg");
            if (_appSettings.HideBeforeCurrentInMultiBoxView) filters.Add("<current");

            return filters.Count > 0 ? string.Join(", ", filters) : "none";
        }

        internal void DrawPageLayouts()
        {
            new Handler(Looper.MainLooper).Post(() =>
                {
                    // Day rollover: move yesterday's data to previous
                    if (!_isHistoricalView)
                        _colonyState.RolloverDay();

                    // Historical view mode
                    if (_appTitleText != null)
                        // PT (Tarakohe, the home colony) shows a bare "Nestcheck"; other colonies
                        // keep their acronym so it's obvious at a glance you're not in PT.
                        _appTitleText.Text = _isHistoricalView ? "Json Nest Viewer"
                            : $"Nestcheck {(CurrentColonyAcronym() == "PT" ? "" : CurrentColonyAcronym())}".TrimEnd();
                    if (_exitHistoricalButton != null)
                        _exitHistoricalButton.Visibility = _isHistoricalView ? ViewStates.Visible : ViewStates.Gone;
                    if (_isHistoricalView)
                    {
                        // Show title but hide settings expand and all other controls
                        if (_expandSettingsButton != null) _expandSettingsButton.Visibility = ViewStates.Gone;
                        if (_statusText != null)
                        {
                            _statusText.Text = _historicalFilename;
                            _statusText.Visibility = ViewStates.Visible;
                        }
                        if (_settingsCard != null) _settingsCard.Visibility = ViewStates.Gone;
                        if (_breedingDatesCard != null) _breedingDatesCard.Visibility = ViewStates.Gone;
                        if (_prevObsHeaderText != null) _prevObsHeaderText.Visibility = ViewStates.Gone;
                        if (_prevObsSummaryLayout != null) _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                        if (_stickyNoteBar != null) _stickyNoteBar.Visibility = ViewStates.Gone;
                        if (_tagModeContentLayout != null) _tagModeContentLayout.Visibility = ViewStates.Gone;
                        if (_unsyncedCardsContainer != null) _unsyncedCardsContainer.Visibility = ViewStates.Gone;
                    }
                    else
                    {
                        if (_expandSettingsButton != null) _expandSettingsButton.Visibility = ViewStates.Visible;
                        if (_statusText != null) _statusText.Visibility = ViewStates.Visible;
                        if (_stickyNoteBar != null) _stickyNoteBar.Visibility = ViewStates.Visible;
                        if (_unsyncedCardsContainer != null) _unsyncedCardsContainer.Visibility = ViewStates.Visible;
                    }

                    // Box tag mode: hide normal content, show simplified tag view
                    bool tagMode = _appSettings.EditBoxTagsMode && !_isHistoricalView;
                    if (_singleBoxDataContentLayout != null && tagMode)
                        _singleBoxDataContentLayout.Visibility = ViewStates.Gone;
                    if (_multiBoxViewCard != null)
                        _multiBoxViewCard.Visibility = tagMode ? ViewStates.Gone : ViewStates.Visible;
                    if (_settingsCard != null)
                        _settingsCard.Visibility = tagMode ? ViewStates.Gone : _settingsCard.Visibility;
                    if (_breedingDatesCard != null)
                        _breedingDatesCard.Visibility = tagMode ? ViewStates.Gone : ViewStates.Visible;
                    // The tag panel carries its own "last known contents" card, so the normal
                    // previous-obs badge would just duplicate it.
                    if (tagMode)
                    {
                        if (_prevObsHeaderText != null) _prevObsHeaderText.Visibility = ViewStates.Gone;
                        if (_prevObsSummaryLayout != null) _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                        if (_stickyNoteBar != null) _stickyNoteBar.Visibility = ViewStates.Gone;
                        if (_stickyNoteBelowPrev != null) _stickyNoteBelowPrev.Visibility = ViewStates.Gone;
                    }

                    // Mode chrome: purple wash, top banner and the fixed finish bar
                    _rootScrollView?.SetBackgroundColor(tagMode ? TAG_MODE_WASH : UIFactory.LIGHT_GRAY);
                    if (_tagModeBanner != null)
                        _tagModeBanner.Visibility = tagMode ? ViewStates.Visible : ViewStates.Gone;
                    // Exactly one bottom bar at a time: Save/Cancel owns the thumb zone during a
                    // capture, Finish owns it otherwise. Two stacked full-width buttons both
                    // starting with a tick was asking to be mis-tapped in the field.
                    bool barCapturing = tagMode && _tagCaptureActive;
                    if (_tagModeFinishBar != null)
                        _tagModeFinishBar.Visibility = tagMode && !barCapturing ? ViewStates.Visible : ViewStates.Gone;
                    if (_tagModeCaptureBar != null)
                        _tagModeCaptureBar.Visibility = barCapturing ? ViewStates.Visible : ViewStates.Gone;
                    if (_tagModeBottomSpacer != null)
                        _tagModeBottomSpacer.Visibility = tagMode ? ViewStates.Visible : ViewStates.Gone;
                    // Settings can't be opened from inside the mode
                    if (_expandSettingsButton != null && tagMode)
                        _expandSettingsButton.Visibility = ViewStates.Invisible;

                    // Force single box view in tag mode
                    if (tagMode)
                    {
                        selectedPage = UIFactory.selectedPage.BoxDataSingle;
                    }

                    // Tag mode content: last known contents, current tag/position, capture panel
                    if (_tagModeContentLayout != null)
                    {
                        _tagModeContentLayout.Visibility = tagMode ? ViewStates.Visible : ViewStates.Gone;
                        if (tagMode)
                        {
                            // Freshest thing we know about the box: today's record if there is one,
                            // otherwise the last earlier visit. Black border for today, orange for older.
                            var todayObs = _colonyState.GetTodayForBox(_currentBoxName);
                            var lastObs = todayObs ?? (_colonyState.PreviousBoxes.ContainsKey(_currentBoxName)
                                ? _colonyState.PreviousBoxes[_currentBoxName] : null);
                            _tagModeLastObsCard!.RemoveAllViews();
                            if (lastObs != null)
                            {
                                bool isToday = todayObs != null;
                                var obsNz = ToNzTime(lastObs.WhenDataCollectedUtc);
                                int daysAgo = (int)(NzToday - obsNz.Date).TotalDays;
                                var whenStr = isToday ? $"Today {obsNz:HH:mm}"
                                    : daysAgo == 1 ? $"Yesterday {obsNz:HH:mm}"
                                    : $"{daysAgo} days ago — {obsNz:d MMM HH:mm}";
                                var byWhom = !string.IsNullOrEmpty(lastObs.ObserverName) ? $" by {lastObs.ObserverName}" : "";

                                _tagModeLastObsCard.Background = _uiFactory.CreateCardBackground(
                                    borderWidth: 4, borderColour: isToday ? Color.Black : UIFactory.WARNING_YELLOW);
                                var obsHeader = new TextView(this)
                                {
                                    TextSize = 13,
                                    Text = $"Last known contents — {whenStr}{byWhom}"
                                };
                                obsHeader.SetTextColor(isToday ? Color.Black : UIFactory.WARNING_YELLOW);
                                obsHeader.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                                _tagModeLastObsCard.AddView(obsHeader);
                                _tagModeLastObsCard.AddView(BuildObsDetailView(lastObs, showBoxLink: false));
                                _tagModeLastObsCard.Visibility = ViewStates.Visible;
                            }
                            else
                            {
                                _tagModeLastObsCard.Visibility = ViewStates.Gone;
                            }

                            // Only editors and admins may delete on the server, so only they are
                            // offered it. Re-checked on every draw, because the role comes down
                            // with the sync and can change while the app is running.
                            if (_tagModeDeleteObsButton != null)
                            {
                                _tagModeDeleteObsButton.Visibility =
                                    _appSettings.CanEditRecords ? ViewStates.Visible : ViewStates.Gone;
                                _tagModeDeleteObsButton.Text = $"🗑  Delete today's observation for Box {_currentBoxName}";
                            }

                            _boxTags.TryGetValue(_currentBoxName, out var tagInfo);
                            bool hasTag = tagInfo != null && !string.IsNullOrEmpty(tagInfo.TagNumber);
                            bool hasPosition = tagInfo != null && (tagInfo.Latitude != 0 || tagInfo.Longitude != 0);

                            _tagModeTagText!.Text = hasTag ? $"🏷 Tag: {tagInfo!.TagNumber}" : "🏷 No tag assigned";
                            _tagModeTagText.SetTextColor(hasTag ? Color.Black : Color.DarkGray);

                            if (hasPosition)
                            {
                                var acc = tagInfo!.Accuracy > 0 ? $"±{FormatAccuracy(tagInfo.Accuracy)}" : "accuracy unknown";
                                var when = tagInfo.ScanTimeUTC != default ? ToNzTime(tagInfo.ScanTimeUTC).ToString("d MMM yyyy") : "";
                                var posLine = $"📍 Position stored ({acc})" + (when != "" ? $", set {when}" : "");
                                // How far off you are says whether you're at the right nest at all.
                                // Uses the app-wide fix, which may be stale — shown, never saved.
                                if (_currentLocation != null)
                                {
                                    var d = new float[1];
                                    Location.DistanceBetween(tagInfo.Latitude, tagInfo.Longitude,
                                        _currentLocation.Latitude, _currentLocation.Longitude, d);
                                    posLine += $"\n     You are about {d[0]:F0} m from it.";
                                }
                                _tagModePositionText!.Text = posLine;
                                _tagModePositionText.SetTextColor(Color.Black);
                            }
                            else
                            {
                                _tagModePositionText!.Text = "📍 No position stored";
                                _tagModePositionText.SetTextColor(Color.DarkGray);
                            }

                            bool capturing = _tagCaptureActive && _tagCaptureBoxName == _currentBoxName;
                            _tagModeSetButton!.Visibility = capturing ? ViewStates.Gone : ViewStates.Visible;
                            _tagModeSetTagButton!.Visibility = capturing ? ViewStates.Gone : ViewStates.Visible;
                            _tagModeCapturePanel!.Visibility = capturing ? ViewStates.Visible : ViewStates.Gone;
                            if (capturing) UpdateTagCaptureSteps();
                        }
                    }

                    ///Single Box Card
                    // Hide the webview button in tag mode, but keep lock icon and click area
                    if (_webviewButton != null)
                    {
                        _webviewButton.Visibility = tagMode ? ViewStates.Gone : ViewStates.Visible;
                        // The box card never collapses — outside tag mode the content is always shown
                        if (_singleBoxDataContentLayout != null && !tagMode)
                            _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                        // Nav buttons are always visible, except during a capture — they're
                        // disabled then anyway, and hiding them keeps the steps up the screen.
                        if (_boxNavigationButtonsLayout != null)
                            _boxNavigationButtonsLayout.Visibility =
                                (tagMode && _tagCaptureActive) ? ViewStates.Gone : ViewStates.Visible;
                        if (_discardButton != null)
                            _discardButton.Visibility = !_isBoxLocked && !tagMode ? ViewStates.Visible : ViewStates.Gone;
                        // Watched toggle takes the X's slot while locked
                        if (_watchedToggle != null)
                        {
                            _watchedToggle.Visibility = _isBoxLocked && !tagMode && !_isHistoricalView ? ViewStates.Visible : ViewStates.Gone;
                            UpdateWatchedToggle();
                        }
                    }

                    if (_dataCardLockIconView != null)
                    {
                        // No lock language in tag mode — there's nothing to lock, and the icon
                        // used to imply that tapping it was a harmless "open the box" action.
                        _dataCardLockIconView.Visibility = tagMode ? ViewStates.Gone : ViewStates.Visible;
                        _dataCardLockIconView.SetColorFilter(null);
                        if (GetDisplayBoxData(_currentBoxName) == null && _isBoxLocked)
                        {
                            _dataCardLockIconView.SetImageResource(Resource.Drawable.locked_yellow);
                            _dataCardLockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.WARNING_YELLOW, // yellow
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                        else if (_isBoxLocked)
                        {
                            _dataCardLockIconView.SetImageResource(Resource.Drawable.locked_green);
                            _dataCardLockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.SUCCESS_GREEN,     // green
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                        else
                        {
                            _dataCardLockIconView.SetImageResource(Resource.Drawable.unlocked_red);
                            _dataCardLockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.DANGER_RED,     // red
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                    }

                    if (_dataCardTitleText != null)
                    {
                        _dataCardTitleText.Text = $"Box {_currentBoxName}";
                        // Show in red if viewing historical data
                        if (false /* no historical view */)
                            _dataCardTitleText.SetTextColor(UIFactory.DANGER_RED);
                        else
                            _dataCardTitleText.SetTextColor(Color.Black);
                    }

                    // Update box tag delete button visibility

                    // Show today's data info or pit_id for empty box
                    var currentObs = GetDisplayBoxData(_currentBoxName);
                    if (tagMode)
                    {
                        // Tag and position are spelled out in the panel below — no need to repeat here
                        _boxSavedTimeTextView.Text = "";
                    }
                    else if (currentObs != null)
                    {
                        var nzTime = ToNzTime(currentObs.WhenDataCollectedUtc);
                        var who = !string.IsNullOrEmpty(currentObs.ObserverName) ? currentObs.ObserverName : "";
                        // Three states: a draft (local edits not yet committed for upload) is neither
                        // synced nor queued — never claim "Synced". IsDraft is persisted, so this holds
                        // after an app restart too (not just via the in-memory _dataChangedSinceUnlock).
                        // Locked-and-committed is "⏳ Unsynced"; otherwise it matches the server.
                        bool unsavedDraft = currentObs.IsDraft || (!_isBoxLocked && _dataChangedSinceUnlock);
                        var syncLine = unsavedDraft ? "Not saved" : currentObs.IsPendingUpload ? "⏳ Unsynced" : "Synced";
                        _boxSavedTimeTextView.Text = $"{syncLine}\n{nzTime:HH:mm}" + (!string.IsNullOrEmpty(who) ? $"\n{who}" : "");
                        _boxSavedTimeTextView.SetTextColor(unsavedDraft || currentObs.IsPendingUpload ? UIFactory.DANGER_RED : Color.Black);
                    }
                    else
                    {
                        _boxSavedTimeTextView.Text = "";
                    }
                    _boxSavedTimeTextView.Gravity = GravityFlags.Right;

                    // Duplicate penguin warning for this box
                    if (_dupPenguinWarningView != null)
                    {
                        var boxObs = _colonyState.GetTodayForBox(_currentBoxName);
                        var dupWarnings = new List<string>();
                        if (boxObs != null)
                        {
                            foreach (var scan in boxObs.ScannedIds)
                            {
                                if (scan.BirdId.StartsWith("NOSCAN_")) continue;
                                var key = scan.BirdId.ToUpper();
                                foreach (var otherBox in _boxNamesAndIndexes.Keys)
                                {
                                    if (otherBox == _currentBoxName) continue;
                                    var otherObs = _colonyState.GetTodayForBox(otherBox);
                                    if (otherObs == null) continue;
                                    if (otherObs.ScannedIds.Any(s => s.BirdId.ToUpper() == key))
                                    {
                                        var (dupLabel, _, _, _) = LookupPenguinLabel(scan.BirdId);
                                        dupWarnings.Add($"⚠ {dupLabel} also in Box {otherBox}");
                                    }
                                }
                            }
                        }
                        if (dupWarnings.Count > 0)
                        {
                            _dupPenguinWarningView.Text = string.Join("\n", dupWarnings.Distinct());
                            _dupPenguinWarningView.Visibility = ViewStates.Visible;
                        }
                        else
                        {
                            _dupPenguinWarningView.Visibility = ViewStates.Gone;
                        }
                    }

                    // Update sticky note bar
                    if (_boxNotes.TryGetValue(_currentBoxName, out var boxNote) && !string.IsNullOrWhiteSpace(boxNote.PersistentNotes))
                    {
                        _interestingBoxTextView.Text = boxNote.PersistentNotes;
                    }
                    else
                    {
                        _interestingBoxTextView.Text = "";
                    }
                    if (_stickyNoteBar != null)
                        _stickyNoteBar.Clickable = !_isBoxLocked;

                    var editTexts = new[] { _adultsEditText, _eggsEditText, _chicksEditText, _notesEditText };
                    _suppressDataChanged = true;

                    foreach (var editText in editTexts)
                    {
                        if (editText != null) editText[0].TextChanged -= OnDataChanged;
                    }

                    var displayData = GetDisplayBoxData(_currentBoxName);
                    if (displayData != null)
                    {
                        var boxData = displayData;
                        if (_adultsEditText != null) _adultsEditText[0].Text = boxData.Adults.ToString();
                        if (_eggsEditText != null) _eggsEditText[0].Text = boxData.Eggs.ToString();
                        if (_chicksEditText != null) _chicksEditText[0].Text = boxData.Chicks.ToString();
                        SetSpinnerStatus(_gateStatusSpinner[0], boxData.GateStatus);
                        if (_notesEditText != null) _notesEditText[0].Text = boxData.Notes;
                        buildScannedIdsLayout(boxData.ScannedIds);
                        var breedingVal = !string.IsNullOrWhiteSpace(boxData.BreedingStatus) ? boxData.BreedingStatus
                            : (_boxNotes.TryGetValue(_currentBoxName, out var bn1) && !string.IsNullOrEmpty(bn1.BreedingStatus) ? bn1.BreedingStatus : "");
                        SetSpinnerStatus(_breedingChanceSpinner[0], breedingVal);
                    }
                    else
                    {
                        // Empty box — no observation yet. Show blank counts while locked (a nest with
                        // no data reads as empty, not "0 0 0"); once unlocked for editing, start at "0"
                        // so the steppers have a number to work from. (Redraw on unlock repopulates.)
                        var emptyCount = _isBoxLocked ? "" : "0";
                        if (_adultsEditText != null) _adultsEditText[0].Text = emptyCount;
                        if (_eggsEditText != null) _eggsEditText[0].Text = emptyCount;
                        if (_chicksEditText != null) _chicksEditText[0].Text = emptyCount;
                        SetSpinnerStatus(_gateStatusSpinner[0], null);
                        if (_notesEditText != null) _notesEditText[0].Text = "";
                        buildScannedIdsLayout(new List<ScanRecord>());

                        var prev = _colonyState.PreviousBoxes.ContainsKey(_currentBoxName) ? _colonyState.PreviousBoxes[_currentBoxName] : null;
                        var breedingFallback = prev != null && !string.IsNullOrEmpty(prev.BreedingStatus) ? prev.BreedingStatus
                            : (_boxNotes.TryGetValue(_currentBoxName, out var bn2) && !string.IsNullOrEmpty(bn2.BreedingStatus) ? bn2.BreedingStatus : "");
                        SetSpinnerStatus(_breedingChanceSpinner[0], breedingFallback);
                    }

                    foreach (var editText in editTexts)
                        if (editText != null) editText[0].TextChanged += OnDataChanged;
                    // Clear suppress after spinner events fire (they're queued after this Post)
                    new Handler(Looper.MainLooper).Post(() => _suppressDataChanged = false);

                    // Update previous observation summary
                    UpdatePreviousObsSummary();

                    // Nav buttons are item 0, title layout is item 1 — don't disable either
                    for (int i = 2; i < _singleBoxDataOuterLayout.ChildCount; i++)
                    {
                        var child = _singleBoxDataOuterLayout.GetChildAt(i);
                        // Keep previous obs summary, unsynced cards, and tag mode content always interactive
                        if (child == _prevObsSummaryLayout || child == _unsyncedCardsContainer || child == _tagModeContentLayout) continue;
                        // Disable editing when box is locked
                        bool shouldDisable = _isBoxLocked;
                        SetEnabledRecursive(child, !shouldDisable, shouldDisable ? 0.8f : 1.0f);
                    }
                    // Previous obs summary is always interactive regardless of lock state
                    if (_prevObsSummaryLayout != null)
                    {
                        _prevObsSummaryLayout.Enabled = true;
                        _prevObsSummaryLayout.Clickable = true;
                        _prevObsSummaryLayout.Alpha = 1.0f;
                        if (_prevObsHeaderText != null) { _prevObsHeaderText.Enabled = true; _prevObsHeaderText.Alpha = 1.0f; }
                        if (_prevObsDetailLayout != null) { _prevObsDetailLayout.Enabled = true; _prevObsDetailLayout.Alpha = 1.0f; }
                    }

                    // Enable/Disable navigation and data buttons when available
                    List<Button> buttonsToToggle = new List<Button> { _prevBoxButton, _selectBoxButton, _nextBoxButton };
                    foreach (var button in buttonsToToggle)
                    {
                        bool canGo = true;
                        if(button.Text.Contains("rev box") && _currentBoxIndex == 1 || button.Text.Contains("ext box") && _currentBoxIndex == _boxNamesAndIndexes.Count)
                        {
                            canGo = false;
                        }
                        // Pinned to the box while capturing, so a position can't land on the wrong nest
                        button.Enabled = _isBoxLocked && canGo && !_tagCaptureActive;
                        button.Alpha = button.Enabled ? 1.0f : 0.5f;
                    }


                    createMultiBoxViewCard();
                    createBreedingDatesCard();

                    // Complete a deferred "forgot to lock" navigation once the box is finally locked
                    if (_isBoxLocked && _pendingBoxTagNavigation != null)
                    {
                        var pending = _pendingBoxTagNavigation;
                        _pendingBoxTagNavigation = null;
                        _pendingScanQueueFrozen = false;
                        NavigateToPendingBox(pending!);
                    }

                    // Pause gate: with no box sets string (colony not resolved) data entry is
                    // blocked outright — never show another colony's boxes. Settings stays
                    // reachable for login/sync. Runs last so it wins over the per-mode logic above.
                    bool noColony = string.IsNullOrWhiteSpace(_appSettings.AllBoxSetsString) && !_isHistoricalView;
                    if (_noColonyBanner != null) _noColonyBanner.Visibility = noColony ? ViewStates.Visible : ViewStates.Gone;
                    if (noColony)
                    {
                        if (_singleBoxDataOuterLayout != null) _singleBoxDataOuterLayout.Visibility = ViewStates.Gone;
                        if (_multiBoxViewCard != null) _multiBoxViewCard.Visibility = ViewStates.Gone;
                        if (_breedingDatesCard != null) _breedingDatesCard.Visibility = ViewStates.Gone;
                        if (_tagModeContentLayout != null) _tagModeContentLayout.Visibility = ViewStates.Gone;
                    }
                    else if (_singleBoxDataOuterLayout != null)
                    {
                        _singleBoxDataOuterLayout.Visibility = ViewStates.Visible;
                    }
                });
        }
        private bool dataCardHasZeroData()
        {
            int.TryParse(_adultsEditText?[0].Text ?? "0", out int adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out int eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out int chicks);

            string? gate = GetSelectedStatus(_gateStatusSpinner[0]); // returns null for blank
            bool noGate = string.IsNullOrEmpty(gate);
            bool noNotes = string.IsNullOrWhiteSpace(_notesEditText?[0].Text);

            return adults == 0 && eggs == 0 && chicks == 0  && noGate && noNotes;
        }
        /// <summary>
        /// Per-box scan/count validation. Returns a description of the problem, or null if the box passes.
        /// Fails when adult scans + no-scans != recorded adults, or chick scans > recorded chicks.
        /// </summary>
        private string? GetBoxScanMismatch(BoxObservation box)
        {
            int noScans = 0, adultScans = 0, chickScans = 0;
            foreach (var s in box.ScannedIds)
            {
                if (s.BirdId.StartsWith("NOSCAN_")) { noScans++; continue; }
                var (_, _, isChick, _) = LookupPenguinLabel(s.BirdId);
                if (isChick) chickScans++;
                else adultScans++;
            }
            var problems = new List<string>();
            if (adultScans + noScans != box.Adults)
                problems.Add($"{box.Adults} adult{(box.Adults != 1 ? "s" : "")} but {adultScans} scanned + {noScans} no-scan");
            if (chickScans > box.Chicks)
                problems.Add($"{chickScans} chick scans > {box.Chicks} chick{(box.Chicks != 1 ? "s" : "")}");
            return problems.Count > 0 ? string.Join("; ", problems) : null;
        }

        /// <summary>
        /// Eggs+chicks lower than the last visit — a loss worth a second look, since it's usually
        /// either a real failure or a miscount. Silent once the chip window has closed: chicks
        /// leave the nest from then on, so the count dropping is the expected outcome, not news.
        /// Returns a description of the drop, or null if there's nothing to flag.
        /// </summary>
        private string? GetOffspringDropWarning(string boxName, BoxObservation today)
        {
            if (!_colonyState.PreviousBoxes.TryGetValue(boxName, out var prev)) return null;

            int nowTotal = today.Eggs + today.Chicks;
            int wasTotal = prev.Eggs + prev.Chicks;
            if (nowTotal >= wasTotal) return null;

            // Losses recorded on this visit are the explanation for the drop. Once they account
            // for it there is nothing to chase, and warning anyway would train people to ignore
            // the warning. A partial account still warns, for the part that isn't explained.
            int drop = wasTotal - nowTotal;
            int explained = (today.FailedEggs ?? 0) + (today.DeadChicks ?? 0);
            if (explained >= drop) return null;

            // Past the chip window's close, fledging accounts for it
            if (_remoteBreedingDates != null
                && _remoteBreedingDates.TryGetValue(boxName, out var dates)
                && !string.IsNullOrEmpty(dates.chipWindowFinish)
                && DateTime.TryParse(dates.chipWindowFinish, out var chipClose)
                && NzToday > chipClose.Date)
                return null;

            string Describe(int eggs, int chicks) =>
                eggs == 0 && chicks == 0 ? "empty"
                : string.Join(" + ", new[] {
                    eggs > 0 ? $"{eggs} egg{(eggs != 1 ? "s" : "")}" : null,
                    chicks > 0 ? $"{chicks} chick{(chicks != 1 ? "s" : "")}" : null
                  }.Where(s => s != null));

            var when = ToNzTime(prev.WhenDataCollectedUtc).ToString("d MMM");
            // Name what has been accounted for, so the number left to explain is the number shown
            var accounted = explained > 0 ? $"; {explained} recorded as failed, {drop - explained} unexplained" : "";
            return $"offspring down {wasTotal} → {nowTotal} ({Describe(prev.Eggs, prev.Chicks)} on {when}, now {Describe(today.Eggs, today.Chicks)}{accounted})";
        }

        private (string label, string sex, bool isChick, PenguinData? pd) LookupPenguinLabel(string birdId)
        {
            var cleanId = new string(birdId.Where(char.IsLetterOrDigit).ToArray()).ToUpper();
            var shortId = cleanId.Length >= 8 ? cleanId.Substring(cleanId.Length - 8) : cleanId;
            string label = shortId;
            string sex = "";
            bool isChick = false;

            PenguinData? pd = null;
            if (_remotePenguinData != null)
            {
                if (!_remotePenguinData.TryGetValue(cleanId, out pd))
                    _remotePenguinData.TryGetValue(shortId, out pd);
            }

            if (pd != null)
            {
                sex = pd.Sex?.ToUpper() ?? "";
                isChick = pd.ChipAs != "Adult" && pd.ChipDate > DateTime.MinValue && (DateTime.UtcNow - pd.ChipDate).TotalDays < 90;
                var num = !string.IsNullOrEmpty(pd.PengNum) ? $"#{pd.PengNum}" : "";
                // Sex is shown via the badge colour; chick size code (if any) is the only stage text.
                var size = pd.ChickSizeCode ?? "";
                label = string.Join(" ", new[] { num, size, pd.ScannedId }.Where(s => !string.IsNullOrEmpty(s))).Replace("  ", " ");
            }

            return (label, sex, isChick, pd);
        }

        // Colony acronym for display (PT, NI). Falls back to the colony name's initials
        // until a colonies sync delivers colony_prefix.
        private string CurrentColonyAcronym()
        {
            if (!string.IsNullOrEmpty(_appSettings?.SelectedColonyPrefix)) return _appSettings!.SelectedColonyPrefix;
            var name = _appSettings?.SelectedColonyName ?? "";
            return string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0])));
        }

        // Peng number with its colony acronym: bare "2" → "PT2"; already-prefixed "NI2" stays.
        private string DisplayPengNum(string? pengNum)
        {
            if (string.IsNullOrEmpty(pengNum)) return "";
            return char.IsLetter(pengNum[0]) ? pengNum : CurrentColonyAcronym() + pengNum;
        }

        private TextView CreateScanBadge(string birdId, Action? onClick = null, float textSize = 10, string? labelOverride = null)
        {
            if (birdId.StartsWith("NOSCAN_"))
            {
                var nsb = new TextView(this) { Text = "No scan", TextSize = textSize };
                var nsPadH = (int)(8 * textSize / 10);
                var nsPadV = (int)(3 * textSize / 10);
                nsb.SetPadding(nsPadH, nsPadV, nsPadH, nsPadV);
                // Thin black outline to match the scan mini-views (like wildwatch)
                var nsBg = new Android.Graphics.Drawables.GradientDrawable();
                nsBg.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                nsBg.SetColor(SCAN_CHIPPED_TODAY_BG);
                nsBg.SetCornerRadius(4 * (Resources?.DisplayMetrics?.Density ?? 2f));
                nsBg.SetStroke(System.Math.Max(1, (int)(Resources?.DisplayMetrics?.Density ?? 2f)), Color.Black);
                nsb.Background = nsBg;
                nsb.SetTextColor(Color.Black);
                nsb.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);
                return nsb;
            }
            var (label, sex, isChick, pd) = LookupPenguinLabel(birdId);

            // Drop the chick-size code's trailing C and move it to the end, so the L/B/S lands in the
            // yellow chick strip (which chipped-as-chick badges already draw).
            bool sizeLetterAtEnd = false;
            if (labelOverride == null && pd != null && !string.IsNullOrEmpty(pd.ChickSizeCode))
            {
                var num = !string.IsNullOrEmpty(pd.PengNum) ? $"#{pd.PengNum}" : "";
                var sizeLetter = pd.ChickSizeCode.TrimEnd('C', 'c');
                label = string.Join(" ", new[] { num, pd.ScannedId, sizeLetter }.Where(s => !string.IsNullOrEmpty(s)));
                sizeLetterAtEnd = true;
            }

            var labelText = labelOverride ?? label;
            var badge = new TextView(this) { TextSize = textSize };
            // Monospace renders a space a full cell wide; scale each inter-token space to 0.5× so
            // the badge sits tighter without touching the glyphs themselves.
            if (labelText.IndexOf(' ') >= 0)
            {
                var spannable = new Android.Text.SpannableString(labelText);
                for (int i = 0; i < labelText.Length; i++)
                    if (labelText[i] == ' ')
                        spannable.SetSpan(new Android.Text.Style.ScaleXSpan(0.5f), i, i + 1, Android.Text.SpanTypes.ExclusiveExclusive);
                badge.TextFormatted = spannable;
            }
            else badge.Text = labelText;
            var padH = (int)(8 * textSize / 10);
            var padV = (int)(3 * textSize / 10);
            bool chippedAsChick = pd != null && pd.ChipAs != "Adult";
            // Size letter at the end sits in its own yellow tab hugging the letter (padH each side),
            // so right padding matches the left. Otherwise reserve the decorative right-15% strip.
            float stripWidthPx = 0f;
            if (sizeLetterAtEnd)
            {
                float letterW = badge.Paint.MeasureText(labelText.Substring(labelText.Length - 1));
                stripWidthPx = letterW + 2 * padH;
            }
            int padRight = (chippedAsChick && !sizeLetterAtEnd) ? padH * 3 : padH;
            badge.SetPadding(padH, padV, padRight, padV);

            Color bg = sex == "M" ? SCAN_MALE_BG : sex == "F" ? SCAN_FEMALE_BG : SCAN_UNKNOWN_BG;
            float radiusPx = 4 * (Resources?.DisplayMetrics?.Density ?? 2f);
            badge.Background = new BadgeBackground(bg, chippedAsChick, SCAN_CHICK_BG, radiusPx, stripWidthPx);
            badge.SetTextColor(sex == "M" ? SCAN_MALE_TEXT : sex == "F" ? SCAN_FEMALE_TEXT : Color.DarkGray);
            badge.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);

            if (onClick != null)
            {
                badge.Clickable = true;
                badge.Click += (s, e) => onClick();
            }

            return badge;
        }

        // Badge background: rounded base colour with an optional yellow strip on the right 15% (chipped-as-chick).
        private class BadgeBackground : Android.Graphics.Drawables.Drawable
        {
            private readonly Color _baseColor;
            private readonly Color _stripColor;
            private readonly bool _chick;
            private readonly float _radius;
            private readonly float _stripWidthPx; // 0 => fall back to the right 15%
            private readonly Paint _paint = new Paint(PaintFlags.AntiAlias);

            public BadgeBackground(Color baseColor, bool chick, Color stripColor, float radius, float stripWidthPx = 0f)
            {
                _baseColor = baseColor; _chick = chick; _stripColor = stripColor; _radius = radius; _stripWidthPx = stripWidthPx;
            }

            public override void Draw(Canvas canvas)
            {
                var b = Bounds;
                // Inset by half the stroke so the outline sits fully inside the bounds, not clipped.
                float stroke = System.Math.Max(1f, _radius / 4f); // ~1dp
                float inset = stroke / 2f;
                var rect = new RectF(b.Left + inset, b.Top + inset, b.Right - inset, b.Bottom - inset);
                _paint.SetStyle(Paint.Style.Fill);
                _paint.Color = _baseColor;
                canvas.DrawRoundRect(rect, _radius, _radius, _paint);
                if (_chick && b.Width() > 0)
                {
                    canvas.Save();
                    var clip = new Android.Graphics.Path();
                    clip.AddRoundRect(rect, _radius, _radius, Android.Graphics.Path.Direction.Cw);
                    canvas.ClipPath(clip);
                    _paint.Color = _stripColor;
                    float stripLeft = _stripWidthPx > 0f ? (b.Right - _stripWidthPx) : (b.Left + b.Width() * 0.85f);
                    canvas.DrawRect(stripLeft, b.Top, b.Right, b.Bottom, _paint);
                    canvas.Restore();
                }
                // Thin black outline, like wildwatch
                _paint.SetStyle(Paint.Style.Stroke);
                _paint.StrokeWidth = stroke;
                _paint.Color = Color.Black;
                canvas.DrawRoundRect(rect, _radius, _radius, _paint);
            }

            public override void SetAlpha(int alpha) { }
            public override void SetColorFilter(ColorFilter? colorFilter) { }
            public override int Opacity => (int)Android.Graphics.Format.Translucent;
        }

        private static readonly Color SCAN_MALE_BG = Color.ParseColor("#E6F3FF");
        private static readonly Color SCAN_FEMALE_BG = Color.ParseColor("#FFE4E1");
        private static readonly Color SCAN_UNKNOWN_BG = Color.ParseColor("#F0F0F0");
        private static readonly Color SCAN_CHICK_BG = Color.ParseColor("#FFEB3B");
        private static readonly Color SCAN_CHIPPED_TODAY_BG = Color.ParseColor("#C8E6C9");
        private static readonly Color BOX_MISMATCH_BG = Color.ParseColor("#FFCDD2");
        private static readonly Color SCAN_MALE_TEXT = Color.ParseColor("#1565C0");
        private static readonly Color SCAN_FEMALE_TEXT = Color.ParseColor("#C62828");

        /// <summary>An egg or chick with a red ✕ struck over it. Drawn rather than written so the
        /// cross sits ON the icon, the way wildwatch marks a failed egg or a dead chick.</summary>
        private class CrossedIconSpan : Android.Text.Style.ReplacementSpan
        {
            private readonly string _icon;
            // Kept on the span rather than made per draw: this runs for every icon on every
            // frame the list scrolls.
            private readonly Paint _cross = new Paint { AntiAlias = true, Color = UIFactory.DANGER_RED, TextAlign = Paint.Align.Center };
            public CrossedIconSpan(string icon) { _icon = icon; _cross.SetTypeface(Typeface.DefaultBold); }

            public override int GetSize(Paint paint, Java.Lang.ICharSequence? text, int start, int end, Paint.FontMetricsInt? fm)
                => (int)Math.Ceiling(paint.MeasureText(_icon));

            public override void Draw(Canvas canvas, Java.Lang.ICharSequence? text, int start, int end,
                                      float x, int top, int y, int bottom, Paint paint)
            {
                canvas.DrawText(_icon, x, y, paint);
                _cross.TextSize = paint.TextSize * 0.85f;
                // Centred ON the glyph, not sitting on its baseline: the emoji's body is the
                // space between ascent and descent, and the cross is centred in that box.
                float middle = y + (paint.Ascent() + paint.Descent()) / 2f;
                float baseline = middle - (_cross.Ascent() + _cross.Descent()) / 2f;
                canvas.DrawText("✕", x + paint.MeasureText(_icon) / 2f, baseline, _cross);
            }
        }

        /// <summary>The loss markers inside a count string — "🥚✗" and "🐣✗" — swapped for the
        /// crossed icon. Everything else is left as written, so any of the count strings can be
        /// passed through this on its way to a TextView.</summary>
        private static Java.Lang.ICharSequence CrossedLosses(string? text)
        {
            if (string.IsNullOrEmpty(text)) return new Java.Lang.String(text ?? "");
            var span = new SpannableString(text);
            foreach (var token in new[] { "🥚✗", "🐣✗" })
            {
                for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                         i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                    span.SetSpan(new CrossedIconSpan(token.Substring(0, token.Length - 1)),
                                 i, i + token.Length, SpanTypes.ExclusiveExclusive);
            }
            return span;
        }

        /// <summary>
        /// Build the observation detail as a View (not just text) so scans can be styled badges.
        /// </summary>
        private View BuildObsDetailView(BoxObservation obs, bool showBoxLink = true, bool showDate = false)
        {
            var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            // Status row: breeding% | icons | gate — evenly spaced; date on the right
            var statusItems = new List<(string text, int size)>();
            if (!string.IsNullOrEmpty(obs.BreedingStatus)) statusItems.Add((obs.BreedingStatus, 13));
            var icons = string.Concat(Enumerable.Repeat("🐧", obs.Adults)) +
                string.Concat(Enumerable.Repeat("🥚", obs.Eggs)) +
                string.Concat(Enumerable.Repeat("🐣", obs.Chicks));
            if (!string.IsNullOrEmpty(icons)) statusItems.Add((icons, 14));
            // Losses recorded on that visit — the reason the counts dropped, shown alongside them
            // so a previous card explains itself instead of just looking like birds went missing.
            var losses = string.Concat(Enumerable.Repeat("🥚✗", obs.FailedEggs ?? 0)) +
                string.Concat(Enumerable.Repeat("🐣✗", obs.DeadChicks ?? 0));
            if (!string.IsNullOrEmpty(losses)) statusItems.Add((losses, 14));
            if (!string.IsNullOrEmpty(obs.GateStatus)) statusItems.Add((obs.GateStatus, 13));
            // Date joins the row as a regular item so spacing stays even between/around all items
            if (showDate) statusItems.Add((ToNzTime(obs.WhenDataCollectedUtc).ToString("d MMM"), 13));

            if (statusItems.Count > 0)
            {
                var statusRow = new LinearLayout(this);
                statusRow.SetGravity(GravityFlags.CenterVertical);
                statusRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                var spacerParams = new LinearLayout.LayoutParams(0, 1, 1f);

                foreach (var (text, size) in statusItems)
                {
                    statusRow.AddView(new View(this) { LayoutParameters = spacerParams });
                    var tv = new TextView(this) { TextFormatted = CrossedLosses(text), TextSize = size };
                    tv.SetTextColor(Color.Black);
                    tv.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                    tv.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    statusRow.AddView(tv);
                }
                statusRow.AddView(new View(this) { LayoutParameters = spacerParams });

                layout.AddView(statusRow);
            }

            // Notes (if any)
            if (!string.IsNullOrEmpty(obs.Notes))
            {
                // Same size as the breeding-prediction line (13), but not bold.
                var notesText = new TextView(this) { Text = obs.Notes, TextSize = 13 };
                notesText.SetTextColor(Color.Black);
                notesText.SetPadding(0, 4, 0, 0);
                layout.AddView(notesText);
            }

            // Badge row: box link + scan mini-views in one FlowLayout. Each mini-view stays whole
            // (the FlowLayout never squeezes a badge into wrapping its own text); if the lot fits on
            // one row they share it with "Box X", otherwise "Box X" keeps its own row and the
            // mini-views flow from the row below (BreakAfterFirstWhenWrapping).
            var density = Resources?.DisplayMetrics?.Density ?? 2f;
            bool hasBoxLink = showBoxLink && !string.IsNullOrEmpty(obs.BoxName);
            var scanFlow = new PenguinMonitor.UI.FlowLayout(this)
            {
                HorizontalSpacing = (int)(6 * density),
                VerticalSpacing = (int)(4 * density),
                BreakAfterFirstWhenWrapping = hasBoxLink,
            };
            var flowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            flowParams.SetMargins(0, 6, 0, 0);
            scanFlow.LayoutParameters = flowParams;

            // Box link badge — first child, so it leads the row (or its own row when wrapped)
            if (hasBoxLink)
            {
                var boxName = obs.BoxName;
                var boxBadge = new TextView(this) { Text = $"Box {boxName}", TextSize = 14 };
                boxBadge.SetPadding(8, 3, 8, 3);
                boxBadge.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 4);
                boxBadge.SetTextColor(Color.White);
                boxBadge.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);
                boxBadge.Clickable = true;
                boxBadge.Click += (s, e) => ShowBoxPanel(boxName);
                scanFlow.AddView(boxBadge);
            }

            foreach (var s in obs.ScannedIds)
            {
                var badgeBirdId = s.BirdId;
                var badge = CreateScanBadge(badgeBirdId, () => ShowBirdPanel(badgeBirdId), textSize: 14);
                scanFlow.AddView(badge);
            }

            layout.AddView(scanFlow);

            return layout;
        }

        /// <summary>
        /// Build a header line for an observation card.
        /// </summary>
        private string BuildObsHeaderText(BoxObservation obs, string label, bool expanded)
        {
            var nzDate = ToNzTime(obs.WhenDataCollectedUtc);
            int daysAgo = (int)(NzToday - nzDate.Date).TotalDays;
            string dateStr = daysAgo == 0 ? "today" : daysAgo == 1 ? "yesterday" : $"{daysAgo} days ago";
            string byWhom = !string.IsNullOrEmpty(obs.ObserverName) ? $" by {obs.ObserverName}" : "";
            string arrow = expanded ? "▾" : "▸";
            string boxStr = !string.IsNullOrEmpty(obs.BoxName) ? $"Box {obs.BoxName} " : "";
            // Local (unsynced) data also carries a "saved" time. Show it when it differs from
            // the observation time — this is what distinguishes a local edit from server data.
            string savedStr = "";
            if (obs.PendingUploadSinceUtc.HasValue)
            {
                var savedNz = ToNzTime(obs.PendingUploadSinceUtc.Value);
                if (Math.Abs((savedNz - nzDate).TotalMinutes) >= 1)
                    savedStr = $", saved {savedNz:d MMM HH:mm}";
            }
            string obsPrefix = string.IsNullOrEmpty(savedStr) ? "" : "obs ";
            if (expanded)
                return $"{arrow} {boxStr}{label}: {obsPrefix}{nzDate:d MMM HH:mm}{savedStr}{byWhom}";
            string status = !string.IsNullOrEmpty(obs.BreedingStatus) ? $" {obs.BreedingStatus}" : "";
            return $"{arrow} {boxStr}{label}: {obsPrefix}{nzDate:d MMM HH:mm}{savedStr}{byWhom} — " +
                $"{string.Concat(Enumerable.Repeat("🐧", obs.Adults))}" +
                $"{string.Concat(Enumerable.Repeat("🥚", obs.Eggs))}" +
                $"{string.Concat(Enumerable.Repeat("🐣", obs.Chicks))}" +
                // Collapsed, the losses are the only sign a visit that recorded one did anything
                $"{string.Concat(Enumerable.Repeat("🥚✗", obs.FailedEggs ?? 0))}" +
                $"{string.Concat(Enumerable.Repeat("🐣✗", obs.DeadChicks ?? 0))}" +
                status;
        }

        private void UpdatePreviousObsSummary()
        {
            if (_prevObsHeaderText == null || _prevObsDetailLayout == null || _prevObsSummaryLayout == null) return;

            // Tag mode shows last-known contents in its own card — this would duplicate it.
            if (_appSettings.EditBoxTagsMode && !_isHistoricalView)
            {
                _prevObsHeaderText.Visibility = ViewStates.Gone;
                _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                _prevObsDetailLayout.Visibility = ViewStates.Gone;
                if (_stickyNoteBar != null) _stickyNoteBar.Visibility = ViewStates.Gone;
                if (_stickyNoteBelowPrev != null) _stickyNoteBelowPrev.Visibility = ViewStates.Gone;
                UpdateUnsyncedCards();
                return;
            }

            var prev = _colonyState.PreviousBoxes.ContainsKey(_currentBoxName) ? _colonyState.PreviousBoxes[_currentBoxName] : null;
            if (prev == null)
            {
                _prevObsHeaderText.Visibility = ViewStates.Gone;
                _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                if (_stickyNoteBelowPrev != null) _stickyNoteBelowPrev.Visibility = ViewStates.Gone;
                UpdateUnsyncedCards();
                return;
            }

            // Compact header: date: icons, show breeding status if no eggs/chicks
            var nzDate = ToNzTime(prev.WhenDataCollectedUtc);
            string compact = $"{nzDate:d MMM}: " +
                string.Concat(Enumerable.Repeat("🐧", prev.Adults)) +
                string.Concat(Enumerable.Repeat("🥚", prev.Eggs)) +
                string.Concat(Enumerable.Repeat("🐣", prev.Chicks)) +
                // Losses too, so the collapsed badge doesn't read as an unexplained empty nest
                string.Concat(Enumerable.Repeat("🥚✗", prev.FailedEggs ?? 0)) +
                string.Concat(Enumerable.Repeat("🐣✗", prev.DeadChicks ?? 0));
            if (prev.Eggs == 0 && prev.Chicks == 0 && !string.IsNullOrEmpty(prev.BreedingStatus))
                compact += $" {prev.BreedingStatus}";

            bool expanded = _prevObsDetailLayout.Visibility == ViewStates.Visible;

            // Collapse the expanded detail when the box has changed — the expansion belonged
            // to the previous box, not this one.
            if (expanded && _prevObsExpandedForBox != _currentBoxName)
            {
                _prevObsDetailLayout.Visibility = ViewStates.Gone;
                _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                expanded = false;
            }

            // Hide compact badge when expanded; the sticky bar moves into the expanded card
            _prevObsHeaderText.TextFormatted = CrossedLosses(compact);
            _prevObsHeaderText.Visibility = expanded ? ViewStates.Gone : ViewStates.Visible;
            if (_stickyNoteBar != null)
                _stickyNoteBar.Visibility = expanded ? ViewStates.Gone : ViewStates.Visible;

            if (expanded)
            {
                _prevObsSummaryLayout.Visibility = ViewStates.Visible;
                _prevObsDetailLayout.RemoveAllViews();
                _prevObsDetailLayout.AddView(BuildObsDetailView(prev, showDate: true));

                // 3rd row: breeding string sourced from wildwatch (the website owns the
                // estimator, so fixing it there fixes it here too). Only shows for boxes
                // wildwatch reports as currently breeding.
                if (_remoteBreedingDates != null
                    && _remoteBreedingDates.TryGetValue(_currentBoxName, out var breedingDates))
                {
                    var breedingStr = breedingDates.breedingDateStatus();
                    if (!string.IsNullOrEmpty(breedingStr))
                    {
                        var breedingText = new TextView(this) { Text = breedingStr, TextSize = 13 };
                        breedingText.SetTextColor(Color.Black);
                        breedingText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                        breedingText.SetPadding(0, 6, 0, 0);
                        _prevObsDetailLayout.AddView(breedingText);
                    }
                }

            }

            // Sticky note sits BELOW the expanded card, not inside it (the top-row sticky
            // bar is hidden while expanded).
            if (_stickyNoteBelowPrev != null)
            {
                bool hasSticky = _boxNotes.TryGetValue(_currentBoxName, out var sticky) && !string.IsNullOrWhiteSpace(sticky.PersistentNotes);
                _stickyNoteBelowPrev.Text = hasSticky ? $"💡 {sticky!.PersistentNotes} 💡" : "";
                _stickyNoteBelowPrev.Visibility = expanded && hasSticky ? ViewStates.Visible : ViewStates.Gone;
            }

            UpdateUnsyncedCards();
        }

        private LinearLayout? _unsyncedCardsContainer;

        private void UpdateUnsyncedCards()
        {
            if (_unsyncedCardsContainer == null) return;
            _unsyncedCardsContainer.RemoveAllViews();

            var nzToday = NzToday;
            var olderPending = _colonyState.PendingObservations
                .Where(o => o.BoxName == _currentBoxName && ToNzTime(o.WhenDataCollectedUtc).Date < nzToday)
                .OrderByDescending(o => o.WhenDataCollectedUtc)
                .ToList();

            foreach (var obs in olderPending)
            {
                var card = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
                card.SetPadding(12, 8, 12, 8);
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 6, borderColour: UIFactory.DANGER_RED);
                var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                cardParams.SetMargins(0, 0, 0, 8);
                card.LayoutParameters = cardParams;

                var headerText = new TextView(this) { TextSize = 13 };
                headerText.SetTextColor(UIFactory.DANGER_RED);
                headerText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                headerText.TextFormatted = CrossedLosses(BuildObsHeaderText(obs, "Unsynced", false));
                card.AddView(headerText);

                // Resolve actions — the server only auto-flags a conflict for TODAY's data,
                // so stale pending like this never surfaces the normal conflict dialog. Offer
                // the same "replace on server" (force-upload) resolution here, plus discard.
                var stuckObs = obs;
                var stuckBox = obs.BoxName ?? "";
                var actionRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
                actionRow.SetPadding(0, 8, 0, 4);

                var replaceBtn = _uiFactory.CreateStyledButton("Replace on server", UIFactory.PRIMARY_BLUE);
                replaceBtn.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                replaceBtn.Click += (s, e) =>
                {
                    _ = Task.Run(async () =>
                    {
                        var (n, error) = await _dataStorageService.UploadConfirmedEdits(_colonyState, _appSettings,
                            new List<(string, string?)> { (stuckBox, ToNzTime(stuckObs.WhenDataCollectedUtc).ToString("yyyy-MM-dd")) });
                        new Handler(Looper.MainLooper).Post(() =>
                        {
                            DataStorageService.SaveColonyState(this, _colonyState);
                            UpdateSyncButtonLabel();
                            DrawPageLayouts();
                            Toast.MakeText(this, n > 0 ? $"Box {stuckBox} synced" : $"Box {stuckBox} not synced: {error ?? "try again"}", ToastLength.Long)?.Show();
                        });
                    });
                };
                actionRow.AddView(replaceBtn);

                var discardBtn = _uiFactory.CreateStyledButton("Discard", UIFactory.DANGER_RED);
                var discardParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                discardParams.SetMargins(8, 0, 0, 0);
                discardBtn.LayoutParameters = discardParams;
                discardBtn.Click += (s, e) =>
                {
                    new AlertDialog.Builder(this)
                        .SetTitle("Discard unsynced?")
                        .SetMessage($"Discard this unsynced observation for Box {stuckBox}? This can't be undone.")
                        .SetPositiveButton("Discard", (s2, e2) =>
                        {
                            _colonyState.PendingObservations.Remove(stuckObs);
                            DataStorageService.SaveColonyState(this, _colonyState);
                            UpdateSyncButtonLabel();
                            DrawPageLayouts();
                        })
                        .SetNegativeButton("Cancel", (s2, e2) => { })
                        .Show();
                };
                actionRow.AddView(discardBtn);
                card.AddView(actionRow);

                var detailLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
                detailLayout.Visibility = ViewStates.Gone;
                card.AddView(detailLayout);

                card.Click += (s, e) =>
                {
                    bool wasVisible = detailLayout.Visibility == ViewStates.Visible;
                    detailLayout.Visibility = wasVisible ? ViewStates.Gone : ViewStates.Visible;
                    headerText.TextFormatted = CrossedLosses(BuildObsHeaderText(obs, "Unsynced", !wasVisible));
                    if (!wasVisible)
                    {
                        detailLayout.RemoveAllViews();
                        detailLayout.AddView(BuildObsDetailView(obs));
                    }
                };

                _unsyncedCardsContainer.AddView(card);
            }
        }

        private void CreateBoxDataCard()
        {
            _singleBoxDataOuterLayout = _uiFactory.CreateCard();
            _singleBoxDataOuterLayout.Visibility = ViewStates.Visible;

            // Horizontal layout for lock icon + box title
            _singleBoxDataTitleLayout = new LinearLayout(this)
            {
                Clickable = true,
                Focusable = true
            };
            _singleBoxDataTitleLayout.SetGravity(GravityFlags.Center);
            // Webview button — replaces the old expand/collapse toggle (the box card never
            // collapses now). Opens the wildwatch box panel for the current nest.
            _webviewButton = new TextView(this) { Text = "🌐", TextSize = 22, Gravity = GravityFlags.Center };
            var wvBtnSize = (int)(48 * (Resources?.DisplayMetrics?.Density ?? 2));
            _webviewButton.LayoutParameters = new LinearLayout.LayoutParams(wvBtnSize, wvBtnSize);
            _webviewButton.Clickable = true;
            _webviewButton.Click += (s, e) => ShowBoxPanel(_currentBoxName);
            _singleBoxDataTitleLayout.AddView(_webviewButton);
            _singleBoxDataTitleLayout.Click += (sender, e) =>
            {
                // Box tags mode has no lock: tapping the header used to arm a GPS capture that
                // then overwrote the nest position on the next tap. Editing is explicit now.
                if (_appSettings.EditBoxTagsMode && !_isHistoricalView) return;

                _isBoxLocked = !_isBoxLocked;
                if (!_isBoxLocked)
                {
                    // Unlock — enter edit mode
                    _dataChangedSinceUnlock = false;
                    _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                    DrawPageLayouts();
                }
                else
                {
                    // In historical view, just lock without saving box data
                    if (_isHistoricalView)
                    {
                        _dataChangedSinceUnlock = false;
                        DrawPageLayouts();
                        return;
                    }

                    // No in-session changes and the box already has today data.
                    var todayObs = _colonyState.GetTodayForBox(_currentBoxName);
                    if (!_dataChangedSinceUnlock && todayObs != null)
                    {
                        if (todayObs.IsDraft)
                        {
                            // A draft (typically restored from a previous session) was never committed
                            // for upload. Locking finalises it: commit directly so it actually syncs —
                            // no server-compare dialog, since the stored copy IS the draft, not server data.
                            CommitDraftForUpload();
                            _dataChangedSinceUnlock = false;
                            DrawPageLayouts();
                            TryBackgroundUpload();
                            return;
                        }
                        // Genuinely committed/synced with no changes — lock silently.
                        _dataChangedSinceUnlock = false;
                        DrawPageLayouts();
                        return;
                    }

                    // The box is finished, so its counts have settled — sanity-check them once here
                    // rather than while the user is still editing. Cancel drops back into edit mode.
                    var closeWarnings = GetBoxCloseWarnings();
                    if (closeWarnings.Count > 0)
                    {
                        ShowBoxCloseWarnings(closeWarnings, FinishLock, () =>
                        {
                            // Draft-save first: the redraw repopulates the fields from stored data,
                            // so without this the counts being queried would be thrown away.
                            // _dataChangedSinceUnlock stays true so the next lock still commits.
                            SaveCurrentBoxData();
                            _isBoxLocked = false;
                            _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                            DrawPageLayouts();
                        });
                        return;
                    }
                    FinishLock();
                }

                void FinishLock()
                {
                    // All zeros and no prior today data — confirm empty
                    if (_colonyState.GetTodayForBox(_currentBoxName) == null && dataCardHasZeroData())
                    {
                        ShowEmptyBoxDialog(() =>
                        {
                            // The one place an empty observation is created on purpose.
                            SaveCurrentBoxData(allowEmpty: true);
                            CommitDraftForUpload();
                            _dataChangedSinceUnlock = false;
                            DrawPageLayouts();
                            TryBackgroundUpload();
                        }, () =>
                        {
                            // "Don't save" means don't save: drop any draft for today so the next
                            // lock has nothing to find and can't quietly commit it.
                            DiscardTodayDraftForCurrentBox();
                            _dataChangedSinceUnlock = false;
                            DrawPageLayouts();
                        });
                    }
                    else
                    {
                        // If box has existing server data, show confirm edit dialog
                        var serverVersion = _colonyState.TodayBoxes.ContainsKey(_currentBoxName)
                            ? _colonyState.TodayBoxes[_currentBoxName] : null;
                        if (serverVersion != null && !serverVersion.IsPendingUpload)
                        {
                            ShowLocalConfirmEditDialog(_currentBoxName, serverVersion, () =>
                            {
                                if (serverVersion.ObservationId.HasValue)
                                    _confirmedAgainstServerObsId[_currentBoxName] = serverVersion.ObservationId.Value;
                                SaveCurrentBoxData();
                                CommitDraftForUpload();
                                // Stamp the confirmed obs ID onto the pending observation
                                var pending = _colonyState.PendingObservations.FirstOrDefault(p => p.BoxName == _currentBoxName && p.IsPendingUpload);
                                if (pending != null && serverVersion.ObservationId.HasValue)
                                    pending.ConfirmedAgainstObsId = serverVersion.ObservationId.Value;
                                _dataChangedSinceUnlock = false;
                                DrawPageLayouts();
                                TryBackgroundUpload();
                            }, () =>
                            {
                                // Restore server version — discard this box's local draft/pending edit for today
                                _colonyState.PendingObservations.RemoveAll(p => p.BoxName == _currentBoxName && ToNzTime(p.WhenDataCollectedUtc).Date == NzToday);
                                _dataChangedSinceUnlock = false;
                                DrawPageLayouts();
                            });
                        }
                        else
                        {
                            SaveCurrentBoxData();
                            CommitDraftForUpload();
                            _dataChangedSinceUnlock = false;
                            DrawPageLayouts();
                            TryBackgroundUpload();
                        }
                    }
                }
            };

            // Add a spacer that expands to fill available space
            var spacer = new View(this);
            spacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            _singleBoxDataTitleLayout.AddView(spacer);

            // Box title text
            _dataCardTitleText = new TextView(this)
            {
                Text = $"Box {_currentBoxIndex}  ",
                TextSize = 30,
                Gravity = GravityFlags.Center
            };
            _dataCardTitleText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _dataCardTitleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _dataCardTitleText.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            _singleBoxDataTitleLayout.AddView(_dataCardTitleText);

            var boxTitleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            boxTitleParams.SetMargins(0, 0, 0, 16);
            _singleBoxDataTitleLayout.LayoutParameters = boxTitleParams;

            // Lock icon
            _dataCardLockIconView = new ImageView(this);
            _dataCardLockIconView.SetImageResource(Android.Resource.Drawable.IcLockLock);
            var iconParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            iconParams.SetMargins(0, 0, 12, 0); // Space between icon and text
            _dataCardLockIconView.LayoutParameters = iconParams;
            _singleBoxDataTitleLayout.AddView(_dataCardLockIconView);

            // Spacer between title and saved time
            var titleSpacer = new View(this);
            titleSpacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            _singleBoxDataTitleLayout.AddView(titleSpacer);

            _boxSavedTimeTextView = new TextView(this);
            _singleBoxDataTitleLayout.AddView(_boxSavedTimeTextView);

            // Discard button — visible only when unlocked. Full header height and square
            // so it's an easy tap target in the field.
            var hdrBtnSize = (int)(48 * (Resources?.DisplayMetrics?.Density ?? 2));
            _discardButton = new TextView(this) { Text = "✕", TextSize = 20, Gravity = GravityFlags.Center };
            _discardButton.SetTextColor(Color.White);
            _discardButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _discardButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.DANGER_RED, 6);
            var discardParams = new LinearLayout.LayoutParams(hdrBtnSize, hdrBtnSize);
            discardParams.SetMargins(12, 0, 4, 0);
            discardParams.Gravity = GravityFlags.CenterVertical;
            _discardButton.LayoutParameters = discardParams;
            _discardButton.Visibility = ViewStates.Gone;
            _discardButton.Clickable = true;
            _discardButton.Click += (s, e) =>
            {
                // There's something to discard if the box has an in-session edit OR a stored local
                // observation for today (a draft — including one restored from a previous session,
                // when _dataChangedSinceUnlock has reset to false). Without the latter, a restored
                // draft could never be cleared back to a no-data state.
                bool hasLocal = _colonyState.PendingObservations.Any(p => p.BoxName == _currentBoxName && ToNzTime(p.WhenDataCollectedUtc).Date == NzToday);
                if (_dataChangedSinceUnlock || hasLocal)
                {
                    bool hasServerData = _colonyState.TodayBoxes.ContainsKey(_currentBoxName);
                    new AlertDialog.Builder(this)
                        .SetMessage(hasServerData ? "Discard changes?" : "Discard data?")
                        .SetPositiveButton(hasServerData ? "Discard changes" : "Discard data", (s2, e2) =>
                        {
                            _colonyState.PendingObservations.RemoveAll(p => p.BoxName == _currentBoxName && ToNzTime(p.WhenDataCollectedUtc).Date == NzToday);
                            SaveToAppDataDir();   // persist the removal so the draft doesn't return on restart
                            _isBoxLocked = true;
                            _dataChangedSinceUnlock = false;
                            DrawPageLayouts();
                        })
                        .SetNegativeButton("Cancel", (s2, e2) => { })
                        .Show();
                }
                else
                {
                    _isBoxLocked = true;
                    _dataChangedSinceUnlock = false;
                    DrawPageLayouts();
                }
            };
            _singleBoxDataTitleLayout.AddView(_discardButton);

            // Watched toggle — occupies the X's slot while the box is LOCKED. One tap flips
            // the website's observation_locations.watched flag (green = watched).
            _watchedToggle = new TextView(this) { Text = "✓", TextSize = 20, Gravity = GravityFlags.Center };
            _watchedToggle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var watchedParams = new LinearLayout.LayoutParams(hdrBtnSize, hdrBtnSize);
            watchedParams.SetMargins(12, 0, 4, 0);
            watchedParams.Gravity = GravityFlags.CenterVertical;
            _watchedToggle.LayoutParameters = watchedParams;
            _watchedToggle.Visibility = ViewStates.Gone;
            _watchedToggle.Clickable = true;
            _watchedToggle.Click += (s, e) =>
            {
                if (!_isBoxLocked) return;
                if (!_boxNotes.TryGetValue(_currentBoxName, out var note) || note.LocationId <= 0)
                {
                    Toast.MakeText(this, "Box not on the server yet — sync first", ToastLength.Short)?.Show();
                    return;
                }
                // Watched lives locally and syncs: flip now, queue the upload. No revert —
                // an offline toggle simply rides along with the next sync.
                note.Watched = !note.Watched;
                note.WatchedPendingUpload = true;
                UpdateWatchedToggle();
                _dataStorageService.SaveBoxNotesToDisk(this, _boxNotes);
                DrawPageLayouts(); // overview's Watched filter reflects the change immediately
                Toast.MakeText(this, note.Watched ? $"Box {_currentBoxName} watched ✓" : $"Box {_currentBoxName} no longer watched", ToastLength.Short)?.Show();
                // Opportunistic immediate push; failure just stays queued
                _ = Task.Run(() => _dataStorageService.UploadPendingWatchedFlags(this, _appSettings, _boxNotes));
            };
            _singleBoxDataTitleLayout.AddView(_watchedToggle);

            // Navigation buttons above the box header
            _boxNavigationButtonsLayout = CreateNavigationLayout();
            _singleBoxDataOuterLayout.AddView(_boxNavigationButtonsLayout);

            _singleBoxDataOuterLayout.AddView(_singleBoxDataTitleLayout);

            _singleBoxDataContentLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            // Combined row: [prev obs compact] [sticky notes centered]
            var prevAndStickyRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            prevAndStickyRow.SetGravity(GravityFlags.CenterVertical);
            prevAndStickyRow.SetPadding(4, 4, 4, 4);
            var prevAndStickyParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            prevAndStickyParams.SetMargins(0, 0, 0, 4);
            prevAndStickyRow.LayoutParameters = prevAndStickyParams;

            // Prev obs compact summary (left-aligned)
            _prevObsHeaderText = new TextView(this) { TextSize = 14 };
            _prevObsHeaderText.SetTextColor(Color.Black);
            _prevObsHeaderText.SetPadding(8, 4, 8, 4);
            _prevObsHeaderText.Background = _uiFactory.CreateRoundedBackground(Color.ParseColor("#FFF3E0"), 6); // light orange
            var prevCompactParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            _prevObsHeaderText.LayoutParameters = prevCompactParams;
            _prevObsHeaderText.Click += (s, e) =>
            {
                // Expand: show full-width detail below
                _prevObsExpandedForBox = _currentBoxName;
                _prevObsDetailLayout.Visibility = ViewStates.Visible;
                _prevObsSummaryLayout.Visibility = ViewStates.Visible;
                UpdatePreviousObsSummary();
            };
            prevAndStickyRow.AddView(_prevObsHeaderText);

            // Sticky note bar (fills remaining space, centered)
            _stickyNoteBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            _stickyNoteBar.SetGravity(GravityFlags.Center);
            _stickyNoteBar.SetPadding(8, 4, 8, 4);
            var stickyBarParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            _stickyNoteBar.LayoutParameters = stickyBarParams;

            var bulb1 = new TextView(this) { Text = "💡", TextSize = 14 };
            _interestingBoxTextView = new TextView(this) { TextSize = 12 };
            _interestingBoxTextView.SetTextColor(UIFactory.PRIMARY_BLUE);
            _interestingBoxTextView.SetPadding(2, 0, 2, 0);
            var bulb2 = new TextView(this) { Text = "💡", TextSize = 14 };

            _stickyNoteBar.AddView(bulb1);
            _stickyNoteBar.AddView(_interestingBoxTextView);
            _stickyNoteBar.AddView(bulb2);
            _stickyNoteBar.Click += (s, e) => { if (!_isBoxLocked) ShowBoxNotesDialog(); };
            prevAndStickyRow.AddView(_stickyNoteBar);

            // Duplicate penguin warning
            _dupPenguinWarningView = new TextView(this) { TextSize = 13 };
            _dupPenguinWarningView.SetTextColor(UIFactory.DANGER_RED);
            _dupPenguinWarningView.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _dupPenguinWarningView.SetPadding(12, 4, 12, 4);
            _dupPenguinWarningView.Visibility = ViewStates.Gone;
            _singleBoxDataOuterLayout.AddView(_dupPenguinWarningView);

            _singleBoxDataOuterLayout.AddView(prevAndStickyRow);

            // Prev obs expanded detail (full width, below the row)
            _prevObsSummaryLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _prevObsSummaryLayout.SetPadding(12, 8, 12, 8);
            _prevObsSummaryLayout.Background = _uiFactory.CreateRoundedBackground(Color.ParseColor("#FFF3E0"), 6); // same light orange as the collapsed badge, no border
            var prevExpandedParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            prevExpandedParams.SetMargins(0, 0, 0, 8);
            _prevObsSummaryLayout.LayoutParameters = prevExpandedParams;
            _prevObsSummaryLayout.Visibility = ViewStates.Gone;

            _prevObsDetailLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _prevObsDetailLayout.Visibility = ViewStates.Gone;
            _prevObsSummaryLayout.AddView(_prevObsDetailLayout);

            _prevObsSummaryLayout.Click += (s, e) =>
            {
                _prevObsDetailLayout.Visibility = ViewStates.Gone;
                _prevObsSummaryLayout.Visibility = ViewStates.Gone;
                UpdatePreviousObsSummary();
            };
            _singleBoxDataOuterLayout.AddView(_prevObsSummaryLayout);

            // Sticky box note shown BELOW the expanded prev-obs card (the top-row sticky bar
            // is hidden while expanded; the note itself doesn't belong inside the orange card).
            _stickyNoteBelowPrev = new TextView(this) { TextSize = 12, Gravity = GravityFlags.Center };
            _stickyNoteBelowPrev.SetTextColor(UIFactory.PRIMARY_BLUE);
            _stickyNoteBelowPrev.SetPadding(12, 0, 12, 6);
            _stickyNoteBelowPrev.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            _stickyNoteBelowPrev.Visibility = ViewStates.Gone;
            _stickyNoteBelowPrev.Click += (s, e) => { if (!_isBoxLocked) ShowBoxNotesDialog(); };
            _singleBoxDataOuterLayout.AddView(_stickyNoteBelowPrev);

            // Container for red unsynced cards (older pending observations)
            _unsyncedCardsContainer = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _singleBoxDataOuterLayout.AddView(_unsyncedCardsContainer);

            // Tag mode content — shown instead of normal data entry when editing box tags.
            // Read-only by default; nothing is written until a capture session is saved.
            _tagModeContentLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _tagModeContentLayout.SetPadding(12, 8, 12, 8);
            _tagModeContentLayout.Visibility = ViewStates.Gone;

            // Last known contents. Border says how fresh it is: black = recorded today,
            // orange = the most recent earlier visit. Same language as the overview cards.
            _tagModeLastObsCard = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _tagModeLastObsCard.SetPadding(12, 8, 12, 8);
            var lastObsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lastObsParams.SetMargins(0, 0, 0, 10);
            _tagModeLastObsCard.LayoutParameters = lastObsParams;
            _tagModeContentLayout.AddView(_tagModeLastObsCard);

            _tagModeTagText = new TextView(this) { TextSize = 15 };
            _tagModeTagText.SetTextColor(Color.Black);
            _tagModeTagText.SetPadding(0, 2, 0, 2);
            _tagModeContentLayout.AddView(_tagModeTagText);

            _tagModePositionText = new TextView(this) { TextSize = 15 };
            _tagModePositionText.SetTextColor(Color.Black);
            _tagModePositionText.SetPadding(0, 2, 0, 10);
            _tagModeContentLayout.AddView(_tagModePositionText);

            // The one and only way into editing. Nothing above this point can change data,
            // and nothing above this point turns the GPS on.
            // Two independent jobs, two independent ways in. Setting a tag must never require
            // taking a GPS reading, and vice versa.
            var tagModeActionRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            var actionRowParams2 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            actionRowParams2.SetMargins(0, 4, 0, 4);
            tagModeActionRow.LayoutParameters = actionRowParams2;

            _tagModeSetTagButton = _uiFactory.CreateStyledButton("🏷  Set tag", TAG_MODE_PURPLE);
            _tagModeSetTagButton.TextSize = 16;
            _tagModeSetTagButton.SetPadding(8, 28, 8, 28);
            var setTagParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            setTagParams.SetMargins(0, 0, 5, 0);
            _tagModeSetTagButton.LayoutParameters = setTagParams;
            _tagModeSetTagButton.Click += (s, e) => StartTagOnlyCapture();
            tagModeActionRow.AddView(_tagModeSetTagButton);

            _tagModeSetButton = _uiFactory.CreateStyledButton("📍  Set location", TAG_MODE_PURPLE);
            _tagModeSetButton.TextSize = 16;
            _tagModeSetButton.SetPadding(8, 28, 8, 28);
            var setBtnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            setBtnParams.SetMargins(5, 0, 0, 0);
            _tagModeSetButton.LayoutParameters = setBtnParams;
            _tagModeSetButton.Click += (s, e) => StartTagCapture();
            tagModeActionRow.AddView(_tagModeSetButton);
            _tagModeContentLayout.AddView(tagModeActionRow);

            // Third job on this page, and the only destructive one — so it gets its own row and the
            // danger colour rather than sitting in the purple pair. Shown only to editors and
            // admins: crud.php answers "Editors only" to everyone else, and a button that always
            // fails is worse than no button.
            // Names the box and the day, so what the button will take is settled before it is pressed.
            // Text is rewritten on every draw, when the box is known.
            _tagModeDeleteObsButton = _uiFactory.CreateStyledButton("🗑  Delete today's observation", UIFactory.DANGER_RED);
            _tagModeDeleteObsButton.TextSize = 16;
            _tagModeDeleteObsButton.SetPadding(8, 24, 8, 24);
            var delParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            delParams.SetMargins(0, 8, 0, 4);
            _tagModeDeleteObsButton.LayoutParameters = delParams;
            _tagModeDeleteObsButton.Click += (s, e) => ShowDeleteObservationPicker();
            _tagModeContentLayout.AddView(_tagModeDeleteObsButton);

            // Capture panel — the guided two-step, then one Save that commits both steps
            _tagModeCapturePanel = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _tagModeCapturePanel.SetPadding(14, 12, 14, 14);
            _tagModeCapturePanel.Background = _uiFactory.CreateCardBackground(borderWidth: 6, borderColour: TAG_MODE_PURPLE);
            var capturePanelParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            capturePanelParams.SetMargins(0, 4, 0, 4);
            _tagModeCapturePanel.LayoutParameters = capturePanelParams;
            _tagModeCapturePanel.Visibility = ViewStates.Gone;

            _tagModeCaptureTitle = new TextView(this) { TextSize = 17 };
            _tagModeCaptureTitle.SetTextColor(TAG_MODE_PURPLE);
            _tagModeCaptureTitle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _tagModeCaptureTitle.SetPadding(0, 0, 0, 12);
            _tagModeCapturePanel.AddView(_tagModeCaptureTitle);

            // Step 1 — GPS. Starts only now, and only counts fixes taken from here on.
            _tagModeStep1Text = new TextView(this) { TextSize = 15 };
            _tagModeStep1Text.SetTextColor(Color.Black);
            _tagModeStep1Text.SetPadding(0, 0, 0, 6);
            _tagModeCapturePanel.AddView(_tagModeStep1Text);

            // Outlined, not filled: a secondary correction that still has to look tappable.
            // Flat grey-on-white was too close to the card to read as a button at all.
            _tagModeMeasureAgainButton = _uiFactory.CreateStyledButton("Measure again", UIFactory.LIGHTER_GRAY);
            _tagModeMeasureAgainButton.TextSize = 14;
            _tagModeMeasureAgainButton.SetPadding(20, 16, 20, 16);
            _tagModeMeasureAgainButton.SetTextColor(TAG_MODE_PURPLE);
            _tagModeMeasureAgainButton.Background = _uiFactory.CreateCardBackground(borderWidth: 3, borderColour: TAG_MODE_PURPLE);
            var measureAgainParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            measureAgainParams.SetMargins(0, 0, 0, 14);
            _tagModeMeasureAgainButton.LayoutParameters = measureAgainParams;
            _tagModeMeasureAgainButton.Visibility = ViewStates.Gone;
            _tagModeMeasureAgainButton.Click += (s, e) =>
            {
                if (!_tagCaptureActive) return;
                // Same placement confirmation either way — restarting mid-session is exactly as
                // dependent on the phone being on the nest as starting was.
                StartTagCapture();
            };
            _tagModeCapturePanel.AddView(_tagModeMeasureAgainButton);

            // Step 2 — box tag, optional
            _tagModeStep2Text = new TextView(this) { TextSize = 15 };
            _tagModeStep2Text.SetTextColor(Color.Black);
            _tagModeStep2Text.SetPadding(0, 0, 0, 6);
            _tagModeCapturePanel.AddView(_tagModeStep2Text);

            // Tag side-action sits inside step 2 rather than in the button stack — it's a
            // correction, not one of the two things you came here to do.
            _tagModeTagActionButton = _uiFactory.CreateStyledButton("Remove tag", UIFactory.LIGHTER_GRAY);
            _tagModeTagActionButton.TextSize = 14;
            _tagModeTagActionButton.SetPadding(20, 16, 20, 16);
            _tagModeTagActionButton.SetTextColor(UIFactory.DANGER_RED);
            _tagModeTagActionButton.Background = _uiFactory.CreateCardBackground(borderWidth: 3, borderColour: UIFactory.DANGER_RED);
            var tagActionParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            tagActionParams.SetMargins(0, 0, 0, 14);
            _tagModeTagActionButton.LayoutParameters = tagActionParams;
            _tagModeTagActionButton.Click += (s, e) => OnTagCaptureTagAction();
            _tagModeCapturePanel.AddView(_tagModeTagActionButton);

            // Save and Cancel deliberately do NOT live here — they're in the fixed bottom bar,
            // so their position never shifts as the step text grows.
            _tagModeContentLayout.AddView(_tagModeCapturePanel);

            _singleBoxDataOuterLayout.AddView(_tagModeContentLayout);

            _scannedIdsLayout = new List<LinearLayout?>();
            // Scanned birds container
            _scannedIdsLayout.Add(new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical });
            _scannedIdsLayout[0].SetPadding(16, 16, 16, 16);
            _scannedIdsLayout[0].Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var idsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            idsParams.SetMargins(0, 0, 0, 16);
            _scannedIdsLayout[0].LayoutParameters = idsParams;
            _singleBoxDataContentLayout.AddView(_scannedIdsLayout[0]);

            // Headings row: Adults, Eggs, Chicks, Gate Status
            var headingsLayout = new LinearLayout(this);
            var headingsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            headingsParams.SetMargins(0, 0, 0, 8);
            headingsLayout.LayoutParameters = headingsParams;

            var adultsLabel = _uiFactory.CreateDataLabel("Adults");
            var eggsLabel = _uiFactory.CreateDataLabel("Eggs");
            var chicksLabel = _uiFactory.CreateDataLabel("Chicks");
            var breedingChance = _uiFactory.CreateDataLabel("Nest");
            var gateLabel = _uiFactory.CreateDataLabel("Gate");

            headingsLayout.AddView(adultsLabel);
            headingsLayout.AddView(eggsLabel);
            headingsLayout.AddView(chicksLabel);
            headingsLayout.AddView(breedingChance);
            headingsLayout.AddView(gateLabel);
            _singleBoxDataContentLayout.AddView(headingsLayout);

            // Input fields row: Adults, Eggs, Chicks inputs, Gate Status spinner
            var inputFieldsLayout = new LinearLayout(this);
            var inputFieldsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            inputFieldsParams.SetMargins(0, 0, 0, 16);
            inputFieldsLayout.LayoutParameters = inputFieldsParams;

            _adultsEditText = new List<EditText?>();
            _adultsEditText.Add(_uiFactory.CreateStyledNumberField());
            _eggsEditText = new List<EditText?>();
            _eggsEditText.Add(_uiFactory.CreateStyledNumberField());
            _chicksEditText = new List<EditText?>();
            _chicksEditText.Add(_uiFactory.CreateStyledNumberField());

            _breedingChanceSpinner = new List<Spinner?>();
            List<string> items = new List<string> { "", "NO", "UNL", "POT", "CON", "BR", "ABN", "DCM" };
            _breedingChanceSpinner.Add(_uiFactory.CreateSpinner(items));
            string? breedingChanceString = "";
            try { breedingChanceString = _colonyState.GetTodayForBox(_currentBoxName).BreedingStatus; }
            catch { }
            int breedingPercentageIndex = 0;
            if (breedingChanceString != null)
                breedingPercentageIndex = items.FindIndex(x => x == breedingChanceString);
            breedingPercentageIndex = Math.Max(0, breedingPercentageIndex);
            _breedingChanceSpinner[0].SetSelection(breedingPercentageIndex, false);
            _breedingChanceSpinner[0].ItemSelected += (s, e) =>
            {
                if (!_suppressDataChanged && !_isBoxLocked) _dataChangedSinceUnlock = true;
                string selectedItem = items[e.Position];
                string status = _breedingChanceSpinner[0].SelectedItem.ToString();
            };
            _gateStatusSpinner = new List<Spinner?>();
            _gateStatusSpinner.Add(_uiFactory.CreateSpinner(new string[] { "", "Gate up", "Regate" }));
            _gateStatusSpinner[0].ItemSelected += (s, e) =>
            {
                // Ignore programmatic selection during redraw/navigation. The spinner is
                // disabled while the box is locked, so any event while locked is programmatic.
                if (_suppressDataChanged || _isBoxLocked) return;
                _dataChangedSinceUnlock = true;

                string status = _gateStatusSpinner[0].SelectedItem.ToString();
                if (status.Equals("Gate up") || status.Equals("Regate"))
                {
                    // Setting the gate closes the box, so it is a close and gets the same check the
                    // lock button gets. It was skipping all of them, including the improbable-count
                    // one the lock button has always asked — the quickest way to finish a nest was
                    // also the only way to finish it with an unexamined alarm on it.
                    void CloseFromGate()
                    {
                        SaveCurrentBoxData();
                        CommitDraftForUpload();
                        _isBoxLocked = true;
                        DrawPageLayouts();
                        TryBackgroundUpload();
                    }
                    var gateWarnings = GetBoxCloseWarnings();
                    if (gateWarnings.Count > 0)
                        // Going back keeps the gate that was just chosen — it is not what is in
                        // question — and leaves the box open on the counts that are.
                        ShowBoxCloseWarnings(gateWarnings, CloseFromGate, () =>
                        {
                            SaveCurrentBoxData();
                            _isBoxLocked = false;
                            DrawPageLayouts();
                        });
                    else
                        CloseFromGate();
                }
            };

            // Add event handlers
            _adultsEditText[0].TextChanged += OnDataChanged;
            _adultsEditText[0].Click += OnNumberFieldClick;
            _adultsEditText[0].FocusChange += OnNumberFieldFocus;

            _eggsEditText[0].TextChanged += OnDataChanged;
            _eggsEditText[0].Click += OnNumberFieldClick;
            _eggsEditText[0].FocusChange += OnNumberFieldFocus;

            _chicksEditText[0].TextChanged += OnDataChanged;
            _chicksEditText[0].Click += OnNumberFieldClick;
            _chicksEditText[0].FocusChange += OnNumberFieldFocus;

            inputFieldsLayout.AddView(_adultsEditText[0]);
            inputFieldsLayout.AddView(_eggsEditText[0]);
            inputFieldsLayout.AddView(_chicksEditText[0]);
            inputFieldsLayout.AddView(_breedingChanceSpinner[0]);
            inputFieldsLayout.AddView(_gateStatusSpinner[0]);
            // Nest-status and gate fill the row's height (set by the number fields) so they match
            // Adults/Eggs/Chicks exactly and don't paint half-height before their content settles.
            foreach (var sp in new[] { _breedingChanceSpinner[0], _gateStatusSpinner[0] })
                if (sp?.LayoutParameters is LinearLayout.LayoutParams lp) { lp.Height = ViewGroup.LayoutParams.MatchParent; sp.LayoutParameters = lp; }
            _singleBoxDataContentLayout.AddView(inputFieldsLayout);

            var notesLabel = new TextView(this)
            {
                Text = "Notes:",
                TextSize = 16
            };
            notesLabel.SetTextColor(UIFactory.TEXT_PRIMARY);
            notesLabel.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var notesLabelParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            notesLabelParams.SetMargins(0, 0, 0, 8);
            notesLabel.LayoutParameters = notesLabelParams;
            _singleBoxDataContentLayout.AddView(notesLabel);

            _notesEditText = new List<EditText?>();
            _notesEditText.Add(new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences,
                Hint = "Additional observations",
                Gravity = Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Start
            });
            _notesEditText[0].SetTextColor(UIFactory.TEXT_PRIMARY);
            _notesEditText[0].SetHintTextColor(UIFactory.TEXT_SECONDARY);
            _notesEditText[0].SetPadding(16, 16, 16, 16);
            _notesEditText[0].Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var notesEditParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            notesEditParams.SetMargins(0, 0, 0, 8);
            _notesEditText[0].LayoutParameters = notesEditParams;
            _notesEditText[0].TextChanged += OnDataChanged;
            _singleBoxDataContentLayout.AddView(_notesEditText[0]);

            _singleBoxDataOuterLayout.AddView(_singleBoxDataContentLayout);

            // Box tag mode layout — shown instead of normal content when in tag editing mode
        }
        private void SetEnabledRecursive(View view, bool enabled, float alpha)
        {
            if (view == null)
                return;
            view.Enabled = enabled;
            view.Alpha = alpha;
            if (view is ViewGroup group)
            {
                for (int i = 0; i < group.ChildCount; i++)
                {
                    SetEnabledRecursive(group.GetChildAt(i), enabled, alpha);
                }
            }
        }
        private void OnNumberFieldClick(object? sender, EventArgs e)
        {
            if (sender is EditText editText)
            {
                editText.SelectAll();
            }
        }
        private void OnNumberFieldFocus(object? sender, View.FocusChangeEventArgs e)
        {
            if (e.HasFocus && sender is EditText editText)
            {
                editText.Post(() => editText.SelectAll());
            }
        }
        /// <summary>
        /// Build a BoxObservation from the current UI fields without saving.
        /// </summary>
        private BoxObservation BuildObsFromCurrentUI()
        {
            int.TryParse(_adultsEditText?[0].Text ?? "0", out int adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out int eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out int chicks);
            var obs = new BoxObservation
            {
                BoxName = _currentBoxName,
                Adults = adults, Eggs = eggs, Chicks = chicks,
                GateStatus = GetSelectedStatus(_gateStatusSpinner[0]),
                BreedingStatus = GetSelectedStatus(_breedingChanceSpinner[0]),
                Notes = _notesEditText?[0].Text ?? "",
                WhenDataCollectedUtc = DateTime.UtcNow,
                ObserverName = _appSettings.ObserverName,
            };
            // Copy scans and recorded losses from current box data — the loss buttons write straight
            // to the stored observation, so without this the comparison card shows the edit as
            // having no losses and "Replace" looks like it would drop them.
            var existing = _colonyState.GetTodayForBox(_currentBoxName);
            if (existing != null)
            {
                obs.ScannedIds = existing.ScannedIds;
                obs.FailedEggs = existing.FailedEggs;
                obs.DeadChicks = existing.DeadChicks;
            }
            return obs;
        }

        private void ShowLocalConfirmEditDialog(string boxName, BoxObservation serverVersion, Action onConfirm, Action onDiscard)
        {
            var localObs = BuildObsFromCurrentUI();
            ShowComparisonDialog($"Confirm edit: Box {boxName}", serverVersion, localObs, onConfirm, onDiscard);
        }

        private void OnPrevBoxClick(object? sender, EventArgs e)
        {
            NavigateToBox(_currentBoxIndex - 1, () => _currentBoxIndex > 1);
        }
        private void OnNextBoxClick(object? sender, EventArgs e)
        {
            NavigateToBox(_currentBoxIndex + 1, () => _currentBoxIndex < _boxNamesAndIndexes.Count);
        }
        private void NavigateToBox(int targetBox, Func<bool> canNavigate)
        {
            if (!canNavigate())
                return;

            //foreach 
            KeyValuePair<string, int>? boxNameAndIndex = _boxNamesAndIndexes.Where(x => x.Value == targetBox).First();
            if (boxNameAndIndex != null)
            {
                JumpToBox(boxNameAndIndex.Value.Key);
            }            
        }
        /// <summary>Today's observation for this box, if the server has it.
        ///
        /// Today only, on purpose: the case this is for is correcting a nest observation just made,
        /// and the button names the day so there is no question which record goes. Earlier days stay
        /// a wildwatch job. A record with no ObservationId has never been uploaded, so there is
        /// nothing on the server to delete.</summary>
        private BoxObservation? TodaysDeletableObservation()
        {
            var o = _colonyState.GetTodayForBox(_currentBoxName);
            return o?.ObservationId == null ? null : o;
        }

        private void ShowDeleteObservationPicker()
        {
            var todays = TodaysDeletableObservation();
            if (todays == null)
            {
                new AlertDialog.Builder(this)
                    .SetTitle($"Box {_currentBoxName}")
                    .SetMessage("Nothing to delete — this phone holds no uploaded observation for this box today.\n\n"
                              + "A record made today that hasn't synced yet isn't on the server: leave this page and unlock the box to change it.\n\n"
                              + "Earlier days are deleted on wildwatch.")
                    .SetPositiveButton("OK", (s, e) => { })
                    .Show();
                return;
            }
            ConfirmDeleteObservation(todays);
        }

        /// <summary>What the record actually says, written out in the dialog — counts, gate, notes and
        /// the birds scanned. Plain text rather than the card view used elsewhere: the card's badges
        /// are tappable and would send you off to a bird panel from inside a confirm.</summary>
        private string DescribeObservation(BoxObservation obs)
        {
            var lines = new List<string>();
            var nz = ToNzTime(obs.WhenDataCollectedUtc);
            lines.Add($"🗓  {nz:dddd d MMM, HH:mm}");
            if (!string.IsNullOrEmpty(obs.ObserverName)) lines.Add($"👤  {obs.ObserverName}");
            if (!string.IsNullOrEmpty(obs.BreedingStatus)) lines.Add($"📋  {obs.BreedingStatus}");
            lines.Add($"🐧  {obs.Adults} adult{(obs.Adults != 1 ? "s" : "")}   "
                    + $"🥚  {obs.Eggs} egg{(obs.Eggs != 1 ? "s" : "")}   "
                    + $"🐣  {obs.Chicks} chick{(obs.Chicks != 1 ? "s" : "")}");
            int failed = obs.FailedEggs ?? 0, dead = obs.DeadChicks ?? 0;
            if (failed > 0 || dead > 0)
                lines.Add($"✗  {failed} failed egg{(failed != 1 ? "s" : "")}, {dead} dead chick{(dead != 1 ? "s" : "")}");
            if (!string.IsNullOrEmpty(obs.GateStatus)) lines.Add($"🚪  {obs.GateStatus}");
            // NOSCAN_ rows are "looked, no chip read" placeholders, not birds.
            var birds = obs.ScannedIds.Where(s => !s.BirdId.StartsWith("NOSCAN_")).Select(s => s.BirdId).ToList();
            lines.Add(birds.Count > 0 ? $"🔖  {string.Join(", ", birds)}" : "🔖  No birds scanned");
            if (!string.IsNullOrEmpty(obs.Notes)) lines.Add($"📝  {obs.Notes}");
            return string.Join("\n", lines);
        }

        /// <summary>Show what goes, then ask. The reason is optional and audited, matching the same
        /// prompt on wildwatch, so the two leave the same trail.</summary>
        private void ConfirmDeleteObservation(BoxObservation obs)
        {
            var reasonInput = new EditText(this) { Hint = "Reason (optional)", TextSize = 15 };
            reasonInput.SetPadding(16, 12, 16, 12);
            var wrap = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            wrap.SetPadding(36, 4, 36, 0);
            wrap.AddView(reasonInput);

            new AlertDialog.Builder(this)
                .SetTitle($"Delete today's observation for Box {_currentBoxName}?")
                .SetMessage($"{DescribeObservation(obs)}\n\n"
                          + "The birds scanned on this observation and any measurements taken go with it. "
                          + "It can be restored on wildwatch.")
                .SetView(wrap)
                .SetPositiveButton("Delete", async (s, e) =>
                    await DeleteObservationNow(obs, reasonInput.Text))
                .SetNegativeButton("Cancel", (s, e) => { })
                .SetCancelable(false)
                .Show();
        }

        private async Task DeleteObservationNow(BoxObservation obs, string? reason)
        {
            var id = obs.ObservationId!.Value;
            var error = await _dataStorageService.DeleteObservationAsync(id, reason, _appSettings);
            if (error != null)
            {
                new AlertDialog.Builder(this)
                    .SetTitle("Not deleted")
                    .SetMessage(error)
                    .SetPositiveButton("OK", (s, e) => { })
                    .Show();
                return;
            }

            // Gone on the server, so take it off this phone now rather than leaving it on screen
            // until the next sync gets round to saying so. The row store goes too: the derived views
            // are rebuilt from it, so a row left there would put the observation straight back.
            if (_colonyState.TodayBoxes.TryGetValue(_currentBoxName, out var t) && t.ObservationId == id)
                _colonyState.TodayBoxes.Remove(_currentBoxName);
            if (_colonyState.PreviousBoxes.TryGetValue(_currentBoxName, out var pv) && pv.ObservationId == id)
                _colonyState.PreviousBoxes.Remove(_currentBoxName);
            _colonyState.PendingObservations.RemoveAll(o => o.ObservationId == id);
            DataStorageService.SaveColonyState(this, _colonyState);

            var db = SnapshotSyncService.Load(this);
            if (db.Observations.Remove(id)) SnapshotSyncService.Save(this, db);

            DrawPageLayouts();
            Toast.MakeText(this, $"Observation deleted — Box {_currentBoxName}", ToastLength.Short)?.Show();
        }

        /// <summary>Drop today's uncommitted draft for the current box. A committed or already-synced
        /// observation is left alone — that is a record someone made, not a leftover.</summary>
        private void DiscardTodayDraftForCurrentBox()
        {
            var existing = _colonyState.GetTodayForBox(_currentBoxName);
            if (existing == null || !existing.IsDraft || existing.IsPendingUpload) return;
            _colonyState.PendingObservations.Remove(existing);
            SaveToAppDataDir();
        }

        // Not cancelable, for the same reason the close-warning dialog isn't: a back-tap that runs
        // neither answer left the box locked with the draft still sitting there, which is one of the
        // ways an unwanted observation used to survive to the next lock.
        private void ShowEmptyBoxDialog(Action onConfirm, Action onCancel)
        {
            new AlertDialog.Builder(this)
                .SetTitle("Empty Box")
                .SetMessage("This box has been inspected and is empty")
                .SetPositiveButton("Save as empty", (s, e) => onConfirm())
                .SetNegativeButton("Don't save", (s, e) => onCancel())
                .SetCancelable(false)
                .Show();
        }
        private void OnSaveDataClick(object? sender, EventArgs e)
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle("Save data")
                .SetMessage(CrossedLosses(GetSummaryText()))
                .SetPositiveButton("Save", (s, e) => ShowSaveFilenameDialog())
                .SetNeutralButton("Save & upload", (s, e) => ShowSaveFilenameDialog(true))
                .SetNegativeButton("Cancel", (s, e) => { })
                .SetCancelable(true)
                .Create();
            alertDialog?.Show();
        }
        private void ShowConfirmationDialog(string title, string message, (string text, Action action) positiveButton, (string text, Action action)? negativeButton = null)
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetMessage(CrossedLosses(message))
                .SetPositiveButton(positiveButton.text, (s, e) => positiveButton.action())
                .SetCancelable(true)
                .Create();
            if (null != negativeButton)
                alertDialog.SetButton((int)DialogButtonType.Negative, negativeButton?.text, (s, e) => negativeButton?.action());
            alertDialog?.Show();
        }

        // Monitor deletion removed — data managed via wildwatch.co.nz

        private void ShowBoxNotesDialog()
        {
            string currentNotes = "";
            if (_boxNotes.TryGetValue(_currentBoxName, out var boxNote))
                currentNotes = boxNote.PersistentNotes ?? "";

            var mainLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };
            mainLayout.SetPadding(40, 20, 40, 20);

            var helpText = new TextView(this)
            {
                Text = "Use this text field for notes you want during subsequent monitors i.e. Missing nail.",
                TextSize = 15
            };
            helpText.SetTextColor(Color.White);
            var helpParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            helpParams.SetMargins(0, 0, 0, 12);
            helpText.LayoutParameters = helpParams;
            mainLayout.AddView(helpText);

            var notesInput = new EditText(this)
            {
                Text = currentNotes,
                Hint = "",
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences
            };
            notesInput.SetMinLines(3);
            notesInput.SetTextColor(UIFactory.TEXT_PRIMARY);
            notesInput.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            notesInput.SetPadding(16, 16, 16, 16);
            notesInput.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            mainLayout.AddView(notesInput);

            var dialog = new AlertDialog.Builder(this)
                .SetTitle($"Persistent notes - Box {_currentBoxName}")
                .SetView(mainLayout)
                .SetPositiveButton("Save", (s, e) =>
                {
                    string newNotes = notesInput.Text?.Trim() ?? "";
                    // Update local cache
                    if (!_boxNotes.ContainsKey(_currentBoxName))
                    {
                        _boxNotes[_currentBoxName] = new BoxNoteData { BoxName = _currentBoxName };
                    }
                    _boxNotes[_currentBoxName].PersistentNotes = newNotes;
                    // Queued until the server takes it. Out of signal the save just fails, and the
                    // next sync used to pull the server's older text back over the top of it.
                    _boxNotes[_currentBoxName].NotesPendingUpload = true;
                    int locationId = _boxNotes[_currentBoxName].LocationId;

                    // Save to API in background
                    if (locationId > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            bool success = await _dataStorageService.UpdateBoxNotesAsync(locationId, newNotes, _appSettings.AuthToken);
                            if (success)
                            {
                                _boxNotes[_currentBoxName].NotesPendingUpload = false;
                                _dataStorageService.SaveBoxNotesToDisk(this, _boxNotes);
                            }
                            new Handler(Looper.MainLooper).Post(() =>
                            {
                                Toast.MakeText(this, success ? "Notes saved" : "Notes saved — will sync", ToastLength.Short)?.Show();
                                DrawPageLayouts();
                            });
                        });
                    }
                    else
                    {
                        Toast.MakeText(this, "Box not found on server - notes saved locally only", ToastLength.Short)?.Show();
                        DrawPageLayouts();
                    }

                    // Save local cache to disk
                    var boxNotesJson = Newtonsoft.Json.JsonConvert.SerializeObject(_boxNotes, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(System.IO.Path.Combine(FilesDir?.AbsolutePath, DataStorageService.BOX_NOTES_FILENAME), boxNotesJson);
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            dialog?.Show();

            notesInput.RequestFocus();
            var inputManager = (InputMethodManager?)GetSystemService(InputMethodService);
            inputManager?.ShowSoftInput(notesInput, ShowFlags.Implicit);
        }
        private void OnDataChanged(object? sender, TextChangedEventArgs e)
        {
            if (!_suppressDataChanged && !_isBoxLocked) _dataChangedSinceUnlock = true;
            if ((int.TryParse(_eggsEditText?[0].Text ?? "0", out int eggs) && eggs > 0) || (int.TryParse(_chicksEditText?[0].Text ?? "0", out int chicks) && chicks > 0))
            {
                var spinner = _breedingChanceSpinner[0];
                for (int i = 0; i < spinner.Count; i++)
                {
                    if (spinner.GetItemAtPosition(i).ToString() == "BR")
                    {
                        spinner.SetSelection(i, true);
                        break;
                    }
                }
            }
        }
        /// <summary>
        /// Improbable counts in the box as it currently stands, or null if nothing looks odd.
        /// Only meaningful once the box is finished — mid-edit the counts pass through values
        /// (a bird added before the no-scan it replaces is removed, a "1" on the way to "12")
        /// that are not worth interrupting for, so this is checked at lock time.
        /// </summary>
        /// <summary>Everything about this box that wants a second look before it is closed, as lines
        /// for one dialog.
        ///
        /// The improbable-count check was already asked here. The other two were not: a scan/adult
        /// mismatch was computed by a method nothing ever called, and an offspring drop only painted
        /// a red border on the colony grid — both of which reach the person after they have shut the
        /// lid and walked to the next box, when the one thing that would settle it is another look
        /// inside this one. Asked while they are still standing there, they are answerable.</summary>
        private List<string> GetBoxCloseWarnings()
        {
            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?[0].Text ?? "0", out adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out chicks);

            var warnings = new List<string>();

            // Improbably high for a little blue penguin — a miscount or a mistyped field far more
            // often than a real find, so it is worth confirming rather than refusing.
            if (adults > 2) warnings.Add($"{adults} adults");
            if (eggs > 2) warnings.Add($"{eggs} eggs");
            if (chicks > 2) warnings.Add($"{chicks} chicks");
            if (eggs > 0 && chicks > 0 && eggs + chicks > 2) warnings.Add($"{eggs + chicks} eggs & chicks");

            var probe = ProbeObservationFromScreen(adults, eggs, chicks);
            var mismatch = GetBoxScanMismatch(probe);
            if (mismatch != null) warnings.Add(mismatch);
            var drop = GetOffspringDropWarning(_currentBoxName, probe);
            if (drop != null) warnings.Add(drop);

            return warnings;
        }

        /// <summary>The box as it stands on screen: the counts currently in the fields, over the
        /// birds and recorded losses from the stored copy — scans and losses are not fields, so the
        /// stored row is the only place they live. Never saved; it exists purely so the warning
        /// checks can be asked about what is about to be committed rather than what was committed
        /// last time.</summary>
        private BoxObservation ProbeObservationFromScreen(int adults, int eggs, int chicks)
        {
            var probe = new BoxObservation
            {
                BoxName = _currentBoxName, Adults = adults, Eggs = eggs, Chicks = chicks,
            };
            var stored = _colonyState.GetTodayForBox(_currentBoxName);
            if (stored != null)
            {
                probe.ScannedIds.AddRange(stored.ScannedIds);
                probe.FailedEggs = stored.FailedEggs;
                probe.DeadChicks = stored.DeadChicks;
            }
            return probe;
        }

        /// <summary>Put the close warnings in front of the user, with going back to the box as a
        /// real option. Not cancelable: dismissing without choosing would leave the box locked but
        /// never committed for upload, which is the round quietly losing a nest.</summary>
        private void ShowBoxCloseWarnings(List<string> warnings, Action onConfirm, Action onGoBack)
        {
            var message = "Before closing this box, please check:\n\n"
                        + string.Join("\n", warnings.Select(w => $"• {w}"))
                        + "\n\nGo back and look again, or confirm this is right.";
            new AlertDialog.Builder(this)
                .SetTitle("Check before closing")
                .SetMessage(message)
                .SetPositiveButton("Yes, that's right", (s, e) => onConfirm())
                .SetNegativeButton("Go back", (s, e) => onGoBack())
                .SetCancelable(false)
                .Show();
        }
        /// <param name="allowEmpty">Record the box even with nothing in it. Only the "Save as empty"
        /// answer sets this: an empty observation is a real finding, but it has to be asked for.</param>
        private void SaveCurrentBoxData(bool allowEmpty = false)
        {
            var existing = _colonyState.GetTodayForBox(_currentBoxName);

            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?[0].Text ?? "0", out adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out chicks);

            // Nothing counted, nothing scanned, nothing written down. A draft saying "0 adults, 0
            // eggs, 0 chicks" is indistinguishable from a box someone stood at and recorded as
            // empty — and it was being promoted to exactly that by the next lock, because both the
            // lock branches ask "is there a row for today?" rather than "is there anything in it?".
            // Drafts still exist for boxes with content: a backgrounded app can be killed at any
            // time and losing a round is worse than a stray row. But a draft of nothing protects
            // nothing, so it isn't kept.
            if (!allowEmpty && adults == 0 && eggs == 0 && chicks == 0
                && string.IsNullOrEmpty(GetSelectedStatus(_gateStatusSpinner[0]))
                && string.IsNullOrEmpty(GetSelectedStatus(_breedingChanceSpinner[0]))
                && string.IsNullOrWhiteSpace(_notesEditText?[0].Text)
                && (existing == null || (existing.ScannedIds.Count == 0
                                         && (existing.FailedEggs ?? 0) == 0 && (existing.DeadChicks ?? 0) == 0)))
            {
                // Zeroes over a record that already exists are an edit, not an absence: someone
                // correcting a miscounted clutch down to nothing has to be able to save that. Only
                // an uncommitted draft — a row nobody has confirmed — is dropped.
                if (existing == null) return;
                if (existing.IsDraft && !existing.IsPendingUpload)
                {
                    _colonyState.PendingObservations.Remove(existing);
                    SaveToAppDataDir();
                    return;
                }
            }

            var boxData = existing ?? new BoxObservation { BoxName = _currentBoxName };

            bool changed = boxData.Adults != adults || boxData.Eggs != eggs || boxData.Chicks != chicks
                || (boxData.GateStatus ?? "") != (GetSelectedStatus(_gateStatusSpinner[0]) ?? "")
                || (boxData.BreedingStatus ?? "") != (GetSelectedStatus(_breedingChanceSpinner[0]) ?? "")
                || (boxData.Notes ?? "") != (_notesEditText?[0].Text ?? "");

            boxData.Adults = adults;
            boxData.Eggs = eggs;
            boxData.Chicks = chicks;
            boxData.GateStatus = GetSelectedStatus(_gateStatusSpinner[0]);
            boxData.BreedingStatus = GetSelectedStatus(_breedingChanceSpinner[0]);
            boxData.Notes = _notesEditText?[0].Text ?? "";

            if (changed)
            {
                boxData.WhenDataCollectedUtc = DateTime.UtcNow;
                boxData.ObserverName = _appSettings.ObserverName;
                // Draft only — not flagged for upload until the box is locked (see CommitDraftForUpload).
                // IsDraft persists so a restart doesn't make this look synced (both are IsPendingUpload=false).
                boxData.IsDraft = true;
                _colonyState.SaveBoxObservation(_currentBoxName, boxData);
                SaveToAppDataDir();
            }
        }

        /// <summary>
        /// Confirm the current box's draft for upload — called when the box is locked/finalised.
        /// Until this runs, edits are saved locally but not counted as "pending upload" or synced.
        /// </summary>
        private void CommitDraftForUpload()
        {
            var obs = _colonyState.GetTodayForBox(_currentBoxName);
            if (obs == null) return;
            obs.IsPendingUpload = true;
            obs.IsDraft = false;   // committed now — IsPendingUpload tracks it from here
            obs.PendingUploadSinceUtc ??= DateTime.UtcNow;
            // Auto-replace on server if this box was already synced
            if (obs.ObservationId.HasValue)
                obs.ConfirmedAgainstObsId = obs.ObservationId.Value;
            _colonyState.SaveBoxObservation(_currentBoxName, obs);
            SaveToAppDataDir();
        }
        private void buildScannedIdsLayout(List<ScanRecord> scans)
        {
            if (_scannedIdsLayout == null) return;

            // Clear existing views
            _scannedIdsLayout[0].RemoveAllViews();

            if (scans.Count == 0)
            {
                var emptyText = new TextView(this)
                {
                    Text = "No birds scanned yet",
                    TextSize = 14
                };
                emptyText.SetTextColor(UIFactory.TEXT_SECONDARY);
                _scannedIdsLayout[0].AddView(emptyText);
            }
            else
            {
                // Header text
                var headerText = new TextView(this)
                {
                    Text = $"🐧 {scans.Count} bird{(scans.Count == 1 ? "" : "s")} scanned:",
                    TextSize = 14
                };
                headerText.SetTextColor(UIFactory.TEXT_PRIMARY);
                headerText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                var headerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                headerParams.SetMargins(0, 0, 0, 12);
                headerText.LayoutParameters = headerParams;
                _scannedIdsLayout[0].AddView(headerText);

                // Individual scan records with delete buttons
                for (int i = 0; i < scans.Count; i++)
                {
                    var scan = scans[i];
                    var scanLayout = CreateScanRecordView(scan, i);
                    _scannedIdsLayout[0].AddView(scanLayout);
                }
            }

            // One row per loss, listed under the birds — same removable shape as a no-scan, so a
            // mistap is undone the same way everything else here is, and the count is visible as
            // rows rather than as a number stuck on a button.
            var lossNow = _colonyState.GetTodayForBox(_currentBoxName);
            void AddLossRows(int count, string label, Action onRemove)
            {
                for (int i = 0; i < count; i++)
                {
                    var row = new LinearLayout(this);
                    var rowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                    rowParams.SetMargins(0, 2, 0, 2);
                    row.LayoutParameters = rowParams;
                    row.Background = _uiFactory.CreateRoundedBackground(BOX_MISMATCH_BG, 4);
                    row.SetPadding(12, 8, 12, 8);
                    row.SetGravity(GravityFlags.CenterVertical);

                    var text = new TextView(this) { TextFormatted = CrossedLosses(label), TextSize = 14 };
                    text.SetTextColor(Color.Black);
                    text.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                    row.AddView(text);

                    var del = new Button(this) { Text = "Delete", TextSize = 12 };
                    del.SetTextColor(Color.White);
                    del.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                    del.SetPadding(12, 8, 12, 8);
                    del.Background = _uiFactory.CreateRoundedBackground(UIFactory.DANGER_RED, 8);
                    del.SetAllCaps(false);
                    var delParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    delParams.SetMargins(4, 0, 0, 0);
                    del.LayoutParameters = delParams;
                    del.Click += (s, e) => onRemove();
                    row.AddView(del);

                    _scannedIdsLayout[0].AddView(row);
                }
            }
            AddLossRows(lossNow?.FailedEggs ?? 0, "🥚✗ Failed egg", () => BumpLoss(d => d.FailedEggs = Math.Max(0, (d.FailedEggs ?? 0) - 1), "Failed egg removed"));
            AddLossRows(lossNow?.DeadChicks ?? 0, "🐣✗ Dead chick", () => BumpLoss(d => d.DeadChicks = Math.Max(0, (d.DeadChicks ?? 0) - 1), "Dead chick removed"));

            // Penguin search field with dropdown results (select to add a scan)
            var searchContainer = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var searchContainerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            searchContainerParams.SetMargins(0, 12, 0, 0);
            searchContainer.LayoutParameters = searchContainerParams;

            _penguinSearchEditText = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapCharacters,
                Hint = "Enter penguin",
                TextSize = 14
            };
            _penguinSearchEditText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _penguinSearchEditText.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            _penguinSearchEditText.SetPadding(12, 12, 12, 12);
            _penguinSearchEditText.Background = _uiFactory.CreateRoundedBackground(Color.White, 8);

            var editTextParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            editTextParams.SetMargins(0, 0, 0, 0);
            _penguinSearchEditText.LayoutParameters = editTextParams;

            // Search + No scan button row
            var searchRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            searchRow.SetGravity(GravityFlags.CenterVertical);
            _penguinSearchEditText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            searchRow.AddView(_penguinSearchEditText);

            var noScanBtn = new Button(this) { Text = "No scan", TextSize = 14 };
            noScanBtn.SetTextColor(Color.Black);
            noScanBtn.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            noScanBtn.SetPadding(14, 14, 14, 14);
            noScanBtn.Background = ButtonFace();
            noScanBtn.SetAllCaps(false);
            var noScanBtnParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            noScanBtnParams.SetMargins(8, 0, 0, 0);
            noScanBtnParams.Gravity = GravityFlags.CenterVertical;
            noScanBtn.LayoutParameters = noScanBtnParams;
            noScanBtn.Click += (s, e) =>
            {
                BumpCount(_adultsEditText, 1);
                var boxData = _colonyState.GetTodayForBox(_currentBoxName) ?? new BoxObservation { BoxName = _currentBoxName };
                var noScanId = $"NOSCAN_{boxData.ScannedIds.Count(s2 => s2.BirdId.StartsWith("NOSCAN_")) + 1}";
                boxData.ScannedIds.Add(new ScanRecord { BirdId = noScanId, Timestamp = DateTime.UtcNow });
                boxData.WhenDataCollectedUtc = DateTime.UtcNow;
                boxData.ObserverName = _appSettings.ObserverName;
                boxData.IsDraft = true;   // draft until the box is locked; persists across restart
                _colonyState.SaveBoxObservation(_currentBoxName, boxData); // draft until the box is locked
                SaveCurrentBoxData();
                _dataChangedSinceUnlock = true;
                DrawPageLayouts();
                Toast.MakeText(this, "+1 Adult (no scan)", ToastLength.Short)?.Show();
            };
            searchRow.AddView(noScanBtn);

            // Losses seen this visit. They sit straight in the row beside No scan — the "failed"
            // caption that used to head them pushed these two out of line with it, and the ✗ on
            // each already says what they are.
            Button MakeLostButton(string text, string toast, Action<BoxObservation> bump)
            {
                var b = new Button(this) { TextFormatted = CrossedLosses(text), TextSize = 17 };
                b.SetTextColor(Color.Black);
                b.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                b.SetPadding(16, 14, 16, 14);
                b.Background = ButtonFace();
                b.SetAllCaps(false);
                var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                p.SetMargins(6, 0, 0, 0);
                p.Gravity = GravityFlags.CenterVertical;
                b.LayoutParameters = p;
                b.Click += (s, e) => BumpLoss(bump, toast);
                return b;
            }
            // These stand apart from the live egg/chick totals on purpose: they record a loss the
            // totals can't show, such as an egg that failed and was replaced the same day.
            searchRow.AddView(MakeLostButton("🥚✗", "+1 failed egg", d => d.FailedEggs = (d.FailedEggs ?? 0) + 1));
            searchRow.AddView(MakeLostButton("🐣✗", "+1 dead chick", d => d.DeadChicks = (d.DeadChicks ?? 0) + 1));

            searchContainer.AddView(searchRow);

            // Dropdown results container
            var _searchResultsLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            searchContainer.AddView(_searchResultsLayout);

            _penguinSearchEditText.TextChanged += (s, e) =>
            {
                _searchResultsLayout.RemoveAllViews();
                var query = _penguinSearchEditText.Text?.Trim().ToUpper() ?? "";
                if (query.Length < 1 || _remotePenguinData == null) return;

                // Search: exact peng# match first, then pit_id substring, then peng# prefix
                var exactPengNum = new List<PenguinData>();
                var pitIdMatches = new List<PenguinData>();
                var pengNumPrefix = new List<PenguinData>();

                foreach (var pd in _remotePenguinData.Values)
                {
                    if (pd.PengNum == query)
                        exactPengNum.Add(pd);
                    else if (pd.ScannedId.Contains(query))
                        pitIdMatches.Add(pd);
                    else if (pd.PengNum.StartsWith(query))
                        pengNumPrefix.Add(pd);
                }

                // Order: exact peng# → pit_id matches → peng# prefix
                var results = exactPengNum
                    .Concat(pitIdMatches.OrderBy(p => p.ScannedId))
                    .Concat(pengNumPrefix.OrderBy(p => int.TryParse(p.PengNum, out var n) ? n : 9999))
                    .Take(8)
                    .ToList();

                foreach (var pd in results)
                {
                    var fullPitId = !string.IsNullOrEmpty(pd.FullPitId) ? pd.FullPitId : pd.ScannedId;
                    var resultView = CreateScanBadge(fullPitId, () =>
                    {
                        AddScannedId(fullPitId, 0, isManualEntry: true);
                        _penguinSearchEditText.Text = "";
                        _searchResultsLayout.RemoveAllViews();
                        var imm = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
                        imm?.HideSoftInputFromWindow(_penguinSearchEditText.WindowToken, 0);
                    }, textSize: 12);
                    resultView.SetPadding(12, 8, 12, 8);
                    // Wrap the badge rather than stretching it across the card — a peng badge is
                    // a few characters wide, and full-width rows read as a list of empty bars.
                    var resultParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    resultParams.SetMargins(0, 2, 0, 2);
                    resultView.LayoutParameters = resultParams;

                    _searchResultsLayout.AddView(resultView);
                }
            };

            _scannedIdsLayout[0].AddView(searchContainer);

        }
        /// <summary>A raised, outlined face for the row's tap targets. A flat light-grey fill on a
        /// light card read as a label rather than a button.</summary>
        private Android.Graphics.Drawables.GradientDrawable ButtonFace()
        {
            var density = Resources?.DisplayMetrics?.Density ?? 2f;
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(Color.White);
            d.SetCornerRadius(8 * density);
            d.SetStroke((int)(1.5f * density), UIFactory.TEXT_SECONDARY);
            return d;
        }

        /// <summary>Applies a change to today's loss counts and persists it, mirroring the no-scan
        /// button: mutate the stored draft, then let SaveCurrentBoxData fold in the typed counts.</summary>
        private void BumpLoss(Action<BoxObservation> change, string toast)
        {
            var boxData = _colonyState.GetTodayForBox(_currentBoxName) ?? new BoxObservation { BoxName = _currentBoxName };
            change(boxData);
            boxData.WhenDataCollectedUtc = DateTime.UtcNow;
            boxData.ObserverName = _appSettings.ObserverName;
            boxData.IsDraft = true;
            _colonyState.SaveBoxObservation(_currentBoxName, boxData);
            // Persist immediately. SaveCurrentBoxData only writes to disk when a TYPED count
            // (adults/eggs/chicks) changed, and a loss tap changes none of those — so without
            // this the recorded loss lives only in memory and is lost if the app is killed
            // before the box is locked. Every ordinary count edit persists on the spot; match that.
            SaveToAppDataDir();
            SaveCurrentBoxData();
            _dataChangedSinceUnlock = true;
            DrawPageLayouts();
            Toast.MakeText(this, toast, ToastLength.Short)?.Show();
        }

        private LinearLayout CreateScanRecordView(ScanRecord scan, int index)
        {
            var scanLayout = new LinearLayout(this);

            var layoutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            layoutParams.SetMargins(0, 2, 0, 2);
            scanLayout.LayoutParameters = layoutParams;

            // Handle "No scan" placeholder entries
            if (scan.BirdId.StartsWith("NOSCAN_"))
            {
                scanLayout.Background = _uiFactory.CreateRoundedBackground(SCAN_CHIPPED_TODAY_BG, 4);
                scanLayout.SetPadding(12, 8, 12, 8);
                scanLayout.SetGravity(GravityFlags.CenterVertical);
                var noScanText = new TextView(this) { Text = "🐧 No scan", TextSize = 14 };
                noScanText.SetTextColor(Color.Black);
                noScanText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                scanLayout.AddView(noScanText);

                // Same Delete as a known-penguin row (no-scans were otherwise undeletable)
                var noScanDelete = new Button(this) { Text = "Delete", TextSize = 12 };
                noScanDelete.SetTextColor(Color.White);
                noScanDelete.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                noScanDelete.SetPadding(12, 8, 12, 8);
                noScanDelete.Background = _uiFactory.CreateRoundedBackground(UIFactory.DANGER_RED, 8);
                noScanDelete.SetAllCaps(false);
                var noScanDeleteParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                noScanDeleteParams.SetMargins(4, 0, 0, 0);
                noScanDelete.LayoutParameters = noScanDeleteParams;
                noScanDelete.Click += (sender, e) => OnDeleteScanClick(scan);
                scanLayout.AddView(noScanDelete);
                return scanLayout;
            }

            var (_, _, _, penguinData) = LookupPenguinLabel(scan.BirdId);

            // Neutral alternating row — the pengMiniView badge carries the colour now (no whole-line colour)
            scanLayout.Background = _uiFactory.CreateRoundedBackground(index % 2 == 0 ? UIFactory.SCAN_ROW_EVEN : UIFactory.SCAN_ROW_ODD, 4);
            scanLayout.SetPadding(12, 8, 12, 8);
            scanLayout.SetGravity(GravityFlags.CenterVertical);

            // pengMiniView badge — tap shows the bird panel in a modal (no prompt)
            var badge = CreateScanBadge(scan.BirdId, () => ShowBirdPanel(scan.BirdId, penguinData?.PengNum), textSize: 14);
            scanLayout.AddView(badge);

            // Time (with a 🆕 marker for birds chipped today)
            bool chippedToday = penguinData != null && penguinData.ChipDate > DateTime.MinValue && ToNzTime(penguinData.ChipDate).Date == NzToday;
            var timeText = new TextView(this)
            {
                Text = (chippedToday ? "🆕 " : "") + $"{ToNzTime(scan.Timestamp):HH:mm}",
                TextSize = 12
            };
            timeText.SetTextColor(Color.Black);
            timeText.SetPadding(10, 0, 10, 0);
            timeText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            scanLayout.AddView(timeText);

            // Detail button — opens the biometric detail directly
            var detailButton = new Button(this)
            {
                Text = "Detail",
                TextSize = 12
            };
            detailButton.SetTextColor(Color.White);
            detailButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            detailButton.SetPadding(12, 8, 12, 8);
            detailButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.SUCCESS_GREEN, 8);
            detailButton.SetAllCaps(false);

            var detailButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            detailButtonParams.SetMargins(8, 0, 4, 0);
            detailButton.LayoutParameters = detailButtonParams;

            detailButton.Click += (sender, e) => ShowBiometricForm(scan.BirdId, penguinData, penguinData?.PengNum ?? "");

            scanLayout.AddView(detailButton);

            // Move button
            var moveButton = new Button(this)
            {
                Text = "Move",
                TextSize = 12
            };
            moveButton.SetTextColor(Color.White);
            moveButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal); 
            moveButton.SetPadding(12, 8, 12, 8);
            moveButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 8);
            moveButton.SetAllCaps(false);

            var moveButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            moveButtonParams.SetMargins(8, 0, 4, 0);
            moveButton.LayoutParameters = moveButtonParams;

            // Set up move functionality
            moveButton.Click += (sender, e) => OnMoveScanClick(scan);

            scanLayout.AddView(moveButton);

            // Delete button
            var deleteButton = new Button(this)
            {
                Text = "Delete",
                TextSize = 12
            };
            deleteButton.SetTextColor(Color.White);
            deleteButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal); 
            deleteButton.SetPadding(12, 8, 12, 8);
            deleteButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.DANGER_RED, 8);
            deleteButton.SetAllCaps(false);

            var deleteButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            deleteButtonParams.SetMargins(4, 0, 0, 0);
            deleteButton.LayoutParameters = deleteButtonParams;

            // Set up delete functionality
            deleteButton.Click += (sender, e) => OnDeleteScanClick(scan);

            scanLayout.AddView(deleteButton);

            return scanLayout;
        }
        // Opens a read-only Wildwatch panel (bird or box) in a modal WebView (?embed=1 mode of
        // the wildwatch app), reusing the exact web rendering so it can never drift from the
        // website. The session token is injected as window.__WW_TOKEN__ before page scripts run,
        // so it never appears in the URL (and thus never in a server log or history entry).
        //
        // One WebView is kept alive and pre-warmed (WarmEmbedWebView): the embed app boots once,
        // syncs the whole colony into its IndexedDB, and subsequent opens just tell it what to
        // render via the JS bridge (window.wwShow) — no page load, no network, works offline.
        private Android.Webkit.WebView? _embedWebView;
        private int _embedColonyId;
        // ◀/▶ visibility for the open embed dialog — driven by the embed app, which reports
        // its view-history state through document.title ("wwnav:<back>:<fwd>").
        private bool _embedCanBack, _embedCanFwd;
        private Android.Widget.Button? _embedBackBtn, _embedFwdBtn;
        // One-shot callback fired when the embed signals it has finished a colony sync
        // (document.title = "wwready:...") — used by the sync modal's web-cache line.
        private Action? _onEmbedReady;

        // "Chip only" overview filter: boxes with a bird chipped there in the last 30 days
        // that hasn't been seen again on a later day. Computed by the embed's web cache
        // (window.wwChipOnlyBoxes) — the native side has no chip/scan history — and cached
        // here because the overview draws synchronously. Refreshed on every wwready signal.
        private HashSet<string> _chipOnlyBoxes = new HashSet<string>();

        private void RefreshChipOnlyBoxes(bool redrawOnChange = false)
        {
            var webView = _embedWebView;
            if (webView == null) return;
            try
            {
                webView.EvaluateJavascript(
                    "JSON.stringify(typeof window.wwChipOnlyBoxes==='function'?window.wwChipOnlyBoxes(30):[])",
                    new JsResultCallback(v =>
                    {
                        try
                        {
                            // EvaluateJavascript double-encodes: v is a JSON string containing JSON
                            var inner = JsonConvert.DeserializeObject<string>(v ?? "null");
                            var boxes = JsonConvert.DeserializeObject<List<string>>(inner ?? "[]") ?? new List<string>();
                            var next = new HashSet<string>(boxes);
                            bool changed = !next.SetEquals(_chipOnlyBoxes);
                            _chipOnlyBoxes = next;
                            if (changed && redrawOnChange && _appSettings.ShowChipOnlyBoxesInMultiBoxView)
                                RunOnUiThread(DrawPageLayouts);
                        }
                        catch { }
                    }));
            }
            catch { }
        }

        // Manual Sync: clear the HTTP cache and reboot the embed so it re-syncs. The IndexedDB
        // colony cache is deliberately KEPT — the embed's boot sync is incremental (?since=
        // watermark) and self-healing (server row counts ride along on every incremental
        // response; any mismatch triggers an automatic full re-download). Wiping web storage
        // here forced a full gzipped-DB download on every manual sync. Must run on the UI thread.
        private void RefreshEmbedWebView(Action<string> onStatus)
        {
            try
            {
                _embedWebView?.ClearCache(true);
                if (string.IsNullOrEmpty(_appSettings?.AuthToken)) { onStatus("Web view: cleared"); return; }
                var colonyId = CurrentColonyIdOrDefault();
                var webView = GetOrCreateEmbedWebView();
                _embedColonyId = colonyId;
                _onEmbedReady = () => onStatus("Web view ✓");
                webView.LoadUrl($"https://wildwatch.co.nz/box/_?embed=1&colony_id={colonyId}");
                onStatus("Web view: re-syncing...");
            }
            catch { onStatus("Web view ✗"); }
        }

        private void UpdateEmbedNavButtons()
        {
            if (_embedBackBtn != null) { _embedBackBtn.Enabled = _embedCanBack; _embedBackBtn.Alpha = _embedCanBack ? 1f : 0.3f; }
            if (_embedFwdBtn != null) { _embedFwdBtn.Enabled = _embedCanFwd; _embedFwdBtn.Alpha = _embedCanFwd ? 1f : 0.3f; }
        }

        private class EmbedChromeClient : Android.Webkit.WebChromeClient
        {
            private readonly Action<string?> _onTitle;
            public EmbedChromeClient(Action<string?> onTitle) { _onTitle = onTitle; }
            public override void OnReceivedTitle(Android.Webkit.WebView? view, string? title) => _onTitle(title);
        }

        private int CurrentColonyIdOrDefault() =>
            (_appSettings?.SelectedColonyId ?? 0) > 0 ? _appSettings!.SelectedColonyId : 1;

        private static string JsEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private Android.Webkit.WebView GetOrCreateEmbedWebView()
        {
            if (_embedWebView == null)
            {
                var webView = new Android.Webkit.WebView(this);
                webView.Settings.JavaScriptEnabled = true;
                webView.Settings.DomStorageEnabled = true; // the embed uses localStorage + IndexedDB
                webView.SetWebViewClient(new EmbedWebViewClient(_appSettings?.AuthToken ?? ""));
                webView.SetWebChromeClient(new EmbedChromeClient(title =>
                {
                    if (title == null) return;
                    if (title.StartsWith("wwnav:"))
                    {
                        var parts = title.Split(':');
                        _embedCanBack = parts.Length > 1 && parts[1] == "1";
                        _embedCanFwd = parts.Length > 2 && parts[2] == "1";
                        RunOnUiThread(UpdateEmbedNavButtons);
                    }
                    else if (title.StartsWith("wwready"))
                    {
                        var cb = _onEmbedReady;
                        _onEmbedReady = null;
                        cb?.Invoke();
                        // Fresh colony data in the web cache — recompute the Chip-only box list
                        RunOnUiThread(() => RefreshChipOnlyBoxes(redrawOnChange: true));
                    }
                }));
                _embedWebView = webView;
            }
            return _embedWebView;
        }

        // Boot the embed app + colony sync in the background so the first panel open is instant.
        // Called on startup and after a colony switch (which re-syncs via wwSetColony).
        private void WarmEmbedWebView()
        {
            if (string.IsNullOrEmpty(_appSettings?.AuthToken)) return; // embed needs auth
            try
            {
                var colonyId = CurrentColonyIdOrDefault();
                var webView = GetOrCreateEmbedWebView();
                if (webView.Url == null)
                {
                    // "_" is a placeholder id — the WebView is hidden, we only want boot + sync.
                    _embedColonyId = colonyId;
                    webView.LoadUrl($"https://wildwatch.co.nz/box/_?embed=1&colony_id={colonyId}");
                }
                else if (_embedColonyId != colonyId)
                {
                    _embedColonyId = colonyId;
                    webView.EvaluateJavascript($"window.wwSetColony&&window.wwSetColony({colonyId})", null);
                }
            }
            catch { }
        }

        private void OpenEmbedPanel(string kind, string id)
        {
            var colonyId = CurrentColonyIdOrDefault();
            var webView = GetOrCreateEmbedWebView();
            (webView.Parent as ViewGroup)?.RemoveView(webView); // reclaim from a dismissed dialog

            var targetUrl = $"https://wildwatch.co.nz/{kind}/{Android.Net.Uri.Encode(id)}?embed=1&colony_id={colonyId}";
            if (webView.Url == null || _embedColonyId != colonyId)
            {
                // Cold or wrong-colony WebView — full load (boots the app + syncs the colony).
                _embedColonyId = colonyId;
                webView.LoadUrl(targetUrl);
            }
            else
            {
                // Warm path: render via the JS bridge. If the embed app hasn't finished booting
                // yet (wwShow not registered), fall back to a full load of the target.
                var js = $"typeof window.wwShow==='function'?(window.wwShow('{kind}','{JsEscape(id)}'),'ok'):'no'";
                webView.EvaluateJavascript(js, new JsResultCallback(v =>
                {
                    if (v == null || !v.Contains("ok"))
                        RunOnUiThread(() => webView.LoadUrl(targetUrl));
                }));
            }

            var dialog = new AlertDialog.Builder(this)
                .SetView(webView)
                .SetNeutralButton("◀", (s, e) => { })
                .SetPositiveButton("Close", (s, e) => { })
                .SetNegativeButton("▶", (s, e) => { })
                .Create();
            // Detach on dismiss so the warm WebView can be re-hosted by the next dialog.
            dialog.DismissEvent += (s, e) =>
            {
                (webView.Parent as ViewGroup)?.RemoveView(webView);
                _embedBackBtn = _embedFwdBtn = null;
            };
            dialog.Show();
            dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            // Re-wiring Click replaces the auto-dismiss listener, so ◀/▶ keep the dialog open;
            // they step through the embed's view history via the JS bridge.
            _embedBackBtn = dialog.GetButton((int)Android.Content.DialogButtonType.Neutral);
            if (_embedBackBtn != null) _embedBackBtn.Click += (s, e) => webView.EvaluateJavascript("window.wwBack&&window.wwBack()", null);
            _embedFwdBtn = dialog.GetButton((int)Android.Content.DialogButtonType.Negative);
            if (_embedFwdBtn != null) _embedFwdBtn.Click += (s, e) => webView.EvaluateJavascript("window.wwForward&&window.wwForward()", null);
            // Fresh panel = fresh history session; the embed's title signal re-shows them.
            _embedCanBack = _embedCanFwd = false;
            UpdateEmbedNavButtons();
        }

        private class JsResultCallback : Java.Lang.Object, Android.Webkit.IValueCallback
        {
            private readonly Action<string?> _callback;
            public JsResultCallback(Action<string?> callback) { _callback = callback; }
            public void OnReceiveValue(Java.Lang.Object? value) => _callback(value?.ToString());
        }

        // Tapping a bird mini-view goes straight here — no prompt.
        private void ShowBirdPanel(string birdId, string? pengNumHint = null)
        {
            var pengNum = pengNumHint ?? "";
            if (string.IsNullOrEmpty(pengNum))
            {
                PenguinData? pd = null;
                _remotePenguinData?.TryGetValue(birdId, out pd);
                pengNum = pd?.PengNum ?? "";
            }
            if (string.IsNullOrEmpty(pengNum))
            {
                Toast.MakeText(this, "Bird not in database", ToastLength.Short)?.Show();
                return;
            }
            OpenEmbedPanel("bird", pengNum);
        }

        // Tapping a box badge shows its breeding history + observations in a modal.
        private void ShowBoxPanel(string boxName)
        {
            if (string.IsNullOrEmpty(boxName)) return;
            OpenEmbedPanel("box", boxName);
        }

        // Injects the session token as a JS global before the embed's own scripts execute, so
        // its fetch is authenticated without the token ever going into the URL.
        private class EmbedWebViewClient : Android.Webkit.WebViewClient
        {
            private readonly string _injectJs;
            public EmbedWebViewClient(string token)
            {
                // Session tokens are hex (bin2hex from auth.php), so no escaping is needed.
                _injectJs = "window.__WW_TOKEN__='" + (token ?? "") + "';";
            }
            public override void OnPageStarted(Android.Webkit.WebView? view, string? url, Android.Graphics.Bitmap? favicon)
            {
                base.OnPageStarted(view, url, favicon);
                view?.EvaluateJavascript(_injectJs, null);
            }
        }

        // Observed sex-guess scale stored in penguin_biometric_data.observed_sex (wildwatch codes PM/MM/U/MF/PF).
        // First entry is the blank "not recorded" default.
        private static readonly (string code, string label)[] ObservedSexOptions = new[]
        {
            ("", ""),   // unset — blank, so an empty field reads as "not recorded"
            ("PM", "Confident M"),
            ("MM", "Maybe M"),
            ("U", "Unsure"),
            ("MF", "Maybe F"),
            ("PF", "Confident F"),
        };

        private void ShowBiometricForm(string birdId, PenguinData? pd, string pengNum)
        {
            // Read today's biometric record from the local cache (kept fresh by the main sync) —
            // opens instantly and works offline. Any unsynced edit is already in this cache.
            BiometricRecord? existing = null;
            if (!string.IsNullOrEmpty(pengNum))
                existing = _colonyState.GetBiometric(pengNum, NzNow.ToString("yyyy-MM-dd"));
            ShowBiometricFormUI(birdId, pd, pengNum, existing);
        }

        private void ShowBiometricFormUI(string birdId, PenguinData? pd, string pengNum, BiometricRecord? existing)
        {
            var (pengLabel, _, _, _) = LookupPenguinLabel(birdId);
            var title = $"{pengLabel} detail";
            if (existing != null) title += " (update)";

            var scrollView = new ScrollView(this);
            var card = _uiFactory.CreateCard();
            card.SetPadding(16, 16, 16, 16);
            scrollView.AddView(card);

            // Styled input helper
            EditText createInput(string hint, Android.Text.InputTypes inputType)
            {
                var input = new EditText(this) { InputType = inputType, Hint = hint, TextSize = 14 };
                input.SetTextColor(UIFactory.TEXT_PRIMARY);
                input.SetHintTextColor(UIFactory.TEXT_SECONDARY);
                input.SetPadding(16, 12, 16, 12);
                input.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
                var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                p.SetMargins(0, 4, 0, 12);
                input.LayoutParameters = p;
                return input;
            }

            TextView createLabel(string text)
            {
                var lbl = new TextView(this) { Text = text, TextSize = 14 };
                lbl.SetTextColor(UIFactory.TEXT_PRIMARY);
                lbl.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                return lbl;
            }

            // Weight
            card.AddView(createLabel("Weight (g)"));
            var weightInput = createInput("", Android.Text.InputTypes.ClassNumber | Android.Text.InputTypes.NumberFlagDecimal);
            weightInput.Text = existing?.Weight ?? "";
            card.AddView(weightInput);

            // Right flipper length
            card.AddView(createLabel("Flipper (mm)"));
            var flipperInput = createInput("", Android.Text.InputTypes.ClassNumber | Android.Text.InputTypes.NumberFlagDecimal);
            flipperInput.Text = existing?.FlipperLength ?? "";
            card.AddView(flipperInput);

            // Sex
            card.AddView(createLabel("Sex"));
            var sexSpinner = _uiFactory.CreateSpinner(ObservedSexOptions.Select(o => o.label).ToList());
            var savedSex = existing?.ObservedSex ?? "";
            var sexIdx = Array.FindIndex(ObservedSexOptions, o => o.code == savedSex);
            if (sexIdx >= 0) sexSpinner.SetSelection(sexIdx);
            var spinnerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            spinnerParams.SetMargins(0, 4, 0, 12);
            sexSpinner.LayoutParameters = spinnerParams;
            card.AddView(sexSpinner);

            // Conditions
            card.AddView(createLabel("Condition"));
            var conditions = new (string label, string field)[] {
                ("Moulting", "condition_moulting"),
                ("Dead", "condition_dead"),
            };
            bool condChecked(string field) => existing != null && field switch
            {
                "condition_moulting" => existing.ConditionMoulting,
                "condition_dead" => existing.ConditionDead,
                _ => false,
            };
            var conditionChecks = new Dictionary<string, CheckBox>();
            foreach (var (label, field) in conditions)
            {
                var cb = new CheckBox(this) { Text = label };
                cb.SetTextColor(Color.Black);
                cb.Checked = condChecked(field);
                conditionChecks[field] = cb;
                card.AddView(cb);
            }

            // Notes
            card.AddView(createLabel("Notes"));
            var notesInput = createInput("Observations about this bird",
                Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences);
            notesInput.Text = existing?.Notes ?? "";
            card.AddView(notesInput);

            // Save button
            var saveButton = _uiFactory.CreateStyledButton("Save", UIFactory.SUCCESS_GREEN);
            var saveParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            saveParams.SetMargins(0, 16, 0, 0);
            saveButton.LayoutParameters = saveParams;
            card.AddView(saveButton);

            var dialog = new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetView(scrollView)
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            saveButton.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(pengNum))
                {
                    Toast.MakeText(this, "Cannot save — penguin not in database", ToastLength.Short)?.Show();
                    return;
                }

                // Save to the local cache + sync queue — instant, offline-safe. The next sync (and the
                // prompt background flush below) uploads it; the server id is preserved so edits update.
                var selectedSexLabel = sexSpinner.SelectedItem?.ToString() ?? "";
                // A blank form is not a measurement. Saved, it became an empty biometric on wildwatch
                // (date only, "Sex -") — e.g. Save pressed after the sex prompt popped up over the form.
                bool nothingEntered = string.IsNullOrWhiteSpace(weightInput.Text) && string.IsNullOrWhiteSpace(flipperInput.Text)
                    && string.IsNullOrEmpty(ObservedSexOptions.FirstOrDefault(o => o.label == selectedSexLabel).code)
                    && !conditionChecks["condition_moulting"].Checked && !conditionChecks["condition_dead"].Checked
                    && string.IsNullOrWhiteSpace(notesInput.Text);
                if (nothingEntered && existing == null)
                {
                    Toast.MakeText(this, "Nothing entered — not saved", ToastLength.Short)?.Show();
                    dialog.Dismiss();
                    return;
                }
                var record = new BiometricRecord
                {
                    PengNum = pengNum,
                    ObservationDate = NzNow.ToString("yyyy-MM-dd"),
                    Weight = string.IsNullOrEmpty(weightInput.Text) ? null : weightInput.Text,
                    FlipperLength = string.IsNullOrEmpty(flipperInput.Text) ? null : flipperInput.Text,
                    ObservedSex = ObservedSexOptions.FirstOrDefault(o => o.label == selectedSexLabel).code,
                    ConditionMoulting = conditionChecks["condition_moulting"].Checked,
                    ConditionTicks = existing?.ConditionTicks ?? false, // checkbox removed — keep stored value
                    ConditionDead = conditionChecks["condition_dead"].Checked,
                    Notes = string.IsNullOrEmpty(notesInput.Text) ? null : notesInput.Text,
                    BiometricId = existing?.BiometricId,
                    IsPendingUpload = true,
                    PendingUploadSinceUtc = DateTime.UtcNow,
                };
                _colonyState.SaveBiometric(record);
                SaveToAppDataDir();
                Toast.MakeText(this, $"Saved for #{pengNum} — will sync", ToastLength.Short)?.Show();
                dialog.Dismiss();

                // Prompt background flush so it uploads promptly; a full sync also flushes it.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var flush = await _dataStorageService.UploadPendingBiometricsOnly(_colonyState, _appSettings);
                        DataStorageService.SaveColonyState(this, _colonyState);
                        // Say so if the server refused it — the record is still queued, and the
                        // "will sync" toast above would otherwise be the last word on it.
                        if (flush.BiometricUploadErrors > 0 && !flush.AuthFailed)
                            RunOnUiThread(() => Toast.MakeText(this,
                                $"Not saved to wildwatch — {flush.BiometricErrors.FirstOrDefault() ?? "upload rejected"}",
                                ToastLength.Long)?.Show());
                    }
                    catch { }
                });
            };

            dialog.Show();
        }

        // scanCleanup: for scan-triggered callers — on a cancelled (not completed) close, take
        // the provisionally added scan (and its adult count, if the scan added one) back out
        // of the box, so a scanned tag only stays in the observation on form completion. It's
        // structured (not an opaque Action) so it survives a process-kill + restore.
        // restore repopulates the form after an Android process-kill mid-chipping.
        private void ShowNewBirdDialog(string shortId, string fullPitId, (string box, bool decrementAdult)? scanCleanup = null, PendingChipState? restore = null)
        {
            SetDialogActive(true);
            bool completed = false;
            Action? onCancel = scanCleanup.HasValue
                ? () => RemoveUnsavedScanFromBox(scanCleanup.Value.box, fullPitId, scanCleanup.Value.decrementAdult)
                : (Action?)null;
            // Predict the next penguin number for the title (server assigns the real one on
            // create). Trailing digits handle both bare PT numbers ("1012") and prefixed
            // display forms ("NI7"); the prefix of the highest bird carries into the prediction.
            string nextPengLabel = "";
            if (_remotePenguinData != null && _remotePenguinData.Count > 0)
            {
                int maxNum = 0; string maxPrefix = "";
                foreach (var pd0 in _remotePenguinData.Values)
                {
                    var m = Regex.Match(pd0.PengNum ?? "", @"^(.*?)(\d+)$");
                    if (m.Success && int.TryParse(m.Groups[2].Value, out var n) && n > maxNum)
                    {
                        maxNum = n; maxPrefix = m.Groups[1].Value;
                    }
                }
                // Birds queued offline hold their predicted numbers too, so a restart can't
                // double-book a number that's already promised to a queued bird.
                foreach (var qc in _dataStorageService.LoadQueuedChips(this))
                {
                    var qm = Regex.Match(qc.RequestedPengNum ?? "", @"^(.*?)(\d+)$");
                    if (qm.Success && int.TryParse(qm.Groups[2].Value, out var qn) && qn > maxNum)
                    {
                        maxNum = qn; maxPrefix = qm.Groups[1].Value;
                    }
                }
                if (maxNum > 0) nextPengLabel = DisplayPengNum($"{maxPrefix}{maxNum + 1}");
            }
            var scrollView = new ScrollView(this);
            scrollView.SetClipChildren(false);
            scrollView.DescendantFocusability = Android.Views.DescendantFocusability.AfterDescendants;
            var card = _uiFactory.CreateCard();
            card.SetPadding(16, 16, 16, 16);
            scrollView.AddView(card);

            EditText createInput(string hint, Android.Text.InputTypes inputType = Android.Text.InputTypes.ClassText)
            {
                var input = new EditText(this) { InputType = inputType, Hint = hint, TextSize = 14, Focusable = true, FocusableInTouchMode = true };
                input.SetTextColor(UIFactory.TEXT_PRIMARY);
                input.SetHintTextColor(UIFactory.TEXT_SECONDARY);
                input.SetPadding(16, 12, 16, 12);
                input.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
                var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                p.SetMargins(0, 4, 0, 12);
                input.LayoutParameters = p;
                return input;
            }

            TextView createLabel(string text)
            {
                var lbl = new TextView(this) { Text = text, TextSize = 14 };
                lbl.SetTextColor(UIFactory.TEXT_PRIMARY);
                lbl.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                return lbl;
            }

            // Impossible to miss for the scribe: the scanner hit a tag the colony has never seen.
            // Confirming here starts a chipping record, so make "this is a new/unknown bird" shout.
            var newBirdBanner = new TextView(this) { Text = "🆕 UNKNOWN TAG — NEW BIRD", TextSize = 22 };
            newBirdBanner.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);
            newBirdBanner.SetTextColor(Color.White);
            newBirdBanner.SetBackgroundColor(UIFactory.DANGER_RED);
            newBirdBanner.Gravity = GravityFlags.Center;
            newBirdBanner.SetPadding(16, 14, 16, 14);
            var bannerLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            bannerLp.SetMargins(0, 0, 0, 12);
            newBirdBanner.LayoutParameters = bannerLp;
            card.AddView(newBirdBanner);

            // PIT ID (read-only)
            var pitInfo = new TextView(this) { Text = $"PIT ID: {fullPitId}", TextSize = 13 };
            pitInfo.SetTextColor(Color.DarkGray);
            pitInfo.SetPadding(0, 0, 0, 12);
            card.AddView(pitInfo);

            // New penguin / Rechip mode — a rechip attaches this PIT as a NEW chip on an
            // existing bird and retires the bird's old chip (mirrors the wildwatch flow).
            PenguinData? rechipTarget = null;
            string? rechipOldPit = null;
            var modeNew = new RadioButton(this) { Text = "New penguin" };
            modeNew.SetTextColor(Color.Black);
            var modeRechip = new RadioButton(this) { Text = "Rechip" };
            modeRechip.SetTextColor(Color.Black);
            var modeGroup = new RadioGroup(this) { Orientation = Android.Widget.Orientation.Horizontal };
            modeGroup.AddView(modeNew);
            modeGroup.AddView(modeRechip);
            // The rechip search (peng # or chip id, like the wildwatch penguin search) sits
            // right beside the Rechip radio on the same row, shown only in rechip mode.
            var rechipSearchInput = createInput("Peng # or chip id");
            var rechipSearchParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            rechipSearchParams.SetMargins(8, 0, 0, 0);
            rechipSearchInput.LayoutParameters = rechipSearchParams;
            rechipSearchInput.Visibility = ViewStates.Gone;
            modeGroup.AddView(rechipSearchInput);
            // Once a bird is selected the search swaps for its mini view + an ✕ to deselect
            var rechipSelectedRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            rechipSelectedRow.SetGravity(GravityFlags.CenterVertical);
            rechipSelectedRow.LayoutParameters = rechipSearchParams;
            rechipSelectedRow.Visibility = ViewStates.Gone;
            modeGroup.AddView(rechipSelectedRow);
            modeNew.Checked = true; // checked AFTER joining the group so exclusivity tracks it
            card.AddView(modeGroup);

            var rechipResults = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            card.AddView(rechipResults);

            // Chipped as (adult/chick) + Sex on one row to keep the form short
            var chippedSexRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            var chippedCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            chippedCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            chippedCol.AddView(createLabel("Chipped as"));
            var chippedAsAdult = new RadioButton(this) { Text = "Adult" };
            chippedAsAdult.SetTextColor(Color.Black);
            var chippedAsChick = new RadioButton(this) { Text = "Chick" };
            chippedAsChick.SetTextColor(Color.Black);
            var chippedGroup = new RadioGroup(this) { Orientation = Android.Widget.Orientation.Horizontal };
            chippedGroup.AddView(chippedAsAdult);
            chippedGroup.AddView(chippedAsChick);
            chippedAsAdult.Checked = true;
            chippedCol.AddView(chippedGroup);
            chippedSexRow.AddView(chippedCol);

            // Right column swaps with the Adult/Chick toggle: Sex spinner for adults,
            // a matching Chick-size spinner for chicks.
            var sexCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var sexColParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            sexColParams.SetMargins(12, 0, 0, 0);
            sexCol.LayoutParameters = sexColParams;
            var spinnerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            spinnerParams.SetMargins(0, 4, 0, 4);

            var sexLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            sexLayout.AddView(createLabel("Sex"));
            var sexSpinner = _uiFactory.CreateSpinner(ObservedSexOptions.Select(o => o.label).ToList());
            sexSpinner.LayoutParameters = spinnerParams;
            sexLayout.AddView(sexSpinner);
            sexCol.AddView(sexLayout);

            var chickSizeLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            chickSizeLayout.Visibility = ViewStates.Gone;
            chickSizeLayout.AddView(createLabel("Chick size"));
            var chickSizeSpinner = _uiFactory.CreateSpinner(new List<string> { "Unknown", "Single Chick (SC)", "Big Chick (BC)", "Little Chick (LC)" });
            chickSizeSpinner.LayoutParameters = spinnerParams;
            chickSizeLayout.AddView(chickSizeSpinner);
            sexCol.AddView(chickSizeLayout);

            chippedSexRow.AddView(sexCol);
            card.AddView(chippedSexRow);

            chippedAsChick.CheckedChange += (s, e) =>
            {
                // Chick → chick-size spinner; Adult → sex spinner (same spot, one at a time)
                chickSizeLayout.Visibility = chippedAsChick.Checked ? ViewStates.Visible : ViewStates.Gone;
                sexLayout.Visibility = chippedAsChick.Checked ? ViewStates.Gone : ViewStates.Visible;
            };

            // Chipper + assistant are people from the user table (same active-user list the day note
            // picks from), not free text — so a chip record links to the person, and a rename in the
            // web admin reaches every chip they made. ChipBy (the acronym) is gone from the UI.
            var chipUsers = DataStorageService.LoadUsers(this);
            // Only a chipper (a user with a falcon/permit id) may be recorded as the chipper; the
            // assistant can be anyone. Needs sync.php to send falcon_id — until it does, this list
            // is empty and the chipper picker has only the blank option.
            var chipperUsers = chipUsers.Where(u => !string.IsNullOrWhiteSpace(u.falcon_id)).ToList();
            Spinner MakeChipPersonSpinner(List<DataStorageService.SyncUser> src, int selectedId)
            {
                // Index 0 is the unset option — blank; empty reads as "not set".
                var labels = new List<string> { "" };
                labels.AddRange(src.Select(u => u.name ?? ""));
                var sp = _uiFactory.CreateDropdownSpinner();
                // SimpleSpinnerItem closed view (no radio); dropdown list uses the checked item.
                var ad = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, labels);
                ad.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
                sp.Adapter = ad;
                var idx = src.FindIndex(u => u.id == selectedId);
                sp.SetSelection(idx >= 0 ? idx + 1 : 0);
                return sp;
            }
            // Position 0 is the empty label, so a real selection maps to src[pos - 1].
            int SelectedChipUserId(Spinner s, List<DataStorageService.SyncUser> src) =>
                s.SelectedItemPosition > 0 && s.SelectedItemPosition <= src.Count ? src[s.SelectedItemPosition - 1].id : 0;

            // Chip box — SELECT a known nest (no free typing). The current nest shows full-width;
            // tapping it opens the list below to pick another, keeping the current value until you
            // actually choose a different one — no destructive ✕/clear.
            var chipBoxCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            chipBoxCol.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            chipBoxCol.AddView(createLabel("Chip box"));

            // All real nests for this colony (drop the "fake" placeholder an empty box-set leaves).
            var nestNames = _boxNamesAndIndexes?.Keys.Where(k => k != "fake").OrderBy(k => k).ToList()
                            ?? new List<string>();
            string selectedNest = "";
            bool suppressNestSearch = false;

            // Full-width current-nest field; tap to change.
            var nestSelectedView = new TextView(this) { Text = "Tap to choose nest", TextSize = 15 };
            nestSelectedView.SetTextColor(Color.Black);
            nestSelectedView.SetPadding(16, 16, 16, 16);
            nestSelectedView.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            nestSelectedView.Clickable = true;
            nestSelectedView.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            chipBoxCol.AddView(nestSelectedView);

            var nestSearchInput = createInput("Search nest");
            nestSearchInput.Visibility = ViewStates.Gone;
            nestSearchInput.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            chipBoxCol.AddView(nestSearchInput);
            var nestResults = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            chipBoxCol.AddView(nestResults);
            card.AddView(chipBoxCol);

            void RenderNestResults(string q)
            {
                nestResults.RemoveAllViews();
                var qU = (q ?? "").Trim().ToUpper();
                var matches = string.IsNullOrEmpty(qU)
                    ? nestNames.AsEnumerable()
                    : nestNames.Where(k => k.ToUpper().Contains(qU)).OrderBy(k => k.ToUpper().StartsWith(qU) ? 0 : 1).ThenBy(k => k);
                foreach (var m in matches.Take(12))
                {
                    var row = new TextView(this) { Text = m, TextSize = 15 };
                    row.SetTextColor(UIFactory.TEXT_PRIMARY);
                    row.SetPadding(16, 12, 16, 12);
                    row.Clickable = true;
                    row.Click += (s2, e2) => SelectNest(m);
                    nestResults.AddView(row);
                }
            }
            void SelectNest(string nest)
            {
                selectedNest = nest;
                nestSelectedView.Text = nest;
                nestSelectedView.Visibility = ViewStates.Visible;
                nestSearchInput.Visibility = ViewStates.Gone;
                suppressNestSearch = true; nestSearchInput.Text = ""; suppressNestSearch = false;
                nestResults.RemoveAllViews();
                var imm = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
                imm?.HideSoftInputFromWindow(nestSearchInput.WindowToken, 0);
            }
            // Tapping the current nest opens the picker below WITHOUT losing the current value.
            nestSelectedView.Click += (s, e) =>
            {
                nestSearchInput.Visibility = ViewStates.Visible;
                nestSearchInput.RequestFocus();
                RenderNestResults("");   // show all nests to choose from
                // Pop the keyboard — posted because the field has only just become visible.
                var imm = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
                new Handler(Looper.MainLooper).Post(() =>
                    imm?.ShowSoftInput(nestSearchInput, Android.Views.InputMethods.ShowFlags.Implicit));
            };
            nestSearchInput.TextChanged += (s, e) => { if (!suppressNestSearch) RenderNestResults(nestSearchInput.Text); };

            // Pre-select the box being worked on, if it's a real nest.
            var currentNestKey = nestNames.FirstOrDefault(k => string.Equals(k, _currentBoxName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (currentNestKey != null) SelectNest(currentNestKey);
            // No nest list at all (unconfigured colony) — fall back to a plain box-name field so
            // chipping isn't blocked; there's nothing to pick from.
            if (nestNames.Count == 0)
            {
                nestSelectedView.Visibility = ViewStates.Gone;
                nestSearchInput.Visibility = ViewStates.Visible;
                nestSearchInput.Hint = "Box name";
                nestSearchInput.Text = _currentBoxName;
            }
            // The chip box value used on save — the selected nest, or the free-text fallback when
            // the colony has no nest list to pick from.
            string ChipBoxValue() => nestNames.Count == 0 ? (nestSearchInput.Text?.Trim() ?? "") : selectedNest;

            // Chipper below the chip box, then Assistant — one person per row
            var chipperCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var chipperLabel = createLabel("Chipper");
            chipperCol.AddView(chipperLabel);
            // Starts unset — the chipper is chosen explicitly, not assumed to be whoever is logged in.
            // Only chippers (users with a falcon id) appear here.
            var chipperSpinner = MakeChipPersonSpinner(chipperUsers, 0);
            // Picking a chipper clears the red "required" highlight raised by a failed save.
            chipperSpinner.ItemSelected += (s, e) => { if (e.Position > 0) chipperLabel.SetTextColor(UIFactory.TEXT_PRIMARY); };
            chipperCol.AddView(chipperSpinner);
            if (chipperUsers.Count == 0)
            {
                var noChippers = new TextView(this) { Text = "No chippers synced", TextSize = 12 };
                noChippers.SetTextColor(UIFactory.TEXT_SECONDARY);
                chipperCol.AddView(noChippers);
            }
            card.AddView(chipperCol);

            var assistantCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            assistantCol.AddView(createLabel("Assistant"));
            var assistantSpinner = MakeChipPersonSpinner(chipUsers, 0);
            assistantCol.AddView(assistantSpinner);
            card.AddView(assistantCol);
            if (chipUsers.Count == 0)
            {
                var noUsers = new TextView(this) { Text = "Sync to load people", TextSize = 12 };
                noUsers.SetTextColor(UIFactory.TEXT_SECONDARY);
                card.AddView(noUsers);
            }

            // --- Biometric data ---
            var bioHeader = new TextView(this) { Text = "Biometric Data (optional)", TextSize = 15 };
            bioHeader.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            bioHeader.SetTextColor(UIFactory.TEXT_PRIMARY);
            bioHeader.SetPadding(0, 16, 0, 4);
            card.AddView(bioHeader);

            // Weight + Flipper on one row
            var bioRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            var weightCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            weightCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            weightCol.AddView(createLabel("Weight (g)"));
            var weightInput = createInput("", Android.Text.InputTypes.ClassNumber | Android.Text.InputTypes.NumberFlagDecimal);
            weightCol.AddView(weightInput);
            bioRow.AddView(weightCol);

            var flipperCol = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var flipperColParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            flipperColParams.SetMargins(12, 0, 0, 0);
            flipperCol.LayoutParameters = flipperColParams;
            flipperCol.AddView(createLabel("Flipper (mm)"));
            var flipperInput = createInput("", Android.Text.InputTypes.ClassNumber | Android.Text.InputTypes.NumberFlagDecimal);
            flipperCol.AddView(flipperInput);
            bioRow.AddView(flipperCol);
            card.AddView(bioRow);

            card.AddView(createLabel("Notes"));
            var notesInput = createInput("Notes",
                Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences);
            card.AddView(notesInput);

            var dialog = new AlertDialog.Builder(this)
                .SetTitle(string.IsNullOrEmpty(nextPengLabel) ? $"New bird: {shortId}" : $"New bird {nextPengLabel}")
                .SetView(scrollView)
                .SetPositiveButton("Save chip", (EventHandler<DialogClickEventArgs>)null!)
                .SetNegativeButton("Cancel", (s, e) => { SetDialogActive(false); })
                .Create();

            dialog.Show();
            // Slightly wider than the default dialog — the two-column rows need the space
            dialog.Window?.SetLayout((int)((Resources?.DisplayMetrics?.WidthPixels ?? 1080) * 0.98), ViewGroup.LayoutParams.WrapContent);
            var addButton = dialog.GetButton((int)DialogButtonType.Positive);

            // A real chip scanned while this dialog holds the demo placeholder replaces it,
            // so the bird gets its true chip (and the placeholder is never sent).
            _newBirdScanCapture = scanned =>
            {
                if (!IsPlaceholderPit(fullPitId)) return;
                var scanKey = scanned.ToUpper();
                var scanShort = scanKey.Length >= 8 ? scanKey.Substring(scanKey.Length - 8) : scanKey;
                if (_remotePenguinData != null &&
                    (_remotePenguinData.TryGetValue(scanKey, out var owner) || _remotePenguinData.TryGetValue(scanShort, out owner)))
                {
                    Toast.MakeText(this, $"Chip already on {DisplayPengNum(owner?.PengNum)} — not captured", ToastLength.Long)?.Show();
                    return;
                }
                fullPitId = scanKey;
                shortId = scanShort;
                pitInfo.Text = $"PIT ID: {fullPitId}";
                if (!modeRechip.Checked && string.IsNullOrEmpty(nextPengLabel))
                    dialog.SetTitle($"New bird: {shortId}");
                Toast.MakeText(this, $"Chip {scanShort} captured ✓", ToastLength.Short)?.Show();
            };
            dialog.DismissEvent += (s, e) =>
            {
                _newBirdScanCapture = null;
                if (!completed) onCancel?.Invoke();
            };

            // --- Rechip mode wiring ---
            bool suppressRechipSearch = false;
            void ClearRechipTarget()
            {
                rechipTarget = null; rechipOldPit = null;
                rechipSelectedRow.RemoveAllViews();
                rechipSelectedRow.Visibility = ViewStates.Gone;
                suppressRechipSearch = true; rechipSearchInput.Text = ""; suppressRechipSearch = false;
                rechipSearchInput.Visibility = modeRechip.Checked ? ViewStates.Visible : ViewStates.Gone;
                if (modeRechip.Checked) dialog.SetTitle("Rechip penguin");
            }
            void SelectRechipTarget(PenguinData pd, string key)
            {
                rechipTarget = pd;
                // Retire the bird's current chip. The cache carries the whole tag on the bird
                // itself, so take it from there; the key is only a fallback for an entry that
                // predates it. (It used to be found by looking for a 17-char key — the length of
                // the tag with the reader's prefix still on it, which no longer exists.)
                rechipOldPit = !string.IsNullOrEmpty(pd.FullPitId) ? pd.FullPitId
                    : IsFullPitKey(key) ? key
                    : _remotePenguinData?.FirstOrDefault(kv => kv.Value.PengNum == pd.PengNum && IsFullPitKey(kv.Key)).Key;
                rechipResults.RemoveAllViews();
                suppressRechipSearch = true; rechipSearchInput.Text = ""; suppressRechipSearch = false;
                // Swap the search for the selected bird's mini view + ✕
                rechipSearchInput.Visibility = ViewStates.Gone;
                rechipSelectedRow.RemoveAllViews();
                var selLabel = LookupPenguinLabel(key).label;
                if (!string.IsNullOrEmpty(pd.PengNum))
                    selLabel = selLabel.Replace($"#{pd.PengNum}", DisplayPengNum(pd.PengNum));
                var selBadge = CreateScanBadge(key, null, textSize: 14, labelOverride: selLabel);
                rechipSelectedRow.AddView(selBadge);
                var deselect = new TextView(this) { Text = "✕", TextSize = 20 };
                deselect.SetTextColor(UIFactory.DANGER_RED);
                deselect.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
                deselect.SetPadding(24, 4, 12, 4);
                deselect.Clickable = true;
                deselect.Click += (s, e) => ClearRechipTarget();
                rechipSelectedRow.AddView(deselect);
                rechipSelectedRow.Visibility = ViewStates.Visible;
                dialog.SetTitle($"Rechip penguin {DisplayPengNum(pd.PengNum)}");
                addButton.Text = "Save rechip";
            }
            void UpdateMode()
            {
                bool rechip = modeRechip.Checked;
                rechipSearchInput.Visibility = rechip && rechipTarget == null ? ViewStates.Visible : ViewStates.Gone;
                rechipSelectedRow.Visibility = rechip && rechipTarget != null ? ViewStates.Visible : ViewStates.Gone;
                chippedSexRow.Visibility = rechip ? ViewStates.Gone : ViewStates.Visible;
                if (rechip)
                {
                    dialog.SetTitle("Rechip penguin");
                    addButton.Text = "Save rechip";
                }
                else
                {
                    rechipTarget = null; rechipOldPit = null;
                    rechipSelectedRow.RemoveAllViews();
                    rechipResults.RemoveAllViews();
                    suppressRechipSearch = true; rechipSearchInput.Text = ""; suppressRechipSearch = false;
                    dialog.SetTitle(string.IsNullOrEmpty(nextPengLabel) ? $"New bird: {shortId}" : $"New bird {nextPengLabel}");
                    addButton.Text = "Save chip";
                }
            }
            // Belt and braces: enforce mutual exclusivity manually as well as via the group
            modeRechip.CheckedChange += (s, e) => { if (e.IsChecked && modeNew.Checked) modeNew.Checked = false; UpdateMode(); };
            modeNew.CheckedChange += (s, e) => { if (e.IsChecked && modeRechip.Checked) modeRechip.Checked = false; UpdateMode(); };
            rechipSearchInput.TextChanged += (s, e) =>
            {
                if (suppressRechipSearch) return;
                rechipTarget = null; rechipOldPit = null;
                rechipResults.RemoveAllViews();
                var q = (rechipSearchInput.Text ?? "").Trim().ToUpper().TrimStart('#');
                if (q.Length < 1 || _remotePenguinData == null) return;
                bool qIsNum = q.All(char.IsDigit);
                long qVal = qIsNum && long.TryParse(q, out var qv) ? qv : -1;
                // Distinct birds ranked: exact peng-number match first (a "2" puts PT2 and
                // NI2 on top), then number prefix, number contains, then chip-id contains.
                var best = new Dictionary<string, (int rank, long num, PenguinData pd, string key)>();
                foreach (var kv in _remotePenguinData)
                {
                    var pd2 = kv.Value;
                    var pn = pd2.PengNum ?? "";
                    if (pn == "") continue;
                    var pnU = pn.ToUpper();
                    var disp = DisplayPengNum(pn).ToUpper();
                    var digits = new string(pnU.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                    long.TryParse(digits, out var numVal);
                    int rank =
                        (qIsNum && numVal == qVal) || pnU == q || disp == q ? 0 :
                        pnU.StartsWith(q) || disp.StartsWith(q) ? 1 :
                        pnU.Contains(q) || disp.Contains(q) ? 2 :
                        q.Length >= 3 && kv.Key.ToUpper().Contains(q) ? 3 : -1;
                    if (rank < 0) continue;
                    if (best.TryGetValue(pn, out var cur))
                        best[pn] = (Math.Min(cur.rank, rank), numVal, pd2,
                            IsFullPitKey(cur.key) ? cur.key : (IsFullPitKey(kv.Key) ? kv.Key : cur.key));
                    else
                        best[pn] = (rank, numVal, pd2, kv.Key);
                }
                foreach (var mt in best.Values.OrderBy(v => v.rank).ThenBy(v => v.num).Take(6))
                {
                    var pdSel = mt.pd; var keySel = mt.key;
                    // Show the colony acronym instead of '#' so PT2 and NI2 are unambiguous
                    var lbl = LookupPenguinLabel(keySel).label;
                    if (!string.IsNullOrEmpty(pdSel.PengNum))
                        lbl = lbl.Replace($"#{pdSel.PengNum}", DisplayPengNum(pdSel.PengNum));
                    var resultBadge = CreateScanBadge(keySel, () => SelectRechipTarget(pdSel, keySel), textSize: 14, labelOverride: lbl);
                    var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    bp.SetMargins(0, 4, 0, 4);
                    resultBadge.LayoutParameters = bp;
                    rechipResults.AddView(resultBadge);
                }
            };

            // ---- Kill resilience: persist this workflow (PIT included) so an Android
            // process kill mid-bird reopens the form exactly where it was. Snapshot on
            // open and on OnPause; cleared on any intentional dismiss (save or cancel).
            void CapturePendingChip()
            {
                var sizeSel0 = chickSizeSpinner.SelectedItem?.ToString() ?? "";
                SavePendingChip(new PendingChipState
                {
                    FullPitId = fullPitId,
                    BoxName = _currentBoxName,
                    IsRechip = modeRechip.Checked,
                    RechipPengNum = rechipTarget?.PengNum ?? "",
                    IsChick = chippedAsChick.Checked,
                    ChickSizeCode = sizeSel0.Contains("(SC)") ? "SC" : sizeSel0.Contains("(BC)") ? "BC" : sizeSel0.Contains("(LC)") ? "LC" : "",
                    SexCode = ObservedSexOptions.FirstOrDefault(o => o.label == (sexSpinner.SelectedItem?.ToString() ?? "")).code ?? "",
                    ChipBox = ChipBoxValue(),
                    ChipperId = SelectedChipUserId(chipperSpinner, chipperUsers),
                    AssistantId = SelectedChipUserId(assistantSpinner, chipUsers),
                    Weight = weightInput.Text ?? "",
                    Flipper = flipperInput.Text ?? "",
                    Notes = notesInput.Text ?? "",
                    CreatedUtc = DateTime.UtcNow,
                    ScanCleanup = scanCleanup.HasValue,
                    ScanCleanupBox = scanCleanup?.box ?? "",
                    ScanCleanupDecrement = scanCleanup?.decrementAdult ?? false,
                });
            }
            _pendingChipCapture = CapturePendingChip;
            dialog.DismissEvent += (s, e) => { _pendingChipCapture = null; ClearPendingChip(); };

            // Restore a form snapshot after a process kill
            if (restore != null)
            {
                if (nestNames.Count == 0) nestSearchInput.Text = restore.ChipBox;
                else { var rk = nestNames.FirstOrDefault(k => string.Equals(k, restore.ChipBox?.Trim(), StringComparison.OrdinalIgnoreCase)); if (rk != null) SelectNest(rk); }
                if (restore.ChipperId > 0) { var ci = chipperUsers.FindIndex(u => u.id == restore.ChipperId); if (ci >= 0) chipperSpinner.SetSelection(ci + 1); }
                if (restore.AssistantId > 0) { var ai = chipUsers.FindIndex(u => u.id == restore.AssistantId); if (ai >= 0) assistantSpinner.SetSelection(ai + 1); }
                weightInput.Text = restore.Weight;
                flipperInput.Text = restore.Flipper;
                notesInput.Text = restore.Notes;
                if (restore.IsChick) chippedAsChick.Checked = true;
                var sexIdx = Array.FindIndex(ObservedSexOptions, o => o.code == restore.SexCode);
                if (sexIdx >= 0) sexSpinner.SetSelection(sexIdx);
                chickSizeSpinner.SetSelection(restore.ChickSizeCode == "SC" ? 1 : restore.ChickSizeCode == "BC" ? 2 : restore.ChickSizeCode == "LC" ? 3 : 0);
                if (restore.IsRechip)
                {
                    modeRechip.Checked = true;
                    if (!string.IsNullOrEmpty(restore.RechipPengNum) && _remotePenguinData != null)
                    {
                        var kv = _remotePenguinData.FirstOrDefault(k => k.Value.PengNum == restore.RechipPengNum && IsFullPitKey(k.Key));
                        if (kv.Value == null) kv = _remotePenguinData.FirstOrDefault(k => k.Value.PengNum == restore.RechipPengNum);
                        if (kv.Value != null) SelectRechipTarget(kv.Value, kv.Key);
                    }
                }
                Toast.MakeText(this, "Restored unfinished chipping form", ToastLength.Long)?.Show();
            }
            CapturePendingChip(); // the scanned PIT is safe from the moment the form opens

            // Offline path: bank the bird locally; the next sync creates it server-side
            // (the server honours the predicted number, or gives the next free one if taken).
            void QueueChipOffline(bool qIsChick, string qSex, string qChickSize)
            {
                var st = new PendingChipState
                {
                    FullPitId = fullPitId,
                    BoxName = _currentBoxName,
                    IsRechip = false,
                    IsChick = qIsChick,
                    ChickSizeCode = qChickSize,
                    SexCode = qSex,
                    ChipBox = ChipBoxValue(),
                    ChipperId = SelectedChipUserId(chipperSpinner, chipperUsers),
                    AssistantId = SelectedChipUserId(assistantSpinner, chipUsers),
                    Weight = weightInput.Text ?? "",
                    Flipper = flipperInput.Text ?? "",
                    Notes = notesInput.Text ?? "",
                    CreatedUtc = DateTime.UtcNow,
                    RequestedPengNum = nextPengLabel,
                };
                var queue = _dataStorageService.LoadQueuedChips(this);
                queue.Add(st);
                _dataStorageService.SaveQueuedChips(this, queue);

                // Provisional local record so badges and the next prediction see this bird
                if (_remotePenguinData != null && !string.IsNullOrEmpty(fullPitId))
                {
                    var pd = new PenguinData
                    {
                        ScannedId = shortId,
                        PengNum = nextPengLabel,
                        Sex = "",
                        LastKnownLifeStage = qIsChick ? LifeStage.Chick : LifeStage.Adult,
                        ChipDate = DateTime.UtcNow,
                        ChipAs = qIsChick ? "Chick" : "Adult",
                        ChickSizeCode = qChickSize,
                    };
                    _remotePenguinData[fullPitId] = pd;
                    _remotePenguinData[shortId] = pd;
                }

                // The bird is physically present — count it now
                if (qIsChick) BumpCount(_chicksEditText, 1);
                else BumpCount(_adultsEditText, 1);
                SaveCurrentBoxData();
                completed = true; // queued = the scanned tag stays in the box (onCancel must not remove it)
                dialog.Dismiss(); // also clears the pending-chip form file
                SetDialogActive(false);
                DrawPageLayouts();
                Toast.MakeText(this, $"Queued — will sync as {nextPengLabel} (next free number if taken)", ToastLength.Long)?.Show();
            }

            void DoAdd()
            {
                // Test/demo chip: save nothing at all — no penguin (so the number doesn't
                // increment), no chip, no counts. Purely a workflow rehearsal.
                if (IsPlaceholderPit(fullPitId))
                {
                    Toast.MakeText(this, "Test chip not saved", ToastLength.Short)?.Show();
                    dialog.Dismiss();
                    SetDialogActive(false);
                    return;
                }
                addButton.Enabled = false;
                bool isRechip = rechipTarget != null;
                addButton.Text = "Saving...";
                // Any failure (offline is the common one in the field) re-arms the Save button
                // so the user can retry from the same filled-in form; the pending-chip file
                // keeps everything safe if they leave and come back in coverage.
                void RestoreSaveButton() => RunOnUiThread(() =>
                {
                    addButton.Enabled = true;
                    addButton.Text = isRechip ? "Save rechip" : "Save chip";
                });
                var isChick = isRechip ? rechipTarget!.LastKnownLifeStage == LifeStage.Chick : chippedAsChick.Checked;
                var sexLabel = sexSpinner.SelectedItem?.ToString() ?? "";
                // Sex applies to new adults only — hidden for chicks and for rechips
                var sex = (isChick || isRechip) ? "" : ObservedSexOptions.FirstOrDefault(o => o.label == sexLabel).code;
                var chickSize = "";
                if (isChick && !isRechip)
                {
                    var sizeSel = chickSizeSpinner.SelectedItem?.ToString() ?? "";
                    chickSize = sizeSel.Contains("(SC)") ? "SC" : sizeSel.Contains("(BC)") ? "BC" : sizeSel.Contains("(LC)") ? "LC" : "";
                }
                // Read the person spinners here on the UI thread — the network POST below runs off it.
                var chipperId = SelectedChipUserId(chipperSpinner, chipperUsers);
                var assistantId = SelectedChipUserId(assistantSpinner, chipUsers);
                var chipBoxVal = ChipBoxValue();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var client = Http.CreateClient(TimeSpan.FromSeconds(15));
                        var token = _appSettings.AuthToken;
                        // New birds belong to the colony being worked — the server numbers
                        // them within it (e.g. NI7) and stamps penguins.colony_id.
                        var colonyId = _appSettings.SelectedColonyId;

                        var today = NzNow.ToString("yyyy-MM-dd");
                        string? pengNum;

                        if (!isRechip)
                        {
                            // Serialize births through the offline queue: drain any queued (unsynced)
                            // birds BEFORE minting this one's number online, so an online create can't
                            // take a number a queued bird already reserved locally — the single-phone
                            // self-collision that parked birds out of sequence. If the backlog won't
                            // drain (offline, or the server refused), queue this bird too rather than
                            // racing it; the next sync creates them all in order.
                            var backlogWarnings = await _dataStorageService.FlushQueuedChips(this, _appSettings);
                            void ShowBacklogNote(string title) => RunOnUiThread(() =>
                            {
                                if (backlogWarnings.Count == 0) return;
                                new AlertDialog.Builder(this)
                                    .SetTitle(title)
                                    .SetMessage(string.Join("\n\n", backlogWarnings))
                                    .SetPositiveButton("OK", (s5, e5) => { })
                                    .Show();
                            });
                            if (_dataStorageService.LoadQueuedChips(this).Count > 0)
                            {
                                RunOnUiThread(() =>
                                {
                                    ShowBacklogNote("Earlier chips not all synced");
                                    QueueChipOffline(isChick, sex, chickSize);
                                });
                                return;
                            }
                            ShowBacklogNote("Earlier chips synced");

                            // New bird: penguin + chip + biometrics land in ONE server transaction.
                            // Three separate creates used to be able to half-succeed — a drop after
                            // the penguin create left a chipless bird, and a retry made a second
                            // one. The server keys on pit_id, so this is safe to retry as-is.
                            var birdFields = new Dictionary<string, object>
                            {
                                ["pit_id"] = fullPitId,
                                ["chipped_as_adult"] = isChick ? 0 : 1,
                                ["chip_date"] = today,
                                ["observation_date"] = today,
                                ["chip_box"] = chipBoxVal,
                            };
                            // Ask the server to honour the number written down in the field (same as the
                            // offline path). The queue is drained above, so the server is authoritative;
                            // it hands back a different number only if this one is genuinely taken.
                            if (!string.IsNullOrEmpty(nextPengLabel)) birdFields["requested_peng_num"] = nextPengLabel;
                            if (chipperId > 0) birdFields["chipper_id"] = chipperId;
                            if (assistantId > 0) birdFields["assistant_id"] = assistantId;
                            if (!string.IsNullOrEmpty(chickSize)) birdFields["chick_size_code"] = chickSize;
                            if (!string.IsNullOrEmpty(weightInput.Text)) birdFields["weight"] = weightInput.Text;
                            if (!string.IsNullOrEmpty(flipperInput.Text)) birdFields["flipper_length"] = flipperInput.Text;
                            // sex is an observation (observed_sex), not truth on the penguin record
                            if (!string.IsNullOrEmpty(sex)) birdFields["observed_sex"] = sex;
                            if (!string.IsNullOrWhiteSpace(notesInput.Text)) birdFields["notes"] = notesInput.Text.Trim();

                            var birdReq = new HttpRequestMessage(HttpMethod.Post,
                                $"{DataStorageService.WILDWATCH_BASE_URL}/crud.php?action=create_chipped_bird&colony_id={colonyId}");
                            birdReq.Headers.Add("Authorization", $"Bearer {token}");
                            birdReq.Content = new StringContent(
                                JsonConvert.SerializeObject(birdFields), System.Text.Encoding.UTF8, "application/json");
                            var birdResp = await client.SendAsync(birdReq);
                            var birdJson = await birdResp.Content.ReadAsStringAsync();
                            var birdResult = JsonConvert.DeserializeObject<Dictionary<string, object>>(birdJson);

                            pengNum = birdResult?.ContainsKey("peng_num") == true ? birdResult["peng_num"]?.ToString() : null;
                            if (string.IsNullOrEmpty(pengNum))
                            {
                                RunOnUiThread(() =>
                                {
                                    new AlertDialog.Builder(this)
                                        .SetTitle("Failed to save bird")
                                        .SetMessage(DataStorageService.ServerMessage(birdJson, (int)birdResp.StatusCode) + "\n\nNothing was saved — safe to retry.")
                                        .SetPositiveButton("OK", (s2, e2) => { })
                                        .Show();
                                });
                                RestoreSaveButton();
                                return;
                            }
                            // The server assigned a different number than the field wrote down —
                            // it was already taken. Say so, so the paper and the database agree.
                            if (!string.IsNullOrEmpty(nextPengLabel) && pengNum != nextPengLabel)
                                RunOnUiThread(() =>
                                    new AlertDialog.Builder(this)
                                        .SetTitle("Number changed")
                                        .SetMessage($"Bird written down as {nextPengLabel} was saved as {pengNum} — that number was already taken. Rename on wildwatch if the written number matters.")
                                        .SetPositiveButton("OK", (s6, e6) => { })
                                        .Show());
                        }
                        else
                        {
                            pengNum = rechipTarget!.PengNum;

                            // 1. New chip for the existing bird (test placeholder PITs never reach
                            // here — DoAdd exits before saving anything)
                            var chipFields = new Dictionary<string, object>
                            {
                                ["peng_num"] = pengNum,
                                ["pit_id"] = fullPitId,
                                ["chip_date"] = today,
                                ["is_active"] = 1,
                                ["chip_box"] = chipBoxVal,
                            };
                            if (chipperId > 0) chipFields["chipper_id"] = chipperId;
                            if (assistantId > 0) chipFields["assistant_id"] = assistantId;
                            var chipReq = new HttpRequestMessage(HttpMethod.Post,
                                $"{DataStorageService.WILDWATCH_BASE_URL}/crud.php?action=create&table=penguin_chips&colony_id={colonyId}");
                            chipReq.Headers.Add("Authorization", $"Bearer {token}");
                            chipReq.Content = new StringContent(
                                JsonConvert.SerializeObject(chipFields), System.Text.Encoding.UTF8, "application/json");
                            var chipResp = await client.SendAsync(chipReq);
                            var chipJson = await chipResp.Content.ReadAsStringAsync();
                            var chipResult = JsonConvert.DeserializeObject<Dictionary<string, object>>(chipJson);
                            if (chipResult == null || chipResult.ContainsKey("error"))
                            {
                                RunOnUiThread(() =>
                                {
                                    new AlertDialog.Builder(this)
                                        .SetTitle("Failed to create chip")
                                        .SetMessage(chipJson)
                                        .SetPositiveButton("OK", (s3, e3) => { })
                                        .Show();
                                });
                                RestoreSaveButton();
                                return;
                            }

                            // 2. Retire the bird's previous chip (like the wildwatch flow)
                            if (!string.IsNullOrEmpty(rechipOldPit)
                                && !string.Equals(rechipOldPit, fullPitId, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var retireReq = new HttpRequestMessage(HttpMethod.Post,
                                        $"{DataStorageService.WILDWATCH_BASE_URL}/crud.php?action=update&table=penguin_chips&id={Uri.EscapeDataString(rechipOldPit)}&colony_id={colonyId}");
                                    retireReq.Headers.Add("Authorization", $"Bearer {token}");
                                    retireReq.Content = new StringContent(
                                        JsonConvert.SerializeObject(new Dictionary<string, object> { ["is_active"] = 0 }),
                                        System.Text.Encoding.UTF8, "application/json");
                                    await client.SendAsync(retireReq);
                                }
                                catch (Exception rex)
                                {
                                    RunOnUiThread(() => Toast.MakeText(this,
                                        $"New chip saved, but retiring old chip failed: {rex.Message}", ToastLength.Long)?.Show());
                                }
                            }

                            // 3. Biometrics, if any were entered
                            var bioFields = new Dictionary<string, object>();
                            if (!string.IsNullOrEmpty(weightInput.Text)) bioFields["weight"] = weightInput.Text;
                            if (!string.IsNullOrEmpty(flipperInput.Text)) bioFields["flipper_length"] = flipperInput.Text;
                            if (!string.IsNullOrWhiteSpace(notesInput.Text)) bioFields["notes"] = notesInput.Text.Trim();
                            if (bioFields.Count > 0)
                            {
                                bioFields["peng_num"] = pengNum;
                                bioFields["observation_date"] = today;
                                var bioReq = new HttpRequestMessage(HttpMethod.Post,
                                    $"{DataStorageService.WILDWATCH_BASE_URL}/crud.php?action=create&table=penguin_biometric_data&colony_id={colonyId}");
                                bioReq.Headers.Add("Authorization", $"Bearer {token}");
                                bioReq.Content = new StringContent(
                                    JsonConvert.SerializeObject(bioFields), System.Text.Encoding.UTF8, "application/json");
                                await client.SendAsync(bioReq);
                            }
                        }

                        // 4. Update local penguin data cache
                        if (_remotePenguinData != null)
                        {
                            if (isRechip)
                            {
                                // The bird keeps its identity — just teach the cache the new pit
                                _remotePenguinData[fullPitId] = rechipTarget!;
                                _remotePenguinData[shortId] = rechipTarget!;
                            }
                            else
                            {
                                var pd = new PenguinData
                                {
                                    ScannedId = shortId,
                                    PengNum = pengNum,
                                    Sex = "", // sex is unconfirmed until set on penguins table
                                    LastKnownLifeStage = isChick ? LifeStage.Chick : LifeStage.Adult,
                                    ChipDate = DateTime.UtcNow,
                                    ChipAs = isChick ? "Chick" : "Adult",
                                    ChickSizeCode = chickSize,
                                };
                                _remotePenguinData[fullPitId] = pd;
                                _remotePenguinData[shortId] = pd;
                            }
                        }

                        RunOnUiThread(() =>
                        {
                            var verb = isRechip ? "rechipped" : "added";
                            completed = true; // the tag may now stay in the box
                            dialog.Dismiss();
                            SetDialogActive(false);

                            // A "No scan" in this box is an adult already counted but unidentified —
                            // very often the bird just chipped. Decide swap-or-add BEFORE touching the
                            // counts so the adult total only ever moves once, to its final value.
                            var replaceBox = _colonyState.GetTodayForBox(_currentBoxName);
                            var noScanEntry = isChick
                                ? null : replaceBox?.ScannedIds.FirstOrDefault(s2 => s2.BirdId.StartsWith("NOSCAN_"));

                            // The held-scans flush pre-counts each flushed bird as an adult before the
                            // new-bird form opens; scanCleanup.decrementAdult marks that this bird is
                            // ALREADY in the adult count. Completion must not add it a second time (and,
                            // if it turns out to be a chick, must move it out of Adults into Chicks).
                            bool preCountedAsAdult = scanCleanup?.decrementAdult == true;

                            void AddAsNewBird()
                            {
                                if (isChick)
                                {
                                    BumpCount(_chicksEditText, 1);
                                    if (preCountedAsAdult) BumpCount(_adultsEditText, -1);
                                }
                                else if (!preCountedAsAdult)
                                {
                                    BumpCount(_adultsEditText, 1);
                                }
                                SaveCurrentBoxData();
                                DrawPageLayouts();
                                Toast.MakeText(this, $"#{pengNum} {verb} ({(isChick ? "+1 Chick" : "+1 Adult")})", ToastLength.Short)?.Show();
                            }

                            if (noScanEntry != null)
                            {
                                new AlertDialog.Builder(this)
                                    .SetTitle("Replace no-scan?")
                                    .SetMessage($"#{pengNum} was {verb}. Is this the no-scan adult already recorded in box {_currentBoxName}?\n\nReplace it — the adult count stays as it is.")
                                    .SetPositiveButton("Yes, replace", (s3, e3) =>
                                    {
                                        // Swap, don't add: the no-scan already counted this bird as an adult.
                                        replaceBox!.ScannedIds.Remove(noScanEntry);
                                        // If the held-scans flush ALSO counted this bird as an adult, the
                                        // no-scan and the flushed scan are one physical bird counted twice —
                                        // drop the duplicate so the adult total lands on its true value.
                                        if (preCountedAsAdult)
                                        {
                                            replaceBox.Adults = Math.Max(0, replaceBox.Adults - 1);
                                            if (_adultsEditText?[0] != null) _adultsEditText[0].Text = replaceBox.Adults.ToString();
                                        }
                                        // The counts otherwise don't move, so SaveCurrentBoxData would see
                                        // nothing changed — persist the scan-list edit explicitly.
                                        replaceBox.WhenDataCollectedUtc = DateTime.UtcNow;
                                        _colonyState.SaveBoxObservation(_currentBoxName, replaceBox);
                                        SaveToAppDataDir();
                                        DrawPageLayouts();
                                        Toast.MakeText(this, $"#{pengNum} {verb} — replaced the no-scan in box {_currentBoxName}", ToastLength.Short)?.Show();
                                    })
                                    .SetNegativeButton("No, another bird", (s3, e3) => AddAsNewBird())
                                    .SetCancelable(false)   // one of the two must be applied — there is no sane default
                                    .Show();
                            }
                            else
                            {
                                AddAsNewBird();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUiThread(() =>
                            {
                                if (isRechip)
                                {
                                    Toast.MakeText(this, $"Failed: {ex.Message} — form kept, retry when back in coverage", ToastLength.Long)?.Show();
                                }
                                else
                                {
                                    // Offline new bird: offer to queue it for the next sync
                                    var predicted = string.IsNullOrEmpty(nextPengLabel) ? "the next number" : nextPengLabel;
                                    new AlertDialog.Builder(this)
                                        .SetTitle("No connection")
                                        .SetMessage($"Couldn't reach the server ({ex.Message}).\n\nQueue this bird to sync later? It will sync as {predicted} — or the next free number if {predicted} is taken by the time it syncs (renamable on wildwatch).")
                                        .SetPositiveButton("Queue for sync", (s4, e4) => QueueChipOffline(isChick, sex, chickSize))
                                        .SetNegativeButton("Keep editing", (s4, e4) => { })
                                        .Show();
                                }
                            });
                        RestoreSaveButton();
                    }
                });
            }

            addButton.Click += (s, e) =>
            {
                if (modeRechip.Checked && rechipTarget == null)
                {
                    Toast.MakeText(this, "Search and select a penguin to rechip first", ToastLength.Short)?.Show();
                    return;
                }
                // A nest must be selected (or typed, in the no-nest-list fallback) before saving.
                if (string.IsNullOrEmpty(ChipBoxValue()))
                {
                    Toast.MakeText(this, "Select a nest for the chip box", ToastLength.Short)?.Show();
                    return;
                }
                // A chip must be attributed to a chipper — no save without one selected. Flag the
                // Chipper label red so it's obvious what's missing.
                if (SelectedChipUserId(chipperSpinner, chipperUsers) == 0)
                {
                    chipperLabel.SetTextColor(UIFactory.DANGER_RED);
                    Toast.MakeText(this, chipperUsers.Count == 0 ? "No chippers — Sync to load them" : "Select chipper", ToastLength.Short)?.Show();
                    return;
                }
                // Confirmation screen listing everything that will be saved, worded the way
                // the user entered it. "No" just closes this dialog — the input form
                // underneath stays open with its values intact for editing or Cancel.
                bool isTestChip = IsPlaceholderPit(fullPitId);
                var summary = new List<string>();
                summary.Add(rechipTarget != null ? $"Rechip {DisplayPengNum(rechipTarget.PengNum)}"
                                                 : $"New penguin ({(chippedAsChick.Checked ? "chick" : "adult")})");
                // The predicted number comes from the local bird list (synced every sync) —
                // display-only; the server still assigns the real number on create.
                if (rechipTarget == null && !isTestChip)
                    summary.Add(string.IsNullOrEmpty(nextPengLabel) ? "Bird #: unavailable" : $"Bird #: {nextPengLabel}");
                summary.Add($"PIT: {fullPitId}" + (isTestChip ? " (test chip — nothing will be saved)" : ""));
                if (rechipTarget == null)
                {
                    if (chippedAsChick.Checked)
                        summary.Add($"Chick size: {chickSizeSpinner.SelectedItem?.ToString() ?? "Unknown"}");
                    else
                    {
                        var sexSel = sexSpinner.SelectedItem?.ToString();
                        summary.Add($"Sex: {(string.IsNullOrEmpty(sexSel) ? "Not recorded" : sexSel)}");
                    }
                }
                summary.Add($"Chip box: {ChipBoxValue()}");
                summary.Add($"Chipper: {(chipperSpinner.SelectedItemPosition > 0 ? chipperSpinner.SelectedItem?.ToString() : "Not recorded")}");
                if (assistantSpinner.SelectedItemPosition > 0)
                    summary.Add($"Assistant: {assistantSpinner.SelectedItem?.ToString()}");
                if (!string.IsNullOrWhiteSpace(weightInput.Text)) summary.Add($"Weight: {weightInput.Text} g");
                if (!string.IsNullOrWhiteSpace(flipperInput.Text)) summary.Add($"Flipper: {flipperInput.Text} mm");
                if (!string.IsNullOrWhiteSpace(notesInput.Text)) summary.Add($"Notes: {notesInput.Text}");

                new AlertDialog.Builder(this)
                    .SetTitle(rechipTarget != null ? "Confirm rechip" : "Confirm new penguin")
                    .SetMessage(string.Join("\n", summary))
                    .SetPositiveButton("Yes, save", (s2, e2) => DoAdd())
                    .SetNegativeButton("No", (s2, e2) => { })
                    .Show();
            };
        }

        private void OnDeleteScanClick(ScanRecord scanToDelete)
        {
            bool isNoScan = scanToDelete.BirdId.StartsWith("NOSCAN_");
            ShowConfirmationDialog(
                isNoScan ? "Delete No-Scan" : "Delete Bird Scan",
                isNoScan ? "Delete this 'No scan' adult? The adult count will go down by 1."
                         : $"Are you sure you want to delete the scan for bird {scanToDelete.BirdId}?",
                ("Yes, Delete", () =>
                {
                    if ((_colonyState.GetTodayForBox(_currentBoxName) != null))
                    {
                        var boxData = _colonyState.GetTodayForBox(_currentBoxName);
                        var scanToRemove = boxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToDelete.BirdId &&
                            s.Timestamp == scanToDelete.Timestamp);

                        if (scanToRemove != null)
                        {
                            boxData.ScannedIds.Remove(scanToRemove);
                            if (isNoScan)
                            {
                                // Mirror the +1 Adult applied when the no-scan was added
                                BumpCount(_adultsEditText, -1);
                            }
                            else if (_remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData) && (
                                LifeStage.Adult == penguinData.LastKnownLifeStage ||
                                LifeStage.Returnee == penguinData.LastKnownLifeStage ||
                                NzNow > penguinData.ChipDate.AddMonths(3)))
                            {
                                BumpCount(_adultsEditText, -1);
                            }
                            else if (_remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinChick) && LifeStage.Chick == penguinChick.LastKnownLifeStage)
                            {
                                BumpCount(_chicksEditText, -1);
                            }
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(boxData.ScannedIds);
                            Toast.MakeText(this, isNoScan ? $"🗑️ No-scan adult removed from Box {_currentBoxName} (-1 adult)"
                                                          : $"🗑️ Bird {scanToDelete.BirdId} deleted from Box {_currentBoxName}", ToastLength.Short)?.Show();
                            DrawPageLayouts();
                        }
                    }
                }
            ),
                ("Cancel", () => { }
            )
            );
        }
        private void OnMoveScanClick(ScanRecord scanToMove)
        {
            ShowMoveDialog(scanToMove);
        }
        private void ShowMoveDialog(ScanRecord scanToMove)
        {
            var input = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText,
                Hint = $"Enter box name in scope: " + _appSettings.BoxSetString
            };
            input.SetTextColor(UIFactory.TEXT_PRIMARY);

            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle($"Move Bird {scanToMove.BirdId}")
                .SetMessage($"Move from Box { _currentBoxName} to:")
                .SetView(input)
                .SetPositiveButton("Move", (s, e) =>
                {
                    string targetBoxName = input.Text?.Trim() ?? "";
                    if (_boxNamesAndIndexes.ContainsKey(targetBoxName))
                    {
                        if (targetBoxName == _currentBoxName)
                        {
                            Toast.MakeText(this, "Bird is already in this box", ToastLength.Short)?.Show();
                        }
                        else
                        {
                            MoveScanToBox(scanToMove, targetBoxName);
                        }
                    }
                    else
                    {
                        Toast.MakeText(this, $"Box name must be in scope {_appSettings.BoxSetString}", ToastLength.Short)?.Show();
                    }
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            alertDialog?.Show();
            
            input.RequestFocus();
            var inputMethodManager = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
            inputMethodManager?.ShowSoftInput(input, Android.Views.InputMethods.ShowFlags.Implicit);
        }
        private void MoveScanToBox(ScanRecord scanToMove, string targetBoxName)
        {
            ShowConfirmationDialog(
                "Move Bird Scan",
                $"Move bird {scanToMove.BirdId} from Box {_currentBoxName} to Box {targetBoxName}?",
                ("Yes, Move", (Action)(() =>
                {
                    // Remove from current box
                    if ((_colonyState.GetTodayForBox(_currentBoxName) != null))
                    {
                        var currentBoxData = _colonyState.GetTodayForBox(_currentBoxName);
                        var scanToRemove = currentBoxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToMove.BirdId &&
                            s.Timestamp == scanToMove.Timestamp);

                        var targetBoxData = _colonyState.GetTodayForBox(targetBoxName);
                        if (targetBoxData != null && targetBoxData.ScannedIds.Any(s => s.BirdId == scanToMove.BirdId))
                        {
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} exists already in Box {targetBoxName}", ToastLength.Long)?.Show();
                        }
                        else if (scanToRemove != null)
                        {
                            currentBoxData.ScannedIds.Remove(scanToRemove);
                            _colonyState.SaveBoxObservation(_currentBoxName, currentBoxData);

                            // Add to target box
                            if (targetBoxData == null)
                                targetBoxData = new BoxObservation { BoxName = targetBoxName };

                            targetBoxData.ScannedIds.Add(scanToMove);

                            if (_remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData))
                            {
                                if (LifeStage.Adult == penguinData.LastKnownLifeStage || LifeStage.Returnee == penguinData.LastKnownLifeStage || NzNow > penguinData.ChipDate.AddMonths(3))
                                {
                                    BumpCount(_adultsEditText, -1);
                                    targetBoxData.Adults++;
                                }
                                else if (LifeStage.Chick == penguinData.LastKnownLifeStage)
                                {
                                    BumpCount(_chicksEditText, -1);
                                    targetBoxData.Chicks++;
                                }
                            }
                            targetBoxData.IsPendingUpload = true;
                            targetBoxData.PendingUploadSinceUtc ??= DateTime.UtcNow;
                            _colonyState.SaveBoxObservation(targetBoxName, targetBoxData);
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(currentBoxData.ScannedIds);
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} moved from Box {_currentBoxName} to Box {targetBoxName}", ToastLength.Long)?.Show();
                            DrawPageLayouts();
                        }
                    }
                })),
                ("Cancel", () => { })
            );
        }
        private void HandleIncomingJsonIntent()
        {
            try
            {
                if (Intent?.Action != Android.Content.Intent.ActionView || Intent.Data == null) return;
                if (Intent.Data.Scheme == "nestcheck") return; // Handled by HandleAuthDeepLink

                var uri = Intent.Data;
                using var stream = ContentResolver?.OpenInputStream(uri);
                if (stream == null) return;

                using var reader = new System.IO.StreamReader(stream);
                var json = reader.ReadToEnd();
                var filename = uri.LastPathSegment ?? "";

                // Clear intent so it doesn't re-trigger on activity recreate
                Intent.SetData(null);
                Intent.SetAction(null);

                LoadJsonData(json, filename);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Failed to open JSON: {ex.Message}", ToastLength.Long)?.Show();
            }
        }

        private async void LoadFromAppDataDir()
        {
            try
            {
                var internalPath = this.FilesDir?.AbsolutePath;
                if (string.IsNullOrEmpty(internalPath))
                    throw new Exception();
                _appSettings = DataStorageService.loadAppSettingsFromDir(internalPath);
                _appSettings.PropertyChanged += (s, e) => DataStorageService.saveApplicationSettings(_appSettings);

                // Initialize BoxTags API if configured. Auth is the logged-in user's
                // session token (read live via the provider), not the legacy API key.
                if (!string.IsNullOrWhiteSpace(_appSettings.BoxTagsApiUrl))
                {
                    BoxTagService.InitializeApi(_appSettings.BoxTagsApiUrl, () => _appSettings.AuthToken,
                        () => _appSettings.SelectedColonyId > 0 ? _appSettings.SelectedColonyId : 1);
                }

                // Load box tags from local storage
                _boxTags = BoxTagService.LoadBoxTags(internalPath);

                // Load remote penguin data.
                _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
                _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
                _boxNotes = _dataStorageService.LoadBoxNotesFromDisk(this);
                if (_remotePenguinData != null &&  _remoteBreedingDates != null)
                {
                    Toast.MakeText(this, $"{_remotePenguinData.Count} bird, {_remoteBreedingDates.Count} breeding dates found.", ToastLength.Short)?.Show();
                }

                // Load colony state (or migrate from legacy)
                _colonyState = DataStorageService.LoadColonyState(this);
                // Yesterday's label and people are cleared by ColonyState.RolloverDay(), which the
                // first DrawPageLayouts() runs — and which parks an unsent note in PendingDayNotes
                // on the way out. This used to clear the four fields here instead, which got there
                // first and left the rollover nothing to park: a note written out of signal was
                // gone by morning, with DailyLabelPendingUpload still set on an empty date for the
                // next sync to quietly drop.
                if (_colonyState.PreviousBoxes.Count > 0 || _colonyState.TodayBoxes.Count > 0 || _colonyState.PendingObservations.Count > 0)
                    Toast.MakeText(this, $"📱 Data restored...", ToastLength.Short)?.Show();

                // Flag for auto-download after UI is built. Trigger a full sync when the last
                // one is stale (not just "not today"): the incremental poller adopts the
                // server's watermark on a cold start and reports no changes, so a same-day
                // relaunch would otherwise never pull edits made while the app was closed
                // (e.g. another observer's afternoon visit missed by an evening relaunch).
                var syncAgeMin = _colonyState.LastSyncedUtc > DateTime.MinValue
                    ? (DateTime.UtcNow - _colonyState.LastSyncedUtc).TotalMinutes : double.MaxValue;
                _shouldAutoDownloadBirdStats = (_remotePenguinData == null || _remotePenguinData.Count == 0 || syncAgeMin > SyncStaleMinutes);
            }
            catch (Exception ex)
            {
                _remotePenguinData = new Dictionary<string, PenguinData>();
                System.Diagnostics.Debug.WriteLine($"Failed to load data: {ex.Message}");
            }
        }

        private void SaveToAppDataDir()
        {
            DataStorageService.SaveColonyState(this, _colonyState);
            UpdateSyncButtonLabel();

        }

        /// <summary>Re-fits the sync button's label after its text is reset mid-run.</summary>
        private Action? _refitSyncLabel;

        /// <summary>Gives a button a set of labels, longest first, and keeps the longest one that
        /// actually fits the width it ends up with. A single width breakpoint can only ever be
        /// right for one handset; measuring the laid-out button covers every screen between the
        /// narrowest phone and a tablet, and re-runs if the width changes (rotation, split screen).</summary>
        private void FitLabelNow(Button button, params string[] variants)
        {
            var available = button.Width - button.PaddingLeft - button.PaddingRight;
            if (available <= 0) return;
            var pick = variants.FirstOrDefault(v => button.Paint?.MeasureText(v) <= available)
                       ?? variants[variants.Length - 1];
            if (button.Text != pick) button.Text = pick;
        }

        private void FitLabel(Button button, params string[] variants)
        {
            void Apply()
            {
                var available = button.Width - button.PaddingLeft - button.PaddingRight;
                if (available <= 0) return;   // not laid out yet; the layout pass will call back
                var pick = variants.FirstOrDefault(v => button.Paint?.MeasureText(v) <= available)
                           ?? variants[variants.Length - 1];
                // Guard the assignment: setting Text requests another layout, so writing the same
                // string every pass would spin. Changed-only converges after one extra pass.
                if (button.Text != pick) button.Text = pick;
            }
            button.LayoutChange += (s, e) => Apply();
            button.Post(Apply);
        }

        private void UpdateSyncButtonLabel()
        {
            UpdateStatusText();
        }

        private void TryBackgroundUpload()
        {
            if (!_appSettings.IsAuthenticated || _colonyState.PendingUploadCount == 0 || _dialogActive || !_isBoxLocked) return;

            // Use Thread directly to avoid Android SynchronizationContext deadlock
            new Thread(async () =>
            {
                try
                {
                    // Birds chipped offline must exist server-side before the observation that
                    // scanned them goes up, or the server drops the scan as an unknown pit_id and
                    // the box stops being pending with the bird missing from it.
                    var chipWarnings = await _dataStorageService.FlushQueuedChips(this, _appSettings);
                    var result = await _dataStorageService.UploadPendingOnly(_colonyState, _appSettings);
                    if (chipWarnings.Count > 0)
                        (result.ChipWarnings ??= new List<string>()).AddRange(chipWarnings);
                    if (result.Uploaded > 0)
                        DataStorageService.SaveColonyState(this, _colonyState);
                    RunOnUiThread(() =>
                    {
                        if (result.Uploaded > 0)
                        {
                            _lastSyncCheckUtc = DateTime.UtcNow;
                            UpdateStatusText();
                            DrawPageLayouts();
                        }
                        UpdateSyncButtonLabel();
                        // A rejected scan or a refused queued bird is data that didn't land. The
                        // box is no longer pending, so nothing else will mention it — say it here,
                        // as something that has to be dismissed rather than a toast that passes.
                        var lost = new List<string>(result.UploadErrorDetails);
                        if (result.ChipWarnings != null) lost.AddRange(result.ChipWarnings);
                        if (lost.Count > 0)
                            new AlertDialog.Builder(this)
                                .SetTitle("Not everything uploaded")
                                .SetMessage(string.Join("\n\n", lost))
                                .SetPositiveButton("OK", (s2, e2) => { })
                                .Show();
                        if (result.Conflicts != null && result.Conflicts.Count > 0)
                            ShowConflictDialog(result.Conflicts);
                        else if (!string.IsNullOrEmpty(result.Error))
                            Toast.MakeText(this, $"Sync: {result.Error}", ToastLength.Short)?.Show();
                    });
                }
                catch (Exception ex)
                {
                    // A dropped socket on a phone that retries every 15 seconds is not news: the
                    // upload is still queued, the next poll sends it, and saying "sync failed" for
                    // something that fixes itself teaches people to distrust the message that
                    // matters. Anything else still gets said.
                    if (Http.IsTransient(ex))
                        System.Diagnostics.Debug.WriteLine($"Background upload, transient: {ex.Message}");
                    else
                        RunOnUiThread(() =>
                            Toast.MakeText(this, $"Sync failed: {ex.Message}", ToastLength.Short)?.Show());
                }
            }).Start();
        }

        protected override void OnNewIntent(Android.Content.Intent? intent)
        {
            base.OnNewIntent(intent);
            if (intent == null) return;
            Intent = intent;                   // so this.Intent reflects the NEW deep link, not a stale one
            HandleAuthDeepLink(intent);
        }

        private void HandleAuthDeepLink(Android.Content.Intent? intent)
        {
            if (intent?.Data?.Scheme != "nestcheck" || intent?.Data?.Host != "auth") return;
            var token = intent.Data.GetQueryParameter("token");
            var name = intent.Data.GetQueryParameter("name");
            var observerId = intent.Data.GetQueryParameter("observer_id");
            // Consume the deep link immediately so a later activity recreation can't re-process this
            // same login (which would spuriously re-toast / re-apply the previous user).
            intent.SetData(null);
            intent.SetAction(null);
            if (!string.IsNullOrEmpty(token))
            {
                _appSettings.AuthToken = token;
                _appSettings.ObserverName = name ?? "";
                if (int.TryParse(observerId, out var oid)) _appSettings.ObserverId = oid;
                DataStorageService.saveApplicationSettings(_appSettings);
                Toast.MakeText(this, $"Logged in as {name}", ToastLength.Short)?.Show();
                // Auto-sync after login to fetch colony and data
                _shouldAutoDownloadBirdStats = true;
            }
        }
        private void triggerAlertAsync()
        {
            new Thread(TriggerAlert).Start();
        }
        private void TriggerAlert()
        {
            try
            {
                // Vibrate for 500ms
                if (_vibrator != null)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    {
                        // Use VibrationEffect for API 26+
                        var vibrationEffect = VibrationEffect.CreateOneShot(500, VibrationEffect.DefaultAmplitude);
                        _vibrator.Vibrate(vibrationEffect);
                    }
                }

                // Play alert sound
                if (_alertMediaPlayer != null)
                {
                    try
                    {
                        int replayCount = 3;
                        while (replayCount-- > 0)
                        {
                            if (_alertMediaPlayer.IsPlaying)
                            {
                                _alertMediaPlayer.Stop();
                                _alertMediaPlayer.Prepare();
                            }
                            _alertMediaPlayer.Start();
                            Thread.Sleep(1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to play alert sound: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to trigger chick alert: {ex.Message}");
            }
        }
        // Green tick = watched box (mirrors the website's tick); grey = not watched.
        private void UpdateWatchedToggle()
        {
            if (_watchedToggle == null) return;
            bool watched = _boxNotes.TryGetValue(_currentBoxName, out var wn) && wn.Watched;
            _watchedToggle.Background = _uiFactory.CreateRoundedBackground(watched ? UIFactory.SUCCESS_GREEN : UIFactory.LIGHTER_GRAY, 6);
            _watchedToggle.SetTextColor(watched ? Color.White : Color.DarkGray);
        }

        private void ClearInternalStorageData()
        {
            try
            {
                var internalPath = FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(internalPath))
                {
                    _dataStorageService.ClearInternalStorageData(internalPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear auto-save file: {ex.Message}");
            }
        }

        // Logout: wipe all local data (a full reset) and prompt the user to log in again.
        // The user has already confirmed, including any warning about unsynced records.
        private void PerformFullLogout()
        {
            try
            {
                // Stop background sync/polling before tearing down state.
                _dataStorageService.StopBackgroundPolling();

                // Remove every file in internal storage — colony/penguin data, box tags,
                // notes, breeding dates, and settings all go.
                ClearInternalStorageData();

                // Reset in-memory state to a clean, logged-out slate.
                _colonyState = new ColonyState();
                _remotePenguinData = new Dictionary<string, PenguinData>();
                _remoteBreedingDates = null;
                _boxNotes = new Dictionary<string, BoxNoteData>();
                _boxTags = new Dictionary<string, BoxTag>();

                // Fresh default settings (no auth/observer/device prefs) and persist the cleared state.
                var internalPath = FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(internalPath))
                {
                    _appSettings = DataStorageService.loadAppSettingsFromDir(internalPath);
                    _appSettings.PropertyChanged += (s, e) => DataStorageService.saveApplicationSettings(_appSettings);
                    DataStorageService.saveApplicationSettings(_appSettings);
                    if (!string.IsNullOrWhiteSpace(_appSettings.BoxTagsApiUrl))
                    {
                        BoxTagService.InitializeApi(_appSettings.BoxTagsApiUrl, () => _appSettings.AuthToken,
                            () => _appSettings.SelectedColonyId > 0 ? _appSettings.SelectedColonyId : 1);
                    }
                }

                UpdateStatusText();
                DrawPageLayouts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Logout wipe failed: {ex.Message}");
            }

            // Prompt to log back in.
            ShowLoginPrompt();
        }

        // --- Edit Box Tags mode -------------------------------------------------------------

        // Location updates only run when the permission is granted and a provider is on. Without
        // this the capture panel would sit on "waiting for GPS" forever with no explanation.
        //
        // Three separate things can be off, and they need different fixes — sending someone to
        // Android's location screen when the real problem is nestcheck's own "Connect scanner"
        // checkbox just wastes their time in the field.
        private bool EnsureGpsReady()
        {
            // Resolve the manager here rather than trusting _locationManager: it's only assigned
            // by InitializeGPS(), which never runs while "Connect scanner" is unticked.
            _locationManager ??= (LocationManager?)GetSystemService(LocationService);

            // 1. Android permission
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { Android.Manifest.Permission.AccessFineLocation }, LOCATION_PERMISSION_REQUEST);
                return false;
            }

            // 2. Android location services
            bool providerOn = _locationManager?.IsProviderEnabled(LocationManager.GpsProvider) == true
                           || _locationManager?.IsProviderEnabled(LocationManager.NetworkProvider) == true;
            if (!providerOn)
            {
                new AlertDialog.Builder(this)
                    .SetTitle("Phone location is off")
                    .SetMessage("Nest positions need GPS. Turn location on in Android settings, then come back and try again.")
                    .SetNegativeButton("Cancel", (s, e) => { })
                    .SetPositiveButton("Open location settings", (s, e) =>
                        StartActivity(new Android.Content.Intent(Android.Provider.Settings.ActionLocationSourceSettings)))
                    .Show();
                return false;
            }

            // 3. Nestcheck's own scanner/GPS toggle. Nothing to send them to Android for —
            // capturing a position is reason enough to start listening, so just do it.
            InitializeGPS();
            return true;
        }

        // "Set location" — asks the user to confirm the phone is actually on the nest, and says
        // plainly what pressing Start does. Nothing listens until they confirm, so no reading is
        // ever taken with the phone still in their hand.
        private void StartTagCapture()
        {
            if (string.IsNullOrEmpty(_currentBoxName)) return;
            var box = _currentBoxName;

            new AlertDialog.Builder(this)
                .SetTitle($"Set location for Box {box}")
                // Keep the button label short — an AlertDialog button is one line at a fixed
                // height, so anything longer wraps and gets clipped. The confirmation lives in
                // the message, which ends on the question the button answers.
                .SetMessage("Put the phone flat on the box, or hold it over the nest.\n\n"
                          + "Recording will keep reading the GPS and hold on to the most accurate "
                          + "position it sees. Leave the phone where it is, and save when you're "
                          + "happy with it. Nothing is saved until you press Save.\n\n"
                          + "Is the phone in place?")
                .SetNegativeButton("Not yet", (s, e) => { })
                .SetPositiveButton("Start recording", (s, e) =>
                {
                    // Joining a tag session that's already open must not discard the scan it holds
                    if (!_tagCaptureActive || _tagCaptureBoxName != box)
                    {
                        _tagCaptureActive = true;
                        _tagCaptureBoxName = box;
                        _pendingTagId = null;
                        _pendingTagFromBox = null;
                        _pendingTagRemoval = false;
                    }
                    StartTagGpsMeasurement();
                })
                .Show();
        }

        // "Set tag" — a tag session with no GPS involved at all. Assigning a tag used to be
        // reachable only by going through a location recording, which made one job hostage to
        // the other; they're independent and they're now entered independently.
        private void StartTagOnlyCapture()
        {
            if (string.IsNullOrEmpty(_currentBoxName)) return;
            if (!_appSettings.IsBlueToothEnabled)
            {
                Toast.MakeText(this, "Scanner is off — turn on 'Connect scanner' in settings", ToastLength.Long)?.Show();
                return;
            }
            _tagCaptureActive = true;
            _tagCaptureBoxName = _currentBoxName;
            _tagGpsMeasuring = false;
            _bestTagCaptureLocation = null;
            _pendingTagId = null;
            _pendingTagFromBox = null;
            _pendingTagRemoval = false;
            DrawPageLayouts();
        }

        // Starts listening. Called from the bottom bar once the phone is on the box, and again
        // by "Restart measuring" if the user moved or wants to throw the readings away.
        private void StartTagGpsMeasurement()
        {
            if (!EnsureGpsReady()) return;

            // Deliberately NOT seeded from _currentLocation: that fix was taken wherever the
            // user last stood, and being the more accurate one it would beat every reading
            // taken at this box and get saved as this box's position.
            _bestTagCaptureLocation = null;
            _tagGpsMeasuring = true;
            _tagGpsElapsedSeconds = 0;

            // Ticks purely to keep the elapsed time honest — nothing stops the measurement but
            // the user. They watch the accuracy and save when it's good enough for them.
            _tagGpsHandler ??= new Handler(Looper.MainLooper);
            if (_tagGpsRunnable != null) _tagGpsHandler.RemoveCallbacks(_tagGpsRunnable);
            _tagGpsRunnable = new Java.Lang.Runnable(() =>
            {
                if (!_tagCaptureActive || !_tagGpsMeasuring) return;
                _tagGpsElapsedSeconds++;
                UpdateTagCaptureSteps();
                _tagGpsHandler?.PostDelayed(_tagGpsRunnable, 1000);
            });
            _tagGpsHandler.PostDelayed(_tagGpsRunnable, 1000);
            DrawPageLayouts();
        }

        private void StopTagGpsMeasurement()
        {
            _tagGpsMeasuring = false;
            if (_tagGpsHandler != null && _tagGpsRunnable != null)
                _tagGpsHandler.RemoveCallbacks(_tagGpsRunnable);
        }

        private void EndTagCapture()
        {
            StopTagGpsMeasurement();
            _tagCaptureActive = false;
            _tagCaptureBoxName = "";
            _bestTagCaptureLocation = null;
            _pendingTagId = null;
            _pendingTagFromBox = null;
            _pendingTagRemoval = false;
            // The session may have started location updates that the scanner setting had off.
            // Don't leave the GPS running against the user's setting once the session ends.
            if (!_appSettings.IsBlueToothEnabled)
            {
                _locationManager?.RemoveUpdates(this);
                _gpsAccuracy = -1;
            }
            DrawPageLayouts();
        }

        // What the two steps currently hold. Save writes exactly this and nothing else.
        private string CurrentCaptureTag(BoxTag? existing)
        {
            if (_pendingTagRemoval) return "";
            return _pendingTagId ?? existing?.TagNumber ?? "";
        }

        // The step-2 side action changes both label and meaning (remove / undo / clear), so its
        // outline colour follows the label — red only when it's actually destructive.
        private void SetTagActionStyle(string label, Color colour)
        {
            if (_tagModeTagActionButton == null) return;
            _tagModeTagActionButton.Text = label;
            _tagModeTagActionButton.SetTextColor(colour);
            _tagModeTagActionButton.Background = _uiFactory.CreateCardBackground(borderWidth: 3, borderColour: colour);
            _tagModeTagActionButton.Visibility = ViewStates.Visible;
        }

        // Redraws the guided steps. Called on every GPS fix, so it stays cheap.
        private void UpdateTagCaptureSteps()
        {
            if (_tagModeCaptureTitle == null || _tagModeStep1Text == null || _tagModeStep2Text == null
                || _tagModeTagActionButton == null || _tagModeSaveButton == null
                || _tagModeMeasureAgainButton == null) return;
            if (!_tagCaptureActive) return;

            var box = _tagCaptureBoxName;
            _boxTags.TryGetValue(box, out var existing);
            bool hasStoredPosition = existing != null && (existing.Latitude != 0 || existing.Longitude != 0);

            _tagModeCaptureTitle.Text = _tagGpsMeasuring
                ? $"Recording location — Box {box}"
                : $"Set tag — Box {box}";

            // --- Step 1: GPS ---------------------------------------------------------------
            var loc = _bestTagCaptureLocation;

            // What's already on file, and how far away it is. Without the stored accuracy there's
            // no way to tell whether a new reading is an improvement or a downgrade, and the
            // distance is what says "you're at the right nest" (or that you aren't).
            string StoredBlock(Location? from)
            {
                if (!hasStoredPosition) return "\n\nNo position stored for this box yet.";
                var acc = existing!.Accuracy > 0 ? $"±{FormatAccuracy(existing.Accuracy)}" : "accuracy unknown";
                var when = existing.ScanTimeUTC != default ? $", set {ToNzTime(existing.ScanTimeUTC):d MMM yyyy}" : "";
                var block = $"\n\nStored position: {acc}{when}";
                if (from != null)
                {
                    var dist = new float[1];
                    Location.DistanceBetween(existing.Latitude, existing.Longitude, from.Latitude, from.Longitude, dist);
                    block += $"\nYou are {dist[0]:F0} m from it.";
                }
                return block;
            }

            if (!_tagGpsMeasuring)
            {
                // Tag-only session: the position is not being touched, and saying so matters —
                // otherwise "Save tag" looks like it might move the nest.
                _tagModeStep1Text.Text = "Location: not being changed."
                    + StoredBlock(_currentLocation);
                _tagModeStep1Text.SetTextColor(Color.DarkGray);
                _tagModeMeasureAgainButton.Text = "Set location too";
                _tagModeMeasureAgainButton.Visibility = ViewStates.Visible;
            }
            else if (loc == null)
            {
                _tagModeStep1Text.Text = $"Finding GPS…  {_tagGpsElapsedSeconds}s\n"
                    + "Leave the phone where it is."
                    + (_tagGpsElapsedSeconds >= 10 ? "\n\nThis can take a minute under trees." : "")
                    + StoredBlock(_currentLocation);
                _tagModeStep1Text.SetTextColor(UIFactory.WARNING_YELLOW);
                _tagModeMeasureAgainButton.Text = "Restart measuring";
                _tagModeMeasureAgainButton.Visibility = ViewStates.Visible;
            }
            else
            {
                // Best reading since measuring started, updating live. No target to hit and no
                // countdown — the user watches it tighten and saves when they're satisfied.
                bool rough = loc.Accuracy > 15;
                var quality = loc.Accuracy <= 5 ? "Good" : rough ? "Rough" : "OK";
                _tagModeStep1Text.Text = $"Best so far: {quality} — about {loc.Accuracy:F0} m  ({_tagGpsElapsedSeconds}s)\n"
                    + "Leaving it a little longer usually tightens this up."
                    + StoredBlock(loc);
                _tagModeStep1Text.SetTextColor(loc.Accuracy <= 5 ? UIFactory.SUCCESS_GREEN
                    : rough ? UIFactory.DANGER_RED : UIFactory.WARNING_YELLOW);
                _tagModeMeasureAgainButton.Text = "Restart measuring";
                _tagModeMeasureAgainButton.Visibility = ViewStates.Visible;
            }

            // --- Step 2: box tag -----------------------------------------------------------
            var existingTag = existing?.TagNumber ?? "";
            if (_pendingTagRemoval)
            {
                _tagModeStep2Text.Text = $"Tag: {existingTag} will be removed on save.";
                _tagModeStep2Text.SetTextColor(UIFactory.DANGER_RED);
                SetTagActionStyle("Undo removal", TAG_MODE_PURPLE);
            }
            else if (_pendingTagId != null)
            {
                var from = _pendingTagFromBox != null ? $"\nTaken off Box {_pendingTagFromBox}." : "";
                _tagModeStep2Text.Text = $"Tag: {_pendingTagId} ✓{from}";
                _tagModeStep2Text.SetTextColor(UIFactory.SUCCESS_GREEN);
                SetTagActionStyle("Clear scanned tag", TAG_MODE_PURPLE);
            }
            else if (!string.IsNullOrEmpty(existingTag))
            {
                _tagModeStep2Text.Text = $"Tag: {existingTag} ✓ (already set)\n"
                    + "Scan a different tag to replace it.";
                _tagModeStep2Text.SetTextColor(Color.Black);
                SetTagActionStyle("Remove tag", UIFactory.DANGER_RED);
            }
            else
            {
                _tagModeStep2Text.Text = _appSettings.IsBlueToothEnabled
                    ? "Tag: waiting for a scan…\nScan the tag on the box. Optional — you can save a position without one."
                    : "Tag: scanner is off.\nTurn on 'Connect scanner' in settings to assign a tag. You can still save a position.";
                _tagModeStep2Text.SetTextColor(Color.DarkGray);
                _tagModeTagActionButton.Visibility = ViewStates.Gone;
            }

            // --- Save ------------------------------------------------------------------------
            bool tagChanged = _pendingTagId != null || _pendingTagRemoval;
            bool canSave = loc != null || tagChanged;
            _tagModeSaveButton.Text = loc != null && tagChanged ? "✓  Save position and tag"
                : tagChanged && loc == null ? "✓  Save tag"
                : "✓  Save position";
            _tagModeSaveButton.Enabled = canSave;
            _tagModeSaveButton.Alpha = canSave ? 1.0f : 0.4f;
        }

        // The inline step-2 action: clears a scanned tag, or stages/undoes removal of the stored one.
        private void OnTagCaptureTagAction()
        {
            if (!_tagCaptureActive) return;

            if (_pendingTagRemoval)
            {
                _pendingTagRemoval = false;
                UpdateTagCaptureSteps();
                return;
            }
            if (_pendingTagId != null)
            {
                _pendingTagId = null;
                _pendingTagFromBox = null;
                UpdateTagCaptureSteps();
                return;
            }

            var box = _tagCaptureBoxName;
            if (!_boxTags.TryGetValue(box, out var tagInfo) || string.IsNullOrEmpty(tagInfo.TagNumber)) return;
            new AlertDialog.Builder(this)
                .SetTitle($"Remove tag from Box {box}?")
                .SetMessage($"Tag {tagInfo.TagNumber} will no longer point at Box {box}, so scanning it " +
                            $"won't find this box.\n\nThe stored position is kept. Takes effect when you save.")
                .SetNegativeButton("Keep tag", (s, e) => { })
                .SetPositiveButton("Remove on save", (s, e) =>
                {
                    _pendingTagRemoval = true;
                    UpdateTagCaptureSteps();
                })
                .Show();
        }

        // Commits both steps in one go. A stored position is only replaced with an explicit yes,
        // and answering "keep existing" still saves the tag change rather than throwing it away.
        private void SaveTagCapture()
        {
            var boxName = _tagCaptureBoxName;
            if (string.IsNullOrEmpty(boxName)) return;

            var internalPath = this.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(internalPath)) return;

            var loc = _bestTagCaptureLocation;
            var existing = _boxTags.TryGetValue(boxName, out var bt) ? bt : null;
            bool hasStoredPosition = existing != null && (existing.Latitude != 0 || existing.Longitude != 0);

            void Commit(bool writePosition)
            {
                var finalTag = CurrentCaptureTag(existing);
                double lat = writePosition ? loc!.Latitude : existing?.Latitude ?? 0;
                double lon = writePosition ? loc!.Longitude : existing?.Longitude ?? 0;
                float acc = writePosition ? loc!.Accuracy : existing?.Accuracy ?? -1;

                // Free the tag from its old box first so it's never on two boxes at once
                if (_pendingTagFromBox != null)
                    BoxTagService.ClearBoxTagNumber(_boxTags, _pendingTagFromBox, internalPath);

                BoxTagService.AssignBoxTag(_boxTags, boxName, finalTag, lat, lon, acc,
                    internalPath, _appSettings.ObserverId);

                // AssignBoxTag with an empty tag is a location-only update server-side, so an
                // actual removal has to go through the delete path to clear pit_id remotely.
                if (_pendingTagRemoval)
                    BoxTagService.ClearBoxTagNumber(_boxTags, boxName, internalPath);

                var parts = new List<string>();
                if (writePosition) parts.Add($"position ±{loc!.Accuracy:F0}m");
                if (_pendingTagId != null) parts.Add("tag");
                if (_pendingTagRemoval) parts.Add("tag removed");
                Toast.MakeText(this, $"✓ Box {boxName} saved ({string.Join(", ", parts)})", ToastLength.Short)?.Show();
                EndTagCapture();
            }

            if (loc == null)
            {
                Commit(writePosition: false);   // tag-only session
                return;
            }
            if (!hasStoredPosition)
            {
                Commit(writePosition: true);    // nothing to overwrite
                return;
            }

            var dist = new float[1];
            Location.DistanceBetween(existing!.Latitude, existing.Longitude, loc.Latitude, loc.Longitude, dist);
            var oldAcc = existing.Accuracy >= 0 ? $"±{existing.Accuracy:F0}m" : "unknown accuracy";
            new AlertDialog.Builder(this)
                .SetTitle($"Replace stored position for Box {boxName}?")
                .SetMessage($"Box {boxName} already has a position.\n\n" +
                            $"Stored: {existing.Latitude:F6}, {existing.Longitude:F6} ({oldAcc})\n" +
                            $"New: {loc.Latitude:F6}, {loc.Longitude:F6} (±{loc.Accuracy:F0}m)\n\n" +
                            $"The new position is {dist[0]:F0}m from the stored one.")
                .SetNegativeButton("Keep existing", (s, e) => Commit(writePosition: false))
                .SetPositiveButton("Replace", (s, e) => Commit(writePosition: true))
                .Show();
        }

        // Leaves box tags mode. An open capture is unsaved work, so it's confirmed first.
        private void FinishBoxTagsMode()
        {
            if (_tagCaptureActive)
            {
                new AlertDialog.Builder(this)
                    .SetTitle($"Still setting up Box {_tagCaptureBoxName}")
                    .SetMessage("Nothing from this box has been saved yet — the position and any scanned tag will be discarded.")
                    .SetNegativeButton("Keep editing", (s, e) => { })
                    .SetPositiveButton("Discard and finish", (s, e) => ExitBoxTagsMode())
                    .Show();
                return;
            }
            ExitBoxTagsMode();
        }

        private void ExitBoxTagsMode()
        {
            _appSettings.EditBoxTagsMode = false;
            StopTagGpsMeasurement();
            _tagCaptureActive = false;
            _tagCaptureBoxName = "";
            _bestTagCaptureLocation = null;
            _pendingTagId = null;
            _pendingTagFromBox = null;
            _pendingTagRemoval = false;
            _isBoxLocked = true;
            // A capture may have started location updates the scanner setting had switched off.
            // Put that back so the mode doesn't leave the GPS running against the user's wishes.
            if (!_appSettings.IsBlueToothEnabled)
            {
                _locationManager?.RemoveUpdates(this);
                _gpsAccuracy = -1;
            }
            if (_expandSettingsButton != null) _expandSettingsButton.Visibility = ViewStates.Visible;
            selectedPage = UIFactory.selectedPage.BoxDataSingle;
            DrawPageLayouts();
            ScrollToTop();
        }

        private void HandleBoxTagScan(string cleanTagId)
        {
            RunOnUiThread(() =>
            {
                // Check if this tag is already assigned to a box
                string? assignedBoxId = BoxTagService.GetBoxIdByTag(_boxTags, cleanTagId);

                // Box tags mode has its own rules and never touches the lock state.
                if (_appSettings.EditBoxTagsMode && !_isHistoricalView)
                {
                    HandleTagModeScan(cleanTagId, assignedBoxId);
                    return;
                }

                if (!_isBoxLocked)
                {
                    // Normal mode — a box tag means "navigate to that box".
                    if (_pendingBoxTagNavigation != null && assignedBoxId != _pendingBoxTagNavigation)
                    {
                        // A navigation is already pending and the user scanned a *different* box tag.
                        // Keep the original destination (first tag wins) and freeze the scan queue
                        // so nothing else is recorded into it.
                        _pendingScanQueueFrozen = true;
                        TriggerAlert();
                        Toast.MakeText(this, $"⚠️ Lock Box {_currentBoxName} first — heading to Box {_pendingBoxTagNavigation}", ToastLength.Long)?.Show();
                    }
                    else if (assignedBoxId != null && assignedBoxId != _currentBoxName)
                    {
                        // Current box is unlocked — the user forgot to lock it before moving on.
                        // Remind them, defer the navigation, and record incoming scans until they lock.
                        _pendingBoxTagNavigation = assignedBoxId;
                        _pendingScanQueueFrozen = false;
                        TriggerAlert();
                        new AlertDialog.Builder(this)
                            .SetTitle("Box not locked")
                            .SetMessage($"You forgot to lock Box {_currentBoxName}!\n\nValidate the data and lock the box — we'll continue to Box {assignedBoxId} once it's locked.")
                            .SetCancelable(false)
                            .SetPositiveButton("OK", (s, e) => { })
                            .Show();
                    }
                    else if (assignedBoxId == _currentBoxName)
                    {
                        Toast.MakeText(this, $"Already at Box {_currentBoxName} — lock it when done", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        Toast.MakeText(this, $"Unassigned tag — open Box detail in settings to assign it", ToastLength.Short)?.Show();
                    }
                }
                else
                {
                    // Current box is LOCKED
                    if (assignedBoxId != null)
                    {
                        // This tag is assigned to a box - jump to it and unlock
                        if (assignedBoxId == _currentBoxName)
                        {
                            // Same box - just unlock
                            _isBoxLocked = false;
                            selectedPage = UIFactory.selectedPage.BoxDataSingle;
                            if (_singleBoxDataContentLayout != null) _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                            if (_heldScans.Count > 0) FlushHeldScansToCurrentBox();
                            else { DrawPageLayouts(); ScrollToTop(); Toast.MakeText(this, $"🔓 Box {_currentBoxName} unlocked", ToastLength.Short)?.Show(); }
                        }
                        else
                        {
                            // Different box - jump to it and unlock
                            if (_boxNamesAndIndexes.ContainsKey(assignedBoxId))
                            {
                                _currentBoxIndex = _boxNamesAndIndexes[assignedBoxId];
                                _currentBoxName = assignedBoxId;
                                _isBoxLocked = false;
                                    selectedPage = UIFactory.selectedPage.BoxDataSingle;
                                if (_singleBoxDataContentLayout != null) _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                                if (_heldScans.Count > 0) FlushHeldScansToCurrentBox();
                                else { DrawPageLayouts(); ScrollToTop(); Toast.MakeText(this, $"📍 Jumped to Box {assignedBoxId} and unlocked", ToastLength.Short)?.Show(); }
                            }
                            else
                            {
                                Toast.MakeText(this, $"⚠️ Box {assignedBoxId} not in current scope", ToastLength.Long)?.Show();
                            }
                        }
                    }
                    else
                    {
                        // Unassigned box tag scanned while locked - error
                        TriggerAlert();
                        Toast.MakeText(this, $"⚠️ Unknown box tag!\nUnlock a box first to assign this tag.", ToastLength.Long)?.Show();
                    }
                }
            });
        }

        // Scans while in box tags mode. Outside a capture session a tag only navigates — it can
        // never assign, move or overwrite anything, so walking the colony with the reader running
        // is safe. Inside a session it assigns to the box on screen, always confirming first if
        // that would take a tag off another box or replace one already on this box.
        private void HandleTagModeScan(string cleanTagId, string? assignedBoxId)
        {
            bool capturing = _tagCaptureActive && _tagCaptureBoxName == _currentBoxName;

            if (!capturing)
            {
                if (assignedBoxId == _currentBoxName)
                {
                    Toast.MakeText(this, $"That's Box {_currentBoxName}'s tag", ToastLength.Short)?.Show();
                }
                else if (assignedBoxId != null && _boxNamesAndIndexes.ContainsKey(assignedBoxId))
                {
                    JumpToBox(assignedBoxId);
                    ScrollToTop();
                    Toast.MakeText(this, $"📍 Box {assignedBoxId}", ToastLength.Short)?.Show();
                }
                else if (assignedBoxId != null)
                {
                    Toast.MakeText(this, $"⚠️ Box {assignedBoxId} not in current scope", ToastLength.Long)?.Show();
                }
                else
                {
                    TriggerAlert();
                    Toast.MakeText(this, "Unassigned tag — open the box it belongs to, then tap 'Set up this box'", ToastLength.Long)?.Show();
                }
                return;
            }

            if (assignedBoxId == _currentBoxName)
            {
                Toast.MakeText(this, $"Box {_currentBoxName} already has this tag", ToastLength.Short)?.Show();
                return;
            }

            var boxName = _currentBoxName;
            _boxTags.TryGetValue(boxName, out var existing);
            var existingTag = existing?.TagNumber ?? "";

            // The scan fills in step 2; it doesn't write anything. Save commits both steps,
            // so cancelling really does leave the box untouched.
            void Stage()
            {
                _pendingTagId = cleanTagId;
                _pendingTagFromBox = assignedBoxId;
                _pendingTagRemoval = false;
                Toast.MakeText(this, $"🏷 Tag ready — press Save to write it to Box {boxName}", ToastLength.Short)?.Show();
                UpdateTagCaptureSteps();
            }

            void Proceed()
            {
                // Case 1: the tag currently belongs to another box — moving it orphans that box.
                if (assignedBoxId != null)
                {
                    TriggerAlert();
                    new AlertDialog.Builder(this)
                        .SetTitle("Tag belongs to another box")
                        .SetMessage($"This tag is Box {assignedBoxId}'s.\n\nMove it to Box {boxName}? " +
                                    $"Box {assignedBoxId} will be left with no tag.")
                        .SetNegativeButton("Cancel", (s, e) => { })
                        .SetPositiveButton($"Move to Box {boxName}", (s, e) => Stage())
                        .Show();
                    return;
                }

                // Case 2: this box already has a different tag — replacing it breaks the old one.
                if (!string.IsNullOrEmpty(existingTag))
                {
                    TriggerAlert();
                    new AlertDialog.Builder(this)
                        .SetTitle($"Replace Box {boxName}'s tag?")
                        .SetMessage($"Box {boxName} currently has tag {existingTag}.\n\n" +
                                    $"Replace it with the tag you just scanned? Scanning {existingTag} will no longer find this box.")
                        .SetNegativeButton("Keep existing tag", (s, e) => { })
                        .SetPositiveButton("Replace", (s, e) => Stage())
                        .Show();
                    return;
                }

                // Case 3: free tag onto an untagged box — nothing is lost, so no confirmation.
                Stage();
            }

            // A bird sitting in the nest reads just as easily as the box tag stuck to the lid.
            // Nailing a penguin's chip to a box would misroute every future scan of that bird.
            if (BoxTagService.IsPenguinTag(cleanTagId))
            {
                TriggerAlert();
                new AlertDialog.Builder(this)
                    .SetTitle("That's a penguin chip")
                    .SetMessage($"{cleanTagId} is in the penguin range, not the box tag range.\n\n" +
                                "Did you mean to scan the tag on the box?")
                    .SetNegativeButton("Ignore it", (s, e) => { })
                    .SetPositiveButton($"Use it as Box {boxName}'s tag anyway", (s, e) => Proceed())
                    .Show();
                return;
            }

            Proceed();
        }

        // Completes a deferred "forgot to lock" navigation: jump to the box whose tag was scanned
        // and replay the recorded penguin scans into it.
        private void NavigateToPendingBox(string boxName)
        {
            if (!_boxNamesAndIndexes.ContainsKey(boxName))
            {
                Toast.MakeText(this, $"⚠️ Box {boxName} not in current scope", ToastLength.Long)?.Show();
                _heldScans.Clear();
                return;
            }
            _currentBoxIndex = _boxNamesAndIndexes[boxName];
            _currentBoxName = boxName;
            _isBoxLocked = false;
            selectedPage = UIFactory.selectedPage.BoxDataSingle;
            if (_singleBoxDataContentLayout != null) _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
            if (_heldScans.Count > 0)
            {
                // Replay the recorded scans into the now-current box
                FlushHeldScansToCurrentBox();
            }
            else
            {
                DrawPageLayouts();
                ScrollToTop();
                Toast.MakeText(this, $"📍 Box {boxName}", ToastLength.Short)?.Show();
            }
        }

        private void AddScannedId(String fullEid, int _unused = 0, bool isManualEntry = false)
        {
            var cleanEid = BluetoothManager.BareEid(new String(fullEid.Where(char.IsLetterOrDigit).ToArray()));

            // Check if this is a box tag scan (ISO digits 9000250*)
            if (BoxTagService.IsBoxTag(cleanEid))
            {
                HandleBoxTagScan(cleanEid);
                return;
            }

            var boxData = _colonyState.GetTodayForBox(_currentBoxName) ?? new BoxObservation { BoxName = _currentBoxName };

            if (!boxData.ScannedIds.Any(s => s.BirdId == cleanEid))
            {
                var scanRecord = new ScanRecord
                {
                    BirdId = cleanEid, // Store full ID, never truncate
                    Timestamp = (isManualEntry && _appSettings.ActiveSessionTimeStampActive) ? TimeZoneInfo.ConvertTimeToUtc(_appSettings.ActiveSessionLocalTimeStamp, NzTimeZone) : DateTime.UtcNow,
                    Latitude = _currentLocation?.Latitude ?? 0,
                    Longitude = _currentLocation?.Longitude ?? 0,
                    Accuracy = _currentLocation?.Accuracy ?? -1
                };
                boxData.ScannedIds.Add(scanRecord);
                boxData.WhenDataCollectedUtc = DateTime.UtcNow;
                boxData.ObserverName = _appSettings.ObserverName;
                _colonyState.SaveBoxObservation(_currentBoxName, boxData); // draft until the box is locked
                SaveCurrentBoxData();
                RunOnUiThread(() =>
                {
                    // Enhanced toast message with life stage info
                    bool isReturning = false;
                    var displayId = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8) : cleanEid;
                    if (_remotePenguinData != null && _remotePenguinData.TryGetValue(cleanEid.ToUpper(), out var penguinCheck))
                    {
                        isReturning = penguinCheck.LastKnownLifeStage == LifeStage.Returnee ||
                                      (penguinCheck.LastKnownLifeStage == LifeStage.Chick &&
                                       penguinCheck.ChipDate > DateTime.MinValue &&
                                       (NzNow - penguinCheck.ChipDate).TotalDays > 90);
                    }
                    string birdIcon = isReturning ? "🔄🐧" : "🐧";
                    string toastMessage = $"{birdIcon} Bird {displayId} added to Box {_currentBoxName}";
                    // One shout per scan, whatever earns it. A bird that is both flagged on the
                    // website and unsexed is still one bird in your hand, and buzzing twice for it
                    // teaches people to ignore the buzz.
                    bool alerted = false;
                    void RaiseAlert() { if (!alerted) { triggerAlertAsync(); alerted = true; } }
                    if (_remotePenguinData != null && _remotePenguinData.TryGetValue(cleanEid.ToUpper(), out var penguin))
                    {
                        if (penguin.LastKnownLifeStage == LifeStage.Chick)
                        {
                            // Still a chick (a chick chipped >3 months ago is derived as a returnee/adult instead)
                            BumpCount(_chicksEditText, 1);
                            toastMessage += $" (+1 Chick)";
                            SaveCurrentBoxData();
                        }
                        else if (penguin.LastKnownLifeStage == LifeStage.Adult ||
                                 penguin.LastKnownLifeStage == LifeStage.Returnee)
                        {
                            BumpCount(_adultsEditText, 1);
                            SaveCurrentBoxData();

                            bool unsexed = !penguin.Sex.Equals("f", StringComparison.OrdinalIgnoreCase) && !penguin.Sex.Equals("m", StringComparison.OrdinalIgnoreCase);
                            if (unsexed)
                            {
                                RaiseAlert();
                                toastMessage += penguin.LastKnownLifeStage == LifeStage.Returnee ? $" unsexed returnee" : $" unsexed";
                            }
                            else
                                toastMessage += $" (+1 Adult)";
                        }
                        else
                        {
                            toastMessage += ", Not adult or chick.";
                            RaiseAlert();
                        }

                        // Flagged on the website, whatever its life stage or sex. Said last so it's
                        // the end of the message, and shown for longer — it's the reason someone
                        // put the flag there.
                        if (penguin.HasAlert)
                        {
                            RaiseAlert();
                            toastMessage += " ⚠️ ALERT — check this bird on wildwatch";
                        }
                    }
                    else
                    {
                        toastMessage += ", Unknown scan ID!";
                        RaiseAlert();
                        var boxAtScan = _currentBoxName;
                        ShowNewBirdDialog(displayId, cleanEid,
                            scanCleanup: (boxAtScan, false));
                    }
                    DrawPageLayouts();
                    Toast.MakeText(this, toastMessage, alerted ? ToastLength.Long : ToastLength.Short)?.Show();
                    MaybePromptSexConfirmation(cleanEid);
                });
            }
        }

        /// <summary>Guesses agreeing on one side must total this before anyone is asked to sex the
        /// bird for real — two "probably" readings, or four "maybe" ones.</summary>
        private const int SexConfirmScore = 4;
        // One ask per bird per session: the prompt interrupts scanning, and a second one for a
        // bird already declined is just nagging.
        private readonly HashSet<string> _sexPromptShown = new();

        /// <summary>A scanned bird with no confirmed sex whose field guesses have piled up gets one
        /// offer to settle it on the website, where the guesses and the edit both live.</summary>
        private void MaybePromptSexConfirmation(string cleanEid)
        {
            if (_appSettings.EditBoxTagsMode || _isHistoricalView) return;
            if (_remotePenguinData == null || !_remotePenguinData.TryGetValue(cleanEid.ToUpper(), out var pd)) return;
            if (!string.IsNullOrEmpty(pd.Sex)) return;                       // already sexed — nothing to settle
            if (pd.LastKnownLifeStage == LifeStage.Dead) return;
            if (string.IsNullOrEmpty(pd.PengNum) || !_sexPromptShown.Add(pd.PengNum)) return;

            bool mOver = pd.SexGuessM >= SexConfirmScore, fOver = pd.SexGuessF >= SexConfirmScore;
            if (!mOver && !fOver) return;

            // A single side over the line has a sex to apply; guesses on both sides disagree, so
            // there's nothing to apply — that one only offers to open the bird on the website.
            string? winningSex = mOver && !fOver ? "m" : (fOver && !mOver ? "f" : null);
            var display = DisplayPengNum(pd.PengNum);
            var message = winningSex == null
                ? $"Field guesses for #{display} disagree — {pd.SexGuessM} towards male, {pd.SexGuessF} towards female.\n\nSomeone needs to settle it on wildwatch."
                : $"#{display} has enough field guesses to sex it: {(mOver ? pd.SexGuessM : pd.SexGuessF)} points towards {(mOver ? "male" : "female")}.\n\nApply it, or open the bird on wildwatch first?";

            TriggerAlert();
            var builder = new AlertDialog.Builder(this)
                .SetTitle("Sex this bird?")
                .SetNeutralButton("View wildwatch", (s, e) => { })   // Click rewired below so it doesn't dismiss
                .SetNegativeButton("Cancel", (s, e) => { });
            if (winningSex != null)
                builder.SetMessage(message)
                       .SetPositiveButton("Apply sex", (s, e) => ApplySexFromPrompt(pd, winningSex));
            else
                builder.SetMessage(message);
            var dialog = builder.Create();
            dialog.Show();
            // "View wildwatch" opens the bird panel (a full-screen modal) OVER this prompt. Rewiring
            // Click drops the button's auto-dismiss, so closing the panel returns you to the prompt —
            // same trick the embed panel's ◀/▶ buttons use.
            var viewBtn = dialog.GetButton((int)Android.Content.DialogButtonType.Neutral);
            if (viewBtn != null) viewBtn.Click += (s, e) => ShowBirdPanel(cleanEid, pd.PengNum);
        }

        /// <summary>Writes the confirmed sex to the penguin record on wildwatch, then reflects it
        /// locally so the bird stops reading as unsexed for the rest of the session.</summary>
        private async void ApplySexFromPrompt(PenguinData pd, string sex)
        {
            var token = _appSettings.AuthToken;
            var display = DisplayPengNum(pd.PengNum);
            if (string.IsNullOrEmpty(token))
            {
                Toast.MakeText(this, "Not logged in — can't sex on wildwatch", ToastLength.Long)?.Show();
                return;
            }
            Toast.MakeText(this, $"Sexing #{display}…", ToastLength.Short)?.Show();
            var error = await _dataStorageService.ApplyPenguinSex(pd.PengNum, sex, token, CurrentColonyIdOrDefault());
            RunOnUiThread(() =>
            {
                if (error == null)
                {
                    pd.Sex = sex;   // no longer unsexed — future scans this session won't alert or re-prompt
                    Toast.MakeText(this, $"#{display} sexed {(sex == "m" ? "male" : "female")}", ToastLength.Long)?.Show();
                    DrawPageLayouts();
                }
                else
                    new AlertDialog.Builder(this)
                        .SetTitle("Couldn't sex bird")
                        .SetMessage($"{error}\n\nNothing changed — try again or sex it on wildwatch.")
                        .SetPositiveButton("OK", (s, e) => { })
                        .Show();
            });
        }
        // A scanned unknown tag goes into the box before its new-bird form opens, so the
        // scan is visible immediately — this takes it back out when the form is cancelled.
        // decrementAdultCount: the held-scans flush counted the unknown bird as an adult.
        private void RemoveUnsavedScanFromBox(string boxName, string pitId, bool decrementAdultCount)
        {
            var boxData = _colonyState.GetTodayForBox(boxName);
            var rec = boxData?.ScannedIds.FirstOrDefault(s => s.BirdId == pitId);
            if (boxData == null || rec == null) return;
            boxData.ScannedIds.Remove(rec);
            if (decrementAdultCount)
            {
                boxData.Adults = Math.Max(0, boxData.Adults - 1);
                if (boxName == _currentBoxName && _adultsEditText?[0] != null)
                    BumpCount(_adultsEditText, -1);
            }
            boxData.WhenDataCollectedUtc = DateTime.UtcNow;
            _colonyState.SaveBoxObservation(boxName, boxData);
            SaveToAppDataDir();
            DrawPageLayouts();
            var displayId = pitId.Length >= 8 ? pitId.Substring(pitId.Length - 8) : pitId;
            Toast.MakeText(this, $"Scan {displayId} removed from Box {boxName} — new bird cancelled", ToastLength.Short)?.Show();
        }

        private void ShowSaveFilenameDialog(bool upload = false)
        {
            var now = NzNow;
            string defaultFileName = $"PenguinMonitor {now:yyMMdd HHmmss}";            
            if (!string.IsNullOrEmpty((_colonyState.DailyLabel ?? "")))
            {
                if (!Regex.Match((_colonyState.DailyLabel ?? ""), @"-\d\d$").Success)   // string ends with -00 or -37
                {
                    defaultFileName = (_colonyState.DailyLabel ?? "") + "-01";
                }
                else
                {
                    defaultFileName = Regex.Replace((_colonyState.DailyLabel ?? ""), @"-(\d\d)$", match =>
                    {
                        int number = int.Parse(match.Groups[1].Value);
                        return "-" + (number + 1).ToString("D2");
                    });
                }
            }
            var input = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText,
                Text = defaultFileName,
                Hint = "Enter filename (without .json extension)"
            };
            input.SetTextColor(UIFactory.TEXT_PRIMARY);
            input.SetPadding(16, 16, 16, 16);
            input.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);


            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle("Save Data File")
                .SetMessage("Enter a filename for your data export:")
                .SetView(input)
                .SetPositiveButton("Save", (s, e) =>
                {
                    var fileName = input.Text?.Trim();
                    if (string.IsNullOrEmpty(fileName))
                    {
                        Toast.MakeText(this, "Please enter a filename", ToastLength.Short)?.Show();
                        return;
                    }

                    // Clean filename - remove invalid characters
                    var invalidChars = System.IO.Path.GetInvalidFileNameChars();
                    foreach (var invalidChar in invalidChars)
                    {
                        fileName = fileName.Replace(invalidChar, '_');
                    }

                    // Ensure .json extension
                    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        fileName += ".json";
                    }
                    SaveDataWithFilename(fileName, upload);
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            alertDialog?.Show();
            
            input.RequestFocus();
            input.SelectAll();

            var inputMethodManager = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
            inputMethodManager?.ShowSoftInput(input, Android.Views.InputMethods.ShowFlags.Implicit);
        }
        /// <summary>Writes today's colony state to Downloads and offers to share it. This is the
        /// answer to "sync keeps failing and I'm about to lose a day's work": the file leaves the
        /// phone by whatever route works, even with no working API.
        ///
        /// Saved through MediaStore on Android 10+, which both lands it in Downloads and hands
        /// back a content:// URI that can go straight into a share. Below that it goes to the
        /// public Downloads path, which is still readable but can't be shared without a
        /// FileProvider — so those devices get the path and no share button.</summary>
        /// <summary>What the file contains, named item by item. Someone exports because they don't
        /// trust the sync; "3 boxes" leaves them wondering whether the birds and the bird details
        /// went with it.</summary>
        private string ExportSummary(int boxes, int pending)
        {
            var parts = new List<string> { $"{boxes} box observation{(boxes == 1 ? "" : "s")}" };
            int bios = _colonyState.TodayBiometrics.Count;
            int birds = _dataStorageService.LoadQueuedChips(this).Count;
            int notes = _boxNotes.Values.Count(n => !string.IsNullOrWhiteSpace(n.PersistentNotes));
            if (bios > 0) parts.Add($"{bios} bird detail{(bios == 1 ? "" : "s")}");
            if (birds > 0) parts.Add($"{birds} new bird{(birds == 1 ? "" : "s")}");
            if (notes > 0) parts.Add($"{notes} box note{(notes == 1 ? "" : "s")}");
            return string.Join(", ", parts)
                 + (pending > 0 ? $"\n\n{pending} of these have not reached wildwatch." : "\n\nAll of it has reached wildwatch already.");
        }

        private void ExportTodayJson()
        {
            try
            {
                var stamp = ToNzTime(DateTime.UtcNow).ToString("yyyy-MM-dd-HHmm");
                var colony = string.IsNullOrWhiteSpace(CurrentColonyAcronym()) ? "colony" : CurrentColonyAcronym();
                var fileName = $"nestcheck-{colony}-{stamp}.json";

                // The observations keep sync.php's upload shape so the file stays feedable to
                // wildwatch. What goes in them is everything the server might not have: today's
                // round, AND any earlier day still queued — a phone that's been out of signal for
                // three days is exactly when someone reaches for this button, and exporting only
                // today would hand back a file missing the very work at risk.
                var toExport = new Dictionary<string, BoxObservation>();   // box|date, so days don't collide
                string Key(BoxObservation o) => $"{o.BoxName}|{ToNzTime(o.WhenDataCollectedUtc):yyyy-MM-dd}";
                foreach (var kv in _colonyState.TodayBoxes)
                    if (!string.IsNullOrEmpty(kv.Key)) toExport[Key(kv.Value)] = kv.Value;
                foreach (var p in _colonyState.PendingObservations)
                    if (!string.IsNullOrEmpty(p.BoxName)) toExport[Key(p)] = p;   // a local edit wins

                var observations = toExport.Values
                    .OrderBy(o => ToNzTime(o.WhenDataCollectedUtc)).ThenBy(o => o.BoxName)
                    .Select(o => (object)DataStorageService.BuildObservationPayload(o))
                    .ToList();

                // Everything else the phone is holding that the server may not have. The counts
                // dialog has always said "still waiting to upload" — this is what that means, and
                // until now the file didn't contain any of it.
                var users = DataStorageService.LoadUsers(this);
                string? UserName(int id) => id > 0 ? users.FirstOrDefault(u => u.id == id)?.name : null;
                var queuedBirds = _dataStorageService.LoadQueuedChips(this);

                var body = new Dictionary<string, object?>
                {
                    // sync.php's upload shape, unchanged
                    ["daily_label"] = _colonyState.DailyLabel,
                    ["daily_observer_id"] = _colonyState.DailyObserverId,
                    ["daily_scribe_id"] = _colonyState.DailyScribeId,
                    ["observations"] = observations,
                    // and the rest, so the file stands on its own
                    ["exported_at_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["app_version"] = version,
                    ["colony_id"] = CurrentColonyIdOrDefault(),
                    ["colony"] = CurrentColonyAcronym(),
                    // Names beside the ids: a file read by a person, or imported somewhere without
                    // this colony's user table, shouldn't reduce who was out to a pair of numbers.
                    ["daily_observer"] = UserName(_colonyState.DailyObserverId),
                    ["daily_scribe"] = UserName(_colonyState.DailyScribeId),
                    ["day_notes"] = _colonyState.PendingDayNotes.Select(n => new {
                        nz_date = n.NzDate, note = n.Note,
                        observer_id = n.ObserverId, scribe_id = n.ScribeId,
                        observer = UserName(n.ObserverId), scribe = UserName(n.ScribeId),
                    }).ToList(),
                    ["biometrics"] = _colonyState.TodayBiometrics.Values
                        .OrderBy(b => b.ObservationDate).ThenBy(b => b.PengNum)
                        .Select(b => new {
                            peng_num = b.PengNum, observation_date = b.ObservationDate,
                            weight = b.Weight, flipper_length = b.FlipperLength, observed_sex = b.ObservedSex,
                            is_moulting = b.ConditionMoulting, condition_ticks = b.ConditionTicks,
                            dead = b.ConditionDead, notes = b.Notes,
                            biometric_id = b.BiometricId, unsent = b.IsPendingUpload,
                        }).ToList(),
                    ["queued_birds"] = queuedBirds.Select(q => new {
                        pit_id = q.FullPitId, requested_peng_num = q.RequestedPengNum,
                        chip_box = string.IsNullOrEmpty(q.ChipBox) ? q.BoxName : q.ChipBox,
                        chip_date = ToNzTime(q.CreatedUtc).ToString("yyyy-MM-dd"),
                        chipped_as_adult = q.IsChick ? 0 : 1, chick_size_code = q.ChickSizeCode,
                        is_rechip = q.IsRechip, rechip_peng_num = q.RechipPengNum,
                        chipper_id = q.ChipperId, chipper = UserName(q.ChipperId),
                        assistant_id = q.AssistantId, assistant = UserName(q.AssistantId),
                        weight = q.Weight, flipper_length = q.Flipper, observed_sex = q.SexCode, notes = q.Notes,
                    }).ToList(),
                    ["box_notes"] = _boxNotes.Values
                        .Where(n => !string.IsNullOrWhiteSpace(n.PersistentNotes) || n.Watched || n.WatchedPendingUpload)
                        .OrderBy(n => n.BoxName)
                        .Select(n => new {
                            box_name = n.BoxName, location_id = n.LocationId,
                            persistent_notes = n.PersistentNotes, watched = n.Watched,
                            watched_unsent = n.WatchedPendingUpload,
                        }).ToList(),
                    ["unsent"] = new {
                        observations = _colonyState.PendingUploadCount,
                        biometrics = _colonyState.PendingBiometricCount,
                        birds = queuedBirds.Count,
                        day_notes = _colonyState.PendingDayNotes.Count
                                    + (_colonyState.DailyLabelPendingUpload ? 1 : 0),
                    },
                };
                var json = JsonConvert.SerializeObject(body, Formatting.Indented);

                int boxes = observations.Count;
                int pending = _colonyState.PendingUploadCount + _colonyState.PendingBiometricCount
                              + queuedBirds.Count;

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    var values = new ContentValues();
                    values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                    values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "application/json");
                    values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads);
                    var uri = ContentResolver?.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri, values);
                    if (uri == null) { Toast.MakeText(this, "Couldn't create the file", ToastLength.Long)?.Show(); return; }
                    using (var os = ContentResolver!.OpenOutputStream(uri))
                    {
                        if (os == null) { Toast.MakeText(this, "Couldn't write the file", ToastLength.Long)?.Show(); return; }
                        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        os.Write(bytes, 0, bytes.Length);
                        os.Flush();
                    }

                    new AlertDialog.Builder(this)
                        .SetTitle("Data exported")
                        .SetMessage($"Downloads/{fileName}\n\n{ExportSummary(boxes, pending)}")
                        .SetNegativeButton("Done", (s, e) => { })
                        .SetPositiveButton("Share", (s, e) =>
                        {
                            var send = new Android.Content.Intent(Android.Content.Intent.ActionSend);
                            send.SetType("application/json");
                            send.PutExtra(Android.Content.Intent.ExtraStream, uri);
                            send.PutExtra(Android.Content.Intent.ExtraSubject, fileName);
                            send.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
                            StartActivity(Android.Content.Intent.CreateChooser(send, "Send today's data"));
                        })
                        .Show();
                    return;
                }

                var downloadsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
                if (string.IsNullOrEmpty(downloadsPath))
                {
                    Toast.MakeText(this, "Downloads directory not accessible", ToastLength.Long)?.Show();
                    return;
                }
                var path = System.IO.Path.Combine(downloadsPath, fileName);
                File.WriteAllText(path, json);
                new AlertDialog.Builder(this)
                    .SetTitle("Data exported")
                    .SetMessage($"Downloads/{fileName}\n\n{ExportSummary(boxes, pending)}")
                    .SetPositiveButton("Done", (s, e) => { })
                    .Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Export failed: {ex.Message}", ToastLength.Long)?.Show();
            }
        }

        private void SaveDataWithFilename(string fileName, bool upload)
        {
            try
            {
                _colonyState.DailyLabel = fileName.Replace(".json","");
                var jsonContents = JsonConvert.SerializeObject(_colonyState, Formatting.Indented);
                var downloadsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;

                if (string.IsNullOrEmpty(downloadsPath))
                {
                    Toast.MakeText(this, "Downloads directory not accessible", ToastLength.Short)?.Show();
                    return;
                }

                var filePath = System.IO.Path.Combine(downloadsPath, fileName);

                // Check if file already exists
                if (File.Exists(filePath))
                {
                    ShowConfirmationDialog(
                        "File Exists",
                        $"A file named '{fileName}' already exists. Do you want to overwrite it?",
                        ("Overwrite", () => {
                            SaveJsonToPath(filePath, jsonContents);
                        }
                    ),
                        ("Cancel", () => ShowSaveFilenameDialog()) // Go back to filename dialog
                    );
                }
                else
                {
                    SaveJsonToPath(filePath, jsonContents);
                }
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Export failed: {ex.Message}", ToastLength.Short)?.Show();
            }
        }
        private void SaveJsonToPath(string filePath, string json )
        {
            try
            {
                string fileName = System.IO.Path.GetFileName(filePath);
                File.WriteAllText(filePath, json);

                var totalBoxes = _colonyState.TodayBoxes.Count + _colonyState.PendingObservations.Select(o => o.BoxName).Distinct().Count();
                var totalBirds = _colonyState.TodayBoxes.Values.Sum(box => box.ScannedIds.Count) + _colonyState.PendingObservations.Sum(o => o.ScannedIds.Count);

                Toast.MakeText(this, $"💾 Data saved!\n📂 {fileName}\n📦 {totalBoxes} boxes, 🐧 {totalBirds} birds", ToastLength.Short)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to save file: {ex.Message}", ToastLength.Long)?.Show();
            }
        }
        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

                if (requestCode == 1)
                {
                    bool allPermissionsGranted = grantResults.All(result => result == Android.Content.PM.Permission.Granted);

                    if (allPermissionsGranted)
                    {
                        if (_appSettings?.IsBlueToothEnabled == true) InitializeGPS();
                        Toast.MakeText(this, "✅ All permissions granted", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        var deniedPermissions = permissions.Zip(grantResults, (perm, result) => new { Permission = perm, Granted = result == Permission.Granted })
                            .Where(x => !x.Granted)
                            .Select(x => x.Permission)
                            .ToArray();

                        Toast.MakeText(this, $"⚠️ Some permissions denied. App functionality may be limited.\nDenied: {string.Join(", ", deniedPermissions.Select(p => p.Split('.').Last()))}", ToastLength.Long)?.Show();
                    }
                }
                else if (requestCode == LOCATION_PERMISSION_REQUEST) // location asked for by a box tag capture
                {
                    if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                    {
                        InitializeGPS();
                        Toast.MakeText(this, "✅ Location granted — tap 'Set up this box' again", ToastLength.Long)?.Show();
                    }
                    else
                    {
                        Toast.MakeText(this, "❌ Location denied — nest positions can't be recorded", ToastLength.Long)?.Show();
                    }
                }
                else if (requestCode == 2) // READ_EXTERNAL_STORAGE request from LoadJsonDataFromFile
                {
                    if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                    {
                        Toast.MakeText(this, "✅ Storage permission granted. Try loading files again.", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        Toast.MakeText(this, "❌ Storage permission denied. Cannot access Downloads folder.", ToastLength.Short)?.Show();
                    }
                }
            }
        }
        private bool CheckExternalStoragePermissions()
        {
            try
            {
                var sdkVersion = (int)Android.OS.Build.VERSION.SdkInt;
                System.Diagnostics.Debug.WriteLine($"Checking permissions for Android API {sdkVersion}");

                if (OperatingSystem.IsAndroidVersionAtLeast(30)) // Android 11+ (API 30+)
                {
                    // Android 11+ - Check if we have MANAGE_EXTERNAL_STORAGE
                    var hasManageStorage = Android.OS.Environment.IsExternalStorageManager;
                    System.Diagnostics.Debug.WriteLine($"Android 11+: MANAGE_EXTERNAL_STORAGE = {hasManageStorage}");
                    return hasManageStorage;
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(23)) // Android 6+ (API 23+)
                {
                    // Android 6-10 - Check READ_EXTERNAL_STORAGE permission using native API
                    var hasReadPermission = CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Android.Content.PM.Permission.Granted;
                    System.Diagnostics.Debug.WriteLine($"Android 6-10: READ_EXTERNAL_STORAGE = {hasReadPermission}");
                    return hasReadPermission;
                }
                else
                {
                    // Pre-Android 6 - Permission granted at install time
                    System.Diagnostics.Debug.WriteLine("Pre-Android 6: Permissions granted at install time");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Permission check failed: {ex.Message}");
                return false;
            }
        }
        private void ShowBoxJumpDialog()
        {
            var input = new EditText(this)
            {
                Text = _currentBoxIndex.ToString()
            };
            input.InputType = InputTypes.ClassText;      // numeric keyboard
            input.SetSelectAllOnFocus(true);               // easy overwrite
            input.ImeOptions = (ImeAction)ImeFlags.NoExtractUi | ImeAction.Go;

            var dialog = new AlertDialog.Builder(this)
                .SetTitle("Jump to Box")
                .SetMessage($"Enter box name in scope: " + _appSettings.BoxSetString)
                .SetView(input)
                .SetPositiveButton("Go", (s, e) =>
                {
                    var matchingKey = _boxNamesAndIndexes.Keys.FirstOrDefault(k => string.Equals(k, input.Text, StringComparison.OrdinalIgnoreCase));
                    if (matchingKey != null)
                    {
                        JumpToBox(matchingKey);
                    }
                    else
                    {
                        Toast.MakeText(this, $"Box number must be in scope: " + _appSettings.BoxSetString, ToastLength.Short)?.Show();
                    }
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            dialog.Show();

            // Ensure the keyboard pops and the input is focused
            input.Post(() =>
            {
                input.RequestFocus();
                dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible);

                var imm = (InputMethodManager)GetSystemService(Context.InputMethodService);
                imm?.ShowSoftInput(input, ShowFlags.Forced);
            });

            // Let the keyboard's Go/Done key trigger the positive button
            var btnGo = dialog.GetButton((int)DialogButtonType.Positive);
            input.EditorAction += (s, e) =>
            {
                if (e.ActionId == ImeAction.Go || e.ActionId == ImeAction.Done)
                {
                    btnGo?.PerformClick();
                    e.Handled = true;
                }
            };
        }
        private void JumpToBox(string targetBox)
        {
            if (targetBox == _currentBoxName)
            {
                Toast.MakeText(this, $"Already at Box {targetBox}", ToastLength.Short)?.Show();
                return;
            }
            if (!_isBoxLocked)
            {
                Toast.MakeText(this, $"Cannot change box while current box is unlocked.", ToastLength.Short)?.Show();
                return;
            }
            _currentBoxIndex = _boxNamesAndIndexes[targetBox];
            _currentBoxName = targetBox;
            // Box changed. Don't touch card visibility here: DrawPageLayouts posts its work to
            // the next looper pass, so unhiding synchronously drew one frame of the empty data
            // entry card before the tag panel replaced it. DrawPageLayouts owns this either way.
            DrawPageLayouts();
        }
        private string? GetSelectedStatus(Spinner spinner)
        {
            if (spinner?.SelectedItem != null)
            {
                var selected = spinner.SelectedItem.ToString() ?? "";
                return string.IsNullOrEmpty(selected) ? null : selected;
            }
            return null;
        }
        private void SetSpinnerStatus(Spinner spinner, string? gateStatus)
        {
            if (spinner?.Adapter != null)
            {
                var adapter = spinner.Adapter as ArrayAdapter<string>;
                if (adapter != null)
                {
                    var displayValue = gateStatus ?? "";
                    var position = adapter.GetPosition(displayValue);
                    if (position >= 0)
                        spinner.SetSelection(position);
                }
            }
        }
        private void OnDataClick(object? sender, EventArgs e)
        {
            ShowDataOptionsDialog();
        }
        private void ShowDataOptionsDialog()
        {
            var options = new string[]
            {
                "📊 Monitor statistics",
                "💾 Save monitor",
                "📂 Open monitor file",
            };
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Load & Save Options");            
            builder.SetItems(options, (sender, args) =>
            {
                switch (args.Which)
                {
                    case 0: // Summary
                        ShowBoxDataSummary();
                        break;
                    case 1: // Save data
                        OnSaveDataClick(null, EventArgs.Empty);
                        break;
                    case 2: // Load data
                        LoadJsonDataFromFile();
                        break;
                }
            });
            builder.SetNegativeButton("Cancel", (sender, args) => { });            
            var dialog = builder.Create();
            dialog?.Show();
        }
    }
}