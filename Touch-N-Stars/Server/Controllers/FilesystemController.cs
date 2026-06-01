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
///   GET    /api/filesystem/file?path=...           — stream a file (images, FITS, etc.)
///   POST   /api/filesystem/directory               — create a directory (body: { "path": "..." })
///   PUT    /api/filesystem/rename                  — rename/move file or directory
///   DELETE /api/filesystem/directory?path=...      — delete a directory (recursive)
///   DELETE /api/filesystem/file?path=...           — delete a file
/// </summary>
public class FilesystemController : WebApiController
{
    private Task SendJson(object data, int statusCode = 200)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data);
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    private static string GetContentType(string path)
    {
        string extension = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
        return extension switch
        {
            ".fit" => "application/fits",
            ".fits" => "application/fits",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
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
    // GET /api/filesystem/file?path=...
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/file")]
    public async Task GetFile()
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

            var fileInfo = new FileInfo(fullPath);
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = GetContentType(fullPath);
            HttpContext.Response.Headers["Content-Length"] = fileInfo.Length.ToString();

            string fileName = fileInfo.Name;
            HttpContext.Response.Headers["Content-Disposition"] = $"inline; filename=\"{fileName}\"";

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await stream.CopyToAsync(Response.OutputStream);
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.GetFile] {ex.Message}", ex);
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
    // PUT /api/filesystem/rename  body: { "sourcePath": "...", "targetPath": "..." }
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Put, "/filesystem/rename")]
    public async Task Rename()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null
                || !body.TryGetValue("sourcePath", out var sourcePath)
                || !body.TryGetValue("targetPath", out var targetPath)
                || string.IsNullOrWhiteSpace(sourcePath)
                || string.IsNullOrWhiteSpace(targetPath))
            {
                await SendJson(new { success = false, error = "Missing 'sourcePath' or 'targetPath' in request body" }, 400);
                return;
            }

            string sourceFullPath = Path.GetFullPath(sourcePath);
            string targetFullPath = Path.GetFullPath(targetPath);

            bool sourceIsFile = File.Exists(sourceFullPath);
            bool sourceIsDirectory = Directory.Exists(sourceFullPath);

            if (!sourceIsFile && !sourceIsDirectory)
            {
                await SendJson(new { success = false, error = "Source path does not exist" }, 404);
                return;
            }

            if (File.Exists(targetFullPath) || Directory.Exists(targetFullPath))
            {
                await SendJson(new { success = false, error = "Target path already exists" }, 409);
                return;
            }

            string targetParent = sourceIsFile
                ? Path.GetDirectoryName(targetFullPath)
                : Directory.GetParent(targetFullPath)?.FullName;

            if (!string.IsNullOrWhiteSpace(targetParent) && !Directory.Exists(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            if (sourceIsFile)
            {
                File.Move(sourceFullPath, targetFullPath);
                Logger.Info($"[FilesystemController] Renamed/moved file: {sourceFullPath} -> {targetFullPath}");
            }
            else
            {
                Directory.Move(sourceFullPath, targetFullPath);
                Logger.Info($"[FilesystemController] Renamed/moved directory: {sourceFullPath} -> {targetFullPath}");
            }

            await SendJson(new
            {
                success = true,
                sourcePath = sourceFullPath,
                targetPath = targetFullPath,
                itemType = sourceIsFile ? "file" : "directory"
            });
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
    // GET /api/filesystem/file?path=...  — read file content as text
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

            string content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            HttpContext.Response.StatusCode = 200;
            await HttpContext.SendStringAsync(content, "text/plain", Encoding.UTF8);
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
