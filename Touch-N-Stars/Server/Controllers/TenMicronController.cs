using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for 10micron mount model builder / alignment model endpoints.
/// Accesses the TenMicron plugin via reflection to avoid compile-time dependencies.
/// </summary>
public class TenMicronController : WebApiController
{
    private static readonly Type TenMicronPluginType =
        Type.GetType("NINA.Joko.Plugin.TenMicron.TenMicronPlugin, NINA.Joko.Plugin.TenMicron");

    // ── helpers ────────────────────────────────────────────────────────────────

    private static object GetStaticProperty(string name)
    {
        if (TenMicronPluginType == null) return null;
        var prop = TenMicronPluginType.GetProperty(name,
            BindingFlags.Public | BindingFlags.Static);
        return prop?.GetValue(null);
    }

    private static object GetMountModelMediator() => GetStaticProperty("MountModelMediator");
    private static object GetBuilderMediator() => GetStaticProperty("MountModelBuilderMediator");
    private static object GetMountVMMediator() => GetStaticProperty("MountMediator");

    /// <summary>
    /// Tries to get the registered VM handler from a mediator's private handler field.
    /// NINA mediators keep a single "handler" private field of the VM type.
    /// </summary>
    private static object GetMediatorHandler(object mediator)
    {
        if (mediator == null) return null;
        var type = mediator.GetType();
        // Walk up the inheritance chain looking for a "handler" field
        while (type != null && type != typeof(object))
        {
            var field = type.GetField("handler",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(mediator);
            type = type.BaseType;
        }
        return null;
    }

    private static T GetProp<T>(object obj, string name, T fallback = default)
    {
        if (obj == null) return fallback;
        try
        {
            var prop = obj.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return fallback;
            var val = prop.GetValue(obj);
            if (val is T t) return t;
            return fallback;
        }
        catch { return fallback; }
    }

    private static object CallMethod(object obj, string name, object[] args = null)
    {
        if (obj == null) return null;
        try
        {
            var method = obj.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.Instance);
            return method?.Invoke(obj, args ?? Array.Empty<object>());
        }
        catch (TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }
    }

    private static void InvokeICommand(object obj, string propName)
    {
        var cmd = GetProp<object>(obj, propName);
        if (cmd == null) return;
        var execute = cmd.GetType().GetMethod("Execute",
            BindingFlags.Public | BindingFlags.Instance);
        execute?.Invoke(cmd, new object[] { null });
    }

    /// <summary>
    /// Sends a raw LX200 command via the plugin's mount commander and returns the raw response.
    /// Reflects: MountVM.mount (IMount) -> mountCommander (IMountCommander) -> SendCommandString.
    /// Returns null if the mount is not available or the command fails.
    /// </summary>
    private static string SendRawMountCommand(object mountVM, string command)
    {
        if (mountVM == null) return null;
        try
        {
            // Get IMount from MountVM (private field "mount")
            var mountField = mountVM.GetType().GetField("mount",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var mount = mountField?.GetValue(mountVM);
            if (mount == null) return null;

            // Get IMountCommander from Mount (private field "mountCommander")
            var commanderField = mount.GetType().GetField("mountCommander",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var commander = commanderField?.GetValue(mount);
            if (commander == null) return null;

            // Call SendCommandString(command, raw: true)
            var method = commander.GetType().GetMethod("SendCommandString",
                BindingFlags.Public | BindingFlags.Instance);
            return method?.Invoke(commander, new object[] { command, true }) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Sends a raw LX200 command that returns a boolean (single '1'/'0' byte, no '#' terminator).
    /// Uses SendCommandBool on the mount commander. Returns true if the mount acknowledged.
    /// </summary>
    private static bool SendRawMountCommandBool(object mountVM, string command)
    {
        if (mountVM == null) return false;
        try
        {
            var mountField = mountVM.GetType().GetField("mount",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var mount = mountField?.GetValue(mountVM);
            if (mount == null) return false;

            var commanderField = mount.GetType().GetField("mountCommander",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var commander = commanderField?.GetValue(mount);
            if (commander == null) return false;

            var method = commander.GetType().GetMethod("SendCommandBool",
                BindingFlags.Public | BindingFlags.Instance);
            var result = method?.Invoke(commander, new object[] { command, true });
            return result is bool b && b;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns the IP address NINA is actually using to connect to the mount.
    /// On an INDI connection this is profileService.ActiveProfile.TelescopeSettings.IndiAddress.
    /// Falls back to null so callers can use Options.IPAddress instead (ASCOM / LAN direct).
    /// </summary>
    private static string GetConnectionIP(object mountVM)
    {
        if (mountVM == null) return null;
        try
        {
            // profileService is a protected field defined in BaseVM — walk up the hierarchy
            Type t = mountVM.GetType();
            FieldInfo profileServiceField = null;
            while (t != null && profileServiceField == null)
            {
                profileServiceField = t.GetField("profileService",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                t = t.BaseType;
            }
            if (profileServiceField == null) return null;

            var profileService = profileServiceField.GetValue(mountVM);
            if (profileService == null) return null;

            // profileService.ActiveProfile.TelescopeSettings.IndiAddress
            var activeProfile = profileService.GetType()
                .GetProperty("ActiveProfile", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(profileService);
            if (activeProfile == null) return null;

            var telescopeSettings = activeProfile.GetType()
                .GetProperty("TelescopeSettings", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(activeProfile);
            if (telescopeSettings == null) return null;

            var addr = telescopeSettings.GetType()
                .GetProperty("IndiAddress", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(telescopeSettings) as string;
            return string.IsNullOrEmpty(addr) ? null : addr;
        }
        catch { return null; }
    }

    private static bool IsPluginLoaded() => TenMicronPluginType != null &&
                                            GetMountModelMediator() != null;

    private Dictionary<string, object> PluginNotLoaded()
    {
        HttpContext.Response.StatusCode = 503;
        return new Dictionary<string, object>
        {
            { "Success", false },
            { "Error", "TenMicron plugin is not loaded" }
        };
    }

    private Dictionary<string, object> Error(string message, int status = 500)
    {
        HttpContext.Response.StatusCode = status;
        return new Dictionary<string, object>
        {
            { "Success", false },
            { "Error", message }
        };
    }

    // ── status ─────────────────────────────────────────────────────────────────

    /// <summary>GET /tenmicron/status — plugin loaded + mount model info presence</summary>
    [Route(HttpVerbs.Get, "/tenmicron/status")]
    public object GetStatus()
    {
        try
        {
            if (!IsPluginLoaded())
            {
                return new Dictionary<string, object>
                {
                    { "Success", true },
                    { "PluginLoaded", false },
                    { "Connected", false },
                    { "BuildInProgress", false }
                };
            }

            var modelMediator = GetMountModelMediator();
            var info = CallMethod(modelMediator, "GetInfo");
            bool connected = info != null && GetProp<bool>(info, "Connected");

            var builderMediator = GetBuilderMediator();
            var builderVM = GetMediatorHandler(builderMediator);
            bool buildInProgress = builderVM != null && GetProp<bool>(builderVM, "BuildInProgress");

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            var mountInfo = mountVM != null ? GetProp<object>(mountVM, "MountInfo") : null;
            var options = mountVM != null ? GetProp<object>(mountVM, "Options") : null;
            var firmware = mountInfo != null ? GetProp<object>(mountInfo, "ProductFirmware") : null;

            // GPS time sync state via raw LX200 command :gtgpps#
            // Response: 0=Off, 1=GPS synced, 2=GPS+PPS synced
            string gpsSyncState = "Unknown";
            if (connected)
            {
                var gpsRaw = SendRawMountCommand(mountVM, ":gtgpps#");
                if (int.TryParse(gpsRaw?.TrimEnd('#'), out int gpsVal))
                    gpsSyncState = gpsVal switch { 0 => "Off", 1 => "GPS", 2 => "PPS", _ => "Unknown" };
            }

            // Slew rate (current / min / max) via raw LX200 commands
            double? slewRate = null, slewRateMin = null, slewRateMax = null;
            if (connected)
            {
                if (double.TryParse(SendRawMountCommand(mountVM, ":GMs#")?.TrimEnd('#'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double sr))
                    slewRate = sr;
                if (double.TryParse(SendRawMountCommand(mountVM, ":GMsa#")?.TrimEnd('#'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double srMin))
                    slewRateMin = srMin;
                if (double.TryParse(SendRawMountCommand(mountVM, ":GMsb#")?.TrimEnd('#'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double srMax))
                    slewRateMax = srMax;
            }

            // Horizon limit high/low via :Gh# / :Go#
            int? horizonLimitHigh = null, horizonLimitLow = null;
            if (connected)
            {
                if (int.TryParse(SendRawMountCommand(mountVM, ":Gh#")?.TrimEnd('#'), out int hh))
                    horizonLimitHigh = hh;
                if (int.TryParse(SendRawMountCommand(mountVM, ":Go#")?.TrimEnd('#'), out int hl))
                    horizonLimitLow = hl;
            }

            // Connection type via :GINQ# (0=RS-232, 1=GPS/RS-232, 2=LAN, 3=WiFi)
            string connectionType = "Unknown";
            if (connected)
            {
                string[] connLabels = { "RS-232", "GPS/RS-232", "LAN", "WiFi" };
                var connRaw = SendRawMountCommand(mountVM, ":GINQ#")?.TrimEnd('#');
                if (int.TryParse(connRaw, out int ci) && ci >= 0 && ci < connLabels.Length)
                    connectionType = connLabels[ci];
            }

            // DeltaT (UTC / Earth-rotation) expiration via :GDUTV#
            // Response: "V,YYYY-MM-DD" (V=valid) or "X,YYYY-MM-DD" (expired)
            bool deltaTValid = false;
            string deltaTExpiration = null;
            if (connected)
            {
                var dutvRaw = SendRawMountCommand(mountVM, ":GDUTV#")?.TrimEnd('#');
                if (!string.IsNullOrEmpty(dutvRaw))
                {
                    var parts = dutvRaw.Split(',');
                    if (parts.Length == 2)
                    {
                        deltaTValid = parts[0] == "V";
                        deltaTExpiration = parts[1];
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "PluginLoaded", true },
                { "Connected", connected },
                { "BuildInProgress", buildInProgress },
                // toggleable
                { "DualAxisTrackingEnabled", mountInfo != null && GetProp<bool>(mountInfo, "DualAxisTrackingEnabled") },
                { "RefractionCorrectionEnabled", mountInfo != null && GetProp<bool>(mountInfo, "RefractionCorrectionEnabled") },
                { "UnattendedFlipEnabled", mountInfo != null && GetProp<bool>(mountInfo, "UnattendedFlipEnabled") },
                // numeric status
                { "TrackingRateArcsecPerSec", mountInfo != null ? (double)GetProp<decimal>(mountInfo, "TrackingRateArcsecPerSec") : 0.0 },
                { "SlewSettleTimeSeconds", mountInfo != null ? (double)GetProp<decimal>(mountInfo, "SlewSettleTimeSeconds") : 0.0 },
                { "MeridianLimitDegrees", mountInfo != null ? GetProp<int>(mountInfo, "MeridianLimitDegrees") : 0 },
                { "RefractionTemperature", mountInfo != null ? (double)GetProp<decimal>(mountInfo, "RefractionTemperature") : 0.0 },
                { "RefractionPressure", mountInfo != null ? (double)GetProp<decimal>(mountInfo, "RefractionPressure") : 0.0 },
                // enum as string
                { "MountStatus", mountInfo != null ? GetProp<object>(mountInfo, "Status")?.ToString() ?? "" : "" },
                { "GpsSyncState", gpsSyncState },
                // slew rate
                { "SlewRate", (object)slewRate },
                { "SlewRateMin", (object)slewRateMin },
                { "SlewRateMax", (object)slewRateMax },
                // horizon limits
                { "HorizonLimitHigh", (object)horizonLimitHigh },
                { "HorizonLimitLow", (object)horizonLimitLow },
                // connection type
                { "ConnectionType", connectionType },
                // deltaT expiration
                { "DeltaTValid", deltaTValid },
                { "DeltaTExpiration", deltaTExpiration },
                // product / firmware info (static, refreshes on reconnect)
                { "ProductName", firmware != null ? GetProp<string>(firmware, "ProductName") ?? "" : "" },
                { "FirmwareVersion", firmware != null ? GetProp<object>(firmware, "Version")?.ToString() ?? "" : "" },
                { "FirmwareTimestamp", firmware != null ? GetProp<object>(firmware, "Timestamp")?.ToString() ?? "" : "" },
                { "IPAddress", GetConnectionIP(mountVM) ?? (options != null ? GetProp<string>(options, "IPAddress") ?? "" : "") },
                { "MACAddress", options != null ? GetProp<string>(options, "MACAddress") ?? "" : "" },
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetStatus failed", ex);
            return Error(ex.Message);
        }
    }

    // ── alignment model info ───────────────────────────────────────────────────

    /// <summary>GET /tenmicron/alignment-model — returns the current alignment model data</summary>
    [Route(HttpVerbs.Get, "/tenmicron/alignment-model")]
    public async Task<object> GetAlignmentModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var modelMediator = GetMountModelMediator();
            var vmHandler = GetMediatorHandler(modelMediator);

            // Wait for any in-progress alignment model load using the public GetLoadedAlignmentModel API.
            // This avoids triggering an extra mount query (which races with NINA's own polling and
            // can silently fail), whilst still returning up-to-date data when a load is already
            // running (e.g. right after a model build completes via FinishAlignmentSpec).
            if (vmHandler != null)
            {
                var ct = CancellationToken.None;
                var ctsField = vmHandler.GetType().GetField("disconnectCts",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var cts = ctsField?.GetValue(vmHandler) as System.Threading.CancellationTokenSource;
                if (cts != null) ct = cts.Token;

                var getLoadedMethod = vmHandler.GetType().GetMethod("GetLoadedAlignmentModel",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getLoadedMethod != null)
                {
                    var getLoadedTask = getLoadedMethod.Invoke(vmHandler, new object[] { ct }) as Task;
                    if (getLoadedTask != null)
                        await getLoadedTask.ConfigureAwait(false);
                }
            }

            var info = CallMethod(modelMediator, "GetInfo");
            var loadedModel = info != null ? GetProp<object>(info, "LoadedAlignmentModel") : null;
            var modelLoaded = vmHandler != null && GetProp<bool>(vmHandler, "ModelLoaded");

            if (loadedModel == null || !modelLoaded)
            {
                return new Dictionary<string, object>
                {
                    { "Success", true },
                    { "ModelLoaded", false }
                };
            }

            // Collect alignment stars
            var starsCollection = GetProp<object>(loadedModel, "AlignmentStars");
            var starsList = new List<Dictionary<string, object>>();
            if (starsCollection is System.Collections.IEnumerable enumerable)
            {
                foreach (var star in enumerable)
                {
                    starsList.Add(new Dictionary<string, object>
                    {
                        { "Altitude",   Math.Round(GetProp<double>(star, "Altitude"), 2) },
                        { "Azimuth",    Math.Round(GetProp<double>(star, "Azimuth"), 2) },
                        { "ErrorArcsec", Math.Round(GetProp<double>(star, "ErrorArcsec"), 2) },
                        { "ErrorPointRadius", Math.Round(GetProp<double>(star, "ErrorPointRadius"), 2) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "Success",       true },
                { "ModelLoaded",   true },
                { "AlignmentStarCount",  GetProp<int>(loadedModel, "AlignmentStarCount") },
                { "RMSError",            (double)GetProp<decimal>(loadedModel, "RMSError") },
                { "RightAscensionAltitude",           (double)GetProp<decimal>(loadedModel, "RightAscensionAltitude") },
                { "RightAscensionAzimuth",            (double)GetProp<decimal>(loadedModel, "RightAscensionAzimuth") },
                { "PolarAlignErrorDegrees",           (double)GetProp<decimal>(loadedModel, "PolarAlignErrorDegrees") },
                { "PAErrorAltitudeDegrees",           (double)GetProp<decimal>(loadedModel, "PAErrorAltitudeDegrees") },
                { "PAErrorAzimuthDegrees",            (double)GetProp<decimal>(loadedModel, "PAErrorAzimuthDegrees") },
                { "RightAscensionPolarPositionAngleDegrees", (double)GetProp<decimal>(loadedModel, "RightAscensionPolarPositionAngleDegrees") },
                { "OrthogonalityErrorDegrees",        (double)GetProp<decimal>(loadedModel, "OrthogonalityErrorDegrees") },
                { "AzimuthAdjustmentTurns",           (double)GetProp<decimal>(loadedModel, "AzimuthAdjustmentTurns") },
                { "AltitudeAdjustmentTurns",          (double)GetProp<decimal>(loadedModel, "AltitudeAdjustmentTurns") },
                { "ModelTerms",    GetProp<int>(loadedModel, "ModelTerms") },
                { "AlignmentStars", starsList }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetAlignmentModel failed", ex);
            return Error(ex.Message);
        }
    }

    // ── model names ────────────────────────────────────────────────────────────

    /// <summary>GET /tenmicron/model-names — list of saved model names on the mount</summary>
    [Route(HttpVerbs.Get, "/tenmicron/model-names")]
    public async Task<object> GetModelNames()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var modelMediator = GetMountModelMediator();
            var vmHandler = GetMediatorHandler(modelMediator);

            // Trigger a fresh load from the mount via the private LoadModelNames method
            if (vmHandler != null)
            {
                var ct = CancellationToken.None;
                var ctsField = vmHandler.GetType().GetField("disconnectCts",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var cts = ctsField?.GetValue(vmHandler) as System.Threading.CancellationTokenSource;
                if (cts != null) ct = cts.Token;

                var loadMethod = vmHandler.GetType().GetMethod("LoadModelNames",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadMethod != null)
                {
                    var loadTask = loadMethod.Invoke(vmHandler, new object[] { ct }) as Task;
                    if (loadTask != null)
                        await loadTask.ConfigureAwait(false);
                }
            }

            var names = CallMethod(modelMediator, "GetModelNames") as string[];
            // Filter out the UI placeholder entry used by the NINA plugin's ComboBox
            var filtered = names?.Where(n => n != "- Select Model -").ToArray()
                           ?? Array.Empty<string>();
            return new Dictionary<string, object>
            {
                { "Success", true },
                { "ModelNames", filtered }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetModelNames failed", ex);
            return Error(ex.Message);
        }
    }

    // ── dual-axis tracking toggle ──────────────────────────────────────────────

    /// <summary>POST /tenmicron/dual-axis-tracking — enable or disable dual-axis tracking</summary>
    [Route(HttpVerbs.Post, "/tenmicron/dual-axis-tracking")]
    public async Task<object> SetDualAxisTracking()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            bool enabled = false;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("enabled", out var el))
                    enabled = el.GetBoolean();
            }

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            CallMethod(mountVM, "SetDualAxisTracking", new object[] { enabled });

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "DualAxisTrackingEnabled", enabled }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetDualAxisTracking failed", ex);
            return Error(ex.Message);
        }
    }

    // ── refraction correction toggle ────────────────────────────────────────────

    /// <summary>POST /tenmicron/refraction-correction — enable or disable refraction correction</summary>
    [Route(HttpVerbs.Post, "/tenmicron/refraction-correction")]
    public async Task<object> SetRefractionCorrection()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            bool enabled = false;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("enabled", out var el))
                    enabled = el.GetBoolean();
            }

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            CallMethod(mountVM, "SetRefactionCorrectionEnabled", new object[] { enabled });

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "RefractionCorrectionEnabled", enabled }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetRefractionCorrection failed", ex);
            return Error(ex.Message);
        }
    }

    // ── unattended flip ───────────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/unattended-flip/disable — disable unattended flip</summary>
    [Route(HttpVerbs.Post, "/tenmicron/unattended-flip/disable")]
    public object DisableUnattendedFlip()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            CallMethod(mountVM, "DisableUnattendedFlip");

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "UnattendedFlipEnabled", false }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron DisableUnattendedFlip failed", ex);
            return Error(ex.Message);
        }
    }

    // ── reset meridian limit ─────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/reset-meridian-limit — reset meridian slew limit to 0</summary>
    [Route(HttpVerbs.Post, "/tenmicron/reset-meridian-limit")]
    public object ResetMeridianLimit()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            InvokeICommand(mountVM, "ResetMeridianSlewLimitCommand");

            return new Dictionary<string, object> { { "Success", true } };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron ResetMeridianLimit failed", ex);
            return Error(ex.Message);
        }
    }

    // ── reset slew settle time ──────────────────────────────────────────────────

    /// <summary>POST /tenmicron/reset-slew-settle — reset slew settle time to 0</summary>
    [Route(HttpVerbs.Post, "/tenmicron/reset-slew-settle")]
    public object ResetSlewSettle()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            InvokeICommand(mountVM, "ResetSlewSettleLimitCommand");

            return new Dictionary<string, object> { { "Success", true } };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron ResetSlewSettle failed", ex);
            return Error(ex.Message);
        }
    }

    // ── set slew rate ─────────────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/slew-rate — set mount slew rate (2–15×)</summary>
    [Route(HttpVerbs.Post, "/tenmicron/slew-rate")]
    public async Task<object> SetSlewRate()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            int value = 15;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("value", out var el))
                    value = el.GetInt32();
            }
            value = Math.Clamp(value, 2, 15);

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null) return Error("Mount VM not available");

            // Legacy LX200 :Sw# and new-style 10micron :RMs# — both expect single '1' ACK
            bool ok1 = SendRawMountCommandBool(mountVM, $":Sw{value:D2}#");
            bool ok2 = SendRawMountCommandBool(mountVM, $":RMs{value:D2}#");

            return new Dictionary<string, object>
            {
                { "Success", ok1 && ok2 },
                { "SlewRate", value }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetSlewRate failed", ex);
            return Error(ex.Message);
        }
    }

    // ── set horizon limits ────────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/horizon-high — set horizon limit high (0–90°)</summary>
    [Route(HttpVerbs.Post, "/tenmicron/horizon-high")]
    public async Task<object> SetHorizonHigh()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            int value = 90;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("value", out var el))
                    value = el.GetInt32();
            }
            value = Math.Clamp(value, 0, 90);

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null) return Error("Mount VM not available");

            bool ok = SendRawMountCommandBool(mountVM, $":Sh+{value:D2}#");

            return new Dictionary<string, object>
            {
                { "Success", ok },
                { "HorizonLimitHigh", value }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetHorizonHigh failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/horizon-low — set horizon limit low (-5 to 45°)</summary>
    [Route(HttpVerbs.Post, "/tenmicron/horizon-low")]
    public async Task<object> SetHorizonLow()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            int value = 0;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("value", out var el))
                    value = el.GetInt32();
            }
            value = Math.Clamp(value, -5, 45);

            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null) return Error("Mount VM not available");

            // Format: :So+05# or :So-05#
            string sign = value >= 0 ? "+" : "-";
            bool ok = SendRawMountCommandBool(mountVM, $":So{sign}{Math.Abs(value):D2}#");

            return new Dictionary<string, object>
            {
                { "Success", ok },
                { "HorizonLimitLow", value }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetHorizonLow failed", ex);
            return Error(ex.Message);
        }
    }

    // ── builder status ─────────────────────────────────────────────────────────

    /// <summary>GET /tenmicron/builder-status — current model builder VM state</summary>
    [Route(HttpVerbs.Get, "/tenmicron/builder-status")]
    public object GetBuilderStatus()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);

            bool buildInProgress = vm != null && GetProp<bool>(vm, "BuildInProgress");
            bool connected = vm != null && GetProp<bool>(vm, "Connected");
            int goldenSpiralStarCount = vm != null ? GetProp<int>(vm, "GoldenSpiralStarCount", 30) : 30;

            // Serialize model points
            var modelPointsList = new List<Dictionary<string, object>>();
            if (vm != null)
            {
                var modelPoints = GetProp<object>(vm, "DisplayModelPoints");
                if (modelPoints is System.Collections.IEnumerable points)
                {
                    foreach (var pt in points)
                    {
                        var stateObj = GetProp<object>(pt, "ModelPointState");
                        int stateVal = stateObj != null ? (int)stateObj : 0;
                        modelPointsList.Add(new Dictionary<string, object>
                        {
                            { "ModelIndex",      GetProp<int>(pt, "ModelIndex") },
                            { "Azimuth",         Math.Round(GetProp<double>(pt, "Azimuth"), 2) },
                            { "Altitude",        Math.Round(GetProp<double>(pt, "Altitude"), 2) },
                            { "DomeAzimuth",     Math.Round(GetProp<double>(pt, "DomeAzimuth"), 2) },
                            { "DomeAltitude",    Math.Round(GetProp<double>(pt, "DomeAltitude"), 2) },
                            { "ModelPointState", stateVal },
                            { "StateLabel",      stateObj?.ToString() ?? "Generated" }
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "Success",             true },
                { "PluginLoaded",        true },
                { "Connected",           connected },
                { "BuildInProgress",     buildInProgress },
                { "GoldenSpiralStarCount", goldenSpiralStarCount },
                { "ModelPoints",         modelPointsList }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetBuilderStatus failed", ex);
            return Error(ex.Message);
        }
    }

    // ── builder options ────────────────────────────────────────────────────────

    private static void SetProp(object obj, string name, object value)
    {
        if (obj == null) return;
        try
        {
            var prop = obj.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return;
            // Convert value type if needed (e.g. JsonElement → int/double/bool)
            var target = prop.PropertyType;
            object converted = Convert.ChangeType(value, target);
            prop.SetValue(obj, converted);
        }
        catch { /* ignore */ }
    }

    /// <summary>GET /tenmicron/builder-options — returns all point-generation options</summary>
    [Route(HttpVerbs.Get, "/tenmicron/builder-options")]
    public object GetBuilderOptions()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVM = GetMediatorHandler(GetMountVMMediator());
            var opts = mountVM != null ? GetProp<object>(mountVM, "Options") : null;
            if (opts == null)
                return Error("Options not available", 503);

            return new Dictionary<string, object>
            {
                { "Success",                    true },
                { "MinPointAltitude",           GetProp<int>(opts, "MinPointAltitude") },
                { "MaxPointAltitude",           GetProp<int>(opts, "MaxPointAltitude") },
                { "MinPointAzimuth",            GetProp<double>(opts, "MinPointAzimuth") },
                { "MaxPointAzimuth",            GetProp<double>(opts, "MaxPointAzimuth") },
                { "MaxPointRMS",                GetProp<double>(opts, "MaxPointRMS") },
                { "ShowRemovedPoints",          GetProp<bool>(opts, "ShowRemovedPoints") },
                { "MinimizeMeridianFlips",      GetProp<bool>(opts, "MinimizeMeridianFlipsEnabled") },
                { "BuilderNumRetries",          GetProp<int>(opts, "BuilderNumRetries") },
                { "MaxFailedPoints",            GetProp<int>(opts, "MaxFailedPoints") },
                { "RemoveHighRMSAfterBuild",    GetProp<bool>(opts, "RemoveHighRMSPointsAfterBuild") },
                { "LogCommands",                GetProp<bool>(opts, "LogCommands") },
                { "MaxConcurrency",             GetProp<int>(opts, "MaxConcurrency") },
                { "AllowBlindSolves",           GetProp<bool>(opts, "AllowBlindSolves") },
                { "OptimizeDome",               GetProp<bool>(opts, "MinimizeDomeMovementEnabled") },
                { "WestToEast",                 GetProp<bool>(opts, "WestToEastSorting") },
                { "PlateSolveSubframe",         GetProp<double>(opts, "PlateSolveSubframePercentage") },
                { "AlternateDirection",         GetProp<bool>(opts, "AlternateDirectionsBetweenIterations") },
                { "DisableRefractionCorrection",GetProp<bool>(opts, "DisableRefractionCorrection") },
                { "DecJitter",                  GetProp<double>(opts, "DecJitterSigmaDegrees") },
                { "DisableDAT",                 GetProp<bool>(opts, "DisableDATAlignment") },
                // point-generation inputs (persisted to the profile, like the WPF plugin)
                { "GoldenSpiralStarCount",      GetProp<int>(opts, "GoldenSpiralStarCount") },
                { "SiderealRaDelta",            GetProp<double>(opts, "SiderealTrackRADeltaDegrees") },
                { "SiderealStartProvider",      GetProp<string>(opts, "SiderealTrackStartTimeProvider") ?? "" },
                { "SiderealEndProvider",        GetProp<string>(opts, "SiderealTrackEndTimeProvider") ?? "" },
                { "SiderealStartOffset",        GetProp<int>(opts, "SiderealTrackStartOffsetMinutes") },
                { "SiderealEndOffset",          GetProp<int>(opts, "SiderealTrackEndOffsetMinutes") },
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetBuilderOptions failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/builder-option  body: { key: "MinPointAltitude", value: 10 }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/builder-option")]
    public async Task<object> SetBuilderOption()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
            string key = null;
            object value = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
                if (json.RootElement.TryGetProperty("value", out var v))
                {
                    value = v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.True => (object)true,
                        System.Text.Json.JsonValueKind.False => (object)false,
                        System.Text.Json.JsonValueKind.Number => v.TryGetDouble(out var d) ? d : (object)v.GetInt32(),
                        _ => v.GetString()
                    };
                }
            }
            if (string.IsNullOrEmpty(key) || value == null)
                return Error("key and value are required", 400);

            // Map friendly key names to C# property names
            var propMap = new Dictionary<string, string>
            {
                ["MinPointAltitude"] = "MinPointAltitude",
                ["MaxPointAltitude"] = "MaxPointAltitude",
                ["MinPointAzimuth"] = "MinPointAzimuth",
                ["MaxPointAzimuth"] = "MaxPointAzimuth",
                ["MaxPointRMS"] = "MaxPointRMS",
                ["ShowRemovedPoints"] = "ShowRemovedPoints",
                ["MinimizeMeridianFlips"] = "MinimizeMeridianFlipsEnabled",
                ["BuilderNumRetries"] = "BuilderNumRetries",
                ["MaxFailedPoints"] = "MaxFailedPoints",
                ["RemoveHighRMSAfterBuild"] = "RemoveHighRMSPointsAfterBuild",
                ["LogCommands"] = "LogCommands",
                ["MaxConcurrency"] = "MaxConcurrency",
                ["AllowBlindSolves"] = "AllowBlindSolves",
                ["OptimizeDome"] = "MinimizeDomeMovementEnabled",
                ["WestToEast"] = "WestToEastSorting",
                ["PlateSolveSubframe"] = "PlateSolveSubframePercentage",
                ["AlternateDirection"] = "AlternateDirectionsBetweenIterations",
                ["DisableRefractionCorrection"] = "DisableRefractionCorrection",
                ["DecJitter"] = "DecJitterSigmaDegrees",
                ["DisableDAT"] = "DisableDATAlignment",
                ["GoldenSpiralStarCount"] = "GoldenSpiralStarCount",
                ["SiderealRaDelta"] = "SiderealTrackRADeltaDegrees",
                ["SiderealStartProvider"] = "SiderealTrackStartTimeProvider",
                ["SiderealEndProvider"] = "SiderealTrackEndTimeProvider",
                ["SiderealStartOffset"] = "SiderealTrackStartOffsetMinutes",
                ["SiderealEndOffset"] = "SiderealTrackEndOffsetMinutes",
            };

            if (!propMap.TryGetValue(key, out var propName))
                return Error($"Unknown option key: {key}", 400);

            var mountVM = GetMediatorHandler(GetMountVMMediator());
            var opts = mountVM != null ? GetProp<object>(mountVM, "Options") : null;
            if (opts == null)
                return Error("Options not available", 503);

            SetProp(opts, propName, value);
            return new Dictionary<string, object> { { "Success", true } };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SetBuilderOption failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/reset-builder-options — resets all options to factory defaults</summary>
    [Route(HttpVerbs.Post, "/tenmicron/reset-builder-options")]
    public object ResetBuilderOptions()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVM = GetMediatorHandler(GetMountVMMediator());
            var opts = mountVM != null ? GetProp<object>(mountVM, "Options") : null;
            if (opts == null)
                return Error("Options not available", 503);

            opts.GetType().GetMethod("ResetDefaults")?.Invoke(opts, null);

            return new Dictionary<string, object>
            {
                { "Success",                    true },
                { "MinPointAltitude",           GetProp<int>(opts, "MinPointAltitude") },
                { "MaxPointAltitude",           GetProp<int>(opts, "MaxPointAltitude") },
                { "MinPointAzimuth",            GetProp<double>(opts, "MinPointAzimuth") },
                { "MaxPointAzimuth",            GetProp<double>(opts, "MaxPointAzimuth") },
                { "MaxPointRMS",                GetProp<double>(opts, "MaxPointRMS") },
                { "ShowRemovedPoints",          GetProp<bool>(opts, "ShowRemovedPoints") },
                { "MinimizeMeridianFlips",      GetProp<bool>(opts, "MinimizeMeridianFlipsEnabled") },
                { "BuilderNumRetries",          GetProp<int>(opts, "BuilderNumRetries") },
                { "MaxFailedPoints",            GetProp<int>(opts, "MaxFailedPoints") },
                { "RemoveHighRMSAfterBuild",    GetProp<bool>(opts, "RemoveHighRMSPointsAfterBuild") },
                { "LogCommands",                GetProp<bool>(opts, "LogCommands") },
                { "MaxConcurrency",             GetProp<int>(opts, "MaxConcurrency") },
                { "AllowBlindSolves",           GetProp<bool>(opts, "AllowBlindSolves") },
                { "OptimizeDome",               GetProp<bool>(opts, "MinimizeDomeMovementEnabled") },
                { "WestToEast",                 GetProp<bool>(opts, "WestToEastSorting") },
                { "PlateSolveSubframe",         GetProp<double>(opts, "PlateSolveSubframePercentage") },
                { "AlternateDirection",         GetProp<bool>(opts, "AlternateDirectionsBetweenIterations") },
                { "DisableRefractionCorrection",GetProp<bool>(opts, "DisableRefractionCorrection") },
                { "DecJitter",                  GetProp<double>(opts, "DecJitterSigmaDegrees") },
                { "DisableDAT",                 GetProp<bool>(opts, "DisableDATAlignment") },
                // point-generation inputs (persisted to the profile, like the WPF plugin)
                { "GoldenSpiralStarCount",      GetProp<int>(opts, "GoldenSpiralStarCount") },
                { "SiderealRaDelta",            GetProp<double>(opts, "SiderealTrackRADeltaDegrees") },
                { "SiderealStartProvider",      GetProp<string>(opts, "SiderealTrackStartTimeProvider") ?? "" },
                { "SiderealEndProvider",        GetProp<string>(opts, "SiderealTrackEndTimeProvider") ?? "" },
                { "SiderealStartOffset",        GetProp<int>(opts, "SiderealTrackStartOffsetMinutes") },
                { "SiderealEndOffset",          GetProp<int>(opts, "SiderealTrackEndOffsetMinutes") },
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron ResetBuilderOptions failed", ex);
            return Error(ex.Message);
        }
    }

    // ── point generation ───────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/generate-golden-spiral  body: { starCount: 30 }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/generate-golden-spiral")]
    public async Task<object> GenerateGoldenSpiral()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
            int starCount = 30;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("starCount", out var sc))
                        starCount = sc.GetInt32();
                }
                catch { /* use default */ }
            }

            var builderMediator = GetBuilderMediator();
            // This generates points and stores them in the VM; returns ImmutableList<ModelPoint>
            var points = CallMethod(builderMediator, "GenerateGoldenSpiral", new object[] { starCount });
            int count = 0;
            if (points is System.Collections.IEnumerable e)
                foreach (var _ in e) count++;

            return new Dictionary<string, object>
            {
                { "Success",    true },
                { "Message",    $"Generated {count} points" },
                { "PointCount", count }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GenerateGoldenSpiral failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/generate-sidereal-path  body: { ra, dec, raDelta, startProvider, endProvider, startOffset, endOffset }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/generate-sidereal-path")]
    public async Task<object> GenerateSiderealPath()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
            double ra = 0, dec = 0, raDelta = 1.5;
            string startProvider = "Nautical Dusk", endProvider = "Nautical Dawn";
            int startOffset = 0, endOffset = 0;

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(body);
                    var root = json.RootElement;
                    if (root.TryGetProperty("ra", out var raEl)) ra = raEl.GetDouble();
                    if (root.TryGetProperty("dec", out var decEl)) dec = decEl.GetDouble();
                    if (root.TryGetProperty("raDelta", out var rdEl)) raDelta = rdEl.GetDouble();
                    if (root.TryGetProperty("startProvider", out var spEl)) startProvider = spEl.GetString();
                    if (root.TryGetProperty("endProvider", out var epEl)) endProvider = epEl.GetString();
                    if (root.TryGetProperty("startOffset", out var soEl)) startOffset = soEl.GetInt32();
                    if (root.TryGetProperty("endOffset", out var eoEl)) endOffset = eoEl.GetInt32();
                }
                catch { /* use defaults */ }
            }

            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);
            if (vm == null) return Error("Builder VM not available");

            // Build InputCoordinates — RA in hours (0–24), Dec in degrees
            var inputCoordsType = Type.GetType("NINA.Astrometry.InputCoordinates, NINA.Astrometry");
            if (inputCoordsType == null) return Error("InputCoordinates type not found");
            var inputCoords = Activator.CreateInstance(inputCoordsType);
            var coordsPropOnIC = inputCoordsType.GetProperty("Coordinates");
            var innerCoords = coordsPropOnIC?.GetValue(inputCoords);
            if (innerCoords != null)
            {
                innerCoords.GetType().GetProperty("RA")?.SetValue(innerCoords, ra);
                innerCoords.GetType().GetProperty("Dec")?.SetValue(innerCoords, dec);
            }

            // Build Angle for raDelta (degrees)
            var angleType = Type.GetType("NINA.Astrometry.Angle, NINA.Astrometry");
            var byDegreeM = angleType?.GetMethod("ByDegree", BindingFlags.Public | BindingFlags.Static);
            var raDeltaAngle = byDegreeM?.Invoke(null, new object[] { raDelta });
            if (raDeltaAngle == null) return Error("Angle.ByDegree not found");

            // Resolve IDateTimeProvider instances from VM's lists by name
            var startProviders = GetProp<object>(vm, "SiderealPathStartDateTimeProviders") as System.Collections.IEnumerable;
            var endProviders = GetProp<object>(vm, "SiderealPathEndDateTimeProviders") as System.Collections.IEnumerable;
            object startProv = null, endProv = null;
            if (startProviders != null)
                foreach (var p in startProviders)
                {
                    if (string.Equals(GetProp<string>(p, "Name"), startProvider, StringComparison.OrdinalIgnoreCase))
                    { startProv = p; break; }
                }
            if (endProviders != null)
                foreach (var p in endProviders)
                {
                    if (string.Equals(GetProp<string>(p, "Name"), endProvider, StringComparison.OrdinalIgnoreCase))
                    { endProv = p; break; }
                }
            if (startProv == null) return Error($"Start time provider '{startProvider}' not found");
            if (endProv == null) return Error($"End time provider '{endProvider}' not found");

            // Call GenerateSiderealPath on the mediator
            var method = builderMediator.GetType().GetMethod("GenerateSiderealPath",
                BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return Error("GenerateSiderealPath method not found on mediator");

            var result = method.Invoke(builderMediator,
                new object[] { inputCoords, raDeltaAngle, startProv, endProv, startOffset, endOffset });

            int count = 0;
            if (result is System.Collections.IEnumerable e)
                foreach (var _ in e) count++;

            return new Dictionary<string, object>
            {
                { "Success",    true },
                { "Message",    $"Generated {count} sidereal path points" },
                { "PointCount", count }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GenerateSiderealPath failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/sidereal-path-coords-from-scope — returns current mount RA/Dec (hours/degrees)</summary>
    [Route(HttpVerbs.Post, "/tenmicron/sidereal-path-coords-from-scope")]
    public async Task<object> SiderealPathCoordsFromScope()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var vm = GetMediatorHandler(GetBuilderMediator());
            if (vm == null) return Error("Builder VM not available");
            // Invoke the VM's CoordsFromScopeCommand — this calls telescopeMediator.GetInfo() internally
            InvokeICommand(vm, "CoordsFromScopeCommand");
            await Task.Delay(500).ConfigureAwait(false);
            var ic = GetProp<object>(vm, "SiderealPathObjectCoordinates");
            var coords = ic != null ? GetProp<object>(ic, "Coordinates") : null;
            if (coords == null) return Error("Mount coordinates not available — is the telescope connected?");
            double ra = GetProp<double>(coords, "RA");
            double dec = GetProp<double>(coords, "Dec");
            return new Dictionary<string, object>
            {
                { "Success", true },
                { "RA",  ra  },
                { "Dec", dec }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SiderealPathCoordsFromScope failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/sidereal-path-coords-from-sequence — returns first DSO target RA/Dec from loaded sequence</summary>
    [Route(HttpVerbs.Post, "/tenmicron/sidereal-path-coords-from-sequence")]
    public object SiderealPathCoordsFromSequence()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mainContainer = SequenceController.GetMainContainer();
            if (mainContainer == null)
                return Error("No sequence loaded");

            var dso = FindFirstDSOContainer(mainContainer);
            if (dso == null)
                return Error("No DSO target found in current sequence");

            // DeepSkyObjectContainer.Target.InputCoordinates.Coordinates
            var target = GetProp<object>(dso, "Target");
            var inputCoords = target != null ? GetProp<object>(target, "InputCoordinates") : null;
            var coords = inputCoords != null ? GetProp<object>(inputCoords, "Coordinates") : null;
            if (coords == null)
                return Error("Target has no coordinates");

            double ra = GetProp<double>(coords, "RA");
            double dec = GetProp<double>(coords, "Dec");
            return new Dictionary<string, object>
            {
                { "Success", true },
                { "RA",  ra  },
                { "Dec", dec }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SiderealPathCoordsFromSequence failed", ex);
            return Error(ex.Message);
        }
    }

    private static object FindFirstDSOContainer(object container)
    {
        if (container == null) return null;
        if (container.GetType().Name == "DeepSkyObjectContainer")
            return container;

        var itemsProp = container.GetType().GetProperty("Items",
            BindingFlags.Public | BindingFlags.Instance);
        if (itemsProp?.GetValue(container) is System.Collections.IEnumerable items)
            foreach (var child in items)
            {
                var found = FindFirstDSOContainer(child);
                if (found != null) return found;
            }
        return null;
    }

    /// <summary>POST /tenmicron/clear-points</summary>
    [Route(HttpVerbs.Post, "/tenmicron/clear-points")]
    public object ClearPoints()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);
            if (vm == null)
                return Error("MountModelBuilderVM not available");

            // Invoke ClearPointsCommand
            var cmdProp = vm.GetType().GetProperty("ClearPointsCommand",
                BindingFlags.Public | BindingFlags.Instance);
            var cmd = cmdProp?.GetValue(vm);
            if (cmd is System.Windows.Input.ICommand iCmd)
                iCmd.Execute(null);
            else
                return Error("ClearPointsCommand not found on builder VM");

            return new Dictionary<string, object> { { "Success", true } };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron ClearPoints failed", ex);
            return Error(ex.Message);
        }
    }

    // ── build control ──────────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/build-model — fire-and-forget model build with current points</summary>
    [Route(HttpVerbs.Post, "/tenmicron/build-model")]
    public object BuildModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);
            if (vm == null)
                return Error("MountModelBuilderVM not available");

            var cmdProp = vm.GetType().GetProperty("BuildCommand",
                BindingFlags.Public | BindingFlags.Instance);
            var cmd = cmdProp?.GetValue(vm);
            if (cmd is System.Windows.Input.ICommand iCmd)
            {
                // Execute fire-and-forget (the command is an AsyncRelayCommand internally)
                iCmd.Execute(null);
                return new Dictionary<string, object>
                {
                    { "Success", true },
                    { "Message", "Model build started" }
                };
            }
            return Error("BuildCommand not found on builder VM");
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron BuildModel failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/cancel-build</summary>
    [Route(HttpVerbs.Post, "/tenmicron/cancel-build")]
    public object CancelBuild()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);
            if (vm == null)
                return Error("MountModelBuilderVM not available");

            var cmdProp = vm.GetType().GetProperty("CancelBuildCommand",
                BindingFlags.Public | BindingFlags.Instance);
            var cmd = cmdProp?.GetValue(vm);
            if (cmd is System.Windows.Input.ICommand iCmd)
            {
                iCmd.Execute(null);
                return new Dictionary<string, object> { { "Success", true } };
            }
            return Error("CancelBuildCommand not found on builder VM");
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron CancelBuild failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/stop-build</summary>
    [Route(HttpVerbs.Post, "/tenmicron/stop-build")]
    public object StopBuild()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var builderMediator = GetBuilderMediator();
            var vm = GetMediatorHandler(builderMediator);
            if (vm == null)
                return Error("MountModelBuilderVM not available");

            var cmdProp = vm.GetType().GetProperty("StopBuildCommand",
                BindingFlags.Public | BindingFlags.Instance);
            var cmd = cmdProp?.GetValue(vm);
            if (cmd is System.Windows.Input.ICommand iCmd)
            {
                iCmd.Execute(null);
                return new Dictionary<string, object> { { "Success", true } };
            }
            return Error("StopBuildCommand not found on builder VM");
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron StopBuild failed", ex);
            return Error(ex.Message);
        }
    }

    // ── model management ───────────────────────────────────────────────────────

    /// <summary>POST /tenmicron/load-model  body: { name: "..." }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/load-model")]
    public async Task<object> LoadModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var name = await ReadStringField("name").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(name))
                return Error("Model 'name' is required", 400);

            var modelMediator = GetMountModelMediator();
            var result = CallMethod(modelMediator, "LoadModel", new object[] { name });
            bool ok = result is bool b && b;
            if (!ok)
                return Error($"Failed to load model '{name}'");

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "Message", $"Model '{name}' loaded" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron LoadModel failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/save-model  body: { name: "..." }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/save-model")]
    public async Task<object> SaveModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var name = await ReadStringField("name").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(name))
                return Error("Model 'name' is required", 400);

            var modelMediator = GetMountModelMediator();
            var result = CallMethod(modelMediator, "SaveModel", new object[] { name });
            bool ok = result is bool b && b;
            if (!ok)
                return Error($"Failed to save model '{name}'");

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "Message", $"Model saved as '{name}'" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron SaveModel failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/delete-model  body: { name: "..." }</summary>
    [Route(HttpVerbs.Post, "/tenmicron/delete-model")]
    public async Task<object> DeleteModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var name = await ReadStringField("name").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(name))
                return Error("Model 'name' is required", 400);

            var modelMediator = GetMountModelMediator();
            var result = CallMethod(modelMediator, "DeleteModel", new object[] { name });
            bool ok = result is bool b && b;
            if (!ok)
                return Error($"Failed to delete model '{name}'");

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "Message", $"Model '{name}' deleted" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron DeleteModel failed", ex);
            return Error(ex.Message);
        }
    }

    // ── alignment star management ──────────────────────────────────────────────

    /// <summary>POST /tenmicron/delete-worst-star</summary>
    [Route(HttpVerbs.Post, "/tenmicron/delete-worst-star")]
    public async Task<object> DeleteWorstStar()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var modelMediator = GetMountModelMediator();
            int starCount = (int)(CallMethod(modelMediator, "GetAlignmentStarCount") ?? 0);
            if (starCount == 0)
                return Error("No alignment stars to delete", 400);

            // Find the star with the highest error
            double maxError = double.MinValue;
            int worstIndex = -1;
            for (int i = 1; i <= starCount; i++)
            {
                var starInfo = CallMethod(modelMediator, "GetAlignmentStarInfo", new object[] { i });
                if (starInfo == null) continue;
                double err = (double)GetProp<decimal>(starInfo, "ErrorArcseconds");
                if (err > maxError)
                {
                    maxError = err;
                    worstIndex = i;
                }
            }

            if (worstIndex < 0)
                return Error("Could not determine worst star");

            var deleteResult = CallMethod(modelMediator, "DeleteAlignmentStar", new object[] { worstIndex });
            bool ok = deleteResult is bool b && b;
            if (!ok)
                return Error("Failed to delete worst alignment star");

            // Mirror the WPF flow: trigger a fresh model reload from the mount so the
            // cache reflects the updated star count and RMS before the next GET.
            var vmHandler = GetMediatorHandler(modelMediator);
            if (vmHandler != null)
            {
                var ct = CancellationToken.None;
                var ctsField = vmHandler.GetType().GetField("disconnectCts",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var cts = ctsField?.GetValue(vmHandler) as CancellationTokenSource;
                if (cts != null) ct = cts.Token;

                var loadMethod = vmHandler.GetType().GetMethod("LoadAlignmentModel",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadMethod != null)
                {
                    var loadTask = loadMethod.Invoke(vmHandler, new object[] { ct }) as Task;
                    if (loadTask != null)
                        await loadTask.ConfigureAwait(false);
                }
            }

            return new Dictionary<string, object>
            {
                { "Success", true },
                { "Message", $"Deleted star {worstIndex} with error {maxError:0.##} arcsec" },
                { "DeletedStarIndex", worstIndex },
                { "DeletedErrorArcsec", Math.Round(maxError, 2) }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron DeleteWorstStar failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/refresh-alignment-model — force a fresh load from the mount and return the updated model (mirrors WPF RefreshCommand)</summary>
    [Route(HttpVerbs.Post, "/tenmicron/refresh-alignment-model")]
    public async Task<object> RefreshAlignmentModel()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var modelMediator = GetMountModelMediator();
            var vmHandler = GetMediatorHandler(modelMediator);

            if (vmHandler != null)
            {
                var ct = CancellationToken.None;
                var ctsField = vmHandler.GetType().GetField("disconnectCts",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var cts = ctsField?.GetValue(vmHandler) as CancellationTokenSource;
                if (cts != null) ct = cts.Token;

                var loadMethod = vmHandler.GetType().GetMethod("LoadAlignmentModel",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadMethod != null)
                {
                    var loadTask = loadMethod.Invoke(vmHandler, new object[] { ct }) as Task;
                    if (loadTask != null)
                        await loadTask.ConfigureAwait(false);
                }
            }

            var info = CallMethod(modelMediator, "GetInfo");
            var loadedModel = info != null ? GetProp<object>(info, "LoadedAlignmentModel") : null;
            var modelLoaded = vmHandler != null && GetProp<bool>(vmHandler, "ModelLoaded");

            if (loadedModel == null || !modelLoaded)
                return new Dictionary<string, object> { { "Success", true }, { "ModelLoaded", false } };

            var starsCollection = GetProp<object>(loadedModel, "AlignmentStars");
            var starsList = new List<Dictionary<string, object>>();
            if (starsCollection is System.Collections.IEnumerable enumerable)
            {
                foreach (var star in enumerable)
                {
                    starsList.Add(new Dictionary<string, object>
                    {
                        { "Altitude",         Math.Round(GetProp<double>(star, "Altitude"), 2) },
                        { "Azimuth",          Math.Round(GetProp<double>(star, "Azimuth"), 2) },
                        { "ErrorArcsec",      Math.Round(GetProp<double>(star, "ErrorArcsec"), 2) },
                        { "ErrorPointRadius", Math.Round(GetProp<double>(star, "ErrorPointRadius"), 2) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "Success",       true },
                { "ModelLoaded",   true },
                { "AlignmentStarCount",  GetProp<int>(loadedModel, "AlignmentStarCount") },
                { "RMSError",            (double)GetProp<decimal>(loadedModel, "RMSError") },
                { "RightAscensionAltitude",                  (double)GetProp<decimal>(loadedModel, "RightAscensionAltitude") },
                { "RightAscensionAzimuth",                   (double)GetProp<decimal>(loadedModel, "RightAscensionAzimuth") },
                { "PolarAlignErrorDegrees",                  (double)GetProp<decimal>(loadedModel, "PolarAlignErrorDegrees") },
                { "PAErrorAltitudeDegrees",                  (double)GetProp<decimal>(loadedModel, "PAErrorAltitudeDegrees") },
                { "PAErrorAzimuthDegrees",                   (double)GetProp<decimal>(loadedModel, "PAErrorAzimuthDegrees") },
                { "RightAscensionPolarPositionAngleDegrees", (double)GetProp<decimal>(loadedModel, "RightAscensionPolarPositionAngleDegrees") },
                { "OrthogonalityErrorDegrees",               (double)GetProp<decimal>(loadedModel, "OrthogonalityErrorDegrees") },
                { "AzimuthAdjustmentTurns",                  (double)GetProp<decimal>(loadedModel, "AzimuthAdjustmentTurns") },
                { "AltitudeAdjustmentTurns",                 (double)GetProp<decimal>(loadedModel, "AltitudeAdjustmentTurns") },
                { "ModelTerms",    GetProp<int>(loadedModel, "ModelTerms") },
                { "AlignmentStars", starsList }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron RefreshAlignmentModel failed", ex);
            return Error(ex.Message);
        }
    }

    /// <summary>POST /tenmicron/clear-alignment</summary>
    [Route(HttpVerbs.Post, "/tenmicron/clear-alignment")]
    public object ClearAlignment()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var modelMediator = GetMountModelMediator();
            CallMethod(modelMediator, "DeleteAlignment");
            return new Dictionary<string, object>
            {
                { "Success", true },
                { "Message", "Alignment cleared" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron ClearAlignment failed", ex);
            return Error(ex.Message);
        }
    }

    // ── mount time ────────────────────────────────────────────────────────────

    /// <summary>GET /tenmicron/time — fetches current time/date directly from the mount via LX200 commands</summary>
    [Route(HttpVerbs.Get, "/tenmicron/time")]
    public object GetMountTime()
    {
        if (!IsPluginLoaded()) return PluginNotLoaded();
        try
        {
            var mountVMMediator = GetMountVMMediator();
            var mountVM = GetMediatorHandler(mountVMMediator);
            if (mountVM == null)
                return Error("Mount VM not available");

            var modelMediator = GetMountModelMediator();
            var info = CallMethod(modelMediator, "GetInfo");
            bool connected = info != null && GetProp<bool>(info, "Connected");
            if (!connected)
                return Error("Mount is not connected", 503);

            // :GL#   — Local time, response: "HH:MM:SS#"
            // :GC#   — Local date, response: "MM/DD/YY#"
            // :GS#   — Sidereal time, response: "HH:MM:SS#"

            string localTime = SendRawMountCommand(mountVM, ":GL#")?.TrimEnd('#');
            string localDate = SendRawMountCommand(mountVM, ":GC#")?.TrimEnd('#');
            string siderealTime = SendRawMountCommand(mountVM, ":GS#")?.TrimEnd('#');

            return new Dictionary<string, object>
            {
                { "Success",      true },
                { "LocalTime",    localTime },
                { "LocalDate",    localDate },
                { "SiderealTime", siderealTime },
            };
        }
        catch (Exception ex)
        {
            Logger.Error("TenMicron GetMountTime failed", ex);
            return Error(ex.Message);
        }
    }

    // ── utility ────────────────────────────────────────────────────────────────

    private async Task<string> ReadStringField(string field)
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(field, out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }
}
