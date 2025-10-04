using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BluePenguinMonitoring.Models;

namespace BluePenguinMonitoring.Services
{
    public class CsvDataService
    {
        internal List<BoxPredictedDates> ParseBreedingDatesCsvData(string csvBreedingContent)
        {
            var result = new List<BoxPredictedDates>();
            try {                 var lines = csvBreedingContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length <= 1)
                {
                    return result; // No data rows
                }
                // Skip header row (first line)
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line))
                        continue;
                    var columns = ParseCsvLine(line);
                    // Ensure we have enough columns (should have 6 based on header)
                    while (columns.Count < 6)
                    {
                        columns.Add("");
                    }
                    try
                    {
                        BoxPredictedDates newBoxStatusData = new BoxPredictedDates
                        {
                            boxNumber = int.Parse(columns[2]),
                            estHatchDate = columns[5],
                            estPGDate = columns[8],
                            estFledgeDate = columns[10],
                            chipWindowStart = columns[12],
                            chipWindowFinish = columns[13],
                        };
                        result.Add(newBoxStatusData);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CSV parsing error: {ex.Message}");
            }
            return result;
        }
        public List<BirdCsvRowData> ParseBirdCsvData(string csvContent)
        {
            var result = new List<BirdCsvRowData>();
            try
            {
                var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length <= 1)
                {
                    return result; // No data rows
                }

                // Skip header row (first line)
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line))
                        continue;

                    var columns = ParseCsvLine(line);

                    // Ensure we have enough columns (should have 36 based on header)
                    while (columns.Count < 36)
                    {
                        columns.Add("");
                    }

                    var csvRow = new BirdCsvRowData
                    {
                        Number = columns[0],
                        ScannedId = columns[1],
                        ChipDate = columns[2],
                        Sex = columns[3],
                        VidForScanner = columns[4],
                        PlusBoxes = columns[5],
                        ChipBox = columns[6],
                        BreedBox2021 = columns[7],
                        BreedBox2022 = columns[8],
                        BreedBox2023 = columns[9],
                        BreedBox2024 = columns[10],
                        BreedBox2025 = columns[11],
                        LastKnownLifeStage = columns[12],
                        NestSuccess2021 = columns[13],
                        ReClutch21 = columns[14],
                        NestSuccess2022 = columns[15],
                        ReClutch22 = columns[16],
                        NestSuccess2023 = columns[17],
                        ReClutch23 = columns[18],
                        NestSuccess2024 = columns[19],
                        ReClutch24 = columns[20],
                        ChipBy = columns[21],
                        ChipAs = columns[22],
                        ChipOk = columns[23],
                        ChipWeight = columns[24],
                        FlipperLength = columns[25],
                        Persistence = columns[26],
                        AlarmsScanner = columns[27],
                        WasSingle = columns[28],
                        ChickSizeSex = columns[29],
                        ChickReturnDate = columns[30],
                        ReChip = columns[31],
                        ReChipBy = columns[32],
                        ActiveChip2 = columns[33],
                        RechipDate = columns[34],
                        FullIso15Digits = columns[35],
                        Solo = columns.Count > 36 ? columns[36] : "",
                        Kommentar = columns.Count > 37 ? columns[37] : ""
                    };

                    result.Add(csvRow);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CSV parsing error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Returns List of string from the comma seperated values from a CSV line, handling quoted fields with commas correctly.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var currentField = new StringBuilder();
            var inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped quote
                        currentField.Append('"');
                        i++; // Skip next quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            result.Add(currentField.ToString());
            return result;
        }

        public string ConvertToGoogleSheetsCsvUrl(string shareUrl)
        {
            // Extract the spreadsheet ID from the sharing URL
            var uri = new Uri(shareUrl);
            var pathSegments = uri.AbsolutePath.Split('/');
            var spreadsheetId = "";
            
            for (int i = 0; i < pathSegments.Length; i++)
            {
                if (pathSegments[i] == "d" && i + 1 < pathSegments.Length)
                {
                    spreadsheetId = pathSegments[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(spreadsheetId))
            {
                throw new ArgumentException("Could not extract spreadsheet ID from URL");
            }

            // Return the CSV export URL
            return $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv";
        }
    }
}