using Android.Content;
using Newtonsoft.Json;
using PenguinMonitor.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PenguinMonitor.Services
{
    public class BoxTagService
    {
        private const string BOX_TAGS_FILENAME = "box_tags.json";
        private static BoxTagApiService? _apiService;

        /// <summary>
        /// Load box tags from JSON file
        /// </summary>
        public static Dictionary<string, BoxTag> LoadBoxTags(string filesDir)
        {
            string boxTagsPath = Path.Combine(filesDir, BOX_TAGS_FILENAME);
            try
            {
                if (File.Exists(boxTagsPath))
                {
                    var json = File.ReadAllText(boxTagsPath);
                    return JsonConvert.DeserializeObject<Dictionary<string, BoxTag>>(json)
                           ?? new Dictionary<string, BoxTag>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load box tags: {ex.Message}");
            }
            return new Dictionary<string, BoxTag>();
        }

        /// <summary>
        /// Save box tags to JSON file
        /// </summary>
        public static void SaveBoxTags(Dictionary<string, BoxTag> boxTags, string filesDir)
        {
            try
            {
                string saveTo = Path.Combine(filesDir, BOX_TAGS_FILENAME);
                string tempFile = saveTo + ".tmp";
                var json = JsonConvert.SerializeObject(boxTags, Formatting.Indented);

                File.Delete(tempFile);
                File.WriteAllText(tempFile, json);

                // Verify the file can be deserialized
                var test = JsonConvert.DeserializeObject<Dictionary<string, BoxTag>>(File.ReadAllText(tempFile));
                if (test != null)
                {
                    File.Move(tempFile, saveTo, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save box tags: {ex.Message}");
            }
        }

        /// <summary>
        /// Add or update a box tag
        /// </summary>
        public static void AssignBoxTag(Dictionary<string, BoxTag> boxTags, string boxId, string tagNumber,
            double latitude, double longitude, float accuracy, string filesDir)
        {
            boxTags[boxId] = new BoxTag
            {
                BoxID = boxId,
                TagNumber = tagNumber,
                ScanTimeUTC = DateTime.UtcNow,
                Latitude = latitude,
                Longitude = longitude,
                Accuracy = accuracy
            };
            SaveBoxTags(boxTags, filesDir);
        }

        /// <summary>
        /// Remove a box tag
        /// </summary>
        public static void RemoveBoxTag(Dictionary<string, BoxTag> boxTags, string boxId, string filesDir)
        {
            if (boxTags.ContainsKey(boxId))
            {
                boxTags.Remove(boxId);
                SaveBoxTags(boxTags, filesDir);
            }
        }

        /// <summary>
        /// Get box ID by tag number
        /// </summary>
        public static string? GetBoxIdByTag(Dictionary<string, BoxTag> boxTags, string tagNumber)
        {
            foreach (var kvp in boxTags)
            {
                if (kvp.Value.TagNumber == tagNumber)
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// Check if a scanned ID is a box tag (starts with LA9000250)
        /// </summary>
        public static bool IsBoxTag(string scannedId)
        {
            // Clean the ID first
            var cleanId = new String(scannedId.Where(char.IsLetterOrDigit).ToArray());
            return cleanId.Length >= 9 && cleanId.Substring(0, 9).Equals("LA9000250", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if a scanned ID is a penguin tag (starts with LA9560000)
        /// </summary>
        public static bool IsPenguinTag(string scannedId)
        {
            // Clean the ID first
            var cleanId = new String(scannedId.Where(char.IsLetterOrDigit).ToArray());
            return cleanId.Length >= 9 && cleanId.Substring(0, 9).Equals("LA9560000", StringComparison.OrdinalIgnoreCase);
        }

        #region Remote API Methods

        /// <summary>
        /// Initialize the API service with credentials
        /// </summary>
        public static void InitializeApi(string apiUrl, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiUrl) && !string.IsNullOrWhiteSpace(apiKey))
            {
                _apiService = new BoxTagApiService(apiUrl, apiKey);
                System.Diagnostics.Debug.WriteLine($"BoxTagService API initialized: {apiUrl}");
            }
        }

        /// <summary>
        /// Check if API is configured and available
        /// </summary>
        public static bool IsApiConfigured => _apiService != null;

        /// <summary>
        /// Sync box tags with remote API
        /// Downloads remote tags and merges with local (remote wins for conflicts)
        /// </summary>
        public static async Task<Dictionary<string, BoxTag>> SyncWithApiAsync(
            Dictionary<string, BoxTag> localTags,
            string filesDir)
        {
            if (_apiService == null)
            {
                System.Diagnostics.Debug.WriteLine("BoxTagService.SyncWithApiAsync: API not configured");
                return localTags;
            }

            try
            {
                // Check if API is reachable
                if (!await _apiService.IsApiAvailableAsync())
                {
                    System.Diagnostics.Debug.WriteLine("BoxTagService.SyncWithApiAsync: API not available");
                    return localTags;
                }

                // Get remote tags
                var remoteTags = await _apiService.GetAllBoxTagsAsync();
                System.Diagnostics.Debug.WriteLine($"BoxTagService.SyncWithApiAsync: Got {remoteTags.Count} remote tags");

                // Upload any local tags that don't exist remotely or are newer
                foreach (var kvp in localTags)
                {
                    if (!remoteTags.ContainsKey(kvp.Key) ||
                        kvp.Value.ScanTimeUTC > remoteTags[kvp.Key].ScanTimeUTC)
                    {
                        try
                        {
                            await _apiService.SaveBoxTagAsync(kvp.Value);
                            remoteTags[kvp.Key] = kvp.Value;
                            System.Diagnostics.Debug.WriteLine($"BoxTagService.SyncWithApiAsync: Uploaded {kvp.Key}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"BoxTagService.SyncWithApiAsync: Failed to upload {kvp.Key}: {ex.Message}");
                        }
                    }
                }

                // Save merged result locally
                SaveBoxTags(remoteTags, filesDir);

                System.Diagnostics.Debug.WriteLine($"BoxTagService.SyncWithApiAsync: Sync complete, {remoteTags.Count} total tags");
                return remoteTags;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BoxTagService.SyncWithApiAsync failed: {ex.Message}");
                return localTags;
            }
        }

        /// <summary>
        /// Assign a box tag and save to both local and remote
        /// </summary>
        public static async Task AssignBoxTagAsync(
            Dictionary<string, BoxTag> boxTags,
            string boxId,
            string tagNumber,
            double latitude,
            double longitude,
            float accuracy,
            string filesDir)
        {
            var boxTag = new BoxTag
            {
                BoxID = boxId,
                TagNumber = tagNumber,
                ScanTimeUTC = DateTime.UtcNow,
                Latitude = latitude,
                Longitude = longitude,
                Accuracy = accuracy
            };

            boxTags[boxId] = boxTag;

            // Save locally first
            SaveBoxTags(boxTags, filesDir);

            // Then try to save remotely
            if (_apiService != null)
            {
                try
                {
                    await _apiService.SaveBoxTagAsync(boxTag);
                    System.Diagnostics.Debug.WriteLine($"BoxTagService.AssignBoxTagAsync: Saved {boxId} to remote");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"BoxTagService.AssignBoxTagAsync: Remote save failed: {ex.Message}");
                    // Local save already done, will sync later
                }
            }
        }

        /// <summary>
        /// Remove a box tag from both local and remote
        /// </summary>
        public static async Task RemoveBoxTagAsync(
            Dictionary<string, BoxTag> boxTags,
            string boxId,
            string filesDir)
        {
            if (boxTags.ContainsKey(boxId))
            {
                boxTags.Remove(boxId);

                // Save locally first
                SaveBoxTags(boxTags, filesDir);

                // Then try to delete remotely
                if (_apiService != null)
                {
                    try
                    {
                        await _apiService.DeleteBoxTagAsync(boxId);
                        System.Diagnostics.Debug.WriteLine($"BoxTagService.RemoveBoxTagAsync: Deleted {boxId} from remote");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"BoxTagService.RemoveBoxTagAsync: Remote delete failed: {ex.Message}");
                    }
                }
            }
        }

        #endregion
    }
}
