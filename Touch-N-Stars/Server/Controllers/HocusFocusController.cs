using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Plugins.TouchNStars.Tilter;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for HocusFocus plugin integration endpoints
/// </summary>
public class HocusFocusController : WebApiController
{
    // Reflection caching for performance - avoids repeated reflection calls
    private static readonly Dictionary<Type, MethodInfo> CanExecuteMethodCache = new();
    private static readonly object CacheLock = new object();

    /// <summary>
    /// Gets the cached CanExecute method for a command type, or retrieves and caches it if not available
    /// </summary>
    private MethodInfo GetCachedCanExecuteMethod(Type commandType)
    {
        lock (CacheLock)
        {
            if (CanExecuteMethodCache.TryGetValue(commandType, out var cachedMethod))
            {
                return cachedMethod;
            }

            var method = commandType.GetMethod("CanExecute",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(object) },
                null);

            if (method != null)
            {
                CanExecuteMethodCache[commandType] = method;
            }

            return method;
        }
    }

    /// <summary>
    /// Safely invokes CanExecute on a command object
    /// </summary>
    private bool TryGetCanExecuteState(object command)
    {
        try
        {
            var commandType = command?.GetType();
            if (commandType == null)
            {
                return false;
            }

            var canExecuteMethod = GetCachedCanExecuteMethod(commandType);
            if (canExecuteMethod == null)
            {
                return false;
            }

            return (bool)canExecuteMethod.Invoke(command, new object[] { null });
        }
        catch (TargetInvocationException ex)
        {
            Logger.Error($"CanExecute method threw exception: {ex.InnerException}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error checking CanExecute: {ex}");
            return false;
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/region-focus-points")]
    public object GetRegionFocusPoints()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to trigger detailed AutoFocus analysis
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            // Get the RegionFocusPoints from InspectorVM
            var inspectorVMType = inspectorVM.GetType();
            var regionFocusPoints = inspectorVMType.GetProperty("RegionFocusPoints");

            if (regionFocusPoints == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RegionFocusPoints not found on InspectorVM" }
                };
            }

            // Get the actual RegionFocusPoints value (array of collections)
            var regionFocusPointsValue = regionFocusPoints.GetValue(inspectorVM);
            if (regionFocusPointsValue == null)
            {
                HttpContext.Response.StatusCode = 200;
                return new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "RegionFocusPoints", Array.Empty<object>() }
                };
            }

            // Get the RegionCurveFittings and RegionLineFittings
            var regionCurveFittingsProperty = inspectorVMType.GetProperty("RegionCurveFittings");
            var regionLineFittingsProperty = inspectorVMType.GetProperty("RegionLineFittings");

            var regionCurveFittingsValue = regionCurveFittingsProperty?.GetValue(inspectorVM);
            var regionLineFittingsValue = regionLineFittingsProperty?.GetValue(inspectorVM);

            // Serialize the RegionFocusPoints arrays with curve fitting data
            var serializedRegions = new List<object>();
            var regionNames = new[] { "Full", "Center", "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

            if (regionFocusPointsValue is System.Collections.IEnumerable regionsEnumerable)
            {
                int regionIndex = 0;
                // Create a snapshot of the regions to avoid collection modification exceptions
                var regionsSnapshot = regionsEnumerable.Cast<object>().ToList();
                var curveFittingsSnapshot = (regionCurveFittingsValue is System.Collections.IEnumerable curvesEnum)
                    ? curvesEnum.OfType<object>().ToList()
                    : new List<object>();
                var lineFittingsSnapshot = (regionLineFittingsValue is System.Collections.IEnumerable linesEnum)
                    ? linesEnum.OfType<object>().ToList()
                    : new List<object>();

                foreach (var region in regionsSnapshot)
                {
                    var regionList = new List<object>();
                    if (region is System.Collections.IEnumerable regionEnumerable)
                    {
                        // Create a snapshot of the region to avoid collection modification exceptions
                        var regionSnapshot = regionEnumerable.Cast<object>().ToList();
                        foreach (var focusPoint in regionSnapshot)
                        {
                            regionList.Add(SerializeObject(focusPoint));
                        }
                    }

                    // Get curve fitting data for this region
                    var curveFitData = new Dictionary<string, object>();
                    if (regionIndex < curveFittingsSnapshot.Count && curveFittingsSnapshot[regionIndex] != null)
                    {
                        var curveFitting = curveFittingsSnapshot[regionIndex];
                        // Generate curve points from the fitting function
                        var curveFunction = curveFitting as Delegate;
                        if (curveFunction != null && regionList.Count > 0)
                        {
                            var curvePoints = new List<Dictionary<string, object>>();
                            if (regionList.Count >= 2)
                            {
                                // Get min and max X values from focus points
                                var minX = regionList.OfType<Dictionary<string, object>>()
                                    .Where(p => p.ContainsKey("X"))
                                    .Select(p =>
                                    {
                                        if (p["X"] is double xd) return xd;
                                        if (double.TryParse(p["X"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) return x;
                                        return 0.0;
                                    })
                                    .DefaultIfEmpty(0.0)
                                    .Min();
                                var maxX = regionList.OfType<Dictionary<string, object>>()
                                    .Where(p => p.ContainsKey("X"))
                                    .Select(p =>
                                    {
                                        if (p["X"] is double xd) return xd;
                                        if (double.TryParse(p["X"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) return x;
                                        return 0.0;
                                    })
                                    .DefaultIfEmpty(0.0)
                                    .Max();

                                // Generate curve points
                                var step = (maxX - minX) / 20.0; // 20 points along the curve
                                for (var x = minX; x <= maxX; x += step)
                                {
                                    try
                                    {
                                        var y = (double)curveFunction.DynamicInvoke(x);
                                        curvePoints.Add(new Dictionary<string, object> { { "X", x }, { "Y", y } });
                                    }
                                    catch { /* Skip if curve evaluation fails */ }
                                }
                            }
                            curveFitData["CurvePoints"] = curvePoints;
                        }
                    }

                    // Get line fitting data for this region
                    if (regionIndex < lineFittingsSnapshot.Count && lineFittingsSnapshot[regionIndex] != null)
                    {
                        curveFitData["LineFitting"] = SerializeObject(lineFittingsSnapshot[regionIndex]);
                    }

                    var regionName = regionIndex < regionNames.Length ? regionNames[regionIndex] : $"Region{regionIndex}";
                    serializedRegions.Add(new Dictionary<string, object>()
                    {
                        { "regionName", regionName },
                        { "focusPoints", regionList },
                        { "curveFit", curveFitData }
                    });

                    regionIndex++;
                }
            }

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "regionFocusPoints", serializedRegions }
            };

        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch region focus points: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/final-focus-data")]
    public object GetFinalFocusData()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            var inspectorVMType = inspectorVM.GetType();

            // Get the RegionFinalFocusPoints
            var regionFinalFocusPoints = inspectorVMType.GetProperty("RegionFinalFocusPoints");
            if (regionFinalFocusPoints == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RegionFinalFocusPoints not found on InspectorVM" }
                };
            }

            // Serialize RegionFinalFocusPoints (collection of DataPoints)
            var serializedPoints = new List<object>();
            var regionFinalFocusPointsValue = regionFinalFocusPoints.GetValue(inspectorVM);
            if (regionFinalFocusPointsValue != null && regionFinalFocusPointsValue is System.Collections.IEnumerable pointsEnumerable)
            {
                foreach (var focusPoint in pointsEnumerable)
                {
                    serializedPoints.Add(SerializeObject(focusPoint));
                }
            }

            // Get backfocus error data
            var backfocusFocuserPositionDelta = inspectorVMType.GetProperty("BackfocusFocuserPositionDelta")?.GetValue(inspectorVM);
            var backfocusMicronDelta = inspectorVMType.GetProperty("BackfocusMicronDelta")?.GetValue(inspectorVM);
            var backfocusDirection = inspectorVMType.GetProperty("BackfocusDirection")?.GetValue(inspectorVM);
            var criticalFocusMicrons = inspectorVMType.GetProperty("CriticalFocusMicrons")?.GetValue(inspectorVM);
            var backfocusWithinCFZ = inspectorVMType.GetProperty("BackfocusWithinCFZ")?.GetValue(inspectorVM);

            // Get HFR-related data
            var backfocusHFR = inspectorVMType.GetProperty("BackfocusHFR")?.GetValue(inspectorVM);
            var innerHFR = inspectorVMType.GetProperty("InnerHFR")?.GetValue(inspectorVM);
            var outerHFR = inspectorVMType.GetProperty("OuterHFR")?.GetValue(inspectorVM);
            var innerPosition = inspectorVMType.GetProperty("InnerFocuserPosition")?.GetValue(inspectorVM);
            var outerPosition = inspectorVMType.GetProperty("OuterFocuserPosition")?.GetValue(inspectorVM);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "RegionFinalFocusPoints", serializedPoints },
                { "BackfocusFocuserPositionDelta", backfocusFocuserPositionDelta ?? double.NaN },
                { "BackfocusMicronDelta", backfocusMicronDelta ?? double.NaN },
                { "BackfocusDirection", backfocusDirection ?? "" },
                { "CriticalFocusMicrons", criticalFocusMicrons ?? double.NaN },
                { "BackfocusWithinCFZ", backfocusWithinCFZ ?? true },
                { "BackfocusHFR", backfocusHFR ?? double.NaN },
                { "InnerHFR", innerHFR ?? double.NaN },
                { "OuterHFR", outerHFR ?? double.NaN },
                { "InnerPosition", innerPosition ?? double.NaN },
                { "OuterPosition", outerPosition ?? double.NaN }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch final focus data: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/status")]
    public object GetStatus()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            var inspectorVMType = inspectorVM.GetType();

            // Get status data
            var autoFocusCompleted = inspectorVMType.GetProperty("AutoFocusCompleted")?.GetValue(inspectorVM);
            var autoFocusAnalysisProgressOrResult = inspectorVMType.GetProperty("AutoFocusAnalysisProgressOrResult")?.GetValue(inspectorVM);
            var autoFocusAnalysisResult = inspectorVMType.GetProperty("AutoFocusAnalysisResult")?.GetValue(inspectorVM);
            var exposureAnalysisResult = inspectorVMType.GetProperty("ExposureAnalysisResult")?.GetValue(inspectorVM);
            var sensorCurveModelActive = inspectorVMType.GetProperty("SensorCurveModelActive")?.GetValue(inspectorVM);
            var tiltMeasurementActive = inspectorVMType.GetProperty("TiltMeasurementActive")?.GetValue(inspectorVM);
            var tiltMeasurementHistoryActive = inspectorVMType.GetProperty("TiltMeasurementHistoryActive")?.GetValue(inspectorVM);
            var autoFocusChartActive = inspectorVMType.GetProperty("AutoFocusChartActive")?.GetValue(inspectorVM);
            var autoFocusChartActivatedOnce = inspectorVMType.GetProperty("AutoFocusChartActivatedOnce")?.GetValue(inspectorVM);
            var fWHMContoursActive = inspectorVMType.GetProperty("FWHMContoursActive")?.GetValue(inspectorVM);
            var eccentricityVectorsActive = inspectorVMType.GetProperty("EccentricityVectorsActive")?.GetValue(inspectorVM);

            // Fetch command and get CanExecute state using cached method
            var runAFAnalysisCommand = inspectorVMType.GetProperty("RunAutoFocusAnalysisCommand")?.GetValue(inspectorVM);
            var runAFAnalysisState = TryGetCanExecuteState(runAFAnalysisCommand);

            var rerunSavedAFCommand = inspectorVMType.GetProperty("RerunSavedAutoFocusAnalysisCommand")?.GetValue(inspectorVM);
            var rerunSavedAFState = TryGetCanExecuteState(rerunSavedAFCommand);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "CanRunAutoFocusAnalysis", runAFAnalysisState },
                { "CanRerunSavedAutoFocusAnalysis", rerunSavedAFState },
                { "AutoFocusCompleted", autoFocusCompleted ?? false },
                { "AutoFocusAnalysisProgressOrResult", autoFocusAnalysisProgressOrResult ?? false },
                { "AutoFocusAnalysisResult", autoFocusAnalysisResult ?? false },
                { "ExposureAnalysisResult", exposureAnalysisResult ?? false },
                { "SensorCurveModelActive", sensorCurveModelActive ?? false },
                { "TiltMeasurementActive", tiltMeasurementActive ?? false },
                { "TiltMeasurementHistoryActive", tiltMeasurementHistoryActive ?? false },
                { "AutoFocusChartActive", autoFocusChartActive ?? false },
                { "AutoFocusChartActivatedOnce", autoFocusChartActivatedOnce ?? false },
                { "FWHMContoursActive", fWHMContoursActive ?? false },
                { "EccentricityVectorsActive", eccentricityVectorsActive ?? false }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch status data: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/run-detailed-af")]
    public object RunDetailedAutoFocus()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to trigger detailed AutoFocus analysis
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            // Get the RunAutoFocusAnalysisCommand from InspectorVM and execute it
            var inspectorVMType = inspectorVM.GetType();
            var commandProperty = inspectorVMType.GetProperty("RunAutoFocusAnalysisCommand");

            if (commandProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RunAutoFocusAnalysisCommand not found on InspectorVM" }
                };
            }

            var command = commandProperty.GetValue(inspectorVM);
            if (command == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RunAutoFocusAnalysisCommand is null" }
                };
            }

            // Get command type
            var commandType = command.GetType();

            // Check if the command can be executed using cached method
            bool canExecute = TryGetCanExecuteState(command);

            if (!canExecute)
            {
                HttpContext.Response.StatusCode = 409;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RunAutoFocusAnalysisCommand cannot be executed at this time" }
                };
            }

            // Execute the command
            var executeMethod = commandType.GetMethod("Execute", new[] { typeof(object) });

            if (executeMethod == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Could not find Execute method on RunAutoFocusAnalysisCommand" }
                };
            }

            executeMethod.Invoke(command, new object[] { null });

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", "HocusFocus detailed AutoFocus analysis started" },
                { "Status", "running" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to start HocusFocus analysis: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/re-run-detailed-af")]
    public async Task<object> ReRunDetailedAutoFocus()
    {
        try
        {
            // Parse the request body to get the optional afDirectory
            string afDirectory = null;
            try
            {
                using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
                {
                    var jsonStr = await reader.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(jsonStr))
                    {
                        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonStr, jsonOptions);
                        if (payload != null && payload.TryGetValue("afDirectory", out var dir))
                        {
                            afDirectory = dir;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AutoFocus] Error parsing afDirectory from request: {ex.Message}");
            }

            // Access HocusFocus InspectorVM via reflection to trigger detailed AutoFocus analysis
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            // If afDirectory is provided, set it on the plugin so AnalyzeSavedAutoFocusRun can use it
            if (!string.IsNullOrEmpty(afDirectory))
            {
                var selectedAFDirProperty = hocusFocusPluginType.GetProperty("SelectedAFDirectory",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (selectedAFDirProperty != null && selectedAFDirProperty.CanWrite)
                {
                    selectedAFDirProperty.SetValue(null, afDirectory);
                    Logger.Debug($"[AutoFocus] Set SelectedAFDirectory to: {afDirectory}");
                }
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            // Get the ReRunAutoFocusAnalysisCommand from InspectorVM and execute it
            var inspectorVMType = inspectorVM.GetType();
            var commandProperty = inspectorVMType.GetProperty("RerunSavedAutoFocusAnalysisCommand");

            if (commandProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RerunSavedAutoFocusAnalysisCommand not found on InspectorVM" }
                };
            }

            var command = commandProperty.GetValue(inspectorVM);
            if (command == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RerunSavedAutoFocusAnalysisCommand is null" }
                };
            }

            // Get command type
            var commandType = command.GetType();

            // Check if the command can be executed using cached method
            bool canExecute = TryGetCanExecuteState(command);

            if (!canExecute)
            {
                HttpContext.Response.StatusCode = 409;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "RerunSavedAutoFocusAnalysisCommand cannot be executed at this time" }
                };
            }

            // Execute the command
            var executeMethod = commandType.GetMethod("Execute", new[] { typeof(object) });

            if (executeMethod == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Could not find Execute method on RerunSavedAutoFocusAnalysisCommand" }
                };
            }

            executeMethod.Invoke(command, new object[] { null });

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", "HocusFocus detailed AutoFocus analysis started" },
                { "Status", "running" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to start HocusFocus analysis: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/cancel-detailed-af")]
    public object CancelDetailedAutoFocus()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to cancel analysis
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            // Get the CancelAnalyzeCommand from InspectorVM
            var inspectorVMType = inspectorVM.GetType();
            var commandProperty = inspectorVMType.GetProperty("CancelAnalyzeCommand");

            if (commandProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "CancelAnalyzeCommand not found on InspectorVM" }
                };
            }

            var command = commandProperty.GetValue(inspectorVM);
            if (command == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "CancelAnalyzeCommand is null" }
                };
            }

            // Get command type
            var commandType = command.GetType();

            // Check if the command can be executed using cached method
            bool canExecute = TryGetCanExecuteState(command);

            if (!canExecute)
            {
                HttpContext.Response.StatusCode = 409;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "CancelAnalyzeCommand cannot be executed at this time" }
                };
            }

            // Execute the command
            var executeMethod = commandType.GetMethod("Execute", new[] { typeof(object) });

            if (executeMethod == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Could not find Execute method on CancelAnalyzeCommand" }
                };
            }

            executeMethod.Invoke(command, new object[] { null });

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", "HocusFocus detailed AutoFocus analysis cancelled" },
                { "Status", "cancelled" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to cancel HocusFocus analysis: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/clear-detailed-af")]
    public object ClearDetailedAutoFocus()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to trigger detailed AutoFocus analysis
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM not accessible" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            // Get the ClearAutoFocusAnalysisCommand from InspectorVM and execute it
            var inspectorVMType = inspectorVM.GetType();
            var commandProperty = inspectorVMType.GetProperty("ClearAnalysesCommand");

            if (commandProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "ClearAnalysesCommand not found on InspectorVM" }
                };
            }

            var command = commandProperty.GetValue(inspectorVM);
            if (command == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "ClearAnalysesCommand is null" }
                };
            }

            // Get command type
            var commandType = command.GetType();

            // Check if the command can be executed using cached method
            bool canExecute = TryGetCanExecuteState(command);

            if (!canExecute)
            {
                HttpContext.Response.StatusCode = 409;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "ClearAnalysesCommand cannot be executed at this time" }
                };
            }

            // Execute the command
            var executeMethod = commandType.GetMethod("Execute", new[] { typeof(object) });

            if (executeMethod == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Could not find Execute method on ClearAnalysesCommand" }
                };
            }

            executeMethod.Invoke(command, new object[] { null });

            // Explicitly clear tilt measurement history after clearing analyses
            try
            {
                var tiltModelProperty = inspectorVMType.GetProperty("TiltModel");
                if (tiltModelProperty != null)
                {
                    var tiltModel = tiltModelProperty.GetValue(inspectorVM);
                    if (tiltModel != null)
                    {
                        var tiltModelType = tiltModel.GetType();
                        var historyProperty = tiltModelType.GetProperty("SensorTiltHistoryModels");
                        if (historyProperty != null)
                        {
                            var historyModels = historyProperty.GetValue(tiltModel);
                            if (historyModels != null)
                            {
                                var clearMethod = historyModels.GetType().GetMethod("Clear");
                                if (clearMethod != null)
                                {
                                    clearMethod.Invoke(historyModels, null);
                                    Logger.Debug("[ClearDetailedAutoFocus] Tilt measurement history cleared");
                                }
                            }
                        }

                        // Reset the nextHistoryId counter so history IDs start from 1 again
                        try
                        {
                            var nextHistoryIdField = tiltModelType.GetField("nextHistoryId",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (nextHistoryIdField != null)
                            {
                                nextHistoryIdField.SetValue(tiltModel, 0);
                                Logger.Debug("[ClearDetailedAutoFocus] Tilt history ID counter reset to 0");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"[ClearDetailedAutoFocus] Could not reset nextHistoryId: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ClearDetailedAutoFocus] Could not clear tilt history explicitly: {ex.Message}");
                // Don't fail the clear operation if we can't clear history explicitly
            }

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", "HocusFocus clear AutoFocus analysis started" },
                { "Status", "running" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to start HocusFocus clear analysis: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/list-af")]
    public object ListAutoFocus()
    {
        try
        {
            // Access HocusFocus AutoFocusOptions via reflection to get the save path
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var autoFocusOptionsProperty = hocusFocusPluginType.GetProperty("AutoFocusOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (autoFocusOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "AutoFocusOptions not accessible" }
                };
            }

            var autoFocusOptions = autoFocusOptionsProperty.GetValue(null);
            if (autoFocusOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "AutoFocusOptions instance not available" }
                };
            }

            var savePathProperty = autoFocusOptions.GetType().GetProperty("SavePath");
            if (savePathProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "SavePath property not found" }
                };
            }

            var savePath = savePathProperty.GetValue(autoFocusOptions) as string;
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "DirectoryNames", new List<string>() },
                    { "Message", "No save path configured" }
                };
            }

            if (!Directory.Exists(savePath))
            {
                return new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "DirectoryNames", new List<string>() },
                    { "Message", "Save path does not exist" }
                };
            }

            // Get all subdirectories containing "attempt" in their names (nested one level deeper)
            var attemptDirectories = new List<string>();
            var runDirectories = Directory.GetDirectories(savePath);

            foreach (var runDir in runDirectories)
            {
                var attemptDirs = Directory.GetDirectories(runDir)
                    .Where(dir => Path.GetFileName(dir).Contains("attempt", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var attemptDir in attemptDirs)
                {
                    // Combine parent folder name with attempt folder name for the full path reference
                    var parentName = new DirectoryInfo(runDir).Name;
                    var attemptName = new DirectoryInfo(attemptDir).Name;
                    attemptDirectories.Add(Path.Combine(parentName, attemptName));
                }
            }

            var directories = attemptDirectories
                .OrderByDescending(name => name) // Sort in descending order (newest first)
                .ToList();

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "DirectoryNames", directories },
                { "SavePath", savePath }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Error listing AutoFocus directories", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/autofocus/last-run")]
    public object GetLastAutoFocusRun()
    {
        try
        {
            var hocusFocusVMType = Type.GetType("NINA.Joko.Plugins.HocusFocus.AutoFocus.HocusFocusVM, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusVMType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var currentProperty = hocusFocusVMType.GetProperty("Current",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var currentVM = currentProperty?.GetValue(null);
            if (currentVM == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "No AutoFocus run has been started yet in this session" }
                };
            }

            var lastReport = currentVM.GetType().GetProperty("LastReport")?.GetValue(currentVM);
            if (lastReport == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "No AutoFocus run has completed yet" }
                };
            }

            object Get(object obj, string name) => obj?.GetType().GetProperty(name)?.GetValue(obj);
            double GetDouble(object obj, string name) => Get(obj, name) is double d ? d : double.NaN;
            int GetInt(object obj, string name) => Get(obj, name) is int i ? i : -1;

            var durationObj = Get(lastReport, "Duration");
            var rSquaresObj = Get(lastReport, "RSquares");
            var calcFocusPoint = Get(lastReport, "CalculatedFocusPoint");

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Timestamp",              Get(lastReport, "Timestamp") },
                { "Filter",                 Get(lastReport, "Filter") },
                { "Temperature",            GetDouble(lastReport, "Temperature") },
                { "DurationSeconds",        durationObj is TimeSpan ts ? ts.TotalSeconds : double.NaN },
                { "InitialFocuserPosition", GetInt(currentVM, "InitialFocuserPosition") },
                { "FinalFocuserPosition",   GetInt(currentVM, "FinalFocuserPosition") },
                { "InitialHFR",             GetDouble(currentVM, "InitialHFR") },
                { "FinalHFR",               GetDouble(currentVM, "FinalHFR") },
                { "EstimatedFinalHFR",      GetDouble(calcFocusPoint, "Value") },
                { "Fitting",                Get(lastReport, "Fitting")?.ToString() },
                { "RSquares", rSquaresObj == null ? null : new Dictionary<string, object>
                    {
                        { "Hyperbolic", GetDouble(rSquaresObj, "Hyperbolic") },
                        { "Quadratic",  GetDouble(rSquaresObj, "Quadratic") },
                        { "LeftTrend",  GetDouble(rSquaresObj, "LeftTrend") },
                        { "RightTrend", GetDouble(rSquaresObj, "RightTrend") }
                    }
                },
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to read last AutoFocus run: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/autofocus/options")]
    public object GetAutoFocusOptions()
    {
        try
        {
            // Access HocusFocus AutoFocusOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var autoFocusOptionsProperty = hocusFocusPluginType.GetProperty("AutoFocusOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (autoFocusOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "AutoFocusOptions not accessible" }
                };
            }

            var autoFocusOptions = autoFocusOptionsProperty.GetValue(null);
            if (autoFocusOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "AutoFocusOptions instance not available" }
                };
            }

            // Reflect over all properties and build a dictionary
            var optionsDict = new Dictionary<string, object>();
            var enumOptionsDict = new Dictionary<string, string[]>();
            var optionsType = autoFocusOptions.GetType();
            foreach (var prop in optionsType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    var value = prop.GetValue(autoFocusOptions);
                    if (prop.PropertyType.IsEnum)
                    {
                        optionsDict[prop.Name] = value?.ToString();
                        enumOptionsDict[prop.Name] = Enum.GetNames(prop.PropertyType);
                    }
                    else
                    {
                        optionsDict[prop.Name] = value;
                    }
                }
                catch
                {
                    // Skip properties that can't be read
                }
            }

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Options", optionsDict },
                { "EnumOptions", enumOptionsDict }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Error getting AutoFocus options", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/autofocus/options/{optionName}")]
    public async Task<object> SetAutoFocusOption(string optionName)
    {
        try
        {
            // Parse the request body to get the new value
            object newValue = null;
            string jsonStr = null;
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                jsonStr = await reader.ReadToEndAsync();
                Logger.Debug($"[AutoFocus] SetAutoFocusOption {optionName} received: {jsonStr}");
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    // Parse as a simple value wrapper
                    var valueDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr, jsonOptions);
                    if (valueDict != null && valueDict.ContainsKey("value"))
                    {
                        newValue = valueDict["value"];
                    }
                }
            }

            if (newValue == null && jsonStr != null && !jsonStr.Contains("null"))
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = "Value not provided in request body" };
            }

            // Access HocusFocus AutoFocusOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "HocusFocus plugin not loaded" };
            }

            var autoFocusOptionsProperty = hocusFocusPluginType.GetProperty("AutoFocusOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (autoFocusOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "AutoFocusOptions not accessible" };
            }

            var autoFocusOptions = autoFocusOptionsProperty.GetValue(null);
            if (autoFocusOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "AutoFocusOptions instance not available" };
            }

            // Find and set the property
            var optionsType = autoFocusOptions.GetType();
            var prop = optionsType.GetProperty(optionName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

            if (prop == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Option '{optionName}' not found" };
            }

            if (!prop.CanWrite)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Option '{optionName}' is read-only" };
            }

            try
            {
                // Convert the value to the correct type
                var targetType = prop.PropertyType;
                object convertedValue = null;
                Logger.Debug($"[AutoFocus] Converting {optionName} to type {targetType.Name}: raw value={newValue} (type={newValue?.GetType().Name ?? "null"})");

                if (newValue == null)
                {
                    convertedValue = null;
                }
                else if (targetType == typeof(string))
                {
                    convertedValue = newValue.ToString();
                }
                else if (targetType == typeof(bool))
                {
                    if (newValue is bool boolVal)
                    {
                        convertedValue = boolVal;
                    }
                    else if (newValue is JsonElement elem)
                    {
                        convertedValue = elem.GetBoolean();
                    }
                    else if (newValue is string strVal)
                    {
                        convertedValue = bool.Parse(strVal);
                    }
                    else
                    {
                        convertedValue = Convert.ToBoolean(newValue);
                    }
                }
                else if (targetType.IsEnum)
                {
                    if (newValue is JsonElement jelem)
                    {
                        convertedValue = Enum.Parse(targetType, jelem.GetString(), ignoreCase: true);
                    }
                    else
                    {
                        convertedValue = Enum.Parse(targetType, newValue.ToString(), ignoreCase: true);
                    }
                }
                else if (targetType == typeof(int) || targetType == typeof(double) || targetType == typeof(float) ||
                         targetType == typeof(decimal) || targetType == typeof(long) || targetType == typeof(short) ||
                         targetType == typeof(uint) || targetType == typeof(ulong) || targetType == typeof(ushort))
                {
                    if (newValue is JsonElement jelem)
                    {
                        if (jelem.ValueKind == JsonValueKind.Number)
                        {
                            convertedValue = Convert.ChangeType(jelem.GetDecimal(), targetType);
                        }
                        else
                        {
                            convertedValue = Convert.ChangeType(jelem.ToString(), targetType);
                        }
                    }
                    else
                    {
                        convertedValue = Convert.ChangeType(newValue, targetType);
                    }
                }
                else
                {
                    convertedValue = newValue;
                }

                try
                {
                    prop.SetValue(autoFocusOptions, convertedValue);
                    Logger.Debug($"[AutoFocus] Successfully set {optionName} = {convertedValue}");
                    return new { Success = true, Message = $"Option '{optionName}' updated successfully", Value = convertedValue };
                }
                catch (TargetInvocationException tiex) when (tiex.InnerException != null)
                {
                    // Unwrap TargetInvocationException from property setter validation
                    var innerEx = tiex.InnerException;
                    Logger.Error($"[AutoFocus] Validation error setting {optionName} to {convertedValue}: {innerEx.Message}", innerEx);
                    HttpContext.Response.StatusCode = 400;
                    return new { Success = false, Error = $"Validation error: {innerEx.Message}" };
                }
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[AutoFocus] Error during conversion/setting {optionName}: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to set option: {innerEx.Message}" };
            }
            catch (Exception ex)
            {
                Logger.Error($"[AutoFocus] Error setting {optionName}: {ex.Message}", ex);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to set option: {ex.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error setting AutoFocus option", ex);
            HttpContext.Response.StatusCode = 500;
            return new { Success = false, Error = ex.Message };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/autofocus/reset-defaults")]
    public object ResetAutoFocusDefaults()
    {
        try
        {
            // Access HocusFocus AutoFocusOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "HocusFocus plugin not loaded" };
            }

            var autoFocusOptionsProperty = hocusFocusPluginType.GetProperty("AutoFocusOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (autoFocusOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "AutoFocusOptions not accessible" };
            }

            var autoFocusOptions = autoFocusOptionsProperty.GetValue(null);
            if (autoFocusOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "AutoFocusOptions instance not available" };
            }

            // Call ResetDefaults method
            var resetMethod = autoFocusOptions.GetType().GetMethod("ResetDefaults",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (resetMethod == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = "ResetDefaults method not found" };
            }

            try
            {
                resetMethod.Invoke(autoFocusOptions, null);
                Logger.Debug("[AutoFocus] Successfully reset all options to defaults");
                return new { Success = true, Message = "All AutoFocus options have been reset to defaults" };
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[AutoFocus] Error resetting defaults: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to reset defaults: {innerEx.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error resetting AutoFocus options to defaults", ex);
            HttpContext.Response.StatusCode = 500;
            return new { Success = false, Error = ex.Message };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/tilt-corner-measurements")]
    public object GetTiltCornerMeasurements()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to get TiltModel measurements
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM property not found" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            var inspectorVMType = inspectorVM.GetType();
            var tiltModelProperty = inspectorVMType.GetProperty("TiltModel");
            if (tiltModelProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "TiltModel property not found on InspectorVM" }
                };
            }

            var tiltModel = tiltModelProperty.GetValue(inspectorVM);
            if (tiltModel == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "TiltModel instance not available" }
                };
            }

            var tiltModelType = tiltModel.GetType();
            var sensorTiltModelsProperty = tiltModelType.GetProperty("SensorTiltModels");
            if (sensorTiltModelsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "SensorTiltModels property not found on TiltModel" }
                };
            }

            var sensorTiltModels = sensorTiltModelsProperty.GetValue(tiltModel);
            if (sensorTiltModels == null)
            {
                return new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "tiltCornerMeasurements", new List<Dictionary<string, object>>() }
                };
            }

            var cornersArray = new List<Dictionary<string, object>>();

            // Iterate through the SensorTiltModels collection
            foreach (var sensorTiltModel in (System.Collections.IEnumerable)sensorTiltModels)
            {
                var cornerData = new Dictionary<string, object>();
                var sensorTiltModelType = sensorTiltModel.GetType();

                // Extract SensorSide (enum value)
                var sensorSideProperty = sensorTiltModelType.GetProperty("SensorSide");
                if (sensorSideProperty != null)
                {
                    var sensorSideValue = sensorSideProperty.GetValue(sensorTiltModel);
                    cornerData["sensorSide"] = sensorSideValue?.ToString() ?? "Unknown";
                }
                else
                {
                    cornerData["sensorSide"] = "Unknown";
                }

                // Extract FocuserPosition (double)
                var focuserPositionProperty = sensorTiltModelType.GetProperty("FocuserPosition");
                if (focuserPositionProperty != null)
                {
                    var focuserPositionValue = focuserPositionProperty.GetValue(sensorTiltModel);
                    cornerData["focuserPosition"] = focuserPositionValue != null ? Convert.ToDouble(focuserPositionValue) : double.NaN;
                }
                else
                {
                    cornerData["focuserPosition"] = double.NaN;
                }

                // Extract AdjustmentRequiredSteps (double)
                var adjustmentStepsProperty = sensorTiltModelType.GetProperty("AdjustmentRequiredSteps");
                if (adjustmentStepsProperty != null)
                {
                    var adjustmentStepsValue = adjustmentStepsProperty.GetValue(sensorTiltModel);
                    cornerData["adjustmentRequiredSteps"] = adjustmentStepsValue != null ? Convert.ToDouble(adjustmentStepsValue) : double.NaN;
                }
                else
                {
                    cornerData["adjustmentRequiredSteps"] = double.NaN;
                }

                // Extract AdjustmentRequiredMicrons (double)
                var adjustmentMicronsProperty = sensorTiltModelType.GetProperty("AdjustmentRequiredMicrons");
                if (adjustmentMicronsProperty != null)
                {
                    var adjustmentMicronsValue = adjustmentMicronsProperty.GetValue(sensorTiltModel);
                    cornerData["adjustmentRequiredMicrons"] = adjustmentMicronsValue != null ? Convert.ToDouble(adjustmentMicronsValue) : double.NaN;
                }
                else
                {
                    cornerData["adjustmentRequiredMicrons"] = double.NaN;
                }

                // Extract RSquared (double - fit quality metric)
                var rSquaredProperty = sensorTiltModelType.GetProperty("RSquared");
                if (rSquaredProperty != null)
                {
                    var rSquaredValue = rSquaredProperty.GetValue(sensorTiltModel);
                    cornerData["rSquared"] = rSquaredValue != null ? Convert.ToDouble(rSquaredValue) : double.NaN;
                }
                else
                {
                    cornerData["rSquared"] = double.NaN;
                }

                cornersArray.Add(cornerData);
            }

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "tiltCornerMeasurements", cornersArray }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch tilt corner measurements: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/tilt-measurement-history")]
    public object GetTiltMeasurementHistory()
    {
        try
        {
            // Access HocusFocus InspectorVM via reflection to get TiltModel measurement history
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var inspectorVMProperty = hocusFocusPluginType.GetProperty("InspectorVM");
            if (inspectorVMProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM property not found" }
                };
            }

            var inspectorVM = inspectorVMProperty.GetValue(null);
            if (inspectorVM == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus InspectorVM instance not available" }
                };
            }

            var inspectorVMType = inspectorVM.GetType();
            var tiltModelProperty = inspectorVMType.GetProperty("TiltModel");
            if (tiltModelProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "TiltModel property not found on InspectorVM" }
                };
            }

            var tiltModel = tiltModelProperty.GetValue(inspectorVM);
            if (tiltModel == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "TiltModel instance not available" }
                };
            }

            var tiltModelType = tiltModel.GetType();
            var historyProperty = tiltModelType.GetProperty("SensorTiltHistoryModels");
            if (historyProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "SensorTiltHistoryModels property not found on TiltModel" }
                };
            }

            var historyModels = historyProperty.GetValue(tiltModel);
            if (historyModels == null)
            {
                return new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "tiltMeasurementHistory", new List<Dictionary<string, object>>() }
                };
            }

            var historyArray = new List<Dictionary<string, object>>();

            // Iterate through the history collection
            foreach (var historyModel in (System.Collections.IEnumerable)historyModels)
            {
                var historyModelType = historyModel.GetType();
                var historyData = new Dictionary<string, object>();

                // Extract HistoryId
                var historyIdProperty = historyModelType.GetProperty("HistoryId");
                if (historyIdProperty != null)
                {
                    var historyIdValue = historyIdProperty.GetValue(historyModel);
                    historyData["historyId"] = historyIdValue?.ToString() ?? "";
                }

                // Extract BackfocusFocuserPositionDelta
                var backfocusDeltaProperty = historyModelType.GetProperty("BackfocusFocuserPositionDelta");
                if (backfocusDeltaProperty != null)
                {
                    var backfocusDeltaValue = backfocusDeltaProperty.GetValue(historyModel);
                    historyData["backfocusSteps"] = backfocusDeltaValue != null ? Convert.ToDouble(backfocusDeltaValue) : double.NaN;
                }
                else
                {
                    historyData["backfocusSteps"] = double.NaN;
                }

                // Extract TiltPlaneModel (contains Center, TopLeft, TopRight, BottomLeft, BottomRight)
                var tiltPlaneProperty = historyModelType.GetProperty("TiltPlaneModel");
                if (tiltPlaneProperty != null)
                {
                    var tiltPlaneModel = tiltPlaneProperty.GetValue(historyModel);
                    if (tiltPlaneModel != null)
                    {
                        var tiltPlaneType = tiltPlaneModel.GetType();
                        var cornerNames = new[] { "Center", "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

                        foreach (var cornerName in cornerNames)
                        {
                            var cornerProperty = tiltPlaneType.GetProperty(cornerName);
                            if (cornerProperty != null)
                            {
                                var cornerModel = cornerProperty.GetValue(tiltPlaneModel);
                                if (cornerModel != null)
                                {
                                    var cornerModelType = cornerModel.GetType();

                                    // Extract AdjustmentRequiredSteps for corners (not Center)
                                    if (cornerName != "Center")
                                    {
                                        var adjustmentStepsProperty = cornerModelType.GetProperty("AdjustmentRequiredSteps");
                                        if (adjustmentStepsProperty != null)
                                        {
                                            var adjustmentStepsValue = adjustmentStepsProperty.GetValue(cornerModel);
                                            var stepFieldName = $"{cornerName.ToLower()}AdjustmentSteps";
                                            historyData[stepFieldName] = adjustmentStepsValue != null ? Convert.ToDouble(adjustmentStepsValue) : double.NaN;
                                        }
                                    }

                                    // Extract RSquared for all corners
                                    var rSquaredProperty = cornerModelType.GetProperty("RSquared");
                                    if (rSquaredProperty != null)
                                    {
                                        var rSquaredValue = rSquaredProperty.GetValue(cornerModel);
                                        var r2FieldName = $"{cornerName.ToLower()}RSquared";
                                        historyData[r2FieldName] = rSquaredValue != null ? Convert.ToDouble(rSquaredValue) : double.NaN;
                                    }
                                }
                            }
                        }
                    }
                }

                historyArray.Add(historyData);
            }

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "tiltMeasurementHistory", historyArray }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to fetch tilt measurement history: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Serializes an object to a dictionary containing its primitive properties
    /// </summary>
    private object SerializeObject(object obj)
    {
        if (obj == null)
            return null;

        var objType = obj.GetType();

        // Handle basic types
        if (objType.IsPrimitive || objType == typeof(string) || objType == typeof(decimal))
            return obj;

        // Handle enums
        if (objType.IsEnum)
            return obj.ToString();

        // Try to extract properties as a dictionary
        try
        {
            var props = new Dictionary<string, object>();
            foreach (var prop in objType.GetProperties())
            {
                if (prop.GetIndexParameters().Length == 0 && prop.CanRead) // Skip indexed properties
                {
                    try
                    {
                        var propValue = prop.GetValue(obj);
                        if (propValue != null)
                        {
                            var propType = propValue.GetType();
                            if (propType.IsPrimitive || propType == typeof(string) || propType == typeof(decimal) || propType.IsEnum)
                            {
                                props[prop.Name] = propValue;
                            }
                        }
                    }
                    catch
                    {
                        // Skip properties that can't be accessed
                    }
                }
            }
            return props.Count > 0 ? (object)props : obj.ToString();
        }
        catch
        {
            return obj.ToString();
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/star-detection/options")]
    public async Task<object> GetStarDetectionOptions()
    {
        try
        {
            var options = StarDetectionOptionsService.GetHocusFocusStarDetectionOptions();
            if (options != null)
            {
                return options;
            }
            else
            {
                HttpContext.Response.StatusCode = 500;
                return new { error = "Failed to get StarDetectionOptions" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error getting StarDetectionOptions", ex);
            HttpContext.Response.StatusCode = 500;
            return new { error = ex.Message };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/star-detection/reset-defaults")]
    public async Task<object> ResetStarDetectionDefaults()
    {
        try
        {
            // Access HocusFocus StarDetectionOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "HocusFocus plugin not loaded" };
            }

            var starDetectionOptionsProperty = hocusFocusPluginType.GetProperty("StarDetectionOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (starDetectionOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "StarDetectionOptions not accessible" };
            }

            var starDetectionOptions = starDetectionOptionsProperty.GetValue(null);
            if (starDetectionOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "StarDetectionOptions instance not available" };
            }

            // Call ResetDefaults method
            var resetMethod = starDetectionOptions.GetType().GetMethod("ResetDefaults",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (resetMethod == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "ResetDefaults method not found" };
            }

            try
            {
                resetMethod.Invoke(starDetectionOptions, null);
                Logger.Debug("[StarDetection] Successfully reset all options to defaults");
                return new { success = true, message = "All StarDetection options have been reset to defaults" };
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[StarDetection] Error resetting defaults: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = $"Failed to reset defaults: {innerEx.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error resetting StarDetection options to defaults", ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/star-detection/options/{optionName}")]
    public async Task<object> SetStarDetectionOption(string optionName)
    {
        try
        {
            // Parse the request body to get the new value
            object newValue = null;
            string jsonStr = null;
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                jsonStr = await reader.ReadToEndAsync();
                Logger.Debug($"[StarDetection] SetStarDetectionOption {optionName} received: {jsonStr}");
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    // Parse as a simple value wrapper
                    var valueDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr, jsonOptions);
                    if (valueDict != null && valueDict.ContainsKey("value"))
                    {
                        newValue = valueDict["value"];
                    }
                }
            }

            if (newValue == null && jsonStr != null && !jsonStr.Contains("null"))
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Value not provided in request body" };
            }

            // Access HocusFocus StarDetectionOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "HocusFocus plugin not loaded" };
            }

            var starDetectionOptionsProperty = hocusFocusPluginType.GetProperty("StarDetectionOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (starDetectionOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "StarDetectionOptions not accessible" };
            }

            var starDetectionOptions = starDetectionOptionsProperty.GetValue(null);
            if (starDetectionOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { success = false, error = "StarDetectionOptions instance not available" };
            }

            // Find and set the property
            var optionsType = starDetectionOptions.GetType();
            var prop = optionsType.GetProperty(optionName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

            if (prop == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = $"Option '{optionName}' not found" };
            }

            if (!prop.CanWrite)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = $"Option '{optionName}' is read-only" };
            }

            try
            {
                // Convert the value to the correct type
                var targetType = prop.PropertyType;
                object convertedValue = null;
                Logger.Debug($"[StarDetection] Converting {optionName} to type {targetType.Name}: raw value={newValue} (type={newValue?.GetType().Name ?? "null"})");

                if (newValue == null)
                {
                    convertedValue = null;
                }
                else if (targetType == typeof(string))
                {
                    convertedValue = newValue.ToString();
                }
                else if (targetType == typeof(bool))
                {
                    if (newValue is bool boolVal)
                    {
                        convertedValue = boolVal;
                    }
                    else if (newValue is JsonElement elem)
                    {
                        convertedValue = elem.GetBoolean();
                    }
                    else if (newValue is string strVal)
                    {
                        convertedValue = bool.Parse(strVal);
                    }
                    else
                    {
                        convertedValue = Convert.ToBoolean(newValue);
                    }
                }
                else if (targetType.IsEnum)
                {
                    string enumStringValue = null;

                    if (newValue is JsonElement jelem)
                    {
                        enumStringValue = jelem.GetString();
                    }
                    else if (newValue is string strVal)
                    {
                        enumStringValue = strVal;
                    }
                    else
                    {
                        enumStringValue = newValue.ToString();
                    }

                    Logger.Debug($"[StarDetection] Parsing enum {targetType.Name} from string: {enumStringValue}");
                    convertedValue = Enum.Parse(targetType, enumStringValue, ignoreCase: true);
                }
                else if (targetType == typeof(int) || targetType == typeof(double) || targetType == typeof(float) ||
                         targetType == typeof(decimal) || targetType == typeof(long) || targetType == typeof(short) ||
                         targetType == typeof(uint) || targetType == typeof(ulong) || targetType == typeof(ushort))
                {
                    if (newValue is JsonElement jelem)
                    {
                        if (jelem.ValueKind == JsonValueKind.Number)
                        {
                            convertedValue = Convert.ChangeType(jelem.GetDecimal(), targetType);
                        }
                        else
                        {
                            convertedValue = Convert.ChangeType(jelem.ToString(), targetType);
                        }
                    }
                    else
                    {
                        convertedValue = Convert.ChangeType(newValue, targetType);
                    }
                }
                else
                {
                    convertedValue = newValue;
                }

                try
                {
                    prop.SetValue(starDetectionOptions, convertedValue);
                    Logger.Debug($"[StarDetection] Successfully set {optionName} = {convertedValue}");
                    return new { success = true, message = $"Option '{optionName}' updated successfully", value = convertedValue };
                }
                catch (TargetInvocationException tiex) when (tiex.InnerException != null)
                {
                    // Unwrap TargetInvocationException from property setter validation
                    var innerEx = tiex.InnerException;
                    Logger.Error($"[StarDetection] Validation error setting {optionName} to {convertedValue}: {innerEx.Message}", innerEx);
                    HttpContext.Response.StatusCode = 400;
                    return new { success = false, error = $"Validation error: {innerEx.Message}" };
                }
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[StarDetection] Error during conversion/setting {optionName}: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = $"Failed to set option: {innerEx.Message}" };
            }
            catch (Exception ex)
            {
                Logger.Error($"[StarDetection] Error setting {optionName}: {ex.Message}", ex);
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = $"Failed to set option: {ex.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error setting StarDetection option", ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/aberration-inspector/options")]
    public object GetAberrationInspectorOptions()
    {
        try
        {
            // Access HocusFocus InspectorOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "HocusFocus plugin not loaded" }
                };
            }

            var aberrationInspectorOptionsProperty = hocusFocusPluginType.GetProperty("InspectorOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (aberrationInspectorOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "InspectorOptions not accessible" }
                };
            }

            var aberrationInspectorOptions = aberrationInspectorOptionsProperty.GetValue(null);
            if (aberrationInspectorOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "InspectorOptions instance not available" }
                };
            }

            // Reflect over all properties and build a dictionary
            var optionsDict = new Dictionary<string, object>();
            var enumOptionsDict = new Dictionary<string, string[]>();
            var optionsType = aberrationInspectorOptions.GetType();
            foreach (var prop in optionsType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    var value = prop.GetValue(aberrationInspectorOptions);
                    if (prop.PropertyType.IsEnum)
                    {
                        optionsDict[prop.Name] = value?.ToString();
                        enumOptionsDict[prop.Name] = Enum.GetNames(prop.PropertyType);
                    }
                    else
                    {
                        optionsDict[prop.Name] = value;
                    }
                }
                catch
                {
                    // Skip properties that can't be read
                }
            }

            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Options", optionsDict },
                { "EnumOptions", enumOptionsDict }
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Error getting AberrationInspector options", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/aberration-inspector/options/{optionName}")]
    public async Task<object> SetAberrationInspectorOption(string optionName)
    {
        try
        {
            // Parse the request body to get the new value
            object newValue = null;
            string jsonStr = null;
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                jsonStr = await reader.ReadToEndAsync();
                Logger.Debug($"[Inspector] SetInspectorOption {optionName} received: {jsonStr}");
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    // Parse as a simple value wrapper
                    var valueDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr, jsonOptions);
                    if (valueDict != null && valueDict.ContainsKey("value"))
                    {
                        newValue = valueDict["value"];
                    }
                }
            }

            if (newValue == null && jsonStr != null && !jsonStr.Contains("null"))
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = "Value not provided in request body" };
            }

            // Access HocusFocus InspectorOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "HocusFocus plugin not loaded" };
            }

            var aberrationInspectorOptionsProperty = hocusFocusPluginType.GetProperty("InspectorOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (aberrationInspectorOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "InspectorOptions not accessible" };
            }

            var aberrationInspectorOptions = aberrationInspectorOptionsProperty.GetValue(null);
            if (aberrationInspectorOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "InspectorOptions instance not available" };
            }

            // Find and set the property
            var optionsType = aberrationInspectorOptions.GetType();
            var prop = optionsType.GetProperty(optionName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

            if (prop == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Option '{optionName}' not found" };
            }

            if (!prop.CanWrite)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Option '{optionName}' is read-only" };
            }

            try
            {
                // Convert the value to the correct type
                var targetType = prop.PropertyType;
                object convertedValue = null;
                Logger.Debug($"[AberrationInspector] Converting {optionName} to type {targetType.Name}: raw value={newValue} (type={newValue?.GetType().Name ?? "null"})");

                if (newValue == null)
                {
                    convertedValue = null;
                }
                else if (targetType == typeof(string))
                {
                    convertedValue = newValue.ToString();
                }
                else if (targetType == typeof(bool))
                {
                    if (newValue is bool boolVal)
                    {
                        convertedValue = boolVal;
                    }
                    else if (newValue is JsonElement elem)
                    {
                        convertedValue = elem.GetBoolean();
                    }
                    else if (newValue is string strVal)
                    {
                        convertedValue = bool.Parse(strVal);
                    }
                    else
                    {
                        convertedValue = Convert.ToBoolean(newValue);
                    }
                }
                else if (targetType.IsEnum)
                {
                    if (newValue is JsonElement jelem)
                    {
                        convertedValue = Enum.Parse(targetType, jelem.GetString(), ignoreCase: true);
                    }
                    else
                    {
                        convertedValue = Enum.Parse(targetType, newValue.ToString(), ignoreCase: true);
                    }
                }
                else if (targetType == typeof(int) || targetType == typeof(double) || targetType == typeof(float) ||
                         targetType == typeof(decimal) || targetType == typeof(long) || targetType == typeof(short) ||
                         targetType == typeof(uint) || targetType == typeof(ulong) || targetType == typeof(ushort))
                {
                    if (newValue is JsonElement jelem)
                    {
                        if (jelem.ValueKind == JsonValueKind.Number)
                        {
                            convertedValue = Convert.ChangeType(jelem.GetDecimal(), targetType);
                        }
                        else
                        {
                            convertedValue = Convert.ChangeType(jelem.ToString(), targetType);
                        }
                    }
                    else
                    {
                        convertedValue = Convert.ChangeType(newValue, targetType);
                    }
                }
                else
                {
                    convertedValue = newValue;
                }

                try
                {
                    prop.SetValue(aberrationInspectorOptions, convertedValue);
                    Logger.Debug($"[AberrationInspector] Successfully set {optionName} = {convertedValue}");
                    return new { Success = true, Message = $"Option '{optionName}' updated successfully", Value = convertedValue };
                }
                catch (TargetInvocationException tiex) when (tiex.InnerException != null)
                {
                    // Unwrap TargetInvocationException from property setter validation
                    var innerEx = tiex.InnerException;
                    Logger.Error($"[AberrationInspector] Validation error setting {optionName} to {convertedValue}: {innerEx.Message}", innerEx);
                    HttpContext.Response.StatusCode = 400;
                    return new { Success = false, Error = $"Validation error: {innerEx.Message}" };
                }
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[AberrationInspector] Error during conversion/setting {optionName}: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to set option: {innerEx.Message}" };
            }
            catch (Exception ex)
            {
                Logger.Error($"[AberrationInspector] Error setting {optionName}: {ex.Message}", ex);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to set option: {ex.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error setting AberrationInspector option", ex);
            HttpContext.Response.StatusCode = 500;
            return new { Success = false, Error = ex.Message };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/aberration-inspector/reset-defaults")]
    public object ResetAberrationInspectorDefaults()
    {
        try
        {
            // Access HocusFocus InspectorOptions via reflection
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "HocusFocus plugin not loaded" };
            }

            var aberrationInspectorOptionsProperty = hocusFocusPluginType.GetProperty("InspectorOptions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (aberrationInspectorOptionsProperty == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "InspectorOptions not accessible" };
            }

            var aberrationInspectorOptions = aberrationInspectorOptionsProperty.GetValue(null);
            if (aberrationInspectorOptions == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new { Success = false, Error = "InspectorOptions instance not available" };
            }

            // Call ResetDefaults method
            var resetMethod = aberrationInspectorOptions.GetType().GetMethod("ResetDefaults",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (resetMethod == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = "ResetDefaults method not found" };
            }

            try
            {
                resetMethod.Invoke(aberrationInspectorOptions, null);
                Logger.Debug("[Inspector] Successfully reset all options to defaults");
                return new { Success = true, Message = "All AberrationInspector options have been reset to defaults" };
            }
            catch (TargetInvocationException tiex) when (tiex.InnerException != null)
            {
                var innerEx = tiex.InnerException;
                Logger.Error($"[Inspector] Error resetting defaults: {innerEx.Message}", innerEx);
                HttpContext.Response.StatusCode = 400;
                return new { Success = false, Error = $"Failed to reset defaults: {innerEx.Message}" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error resetting Inspector options to defaults", ex);
            HttpContext.Response.StatusCode = 500;
            return new { Success = false, Error = ex.Message };
        }
    }

    [Route(HttpVerbs.Get, "/hocusfocus/browse-directories")]
    public object BrowseDirectories()
    {
        try
        {
            // Get the path query parameter (defaults to home directory or Documents)
            string pathParam = null;
            if (HttpContext.Request.QueryString.AllKeys.Contains("path"))
            {
                pathParam = HttpContext.Request.QueryString["path"]?.ToString();
            }
            var path = string.IsNullOrWhiteSpace(pathParam) ? GetDefaultBrowsePath() : Uri.UnescapeDataString(pathParam);

            Logger.Debug($"[BrowseDirectories] Requested path: {pathParam}, resolved to: {path}");

            // Security: Prevent path traversal attacks
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                Logger.Error($"[BrowseDirectories] Path does not exist: {fullPath}");
                HttpContext.Response.StatusCode = 404;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Path does not exist" }
                };
            }

            // Get subdirectories
            var subdirectories = new List<Dictionary<string, object>>();
            try
            {
                // Get all directories and filter out hidden ones (starting with .)
                var dirs = Directory.GetDirectories(fullPath)
                    .Where(d => !Path.GetFileName(d).StartsWith("."))
                    .OrderBy(d => Path.GetFileName(d))
                    .ToList();
                foreach (var dir in dirs)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        // Check if directory has non-hidden subdirectories
                        var hasVisibleSubdirs = Directory.GetDirectories(dir)
                            .Any(d => !Path.GetFileName(d).StartsWith("."));

                        subdirectories.Add(new Dictionary<string, object>()
                        {
                            { "name", dirInfo.Name },
                            { "path", dirInfo.FullName },
                            { "hasSubdirs", hasVisibleSubdirs }
                        });
                    }
                    catch { /* Skip directories we can't access */ }
                }
            }
            catch (UnauthorizedAccessException)
            {
                HttpContext.Response.StatusCode = 403;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Access denied to this directory" }
                };
            }

            // Get parent directory for navigation
            var parentPath = Directory.GetParent(fullPath)?.FullName;

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "currentPath", fullPath },
                { "parentPath", parentPath },
                { "directories", subdirectories }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[BrowseDirectories] Error browsing directories: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to browse directories: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Scans for available ETA Tilter devices
    /// </summary>
    [Route(HttpVerbs.Get, "/hocusfocus/tilter/scan-devices")]
    public object ScanTilterDevices()
    {
        try
        {
            var tilterService = TilterService.Instance;
            var devices = tilterService.ScanDevices();

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Response", devices }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScanTilterDevices] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to scan devices: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Gets list of available ETA Tilter devices
    /// </summary>
    [Route(HttpVerbs.Get, "/hocusfocus/tilter/devices")]
    public object GetTilterDevices()
    {
        try
        {
            var tilterService = TilterService.Instance;
            var devices = tilterService.GetAvailableDevices();

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Response", devices }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[GetTilterDevices] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to get devices: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Connects to an ETA Tilter device
    /// </summary>
    [Route(HttpVerbs.Post, "/hocusfocus/tilter/connect")]
    public async Task<object> ConnectTilterDevice()
    {
        try
        {
            var json = await HttpContext.GetRequestBodyAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("deviceId", out var deviceIdElement))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Missing deviceId parameter" }
                };
            }

            int deviceId;
            // Handle both int and string formats
            if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (!deviceIdElement.TryGetInt32(out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId value" }
                    };
                }
            }
            else if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (!int.TryParse(deviceIdElement.GetString(), out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId format" }
                    };
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Invalid deviceId parameter type" }
                };
            }

            var tilterService = TilterService.Instance;
            bool connected = tilterService.ConnectDevice(deviceId);

            HttpContext.Response.StatusCode = connected ? 200 : 500;
            return new Dictionary<string, object>()
            {
                { "Success", connected },
                { "Message", connected ? "Device connected successfully" : "Failed to connect device" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[ConnectTilterDevice] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to connect device: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Disconnects from an ETA Tilter device
    /// </summary>
    [Route(HttpVerbs.Post, "/hocusfocus/tilter/disconnect")]
    public async Task<object> DisconnectTilterDevice()
    {
        try
        {
            var json = await HttpContext.GetRequestBodyAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("deviceId", out var deviceIdElement))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Missing deviceId parameter" }
                };
            }

            int deviceId;
            // Handle both int and string formats
            if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (!deviceIdElement.TryGetInt32(out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId value" }
                    };
                }
            }
            else if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (!int.TryParse(deviceIdElement.GetString(), out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId format" }
                    };
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Invalid deviceId parameter type" }
                };
            }

            var tilterService = TilterService.Instance;
            bool disconnected = tilterService.DisconnectDevice(deviceId);

            HttpContext.Response.StatusCode = disconnected ? 200 : 500;
            return new Dictionary<string, object>()
            {
                { "Success", disconnected },
                { "Message", disconnected ? "Device disconnected successfully" : "Failed to disconnect device" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[DisconnectTilterDevice] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to disconnect device: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Checks if an ETA Tilter device is currently connected
    /// </summary>
    [Route(HttpVerbs.Get, "/hocusfocus/tilter/is-connected/{deviceId}")]
    public object IsTilterDeviceConnected(int deviceId)
    {
        try
        {
            var tilterService = TilterService.Instance;
            bool isConnected = tilterService.IsDeviceConnected(deviceId);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "IsConnected", isConnected }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[IsTilterDeviceConnected] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "IsConnected", false },
                { "Error", ex.Message }
            };
        }
    }

    /// <summary>
    /// Gets status of an ETA Tilter device
    /// </summary>
    [Route(HttpVerbs.Get, "/hocusfocus/tilter/status/{deviceId}")]
    public object GetTilterStatus(int deviceId)
    {
        try
        {
            var tilterService = TilterService.Instance;
            // GetTilterStatus is only for real ETA devices which use 78.0mm outer radius
            var status = tilterService.GetDeviceStatus(deviceId, 78.0);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Response", status }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[GetTilterStatus] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to get status: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Sets positions for an ETA Tilter device
    /// </summary>
    [Route(HttpVerbs.Post, "/hocusfocus/tilter/set-positions")]
    public async Task<object> SetTilterPositions()
    {
        try
        {
            var json = await HttpContext.GetRequestBodyAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("deviceId", out var deviceIdElement))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Missing deviceId parameter" }
                };
            }

            int deviceId;
            // Handle both int and string formats
            if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (!deviceIdElement.TryGetInt32(out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId value" }
                    };
                }
            }
            else if (deviceIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (!int.TryParse(deviceIdElement.GetString(), out deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Invalid deviceId format" }
                    };
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Invalid deviceId parameter type" }
                };
            }

            if (!root.TryGetProperty("positions", out var positionsElement))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>()
                {
                    { "Success", false },
                    { "Error", "Missing positions parameter" }
                };
            }

            // Extract the three position values - only set if present in the request
            float? position1 = null, position2 = null, position3 = null;

            if (positionsElement.TryGetProperty("position1", out var p1) && p1.TryGetSingle(out var p1Val))
                position1 = p1Val;
            if (positionsElement.TryGetProperty("position2", out var p2) && p2.TryGetSingle(out var p2Val))
                position2 = p2Val;
            if (positionsElement.TryGetProperty("position3", out var p3) && p3.TryGetSingle(out var p3Val))
                position3 = p3Val;

            var tilterService = TilterService.Instance;
            bool success = tilterService.SetDevicePositions(deviceId, position1, position2, position3);

            HttpContext.Response.StatusCode = success ? 200 : 500;
            return new Dictionary<string, object>()
            {
                { "Success", success },
                { "Message", success ? "Positions set successfully" : "Failed to set positions" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[SetTilterPositions] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to set positions: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Gets the current sensor configuration (size and orientation)
    /// </summary>
    [Route(HttpVerbs.Get, "/hocusfocus/tilter/sensor-config")]
    public object GetSensorConfiguration()
    {
        try
        {
            var tilterService = TilterService.Instance;
            var config = tilterService.GetSensorConfiguration();

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "SensorWidth", config.SensorWidth },
                { "SensorHeight", config.SensorHeight },
                { "SensorRotation", config.SensorRotation },
                { "TilterOuterRadius", config.TilterOuterRadius },
                { "TilterThreadPitch", config.TilterThreadPitch }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[GetSensorConfiguration] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to get sensor configuration: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Sets the sensor configuration (size and orientation)
    /// </summary>
    [Route(HttpVerbs.Post, "/hocusfocus/tilter/sensor-config")]
    public async Task<object> SetSensorConfiguration()
    {
        try
        {
            using var json = JsonDocument.Parse(await HttpContext.GetRequestBodyAsStringAsync());
            var root = json.RootElement;

            double width = 36.0;
            double height = 24.0;
            double rotation = 0.0;
            double outerRadius = 0.0;
            double threadPitch = 0.0;

            if (root.TryGetProperty("sensorWidth", out var widthElement) && widthElement.TryGetDouble(out var widthVal))
                width = widthVal;
            if (root.TryGetProperty("sensorHeight", out var heightElement) && heightElement.TryGetDouble(out var heightVal))
                height = heightVal;
            if (root.TryGetProperty("sensorRotation", out var rotationElement) && rotationElement.TryGetDouble(out var rotationVal))
                rotation = Math.Clamp(rotationVal, 0, 359.9);
            if (root.TryGetProperty("tilterOuterRadius", out var outerRadiusElement) && outerRadiusElement.TryGetDouble(out var outerRadiusVal))
                outerRadius = outerRadiusVal;
            if (root.TryGetProperty("tilterThreadPitch", out var threadPitchElement) && threadPitchElement.TryGetDouble(out var threadPitchVal))
                threadPitch = threadPitchVal;

            var config = new TilterService.SensorConfigurationDTO
            {
                SensorWidth = width,
                SensorHeight = height,
                SensorRotation = rotation,
                TilterOuterRadius = outerRadius,
                TilterThreadPitch = threadPitch
            };

            var tilterService = TilterService.Instance;
            tilterService.SetSensorConfiguration(config);

            HttpContext.Response.StatusCode = 200;
            return new Dictionary<string, object>()
            {
                { "Success", true },
                { "Message", "Sensor configuration updated successfully" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[SetSensorConfiguration] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to set sensor configuration: {ex.Message}" }
            };
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/tilter/apply-tilt-plane")]
    public object ApplyTiltPlane()
    {
        try
        {
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                string json = reader.ReadToEnd();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Extract device ID
                if (!root.TryGetProperty("deviceId", out var deviceIdElement) || !deviceIdElement.TryGetInt32(out var deviceId))
                {
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Missing or invalid deviceId parameter" }
                    };
                }

                // Extract desired Z values at the four corners
                double tlZ = 0, trZ = 0, blZ = 0, brZ = 0;

                if (root.TryGetProperty("imagePlaneTopLeftZ", out var tlElement) && tlElement.ValueKind != System.Text.Json.JsonValueKind.Null && tlElement.TryGetDouble(out var tlVal))
                    tlZ = tlVal;
                if (root.TryGetProperty("imagePlaneTopRightZ", out var trElement) && trElement.ValueKind != System.Text.Json.JsonValueKind.Null && trElement.TryGetDouble(out var trVal))
                    trZ = trVal;
                if (root.TryGetProperty("imagePlaneBottomLeftZ", out var blElement) && blElement.ValueKind != System.Text.Json.JsonValueKind.Null && blElement.TryGetDouble(out var blVal))
                    blZ = blVal;
                if (root.TryGetProperty("imagePlaneBottomRightZ", out var brElement) && brElement.ValueKind != System.Text.Json.JsonValueKind.Null && brElement.TryGetDouble(out var brVal))
                    brZ = brVal;

                // Extract outer radius - optional parameter (no default)
                double? outerRadius = null;
                if (root.TryGetProperty("outerRadius", out var outerRadiusElement) && outerRadiusElement.ValueKind != System.Text.Json.JsonValueKind.Null && outerRadiusElement.TryGetDouble(out var outerRadiusVal))
                {
                    outerRadius = outerRadiusVal;
                }

                // Extract dontOffsetToZero flag - optional parameter (default false)
                bool dontOffsetToZero = false;
                if (root.TryGetProperty("dontOffsetToZero", out var dontOffsetElement) && dontOffsetElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    try
                    {
                        dontOffsetToZero = dontOffsetElement.GetBoolean();
                    }
                    catch
                    {
                        dontOffsetToZero = false;
                    }
                }

                var tilterService = TilterService.Instance;

                // Check if device is connected first (skip for manual tilter device -1)
                if (deviceId != -1 && !tilterService.IsDeviceConnected(deviceId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", $"Device {deviceId} is not connected. Please connect the device before calculating positions." }
                    };
                }

                // Determine the outer radius to use
                double? finalOuterRadius = outerRadius;

                // If no outer radius provided (ETA device case), try to fetch from device
                // Skip this for manual tilter device -1 (it's virtual and requires explicit outerRadius)
                if (outerRadius == null && deviceId != -1)
                {
                    try
                    {
                        // For ETA devices, fetch the radius from the device itself
                        var etaStatus = new WandererSDK.WTEtaStatus();
                        var statusResult = WandererSDK.WTETAGetStatus(deviceId, ref etaStatus);

                        if (statusResult == WandererSDK.WTErrorType.Success)
                        {
                            if (etaStatus.Radius > 0)
                            {
                                finalOuterRadius = etaStatus.Radius;
                                Logger.Info($"[ApplyTiltPlane] Fetched ETA device radius: {etaStatus.Radius:F2}mm");
                            }
                            else
                            {
                                Logger.Warning($"[ApplyTiltPlane] ETA device returned invalid radius: {etaStatus.Radius:F2}mm");
                            }
                        }
                        else
                        {
                            Logger.Warning($"[ApplyTiltPlane] Failed to fetch ETA device radius, status: {statusResult}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[ApplyTiltPlane] Error fetching ETA device radius: {ex.Message}");
                    }
                }

                // If we still don't have an outer radius, it's required
                if (finalOuterRadius == null || finalOuterRadius <= 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", "Tilter outer radius not available. For manual tilters, please configure the Tilter Screw Outer Radius before calculating positions. For ETA devices, ensure the device is properly connected." }
                    };
                }

                // Retrieve current actuator positions to use as the baseline for corrections
                // Skip this for manual tilter device -1 (it's virtual and has no current positions)
                double currentP1 = 0, currentP2 = 0, currentP3 = 0;
                if (deviceId != -1)
                {
                    try
                    {
                        var status = tilterService.GetDeviceStatus(deviceId, finalOuterRadius);
                        if (status != null)
                        {
                            currentP1 = status.CurrentPosition1;
                            currentP2 = status.CurrentPosition2;
                            currentP3 = status.CurrentPosition3;
                            Logger.Info($"[ApplyTiltPlane] Current device positions (mm) - P1: {currentP1:F6}, P2: {currentP2:F6}, P3: {currentP3:F6}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[ApplyTiltPlane] Could not retrieve current positions, using 0: {ex.Message}");
                    }
                }

                var desiredPlane = new TilterService.ApplyTiltPlaneDTO
                {
                    ImagePlaneTopLeftZ = tlZ,
                    ImagePlaneTopRightZ = trZ,
                    ImagePlaneBottomLeftZ = blZ,
                    ImagePlaneBottomRightZ = brZ,
                    OuterRadius = finalOuterRadius.Value,
                    DontOffsetToZero = dontOffsetToZero
                };

                var result = tilterService.CalculateActuatorPositions(desiredPlane, currentP1, currentP2, currentP3, dontOffsetToZero);

                if (!result.Success)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new Dictionary<string, object>()
                    {
                        { "Success", false },
                        { "Error", result.Message }
                    };
                }

                HttpContext.Response.StatusCode = 200;
                var responseDict = new Dictionary<string, object>()
                {
                    { "Success", true },
                    { "Message", result.Message },
                    { "Position1", result.Position1 },
                    { "Position2", result.Position2 },
                    { "Position3", result.Position3 }
                };

                // Include raw positions for manual tilters (to show what was calculated before offsetting)
                if (result.RawPosition1.HasValue)
                    responseDict["RawPosition1"] = result.RawPosition1;
                if (result.RawPosition2.HasValue)
                    responseDict["RawPosition2"] = result.RawPosition2;
                if (result.RawPosition3.HasValue)
                    responseDict["RawPosition3"] = result.RawPosition3;

                return responseDict;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ApplyTiltPlane] Error: {ex.Message}", ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>()
            {
                { "Success", false },
                { "Error", $"Failed to apply tilt plane: {ex.Message}" }
            };
        }
    }

    /// <summary>
    /// Helper method to determine the default starting path for directory browsing
    /// </summary>
    private string GetDefaultBrowsePath()
    {
        try
        {
            // Try to get the AutoFocusOptions SavePath first
            var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
            if (hocusFocusPluginType != null)
            {
                var autoFocusOptionsProperty = hocusFocusPluginType.GetProperty("AutoFocusOptions",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (autoFocusOptionsProperty != null)
                {
                    var autoFocusOptions = autoFocusOptionsProperty.GetValue(null);
                    if (autoFocusOptions != null)
                    {
                        var savePathProperty = autoFocusOptions.GetType().GetProperty("SavePath");
                        if (savePathProperty != null)
                        {
                            var savePath = savePathProperty.GetValue(autoFocusOptions) as string;
                            if (!string.IsNullOrWhiteSpace(savePath) && Directory.Exists(savePath))
                            {
                                return savePath;
                            }
                        }
                    }
                }
            }
        }
        catch { /* Fall through to default */ }

        // Fallback to common directories
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
