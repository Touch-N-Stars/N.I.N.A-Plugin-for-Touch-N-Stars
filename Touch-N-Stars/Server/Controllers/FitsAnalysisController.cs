using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.PlateSolving;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for FITS file analysis and plate solving.
/// Replicates NINA's Framing Assistant file plate solve flow as REST API.
///
/// Routes:
///   GET  /api/fits/parameters?path=... — Step 1: read FITS headers, return params for frontend form
///   POST /api/fits/analyze            — Step 2: plate solve with confirmed parameters
/// </summary>
internal class FitsAnalysisController : WebApiController
{
    private Task SendJson(object data, int statusCode = 200)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data);
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    // -------------------------------------------------------------------------
    // GET /api/fits/parameters?path=...
    // Step 1: Liest FITS-Header aus und gibt Parameter zurück.
    // Das Frontend zeigt diese dem User zur Kontrolle an (wie NINAs Dialog).
    // Wenn hasWcs=true sind Koordinaten bereits gelöst, kein Solve nötig.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/fits/parameters")]
    public async Task GetParameters()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new FitsParameters { Success = false, Error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));
            if (!File.Exists(fullPath))
            {
                await SendJson(new FitsParameters { Success = false, Error = "File does not exist" }, 404);
                return;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext != ".fits" && ext != ".fit" && ext != ".fts" && ext != ".fz")
            {
                await SendJson(new FitsParameters { Success = false, Error = "File must be a FITS file (.fits, .fit, .fts, .fz)" }, 400);
                return;
            }

            Logger.Info($"[FitsAnalysisController] Reading FITS parameters: {fullPath}");
            var imageData = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(fullPath, 16, false, RawConverterEnum.FREEIMAGE);

            if (imageData == null)
            {
                await SendJson(new FitsParameters { Success = false, Error = "Failed to load FITS file" }, 500);
                return;
            }

            var profile = TouchNStars.Mediators.Profile.ActiveProfile;
            var meta = imageData.MetaData;

            // FocalLength: FITS-Header (FOCALLEN) → Profil-Fallback (exakt wie FramingAssistantVM)
            double focalLength = !double.IsNaN(meta.Telescope.FocalLength) && meta.Telescope.FocalLength > 0
                ? meta.Telescope.FocalLength
                : profile.TelescopeSettings.FocalLength;

            // PixelSize: FITS-Header (XPIXSZ / BinX) → Profil-Fallback
            double pixelSize = !double.IsNaN(meta.Camera.PixelSize) && meta.Camera.PixelSize > 0
                ? meta.Camera.PixelSize
                : profile.CameraSettings.PixelSize;

            int binning = meta.Camera.BinX > 0 ? meta.Camera.BinX : 1;

            // WCS prüfen (bereits gelöst)
            bool hasWcs = meta.WorldCoordinateSystem?.Coordinates != null;

            // Koordinaten-Hint (Teleskop-Pointing oder Target)
            Coordinates coords = meta.Telescope.Coordinates
                                 ?? meta.Target.Coordinates
                                 ?? ExtractCoordinatesFromGenericHeaders(meta.GenericHeaders);

            var result = new FitsParameters
            {
                Success = true,
                HasWcs = hasWcs,
                FocalLength = focalLength,
                PixelSize = pixelSize,
                Binning = binning,
                HasCoordinates = coords != null
            };

            if (hasWcs)
            {
                var wcsCoords = meta.WorldCoordinateSystem.Coordinates;
                result.Ra = wcsCoords.RADegrees;
                result.Dec = wcsCoords.Dec;
                result.RaString = wcsCoords.RAString;
                result.DecString = wcsCoords.DecString;
            }
            else if (coords != null)
            {
                result.Ra = coords.RADegrees;
                result.Dec = coords.Dec;
                result.RaString = coords.RAString;
                result.DecString = coords.DecString;
            }

            await SendJson(result);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FitsAnalysisController.GetParameters] {ex.Message}", ex);
            await SendJson(new FitsParameters { Success = false, Error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // POST /api/fits/analyze
    // Step 2: Plate Solve mit vom User bestätigten Parametern.
    // Body: { path, focalLength, pixelSize, binning, ra?, dec?, blindSolve }
    // Wenn hasWcs=true war, gibt der Endpoint sofort die WCS-Koordinaten zurück.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Post, "/fits/analyze")]
    public async Task Analyze()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null)
            {
                await SendJson(new FitsSolveResult { Success = false, Error = "Missing request body" }, 400);
                return;
            }

            if (!body.TryGetValue("path", out var pathObj) || string.IsNullOrWhiteSpace(pathObj?.ToString()))
            {
                await SendJson(new FitsSolveResult { Success = false, Error = "Missing 'path' in request body" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(pathObj.ToString());
            if (!File.Exists(fullPath))
            {
                await SendJson(new FitsSolveResult { Success = false, Error = "File does not exist" }, 404);
                return;
            }

            // Parameter aus Body lesen (vom Frontend ggf. korrigiert)
            double focalLength = ParseBodyDouble(body, "focalLength");
            double pixelSize   = ParseBodyDouble(body, "pixelSize");
            int    binning     = ParseBodyInt(body, "binning", defaultVal: 1);
            bool   blindSolve  = ParseBodyBool(body, "blindSolve");

            // Optional: Koordinaten-Hint
            double? ra  = ParseBodyNullableDouble(body, "ra");
            double? dec = ParseBodyNullableDouble(body, "dec");

            if (focalLength <= 0)
            {
                await SendJson(new FitsSolveResult { Success = false, Error = "focalLength must be > 0" }, 400);
                return;
            }

            Logger.Info($"[FitsAnalysisController] Loading FITS for plate solve: {fullPath}");
            var imageData = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(fullPath, 16, false, RawConverterEnum.FREEIMAGE);

            if (imageData == null)
            {
                await SendJson(new FitsSolveResult { Success = false, Error = "Failed to load FITS file" }, 500);
                return;
            }

            // WCS bereits vorhanden? → direkt zurückgeben (kein Solve nötig)
            var wcs = imageData.MetaData.WorldCoordinateSystem;
            if (wcs?.Coordinates != null)
            {
                Logger.Info($"[FitsAnalysisController] WCS headers found, returning directly");
                var wcsCoords = wcs.Coordinates;
                await SendJson(new FitsSolveResult
                {
                    Success = true,
                    Ra = wcsCoords.RADegrees,
                    Dec = wcsCoords.Dec,
                    RaString = wcsCoords.RAString,
                    DecString = wcsCoords.DecString,
                    Rotation = wcs.Rotation,
                    PixelScale = wcs.PixelScaleX,
                    SolvedFromWcs = true
                });
                return;
            }

            // Koordinaten-Hint bauen (null = Blind Solve)
            Coordinates hint = null;
            if (!blindSolve && ra.HasValue && dec.HasValue)
            {
                hint = new Coordinates(Angle.ByDegree(ra.Value), Angle.ByDegree(dec.Value), Epoch.J2000);
                Logger.Info($"[FitsAnalysisController] Coordinate hint: RA={ra.Value:F4} Dec={dec.Value:F4}");
            }
            else
            {
                Logger.Info($"[FitsAnalysisController] Blind solve requested");
            }

            var profile = TouchNStars.Mediators.Profile.ActiveProfile;
            var solveSettings = profile.PlateSolveSettings;

            var parameter = new PlateSolveParameter
            {
                FocalLength      = focalLength,
                PixelSize        = pixelSize,
                Binning          = binning,
                Coordinates      = hint,
                SearchRadius     = solveSettings.SearchRadius,
                DownSampleFactor = solveSettings.DownSampleFactor,
                MaxObjects       = solveSettings.MaxObjects,
                Regions          = solveSettings.Regions,
                BlindFailoverEnabled = hint != null && solveSettings.BlindFailoverEnabled
            };

            // Genau wie NINA intern: statische Factory + new ImageSolver (wie FramingAssistantVM)
            var plateSolver = PlateSolverFactory.GetPlateSolver(solveSettings);
            var blindSolver = PlateSolverFactory.GetBlindSolver(solveSettings);
            var imageSolver = new ImageSolver(plateSolver, blindSolver);

            Logger.Info($"[FitsAnalysisController] Starting plate solve (FL={focalLength}mm, PS={pixelSize}µm, Bin={binning})");
            var progress = new Progress<ApplicationStatus>();
            var result = await imageSolver.Solve(imageData, parameter, progress, CancellationToken.None);

            if (result.Success)
            {
                Logger.Info($"[FitsAnalysisController] Solve successful: RA={result.Coordinates.RADegrees:F4} Dec={result.Coordinates.Dec:F4} PA={result.PositionAngle:F2}");
                var solvedCoords = result.Coordinates;
                await SendJson(new FitsSolveResult
                {
                    Success      = true,
                    Ra           = solvedCoords.RADegrees,
                    Dec          = solvedCoords.Dec,
                    RaString     = solvedCoords.RAString,
                    DecString    = solvedCoords.DecString,
                    Rotation     = result.PositionAngle,
                    PixelScale   = result.Pixscale,
                    SolvedFromWcs = false
                });
            }
            else
            {
                Logger.Warning($"[FitsAnalysisController] Plate solve failed for: {fullPath}");
                await SendJson(new FitsSolveResult
                {
                    Success = false,
                    Error = "Plate solving failed. Check NINA plate solver settings (ASTAP/Astrometry.net path and configuration)."
                }, 422);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[FitsAnalysisController.Analyze] {ex.Message}", ex);
            await SendJson(new FitsSolveResult { Success = false, Error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // Hilfsmethoden
    // -------------------------------------------------------------------------

    private static Coordinates ExtractCoordinatesFromGenericHeaders(List<IGenericMetaDataHeader> headers)
    {
        if (headers == null || headers.Count == 0) return null;
        var dict = headers.ToDictionary(h => h.Key, h => h, StringComparer.OrdinalIgnoreCase);

        // RA + DEC (Dezimalgrad)
        if (dict.TryGetValue("RA", out var raH) && dict.TryGetValue("DEC", out var decH) &&
            TryGetDouble(raH, out double ra) && TryGetDouble(decH, out double dec))
        {
            Logger.Debug($"[FitsAnalysisController] Coordinate hint from RA/DEC: {ra:F4}/{dec:F4}");
            return new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000);
        }

        // OBJCTRA + OBJCTDEC (HMS/DMS String)
        if (dict.TryGetValue("OBJCTRA", out var objRaH) && dict.TryGetValue("OBJCTDEC", out var objDecH))
        {
            string raStr  = GetStringValue(objRaH)?.Trim();
            string decStr = GetStringValue(objDecH)?.Trim();
            if (!string.IsNullOrWhiteSpace(raStr) && !string.IsNullOrWhiteSpace(decStr))
            {
                try
                {
                    double raDeg  = CoreUtility.HmsToDegrees(raStr.Replace(' ', ':'));
                    double decDeg = CoreUtility.DmsToDegrees(decStr.Replace(' ', ':'));
                    Logger.Debug($"[FitsAnalysisController] Coordinate hint from OBJCTRA/OBJCTDEC: {raDeg:F4}/{decDeg:F4}");
                    return new Coordinates(Angle.ByDegree(raDeg), Angle.ByDegree(decDeg), Epoch.J2000);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[FitsAnalysisController] Could not parse OBJCTRA/OBJCTDEC: {ex.Message}");
                }
            }
        }

        return null;
    }

    private static bool TryGetDouble(IGenericMetaDataHeader header, out double value)
    {
        if (header is IGenericMetaDataHeader<double> dh) { value = dh.Value; return true; }
        if (header is IGenericMetaDataHeader<string> sh &&
            double.TryParse(sh.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
        value = 0;
        return false;
    }

    private static string GetStringValue(IGenericMetaDataHeader header)
    {
        if (header is IGenericMetaDataHeader<string> sh) return sh.Value;
        if (header is IGenericMetaDataHeader<double> dh) return dh.Value.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static double ParseBodyDouble(Dictionary<string, object> body, string key, double defaultVal = 0)
    {
        if (body.TryGetValue(key, out var val) && val != null &&
            double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            return result;
        return defaultVal;
    }

    private static double? ParseBodyNullableDouble(Dictionary<string, object> body, string key)
    {
        if (body.TryGetValue(key, out var val) && val != null &&
            double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            return result;
        return null;
    }

    private static int ParseBodyInt(Dictionary<string, object> body, string key, int defaultVal = 0)
    {
        if (body.TryGetValue(key, out var val) && val != null &&
            int.TryParse(val.ToString(), out int result))
            return result;
        return defaultVal;
    }

    private static bool ParseBodyBool(Dictionary<string, object> body, string key)
    {
        if (body.TryGetValue(key, out var val) && val != null)
        {
            if (val is bool b) return b;
            if (bool.TryParse(val.ToString(), out bool result)) return result;
        }
        return false;
    }
}
