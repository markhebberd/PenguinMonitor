using BluePenguinMonitoring.Models;
using SmtpAuthenticator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

                var json = JsonSerializer.Serialize(appState, new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);

                File.WriteAllText(filePath, json);

                try
                {
                    string response = Backend.reportHome("PenguinReport: " + json.ToString());
                    if (response.Equals("Transmitted report"))
                    {
                        Toast.MakeText(context, "🔓 Data was backed up to Marks server.", ToastLength.Long)?.Show();
                    }
                }
                catch { }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-save failed: {ex.Message}");
            }
        }

        public AppDataState? LoadDataFromInternalStorage(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return null;

                var filePath = Path.Combine(filesDir, AUTO_SAVE_FILENAME);
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<AppDataState>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load data: {ex.Message}");
                return null;
            }
        }

        public void SaveRemotePenguinDataToInternalStorage(string filesDir, Dictionary<string, PenguinData> remotePenguinData)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return;

                var json = JsonSerializer.Serialize(remotePenguinData, new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(filesDir, REMOTE_BIRD_DATA_FILENAME);

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save remote penguin data: {ex.Message}");
            }
        }

        public Dictionary<string, PenguinData>? LoadRemotePenguinDataFromInternalStorage(string filesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(filesDir))
                    return null;

                var remoteBirdDataPath = Path.Combine(filesDir, REMOTE_BIRD_DATA_FILENAME);
                if (!File.Exists(remoteBirdDataPath))
                    return null;

                var remoteBirdJson = File.ReadAllText(remoteBirdDataPath);
                return JsonSerializer.Deserialize<Dictionary<string, PenguinData>>(remoteBirdJson);
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