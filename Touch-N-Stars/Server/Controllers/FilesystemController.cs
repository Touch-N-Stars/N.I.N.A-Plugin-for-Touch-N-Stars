using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for browsing and managing the local filesystem.
/// Routes:
///   GET    /api/filesystem/browse?path=...         — list directories and files
///   POST   /api/filesystem/directory               — create a directory (body: { "path": "..." })
///   DELETE /api/filesystem/directory?path=...      — delete a directory (recursive)
///   GET    /api/filesystem/file?path=...&amp;download=1 — stream a file (binary)
///   PUT    /api/filesystem/rename                  — rename/move (body: { "sourcePath", "targetPath" })
///   DELETE /api/filesystem/file?path=...           — delete a file
/// </summary>
public class FilesystemController : WebApiController
{
    private const int StreamBufferSize = 81920;

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

    private Task SendJson(object data, int statusCode = 200)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data);
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
