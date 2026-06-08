using TouchNStars.Server.Models;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for PINS device management
/// Accesses the PINS plugin via reflection to avoid compile-time dependencies
/// </summary>
public class PinsController : WebApiController
{
    private static readonly Type PINSType = Type.GetType("NINA.PINS.PINS, NINA.PINS");
    private static readonly Type PowerBoxDriverType = Type.GetType("NINA.PINS.Drivers.PowerBoxDriver, NINA.PINS");
    private static readonly Type MeteoStationDriverType = Type.GetType("NINA.PINS.Drivers.MeteoStationDriver, NINA.PINS");
    private static readonly Type LensControlDriverType = Type.GetType("NINA.PINS.Drivers.LensControlDriver, NINA.PINS");

    private static object GetPINSConnectedPowerBox()
    {
        if (PINSType == null) return null;
        
        var property = PINSType.GetProperty("ConnectedPowerBox", BindingFlags.Public | BindingFlags.Static);
        if (property == null) return null;
        
        var powerBox = property.GetValue(null);
        if (powerBox == null) return null;
        
        // Check if the PowerBox is actually connected
        if (!GetPropertyBool(powerBox, "Connected"))
        {
            return null;
        }
        
        return powerBox;
    }

    private static object GetPINSConnectedMeteoStation()
    {
        if (PINSType == null) return null;

        var property = PINSType.GetProperty("ConnectedMeteoStation", BindingFlags.Public | BindingFlags.Static);
        if (property == null) return null;

        var meteoStation = property.GetValue(null);
        if (meteoStation == null) return null;

        // Check if the MeteoStation is actually connected
        if (!GetPropertyBool(meteoStation, "Connected"))
        {
            return null;
        }

        return meteoStation;
    }

    private static object GetPINSConnectedLensControl()
    {
        if (PINSType == null) return null;

        var property = PINSType.GetProperty("ConnectedLensControl", BindingFlags.Public | BindingFlags.Static);
        if (property == null) return null;

        var lensControl = property.GetValue(null);
        if (lensControl == null) return null;

        if (!GetPropertyBool(lensControl, "Connected"))
            return null;

        return lensControl;
    }

    private static object GetWeatherData(object mediator)
    {
        if (mediator == null) return null;

        try
        {
            var method = mediator.GetType().GetMethod("GetInfo", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return null;

            return method.Invoke(mediator, null);
        }
        catch
        {
            return null;
        }
    }

    private static T GetPropertyValue<T>(object obj, string propertyName, T defaultValue = default)
    {
        if (obj == null) return defaultValue;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return defaultValue;
            
            var value = property.GetValue(obj);
            return value is T t ? t : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static object GetPropertyObject(object obj, string propertyName)
    {
        if (obj == null) return null;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return null;
            
            return property.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private static int GetPropertyInt(object obj, string propertyName, int defaultValue = 0)
    {
        if (obj == null) return defaultValue;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return defaultValue;
            
            var value = property.GetValue(obj);
            return value is int i ? i : int.TryParse(value?.ToString() ?? "", out int result) ? result : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static double GetPropertyDouble(object obj, string propertyName, double defaultValue = double.NaN)
    {
        if (obj == null) return defaultValue;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return defaultValue;
            
            var value = property.GetValue(obj);
            if (value is double d) return d;
            if (value is float f) return f;
            if (double.TryParse(value?.ToString() ?? "", out double result)) return result;
            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool GetPropertyBool(object obj, string propertyName, bool defaultValue = false)
    {
        if (obj == null) return defaultValue;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return defaultValue;
            
            var value = property.GetValue(obj);
            if (value is bool b) return b;
            if (bool.TryParse(value?.ToString() ?? "", out bool result)) return result;
            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
    /// <summary>
    /// GET /api/pins/powerbox - Get PowerBox device information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox")]
    public ApiResponse GetPowerBoxInfo()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Get actual port counts reported by the device
            int powerPortCount = GetPropertyInt(powerBox, "ActualPowerPortCount", 8);
            int usbPortCount = GetPropertyInt(powerBox, "ActualUSBPortCount", 8);
            int dewPortCount = GetPropertyInt(powerBox, "ActualDewPortCount", 2);
            int buckPortCount = GetPropertyInt(powerBox, "ActualBuckPortCount", 1);
            int pwmPortCount = GetPropertyInt(powerBox, "ActualPWMPortCount", 1);

            var info = new
            {
                Name = GetPropertyValue(powerBox, "Name", ""),
                DisplayName = GetPropertyValue(powerBox, "DisplayName", ""),
                Id = GetPropertyValue(powerBox, "Id", ""),
                Description = GetPropertyValue(powerBox, "Description", ""),
                DriverInfo = GetPropertyValue(powerBox, "DriverInfo", ""),
                DriverVersion = GetPropertyValue(powerBox, "DriverVersion", ""),
                Firmware = GetPropertyValue(powerBox, "Firmware", ""),
                Connected = GetPropertyBool(powerBox, "Connected"),
                PowerPorts = powerPortCount,
                USBPorts = usbPortCount,
                DewPorts = dewPortCount,
                BuckPorts = buckPortCount,
                PWMPorts = pwmPortCount
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = info,
                StatusCode = 200,
                Type = "PowerBoxInfo"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox information: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox information",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/status - Get PowerBox real-time status data
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/status")]
    public ApiResponse GetPowerBoxStatus()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var status = new
            {
                // Environment
                CoreTemp = GetPropertyDouble(powerBox, "CoreTemp"),
                Temperature = GetPropertyDouble(powerBox, "Temperature"),
                Humidity = GetPropertyDouble(powerBox, "Humidity"),
                DewPoint = GetPropertyDouble(powerBox, "DewPoint"),
                UpTime = GetPropertyValue(powerBox, "UpTimeFormatted", ""),
                ExtSensor = GetPropertyBool(powerBox, "ExtSensor"),
                HasWifi = GetPropertyBool(powerBox, "HasWifi"),
                EnvUpdateRate = GetPropertyInt(powerBox, "EnvUpdateRate"),
                TemperatureOffset = GetPropertyDouble(powerBox, "TemperatureOffset"),
                HumidityOffset = GetPropertyDouble(powerBox, "HumidityOffset"),

                // Supply
                Rail12V = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "Supply12V"),
                Rail12A = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "Supply12A"),
                Rail12W = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "Supply12W"),
                Rail5V = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "Supply5V"),
                Rail5A = GetPropertyDouble(powerBox, "Supply5A"),
                Rail5W = GetPropertyDouble(powerBox, "Supply5W"),
                AverageAmps = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "AverageAmps"),
                AmpsPerHour = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "AmpsPerHour"),
                WattsPerHour = GetPropertyDouble(GetPropertyObject(powerBox, "PowerSupply"), "WattsPerHour"),
                UpdateRate = GetPropertyInt(powerBox, "UpdateRate")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = status,
                StatusCode = 200,
                Type = "PowerBoxStatus"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox status: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox status",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/powerports/status - Get PowerBox power port status
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/powerports/status")]
    public ApiResponse GetPowerBoxPowerPorts()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var powerPorts = GetPropertyObject(powerBox, "PowerPorts");
            if (powerPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            int actualPowerPortCount = GetPropertyInt(powerBox, "ActualPowerPortCount", -1);
            var ports = MapPowerPorts(powerPorts, actualPowerPortCount);
            
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = ports,
                StatusCode = 200,
                Type = "PowerPorts"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox power ports: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox power ports",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/powerports/{portIndex}/set - Enable/disable a power port
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/powerports/{portIndex}/set-enabled")]
    public ApiResponse SetPowerPort(int portIndex, [QueryField] bool enabled)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var powerPorts = GetPropertyObject(powerBox, "PowerPorts");
            if (powerPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(powerPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Power port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Check if port is read-only
            bool isReadOnly = GetPropertyBool(targetPort, "ReadOnly");
            if (isReadOnly)
            {
                HttpContext.Response.StatusCode = 403;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot modify read-only power port",
                    StatusCode = 403,
                    Type = "Error"
                };
            }

            // Set the Enabled property
            var enabledProperty = targetPort.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanWrite)
            {
                enabledProperty.SetValue(targetPort, enabled);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Enabled property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PowerPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting power port {portIndex}: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting power port state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/powerports/{portIndex}/set-name - Set power port name
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/powerports/{portIndex}/set-name")]
    public ApiResponse SetPowerPortName(int portIndex, [QueryField] string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var powerPorts = GetPropertyObject(powerBox, "PowerPorts");
            if (powerPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(powerPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Power port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Name property
            var nameProperty = targetPort.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(targetPort, name);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Name property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PowerPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting power port {portIndex} name: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting power port name",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/powerports/{portIndex}/set-bootstate - Set power port boot state
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/powerports/{portIndex}/set-bootstate")]
    public ApiResponse SetPowerPortBootState(int portIndex, [QueryField] bool bootstate)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var powerPorts = GetPropertyObject(powerBox, "PowerPorts");
            if (powerPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(powerPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Power port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the BootState property
            var bootStateProperty = targetPort.GetType().GetProperty("BootState", BindingFlags.Public | BindingFlags.Instance);
            if (bootStateProperty != null && bootStateProperty.CanWrite)
            {
                bootStateProperty.SetValue(targetPort, bootstate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set BootState property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PowerPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting power port {portIndex} boot state: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting power port boot state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/usbports/status - Get PowerBox USB port information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/usbports/status")]
    public ApiResponse GetPowerBoxUSBPorts()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var usbPorts = GetPropertyObject(powerBox, "USBPorts");
            if (usbPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            int actualUSBPortCount = GetPropertyInt(powerBox, "ActualUSBPortCount", -1);
            var ports = MapPowerPorts(usbPorts, actualUSBPortCount);
            
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = ports,
                StatusCode = 200,
                Type = "USBPorts"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox USB ports: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox USB ports",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/usbports/{portIndex}/set-enabled - Enable/disable a USB port
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/usbports/{portIndex}/set-enabled")]
    public ApiResponse SetUSBPort(int portIndex, [QueryField] bool enabled)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var usbPorts = GetPropertyObject(powerBox, "USBPorts");
            if (usbPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(usbPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"USB port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Check if port is read-only
            bool isReadOnly = GetPropertyBool(targetPort, "ReadOnly");
            if (isReadOnly)
            {
                HttpContext.Response.StatusCode = 403;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot modify read-only USB port",
                    StatusCode = 403,
                    Type = "Error"
                };
            }

            // Set the Enabled property
            var enabledProperty = targetPort.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanWrite)
            {
                enabledProperty.SetValue(targetPort, enabled);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Enabled property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "USBPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting USB port {portIndex}: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting USB port state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/usbports/{portIndex}/set-name - Set USB port name
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/usbports/{portIndex}/set-name")]
    public ApiResponse SetUSBPortName(int portIndex, [QueryField] string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var usbPorts = GetPropertyObject(powerBox, "USBPorts");
            if (usbPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(usbPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"USB port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Name property
            var nameProperty = targetPort.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(targetPort, name);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Name property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "USBPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting USB port {portIndex} name: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting USB port name",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/usbports/{portIndex}/set-bootstate - Set USB port boot state
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/usbports/{portIndex}/set-bootstate")]
    public ApiResponse SetUSBPortBootState(int portIndex, [QueryField] bool bootstate)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var usbPorts = GetPropertyObject(powerBox, "USBPorts");
            if (usbPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(usbPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "USBPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"USB port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the BootState property
            var bootStateProperty = targetPort.GetType().GetProperty("BootState", BindingFlags.Public | BindingFlags.Instance);
            if (bootStateProperty != null && bootStateProperty.CanWrite)
            {
                bootStateProperty.SetValue(targetPort, bootstate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set BootState property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                BootState = GetPropertyBool(targetPort, "BootState"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent"),
                ReadOnly = GetPropertyBool(targetPort, "ReadOnly")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "USBPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting USB port {portIndex} boot state: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting USB port boot state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/dewports/status - Get PowerBox dew port information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/dewports/status")]
    public ApiResponse GetPowerBoxDewPorts()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            int actualDewPortCount = GetPropertyInt(powerBox, "ActualDewPortCount", -1);
            var ports = MapDewPorts(dewPorts, actualDewPortCount);
            
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = ports,
                StatusCode = 200,
                Type = "DewPorts"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox dew ports: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox dew ports",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/dewports/{portIndex}/set-enabled - Enable/disable a dew port
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/dewports/{portIndex}/set-enabled")]
    public ApiResponse SetDewPortEnabled(int portIndex, [QueryField] bool enabled)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Dew port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Check if AutoMode is enabled - cannot manually control when in auto mode
            bool autoMode = GetPropertyBool(targetPort, "AutoMode");
            if (autoMode)
            {
                HttpContext.Response.StatusCode = 403;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot manually control dew port when AutoMode is enabled",
                    StatusCode = 403,
                    Type = "Error"
                };
            }

            // Set the Enabled property
            var enabledProperty = targetPort.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanWrite)
            {
                enabledProperty.SetValue(targetPort, enabled);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Enabled property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new DewPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                PowerLevel = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                AutoMode = GetPropertyBool(targetPort, "AutoMode"),
                AutoThreshold = GetPropertyDouble(targetPort, "AutoThreshold"),
                Probe = GetPropertyDouble(targetPort, "Probe"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "DewPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting dew port {portIndex} enabled: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting dew port enabled state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/dewports/{portIndex}/set-name - Set dew port name
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/dewports/{portIndex}/set-name")]
    public ApiResponse SetDewPortName(int portIndex, [QueryField] string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Dew port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Name property
            var nameProperty = targetPort.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(targetPort, name);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Name property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new DewPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                PowerLevel = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                AutoMode = GetPropertyBool(targetPort, "AutoMode"),
                AutoThreshold = GetPropertyDouble(targetPort, "AutoThreshold"),
                Probe = GetPropertyDouble(targetPort, "Probe"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "DewPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting dew port {portIndex} name: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting dew port name",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/dewports/{portIndex}/set-automode - Set dew port auto mode
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/dewports/{portIndex}/set-automode")]
    public ApiResponse SetDewPortAutoMode(int portIndex, [QueryField] bool automode)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Dew port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the AutoMode property
            var autoModeProperty = targetPort.GetType().GetProperty("AutoMode", BindingFlags.Public | BindingFlags.Instance);
            if (autoModeProperty != null && autoModeProperty.CanWrite)
            {
                autoModeProperty.SetValue(targetPort, automode);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set AutoMode property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new DewPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                PowerLevel = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                AutoMode = GetPropertyBool(targetPort, "AutoMode"),
                AutoThreshold = GetPropertyDouble(targetPort, "AutoThreshold"),
                Probe = GetPropertyDouble(targetPort, "Probe"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "DewPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting dew port {portIndex} auto mode: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting dew port auto mode",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/dewports/{portIndex}/set-autothreshold - Set dew port auto threshold
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/dewports/{portIndex}/set-autothreshold")]
    public ApiResponse SetDewPortAutoThreshold(int portIndex, [QueryField] double autothreshold)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Dew port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the AutoThreshold property
            var autoThresholdProperty = targetPort.GetType().GetProperty("AutoThreshold", BindingFlags.Public | BindingFlags.Instance);
            if (autoThresholdProperty != null && autoThresholdProperty.CanWrite)
            {
                autoThresholdProperty.SetValue(targetPort, autothreshold);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set AutoThreshold property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new DewPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                PowerLevel = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                AutoMode = GetPropertyBool(targetPort, "AutoMode"),
                AutoThreshold = GetPropertyDouble(targetPort, "AutoThreshold"),
                Probe = GetPropertyDouble(targetPort, "Probe"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "DewPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting dew port {portIndex} auto threshold: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting dew port auto threshold",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/dewports/{portIndex}/set-powerlevel - Set dew port power level
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/dewports/{portIndex}/set-powerlevel")]
    public ApiResponse SetDewPortPowerLevel(int portIndex, [QueryField] int powerlevel)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var dewPorts = GetPropertyObject(powerBox, "DewPorts");
            if (dewPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "DewPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                int index = GetPropertyInt(port, "Index");
                if (index == portIndex)
                {
                    targetPort = port;
                    break;
                }
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Dew port {portIndex} not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Check if AutoMode is enabled - cannot manually control when in auto mode
            bool autoMode = GetPropertyBool(targetPort, "AutoMode");
            if (autoMode)
            {
                HttpContext.Response.StatusCode = 403;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot manually control dew port when AutoMode is enabled",
                    StatusCode = 403,
                    Type = "Error"
                };
            }

            // Set the SetPower property
            var setPowerProperty = targetPort.GetType().GetProperty("SetPower", BindingFlags.Public | BindingFlags.Instance);
            if (setPowerProperty != null && setPowerProperty.CanWrite)
            {
                setPowerProperty.SetValue(targetPort, powerlevel);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set power level on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new DewPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                PowerLevel = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                AutoMode = GetPropertyBool(targetPort, "AutoMode"),
                AutoThreshold = GetPropertyDouble(targetPort, "AutoThreshold"),
                Probe = GetPropertyDouble(targetPort, "Probe"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "DewPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting dew port {portIndex} power level: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting dew port power level",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/buck/status - Get PowerBox buck converter port information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/buck/status")]
    public ApiResponse GetPowerBoxBuckPort()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var buckPorts = GetPropertyObject(powerBox, "BuckPorts");
            if (buckPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            int actualBuckPortCount = GetPropertyInt(powerBox, "ActualBuckPortCount", -1);
            var ports = MapBuckPorts(buckPorts, actualBuckPortCount);
            
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = ports.Ports.Length > 0 ? ports.Ports[0] : null,
                StatusCode = 200,
                Type = "BuckPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox buck port: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox buck port",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/buck/set-enabled - Enable/disable the buck converter port
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/buck/set-enabled")]
    public ApiResponse SetBuckPortEnabled([QueryField] bool enabled)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var buckPorts = GetPropertyObject(powerBox, "BuckPorts");
            if (buckPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(buckPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Buck port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Enabled property
            var enabledProperty = targetPort.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanWrite)
            {
                enabledProperty.SetValue(targetPort, enabled);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Enabled property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new BuckPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                SetVoltage = GetPropertyDouble(targetPort, "SetVoltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                MaxVoltage = GetPropertyDouble(targetPort, "MaxVoltage"),
                MinVoltage = GetPropertyDouble(targetPort, "MinVoltage"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "BuckPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting buck port enabled: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting buck port enabled state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/buck/set-name - Set buck converter port name
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/buck/set-name")]
    public ApiResponse SetBuckPortName([QueryField] string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var buckPorts = GetPropertyObject(powerBox, "BuckPorts");
            if (buckPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(buckPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Buck port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Name property
            var nameProperty = targetPort.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(targetPort, name);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Name property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new BuckPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                SetVoltage = GetPropertyDouble(targetPort, "SetVoltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                MaxVoltage = GetPropertyDouble(targetPort, "MaxVoltage"),
                MinVoltage = GetPropertyDouble(targetPort, "MinVoltage"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "BuckPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting buck port name: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting buck port name",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/buck/set-bootstate - Set buck converter port boot state
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/buck/set-bootstate")]
    public ApiResponse SetBuckPortBootState([QueryField] bool bootstate)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var buckPorts = GetPropertyObject(powerBox, "BuckPorts");
            if (buckPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(buckPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Buck port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the BootState property
            var bootStateProperty = targetPort.GetType().GetProperty("BootState", BindingFlags.Public | BindingFlags.Instance);
            if (bootStateProperty != null && bootStateProperty.CanWrite)
            {
                bootStateProperty.SetValue(targetPort, bootstate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set BootState property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new BuckPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                SetVoltage = GetPropertyDouble(targetPort, "SetVoltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                MaxVoltage = GetPropertyDouble(targetPort, "MaxVoltage"),
                MinVoltage = GetPropertyDouble(targetPort, "MinVoltage"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "BuckPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting buck port boot state: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting buck port boot state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/buck/set-voltage - Set buck converter output voltage
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/buck/set-voltage")]
    public ApiResponse SetBuckPortVoltage([QueryField] double voltage)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var buckPorts = GetPropertyObject(powerBox, "BuckPorts");
            if (buckPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(buckPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "BuckPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Buck port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Get the current limits for validation
            double minVoltage = GetPropertyDouble(targetPort, "MinVoltage");
            double maxVoltage = GetPropertyDouble(targetPort, "MaxVoltage");

            if (voltage < minVoltage || voltage > maxVoltage)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Voltage {voltage} is outside valid range {minVoltage}-{maxVoltage}V",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Set the SetVoltage property
            var voltageProperty = targetPort.GetType().GetProperty("SetVoltage", BindingFlags.Public | BindingFlags.Instance);
            if (voltageProperty != null && voltageProperty.CanWrite)
            {
                voltageProperty.SetValue(targetPort, voltage);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set voltage on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new BuckPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Voltage = GetPropertyDouble(targetPort, "Voltage"),
                SetVoltage = GetPropertyDouble(targetPort, "SetVoltage"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                MaxVoltage = GetPropertyDouble(targetPort, "MaxVoltage"),
                MinVoltage = GetPropertyDouble(targetPort, "MinVoltage"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "BuckPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting buck port voltage: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting buck port voltage",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/pwm/status - Get PowerBox PWM port information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/pwm/status")]
    public ApiResponse GetPowerBoxPWMPort()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var pwmPorts = GetPropertyObject(powerBox, "PWMPorts");
            if (pwmPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            int actualPWMPortCount = GetPropertyInt(powerBox, "ActualPWMPortCount", -1);
            var ports = MapPWMPorts(pwmPorts, actualPWMPortCount);
            
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = ports.Ports.Length > 0 ? ports.Ports[0] : null,
                StatusCode = 200,
                Type = "PWMPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PowerBox PWM port: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PowerBox PWM port",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/pwm/set-enabled - Enable/disable the PWM port
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/pwm/set-enabled")]
    public ApiResponse SetPWMPortEnabled([QueryField] bool enabled)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var pwmPorts = GetPropertyObject(powerBox, "PWMPorts");
            if (pwmPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(pwmPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWM port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Enabled property
            var enabledProperty = targetPort.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanWrite)
            {
                enabledProperty.SetValue(targetPort, enabled);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Enabled property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PWMPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Power = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PWMPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting PWM port enabled: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting PWM port enabled state",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/pwm/set-name - Set PWM port name
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/pwm/set-name")]
    public ApiResponse SetPWMPortName([QueryField] string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Name cannot be empty",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var pwmPorts = GetPropertyObject(powerBox, "PWMPorts");
            if (pwmPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(pwmPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWM port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Set the Name property
            var nameProperty = targetPort.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(targetPort, name);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set Name property on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PWMPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Power = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PWMPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting PWM port name: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting PWM port name",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/pwm/set-power - Set PWM port output power
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/pwm/set-power")]
    public ApiResponse SetPWMPortPower([QueryField] int power)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var pwmPorts = GetPropertyObject(powerBox, "PWMPorts");
            if (pwmPorts == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsCollection = GetPropertyObject(pwmPorts, "Ports");
            if (portsCollection == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWMPorts collection not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            object targetPort = null;

            foreach (var port in portsEnumerable)
            {
                targetPort = port;
                break;
            }

            if (targetPort == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PWM port not found",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Get the resolution for validation
            int resolution = GetPropertyInt(targetPort, "Resolution");

            if (power < 0 || power > resolution)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Power {power} is outside valid range 0-{resolution}",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Set the SetPower property
            var powerProperty = targetPort.GetType().GetProperty("SetPower", BindingFlags.Public | BindingFlags.Instance);
            if (powerProperty != null && powerProperty.CanWrite)
            {
                powerProperty.SetValue(targetPort, power);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set power on this port",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return updated port info
            var portInfo = new PWMPortInfo
            {
                Index = GetPropertyInt(targetPort, "Index"),
                Name = GetPropertyValue(targetPort, "Name", ""),
                Power = GetPropertyInt(targetPort, "Power"),
                SetPower = GetPropertyInt(targetPort, "SetPower"),
                Current = GetPropertyDouble(targetPort, "Current"),
                Enabled = GetPropertyBool(targetPort, "Enabled"),
                Resolution = GetPropertyInt(targetPort, "Resolution"),
                Overcurrent = GetPropertyBool(targetPort, "Overcurrent")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = portInfo,
                StatusCode = 200,
                Type = "PWMPort"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting PWM port power: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting PWM port power",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/powerbox/wifi - Get PowerBox WiFi current status
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/powerbox/wifi")]
    public ApiResponse GetPowerBoxWiFi()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var wifiObj = GetPropertyObject(powerBox, "WiFi");
            if (wifiObj == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "WiFi information not available",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var wifiInfo = new
            {
                SSID = GetPropertyValue(wifiObj, "SSID", ""),
                IPAddress = GetPropertyValue(wifiObj, "IPAddress", ""),
                HostName = GetPropertyValue(wifiObj, "HostName", ""),
                Mode = GetPropertyValue(wifiObj, "Mode", ""),
                RSSI = GetPropertyInt(wifiObj, "RSSI"),
                Channel = GetPropertyInt(wifiObj, "Channel")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = wifiInfo,
                StatusCode = 200,
                Type = "WiFiInfo"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving WiFi information: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving WiFi information",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/powerbox/wifi/connect-ap - Create a WiFi hotspot (Access Point mode)
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/powerbox/wifi/connect-ap")]
    public ApiResponse ConnectWiFiAP([QueryField] string ssid, [QueryField] string password)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            if (string.IsNullOrEmpty(ssid) || string.IsNullOrEmpty(password))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "SSID and password are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Validate password length (WiFi standard requires at least 8 characters)
            if (password.Length < 8)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Password must be at least 8 characters",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Set the SSID and password properties for hotspot mode
            var ssidProperty = powerBox.GetType().GetProperty("WiFiSSID", BindingFlags.Public | BindingFlags.Instance);
            var passwordProperty = powerBox.GetType().GetProperty("WiFiPASS", BindingFlags.Public | BindingFlags.Instance);

            if (ssidProperty != null && ssidProperty.CanWrite)
            {
                ssidProperty.SetValue(powerBox, ssid);
            }

            if (passwordProperty != null && passwordProperty.CanWrite)
            {
                passwordProperty.SetValue(powerBox, password);
            }

            // Get the WiFiHotspotCommand and execute it
            var wifiHotspotCommandProperty = powerBox.GetType().GetProperty("WiFiHotspotCommand", BindingFlags.Public | BindingFlags.Instance);
            if (wifiHotspotCommandProperty == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "WiFi hotspot mode not supported on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var command = wifiHotspotCommandProperty.GetValue(powerBox);
            if (command == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "WiFi hotspot command not available",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Execute the command asynchronously
            var executeMethod = command.GetType().GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
            if (executeMethod == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot execute WiFi hotspot command",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Execute the async command
            var task = executeMethod.Invoke(command, new[] { (object)null }) as Task;
            if (task != null)
            {
                task.Wait();
            }

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "Success",
                Response = new { Message = $"Creating WiFi hotspot: {ssid}" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error creating WiFi AP: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while creating WiFi hotspot",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    [Route(HttpVerbs.Get, "/pins/meteostation")]
    public ApiResponse GetMeteoStationInfo()
    {
        try
        {
            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var info = new
            {
                Name = GetPropertyValue(meteoStation, "Name", ""),
                DisplayName = GetPropertyValue(meteoStation, "DisplayName", ""),
                Id = GetPropertyValue(meteoStation, "Id", ""),
                Description = GetPropertyValue(meteoStation, "Description", ""),
                DriverInfo = GetPropertyValue(meteoStation, "DriverInfo", ""),
                DriverVersion = GetPropertyValue(meteoStation, "DriverVersion", ""),
                Firmware = GetPropertyValue(meteoStation, "Firmware", ""),
                Connected = GetPropertyBool(meteoStation, "Connected"),
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = info,
                StatusCode = 200,
                Type = "MeteoStationInfo"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving MeteoStation information: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving MeteoStation information",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/set-temperature-offset - Set PowerBox temperature sensor offset
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/set-temperature-offset")]
    public ApiResponse SetPowerBoxTemperatureOffset([QueryField] double offset)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var temperatureOffsetProperty = powerBox.GetType().GetProperty("TemperatureOffset", BindingFlags.Public | BindingFlags.Instance);
            if (temperatureOffsetProperty != null && temperatureOffsetProperty.CanWrite)
            {
                temperatureOffsetProperty.SetValue(powerBox, offset);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set temperature offset on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current temperature and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Temperature = GetPropertyDouble(powerBox, "Temperature"),
                    TemperatureOffset = GetPropertyDouble(powerBox, "TemperatureOffset")
                },
                StatusCode = 200,
                Type = "EnvironmentConfig"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting temperature offset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting temperature offset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/set-humidity-offset - Set PowerBox humidity sensor offset
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/set-humidity-offset")]
    public ApiResponse SetPowerBoxHumidityOffset([QueryField] double offset)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var humidityOffsetProperty = powerBox.GetType().GetProperty("HumidityOffset", BindingFlags.Public | BindingFlags.Instance);
            if (humidityOffsetProperty != null && humidityOffsetProperty.CanWrite)
            {
                humidityOffsetProperty.SetValue(powerBox, offset);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set humidity offset on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current humidity and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Humidity = GetPropertyDouble(powerBox, "Humidity"),
                    HumidityOffset = GetPropertyDouble(powerBox, "HumidityOffset")
                },
                StatusCode = 200,
                Type = "EnvironmentConfig"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting humidity offset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting humidity offset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/set-env-update-rate - Set PowerBox environment sensor update rate
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/set-env-update-rate")]
    public ApiResponse SetPowerBoxEnvUpdateRate([QueryField] int updateRate)
    {
        try
        {
            // Validate update rate range (1-60 seconds)
            if (updateRate < 1 || updateRate > 60)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Environment update rate must be between 1 and 60 seconds",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var envUpdateRateProperty = powerBox.GetType().GetProperty("EnvUpdateRate", BindingFlags.Public | BindingFlags.Instance);
            if (envUpdateRateProperty != null && envUpdateRateProperty.CanWrite)
            {
                envUpdateRateProperty.SetValue(powerBox, updateRate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set environment update rate on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current temperature and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    EnvUpdateRate = GetPropertyInt(powerBox, "EnvUpdateRate"),
                },
                StatusCode = 200,
                Type = "EnvironmentConfig"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting environment update rate: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting environment update rate",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/powerbox/set-update-rate - Set PowerBox data update rate (in seconds, 1-60s)
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/powerbox/set-update-rate")]
    public ApiResponse SetPowerBoxUpdateRate([QueryField] int updateRate)
    {
        try
        {
            // Validate update rate range (1-60 seconds)
            if (updateRate < 1 || updateRate > 60)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Update rate must be between 1 and 60 seconds",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var updateRateProperty = powerBox.GetType().GetProperty("UpdateRate", BindingFlags.Public | BindingFlags.Instance);
            if (updateRateProperty != null && updateRateProperty.CanWrite)
            {
                updateRateProperty.SetValue(powerBox, updateRate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set update rate on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current temperature and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    UpdateRate = GetPropertyInt(powerBox, "UpdateRate"),
                },
                StatusCode = 200,
                Type = "Config"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting update rate: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting update rate",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/meteostation/status - Get MeteoStation real-time status data
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/meteostation/status")]
    public ApiResponse GetMeteoStationStatus()
    {
        try
        {
            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var status = new
            {
                Connected = GetPropertyBool(meteoStation, "Connected"),
                Temperature = GetPropertyDouble(meteoStation, "Temperature"),
                Humidity = GetPropertyDouble(meteoStation, "Humidity"),
                DewPoint = GetPropertyDouble(meteoStation, "DewPoint"),
                SkyBrightness = GetPropertyDouble(meteoStation, "SkyBrightness"),
                SkyQuality = GetPropertyDouble(meteoStation, "SkyQuality"),
                SkyTemperature = GetPropertyDouble(meteoStation, "SkyTemperature"),
                CloudCover = GetPropertyDouble(meteoStation, "CloudCover"),
                UpTime = GetPropertyValue(meteoStation, "UpTimeFormatted", ""),
                EnvUpdateRate = GetPropertyInt(meteoStation, "UpdateRate"),
                TemperatureOffset = GetPropertyDouble(meteoStation, "TemperatureOffset"),
                HumidityOffset = GetPropertyDouble(meteoStation, "HumidityOffset")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = status,
                StatusCode = 200,
                Type = "MeteoStationStatus"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving MeteoStation status: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving MeteoStation status",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/meteostation/set-temperature-offset - Set MeteoStation temperature sensor offset
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/meteostation/set-temperature-offset")]
    public ApiResponse SetMeteoStationTemperatureOffset([QueryField] double offset)
    {
        try
        {
            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var temperatureOffsetProperty = meteoStation.GetType().GetProperty("TemperatureOffset", BindingFlags.Public | BindingFlags.Instance);
            if (temperatureOffsetProperty != null && temperatureOffsetProperty.CanWrite)
            {
                // Invoke the property setter method directly
                var setMethod = temperatureOffsetProperty.GetSetMethod();
                if (setMethod != null)
                {
                    setMethod.Invoke(meteoStation, new object[] { offset });
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set temperature offset on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current temperature and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Temperature = GetPropertyDouble(meteoStation, "Temperature"),
                    TemperatureOffset = GetPropertyDouble(meteoStation, "TemperatureOffset")
                },
                StatusCode = 200,
                Type = "EnvironmentConfig"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting temperature offset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting temperature offset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/meteostation/set-humidity-offset - Set MeteoStation humidity sensor offset
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/meteostation/set-humidity-offset")]
    public ApiResponse SetMeteoStationHumidityOffset([QueryField] double offset)
    {
        try
        {
            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var humidityOffsetProperty = meteoStation.GetType().GetProperty("HumidityOffset", BindingFlags.Public | BindingFlags.Instance);
            if (humidityOffsetProperty != null && humidityOffsetProperty.CanWrite)
            {
                // Invoke the property setter method directly
                var setMethod = humidityOffsetProperty.GetSetMethod();
                if (setMethod != null)
                {
                    setMethod.Invoke(meteoStation, new object[] { offset });
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set humidity offset on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current humidity and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Humidity = GetPropertyDouble(meteoStation, "Humidity"),
                    HumidityOffset = GetPropertyDouble(meteoStation, "HumidityOffset")
                },
                StatusCode = 200,
                Type = "EnvironmentConfig"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting humidity offset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting humidity offset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/meteostation/set-update-rate - Set MeteoStation environment sensor update rate (in seconds, 1-60s)
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/meteostation/set-update-rate")]
    public ApiResponse SetMeteoStationUpdateRate([QueryField] int updateRate)
    {
        try
        {
            // Validate update rate range (1-60 seconds)
            if (updateRate < 1 || updateRate > 60)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Update rate must be between 1 and 60 seconds",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var updateRateProperty = meteoStation.GetType().GetProperty("UpdateRate", BindingFlags.Public | BindingFlags.Instance);
            if (updateRateProperty != null && updateRateProperty.CanWrite)
            {
                updateRateProperty.SetValue(meteoStation, updateRate);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Cannot set update rate on this device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            // Return current temperature and offset
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    UpdateRate = GetPropertyInt(meteoStation, "UpdateRate"),
                },
                StatusCode = 200,
                Type = "Config"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting update rate: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting update rate",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/meteostation/factory-reset - Perform factory reset on the connected MeteoStation device
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/meteostation/factory-reset")]
    public ApiResponse MeteoStationFactoryReset()
    {
        try
        {
            var meteoStation = GetPINSConnectedMeteoStation();
            if (meteoStation == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "MeteoStation device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Invoke the Reset method using reflection (private async Task Reset())
            var factoryResetMethod = meteoStation.GetType().GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (factoryResetMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Reset method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            // Execute the factory reset
            factoryResetMethod.Invoke(meteoStation, null);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Message = "Factory reset initiated on MeteoStation device",
                    DeviceId = GetPropertyValue(meteoStation, "Id", "")
                },
                StatusCode = 200,
                Type = "FactoryResetResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error performing factory reset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while performing factory reset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/devices - Get all connected PINS devices
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/devices")]
    public ApiResponse GetAllPinsDevices()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            var meteoStation = GetPINSConnectedMeteoStation();
            var lensControl = GetPINSConnectedLensControl();

            var devices = new
            {
                powerBox = GetPropertyValue(powerBox, "Id", "not connected"),
                meteoStation = GetPropertyValue(meteoStation, "Id", "not connected"),
                lensControl = GetPropertyValue(lensControl, "Id", "not connected")
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = devices,
                StatusCode = 200,
                Type = "PinsDevices"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving PINS devices: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving PINS devices",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/powerbox/factory-reset - Perform factory reset on the connected PowerBox device
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/powerbox/factory-reset")]
    public ApiResponse PowerBoxFactoryReset()
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Invoke the Reset method using reflection (private async Task Reset())
            var factoryResetMethod = powerBox.GetType().GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (factoryResetMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Reset method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            // Execute the factory reset
            factoryResetMethod.Invoke(powerBox, null);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Message = "Factory reset initiated on PowerBox device",
                    DeviceId = GetPropertyValue(powerBox, "Id", "")
                },
                StatusCode = 200,
                Type = "FactoryResetResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error performing factory reset: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while performing factory reset",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/powerbox/beep - Make PowerBox beep with specified volume and duration
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/powerbox/beep")]
    public ApiResponse BeepPowerBox([QueryField] int volume = 100, [QueryField] int lengthMs = 1000)
    {
        try
        {
            var powerBox = GetPINSConnectedPowerBox();
            if (powerBox == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "PowerBox device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            // Call the Beep method via reflection with volume and length parameters
            var method = powerBox.GetType().GetMethod("Beep", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (method == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Beep method not available on PowerBox device",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            try
            {
                method.Invoke(powerBox, new object[] { volume, lengthMs });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error invoking Beep method: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Error calling Beep method: {ex.InnerException?.Message ?? ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Message = "PowerBox beep successful", Volume = volume, LengthMs = lengthMs },
                StatusCode = 200,
                Type = "Success"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error beeping PowerBox: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while beeping PowerBox",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    #region Helper Methods

    private PowerPortsInfo MapPowerPorts(object powerPorts, int maxPorts = -1)
    {
        if (powerPorts == null) return new PowerPortsInfo { MaxPorts = 0, Ports = new PortInfo[0] };
        if (maxPorts == 0) return new PowerPortsInfo { MaxPorts = 0, Ports = new PortInfo[0] };

        try
        {
            var portsCollection = GetPropertyObject(powerPorts, "Ports");
            if (portsCollection == null) return new PowerPortsInfo { MaxPorts = 0, Ports = new PortInfo[0] };

            var portsList = new List<PortInfo>();
            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            if (portsEnumerable != null)
            {
                foreach (var port in portsEnumerable)
                {
                    if (maxPorts >= 0 && portsList.Count >= maxPorts) break;
                    portsList.Add(new PortInfo
                    {
                        Index = GetPropertyInt(port, "Index"),
                        Name = GetPropertyValue(port, "Name", ""),
                        Voltage = GetPropertyDouble(port, "Voltage"),
                        Current = GetPropertyDouble(port, "Current"),
                        Enabled = GetPropertyBool(port, "Enabled"),
                        BootState = GetPropertyBool(port, "BootState"),
                        Overcurrent = GetPropertyBool(port, "Overcurrent"),
                        ReadOnly = GetPropertyBool(port, "ReadOnly")
                    });
                }
            }

            return new PowerPortsInfo
            {
                MaxPorts = portsList.Count,
                Ports = portsList.ToArray()
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error mapping power ports: {ex.Message}");
            return new PowerPortsInfo { MaxPorts = 0, Ports = new PortInfo[0] };
        }
    }

    private DewPortsInfo MapDewPorts(object dewPorts, int maxPorts = -1)
    {
        if (dewPorts == null) return new DewPortsInfo { MaxPorts = 0, Ports = new DewPortInfo[0] };
        if (maxPorts == 0) return new DewPortsInfo { MaxPorts = 0, Ports = new DewPortInfo[0] };

        try
        {
            var portsCollection = GetPropertyObject(dewPorts, "Ports");
            if (portsCollection == null) return new DewPortsInfo { MaxPorts = 0, Ports = new DewPortInfo[0] };

            var portsList = new List<DewPortInfo>();
            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            if (portsEnumerable != null)
            {
                foreach (var port in portsEnumerable)
                {
                    if (maxPorts >= 0 && portsList.Count >= maxPorts) break;
                    portsList.Add(new DewPortInfo
                    {
                        Index = GetPropertyInt(port, "Index"),
                        Name = GetPropertyValue(port, "Name", ""),
                        Current = GetPropertyDouble(port, "Current"),
                        Enabled = GetPropertyBool(port, "Enabled"),
                        Resolution = GetPropertyInt(port, "Resolution"),
                        PowerLevel = GetPropertyInt(port, "Power"),
                        SetPower = GetPropertyInt(port, "SetPower"),
                        AutoMode = GetPropertyBool(port, "AutoMode"),
                        AutoThreshold = GetPropertyDouble(port, "AutoThreshold"),
                        Probe = GetPropertyDouble(port, "Probe"),
                        Overcurrent = GetPropertyBool(port, "Overcurrent")
                    });
                }
            }

            return new DewPortsInfo
            {
                MaxPorts = portsList.Count,
                Ports = portsList.ToArray()
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error mapping dew ports: {ex.Message}");
            return new DewPortsInfo { MaxPorts = 0, Ports = new DewPortInfo[0] };
        }
    }

    private BuckPortsInfo MapBuckPorts(object buckPorts, int maxPorts = -1)
    {
        if (buckPorts == null) return new BuckPortsInfo { MaxPorts = 0, Ports = new BuckPortInfo[0] };
        if (maxPorts == 0) return new BuckPortsInfo { MaxPorts = 0, Ports = new BuckPortInfo[0] };

        try
        {
            var portsCollection = GetPropertyObject(buckPorts, "Ports");
            if (portsCollection == null) return new BuckPortsInfo { MaxPorts = 0, Ports = new BuckPortInfo[0] };

            var portsList = new List<BuckPortInfo>();
            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            if (portsEnumerable != null)
            {
                foreach (var port in portsEnumerable)
                {
                    if (maxPorts >= 0 && portsList.Count >= maxPorts) break;
                    portsList.Add(new BuckPortInfo
                    {
                        Index = GetPropertyInt(port, "Index"),
                        Name = GetPropertyValue(port, "Name", ""),
                        Voltage = GetPropertyDouble(port, "Voltage"),
                        SetVoltage = GetPropertyDouble(port, "SetVoltage"),
                        Current = GetPropertyDouble(port, "Current"),
                        Enabled = GetPropertyBool(port, "Enabled"),
                        MaxVoltage = GetPropertyDouble(port, "MaxVoltage"),
                        MinVoltage = GetPropertyDouble(port, "MinVoltage"),
                        Overcurrent = GetPropertyBool(port, "Overcurrent")
                    });
                }
            }

            return new BuckPortsInfo
            {
                MaxPorts = portsList.Count,
                Ports = portsList.ToArray()
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error mapping buck ports: {ex.Message}");
            return new BuckPortsInfo { MaxPorts = 0, Ports = new BuckPortInfo[0] };
        }
    }

    private PWMPortsInfo MapPWMPorts(object pwmPorts, int maxPorts = -1)
    {
        if (pwmPorts == null) return new PWMPortsInfo { MaxPorts = 0, Ports = new PWMPortInfo[0] };
        if (maxPorts == 0) return new PWMPortsInfo { MaxPorts = 0, Ports = new PWMPortInfo[0] };

        try
        {
            var portsCollection = GetPropertyObject(pwmPorts, "Ports");
            if (portsCollection == null) return new PWMPortsInfo { MaxPorts = 0, Ports = new PWMPortInfo[0] };

            var portsList = new List<PWMPortInfo>();
            var portsEnumerable = portsCollection as System.Collections.IEnumerable;
            if (portsEnumerable != null)
            {
                foreach (var port in portsEnumerable)
                {
                    if (maxPorts >= 0 && portsList.Count >= maxPorts) break;
                    portsList.Add(new PWMPortInfo
                    {
                        Index = GetPropertyInt(port, "Index"),
                        Name = GetPropertyValue(port, "Name", ""),
                        Power = GetPropertyInt(port, "Power"),
                        SetPower = GetPropertyInt(port, "SetPower"),
                        Current = GetPropertyDouble(port, "Current"),
                        Enabled = GetPropertyBool(port, "Enabled"),
                        Resolution = GetPropertyInt(port, "Resolution"),
                        Overcurrent = GetPropertyBool(port, "Overcurrent")
                    });
                }
            }

            return new PWMPortsInfo
            {
                MaxPorts = portsList.Count,
                Ports = portsList.ToArray()
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error mapping PWM ports: {ex.Message}");
            return new PWMPortsInfo { MaxPorts = 0, Ports = new PWMPortInfo[0] };
        }
    }

    /// <summary>
    /// GET /api/pins/lenscontrol - Get LensControl device information
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/lenscontrol")]
    public ApiResponse GetLensControlInfo()
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var info = new
            {
                Name = GetPropertyValue(lensControl, "Name", ""),
                DisplayName = GetPropertyValue(lensControl, "DisplayName", ""),
                Id = GetPropertyValue(lensControl, "Id", ""),
                UniqueId = GetPropertyValue(lensControl, "UniqueId", ""),
                Firmware = GetPropertyValue(lensControl, "Firmware", ""),
                DriverVersion = GetPropertyValue(lensControl, "DriverVersion", ""),
                Connected = GetPropertyBool(lensControl, "Connected"),
                LensName = GetPropertyValue(lensControl, "LensName", ""),
                FocalLength = GetPropertyInt(lensControl, "FocalLength"),
                Position = GetPropertyInt(lensControl, "Position"),
                MaxStep = GetPropertyInt(lensControl, "MaxStep"),
                Aperture = GetPropertyInt(lensControl, "Aperture"),
                MinAperture = GetPropertyInt(lensControl, "MinAperture"),
                MaxAperture = GetPropertyInt(lensControl, "MaxAperture"),
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = info,
                StatusCode = 200,
                Type = "LensControlInfo"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving LensControl information: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving LensControl information",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/pins/lenscontrol/status - Get LensControl real-time status
    /// </summary>
    [Route(HttpVerbs.Get, "/pins/lenscontrol/status")]
    public ApiResponse GetLensControlStatus()
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var status = new
            {
                Connected = GetPropertyBool(lensControl, "Connected"),
                LensName = GetPropertyValue(lensControl, "LensName", ""),
                FocalLength = GetPropertyInt(lensControl, "FocalLength"),
                Position = GetPropertyInt(lensControl, "Position"),
                MaxStep = GetPropertyInt(lensControl, "MaxStep"),
                IsMoving = GetPropertyBool(lensControl, "IsMoving"),
                Aperture = GetPropertyInt(lensControl, "Aperture"),
                MinAperture = GetPropertyInt(lensControl, "MinAperture"),
                MaxAperture = GetPropertyInt(lensControl, "MaxAperture"),
            };

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = status,
                StatusCode = 200,
                Type = "LensControlStatus"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error retrieving LensControl status: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while retrieving LensControl status",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/lenscontrol/move?position={int} - Move to absolute position (fire and forget)
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/lenscontrol/move")]
    public ApiResponse MoveLensControl([QueryField] int position)
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var maxStep = GetPropertyInt(lensControl, "MaxStep");
            if (position < 0 || (maxStep > 0 && position > maxStep))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Position must be between 0 and {maxStep}",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var moveMethod = lensControl.GetType().GetMethod(
                "Move", BindingFlags.Public | BindingFlags.Instance);
            if (moveMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Move method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            // Fire and forget — poll /status to track progress
            moveMethod.Invoke(lensControl, new object[] { position, System.Threading.CancellationToken.None, 1000 });

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { TargetPosition = position },
                StatusCode = 200,
                Type = "MoveResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error moving LensControl: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while moving LensControl",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/lenscontrol/halt - Halt any in-progress move
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/lenscontrol/halt")]
    public ApiResponse HaltLensControl()
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var haltMethod = lensControl.GetType().GetMethod(
                "Halt", BindingFlags.Public | BindingFlags.Instance);
            if (haltMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Halt method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            haltMethod.Invoke(lensControl, null);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Position = GetPropertyInt(lensControl, "Position") },
                StatusCode = 200,
                Type = "HaltResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error halting LensControl: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while halting LensControl",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// PUT /api/pins/lenscontrol/set-aperture?aperture={int} - Set lens aperture
    /// </summary>
    [Route(HttpVerbs.Put, "/pins/lenscontrol/set-aperture")]
    public ApiResponse SetLensControlAperture([QueryField] int aperture)
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var minAperture = GetPropertyInt(lensControl, "MinAperture");
            var maxAperture = GetPropertyInt(lensControl, "MaxAperture");
            // maxAperture is widest (numerically smaller), minAperture is narrowest (numerically larger)
            if (maxAperture > 0 && (aperture < maxAperture || aperture > minAperture))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Aperture must be between {maxAperture} (widest) and {minAperture} (narrowest)",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var setApertureMethod = lensControl.GetType().GetMethod(
                "SetAperture", BindingFlags.Public | BindingFlags.Instance);
            if (setApertureMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "SetAperture method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            var success = (bool)setApertureMethod.Invoke(lensControl, new object[] { aperture });
            if (!success)
            {
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Failed to set aperture on device",
                    StatusCode = 500,
                    Type = "Error"
                };
            }

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Aperture = GetPropertyInt(lensControl, "Aperture") },
                StatusCode = 200,
                Type = "ApertureResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting LensControl aperture: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while setting aperture",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/lenscontrol/calibrate - Calibrate the LensControl device
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/lenscontrol/calibrate")]
    public ApiResponse CalibrateLensControl()
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var calibrateMethod = lensControl.GetType().GetMethod(
                "Calibrate", BindingFlags.Public | BindingFlags.Instance);
            if (calibrateMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Calibrate method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            var success = (bool)calibrateMethod.Invoke(lensControl, null);
            if (!success)
            {
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Calibration failed on device",
                    StatusCode = 500,
                    Type = "Error"
                };
            }

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Message = "LensControl calibration initiated" },
                StatusCode = 200,
                Type = "CalibrateResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error calibrating LensControl: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while calibrating LensControl",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/pins/lenscontrol/restart - Restart the LensControl device
    /// </summary>
    [Route(HttpVerbs.Post, "/pins/lenscontrol/restart")]
    public ApiResponse RestartLensControl()
    {
        try
        {
            var lensControl = GetPINSConnectedLensControl();
            if (lensControl == null)
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = "LensControl device not connected",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var rebootMethod = lensControl.GetType().GetMethod(
                "Reboot", BindingFlags.NonPublic | BindingFlags.Instance);
            if (rebootMethod == null)
            {
                HttpContext.Response.StatusCode = 501;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Restart method not available on this device",
                    StatusCode = 501,
                    Type = "Error"
                };
            }

            rebootMethod.Invoke(lensControl, null);

            HttpContext.Response.StatusCode = 200;
            return new ApiResponse
            {
                Success = true,
                Response = new { Message = "LensControl restart initiated" },
                StatusCode = 200,
                Type = "RestartResult"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error restarting LensControl: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = "An error occurred while restarting LensControl",
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    private MeteoStationInfo MapMeteoStationToInfo(object meteoStation)
    {
        var cloudModel = GetPropertyObject(meteoStation, "CloudModel");

        return new MeteoStationInfo
        {
            Name = GetPropertyValue(meteoStation, "Name", ""),
            DisplayName = GetPropertyValue(meteoStation, "DisplayName", ""),
            Id = GetPropertyValue(meteoStation, "Id", ""),
            UniqueId = GetPropertyValue(meteoStation, "UniqueId", ""),
            Firmware = GetPropertyValue(meteoStation, "Firmware", ""),
            DriverVersion = GetPropertyValue(meteoStation, "DriverVersion", ""),
            UpTimeFormatted = GetPropertyValue(meteoStation, "UpTimeFormatted", ""),
            Connected = GetPropertyBool(meteoStation, "Connected"),
            Temperature = GetPropertyDouble(meteoStation, "Temperature"),
            Humidity = GetPropertyDouble(meteoStation, "Humidity"),
            DewPoint = GetPropertyDouble(meteoStation, "DewPoint"),
            SkyBrightness = GetPropertyDouble(meteoStation, "SkyBrightness"),
            SkyQuality = GetPropertyDouble(meteoStation, "SkyQuality"),
            SkyTemperature = GetPropertyDouble(meteoStation, "SkyTemperature"),
            CloudCover = GetPropertyDouble(meteoStation, "CloudCover"),
            UpdateRate = GetPropertyInt(meteoStation, "UpdateRate"),
            TemperatureOffset = GetPropertyDouble(meteoStation, "TemperatureOffset"),
            HumidityOffset = GetPropertyDouble(meteoStation, "HumidityOffset"),
            LuxScalingFactor = GetPropertyDouble(meteoStation, "LuxScalingFactor"),
            CloudModel = new CloudModelInfo
            {
                CloudK1 = GetPropertyInt(meteoStation, "CloudK1"),
                CloudK2 = GetPropertyInt(meteoStation, "CloudK2"),
                CloudK3 = GetPropertyInt(meteoStation, "CloudK3"),
                CloudK4 = GetPropertyInt(meteoStation, "CloudK4"),
                CloudK5 = GetPropertyInt(meteoStation, "CloudK5"),
                CloudK6 = GetPropertyInt(meteoStation, "CloudK6"),
                CloudK7 = GetPropertyInt(meteoStation, "CloudK7"),
                CloudFlagPercent = GetPropertyInt(meteoStation, "CloudFlagPercent"),
                CloudTemperatureOvercast = GetPropertyInt(meteoStation, "CloudTemperatureOvercast")
            }
        };
    }

    #endregion
}
