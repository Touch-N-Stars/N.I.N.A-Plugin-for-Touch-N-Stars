using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for mount control operations.
/// </summary>
public class MountController : WebApiController
{
    /// <summary>
    /// PUT /api/equipment/mount/trackingrate
    /// Sets a custom sidereal tracking rate on the connected mount.
    /// Body: { "raDegreesPerHour": 0.123, "decDegreesPerHour": 0.0 }
    /// Returns 400 if the body is missing required fields or the mount is not connected.
    /// </summary>
    [Route(HttpVerbs.Put, "/equipment/mount/trackingrate")]
    public async Task<object> SetCustomTrackingRate()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, double>>();

            if (body == null
                || !body.TryGetValue("raDegreesPerHour", out var raDeg)
                || !body.TryGetValue("decDegreesPerHour", out var decDeg))
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Body must contain 'raDegreesPerHour' and 'decDegreesPerHour'" };
            }

            var info = TouchNStars.Mediators.Telescope.GetInfo();
            if (info == null || !info.Connected)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Mount is not connected" };
            }

            var rate = SiderealShiftTrackingRate.Create(raDeg, decDeg);
            bool result = TouchNStars.Mediators.Telescope.SetCustomTrackingRate(rate);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Mount rejected the custom tracking rate" };
            }

            return new { success = true, raDegreesPerHour = raDeg, decDegreesPerHour = decDeg };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }

    // Helpers for guide rate — IndiTelescope is internal to NINA.Equipment so we use reflection
    // to access its public CanSetGuideRates, GuideRateRightAscensionArcsecPerSec, and
    // GuideRateDeclinationArcsecPerSec properties without a compile-time type reference.
    private static bool DeviceCanSetGuideRates(object device)
    {
        if (device == null) return false;
        var prop = device.GetType().GetProperty("CanSetGuideRates", BindingFlags.Public | BindingFlags.Instance);
        return prop != null && prop.GetValue(device) is true;
    }

    private static void SetDeviceGuideRate(object device, double raArcsecPerSec, double decArcsecPerSec)
    {
        var t = device.GetType();
        t.GetProperty("GuideRateRightAscensionArcsecPerSec", BindingFlags.Public | BindingFlags.Instance)?.SetValue(device, raArcsecPerSec);
        t.GetProperty("GuideRateDeclinationArcsecPerSec", BindingFlags.Public | BindingFlags.Instance)?.SetValue(device, decArcsecPerSec);
    }

    /// <summary>
    /// Returns the current RA and DEC guide rates in arcsec/s and as sidereal multipliers.
    /// Also reports whether the driver supports setting guide rates.
    /// Returns 400 if the mount is not connected.
    /// </summary>
    [Route(HttpVerbs.Get, "/equipment/mount/guiderate")]
    public object GetGuideRate()
    {
        try
        {
            var info = TouchNStars.Mediators.Telescope.GetInfo();
            if (info == null || !info.Connected)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Mount is not connected" };
            }

            var device = TouchNStars.Mediators.Telescope.GetDevice();
            bool canSet = DeviceCanSetGuideRates(device);
            double raArcsecPerSec = info.GuideRateRightAscensionArcsecPerSec;
            double decArcsecPerSec = info.GuideRateDeclinationArcsecPerSec;

            return new
            {
                success = true,
                canSetGuideRate = canSet,
                raArcsecPerSec,
                decArcsecPerSec,
                raSiderealMultiplier = double.IsNaN(raArcsecPerSec) ? (object)null : raArcsecPerSec / 15.0,
                decSiderealMultiplier = double.IsNaN(decArcsecPerSec) ? (object)null : decArcsecPerSec / 15.0,
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }

    /// <summary>
    /// PUT /api/equipment/mount/guiderate
    /// Sets the RA and DEC guide rates.
    /// Body: { "raSiderealMultiplier": 0.5, "decSiderealMultiplier": 0.5 }
    /// Values are sidereal multipliers (0.5 = half sidereal rate).
    /// Returns 400 if mount not connected or driver does not support setting guide rates.
    /// </summary>
    [Route(HttpVerbs.Put, "/equipment/mount/guiderate")]
    public async Task<object> SetGuideRate()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, double>>();
            if (body == null
                || !body.TryGetValue("raSiderealMultiplier", out var raMultiplier)
                || !body.TryGetValue("decSiderealMultiplier", out var decMultiplier))
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Body must contain 'raSiderealMultiplier' and 'decSiderealMultiplier'" };
            }

            if (raMultiplier <= 0.0 || raMultiplier > 1.0 || decMultiplier <= 0.0 || decMultiplier > 1.0)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Sidereal multipliers must be in range (0, 1.0]" };
            }

            var info = TouchNStars.Mediators.Telescope.GetInfo();
            if (info == null || !info.Connected)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Mount is not connected" };
            }

            var device = TouchNStars.Mediators.Telescope.GetDevice();
            if (!DeviceCanSetGuideRates(device))
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Mount driver does not support setting guide rates" };
            }

            SetDeviceGuideRate(device, raMultiplier * 15.0, decMultiplier * 15.0);

            return new
            {
                success = true,
                raSiderealMultiplier = raMultiplier,
                decSiderealMultiplier = decMultiplier,
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }
}
