using Android;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Locations;
using Android.Media;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.Animations;
using Android.Views.InputMethods;
using Android.Widget;
using BluePenguinMonitoring.Models;
using BluePenguinMonitoring.Services;
using BluePenguinMonitoring.UI.Factories;
using BluePenguinMonitoring.UI.Gestures;
using BluePenguinMonitoring.UI.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmtpAuthenticator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BluePenguinMonitoring
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
        // Bluetooth manager
        private BluetoothManager? _bluetoothManager;

        // GPS components
        private LocationManager? _locationManager;
        private Location? _currentLocation;
        private float _gpsAccuracy = -1;

        // UI Components
        private TextView? _statusText; // scanner and GPS status

        private Button? _prevBoxButton;
        private Button? _selectBoxButton;
        private Button? _nextBoxButton;

        private LinearLayout? _scannedIdsLayout;
        private EditText? _adultsEditText;
        private EditText? _eggsEditText;
        private EditText? _chicksEditText;
        private Spinner? _gateStatusSpinner;
        private Spinner? _breedingChanceSpinner; 
        private EditText? _notesEditText;
        private EditText? _manualScanEditText;
        // Add a field for the data card title so it can be updated dynamically
        private TextView? _dataCardTitleText;
        private LinearLayout? _dataCardTitleLayout;
        private ImageView? _lockIconView;
        private bool _isBoxLocked;
        private ScrollView? _rootScrollView;
        private LinearLayout? _topButtonLayout;
        private LinearLayout? _settingsCard;
        private CheckBox? _isBluetoothEnabledCheckBox;
        private TextView? _interestingBoxTextView;

        private UIFactory.selectedPage selectedPage;
        // ===== Multi-page menu state =====
        private readonly (string Text, UIFactory.selectedPage Page)[] _menuItems = new[]
        {
            ("⚙️ Settings",      UIFactory.selectedPage.Settings),
            ("📦 Single box data",  UIFactory.selectedPage.BoxDataSingle),
            ("📊 Data overview", UIFactory.selectedPage.BoxOverview),
         };

        // Add gesture detection components
        private GestureDetector? _gestureDetector;
        private LinearLayout? _dataCard;

        // Services
        public UIFactory? _uiFactory;
        private DataStorageService _dataStorageService = new DataStorageService();

        // Data storage
        Dictionary<int, MonitorDetails> _allMonitorData = new Dictionary<int, MonitorDetails>();
        private AppSettings _appSettings;
        private Dictionary<string, PenguinData>? _remotePenguinData ;
        private Dictionary<int, BoxRemoteData>? _remoteBoxData;
        private Dictionary<int, BoxPredictedDates>? _remoteBreedingDates;
        private int _currentBox = 1;

        // High value confirmation tracking - reset on each entry
        private bool _isProcessingConfirmation = false;

        // Vibration and sound components
        private Vibrator? _vibrator;
        private MediaPlayer? _alertMediaPlayer;

        //Lazy versioning.
        private static int version = 29;
        private static int numberMonitorBoxes = 150;

        //multibox View
        private LinearLayout? _multiBoxViewCard;
        private LinearLayout? _multiboxBoxFilterCard;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestPermissions();
            LoadFromAppDataDir();
            CreateDataRecordingUI();
            InitializeVibrationAndSound();
        }
        private void RequestPermissions()
        {
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
                    //InitializeBluetooth();
                }
            }
            else
            {
                // Pre-Android 6 or no permissions needed
                InitializeGPS();
                //InitializeBluetooth();
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
            if (_currentBox > 1)
            {
                NavigateToBox(_currentBox - 1, () => _currentBox > 1);
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
            if (_currentBox < numberMonitorBoxes)
            {
                NavigateToBox(_currentBox + 1, () => _currentBox < numberMonitorBoxes);
            }
            else
            {
                Toast.MakeText(this, "Already at last box", ToastLength.Short)?.Show();
            }
        }
        private void OnEidDataReceived(string eidData)
        {
            if (eidData.Length != 15)
                return;
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
                        Toast.MakeText(this, "⚠️ Android 11+ detected!\n\nFor file access, please:\n1. Go to Settings > Apps > BluePenguinMonitoring\n2. Enable 'All files access'", ToastLength.Long)?.Show();

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
                // Open *.json style export. 
                if (loadedData["Boxes"] != null)
                {
                    _allMonitorData[0].BoxData.Clear();
                    foreach (var boxItem in loadedData["Boxes"])
                    {
                        var boxNumber = boxItem?["BoxNumber"]?.Value<int>() ?? 0;
                        var dataNode = boxItem?["Data"];

                        if (boxNumber > 0 && dataNode != null)
                        {
                            var boxData = new BoxData
                            {
                                Adults = dataNode["Adults"]?.Value<int>() ?? 0,
                                Eggs = dataNode["Eggs"]?.Value<int>() ?? 0,
                                Chicks = dataNode["Chicks"]?.Value<int>() ?? 0,
                                GateStatus = (dataNode["GateStatus"]?.Value<string>() ?? "").Replace("gate up", "Gate up").Replace("regate", "Regate"),
                                Notes = dataNode["Notes"]?.Value<string>() ?? "",
                                whenDataCollectedUtc = DateTime.TryParseExact(dataNode["whenDataCollectedUtc"]?.Value<string>() ?? "",
                                "yyyy-MM-ddTHH:mm:ssZ",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind,
                                out DateTime whenCollected) ? whenCollected.ToUniversalTime() : DateTime.MinValue.ToUniversalTime(),
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
                            _allMonitorData[0].BoxData[boxNumber] = boxData;
                            boxCount++;
                        }
                    }
                }
                else if (loadedData["BoxData"] != null)
                {
                    _allMonitorData[0].BoxData.Clear();
                    
                    var boxDatas = loadedData["BoxData"] as JObject;
                    foreach (var boxItem in boxDatas)
                    {
                        int boxNumber = int.Parse(boxItem.Key);
                        var dataNode = boxItem.Value;

                        var boxData = new BoxData
                        {
                            Adults = dataNode["Adults"]?.Value<int>() ?? 0,
                            Eggs = dataNode["Eggs"]?.Value<int>() ?? 0,
                            Chicks = dataNode["Chicks"]?.Value<int>() ?? 0,
                            GateStatus = (dataNode["GateStatus"]?.Value<string>() ?? "").Replace("gate up", "Gate up").Replace("regate", "Regate"),
                            Notes = dataNode["Notes"]?.Value<string>() ?? "",
                            whenDataCollectedUtc = DateTime.TryParseExact(dataNode["whenDataCollectedUtc"]?.Value<string>() ?? "",
                                "yyyy-MM-ddTHH:mm:ssZ",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind,
                                out DateTime whenCollected) ? whenCollected.ToUniversalTime() : DateTime.MinValue.ToUniversalTime(),
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
                        _allMonitorData[0].BoxData[boxNumber] = boxData;
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
            {
                Toast.MakeText(this, "No box data to display", ToastLength.Short)?.Show();
                return;
            }

            string summary = GetMonitorSummaryText(selectedMonitor);

            ShowConfirmationDialog(
                "📊 Data summary",
                summary,
                ("OK", () => { }
            )
            );
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
        private void OnDownloadCsvClick(object? sender, EventArgs e)
        {
            if (sender is Button clickedButton && _isDownloadingCsvData == false)
            {
                _isDownloadingCsvData = true;
                clickedButton.Text = "Loading data";
                clickedButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.WARNING_YELLOW, 8);

                _allMonitorData = _dataStorageService.requestPastMonitorDetailsFromServer(_allMonitorData);

                _ = Task.Run(async () =>
                {
                    await _dataStorageService.DownloadCsvDataAsync(this);
                    _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
                    _remoteBoxData = await _dataStorageService.loadRemoteBoxInfoFromAppDataDir(this);
                    _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
                    new Handler(Looper.MainLooper).Post(() =>
                    {
                        _isDownloadingCsvData = false;
                        clickedButton.Text = "Bird Stats";
                        clickedButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 8);
                        if (!_allMonitorData.ContainsKey(_appSettings.CurrentlyVisibleMonitor))
                            _appSettings.CurrentlyVisibleMonitor = 0;
                        DrawPageLayouts();
                    });
                });
            }
        }
        private void CreateDataRecordingUI()
        {
            _uiFactory = new UIFactory(this);
            selectedPage = UIFactory.selectedPage.BoxDataSingle;
            _isBoxLocked = true;
            _rootScrollView = new ScrollView(this);
            _rootScrollView.SetBackgroundColor(UIFactory.LIGHT_GRAY);

            // Initialize gesture detector and apply to ScrollView
            _gestureDetector = new GestureDetector(this, new SwipeGestureDetector(this));
            _rootScrollView.Touch += OnScrollViewTouch;

            var parentLinearLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };

            // App header
            var headerCard = _uiFactory.CreateCard(borderWidth: 0);
            var menuButton = new ImageButton(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent,
                    ViewGroup.LayoutParams.WrapContent)
            };
            menuButton.SetPadding(0, 0, 0, 0);
            menuButton.SetImageResource(Android.Resource.Drawable.IcMenuManage); // Use built-in menu icon
            menuButton.SetBackgroundColor(Color.Transparent); // No background
            menuButton.Click += hamburgerButtonClick;

            var titleCard = _uiFactory.CreateCard(Android.Widget.Orientation.Horizontal, padding: 0, borderWidth: 0);
            titleCard.SetGravity(GravityFlags.Center);

            // Add to headerCard before titleText
            titleCard.AddView(menuButton);

            ImageView iconView = new ImageView(this);
            iconView.SetPadding(0, 0, 0, 0);
            iconView.SetImageResource(Resource.Mipmap.appicon);
            iconView.ScaleX = iconView.ScaleY = 0.7f;
            titleCard.AddView(iconView);

            var titleText = new TextView(this)
            {
                Text = "Penguin Monitor",
                TextSize = 28,
                Gravity = GravityFlags.Center
            };
            titleText.SetPadding(0, 0, 0, 0);
            titleText.SetTextColor(UIFactory.PRIMARY_BLUE);
            titleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            titleCard.AddView(titleText);
            _statusText = new TextView(this)
            {
                Text = "Connecting to HR5... | GPS: No signal",
                TextSize = 14,
                Gravity = GravityFlags.Center
            };
            _statusText.SetTextColor(UIFactory.TEXT_SECONDARY);
            var statusParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            statusParams.SetMargins(0, 0, 0, 0);
            _statusText.LayoutParameters = statusParams;
            var titleAndStatusCard = _uiFactory.CreateCard(padding: 5);
            titleAndStatusCard.AddView(titleCard);
            titleAndStatusCard.AddView(_statusText);
            titleAndStatusCard.Background = _uiFactory.CreateCardBackground(borderWidth: 0);

            headerCard.AddView(titleAndStatusCard);

            parentLinearLayout.AddView(headerCard);

            // Action buttons
            _topButtonLayout = CreateStyledButtonLayout(
                ("Clear All", OnClearBoxesClick, UIFactory.DANGER_RED),
                ("Clear Box", OnClearBoxClick, UIFactory.WARNING_YELLOW),
                ("Bird Stats", OnDownloadCsvClick, UIFactory.PRIMARY_BLUE),
                ("Save/Load", OnDataClick, UIFactory.SUCCESS_GREEN)
            );
            statusParams.SetMargins(0, 0, 0, 10);
            _topButtonLayout.LayoutParameters = statusParams;
            headerCard.AddView(_topButtonLayout);

            // Navigation card
            var boxNavLayout = CreateNavigationLayout();
            boxNavLayout.LayoutParameters = statusParams;
            headerCard.AddView(boxNavLayout);

            //Settings Card
            createSettingsCard();

            // Data card
            CreateBoxDataCard();

            //Create Multi box view card
            _appSettings.ShowMultiboxFilterCard = false;
            // _hideBoxesWithDataInMultiBoxView = false;
            createMultiBoxViewCard();

            parentLinearLayout.AddView(_settingsCard);
            parentLinearLayout.AddView(_dataCard);
            parentLinearLayout.AddView(_multiBoxViewCard);

            DrawPageLayouts();
            _rootScrollView.AddView(parentLinearLayout);
            SetContentView(_rootScrollView);

            _rootScrollView.SetOnApplyWindowInsetsListener(new ViewInsetsListener());
        }
        private void hamburgerButtonClick(object? sender, EventArgs e)
        {
            var labels = _menuItems.Select(m => m.Text).ToArray();
            // Seed check state from currently visible pages
            var checkedItems = _menuItems
                     .Select(m => _appSettings.VisiblePages.Contains(m.Page))
                     .ToArray();

            // Collect changes temporarily before applying
            var tempVisible = new HashSet<UIFactory.selectedPage>(_appSettings.VisiblePages);

            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Menu");

            // Multi-choice (checkboxes)
            builder.SetMultiChoiceItems(labels, checkedItems, (s, args) =>
            {
                var page = _menuItems[args.Which].Page;
                if (args.IsChecked) tempVisible.Add(page);
                else tempVisible.Remove(page);
            });

            builder.SetPositiveButton("Apply", (s, args) =>
            {
                if (tempVisible.Count == 0)
                {
                    Toast.MakeText(this, "At least one page must be visible", ToastLength.Short)?.Show();
                    return;
                }
                _appSettings.AddVisiblePage(UIFactory.selectedPage.BoxOverview);
                DrawPageLayouts();
            });
            builder.Show();
        }
        private void createMultiBoxViewCard()
        {
            if (_multiBoxViewCard == null)
            {
                _multiBoxViewCard = _uiFactory.CreateCard();
            }
            else
            {
                _multiBoxViewCard.RemoveAllViews();
            }
            var headerCard = _uiFactory.CreateCard(Android.Widget.Orientation.Horizontal, borderWidth:4);
            headerCard.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            
            var showFiltersButton = new ImageButton(this);
            showFiltersButton.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            showFiltersButton.SetPadding(0, 0, 0, 0);
            showFiltersButton.SetImageResource(Android.Resource.Drawable.IcMenuManage); // Use built-in menu icon
            showFiltersButton.SetBackgroundColor(Color.Transparent); // No background
            showFiltersButton.Click += (sender, e) =>
            {
                _appSettings.ShowMultiboxFilterCard = !_appSettings.ShowMultiboxFilterCard;
                DrawPageLayouts();
            };
            headerCard.AddView(showFiltersButton);

            TextView multiBoxTitle = new TextView(this)
            {
                Text = "Box Overview",
                TextSize = 30,
                Gravity = GravityFlags.Left
            };
            multiBoxTitle.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            multiBoxTitle.SetTextColor(Color.Black);
            multiBoxTitle.SetPadding(0, 0, 0, 0);
            headerCard.AddView(multiBoxTitle);

            // Add a spacer that expands to fill available space
            var spacer = new View(this);
            spacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            headerCard.AddView(spacer);

            TextView timeTV = new TextView(this)
            {
                TextSize = 12,
            };


            if (_appSettings.CurrentlyVisibleMonitor == 0)
                timeTV.Text = "Data is local only";
            else
                timeTV.Text = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename; // + timeTV.Text).Trim();

            bool timeFound = false;
            foreach (BoxData box in _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Values)
            {
                foreach (ScanRecord sc in box.ScannedIds)
                {
                    timeTV.Text += "\n" + sc.Timestamp.ToLocalTime().ToString("d MMM yy");
                    timeFound = true;
                    break;
                }
                if (!timeFound && box.whenDataCollectedUtc.Year > 2015)
                {
                    timeTV.Text += "\n" + box.whenDataCollectedUtc.ToLocalTime().ToString("d MMM yy");
                    timeFound = true;
                }
                if (timeFound) break;
            }
            if(!timeFound)
                timeTV.Text += "\nNo date in data" ;
            timeTV.Text = timeTV.Text.Trim();
            timeTV.SetTextColor(Color.Black);
            timeTV.SetPadding(0,0,0,0);
            timeTV.Gravity = GravityFlags.Right | GravityFlags.Bottom;
            timeTV.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent);
            headerCard.AddView(timeTV);

            _multiBoxViewCard.AddView(headerCard);

            _multiboxBoxFilterCard = _uiFactory.CreateCard(padding: 0, borderWidth: 4);
            TextView filtersTitle = new TextView(this)
            {
                Text = "Show Boxes",
                TextSize = 16,
                Gravity = GravityFlags.Center,
                
            };
            filtersTitle.SetTypeface(null, TypefaceStyle.Bold);
            filtersTitle.SetTextColor(Color.Black);
            _multiboxBoxFilterCard.AddView(filtersTitle);

            var allAndDataFiltersLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };

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
            allAndDataFiltersLayout.AddView(showAllBoxesInMultiBoxView);

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
            allAndDataFiltersLayout.AddView(showBoxesWithDataInMultiboxView);

            _multiboxBoxFilterCard.AddView(allAndDataFiltersLayout);


            var breedingChanceFilterLayout= new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
            CheckBox showNoBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "No",
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
                Text = "Unlikely",
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
                Text = "Potential",
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
                Text = "Confident",
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
            _multiboxBoxFilterCard.AddView(breedingChanceFilterLayout);

            var specialBoxFilterLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
            CheckBox showSpecialBoxesInMultiboxView = new CheckBox(this)
            {
                Text = "Special",
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
            _multiboxBoxFilterCard.AddView(specialBoxFilterLayout);


            TextView hideFiltersTitle = new TextView(this)
            {
                Text = "Hide Boxes",
                TextSize = 16,
                Gravity = GravityFlags.Center,

            };
            hideFiltersTitle.SetTypeface(null, TypefaceStyle.Bold);
            hideFiltersTitle.SetTextColor(Color.Black);
            _multiboxBoxFilterCard.AddView(hideFiltersTitle);

            var hideBoxesLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
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
            _multiboxBoxFilterCard.AddView(hideBoxesLayout);



            TextView browseDataTitle = new TextView(this)
            {
                Text = "Browse Data",
                TextSize = 16,
                Gravity = GravityFlags.Center,

            };
            browseDataTitle.SetTypeface(null, TypefaceStyle.Bold);
            browseDataTitle.SetTextColor(Color.Black);
            _multiboxBoxFilterCard.AddView(browseDataTitle);


            var browseOtherDataButtonLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
            var outsideButtonParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            outsideButtonParams.SetMargins(8, 0, 8, 8);

            Button gotoPreviousData = _uiFactory.CreateStyledButton("← Show previous", UIFactory.PRIMARY_BLUE);
            gotoPreviousData.LayoutParameters = outsideButtonParams;
            bool olderAvailable = _allMonitorData.Count > _appSettings.CurrentlyVisibleMonitor + 1;
            SetEnabledRecursive(gotoPreviousData, olderAvailable, olderAvailable ? 1.0f: 0.5f);
            gotoPreviousData.Click += (s, e) =>
            {
                _appSettings.CurrentlyVisibleMonitor++;
                _appSettings.ActiveSessionLocalTimeStamp = getLocalDateTime(_allMonitorData[_appSettings.CurrentlyVisibleMonitor]);
                _appSettings.ActiveSessionTimeStampActive = true;
                DrawPageLayouts();
            };
            browseOtherDataButtonLayout.AddView(gotoPreviousData);

            Button gotoNextData = _uiFactory.CreateStyledButton("Show next →", UIFactory.PRIMARY_BLUE);
            var gotoNextButtonParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            gotoNextButtonParams.SetMargins(0, 0, 0, 0);
            gotoNextData.LayoutParameters = gotoNextButtonParams;
            bool newerAvailable = _appSettings.CurrentlyVisibleMonitor != 0;
            SetEnabledRecursive(gotoNextData, newerAvailable, newerAvailable ? 1.0f : 0.5f);
            gotoNextData.Click += (s, e) =>
            {
                _appSettings.CurrentlyVisibleMonitor--;
                _appSettings.ActiveSessionLocalTimeStamp = getLocalDateTime(_allMonitorData[_appSettings.CurrentlyVisibleMonitor]);
                _appSettings.ActiveSessionTimeStampActive = _appSettings.CurrentlyVisibleMonitor != 0;
                DrawPageLayouts();
            };
            browseOtherDataButtonLayout.AddView(gotoNextData);

            Button showLatestDataButton = _uiFactory.CreateStyledButton("Show latest →", UIFactory.PRIMARY_BLUE);
            outsideButtonParams.SetMargins(8, 0, 8, 8);
            showLatestDataButton.LayoutParameters = outsideButtonParams;
            SetEnabledRecursive(showLatestDataButton, newerAvailable, newerAvailable ? 1.0f : 0.5f);
            showLatestDataButton.Click += (s, e) =>
            {
                _appSettings.CurrentlyVisibleMonitor = 0;
                _appSettings.ActiveSessionTimeStampActive = false;
                DrawPageLayouts();
            };
            browseOtherDataButtonLayout.AddView(showLatestDataButton);

            _multiboxBoxFilterCard.AddView(browseOtherDataButtonLayout);

            _multiBoxViewCard.AddView(_multiboxBoxFilterCard);
            _multiboxBoxFilterCard.Visibility = _appSettings.ShowMultiboxFilterCard ? ViewStates.Visible : ViewStates.Gone;

            int boxesPerRow = 3;
            LinearLayout? currentRow = null;

            int visibleBoxCount = 0;
            for (int boxNumber = 1; boxNumber <= numberMonitorBoxes; boxNumber++)
            {
                if (visibleBoxCount % boxesPerRow == 0)
                {
                    currentRow = new LinearLayout(this)
                    {
                        Orientation = Android.Widget.Orientation.Horizontal
                    };
                    currentRow.SetPadding(0, 0, 0, 0);

                    var rowParams = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent,
                        ViewGroup.LayoutParams.WrapContent);
                    currentRow.LayoutParameters = rowParams;
                    currentRow.SetGravity(GravityFlags.Center);

                    _multiBoxViewCard.AddView(currentRow);
                }
                if (_remoteBoxData != null && _remoteBoxData.ContainsKey(boxNumber))
                {
                    bool showBox = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(boxNumber) && _appSettings.ShowBoxesWithDataInMultiBoxView
                                    || _appSettings.ShowAllBoxesInMultiBoxView
                                    || _appSettings.ShowConfidentBoxesInMultiBoxView && _remoteBoxData[boxNumber].breedingLikelyhoodText == "CON"
                                    || _appSettings.ShowPotentialBoxesInMultiBoxView && _remoteBoxData[boxNumber].breedingLikelyhoodText == "POT"
                                    || _appSettings.ShowUnlikleyBoxesInMultiBoxView && _remoteBoxData[boxNumber].breedingLikelyhoodText == "UNL"
                                    || _appSettings.ShowNoBoxesInMultiBoxView && _remoteBoxData[boxNumber].breedingLikelyhoodText == "NO"
                                    || _appSettings.ShowInterestingBoxesInMultiBoxView && !string.IsNullOrWhiteSpace(_remoteBoxData[boxNumber].PersistentNotes)
                                    || _appSettings.ShowSingleEggBoxesInMultiboxView && _remoteBoxData[boxNumber].numEggs() == 1;

                    bool hideBoxWithData = _appSettings.HideBoxesWithDataInMultiBoxView && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(boxNumber);
                    bool hideDCM = _appSettings.HideDCMInMultiBoxView && _remoteBoxData[boxNumber].breedingLikelyhoodText == "DCM";

                    if (showBox && !hideBoxWithData && !hideDCM)
                    {
                        View? card;
                        if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(boxNumber))
                            card = CreateBoxSummaryCard(boxNumber, _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[boxNumber], boxNumber == _currentBox);
                        else
                            card = CreateBoxRemoteSummaryCard(boxNumber, _remoteBoxData[boxNumber], boxNumber == _currentBox);
                        currentRow?.AddView(card);
                        visibleBoxCount++;
                    }
                }
            }
            if (visibleBoxCount == 0)
            {
                var empty = new TextView(this) { Text = "No boxes yet — scan or load data." };
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

        private View? CreateBoxRemoteSummaryCard(int boxNumber, BoxRemoteData boxData, bool selected = false)
        {
            var card = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };
            card.SetPadding(5, 5, 5, 5);
            card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, UIFactory.WARNING_YELLOW, backgroundColor: selected?UIFactory.WARNING_YELLOW:null);

            var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1f);
            cardParams.SetMargins(5,5,5,5);
            card.LayoutParameters = cardParams;

            var title = new TextView(this)
            {
                Text = $"Box {boxNumber}",
                Gravity = GravityFlags.Center,
                TextSize = 18
            };
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            title.SetTextColor(Color.Black);

            var summary = new TextView(this)
            {
                Text = boxData.boxMiniStatus(),
                Gravity = GravityFlags.Center,
                TextSize = 14
            };
            if (_remoteBreedingDates.ContainsKey(boxNumber))
            {
                summary.Text += "\n" + _remoteBreedingDates[boxNumber].breedingDateStatus();
            }
            summary.SetTextColor(Color.Black);

            card.AddView(title);
            card.AddView(summary);
            card.Click += (sender, e) =>
            {
                JumpToBox(boxNumber);
                ScrollToTop();
            };
            return card;
        }
        private View? CreateBoxSummaryCard(int boxNumber, BoxData thisBoxData, bool selected)
        {
            var card = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };
            card.SetPadding(5, 5, 5, 5);
            var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1f);
            cardParams.SetMargins(5, 5, 5, 5);
            card.LayoutParameters = cardParams;
            bool gotRemoteBoxData = _remoteBoxData.TryGetValue(boxNumber, out var thisRemoteBoxData);
            bool differenceFound = false;
            if (gotRemoteBoxData && thisBoxData.Eggs != thisRemoteBoxData?.numEggs()
                || thisBoxData.Chicks != thisRemoteBoxData?.numChicks()
                || thisRemoteBoxData?.breedingLikelyhoodText != "BR" && thisBoxData.Chicks + thisBoxData.Eggs + thisBoxData.Adults != 0)
            {
                differenceFound = true;
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 8, borderColour: UIFactory.PRIMARY_BLUE, backgroundColor:selected?UIFactory.WARNING_YELLOW:null);
            }
            else
            {
                card.Background = _uiFactory.CreateCardBackground(borderWidth: 3, backgroundColor: selected ? UIFactory.WARNING_YELLOW : null);
            }

            var title = new TextView(this)
            {
                Text = $"Box {boxNumber}",
                Gravity = GravityFlags.Center,
                TextSize = 18
            };
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Normal);
            title.SetTextColor(Color.Black);

            var summary = new TextView(this)
            {
                Text = $"{string.Concat(Enumerable.Repeat("🐧", thisBoxData.Adults))}" +
                    $"{string.Concat(Enumerable.Repeat("🥚", thisBoxData.Eggs))}" + 
                    $"{string.Concat(Enumerable.Repeat("🐣", thisBoxData.Chicks))}", 
                Gravity = GravityFlags.Center,
                TextSize = 14
            };
            if (boxNumber == 99)
                ;
            var remoteChicks = thisRemoteBoxData?.numChicks(); 
            var remoteEggs = thisRemoteBoxData?.numEggs();

            if (differenceFound && remoteChicks + remoteEggs > 0 && (thisBoxData.Eggs != remoteEggs || thisBoxData.Chicks != remoteChicks))
            {
                summary.Text += $"({string.Concat(Enumerable.Repeat("🥚", thisRemoteBoxData.numEggs()))}{string.Concat(Enumerable.Repeat("🐣", thisRemoteBoxData.numChicks()))})";
            }
            if (_remoteBreedingDates.ContainsKey(boxNumber))
            {
                summary.Text += "\n" + _remoteBreedingDates[boxNumber].breedingDateStatus();
            }
            if (differenceFound && thisRemoteBoxData?.breedingLikelyhoodText != "BR" && thisBoxData.Chicks + thisBoxData.Eggs + thisBoxData.Adults != 0)
            {
                summary.Text += $"({thisRemoteBoxData.breedingLikelyhoodText})";
            }
            summary.SetTextColor(Color.Black);

            string gateStatus = thisBoxData.GateStatus;
            string notes = string.IsNullOrWhiteSpace(thisBoxData.Notes) ? "" : "notes";
            notes += gotRemoteBoxData && !string.IsNullOrEmpty(thisRemoteBoxData.PersistentNotes) ? $" ({thisRemoteBoxData.PersistentNotes})" : ""; 
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

            card.AddView(title);
            if(!string.IsNullOrEmpty(summary.Text)) card.AddView(summary);
            if(!string.IsNullOrEmpty(gate_and_notes.Text)) card.AddView(gate_and_notes);
            card.Click += (sender, e) =>
            {
                JumpToBox(boxNumber);
                ScrollToTop();
            };
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
            _settingsCard = _uiFactory.CreateCard();

            TextView versionText = new TextView(this)
            {
                Text = "Version: " + version
            };
            versionText.SetTextColor(Color.Black);
            _settingsCard.AddView(versionText);

            _isBluetoothEnabledCheckBox = new CheckBox(this)
            {
                Text = "Enable bluetooth",
            };
            _isBluetoothEnabledCheckBox.SetTextColor(Color.Black);
            _isBluetoothEnabledCheckBox.Checked = false;
            _isBluetoothEnabledCheckBox.CheckedChange += (s, e) =>
            {
                if (_isBluetoothEnabledCheckBox.Checked)
                {
                    InitializeBluetooth();
                }
                else
                {
                    _bluetoothManager?.Dispose();
                    _bluetoothManager = null;
                    UpdateStatusText("Bluetooth Disabled");
                }
            };
            _settingsCard.AddView(_isBluetoothEnabledCheckBox);

            CheckBox setTimeActiveSessionPicker = new CheckBox(this)
            {
                Text = "Set time for active session",
                Checked = _appSettings.ActiveSessionTimeStampActive
            };
            setTimeActiveSessionPicker.SetTextColor(Color.Black);
            setTimeActiveSessionPicker.SetPadding(0, 0, 0, 0);
            setTimeActiveSessionPicker.CheckedChange += (s, e) =>
            {
                _appSettings.ActiveSessionTimeStampActive = setTimeActiveSessionPicker.Checked;
                if (setTimeActiveSessionPicker.Checked)
                {
                    var timePicker = new TimePickerDialog(this,
                        (sender, e) =>
                        {
                            _appSettings.ActiveSessionLocalTimeStamp = _appSettings.ActiveSessionLocalTimeStamp.Date.AddHours(e.HourOfDay).AddMinutes(e.Minute);
                        },
                        _appSettings.ActiveSessionLocalTimeStamp.Hour,
                        _appSettings.ActiveSessionLocalTimeStamp.Minute,
                        true); // true = 24 hour format

                    var datePicker = new DatePickerDialog(this, (sender, e) =>
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
            _settingsCard.AddView(setTimeActiveSessionPicker);



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
            var layout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
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
            var layout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };

            _prevBoxButton = _uiFactory.CreateStyledButton("← Prev box", UIFactory.PRIMARY_BLUE);
            _prevBoxButton.Click += OnPrevBoxClick;
            var prevParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            _prevBoxButton.LayoutParameters = prevParams;

            _selectBoxButton = new Button(this)
            {
                Text = "Select Box",
                Clickable = true,
                Focusable = true
            };
            _selectBoxButton.SetTextColor(Color.White);
            _selectBoxButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _selectBoxButton.SetPadding(16, 24, 16, 24);
            _selectBoxButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_BLUE, 8);
            _selectBoxButton.Click += OnBoxNumberClick;
            var boxParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            boxParams.SetMargins(8, 0, 8, 0);
            _selectBoxButton.LayoutParameters = boxParams;

            _nextBoxButton = _uiFactory.CreateStyledButton("Next box →", UIFactory.PRIMARY_BLUE);
            _nextBoxButton.Click += OnNextBoxClick;
            var nextParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            _nextBoxButton.LayoutParameters = nextParams;

            layout.AddView(_prevBoxButton);
            layout.AddView(_selectBoxButton);
            layout.AddView(_nextBoxButton);

            return layout;
        }
        internal void DrawPageLayouts()
        {
            new Handler(Looper.MainLooper).Post(() =>
                {
                    // Allow multiple pages visible at once
                    bool showSingle = _appSettings.VisiblePages.Contains(UIFactory.selectedPage.BoxDataSingle);
                    bool showSettings = _appSettings.VisiblePages.Contains(UIFactory.selectedPage.Settings);
                    bool showOverview = _appSettings.VisiblePages.Contains(UIFactory.selectedPage.BoxOverview);

                    _dataCard.Visibility = showSingle ? ViewStates.Visible : ViewStates.Gone;
                    _settingsCard.Visibility = showSettings ? ViewStates.Visible : ViewStates.Gone;
                    _multiBoxViewCard.Visibility = showOverview ? ViewStates.Visible : ViewStates.Gone;


                    ///Single Box Card
                    ///
                    // Update lock icon
                    if (_lockIconView != null)
                    {
                        _lockIconView.SetColorFilter(null);
                        if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox) && _isBoxLocked)
                        {
                            _lockIconView.SetImageResource(Resource.Drawable.locked_yellow);
                            _lockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.WARNING_YELLOW, // yellow
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                        else if (_isBoxLocked)
                        {
                            _lockIconView.SetImageResource(Resource.Drawable.locked_green);
                            _lockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.SUCCESS_GREEN,     // green
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                        else
                        {
                            _lockIconView.SetImageResource(Resource.Drawable.unlocked_red);
                            _lockIconView.SetColorFilter(
                                new Android.Graphics.PorterDuffColorFilter(
                                    UIFactory.DANGER_RED,     // red
                                    Android.Graphics.PorterDuff.Mode.SrcIn));
                        }
                    }

                    if (_dataCardTitleText != null)
                    {
                        _dataCardTitleText.Text = $"Box {_currentBox}";
                    }
                    _interestingBoxTextView.Visibility = ViewStates.Gone;
                    if (null != _remoteBoxData && _remoteBoxData.ContainsKey(_currentBox) && !string.IsNullOrWhiteSpace(_remoteBoxData[_currentBox].PersistentNotes))
                    {
                        _interestingBoxTextView.Text = "💡 Note: " + _remoteBoxData[_currentBox].PersistentNotes;
                        _interestingBoxTextView.Visibility = ViewStates.Visible;
                        _interestingBoxTextView.Gravity = GravityFlags.Center;
                        _interestingBoxTextView.SetBackgroundColor(UIFactory.LIGHT_GRAY);
                        _interestingBoxTextView.SetTextColor(UIFactory.PRIMARY_BLUE);
                    }

                    var editTexts = new[] { _adultsEditText, _eggsEditText, _chicksEditText, _notesEditText };

                    foreach (var editText in editTexts)
                    {
                        if (editText != null) editText.TextChanged -= OnDataChanged;
                    }

                    if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                    {
                        var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox];
                        if (_adultsEditText != null) _adultsEditText.Text = boxData.Adults.ToString();
                        if (_eggsEditText != null) _eggsEditText.Text = boxData.Eggs.ToString();
                        if (_chicksEditText != null) _chicksEditText.Text = boxData.Chicks.ToString();
                        SetSpinnerStatus(_gateStatusSpinner, boxData.GateStatus);
                        if (_notesEditText != null) _notesEditText.Text = boxData.Notes;
                        buildScannedIdsLayout(boxData.ScannedIds);
                        SetSpinnerStatus(_breedingChanceSpinner, !string.IsNullOrWhiteSpace(boxData.BreedingChance) ? boxData.BreedingChance : _remoteBoxData[_currentBox].breedingLikelyhoodText   );
                    }
                    else
                    {
                        if (_adultsEditText != null) _adultsEditText.Text = "0";
                        if (_eggsEditText != null) _eggsEditText.Text = "0";
                        if (_chicksEditText != null) _chicksEditText.Text = "0";
                        SetSpinnerStatus(_gateStatusSpinner, null);
                        if (_notesEditText != null) _notesEditText.Text = "";
                        buildScannedIdsLayout(new List<ScanRecord>());
                        SetSpinnerStatus(_breedingChanceSpinner, _remoteBoxData[_currentBox].breedingLikelyhoodText);
                    }

                    foreach (var editText in editTexts)
                    {
                        if (editText != null) editText.TextChanged += OnDataChanged;
                    }

                    //disable/enable UI elememts according to _isBoxLocked
                    for (int i = 0; i < _topButtonLayout.ChildCount; i++)
                    {
                        Button child = (Button) _topButtonLayout.GetChildAt(i);
                        SetEnabledRecursive(child, _isBoxLocked, _isBoxLocked ? 1.0f : 0.5f);
                        if (_appSettings.CurrentlyVisibleMonitor == 0 &&   _isBoxLocked && child.Text.Equals("Clear All") && _allMonitorData[0].BoxData.Count == 0)
                            SetEnabledRecursive(child, false, 0.5f);
                        else if ( _isBoxLocked && child.Text.Equals("Clear Box") && !_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                            SetEnabledRecursive(child, false, 0.5f);
                    }

                    // Enable/Disable navigation and data buttons when available
                    List<Button> buttonsToToggle = new List<Button> { _prevBoxButton, _selectBoxButton, _nextBoxButton };
                    foreach (var button in buttonsToToggle)
                    {
                        bool canGo = true;
                        if(button.Text.Contains("rev") && _currentBox == 1 || button.Text.Contains("ext") && _currentBox == numberMonitorBoxes) 
                        {
                            canGo = false;
                        }
                        button.Enabled = _isBoxLocked && canGo;
                        button.Alpha = (_isBoxLocked && canGo) ? 1.0f : 0.5f; // Grey out when unlocked
                    }

                    // title Layout "Box n" is item 0, which we don't want to disable!
                    for (int i = 1; i < _dataCard.ChildCount; i++)
                    {
                        var child = _dataCard.GetChildAt(i);
                        SetEnabledRecursive(child, !_isBoxLocked, _isBoxLocked ? 0.8f : 1.0f);
                    }




                    if (showOverview)
                    {
                        createMultiBoxViewCard();
                    }
                });
        }
        private bool dataCardHasZeroData()
        {
            int.TryParse(_adultsEditText?.Text ?? "0", out int adults);
            int.TryParse(_eggsEditText?.Text ?? "0", out int eggs);
            int.TryParse(_chicksEditText?.Text ?? "0", out int chicks);

            string? gate = GetSelectedGateStatus(_gateStatusSpinner); // returns null for blank
            bool noGate = string.IsNullOrEmpty(gate);
            bool noNotes = string.IsNullOrWhiteSpace(_notesEditText?.Text);

            return adults == 0 && eggs == 0 && chicks == 0  && noGate && noNotes;
        }
        private void CreateBoxDataCard()
        {
            if (_dataCard == null)
            {
                _dataCard = _uiFactory.CreateCard();
            }
            else
            {
                _dataCard.RemoveAllViews();
            }

            // Horizontal layout for lock icon + box title
            _dataCardTitleLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal,
                Clickable = true,
                Focusable = true
            };
            _dataCardTitleLayout.SetGravity(GravityFlags.Center);
            _dataCardTitleLayout.Click += (sender, e) =>
            {
                _isBoxLocked = !_isBoxLocked;
                if (!_isBoxLocked)
                {
                    DrawPageLayouts();
                }
                else
                {
                    if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox) && dataCardHasZeroData())
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

            // Box title text
            _dataCardTitleText = new TextView(this)
            {
                Text = $"Box {_currentBox}  ",
                TextSize = 30,
                Gravity = GravityFlags.Center
            };
            _dataCardTitleText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _dataCardTitleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _dataCardTitleText.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            _dataCardTitleLayout.AddView(_dataCardTitleText);

            // visually indicate locked state
            if (_isBoxLocked)
            {
                _dataCardTitleText.SetTextColor(Color.Gray);
            }
            else
            {
                _dataCardTitleText.SetTextColor(UIFactory.TEXT_PRIMARY);
            }

            var boxTitleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            boxTitleParams.SetMargins(0, 0, 0, 16);
            _dataCardTitleLayout.LayoutParameters = boxTitleParams;

            // Lock icon
            _lockIconView = new ImageView(this);
            _lockIconView.SetImageResource(Android.Resource.Drawable.IcLockLock);
            var iconParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            iconParams.SetMargins(0, 0, 12, 0); // Space between icon and text
            _lockIconView.LayoutParameters = iconParams;
            _dataCardTitleLayout.AddView(_lockIconView);

            _dataCard.AddView(_dataCardTitleLayout);


            _interestingBoxTextView = new TextView(this)
            {
                //Text = _remoteBoxData[_currentBox].PersistentNotes
            };
            _interestingBoxTextView.Visibility = ViewStates.Gone;
            _dataCard.AddView(_interestingBoxTextView);

            // Scanned birds container
            _scannedIdsLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };
            _scannedIdsLayout.SetPadding(16, 16, 16, 16);
            _scannedIdsLayout.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var idsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            idsParams.SetMargins(0, 0, 0, 16);
            _scannedIdsLayout.LayoutParameters = idsParams;
            _dataCard.AddView(_scannedIdsLayout);

            // Headings row: Adults, Eggs, Chicks, Gate Status
            var headingsLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
            var headingsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            headingsParams.SetMargins(0, 0, 0, 8);
            headingsLayout.LayoutParameters = headingsParams;

            var adultsLabel = _uiFactory.CreateDataLabel("Adults");
            var eggsLabel = _uiFactory.CreateDataLabel("Eggs");
            var chicksLabel = _uiFactory.CreateDataLabel("Chicks");
            var breedingChance = _uiFactory.CreateDataLabel("Breeding %");
            var gateLabel = _uiFactory.CreateDataLabel("Gate");

            headingsLayout.AddView(adultsLabel);
            headingsLayout.AddView(eggsLabel);
            headingsLayout.AddView(chicksLabel);
            headingsLayout.AddView(breedingChance);
            headingsLayout.AddView(gateLabel);
            _dataCard.AddView(headingsLayout);

            // Input fields row: Adults, Eggs, Chicks inputs, Gate Status spinner
            var inputFieldsLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
            var inputFieldsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            inputFieldsParams.SetMargins(0, 0, 0, 16);
            inputFieldsLayout.LayoutParameters = inputFieldsParams;

            _adultsEditText = _uiFactory.CreateStyledNumberField();
            _eggsEditText = _uiFactory.CreateStyledNumberField();
            _chicksEditText = _uiFactory.CreateStyledNumberField();

            _breedingChanceSpinner = new Spinner(this);
            _breedingChanceSpinner.SetPadding(16, 20, 16, 20);
            _breedingChanceSpinner.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);

            // Set the spinner to have the same layout weight as the input fields
            var spinnerParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            spinnerParams.SetMargins(4, 0, 4, 0);
            _breedingChanceSpinner.LayoutParameters = spinnerParams;
            List<string> items = new List<string> { "", "NO", "UNL", "POT", "CON", "BR" };
            ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, items);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _breedingChanceSpinner.Adapter = adapter;
            string? breedingChanceString = "";
            try { breedingChanceString = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox].BreedingChance; }
            catch { }
            int breedingPercentageIndex = 0;
            if (breedingChanceString != null)
                breedingPercentageIndex = items.FindIndex(x => x == breedingChanceString);
            breedingPercentageIndex = Math.Max(0, breedingPercentageIndex);
            _breedingChanceSpinner.SetSelection(breedingPercentageIndex, false);
            _breedingChanceSpinner.ItemSelected += (s, e) =>
            {
                string selectedItem = items[e.Position];
                string status = _breedingChanceSpinner.SelectedItem.ToString();
                if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Add(_currentBox, new BoxData());
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox].BreedingChance = status;
            };

            _gateStatusSpinner = _uiFactory.CreateGateStatusSpinner();
            _gateStatusSpinner.ItemSelected += (s, e) => 
            {
                string status = _gateStatusSpinner.SelectedItem.ToString();
                if (status.Equals("Gate up") || status.Equals("Regate"))
                {
                    if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                    {
                        _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Add(_currentBox, new BoxData());
                        _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox].GateStatus = status;
                        SaveToAppDataDir();
                        _isBoxLocked = true;
                        DrawPageLayouts();
                    }
                }
            };

            // Add event handlers
            _adultsEditText.TextChanged += OnDataChanged;
            _adultsEditText.Click += OnNumberFieldClick;
            _adultsEditText.FocusChange += OnNumberFieldFocus;

            _eggsEditText.TextChanged += OnDataChanged;
            _eggsEditText.Click += OnNumberFieldClick;
            _eggsEditText.FocusChange += OnNumberFieldFocus;

            _chicksEditText.TextChanged += OnDataChanged;
            _chicksEditText.Click += OnNumberFieldClick;
            _chicksEditText.FocusChange += OnNumberFieldFocus;

            inputFieldsLayout.AddView(_adultsEditText);
            inputFieldsLayout.AddView(_eggsEditText);
            inputFieldsLayout.AddView(_chicksEditText);
            inputFieldsLayout.AddView(_breedingChanceSpinner);
            inputFieldsLayout.AddView(_gateStatusSpinner);
            _dataCard.AddView(inputFieldsLayout);

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
            _dataCard.AddView(notesLabel);

            _notesEditText = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences,
                Hint = "Enter any additional notes...",
                Gravity = Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Start
            };
            _notesEditText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _notesEditText.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            _notesEditText.SetPadding(16, 16, 16, 16);
            _notesEditText.Background = _uiFactory.CreateRoundedBackground(UIFactory.LIGHTER_GRAY, 8);
            var notesEditParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            notesEditParams.SetMargins(0, 0, 0, 8);
            _notesEditText.LayoutParameters = notesEditParams;
            _notesEditText.TextChanged += OnDataChanged;
            _dataCard.AddView(_notesEditText);
        }
        private void SetEnabledRecursive(View view, bool enabled, float alpha)
        {
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
            NavigateToBox(_currentBox - 1, () => _currentBox > 1);
        }
        private void OnNextBoxClick(object? sender, EventArgs e)
        {
            NavigateToBox(_currentBox + 1, () => _currentBox < numberMonitorBoxes);
        }
        private void NavigateToBox(int targetBox, Func<bool> canNavigate)
        {
            if (!canNavigate())
                return;

            _currentBox = targetBox;
            DrawPageLayouts();
        }
        private void ShowEmptyBoxDialog(Action onConfirm, Action onCancel)
        {
            ShowConfirmationDialog(
                "Empty Box Confirmation",
                "Please confirm this box has been inspected and is empty",
                ("Confirm Empty", onConfirm),
                ("Cancel", onCancel)
            );
        }
        private void OnClearBoxClick(object? sender, EventArgs e)
        {
            ShowConfirmationDialog(
                "Clear Box Data",
                "Are you sure you want to clear data for box " + _currentBox + "?",
                ("Yes", () =>
                {
                    _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.Remove(_currentBox);
                    DrawPageLayouts();
                }
            ),
                ("No", () => { }
            )
            );
        }
        private void OnClearBoxesClick(object? sender, EventArgs e)
        {
            ShowConfirmationDialog(
                "Clear All Data",
                "Are you sure you want to clear data for ALL boxes? This cannot be undone!",
                ("Yes, Clear All", new Action(() =>
                {
                    _allMonitorData[0].BoxData.Clear();  //by design this only clears the current monitor.
                    _currentBox = 1;
                    ClearInternalStorageData();
                    SaveToAppDataDir();
                    DrawPageLayouts();
                })),
                ("Cancel", new Action(() => { }))
            );
        }
        private void OnSaveDataClick(object? sender, EventArgs e)
        {
            ShowSaveConfirmation();
        }
        private void ShowSaveConfirmation()
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle("Save data")
                .SetMessage(GetMonitorSummaryText(0))
                .SetPositiveButton("Save", (s, e) => ShowSaveFilenameDialog())
                .SetNeutralButton("Save & upload", (s,e)=> ShowSaveFilenameDialog(true))
                .SetNegativeButton("Cancel", (s, e) => { })
                .SetCancelable(true)
                .Create();

            alertDialog?.Show();


        }
        private void ShowConfirmationDialog(string title, string message, (string text, Action action) positiveButton)
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetMessage(message)
                .SetPositiveButton(positiveButton.text, (s, e) => positiveButton.action())
                .SetCancelable(true)
                .Create();

            alertDialog?.Show();
        }
        private void ShowConfirmationDialog(string title, string message, (string text, Action action) positiveButton, (string text, Action action) negativeButton)
        {
            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetMessage(message)
                .SetPositiveButton(positiveButton.text, (s, e) => positiveButton.action())
                .SetNegativeButton(negativeButton.text, (s, e) => negativeButton.action())
                .SetCancelable(true)
                .Create();

            alertDialog?.Show();
        }
        private void OnDataChanged(object? sender, TextChangedEventArgs e)
        {
            if (_isProcessingConfirmation)
                return;
            CheckForHighValueConfirmation();
        }
        private void CheckForHighValueConfirmation()
        {
            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?.Text ?? "0", out adults);
            int.TryParse(_eggsEditText?.Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?.Text ?? "0", out chicks);

            // Check if any values are 3 or greater - no state tracking, ask every time
            var highValues = new List<(string type, int count)>();
            if (adults > 2) highValues.Add(("adults", adults));
            if (eggs > 2) highValues.Add(("eggs", eggs));
            if (chicks > 2) highValues.Add(("chicks", chicks));

            if (highValues.Count > 0)
            {
                ShowHighValueConfirmationDialog(highValues);
            }
        }
        private void ShowHighValueConfirmationDialog(List<(string type, int count)> highValues)
        {
            _isProcessingConfirmation = true;

            var message = "Are you sure you have found:\n\n";
            foreach (var (type, count) in highValues)
            {
                message += $"• {count} {type}\n";
            }
            message += "\nThis is a high count. Please confirm this is correct.";

            ShowConfirmationDialog(
                "High Value Confirmation",
                message,
                ("Yes, Correct", () =>
                {
                    _isProcessingConfirmation = false;
                }
            ),
                ("No, Let me fix", () =>
                {
                    _isProcessingConfirmation = false;
                    // Don't save, let user modify the values
                }
            )
            );
        }
        private void SaveCurrentBoxData()
        {
            if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
            {
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox] = new BoxData();
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox].whenDataCollectedUtc = DateTime.UtcNow;
            }

            var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox];
            string boxDataString = boxData.ToString();
            
            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?.Text ?? "0", out adults);
            int.TryParse(_eggsEditText?.Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?.Text ?? "0", out chicks);

            boxData.Adults = adults;
            boxData.Eggs = eggs;
            boxData.Chicks = chicks;
            boxData.GateStatus = GetSelectedGateStatus(_gateStatusSpinner);
            boxData.Notes = _notesEditText?.Text ?? "";

            if (boxData.ToString() != boxDataString)
            {
                boxData.whenDataCollectedUtc = DateTime.UtcNow; // Update timestamp if data changed
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox] = boxData;
                SaveToAppDataDir();
            }
        }
        private void buildScannedIdsLayout(List<ScanRecord> scans)
        {
            if (_scannedIdsLayout == null) return;

            // Clear existing views
            _scannedIdsLayout.RemoveAllViews();

            if (scans.Count == 0)
            {
                var emptyText = new TextView(this)
                {
                    Text = "No birds scanned yet",
                    TextSize = 14
                };
                emptyText.SetTextColor(UIFactory.TEXT_SECONDARY);
                _scannedIdsLayout.AddView(emptyText);
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
                _scannedIdsLayout.AddView(headerText);

                // Individual scan records with delete buttons
                for (int i = 0; i < scans.Count; i++)
                {
                    var scan = scans[i];
                    var scanLayout = CreateScanRecordView(scan, i);
                    _scannedIdsLayout.AddView(scanLayout);
                }
            }

            // Add manual input section at the bottom
            var manualInputLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };
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
            _scannedIdsLayout.AddView(manualInputLayout);
        }
        private LinearLayout CreateScanRecordView(ScanRecord scan, int index)
        {
            var scanLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal
            };

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
                    if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                    {
                        var boxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox];
                        var scanToRemove = boxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToDelete.BirdId &&
                            s.Timestamp == scanToDelete.Timestamp);

                        if (scanToRemove != null)
                        {
                            boxData.ScannedIds.Remove(scanToRemove);
                            _remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData);
                            if (penguinData!=null && LifeStage.Adult == penguinData.LastKnownLifeStage)
                            {
                                _adultsEditText.Text = "" + Math.Max(0, int.Parse(_adultsEditText.Text ?? "0") - 1);
                            }
                            else if (penguinData != null && LifeStage.Chick == penguinData.LastKnownLifeStage)
                            {
                                _chicksEditText.Text = "" + Math.Max(0, int.Parse(_chicksEditText.Text ?? "0") - 1);
                            }
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(boxData.ScannedIds);
                            Toast.MakeText(this, $"🗑️ Bird {scanToDelete.BirdId} deleted from Box {_currentBox}", ToastLength.Short)?.Show();
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
                InputType = Android.Text.InputTypes.ClassNumber,
                Hint = $"Enter box number (1-{numberMonitorBoxes})"
            };
            input.SetTextColor(UIFactory.TEXT_PRIMARY);

            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle($"Move Bird {scanToMove.BirdId}")
                .SetMessage($"Move from Box {_currentBox} to:")
                .SetView(input)
                .SetPositiveButton("Move", (s, e) =>
                {
                    if (int.TryParse(input.Text, out int targetBox))
                    {
                        if (targetBox >= 1 && targetBox <= numberMonitorBoxes)
                        {
                            if (targetBox == _currentBox)
                            {
                                Toast.MakeText(this, "Bird is already in this box", ToastLength.Short)?.Show();
                            }
                            else
                            {
                                MoveScanToBox(scanToMove, targetBox);
                            }
                        }
                        else
                        {
                            Toast.MakeText(this, $"Box number must be between 1 and {numberMonitorBoxes}", ToastLength.Short)?.Show();
                        }
                    }
                    else
                    {
                        Toast.MakeText(this, "Please enter a valid box number", ToastLength.Short)?.Show();
                    }
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            alertDialog?.Show();
            
            input.RequestFocus();
            var inputMethodManager = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
            inputMethodManager?.ShowSoftInput(input, Android.Views.InputMethods.ShowFlags.Implicit);
        }
        private void MoveScanToBox(ScanRecord scanToMove, int targetBox)
        {
            ShowConfirmationDialog(
                "Move Bird Scan",
                $"Move bird {scanToMove.BirdId} from Box {_currentBox} to Box {targetBox}?",
                ("Yes, Move", () =>
                {
                    // Remove from current box
                    if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(_currentBox))
                    {
                        var currentBoxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[_currentBox];
                        var scanToRemove = currentBoxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToMove.BirdId &&
                            s.Timestamp == scanToMove.Timestamp);

                        if (_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(targetBox) 
                        && _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBox].ScannedIds.Any(s => s.BirdId == scanToMove.BirdId))
                        {
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} exists already in Box {targetBox}", ToastLength.Long)?.Show();
                        }
                        else if (scanToRemove != null)
                        {
                            currentBoxData.ScannedIds.Remove(scanToRemove);

                            // Add to target box
                            if (!_allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData.ContainsKey(targetBox))
                                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBox] = new BoxData();

                            var targetBoxData = _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBox];
                            targetBoxData.ScannedIds.Add(scanToMove);

                            _remotePenguinData.TryGetValue(scanToRemove.BirdId, out var penguinData);
                            if (LifeStage.Adult == penguinData.LastKnownLifeStage)
                            {
                                _adultsEditText.Text = "" + Math.Max(0, int.Parse(_adultsEditText.Text ?? "0") - 1);
                                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBox].Adults++;
                            }
                            else if (LifeStage.Chick == penguinData.LastKnownLifeStage)
                            {
                                _chicksEditText.Text = "" + Math.Max(0, int.Parse(_chicksEditText.Text ?? "0") - 1);
                                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].BoxData[targetBox].Adults++;
                            }
                            SaveCurrentBoxData();
                            buildScannedIdsLayout(currentBoxData.ScannedIds);
                            Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} moved from Box {_currentBox} to Box {targetBox}", ToastLength.Long)?.Show();
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
                _appSettings.filesDir = internalPath;

                // Load remote penguin data.
                _remotePenguinData = await _dataStorageService.loadRemotePengInfoFromAppDataDir(this);
                _remoteBoxData = await _dataStorageService.loadRemoteBoxInfoFromAppDataDir(this);
                _remoteBreedingDates = await _dataStorageService.loadBreedingDatesFromAppDataDir(this);
                if (_remotePenguinData != null && _remoteBoxData != null && _remoteBreedingDates != null)
                {
                    Toast.MakeText(this, $"{_remotePenguinData.Count} bird, {_remoteBoxData.Count} box, {_remoteBreedingDates.Count} breeding dates found.", ToastLength.Short)?.Show();
                }

                // Load main app data
                _allMonitorData = _dataStorageService.LoadFromAppDataDir(internalPath);
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
            _dataStorageService.SaveDataToInternalStorage(FilesDir?.AbsolutePath ?? "", _allMonitorData, this, reportHome: reportHome);
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
        private void AddScannedId(String fullEid, int addtoThisMonitor )
        {
            var cleanEid = new String(fullEid.Where(char.IsLetterOrDigit).ToArray());
            var shortId = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8) : cleanEid;

            // 
            if (!_allMonitorData[addtoThisMonitor].BoxData.ContainsKey(_currentBox))
                _allMonitorData[addtoThisMonitor].BoxData[_currentBox] = new BoxData();
            var boxData = _allMonitorData[addtoThisMonitor].BoxData[_currentBox];

            if (!boxData.ScannedIds.Any(s => s.BirdId == shortId))
            {
                var scanRecord = new ScanRecord
                {
                    BirdId = shortId,
                    Timestamp = DateTime.UtcNow,
                    Latitude = _currentLocation?.Latitude ?? 0,
                    Longitude = _currentLocation?.Longitude ?? 0,
                    Accuracy = _currentLocation?.Accuracy ?? -1
                };

                boxData.ScannedIds.Add(scanRecord);
                SaveCurrentBoxData();

                RunOnUiThread(() =>
                {
                    // Enhanced toast message with life stage info
                    string toastMessage = $"🐧 Bird {shortId} added to Box {_currentBox}";
                    if (_remotePenguinData.TryGetValue(shortId, out var penguin))
                    {
                        if (penguin.LastKnownLifeStage == LifeStage.Adult || 
                            penguin.LastKnownLifeStage == LifeStage.Returnee)
                        {
                            _adultsEditText.Text = (int.Parse(_adultsEditText.Text ?? "0") + 1).ToString();
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
                            _chicksEditText.Text = (int.Parse(_chicksEditText.Text ?? "0") + 1).ToString();
                            SaveCurrentBoxData();
                            triggerAlertAsync();
                            toastMessage += $" (+1 Chick)";
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
            var now = DateTime.UtcNow;
            var defaultFileName = $"PenguinMonitor {now:yyMMdd HHmmss}";

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
                _allMonitorData[_appSettings.CurrentlyVisibleMonitor].filename = fileName;
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
                        InitializeBluetooth();
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
        private void OnBoxNumberClick(object? sender, EventArgs e)
        {
            ShowBoxJumpDialog();
        }
        private void ShowBoxJumpDialog()
        {
            var input = new EditText(this)
            {
                Text = _currentBox.ToString()
            };
            input.InputType = InputTypes.ClassNumber;      // numeric keyboard
            input.SetSelectAllOnFocus(true);               // easy overwrite
            input.ImeOptions = (ImeAction)ImeFlags.NoExtractUi | ImeAction.Go;

            var dialog = new AlertDialog.Builder(this)
                .SetTitle("Jump to Box")
                .SetMessage($"Enter box number (1–{numberMonitorBoxes}):")
                .SetView(input)
                .SetPositiveButton("Go", (s, e) =>
                {
                    if (int.TryParse(input.Text, out int targetBox) && targetBox >= 1 && targetBox <= numberMonitorBoxes)
                    {
                        JumpToBox(targetBox);
                    }
                    else
                    {
                        Toast.MakeText(this, $"Box number must be between 1 and {numberMonitorBoxes}", ToastLength.Short)?.Show();
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
        private void JumpToBox(int targetBox)
        {
            if (targetBox == _currentBox)
            {
                Toast.MakeText(this, $"Already at Box {_currentBox}", ToastLength.Short)?.Show();
                return;
            }
            if (!_isBoxLocked)
            {
                Toast.MakeText(this, $"Cannot change box while current box is unlocked.", ToastLength.Short)?.Show();
                return;
            }
            if (!_appSettings.VisiblePages.Contains(UIFactory.selectedPage.BoxDataSingle))
                _appSettings.AddVisiblePage(UIFactory.selectedPage.BoxDataSingle);
            _currentBox = targetBox;
            DrawPageLayouts();
        }
        private string? GetSelectedGateStatus(Spinner spinner)
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
                "📂 Load from file",
                "📂 Load from server",
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