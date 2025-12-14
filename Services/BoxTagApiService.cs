using Newtonsoft.Json;
using PenguinMonitor.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PenguinMonitor.Services
{
    /// <summary>
    /// HTTP client service for BoxTags REST API
    /// </summary>
    public class BoxTagApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiUrl;
        private readonly string _apiKey;

        public BoxTagApiService(string apiUrl, string apiKey)
        {
            _apiUrl = apiUrl.TrimEnd('/');
            _apiKey = apiKey;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        }

        /// <summary>
        /// Get all box tags from the remote API
        /// </summary>
        public async Task<Dictionary<string, BoxTag>> GetAllBoxTagsAsync()
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(_apiUrl);
                var httpTime = sw.ElapsedMilliseconds;
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var readTime = sw.ElapsedMilliseconds;
                var result = JsonConvert.DeserializeObject<ApiResponse<Dictionary<string, BoxTag>>>(json);
                System.Diagnostics.Debug.WriteLine($"BoxTagApiService.GetAllBoxTagsAsync: HTTP={httpTime}ms, Read={readTime - httpTime}ms, Total={sw.ElapsedMilliseconds}ms");

                if (result?.Success == true && result.Data != null)
                {
                    return result.Data;
                }

                return new Dictionary<string, BoxTag>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BoxTagApiService.GetAllBoxTagsAsync failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get a single box tag by box ID
        /// </summary>
        public async Task<BoxTag?> GetBoxTagAsync(string boxId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_apiUrl}?box_id={Uri.EscapeDataString(boxId)}");

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<ApiResponse<BoxTag>>(json);

                return result?.Data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BoxTagApiService.GetBoxTagAsync failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Create or update a box tag
        /// </summary>
        public async Task<bool> SaveBoxTagAsync(BoxTag boxTag)
        {
            try
            {
                var json = JsonConvert.SerializeObject(boxTag);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_apiUrl, content);
                response.EnsureSuccessStatusCode();

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BoxTagApiService.SaveBoxTagAsync failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Delete a box tag
        /// </summary>
        public async Task<bool> DeleteBoxTagAsync(string boxId)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, $"{_apiUrl}?box_id={Uri.EscapeDataString(boxId)}");
                var response = await _httpClient.SendAsync(request);

                // 404 is acceptable - tag already doesn't exist
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return true;
                }

                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BoxTagApiService.DeleteBoxTagAsync failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Check if the API is reachable
        /// </summary>
        public async Task<bool> IsApiAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_apiUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// API response wrapper
        /// </summary>
        private class ApiResponse<T>
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("data")]
            public T? Data { get; set; }

            [JsonProperty("error")]
            public string? Error { get; set; }

            [JsonProperty("message")]
            public string? Message { get; set; }
        }
    }
}
