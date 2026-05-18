using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
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
}
