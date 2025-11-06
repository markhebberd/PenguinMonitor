using Android.Content;
using Newtonsoft.Json;
using PenguinMonitor.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PenguinMonitor.Services
{
    public class BoxTagService
    {
        private const string BOX_TAGS_FILENAME = "box_tags.json";

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
    }
}
