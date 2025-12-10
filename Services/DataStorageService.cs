using Android.App.SdkSandbox;
using Android.Content;
using Android.OS;
using PenguinMonitor.Models;
using Newtonsoft.Json;
using SmtpAuthenticator;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PenguinMonitor.Services
{
    public class DataStorageService
    {
        private const string APP_SETTINGS_FILENAME = "app_settings.json";
        private const string ALL_MONITOR_DATA_FILENAME = "penguin_data_autosave.json";
        internal const string REMOTE_BIRD_DATA_FILENAME = "remotePenguinData.json";
        internal const string REMOTE_BOX_DATA_FILENAME = "remoteBoxData.json";
        internal const string BREEDING_DATES_FILENAME = "predictedDates.json";

        private CsvDataService _csvDataService = new CsvDataService();

        // HTTP client for CSV downloads
        private static readonly HttpClient _httpClient = new HttpClient();
        internal const string ALL_PENGS_URL = "https://docs.google.com/spreadsheets/d/1A2j56iz0_VNHiWNJORAzGDqTbZsEd76j-YI_gQZsDEE";

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
                    //Toast.MakeText(this, "Response from Penguin server: " + response, ToastLength.Long)?.Show();
                };
                bw.RunWorkerAsync();
            }
            catch { }
        }
        public static Dictionary<int, MonitorDetails> requestPastMonitorDetailsFromServer(Dictionary<int, MonitorDetails> _allMonitorData)
        {
            try
            {
                MonitorDetails temp = _allMonitorData[0];
                _allMonitorData.Clear();
                _allMonitorData.Add(0, temp);

                string response = "No Response";
                response = Backend.RequestServerResponse("PenguinRequest-Saved:");
                
                foreach (string json in response.Split("~~~~", StringSplitOptions.RemoveEmptyEntries))
                {   
                    MonitorDetails monitor = Newtonsoft.Json.JsonConvert.DeserializeObject<MonitorDetails>(json);
                            
                    /// Don't import deleted monitors
                    if(monitor.IsDeleted)
                        continue;

                    /// Fix any old data with bad timestamps

                    //bool adjusted = false;
                    //DateTime lastSaved = monitor.LastSaved.ToUniversalTime();
                    //List<BoxData> bds = monitor.BoxData.Values.ToList();
                    //bds.Reverse();

                    //DateTime highest = DateTime.MinValue;
                    //DateTime lowest = DateTime.MaxValue;
                    //foreach (BoxData box in bds)
                    //{
                    //    DateTime boxHighest = DateTime.MinValue;

                    //    if (box.whenDataCollectedUtc.ToUniversalTime() > boxHighest)
                    //        boxHighest = box.whenDataCollectedUtc.ToUniversalTime();
                    //    if (box.whenDataCollectedUtc.ToUniversalTime() > highest)
                    //        highest = box.whenDataCollectedUtc.ToUniversalTime();
                    //    if (box.whenDataCollectedUtc.ToUniversalTime() < lowest)
                    //        lowest = box.whenDataCollectedUtc.ToUniversalTime();

                    //    for (int j = 0; j < box.ScannedIds.Count; j++)
                    //    {
                    //        if (box.whenDataCollectedUtc < box.ScannedIds[j].Timestamp.ToUniversalTime())
                    //        {

                    //            if (box.ScannedIds[j].Timestamp.ToUniversalTime() > boxHighest)
                    //                boxHighest = box.ScannedIds[j].Timestamp.ToUniversalTime();
                    //            if (box.ScannedIds[j].Timestamp.ToUniversalTime() > highest)
                    //                highest = box.ScannedIds[j].Timestamp.ToUniversalTime();
                    //            if (box.ScannedIds[j].Timestamp.ToUniversalTime() < lowest)
                    //                lowest = box.ScannedIds[j].Timestamp.ToUniversalTime();
                    //        }
                    //    }
                    //    if (box.whenDataCollectedUtc < boxHighest)
                    //    {
                    //        box.whenDataCollectedUtc = boxHighest;
                    //        adjusted = true;
                    //    }
                    //    else if (box.whenDataCollectedUtc < lowest)
                    //    {  box.whenDataCollectedUtc = lowest;
                    //        adjusted = true;
                    //    }
                    //}

                    //foreach (BoxData box in bds)
                    //{
                    //    if(box.whenDataCollectedUtc.Year < 2020)
                    //    {  
                    //        box.whenDataCollectedUtc = highest;
                    //        adjusted = true;
                    //    }
                    //}
                    //if (monitor.LastSaved.Year < 2020)
                    //{
                    //    monitor.LastSaved = highest.ToUniversalTime();
                    //    adjusted = true;
                    //}
                    //monitor.filename += " GenTime";
                    //if (adjusted)
                    //{
                    //    var currentDataJson = JsonConvert.SerializeObject(monitor, Formatting.Indented);
                    //    string response = Backend.RequestServerResponse("PenguinReport-Saved:" + currentDataJson.ToString());
                    //}
                    _allMonitorData.Add(_allMonitorData.Count, monitor);
                }
            }
            catch { }
            return _allMonitorData;
        }
        public async static Task SaveAllMonitorDataToDisk(Android.Content.Context context, Dictionary<int, MonitorDetails> _allMonitorData, bool reportHome = true, bool downloadRemoteMonitorData = false)
        {
            try
            {
                if (downloadRemoteMonitorData)
                    _allMonitorData = requestPastMonitorDetailsFromServer(_allMonitorData);

                if (string.IsNullOrEmpty(context.FilesDir?.AbsolutePath))
                    return;
                var allMonitorDataJson = JsonConvert.SerializeObject(_allMonitorData, Formatting.Indented);
                var filePath = Path.Combine(context.FilesDir?.AbsolutePath, ALL_MONITOR_DATA_FILENAME);
                File.WriteAllText(filePath, allMonitorDataJson);
                if (reportHome && _allMonitorData[0].BoxData.Count > 0) {
                    try
                    {
                        var currentDataJson = JsonConvert.SerializeObject(_allMonitorData[0], Formatting.Indented);
                        string response = "No Response";
                        BackgroundWorker bw = new BackgroundWorker();
                        bw.DoWork += (sender, e) => 
                            response = Backend.RequestServerResponse("PenguinReport:" + currentDataJson.ToString()); 
                        bw.RunWorkerCompleted += (sender, e) =>
                        {
                            new Handler(Looper.MainLooper).Post(() =>
                            {
                                if (response == "fail")
                                {
                                    Toast.MakeText(context, "Unable to incremental on server.", ToastLength.Short)?.Show();
                                }
                                else
                                {
                                    Toast.MakeText(context, "Boxes " + response + " on server.", ToastLength.Short)?.Show();
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
        public Dictionary<int, MonitorDetails>? LoadAllMonitorDataFromDisk(Android.Content.Context? context)
        {
            try
            {
                var filePath = Path.Combine(context.FilesDir?.AbsolutePath, ALL_MONITOR_DATA_FILENAME);
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
        internal async Task DownloadRemoteData(Android.Content.Context? context, Dictionary<int, MonitorDetails> allMonitorData)
        {
            try
            {
                Task<HttpResponseMessage> responseBirdsTask =
                     _httpClient.GetAsync(_csvDataService.ConvertToGoogleSheetsCsvUrl(ALL_PENGS_URL));
                Task saveMonitorDataToDiskTask = SaveAllMonitorDataToDisk(context, allMonitorData, reportHome:false, downloadRemoteMonitorData: true);

                // Await them in parallel
                await Task.WhenAll(responseBirdsTask, saveMonitorDataToDiskTask);
                // Retrieve results
                HttpResponseMessage responseBirds = await responseBirdsTask;

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
                                VidForScanner = row.VidForScanner ?? "",
                                ChipDate = DateTime.TryParse(row.ChipDate, out DateTime chipDateFound) ? chipDateFound : DateTime.MinValue
                            };
                            remotePenguinData[eightDigitId] = penguinData;
                        }
                    }
                }
                var birdJson = JsonConvert.SerializeObject(remotePenguinData, Formatting.Indented);
                File.WriteAllText(Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BIRD_DATA_FILENAME), birdJson);

                int boxDataCount = 0;
                foreach (MonitorDetails monitorDetails in allMonitorData.Values)
                    boxDataCount += monitorDetails.BoxData.Count;

                new Handler(Looper.MainLooper).Post(() =>
                {
                    Toast.MakeText(context, $"Got {boxDataCount} box monitor, {remotePenguinData.Count} remote bird infos", ToastLength.Long)?.Show();
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
        public async Task<Dictionary<string, PenguinData>?> loadRemotePengInfoFromAppDataDir(Android.Content.Context? context)
        {
            try
            {
                string remoteBirdPath = Path.Combine(context.FilesDir?.AbsolutePath, REMOTE_BIRD_DATA_FILENAME);
                //if (!File.Exists(remoteBirdPath))
                //{
                //    await DownloadRemoteData(context, allMonitorData);
                //}
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
                //if (!File.Exists(remoteBoxDataPath))
                //{
                //    await DownloadRemoteData(context, allMonitorData);
                //}
                var remoteBoxDataJson = File.ReadAllText(remoteBoxDataPath);
                return JsonConvert.DeserializeObject<Dictionary<int, BoxRemoteData>>(remoteBoxDataJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote box data: {ex.Message}");
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
        public void ClearInternalStorageData(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var filePath = Path.Combine(filesDir, ALL_MONITOR_DATA_FILENAME);
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
        internal static List<BoxData> getOlderBoxDatas(Dictionary<int, MonitorDetails> allMonitorData, int currentlyVisibleMonitor, string boxName)
        {
            List<BoxData> olderBoxDatas = new List<BoxData>();
            for (int i = currentlyVisibleMonitor + 1; i < allMonitorData.Count; i++)
                if (allMonitorData[i].BoxData.ContainsKey(boxName))
                    olderBoxDatas.Add(allMonitorData[i].BoxData[boxName]);
            string? lastBreedingStatus = null;
            for (int i = olderBoxDatas.Count - 1; i >= 0; i--)
            {
                if (olderBoxDatas.ElementAt(i).BreedingChance == null)
                    olderBoxDatas.ElementAt(i).BreedingChance = lastBreedingStatus;
                else
                    lastBreedingStatus = olderBoxDatas.ElementAt(i).BreedingChance;
            }
            return olderBoxDatas;
        }
        internal static string getStickyNotes(List<BoxData> olderBoxes)
        {
            HashSet<string> removedStickies = new HashSet<string>();
            HashSet<string> addedStickies = new HashSet<string>();
            foreach (BoxData boxData in olderBoxes)
            {
                foreach (string part in boxData.Notes.Split(" ", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith("l-") && part.Length > 2)
                    {
                        string sticky = part.Substring(2);
                        removedStickies.Add(sticky);
                        addedStickies.Remove(sticky);
                    }
                    else if (part.StartsWith("l=") && part.Length > 2)
                    {
                        string sticky = part.Substring(2);
                        if (!removedStickies.Contains(sticky))
                            addedStickies.Add(sticky);
                    }
                    else if (part.StartsWith("l="))
                    {
                        return string.Join(" ", addedStickies);
                    }
                }
            }
            return string.Join(" ", addedStickies);
        }
        internal static string GetBoxBreedingStatusString(string boxName, BoxData? thisBoxData, List<BoxData> olderBoxDatas)
        {
            if(olderBoxDatas.Count == 0)
                return "";
            int skip = 0;
            if (thisBoxData == null) // iterate only using olderboxdatas
            {
                if (olderBoxDatas.Count == 1)
                    return "";
                thisBoxData = olderBoxDatas[0];
                skip = 1;
            }
            if (boxName == "49")
                ;
            if (thisBoxData == null || thisBoxData.Eggs + thisBoxData.Chicks == 0 || olderBoxDatas == null || olderBoxDatas.Count==0)
                return "";
            string breedingStatusString = "";
            DateTime whenOffspringFound = thisBoxData.whenDataCollectedUtc.ToLocalTime().Date;
            DateTime whenOffspringNotFound = DateTime.MinValue;
            foreach (BoxData olderBoxData in olderBoxDatas.Skip(skip))
            {
                if (olderBoxData.Eggs + olderBoxData.Chicks == 0)
                {
                    if (thisBoxData.Eggs > 1) //in case of multiple eggs, assume first one was laid 2 days before found
                        whenOffspringFound = whenOffspringFound.AddDays(-2);
                    whenOffspringNotFound = olderBoxData.whenDataCollectedUtc.ToLocalTime().Date;
                    TimeSpan uncertainty = (whenOffspringFound - whenOffspringNotFound)/2;
                    DateTime probableLaidDate = whenOffspringNotFound.AddDays(Math.Ceiling(uncertainty.TotalDays));
                    int daysSinceLaid = (int)(DateTime.UtcNow.ToLocalTime().Date - probableLaidDate).TotalDays;
                    return breedingDateStatus(daysSinceLaid) + (uncertainty.TotalDays > 1 ? " ±" + (int)uncertainty.TotalDays : "");
                }
                whenOffspringFound = olderBoxData.whenDataCollectedUtc;
            }
            return "";
        }
        public static string breedingDateStatus(int daysSinceLaid)
        {
            DateTime estHatch = DateTime.Today.AddDays(38 - daysSinceLaid);
            if (estHatch.AddDays(3) >= DateTime.Today)
                return "Hatch" + getDateString(estHatch);

            DateTime estPG = DateTime.Today.AddDays(52 - daysSinceLaid);
            if (estPG.AddDays(3) >= DateTime.Today)
                return "PG" + getDateString(estPG);

            DateTime chipStart = DateTime.Today.AddDays(80 - daysSinceLaid);
            if (chipStart.AddDays(3) >= DateTime.Today)
                return "Chip" + getDateString(chipStart);

            DateTime estFledge = DateTime.Today.AddDays(87 - daysSinceLaid);
            if (estFledge.AddDays(3) >= DateTime.Today)
                return "Fledge" + getDateString(estFledge);
            return "Fail detecting laid date?";
        }
        private static string getDateString(DateTime expectedDate)
        {
            DateTime today = DateTime.Today;
            if (expectedDate.Date.Equals(today))
            {
                return " today";
            }
            if ((expectedDate.Date - today).TotalDays == 1 && expectedDate > today)
            {
                return " tomorrow";
            }
            if ((today - expectedDate.Date).TotalDays == 1)
            {
                return " yesterday";
            }
            if (expectedDate > today)
            {
                return " " + Math.Ceiling((expectedDate - today).TotalDays) + " days";
            }
            return " " + Math.Ceiling((today - expectedDate).TotalDays) + " days ago";
        }
    }
}