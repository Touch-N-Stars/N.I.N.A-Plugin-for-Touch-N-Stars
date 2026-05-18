using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for guider shift-rate (comet / solar-system tracking) operations.
/// </summary>
public class GuiderShiftController : WebApiController
{
    /// <summary>
    /// PUT /api/equipment/guider/shiftrate
    /// Sends a sidereal shift rate so the guider locks on a moving target.
    /// Body: { "raDegreesPerHour": 0.123, "decDegreesPerHour": 0.0 }
    /// Returns 400 if the body is missing required fields or the guider is not connected.
    /// </summary>
    [Route(HttpVerbs.Put, "/equipment/guider/shiftrate")]
    public async Task<object> SetShiftRate()
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

            var info = TouchNStars.Mediators.Guider.GetInfo();
            if (info == null || !info.Connected)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Guider is not connected" };
            }

            var rate = SiderealShiftTrackingRate.Create(raDeg, decDeg);
            bool result = await TouchNStars.Mediators.Guider.SetShiftRate(rate, CancellationToken.None);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Guider rejected the shift rate" };
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

    /// <summary>
    /// DELETE /api/equipment/guider/shiftrate
    /// Stops guider shift tracking (returns to standard star locking).
    /// Returns 400 if the guider is not connected.
    /// </summary>
    [Route(HttpVerbs.Delete, "/equipment/guider/shiftrate")]
    public async Task<object> StopShifting()
    {
        try
        {
            var info = TouchNStars.Mediators.Guider.GetInfo();
            if (info == null || !info.Connected)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Guider is not connected" };
            }

            bool result = await TouchNStars.Mediators.Guider.StopShifting(CancellationToken.None);

            if (!result)
            {
                HttpContext.Response.StatusCode = 400;
                return new { success = false, error = "Guider failed to stop shifting" };
            }

            return new { success = true };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = ex.Message };
        }
    }
}
