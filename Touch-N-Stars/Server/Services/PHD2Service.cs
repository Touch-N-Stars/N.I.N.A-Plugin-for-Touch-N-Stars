using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using TouchNStars.PHD2;

namespace TouchNStars.Server.Services
{
    public class PHD2Service : IDisposable
    {
        private PHD2Client client;
        private readonly object lockObject = new object();
        private string lastError;
        private Task<bool> currentConnectionTask = null;

        private async Task WaitForConnectionIfNeeded()
        {
            Task<bool> connectionTask = null;
            lock (lockObject)
            {
                if (currentConnectionTask != null && !currentConnectionTask.IsCompleted)
                {
                    connectionTask = currentConnectionTask;
                }
            }

            if (connectionTask != null)
            {
                Logger.Debug("Waiting for ongoing PHD2 connection");
                await connectionTask;
            }
        }

        public async Task WaitForConnectionAsync()
        {
            await WaitForConnectionIfNeeded();
        }

        public bool IsConnected => client?.IsConnected ?? false;
        public string LastError => lastError;

        public PHD2Service()
        {
            client = new PHD2Client();
        }

        public async Task<bool> ConnectAsync(string hostname = "localhost", uint instance = 1)
        {
            lock (lockObject)
            {
                if (client?.IsConnected == true)
                {
                    Logger.Debug("PHD2 already connected, skipping connection attempt");
                    return true;
                }

                if (currentConnectionTask != null && !currentConnectionTask.IsCompleted)
                {
                    Logger.Debug("PHD2 connection already in progress, waiting for existing attempt");
                }
                else
                {
                    currentConnectionTask = ConnectInternalAsync(hostname, instance);
                }
            }

            return await currentConnectionTask;
        }

        private async Task<bool> ConnectInternalAsync(string hostname, uint instance)
        {
            return await Task.Run(() =>
            {
                const int maxRetries = 3;
                int attemptCount = 0;

                while (attemptCount < maxRetries)
                {
                    attemptCount++;

                    try
                    {
                        lock (lockObject)
                        {
                            Logger.Info($"PHD2 connection attempt {attemptCount}/{maxRetries} to {hostname}, instance {instance}");

                            // Defensive disconnect of existing connection
                            if (client != null && client.IsConnected)
                            {
                                try
                                {
                                    Logger.Debug("Gracefully disconnecting existing PHD2 client");
                                    client.Disconnect();
                                }
                                catch (Exception disconnectEx)
                                {
                                    Logger.Warning($"Error during graceful disconnect: {disconnectEx.Message}");
                                }
                            }

                            ushort port = (ushort)(4400 + instance - 1);

                            Logger.Debug($"Creating PHD2 client for {hostname}:{port}");
                            client = new PHD2Client(hostname, instance);

                            Logger.Debug("Attempting PHD2 client connection");
                            client.Connect();

                            if (client.IsConnected)
                            {
                                Logger.Info($"PHD2 connection successful on attempt {attemptCount}");
                                lastError = null;
                                return true;
                            }
                            else
                            {
                                throw new InvalidOperationException("Client reports not connected after Connect() call");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string errorMsg = $"PHD2 connection attempt {attemptCount} failed: {ex.Message}";
                        Logger.Warning(errorMsg);

                        if (attemptCount < maxRetries)
                        {
                            Logger.Info($"Retrying PHD2 connection in {attemptCount * 1000}ms...");
                            System.Threading.Thread.Sleep(attemptCount * 1000); // Progressive backoff
                        }
                        else
                        {
                            lastError = $"Failed to connect to PHD2 at {hostname}:{4400 + instance - 1} after {maxRetries} attempts: {ex.Message}";
                            Logger.Error($"All PHD2 connection attempts failed: {ex}");
                        }
                    }
                }

                lock (lockObject)
                {
                    currentConnectionTask = null;
                }
                return false;
            });
        }

        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        client?.Disconnect();
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Error disconnecting from PHD2: {ex}");
                }
            });
        }

        public async Task<bool> StartGuidingAsync(double settlePixels = 2.0, double settleTime = 10.0, double settleTimeout = 100.0)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Guide(settlePixels, settleTime, settleTimeout);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to start guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> StopGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.StopCapture();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to stop guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> DitherAsync(double ditherPixels = 3.0, double settlePixels = 2.0, double settleTime = 10.0, double settleTimeout = 100.0)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Dither(ditherPixels, settlePixels, settleTime, settleTimeout);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to dither: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> PauseGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Pause();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to pause guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> UnpauseGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Unpause();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to unpause guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> StartLoopingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Loop();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to start looping: {ex}");
                    return false;
                }
            });
        }

        public async Task<System.Collections.Generic.List<string>> GetEquipmentProfilesAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return new System.Collections.Generic.List<string>();
                        }

                        var profiles = client.GetEquipmentProfiles();
                        lastError = null;
                        return profiles;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get equipment profiles: {ex}");
                    return new System.Collections.Generic.List<string>();
                }
            });
        }

        public async Task<int> CreateProfileAsync(string profileName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return -1;
                        }

                        var profileId = client.CreateProfile(profileName);
                        lastError = null;
                        Logger.Info($"Created PHD2 profile '{profileName}' with ID {profileId}");
                        return profileId;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to create profile '{profileName}': {ex}");
                    return -1;
                }
            });
        }

        public async Task<bool> DeleteProfileAsync(string profileName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.DeleteProfile(profileName);
                        lastError = null;
                        Logger.Info($"Deleted PHD2 profile '{profileName}'");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to delete profile '{profileName}': {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> RenameProfileAsync(string newName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.RenameProfile(newName);
                        lastError = null;
                        Logger.Info($"Renamed PHD2 profile to '{newName}'");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to rename profile to '{newName}': {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> SetProfileAsync(int profileId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.SetProfile(profileId);
                        lastError = null;
                        Logger.Info($"Selected PHD2 profile ID {profileId}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set profile to ID {profileId}: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> ConnectEquipmentAsync(string profileName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.ConnectEquipment(profileName);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to connect equipment: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> DisconnectEquipmentAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.DisconnectEquipment();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to disconnect equipment: {ex}");
                    return false;
                }
            });
        }

        public async Task<PHD2Status> GetStatusAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            return new PHD2Status
                            {
                                IsConnected = false,
                                AppState = "Disconnected"
                            };
                        }

                        var status = client.GetStatus();
                        lastError = null;
                        return status;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get PHD2 status: {ex}");
                    return new PHD2Status
                    {
                        IsConnected = false,
                        AppState = "Error"
                    };
                }
            });
        }

        public async Task<SettleProgress> CheckSettlingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return null;
                        }

                        if (!client.IsSettling())
                        {
                            return new SettleProgress { Done = true };
                        }

                        var progress = client.CheckSettling();
                        lastError = null;
                        return progress;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to check settling: {ex}");
                    return null;
                }
            });
        }

        public async Task<double> GetPixelScaleAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetPixelScale();
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Pixel scale not available"))
                {
                    // This is expected when no camera is connected or calibration hasn't been done
                    // Log at Debug level to avoid spam in normal operation
                    Logger.Debug($"Pixel scale not available: {ex.Message}");
                    return 0.0;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get pixel scale: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task<int> GetFocalLengthAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetFocalLength();
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Focal length not available"))
                {
                    Logger.Debug($"Focal length not available: {ex.Message}");
                    return 0;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get focal length: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetFocalLengthAsync(int focalLength)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetFocalLength(focalLength);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set focal length: {ex}");
                    throw;
                }
            });
        }

        // Calibration methods
        public async Task<int> GetCalibrationStepAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCalibrationStep();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get calibration step: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetCalibrationStepAsync(int step)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCalibrationStep(step);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set calibration step: {ex}");
                    throw;
                }
            });
        }

        public async Task<int> GetCalibrationDistanceAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCalibrationDistance();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get calibration distance: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetCalibrationDistanceAsync(int distance)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCalibrationDistance(distance);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set calibration distance: {ex}");
                    throw;
                }
            });
        }

        public async Task ClearMountCalibrationAsync()
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.ClearMountCalibration();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to clear mount calibration: {ex}");
                    throw;
                }
            });
        }

        // Auto-restore calibration methods
        public async Task<bool> GetAutoRestoreCalibrationsAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAutoRestoreCalibration();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get auto restore calibration: {ex}");
                    return false;
                }
            });
        }

        public async Task SetAutoRestoreCalibrationsAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetAutoRestoreCalibration(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set auto restore calibration: {ex}");
                    throw;
                }
            });
        }

        // Orthogonal assumption methods
        public async Task<bool> GetAssumeDecOrthogonalAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAssumeDecOrthogonal();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get assume dec orthogonal: {ex}");
                    return false;
                }
            });
        }

        public async Task SetAssumeDecOrthogonalAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetAssumeDecOrthogonal(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set assume dec orthogonal: {ex}");
                    throw;
                }
            });
        }

        // DEC compensation methods
        public async Task<bool> GetUseDecCompensationAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetUseDecCompensation();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get use dec compensation: {ex}");
                    return false;
                }
            });
        }

        public async Task SetUseDecCompensationAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetUseDecCompensation(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set use dec compensation: {ex}");
                    throw;
                }
            });
        }

        // Search region method
        public async Task<int> GetSearchRegionAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetSearchRegion();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get search region: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetSearchRegionAsync(int pixels)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetSearchRegion(pixels);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set search region: {ex}");
                    throw;
                }
            });
        }

        // HFR threshold methods
        public async Task<double> GetMinStarHFRAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetMinStarHFR();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get min star HFR: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetMinStarHFRAsync(double hfr)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetMinStarHFR(hfr);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set min star HFR: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetMaxStarHFRAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetMaxStarHFR();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get max star HFR: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetMaxStarHFRAsync(double hfr)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetMaxStarHFR(hfr);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set max star HFR: {ex}");
                    throw;
                }
            });
        }

        // Lost star beep method
        public async Task<bool> GetBeepForLostStarAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetBeepForLostStar();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get beep for lost star: {ex}");
                    return false;
                }
            });
        }

        public async Task SetBeepForLostStarAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetBeepForLostStar(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set beep for lost star: {ex}");
                    throw;
                }
            });
        }

        // Mass change threshold methods
        public async Task<bool> GetMassChangeThresholdEnabledAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetMassChangeThresholdEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get mass change threshold enabled: {ex}");
                    return false;
                }
            });
        }

        public async Task SetMassChangeThresholdEnabledAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetMassChangeThresholdEnabled(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set mass change threshold enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetMassChangeThresholdAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetMassChangeThreshold();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get mass change threshold: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetMassChangeThresholdAsync(double threshold)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetMassChangeThreshold(threshold);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set mass change threshold: {ex}");
                    throw;
                }
            });
        }

        // AF SNR method
        public async Task<double> GetAFMinStarSNRAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAFMinStarSNR();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get AF min star SNR: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetAFMinStarSNRAsync(double snr)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetAFMinStarSNR(snr);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set AF min star SNR: {ex}");
                    throw;
                }
            });
        }

        // Multi-star mode method
        public async Task<int> GetTimeLapseAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetTimeLapse();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get time lapse: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetTimeLapseAsync(int ms)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetTimeLapse(ms);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set time lapse: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetUseMultipleStarsAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetUseMultipleStars();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get use multiple stars: {ex}");
                    return false;
                }
            });
        }

        public async Task SetUseMultipleStarsAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetUseMultipleStars(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set use multiple stars: {ex}");
                    throw;
                }
            });
        }

        // Auto-select downsample method
        public async Task<string> GetAutoSelectDownsampleAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAutoSelectDownsample();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get auto select downsample: {ex}");
                    return "Auto";
                }
            });
        }

        public async Task SetAutoSelectDownsampleAsync(string value)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetAutoSelectDownsample(value);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set auto select downsample: {ex}");
                    throw;
                }
            });
        }

        // Image scaling method
        public async Task<bool> GetAlwaysScaleImagesAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAlwaysScaleImages();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get always scale images: {ex}");
                    return false;
                }
            });
        }

        public async Task SetAlwaysScaleImagesAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetAlwaysScaleImages(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set always scale images: {ex}");
                    throw;
                }
            });
        }

        // Meridian flip methods
        public async Task<bool> GetReverseDecAfterFlipAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetReverseDecAfterFlip();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get reverse dec on flip: {ex}");
                    return false;
                }
            });
        }

        public async Task SetReverseDecAfterFlipAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetReverseDecAfterFlip(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set reverse dec on flip: {ex}");
                    throw;
                }
            });
        }

        // Fast recenter methods
        public async Task<bool> GetFastRecenterEnabledAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetFastRecenterEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get fast recenter enabled: {ex}");
                    return false;
                }
            });
        }

        public async Task SetFastRecenterEnabledAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetFastRecenterEnabled(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set fast recenter enabled: {ex}");
                    throw;
                }
            });
        }

        // Mount guide output methods
        public async Task<bool> GetMountGuideOutputEnabledAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetMountGuideOutputEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get mount guide output enabled: {ex}");
                    return false;
                }
            });
        }

        public async Task SetMountGuideOutputEnabledAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetMountGuideOutputEnabled(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set mount guide output enabled: {ex}");
                    throw;
                }
            });
        }

        // Camera control methods
        public async Task<int> GetCameraGainAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCameraGain();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get camera gain: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetCameraGainAsync(int gain)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCameraGain(gain);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set camera gain: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetCameraCoolerOnAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCameraCoolerOn();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get camera cooler on: {ex}");
                    return false;
                }
            });
        }

        public async Task SetCameraCoolerOnAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCameraCoolerOn(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set camera cooler on: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetCameraTemperatureSetpointAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCameraTemperatureSetpoint();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get camera temperature setpoint: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetCameraTemperatureSetpointAsync(double temperature)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCameraTemperatureSetpoint(temperature);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set camera temperature setpoint: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetCameraUseSubframesAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCameraUseSubframes();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get camera use subframes: {ex}");
                    return false;
                }
            });
        }

        public async Task SetCameraUseSubframesAsync(bool enabled)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCameraUseSubframes(enabled);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set camera use subframes: {ex}");
                    throw;
                }
            });
        }

        public async Task<int> GetCameraBinningAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCameraBinning();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get camera binning: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetCameraBinningAsync(int binning)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetCameraBinning(binning);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set camera binning: {ex}");
                    throw;
                }
            });
        }

        // Auto exposure methods
        public async Task<object> GetCameraInfoAsync()
        {
            return await Task.Run(() =>
            {
                lock (lockObject)
                {
                    if (client == null || !client.IsConnected)
                        throw new InvalidOperationException("PHD2 not connected");

                    // Prefer the aggregated RPC added to the local PHD2 build.
                    // Fall back to individual calls when running against an older PHD2.
                    try
                    {
                        var rpc = client.GetCameraInfoRpc();
                        if (rpc != null)
                        {
                            AppendExtraFields(rpc);
                            return (object)rpc;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"get_camera_info RPC unavailable, falling back to individual calls: {ex.Message}");
                    }

                    // Individual-call fallback
                    var info = new Dictionary<string, object>();

                    try
                    {
                        var equipment = client.GetCurrentEquipment() as Dictionary<string, object>;
                        if (equipment != null && equipment.TryGetValue("camera", out var camEntry))
                        {
                            if (camEntry is Dictionary<string, object> cam)
                            {
                                info["name"] = cam.TryGetValue("name", out var n) ? n : null;
                                info["connected"] = cam.TryGetValue("connected", out var c) ? c : false;
                            }
                        }
                    }
                    catch { }

                    if (!info.ContainsKey("name")) info["name"] = null;
                    if (!info.ContainsKey("connected")) info["connected"] = false;

                    try { info["gain_pct"] = client.GetCameraGain(); } catch { info["gain_pct"] = null; }
                    try { info["bits_per_pixel"] = client.GetCameraBitdepth(); } catch { info["bits_per_pixel"] = null; }
                    try { info["binning"] = client.GetCameraBinning(); } catch { info["binning"] = null; }
                    try { info["has_subframes"] = client.GetCameraUseSubframes(); } catch { info["has_subframes"] = null; }

                    try
                    {
                        var fs = client.GetCameraFrameSize();
                        info["frame_width"] = fs?.Width;
                        info["frame_height"] = fs?.Height;
                    }
                    catch { info["frame_width"] = null; info["frame_height"] = null; }

                    try { info["pixel_scale"] = client.GetPixelScale(); } catch { info["pixel_scale"] = null; }
                    try { info["exposure_ms"] = client.GetExposure(); } catch { info["exposure_ms"] = null; }
                    try { info["cooler_on"] = client.GetCameraCoolerOn(); } catch { info["cooler_on"] = null; }
                    try { info["cooler_setpoint"] = client.GetCameraTemperatureSetpoint(); } catch { info["cooler_setpoint"] = null; }

                    AppendExtraFields(info);

                    return (object)info;
                }
            });
        }

        /// <summary>
        /// Appends saturation, time-lapse (variable delay), and noise-reduction
        /// fields to a camera-info dictionary. All fetches are best-effort.
        /// </summary>
        private void AppendExtraFields(Dictionary<string, object> info)
        {
            // Saturation: either by fixed ADU threshold or by star-profile analysis
            try
            {
                bool byAdu = client.GetSaturationByADU();
                info["saturation_by_adu"] = byAdu;
                if (byAdu)
                {
                    int? aduValue = null;
                    try { aduValue = client.GetSaturationADUValue(); info["saturation_adu_value"] = aduValue; } catch { info["saturation_adu_value"] = null; }

                    // Warn when the ADU threshold does not match the camera's expected max ADU
                    // for its bit depth (e.g. 255 on a 16-bit camera whose max is 65535).
                    bool warning = false;
                    if (aduValue.HasValue
                        && info.TryGetValue("bits_per_pixel", out var bppObj)
                        && bppObj != null)
                    {
                        try
                        {
                            int bpp = Convert.ToInt32(bppObj);
                            int maxAdu = (1 << bpp) - 1;
                            warning = aduValue.Value != maxAdu;
                        }
                        catch { }
                    }
                    info["saturation_adu_warning"] = warning;
                }
            }
            catch { info["saturation_by_adu"] = null; }

            // Time lapse: fixed ms delay between exposures (disabled when variable delay is active)
            try { info["time_lapse_ms"] = client.GetTimeLapse(); } catch { info["time_lapse_ms"] = null; }

            // Variable delay: state-dependent short/long delay (mutually exclusive with time lapse)
            try
            {
                var vd = client.GetVariableDelaySettings();
                if (vd != null)
                {
                    bool enabled = vd["Enabled"]?.ToObject<bool>() ?? false;
                    info["variable_delay_enabled"] = enabled;
                    if (enabled)
                    {
                        info["variable_delay_short_sec"] = vd["ShortDelaySeconds"]?.ToObject<int>();
                        info["variable_delay_long_sec"] = vd["LongDelaySeconds"]?.ToObject<int>();
                    }
                }
            }
            catch { info["variable_delay_enabled"] = null; }

            // Noise reduction: 0=None, 1=2x2Mean, 2=3x3Median
            try { info["noise_reduction_method"] = client.GetNoiseReductionMethod(); } catch { info["noise_reduction_method"] = null; }

            // Pixel scale (arcsec/px) — only present in fast-path (get_camera_info RPC) when appended here;
            // the fallback path adds it directly before calling AppendExtraFields.
            if (!info.ContainsKey("pixel_scale"))
                try { info["pixel_scale"] = client.GetPixelScale(); } catch { info["pixel_scale"] = null; }
        }

        // Auto exposure methods
        public async Task<double> GetAutoExposureMinAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetAutoExposureMin();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get auto exposure min: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetAutoExposureMinAsync(double min)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetAutoExposureMin(min);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set auto exposure min: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetAutoExposureMaxAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetAutoExposureMax();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get auto exposure max: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetAutoExposureMaxAsync(double max)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetAutoExposureMax(max);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set auto exposure max: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetAutoExposureTargetSNRAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetAutoExposureTargetSNR();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get auto exposure target SNR: {ex}");
                    return 0.0;
                }
            });
        }

        public async Task SetAutoExposureTargetSNRAsync(double snr)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetAutoExposureTargetSNR(snr);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set auto exposure target SNR: {ex}");
                    throw;
                }
            });
        }

        // Dither mode
        public async Task<string> GetDitherModeAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetDitherMode();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get dither mode: {ex}");
                    return "random";
                }
            });
        }

        public async Task SetDitherModeAsync(string mode)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetDitherMode(mode);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set dither mode: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetDitherRaOnlyAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetDitherRaOnly();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get dither ra_only: {ex}");
                    return false;
                }
            });
        }

        public async Task SetDitherRaOnlyAsync(bool raOnly)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetDitherRaOnly(raOnly);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set dither ra_only: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetDitherScaleAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetDitherScale();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get dither scale: {ex}");
                    return 1.0;
                }
            });
        }

        public async Task SetDitherScaleAsync(double scale)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetDitherScale(scale);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set dither scale: {ex}");
                    throw;
                }
            });
        }

        // Saturation by ADU
        public async Task<bool> GetSaturationByADUAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetSaturationByADU();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get saturation by ADU: {ex}");
                    return false;
                }
            });
        }

        public async Task SetSaturationByADUAsync(bool byADU, int? aduValue = null)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetSaturationByADU(byADU, aduValue);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set saturation by ADU: {ex}");
                    throw;
                }
            });
        }

        public async Task<int> GetSaturationADUValueAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetSaturationADUValue();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get saturation ADU value: {ex}");
                    return 0;
                }
            });
        }

        public async Task SetSaturationADUValueAsync(int aduValue)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetSaturationADUValue(aduValue);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set saturation ADU value: {ex}");
                    throw;
                }
            });
        }

        // Guide algorithm selection
        public async Task<string> GetGuideAlgorithmRAAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetGuideAlgorithmRA();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get guide algorithm RA: {ex}");
                    return "none";
                }
            });
        }

        public async Task SetGuideAlgorithmRAAsync(string algorithm)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetGuideAlgorithmRA(algorithm);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set guide algorithm RA: {ex}");
                    throw;
                }
            });
        }

        public async Task<string> GetGuideAlgorithmDECAsync()
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        return client.GetGuideAlgorithmDEC();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get guide algorithm DEC: {ex}");
                    return "none";
                }
            });
        }

        public async Task SetGuideAlgorithmDECAsync(string algorithm)
        {
            await WaitForConnectionIfNeeded();
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }
                        client.SetGuideAlgorithmDEC(algorithm);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set guide algorithm DEC: {ex}");
                    throw;
                }
            });
        }


        // PHD2 "set_" methods
        public async Task SetExposureAsync(int exposureMs)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetExposure(exposureMs);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set exposure: {ex}");
                    throw;
                }
            });
        }

        public async Task SetDecGuideModeAsync(string mode)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetDecGuideMode(mode);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set Dec guide mode: {ex}");
                    throw;
                }
            });
        }

        public async Task SetGuideOutputEnabledAsync(bool enabled)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetGuideOutputEnabled(enabled);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set guide output enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockPositionAsync(double x, double y, bool exact = true)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockPosition(x, y, exact);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock position: {ex}");
                    throw;
                }
            });
        }

        /// <summary>
        /// Auto-select a star using PHD2's find_star method
        /// </summary>
        /// <param name="roi">Optional region of interest [x, y, width, height]. If null, uses full frame.</param>
        /// <returns>The lock position coordinates [x, y] of the selected star</returns>
        public async Task<double[]> FindStarAsync(int[] roi = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var result = client.FindStar(roi);
                        lastError = null;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to find star: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockShiftEnabledAsync(bool enabled)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockShiftEnabled(enabled);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock shift enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockShiftParamsAsync(double xRate, double yRate, string units = "arcsec/hr", string axes = "RA/Dec")
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockShiftParams(xRate, yRate, units, axes);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock shift params: {ex}");
                    throw;
                }
            });
        }

        public async Task SetAlgoParamAsync(string axis, string name, double value)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        // Log the exact value being sent
                        Logger.Info($"Setting PHD2 algorithm parameter {axis}.{name} = {value:F10} (raw: {value})");

                        // Round to a reasonable precision to avoid floating-point precision issues
                        // PHD2 typically works with values to 2-3 decimal places
                        double roundedValue = Math.Round(value, 3);

                        // Add a tiny offset to all values to avoid PHD2's internal floating-point precision issues
                        // This ensures we don't hit any problematic decimal representations
                        // Use 0.001 offset which will survive the 3-decimal rounding
                        double adjustedValue = roundedValue + 0.001;

                        Logger.Info($"Adjusted value from {roundedValue:F4} to {adjustedValue:F4} to avoid PHD2 floating-point issues");

                        roundedValue = adjustedValue;

                        client.SetAlgoParam(axis, name, roundedValue);

                        // Read back the value to verify what PHD2 actually received
                        try
                        {
                            var actualValue = client.GetAlgoParam(axis, name);
                            Logger.Info($"PHD2 confirmed parameter {axis}.{name} = {actualValue:F10} (expected: {roundedValue:F10})");

                            if (Math.Abs(actualValue - roundedValue) > 0.001)
                            {
                                Logger.Warning($"Value mismatch! Sent: {roundedValue:F10}, Got back: {actualValue:F10}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Could not read back parameter value: {ex.Message}");
                        }

                        lastError = null;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
                {
                    // This is expected behavior for invalid axis names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 invalid axis: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set algorithm parameter: {ex}");
                    throw;
                }
            });
        }

        public async Task SetVariableDelaySettingsAsync(bool enabled, int shortDelaySeconds, int longDelaySeconds)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetVariableDelaySettings(enabled, shortDelaySeconds, longDelaySeconds);
                        lastError = null;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
                {
                    // This is expected behavior for unsupported PHD2 versions
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 method not supported: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set variable delay settings: {ex}");
                    throw;
                }
            });
        }

        // PHD2 "get_" methods
        public async Task<int> GetExposureAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetExposure();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get exposure: {ex}");
                    throw;
                }
            });
        }

        public async Task<string> GetDecGuideModeAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetDecGuideMode();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get Dec guide mode: {ex}");
                    throw;
                }
            });
        }

        public async Task<int> GetMaxRaDurationAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        return client.GetMaxRaDuration();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get max RA duration: {ex}");
                    throw;
                }
            });
        }

        public async Task SetMaxRaDurationAsync(int ms)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.SetMaxRaDuration(ms);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set max RA duration: {ex}");
                    throw;
                }
            });
        }

        public async Task<int> GetMaxDecDurationAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        return client.GetMaxDecDuration();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get max DEC duration: {ex}");
                    throw;
                }
            });
        }

        public async Task SetMaxDecDurationAsync(int ms)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.SetMaxDecDuration(ms);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set max DEC duration: {ex}");
                    throw;
                }
            });
        }

        public async Task<JObject> GetDarkLibraryInfoAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        return client.GetDarkLibraryInfo();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get dark library info: {ex}");
                    throw;
                }
            });
        }

        public async Task LoadDarkLibraryAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.LoadDarkLibrary();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to load dark library: {ex}");
                    throw;
                }
            });
        }

        public async Task UnloadDarkLibraryAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.UnloadDarkLibrary();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to unload dark library: {ex}");
                    throw;
                }
            });
        }

        public async Task DeleteDarkLibraryAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.DeleteDarkLibrary();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to delete dark library: {ex}");
                    throw;
                }
            });
        }

        public async Task StartBuildDarkLibraryAsync(int[] expTimesMs, int frameCount)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.StartBuildDarkLibrary(expTimesMs, frameCount);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to start dark library build: {ex}");
                    throw;
                }
            });
        }

        public async Task CancelBuildDarkLibraryAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");
                        client.CancelBuildDarkLibrary();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to cancel dark library build: {ex}");
                    throw;
                }
            });
        }

        public DarkBuildStatus GetDarkBuildStatus()
        {
            lock (lockObject)
            {
                return client?.DarkBuild ?? new DarkBuildStatus();
            }
        }

        public async Task<bool> GetGuideOutputEnabledAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetGuideOutputEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get guide output enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task<double[]> GetLockPositionAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockPosition();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock position: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetLockShiftEnabledAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockShiftEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock shift enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetLockShiftParamsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockShiftParams();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock shift params: {ex}");
                    throw;
                }
            });
        }

        public async Task<string[]> GetAlgoParamNamesAsync(string axis)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAlgoParamNames(axis);
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
                {
                    // This is expected behavior for invalid axis names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 invalid axis: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get algorithm parameter names: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetAlgoParamAsync(string axis, string name)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAlgoParam(axis, name);
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis") || ex.Message.Contains("could not get param"))
                {
                    // This is expected behavior for invalid axis or parameter names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 parameter error: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get algorithm parameter: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetVariableDelaySettingsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetVariableDelaySettings();
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
                {
                    // This is expected behavior for older PHD2 versions
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 method not supported: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get variable delay settings: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetConnectedAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetConnected();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get connected status: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetPausedAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetPaused();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get paused status: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetCurrentEquipmentAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCurrentEquipment();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get current equipment: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetProfileAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetProfile();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get profile: {ex}");
                    throw;
                }
            });
        }

        /// <summary>
        /// Save the current image from PHD2 to a FITS file
        /// </summary>
        /// <returns>The full path to the saved FITS image file</returns>
        public async Task<string> SaveImageAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var filename = client.SaveImage();
                        lastError = null;
                        Logger.Debug($"PHD2 image saved to: {filename}");
                        return filename;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;

                    // "no image available" is expected when PHD2 hasn't captured an image yet
                    if (ex.Message.Contains("no image available"))
                    {
                        Logger.Debug($"PHD2 image not available yet: {ex.Message}");
                    }
                    else
                    {
                        Logger.Error($"Failed to save PHD2 image: {ex}");
                    }
                    throw;
                }
            });
        }

        /// <summary>
        /// Get the star image from PHD2 as base64 encoded data
        /// </summary>
        /// <param name="size">Optional size parameter for the image</param>
        /// <returns>Star image data including dimensions, star position, and base64 encoded pixels</returns>
        public async Task<StarImageData> GetStarImageAsync(int? size = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var starImage = client.GetStarImage(size);
                        lastError = null;
                        Logger.Debug($"PHD2 star image retrieved: {starImage.Width}x{starImage.Height}, star at ({starImage.StarPosX}, {starImage.StarPosY})");
                        return starImage;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message == "no star selected")
                {
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 star image not available: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get PHD2 star image: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetCalibrationDataAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");

                        var result = client.GetCalibrationData("Mount");
                        if (result == null)
                            return (object)new { Calibrated = false };

                        bool calibrated = result["calibrated"]?.ToObject<bool?>() == true;
                        if (!calibrated)
                            return (object)new { Calibrated = false };

                        return (object)new
                        {
                            Calibrated = true,
                            XAngle = result["xAngle"]?.ToObject<double?>() ?? 0,
                            XRate = result["xRate"]?.ToObject<double?>() ?? 0,
                            XParity = result["xParity"]?.ToObject<string>() ?? "",
                            YAngle = result["yAngle"]?.ToObject<double?>() ?? 0,
                            YRate = result["yRate"]?.ToObject<double?>() ?? 0,
                            YParity = result["yParity"]?.ToObject<string>() ?? "",
                            Declination = result["declination"]?.ToObject<double?>() ?? 0
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to get PHD2 calibration data: {ex.Message}");
                    throw;
                }
            });
        }

        public async Task<List<double[]>> GetSecondaryStarsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            return new List<double[]>();

                        return client.GetSecondaryStars();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Failed to get PHD2 secondary stars: {ex.Message}");
                    return new List<double[]>();
                }
            });
        }

        public async Task<Dictionary<string, List<CameraInfo>>> GetAllCameraIdsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAllCameraIds();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get all camera IDs: {ex}");
                    return new Dictionary<string, List<CameraInfo>>();
                }
            });
        }

        private bool disposed = false;

        // Backlash compensation
        public async Task<(bool Enabled, int PulseWidth, int Floor, int Ceiling)> GetBacklashCompAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");

                        return client.GetBacklashComp();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get backlash comp: {ex}");
                    return (false, 0, 0, 0);
                }
            });
        }

        public async Task SetBacklashCompAsync(bool? enabled, int? pulseWidth, int? floor, int? ceiling)
        {
            await WaitForConnectionIfNeeded();

            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                            throw new InvalidOperationException("PHD2 not connected");

                        client.SetBacklashComp(enabled, pulseWidth, floor, ceiling);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set backlash comp: {ex}");
                    throw;
                }
            });
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    try
                    {
                        lock (lockObject)
                        {
                            Logger.Debug("Disposing PHD2Service");

                            // Gracefully disconnect first
                            if (client != null && client.IsConnected)
                            {
                                try
                                {
                                    Logger.Debug("Disconnecting PHD2 client during dispose");
                                    client.Disconnect();
                                }
                                catch (Exception disconnectEx)
                                {
                                    Logger.Warning($"Error during PHD2 disconnect in dispose: {disconnectEx.Message}");
                                }
                            }

                            // Dispose the client
                            client?.Dispose();
                            client = null;

                            Logger.Debug("PHD2Service disposed successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing PHD2Service: {ex}");
                    }
                }
                disposed = true;
            }
        }

        ~PHD2Service()
        {
            Dispose(false);
        }
    }
}
