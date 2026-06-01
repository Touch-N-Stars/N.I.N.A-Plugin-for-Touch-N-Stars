using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using HttpMultipartParser;
using NINA.Core.Utility;
using Newtonsoft.Json;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

public class StellariumLandscapeController : WebApiController
{
    private readonly StellariumLandscapeService landscapeService = new();

    [Route(HttpVerbs.Post, "/stellarium/landscape/create")]
    public async Task Create()
    {
        try
        {
            if (!IsMultipartFormData())
            {
                await SendJson(new { success = false, error = "Content-Type must be multipart/form-data." }, 400);
                return;
            }

            if (TryGetContentLength(out long contentLength) && contentLength > StellariumLandscapeService.MaxUploadBytes)
            {
                await SendJson(new
                {
                    success = false,
                    error = $"Request body exceeds maximum upload size ({StellariumLandscapeService.MaxUploadBytes / (1024 * 1024)} MB)."
                }, 400);
                return;
            }

            MultipartFormDataParser parser;
            try
            {
                parser = await MultipartFormDataParser.ParseAsync(HttpContext.Request.InputStream).ConfigureAwait(false);
            }
            catch (MultipartParseException ex)
            {
                Logger.Warning($"[StellariumLandscapeController] Multipart parse failed: {ex.Message}");
                await SendJson(new { success = false, error = "Malformed multipart/form-data request." }, 400);
                return;
            }

            var imageFile = parser.Files.FirstOrDefault(f => string.Equals(f.Name, "image", StringComparison.OrdinalIgnoreCase));
            if (imageFile == null)
            {
                await SendJson(new { success = false, error = "Missing required field 'image'." }, 400);
                return;
            }

            byte[] imageBytes;
            try
            {
                imageBytes = await ReadStreamWithLimitAsync(imageFile.Data, StellariumLandscapeService.MaxUploadBytes).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await SendJson(new { success = false, error = ex.Message }, 400);
                return;
            }

            string name = GetFormValue(parser, "name");
            string folderName = GetFormValue(parser, "folderName");
            string northOffsetRaw = GetFormValue(parser, "northOffsetDeg");

            if (string.IsNullOrWhiteSpace(name))
            {
                await SendJson(new { success = false, error = "Missing required field 'name'." }, 400);
                return;
            }

            if (string.IsNullOrWhiteSpace(folderName))
            {
                await SendJson(new { success = false, error = "Missing required field 'folderName'." }, 400);
                return;
            }

            if (!TryParseDouble(northOffsetRaw, out double northOffsetDeg))
            {
                await SendJson(new { success = false, error = "Field 'northOffsetDeg' must be a valid number." }, 400);
                return;
            }

            if (!TryParseOptionalDouble(GetFormValue(parser, "latitude"), out double? latitude))
            {
                await SendJson(new { success = false, error = "Field 'latitude' must be a valid number." }, 400);
                return;
            }

            if (!TryParseOptionalDouble(GetFormValue(parser, "longitude"), out double? longitude))
            {
                await SendJson(new { success = false, error = "Field 'longitude' must be a valid number." }, 400);
                return;
            }

            if (!TryParseOptionalDouble(GetFormValue(parser, "altitude"), out double? altitude))
            {
                await SendJson(new { success = false, error = "Field 'altitude' must be a valid number." }, 400);
                return;
            }

            var request = new LandscapeCreateRequest
            {
                ImageBytes = imageBytes,
                Name = name,
                FolderName = folderName,
                NorthOffsetDeg = northOffsetDeg,
                Description = GetFormValue(parser, "description"),
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                Author = GetFormValue(parser, "author"),
                OriginalFileName = imageFile.FileName,
                ContentType = imageFile.ContentType
            };

            StellariumLandscapeService.LandscapeBuildResult result = await landscapeService
                .CreateLandscapeZipAsync(request)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                await SendJson(new { success = false, error = result.ErrorMessage }, 400);
                return;
            }

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "application/zip";
            HttpContext.Response.ContentLength64 = result.ZipBytes.Length;
            HttpContext.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{result.DownloadFileName}\"";

            await HttpContext.Response.OutputStream
                .WriteAsync(result.ZipBytes, 0, result.ZipBytes.Length)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[StellariumLandscapeController.Create] {ex.Message}", ex);
            await SendJson(new { success = false, error = "Failed to create Stellarium landscape package." }, 500);
        }
    }

    private Task SendJson(object data, int statusCode)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data);
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    private bool IsMultipartFormData()
    {
        string contentType = HttpContext.Request.ContentType;
        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetContentLength(out long contentLength)
    {
        contentLength = 0;
        string header = HttpContext.Request.Headers["Content-Length"];
        return long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
    }

    private static string GetFormValue(MultipartFormDataParser parser, string fieldName)
    {
        if (parser?.Parameters == null)
        {
            return null;
        }

        return parser.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            ?.Data;
    }

    private static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseOptionalDouble(string value, out double? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(Stream stream, long maxBytes)
    {
        if (stream == null)
        {
            return Array.Empty<byte>();
        }

        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException($"Image exceeds maximum upload size ({maxBytes / (1024 * 1024)} MB).");
            }

            await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}
