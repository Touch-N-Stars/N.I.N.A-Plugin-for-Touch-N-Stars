using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Services;

public class StellariumLandscapeService
{
    public const int HipsOrder = 0;
    public const int HipsOrderMin = 0;
    public const int HipsTileWidth = 512;
    public const long MaxUploadBytes = 80 * 1024 * 1024;
    public const int MinimumImageWidth = 1024;
    public const int MinimumImageHeight = 512;

    private const double NorthOffsetDirectionSign = -1.0;
    private const double ExpectedAspectRatio = 2.0;
    private const double AllowedAspectRatioDelta = 0.08;
    private static readonly bool SwapFaceAxes = true;
    private static readonly bool FlipFaceVAxis = false;
    private static readonly bool FlipFaceUAxis = false;
    private static readonly bool FlipPanoramaHorizontally = true;
    private static readonly bool FlipPanoramaVertically = false;
    private const string FrontendAppFolderName = "app";
    private const string StellariumDataFolderName = "stellarium-data";
    private const string LandscapesFolderName = "landscapes";
    private const string NormalizedPluginFolderName = "touchnstars";

    private static readonly WebpEncoder TileEncoder = new()
    {
        FileFormat = WebpFileFormatType.Lossless,
        Method = WebpEncodingMethod.BestQuality,
        Quality = 100
    };

    private static readonly WebpEncoder AllskyEncoder = new()
    {
        FileFormat = WebpFileFormatType.Lossy,
        Method = WebpEncodingMethod.Level4,
        Quality = 85
    };

    public async Task<LandscapeBuildResult> CreateLandscapeZipAsync(LandscapeCreateRequest request, CancellationToken cancellationToken = default)
    {
        LandscapeValidationResult validation = ValidateRequest(request);
        if (!validation.IsValid)
        {
            return LandscapeBuildResult.FromFailure(validation.ErrorMessage);
        }

        string sanitizedFolderName = validation.SanitizedFolderName;
        string sourceMd5 = ComputeMd5Hex(request.ImageBytes);

        IImageFormat sourceFormat = Image.DetectFormat(request.ImageBytes);
        if (!IsSupportedSourceFormat(sourceFormat))
        {
            return LandscapeBuildResult.FromFailure("Unsupported image format. Use PNG, JPEG, or WebP.");
        }

        try
        {
            await using MemoryStream imageStream = new(request.ImageBytes, writable: false);
            using Image<Rgba32> sourceImage = await Image.LoadAsync<Rgba32>(imageStream, cancellationToken).ConfigureAwait(false);

            if (sourceImage.Width < MinimumImageWidth || sourceImage.Height < MinimumImageHeight)
            {
                return LandscapeBuildResult.FromFailure($"Image is too small. Minimum size is {MinimumImageWidth}x{MinimumImageHeight}.");
            }

            if (!IsValidEquirectangularRatio(sourceImage.Width, sourceImage.Height))
            {
                return LandscapeBuildResult.FromFailure("Input image must be equirectangular with width approximately 2x height.");
            }

            byte[][] tileBytes = new byte[12][];
            for (int basePixel = 0; basePixel < 12; basePixel++)
            {
                tileBytes[basePixel] = await RenderOrderZeroTileAsync(sourceImage, basePixel, request.NorthOffsetDeg, cancellationToken).ConfigureAwait(false);
            }

            byte[] allskyBytes = await BuildAllskyAsync(tileBytes, cancellationToken).ConfigureAwait(false);

            string properties = BuildPropertiesFile(
                request.Name.Trim(),
                sanitizedFolderName,
                DateTime.UtcNow,
                sourceMd5);

            string description = BuildDescriptionFile(request.Name, request.Description, request.Author);

            byte[] zipBytes = BuildLandscapeZip(sanitizedFolderName, properties, description, allskyBytes, tileBytes);

            // Auto-install into frontend static data if a valid app path can be resolved.
            string installPath = TryInstallLandscapeToFrontend(
                sanitizedFolderName,
                properties,
                description,
                allskyBytes,
                tileBytes);

            if (!string.IsNullOrWhiteSpace(installPath))
            {
                Logger.Info($"[StellariumLandscapeService] Landscape installed to '{installPath}'.");
            }

            string downloadName = $"{sanitizedFolderName}.zip";

            return LandscapeBuildResult.FromSuccess(zipBytes, downloadName, sanitizedFolderName);
        }
        catch (UnknownImageFormatException)
        {
            return LandscapeBuildResult.FromFailure("Unsupported image format. Use PNG, JPEG, or WebP.");
        }
        catch (Exception ex)
        {
            Logger.Error($"[StellariumLandscapeService] Landscape build failed: {ex}");
            return LandscapeBuildResult.FromFailure("Failed to create landscape package.");
        }
    }

    public IReadOnlyList<InstalledLandscapeInfo> ListInstalledLandscapes()
    {
        try
        {
            string landscapesRoot = ResolveFrontendLandscapesRoot(createIfMissing: false);
            if (string.IsNullOrWhiteSpace(landscapesRoot) || !Directory.Exists(landscapesRoot))
            {
                return Array.Empty<InstalledLandscapeInfo>();
            }

            List<InstalledLandscapeInfo> results = new();
            foreach (string folderPath in Directory.GetDirectories(landscapesRoot))
            {
                string folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                string propertiesPath = Path.Combine(folderPath, "properties");
                string allskyPath = Path.Combine(folderPath, "Norder0", "Allsky.webp");
                if (!File.Exists(propertiesPath) && !File.Exists(allskyPath))
                {
                    continue;
                }

                string title = TryReadObsTitle(propertiesPath) ?? folderName;

                results.Add(new InstalledLandscapeInfo
                {
                    FolderName = folderName,
                    Title = title,
                    ServiceUrl = $"/stellarium-data/landscapes/{folderName}",
                    HasAllsky = File.Exists(allskyPath)
                });
            }

            return results
                .OrderBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[StellariumLandscapeService] ListInstalledLandscapes failed: {ex.Message}");
            return Array.Empty<InstalledLandscapeInfo>();
        }
    }

    internal static LandscapeValidationResult ValidateRequest(LandscapeCreateRequest request)
    {
        if (request == null)
        {
            return new LandscapeValidationResult { ErrorMessage = "Request payload is missing." };
        }

        if (request.ImageBytes == null || request.ImageBytes.Length == 0)
        {
            return new LandscapeValidationResult { ErrorMessage = "Missing image payload." };
        }

        if (request.ImageBytes.LongLength > MaxUploadBytes)
        {
            return new LandscapeValidationResult
            {
                ErrorMessage = $"Image exceeds maximum upload size ({MaxUploadBytes / (1024 * 1024)} MB)."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new LandscapeValidationResult { ErrorMessage = "Field 'name' is required." };
        }

        string sanitizedFolderName = SanitizeFolderName(request.FolderName);
        if (string.IsNullOrWhiteSpace(sanitizedFolderName))
        {
            return new LandscapeValidationResult { ErrorMessage = "Field 'folderName' is invalid." };
        }

        if (request.NorthOffsetDeg < 0.0 || request.NorthOffsetDeg > 360.0)
        {
            return new LandscapeValidationResult { ErrorMessage = "Field 'northOffsetDeg' must be in range 0..360." };
        }

        return new LandscapeValidationResult
        {
            SanitizedFolderName = sanitizedFolderName
        };
    }

    internal static bool IsValidEquirectangularRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double ratio = width / (double)height;
        return Math.Abs(ratio - ExpectedAspectRatio) <= AllowedAspectRatioDelta;
    }

    internal static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        string candidate = folderName.Trim().ToLowerInvariant();
        StringBuilder sanitized = new(candidate.Length);
        bool lastWasSeparator = false;

        foreach (char ch in candidate)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sanitized.Append(ch);
                lastWasSeparator = false;
                continue;
            }

            if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
            {
                if (!lastWasSeparator && sanitized.Length > 0)
                {
                    sanitized.Append('_');
                    lastWasSeparator = true;
                }
            }
        }

        while (sanitized.Length > 0 && sanitized[^1] == '_')
        {
            sanitized.Length -= 1;
        }

        return sanitized.ToString();
    }

    internal static string BuildPropertiesFile(string name, string folderName, DateTime releaseDateUtc, string sourceMd5)
    {
        string releaseDate = releaseDateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return string.Join("\n", new[]
        {
            "hips_order = 0",
            "hips_order_min = 0",
            "hips_tile_width = 512",
            "hips_tile_format = webp",
            "dataproduct_type = image",
            $"obs_title = {name}",
            $"hips_service_url = /stellarium-data/landscapes/{folderName}",
            $"hips_release_date = {releaseDate}",
            $"source_md5 = {sourceMd5}",
            "type = landscape",
            string.Empty
        });
    }

    internal static byte[] BuildLandscapeZip(
        string folderName,
        string propertiesContent,
        string descriptionContent,
        byte[] allskyWebp,
        IReadOnlyList<byte[]> npixTiles)
    {
        if (npixTiles == null || npixTiles.Count != 12)
        {
            throw new ArgumentException("Exactly 12 Npix tiles are required.", nameof(npixTiles));
        }

        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddTextEntry(zip, $"{folderName}/properties", propertiesContent);
            AddTextEntry(zip, $"{folderName}/description.en.utf8", descriptionContent);
            AddBinaryEntry(zip, $"{folderName}/Norder0/Allsky.webp", allskyWebp);

            for (int i = 0; i < 12; i++)
            {
                AddBinaryEntry(zip, $"{folderName}/Norder0/Dir0/Npix{i}.webp", npixTiles[i]);
            }
        }

        return output.ToArray();
    }

    private static async Task<byte[]> RenderOrderZeroTileAsync(
        Image<Rgba32> sourceImage,
        int basePixel,
        double northOffsetDeg,
        CancellationToken cancellationToken)
    {
        using Image<Rgba32> tile = new(HipsTileWidth, HipsTileWidth);

        tile.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < HipsTileWidth; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);

                for (int x = 0; x < HipsTileWidth; x++)
                {
                    (double u, double v) = MapTilePixelToFaceUv(x, y, HipsTileWidth);
                    HealpixNestedProjection.SphericalCoordinates lonLat = HealpixNestedProjection.BasePixelUvToLonLat(basePixel, u, v);
                    double offsetLongitude = ApplyNorthOffset(lonLat.LongitudeRadians, northOffsetDeg);
                    row[x] = SampleBilinear(sourceImage, offsetLongitude, lonLat.LatitudeRadians);
                }
            }
        });

        await using MemoryStream tileStream = new();
        await tile.SaveAsync(tileStream, TileEncoder, cancellationToken).ConfigureAwait(false);
        return tileStream.ToArray();
    }

    private static async Task<byte[]> BuildAllskyAsync(IReadOnlyList<byte[]> npixTiles, CancellationToken cancellationToken)
    {
        const int previewTileSize = 256;
        const int columns = 3;
        const int rows = 4;
        using Image<Rgba32> allsky = new(previewTileSize * columns, previewTileSize * rows);

        for (int i = 0; i < 12; i++)
        {
            using Image<Rgba32> tile = Image.Load<Rgba32>(npixTiles[i]);
            using Image<Rgba32> resized = tile.Clone(x => x.Resize(previewTileSize, previewTileSize));

            // Stellarium expects order-0 allsky laid out as 3 columns x 4 rows.
            int column = i % columns;
            int row = i / columns;

            allsky.Mutate(ctx =>
                ctx.DrawImage(resized, new Point(column * previewTileSize, row * previewTileSize), 1.0f));
        }

        await using MemoryStream allskyStream = new();
        await allsky.SaveAsync(allskyStream, AllskyEncoder, cancellationToken).ConfigureAwait(false);
        return allskyStream.ToArray();
    }

    private static string BuildDescriptionFile(string name, string description, string author)
    {
        string text = string.IsNullOrWhiteSpace(description) ? name?.Trim() : description.Trim();

        if (!string.IsNullOrWhiteSpace(author))
        {
            text = string.IsNullOrWhiteSpace(text)
                ? $"Author: {author.Trim()}"
                : $"{text}\n\nAuthor: {author.Trim()}";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Custom landscape";
        }

        return text + "\n";
    }

    private static bool IsSupportedSourceFormat(IImageFormat sourceFormat)
    {
        if (sourceFormat == null)
        {
            return false;
        }

        string name = sourceFormat.Name?.Trim().ToUpperInvariant();
        return name is "PNG" or "JPEG" or "JPG" or "WEBP";
    }

    private static double ApplyNorthOffset(double longitudeRadians, double northOffsetDeg)
    {
        double offsetRadians = DegreesToRadians(northOffsetDeg) * NorthOffsetDirectionSign;
        return HealpixNestedProjection.NormalizeLongitudeRadians(longitudeRadians + offsetRadians);
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    // Centralize tile-axis orientation here so visual tuning only requires one edit.
    private static (double U, double V) MapTilePixelToFaceUv(int pixelX, int pixelY, int tileWidth)
    {
        double rawU = (pixelX + 0.5) / tileWidth;
        double rawV = (pixelY + 0.5) / tileWidth;

        double u = SwapFaceAxes ? rawV : rawU;
        double v = SwapFaceAxes ? rawU : rawV;

        if (FlipFaceUAxis)
        {
            u = 1.0 - u;
        }

        if (FlipFaceVAxis)
        {
            v = 1.0 - v;
        }

        return (u, v);
    }

    private static Rgba32 SampleBilinear(Image<Rgba32> source, double longitudeRadians, double latitudeRadians)
    {
        double lon = HealpixNestedProjection.NormalizeLongitudeRadians(longitudeRadians);

        double x = lon / (2.0 * Math.PI) * source.Width;
        if (FlipPanoramaHorizontally)
        {
            x = source.Width - x;
        }

        double y = FlipPanoramaVertically
            ? (Math.PI / 2.0 + latitudeRadians) / Math.PI * source.Height
            : (Math.PI / 2.0 - latitudeRadians) / Math.PI * source.Height;

        y = Math.Clamp(y, 0.0, source.Height - 1.000001);

        int x0 = PositiveModulo((int)Math.Floor(x), source.Width);
        int x1 = (x0 + 1) % source.Width;
        int y0 = (int)Math.Floor(y);
        int y1 = Math.Min(y0 + 1, source.Height - 1);

        double tx = x - Math.Floor(x);
        double ty = y - y0;

        Rgba32 c00 = source[x0, y0];
        Rgba32 c10 = source[x1, y0];
        Rgba32 c01 = source[x0, y1];
        Rgba32 c11 = source[x1, y1];

        return InterpolateBilinear(c00, c10, c01, c11, tx, ty);
    }

    private static Rgba32 InterpolateBilinear(Rgba32 c00, Rgba32 c10, Rgba32 c01, Rgba32 c11, double tx, double ty)
    {
        byte r = InterpolateChannel(c00.R, c10.R, c01.R, c11.R, tx, ty);
        byte g = InterpolateChannel(c00.G, c10.G, c01.G, c11.G, tx, ty);
        byte b = InterpolateChannel(c00.B, c10.B, c01.B, c11.B, tx, ty);
        byte a = InterpolateChannel(c00.A, c10.A, c01.A, c11.A, tx, ty);

        return new Rgba32(r, g, b, a);
    }

    private static byte InterpolateChannel(byte c00, byte c10, byte c01, byte c11, double tx, double ty)
    {
        double top = (c00 * (1.0 - tx)) + (c10 * tx);
        double bottom = (c01 * (1.0 - tx)) + (c11 * tx);
        double value = (top * (1.0 - ty)) + (bottom * ty);
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static int PositiveModulo(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static string ComputeMd5Hex(byte[] bytes)
    {
        using MD5 md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AddTextEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content ?? string.Empty);
    }

    private static void AddBinaryEntry(ZipArchive archive, string path, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static string TryInstallLandscapeToFrontend(
        string folderName,
        string propertiesContent,
        string descriptionContent,
        byte[] allskyWebp,
        IReadOnlyList<byte[]> npixTiles)
    {
        try
        {
            string landscapesRoot = ResolveFrontendLandscapesRoot(createIfMissing: true);
            if (string.IsNullOrWhiteSpace(landscapesRoot))
            {
                return null;
            }

            string targetRoot = Path.Combine(landscapesRoot, folderName);
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }

            Directory.CreateDirectory(Path.Combine(targetRoot, "Norder0", "Dir0"));

            File.WriteAllText(
                Path.Combine(targetRoot, "properties"),
                propertiesContent ?? string.Empty,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.WriteAllText(
                Path.Combine(targetRoot, "description.en.utf8"),
                descriptionContent ?? string.Empty,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.WriteAllBytes(Path.Combine(targetRoot, "Norder0", "Allsky.webp"), allskyWebp);

            for (int i = 0; i < npixTiles.Count; i++)
            {
                File.WriteAllBytes(
                    Path.Combine(targetRoot, "Norder0", "Dir0", $"Npix{i}.webp"),
                    npixTiles[i]);
            }

            return targetRoot;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[StellariumLandscapeService] Auto-install failed: {ex.Message}");
            return null;
        }
    }

    private static string ResolveFrontendLandscapesRoot(bool createIfMissing)
    {
        string configuredAppPath = Environment.GetEnvironmentVariable("TNS_FRONTEND_APP_PATH");
        if (!string.IsNullOrWhiteSpace(configuredAppPath))
        {
            return EnsureLandscapesDirectory(configuredAppPath, createIfMissing);
        }

        string fromBaseDirectory = TryResolveFromBaseDirectory(AppContext.BaseDirectory, createIfMissing);
        if (!string.IsNullOrWhiteSpace(fromBaseDirectory))
        {
            return fromBaseDirectory;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string ninaPluginsRoot = Path.Combine(localAppData, "NINA", "Plugins");
        if (Directory.Exists(ninaPluginsRoot))
        {
            foreach (string versionFolder in Directory.GetDirectories(ninaPluginsRoot))
            {
                foreach (string pluginFolder in Directory.GetDirectories(versionFolder))
                {
                    string pluginFolderName = Path.GetFileName(pluginFolder);
                    if (!IsTouchNStarsPluginFolder(pluginFolderName))
                    {
                        continue;
                    }

                    string appFolder = Path.Combine(pluginFolder, FrontendAppFolderName);
                    if (Directory.Exists(appFolder))
                    {
                        return EnsureLandscapesDirectory(appFolder, createIfMissing);
                    }
                }
            }
        }

        return null;
    }

    private static string TryResolveFromBaseDirectory(string baseDirectory, bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        DirectoryInfo current = new(baseDirectory);
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidateApp = Path.Combine(current.FullName, FrontendAppFolderName);
            if (Directory.Exists(candidateApp))
            {
                return EnsureLandscapesDirectory(candidateApp, createIfMissing);
            }

            current = current.Parent;
        }

        return null;
    }

    private static string EnsureLandscapesDirectory(string appFolder, bool createIfMissing)
    {
        string landscapesPath = Path.Combine(appFolder, StellariumDataFolderName, LandscapesFolderName);

        if (createIfMissing)
        {
            Directory.CreateDirectory(landscapesPath);
            return landscapesPath;
        }

        if (!Directory.Exists(landscapesPath))
        {
            return null;
        }

        return landscapesPath;
    }

    private static bool IsTouchNStarsPluginFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        string normalized = NormalizeFolderName(folderName);
        return string.Equals(normalized, NormalizedPluginFolderName, StringComparison.Ordinal);
    }

    private static string NormalizeFolderName(string value)
    {
        StringBuilder normalized = new(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                normalized.Append(char.ToLowerInvariant(ch));
            }
        }

        return normalized.ToString();
    }

    private static string TryReadObsTitle(string propertiesPath)
    {
        try
        {
            if (!File.Exists(propertiesPath))
            {
                return null;
            }

            foreach (string line in File.ReadLines(propertiesPath))
            {
                if (!line.StartsWith("obs_title", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0 || separatorIndex + 1 >= line.Length)
                {
                    continue;
                }

                string value = line[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public class InstalledLandscapeInfo
    {
        public string FolderName { get; set; }

        public string Title { get; set; }

        public string ServiceUrl { get; set; }

        public bool HasAllsky { get; set; }
    }

    public class LandscapeBuildResult
    {
        public bool Success { get; private set; }

        public string ErrorMessage { get; private set; }

        public byte[] ZipBytes { get; private set; }

        public string DownloadFileName { get; private set; }

        public string FolderName { get; private set; }

        public static LandscapeBuildResult FromSuccess(byte[] zipBytes, string downloadFileName, string folderName)
        {
            return new LandscapeBuildResult
            {
                Success = true,
                ZipBytes = zipBytes,
                DownloadFileName = downloadFileName,
                FolderName = folderName
            };
        }

        public static LandscapeBuildResult FromFailure(string errorMessage)
        {
            return new LandscapeBuildResult
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Landscape build failed."
            };
        }
    }
}
