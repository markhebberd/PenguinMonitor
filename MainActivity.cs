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
using SmtpAuthenticator;
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
    public class MainActivity : Activity, ILocationListener
    {
        //Lazy versioning.
        private static string version = "37.19";
        // Bluetooth manager
        private BluetoothManager? _bluetoothManager;

        // GPS components
        private LocationManager? _locationManager;
        private Location? _currentLocation;
        private float _gpsAccuracy = -1;

        // UI Components
        private ScrollView? _rootScrollView;
        private TextView? _statusText; // scanner and GPS status

        private LinearLayout? _topButtonLayout; //Clear, bird stats and save/load. 

        private Button _showLatestMonitorButton;
        private Button _gotoNextMonitorButton;
        private Button _gotoPreviousMonitorButton;

        private Button? _prevBoxButton;
        private Button? _selectBoxButton;
        private Button? _nextBoxButton;

        private LinearLayout? _settingsCard;
        private LinearLayout _overviewFiltersLayout;
        private CheckBox? _isBluetoothEnabledCheckBox;
        private TextView? _interestingBoxTextView;
        private CheckBox _setTimeActiveSessionCheckBox;
        private TextView _boxSavedTimeTextView;
        private Spinner _boxSetSelector;

        // Single box data 
        private bool _isBoxLocked;
        private bool _highOffspringCountConfirmed;
        private LinearLayout? _singleBoxDataOuterLayout;
        private LinearLayout? _singleBoxDataTitleLayout;
        private LinearLayout _singleBoxDataContentLayout;
        private LinearLayout _boxNavigationButtonsLayout;
        private TextView? _dataCardTitleText;
        private ImageView? _dataCardLockIconView;
        private Button? _deleteBoxTagButton;

        private ScrollView? _prevBoxDataScrollView;

        private List<LinearLayout?> _scannedIdsLayout;
        private EditText? _manualScanEditText;

        private List<EditText?> _adultsEditText;
        private List<EditText?> _eggsEditText;
        private List<EditText?> _chicksEditText;
        private List<Spinner?> _breedingChanceSpinner;
        private List<Spinner?> _gateStatusSpinner;
        private List<EditText?> _notesEditText;

        private UIFactory.selectedPage selectedPage;
        private readonly (string Text, UIFactory.selectedPage Page)[] _menuItems = new[]
        {
            ("⚙️ Settings",      UIFactory.selectedPage.Settings),
            ("📦 Single box data",  UIFactory.selectedPage.BoxDataSingle),
            ("📊 Data overview", UIFactory.selectedPage.BoxOverview),
         };

        // Add gesture detection components
        private GestureDetector? _gestureDetector;

        // Services
        public UIFactory? _uiFactory;
        private DataStorageService _dataStorageService = new DataStorageService();

        // Data storage
        private int _currentBoxIndex = 1;
        private string _currentBoxName = "";
        private Dictionary<string, int> _boxNamesAndIndexes;
        Dictionary<int, MonitorDetails> _allMonitorData = new Dictionary<int, MonitorDetails>();
        private AppSettings _appSettings;
        private Dictionary<string, PenguinData>? _remotePenguinData ;
        private Dictionary<string, BoxPredictedDates>? _remoteBreedingDates;
        private Dictionary<string, BoxTag> _boxTags;

        // High value confirmation tracking - reset on each entry
        private DateTime doNotDisplayBefore;

        // Vibration and sound components
        private Vibrator? _vibrator;
        private MediaPlayer? _alertMediaPlayer;

        //multibox View
        private LinearLayout? _multiBoxViewCard;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestPermissions();
            LoadFromAppDataDir();
            CreateUI();
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
                    InitializeGPS();
                }
            }
            else
            {
                // Pre-Android 6 or no permissions needed
                InitializeGPS();
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
            _currentLocation = location;
            _gpsAccuracy = location.Accuracy;
            UpdateStatusText();
        }
        public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { } // required by ILocationListener
        public void OnProviderDisabled(string provider) { } // required by ILocationListener
        public void OnProviderEnabled(string provider) { } // required by ILocationListener
        private void InitializeBluetooth()
        {
            _bluetoothManager = new BluetoothManager();
            _bluetoothManager.StatusChanged += OnBluetoothStatusChanged;
            _bluetoothManager.EidDataReceived += OnEidDataReceived;
            if (_isBluetoothEnabledCheckBox.Checked)
                _ = _bluetoothManager.StartConnectionAsync();
        }
        private void OnBluetoothStatusChanged(string status)
        {
            RunOnUiThread(() => UpdateStatusText(status));
        }
        public void OnSwipePrevious()
        {
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
            if (eidData.Length != 15)
                ;// new Handler(Looper.MainLooper).Post(() => Toast.MakeText(this, "EID length " + eidData.Length + ", '" + eidData + "'", ToastLength.Long)?.Show());
            AddScannedId(eidData, 0);
            _isBoxLocked = false;
            DrawPageLayouts();
        }
        private void UpdateStatusText(string? bluetoothStatus = null)
        {
            var btStatus = "Bluetooth Disabled";
            if (_bluetoothManager != null)
            {
                btStatus = bluetoothStatus ?? (_bluetoothManager?.IsConnected == true ? "HR5 Connected" : "Connecting to HR5...");
            }
            var gpsStatus = _gpsAccuracy > 0 ? $" | GPS: ±{_gpsAccuracy:F1}m" : " | GPS: No signal";

            RunOnUiThread(() =>
            {
                if (_statusText != null)
                {
                    _statusText.Text = btStatus + gpsStatus;

                    // Update status color based on connection state
                    if (btStatus.Contains("Connected") && _gpsAccuracy > 0)
                        _statusText.SetTextColor(UIFactory.SUCCESS_GREEN);
                    else if (btStatus.Contains("Connected"))
                        _statusText.SetTextColor(UIFactory.WARNING_YELLOW);
                    else
                        _statusText.SetTextColor(UIFactory.TEXT_SECONDARY);
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
        private void LoadJsonDataFromServer()
        {
            try
            {
                string jsonReply = Backend.RequestServerResponse("PenguinReportRequest:");
                LoadJsonData(jsonReply);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed: {ex.Message}", ToastLength.Long)?.Show();
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
                
                ShowConfirmationDialog(
                    "Load JSON Data",
                    $"Load data from:\n{fileName}\n\nThis will replace current box data.",
                    ("Load", () => LoadJsonFileData(selectedFile)),
                    ("Cancel", () => { })
                );
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
                if (loadedData["BoxData"] != null)
                {
                    _allMonitorData[0].BoxData.Clear();
                    
                    var boxDatas = loadedData["BoxData"] as JObject;
                    foreach (var boxItem in boxDatas)
                    {
                        string boxName = boxItem.Key;
                        var dataNode = boxItem.Value;
                        var boxData = new BoxData
                        {
                            Adults = dataNode["Adults"]?.Value<int>() ?? 0,
                            Eggs = dataNode["Eggs"]?.Value<int>() ?? 0,
                            Chicks = dataNode["Chicks"]?.Value<int>() ?? 0,
                            GateStatus = (dataNode["GateStatus"]?.Value<string>() ?? "").Replace("gate up", "Gate up").Replace("regate", "Regate"),
                            Notes = dataNode["Notes"]?.Value<string>() ?? "",
                            whenDataCollectedUtc = dataNode["whenDataCollectedUtc"]?.Value<DateTime>().ToUniversalTime() ?? DateTime.MinValue.ToUniversalTime(),
                        };
                        // Load scanned IDs
                        var scannedIdsNode = dataNode["ScannedIds"];
                        if (scannedIdsNode != null)
                        {
                            foreach (var scanItem in scannedIdsNode)
                            {
                                var scanRecord = new ScanRecord
                                {
                                    BirdId = scanItem?["BirdId"]?.Value<string>() ?? "",
                                    Timestamp = scanItem?["Timestamp"]?.Value<DateTime>() ?? DateTime.UtcNow,
                                    Latitude = scanItem?["Latitude"]?.Value<double>() ?? 0,
                                    Longitude = scanItem?["Longitude"]?.Value<double>() ?? 0,
                                    Accuracy = scanItem?["Accuracy"]?.Value<float>() ?? -1
                                };
                                boxData.ScannedIds.Add(scanRecord);
                                birdCount++;
                            }
                        }
                        _allMonitorData[0].BoxData[boxName] = boxData;
                        boxCount++;
                    }
                }
                else
                {
                    Toast.MakeText(this, "❌ No box data found in JSON file", ToastLength.Long)?.Show();
                    return;
                }

                SaveToAppDataDir(reportHome: false);

                // Refresh UI
                DrawPageLayouts();

                Toast.MakeText(this, $"✅ Loaded {boxCount} boxes, {birdCount} birds", ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to load JSON: {ex.Message}", ToastLength.Long)?.Show();
            }
        }
        private void ShowBoxDataSummary(int selectedMonitor)
        {
            if (_allMonitorData[selectedMonitor].BoxData.Count == 0)
                Toast.MakeText(this, "No boxes with data", ToastLength.Short)?.Show();
            else
                ShowConfirmationDialog(
                    "📊 Data summary",
                    GetMonitorSummaryText(selectedMonitor),
                    ("OK", () => { }),
                    null);
        }
        private string GetMonitorSummaryText(int selectedMonitor)
        {
            var totalScannedBirds = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.ScannedIds.Count);
            var totalAdults = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.Adults);
            var totalFemales = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.ScannedIds.Count(id =>
                _remotePenguinData.ContainsKey(id.BirdId) && _remotePenguinData[id.BirdId].Sex.Equals("F", StringComparison.OrdinalIgnoreCase)));
            var totalmales = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.ScannedIds.Count(id =>
                _remotePenguinData.ContainsKey(id.BirdId) && _remotePenguinData[id.BirdId].Sex.Equals("M", StringComparison.OrdinalIgnoreCase)));
            var totalEggs = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.Eggs);
            var totalChicks = _allMonitorData[selectedMonitor].BoxData.Values.Sum(box => box.Chicks);
            var gateUpCount = _allMonitorData[selectedMonitor].BoxData.Values.Count(box => box.GateStatus == "Gate up");
            var regateCount = _allMonitorData[selectedMonitor].BoxData.Values.Count(box => box.GateStatus == "Regate");

            string percentFemale = totalFemales + totalmales > 0 ? ", " + ((int)(100 * totalFemales / (totalFemales + totalmales))).ToString() + "% female" : "";

            string summary = $"📦 {_allMonitorData[selectedMonitor].BoxData.Count} boxes with data\n" +
                         $"🐧 {totalScannedBirds} bird scans" + percentFemale + "\n" + 
                         $"👥 {totalAdults} adults\n" +
                         $"🥚 {totalEggs} eggs\n" +
                         $"🐣 {totalChicks} chicks\n" +
                         $"🚪 Gate: {gateUpCount} up, {regateCount} regate\n\n" +
                         $"Box range: {(_allMonitorData[selectedMonitor].BoxData.Keys.Any() ? _allMonitorData[selectedMonitor].BoxData.Keys.Min() : 0)} - {(_allMonitorData[selectedMonitor].BoxData.Keys.Any() ? _allMonitorData[selectedMonitor].BoxData.Keys.Max() : 0)}";
            return summary;
        }
        private bool _isDownloadingCsvData = false;
        private void OnBirdStatsClick(object? sender, EventArgs e)
        {
            if (_isDownloadingCsvData)
                return;

            _isDownloadingCsvData = true;
            for (int i = 0; i < _topButtonLayout.ChildCount; i++)
            {
                Button child = (Button)_topButtonLayout.GetChildAt(i);
                if (child.Text.Equals("Bird stats"))
                {
                    SetEnabledRecursive(child, false, 0.5f);
                    child.Text = "Loading data";
                    child.Background = _uiFactory.CreateRoundedBackground(UIFactory.WARNING_YELLOW, 8);
                    _ = Task.Run(async () =>
                    {
                        await _dataStorageService.DownloadRemoteData(this, _allMonitorData);
                        _allMonitorData = _dataStorageService.LoadAllMonitorDataFromDisk(this);
                        _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
                        _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
                        new Handler(Looper.MainLooper).Post(() =>
                        {
                            _isDownloadingCsvData = false;
                            child.Text = "Bird stats";
                            child.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 8);
                            if (!_allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor))
                                _appSettings.CurrentlyVisibleMonitor = 0;
                            SaveToAppDataDir(false);
                            DrawPageLayouts();
                            SetEnabledRecursive(child, true, 1.0f);
                        });
                    });
                }
            }
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

            var headerStatusSettingsCard = _uiFactory.CreateCard(padding: 20);
            headerStatusSettingsCard.Background = _uiFactory.CreateCardBackground(borderWidth: 4);            

            var titleCard = _uiFactory.CreateCard(Android.Widget.Orientation.Horizontal, padding: 0, borderWidth: 0);
            titleCard.SetGravity(GravityFlags.Center);

            var expandSettingsImageButton = new ImageButton(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            };
            expandSettingsImageButton.SetImageResource(Resource.Drawable.unfold); 
            expandSettingsImageButton.SetBackgroundColor(Color.Transparent); 
            expandSettingsImageButton.Click += (s,e) => {
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

            var titleText = new TextView(this)
            {
                Text = "Penguin Nestcheck",
                TextSize = 28,
                Gravity = GravityFlags.Center
            };
            titleText.SetPadding(0, 0, 50, 0);
            titleText.SetTextColor(UIFactory.PRIMARY_BLUE);
            titleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            titleCard.AddView(titleText);

            var spacer2 = new View(this);
            spacer2.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 7f);
            titleCard.AddView(spacer2);

            headerStatusSettingsCard.AddView(titleCard);

            _statusText = new TextView(this)
            {
                TextSize = 14,
                Gravity = GravityFlags.Center
            };
            _statusText.SetTextColor(UIFactory.TEXT_SECONDARY);
            var statusParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            statusParams.SetMargins(0, 0, 0, 0);
            _statusText.LayoutParameters = statusParams;

            headerStatusSettingsCard.AddView(_statusText);

            //Settings Card
            createSettingsCard();
            headerStatusSettingsCard.AddView(_settingsCard);
            CreateBoxSetsDictionary();

            parentLinearLayout.AddView(headerStatusSettingsCard);

            // Action buttons
            _topButtonLayout = CreateStyledButtonLayout(
                ("Clear all", OnClearBoxesClick, UIFactory.DANGER_RED),
                ("Clear box", OnClearBoxClick, UIFactory.WARNING_YELLOW),
                ("Bird stats", OnBirdStatsClick, UIFactory.PRIMARY_BLUE),
                ("Save/Load", OnDataClick, UIFactory.SUCCESS_GREEN)
            );
            statusParams.SetMargins(0, 0, 0, 10);
            _topButtonLayout.LayoutParameters = statusParams;
            headerStatusSettingsCard.AddView(_topButtonLayout);

           // Data card
            CreateBoxDataCard();

            //Create Multi box view card
            _multiBoxViewCard = _uiFactory.CreateCard();
            _multiBoxViewCard.Visibility = ViewStates.Visible;

            parentLinearLayout.AddView(_singleBoxDataOuterLayout);
            parentLinearLayout.AddView(_multiBoxViewCard);

            _rootScrollView.AddView(parentLinearLayout);
            SetContentView(_rootScrollView);

            _rootScrollView.SetOnApplyWindowInsetsListener(new ViewInsetsListener());

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
                setString = "1";
            else
                setString = _appSettings.BoxSetString.ToLower() == "all" ? _appSettings.AllBoxSetsString : _appSettings.BoxSetString;

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
                                        _boxNamesAndIndexes[boxName] = currentIndex++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Single box name/number
                            _boxNamesAndIndexes[trimmedPart] = currentIndex++;
                        }
                    }
                }
            }
            if (_boxNamesAndIndexes.Count > 1000)
                _boxNamesAndIndexes = _boxNamesAndIndexes.Take(1000).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (_boxNamesAndIndexes.Count == 0)
                _boxNamesAndIndexes.Add("fake", 1);
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
                //_overviewFiltersLayout.Visibility = _appSettings.ShowMultiboxFilterCard ? ViewStates.Visible : ViewStates.Gone;
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
            multiBoxTitle.SetPadding(0, 0, 0, 0);
            headerTitle.AddView(multiBoxTitle);

            // Add a spacer that expands to fill available space
            var spacer = new View(this);
            spacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            headerTitle.AddView(spacer);

            TextView timeTV = new TextView(this)
            {
                TextSize = 12,
            };
            if (_appSettings.CurrentlyVisibleMonitor == 0)
                timeTV.Text = "Data is local only";
            else
                timeTV.Text = _allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor) ? _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename.Replace("PenguinMonitor", "").Trim() : "";

            bool timeFound = false;
            if (_allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor))
                foreach (BoxData box in _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Values)
                {
                    foreach (ScanRecord sc in box.ScannedIds)
                    {
                        timeTV.Text += "\n" + sc.Timestamp.ToLocalTime().ToString("d MMM yyyy, HH:mm");
                        timeFound = true;
                        break;
                    }
                    if (!timeFound && box.whenDataCollectedUtc.Year > 2015)
                    {
                        timeTV.Text += "\n" + box.whenDataCollectedUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
                        timeFound = true;
                    }
                    if (timeFound) break;
                }
            if (!timeFound)
                timeTV.Text += "\nNo date in data";
            timeTV.Text = timeTV.Text.Trim();
            timeTV.SetTextColor(Color.Black);
            timeTV.SetPadding(0, 0, 0, 0);
            timeTV.Gravity = GravityFlags.Right | GravityFlags.Bottom;
            timeTV.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent);
            headerTitle.AddView(timeTV);

            OverviewHeaderCard.AddView(headerTitle);

            _overviewFiltersLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };

            TextView showBoxesTitle = new TextView(this)
            {
                Text = "Show Boxes",
                TextSize = 16,
                Gravity = GravityFlags.Center,

            };
            showBoxesTitle.SetTypeface(null, TypefaceStyle.Bold);
            showBoxesTitle.SetTextColor(Color.Black);
            _overviewFiltersLayout.AddView(showBoxesTitle);

            var allAndDataCheckBoxesLayout = new LinearLayout(this);

            CheckBox showAllBoxesInMultiBoxView = new CheckBox(this)
            {
                Text = "All",
                Checked = _appSettings.ShowAllBoxesInMultiBoxView
            };
            showAllBoxesInMultiBoxView.SetTextColor(Color.Black);
            showAllBoxesInMultiBoxView.Click += (s, e) =>
            {
                _appSettings.ShowAllBoxesInMultiBoxView = showAllBoxesInMultiBoxView.Checked;
                DrawPageLayouts();
            };
            allAndDataCheckBoxesLayout.AddView(showAllBoxesInMultiBoxView);

            CheckBox showBoxesWithDataInMultiboxView = new CheckBox(this)
            {
                Text = "With data",
                Checked = _appSettings.ShowBoxesWithDataInMultiBoxView

            };
            showBoxesWithDataInMultiboxView.SetTextColor(Color.Black);
            showBoxesWithDataInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowBoxesWithDataInMultiBoxView = showBoxesWithDataInMultiboxView.Checked;
                if (_appSettings.ShowBoxesWithDataInMultiBoxView) _appSettings.HideBoxesWithDataInMultiBoxView = false;
                DrawPageLayouts();
            };
            allAndDataCheckBoxesLayout.AddView(showBoxesWithDataInMultiboxView);
            _overviewFiltersLayout.AddView(allAndDataCheckBoxesLayout);

            var breedingChanceFilterLayout = new LinearLayout(this);
            CheckBox showNoBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "NO",
                Checked = _appSettings.ShowNoBoxesInMultiBoxView,
            };
            showNoBoxesInMultiboxView.SetTextColor(Color.Black);
            showNoBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowNoBoxesInMultiBoxView = showNoBoxesInMultiboxView.Checked;
                if (_appSettings.ShowNoBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            breedingChanceFilterLayout.AddView(showNoBoxesInMultiboxView);

            CheckBox showUnlikelyBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "UNL",
                Checked = _appSettings.ShowUnlikleyBoxesInMultiBoxView,
            };
            showUnlikelyBoxesInMultiboxView.SetTextColor(Color.Black);
            showUnlikelyBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowUnlikleyBoxesInMultiBoxView = showUnlikelyBoxesInMultiboxView.Checked;
                if (_appSettings.ShowUnlikleyBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            breedingChanceFilterLayout.AddView(showUnlikelyBoxesInMultiboxView);

            CheckBox showPotentialBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "POT",
                Checked = _appSettings.ShowPotentialBoxesInMultiBoxView,
            };
            showPotentialBoxesInMultiboxView.SetTextColor(Color.Black);
            showPotentialBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowPotentialBoxesInMultiBoxView = showPotentialBoxesInMultiboxView.Checked;
                if (_appSettings.ShowPotentialBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            breedingChanceFilterLayout.AddView(showPotentialBoxesInMultiboxView);

            CheckBox showConfidentBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "CON",
                Checked = _appSettings.ShowConfidentBoxesInMultiBoxView,
            };
            showConfidentBoxesInMultiboxView.SetTextColor(Color.Black);
            showConfidentBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowConfidentBoxesInMultiBoxView = showConfidentBoxesInMultiboxView.Checked;
                if (_appSettings.ShowConfidentBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            breedingChanceFilterLayout.AddView(showConfidentBoxesInMultiboxView);

            CheckBox showBreedingBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "BR",
                Checked = _appSettings.ShowBreedingBoxesInMultiBoxView,
            };
            showBreedingBoxesInMultiboxView.SetTextColor(Color.Black);
            showBreedingBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowBreedingBoxesInMultiBoxView = showBreedingBoxesInMultiboxView.Checked;
                if (_appSettings.ShowBreedingBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            breedingChanceFilterLayout.AddView(showBreedingBoxesInMultiboxView);
            _overviewFiltersLayout.AddView(breedingChanceFilterLayout);

            var specialBoxFilterLayout = new LinearLayout(this);

            CheckBox showBoxesWithNotesInMultiboxView = new CheckBox(this)
            {
                Text = "Has notes",
                Checked = _appSettings.showBoxesWithNotesInMultiboxView
            };
            showBoxesWithNotesInMultiboxView.SetTextColor(Color.Black);
            showBoxesWithNotesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.showBoxesWithNotesInMultiboxView = showBoxesWithNotesInMultiboxView.Checked;
                if (_appSettings.showBoxesWithNotesInMultiboxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            specialBoxFilterLayout.AddView(showBoxesWithNotesInMultiboxView);

            CheckBox showSpecialBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "has label",
                Checked = _appSettings.ShowInterestingBoxesInMultiBoxView
            };
            showSpecialBoxesInMultiboxView.SetTextColor(Color.Black);
            showSpecialBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowInterestingBoxesInMultiBoxView = showSpecialBoxesInMultiboxView.Checked;
                if (_appSettings.ShowInterestingBoxesInMultiBoxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            specialBoxFilterLayout.AddView(showSpecialBoxesInMultiboxView);

            CheckBox showSingleEggBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "Single egg",
                Checked = _appSettings.ShowSingleEggBoxesInMultiboxView
            };
            showSingleEggBoxesInMultiboxView.SetPadding(0, 0, 0, 0);
            showSingleEggBoxesInMultiboxView.SetTextColor(Color.Black);
            showSingleEggBoxesInMultiboxView.Click += (s, e) =>
            {
                _appSettings.ShowSingleEggBoxesInMultiboxView = showSingleEggBoxesInMultiboxView.Checked;
                if (_appSettings.ShowSingleEggBoxesInMultiboxView) _appSettings.ShowAllBoxesInMultiBoxView = false;
                DrawPageLayouts();
            };
            specialBoxFilterLayout.AddView(showSingleEggBoxesInMultiboxView);
            _overviewFiltersLayout.AddView(specialBoxFilterLayout);

            TextView hideBoxesTitle = new TextView(this)
            {
                Text = "Hide Boxes",
                TextSize = 16,
                Gravity = GravityFlags.Center,

            };
            hideBoxesTitle.SetTypeface(null, TypefaceStyle.Bold);
            hideBoxesTitle.SetTextColor(Color.Black);
            _overviewFiltersLayout.AddView(hideBoxesTitle);

            var hideBoxesLayout = new LinearLayout(this);
            CheckBox hideBoxesWithNoDataInMultiboxView = new CheckBox(this)
            {
                Text = "With data",
                Checked = _appSettings.HideBoxesWithDataInMultiBoxView
            };
            hideBoxesWithNoDataInMultiboxView.SetTextColor(Color.Black);
            hideBoxesWithNoDataInMultiboxView.Click += (s, e) =>
            {
                _appSettings.HideBoxesWithDataInMultiBoxView = hideBoxesWithNoDataInMultiboxView.Checked;
                if (_appSettings.HideBoxesWithDataInMultiBoxView) _appSettings.ShowBoxesWithDataInMultiBoxView = false;
                DrawPageLayouts();
            };
            hideBoxesLayout.AddView(hideBoxesWithNoDataInMultiboxView);

            CheckBox hideDCMInMultiboxView = new CheckBox(this)
            {
                Text = "Decomissioned",
                Checked = _appSettings.HideDCMInMultiBoxView
            };
            hideDCMInMultiboxView.SetTextColor(Color.Black);
            hideDCMInMultiboxView.Click += (s, e) =>
            {
                _appSettings.HideDCMInMultiBoxView = hideDCMInMultiboxView.Checked;
                DrawPageLayouts();
            };
            hideBoxesLayout.AddView(hideDCMInMultiboxView);

            CheckBox hideBeforeCurrentCheckbox = new CheckBox(this)
            {
                Text = "< current",
                Checked = _appSettings.HideBeforeCurrentInMultiBoxView
            };
            hideBeforeCurrentCheckbox.SetTextColor(Color.Black);
            hideBeforeCurrentCheckbox.Click += (s, e) =>
            {
                _appSettings.HideBeforeCurrentInMultiBoxView = hideBeforeCurrentCheckbox.Checked;
                DrawPageLayouts();
            };
            hideBoxesLayout.AddView(hideBeforeCurrentCheckbox);
            _overviewFiltersLayout.AddView(hideBoxesLayout);

            //Navigate Monitors
            var browseOtherMonitorsLayout = new LinearLayout(this);
            var navigationButtonLayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            navigationButtonLayoutParameters.SetMargins(8, 16, 8, 16);

            _gotoPreviousMonitorButton = _uiFactory.CreateStyledButton("← Prev monitor", UIFactory.PRIMARY_BLUE);
            _gotoPreviousMonitorButton.LayoutParameters = navigationButtonLayoutParameters;
            _gotoPreviousMonitorButton.Click += (s, e) =>
            {
                if (_appSettings.CurrentlyVisibleMonitor < _allMonitorData.Count - 1)
                    _appSettings.CurrentlyVisibleMonitor++;
                _appSettings.CurrentlyVisibleMonitor = Math.Min(_appSettings.CurrentlyVisibleMonitor, _allMonitorData.Count - 1);
                _appSettings.ActiveSessionLocalTimeStamp = getLocalDateTime(_allMonitorData[_appSettings.CurrentlyVisibleMonitor]);
                _appSettings.ActiveSessionTimeStampActive = true;
                DrawPageLayouts();
            };
            bool olderMonitorAvailable = _allMonitorData.Count > _appSettings.CurrentlyVisibleMonitor + 1;
            SetEnabledRecursive(_gotoPreviousMonitorButton, olderMonitorAvailable, olderMonitorAvailable ? 1.0f : 0.5f);
            browseOtherMonitorsLayout.AddView(_gotoPreviousMonitorButton);
            _gotoNextMonitorButton = _uiFactory.CreateStyledButton("Next monitor →", UIFactory.PRIMARY_BLUE);
            _gotoNextMonitorButton.LayoutParameters = navigationButtonLayoutParameters;
            _gotoNextMonitorButton.Click += (s, e) =>
            {
                if (_appSettings.CurrentlyVisibleMonitor > 0)
                    _appSettings.CurrentlyVisibleMonitor--;
                _appSettings.CurrentlyVisibleMonitor = Math.Max(_appSettings.CurrentlyVisibleMonitor, 0);
                if (_appSettings.CurrentlyVisibleMonitor != 0 && _allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor))
                    _appSettings.ActiveSessionLocalTimeStamp = getLocalDateTime(_allMonitorData[_appSettings.CurrentlyVisibleMonitor]);
                else
                    _appSettings.ActiveSessionTimeStampActive = false;
                DrawPageLayouts();
            };
            bool newerMonitorAvailable = _appSettings.CurrentlyVisibleMonitor != 0;
            SetEnabledRecursive(_gotoNextMonitorButton, newerMonitorAvailable, newerMonitorAvailable ? 1.0f : 0.5f);
            browseOtherMonitorsLayout.AddView(_gotoNextMonitorButton);
            _showLatestMonitorButton = _uiFactory.CreateStyledButton("Latest →|", UIFactory.PRIMARY_BLUE);
            _showLatestMonitorButton.LayoutParameters = navigationButtonLayoutParameters;
            _showLatestMonitorButton.Click += (s, e) =>
            {
                _appSettings.CurrentlyVisibleMonitor = 0;
                _appSettings.ActiveSessionTimeStampActive = false;
                DrawPageLayouts();
            };
            SetEnabledRecursive(_showLatestMonitorButton, newerMonitorAvailable, newerMonitorAvailable ? 1.0f : 0.5f);
            browseOtherMonitorsLayout.AddView(_showLatestMonitorButton);
            _overviewFiltersLayout.AddView(browseOtherMonitorsLayout);

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
                var olderBoxDatas = DataStorageService.getOlderBoxDatas(_allMonitorData, _appSettings.CurrentlyVisibleMonitor, boxName);
                string nrfPercentageString = olderBoxDatas.Count > 0 && olderBoxDatas.First().Eggs > 0 ? olderBoxDatas.Count(x => x.Adults == 0 && x.Eggs > 0) + "/" + olderBoxDatas.Count(x => x.Eggs > 0) : "0";
                if (boxName == "92")
                    ;

                BoxData mostRecentBoxData = new BoxData();

                if (olderBoxDatas.Count > 0)
                    mostRecentBoxData = olderBoxDatas.First();

                bool currentBoxDataFound = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.TryGetValue(boxName, out BoxData currentBoxData);
                if (currentBoxDataFound && currentBoxData != null)
                    mostRecentBoxData = currentBoxData;

                string persistentNotes = DataStorageService.getPersistentNotes(olderBoxDatas);
                bool showBox = _appSettings.ShowAllBoxesInMultiBoxView
                            || _appSettings.ShowBoxesWithDataInMultiBoxView && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(boxName)
                            || _appSettings.ShowBreedingBoxesInMultiBoxView && mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance.Equals("BR")
                            || _appSettings.ShowConfidentBoxesInMultiBoxView && mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance.Equals("CON")
                            || _appSettings.ShowPotentialBoxesInMultiBoxView && mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance.Equals("POT")
                            || _appSettings.ShowUnlikleyBoxesInMultiBoxView && mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance.Equals("UNL")
                            || _appSettings.ShowNoBoxesInMultiBoxView && mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance.Equals("NO")
                            || _appSettings.ShowBoxesWithNotesInMultiboxView && mostRecentBoxData != null && !String.IsNullOrWhiteSpace(mostRecentBoxData.Notes)
                            || _appSettings.ShowInterestingBoxesInMultiBoxView && (mostRecentBoxData.Eggs > 0 && !nrfPercentageString.StartsWith("0") || !string.IsNullOrWhiteSpace(persistentNotes))
                            || _appSettings.ShowSingleEggBoxesInMultiboxView && (mostRecentBoxData.Eggs == 1);

                bool hideBoxWithData = _appSettings.HideBoxesWithDataInMultiBoxView && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(boxName);
                bool hideDCM = _appSettings.HideDCMInMultiBoxView && ((mostRecentBoxData.BreedingChance != null && mostRecentBoxData.BreedingChance == "DCM"));
                bool hideBeforeCurrent = _appSettings.HideBeforeCurrentInMultiBoxView && _currentBoxIndex > _boxNamesAndIndexes[boxName];

                if (showBox && !hideBoxWithData && !hideDCM && !hideBeforeCurrent)
                {
                    View? card;
                    if (currentBoxDataFound)
                        card = CreateBoxSummaryCard(boxName, currentBoxData, _boxNamesAndIndexes[boxName] == _currentBoxIndex, olderBoxDatas, nrfPercentageString);
                    else
                        card = CreateBoxSummaryCard(boxName, null, _boxNamesAndIndexes[boxName] == _currentBoxIndex, olderBoxDatas, nrfPercentageString);
                    currentRow?.AddView(card);
                    visibleBoxCount++;
                }
            }
            if (visibleBoxCount == 0)
            {
                var empty = new TextView(this) { Text = "No boxes to show." };
                _multiBoxViewCard.AddView(empty);
            }
        }
        private DateTime getLocalDateTime(MonitorDetails monitorDetails)
        {
            foreach (var boxData in monitorDetails.BoxData.Values)
            {
                if (boxData.whenDataCollectedUtc.Year > 2010)
                    return boxData.whenDataCollectedUtc.ToLocalTime();
                foreach (var scan in boxData.ScannedIds)
                    if (scan.Timestamp.Year > 2010)
                        return scan.Timestamp.ToLocalTime();
            }
            return DateTime.MinValue;
        }
        private View? CreateBoxSummaryCard(string boxName, BoxData? thisBoxData, bool selected, List<BoxData> olderBoxDatas, string nrfPercentageString)
        {
            bool currentExists = thisBoxData != null;
            if (!currentExists && olderBoxDatas.Count > 0)
            {

                thisBoxData = olderBoxDatas.First();
                olderBoxDatas.RemoveAt(0);
            }

            var card = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            card.SetPadding(5, 5, 5, 5);
            var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1f);
            cardParams.SetMargins(5, 5, 5, 5);
            card.LayoutParameters = cardParams;
            bool differenceFound = false;

            card.Click += (sender, e) =>
            {
                JumpToBox(boxName);
                ScrollToTop();
            };

            if (selected)
                ;

            if (!currentExists)
            {
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, UIFactory.WARNING_YELLOW, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else if (olderBoxDatas.Count > 0
                && thisBoxData.Eggs + thisBoxData.Chicks < olderBoxDatas.First().Eggs + olderBoxDatas.First().Chicks)
            {
                differenceFound = true;
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.DANGER_RED, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else if (thisBoxData.BreedingChance != "BR" && thisBoxData.Eggs + thisBoxData.Chicks > 0)
            {
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.DANGER_RED, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else if (olderBoxDatas.Count > 0
                && (thisBoxData.Eggs != olderBoxDatas.First().Eggs || thisBoxData.Chicks != olderBoxDatas.First().Chicks))
            {
                differenceFound = true;
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.PRIMARY_BLUE, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }
            else
            {
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 3, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }

            var title = new TextView(this)
            {
                Text = $"Box {boxName}",
                Gravity = GravityFlags.Center,
                TextSize = 18
            };
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            title.SetTextColor(Color.Black);
            card.AddView(title);

            if (thisBoxData == null)
                return card;
            
            var summary = new TextView(this)
            {
                Text = $"{string.Concat(Enumerable.Repeat("🐧", thisBoxData.Adults))}" +
                    $"{string.Concat(Enumerable.Repeat("🥚", thisBoxData.Eggs))}" +
                    $"{string.Concat(Enumerable.Repeat("🐣", thisBoxData.Chicks))}",
                Gravity = GravityFlags.Center,
                TextSize = 14
            };
            if (boxName == "2")
                ;
            if (differenceFound ) 
            {
                var previousChicks = olderBoxDatas.First().Chicks;
                var previousEggs = olderBoxDatas.First().Eggs;
                if (differenceFound && previousChicks + previousEggs > 0 && (thisBoxData.Eggs != previousEggs || thisBoxData.Chicks != previousChicks))
                    summary.Text += $"({string.Concat(Enumerable.Repeat("🥚", previousEggs))}{string.Concat(Enumerable.Repeat("🐣", previousChicks))})";
            }

            if (thisBoxData.BreedingChance != null && thisBoxData.BreedingChance != "BR" || (thisBoxData.BreedingChance == "BR" && thisBoxData.Chicks + thisBoxData.Eggs == 0))
                summary.Text += $"{thisBoxData.BreedingChance}";
            
            
            string calculatedBreedingStatusString = DataStorageService.GetBoxBreedingStatusString(boxName, thisBoxData, olderBoxDatas);
            if (!string.IsNullOrWhiteSpace(calculatedBreedingStatusString))
                summary.Text += "\n" + calculatedBreedingStatusString;
            else if (_remoteBreedingDates != null && _remoteBreedingDates.ContainsKey(boxName))
                summary.Text += "\nB:" + _remoteBreedingDates[boxName].breedingDateStatus();
            
            summary.SetTextColor(Color.Black);

            string gateStatus = thisBoxData.GateStatus;
            string notes = string.IsNullOrWhiteSpace(thisBoxData.Notes) ? "" : "notes";
            string persistentNotes = DataStorageService.getPersistentNotes(olderBoxDatas);
            notes += !string.IsNullOrEmpty(persistentNotes) ? $" ({persistentNotes})" : "";
            notes += !nrfPercentageString.StartsWith("0") ? $" (NRF:{nrfPercentageString})" : "";
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
            gate_and_notes.SetTextColor(Color.DarkGray);

            if(!string.IsNullOrEmpty(summary.Text)) card.AddView(summary);
            if(!string.IsNullOrEmpty(gate_and_notes.Text)) card.AddView(gate_and_notes);
            return card;
        }
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
                Text = "Enable bluetooth",
            };
            _isBluetoothEnabledCheckBox.SetTextColor(Color.Black);
            _isBluetoothEnabledCheckBox.CheckedChange += (s, e) =>
            {
                if (_isBluetoothEnabledCheckBox.Checked)
                {
                    InitializeBluetooth();
                    _appSettings.IsBlueToothEnabled = true;
                }
                else
                {
                    _appSettings.IsBlueToothEnabled = false;
                    _bluetoothManager?.Dispose();
                    _bluetoothManager = null;
                    UpdateStatusText("Bluetooth Disabled");
                }
            };
            _isBluetoothEnabledCheckBox.Checked = _appSettings.IsBlueToothEnabled;
            _settingsCard.AddView(_isBluetoothEnabledCheckBox);

            _setTimeActiveSessionCheckBox = new CheckBox(this)
            {
                Text = "Set time for monitor: " + _appSettings.ActiveSessionLocalTimeStamp.ToString("HH:mm, d MMM yyyy"),
                Checked = _appSettings.ActiveSessionTimeStampActive
            };
            _setTimeActiveSessionCheckBox.SetTextColor(Color.Black);
            _setTimeActiveSessionCheckBox.SetPadding(0, 0, 0, 0);
            _setTimeActiveSessionCheckBox.CheckedChange += (s, e) => { };
            _setTimeActiveSessionCheckBox.Click += (s, e) =>
            {
                _appSettings.ActiveSessionTimeStampActive = _setTimeActiveSessionCheckBox.Checked;
                if (_setTimeActiveSessionCheckBox.Checked)
                {
                    var timePicker = new TimePickerDialog(
                        this,
                        Android.Resource.Style.ThemeHoloLightDialogNoActionBar, 
                        (s, ex) =>
                        {
                            _appSettings.ActiveSessionLocalTimeStamp = _appSettings.ActiveSessionLocalTimeStamp.Date.AddHours(ex.HourOfDay).AddMinutes(ex.Minute);
                            _setTimeActiveSessionCheckBox.Text = "Set time for monitor: " + _appSettings.ActiveSessionLocalTimeStamp.ToString("HH:mm, d MMM yyyy");
                        },
                        _appSettings.ActiveSessionLocalTimeStamp.Hour,
                        _appSettings.ActiveSessionLocalTimeStamp.Minute,
                        true // 24 hour format
                    );
                    var datePicker = new DatePickerDialog(
                        this,
                        Android.Resource.Style.ThemeHoloLightDialogNoActionBar,
                        (sender, e) =>
                            {
                                _appSettings.ActiveSessionLocalTimeStamp = e.Date + _appSettings.ActiveSessionLocalTimeStamp.TimeOfDay;
                                // e.Date is the selected date
                                Toast.MakeText(this, $"Date picked: {e.Date.ToShortDateString()}", ToastLength.Short).Show();
                                timePicker.Show();
                            },
                        _appSettings.ActiveSessionLocalTimeStamp.Year,
                        _appSettings.ActiveSessionLocalTimeStamp.Month - 1, // Month is 0-based in Android
                        _appSettings.ActiveSessionLocalTimeStamp.Day);
                    datePicker.Show();
                }
            };
            _settingsCard.AddView(_setTimeActiveSessionCheckBox);

            Button toggleOverview = _uiFactory.CreateStyledButton("Toggle overview visibility", UIFactory.PRIMARY_BLUE);
            toggleOverview.Click += (s, e) => _multiBoxViewCard.Visibility = _multiBoxViewCard.Visibility.Equals(ViewStates.Visible) ? ViewStates.Gone : ViewStates.Visible;
            _settingsCard.AddView(toggleOverview);

            // Box tag delete button visibility setting
            var showBoxTagDeleteCheckBox = new CheckBox(this)
            {
                Text = "Show box tag delete button",
                Checked = _appSettings.ShowBoxTagDeleteButton
            };
            showBoxTagDeleteCheckBox.SetTextColor(Color.Black);
            showBoxTagDeleteCheckBox.Click += (s, e) =>
            {
                _appSettings.ShowBoxTagDeleteButton = showBoxTagDeleteCheckBox.Checked;
                DrawPageLayouts(); // Refresh UI to show/hide delete button
            };
            _settingsCard.AddView(showBoxTagDeleteCheckBox);

            LinearLayout enterBoxSetsLayout = new LinearLayout(this) ;
            enterBoxSetsLayout.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            var enterSetsTitle = new TextView(this)
            {
                Text = "Box sets: ",
                TextSize = 16
            };
            enterSetsTitle.SetTextColor(UIFactory.TEXT_PRIMARY);
            enterSetsTitle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var notesLabelParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            notesLabelParams.SetMargins(8,8,8,8);
            enterSetsTitle.LayoutParameters = notesLabelParams;
            enterBoxSetsLayout.AddView(enterSetsTitle);

            EditText enterSetStringEditText = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences,
                Hint = "{1-150,AA-AC},{N1-N6}..."
            };
            enterSetStringEditText.SetTextColor(UIFactory.TEXT_PRIMARY);
            enterSetStringEditText.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            enterSetStringEditText.SetPadding(16, 16, 16, 16);
            enterSetStringEditText.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var notesEditParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1.0f);
            notesEditParams.SetMargins(8,8,8,8);
            enterSetStringEditText.LayoutParameters = notesEditParams;
            enterSetStringEditText.Text = _appSettings.AllBoxSetsString ?? "";
            enterBoxSetsLayout.AddView(enterSetStringEditText);

            var applyButton = new Button(this)
            {
                Text = "Apply",
                TextSize = 12
            };
            applyButton.Click += (s, e) =>
            {
                _appSettings.AllBoxSetsString = enterSetStringEditText.Text;
                UpdateBoxSetsSelector();
            };
            applyButton.SetTextColor(Color.White);
            applyButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            applyButton.SetPadding(12, 8, 12, 8);
            applyButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.SUCCESS_GREEN, 8);
            applyButton.SetAllCaps(false);
            var applyButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            //applyButtonParams.SetMargins(8, 0, 4, 0);
            applyButton.LayoutParameters = applyButtonParams;
            enterBoxSetsLayout.AddView(applyButton);
            _settingsCard.AddView(enterBoxSetsLayout);

            LinearLayout selectBoxSetsLayout = new LinearLayout(this);
            selectBoxSetsLayout.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            var selectSetsTitle = new TextView(this)
            {
                Text = "Select box set(s):",
                TextSize = 16,
                Gravity = GravityFlags.CenterHorizontal
            };
            selectSetsTitle.SetTextColor(UIFactory.TEXT_PRIMARY);
            selectSetsTitle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var notesLabelParams1 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            notesLabelParams1.SetMargins(8,8,8,8);
            selectSetsTitle.LayoutParameters = notesLabelParams1;
            selectBoxSetsLayout.AddView(selectSetsTitle);

            _boxSetSelector = _uiFactory.CreateSpinner(new List<string>());
            _boxSetSelector.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            UpdateBoxSetsSelector();
            _boxSetSelector.ItemSelected += (s, e) =>
            {
                _appSettings.BoxSetString = _boxSetSelector.SelectedItem == null ? "All" : (string)_boxSetSelector.SelectedItem;
                CreateBoxSetsDictionary();
                JumpToBox(_boxNamesAndIndexes.First().Key);
                DrawPageLayouts();
            };
            selectBoxSetsLayout.AddView(_boxSetSelector);
            _settingsCard.AddView(selectBoxSetsLayout);
        }

        private void UpdateBoxSetsSelector()
        {
            //Update box sets selector spinner
            List<string> boxSets = (_appSettings.AllBoxSetsString ?? "").Split(new string[] { "},{", "{", "}" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            boxSets.Add("All");
            ArrayAdapter<string> adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, boxSets);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _boxSetSelector.Adapter = adapter;
            if (_appSettings.BoxSetString != null && boxSets.Contains(_appSettings.BoxSetString))
                _boxSetSelector.SetSelection(boxSets.IndexOf(_appSettings.BoxSetString));
        }

        private void OnScrollViewTouch(object? sender, View.TouchEventArgs e)
        {
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
        private LinearLayout CreateNavigationLayout()
        {
            var layout = new LinearLayout(this);
            layout.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            _prevBoxButton = _uiFactory.CreateStyledButton("← Prev box", UIFactory.PRIMARY_BLUE);
            _prevBoxButton.Click += OnPrevBoxClick;
            var buttonParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            buttonParams.SetMargins(8, 16, 8, 16);
            _prevBoxButton.LayoutParameters = buttonParams;

            _selectBoxButton = _uiFactory.CreateStyledButton("Select box", UIFactory.PRIMARY_BLUE);
            _selectBoxButton.Click += (s,e) => ShowBoxJumpDialog();
            _selectBoxButton.LayoutParameters = buttonParams;

            _nextBoxButton = _uiFactory.CreateStyledButton("Next box →", UIFactory.PRIMARY_BLUE);
            _nextBoxButton.Click += OnNextBoxClick;
            _nextBoxButton.LayoutParameters = buttonParams;

            layout.AddView(_prevBoxButton);
            layout.AddView(_selectBoxButton);
            layout.AddView(_nextBoxButton);

            return layout;
        }
        internal void DrawPageLayouts()
        {
            new Handler(Looper.MainLooper).Post(() =>
                {
                    if (_appSettings.CurrentlyVisibleMonitor >= _allMonitorData.Count)
                        _appSettings.CurrentlyVisibleMonitor = Math.Max(0, _allMonitorData.Count - 1);

                    if (_appSettings.CurrentlyVisibleMonitor < 0)
                        _appSettings.CurrentlyVisibleMonitor = 0;

                    // update settings card
                    _setTimeActiveSessionCheckBox.Checked = _appSettings.ActiveSessionTimeStampActive;
                    _setTimeActiveSessionCheckBox.Text = "Set time for monitor: " + _appSettings.ActiveSessionLocalTimeStamp.ToString("HH:mm, d MMM yyyy");

                    ///Single Box Card
                    // Update lock icon
                    if (_dataCardLockIconView != null)
                    {
                        _dataCardLockIconView.SetColorFilter(null);
                        if (_allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor) && !_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName ) && _isBoxLocked)
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
                        _dataCardTitleText.Text = $"Box {_currentBoxName}";

                    // Update box tag delete button visibility
                    if (_deleteBoxTagButton != null)
                    {
                        bool boxHasTag = _boxTags != null && _boxTags.ContainsKey(_currentBoxName);
                        _deleteBoxTagButton.Visibility = (_appSettings.ShowBoxTagDeleteButton && boxHasTag) ? ViewStates.Visible : ViewStates.Gone;
                    }

                    _boxSavedTimeTextView.Text = _allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor) && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName) ?
                        _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName].whenDataCollectedUtc.ToLocalTime().ToString("d MMM yyyy\nHH:mm") : "";
                    _boxSavedTimeTextView.SetTextColor(Color.Black);
                    _boxSavedTimeTextView.Gravity = GravityFlags.Right;

                    _interestingBoxTextView.Visibility = ViewStates.Gone;
                    string persistentNotes = DataStorageService.getPersistentNotes(DataStorageService.getOlderBoxDatas(_allMonitorData, _appSettings.CurrentlyVisibleMonitor, _currentBoxName));
                    if (!string.IsNullOrWhiteSpace(persistentNotes))
                    {
                        _interestingBoxTextView.Text = "💡 Note: " + persistentNotes;
                        _interestingBoxTextView.Visibility = ViewStates.Visible;
                        _interestingBoxTextView.Gravity = GravityFlags.Center;
                        _interestingBoxTextView.SetBackgroundColor(UIFactory.LIGHT_GRAY);
                        _interestingBoxTextView.SetTextColor(UIFactory.PRIMARY_BLUE);
                    }

                    var editTexts = new[] { _adultsEditText, _eggsEditText, _chicksEditText, _notesEditText };

                    foreach (var editText in editTexts)
                    {
                        if (editText != null) editText[0].TextChanged -= OnDataChanged;
                    }

                    if (_allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor) && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName))
                    {
                        var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName];
                        if (_adultsEditText != null) _adultsEditText[0].Text = boxData.Adults.ToString();
                        if (_eggsEditText != null) _eggsEditText[0].Text = boxData.Eggs.ToString();
                        if (_chicksEditText != null) _chicksEditText[0].Text = boxData.Chicks.ToString();
                        SetSpinnerStatus(_gateStatusSpinner[0], boxData.GateStatus);
                        if (_notesEditText != null) _notesEditText[0].Text = boxData.Notes;
                        buildScannedIdsLayout(boxData.ScannedIds);
                        SetSpinnerStatus(_breedingChanceSpinner[0], !string.IsNullOrWhiteSpace(boxData.BreedingChance) ? boxData.BreedingChance : "");
                    }
                    else
                    {
                        if (_adultsEditText != null) _adultsEditText[0].Text = "0";
                        if (_eggsEditText != null) _eggsEditText[0].Text = "0";
                        if (_chicksEditText != null) _chicksEditText[0].Text = "0";
                        SetSpinnerStatus(_gateStatusSpinner[0], null);
                        if (_notesEditText != null) _notesEditText[0].Text = "";
                        buildScannedIdsLayout(new List<ScanRecord>());

                        SetSpinnerStatus(_breedingChanceSpinner[0], "");
                        var olderBoxDatas = DataStorageService.getOlderBoxDatas(_allMonitorData, _appSettings.CurrentlyVisibleMonitor, _currentBoxName);
                        int iterator = -1;
                        while (iterator++ < olderBoxDatas.Count-1)
                            if (!string.IsNullOrEmpty(olderBoxDatas[iterator].BreedingChance))
                            {
                                SetSpinnerStatus(_breedingChanceSpinner[0], olderBoxDatas[iterator].BreedingChance);
                                break;
                            }
                    }

                    foreach (var editText in editTexts)
                        if (editText != null) editText[0].TextChanged += OnDataChanged;

                    //disable/enable UI elememts according to _isBoxLocked
                    for (int i = 0; i < _topButtonLayout.ChildCount; i++)
                    {
                        Button child = (Button) _topButtonLayout.GetChildAt(i);
                        SetEnabledRecursive(child, _isBoxLocked, _isBoxLocked ? 1.0f : 0.5f);
                        if ((child.Text.Equals("Clear all") || child.Text.Equals("Delete")))
                        {
                            child.Text = _appSettings.CurrentlyVisibleMonitor == 0 ? "Clear all":"Delete" ;
                            if (!_isBoxLocked || _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Count == 0)
                                SetEnabledRecursive(child, false, 0.5f); 
                        }
                        else if (_isBoxLocked && child.Text.Equals("Clear Box") && !_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName))
                            SetEnabledRecursive(child, false, 0.5f);
                    }

                    // title Layout "Box n" is item 0, which we don't want to disable!
                    for (int i = 1; i < _singleBoxDataOuterLayout.ChildCount; i++)
                    {
                        var child = _singleBoxDataOuterLayout.GetChildAt(i);
                        SetEnabledRecursive(child, !_isBoxLocked, _isBoxLocked ? 0.8f : 1.0f);
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
                        button.Enabled = _isBoxLocked && canGo;
                        button.Alpha = button.Enabled ? 2.0f : 0.5f; // Grey out when unlocked
                    }
                    createMultiBoxViewCard();
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
            var expandSingleBoxImageButton = new ImageButton(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            };
            expandSingleBoxImageButton.SetImageResource(Resource.Drawable.unfold);
            expandSingleBoxImageButton.SetBackgroundColor(Color.Transparent);
            expandSingleBoxImageButton.Click += (s, e) =>
            {
                if (_singleBoxDataContentLayout.Visibility == ViewStates.Gone)
                {
                    _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                    _boxNavigationButtonsLayout.Visibility = ViewStates.Visible;
                    expandSingleBoxImageButton.SetImageResource(Resource.Drawable.fold);
                }
                else
                {
                    _singleBoxDataContentLayout.Visibility = ViewStates.Gone;
                    _boxNavigationButtonsLayout.Visibility = ViewStates.Gone;
                    expandSingleBoxImageButton.SetImageResource(Resource.Drawable.unfold);
                }
            };
            _singleBoxDataTitleLayout.AddView(expandSingleBoxImageButton);
            _singleBoxDataTitleLayout.Click += (sender, e) =>
            {
                _isBoxLocked = !_isBoxLocked;
                if (!_isBoxLocked)
                {
                    DrawPageLayouts();
                    _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
                    _highOffspringCountConfirmed = false;
                }
                else
                {
                    if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName) && dataCardHasZeroData())
                    {
                        ShowEmptyBoxDialog(() =>
                        {
                            SaveCurrentBoxData();
                            DrawPageLayouts();
                        }, () =>
                        {
                            DrawPageLayouts();
                        });
                    }
                    else
                    {
                        SaveCurrentBoxData();
                        DrawPageLayouts();
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

            // Add a spacer that expands to fill available space
            var spacer1 = new View(this);
            spacer1.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            _singleBoxDataTitleLayout.AddView(spacer1);

            _boxSavedTimeTextView = new TextView(this);
            _singleBoxDataTitleLayout.AddView(_boxSavedTimeTextView);

            // Box tag delete button (visible only if setting is enabled and box has a tag)
            _deleteBoxTagButton = new Button(this)
            {
                Text = "✕",
                Visibility = ViewStates.Gone
            };
            _deleteBoxTagButton.SetTextColor(Color.White);
            _deleteBoxTagButton.SetBackgroundColor(UIFactory.ERROR_RED);
            var deleteButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            deleteButtonParams.SetMargins(8, 0, 0, 0);
            _deleteBoxTagButton.LayoutParameters = deleteButtonParams;
            _deleteBoxTagButton.Click += (s, e) =>
            {
                var internalPath = this.FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(internalPath))
                {
                    BoxTagService.RemoveBoxTag(_boxTags, _currentBoxName, internalPath);
                    Toast.MakeText(this, $"Box tag removed from Box {_currentBoxName}", ToastLength.Short)?.Show();
                    DrawPageLayouts(); // Refresh to hide the button
                }
            };
            _singleBoxDataTitleLayout.AddView(_deleteBoxTagButton);

            _singleBoxDataOuterLayout.AddView(_singleBoxDataTitleLayout);

            _singleBoxDataContentLayout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            _interestingBoxTextView = new TextView(this);
            _interestingBoxTextView.Visibility = ViewStates.Gone;
            _singleBoxDataContentLayout.AddView(_interestingBoxTextView);

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
            var breedingChance = _uiFactory.CreateDataLabel("Confidence");
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
            _breedingChanceSpinner.Add(new Spinner(this));
            _breedingChanceSpinner[0].SetPadding(16, 20, 16, 20);
            _breedingChanceSpinner[0].Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);

            // Set the spinner to have the same layout weight as the input fields
            var spinnerParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            spinnerParams.SetMargins(4, 0, 4, 0);
            _breedingChanceSpinner[0].LayoutParameters = spinnerParams;
            _breedingChanceSpinner[0].SetGravity(GravityFlags.Center);
            List<string> items = new List<string> { "", "NO", "UNL", "POT", "CON", "BR", "DCM" };
            ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, items);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _breedingChanceSpinner[0].Adapter = adapter;
            string? breedingChanceString = "";
            try { breedingChanceString = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName].BreedingChance; }
            catch { }
            int breedingPercentageIndex = 0;
            if (breedingChanceString != null)
                breedingPercentageIndex = items.FindIndex(x => x == breedingChanceString);
            breedingPercentageIndex = Math.Max(0, breedingPercentageIndex);
            _breedingChanceSpinner[0].SetSelection(breedingPercentageIndex, false);
            _breedingChanceSpinner[0].ItemSelected += (s, e) =>
            {
                string selectedItem = items[e.Position];
                string status = _breedingChanceSpinner[0].SelectedItem.ToString();
            };
            _gateStatusSpinner = new List<Spinner?>();
            _gateStatusSpinner.Add(_uiFactory.CreateSpinner(new string[] { "", "Gate up", "Regate" }));
            _gateStatusSpinner[0].ItemSelected += (s, e) =>
            {
                string status = _gateStatusSpinner[0].SelectedItem.ToString();
                if (status.Equals("Gate up") || status.Equals("Regate"))
                {
                    SaveCurrentBoxData();
                    _isBoxLocked = true;
                    DrawPageLayouts();
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
                Hint = "Enter any additional notes...",
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

            // Add button to manage persistent notes
            var managePersistentNotesButton = new Button(this)
            {
                Text = "📌 Manage Persistent Notes"
            };
            managePersistentNotesButton.SetTextColor(Color.White);
            managePersistentNotesButton.SetBackgroundColor(UIFactory.PRIMARY_BLUE);
            var manageNotesButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            manageNotesButtonParams.SetMargins(0, 0, 0, 16);
            managePersistentNotesButton.LayoutParameters = manageNotesButtonParams;
            managePersistentNotesButton.Click += (s, e) => ShowPersistentNotesDialog();
            _singleBoxDataContentLayout.AddView(managePersistentNotesButton);
            _singleBoxDataOuterLayout.AddView(_singleBoxDataContentLayout);

            // Navigation card
            _boxNavigationButtonsLayout = CreateNavigationLayout();
            _singleBoxDataOuterLayout.AddView(_boxNavigationButtonsLayout);
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
        private void ShowEmptyBoxDialog(Action onConfirm, Action onCancel)
        {
            ShowConfirmationDialog(
                "Empty Box Confirmation",
                "Please confirm this box has been inspected and is empty",
                ("Confirm empty", onConfirm),
                ("Lock without saving", onCancel)
            );
        }
        private void OnClearBoxClick(object? sender, EventArgs e)
        {
            ShowConfirmationDialog(
                "Clear Box Data",
                "Clear data for box " + _currentBoxName + "?",
                ("Yes", () =>
                {
                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Remove(_currentBoxName);
                    SaveToAppDataDir();
                    DrawPageLayouts();
                }),
                ("No", () => { } ));
        }
        private void OnClearBoxesClick(object? s, EventArgs e)
        {
            if (_appSettings.CurrentlyVisibleMonitor==0)
            {
                ShowConfirmationDialog(
                    "Clear all data",
                    "Clear data for all boxes?",
                    ("Yes, clear all", new Action(() =>
                    {
                        _allMonitorData[0].BoxData.Clear();
                        _currentBoxIndex = 1;
                        ClearInternalStorageData();
                        SaveToAppDataDir(reportHome:false);
                        DrawPageLayouts();
                    })),
                    ("Cancel", new Action(() => { }))
                );
            }
            else //looking at an old monitor on the server. 
            {
                ShowConfirmationDialog(
                    "Delete monitoring data",
                    "Set this monitor to be ignored on the server?",
                    ("Yes, flag to be ignored", new Action(() =>
                    {
                        string question = "DeletePenguinMonitor:" + _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename + "~~~~" + _allMonitorData[_appSettings.CurrentlyVisibleMonitor].LastSaved.ToFileTimeUtc();
                        string response = Backend.RequestServerResponse(question);
                        Toast.MakeText(this, "Server response: " + response, ToastLength.Long).Show();
                        OnBirdStatsClick(s, e);
                    })),
                    ("Cancel", new Action(() => { }))
                );
            }
        }
        private void OnSaveDataClick(object? sender, EventArgs e)
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle("Save data")
                .SetMessage(GetMonitorSummaryText(0))
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
                .SetMessage(message)
                .SetPositiveButton(positiveButton.text, (s, e) => positiveButton.action())
                .SetCancelable(true)
                .Create();
            if (null != negativeButton)
                alertDialog.SetButton((int)DialogButtonType.Negative, negativeButton?.text, (s, e) => negativeButton?.action());
            alertDialog?.Show();
        }

        private void ShowPersistentNotesDialog()
        {
            // Get current persistent notes for this box
            var olderBoxDatas = DataStorageService.getOlderBoxDatas(_allMonitorData, _appSettings.CurrentlyVisibleMonitor, _currentBoxName);
            string persistentNotesString = DataStorageService.getPersistentNotes(olderBoxDatas);
            var currentNotes = new List<string>();
            if (!string.IsNullOrWhiteSpace(persistentNotesString))
            {
                currentNotes = persistentNotesString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            // Create main container
            var mainLayout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            mainLayout.SetPadding(40, 20, 40, 20);

            // Title for current notes
            var titleText = new TextView(this)
            {
                Text = "Current Persistent Notes:",
                TextSize = 14
            };
            titleText.SetTextColor(UIFactory.TEXT_PRIMARY);
            titleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var titleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            titleParams.SetMargins(0, 0, 0, 16);
            titleText.LayoutParameters = titleParams;
            mainLayout.AddView(titleText);

            // ScrollView for existing notes
            var notesScrollView = new ScrollView(this);
            var notesContainer = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            notesScrollView.AddView(notesContainer);
            var scrollParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f);
            scrollParams.SetMargins(0, 0, 0, 16);
            notesScrollView.LayoutParameters = scrollParams;

            // Add existing notes
            if (currentNotes.Count == 0)
            {
                var emptyText = new TextView(this)
                {
                    Text = "No persistent notes yet",
                    TextSize = 14
                };
                emptyText.SetTextColor(UIFactory.TEXT_SECONDARY);
                emptyText.SetPadding(8, 8, 8, 8);
                notesContainer.AddView(emptyText);
            }
            else
            {
                foreach (var note in currentNotes)
                {
                    var noteLayout = new LinearLayout(this)
                    {
                        Orientation = Orientation.Horizontal
                    };
                    var noteLayoutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                    noteLayoutParams.SetMargins(0, 4, 0, 4);
                    noteLayout.LayoutParameters = noteLayoutParams;

                    var noteText = new TextView(this)
                    {
                        Text = note,
                        TextSize = 14
                    };
                    noteText.SetTextColor(UIFactory.TEXT_PRIMARY);
                    noteText.SetPadding(12, 12, 12, 12);
                    noteText.SetBackgroundColor(UIFactory.LIGHTER_GRAY);
                    var noteTextParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f);
                    noteTextParams.SetMargins(0, 0, 8, 0);
                    noteText.LayoutParameters = noteTextParams;

                    var deleteButton = new Button(this)
                    {
                        Text = "✕"
                    };
                    deleteButton.SetTextColor(Color.White);
                    deleteButton.SetBackgroundColor(UIFactory.ERROR_RED);
                    var deleteParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    deleteButton.LayoutParameters = deleteParams;

                    string noteToRemove = note;
                    deleteButton.Click += (s, e) =>
                    {
                        // Remove this note by adding l-notename to the notes field
                        string currentNotesText = _notesEditText?[0].Text ?? "";
                        if (!currentNotesText.EndsWith(" "))
                            currentNotesText += " ";
                        currentNotesText += $"l-{noteToRemove} ";
                        _notesEditText[0].Text = currentNotesText;

                        SaveCurrentBoxData();
                        Toast.MakeText(this, $"Removed persistent note: {noteToRemove}", ToastLength.Short)?.Show();

                        // Close and reopen dialog to refresh
                        ShowPersistentNotesDialog();
                    };

                    noteLayout.AddView(noteText);
                    noteLayout.AddView(deleteButton);
                    notesContainer.AddView(noteLayout);
                }
            }

            mainLayout.AddView(notesScrollView);

            // Add new note section
            var addSectionLayout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            var addSectionParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            addSectionParams.SetMargins(0, 16, 0, 0);
            addSectionLayout.LayoutParameters = addSectionParams;

            var addLabel = new TextView(this)
            {
                Text = "Add New Persistent Note:",
                TextSize = 14
            };
            addLabel.SetTextColor(UIFactory.TEXT_PRIMARY);
            addLabel.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var addLabelParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            addLabelParams.SetMargins(0, 0, 0, 8);
            addLabel.LayoutParameters = addLabelParams;
            addSectionLayout.AddView(addLabel);

            var newNoteInput = new EditText(this)
            {
                Hint = "Enter note text...",
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapSentences
            };
            newNoteInput.SetTextColor(UIFactory.TEXT_PRIMARY);
            newNoteInput.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            newNoteInput.SetPadding(16, 16, 16, 16);
            newNoteInput.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var inputParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            inputParams.SetMargins(0, 0, 0, 8);
            newNoteInput.LayoutParameters = inputParams;
            addSectionLayout.AddView(newNoteInput);

            var addButton = new Button(this)
            {
                Text = "Add Note"
            };
            addButton.SetTextColor(Color.White);
            addButton.SetBackgroundColor(UIFactory.PRIMARY_BLUE);
            addButton.Click += (s, e) =>
            {
                string newNote = newNoteInput.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newNote))
                {
                    Toast.MakeText(this, "Please enter a note", ToastLength.Short)?.Show();
                    return;
                }

                // Replace spaces with underscores to make it a single token
                newNote = newNote.Replace(" ", "_");

                // Add this note by appending l=notename to the notes field
                string currentNotesText = _notesEditText?[0].Text ?? "";
                if (!currentNotesText.EndsWith(" "))
                    currentNotesText += " ";
                currentNotesText += $"l={newNote} ";
                _notesEditText[0].Text = currentNotesText;

                SaveCurrentBoxData();
                Toast.MakeText(this, $"Added persistent note: {newNote}", ToastLength.Short)?.Show();

                // Close and reopen dialog to refresh
                ShowPersistentNotesDialog();
            };
            addSectionLayout.AddView(addButton);

            mainLayout.AddView(addSectionLayout);

            // Create and show dialog
            var dialog = new AlertDialog.Builder(this)
                .SetTitle($"Persistent Notes - Box {_currentBoxName}")
                .SetView(mainLayout)
                .SetNegativeButton("Close", (s, e) => { })
                .Create();

            dialog?.Show();

            // Focus the input field and show keyboard
            newNoteInput.RequestFocus();
            var inputManager = (InputMethodManager?)GetSystemService(InputMethodService);
            inputManager?.ShowSoftInput(newNoteInput, ShowFlags.Implicit);
        }
        private void OnDataChanged(object? sender, TextChangedEventArgs e)
        {
            CheckForHighOffspringCount();
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
        private void CheckForHighOffspringCount()
        {
            if(_highOffspringCountConfirmed)
                return;

            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?[0].Text ?? "0", out adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out chicks);

            // Check if any values are 3 or greater - no state tracking, ask every time
            var highValues = new List<(string type, int count)>();
            if (adults > 2) highValues.Add(("adults", adults));
            if (eggs > 2) highValues.Add(("eggs", eggs));
            if (chicks > 2) highValues.Add(("chicks", chicks));
            if (chicks + eggs > 2 && eggs > 0 && chicks > 0) highValues.Add(("eggs & chicks", chicks + eggs));

            if (highValues.Count > 0)
            {
                var message = "Are you sure you have found:\n\n";
                foreach (var (type, count) in highValues)
                    message += $"• {count} {type}\n";
                message += "\nPlease check this is correct.";
                ShowConfirmationDialog(
                    "High Value Confirmation",
                    message,
                    ("OK", () =>{ _highOffspringCountConfirmed = true; }
                ),
                   null
                );
            }
        }
        private void SaveCurrentBoxData()
        {
            if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName))
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName] = new BoxData();
            var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName];
            string boxDataString = boxData.ToString();
            
            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?[0].Text ?? "0", out adults);
            int.TryParse(_eggsEditText?[0].Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?[0].Text ?? "0", out chicks);

            boxData.Adults = adults;
            boxData.Eggs = eggs;
            boxData.Chicks = chicks;
            boxData.GateStatus = GetSelectedStatus(_gateStatusSpinner[0]);
            boxData.BreedingChance = GetSelectedStatus(_breedingChanceSpinner[0]);
            boxData.Notes = _notesEditText?[0].Text ?? "";

            if (boxData.ToString() != boxDataString)
            {
                if (_appSettings.ActiveSessionTimeStampActive)
                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].LastSaved = boxData.whenDataCollectedUtc = _appSettings.ActiveSessionLocalTimeStamp.ToUniversalTime();
                else
                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].LastSaved = boxData.whenDataCollectedUtc = DateTime.UtcNow;
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName] = boxData;
                SaveToAppDataDir();
            }
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

            // Add manual input section at the bottom
            var manualInputLayout = new LinearLayout(this);
            var manualInputParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            manualInputParams.SetMargins(0, 12, 0, 0);
            manualInputLayout.LayoutParameters = manualInputParams;

            _manualScanEditText = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapCharacters,
                Hint = "Enter 8-digit scan number",
                TextSize = 14
            };
            _manualScanEditText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _manualScanEditText.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            _manualScanEditText.SetPadding(12, 12, 12, 12);
            _manualScanEditText.Background = _uiFactory.CreateRoundedBackground(Color.White, 8);
            _manualScanEditText.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(8) });

            var editTextParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            editTextParams.SetMargins(0, 0, 8, 0);
            _manualScanEditText.LayoutParameters = editTextParams;

            var addButton = new Button(this)
            {
                Text = "Add",
                TextSize = 12
            };
            addButton.SetTextColor(Color.White);
            addButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            addButton.SetPadding(16, 12, 16, 12); 
            addButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.SUCCESS_GREEN, 8);
            addButton.SetAllCaps(false);

            var addButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            addButton.LayoutParameters = addButtonParams;

            addButton.Click += OnManualAddClick;

            manualInputLayout.AddView(_manualScanEditText);
            manualInputLayout.AddView(addButton);
            _scannedIdsLayout[0].AddView(manualInputLayout);
        }
        private LinearLayout CreateScanRecordView(ScanRecord scan, int index)
        {
            var scanLayout = new LinearLayout(this);

            var layoutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            layoutParams.SetMargins(0, 2, 0, 2);
            scanLayout.LayoutParameters = layoutParams;

            // Determine background color based on penguin sex data
            Color backgroundColor;
            string additionalInfo = "";
            
            if (null != _remotePenguinData && _remotePenguinData.TryGetValue(scan.BirdId, out var penguinData))
            {
                // Penguin found in remote data - prioritize life stage over sex
                if (penguinData.LastKnownLifeStage == LifeStage.Chick)
                {
                    backgroundColor = UIFactory.CHICK_BACKGROUND;
                    additionalInfo = " 🐣";
                }
                else if (penguinData.Sex.Equals("F", StringComparison.OrdinalIgnoreCase))
                {
                    backgroundColor = UIFactory.FEMALE_BACKGROUND;
                    additionalInfo = " ♀";
                }
                else if (penguinData.Sex.Equals("M", StringComparison.OrdinalIgnoreCase))
                {
                    backgroundColor = UIFactory.MALE_BACKGROUND;
                    additionalInfo = " ♂";
                }
                else
                {
                    // Unknown sex, use alternating colors
                    backgroundColor = index % 2 == 0 ? UIFactory.SCAN_ROW_EVEN : UIFactory.SCAN_ROW_ODD;
                }
            }
            else
            {
                // Penguin not found in remote data, use alternating colors
                backgroundColor = index % 2 == 0 ? UIFactory.SCAN_ROW_EVEN : UIFactory.SCAN_ROW_ODD;
            }

            scanLayout.Background = _uiFactory.CreateRoundedBackground(backgroundColor, 4);
            scanLayout.SetPadding(12, 8, 12, 8);

            // Scan info text with additional penguin information
            var timeStr = scan.Timestamp.ToLocalTime().ToString("MMM dd, HH:mm");
            var scanText = new TextView(this)
            {
                Text = $"• {scan.BirdId}{additionalInfo} at {timeStr}",
                TextSize = 14
            };
            scanText.SetTextColor(UIFactory.TEXT_PRIMARY);
            var textParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            scanText.LayoutParameters = textParams;
            scanLayout.AddView(scanText);

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
        private void OnDeleteScanClick(ScanRecord scanToDelete)
        {
            ShowConfirmationDialog(
                "Delete Bird Scan",
                $"Are you sure you want to delete the scan for bird {scanToDelete.BirdId}?",
                ("Yes, Delete", () =>
                {
                    if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName))
                    {
                        var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName];
                        var scanToRemove = boxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToDelete.BirdId &&
                            s.Timestamp == scanToDelete.Timestamp);

                        if (scanToRemove != null)
                        {
                            boxData.ScannedIds.Remove(scanToRemove);
                            if (_remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData) && (
                                LifeStage.Adult == penguinData.LastKnownLifeStage || 
                                LifeStage.Returnee == penguinData.LastKnownLifeStage || 
                                DateTime.Now > penguinData.ChipDate.AddMonths(3)))
                            {
                                _adultsEditText[0].Text = "" + Math.Max(0, int.Parse(_adultsEditText[0].Text ?? "0") - 1);
                            }
                            else if (penguinData != null && LifeStage.Chick == penguinData.LastKnownLifeStage)
                            {
                                _chicksEditText[0].Text = "" + Math.Max(0, int.Parse(_chicksEditText[0].Text ?? "0") - 1);
                            }
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(boxData.ScannedIds);
                            Toast.MakeText(this, $"🗑️ Bird {scanToDelete.BirdId} deleted from Box {_currentBoxIndex}", ToastLength.Short)?.Show();
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
                ("Yes, Move", () =>
                {
                    // Remove from current box
                    if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBoxName))
                    {
                        var currentBoxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBoxName];
                        var scanToRemove = currentBoxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToMove.BirdId &&
                            s.Timestamp == scanToMove.Timestamp);

                        if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(targetBoxName) 
                        && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBoxName].ScannedIds.Any(s => s.BirdId == scanToMove.BirdId))
                        {
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} exists already in Box {targetBoxName}", ToastLength.Long)?.Show();
                        }
                        else if (scanToRemove != null)
                        {
                            currentBoxData.ScannedIds.Remove(scanToRemove);

                            // Add to target box
                            if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(targetBoxName))
                                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBoxName] = new BoxData();

                            _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBoxName].ScannedIds.Add(scanToMove);

                            if (_remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData))
                            {
                                if (LifeStage.Adult == penguinData.LastKnownLifeStage || LifeStage.Returnee == penguinData.LastKnownLifeStage || DateTime.Now > penguinData.ChipDate.AddMonths(3))
                                {
                                    _adultsEditText[0].Text = "" + Math.Max(0, int.Parse(_adultsEditText[0].Text ?? "0") - 1);
                                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBoxName].Adults++;
                                }
                                else if (LifeStage.Chick == penguinData.LastKnownLifeStage)
                                {
                                    _chicksEditText[0].Text = "" + Math.Max(0, int.Parse(_chicksEditText[0].Text ?? "0") - 1);
                                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBoxName].Chicks++;
                                }
                            }
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(currentBoxData.ScannedIds);
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} moved from Box {_currentBoxName} to Box {targetBoxName}", ToastLength.Long)?.Show();
                            DrawPageLayouts();
                        }
                    }
                }),
                ("Cancel", () => { })
            );
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

                // Load box tags
                _boxTags = BoxTagService.LoadBoxTags(internalPath);

                // Load remote penguin data.
                _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
                _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
                if (_remotePenguinData != null &&  _remoteBreedingDates != null)
                {
                    Toast.MakeText(this, $"{_remotePenguinData.Count} bird, {_remoteBreedingDates.Count} breeding dates found.", ToastLength.Short)?.Show();
                }

                // Load main app data
                _allMonitorData = _dataStorageService.LoadAllMonitorDataFromDisk(this);
                if (_allMonitorData != null)
                {
                    Toast.MakeText(this, $"📱 Data restored...", ToastLength.Short)?.Show();
                }
                else
                {
                    _allMonitorData = new Dictionary<int, MonitorDetails>();
                    _allMonitorData.Add(0, new MonitorDetails());
                }
            }
            catch (Exception ex)
            {
                _remotePenguinData = new Dictionary<string, PenguinData>();
                System.Diagnostics.Debug.WriteLine($"Failed to load data: {ex.Message}");
            }
        }
        private void SaveToAppDataDir(bool reportHome = true)
        {
            DataStorageService.SaveAllMonitorDataToDisk(this, _allMonitorData,  reportHome: reportHome);
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

        private void HandleBoxTagScan(string cleanTagId)
        {
            RunOnUiThread(() =>
            {
                // Check if this tag is already assigned to a box
                string? assignedBoxId = BoxTagService.GetBoxIdByTag(_boxTags, cleanTagId);

                if (!_isBoxLocked)
                {
                    // Current box is UNLOCKED
                    if (assignedBoxId != null && assignedBoxId != _currentBoxName)
                    {
                        // Tag belongs to a different box - error!
                        TriggerAlert();
                        Toast.MakeText(this, $"⚠️ ERROR: Tag belongs to Box {assignedBoxId}!\nCurrent box is {_currentBoxName}", ToastLength.Long)?.Show();
                        return;
                    }

                    // Assign this tag to the current box
                    var internalPath = this.FilesDir?.AbsolutePath;
                    if (!string.IsNullOrEmpty(internalPath))
                    {
                        BoxTagService.AssignBoxTag(
                            _boxTags,
                            _currentBoxName,
                            cleanTagId,
                            _currentLocation?.Latitude ?? 0,
                            _currentLocation?.Longitude ?? 0,
                            _currentLocation?.Accuracy ?? -1,
                            internalPath
                        );
                        Toast.MakeText(this, $"📌 Box tag assigned to Box {_currentBoxName}", ToastLength.Short)?.Show();
                        DrawPageLayouts(); // Refresh UI to show delete button if enabled
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
                            DrawPageLayouts();
                            Toast.MakeText(this, $"🔓 Box {_currentBoxName} unlocked", ToastLength.Short)?.Show();
                        }
                        else
                        {
                            // Different box - jump to it and unlock
                            if (_boxNamesAndIndexes.ContainsKey(assignedBoxId))
                            {
                                _currentBoxIndex = _boxNamesAndIndexes[assignedBoxId];
                                _currentBoxName = assignedBoxId;
                                _isBoxLocked = false;
                                DrawPageLayouts();
                                Toast.MakeText(this, $"📍 Jumped to Box {assignedBoxId} and unlocked", ToastLength.Short)?.Show();
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

        private void AddScannedId(String fullEid, int addtoThisMonitor )
        {
            var cleanEid = new String(fullEid.Where(char.IsLetterOrDigit).ToArray());

            // Check if this is a box tag scan (LA9000250*)
            if (BoxTagService.IsBoxTag(cleanEid))
            {
                HandleBoxTagScan(cleanEid);
                return;
            }

            var shortId = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8) : cleanEid;

            if (!_allMonitorData[addtoThisMonitor].BoxData.ContainsKey(_currentBoxName))
                _allMonitorData[addtoThisMonitor].BoxData[_currentBoxName] = new BoxData();
            var boxData = _allMonitorData[addtoThisMonitor].BoxData[_currentBoxName];

            if (!boxData.ScannedIds.Any(s => s.BirdId == shortId))
            {
                var scanRecord = new ScanRecord
                {
                    BirdId = shortId,
                    Timestamp = _appSettings.ActiveSessionTimeStampActive ? _appSettings.ActiveSessionLocalTimeStamp.ToUniversalTime() : DateTime.UtcNow,
                    Latitude = _currentLocation?.Latitude ?? 0,
                    Longitude = _currentLocation?.Longitude ?? 0,
                    Accuracy = _currentLocation?.Accuracy ?? -1
                };
                boxData.ScannedIds.Add(scanRecord);
                SaveCurrentBoxData();
                RunOnUiThread(() =>
                {
                    // Enhanced toast message with life stage info
                    string toastMessage = $"🐧 Bird {shortId} added to Box {_currentBoxIndex}";
                    if (_remotePenguinData != null && _remotePenguinData.TryGetValue(shortId, out var penguin))
                    {
                        if (penguin.LastKnownLifeStage == LifeStage.Adult || 
                            penguin.LastKnownLifeStage == LifeStage.Returnee)
                        {
                            _adultsEditText[0].Text = (int.Parse(_adultsEditText[0].Text ?? "0") + 1).ToString();
                            SaveCurrentBoxData();
                            if (!penguin.Sex.Equals("f", StringComparison.OrdinalIgnoreCase) && !penguin.Sex.Equals("m", StringComparison.OrdinalIgnoreCase))
                            {
                                triggerAlertAsync();
                                toastMessage += $" unsexed";
                            }
                            else
                                toastMessage += $" (+1 Adult)";
                        }
                        else if (penguin.LastKnownLifeStage == LifeStage.Chick)
                        {
                            if (penguin.ChipDate > DateTime.Today.AddYears(-20) && DateTime.Today > penguin.ChipDate.AddMonths(3))
                            {
                                _adultsEditText[0].Text = (int.Parse(_adultsEditText[0].Text ?? "0") + 1).ToString();
                                toastMessage += $" (+1 Adult)";
                            }
                            else
                            {
                                _chicksEditText[0].Text = (int.Parse(_chicksEditText[0].Text ?? "0") + 1).ToString();
                                toastMessage += $" (+1 Chick)";
                            }
                            SaveCurrentBoxData();
                            triggerAlertAsync();
                        }
                        else
                        {
                            toastMessage += ", Not adult or chick.";
                            triggerAlertAsync();
                        }
                    }
                    else
                    {
                        toastMessage += ", Unknown scan ID!";
                        triggerAlertAsync();
                    }
                    DrawPageLayouts();
                    Toast.MakeText(this, toastMessage, ToastLength.Short)?.Show();
                });
            }
        }
        private void ShowSaveFilenameDialog(bool upload = false)
        {
            var now = DateTime.Now;
            string defaultFileName = $"PenguinMonitor {now:yyMMdd HHmmss}";            
            if (!string.IsNullOrEmpty(_allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename))
            {
                if (!Regex.Match(_allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename, @"-\d\d$").Success)   // string ends with -00 or -37
                {
                    defaultFileName = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename + "-01";
                }
                else
                {
                    defaultFileName = Regex.Replace(_allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename, @"-(\d\d)$", match =>
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
        private void SaveDataWithFilename(string fileName, bool upload)
        {
            try
            {
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename = fileName.Replace(".json","");
                var jsonContents = JsonConvert.SerializeObject(_allMonitorData[_appSettings.CurrentlyVisibleMonitor], Formatting.Indented);
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
                            SaveMonitorDetailsToPath(filePath, jsonContents);
                            if (upload) 
                                _dataStorageService.uploadCurrentMonitorDetailsToServer(jsonContents);
                        }
                    ),
                        ("Cancel", () => ShowSaveFilenameDialog()) // Go back to filename dialog
                    );
                }
                else
                {
                    SaveMonitorDetailsToPath(filePath, jsonContents);
                    if (upload)
                        _dataStorageService.uploadCurrentMonitorDetailsToServer(jsonContents);
                }
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Export failed: {ex.Message}", ToastLength.Short)?.Show();
            }
        }
        private void SaveMonitorDetailsToPath(string filePath, string json )
        {
            try
            {
                string fileName = System.IO.Path.GetFileName(filePath);
                File.WriteAllText(filePath, json);

                var totalBoxes = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Count;
                var totalBirds = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Values.Sum(box => box.ScannedIds.Count);

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
                        InitializeGPS();
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
                    if (_boxNamesAndIndexes.ContainsKey(input.Text) )
                    {
                        JumpToBox(input.Text); 
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
            _singleBoxDataContentLayout.Visibility = ViewStates.Visible;
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
        private void OnManualAddClick(object? sender, EventArgs e)
        {
            if (_manualScanEditText == null) return;

            var inputText = _manualScanEditText.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(inputText))
            {
                Toast.MakeText(this, "Please enter a scan number", ToastLength.Short)?.Show();
                return;
            }

            // Validate 8-digit alphanumeric
            var cleanInput = new string(inputText.Where(char.IsLetterOrDigit).ToArray()).ToUpper();
            
            if (cleanInput.Length != 8)
            {
                Toast.MakeText(this, "Scan number must be exactly 8 digits/letters", ToastLength.Short)?.Show();
                _manualScanEditText.RequestFocus();
                return;
            }
            AddScannedId(cleanInput, _appSettings.CurrentlyVisibleMonitor);
        }
        private void OnDataClick(object? sender, EventArgs e)
        {
            ShowDataOptionsDialog();
        }
        private void ShowDataOptionsDialog()
        {
            var options = new string[] 
            {
                "📊 Data overview",
                "💾 Save to file", 
                "📂 From device",
                "📂 Active monitor on server",
            };
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Data Options");            
            builder.SetItems(options, (sender, args) =>
            {
                switch (args.Which)
                {
                    case 0: // Summary
                        ShowBoxDataSummary(_appSettings.CurrentlyVisibleMonitor);
                        break;
                    case 1: // Save data
                        OnSaveDataClick(null, EventArgs.Empty);
                        break;
                    case 2: // Load data
                        LoadJsonDataFromFile();
                        break;
                    case 3: // Load data
                        LoadJsonDataFromServer();
                        break;
                }
            });
            builder.SetNegativeButton("Cancel", (sender, args) => { });            
            var dialog = builder.Create();
            dialog?.Show();
        }
    }
}