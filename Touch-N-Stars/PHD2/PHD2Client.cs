/*
MIT License - Based on the PHD2 client implementation by Andy Galasso
https://github.com/agalasso/phd2client

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TouchNStars.PHD2
{
    // Settling progress information returned by CheckSettling()
    public class SettleProgress
    {
        public bool Done { get; set; }
        public double Distance { get; set; }
        public double SettlePx { get; set; }
        public double Time { get; set; }
        public double SettleTime { get; set; }
        public int Status { get; set; }
        public string Error { get; set; }
    }

    public class GuideStats
    {
        public double RmsTotal { get; set; }
        public double RmsRA { get; set; }
        public double RmsDec { get; set; }
        public double PeakRA { get; set; }
        public double PeakDec { get; set; }

        public GuideStats Clone() { return (GuideStats)MemberwiseClone(); }
    }

    public class PHD2Exception : ApplicationException
    {
        public PHD2Exception(string message) : base(message) { }
        public PHD2Exception(string message, Exception inner) : base(message, inner) { }
    }

    public class StarLostInfo
    {
        public int Frame { get; set; }
        public double Time { get; set; }
        public double StarMass { get; set; }
        public double SNR { get; set; }
        public double AvgDist { get; set; }
        public int ErrorCode { get; set; }
        public string Status { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PHD2Status
    {
        public string AppState { get; set; }
        public double AvgDist { get; set; }
        public GuideStats Stats { get; set; }
        public string Version { get; set; }
        public string PHDSubver { get; set; }
        public bool IsConnected { get; set; }
        public bool IsGuiding { get; set; }
        public bool IsSettling { get; set; }
        public SettleProgress SettleProgress { get; set; }
        public StarLostInfo LastStarLost { get; set; }
        public GuideStarInfo CurrentStar { get; set; }
        public CalibrationStepInfo CalibrationStep { get; set; }
    }

    public class GuideStarInfo
    {
        public double SNR { get; set; }
        public double HFD { get; set; }
        public double StarMass { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public class CalibrationStepInfo
    {
        public string Direction { get; set; }
        public int Step { get; set; }
        public string Message { get; set; }
        public double Dist { get; set; }
        public double Dx { get; set; }
        public double Dy { get; set; }
    }

    public class StarImageData
    {
        public int Frame { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double StarPosX { get; set; }
        public double StarPosY { get; set; }
        public string Pixels { get; set; } // Base64 encoded 16-bit pixel data
    }

    public class CameraInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    internal class PHD2Connection : IDisposable
    {
        private TcpClient tcpClient;
        private StreamWriter streamWriter;
        private StreamReader streamReader;

        public bool Connect(string hostname, ushort port)
        {
            try
            {
                tcpClient = new TcpClient(hostname, port);
                streamWriter = new StreamWriter(tcpClient.GetStream())
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                streamReader = new StreamReader(tcpClient.GetStream());
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        public bool IsConnected => tcpClient?.Connected ?? false;

        public string ReadLine()
        {
            try
            {
                return streamReader?.ReadLine();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void WriteLine(string line)
        {
            streamWriter?.WriteLine(line);
        }

        public void Terminate()
        {
            tcpClient?.Close();
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            streamWriter?.Dispose();
            streamReader?.Dispose();
            tcpClient?.Close();
            tcpClient?.Dispose();
        }
    }

    internal class Accumulator
    {
        private readonly List<double> values = new List<double>();

        public void Add(double value)
        {
            values.Add(value);
        }

        public void Reset()
        {
            values.Clear();
        }

        public double Stdev()
        {
            if (values.Count < 2) return 0.0;

            double sum = 0.0;
            double sumSquares = 0.0;

            foreach (var value in values)
            {
                sum += value;
                sumSquares += value * value;
            }

            double mean = sum / values.Count;
            double variance = (sumSquares - sum * mean) / (values.Count - 1);
            return Math.Sqrt(Math.Max(0.0, variance));
        }

        public double Peak()
        {
            double peak = 0.0;
            foreach (var value in values)
            {
                if (Math.Abs(value) > peak)
                    peak = Math.Abs(value);
            }
            return peak;
        }
    }

    public class PHD2Client : IDisposable
    {
        private readonly string hostname;
        private readonly uint instance;
        private PHD2Connection connection;
        private Thread workerThread;
        private volatile bool terminate;
        private readonly object syncObject = new object();
        private JObject response;

        private readonly Accumulator accumRA = new Accumulator();
        private readonly Accumulator accumDec = new Accumulator();
        private bool accumActive;
        private double settlePx;

        // Current state
        public string AppState { get; private set; } = "Stopped";
        public double AvgDist { get; private set; }
        public GuideStats Stats { get; private set; } = new GuideStats();
        public string Version { get; private set; }
        public string PHDSubver { get; private set; }
        public StarLostInfo LastStarLost { get; private set; }
        public GuideStarInfo CurrentStar { get; private set; } = new GuideStarInfo();
        public CalibrationStepInfo LastCalibrationStep { get; private set; }
        private SettleProgress settle;

        public PHD2Client(string hostname = "localhost", uint instance = 1)
        {
            this.hostname = hostname;
            this.instance = instance;
            this.connection = new PHD2Connection();
        }

        public bool IsConnected => connection?.IsConnected ?? false;

        public void Connect()
        {
            Disconnect();

            ushort port = (ushort)(4400 + instance - 1);
            if (!connection.Connect(hostname, port))
                throw new PHD2Exception($"Could not connect to PHD2 instance {instance} on {hostname}");

            terminate = false;
            workerThread = new Thread(Worker) { IsBackground = true };
            workerThread.Start();
        }

        public void Disconnect()
        {
            if (workerThread != null)
            {
                terminate = true;
                connection?.Terminate();
                workerThread.Join(5000);
                workerThread = null;
            }

            connection?.Close();
            connection = new PHD2Connection();
        }

        public JObject Call(string method, JToken param = null)
        {
            string jsonRpc = MakeJsonRpc(method, param);
            Debug.WriteLine($"PHD2 Call: {jsonRpc}");

            // Also log to NINA logger for better visibility
            if (method == "set_algo_param")
            {
                System.Diagnostics.Trace.WriteLine($"PHD2 JSON-RPC: {jsonRpc}");
            }

            if (!IsConnected)
                throw new PHD2Exception("PHD2 Server disconnected");

            try
            {
                connection.WriteLine(jsonRpc);
            }
            catch (Exception ex)
            {
                // Connection was lost while trying to send the command
                throw new PHD2Exception($"Failed to send command to PHD2: {ex.Message}");
            }

            lock (syncObject)
            {
                var timeout = DateTime.Now.AddSeconds(10); // 10 second timeout

                while (response == null && DateTime.Now < timeout)
                {
                    if (!IsConnected)
                        throw new PHD2Exception("PHD2 Server disconnected during call");

                    Monitor.Wait(syncObject, 1000); // Wait max 1 second at a time
                }

                if (response == null)
                {
                    throw new PHD2Exception($"Timeout waiting for response to {method} - PHD2 may be disconnected");
                }

                JObject result = response;
                response = null;

                if (IsFailedResponse(result))
                    throw new PHD2Exception((string)result["error"]["message"]);

                return result;
            }
        }

        private void Worker()
        {
            try
            {
                while (!terminate)
                {
                    string line = connection.ReadLine();
                    if (line == null)
                    {
                        // Connection lost - wake up any waiting calls
                        lock (syncObject)
                        {
                            Monitor.PulseAll(syncObject);
                        }
                        break;
                    }

                    Debug.WriteLine($"PHD2 Response: {line}");

                    try
                    {
                        JObject json = JObject.Parse(line);

                        if (json.ContainsKey("jsonrpc"))
                        {
                            // Response to a call
                            lock (syncObject)
                            {
                                response = json;
                                Monitor.Pulse(syncObject);
                            }
                        }
                        else
                        {
                            // Event notification
                            HandleEvent(json);
                        }
                    }
                    catch (JsonReaderException ex)
                    {
                        Debug.WriteLine($"Invalid JSON from PHD2: {ex.Message}: {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PHD2 Worker thread error: {ex.Message}");
                // Connection lost - wake up any waiting calls
                lock (syncObject)
                {
                    Monitor.PulseAll(syncObject);
                }
            }
        }

        private void HandleEvent(JObject eventObj)
        {
            string eventType = (string)eventObj["Event"];

            switch (eventType)
            {
                case "AppState":
                    AppState = (string)eventObj["State"];
                    break;

                case "Version":
                    Version = (string)eventObj["PHDVersion"];
                    PHDSubver = (string)eventObj["PHDSubver"];
                    break;

                case "StartGuiding":
                    AppState = "Guiding";
                    accumRA.Reset();
                    accumDec.Reset();
                    accumActive = true;
                    break;

                case "GuideStep":
                    if (accumActive)
                    {
                        accumRA.Add((double)eventObj["RADistanceRaw"]);
                        accumDec.Add((double)eventObj["DECDistanceRaw"]);
                        Stats = GetAccumulatedStats();
                    }
                    AppState = "Guiding";
                    AvgDist = (double)eventObj["AvgDist"];

                    // Update current star info from GuideStep event
                    if (CurrentStar != null)
                    {
                        if (eventObj["SNR"] != null) CurrentStar.SNR = (double)eventObj["SNR"];
                        if (eventObj["HFD"] != null) CurrentStar.HFD = (double)eventObj["HFD"];
                        if (eventObj["StarMass"] != null) CurrentStar.StarMass = (double)eventObj["StarMass"];
                        CurrentStar.LastUpdate = DateTime.Now;
                    }
                    break;

                case "LoopingExposures":
                    // PHD2 includes SNR/HFD/StarMass in LoopingExposures when a star is found
                    if (eventObj["StarMass"] != null && CurrentStar != null)
                    {
                        if (eventObj["SNR"] != null) CurrentStar.SNR = (double)eventObj["SNR"];
                        if (eventObj["HFD"] != null) CurrentStar.HFD = (double)eventObj["HFD"];
                        CurrentStar.StarMass = (double)eventObj["StarMass"];
                        CurrentStar.LastUpdate = DateTime.Now;
                    }
                    break;

                case "GuidingStopped":
                    AppState = "Stopped";
                    break;

                case "Paused":
                    AppState = "Paused";
                    break;

                case "StarSelected":
                    AppState = "Selected";
                    break;

                case "Calibrating":
                    AppState = "Calibrating";
                    LastCalibrationStep = new CalibrationStepInfo
                    {
                        Direction = (string)eventObj["dir"],
                        Step = eventObj["step"] != null ? (int)eventObj["step"] : 0,
                        Message = (string)eventObj["State"],
                        Dist = eventObj["dist"] != null ? (double)eventObj["dist"] : 0,
                        Dx = eventObj["dx"] != null ? (double)eventObj["dx"] : 0,
                        Dy = eventObj["dy"] != null ? (double)eventObj["dy"] : 0
                    };
                    break;

                case "CalibrationComplete":
                    LastCalibrationStep = null;
                    break;

                case "CalibrationFailed":
                    LastCalibrationStep = null;
                    break;

                case "StarLost":
                    AppState = "LostLock";
                    AvgDist = (double)eventObj["AvgDist"];

                    // Capture detailed star lost information
                    LastStarLost = new StarLostInfo
                    {
                        Frame = (int)eventObj["Frame"],
                        Time = (double)eventObj["Time"],
                        StarMass = (double)eventObj["StarMass"],
                        SNR = (double)eventObj["SNR"],
                        AvgDist = (double)eventObj["AvgDist"],
                        ErrorCode = (int)eventObj["ErrorCode"],
                        Status = (string)eventObj["Status"],
                        Timestamp = DateTime.Now
                    };
                    break;

                case "SettleBegin":
                    accumActive = false;
                    break;

                case "Settling":
                    var settleProgress = new SettleProgress
                    {
                        Done = false,
                        Distance = (double)eventObj["Distance"],
                        SettlePx = settlePx,
                        Time = (double)eventObj["Time"],
                        SettleTime = (double)eventObj["SettleTime"],
                        Status = 0
                    };
                    lock (syncObject)
                    {
                        settle = settleProgress;
                    }
                    break;

                case "SettleDone":
                    var doneProgress = new SettleProgress
                    {
                        Done = true,
                        Status = (int)eventObj["Status"],
                        Error = (string)eventObj["Error"]
                    };
                    lock (syncObject)
                    {
                        settle = doneProgress;
                    }
                    accumActive = true;
                    break;
            }
        }

        private GuideStats GetAccumulatedStats()
        {
            var stats = new GuideStats
            {
                RmsRA = accumRA.Stdev(),
                RmsDec = accumDec.Stdev(),
                PeakRA = accumRA.Peak(),
                PeakDec = accumDec.Peak()
            };
            stats.RmsTotal = Math.Sqrt(stats.RmsRA * stats.RmsRA + stats.RmsDec * stats.RmsDec);
            return stats;
        }

        public void Guide(double settlePixels, double settleTime, double settleTimeout)
        {
            CheckConnected();
            try
            {
                var settleParam = new JObject
                {
                    ["pixels"] = settlePixels,
                    ["time"] = settleTime,
                    ["timeout"] = settleTimeout
                };

                Call("guide", new JArray { settleParam, false }); // false = don't force calibration
                settlePx = settlePixels;
            }
            catch (Exception)
            {
                lock (syncObject)
                {
                    settle = null;
                }
                throw;
            }
        }

        public void Dither(double ditherPixels, double settlePixels, double settleTime, double settleTimeout)
        {
            CheckConnected();
            try
            {
                var settleParam = new JObject
                {
                    ["pixels"] = settlePixels,
                    ["time"] = settleTime,
                    ["timeout"] = settleTimeout
                };

                Call("dither", new JArray { ditherPixels, false, settleParam }); // false = RA only
                settlePx = settlePixels;
            }
            catch (Exception)
            {
                lock (syncObject)
                {
                    settle = null;
                }
                throw;
            }
        }

        public bool IsSettling()
        {
            CheckConnected();
            lock (syncObject)
            {
                if (settle != null)
                    return true;
            }

            // Initialize settle state
            var result = Call("get_settling");
            bool val = (bool)result["result"];

            if (val)
            {
                var settleProgress = new SettleProgress
                {
                    Done = false,
                    Distance = -1.0,
                    SettlePx = 0.0,
                    Time = 0.0,
                    SettleTime = 0.0,
                    Status = 0
                };
                lock (syncObject)
                {
                    if (settle == null)
                        settle = settleProgress;
                }
            }

            return val;
        }

        public SettleProgress CheckSettling()
        {
            CheckConnected();
            var result = new SettleProgress();

            lock (syncObject)
            {
                if (settle == null)
                    throw new PHD2Exception("Not settling");

                if (settle.Done)
                {
                    result.Done = true;
                    result.Status = settle.Status;
                    result.Error = settle.Error;
                    settle = null;
                }
                else
                {
                    result.Done = false;
                    result.Distance = settle.Distance;
                    result.SettlePx = settlePx;
                    result.Time = settle.Time;
                    result.SettleTime = settle.SettleTime;
                }
            }

            return result;
        }

        public void StopCapture(uint timeoutSeconds = 10)
        {
            CheckConnected();
            Call("stop_capture");
            // Wait for capture to stop
            Thread.Sleep(100);
        }

        public void Loop(uint timeoutSeconds = 10)
        {
            CheckConnected();
            Call("loop");
        }

        public void Pause()
        {
            CheckConnected();
            Call("set_paused", new JValue(true));
        }

        public void Unpause()
        {
            CheckConnected();
            Call("set_paused", new JValue(false));
        }

        public List<string> GetEquipmentProfiles()
        {
            CheckConnected();
            var result = Call("get_profiles");
            var profiles = new List<string>();

            foreach (var profile in result["result"])
            {
                profiles.Add((string)profile["name"]);
            }

            return profiles;
        }

        public Dictionary<string, List<CameraInfo>> GetAllCameraIds()
        {
            CheckConnected();
            var result = Call("get_all_camera_ids");
            var cameraIds = new Dictionary<string, List<CameraInfo>>();

            if (result["result"] is JObject resultObj)
            {
                foreach (var property in resultObj.Properties())
                {
                    var cameraName = property.Name;
                    var cameras = new List<CameraInfo>();

                    foreach (var cameraData in property.Value)
                    {
                        if (cameraData is JObject cameraObj)
                        {
                            var cameraInfo = new CameraInfo
                            {
                                Id = (string)cameraObj["id"],
                                Name = (string)cameraObj["name"]
                            };
                            cameras.Add(cameraInfo);
                        }
                    }

                    cameraIds[cameraName] = cameras;
                }
            }

            return cameraIds;
        }

        public int CreateProfile(string profileName)
        {
            CheckConnected();

            if (string.IsNullOrEmpty(profileName))
                throw new PHD2Exception("Profile name cannot be empty");

            var parameters = new JObject { ["name"] = profileName };

            try
            {
                var result = Call("create_profile", parameters);
                return (int)result["result"];
            }
            catch (PHD2Exception ex)
            {
                if (ex.Message.Contains("profile already exists"))
                {
                    // Profile already exists, get its ID
                    var profiles = Call("get_profiles");
                    foreach (var profile in profiles["result"])
                    {
                        if ((string)profile["name"] == profileName)
                        {
                            return (int)profile["id"];
                        }
                    }
                }
                throw;
            }
        }

        public void DeleteProfile(string profileName)
        {
            CheckConnected();

            if (string.IsNullOrEmpty(profileName))
                throw new PHD2Exception("Profile name cannot be empty");

            var parameters = new JObject { ["name"] = profileName };
            Call("delete_profile", parameters);
        }

        public void RenameProfile(string newName)
        {
            CheckConnected();

            if (string.IsNullOrEmpty(newName))
                throw new PHD2Exception("Profile name cannot be empty");

            var parameters = new JObject { ["name"] = newName };
            Call("rename_profile", parameters);
        }

        public void ConnectEquipment(string profileName)
        {
            CheckConnected();

            var profiles = Call("get_profiles");
            int profileId = -1;

            foreach (var profile in profiles["result"])
            {
                if ((string)profile["name"] == profileName)
                {
                    profileId = (int)profile["id"];
                    break;
                }
            }

            if (profileId == -1)
                throw new PHD2Exception($"Invalid PHD2 profile name: {profileName}");

            StopCapture();
            Call("set_connected", new JValue(false));
            Call("set_profile", new JValue(profileId));
            Call("set_connected", new JValue(true));
        }

        public void DisconnectEquipment()
        {
            CheckConnected();
            StopCapture();
            Call("set_connected", new JValue(false));
        }

        public double GetPixelScale()
        {
            CheckConnected();
            var result = Call("get_pixel_scale");
            var pixelScale = result["result"];

            // PHD2 returns null when no pixel scale is configured (e.g., no camera connected or no calibration done)
            if (pixelScale == null || pixelScale.Type == JTokenType.Null)
            {
                throw new PHD2Exception("Pixel scale not available - camera may not be connected or calibration not done");
            }

            return (double)pixelScale;
        }

        public int GetFocalLength()
        {
            CheckConnected();
            var result = Call("get_focal_length");
            var focalLength = result["result"];

            // PHD2 returns null when no focal length is configured
            if (focalLength == null || focalLength.Type == JTokenType.Null)
            {
                throw new PHD2Exception("Focal length not available");
            }

            return (int)focalLength;
        }

        public void SetFocalLength(int focalLength)
        {
            CheckConnected();
            Call("set_focal_length", focalLength);
        }

        // Calibration methods
        public int GetCalibrationStep()
        {
            CheckConnected();
            var result = Call("get_calibration_step");
            var step = result["result"];

            if (step == null || step.Type == JTokenType.Null)
            {
                throw new PHD2Exception("Calibration step not available");
            }

            return (int)step;
        }

        public void SetCalibrationStep(int step)
        {
            CheckConnected();
            Call("set_calibration_step", step);
        }

        public void ClearMountCalibration()
        {
            CheckConnected();
            Call("clear_mount_calibration");
        }

        // Auto-restore calibration methods
        public bool GetAutoRestoreCalibration()
        {
            CheckConnected();
            var result = Call("get_auto_restore_calibration");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetAutoRestoreCalibration(bool enabled)
        {
            CheckConnected();
            Call("set_auto_restore_calibration", enabled);
        }

        // Orthogonal assumption methods
        public bool GetAssumeDecOrthogonal()
        {
            CheckConnected();
            var result = Call("get_assume_dec_orthogonal");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetAssumeDecOrthogonal(bool enabled)
        {
            CheckConnected();
            Call("set_assume_dec_orthogonal", enabled);
        }

        // DEC compensation methods
        public bool GetUseDecCompensation()
        {
            CheckConnected();
            var result = Call("get_use_dec_compensation");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetUseDecCompensation(bool enabled)
        {
            CheckConnected();
            Call("set_use_dec_compensation", enabled);
        }

        // Search region method
        public int GetSearchRegion()
        {
            CheckConnected();
            var result = Call("get_search_region");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return 0;

            return (int)value;
        }

        public void SetSearchRegion(int pixels)
        {
            CheckConnected();
            Call("set_search_region", pixels);
        }

        // HFR threshold methods
        public double GetMinStarHFR()
        {
            CheckConnected();
            var result = Call("get_min_star_hfd");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return 0.0;

            return (double)value;
        }

        public void SetMinStarHFR(double hfr)
        {
            CheckConnected();
            Call("set_min_star_hfd", hfr);
        }

        public double GetMaxStarHFR()
        {
            CheckConnected();
            var result = Call("get_max_star_hfd");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return 0.0;

            return (double)value;
        }

        public void SetMaxStarHFR(double hfr)
        {
            CheckConnected();
            Call("set_max_star_hfd", hfr);
        }

        // Lost star beep method
        public bool GetBeepForLostStar()
        {
            CheckConnected();
            var result = Call("get_beep_for_lost_star");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetBeepForLostStar(bool enabled)
        {
            CheckConnected();
            Call("set_beep_for_lost_star", enabled);
        }

        // Mass change threshold methods
        public bool GetMassChangeThresholdEnabled()
        {
            CheckConnected();
            var result = Call("get_mass_change_threshold_enabled");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetMassChangeThresholdEnabled(bool enabled)
        {
            CheckConnected();
            Call("set_mass_change_threshold_enabled", enabled);
        }

        public double GetMassChangeThreshold()
        {
            CheckConnected();
            var result = Call("get_mass_change_threshold");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return 0.0;

            return (double)value;
        }

        public void SetMassChangeThreshold(double threshold)
        {
            CheckConnected();
            Call("set_mass_change_threshold", threshold);
        }

        // AF SNR method
        public double GetAFMinStarSNR()
        {
            CheckConnected();
            var result = Call("get_af_min_star_snr");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return 0.0;

            return (double)value;
        }

        public void SetAFMinStarSNR(double snr)
        {
            CheckConnected();
            Call("set_af_min_star_snr", snr);
        }

        // Multi-star mode method
        public bool GetUseMultipleStars()
        {
            CheckConnected();
            var result = Call("get_use_multiple_stars");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetUseMultipleStars(bool enabled)
        {
            CheckConnected();
            Call("set_use_multiple_stars", enabled);
        }

        // Auto-select downsample method
        public string GetAutoSelectDownsample()
        {
            CheckConnected();
            var result = Call("get_auto_select_downsample");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return "Auto";

            return (string)value;
        }

        public void SetAutoSelectDownsample(string value)
        {
            CheckConnected();
            // Validate the value
            if (value != "Auto" && value != "1" && value != "2" && value != "3")
            {
                throw new PHD2Exception($"Invalid downsample value: {value}. Must be 'Auto', '1', '2', or '3'");
            }
            Call("set_auto_select_downsample", value);
        }

        // Image scaling method
        public bool GetAlwaysScaleImages()
        {
            CheckConnected();
            var result = Call("get_always_scale_images");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetAlwaysScaleImages(bool enabled)
        {
            CheckConnected();
            Call("set_always_scale_images", enabled);
        }

        // Meridian flip methods
        public bool GetReverseDecAfterFlip()
        {
            CheckConnected();
            var result = Call("get_reverse_dec_on_flip");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetReverseDecAfterFlip(bool enabled)
        {
            CheckConnected();
            Call("set_reverse_dec_on_flip", enabled);
        }

        // Fast recenter methods
        public bool GetFastRecenterEnabled()
        {
            CheckConnected();
            var result = Call("get_fast_recenter_enabled");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetFastRecenterEnabled(bool enabled)
        {
            CheckConnected();
            Call("set_fast_recenter_enabled", enabled);
        }

        // Mount guide output methods
        public bool GetMountGuideOutputEnabled()
        {
            CheckConnected();
            var result = Call("get_mount_guide_output_enabled");
            var value = result["result"];

            if (value == null || value.Type == JTokenType.Null)
                return false;

            return (bool)value;
        }

        public void SetMountGuideOutputEnabled(bool enabled)
        {
            CheckConnected();
            Call("set_mount_guide_output_enabled", enabled);
        }

        public PHD2Status GetStatus()
        {
            return new PHD2Status
            {
                AppState = AppState,
                AvgDist = AvgDist,
                Stats = Stats?.Clone(),
                Version = Version,
                PHDSubver = PHDSubver,
                IsConnected = IsConnected,
                IsGuiding = IsGuiding(),
                IsSettling = settle != null,
                SettleProgress = settle,
                LastStarLost = LastStarLost,
                CurrentStar = CurrentStar != null ? new GuideStarInfo
                {
                    SNR = CurrentStar.SNR,
                    HFD = CurrentStar.HFD,
                    StarMass = CurrentStar.StarMass,
                    LastUpdate = CurrentStar.LastUpdate
                } : null,
                CalibrationStep = LastCalibrationStep
            };
        }

        // Additional "set_" methods for comprehensive PHD2 control
        public void SetExposure(int exposureMs)
        {
            CheckConnected();
            Call("set_exposure", new JValue(exposureMs));
        }

        public void SetDecGuideMode(string mode)
        {
            CheckConnected();
            // Valid modes: "Off", "Auto", "North", "South"
            if (!new[] { "Off", "Auto", "North", "South" }.Contains(mode))
                throw new PHD2Exception($"Invalid Dec guide mode: {mode}. Valid modes are: Off, Auto, North, South");

            Call("set_dec_guide_mode", new JValue(mode));
        }

        public void SetGuideOutputEnabled(bool enabled)
        {
            CheckConnected();
            Call("set_guide_output_enabled", new JValue(enabled));
        }

        public void SetLockPosition(double x, double y, bool exact = true)
        {
            CheckConnected();
            var param = new JArray { x, y, exact };
            Call("set_lock_position", param);
        }

        public void SetLockShiftEnabled(bool enabled)
        {
            CheckConnected();
            Call("set_lock_shift_enabled", new JValue(enabled));
        }

        public void SetLockShiftParams(double xRate, double yRate, string units = "arcsec/hr", string axes = "RA/Dec")
        {
            CheckConnected();
            // Valid units: "arcsec/hr", "pixels/hr"
            // Valid axes: "RA/Dec", "X/Y"
            if (!new[] { "arcsec/hr", "pixels/hr" }.Contains(units))
                throw new PHD2Exception($"Invalid units: {units}. Valid units are: arcsec/hr, pixels/hr");

            if (!new[] { "RA/Dec", "X/Y" }.Contains(axes))
                throw new PHD2Exception($"Invalid axes: {axes}. Valid axes are: RA/Dec, X/Y");

            var param = new JObject
            {
                ["rate"] = new JArray { xRate, yRate },
                ["units"] = units,
                ["axes"] = axes
            };
            Call("set_lock_shift_params", param);
        }

        public void SetAlgoParam(string axis, string name, double value)
        {
            CheckConnected();
            // Valid axes: "ra", "x", "dec", "y"
            axis = axis.ToLower();
            if (!new[] { "ra", "x", "dec", "y" }.Contains(axis))
                throw new PHD2Exception($"Invalid axis: {axis}. Valid axes are: ra, x, dec, y");

            // Round to 3 decimal places to avoid floating point precision issues
            double roundedValue = Math.Round(value, 3);

            var param = new JArray { axis, name, roundedValue };

            // Log the exact JSON being sent to PHD2
            Debug.WriteLine($"PHD2 SetAlgoParam: axis={axis}, name={name}, original={value:F10}, rounded={roundedValue:F10}");
            Debug.WriteLine($"PHD2 SetAlgoParam JSON: {param.ToString()}");

            Call("set_algo_param", param);
        }

        public void SetVariableDelaySettings(bool enabled, int shortDelaySeconds, int longDelaySeconds)
        {
            CheckConnected();
            var param = new JObject
            {
                ["Enabled"] = enabled,
                ["ShortDelaySeconds"] = shortDelaySeconds,
                ["LongDelaySeconds"] = longDelaySeconds
            };
            Call("set_variable_delay_settings", param);
        }

        public void SetConnectedEquipment(bool connected)
        {
            CheckConnected();
            Call("set_connected", new JValue(connected));
        }

        public void SetProfile(int profileId)
        {
            CheckConnected();
            Call("set_profile", new JValue(profileId));
        }

        public void SetPaused(bool paused, bool fullPause = false)
        {
            CheckConnected();
            var param = fullPause ? new JArray { paused, "full" } : new JArray { paused };
            Call("set_paused", param);
        }

        // Get methods for retrieving current settings
        public int GetExposure()
        {
            CheckConnected();
            var result = Call("get_exposure");
            return (int)result["result"];
        }

        public string GetDecGuideMode()
        {
            CheckConnected();
            var result = Call("get_dec_guide_mode");
            return (string)result["result"];
        }

        public int GetMaxRaDuration()
        {
            CheckConnected();
            var result = Call("get_max_ra_duration");
            return (int)result["result"];
        }

        public void SetMaxRaDuration(int ms)
        {
            CheckConnected();
            Call("set_max_ra_duration", new JValue(ms));
        }

        public int GetMaxDecDuration()
        {
            CheckConnected();
            var result = Call("get_max_dec_duration");
            return (int)result["result"];
        }

        public void SetMaxDecDuration(int ms)
        {
            CheckConnected();
            Call("set_max_dec_duration", new JValue(ms));
        }

        public bool GetGuideOutputEnabled()
        {
            CheckConnected();
            var result = Call("get_guide_output_enabled");
            return (bool)result["result"];
        }

        public double[] GetLockPosition()
        {
            CheckConnected();
            var result = Call("get_lock_position");
            var pos = result["result"];
            if (pos.Type == JTokenType.Null)
                return null;
            return new double[] { (double)pos[0], (double)pos[1] };
        }

        public bool GetLockShiftEnabled()
        {
            CheckConnected();
            var result = Call("get_lock_shift_enabled");
            return (bool)result["result"];
        }

        public JObject GetLockShiftParams()
        {
            CheckConnected();
            var result = Call("get_lock_shift_params");
            return (JObject)result["result"];
        }

        public double GetAlgoParam(string axis, string name)
        {
            CheckConnected();
            axis = axis.ToLower();
            if (!new[] { "ra", "x", "dec", "y" }.Contains(axis))
                throw new PHD2Exception($"Invalid axis: {axis}. Valid axes are: ra, x, dec, y");

            var param = new JArray { axis, name };
            var result = Call("get_algo_param", param);
            var token = result["result"];
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                throw new PHD2Exception($"could not get param '{name}': result is not numeric ({token})");
            return (double)token;
        }

        public string[] GetAlgoParamNames(string axis)
        {
            CheckConnected();
            axis = axis.ToLower();
            if (!new[] { "ra", "x", "dec", "y" }.Contains(axis))
                throw new PHD2Exception($"Invalid axis: {axis}. Valid axes are: ra, x, dec, y");

            var result = Call("get_algo_param_names", new JValue(axis));
            return result["result"].ToObject<string[]>();
        }

        public JObject GetVariableDelaySettings()
        {
            CheckConnected();
            var result = Call("get_variable_delay_settings");
            return (JObject)result["result"];
        }

        public bool GetConnected()
        {
            CheckConnected();
            var result = Call("get_connected");
            return (bool)result["result"];
        }

        public bool GetPaused()
        {
            CheckConnected();
            var result = Call("get_paused");
            return (bool)result["result"];
        }

        public object GetCurrentEquipment()
        {
            try
            {
                CheckConnected();
                var result = Call("get_current_equipment");

                var equipmentObj = new Dictionary<string, object>();
                var resultData = result["result"];

                // PHD2's get_current_equipment returns an object with device info including connection status
                // Format: {"camera": {"name": "Simulator", "connected": true}, "mount": {"name": "On Camera", "connected": true}, ...}

                if (resultData is JObject equipmentDict)
                {
                    // Handle the standard object format returned by PHD2
                    foreach (var kvp in equipmentDict)
                    {
                        string deviceType = kvp.Key.ToLower();

                        if (kvp.Value is JObject deviceInfo)
                        {
                            // PHD2 returns device info as an object with name and connected properties
                            var deviceObj = new Dictionary<string, object>();

                            if (deviceInfo["name"] != null)
                            {
                                deviceObj["name"] = deviceInfo["name"].ToString();
                            }

                            if (deviceInfo["connected"] != null)
                            {
                                deviceObj["connected"] = deviceInfo["connected"].Value<bool>();
                            }
                            else
                            {
                                // Fallback: if connected field is missing, assume connected if device has a name
                                deviceObj["connected"] = !string.IsNullOrEmpty(deviceObj.ContainsKey("name") ? deviceObj["name"].ToString() : "");
                            }

                            equipmentObj[deviceType] = deviceObj;
                        }
                        else
                        {
                            // Legacy format: just device name as string
                            string deviceName = kvp.Value?.ToString() ?? "";
                            equipmentObj[deviceType] = new Dictionary<string, object>
                            {
                                ["name"] = deviceName,
                                ["connected"] = !string.IsNullOrEmpty(deviceName)
                            };
                        }
                    }
                }
                else if (resultData is JArray equipmentArray)
                {
                    // Handle legacy array format [["Camera", "camera_name"], ["Mount", "mount_name"], ...]
                    foreach (JArray item in equipmentArray)
                    {
                        if (item.Count >= 2)
                        {
                            string deviceType = item[0].ToString().ToLower();
                            string deviceName = item[1].ToString();

                            equipmentObj[deviceType] = new Dictionary<string, object>
                            {
                                ["name"] = deviceName,
                                ["connected"] = !string.IsNullOrEmpty(deviceName)
                            };
                        }
                    }
                }
                else
                {
                    // Unknown format, return empty equipment list
                    System.Diagnostics.Debug.WriteLine($"Unknown equipment format: {resultData?.Type}");
                }

                return equipmentObj;
            }
            catch (PHD2Exception)
            {
                // PHD2 not connected or other PHD2-specific error
                // Return empty equipment list instead of throwing to prevent NINA crashes
                return new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                // Log the error but don't crash NINA
                System.Diagnostics.Debug.WriteLine($"Error getting PHD2 equipment: {ex.Message}");
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Auto-select a star. If ROI is provided, star selection will be confined to the specified region.
        /// </summary>
        /// <param name="roi">Optional region of interest [x, y, width, height]. If null, uses full frame.</param>
        /// <returns>The lock position coordinates [x, y] of the selected star</returns>
        public double[] FindStar(int[] roi = null)
        {
            CheckConnected();

            JObject result;
            if (roi != null)
            {
                if (roi.Length != 4)
                    throw new PHD2Exception("ROI must be an array of 4 integers: [x, y, width, height]");

                var roiParam = new JArray { roi[0], roi[1], roi[2], roi[3] };
                result = Call("find_star", new JObject { ["roi"] = roiParam });
            }
            else
            {
                result = Call("find_star");
            }

            var pos = result["result"];
            if (pos.Type == JTokenType.Array && pos.Count() == 2)
            {
                return new double[] { (double)pos[0], (double)pos[1] };
            }

            throw new PHD2Exception("find_star did not return valid coordinates");
        }

        /// <summary>
        /// Save the current image from PHD2 to a FITS file
        /// </summary>
        /// <returns>The full path to the saved FITS image file</returns>
        public string SaveImage()
        {
            CheckConnected();
            var result = Call("save_image");
            var filename = (string)result["result"]["filename"];

            if (string.IsNullOrEmpty(filename))
                throw new PHD2Exception("save_image did not return a valid filename");

            return filename;
        }

        /// <summary>
        /// Get the star image from PHD2 as base64 encoded data
        /// </summary>
        /// <param name="size">Optional size parameter for the image (minimum 15 pixels, default 15)</param>
        /// <returns>Star image data including dimensions, star position, and base64 encoded pixels</returns>
        public StarImageData GetStarImage(int? size = null)
        {
            CheckConnected();

            JObject result;
            if (size.HasValue)
            {
                // PHD2 requires size >= 15
                int actualSize = Math.Max(15, size.Value);
                result = Call("get_star_image", new JValue(actualSize));
            }
            else
            {
                result = Call("get_star_image");
            }

            var resultData = result["result"];
            if (resultData == null)
                throw new PHD2Exception("get_star_image returned null result");

            return new StarImageData
            {
                Frame = (int)resultData["frame"],
                Width = (int)resultData["width"],
                Height = (int)resultData["height"],
                StarPosX = (double)resultData["star_pos"][0],
                StarPosY = (double)resultData["star_pos"][1],
                Pixels = (string)resultData["pixels"]
            };
        }

        public JObject GetCalibrationData(string which = "Mount")
        {
            CheckConnected();
            var result = Call("get_calibration_data", new JValue(which));
            return result["result"] as JObject;
        }

        public List<double[]> GetSecondaryStars()
        {
            CheckConnected();

            var result = Call("get_secondary_stars");
            var arr = result["result"] as JArray;
            var stars = new List<double[]>();
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    var x = (double)item["x"];
                    var y = (double)item["y"];
                    stars.Add(new[] { x, y });
                }
            }
            return stars;
        }

        private bool IsGuiding()
        {
            return AppState == "Guiding" || AppState == "LostLock";
        }

        private void CheckConnected()
        {
            if (!IsConnected)
                throw new PHD2Exception("PHD2 Server disconnected");
        }

        private static string MakeJsonRpc(string method, JToken param)
        {
            var request = new JObject
            {
                ["method"] = method,
                ["id"] = 1
            };

            if (param != null && param.Type != JTokenType.Null)
            {
                if (param.Type == JTokenType.Array || param.Type == JTokenType.Object)
                    request["params"] = param;
                else
                {
                    var array = new JArray { param };
                    request["params"] = array;
                }
            }

            return request.ToString(Formatting.None);
        }

        private static bool IsFailedResponse(JObject response)
        {
            return response.ContainsKey("error");
        }

        public void Dispose()
        {
            Disconnect();
            connection?.Dispose();
        }

        public object GetProfile()
        {
            CheckConnected();
            var result = Call("get_profile");
            var profileResult = result["result"];

            // PHD2's get_profile can return either a string or an object
            if (profileResult.Type == JTokenType.String)
            {
                try
                {
                    // Try to parse as JSON if it's a string
                    var parsed = JObject.Parse(profileResult.ToString());
                    return new Dictionary<string, object>
                    {
                        ["id"] = parsed["id"]?.Value<int>() ?? 0,
                        ["name"] = parsed["name"]?.ToString() ?? ""
                    };
                }
                catch
                {
                    // If parsing fails, return the string as the profile name
                    return new Dictionary<string, object>
                    {
                        ["name"] = profileResult.ToString()
                    };
                }
            }
            else if (profileResult.Type == JTokenType.Object)
            {
                // If it's already an object, convert to dictionary
                var profileObj = (JObject)profileResult;
                return new Dictionary<string, object>
                {
                    ["id"] = profileObj["id"]?.Value<int>() ?? 0,
                    ["name"] = profileObj["name"]?.ToString() ?? ""
                };
            }

            // Fallback - return as string
            return new Dictionary<string, object>
            {
                ["name"] = profileResult.ToString()
            };
        }

        // Camera control methods
        public int GetCameraGain()
        {
            CheckConnected();
            var result = Call("get_camera_gain");
            if (result == null || result["result"] == null)
                return 0;
            return (int)result["result"];
        }

        public void SetCameraGain(int gain)
        {
            CheckConnected();
            var param = new JObject { ["gain"] = gain };
            Call("set_camera_gain", param);
        }

        public bool GetCameraCoolerOn()
        {
            CheckConnected();
            var result = Call("get_camera_cooler_on");
            if (result == null || result["result"] == null)
                return false;
            return (bool)result["result"];
        }

        public void SetCameraCoolerOn(bool enabled)
        {
            CheckConnected();
            var param = new JObject { ["enabled"] = enabled };
            Call("set_camera_cooler_on", param);
        }

        public double GetCameraTemperatureSetpoint()
        {
            CheckConnected();
            var result = Call("get_camera_temperature_setpoint");
            if (result == null || result["result"] == null)
                return 0.0;
            return (double)result["result"];
        }

        public void SetCameraTemperatureSetpoint(double temperature)
        {
            CheckConnected();
            var param = new JObject { ["temperature"] = temperature };
            Call("set_camera_temperature_setpoint", param);
        }

        public bool GetCameraUseSubframes()
        {
            CheckConnected();
            var result = Call("get_camera_use_subframes");
            if (result == null || result["result"] == null)
                return false;
            return (bool)result["result"];
        }

        public void SetCameraUseSubframes(bool enabled)
        {
            CheckConnected();
            var param = new JObject { ["enabled"] = enabled };
            Call("set_camera_use_subframes", param);
        }

        public int GetCameraBinning()
        {
            CheckConnected();
            var result = Call("get_camera_binning");
            if (result == null || result["result"] == null)
                return 0;
            return (int)result["result"];
        }

        public void SetCameraBinning(int binning)
        {
            CheckConnected();
            var param = new JObject { ["binning"] = binning };
            Call("set_camera_binning", param);
        }

        public int GetCameraBitdepth()
        {
            CheckConnected();
            var result = Call("get_camera_bitdepth");
            if (result == null || result["result"] == null)
                return 0;
            return (int)result["result"];
        }

        /// <summary>
        /// Calls the get_camera_info RPC (added to PHD2 locally).
        /// Returns a Dictionary with keys: name, connected, pixel_size, is_color,
        /// has_gain_control, gain, has_subframes, has_cooler, max_hw_binning, binning,
        /// and (when connected) bits_per_pixel, frame_width, frame_height.
        /// </summary>
        public Dictionary<string, object> GetCameraInfoRpc()
        {
            CheckConnected();
            var result = Call("get_camera_info");
            if (result == null || result["result"] == null || result["result"].Type == JTokenType.Null)
                return null;

            var obj = result["result"] as JObject;
            if (obj == null)
                return null;

            var info = new Dictionary<string, object>();
            foreach (var prop in obj.Properties())
            {
                var val = prop.Value;
                switch (val.Type)
                {
                    case JTokenType.Boolean: info[prop.Name] = (bool)val; break;
                    case JTokenType.Integer: info[prop.Name] = (long)val; break;
                    case JTokenType.Float:   info[prop.Name] = (double)val; break;
                    case JTokenType.String:  info[prop.Name] = (string)val; break;
                    case JTokenType.Null:    info[prop.Name] = null; break;
                    default:                 info[prop.Name] = val.ToString(); break;
                }
            }
            return info;
        }

        /// <summary>
        /// Returns the guide frame size as (Width, Height), or null if the camera is not connected.
        /// PHD2 serialises wxSize as a two-element JSON array [x, y].
        /// </summary>
        public (int Width, int Height)? GetCameraFrameSize()
        {
            CheckConnected();
            try
            {
                var result = Call("get_camera_frame_size");
                if (result == null || result["result"] == null || result["result"].Type == JTokenType.Null)
                    return null;
                var ary = result["result"] as JArray;
                if (ary == null || ary.Count < 2)
                    return null;
                return ((int)ary[0], (int)ary[1]);
            }
            catch
            {
                return null; // camera not connected in PHD2
            }
        }

        public string GetSelectedCamera()
        {
            CheckConnected();
            var result = Call("get_selected_camera");
            if (result == null || result["result"] == null || result["result"].Type == JTokenType.Null)
                return null;
            return (string)result["result"];
        }

        public string GetSelectedCameraId()
        {
            CheckConnected();
            var result = Call("get_selected_camera_id");
            if (result == null || result["result"] == null || result["result"].Type == JTokenType.Null)
                return null;
            return (string)result["result"];
        }

        // Auto exposure methods
        public double GetAutoExposureMin()
        {
            CheckConnected();
            var result = Call("get_auto_exposure_min");
            if (result == null || result["result"] == null)
                return 0.0;
            return (double)result["result"];
        }

        public void SetAutoExposureMin(double min)
        {
            CheckConnected();
            var param = new JObject { ["exposure"] = min };
            Call("set_auto_exposure_min", param);
        }

        public double GetAutoExposureMax()
        {
            CheckConnected();
            var result = Call("get_auto_exposure_max");
            if (result == null || result["result"] == null)
                return 0.0;
            return (double)result["result"];
        }

        public void SetAutoExposureMax(double max)
        {
            CheckConnected();
            var param = new JObject { ["exposure"] = max };
            Call("set_auto_exposure_max", param);
        }

        public double GetAutoExposureTargetSNR()
        {
            CheckConnected();
            var result = Call("get_auto_exposure_target_snr");
            if (result == null || result["result"] == null)
                return 0.0;
            return (double)result["result"];
        }

        public void SetAutoExposureTargetSNR(double snr)
        {
            CheckConnected();
            var param = new JObject { ["target_snr"] = snr };
            Call("set_auto_exposure_target_snr", param);
        }

        // Dither mode
        public string GetDitherMode()
        {
            CheckConnected();
            var result = Call("get_dither_mode");
            if (result == null || result["result"] == null)
                return "random";
            return (string)result["result"];
        }

        public void SetDitherMode(string mode)
        {
            CheckConnected();
            var param = new JObject { ["mode"] = mode };
            Call("set_dither_mode", param);
        }

        public bool GetDitherRaOnly()
        {
            CheckConnected();
            var result = Call("get_dither_ra_only");
            if (result == null || result["result"] == null)
                return false;
            return (bool)result["result"];
        }

        public void SetDitherRaOnly(bool raOnly)
        {
            CheckConnected();
            var param = new JObject { ["ra_only"] = raOnly };
            Call("set_dither_ra_only", param);
        }

        public double GetDitherScale()
        {
            CheckConnected();
            var result = Call("get_dither_scale");
            if (result == null || result["result"] == null)
                return 1.0;
            return (double)result["result"];
        }

        public void SetDitherScale(double scale)
        {
            CheckConnected();
            var param = new JObject { ["scale"] = scale };
            Call("set_dither_scale", param);
        }

        // Saturation by ADU
        public bool GetSaturationByADU()
        {
            CheckConnected();
            var result = Call("get_saturation_by_adu");
            if (result == null || result["result"] == null)
                return false;
            return (bool)result["result"];
        }

        public void SetSaturationByADU(bool byADU, int? aduValue = null)
        {
            CheckConnected();
            var param = new JObject { ["by_adu"] = byADU };
            if (byADU && aduValue.HasValue)
                param["adu_value"] = aduValue.Value;
            Call("set_saturation_by_adu", param);
        }

        public int GetSaturationADUValue()
        {
            CheckConnected();
            var result = Call("get_saturation_adu_value");
            if (result == null || result["result"] == null)
                return 0;
            return (int)result["result"];
        }

        // Guide algorithm selection
        public string GetGuideAlgorithmRA()
        {
            CheckConnected();
            var result = Call("get_guide_algorithm_ra");
            if (result == null || result["result"] == null)
                return "None";
            return (string)result["result"];
        }

        public void SetGuideAlgorithmRA(string algorithm)
        {
            CheckConnected();
            var param = new JObject { ["algorithm"] = algorithm };
            Call("set_guide_algorithm_ra", param);
        }

        public string GetGuideAlgorithmDEC()
        {
            CheckConnected();
            var result = Call("get_guide_algorithm_dec");
            if (result == null || result["result"] == null)
                return "None";
            return (string)result["result"];
        }

        public void SetGuideAlgorithmDEC(string algorithm)
        {
            CheckConnected();
            var param = new JObject { ["algorithm"] = algorithm };
            Call("set_guide_algorithm_dec", param);
        }

        public void SetSaturationADUValue(int aduValue)
        {
            CheckConnected();
            var param = new JObject { ["adu_value"] = aduValue };
            Call("set_saturation_adu_value", param);
        }

    }
}
