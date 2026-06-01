using TouchNStars.Server.Models;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for TNS settings management
/// </summary>
public class SettingsController : WebApiController
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA", "TnsCache", "settings.json"
    );
    private static readonly object _fileLock = new();

    /// <summary>
    /// POST /api/settings - Save or create a setting
    /// </summary>
    [Route(HttpVerbs.Post, "/settings")]
    public async Task<ApiResponse> SaveSetting()
    {
        try
        {
            var setting = await HttpContext.GetRequestDataAsync<Setting>();

            if (string.IsNullOrEmpty(setting.Key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            Dictionary<string, string> currentSettings = new();
            if (File.Exists(SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(SettingsFilePath);
                currentSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true }) ?? new();
            }

            currentSettings[setting.Key] = setting.Value;

            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(
                    currentSettings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(SettingsFilePath, updatedJson);
            }

            return new ApiResponse
            {
                Success = true,
                Response = setting,
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/settings - Get all settings
    /// </summary>
    [Route(HttpVerbs.Get, "/settings")]
    public async Task<Dictionary<string, string>> GetAllSettings()
    {
        if (!File.Exists(SettingsFilePath)) return new Dictionary<string, string>();

        var json = await File.ReadAllTextAsync(SettingsFilePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true }) ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// GET /api/settings/{key} - Get a specific setting by key
    /// </summary>
    [Route(HttpVerbs.Get, "/settings/{key}")]
    public async Task<ApiResponse> GetSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!File.Exists(SettingsFilePath))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Einstellung nicht gefunden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true }) ?? new();

            if (!settings.ContainsKey(key))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Einstellung nicht gefunden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new Setting { Key = key, Value = settings[key] },
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// DELETE /api/settings/{key} - Delete a setting
    /// </summary>
    [Route(HttpVerbs.Delete, "/settings/{key}")]
    public async Task<ApiResponse> DeleteSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!File.Exists(SettingsFilePath))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Keine Einstellungen vorhanden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true }) ?? new();

            if (!settings.ContainsKey(key))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Einstellung nicht gefunden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            settings.Remove(key);

            lock (_fileLock)
            {
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(
                    settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(SettingsFilePath, updatedJson);
            }

            return new ApiResponse
            {
                Success = true,
                Response = $"Einstellung '{key}' gelöscht",
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/settings/{key} - Update an existing setting
    /// </summary>
    [Route(HttpVerbs.Put, "/settings/{key}")]
    public async Task<ApiResponse> UpdateSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var updatedSetting = await HttpContext.GetRequestDataAsync<Setting>();

            if (!File.Exists(SettingsFilePath))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Keine Einstellungen vorhanden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true }) ?? new();

            if (!settings.ContainsKey(key))
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "Einstellung nicht gefunden",
                    StatusCode = 404,
                    Type = "NotFound"
                };
            }

            settings[key] = updatedSetting.Value;

            lock (_fileLock)
            {
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(
                    settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(SettingsFilePath, updatedJson);
            }

            return new ApiResponse
            {
                Success = true,
                Response = new Setting { Key = key, Value = settings[key] },
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }
}
