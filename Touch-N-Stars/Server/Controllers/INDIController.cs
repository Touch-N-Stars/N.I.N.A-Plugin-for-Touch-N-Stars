using TouchNStars.Server.Models;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Model;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for INDI driver management
/// </summary>
public class INDIController : WebApiController
{
    /// <summary>
    /// GET /api/indi/devices - List every INDI device currently visible to the embedded indiserver,
    /// together with its driver metadata and connection state. Drivers only appear once NINA (or
    /// another client of the same server) has loaded them.
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/devices")]
    public ApiResponse GetActiveDevices()
    {
        try
        {
            var devices = INDIClient.Instance.GetDeviceSnapshots()
                .Select(d => new
                {
                    d.Device,
                    d.DriverExec,
                    d.DriverName,
                    d.Version,
                    d.Interface,
                    d.Connected,
                    PropertyCount = d.Properties.Count
                })
                .OrderBy(d => d.Device, StringComparer.OrdinalIgnoreCase)
                .ToList();

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Response = devices, StatusCode = 200, Type = "INDIDevices" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving active INDI devices: {ex}");
            return ErrorResponse("An unexpected error occurred while retrieving active INDI devices");
        }
    }

    /// <summary>
    /// GET /api/indi/properties[?device=Name] - Return the full property tree for all devices, or for
    /// a single device when the query parameter is supplied. This is the generic surface that backs
    /// the INDI control panel: every property of every type is included, whatever it is.
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/properties")]
    public ApiResponse GetProperties([QueryField] string device)
    {
        try
        {
            var snapshots = INDIClient.Instance.GetDeviceSnapshots(string.IsNullOrWhiteSpace(device) ? null : device);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Response = snapshots, StatusCode = 200, Type = "INDIProperties" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving INDI properties: {ex}");
            return ErrorResponse("An unexpected error occurred while retrieving INDI properties");
        }
    }

    /// <summary>
    /// POST /api/indi/properties/refresh[?device=Name] - Ask the server to re-send property definitions
    /// (getProperties). Useful right after opening the control panel to force a fresh snapshot.
    /// </summary>
    [Route(HttpVerbs.Post, "/indi/properties/refresh")]
    public ApiResponse RefreshProperties([QueryField] string device)
    {
        try
        {
            INDIClient.Instance.GetProperties(string.IsNullOrWhiteSpace(device) ? null : device);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Response = "Property refresh requested", StatusCode = 200, Type = "INDIRefresh" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error refreshing INDI properties: {ex}");
            return ErrorResponse("An unexpected error occurred while refreshing INDI properties");
        }
    }

    /// <summary>
    /// POST /api/indi/properties/set - Write a writable property on any device.
    /// Body: { "device": "Telescope Simulator", "property": "EQUATORIAL_EOD_COORD",
    ///         "elements": { "RA": 12.34, "DEC": 56.7 } }
    /// Switch elements accept true/false (or "On"/"Off"); switch rules are honored server-side.
    /// </summary>
    [Route(HttpVerbs.Post, "/indi/properties/set")]
    public async Task<ApiResponse> SetProperty()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();

            var device = body != null && body.TryGetValue("device", out var d) ? d?.ToString() : null;
            var property = body != null && body.TryGetValue("property", out var p) ? p?.ToString() : null;

            if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(property))
            {
                return ErrorResponse("Body must contain 'device' and 'property'", 400);
            }

            if (body == null || !body.TryGetValue("elements", out var elementsObj)
                || elementsObj is not IDictionary<string, object> elements
                || elements.Count == 0)
            {
                return ErrorResponse("Body must contain a non-empty 'elements' object", 400);
            }

            if (!INDIClient.Instance.SetProperty(device, property, elements, out var error))
            {
                return ErrorResponse(error ?? "Failed to set property", 400);
            }

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { device, property },
                StatusCode = 200,
                Type = "INDISetProperty"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting INDI property: {ex}");
            return ErrorResponse("An unexpected error occurred while setting the INDI property");
        }
    }

    /// <summary>
    /// GET /api/indi/mount/slew-rates[?device=Name] - Describe how the connected INDI mount lets a
    /// client choose its manual (MoveAxis) slew rate. Returns a SlewRateCapability: discrete named
    /// switch steps, a continuous numeric °/s range, or none. The frontend should render its rate
    /// control from this instead of assuming a fixed scale. Defaults to the first connected mount.
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/mount/slew-rates")]
    public ApiResponse GetMountSlewRates([QueryField] string device)
    {
        try
        {
            var mount = INDIClient.Instance.GetRegisteredDevice<INDITelescope>(
                string.IsNullOrWhiteSpace(device) ? null : device);
            if (mount == null)
            {
                return ErrorResponse("No INDI mount is currently connected", 404);
            }

            var capability = mount.GetSlewRateCapability();
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Response = capability, StatusCode = 200, Type = "INDIMountSlewRates" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving INDI mount slew rates: {ex}");
            return ErrorResponse("An unexpected error occurred while retrieving mount slew rates");
        }
    }

    /// <summary>
    /// POST /api/indi/mount/slew-rate - Select the mount's manual slew rate.
    /// Body: { "device": "EQMod Mount", "index": 3 } for discrete drivers, or
    ///       { "device": "Telescope Simulator", "value": 1.5 } (°/s) for continuous drivers.
    /// The selector must match the driver's capability kind; 'device' is optional and defaults
    /// to the first connected mount.
    /// </summary>
    [Route(HttpVerbs.Post, "/indi/mount/slew-rate")]
    public async Task<ApiResponse> SetMountSlewRate()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            var device = body != null && body.TryGetValue("device", out var d) ? d?.ToString() : null;

            var mount = INDIClient.Instance.GetRegisteredDevice<INDITelescope>(
                string.IsNullOrWhiteSpace(device) ? null : device);
            if (mount == null)
            {
                return ErrorResponse("No INDI mount is currently connected", 404);
            }

            var capability = mount.GetSlewRateCapability();

            if (body != null && body.TryGetValue("index", out var indexObj)
                && int.TryParse(indexObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (capability.Kind != SlewRateKind.Discrete)
                {
                    return ErrorResponse("This mount does not use discrete slew rates; send 'value' instead", 400);
                }
                mount.SetSlewRateIndex(index);
                HttpContext.Response.StatusCode = 200;
                return new ApiResponse { Success = true, Response = new { device = mount.DeviceName, index }, StatusCode = 200, Type = "INDIMountSlewRate" };
            }

            if (body != null && body.TryGetValue("value", out var valueObj)
                && double.TryParse(valueObj?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                if (capability.Kind != SlewRateKind.Continuous)
                {
                    return ErrorResponse("This mount does not use a continuous slew rate; send 'index' instead", 400);
                }
                mount.SetSlewRateValue(value);
                HttpContext.Response.StatusCode = 200;
                return new ApiResponse { Success = true, Response = new { device = mount.DeviceName, value }, StatusCode = 200, Type = "INDIMountSlewRate" };
            }

            return ErrorResponse("Body must contain an integer 'index' (discrete) or a numeric 'value' (continuous)", 400);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting INDI mount slew rate: {ex}");
            return ErrorResponse("An unexpected error occurred while setting the mount slew rate");
        }
    }

    /// <summary>
    /// GET /api/indi/messages[?limit=200] - Return the most recent human-readable INDI messages
    /// (driver/server log lines) with their timestamps, oldest first.
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/messages")]
    public ApiResponse GetMessages([QueryField] int limit)
    {
        try
        {
            var messages = INDIClient.Instance.GetMessages(limit > 0 ? limit : 200);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Response = messages, StatusCode = 200, Type = "INDIMessages" };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving INDI messages: {ex}");
            return ErrorResponse("An unexpected error occurred while retrieving INDI messages");
        }
    }

    private ApiResponse ErrorResponse(string error, int statusCode = 500)
    {
        HttpContext.Response.StatusCode = statusCode;
        return new ApiResponse { Success = false, Error = error, StatusCode = statusCode, Type = "Error" };
    }

    /// <summary>
    /// GET /api/indi/camera - Get available INDI camera drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/camera")]
    public ApiResponse GetCameraDrivers()
    {
        return GetDriversByType("camera");
    }

    /// <summary>
    /// GET /api/indi/focuser - Get available INDI focuser drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/focuser")]
    public ApiResponse GetFocuserDrivers()
    {
        return GetDriversByType("focuser");
    }

    /// <summary>
    /// GET /api/indi/filterwheel - Get available INDI filterwheel drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/filterwheel")]
    public ApiResponse GetFilterwheelDrivers()
    {
        return GetDriversByType("filterwheel");
    }

    /// <summary>
    /// GET /api/indi/rotator - Get available INDI rotator drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/rotator")]
    public ApiResponse GetRotatorDrivers()
    {
        return GetDriversByType("rotator");
    }

    /// <summary>
    /// GET /api/indi/telescope - Get available INDI telescope mount drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/telescope")]
    public ApiResponse GetTelescopeDrivers()
    {
        return GetDriversByType("telescope");
    }

    /// <summary>
    /// GET /api/indi/weather - Get available INDI weather device drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/weather")]
    public ApiResponse GetWeatherDrivers()
    {
        return GetDriversByType("weather");
    }

    /// <summary>
    /// GET /api/indi/switches - Get available INDI switch/power device drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/switches")]
    public ApiResponse GetSwitchDrivers()
    {
        return GetDriversByType("switches");
    }

    /// <summary>
    /// GET /api/indi/flatpanel - Get available INDI flat panel drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/flatpanel")]
    public ApiResponse GetFlatpanelDrivers()
    {
        return GetDriversByType("flatpanel");
    }

    /// <summary>
    /// GET /api/indi/safetymonitor - Get available INDI safety monitor drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/safetymonitor")]
    public ApiResponse GetSafetymonitorDrivers()
    {
        return GetDriversByType("safetymonitor");
    }

    /// <summary>
    /// GET /api/indi/dome - Get available INDI dome drivers
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/dome")]
    public ApiResponse GetDomeDrivers()
    {
        return GetDriversByType("dome");
    }

    /// <summary>
    /// GET /api/indi/serialports - Get available serial ports for INDI connections
    /// Returns objects with port name and description (manufacturer/product from sysfs on Linux)
    /// </summary>
    [Route(HttpVerbs.Get, "/indi/serialports")]
    public ApiResponse GetAvailableSerialPorts()
    {
        try
        {
            var portNames = SerialPort.GetPortNames().OrderBy(s => s).ToArray();
            var portInfos = portNames.Select(p => new
            {
                Port = p,
                Description = GetSerialPortDescription(p)
            }).ToList();

            var byIdLinks = GetSerialByIdLinks();

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Ports = portInfos, ByIdLinks = byIdLinks },
                StatusCode = 200,
                Type = "SerialPorts"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Unexpected error while retrieving available serial ports: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An unexpected error occurred while retrieving serial ports",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    private static string GetSerialPortDescription(string portName)
    {
        try
        {
            var ttyName = Path.GetFileName(portName); // e.g. "ttyUSB0"

            // Same approach as UsbDeviceWatcher: enumerate /sys/bus/usb/devices
            var usbDevicesPath = "/sys/bus/usb/devices";
            if (!Directory.Exists(usbDevicesPath))
                return "";

            // Look through USB interface directories (e.g. 3-2:1.0) for one that contains our tty
            foreach (var dir in Directory.GetDirectories(usbDevicesPath))
            {
                var dirName = Path.GetFileName(dir);
                // Interface directories contain a colon (e.g. "3-2:1.0")
                if (!dirName.Contains(':'))
                    continue;

                var ttySubDir = Path.Combine(dir, ttyName);
                if (!Directory.Exists(ttySubDir))
                    continue;

                // Found the interface that owns this tty port
                // The parent USB device dir is the part before the colon (e.g. "3-2")
                var parentDeviceName = dirName.Split(':')[0];
                var parentDevicePath = Path.Combine(usbDevicesPath, parentDeviceName);

                var manufacturer = ReadSysfsFile(Path.Combine(parentDevicePath, "manufacturer"));
                var product = ReadSysfsFile(Path.Combine(parentDevicePath, "product"));

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(manufacturer)) parts.Add(manufacturer);
                if (!string.IsNullOrEmpty(product)) parts.Add(product);

                return string.Join(" - ", parts);
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    private static List<object> GetSerialByIdLinks()
    {
        var result = new List<object>();
        var byIdPath = "/dev/serial/by-id";
        if (!Directory.Exists(byIdPath))
            return result;

        try
        {
            foreach (var link in Directory.GetFiles(byIdPath).OrderBy(s => s))
            {
                var linkName = Path.GetFileName(link);
                var resolvedPath = "";
                try
                {
                    var target = new FileInfo(link).LinkTarget;
                    if (target != null)
                        resolvedPath = Path.GetFullPath(Path.Combine(byIdPath, target));
                }
                catch { }

                result.Add(new { Id = linkName, Path = $"{byIdPath}/{linkName}", ResolvedPort = resolvedPath });
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error enumerating /dev/serial/by-id: {ex}");
        }

        return result;
    }

    private static string ReadSysfsFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Helper method to get drivers by type
    /// </summary>
    private ApiResponse GetDriversByType(string driverType)
    {
        try
        {
            var drivers = INDIDriverRegistry.GetDrivers(driverType);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = drivers,
                StatusCode = 200,
                Type = "INDIDrivers"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Unexpected error while retrieving INDI drivers: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An unexpected error occurred while retrieving INDI drivers",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }
}
