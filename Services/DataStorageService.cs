using Android.App.SdkSandbox;
using Android.Content;
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
        private const string APP_SETTINGS_FILENAME = "app_settings.json";
        private const string AUTO_SAVE_FILENAME = "penguin_data_autosave.json";
        internal const string REMOTE_BIRD_DATA_FILENAME = "remotePenguinData.json";
        internal const string REMOTE_BOX_DATA_FILENAME = "remoteBoxData.json";
        internal const string BREEDING_DATES_FILENAME = "predictedDates.json";

        private CsvDataService _csvDataService = new CsvDataService();

        // HTTP client for CSV downloads
        private static readonly HttpClient _httpClient = new HttpClient();
        internal const string ALL_PENGS_URL = "https://docs.google.com/spreadsheets/d/1A2j56iz0_VNHiWNJORAzGDqTbZsEd76j-YI_gQZsDEE";
        internal const string BOX_STATUS_URL = "https://docs.google.com/spreadsheets/d/1B-jWzxb4PhbMerWD36jO3TysTCbNsZ9Lo0gldgYreLc";
        internal const string BREEDING_DATES_URL = "https://docs.google.com/spreadsheets/d/1OZMPnmEm2YAGx8M9Ha_qKoB3KJgSQ4qw";


        public void uploadCurrentMonitorDetailsToServer(string currentDataJson)
        {
            try
            {
                string response = "No Response";
                BackgroundWorker bw = new BackgroundWorker();
                bw.DoWork += (sender, e) =>
                {
                    response = Backend.RequestServerResponse("PenguinReport-Saved:" + currentDataJson.ToString());
                };
                bw.RunWorkerCompleted += (sender, e) =>
                {
                    
                };
                bw.RunWorkerAsync();
            }
            catch { }
        }

        public Dictionary<int, MonitorDetails> requestPastMonitorDetailsFromServer(Dictionary<int, MonitorDetails> _allMonitorData)
        {
            try
            {
                MonitorDetails temp = _allMonitorData[0];
                _allMonitorData.Clear();
                _allMonitorData.Add(0, temp);

                string response = "No Response";
                BackgroundWorker bw = new BackgroundWorker();
                bw.DoWork += (sender, e) =>
                {
                    response = Backend.RequestServerResponse("PenguinRequest-Saved" );
                };
                bw.RunWorkerCompleted += (sender, e) =>
                {
                    try
                    {
                        foreach (string json in response.Split("~~~~", StringSplitOptions.RemoveEmptyEntries))
                        {
                            MonitorDetails monitor = Newtonsoft.Json.JsonConvert.DeserializeObject<MonitorDetails>(json);
                            _allMonitorData.Add(_allMonitorData.Count, monitor);
                        }
                    }
                    catch { }
                };
                bw.RunWorkerAsync();
            }
            catch { }
            return _allMonitorData;
        }
        public void SaveDataToInternalStorage(string filesDir, Dictionary<int, MonitorDetails> _allMonitorData, Android.Content.Context context, bool reportHome = true)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var allMonitorDataJson = JsonConvert.SerializeObject(_allMonitorData, Formatting.Indented);
                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);

                File.WriteAllText(filePath, allMonitorDataJson);

                if (reportHome && _allMonitorData[0].BoxData.Count > 0 )
                {
                    try
                    {
                        var currentDataJson = JsonConvert.SerializeObject(_allMonitorData[0], Formatting.Indented);

                        string response = "No Response";
                        BackgroundWorker bw = new BackgroundWorker();
                        bw.DoWork += (sender, e) =>
                        {
                            response = Backend.RequestServerResponse("PenguinReport:" + currentDataJson.ToString());
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
        public Dictionary<int, MonitorDetails>? LoadFromAppDataDir(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return null;

                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<int, MonitorDetails>>(json);
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
                Task<HttpResponseMessage> responseBirdsTask = 
                     _httpClient.GetAsync(_csvDataService.ConvertToGoogleSheetsCsvUrl(ALL_PENGS_URL));
                Task<HttpResponseMessage> responseBoxesTask =
                    _httpClient.GetAsync(_csvDataService.ConvertToGoogleSheetsCsvUrl(BOX_STATUS_URL));
                Task<HttpResponseMessage> responseBreedingDatesTask =
                    _httpClient.GetAsync(_csvDataService.ConvertToGoogleSheetsCsvUrl(BREEDING_DATES_URL)); 
                ;
                // Await them in parallel
                await Task.WhenAll(responseBirdsTask, responseBoxesTask, responseBreedingDatesTask);
                // Retrieve results
                HttpResponseMessage responseBirds = await responseBirdsTask;
                HttpResponseMessage responseBoxes = await responseBoxesTask;
                HttpResponseMessage responseBreedingDates = await responseBreedingDatesTask;

                responseBirds.EnsureSuccessStatusCode();
                var csvContentBirds = await responseBirds.Content.ReadAsStringAsync();
                var parsedDataBirds = _csvDataService.ParseBirdCsvData(csvContentBirds);

                Dictionary<string, PenguinData> remotePenguinData = new Dictionary<string, PenguinData>();
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
                                else
                                {
                                    throw new Exception("Unknown life stage: " + row.LastKnownLifeStage);
                                }
                            }
                            var penguinData = new PenguinData
                            {
                                ScannedId = eightDigitId,
                                LastKnownLifeStage = lifeStage,
                                Sex = row.Sex ?? "",
                                VidForScanner = row.VidForScanner ?? ""
                            };
                            remotePenguinData[eightDigitId] = penguinData;
                        }
                    }
                }
                var birdJson = JsonConvert.SerializeObject(remotePenguinData, Formatting.Indented);
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BIRD_DATA_FILENAME), birdJson);

                responseBoxes.EnsureSuccessStatusCode();
                var csvContent = await responseBoxes.Content.ReadAsStringAsync();
                List<BoxRemoteData> parsedData = _csvDataService.ParseBoxCsvData(csvContent);

                Dictionary<int, BoxRemoteData> remoteBoxData = new Dictionary<int, BoxRemoteData>();
                foreach (var row in parsedData)
                {
                    if ( row.boxNumber != null)
                    {
                        remoteBoxData.Add(row.boxNumber, row);
                    }
                }
                var boxJson = JsonConvert.SerializeObject(remoteBoxData, Formatting.Indented);
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BOX_DATA_FILENAME), boxJson);


                responseBreedingDates.EnsureSuccessStatusCode();
                var csvBreedingContent = await responseBreedingDates.Content.ReadAsStringAsync();
                List<BoxPredictedDates> parsedBreedingData = _csvDataService.ParseBreedingDatesCsvData(csvBreedingContent);

                Dictionary<int, BoxPredictedDates> predictedDates = new Dictionary<int, BoxPredictedDates>();
                foreach (var row in parsedBreedingData)
                {
                    if (row.boxNumber != null)
                    {
                        predictedDates.Add(row.boxNumber, row);
                    }
                }
                var pdJson = JsonConvert.SerializeObject(predictedDates, Formatting.Indented);
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, BREEDING_DATES_FILENAME), pdJson);

                new Handler(Looper.MainLooper).Post(() =>
                {
                    Toast.MakeText(context, $"✅ Got {remotePenguinData.Count} bird, {remoteBoxData.Count} box, {predictedDates.Count} breeding records", ToastLength.Short)?.Show();
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
        public static void saveApplicationSettings(string filesDir, AppSettings appSettings)
        {
            var appSettingsJson = JsonConvert.SerializeObject(appSettings, Formatting.Indented);
            File.WriteAllText(Path.Combine(filesDir, APP_SETTINGS_FILENAME), appSettingsJson);
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
        public async Task<Dictionary<int, BoxRemoteData>?> loadRemoteBoxInfoFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string remoteBoxDataPath = Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BOX_DATA_FILENAME);
                if (!File.Exists(remoteBoxDataPath))
                {
                    await DownloadCsvDataAsync(context);
                }
                var remoteBoxDataJson = File.ReadAllText(remoteBoxDataPath);
                return JsonConvert.DeserializeObject<Dictionary<int, BoxRemoteData>>(remoteBoxDataJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote box data: {ex.Message}");
                return null;
            }
        }
        public async Task<Dictionary<int, BoxPredictedDates>?> loadBreedingDatesFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string breedingDatesPath = Path.Combine(context.FilesDir?.AbsolutePath, BREEDING_DATES_FILENAME);
                if (!File.Exists(breedingDatesPath))
                {
                    await DownloadCsvDataAsync(context);
                }
                var breedingDatesJson = File.ReadAllText(breedingDatesPath);
                return JsonConvert.DeserializeObject<Dictionary<int, BoxPredictedDates>>(breedingDatesJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load predicted breeding data: {ex.Message}");
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