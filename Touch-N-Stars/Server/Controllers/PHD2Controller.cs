using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TouchNStars.PHD2;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for PHD2 guiding integration
/// </summary>
public class PHD2Controller : WebApiController
{
    private static PHD2Service phd2Service;
    private static PHD2ImageService phd2ImageService;
    private static readonly object phd2InitLock = new object();

    private static void EnsurePHD2ServicesInitialized()
    {
        if (phd2Service == null || phd2ImageService == null)
        {
            lock (phd2InitLock)
            {
                if (phd2Service == null)
                {
                    try
                    {
                        Logger.Debug("Initializing PHD2Service on demand");
                        phd2Service = new PHD2Service();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to initialize PHD2Service: {ex}");
                        throw;
                    }
                }

                if (phd2ImageService == null && phd2Service != null)
                {
                    try
                    {
                        Logger.Debug("Initializing PHD2ImageService on demand");
                        phd2ImageService = new PHD2ImageService(phd2Service);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to initialize PHD2ImageService: {ex}");
                        throw;
                    }
                }
            }
        }
    }

    public static void CleanupPHD2Service()
    {
        phd2ImageService?.Dispose();
        phd2Service?.Dispose();
    }

    /// <summary>
    /// GET /api/phd2/status - Get PHD2 connection and guiding status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/status")]
    public async Task<ApiResponse> GetPHD2Status()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var status = await phd2Service.GetStatusAsync();
            return new ApiResponse
            {
                Success = true,
                Response = status,
                StatusCode = 200,
                Type = "PHD2Status"
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
    /// POST /api/phd2/connect - Connect to PHD2
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/connect")]
    public async Task<ApiResponse> ConnectPHD2()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            string hostname = "localhost";
            uint instance = 1;

            try
            {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null)
                {
                    if (requestData.ContainsKey("hostname") && requestData["hostname"] != null)
                    {
                        hostname = requestData["hostname"].ToString();
                    }
                    if (requestData.ContainsKey("instance") && requestData["instance"] != null)
                    {
                        if (uint.TryParse(requestData["instance"].ToString(), out uint parsedInstance))
                        {
                            instance = parsedInstance;
                        }
                    }
                }
            }
            catch
            {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.ConnectAsync(hostname, instance);

            return new ApiResponse
            {
                Success = result,
                Response = new { Connected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Connection"
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
    /// POST /api/phd2/disconnect - Disconnect from PHD2
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/disconnect")]
    public async Task<ApiResponse> DisconnectPHD2()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.DisconnectAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Connected = false },
                StatusCode = 200,
                Type = "PHD2Connection"
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
    /// GET /api/phd2/profiles - Get equipment profiles
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/profiles")]
    public async Task<ApiResponse> GetPHD2Profiles()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var profiles = await phd2Service.GetEquipmentProfilesAsync();

            return new ApiResponse
            {
                Success = true,
                Response = profiles,
                StatusCode = 200,
                Type = "PHD2Profiles"
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
    /// POST /api/phd2/profile/create - Create a new equipment profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/profile/create")]
    public async Task<ApiResponse> CreatePHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();

            if (requestData == null || !requestData.ContainsKey("name") || requestData["name"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string profileName = requestData["name"].ToString();

            if (string.IsNullOrEmpty(profileName))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            int profileId = await phd2Service.CreateProfileAsync(profileName);

            if (profileId < 0)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "Failed to create profile",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new { ProfileId = profileId, ProfileName = profileName },
                StatusCode = 200,
                Type = "PHD2Profile"
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
    /// POST /api/phd2/profile/delete - Delete an equipment profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/profile/delete")]
    public async Task<ApiResponse> DeletePHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();

            if (requestData == null || !requestData.ContainsKey("name") || requestData["name"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string profileName = requestData["name"].ToString();

            if (string.IsNullOrEmpty(profileName))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.DeleteProfileAsync(profileName);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "Failed to delete profile",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new { ProfileName = profileName, Deleted = true },
                StatusCode = 200,
                Type = "PHD2Profile"
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
    /// POST /api/phd2/profile/rename - Rename the current equipment profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/profile/rename")]
    public async Task<ApiResponse> RenamePHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();

            if (requestData == null || !requestData.ContainsKey("name") || requestData["name"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "New profile name is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string newName = requestData["name"].ToString();

            if (string.IsNullOrEmpty(newName))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.RenameProfileAsync(newName);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "Failed to rename profile",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new { NewName = newName, Renamed = true },
                StatusCode = 200,
                Type = "PHD2Profile"
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
    /// PUT /api/phd2/profile/select - Select an equipment profile by ID
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/profile/select")]
    public async Task<ApiResponse> SelectPHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();

            if (requestData == null || !requestData.ContainsKey("id") || requestData["id"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile ID is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["id"].ToString(), out int profileId) || profileId <= 0)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile ID must be a valid positive integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.SetProfileAsync(profileId);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "Failed to select profile",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new { ProfileId = profileId, Selected = true },
                StatusCode = 200,
                Type = "PHD2Profile"
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
    /// POST /api/phd2/connect-equipment - Connect equipment with profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/connect-equipment")]
    public async Task<ApiResponse> ConnectPHD2Equipment()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            string profileName = null;

            try
            {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null && requestData.ContainsKey("profileName") && requestData["profileName"] != null)
                {
                    profileName = requestData["profileName"].ToString();
                }
            }
            catch
            {
                // Handle parsing errors
            }

            if (string.IsNullOrEmpty(profileName))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Profile name is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.ConnectEquipmentAsync(profileName);

            return new ApiResponse
            {
                Success = result,
                Response = new { EquipmentConnected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Equipment"
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
    /// POST /api/phd2/disconnect-equipment - Disconnect equipment
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/disconnect-equipment")]
    public async Task<ApiResponse> DisconnectPHD2Equipment()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.DisconnectEquipmentAsync();

            return new ApiResponse
            {
                Success = result,
                Response = new { EquipmentDisconnected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Equipment"
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
    /// POST /api/phd2/start-guiding - Start guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/start-guiding")]
    public async Task<ApiResponse> StartPHD2Guiding()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            double settlePixels = 2.0;
            double settleTime = 10.0;
            double settleTimeout = 100.0;

            try
            {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null)
                {
                    if (requestData.ContainsKey("settlePixels") && requestData["settlePixels"] != null)
                    {
                        if (double.TryParse(requestData["settlePixels"].ToString(), out double parsedSettlePixels))
                        {
                            settlePixels = parsedSettlePixels;
                        }
                    }
                    if (requestData.ContainsKey("settleTime") && requestData["settleTime"] != null)
                    {
                        if (double.TryParse(requestData["settleTime"].ToString(), out double parsedSettleTime))
                        {
                            settleTime = parsedSettleTime;
                        }
                    }
                    if (requestData.ContainsKey("settleTimeout") && requestData["settleTimeout"] != null)
                    {
                        if (double.TryParse(requestData["settleTimeout"].ToString(), out double parsedSettleTimeout))
                        {
                            settleTimeout = parsedSettleTimeout;
                        }
                    }
                }
            }
            catch
            {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.StartGuidingAsync(settlePixels, settleTime, settleTimeout);

            return new ApiResponse
            {
                Success = result,
                Response = new { GuidingStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Guiding"
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
    /// POST /api/phd2/stop-guiding - Stop guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/stop-guiding")]
    public async Task<ApiResponse> StopPHD2Guiding()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.StopGuidingAsync();

            return new ApiResponse
            {
                Success = result,
                Response = new { GuidingStopped = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Guiding"
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
    /// POST /api/phd2/dither - Dither guiding star
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dither")]
    public async Task<ApiResponse> DitherPHD2()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            double ditherPixels = 3.0;
            double settlePixels = 2.0;
            double settleTime = 10.0;
            double settleTimeout = 100.0;

            try
            {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null)
                {
                    if (requestData.ContainsKey("ditherPixels") && requestData["ditherPixels"] != null)
                    {
                        if (double.TryParse(requestData["ditherPixels"].ToString(), out double parsedDitherPixels))
                        {
                            ditherPixels = parsedDitherPixels;
                        }
                    }
                    if (requestData.ContainsKey("settlePixels") && requestData["settlePixels"] != null)
                    {
                        if (double.TryParse(requestData["settlePixels"].ToString(), out double parsedSettlePixels))
                        {
                            settlePixels = parsedSettlePixels;
                        }
                    }
                    if (requestData.ContainsKey("settleTime") && requestData["settleTime"] != null)
                    {
                        if (double.TryParse(requestData["settleTime"].ToString(), out double parsedSettleTime))
                        {
                            settleTime = parsedSettleTime;
                        }
                    }
                    if (requestData.ContainsKey("settleTimeout") && requestData["settleTimeout"] != null)
                    {
                        if (double.TryParse(requestData["settleTimeout"].ToString(), out double parsedSettleTimeout))
                        {
                            settleTimeout = parsedSettleTimeout;
                        }
                    }
                }
            }
            catch
            {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.DitherAsync(ditherPixels, settlePixels, settleTime, settleTimeout);

            return new ApiResponse
            {
                Success = result,
                Response = new { DitherStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Dither"
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
    /// POST /api/phd2/pause - Pause guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/pause")]
    public async Task<ApiResponse> PausePHD2()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.PauseGuidingAsync();

            return new ApiResponse
            {
                Success = result,
                Response = new { Paused = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Pause"
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
    /// POST /api/phd2/unpause - Resume guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/unpause")]
    public async Task<ApiResponse> UnpausePHD2()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.UnpauseGuidingAsync();

            return new ApiResponse
            {
                Success = result,
                Response = new { Unpaused = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Pause"
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
    /// POST /api/phd2/start-looping - Start looping exposures
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/start-looping")]
    public async Task<ApiResponse> StartPHD2Looping()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.StartLoopingAsync();

            return new ApiResponse
            {
                Success = result,
                Response = new { LoopingStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Looping"
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
    /// GET /api/phd2/settling - Get settling status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/settling")]
    public async Task<ApiResponse> GetPHD2Settling()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var settling = await phd2Service.CheckSettlingAsync();

            return new ApiResponse
            {
                Success = true,
                Response = settling,
                StatusCode = 200,
                Type = "PHD2Settling"
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
    /// GET /api/phd2/pixel-scale - Get pixel scale
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/pixel-scale")]
    public async Task<ApiResponse> GetPHD2PixelScale()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var pixelScale = await phd2Service.GetPixelScaleAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { PixelScale = pixelScale },
                StatusCode = 200,
                Type = "PHD2PixelScale"
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
    /// GET /api/phd2/calibration/focal-length - Get focal length
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/focal-length")]
    public async Task<ApiResponse> GetPHD2FocalLength()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var focalLength = await phd2Service.GetFocalLengthAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { FocalLength = focalLength },
                StatusCode = 200,
                Type = "PHD2FocalLength"
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
    /// PUT /api/phd2/focal-length - Set focal length
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/focal-length")]
    public async Task<ApiResponse> SetPHD2FocalLength()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("focalLength") || requestData["focalLength"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "focalLength parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["focalLength"].ToString(), out int focalLength))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "focalLength must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetFocalLengthAsync(focalLength);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2FocalLength = focalLength;

            return new ApiResponse
            {
                Success = true,
                Response = new { FocalLength = focalLength },
                StatusCode = 200,
                Type = "PHD2FocalLength"
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
    /// GET /api/phd2/all-info - Get comprehensive PHD2 information
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/all-info")]
    public async Task<ApiResponse> GetAllPHD2Info()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Get all available PHD2 information in parallel
            var statusTask = phd2Service.GetStatusAsync();
            var profilesTask = phd2Service.GetEquipmentProfilesAsync();
            var settlingTask = phd2Service.CheckSettlingAsync();
            var pixelScaleTask = phd2Service.GetPixelScaleAsync();

            await Task.WhenAll(statusTask, profilesTask, settlingTask, pixelScaleTask);

            var status = await statusTask;
            var profiles = await profilesTask;
            var settling = await settlingTask;
            var pixelScale = await pixelScaleTask;

            // Try to get star image info if available
            object starImageInfo = null;
            try
            {
                if (phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping" || status?.AppState == "Calibrating" || status?.AppState == "Selected" || status?.AppState == "Paused"))
                {
                    var starImage = await phd2Service.GetStarImageAsync(15); // Get minimal size star image for info
                    if (starImage != null)
                    {
                        starImageInfo = new
                        {
                            Available = true,
                            Frame = starImage.Frame,
                            Width = starImage.Width,
                            Height = starImage.Height,
                            StarPosition = new
                            {
                                X = starImage.StarPosX,
                                Y = starImage.StarPosY
                            },
                            StarInfo = status?.CurrentStar != null ? new
                            {
                                SNR = status.CurrentStar.SNR,
                                HFD = status.CurrentStar.HFD,
                                StarMass = status.CurrentStar.StarMass,
                                LastUpdate = status.CurrentStar.LastUpdate,
                                TimeSinceUpdate = DateTime.Now - status.CurrentStar.LastUpdate
                            } : null
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get star image info: {ex.Message}");
                starImageInfo = new
                {
                    Available = false,
                    Error = ex.Message
                };
            }

            if (starImageInfo == null)
            {
                starImageInfo = new
                {
                    Available = false,
                    Reason = "Not guiding or looping"
                };
            }

            var allInfo = new
            {
                Connection = new
                {
                    IsConnected = phd2Service.IsConnected,
                    LastError = phd2Service.LastError
                },
                Status = status,
                EquipmentProfiles = profiles,
                Settling = settling,
                PixelScale = pixelScale,
                StarImage = starImageInfo,
                StarInfo = status?.CurrentStar != null ? new
                {
                    SNR = status.CurrentStar.SNR,
                    HFD = status.CurrentStar.HFD,
                    StarMass = status.CurrentStar.StarMass,
                    LastUpdate = status.CurrentStar.LastUpdate
                } : null,
                Capabilities = new
                {
                    CanGuide = phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping" || status?.AppState == "Stopped"),
                    CanDither = phd2Service.IsConnected && status?.AppState == "Guiding",
                    CanPause = phd2Service.IsConnected && status?.AppState == "Guiding",
                    CanLoop = phd2Service.IsConnected && status?.AppState == "Stopped",
                    CanGetStarImage = phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping")
                },
                GuideStats = status?.Stats != null ? new
                {
                    RmsTotal = status.Stats.RmsTotal,
                    RmsRA = status.Stats.RmsRA,
                    RmsDec = status.Stats.RmsDec,
                    PeakRA = status.Stats.PeakRA,
                    PeakDec = status.Stats.PeakDec,
                    AvgDistance = status.AvgDist
                } : null,
                StarLostInfo = status?.LastStarLost != null ? new
                {
                    Frame = status.LastStarLost.Frame,
                    Time = status.LastStarLost.Time,
                    StarMass = status.LastStarLost.StarMass,
                    SNR = status.LastStarLost.SNR,
                    AvgDist = status.LastStarLost.AvgDist,
                    ErrorCode = status.LastStarLost.ErrorCode,
                    Status = status.LastStarLost.Status,
                    Timestamp = status.LastStarLost.Timestamp,
                    TimeSinceLost = DateTime.Now - status.LastStarLost.Timestamp
                } : null,
                ServerInfo = new
                {
                    PHD2Version = status?.Version,
                    PHD2Subversion = status?.PHDSubver,
                    AppState = status?.AppState,
                    IsGuiding = status?.IsGuiding ?? false,
                    IsSettling = status?.IsSettling ?? false
                }
            };

            return new ApiResponse
            {
                Success = true,
                Response = allInfo,
                StatusCode = 200,
                Type = "PHD2AllInfo"
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

    // PHD2 Parameter Control Endpoints

    /// <summary>
    /// POST /api/phd2/set-exposure - Set exposure time
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-exposure")]
    public async Task<ApiResponse> SetPHD2Exposure()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("exposureMs") || requestData["exposureMs"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposureMs parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["exposureMs"].ToString(), out int exposureMs))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposureMs must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetExposureAsync(exposureMs);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2ExposureMs = exposureMs;

            return new ApiResponse
            {
                Success = true,
                Response = new { ExposureSet = exposureMs },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-exposure - Get exposure time
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-exposure")]
    public async Task<ApiResponse> GetPHD2Exposure()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var exposure = await phd2Service.GetExposureAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Exposure = exposure },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// POST /api/phd2/set-dec-guide-mode - Set declination guide mode
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-dec-guide-mode")]
    public async Task<ApiResponse> SetPHD2DecGuideMode()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("mode") || requestData["mode"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "mode parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string mode = requestData["mode"].ToString();
            await phd2Service.SetDecGuideModeAsync(mode);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2DecGuideMode = mode;

            return new ApiResponse
            {
                Success = true,
                Response = new { DecGuideModeSet = mode },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-dec-guide-mode - Get declination guide mode
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-dec-guide-mode")]
    public async Task<ApiResponse> GetPHD2DecGuideMode()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var mode = await phd2Service.GetDecGuideModeAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { DecGuideMode = mode },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-max-ra-duration - Get max RA guide pulse duration (ms).
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-max-ra-duration")]
    public async Task<ApiResponse> GetPHD2MaxRaDuration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var value = await phd2Service.GetMaxRaDurationAsync();
            return new ApiResponse { Success = true, Response = new { MaxRaDuration = value }, StatusCode = 200, Type = "PHD2Parameter" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-max-ra-duration - Set max RA guide pulse duration (ms).
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-max-ra-duration")]
    public async Task<ApiResponse> SetPHD2MaxRaDuration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("ms") || requestData["ms"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "ms parameter is required", StatusCode = 400, Type = "Error" };
            }
            if (!int.TryParse(requestData["ms"].ToString(), out int ms))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "ms must be an integer", StatusCode = 400, Type = "Error" };
            }
            await phd2Service.SetMaxRaDurationAsync(ms);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MaxRADuration = ms;
            return new ApiResponse { Success = true, Response = new { MaxRaDurationSet = ms }, StatusCode = 200, Type = "PHD2Parameter" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-max-dec-duration - Get max DEC guide pulse duration (ms).
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-max-dec-duration")]
    public async Task<ApiResponse> GetPHD2MaxDecDuration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var value = await phd2Service.GetMaxDecDurationAsync();
            return new ApiResponse { Success = true, Response = new { MaxDecDuration = value }, StatusCode = 200, Type = "PHD2Parameter" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-max-dec-duration - Set max DEC guide pulse duration (ms).
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-max-dec-duration")]
    public async Task<ApiResponse> SetPHD2MaxDecDuration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("ms") || requestData["ms"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "ms parameter is required", StatusCode = 400, Type = "Error" };
            }
            if (!int.TryParse(requestData["ms"].ToString(), out int ms))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "ms must be an integer", StatusCode = 400, Type = "Error" };
            }
            await phd2Service.SetMaxDecDurationAsync(ms);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MaxDecDuration = ms;
            return new ApiResponse { Success = true, Response = new { MaxDecDurationSet = ms }, StatusCode = 200, Type = "PHD2Parameter" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// GET /api/phd2/dark-library/info - Get dark library info
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/dark-library/info")]
    public async Task<ApiResponse> GetPHD2DarkLibraryInfo()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var info = await phd2Service.GetDarkLibraryInfoAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Exists = info.Value<bool>("exists"),
                    Loaded = info.Value<bool>("loaded"),
                    NumDarks = info.Value<int>("numDarks"),
                    MinExposureSec = info.Value<double>("minExposureSec"),
                    MaxExposureSec = info.Value<double>("maxExposureSec"),
                },
                StatusCode = 200,
                Type = "PHD2DarkLibrary"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/dark-library/load - Load the dark library
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dark-library/load")]
    public async Task<ApiResponse> LoadPHD2DarkLibrary()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.LoadDarkLibraryAsync();
            return new ApiResponse { Success = true, Response = new { Loaded = true }, StatusCode = 200, Type = "PHD2DarkLibrary" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/dark-library/unload - Unload the dark library
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dark-library/unload")]
    public async Task<ApiResponse> UnloadPHD2DarkLibrary()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.UnloadDarkLibraryAsync();
            return new ApiResponse { Success = true, Response = new { Unloaded = true }, StatusCode = 200, Type = "PHD2DarkLibrary" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// DELETE /api/phd2/dark-library - Delete the dark library file
    /// </summary>
    [Route(HttpVerbs.Delete, "/phd2/dark-library")]
    public async Task<ApiResponse> DeletePHD2DarkLibrary()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.DeleteDarkLibraryAsync();
            return new ApiResponse { Success = true, Response = new { Deleted = true }, StatusCode = 200, Type = "PHD2DarkLibrary" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/dark-library/build - Start building a dark library.
    /// Body: { "expTimesMs": [500, 1000, 2000], "frameCount": 10 }
    /// Returns immediately; progress arrives as PHD2 events DarkLibraryBuildProgress / DarkLibraryBuildComplete.
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dark-library/build")]
    public async Task<ApiResponse> StartPHD2DarkLibraryBuild()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("expTimesMs") || requestData["expTimesMs"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "expTimesMs array is required", StatusCode = 400, Type = "Error" };
            }

            int[] expTimesMs;
            try
            {
                var arrVal = requestData["expTimesMs"];
                int[] parsed = null;
                if (arrVal is System.Collections.Generic.List<object> list)
                {
                    parsed = list.Select(o => Convert.ToInt32(o)).ToArray();
                }
                else if (arrVal is object[] objArr)
                {
                    parsed = objArr.Select(o => Convert.ToInt32(o)).ToArray();
                }
                else if (arrVal is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    parsed = je.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                }
                if (parsed == null || parsed.Length == 0)
                    throw new ArgumentException("empty");
                expTimesMs = parsed;
            }
            catch (Exception innerEx)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse { Success = false, Error = "expTimesMs must be a non-empty array of integers (ms)", StatusCode = 400, Type = "Error" };
            }

            int frameCount = 5;
            if (requestData.ContainsKey("frameCount") && requestData["frameCount"] != null)
            {
                if (!int.TryParse(requestData["frameCount"].ToString(), out frameCount) || frameCount < 1)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "frameCount must be a positive integer", StatusCode = 400, Type = "Error" };
                }
            }

            await phd2Service.StartBuildDarkLibraryAsync(expTimesMs, frameCount);
            return new ApiResponse { Success = true, Response = new { Building = true }, StatusCode = 200, Type = "PHD2DarkLibrary" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/dark-library/cancel-build - Cancel an in-progress dark library build
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dark-library/cancel-build")]
    public async Task<ApiResponse> CancelPHD2DarkLibraryBuild()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.CancelBuildDarkLibraryAsync();
            return new ApiResponse { Success = true, Response = new { Cancelled = true }, StatusCode = 200, Type = "PHD2DarkLibrary" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// GET /api/phd2/dark-library/build-status - Poll dark library build progress
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/dark-library/build-status")]
    public Task<ApiResponse> GetPHD2DarkLibraryBuildStatus()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var status = phd2Service.GetDarkBuildStatus();
            return Task.FromResult(new ApiResponse
            {
                Success = true,
                Response = new
                {
                    status.Active,
                    status.Complete,
                    status.Success,
                    status.Frame,
                    status.TotalFrames,
                    status.ExposureMs,
                    status.Error
                },
                StatusCode = 200,
                Type = "PHD2DarkLibraryBuildStatus"
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return Task.FromResult(new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" });
        }
    }

    /// <summary>
    /// POST /api/phd2/set-guide-output-enabled - Enable/disable guide output
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-guide-output-enabled")]
    public async Task<ApiResponse> SetPHD2GuideOutputEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || requestData["enabled"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetGuideOutputEnabledAsync(enabled);

            return new ApiResponse
            {
                Success = true,
                Response = new { GuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-guide-output-enabled - Get guide output status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-guide-output-enabled")]
    public async Task<ApiResponse> GetPHD2GuideOutputEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetGuideOutputEnabledAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { GuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// POST /api/phd2/set-lock-position - Set lock position
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-position")]
    public async Task<ApiResponse> SetPHD2LockPosition()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("x") || !requestData.ContainsKey("y"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "x and y parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["x"].ToString(), out double x) ||
                !double.TryParse(requestData["y"].ToString(), out double y))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "x and y must be valid numbers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool exact = true;
            if (requestData.ContainsKey("exact") && requestData["exact"] != null)
            {
                bool.TryParse(requestData["exact"].ToString(), out exact);
            }

            await phd2Service.SetLockPositionAsync(x, y, exact);

            return new ApiResponse
            {
                Success = true,
                Response = new { LockPositionSet = new { X = x, Y = y, Exact = exact } },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-lock-position - Get lock position
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-position")]
    public async Task<ApiResponse> GetPHD2LockPosition()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var position = await phd2Service.GetLockPositionAsync();

            if (position == null || position.Length < 2)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Keine Daten vorhanden - PHD2 ist derzeit nicht am guidenden oder es wurde keine Lock-Position festgelegt",
                    StatusCode = 400,
                    Type = "NoDataAvailable"
                };
            }

            return new ApiResponse
            {
                Success = true,
                Response = new { LockPosition = new { X = position[0], Y = position[1] } },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("PHD2 not connected"))
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "PHD2 ist nicht erreichbar",
                StatusCode = 500,
                Type = "PHD2NotConnected"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = "Keine Daten vorhanden - " + ex.Message,
                StatusCode = 400,
                Type = "NoDataAvailable"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/find-star - Auto-select a star
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/find-star")]
    public async Task<ApiResponse> FindPHD2Star()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            int[] roi = null;

            // Parse optional ROI parameter
            if (requestData != null && requestData.ContainsKey("roi") && requestData["roi"] != null)
            {
                try
                {
                    var roiData = requestData["roi"];
                    if (roiData is Newtonsoft.Json.Linq.JArray roiArray && roiArray.Count == 4)
                    {
                        roi = new int[4];
                        for (int i = 0; i < 4; i++)
                        {
                            roi[i] = (int)roiArray[i];
                        }
                    }
                    else if (roiData is System.Collections.Generic.List<object> roiList && roiList.Count == 4)
                    {
                        roi = new int[4];
                        for (int i = 0; i < 4; i++)
                        {
                            roi[i] = Convert.ToInt32(roiList[i]);
                        }
                    }
                    else
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "roi must be an array of 4 integers: [x, y, width, height]",
                            StatusCode = 400,
                            Type = "Error"
                        };
                    }
                }
                catch
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "roi must be an array of 4 integers: [x, y, width, height]",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
            }

            var position = await phd2Service.FindStarAsync(roi);

            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    StarPosition = new { X = position[0], Y = position[1] },
                    ROI = roi != null ? new { X = roi[0], Y = roi[1], Width = roi[2], Height = roi[3] } : null
                },
                StatusCode = 200,
                Type = "PHD2StarSelection"
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
    /// GET /api/phd2/star-positions - Get primary and secondary guide star positions
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/star-positions")]
    public async Task<ApiResponse> GetPHD2StarPositions()
    {
        try
        {
            EnsurePHD2ServicesInitialized();

            var secondaryStars = await phd2Service.GetSecondaryStarsAsync();
            var secondary = secondaryStars
                .Select(s => new { X = s[0], Y = s[1] })
                .ToList();

            return new ApiResponse
            {
                Success = true,
                Response = new { Secondary = secondary },
                StatusCode = 200,
                Type = "PHD2StarPositions"
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
    /// POST /api/phd2/set-lock-shift-enabled - Enable/disable lock shift
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-shift-enabled")]
    public async Task<ApiResponse> SetPHD2LockShiftEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || requestData["enabled"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetLockShiftEnabledAsync(enabled);

            return new ApiResponse
            {
                Success = true,
                Response = new { LockShiftEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-lock-shift-enabled - Get lock shift status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-shift-enabled")]
    public async Task<ApiResponse> GetPHD2LockShiftEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetLockShiftEnabledAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { LockShiftEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// POST /api/phd2/set-lock-shift-params - Set lock shift parameters
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-shift-params")]
    public async Task<ApiResponse> SetPHD2LockShiftParams()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("xRate") || !requestData.ContainsKey("yRate"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "xRate and yRate parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["xRate"].ToString(), out double xRate) ||
                !double.TryParse(requestData["yRate"].ToString(), out double yRate))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "xRate and yRate must be valid numbers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string units = "arcsec/hr";
            string axes = "RA/Dec";
            if (requestData.ContainsKey("units") && requestData["units"] != null)
            {
                units = requestData["units"].ToString();
            }
            if (requestData.ContainsKey("axes") && requestData["axes"] != null)
            {
                axes = requestData["axes"].ToString();
            }

            await phd2Service.SetLockShiftParamsAsync(xRate, yRate, units, axes);

            return new ApiResponse
            {
                Success = true,
                Response = new { LockShiftParamsSet = new { XRate = xRate, YRate = yRate, Units = units, Axes = axes } },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-lock-shift-params - Get lock shift parameters
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-shift-params")]
    public async Task<ApiResponse> GetPHD2LockShiftParams()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var parameters = await phd2Service.GetLockShiftParamsAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { LockShiftParams = parameters },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// POST /api/phd2/set-algo-param - Set algorithm parameter
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-algo-param")]
    public async Task<ApiResponse> SetPHD2AlgoParam()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("axis") || !requestData.ContainsKey("name") || !requestData.ContainsKey("value"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "axis, name, and value parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string axis = requestData["axis"].ToString();
            string name = requestData["name"].ToString();

            if (!double.TryParse(requestData["value"].ToString(), out double value))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "value must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAlgoParamAsync(axis, name, value);
            var _gs = TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings;
            if (axis == "ra" && name == "minMove") _gs.PHD2RAMinMove = value;
            else if (axis == "dec" && name == "minMove") _gs.PHD2DecMinMove = value;
            else if (axis == "ra" && name == "aggression") _gs.PHD2RAAggressiveness = value;
            else if (axis == "dec" && name == "aggression") _gs.PHD2DecAggressiveness = value;
            else if (axis == "ra" && name == "hysteresis") _gs.PHD2RAHysteresis = value;
            else if (axis == "dec" && name == "hysteresis") _gs.PHD2DecHysteresis = value;
            else if (axis == "dec" && name == "fastSwitch") _gs.PHD2DecFastSwitch = value != 0.0;
            else if (axis == "ra" && name == "fastSwitch") _gs.PHD2RAFastSwitch = value != 0.0;
            else if (axis == "ra" && name == "slopeWeight") _gs.PHD2RASlopeWeight = value;
            else if (axis == "dec" && name == "slopeWeight") _gs.PHD2DecSlopeWeight = value;
            else if (axis == "ra" && name == "aggressiveness") _gs.PHD2RALowpass2Aggressiveness = value;
            else if (axis == "dec" && name == "aggressiveness") _gs.PHD2DecLowpass2Aggressiveness = value;
            else if (axis == "ra" && name == "predictiveWeight") _gs.PHD2RAPredictiveWeight = value;
            else if (axis == "dec" && name == "predictiveWeight") _gs.PHD2DecPredictiveWeight = value;
            else if (axis == "ra" && name == "reactiveWeight") _gs.PHD2RAReactiveWeight = value;
            else if (axis == "dec" && name == "reactiveWeight") _gs.PHD2DecReactiveWeight = value;
            else if (axis == "ra" && name == "periodLength") { _gs.PHD2RAPeriodLength = value; _gs.PHD2RAGPAutoAdjustPeriod = false; }
            else if (axis == "dec" && name == "periodLength") { _gs.PHD2DecPeriodLength = value; _gs.PHD2DecGPAutoAdjustPeriod = false; }
            else if (axis == "ra" && name == "expFactor") _gs.PHD2RAExpFactor = value;
            else if (axis == "dec" && name == "expFactor") _gs.PHD2DecExpFactor = value;

            return new ApiResponse
            {
                Success = true,
                Response = new { Message = $"Algorithm parameter set: {axis}.{name} = {value}" },
                StatusCode = 200,
                Type = "PHD2AlgoParamSet"
            };
        }
        catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
        {
            // Expected behavior for invalid axis parameter
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2InvalidAxis"
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
    /// GET /api/phd2/get-algo-param-names - Get algorithm parameter names
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-algo-param-names")]
    public async Task<ApiResponse> GetPHD2AlgoParamNames([QueryField(true)] string axis)
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var paramNames = await phd2Service.GetAlgoParamNamesAsync(axis);

            return new ApiResponse
            {
                Success = true,
                Response = new { Axis = axis, ParameterNames = paramNames },
                StatusCode = 200,
                Type = "PHD2AlgoParamNames"
            };
        }
        catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
        {
            // Expected behavior for invalid axis parameter
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2InvalidAxis"
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
    /// GET /api/phd2/get-algo-param - Get algorithm parameter value
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-algo-param")]
    public async Task<ApiResponse> GetPHD2AlgoParam([QueryField(true)] string axis, [QueryField(true)] string name)
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var value = await phd2Service.GetAlgoParamAsync(axis, name);

            return new ApiResponse
            {
                Success = true,
                Response = new { Axis = axis, Name = name, Value = value },
                StatusCode = 200,
                Type = "PHD2AlgoParam"
            };
        }
        catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis") || ex.Message.Contains("could not get param"))
        {
            // Expected behavior for invalid axis or parameter names
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = ex.Message.Contains("Invalid axis") ? "PHD2InvalidAxis" : "PHD2ParamNotFound"
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
    /// POST /api/phd2/set-variable-delay-settings - Set variable delay settings
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-variable-delay-settings")]
    public async Task<ApiResponse> SetPHD2VariableDelaySettings()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || !requestData.ContainsKey("shortDelaySeconds") || !requestData.ContainsKey("longDelaySeconds"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled, shortDelaySeconds, and longDelaySeconds parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled) ||
                !int.TryParse(requestData["shortDelaySeconds"].ToString(), out int shortDelaySeconds) ||
                !int.TryParse(requestData["longDelaySeconds"].ToString(), out int longDelaySeconds))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be boolean, shortDelaySeconds and longDelaySeconds must be integers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetVariableDelaySettingsAsync(enabled, shortDelaySeconds, longDelaySeconds);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2VarDelayEnabled = enabled;
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2VarDelayShortSec = shortDelaySeconds;
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2VarDelayLongSec = longDelaySeconds;

            return new ApiResponse
            {
                Success = true,
                Response = new { Message = $"Variable delay settings updated: enabled={enabled}, short={shortDelaySeconds}s, long={longDelaySeconds}s" },
                StatusCode = 200,
                Type = "PHD2VariableDelaySet"
            };
        }
        catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
        {
            // Expected behavior for unsupported PHD2 versions
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2MethodNotFound"
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
    /// GET /api/phd2/get-variable-delay-settings - Get variable delay settings
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-variable-delay-settings")]
    public async Task<ApiResponse> GetPHD2VariableDelaySettings()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var settings = await phd2Service.GetVariableDelaySettingsAsync();

            return new ApiResponse
            {
                Success = true,
                Response = settings,
                StatusCode = 200,
                Type = "PHD2VariableDelay"
            };
        }
        catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
        {
            // Expected behavior for unsupported PHD2 versions
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2MethodNotFound"
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
    /// GET /api/phd2/time-lapse - Get time lapse delay in ms
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/time-lapse")]
    public async Task<ApiResponse> GetTimeLapse()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            int ms = await phd2Service.GetTimeLapseAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { TimeLapseMs = ms },
                StatusCode = 200,
                Type = "PHD2TimeLapse"
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
    /// PUT /api/phd2/time-lapse - Set time lapse delay in ms
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/time-lapse")]
    public async Task<ApiResponse> SetTimeLapse()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("ms"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "ms parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["ms"].ToString(), out int ms) || ms < 0)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "ms must be a non-negative integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetTimeLapseAsync(ms);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2TimeLapseMs = ms;

            return new ApiResponse
            {
                Success = true,
                Response = new { TimeLapseMs = ms },
                StatusCode = 200,
                Type = "PHD2TimeLapse"
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
    /// GET /api/phd2/get-connected - Get connection status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-connected")]
    public async Task<ApiResponse> GetPHD2Connected()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var connected = await phd2Service.GetConnectedAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Connected = connected },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-paused - Get paused status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-paused")]
    public async Task<ApiResponse> GetPHD2Paused()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var paused = await phd2Service.GetPausedAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Paused = paused },
                StatusCode = 200,
                Type = "PHD2Parameter"
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
    /// GET /api/phd2/get-current-equipment - Get current equipment
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-current-equipment")]
    public async Task<ApiResponse> GetPHD2CurrentEquipment()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Check if PHD2 is connected first
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var equipment = await phd2Service.GetCurrentEquipmentAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CurrentEquipment = equipment },
                StatusCode = 200,
                Type = "PHD2CurrentEquipment"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting current equipment: {ex}");
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
    /// GET /api/phd2/get-profile - Get profile
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-profile")]
    public async Task<ApiResponse> GetPHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Check if PHD2 is connected first
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var profile = await phd2Service.GetProfileAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Profile = profile },
                StatusCode = 200,
                Type = "PHD2Profile"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting profile: {ex}");
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

    // PHD2 Image API Endpoints

    /// <summary>
    /// GET /api/phd2/current-image - Get current PHD2 image
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/current-image")]
    public async Task GetPHD2CurrentImage([QueryField] double gamma)
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            double effectiveGamma = gamma > 0 ? gamma : 0.5;
            var imageBytes = await phd2ImageService.GetCurrentImageBytesAsync(effectiveGamma);

            if (imageBytes == null)
            {
                HttpContext.Response.StatusCode = 404;
                HttpContext.Response.ContentType = "application/json";
                var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
                {
                    Success = false,
                    Error = phd2ImageService.LastError ?? "No current PHD2 image available",
                    StatusCode = 404,
                    Type = "PHD2ImageNotFound"
                });
                Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
                return;
            }

            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("Pragma", "no-cache");
            HttpContext.Response.Headers.Add("Expires", "0");
            HttpContext.Response.Headers.Add("Last-Modified", phd2ImageService.LastImageTime.ToString("R"));

            Response.OutputStream.Write(imageBytes, 0, imageBytes.Length);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error serving PHD2 image: {ex}");
            HttpContext.Response.StatusCode = 500;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
    }

    /// <summary>
    /// GET /api/phd2/image-info - Get PHD2 image info
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/image-info")]
    public async Task<ApiResponse> GetPHD2ImageInfo()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    HasCurrentImage = phd2ImageService.HasCurrentImage,
                    LastImageTime = phd2ImageService.LastImageTime,
                    LastError = phd2ImageService.LastError,
                    TimeSinceLastImage = phd2ImageService.HasCurrentImage ?
                        DateTime.Now - phd2ImageService.LastImageTime : (TimeSpan?)null
                },
                StatusCode = 200,
                Type = "PHD2ImageInfo"
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
    /// POST /api/phd2/refresh-image - Refresh PHD2 image
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/refresh-image")]
    public async Task<ApiResponse> RefreshPHD2Image()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool success = await phd2ImageService.RefreshImageAsync();

            return new ApiResponse
            {
                Success = success,
                Response = new
                {
                    ImageRefreshed = success,
                    LastImageTime = phd2ImageService.LastImageTime,
                    Error = phd2ImageService.LastError
                },
                StatusCode = success ? 200 : 400,
                Type = "PHD2ImageRefresh"
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
    /// GET /api/phd2/calibration-data - Get PHD2 mount calibration data
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration-data")]
    public async Task<ApiResponse> GetPHD2CalibrationData()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var data = await phd2Service.GetCalibrationDataAsync();
            return new ApiResponse { Success = true, Response = data, StatusCode = 200, Type = "PHD2CalibrationData" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting PHD2 calibration data: {ex}");
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2/clear-calibration - Clear PHD2 mount calibration
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/clear-calibration")]
    public async Task<ApiResponse> ClearPHD2Calibration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.ClearMountCalibrationAsync();
            return new ApiResponse { Success = true, Response = "Calibration cleared", StatusCode = 200, Type = "PHD2ClearCalibration" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error clearing PHD2 calibration: {ex}");
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// GET /api/phd2/star-image - Get PHD2 star image
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/star-image")]
    public async Task GetPHD2StarImage([QueryField] int size)
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // PHD2 requires size >= 15, default to 15 if not specified or too small
            int requestedSize = size > 0 ? Math.Max(15, size) : 15;

            // Get star image data from PHD2
            var starImageData = await phd2Service.GetStarImageAsync(requestedSize);

            if (starImageData == null)
            {
                HttpContext.Response.StatusCode = 404;
                HttpContext.Response.ContentType = "application/json";
                var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "No PHD2 star image available",
                    StatusCode = 404,
                    Type = "PHD2StarImageNotFound"
                });
                Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
                return;
            }

            // Convert base64 pixel data to JPG - no additional scaling needed as PHD2 already provides correct size
            byte[] jpgBytes = ImageConverter.ConvertBase64StarImageToJpg(
                starImageData.Pixels,
                starImageData.Width,
                starImageData.Height,
                null // Don't scale further as PHD2 already provided the requested size
            );

            // Set response headers
            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("Pragma", "no-cache");
            HttpContext.Response.Headers.Add("Expires", "0");
            HttpContext.Response.Headers.Add("X-Star-Position", $"{starImageData.StarPosX},{starImageData.StarPosY}");
            HttpContext.Response.Headers.Add("X-Image-Size", $"{starImageData.Width}x{starImageData.Height}");
            HttpContext.Response.Headers.Add("X-Frame", starImageData.Frame.ToString());
            HttpContext.Response.Headers.Add("X-Requested-Size", requestedSize.ToString());

            // Write JPG data to response
            Response.OutputStream.Write(jpgBytes, 0, jpgBytes.Length);
        }
        catch (PHD2Exception ex) when (ex.Message == "no star selected")
        {
            Logger.Debug($"PHD2 star image not available: {ex.Message}");
            HttpContext.Response.StatusCode = 404;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 404,
                Type = "PHD2StarNotSelected"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
        catch (Exception ex)
        {
            Logger.Error($"Error serving PHD2 star image: {ex}");
            HttpContext.Response.StatusCode = 500;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
    }

    /// <summary>
    /// GET /api/phd2/camera/info - Get detailed info about the currently selected guide camera
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/info")]
    public async Task<ApiResponse> GetCameraInfo()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var info = await phd2Service.GetCameraInfoAsync();
            return new ApiResponse
            {
                Success = true,
                Response = info,
                StatusCode = 200,
                Type = "PHD2CameraInfo"
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
    /// GET /api/phd2/camera/ids - Get all camera IDs grouped by camera name from PHD2
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/ids")]
    public async Task<ApiResponse> GetAllCameraIds()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Check if PHD2 is connected first
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var cameraIds = await phd2Service.GetAllCameraIdsAsync();

            return new ApiResponse
            {
                Success = true,
                Response = cameraIds,
                StatusCode = 200,
                Type = "PHD2CameraIds"
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

    // Calibration Step endpoints
    /// <summary>
    /// GET /api/phd2/calibration/step - Get calibration step
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/step")]
    public async Task<ApiResponse> GetCalibrationStep()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var step = await phd2Service.GetCalibrationStepAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CalibrationStep = step },
                StatusCode = 200,
                Type = "PHD2CalibrationStep"
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
    /// PUT /api/phd2/calibration/step - Set calibration step
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/step")]
    public async Task<ApiResponse> SetCalibrationStep()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("calibrationStep") || requestData["calibrationStep"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "calibrationStep parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["calibrationStep"].ToString(), out int step))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "calibrationStep must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCalibrationStepAsync(step);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2CalibrationStepMs = step;

            return new ApiResponse
            {
                Success = true,
                Response = new { CalibrationStep = step },
                StatusCode = 200,
                Type = "PHD2CalibrationStep"
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
    /// GET /api/phd2/calibration/distance - Get calibration distance
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/distance")]
    public async Task<ApiResponse> GetCalibrationDistance()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var distance = await phd2Service.GetCalibrationDistanceAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CalibrationDistance = distance },
                StatusCode = 200,
                Type = "PHD2CalibrationDistance"
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
    /// PUT /api/phd2/calibration/distance - Set calibration distance
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/distance")]
    public async Task<ApiResponse> SetCalibrationDistance()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("calibrationDistance") || requestData["calibrationDistance"] == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "calibrationDistance parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["calibrationDistance"].ToString(), out int distance))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "calibrationDistance must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCalibrationDistanceAsync(distance);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2CalibrationDistancePx = distance;

            return new ApiResponse
            {
                Success = true,
                Response = new { CalibrationDistance = distance },
                StatusCode = 200,
                Type = "PHD2CalibrationDistance"
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
    /// POST /api/phd2/calibration/clear - Clear mount calibration
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/calibration/clear")]
    public async Task<ApiResponse> ClearMountCalibration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            await phd2Service.ClearMountCalibrationAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Message = "Mount calibration cleared" },
                StatusCode = 200,
                Type = "PHD2CalibrationCleared"
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

    // Auto-Restore Calibration endpoints
    /// <summary>
    /// GET /api/phd2/calibration/auto-restore - Get auto restore calibration
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/auto-restore")]
    public async Task<ApiResponse> GetAutoRestoreCalibration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetAutoRestoreCalibrationsAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { AutoRestoreCalibration = enabled },
                StatusCode = 200,
                Type = "PHD2AutoRestoreCalibration"
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
    /// PUT /api/phd2/calibration/auto-restore - Set auto restore calibration
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/auto-restore")]
    public async Task<ApiResponse> SetAutoRestoreCalibration()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAutoRestoreCalibrationsAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2AutoRestoreCalibration = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { AutoRestoreCalibration = enabled },
                StatusCode = 200,
                Type = "PHD2AutoRestoreCalibration"
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

    // Assume Dec Orthogonal endpoints
    /// <summary>
    /// GET /api/phd2/calibration/assume-dec-orthogonal - Get assume DEC orthogonal
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/assume-dec-orthogonal")]
    public async Task<ApiResponse> GetAssumeDecOrthogonal()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetAssumeDecOrthogonalAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { AssumeDecOrthogonal = enabled },
                StatusCode = 200,
                Type = "PHD2AssumeDecOrthogonal"
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
    /// PUT /api/phd2/calibration/assume-dec-orthogonal - Set assume DEC orthogonal
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/assume-dec-orthogonal")]
    public async Task<ApiResponse> SetAssumeDecOrthogonal()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAssumeDecOrthogonalAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2AssumeDecOrthogonal = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { AssumeDecOrthogonal = enabled },
                StatusCode = 200,
                Type = "PHD2AssumeDecOrthogonal"
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

    // DEC Compensation endpoints
    /// <summary>
    /// GET /api/phd2/calibration/use-dec-compensation - Get use DEC compensation
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/calibration/use-dec-compensation")]
    public async Task<ApiResponse> GetUseDecCompensation()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetUseDecCompensationAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { UseDecCompensation = enabled },
                StatusCode = 200,
                Type = "PHD2UseDecCompensation"
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
    /// PUT /api/phd2/calibration/use-dec-compensation - Set use DEC compensation
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/calibration/use-dec-compensation")]
    public async Task<ApiResponse> SetUseDecCompensation()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetUseDecCompensationAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2UseDecCompensation = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { UseDecCompensation = enabled },
                StatusCode = 200,
                Type = "PHD2UseDecCompensation"
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

    // Search Region endpoint
    /// <summary>
    /// GET /api/phd2/tracking/search-region - Get search region
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/search-region")]
    public async Task<ApiResponse> GetSearchRegion()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var pixels = await phd2Service.GetSearchRegionAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { SearchRegion = pixels },
                StatusCode = 200,
                Type = "PHD2SearchRegion"
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
    /// PUT /api/phd2/tracking/search-region - Set search region
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/search-region")]
    public async Task<ApiResponse> SetSearchRegion()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("pixels"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "pixels parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["pixels"].ToString(), out int pixels))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "pixels must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetSearchRegionAsync(pixels);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2SearchRegion = pixels;

            return new ApiResponse
            {
                Success = true,
                Response = new { SearchRegion = pixels },
                StatusCode = 200,
                Type = "PHD2SearchRegion"
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

    // Min Star HFR endpoints
    /// <summary>
    /// GET /api/phd2/tracking/min-star-hfr - Get minimum star HFR
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/min-star-hfr")]
    public async Task<ApiResponse> GetMinStarHFR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var hfr = await phd2Service.GetMinStarHFRAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { MinStarHFR = hfr },
                StatusCode = 200,
                Type = "PHD2MinStarHFR"
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
    /// PUT /api/phd2/tracking/min-star-hfr - Set minimum star HFR
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/min-star-hfr")]
    public async Task<ApiResponse> SetMinStarHFR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("hfr"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "hfr parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["hfr"].ToString(), out double hfr))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "hfr must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetMinStarHFRAsync(hfr);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MinStarHFD = hfr;

            return new ApiResponse
            {
                Success = true,
                Response = new { MinStarHFR = hfr },
                StatusCode = 200,
                Type = "PHD2MinStarHFR"
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

    // Max Star HFR endpoints
    /// <summary>
    /// GET /api/phd2/tracking/max-star-hfr - Get maximum star HFR
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/max-star-hfr")]
    public async Task<ApiResponse> GetMaxStarHFR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var hfr = await phd2Service.GetMaxStarHFRAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { MaxStarHFR = hfr },
                StatusCode = 200,
                Type = "PHD2MaxStarHFR"
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
    /// PUT /api/phd2/tracking/max-star-hfr - Set maximum star HFR
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/max-star-hfr")]
    public async Task<ApiResponse> SetMaxStarHFR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("hfr"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "hfr parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["hfr"].ToString(), out double hfr))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "hfr must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetMaxStarHFRAsync(hfr);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MaxStarHFD = hfr;

            return new ApiResponse
            {
                Success = true,
                Response = new { MaxStarHFR = hfr },
                StatusCode = 200,
                Type = "PHD2MaxStarHFR"
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

    // Beep for Lost Star endpoints
    /// <summary>
    /// GET /api/phd2/tracking/beep-for-lost-star - Get beep for lost star
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/beep-for-lost-star")]
    public async Task<ApiResponse> GetBeepForLostStar()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetBeepForLostStarAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { BeepForLostStar = enabled },
                StatusCode = 200,
                Type = "PHD2BeepForLostStar"
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
    /// PUT /api/phd2/tracking/beep-for-lost-star - Set beep for lost star
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/beep-for-lost-star")]
    public async Task<ApiResponse> SetBeepForLostStar()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetBeepForLostStarAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2BeepForLostStar = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { BeepForLostStar = enabled },
                StatusCode = 200,
                Type = "PHD2BeepForLostStar"
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

    // Mass Change Threshold endpoints
    /// <summary>
    /// GET /api/phd2/tracking/star-mass-detection - Get mass change threshold enabled
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/star-mass-detection")]
    public async Task<ApiResponse> GetMassChangeThresholdEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetMassChangeThresholdEnabledAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { MassChangeThresholdEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2MassChangeThresholdEnabled"
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
    /// PUT /api/phd2/tracking/star-mass-detection - Set mass change threshold enabled
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/star-mass-detection")]
    public async Task<ApiResponse> SetMassChangeThresholdEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetMassChangeThresholdEnabledAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MassChangeThresholdEnabled = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { MassChangeThresholdEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2MassChangeThresholdEnabled"
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
    /// GET /api/phd2/tracking/star-mass-detection/threshold - Get mass change threshold
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/star-mass-detection/threshold")]
    public async Task<ApiResponse> GetMassChangeThreshold()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var threshold = await phd2Service.GetMassChangeThresholdAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { MassChangeThreshold = threshold * 100d },
                StatusCode = 200,
                Type = "PHD2MassChangeThreshold"
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
    /// PUT /api/phd2/tracking/star-mass-detection/threshold - Set mass change threshold
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/star-mass-detection/threshold")]
    public async Task<ApiResponse> SetMassChangeThreshold()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("threshold"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "threshold parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["threshold"].ToString(), out double threshold))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "threshold must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetMassChangeThresholdAsync(threshold / 100d);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2MassChangeThreshold = threshold / 100d;

            return new ApiResponse
            {
                Success = true,
                Response = new { MassChangeThreshold = threshold },
                StatusCode = 200,
                Type = "PHD2MassChangeThreshold"
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

    // AF Min Star SNR endpoints
    /// <summary>
    /// GET /api/phd2/tracking/min-star-snr - Get AutoFind minimum star SNR
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/min-star-snr")]
    public async Task<ApiResponse> GetAFMinStarSNR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var snr = await phd2Service.GetAFMinStarSNRAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { AFMinStarSNR = snr },
                StatusCode = 200,
                Type = "PHD2AFMinStarSNR"
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
    /// PUT /api/phd2/tracking/min-star-snr - Set AutoFind minimum star SNR
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/min-star-snr")]
    public async Task<ApiResponse> SetAFMinStarSNR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("snr"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "snr parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["snr"].ToString(), out double snr))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "snr must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAFMinStarSNRAsync(snr);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2AfMinStarSnr = snr;

            return new ApiResponse
            {
                Success = true,
                Response = new { AFMinStarSNR = snr },
                StatusCode = 200,
                Type = "PHD2AFMinStarSNR"
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

    // Use Multiple Stars endpoints
    /// <summary>
    /// GET /api/phd2/tracking/multistar - Get use multiple stars
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/multistar")]
    public async Task<ApiResponse> GetUseMultipleStars()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetUseMultipleStarsAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { UseMultipleStars = enabled },
                StatusCode = 200,
                Type = "PHD2UseMultipleStars"
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
    /// PUT /api/phd2/tracking/multistar - Set use multiple stars
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/multistar")]
    public async Task<ApiResponse> SetUseMultipleStars()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetUseMultipleStarsAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2UseMultipleStars = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { UseMultipleStars = enabled },
                StatusCode = 200,
                Type = "PHD2UseMultipleStars"
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

    // Auto Select Downsample endpoints
    /// <summary>
    /// GET /api/phd2/tracking/downsample - Get auto select downsample
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/tracking/downsample")]
    public async Task<ApiResponse> GetAutoSelectDownsample()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var value = await phd2Service.GetAutoSelectDownsampleAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { AutoSelectDownsample = value },
                StatusCode = 200,
                Type = "PHD2AutoSelectDownsample"
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
    /// PUT /api/phd2/tracking/downsample - Set auto select downsample
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/tracking/downsample")]
    public async Task<ApiResponse> SetAutoSelectDownsample()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("value"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "value parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string value = requestData["value"].ToString();
            if (value != "Auto" && value != "1" && value != "2" && value != "3")
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "value must be 'Auto', '1', '2', or '3'",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAutoSelectDownsampleAsync(value);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2AutoSelectDownsample = value;

            return new ApiResponse
            {
                Success = true,
                Response = new { AutoSelectDownsample = value },
                StatusCode = 200,
                Type = "PHD2AutoSelectDownsample"
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

    // Always Scale Images endpoints
    /// <summary>
    /// GET /api/phd2/always-scale-images - Get always scale images
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/always-scale-images")]
    public async Task<ApiResponse> GetAlwaysScaleImages()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetAlwaysScaleImagesAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { AlwaysScaleImages = enabled },
                StatusCode = 200,
                Type = "PHD2AlwaysScaleImages"
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
    /// PUT /api/phd2/always-scale-images - Set always scale images
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/always-scale-images")]
    public async Task<ApiResponse> SetAlwaysScaleImages()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAlwaysScaleImagesAsync(enabled);

            return new ApiResponse
            {
                Success = true,
                Response = new { AlwaysScaleImages = enabled },
                StatusCode = 200,
                Type = "PHD2AlwaysScaleImages"
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

    // Reverse DEC on Flip endpoints
    /// <summary>
    /// GET /api/phd2/reverse-dec-after-flip - Get reverse DEC after flip
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/reverse-dec-after-flip")]
    public async Task<ApiResponse> GetReverseDecAfterFlip()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetReverseDecAfterFlipAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { ReverseDecAfterFlip = enabled },
                StatusCode = 200,
                Type = "PHD2ReverseDecAfterFlip"
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
    /// PUT /api/phd2/reverse-dec-after-flip - Set reverse DEC after flip
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/reverse-dec-after-flip")]
    public async Task<ApiResponse> SetReverseDecAfterFlip()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetReverseDecAfterFlipAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2ReverseDecOnFlip = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { ReverseDecAfterFlip = enabled },
                StatusCode = 200,
                Type = "PHD2ReverseDecAfterFlip"
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

    // Fast Recenter endpoints
    /// <summary>
    /// GET /api/phd2/fast-recenter - Get fast recenter enabled
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/fast-recenter")]
    public async Task<ApiResponse> GetFastRecenterEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetFastRecenterEnabledAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { FastRecenterEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2FastRecenterEnabled"
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
    /// PUT /api/phd2/fast-recenter - Set fast recenter enabled
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/fast-recenter")]
    public async Task<ApiResponse> SetFastRecenterEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetFastRecenterEnabledAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2FastRecenter = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { FastRecenterEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2FastRecenterEnabled"
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

    // Mount Guide Output endpoints
    /// <summary>
    /// GET /api/phd2/mount-guide-output - Get mount guide output enabled
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/mount-guide-output")]
    public async Task<ApiResponse> GetMountGuideOutputEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetMountGuideOutputEnabledAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { MountGuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2MountGuideOutputEnabled"
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
    /// PUT /api/phd2/mount-guide-output - Set mount guide output enabled
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/mount-guide-output")]
    public async Task<ApiResponse> SetMountGuideOutputEnabled()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetMountGuideOutputEnabledAsync(enabled);

            return new ApiResponse
            {
                Success = true,
                Response = new { MountGuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2MountGuideOutputEnabled"
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

    // Camera control endpoints

    /// <summary>
    /// GET /api/phd2/guide/algorithm-ra - Get guide algorithm for RA axis
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/guide/algorithm-ra")]
    public async Task<ApiResponse> GetGuideAlgorithmRA()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var algorithm = await phd2Service.GetGuideAlgorithmRAAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { GuideAlgorithmRA = algorithm },
                StatusCode = 200,
                Type = "PHD2GuideAlgorithmRA"
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
    /// PUT /api/phd2/guide/algorithm-ra - Set guide algorithm for RA axis
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/guide/algorithm-ra")]
    public async Task<ApiResponse> SetGuideAlgorithmRA()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("algorithm"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "algorithm parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            var algorithm = requestData["algorithm"].ToString();
            // Allowed display names
            var allowed = new[] { "None", "Hysteresis", "Lowpass", "Lowpass2", "Resist Switch", "Predictive PEC", "ZFilter" };
            if (!(algorithm == "None" || algorithm == "Hysteresis" || algorithm == "Lowpass" || algorithm == "Lowpass2" || algorithm == "Resist Switch" || algorithm == "Predictive PEC" || algorithm.StartsWith("ZFilter")))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "algorithm must be one of: None, Hysteresis, Lowpass, Lowpass2, Resist Switch, Predictive PEC, ZFilter*",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetGuideAlgorithmRAAsync(algorithm);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2GuideAlgorithmRA = algorithm;
            return new ApiResponse
            {
                Success = true,
                Response = new { GuideAlgorithmRA = algorithm },
                StatusCode = 200,
                Type = "PHD2GuideAlgorithmRA"
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
    /// GET /api/phd2/guide/algorithm-dec - Get guide algorithm for DEC axis
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/guide/algorithm-dec")]
    public async Task<ApiResponse> GetGuideAlgorithmDEC()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var algorithm = await phd2Service.GetGuideAlgorithmDECAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { GuideAlgorithmDEC = algorithm },
                StatusCode = 200,
                Type = "PHD2GuideAlgorithmDEC"
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
    /// PUT /api/phd2/guide/algorithm-dec - Set guide algorithm for DEC axis
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/guide/algorithm-dec")]
    public async Task<ApiResponse> SetGuideAlgorithmDEC()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("algorithm"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "algorithm parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            var algorithm = requestData["algorithm"].ToString();
            // Allowed display names
            var allowed = new[] { "None", "Hysteresis", "Lowpass", "Lowpass2", "Resist Switch", "Predictive PEC", "ZFilter" };
            if (!(algorithm == "None" || algorithm == "Hysteresis" || algorithm == "Lowpass" || algorithm == "Lowpass2" || algorithm == "Resist Switch" || algorithm == "Predictive PEC" || algorithm.StartsWith("ZFilter")))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "algorithm must be one of: None, Hysteresis, Lowpass, Lowpass2, Resist Switch, Predictive PEC, ZFilter*",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetGuideAlgorithmDECAsync(algorithm);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2GuideAlgorithmDec = algorithm;
            return new ApiResponse
            {
                Success = true,
                Response = new { GuideAlgorithmDEC = algorithm },
                StatusCode = 200,
                Type = "PHD2GuideAlgorithmDEC"
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
    /// GET /api/phd2/camera/saturation-by-adu - Get saturation by ADU
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/saturation-by-adu")]
    public async Task<ApiResponse> GetSaturationByADU()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var byADU = await phd2Service.GetSaturationByADUAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { SaturationByADU = byADU },
                StatusCode = 200,
                Type = "PHD2SaturationByADU"
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
    /// PUT /api/phd2/camera/saturation-by-adu - Set saturation by ADU
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/saturation-by-adu")]
    public async Task<ApiResponse> SetSaturationByADU()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("by_adu"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "by_adu parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!bool.TryParse(requestData["by_adu"].ToString(), out bool byADU))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "by_adu must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            int? aduValue = null;
            if (byADU && requestData.ContainsKey("adu_value"))
            {
                if (!int.TryParse(requestData["adu_value"].ToString(), out int adu))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "adu_value must be a valid integer",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
                aduValue = adu;
            }
            await phd2Service.SetSaturationByADUAsync(byADU, aduValue);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2SaturationByADU = byADU;
            if (aduValue.HasValue)
                TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2SaturationADUValue = aduValue.Value;
            return new ApiResponse
            {
                Success = true,
                Response = new { SaturationByADU = byADU, ADUValue = aduValue },
                StatusCode = 200,
                Type = "PHD2SaturationByADU"
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
    /// GET /api/phd2/camera/saturation-adu-value - Get saturation ADU value
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/saturation-adu-value")]
    public async Task<ApiResponse> GetSaturationADUValue()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var aduValue = await phd2Service.GetSaturationADUValueAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { SaturationADUValue = aduValue },
                StatusCode = 200,
                Type = "PHD2SaturationADUValue"
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
    /// PUT /api/phd2/camera/saturation-adu-value - Set saturation ADU value
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/saturation-adu-value")]
    public async Task<ApiResponse> SetSaturationADUValue()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("adu_value"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "adu_value parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!int.TryParse(requestData["adu_value"].ToString(), out int aduValue))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "adu_value must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetSaturationADUValueAsync(aduValue);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2SaturationByADU = true;
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2SaturationADUValue = aduValue;
            return new ApiResponse
            {
                Success = true,
                Response = new { SaturationADUValue = aduValue },
                StatusCode = 200,
                Type = "PHD2SaturationADUValue"
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
    /// GET /api/phd2/guide/dither-mode - Get dither mode
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/guide/dither-mode")]
    public async Task<ApiResponse> GetDitherMode()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var mode = await phd2Service.GetDitherModeAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherMode = mode },
                StatusCode = 200,
                Type = "PHD2DitherMode"
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
    /// PUT /api/phd2/guide/dither-mode - Set dither mode
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/guide/dither-mode")]
    public async Task<ApiResponse> SetDitherMode()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("mode"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "mode parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            var mode = requestData["mode"].ToString();
            if (mode != "random" && mode != "spiral")
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "mode must be 'random' or 'spiral'",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetDitherModeAsync(mode);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2DitherMode = mode;
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherMode = mode },
                StatusCode = 200,
                Type = "PHD2DitherMode"
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
    /// GET /api/phd2/dither-ra-only - Get dither RA only
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/guide/dither-ra-only")]
    public async Task<ApiResponse> GetDitherRaOnly()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var raOnly = await phd2Service.GetDitherRaOnlyAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherRaOnly = raOnly },
                StatusCode = 200,
                Type = "PHD2DitherRaOnly"
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
    /// PUT /api/phd2/guide/dither-ra-only - Set dither RA only
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/guide/dither-ra-only")]
    public async Task<ApiResponse> SetDitherRaOnly()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("ra_only"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "ra_only parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!bool.TryParse(requestData["ra_only"].ToString(), out bool raOnly))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "ra_only must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetDitherRaOnlyAsync(raOnly);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2DitherRAOnly = raOnly;
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherRaOnly = raOnly },
                StatusCode = 200,
                Type = "PHD2DitherRaOnly"
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
    /// GET /api/phd2/dither-scale - Get dither scale
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/guide/dither-scale")]
    public async Task<ApiResponse> GetDitherScale()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var scale = await phd2Service.GetDitherScaleAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherScale = scale },
                StatusCode = 200,
                Type = "PHD2DitherScale"
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
    /// PUT /api/phd2/dither-scale - Set dither scale
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/guide/dither-scale")]
    public async Task<ApiResponse> SetDitherScale()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("scale"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "scale parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!double.TryParse(requestData["scale"].ToString(), out double scale))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "scale must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetDitherScaleAsync(scale);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2DitherScale = scale;
            return new ApiResponse
            {
                Success = true,
                Response = new { DitherScale = scale },
                StatusCode = 200,
                Type = "PHD2DitherScale"
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
    /// GET /api/phd2/auto/exposure-min - Get auto exposure min
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/auto/exposure-min")]
    public async Task<ApiResponse> GetAutoExposureMin()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var min = await phd2Service.GetAutoExposureMinAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureMin = min },
                StatusCode = 200,
                Type = "PHD2AutoExposureMin"
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
    /// PUT /api/phd2/auto/exposure-min - Set auto exposure min
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/auto/exposure-min")]
    public async Task<ApiResponse> SetAutoExposureMin()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("exposure"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposure parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!double.TryParse(requestData["exposure"].ToString(), out double min))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposure must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetAutoExposureMinAsync(min);
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureMin = min },
                StatusCode = 200,
                Type = "PHD2AutoExposureMin"
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
    /// GET /api/phd2/auto-exposure-max - Get auto exposure max
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/auto/exposure-max")]
    public async Task<ApiResponse> GetAutoExposureMax()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var max = await phd2Service.GetAutoExposureMaxAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureMax = max },
                StatusCode = 200,
                Type = "PHD2AutoExposureMax"
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
    /// PUT /api/phd2/auto-exposure-max - Set auto exposure max
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/auto/exposure-max")]
    public async Task<ApiResponse> SetAutoExposureMax()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("exposure"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposure parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!double.TryParse(requestData["exposure"].ToString(), out double max))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "exposure must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetAutoExposureMaxAsync(max);
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureMax = max },
                StatusCode = 200,
                Type = "PHD2AutoExposureMax"
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
    /// GET /api/phd2/auto/target-snr - Get auto exposure target SNR
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/auto/target-snr")]
    public async Task<ApiResponse> GetAutoExposureTargetSNR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var snr = await phd2Service.GetAutoExposureTargetSNRAsync();
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureTargetSNR = snr },
                StatusCode = 200,
                Type = "PHD2AutoExposureTargetSNR"
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
    /// PUT /api/phd2/auto/target-snr - Set auto exposure target SNR
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/auto/target-snr")]
    public async Task<ApiResponse> SetAutoExposureTargetSNR()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("target_snr"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "target_snr parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            if (!double.TryParse(requestData["target_snr"].ToString(), out double snr))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "target_snr must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }
            await phd2Service.SetAutoExposureTargetSNRAsync(snr);
            return new ApiResponse
            {
                Success = true,
                Response = new { AutoExposureTargetSNR = snr },
                StatusCode = 200,
                Type = "PHD2AutoExposureTargetSNR"
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
    /// GET /api/phd2/camera/gain - Get camera gain
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/gain")]
    public async Task<ApiResponse> GetCameraGain()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var gain = await phd2Service.GetCameraGainAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraGain = gain },
                StatusCode = 200,
                Type = "PHD2CameraGain"
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
    /// PUT /api/phd2/camera/gain - Set camera gain
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/gain")]
    public async Task<ApiResponse> SetCameraGain()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("gain"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "gain parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["gain"].ToString(), out int gain))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "gain must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCameraGainAsync(gain);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2CameraGain = gain;

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraGain = gain },
                StatusCode = 200,
                Type = "PHD2CameraGain"
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
    /// GET /api/phd2/camera/cooler-on - Get camera cooler status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/cooler-on")]
    public async Task<ApiResponse> GetCameraCoolerOn()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var coolerOn = await phd2Service.GetCameraCoolerOnAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraCoolerOn = coolerOn },
                StatusCode = 200,
                Type = "PHD2CameraCoolerOn"
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
    /// PUT /api/phd2/camera/cooler-on - Set camera cooler status
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/cooler-on")]
    public async Task<ApiResponse> SetCameraCoolerOn()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCameraCoolerOnAsync(enabled);

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraCoolerOn = enabled },
                StatusCode = 200,
                Type = "PHD2CameraCoolerOn"
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
    /// GET /api/phd2/camera/temperature-setpoint - Get camera temperature setpoint
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/temperature-setpoint")]
    public async Task<ApiResponse> GetCameraTemperatureSetpoint()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var temperature = await phd2Service.GetCameraTemperatureSetpointAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraTemperatureSetpoint = temperature },
                StatusCode = 200,
                Type = "PHD2CameraTemperatureSetpoint"
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
    /// PUT /api/phd2/camera/temperature-setpoint - Set camera temperature setpoint
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/temperature-setpoint")]
    public async Task<ApiResponse> SetCameraTemperatureSetpoint()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("temperature"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "temperature parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["temperature"].ToString(), out double temperature))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "temperature must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCameraTemperatureSetpointAsync(temperature);

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraTemperatureSetpoint = temperature },
                StatusCode = 200,
                Type = "PHD2CameraTemperatureSetpoint"
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
    /// GET /api/phd2/camera/use-subframes - Get camera use subframes setting
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/use-subframes")]
    public async Task<ApiResponse> GetCameraUseSubframes()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var useSubframes = await phd2Service.GetCameraUseSubframesAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraUseSubframes = useSubframes },
                StatusCode = 200,
                Type = "PHD2CameraUseSubframes"
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
    /// PUT /api/phd2/camera/use-subframes - Set camera use subframes setting
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/use-subframes")]
    public async Task<ApiResponse> SetCameraUseSubframes()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCameraUseSubframesAsync(enabled);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2UseSubframes = enabled;

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraUseSubframes = enabled },
                StatusCode = 200,
                Type = "PHD2CameraUseSubframes"
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
    /// GET /api/phd2/camera/binning - Get camera binning setting
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/camera/binning")]
    public async Task<ApiResponse> GetCameraBinning()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var binning = await phd2Service.GetCameraBinningAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraBinning = binning },
                StatusCode = 200,
                Type = "PHD2CameraBinning"
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
    /// PUT /api/phd2/camera/binning - Set camera binning setting
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/camera/binning")]
    public async Task<ApiResponse> SetCameraBinning()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("binning"))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "binning parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["binning"].ToString(), out int binning))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "binning must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetCameraBinningAsync(binning);
            TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2CameraBinning = binning;

            return new ApiResponse
            {
                Success = true,
                Response = new { CameraBinning = binning },
                StatusCode = 200,
                Type = "PHD2CameraBinning"
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
    /// GET /api/phd2/backlash/comp - Get backlash compensation settings
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/backlash/comp")]
    public async Task<ApiResponse> GetBacklashComp()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var (enabled, pulseWidth, floor, ceiling) = await phd2Service.GetBacklashCompAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Enabled = enabled, PulseWidth = pulseWidth, Floor = floor, Ceiling = ceiling },
                StatusCode = 200,
                Type = "PHD2BacklashComp"
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
    /// PUT /api/phd2/backlash/comp - Set backlash compensation settings
    /// </summary>
    [Route(HttpVerbs.Put, "/phd2/backlash/comp")]
    public async Task<ApiResponse> SetBacklashComp()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Request body is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool? enabled = null;
            int? pulseWidth = null;
            int? floor = null;
            int? ceiling = null;

            if (requestData.ContainsKey("enabled") && requestData["enabled"] != null)
            {
                if (!bool.TryParse(requestData["enabled"].ToString(), out bool e))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "enabled must be a valid boolean", StatusCode = 400, Type = "Error" };
                }
                enabled = e;
            }

            if (requestData.ContainsKey("pulseWidth") && requestData["pulseWidth"] != null)
            {
                if (!int.TryParse(requestData["pulseWidth"].ToString(), out int pw))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "pulseWidth must be a valid integer", StatusCode = 400, Type = "Error" };
                }
                pulseWidth = pw;
            }

            if (requestData.ContainsKey("floor") && requestData["floor"] != null)
            {
                if (!int.TryParse(requestData["floor"].ToString(), out int f))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "floor must be a valid integer", StatusCode = 400, Type = "Error" };
                }
                floor = f;
            }

            if (requestData.ContainsKey("ceiling") && requestData["ceiling"] != null)
            {
                if (!int.TryParse(requestData["ceiling"].ToString(), out int c))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "ceiling must be a valid integer", StatusCode = 400, Type = "Error" };
                }
                ceiling = c;
            }

            await phd2Service.SetBacklashCompAsync(enabled, pulseWidth, floor, ceiling);

            if (enabled.HasValue)
                TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2BacklashCompEnabled = enabled.Value;
            if (pulseWidth.HasValue)
                TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2BacklashPulseWidth = pulseWidth.Value;
            if (floor.HasValue)
                TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2BacklashFloor = floor.Value;
            if (ceiling.HasValue)
                TouchNStars.Mediators.Profile.ActiveProfile.GuiderSettings.PHD2BacklashCeiling = ceiling.Value;

            return new ApiResponse
            {
                Success = true,
                Response = new { Enabled = enabled, PulseWidth = pulseWidth, Floor = floor, Ceiling = ceiling },
                StatusCode = 200,
                Type = "PHD2BacklashComp"
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
