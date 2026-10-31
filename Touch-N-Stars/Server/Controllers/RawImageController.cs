using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Utility;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Real sensor data for the app's histogram and client-side stretch.
/// Routes:
///   GET /api/raw-image/prepared?maxDim=...                  — the image NINA prepared last
///   GET /api/raw-image/history?filename=...&amp;maxDim=...      — a saved frame of the image history
///       (or index=...&amp;mean=...&amp;median=... when the backend's history has no file name)
///   GET /api/raw-image/livestack?target=...&amp;filter=...&amp;maxDim=... — a Livestack stack
/// The body is the binary container described in RawImageEncoder.WriteContainer.
/// Errors are JSON: { success: false, code, error }.
/// </summary>
public class RawImageController : WebApiController
{
    private const int DefaultMaxDim = 1600;
    private const int MaxMaxDim = 4096;
    private static readonly RawImageService Service = new();

    [Route(HttpVerbs.Get, "/raw-image/prepared")]
    public Task GetPrepared() =>
        Send(gzip => Service.GetPreparedAsync(ParseMaxDim(), gzip, HttpContext.CancellationToken));

    [Route(HttpVerbs.Get, "/raw-image/history")]
    public Task GetHistory() =>
        Send(gzip => Service.GetHistoryAsync(
            HttpContext.Request.QueryString["filename"],
            ParseLong("index"), ParseDouble("mean"), ParseDouble("median"),
            ParseMaxDim(), gzip, HttpContext.CancellationToken));

    [Route(HttpVerbs.Get, "/raw-image/livestack")]
    public Task GetLivestack() =>
        Send(gzip => Service.GetLivestackAsync(
            HttpContext.Request.QueryString["target"],
            HttpContext.Request.QueryString["filter"],
            ParseMaxDim(), gzip, HttpContext.CancellationToken));

    private async Task Send(Func<bool, Task<byte[]>> produce)
    {
        try
        {
            string acceptEncoding = HttpContext.Request.Headers["Accept-Encoding"] ?? string.Empty;
            bool gzip = acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

            byte[] payload = await produce(gzip);

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "application/octet-stream";
            if (gzip) HttpContext.Response.Headers["Content-Encoding"] = "gzip";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            HttpContext.Response.ContentLength64 = payload.Length;
            await HttpContext.Response.OutputStream.WriteAsync(payload, 0, payload.Length, HttpContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The app switched images or closed the panel - nothing to send back.
        }
        catch (RawImageException ex)
        {
            await SendError(ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"[RawImageController] {ex.Message}", ex);
            await SendError(500, "internal-error", ex.Message);
        }
    }

    private int ParseMaxDim()
    {
        string raw = HttpContext.Request.QueryString["maxDim"];
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
        {
            return DefaultMaxDim;
        }
        return Math.Min(value, MaxMaxDim);
    }

    private long? ParseLong(string name) =>
        long.TryParse(HttpContext.Request.QueryString[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    private double? ParseDouble(string name) =>
        double.TryParse(HttpContext.Request.QueryString[name], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private Task SendError(int statusCode, string code, string message)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(new { success = false, code, error = message });
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }
}
