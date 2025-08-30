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
        private const string REMOTE_BIRD_DATA_FILENAME = "remotePenguinData.json";

        public void SaveDataToInternalStorage(string filesDir, AppDataState appState, Android.Content.Context context)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var json = JsonConvert.SerializeObject(appState, Formatting.Indented);
                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);

                File.WriteAllText(filePath, json);

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

        public void cacheRemotePengInfoToAppDataDir(string filesDir, Dictionary<string, PenguinData> remotePenguinData)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var json = JsonConvert.SerializeObject(remotePenguinData, Formatting.Indented);
                var filePath = Path.Combine(filesDir, REMOTE_BIRD_DATA_FILENAME);

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save remote penguin data: {ex.Message}");
            }
        }

        public Dictionary<string, PenguinData>? loadRemotePengInfoFromAppDataDir(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return null;

                var remoteBirdDataPath = Path.Combine(filesDir, REMOTE_BIRD_DATA_FILENAME);
                if (!File.Exists(remoteBirdDataPath))
                    return null;

                var remoteBirdJson = File.ReadAllText(remoteBirdDataPath);
                return JsonConvert.DeserializeObject<Dictionary<string, PenguinData>>(remoteBirdJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load remote penguin data: {ex.Message}");
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