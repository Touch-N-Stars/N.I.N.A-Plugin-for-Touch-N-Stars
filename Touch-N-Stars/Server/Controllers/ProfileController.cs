using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for NINA profile management.
/// </summary>
public class ProfileController : WebApiController
{
    /// <summary>
    /// POST /api/profile/horizon
    /// Accepts the raw text content of a .hrz horizon file in the request body.
    /// Parses it, writes it to a temp file, and updates the active profile's horizon settings.
    /// Returns 400 if the body is empty or the horizon data is malformed (fewer than 2 points).
    /// </summary>
    [Route(HttpVerbs.Post, "/profile/horizon")]
    public async Task<object> SetHorizonFromBody()
    {
        string body;
        using (var reader = new StreamReader(HttpContext.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { success = false, error = "Request body is empty" };
        }

        CustomHorizon horizon;
        try
        {
            using var sr = new StringReader(body);
            horizon = CustomHorizon.FromReader_Standard(sr);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 400;
            return new { success = false, error = "Horizon data is malformed: " + ex.Message };
        }

        string filePath = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "tnshorizon.hrz");
        await File.WriteAllTextAsync(filePath, body, Encoding.UTF8);

        TouchNStars.Mediators.Profile.ActiveProfile.AstrometrySettings.HorizonFilePath = filePath;
        TouchNStars.Mediators.Profile.ActiveProfile.AstrometrySettings.Horizon = horizon;

        return "Horizon updated";
    }
}
