using Android;
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
using Android.Widget;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using BluePenguinMonitoring.Models;
using BluePenguinMonitoring.UI.Gestures;
using BluePenguinMonitoring.UI.Utils;
using BluePenguinMonitoring.UI.Factories;
using BluePenguinMonitoring.Services;

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

        private LinearLayout? _scannedIdsContainer;
        private EditText? _adultsEditText;
        private EditText? _eggsEditText;
        private EditText? _chicksEditText;
        private Spinner? _gateStatusSpinner;
        private EditText? _notesEditText;
        private EditText? _manualScanEditText;

        // Add gesture detection components
        private GestureDetector? _gestureDetector;
        private LinearLayout? _dataCard;

        // Services
        private UIFactory _uiFactory;
        private DataStorageService _dataStorageService = new DataStorageService();
        private CsvDataService _csvDataService = new CsvDataService();

        // HTTP client for CSV downloads
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string GOOGLE_SHEETS_URL = "https://docs.google.com/spreadsheets/d/1A2j56iz0_VNHiWNJORAzGDqTbZsEd76j-YI_gQZsDEE";

        // Data storage
        private Dictionary<int, BoxData> _boxDataStorage = new Dictionary<int, BoxData>();
        private List<CsvRowData> _downloadedCsvData = new List<CsvRowData>();
        private Dictionary<string, PenguinData> _remotePenguinData = new Dictionary<string, PenguinData>();

        private int _currentBox = 1;

        // High value confirmation tracking - reset on each entry
        private bool _isProcessingConfirmation = false;

        // Vibration and sound components
        private Vibrator? _vibrator;
        private MediaPlayer? _alertMediaPlayer;

        // Add a field for the data card title so it can be updated dynamically
        private TextView? _dataCardTitle;
        private LinearLayout _dataCardTitleLayout;
        private ImageView _lockIconView;
        private bool _isBoxLocked;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            _uiFactory = new UIFactory(this);
            RequestPermissions();
            LoadDataFromInternalStorage();
            CreateDataRecordingUI();
            LoadBoxData();
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

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
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

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M && permissions.Count > 0)
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
                    InitializeBluetooth();
                }
            }
            else
            {
                // Pre-Android 6 or no permissions needed
                InitializeGPS();
                InitializeBluetooth();
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
                    _alertMediaPlayer = MediaPlayer.Create(this, notificationUri);
                    _alertMediaPlayer?.SetAudioAttributes(
                        new AudioAttributes.Builder()
                            .SetUsage(AudioUsageKind.Alarm)
                            .SetContentType(AudioContentType.Sonification)
                            .Build()
                    );
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
                Toast.MakeText(this, "Please enable location services for accurate positioning", ToastLength.Long)?.Show();
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
            if (_currentBox < 150)
            {
                NavigateToBox(_currentBox + 1, () => _currentBox < 150);
            }
            else
            {
                Toast.MakeText(this, "Already at last box", ToastLength.Short)?.Show();
            }
        }
        private void OnEidDataReceived(string eidData)
        {
            AddScannedId(eidData);
        }
        private void UpdateStatusText(string? bluetoothStatus = null)
        {
            var btStatus = bluetoothStatus ?? (_bluetoothManager?.IsConnected == true ? "HR5 Connected" : "Connecting to HR5...");
            var gpsStatus = _gpsAccuracy > 0 ? $" | GPS: ±{_gpsAccuracy:F1}m" : " | GPS: No signal";

            RunOnUiThread(() =>
            {
                if (_statusText != null)
                {
                    _statusText.Text = btStatus + gpsStatus;

                    // Update status color based on connection state
                    if (btStatus.Contains("Connected") && _gpsAccuracy > 0)
                        _statusText.SetTextColor(UIFactory.SUCCESS_COLOR);
                    else if (btStatus.Contains("Connected"))
                        _statusText.SetTextColor(UIFactory.WARNING_COLOR);
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
                    
                    if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R) // Android 11+
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
                        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M)
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
                var jsonFiles = allFiles.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();

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
            try
            {
                var json = File.ReadAllText(filePath);
                var loadedData = JsonSerializer.Deserialize<JsonNode>(json);

                if (loadedData == null)
                {
                    Toast.MakeText(this, "❌ Invalid JSON file format", ToastLength.Long)?.Show();
                    return;
                }

                // Extract box data from the JSON structure
                var boxesNode = loadedData["Boxes"];
                if (boxesNode == null)
                {
                    Toast.MakeText(this, "❌ No box data found in JSON file", ToastLength.Long)?.Show();
                    return;
                }

                var newBoxDataStorage = new Dictionary<int, BoxData>();
                int boxCount = 0;
                int birdCount = 0;

                foreach (var boxItem in boxesNode.AsArray())
                {
                    var boxNumber = boxItem?["BoxNumber"]?.GetValue<int>() ?? 0;
                    var dataNode = boxItem?["Data"];

                    if (boxNumber > 0 && dataNode != null)
                    {
                        var boxData = new BoxData
                        {
                            Adults = dataNode["Adults"]?.GetValue<int>() ?? 0,
                            Eggs = dataNode["Eggs"]?.GetValue<int>() ?? 0,
                            Chicks = dataNode["Chicks"]?.GetValue<int>() ?? 0,
                            GateStatus = dataNode["GateStatus"]?.GetValue<string>(),
                            Notes = dataNode["Notes"]?.GetValue<string>() ?? ""
                        };

                        // Load scanned IDs
                        var scannedIdsNode = dataNode["ScannedIds"];
                        if (scannedIdsNode != null)
                        {
                            foreach (var scanItem in scannedIdsNode.AsArray())
                            {
                                var scanRecord = new ScanRecord
                                {
                                    BirdId = scanItem?["BirdId"]?.GetValue<string>() ?? "",
                                    Timestamp = scanItem?["Timestamp"]?.GetValue<DateTime>() ?? DateTime.Now,
                                    Latitude = scanItem?["Latitude"]?.GetValue<double>() ?? 0,
                                    Longitude = scanItem?["Longitude"]?.GetValue<double>() ?? 0,
                                    Accuracy = scanItem?["Accuracy"]?.GetValue<float>() ?? -1
                                };

                                boxData.ScannedIds.Add(scanRecord);
                                birdCount++;
                            }
                        }

                        newBoxDataStorage[boxNumber] = boxData;
                        boxCount++;
                    }
                }

                // Replace current data with loaded data
                _boxDataStorage = newBoxDataStorage;

                // Update current box if it exists in loaded data, otherwise go to first box
                if (!_boxDataStorage.ContainsKey(_currentBox))
                {
                    _currentBox = _boxDataStorage.Keys.Any() ? _boxDataStorage.Keys.Min() : 1;
                }

                // Refresh UI
                LoadBoxData();
                UpdateUI();
                SaveDataToInternalStorage(); // Auto-save the loaded data

                var fileName = System.IO.Path.GetFileName(filePath);
                Toast.MakeText(this, $"✅ Loaded {boxCount} boxes, {birdCount} birds\nFrom: {fileName}", ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to load JSON: {ex.Message}", ToastLength.Long)?.Show();
            }
        }
        private void ShowBoxDataSummary()
        {
            if (_boxDataStorage.Count == 0)
            {
                Toast.MakeText(this, "No box data to display", ToastLength.Short)?.Show();
                return;
            }

            var totalBirds = _boxDataStorage.Values.Sum(box => box.ScannedIds.Count);
            var totalAdults = _boxDataStorage.Values.Sum(box => box.Adults);
            var totalEggs = _boxDataStorage.Values.Sum(box => box.Eggs);
            var totalChicks = _boxDataStorage.Values.Sum(box => box.Chicks);
            var gateUpCount = _boxDataStorage.Values.Count(box => box.GateStatus == "gate up");
            var regateCount = _boxDataStorage.Values.Count(box => box.GateStatus == "regate");

            var summary = $"📊 Data Summary:\n\n" +
                         $"📦 {_boxDataStorage.Count} boxes with data\n" +
                         $"🐧 {totalBirds} bird scans\n" +
                         $"👥 {totalAdults} adults\n" +
                         $"🥚 {totalEggs} eggs\n" +
                         $"🐣 {totalChicks} chicks\n" +
                         $"🚪 Gate: {gateUpCount} up, {regateCount} regate\n\n" +
                         $"Box range: {(_boxDataStorage.Keys.Any() ? _boxDataStorage.Keys.Min() : 0)} - {(_boxDataStorage.Keys.Any() ? _boxDataStorage.Keys.Max() : 0)}";

            ShowConfirmationDialog(
                "Box Data Summary",
                summary,
                ("OK", () => { }
            ),
                ("Load JSON", LoadJsonDataFromFile)
            );
        }
        private async Task DownloadCsvDataAsync()
        {
            try
            {
                var csvUrl = _csvDataService.ConvertToGoogleSheetsCsvUrl(GOOGLE_SHEETS_URL);

                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, "📥 Downloading CSV data...", ToastLength.Short)?.Show();
                });

                var response = await _httpClient.GetAsync(csvUrl);
                response.EnsureSuccessStatusCode();

                var csvContent = await response.Content.ReadAsStringAsync();
                var parsedData = _csvDataService.ParseCsvData(csvContent);

                _downloadedCsvData = parsedData;

                // Populate the penguin data dictionary
                _remotePenguinData.Clear();
                foreach (var row in parsedData)
                {
                    if (!string.IsNullOrEmpty(row.ScannedId) && row.ScannedId.Length >= 8)
                    {
                        // Extract the 8-digit ID (take last 8 characters to match scanning behavior)
                        var cleanId = new string(row.ScannedId.Where(char.IsLetterOrDigit).ToArray());
                        var eightDigitId = cleanId.Length >= 8 ? cleanId.Substring(cleanId.Length - 8).ToUpper() : cleanId.ToUpper();

                        if (eightDigitId.Length == 8)
                        {
                            // Parse life stage
                            var lifeStage = LifeStage.Adult; // Default
                            if (!string.IsNullOrEmpty(row.LastKnownLifeStage))
                            {
                                if (Enum.TryParse<LifeStage>(row.LastKnownLifeStage, true, out var parsedLifeStage))
                                {
                                    lifeStage = parsedLifeStage;
                                }
                            }

                            var penguinData = new PenguinData
                            {
                                ScannedId = eightDigitId,
                                LastKnownLifeStage = lifeStage,
                                Sex = row.Sex ?? "",
                                VidForScanner = row.VidForScanner ?? ""
                            };

                            _remotePenguinData[eightDigitId] = penguinData;
                        }
                    }
                }

                // Save the remote penguin data to internal storage
                SaveRemotePenguinDataToInternalStorage();

                RunOnUiThread(() =>
                {
                    DrawBoxLayout();
                    Toast.MakeText(this, $"✅ Downloaded {parsedData.Count} rows, {_remotePenguinData.Count} penguin records", ToastLength.Short)?.Show();
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, $"❌ Download failed: {ex.Message}", ToastLength.Long)?.Show();
                });
            }
        }

        private void OnDownloadCsvClick(object? sender, EventArgs e)
        {
            _ = Task.Run(async () => await DownloadCsvDataAsync());
        }
        private void SaveRemotePenguinDataToInternalStorage()
        {
            try
            {
                var internalPath = FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(internalPath))
                {
                    _dataStorageService.SaveRemotePenguinDataToInternalStorage(internalPath, _remotePenguinData);
                    RunOnUiThread(() =>
                    {
                        Toast.MakeText(this, $"💾 Bird stats saved! ({_remotePenguinData.Count} records)", ToastLength.Short)?.Show();
                    });
                }
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, $"❌ Failed to save bird stats: {ex.Message}", ToastLength.Long)?.Show();
                });
            }
        }
        private void CreateDataRecordingUI()
        {
            _isBoxLocked = true;
            var scrollView = new ScrollView(this);
            scrollView.SetBackgroundColor(UIFactory.BACKGROUND_COLOR);

            // Initialize gesture detector and apply to ScrollView
            _gestureDetector = new GestureDetector(this, new SwipeGestureDetector(this));
            scrollView.Touch += OnScrollViewTouch;

            var layout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };

            // App header
            var headerCard = _uiFactory.CreateCard();
            var titleText = new TextView(this)
            {
                Text = "🐧 Penguin Monitoring",
                TextSize = 24,
                Gravity = GravityFlags.Center
            };
            titleText.SetTextColor(UIFactory.PRIMARY_COLOR);
            titleText.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            headerCard.AddView(titleText);

            _statusText = new TextView(this)
            {
                Text = "Connecting to HR5... | GPS: No signal",
                TextSize = 14,
                Gravity = GravityFlags.Center
            };
            _statusText.SetTextColor(UIFactory.TEXT_SECONDARY);
            var statusParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            statusParams.SetMargins(0, 20, 0, 0);
            _statusText.LayoutParameters = statusParams;
            headerCard.AddView(_statusText);
            layout.AddView(headerCard);

            // Action buttons
            var topButtonLayout = CreateStyledButtonLayout(
                ("Clear All", OnClearBoxesClick, UIFactory.DANGER_COLOR),
                ("Clear Box", OnClearBoxClick, UIFactory.WARNING_COLOR),
                ("Bird Stats", OnDownloadCsvClick, UIFactory.PRIMARY_DARK),
                ("Data", OnDataClick, UIFactory.SUCCESS_COLOR)
            );
            topButtonLayout.LayoutParameters = statusParams;
            headerCard.AddView(topButtonLayout);

            // Navigation card
            var boxNavLayout = CreateNavigationLayout();
            boxNavLayout.LayoutParameters = statusParams;
            headerCard.AddView(boxNavLayout);

            // Data card
            _dataCard = _uiFactory.CreateCard();
            CreateBoxDataCard(_dataCard);
            DrawBoxLayout();

            layout.AddView(_dataCard);
            scrollView.AddView(layout);
            SetContentView(scrollView);

            scrollView.SetOnApplyWindowInsetsListener(new ViewInsetsListener());
        }
        private void OnScrollViewTouch(object? sender, View.TouchEventArgs e)
        {
            if (_gestureDetector != null && e.Event != null)
            {
                _gestureDetector.OnTouchEvent(e.Event);
            }
            e.Handled = false; // Allow scrolling to continue
        }
        private class ViewInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
        {
            public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
            {
                int topInset = insets.SystemWindowInsetTop;
                int bottomInset = insets.SystemWindowInsetBottom;
                
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.P && insets.DisplayCutout != null)
                {
                    topInset = Math.Max(topInset, insets.DisplayCutout.SafeInsetTop);
                }

                // Apply padding to avoid content being hidden behind system UI
                v.SetPadding(20, topInset + 20, 20, bottomInset + 20);

                return insets;
            }
        }

        private LinearLayout CreateCard()
        {
            var card = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };

            card.SetPadding(20, 16, 20, 16);
            card.Background = CreateCardBackground();

            var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            cardParams.SetMargins(0, 0, 0, 16);
            card.LayoutParameters = cardParams;

            return card;
        }

        private GradientDrawable CreateCardBackground()
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(UIFactory.CARD_COLOR);
            drawable.SetCornerRadius(12 * Resources?.DisplayMetrics?.Density ?? 12);
            drawable.SetStroke(1, Color.ParseColor("#E0E0E0"));
            return drawable;
        }

        private GradientDrawable CreateRoundedBackground(Color color, int radiusDp)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(color);
            drawable.SetCornerRadius(radiusDp * Resources?.DisplayMetrics?.Density ?? 8);
            return drawable;
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

            _prevBoxButton = _uiFactory.CreateStyledButton("← Prev box", UIFactory.PRIMARY_COLOR);
            _prevBoxButton.Click += OnPrevBoxClick;
            var prevParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            _prevBoxButton.LayoutParameters = prevParams;

            _selectBoxButton = new Button(this)
            {
                Text = "Select Box",
                Gravity = GravityFlags.Center,
                Clickable = true,
                Focusable = true
            };
            _selectBoxButton.SetTextColor(Color.White);
            _selectBoxButton.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            _selectBoxButton.SetPadding(16, 24, 16, 24);
            _selectBoxButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_COLOR, 8);
            _selectBoxButton.Click += OnBoxNumberClick;
            var boxParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            boxParams.SetMargins(8, 0, 8, 0);
            _selectBoxButton.LayoutParameters = boxParams;

            _nextBoxButton = _uiFactory.CreateStyledButton("Next box →", UIFactory.PRIMARY_COLOR);
            _nextBoxButton.Click += OnNextBoxClick;
            var nextParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            _nextBoxButton.LayoutParameters = nextParams;

            layout.AddView(_prevBoxButton);
            layout.AddView(_selectBoxButton);
            layout.AddView(_nextBoxButton);

            return layout;
        }
        private void DrawBoxLayout()
        {
            // Update lock icon
            if (_lockIconView != null)
            {
                _lockIconView.SetImageResource(_isBoxLocked
                    ? Android.Resource.Drawable.IcLockLock
                    : Resource.Drawable.LockUnlocked);
            }

            // Enable/Disable navigation and data buttons when locked/unlocked
            List<Button> buttonsToToggle = new List<Button> { _prevBoxButton, _nextBoxButton, _selectBoxButton };
            foreach (var button in buttonsToToggle)
            {
                button.Enabled = _isBoxLocked;
                button.Alpha = _isBoxLocked ? 1.0f : 0.5f; // Grey out when unlocked
            }

            // title "Box n" is item 0, which we don't want to disable!
            for (int i = 1; i < _dataCard.ChildCount; i++)
            {
                var child = _dataCard.GetChildAt(i);
                SetEnabledRecursive(child, !_isBoxLocked, _isBoxLocked ? 0.8f : 1.0f);
            }

            // Find and disable the "Data" button
            var parentLayout = _prevBoxButton?.Parent as LinearLayout;
            var headerLayout = parentLayout?.Parent as LinearLayout;
            if (headerLayout != null)
            {
                for (int i = 0; i < headerLayout.ChildCount; i++)
                {
                    var child = headerLayout.GetChildAt(i);
                    if (child is LinearLayout buttonRow)
                    {
                        for (int j = 0; j < buttonRow.ChildCount; j++)
                        {
                            var btn = buttonRow.GetChildAt(j) as Button;
                            if (btn != null && btn.Text == "Data")
                            {
                                btn.Enabled = _isBoxLocked;
                            }
                        }
                    }
                }
            }
        }
        private void CreateBoxDataCard(LinearLayout layout)
        {
            // Horizontal layout for lock icon + box title
            _dataCardTitleLayout = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Horizontal,
                Clickable = true,
                Focusable = true
            };
            _dataCardTitleLayout.SetGravity(GravityFlags.CenterHorizontal);
            _dataCardTitleLayout.Click += (sender, e) =>
            {
                _isBoxLocked = !_isBoxLocked;
                if (!_isBoxLocked)
                {
                    Toast.MakeText(this, "🔓 Box unlocked for editing\n\nPlease lock the box when done to prevent data loss.", ToastLength.Long)?.Show();
                }
                else
                {
                    SaveCurrentBoxData();
                    Toast.MakeText(this, "🔒 Box locked", ToastLength.Short)?.Show();
                }
                DrawBoxLayout();
            };

            // Update lock icon
            if (_lockIconView != null)
            {
                _lockIconView.SetImageResource(_isBoxLocked
                    ? Android.Resource.Drawable.IcLockLock
                    : Resource.Drawable.LockUnlocked);
            }

            // Enable/Disable navigation and data buttons when locked/unlocked
            List<Button> buttonsToToggle = new List<Button> { _prevBoxButton, _nextBoxButton, _selectBoxButton };
            foreach (var button in buttonsToToggle)
            {
                button.Enabled = _isBoxLocked;
                button.Alpha = _isBoxLocked ? 1.0f : 0.5f; // Grey out when unlocked
            }

            // title "Box n" is item 0, which we don't want to disable!
            for (int i = 1; i < _dataCard.ChildCount; i++)
            {
                var child = _dataCard.GetChildAt(i);
                SetEnabledRecursive(child, !_isBoxLocked, _isBoxLocked ? 0.8f : 1.0f);
            }

            // Find and disable the "Data" button
            var parentLayout = _prevBoxButton?.Parent as LinearLayout;
            var headerLayout = parentLayout?.Parent as LinearLayout;
            if (headerLayout != null)
            {
                for (int i = 0; i < headerLayout.ChildCount; i++)
                {
                    var child = headerLayout.GetChildAt(i);
                    if (child is LinearLayout buttonRow)
                    {
                        for (int j = 0; j < buttonRow.ChildCount; j++)
                        {
                            var btn = buttonRow.GetChildAt(j) as Button;
                            if (btn != null && btn.Text == "Data")
                            {
                                btn.Enabled = _isBoxLocked;
                            }
                        }
                    }
                }
            }

            // Box title text
            _dataCardTitle = new TextView(this)
            {
                Text = $"Box {_currentBox}",
                TextSize = 30,
                Gravity = GravityFlags.CenterHorizontal
            };
            _dataCardTitle.SetTextColor(UIFactory.TEXT_PRIMARY);
            _dataCardTitle.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            var dataTitleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            _dataCardTitle.LayoutParameters = dataTitleParams;
            _dataCardTitleLayout.AddView(_dataCardTitle);

            // visually indicate locked state
            if (_isBoxLocked)
            {
                _dataCardTitle.SetTextColor(Color.Gray);
                SaveCurrentBoxData();
            }
            else
            {
                _dataCardTitle.SetTextColor(UIFactory.TEXT_PRIMARY);
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

            

            layout.AddView(_dataCardTitleLayout);

            // Scanned birds container
            _scannedIdsContainer = new LinearLayout(this)
            {
                Orientation = Android.Widget.Orientation.Vertical
            };
            _scannedIdsContainer.SetPadding(16, 16, 16, 16);
            _scannedIdsContainer.Background = _uiFactory.CreateRoundedBackground(UIFactory.TEXT_FIELD_BACKGROUND_COLOR, 8);
            var idsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            idsParams.SetMargins(0, 0, 0, 16);
            _scannedIdsContainer.LayoutParameters = idsParams;
            layout.AddView(_scannedIdsContainer);

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
            var gateLabel = _uiFactory.CreateDataLabel("Gate Status");

            headingsLayout.AddView(adultsLabel);
            headingsLayout.AddView(eggsLabel);
            headingsLayout.AddView(chicksLabel);
            headingsLayout.AddView(gateLabel);
            layout.AddView(headingsLayout);

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
            _gateStatusSpinner = _uiFactory.CreateGateStatusSpinner();

            // Set the spinner to have the same layout weight as the input fields
            var spinnerParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            spinnerParams.SetMargins(4, 0, 4, 0);
            _gateStatusSpinner.LayoutParameters = spinnerParams;

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

            _gateStatusSpinner.ItemSelected += OnGateStatusChanged;

            inputFieldsLayout.AddView(_adultsEditText);
            inputFieldsLayout.AddView(_eggsEditText);
            inputFieldsLayout.AddView(_chicksEditText);
            inputFieldsLayout.AddView(_gateStatusSpinner);
            layout.AddView(inputFieldsLayout);

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
            layout.AddView(notesLabel);

            _notesEditText = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagCapSentences,
                Hint = "Enter any additional notes...",
                Gravity = Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Start
            };
            _notesEditText.SetLines(3);
            _notesEditText.SetTextColor(UIFactory.TEXT_PRIMARY);
            _notesEditText.SetHintTextColor(UIFactory.TEXT_SECONDARY);
            _notesEditText.SetPadding(16, 16, 16, 16);
            _notesEditText.Background = _uiFactory.CreateRoundedBackground(UIFactory.TEXT_FIELD_BACKGROUND_COLOR, 8);
            var notesEditParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            notesEditParams.SetMargins(0, 0, 0, 8);
            _notesEditText.LayoutParameters = notesEditParams;
            _notesEditText.TextChanged += OnDataChanged;
            layout.AddView(_notesEditText);
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

        private void OnGateStatusChanged(object? sender, AdapterView.ItemSelectedEventArgs e)
        {
            // Only save if a real gate status is selected (not the blank option)
            var selectedItem = _gateStatusSpinner?.SelectedItem?.ToString() ?? "";
            if (!string.IsNullOrEmpty(selectedItem))
            {
                SaveCurrentBoxData();
            }
        }

        private void OnPrevBoxClick(object? sender, EventArgs e)
        {
            NavigateToBox(_currentBox - 1, () => _currentBox > 1);
        }

        private void OnNextBoxClick(object? sender, EventArgs e)
        {
            NavigateToBox(_currentBox + 1, () => _currentBox < 150);
        }

        private void NavigateToBox(int targetBox, Func<bool> canNavigate)
        {
            if (!canNavigate())
                return;

            if (!_boxDataStorage.ContainsKey(_currentBox))
            {
                ShowEmptyBoxDialog(() =>
                {
                    _currentBox = targetBox;
                    CompleteNavigation();
                }, () =>
                {
                    _currentBox = targetBox;
                    CompleteNavigation();
                });
            }
            else
            {
                _currentBox = targetBox;
                CompleteNavigation();
            }
        }

        private void CompleteNavigation()
        {
            LoadBoxData();
            UpdateUI();
        }

        private void ShowEmptyBoxDialog(Action onConfirm, Action onSkip)
        {
            ShowConfirmationDialog(
                "Empty Box Confirmation",
                "Please confirm this box has been inspected and is empty",
                ("Confirm Empty", onConfirm),
                ("Skip", onSkip)
            );
        }

        private void OnClearBoxClick(object? sender, EventArgs e)
        {
            ShowConfirmationDialog(
                "Clear Box Data",
                "Are you sure you want to clear data for box " + _currentBox + "?",
                ("Yes", () =>
                {
                    _boxDataStorage.Remove(_currentBox);
                    LoadBoxData();
                    UpdateUI();
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
                ("Yes, Clear All", () =>
                {
                    _boxDataStorage.Clear();
                    _currentBox = 1;
                    LoadBoxData();
                    ClearInternalStorageData();
                    UpdateUI();
                }
            ),
                ("Cancel", () => { }
            )
            );
        }

        private void OnSaveDataClick(object? sender, EventArgs e)
        {
            if (!_boxDataStorage.ContainsKey(_currentBox))
            {
                ShowConfirmationDialog(
                    "Empty Box Confirmation",
                    "Confirm this box has been inspected, and is empty",
                    ("Confirm Empty", () =>
                    {
                        SaveCurrentBoxData();
                        ShowSaveConfirmation();
                    }
                ),
                    ("Skip", () => { }
                )
                );
            }
            else
            {
                SaveCurrentBoxData();
                ShowSaveConfirmation();
            }
        }

        private void ShowSaveConfirmation()
        {
            var totalBoxes = _boxDataStorage.Count;
            var totalBirds = _boxDataStorage.Values.Sum(box => box.ScannedIds.Count);
            var totalAdults = _boxDataStorage.Values.Sum(box => box.Adults);
            var totalEggs = _boxDataStorage.Values.Sum(box => box.Eggs);
            var totalChicks = _boxDataStorage.Values.Sum(box => box.Chicks);
            
            // Only count actual gate status values - ignore nulls
            var gateUpCount = _boxDataStorage.Values.Count(box => box.GateStatus == "gate up");
            var regateCount = _boxDataStorage.Values.Count(box => box.GateStatus == "regate");

            ShowConfirmationDialog(
                "Save All Data",
                $"Save data to Downloads folder?\n\n📦 {totalBoxes} boxes\n🐧 {totalBirds} bird scans\n👥 {totalAdults} adults\n🥚 {totalEggs} eggs\n🐣 {totalChicks} chicks\n🚪 Gate: {gateUpCount} up, {regateCount} regate",
                ("Save", SaveAllData),
                ("Cancel", () => { }
            )
            );
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
            else
            {
                // No high values, save normally
                SaveCurrentBoxData();
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
                    SaveCurrentBoxData();
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
            if (!_boxDataStorage.ContainsKey(_currentBox))
                _boxDataStorage[_currentBox] = new BoxData();

            var boxData = _boxDataStorage[_currentBox];

            int adults, eggs, chicks;
            int.TryParse(_adultsEditText?.Text ?? "0", out adults);
            int.TryParse(_eggsEditText?.Text ?? "0", out eggs);
            int.TryParse(_chicksEditText?.Text ?? "0", out chicks);

            boxData.Adults = adults;
            boxData.Eggs = eggs;
            boxData.Chicks = chicks;
            boxData.GateStatus = GetSelectedGateStatus();
            boxData.Notes = _notesEditText?.Text ?? "";

            SaveDataToInternalStorage();
        }

        private void LoadBoxData()
        {
            var editTexts = new[] { _adultsEditText, _eggsEditText, _chicksEditText, _notesEditText };

            foreach (var editText in editTexts)
            {
                if (editText != null) editText.TextChanged -= OnDataChanged;
            }

            _gateStatusSpinner.ItemSelected -= OnGateStatusChanged;

            if (_boxDataStorage.ContainsKey(_currentBox))
            {
                var boxData = _boxDataStorage[_currentBox];
                if (_adultsEditText != null) _adultsEditText.Text = boxData.Adults.ToString();
                if (_eggsEditText != null) _eggsEditText.Text = boxData.Eggs.ToString();
                if (_chicksEditText != null) _chicksEditText.Text = boxData.Chicks.ToString();
                SetSelectedGateStatus(boxData.GateStatus);
                if (_notesEditText != null) _notesEditText.Text = boxData.Notes;
                UpdateScannedIdsDisplay(boxData.ScannedIds);
            }
            else
            {
                if (_adultsEditText != null) _adultsEditText.Text = "0";
                if (_eggsEditText != null) _eggsEditText.Text = "0";
                if (_chicksEditText != null) _chicksEditText.Text = "0";
                SetSelectedGateStatus(null);
                if (_notesEditText != null) _notesEditText.Text = "";
                UpdateScannedIdsDisplay(new List<ScanRecord>());
            }

            foreach (var editText in editTexts)
            {
                if (editText != null) editText.TextChanged += OnDataChanged;
            }

            _gateStatusSpinner.ItemSelected += OnGateStatusChanged;
        }

        // Update the title when the box changes
        private void UpdateUI()
        {
            if (_dataCardTitle != null) _dataCardTitle.Text = $"Box {_currentBox}";
            if (_prevBoxButton != null)
            {
                _prevBoxButton.Enabled = _currentBox > 1;
                _prevBoxButton.Alpha = _currentBox > 1 ? 1.0f : 0.5f;
            }
            if (_nextBoxButton != null)
            {
                _nextBoxButton.Enabled = _currentBox < 150;
                _nextBoxButton.Alpha = _currentBox < 150 ? 1.0f : 0.5f;
            }
        }

        private void UpdateScannedIdsDisplay(List<ScanRecord> scans)
        {
            if (_scannedIdsContainer == null) return;

            // Clear existing views
            _scannedIdsContainer.RemoveAllViews();

            if (scans.Count == 0)
            {
                var emptyText = new TextView(this)
                {
                    Text = "No birds scanned yet",
                    TextSize = 14
                };
                emptyText.SetTextColor(UIFactory.TEXT_SECONDARY);
                _scannedIdsContainer.AddView(emptyText);
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
                _scannedIdsContainer.AddView(headerText);

                // Individual scan records with delete buttons
                for (int i = 0; i < scans.Count; i++)
                {
                    var scan = scans[i];
                    var scanLayout = CreateScanRecordView(scan, i);
                    _scannedIdsContainer.AddView(scanLayout);
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
            _manualScanEditText.Background = _uiFactory.CreateRoundedBackground(Color.White, 6);
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
            addButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.SUCCESS_COLOR, 6);
            addButton.SetAllCaps(false);

            var addButtonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            addButton.LayoutParameters = addButtonParams;

            addButton.Click += OnManualAddClick;

            manualInputLayout.AddView(_manualScanEditText);
            manualInputLayout.AddView(addButton);
            _scannedIdsContainer.AddView(manualInputLayout);
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
            
            if (_remotePenguinData.TryGetValue(scan.BirdId, out var penguinData))
            {
                // Penguin found in remote data - prioritize life stage over sex
                if (penguinData.LastKnownLifeStage == LifeStage.Chick)
                {
                    backgroundColor = UIFactory.CHICK_BACKGROUND;
                    additionalInfo = " 🐣"; // Chick emoji
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
            var timeStr = scan.Timestamp.ToString("MMM dd, HH:mm");
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
            moveButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.PRIMARY_COLOR, 6);
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
            deleteButton.Background = _uiFactory.CreateRoundedBackground(UIFactory.DANGER_COLOR, 6);
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
                    if (_boxDataStorage.ContainsKey(_currentBox))
                    {
                        var boxData = _boxDataStorage[_currentBox];
                        var scanToRemove = boxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToDelete.BirdId &&
                            s.Timestamp == scanToDelete.Timestamp);

                        if (scanToRemove != null)
                        {
                            boxData.ScannedIds.Remove(scanToRemove);
                            SaveDataToInternalStorage();
                            UpdateScannedIdsDisplay(boxData.ScannedIds);

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
                Hint = "Enter box number (1-150)"
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
                        if (targetBox >= 1 && targetBox <= 150)
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
                            Toast.MakeText(this, "Box number must be between 1 and 150", ToastLength.Short)?.Show();
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
                    if (_boxDataStorage.ContainsKey(_currentBox))
                    {
                        var currentBoxData = _boxDataStorage[_currentBox];
                        var scanToRemove = currentBoxData.ScannedIds.FirstOrDefault(s =>
                            s.BirdId == scanToMove.BirdId &&
                            s.Timestamp == scanToMove.Timestamp);

                        if (scanToRemove != null)
                        {
                            currentBoxData.ScannedIds.Remove(scanToRemove);

                            // Add to target box
                            if (!_boxDataStorage.ContainsKey(targetBox))
                                _boxDataStorage[targetBox] = new BoxData();

                            var targetBoxData = _boxDataStorage[targetBox];
                        
                            // Check if bird already exists in target box
                            if (!targetBoxData.ScannedIds.Any(s => s.BirdId == scanToMove.BirdId))
                            {
                                targetBoxData.ScannedIds.Add(scanToMove);                                

                                SaveDataToInternalStorage();
                                UpdateScannedIdsDisplay(currentBoxData.ScannedIds);

                                Toast.MakeText(this, $"🔄 Bird {scanToMove.BirdId} moved from Box {_currentBox} to Box {targetBox}", ToastLength.Long)?.Show();
                            }
                            else
                            {
                                // Restore to current box since target already has this bird
                                currentBoxData.ScannedIds.Add(scanToRemove);
                                Toast.MakeText(this, $"❌ Bird {scanToMove.BirdId} already exists in Box {targetBox}", ToastLength.Long)?.Show();
                            }
                        }
                    }
                }),
                ("Cancel", () => { })
            );
        }

        private void LoadDataFromInternalStorage()
        {
            try
            {
                var internalPath = FilesDir?.AbsolutePath;
                if (string.IsNullOrEmpty(internalPath))
                    return;

                // Load remote penguin data.
                var remotePenguinData = _dataStorageService.LoadRemotePenguinDataFromInternalStorage(internalPath);
                if (remotePenguinData != null)
                {
                    _remotePenguinData = remotePenguinData;
                    Toast.MakeText(this, $"🐧 {_remotePenguinData.Count} bird records loaded", ToastLength.Short)?.Show();
                }

                // Load main app data
                var appState = _dataStorageService.LoadDataFromInternalStorage(internalPath);
                if (appState != null)
                {
                    _currentBox = appState.CurrentBox;
                    _boxDataStorage = appState.BoxData ?? new Dictionary<int, BoxData>();
                    Toast.MakeText(this, $"📱 Data restored...", ToastLength.Short)?.Show();
                }
            }
            catch (Exception ex)
            {
                _currentBox = 1;
                _boxDataStorage = new Dictionary<int, BoxData>();
                _remotePenguinData = new Dictionary<string, PenguinData>();
                System.Diagnostics.Debug.WriteLine($"Failed to load data: {ex.Message}");
            }
        }
        private void TriggerChickAlert()
        {
            try
            {
                // Vibrate for 500ms
                if (_vibrator != null)
                {
                    if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                    {
                        // Use VibrationEffect for API 26+
                        var vibrationEffect = VibrationEffect.CreateOneShot(500, VibrationEffect.DefaultAmplitude);
                        _vibrator.Vibrate(vibrationEffect);
                    }
                    else
                    {
                        // Use deprecated method for older APIs
#pragma warning disable CS0618 // Type or member is obsolete
                        _vibrator.Vibrate(500);
#pragma warning restore CS0618 // Type or member is obsolete
                    }
                }

                // Play alert sound
                if (_alertMediaPlayer != null)
                {
                    try
                    {
                        if (_alertMediaPlayer.IsPlaying)
                        {
                            _alertMediaPlayer.Stop();
                            _alertMediaPlayer.Prepare();
                        }
                        _alertMediaPlayer.Start();
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

        private void SaveDataToInternalStorage()
        {
            var appState = new AppDataState
            {
                CurrentBox = _currentBox,
                LastSaved = DateTime.Now,
                BoxData = _boxDataStorage
            };

            _dataStorageService.SaveDataToInternalStorage(FilesDir?.AbsolutePath ?? "", appState);
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

        private void AddScannedId(String fullEid)
        {
            var cleanEid = new String(fullEid.Where(char.IsLetterOrDigit).ToArray());
            var shortId = cleanEid.Length >= 8 ? cleanEid.Substring(cleanEid.Length - 8) : cleanEid;

            if (!_boxDataStorage.ContainsKey(_currentBox))
                _boxDataStorage[_currentBox] = new BoxData();

            var boxData = _boxDataStorage[_currentBox];

            if (!boxData.ScannedIds.Any(s => s.BirdId == shortId))
            {
                var scanRecord = new ScanRecord
                {
                    BirdId = shortId,
                    Timestamp = DateTime.Now,
                    Latitude = _currentLocation?.Latitude ?? 0,
                    Longitude = _currentLocation?.Longitude ?? 0,
                    Accuracy = _currentLocation?.Accuracy ?? -1
                };

                boxData.ScannedIds.Add(scanRecord);

                // Check if this penguin should auto-increment Adults count
                if (_remotePenguinData.TryGetValue(shortId, out var penguinData))
                {
                    if (penguinData.LastKnownLifeStage == LifeStage.Adult || 
                        penguinData.LastKnownLifeStage == LifeStage.Returnee)
                    {
                        boxData.Adults++;
                        
                        RunOnUiThread(() =>
                        {
                            // Update the Adults field in the UI
                            if (_adultsEditText != null)
                            {
                                _adultsEditText.Text = boxData.Adults.ToString();
                            }
                        });
                    }
                }

                SaveDataToInternalStorage();

                RunOnUiThread(() =>
                {
                    UpdateScannedIdsDisplay(boxData.ScannedIds);
                    
                    // Enhanced toast message with life stage info
                    string toastMessage = $"🐧 Bird {shortId} added to Box {_currentBox}";
                    if (_remotePenguinData.TryGetValue(shortId, out var penguin))
                    {
                        if (penguin.LastKnownLifeStage == LifeStage.Adult || 
                            penguin.LastKnownLifeStage == LifeStage.Returnee)
                        {
                            toastMessage += $" (+1 Adult)";
                        }
                        else if (penguin.LastKnownLifeStage == LifeStage.Chick)
                        {
                            toastMessage += $" (Chick)";
                            TriggerChickAlert();
                        }
                    }
                    
                    Toast.MakeText(this, toastMessage, ToastLength.Short)?.Show();
                });
            }
        }

        private void SaveAllData()
        {
            ShowSaveFilenameDialog();
        }

        private void ShowSaveFilenameDialog()
        {
            var now = DateTime.Now;
            var defaultFileName = $"PenguinMonitoring {now:yyMMdd HHmmss}";

            var input = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassText,
                Text = defaultFileName,
                Hint = "Enter filename (without .json extension)"
            };
            input.SetTextColor(UIFactory.TEXT_PRIMARY);
            input.SetPadding(16, 16, 16, 16);
            input.Background = _uiFactory.CreateRoundedBackground(UIFactory.TEXT_FIELD_BACKGROUND_COLOR, 8);

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

                    SaveDataWithFilename(fileName);
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Create();

            alertDialog?.Show();
            
            input.RequestFocus();
            input.SelectAll();

            var inputMethodManager = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
            inputMethodManager?.ShowSoftInput(input, Android.Views.InputMethods.ShowFlags.Implicit);
        }

        private void SaveDataWithFilename(string fileName)
        {
            try
            {
                var exportData = new
                {
                    ExportTimestamp = DateTime.Now,
                    TotalBoxes = _boxDataStorage.Count,
                    TotalBirds = _boxDataStorage.Values.Sum(box => box.ScannedIds.Count),
                    Boxes = _boxDataStorage.Select(kvp => new
                    {
                        BoxNumber = kvp.Key,
                        Data = kvp.Value
                    }).OrderBy(b => b.BoxNumber).ToList()
                };

                var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });

                var downloadsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;

                if (string.IsNullOrEmpty(downloadsPath))
                {
                    Toast.MakeText(this, "Downloads directory not accessible", ToastLength.Long)?.Show();
                    return;
                }

                var filePath = System.IO.Path.Combine(downloadsPath, fileName);

                // Check if file already exists
                if (File.Exists(filePath))
                {
                    ShowConfirmationDialog(
                        "File Exists",
                        $"A file named '{fileName}' already exists. Do you want to overwrite it?",
                        ("Overwrite", () => SaveFileToPath(filePath, json, fileName)),
                        ("Cancel", () => ShowSaveFilenameDialog()) // Go back to filename dialog
                    );
                }
                else
                {
                    SaveFileToPath(filePath, json, fileName);
                }
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Export failed: {ex.Message}", ToastLength.Long)?.Show();
            }
        }

        private void SaveFileToPath(string filePath, string json, string fileName)
        {
            try
            {
                File.WriteAllText(filePath, json);

                var totalBoxes = _boxDataStorage.Count;
                var totalBirds = _boxDataStorage.Values.Sum(box => box.ScannedIds.Count);

                Toast.MakeText(this, $"💾 Data saved!\n📂 {fileName}\n📦 {totalBoxes} boxes, 🐧 {totalBirds} birds", ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"❌ Failed to save file: {ex.Message}", ToastLength.Long)?.Show();
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
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
                    Toast.MakeText(this, "❌ Storage permission denied. Cannot access Downloads folder.", ToastLength.Long)?.Show();
                }
            }
        }

        private bool CheckExternalStoragePermissions()
        {
            try
            {
                var sdkVersion = (int)Android.OS.Build.VERSION.SdkInt;
                System.Diagnostics.Debug.WriteLine($"Checking permissions for Android API {sdkVersion}");

                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R) // Android 11+ (API 30+)
                {
                    // Android 11+ - Check if we have MANAGE_EXTERNAL_STORAGE
                    var hasManageStorage = Android.OS.Environment.IsExternalStorageManager;
                    System.Diagnostics.Debug.WriteLine($"Android 11+: MANAGE_EXTERNAL_STORAGE = {hasManageStorage}");
                    return hasManageStorage;
                }
                else if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M) // Android 6+ (API 23+)
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
                InputType = Android.Text.InputTypes.ClassNumber,
                Text = _currentBox.ToString(),
                Hint = "Box number"
            };
            input.SetTextColor(UIFactory.TEXT_PRIMARY);

            var alertDialog = new AlertDialog.Builder(this)
                .SetTitle("Jump to Box")
                .SetMessage("Enter box number (1-150):")
                .SetView(input)
                .SetPositiveButton("Go", (s, e) =>
                {
                    if (int.TryParse(input.Text, out int targetBox))
                    {
                        if (targetBox >= 1 && targetBox <= 150)
                        {
                            JumpToBox(targetBox);
                        }
                        else
                        {
                            Toast.MakeText(this, "Box number must be between 1 and 150", ToastLength.Short)?.Show();
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
            input.SelectAll();

            var inputMethodManager = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
            inputMethodManager?.ShowSoftInput(input, Android.Views.InputMethods.ShowFlags.Implicit);
        }

        private void JumpToBox(int targetBox)
        {
            if (targetBox == _currentBox)
            {
                Toast.MakeText(this, $"Already at Box {_currentBox}", ToastLength.Short)?.Show();
                return;
            }

            _currentBox = targetBox;
            LoadBoxData();
            UpdateUI();

            Toast.MakeText(this, $"📦 Jumped to Box {_currentBox}", ToastLength.Short)?.Show();
        }

        private string? GetSelectedGateStatus()
        {
            if (_gateStatusSpinner?.SelectedItem != null)
            {
                var selected = _gateStatusSpinner.SelectedItem.ToString() ?? "";
                return string.IsNullOrEmpty(selected) ? null : selected;
            }
            return null;
        }

        private void SetSelectedGateStatus(string? gateStatus)
        {
            if (_gateStatusSpinner?.Adapter != null)
            {
                var adapter = _gateStatusSpinner.Adapter as ArrayAdapter<string>;
                if (adapter != null)
                {
                    var displayValue = gateStatus ?? "";
                    var position = adapter.GetPosition(displayValue);
                    if (position >= 0)
                        _gateStatusSpinner.SetSelection(position);
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

            // Check if bird already exists in current box
            if (!_boxDataStorage.ContainsKey(_currentBox))
                _boxDataStorage[_currentBox] = new BoxData();

            var boxData = _boxDataStorage[_currentBox];

            if (boxData.ScannedIds.Any(s => s.BirdId == cleanInput))
            {
                Toast.MakeText(this, $"Bird {cleanInput} already scanned in this box", ToastLength.Short)?.Show();
                _manualScanEditText.Text = "";
                return;
            }

            // Add the scan record
            var scanRecord = new ScanRecord
            {
                BirdId = cleanInput,
                Timestamp = DateTime.Now,
                Latitude = _currentLocation?.Latitude ?? 0,
                Longitude = _currentLocation?.Longitude ?? 0,
                Accuracy = _currentLocation?.Accuracy ?? -1
            };

            boxData.ScannedIds.Add(scanRecord);

            // Check if this penguin should auto-increment Adults count
            if (_remotePenguinData.TryGetValue(cleanInput, out var penguinData))
            {
                if (penguinData.LastKnownLifeStage == LifeStage.Adult || 
                    penguinData.LastKnownLifeStage == LifeStage.Returnee)
                {
                    boxData.Adults++;
                    
                    // Update the Adults field in the UI
                    if (_adultsEditText != null)
                    {
                        _adultsEditText.Text = boxData.Adults.ToString();
                    }
                }
                else if (penguinData.LastKnownLifeStage == LifeStage.Chick)
                {
                    // Trigger chick alert for manual entries as well
                    TriggerChickAlert();
                }
            }

            SaveDataToInternalStorage();

            // Clear input and update display
            _manualScanEditText.Text = "";
            UpdateScannedIdsDisplay(boxData.ScannedIds);

            // Enhanced toast message with life stage info
            string toastMessage = $"🐧 Bird {cleanInput} manually added to Box {_currentBox}";
            if (_remotePenguinData.TryGetValue(cleanInput, out var penguin))
            {
                if (penguin.LastKnownLifeStage == LifeStage.Adult || 
                    penguin.LastKnownLifeStage == LifeStage.Returnee)
                {
                    toastMessage += $" (+1 Adult)";
                }
                else if (penguin.LastKnownLifeStage == LifeStage.Chick)
                {
                    toastMessage += $" (Chick)";
                }
            }
            
            Toast.MakeText(this, toastMessage, ToastLength.Short)?.Show();
        }

        private void OnDataClick(object? sender, EventArgs e)
        {
            ShowDataOptionsDialog();
        }

        private void ShowDataOptionsDialog()
        {
            var options = new string[] 
            {
                "📊 Summary - View data overview",
                "💾 Save Data - Export to file", 
                "📂 Load Data - Import from file"
            };

            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Data Options");
            
            builder.SetItems(options, (sender, args) =>
            {
                switch (args.Which)
                {
                    case 0: // Summary
                        ShowBoxDataSummary();
                        break;
                    case 1: // Save Data
                        OnSaveDataClick(null, EventArgs.Empty);
                        break;
                    case 2: // Load Data
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