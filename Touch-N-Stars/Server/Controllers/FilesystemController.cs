using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for browsing and managing the local filesystem.
/// Routes:
///   GET    /api/filesystem/browse?path=...         — list directories and files
///   POST   /api/filesystem/directory               — create a directory (body: { "path": "..." })
///   DELETE /api/filesystem/directory?path=...      — delete a directory (recursive)
///   GET    /api/filesystem/file?path=...&amp;download=1 — stream a file (binary)
///   GET    /api/filesystem/imageinfo?path=...              — image/FITS metadata for preview
///   GET    /api/filesystem/preview?path=...&amp;maxWidth=...   — server-rendered JPEG/PNG preview
///   PUT    /api/filesystem/rename                  — rename/move (body: { "sourcePath", "targetPath" })
///   DELETE /api/filesystem/file?path=...           — delete a file
/// </summary>
public class FilesystemController : WebApiController
{
    private const int StreamBufferSize = 81920;

    private readonly ImagePreviewService previewService = new();

    // Deliberately not BaseImageData.FileIsSupported's regex — it is missing .fz/.rw2/.dng.
    // Mirrors the extension switch in NINA.Image.ImageData.BaseImageData.FromFile instead.
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".tif", ".tiff", ".jpg", ".jpeg", ".png",
        ".xisf", ".fit", ".fits", ".fts", ".fz",
        ".cr2", ".cr3", ".nef", ".raf", ".raw", ".pef", ".dng", ".arw", ".orf", ".rw2"
    };

    private static readonly HashSet<string> RawImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".raf", ".raw", ".pef", ".dng", ".arw", ".orf", ".rw2"
    };

    private static bool IsSupportedImageExtension(string path) =>
        SupportedImageExtensions.Contains(Path.GetExtension(path) ?? string.Empty);

    // FITS bit depth is self-describing in the header, so 16 is just the historical default used
    // for the load-call argument there. Raw files have no such header, so use the camera profile's
    // configured bit depth instead of hardcoding 16.
    private static int ResolveBitDepth(string path)
    {
        string ext = Path.GetExtension(path) ?? string.Empty;
        if (RawImageExtensions.Contains(ext))
        {
            double profileBitDepth = TouchNStars.Mediators.Profile.ActiveProfile.CameraSettings.BitDepth;
            return profileBitDepth > 0 ? (int)profileBitDepth : 16;
        }
        return 16;
    }

    private int ParseIntParam(string name, int defaultVal, int? max = null)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
        {
            value = defaultVal;
        }
        if (max.HasValue) value = Math.Min(value, max.Value);
        return value;
    }

    private double ParseDoubleParam(string name, double defaultVal)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
        {
            return defaultVal;
        }
        return value;
    }

    private bool ParseBoolParam(string name, bool defaultVal)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw)) return defaultVal;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return bool.TryParse(raw, out bool value) ? value : defaultVal;
    }

    private static string HeaderValueToString(IGenericMetaDataHeader header) => header switch
    {
        IGenericMetaDataHeader<string> s => s.Value,
        IGenericMetaDataHeader<double> d => d.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<int> i => i.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<uint> ui => ui.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<bool> b => b.Value ? "T" : "F",
        IGenericMetaDataHeader<DateTime> dt => dt.Value.ToString("o"),
        _ => header?.ToString()
    };

    private static ImageInfo BuildImageInfo(NINA.Image.Interfaces.IImageData imageData)
    {
        var props = imageData.Properties;
        var meta = imageData.MetaData;
        return new ImageInfo
        {
            Success = true,
            IsSupported = true,
            Width = props.Width,
            Height = props.Height,
            BitDepth = props.BitDepth,
            IsBayered = props.IsBayered,
            // The pattern that would actually be used to render, not the profile's override -
            // meta.Camera.BayerPattern is never populated on a file load and so always read "Auto".
            BayerPattern = props.IsBayered ? ImagePreviewService.ResolveBayerPattern(imageData).ToString() : string.Empty,
            FocalLength = double.IsNaN(meta.Telescope.FocalLength) ? null : meta.Telescope.FocalLength,
            PixelSize = double.IsNaN(meta.Camera.PixelSize) ? null : meta.Camera.PixelSize,
            CameraName = meta.Camera.Name,
            TelescopeName = meta.Telescope.Name,
            ExposureStart = meta.Image.ExposureStart == DateTime.MinValue ? null : meta.Image.ExposureStart.ToString("o"),
            ExposureTime = double.IsNaN(meta.Image.ExposureTime) ? null : meta.Image.ExposureTime,
            FilterName = meta.FilterWheel.Filter,
            Headers = meta.GenericHeaders.Select(h => new ImageHeaderEntry
            {
                Key = h.Key,
                Value = HeaderValueToString(h),
                Comment = h.Comment
            }).ToList()
        };
    }

    private static readonly Dictionary<string, string> ContentTypesByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".fit"] = "application/fits",
            [".fits"] = "application/fits",
            [".fts"] = "application/fits",
            [".txt"] = "text/plain",
            [".log"] = "text/plain",
            [".csv"] = "text/csv",
            [".json"] = "application/json",
            [".xml"] = "application/xml"
        };

    // The anonymous responses in this controller already use camelCase field names; the
    // ImageInfo POCO does not, and the client reads camelCase throughout. Without this
    // resolver /filesystem/imageinfo answers { "Success": ... } and every client check
    // against info.success silently fails.
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private Task SendJson(object data, int statusCode = 200)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data, JsonSettings);
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    private static string GetContentType(string path)
    {
        string extension = Path.GetExtension(path);
        return ContentTypesByExtension.TryGetValue(extension ?? string.Empty, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/browse
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/browse")]
    public async Task Browse()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            string path = string.IsNullOrWhiteSpace(pathParam)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Uri.UnescapeDataString(pathParam);

            string fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Path does not exist" }, 404);
                return;
            }

            var directories = new List<object>();
            var files = new List<object>();

            try
            {
                foreach (var dir in Directory.GetDirectories(fullPath).OrderBy(d => d))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        directories.Add(new
                        {
                            name = info.Name,
                            path = info.FullName,
                            lastModified = info.LastWriteTimeUtc.ToString("o")
                        });
                    }
                    catch { /* skip inaccessible entries */ }
                }

                foreach (var file in Directory.GetFiles(fullPath).OrderBy(f => f))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        files.Add(new
                        {
                            name = info.Name,
                            path = info.FullName,
                            size = info.Length,
                            lastModified = info.LastWriteTimeUtc.ToString("o")
                        });
                    }
                    catch { /* skip inaccessible entries */ }
                }
            }
            catch (UnauthorizedAccessException)
            {
                await SendJson(new { success = false, error = "Access denied" }, 403);
                return;
            }

            string parentPath = Directory.GetParent(fullPath)?.FullName;

            await SendJson(new
            {
                success = true,
                currentPath = fullPath,
                parentPath,
                directories,
                files
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.Browse] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // POST /api/filesystem/directory  body: { "path": "C:\\some\\new\\dir" }
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Post, "/filesystem/directory")]
    public async Task CreateDirectory()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null || !body.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                await SendJson(new { success = false, error = "Missing 'path' in request body" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(path);

            if (Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Directory already exists" }, 409);
                return;
            }

            Directory.CreateDirectory(fullPath);
            Logger.Info($"[FilesystemController] Created directory: {fullPath}");

            await SendJson(new { success = true, path = fullPath }, 201);
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.CreateDirectory] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // DELETE /api/filesystem/directory?path=...
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Delete, "/filesystem/directory")]
    public async Task DeleteDirectory()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Directory does not exist" }, 404);
                return;
            }

            Directory.Delete(fullPath, recursive: true);
            Logger.Info($"[FilesystemController] Deleted directory: {fullPath}");

            await SendJson(new { success = true, path = fullPath });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.DeleteDirectory] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/file?path=...[&download=1]  — stream raw file content
    //
    // The response is a byte-for-byte copy of the file. Reading it as UTF-8 text
    // would replace every byte >= 0x80 with U+FFFD, which corrupts images and the
    // pixel data of FITS files.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/file")]
    public async Task ReadFile()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            var info = new FileInfo(fullPath);
            string downloadParam = HttpContext.Request.QueryString["download"];
            bool asAttachment = downloadParam == "1" ||
                string.Equals(downloadParam, "true", StringComparison.OrdinalIgnoreCase);

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = GetContentType(fullPath);
            // Content-Length lets the client show real download progress.
            HttpContext.Response.ContentLength64 = info.Length;
            HttpContext.Response.Headers["Content-Disposition"] =
                $"{(asAttachment ? "attachment" : "inline")}; filename=\"{info.Name}\"";

            using var input = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, useAsync: true);
            await input.CopyToAsync(HttpContext.Response.OutputStream, StreamBufferSize).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.ReadFile] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/imageinfo?path=...
    //
    // Reports dimensions/bit depth/bayer pattern/header table for a file, and whether it can be
    // rendered by /filesystem/preview at all. A load failure (e.g. missing libraw on PINS) is
    // reported as isSupported:false, not a 500 — that is what lets the frontend fall back to
    // "download to device" instead of showing a broken preview modal.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/imageinfo")]
    public async Task GetImageInfo()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new ImageInfo { Success = false, Error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));
            if (!File.Exists(fullPath))
            {
                await SendJson(new ImageInfo { Success = false, Error = "File does not exist" }, 404);
                return;
            }

            if (!IsSupportedImageExtension(fullPath))
            {
                await SendJson(new ImageInfo { Success = true, IsSupported = false });
                return;
            }

            int bitDepth = ResolveBitDepth(fullPath);
            // RawConverterEnum.FREEIMAGE is a no-op (see ImagePreviewService) but is the only
            // CreateFromFile overload the referenced NINA package version exposes; the load
            // itself does not observe HttpContext.CancellationToken because of that.
            var imageData = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(fullPath, bitDepth, false, RawConverterEnum.FREEIMAGE);

            if (imageData == null)
            {
                await SendJson(new ImageInfo { Success = true, IsSupported = false, Error = "Failed to load image" });
                return;
            }

            // Same correction the preview path applies, so both endpoints agree on IsBayered -
            // this is what decides whether the client offers the debayer option at all.
            imageData = ImagePreviewService.ApplyBayerDetection(imageData);

            await SendJson(BuildImageInfo(imageData));
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new ImageInfo { Success = false, Error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            // Covers e.g. a raw file when libraw.so isn't loadable on PINS — reported as
            // unsupported so the row falls back to download rather than a hard error.
            Logger.Warning($"[FilesystemController.GetImageInfo] {ex.Message}");
            await SendJson(new ImageInfo { Success = true, IsSupported = false, Error = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/preview?path=...&maxWidth=...&quality=...&stretch=...
    //     &blackClipping=...&unlinked=...&debayer=...
    //
    // Renders the file through ImageDataFactory -> RenderImage -> Stretch -> scaled JPEG (or PNG
    // when quality<0), streamed the same way ReadFile() streams raw bytes.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/preview")]
    public async Task GetPreview()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));
            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            if (!IsSupportedImageExtension(fullPath))
            {
                await SendJson(new { success = false, error = "Unsupported file type" }, 400);
                return;
            }

            var profile = TouchNStars.Mediators.Profile.ActiveProfile;
            int maxWidth = ParseIntParam("maxWidth", defaultVal: 2048, max: 2048);
            int quality = ParseIntParam("quality", defaultVal: 85);
            double stretch = ParseDoubleParam("stretch", profile.ImageSettings.AutoStretchFactor);
            double blackClipping = ParseDoubleParam("blackClipping", profile.ImageSettings.BlackClipping);
            bool unlinked = ParseBoolParam("unlinked", profile.ImageSettings.UnlinkedStretch);
            bool debayer = ParseBoolParam("debayer", true);
            int bitDepth = ResolveBitDepth(fullPath);

            var (bytes, contentType) = await previewService.RenderPreviewAsync(
                fullPath, maxWidth, quality, stretch, blackClipping, unlinked, debayer, bitDepth,
                HttpContext.CancellationToken);

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = contentType;
            HttpContext.Response.ContentLength64 = bytes.Length;
            await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, HttpContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client closed the modal or moved the slider again - nothing to send back.
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.GetPreview] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // PUT /api/filesystem/rename  body: { "sourcePath": "...", "targetPath": "..." }
    //
    // Handles both files and directories. The target's parent directory has to
    // exist, so this doubles as a move within the existing folder tree.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Put, "/filesystem/rename")]
    public async Task Rename()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();

            if (body == null
                || !body.TryGetValue("sourcePath", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath)
                || !body.TryGetValue("targetPath", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            {
                await SendJson(new { success = false, error = "Missing 'sourcePath' or 'targetPath' in request body" }, 400);
                return;
            }

            string fullSource = Path.GetFullPath(sourcePath);
            string fullTarget = Path.GetFullPath(targetPath);

            if (string.Equals(fullSource, fullTarget, StringComparison.Ordinal))
            {
                await SendJson(new { success = true, path = fullTarget });
                return;
            }

            bool sourceIsDirectory = Directory.Exists(fullSource);
            if (!sourceIsDirectory && !File.Exists(fullSource))
            {
                await SendJson(new { success = false, error = "Source does not exist" }, 404);
                return;
            }

            if (File.Exists(fullTarget) || Directory.Exists(fullTarget))
            {
                await SendJson(new { success = false, error = "Target already exists" }, 409);
                return;
            }

            string targetParent = Path.GetDirectoryName(fullTarget);
            if (string.IsNullOrEmpty(targetParent) || !Directory.Exists(targetParent))
            {
                await SendJson(new { success = false, error = "Target directory does not exist" }, 400);
                return;
            }

            if (sourceIsDirectory)
            {
                Directory.Move(fullSource, fullTarget);
            }
            else
            {
                File.Move(fullSource, fullTarget);
            }

            Logger.Info($"[FilesystemController] Renamed: {fullSource} -> {fullTarget}");

            await SendJson(new { success = true, path = fullTarget });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.Rename] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // DELETE /api/filesystem/file?path=...
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Delete, "/filesystem/file")]
    public async Task DeleteFile()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            File.Delete(fullPath);
            Logger.Info($"[FilesystemController] Deleted file: {fullPath}");

            await SendJson(new { success = true, path = fullPath });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.DeleteFile] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }
}
