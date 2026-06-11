using TouchNStars.Server.Models;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for INDI driver management
/// </summary>
public class INDIController : WebApiController
{
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
