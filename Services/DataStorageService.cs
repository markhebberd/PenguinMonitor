using Android.OS;
using BluePenguinMonitoring.Models;
using Newtonsoft.Json;
using SmtpAuthenticator;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluePenguinMonitoring.Services
{
    public class DataStorageService
    {
        private const string AUTO_SAVE_FILENAME = "penguin_data_autosave.json";
        internal const string REMOTE_BIRD_DATA_FILENAME = "remotePenguinData.json";
        internal const string REMOTE_BOX_DATA_FILENAME = "remoteBoxData.json";

        private CsvDataService _csvDataService = new CsvDataService();

        // HTTP client for CSV downloads
        private static readonly HttpClient _httpClient = new HttpClient();
        internal const string ALL_PENGS_URL = "https://docs.google.com/spreadsheets/d/1A2j56iz0_VNHiWNJORAzGDqTbZsEd76j-YI_gQZsDEE";
        internal const string BOX_STATUS_URL = "https://docs.google.com/spreadsheets/d/1B-jWzxb4PhbMerWD36jO3TysTCbNsZ9Lo0gldgYreLc";

        public void SaveDataToInternalStorage(string filesDir, AppDataState appState, Android.Content.Context context, bool reportHome = true)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var json = JsonConvert.SerializeObject(appState, Formatting.Indented);
                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);

                File.WriteAllText(filePath, json);

                if (reportHome)
                {
                    try
                    {
                        string response = "No Response";
                        BackgroundWorker bw = new BackgroundWorker();
                        bw.DoWork += (sender, e) =>
                        {
                            response = Backend.RequestServerResponse("PenguinReport:" + json.ToString());
                        };
                        bw.RunWorkerCompleted += (sender, e) =>
                        {
                            new Handler(Looper.MainLooper).Post(() =>
                            {
                                if (response == "fail")
                                {
                                    Toast.MakeText(context, "Unable to backup on Marks server.", ToastLength.Short)?.Show();
                                }
                                else
                                {
                                    Toast.MakeText(context, "Data was " + response + " on Marks server.", ToastLength.Short)?.Show();
                                }
                            });
                        };
                        bw.RunWorkerAsync();
                    }
                    catch { }
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-save failed: {ex.Message}");
            }
        }
        public AppDataState? LoadFromAppDataDir(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return null;

                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<AppDataState>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load data: {ex.Message}");
                return null;
            }
        }
        internal async Task DownloadCsvDataAsync(Android.Content.Context? context)
        {
            try
            {
                var csvUrlBirds = _csvDataService.ConvertToGoogleSheetsCsvUrl(ALL_PENGS_URL);
                var responseBirds = await _httpClient.GetAsync(csvUrlBirds);
                responseBirds.EnsureSuccessStatusCode();

                var csvContentBirds = await responseBirds.Content.ReadAsStringAsync();
                var parsedDataBirds = _csvDataService.ParseBirdCsvData(csvContentBirds);

                Dictionary<string, PenguinData> _remotePenguinData = new Dictionary<string, PenguinData>();
                foreach (var row in parsedDataBirds)
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
                var internalPath = context.FilesDir?.AbsolutePath;

                var birdJson = JsonConvert.SerializeObject(_remotePenguinData, Formatting.Indented);
                File.WriteAllText(Path.Combine(internalPath, REMOTE_BIRD_DATA_FILENAME), birdJson);

                var csvUrl = _csvDataService.ConvertToGoogleSheetsCsvUrl(BOX_STATUS_URL);
                var response = await _httpClient.GetAsync(csvUrl);
                response.EnsureSuccessStatusCode();

                var csvContent = await response.Content.ReadAsStringAsync();
                List<BoxStatusRemoteData> parsedData = _csvDataService.ParseBoxCsvData(csvContent);

                Dictionary<int, BoxStatusRemoteData> _remoteBoxData = new Dictionary<int, BoxStatusRemoteData>();
                foreach (var row in parsedData)
                {
                    if ( row.boxNumber != null)
                    {
                        _remoteBoxData.Add(row.boxNumber, row);
                    }
                }

                var boxJson = JsonConvert.SerializeObject(_remoteBoxData, Formatting.Indented);
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BOX_DATA_FILENAME), boxJson);

                new Handler(Looper.MainLooper).Post(() =>
                {
                    Toast.MakeText(context, $"✅ Got {_remotePenguinData.Count} penguin, {_remoteBoxData.Count} box records", ToastLength.Short)?.Show();
                });
            }
            catch (Exception ex)
            {
                new Handler(Looper.MainLooper).Post(() =>
                  {
                      Toast.MakeText(context, $"❌ Download failed: {ex.Message}", ToastLength.Long)?.Show();
                  });
            }
        }
        public async Task<Dictionary<string, PenguinData>?> loadRemotePengInfoFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string remoteBirdPath = Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BIRD_DATA_FILENAME);
                if (!File.Exists(remoteBirdPath))
                {
                    await DownloadCsvDataAsync(context);
                }
                var remoteBirdJson = File.ReadAllText(remoteBirdPath);
                return JsonConvert.DeserializeObject<Dictionary<string, PenguinData>>(remoteBirdJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote penguin data: {ex.Message}");
                return null;
            }
        }
        public async Task<Dictionary<int, BoxStatusRemoteData>?> loadRemoteBoxInfoFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string remoteBoxDataPath = Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BOX_DATA_FILENAME);
                if (!File.Exists(remoteBoxDataPath))
                {
                    await DownloadCsvDataAsync(context);
                }
                var remoteBoxDataJson = File.ReadAllText(remoteBoxDataPath);
                return JsonConvert.DeserializeObject<Dictionary<int, BoxStatusRemoteData>>(remoteBoxDataJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote box data: {ex.Message}");
                return null;
            }
        }
        public void ClearInternalStorageData(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear auto-save file: {ex.Message}");
            }
        }
    }
}