using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for TPPA (Two-Point Polar Alignment) / PolarAlignment plugin integration endpoints
/// </summary>
public class TPPAController : WebApiController
{
    private object GetPolarAlignmentSettingsInstance()
    {
        var settingsType = Type.GetType("NINA.Plugins.PolarAlignment.Properties.Settings, NINA.Plugins.PolarAlignment");
        if (settingsType == null) return null;
        var defaultProperty = settingsType.GetProperty("Default",
            BindingFlags.Public | BindingFlags.Static);
        return defaultProperty?.GetValue(null);
    }

    /// <summary>
    /// Gets the live DockablePolarAlignmentVM instance by accessing the MessageBroker's
    /// subscriber list for the start-alignment topic. This is the same object WPF binds to.
    /// </summary>
    private object GetPolarAlignmentVMInstance()
    {
        try
        {
            const string topic = "PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment";
            var broker = TouchNStars.Mediators.MessageBroker;
            if (broker == null)
            {
                Logger.Warning("MessageBroker is null");
                return null;
            }

            // Access the private subscribers dictionary via reflection
            var subscribersField = broker.GetType().GetField("subscribers",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (subscribersField == null)
            {
                Logger.Warning("Could not find subscribers field on MessageBroker");
                return null;
            }

            var subscribersDict = subscribersField.GetValue(broker) as System.Collections.IDictionary;
            if (subscribersDict == null || !subscribersDict.Contains(topic))
            {
                Logger.Warning($"No subscribers found for topic: {topic}");
                return null;
            }

            var list = subscribersDict[topic] as System.Collections.IList;
            if (list == null || list.Count == 0)
            {
                Logger.Warning("Subscriber list for start-alignment topic is empty");
                return null;
            }

            // Find the DockablePolarAlignmentVM subscriber
            foreach (var subscriber in list)
            {
                if (subscriber == null) continue;
                if (subscriber.GetType().Name == "DockablePolarAlignmentVM")
                {
                    return subscriber;
                }
            }

            Logger.Warning("DockablePolarAlignmentVM not found in start-alignment subscribers");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error in GetPolarAlignmentVMInstance: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Gets the live PolarAlignment instruction instance from DockablePolarAlignmentVM.
    /// This is the same object WPF binds to for TargetDistance, MoveRate etc.
    /// </summary>
    private object GetPolarAlignmentInstruction()
    {
        var vm = GetPolarAlignmentVMInstance();
        if (vm == null) return null;

        var paProp = vm.GetType().GetProperty("PolarAlignment", BindingFlags.Public | BindingFlags.Instance);
        if (paProp == null)
        {
            Logger.Warning("PolarAlignment property not found on DockablePolarAlignmentVM");
            return null;
        }

        var pa = paProp.GetValue(vm);
        Logger.Info($"GetPolarAlignmentInstruction: Found PolarAlignment instance: {(pa != null ? pa.GetType().Name : "null")}");
        return pa;
    }

    /// <summary>
    /// Gets whether TPPA is currently running, read directly from the live
    /// DockablePolarAlignmentVM.IsRunning, regardless of what triggered the run
    /// (this API, the NINA UI, or a sequence).
    /// GET /tppa/info
    /// </summary>
    [Route(HttpVerbs.Get, "/tppa/info")]
    public object GetTPPAInfo()
    {
        try
        {
            var vm = GetPolarAlignmentVMInstance();
            if (vm == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "PolarAlignment plugin not loaded" }
                };
            }

            var isRunningProp = vm.GetType().GetProperty("IsRunning", BindingFlags.Public | BindingFlags.Instance);
            var isRunning = isRunningProp != null && (bool)(isRunningProp.GetValue(vm) ?? false);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "IsRunning", isRunning }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch TPPA info: {ex.Message}" }
            };
        }
    }

    // Maps Settings.Default property names to PolarAlignment instruction property names
    private static readonly Dictionary<string, string> SettingsToInstructionMap = new()
    {
        { "DefaultTargetDistance", "TargetDistance" },
        { "DefaultMoveRate",       "MoveRate" },
        { "DefaultEastDirection",  "EastDirection" },
        { "DefaultSearchRadius",   "SearchRadius" },
        { "AlignmentTolerance",    "AlignmentTolerance" },
    };

    /// <summary>
    /// Gets all available TPPA/PolarAlignment options and their current values
    /// </summary>
    [Route(HttpVerbs.Get, "/tppa/options")]
    public object GetTPPAOptions()
    {
        try
        {
            var settingsInstance = GetPolarAlignmentSettingsInstance();
            if (settingsInstance == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "PolarAlignment plugin not loaded" }
                };
            }

            var settingsType = settingsInstance.GetType();
            var options = new Dictionary<string, object>();

            // Define which options to include (excluding colors and WPF-specific options)
            var optionProperties = new[]
            {
                // Movement & Control
                "DefaultMoveRate",
                "DefaultEastDirection",
                "MoveTimeoutFactor",

                // Targeting Parameters
                "DefaultTargetDistance",
                "DefaultSearchRadius",
                "DefaultAzimuthOffset",
                "DefaultAltitudeOffset",

                // Alignment Control
                "AlignmentTolerance",
                "RefractionAdjustment",
                "StopTrackingWhenDone",

                // Automation Options
                "AutomatedAdjustmentSettleTime",
                "AutoPause",
                "LogError"
            };

            foreach (var optionName in optionProperties)
            {
                var property = settingsType.GetProperty(optionName,
                    BindingFlags.Public | BindingFlags.Instance);

                if (property != null && property.CanRead)
                {
                    try
                    {
                        var value = property.GetValue(settingsInstance);
                        options[optionName] = new Dictionary<string, object>()
                        {
                            { "Value", value ?? "" },
                            { "Type", property.PropertyType.Name }
                        };
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to read option {optionName}: {ex.Message}");
                        options[optionName] = new Dictionary<string, object>()
                        {
                            { "Value", null },
                            { "Type", property.PropertyType.Name },
                            { "Error", ex.Message }
                        };
                    }
                }
            }

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Options", options }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch TPPA options: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Sets multiple TPPA/PolarAlignment options at once via JSON body
    /// POST /tppa/options
    /// Request body: { "DefaultMoveRate": 5, "StopTrackingWhenDone": true, ... }
    /// </summary>
    [Route(HttpVerbs.Post, "/tppa/options")]
    public async Task<object> SetTPPAOptions()
    {
        try
        {
            var settingsInstance = GetPolarAlignmentSettingsInstance();
            if (settingsInstance == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "PolarAlignment plugin not loaded" }
                };
            }

            var settingsType = settingsInstance.GetType();

            // Parse the request body to get options dictionary
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || requestData.Count == 0)
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Request body must contain at least one option" }
                };
            }

            var setCount = 0;
            var failures = new Dictionary<string, string>();

            foreach (var kvp in requestData)
            {
                var optionName = kvp.Key;
                var newValue = kvp.Value;

                try
                {
                    var property = settingsType.GetProperty(optionName,
                        BindingFlags.Public | BindingFlags.Instance);

                    if (property == null)
                    {
                        failures[optionName] = $"Option '{optionName}' not found";
                        continue;
                    }

                    if (!property.CanWrite)
                    {
                        failures[optionName] = $"Option '{optionName}' is read-only";
                        continue;
                    }

                    // Convert the value to the correct type (culture-invariant)
                    object convertedValue = newValue;
                    if (newValue != null)
                    {
                        var targetType = property.PropertyType;

                        if (targetType == typeof(bool) && newValue is string strBool)
                        {
                            convertedValue = bool.Parse(strBool);
                        }
                        else if (targetType == typeof(int) && newValue is not int)
                        {
                            convertedValue = newValue is string strInt 
                                ? int.Parse(strInt, CultureInfo.InvariantCulture)
                                : Convert.ToInt32(newValue, CultureInfo.InvariantCulture);
                        }
                        else if (targetType == typeof(double) && newValue is not double)
                        {
                            convertedValue = newValue is string strDouble
                                ? double.Parse(strDouble, CultureInfo.InvariantCulture)
                                : Convert.ToDouble(newValue, CultureInfo.InvariantCulture);
                        }
                        else if (targetType == typeof(float) && newValue is not float)
                        {
                            convertedValue = newValue is string strFloat
                                ? float.Parse(strFloat, CultureInfo.InvariantCulture)
                                : Convert.ToSingle(newValue, CultureInfo.InvariantCulture);
                        }
                    }

                    property.SetValue(settingsInstance, convertedValue);
                    setCount++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to set option {optionName}: {ex.Message}");
                    failures[optionName] = ex.Message;
                }
            }

            // Save and reload settings
            try
            {
                var saveMethod = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                saveMethod?.Invoke(settingsInstance, null);
                var reloadMethod = settingsType.GetMethod("Reload", BindingFlags.Public | BindingFlags.Instance);
                reloadMethod?.Invoke(settingsInstance, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during Save/Reload: {ex.Message}", ex);
            }

            // Sync updated settings to the live PolarAlignment instruction instance
            // (same as what WPF binds to via DockablePolarAlignmentVM.PolarAlignment)
            try
            {
                var paInstance = GetPolarAlignmentInstruction();
                if (paInstance != null)
                {
                    var paType = paInstance.GetType();
                    foreach (var kvp in requestData)
                    {
                        var settingName = kvp.Key;
                        var newValue = kvp.Value;
                        if (!SettingsToInstructionMap.TryGetValue(settingName, out var instrPropName))
                            continue;

                        var instrProp = paType.GetProperty(instrPropName,
                            BindingFlags.Public | BindingFlags.Instance);
                        if (instrProp == null || !instrProp.CanWrite || newValue == null)
                            continue;

                        try
                        {
                            object converted = newValue;
                            var targetType = instrProp.PropertyType;
                            if (targetType == typeof(int) && newValue is not int)
                                converted = Convert.ToInt32(newValue, CultureInfo.InvariantCulture);
                            else if (targetType == typeof(double) && newValue is not double)
                                converted = Convert.ToDouble(newValue, CultureInfo.InvariantCulture);
                            else if (targetType == typeof(bool) && newValue is string sb)
                                converted = bool.Parse(sb);

                            instrProp.SetValue(paInstance, converted);
                            Logger.Info($"Synced PolarAlignment.{instrPropName} = {converted}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Could not sync PolarAlignment.{instrPropName}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Logger.Warning("PolarAlignment instruction instance not found — values saved to settings only");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error syncing PolarAlignment instruction: {ex.Message}", ex);
            }

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", setCount > 0 },
                { "Message", $"Set {setCount} option(s)" },
                { "SetCount", setCount },
                { "Failures", failures.Count > 0 ? failures : null }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to set TPPA options: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Resets all TPPA/PolarAlignment options to their default values
    /// POST /tppa/reset
    /// </summary>
    [Route(HttpVerbs.Post, "/tppa/reset")]
    public object ResetTPPAOptions()
    {
        try
        {
            var settingsInstance = GetPolarAlignmentSettingsInstance();
            if (settingsInstance == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "PolarAlignment plugin not loaded" }
                };
            }

            var settingsType = settingsInstance.GetType();

            // Define default values for each option
            var defaults = new Dictionary<string, object>()
            {
                // Movement & Control
                { "DefaultMoveRate", 3.0 },
                { "DefaultEastDirection", true },
                { "MoveTimeoutFactor", 2.0 },

                // Targeting Parameters
                { "DefaultTargetDistance", 10 },
                { "DefaultSearchRadius", 10.0 },
                { "DefaultAzimuthOffset", 1.0 },
                { "DefaultAltitudeOffset", 2.0 },

                // Alignment Control
                { "AlignmentTolerance", 0.0 },
                { "RefractionAdjustment", false },
                { "StopTrackingWhenDone", true },

                // Automation Options
                { "AutomatedAdjustmentSettleTime", 2.0 },
                { "AutoPause", false },
                { "LogError", false }
            };

            var resetCount = 0;
            var failedResets = new List<string>();

            foreach (var kvp in defaults)
            {
                try
                {
                    var property = settingsType.GetProperty(kvp.Key,
                        BindingFlags.Public | BindingFlags.Instance);

                    if (property != null && property.CanWrite)
                    {
                        // Convert the default value to the correct type if needed (culture-invariant)
                        var convertedValue = kvp.Value;
                        if (kvp.Value != null && property.PropertyType != kvp.Value.GetType())
                        {
                            convertedValue = Convert.ChangeType(kvp.Value, property.PropertyType, CultureInfo.InvariantCulture);
                        }

                        property.SetValue(settingsInstance, convertedValue);
                        resetCount++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to reset option {kvp.Key}: {ex.Message}");
                    failedResets.Add(kvp.Key);
                }
            }

            // Save the settings
            try
            {
                var saveMethod = settingsType.GetMethod("Save",
                    BindingFlags.Public | BindingFlags.Instance);
                if (saveMethod != null)
                {
                    saveMethod.Invoke(settingsInstance, null);
                }

                // Reload settings from disk to ensure the in-memory cache is updated
                // without requiring a restart
                var reloadMethod = settingsType.GetMethod("Reload",
                    BindingFlags.Public | BindingFlags.Instance);
                if (reloadMethod != null)
                {
                    reloadMethod.Invoke(settingsInstance, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not explicitly save settings after reset: {ex.Message}");
            }

            // Sync reset values to the live PolarAlignment instruction instance
            try
            {
                var paInstance = GetPolarAlignmentInstruction();
                if (paInstance != null)
                {
                    var paType = paInstance.GetType();
                    foreach (var kvp in defaults)
                    {
                        if (!SettingsToInstructionMap.TryGetValue(kvp.Key, out var instrPropName))
                            continue;

                        var instrProp = paType.GetProperty(instrPropName,
                            BindingFlags.Public | BindingFlags.Instance);
                        if (instrProp == null || !instrProp.CanWrite)
                            continue;

                        try
                        {
                            var converted = kvp.Value;
                            if (kvp.Value != null && instrProp.PropertyType != kvp.Value.GetType())
                                converted = Convert.ChangeType(kvp.Value, instrProp.PropertyType, CultureInfo.InvariantCulture);

                            instrProp.SetValue(paInstance, converted);
                            Logger.Info($"Reset PolarAlignment.{instrPropName} = {converted}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Could not reset PolarAlignment.{instrPropName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error syncing PolarAlignment instruction reset: {ex.Message}", ex);
            }

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", $"Reset {resetCount} TPPA options to defaults" },
                { "ResetCount", resetCount },
                { "FailedResets", failedResets.Count > 0 ? failedResets : null }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to reset TPPA options: {ex.Message}" }
            };
        }
    }
}
