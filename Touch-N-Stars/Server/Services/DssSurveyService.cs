using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TouchNStars.Server.Services;

/// <summary>
/// Downloads the DSS colour HiPS (CDS/P/DSS2/color) tile by tile into the persistent
/// Celestia Atlas data directory and keeps the served survey consistent with what is
/// actually on disk. Tiles are stored as the source JPEGs, unchanged: re-encoding to WebP
/// cost ~1.3 s per tile on a PINS host and made the download CPU-bound.
///
/// Only one download job runs at a time and its state lives in memory; everything else
/// (installed order, disk usage, resume position) is reconstructed from the files, so a
/// server restart mid-job simply leaves a resumable partial survey behind. An order is
/// advertised in <c>properties</c> only once every one of its tiles exists.
/// </summary>
public sealed class DssSurveyService
{
    public const string SurveyRoute = "/celestia-atlas-data/surveys/dss";
    public const int MinOrder = 3;
    public const int BaseOrder = 4;
    public const int MaxOrder = 7;
    public const int TileWidth = 512;
    public const double FreeSpaceMargin = 0.10;

    /// <summary>
    /// Tile sources, tried in this order per tile. STScI publishes an official mirror of the
    /// identical CDS HiPS (same creator_did and release) on S3; it answers in well under a
    /// second where the CDS community server took 20-100 s per tile when measured, so the
    /// mirror goes first and the master stays as fallback. TNS_DSS_SURVEY_SOURCE_URL
    /// (comma-separated) overrides the list.
    /// </summary>
    public static readonly string[] DefaultSourceUrls =
    {
        "https://stpubdata.s3.us-east-1.amazonaws.com/mast/skybackgrounds/DSSColor",
        "https://alasky.cds.unistra.fr/DSS/DSSColor"
    };

    private const string SurveysFolderName = "surveys";
    private const string DssFolderName = "dss";
    private const string PropertiesFileName = "properties";
    private const string TileExtension = ".jpg";
    private const string SourceTileExtension = TileExtension;
    private const string LegacyTileExtension = ".webp";
    private const int ParallelDownloads = 8;
    private const int TileAttempts = 3;
    private const int MaxConsecutiveFailures = 25;
    private const int AllskyColumns = 27;
    private const int AllskyTileWidth = 64;

    // Average source JPEG bytes per tile: means of 60 random tiles per order sampled from the
    // STScI mirror on 2026-09-14. The app carries the same table for its size estimate
    // (offlineSkySurvey.js).
    private static readonly IReadOnlyDictionary<int, long> AverageTileBytes = new Dictionary<int, long>
    {
        [3] = 42_000,
        [4] = 55_000,
        [5] = 75_000,
        [6] = 93_000,
        [7] = 97_000
    };

    private static readonly JpegEncoder AllskyEncoder = new() { Quality = 85 };

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Lazy<DssSurveyService> LazyInstance = new(() => new DssSurveyService());

    public static DssSurveyService Instance => LazyInstance.Value;

    private readonly object sync = new();
    private readonly string[] sourceUrls;
    private readonly string rootOverride;
    private SurveyInventory cachedInventory;
    private DownloadJob job;

    /// <param name="sourceUrlOverride">Tile sources for tests; null uses the environment/defaults.</param>
    /// <param name="rootOverride">Survey folder for tests; null uses the persistent data directory.</param>
    public DssSurveyService(string[] sourceUrlOverride = null, string rootOverride = null)
    {
        sourceUrls = sourceUrlOverride ?? ResolveSourceUrls();
        this.rootOverride = rootOverride;
    }

    // ------------------------------------------------------------------ HiPS layout helpers

    public static int TileCount(int order) => 12 << (2 * order);

    public static int TileCountUpTo(int order)
    {
        int total = 0;
        for (int o = MinOrder; o <= order; o++)
        {
            total += TileCount(o);
        }

        return total;
    }

    /// <summary>HiPS groups tiles into Dir folders of 10000 (Dir0, Dir10000, ...).</summary>
    public static string TileDirectoryName(int npix) => $"Dir{npix / 10000 * 10000}";

    public static string TileRelativePath(int order, int npix, string extension = TileExtension)
    {
        return Path.Combine($"Norder{order}", TileDirectoryName(npix), $"Npix{npix}{extension}");
    }

    public static string TileUrl(string baseUrl, int order, int npix)
    {
        return $"{baseUrl.TrimEnd('/')}/Norder{order}/{TileDirectoryName(npix)}/Npix{npix}{SourceTileExtension}";
    }

    public static long EstimateBytes(int order, int tileCount)
    {
        return AverageTileBytes.TryGetValue(order, out long perTile) ? perTile * tileCount : 0;
    }

    /// <summary>Highest order N for which orders MinOrder..N are all complete, or null.</summary>
    public static int? ResolveInstalledOrder(IReadOnlyList<OrderState> orders)
    {
        int? installed = null;
        foreach (OrderState state in orders.OrderBy(o => o.Order))
        {
            if (!state.Complete)
            {
                break;
            }

            installed = state.Order;
        }

        return installed;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>
    /// Persistent survey folder next to the user landscapes; TNS_DSS_SURVEY_PATH overrides it.
    /// Same logic on Windows/NINA and PINS.
    /// </summary>
    public static string ResolvePersistentSurveyRoot(bool createIfMissing)
    {
        string configured = Environment.GetEnvironmentVariable("TNS_DSS_SURVEY_PATH");
        string root = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                StellariumLandscapeService.ResolvePersistentCelestiaAtlasDataRoot(),
                SurveysFolderName,
                DssFolderName);

        if (createIfMissing)
        {
            Directory.CreateDirectory(root);
        }

        return root;
    }

    private string ResolveRoot(bool createIfMissing)
    {
        if (rootOverride == null)
        {
            return ResolvePersistentSurveyRoot(createIfMissing);
        }

        if (createIfMissing)
        {
            Directory.CreateDirectory(rootOverride);
        }

        return rootOverride;
    }

    private static string[] ResolveSourceUrls()
    {
        string configured = Environment.GetEnvironmentVariable("TNS_DSS_SURVEY_SOURCE_URL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultSourceUrls;
        }

        string[] urls = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return urls.Length > 0 ? urls : DefaultSourceUrls;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Touch-N-Stars-DSS-Survey/1.0");
        return client;
    }

    // ------------------------------------------------------------------ status

    public SurveyStatus GetStatus()
    {
        string root = ResolveRoot(createIfMissing: false);
        SurveyInventory inventory = GetInventory(root);
        DownloadJob current;
        lock (sync)
        {
            current = job;
        }

        return new SurveyStatus
        {
            Path = root,
            SourceUrls = sourceUrls,
            InstalledOrder = inventory.InstalledOrder,
            HasAllsky = inventory.HasAllsky,
            LegacyFormat = inventory.HasLegacyTiles,
            TotalBytes = inventory.TotalBytes,
            FreeBytes = GetFreeBytes(root),
            Orders = inventory.Orders,
            Job = current?.Snapshot()
        };
    }

    // ------------------------------------------------------------------ start / cancel / delete

    public OperationResult StartDownload(int targetOrder)
    {
        if (targetOrder < BaseOrder || targetOrder > MaxOrder)
        {
            return OperationResult.Fail(400, $"targetOrder must be between {BaseOrder} and {MaxOrder}.");
        }

        string root = ResolveRoot(createIfMissing: true);
        SurveyInventory inventory = GetInventory(root, forceRescan: true);

        long missingBytes = 0;
        int missingTiles = 0;
        foreach (OrderState state in inventory.Orders.Where(o => o.Order <= targetOrder))
        {
            int missing = state.TileCount - state.TilesPresent;
            missingTiles += missing;
            missingBytes += EstimateBytes(state.Order, missing);
        }

        long? free = GetFreeBytes(root);
        long required = (long)Math.Ceiling(missingBytes * (1 + FreeSpaceMargin));
        if (free.HasValue && missingTiles > 0 && free.Value < required)
        {
            return OperationResult.Fail(
                507,
                $"Not enough free disk space: about {required / 1_000_000} MB needed, {free.Value / 1_000_000} MB free.");
        }

        lock (sync)
        {
            if (job != null && job.IsRunning)
            {
                return OperationResult.Fail(409, "A survey download is already running.");
            }

            DownloadJob newJob = new(targetOrder, TileCountUpTo(targetOrder));
            newJob.TilesDone = inventory.Orders.Where(o => o.Order <= targetOrder).Sum(o => o.TilesPresent);
            job = newJob;
            newJob.Task = Task.Run(() => RunJobAsync(newJob, root));
        }

        Logger.Info($"[DssSurveyService] Download to order {targetOrder} started ({missingTiles} tiles missing).");
        return OperationResult.Ok();
    }

    /// <summary>Completes when the current job has finished (tests).</summary>
    internal Task WaitForJobAsync()
    {
        lock (sync)
        {
            return job?.Task ?? Task.CompletedTask;
        }
    }

    public OperationResult CancelDownload()
    {
        lock (sync)
        {
            if (job == null || !job.IsRunning)
            {
                return OperationResult.Fail(409, "No survey download is running.");
            }

            job.Cancellation.Cancel();
        }

        return OperationResult.Ok();
    }

    /// <param name="keepOrder">
    /// When null, the whole survey is removed. Otherwise only the orders above
    /// <paramref name="keepOrder"/> are removed (e.g. downgrade order 5 back to the base
    /// order 4); <paramref name="keepOrder"/> itself and everything below stay on disk.
    /// Must be an order strictly below the currently installed one.
    /// </param>
    public OperationResult DeleteSurvey(int? keepOrder = null)
    {
        lock (sync)
        {
            if (job != null && job.IsRunning)
            {
                return OperationResult.Fail(409, "Cancel the running survey download first.");
            }

            job = null;
        }

        string root = ResolveRoot(createIfMissing: false);

        if (keepOrder == null)
        {
            lock (sync)
            {
                cachedInventory = null;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                Logger.Info($"[DssSurveyService] Survey deleted from '{root}'.");
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Error($"[DssSurveyService] Delete failed: {ex.Message}");
                return OperationResult.Fail(500, $"Failed to delete the survey: {ex.Message}");
            }
        }

        if (keepOrder.Value < BaseOrder || keepOrder.Value >= MaxOrder)
        {
            return OperationResult.Fail(400, $"keepOrder must be between {BaseOrder} and {MaxOrder - 1}.");
        }

        SurveyInventory inventory = GetInventory(root, forceRescan: true);
        if (inventory.InstalledOrder == null || keepOrder.Value >= inventory.InstalledOrder.Value)
        {
            return OperationResult.Fail(409, $"Order {keepOrder.Value} is not installed above the current order; nothing to delete.");
        }

        try
        {
            for (int order = keepOrder.Value + 1; order <= MaxOrder; order++)
            {
                string orderDir = Path.Combine(root, $"Norder{order}");
                if (Directory.Exists(orderDir))
                {
                    Directory.Delete(orderDir, recursive: true);
                }
            }

            InvalidateInventory();
            Logger.Info($"[DssSurveyService] Survey downgraded to order {keepOrder.Value} in '{root}'.");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Logger.Error($"[DssSurveyService] Delete failed: {ex.Message}");
            return OperationResult.Fail(500, $"Failed to delete the survey: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ download job

    private async Task RunJobAsync(DownloadJob current, string root)
    {
        CancellationToken token = current.Cancellation.Token;
        try
        {
            DeleteLegacyTiles(root);

            for (int order = MinOrder; order <= current.TargetOrder; order++)
            {
                current.CurrentOrder = order;
                await DownloadOrderAsync(current, root, order, token).ConfigureAwait(false);

                if (current.FailedInOrder > 0)
                {
                    throw new SurveyDownloadException(
                        $"{current.FailedInOrder} tiles of order {order} could not be downloaded.");
                }

                if (order == MinOrder && !File.Exists(AllskyPath(root)))
                {
                    await BuildAllskyAsync(root, token).ConfigureAwait(false);
                }

                WriteProperties(root, order, sourceUrls[0]);
                InvalidateInventory();
            }

            current.Finish("completed", null);
            Logger.Info($"[DssSurveyService] Download to order {current.TargetOrder} completed.");
        }
        catch (OperationCanceledException)
        {
            current.Finish("cancelled", "Download cancelled.");
            Logger.Info("[DssSurveyService] Download cancelled.");
        }
        catch (SurveyDownloadException ex)
        {
            current.Finish("failed", ex.Message);
            Logger.Warning($"[DssSurveyService] Download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            current.Finish("failed", ex.Message);
            Logger.Error($"[DssSurveyService] Download failed: {ex}");
        }
        finally
        {
            InvalidateInventory();
        }
    }

    private async Task DownloadOrderAsync(DownloadJob current, string root, int order, CancellationToken token)
    {
        int tileCount = TileCount(order);
        List<int> missing = new();
        for (int npix = 0; npix < tileCount; npix++)
        {
            if (!TileExists(Path.Combine(root, TileRelativePath(order, npix))))
            {
                missing.Add(npix);
            }
        }

        current.FailedInOrder = 0;
        if (missing.Count == 0)
        {
            return;
        }

        foreach (int dirStart in missing.Select(n => n / 10000 * 10000).Distinct())
        {
            Directory.CreateDirectory(Path.Combine(root, $"Norder{order}", $"Dir{dirStart}"));
        }

        using SemaphoreSlim gate = new(ParallelDownloads);
        int consecutiveFailures = 0;
        Exception abortReason = null;
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task[] workers = missing.Select(async npix =>
        {
            await gate.WaitAsync(abort.Token).ConfigureAwait(false);
            try
            {
                abort.Token.ThrowIfCancellationRequested();
                TileResult result = await DownloadTileAsync(root, order, npix, abort.Token).ConfigureAwait(false);
                if (result.Success)
                {
                    Interlocked.Exchange(ref consecutiveFailures, 0);
                    current.RecordTile(result.Bytes);
                }
                else
                {
                    current.RecordFailure();
                    int failures = Interlocked.Increment(ref consecutiveFailures);
                    if (failures >= MaxConsecutiveFailures)
                    {
                        abortReason ??= new SurveyDownloadException(
                            $"Download aborted after {MaxConsecutiveFailures} consecutive failures: {result.Error}");
                        abort.Cancel();
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortReason != null && !token.IsCancellationRequested)
        {
            throw abortReason;
        }
    }

    private async Task<TileResult> DownloadTileAsync(string root, int order, int npix, CancellationToken token)
    {
        string targetPath = Path.Combine(root, TileRelativePath(order, npix));
        string lastError = "unknown error";

        for (int attempt = 1; attempt <= TileAttempts; attempt++)
        {
            bool notFoundEverywhere = true;
            foreach (string source in sourceUrls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using HttpResponseMessage response = await Http
                        .GetAsync(TileUrl(source, order, npix), HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        lastError = $"tile {order}/{npix} not found at {source}";
                        continue;
                    }

                    notFoundEverywhere = false;

                    response.EnsureSuccessStatusCode();
                    byte[] jpeg = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    if (!IsJpeg(jpeg))
                    {
                        throw new SurveyDownloadException($"tile {order}/{npix} from {source} is not a JPEG");
                    }

                    WriteAtomically(targetPath, jpeg);
                    return TileResult.Ok(jpeg.Length);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    notFoundEverywhere = false;
                    lastError = ex.Message;
                }
            }

            // A 404 from every source is permanent; only transient errors earn a retry.
            if (notFoundEverywhere)
            {
                break;
            }

            if (attempt < TileAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token).ConfigureAwait(false);
            }
        }

        Logger.Debug($"[DssSurveyService] Tile {order}/{npix} failed: {lastError}");
        return TileResult.Fail(lastError);
    }

    /// <summary>The source is stored unchanged, so only the JPEG SOI marker guards against
    /// an error page being kept as a tile.</summary>
    internal static bool IsJpeg(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    /// <summary>
    /// Removes the WebP tiles and Allsky of a survey written by an earlier plugin version.
    /// The app only ever sees one tile format, so a JPEG download never starts on top of them.
    /// </summary>
    private static void DeleteLegacyTiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        int deleted = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*" + LegacyTileExtension, SearchOption.AllDirectories))
        {
            File.Delete(path);
            deleted++;
        }

        if (deleted > 0)
        {
            Logger.Info($"[DssSurveyService] Removed {deleted} legacy WebP files before the download.");
        }
    }

    /// <summary>
    /// The order-3 Allsky preview the Atlas expects: 768 tiles at 64 px in 27 columns
    /// (1728 x 1856), built from the tiles already on disk. This is the only place the
    /// server still decodes tiles.
    /// </summary>
    private static async Task BuildAllskyAsync(string root, CancellationToken token)
    {
        int tileCount = TileCount(MinOrder);
        int rows = (int)Math.Ceiling(tileCount / (double)AllskyColumns);
        using Image<Rgb24> allsky = new(AllskyColumns * AllskyTileWidth, rows * AllskyTileWidth);

        for (int npix = 0; npix < tileCount; npix++)
        {
            token.ThrowIfCancellationRequested();
            string tilePath = Path.Combine(root, TileRelativePath(MinOrder, npix));
            using Image<Rgb24> tile = await Image.LoadAsync<Rgb24>(tilePath, token).ConfigureAwait(false);
            tile.Mutate(x => x.Resize(AllskyTileWidth, AllskyTileWidth));
            Point position = new(npix % AllskyColumns * AllskyTileWidth, npix / AllskyColumns * AllskyTileWidth);
            allsky.Mutate(x => x.DrawImage(tile, position, 1f));
        }

        using MemoryStream output = new();
        allsky.Save(output, AllskyEncoder);
        WriteAtomically(AllskyPath(root), output.ToArray());
    }

    private static string AllskyPath(string root) => Path.Combine(root, $"Norder{MinOrder}", "Allsky" + TileExtension);

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = path + ".part";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static bool TileExists(string path)
    {
        FileInfo info = new(path);
        return info.Exists && info.Length > 0;
    }

    // ------------------------------------------------------------------ properties

    public static string BuildPropertiesFile(int installedOrder, string masterUrl, DateTime releaseDateUtc)
    {
        StringBuilder sb = new();
        sb.Append("creator_did          = ivo://CDS/P/DSS2/color\n");
        sb.Append("obs_collection       = DSS colored\n");
        sb.Append("obs_title            = DSS colored\n");
        sb.Append("obs_copyright        = Digitized Sky Survey - STScI/NASA, Colored & Healpixed by CDS\n");
        sb.Append("obs_copyright_url    = http://archive.stsci.edu/dss/copyright.html\n");
        sb.Append("hips_copyright       = CNRS/Unistra\n");
        sb.Append("hips_creator         = CDS (A.Oberto, P.Fernique)\n");
        sb.Append("hips_builder         = Touch-N-Stars DssSurveyService\n");
        sb.Append("hips_version         = 1.4\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_release_date    = {releaseDateUtc:yyyy-MM-dd'T'HH:mm'Z'}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_order           = {installedOrder}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_order_min       = {MinOrder}\n");
        sb.Append("hips_frame           = equatorial\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_tile_width      = {TileWidth}\n");
        sb.Append("hips_tile_format     = jpeg\n");
        sb.Append("hips_status          = private mirror unclonable\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_master_url      = {masterUrl}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_service_url     = {SurveyRoute}\n");
        sb.Append("dataproduct_type     = image\n");
        sb.Append("dataproduct_subtype  = color\n");
        sb.Append("moc_sky_fraction     = 1\n");
        sb.Append("prov_progenitor      = STScI\n");
        sb.Append("obs_ack              = The Digitized Sky Surveys were produced at the Space Telescope Science Institute under U.S. Government grant NAG W-2166. The images of these surveys are based on photographic data obtained using the Oschin Schmidt Telescope on Palomar Mountain and the UK Schmidt Telescope. The plates were processed into the present compressed digital form with the permission of these institutions.\n");
        return sb.ToString();
    }

    private static void WriteProperties(string root, int installedOrder, string masterUrl)
    {
        File.WriteAllText(
            Path.Combine(root, PropertiesFileName),
            BuildPropertiesFile(installedOrder, masterUrl, DateTime.UtcNow),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static int? ReadAdvertisedOrder(string propertiesPath)
    {
        try
        {
            if (!File.Exists(propertiesPath))
            {
                return null;
            }

            foreach (string line in File.ReadLines(propertiesPath))
            {
                if (!line.StartsWith("hips_order", StringComparison.Ordinal) || line.StartsWith("hips_order_min", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator > 0 && int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
                {
                    return order;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[DssSurveyService] properties unreadable: {ex.Message}");
        }

        return null;
    }

    // ------------------------------------------------------------------ inventory

    private SurveyInventory GetInventory(string root, bool forceRescan = false)
    {
        bool jobRunning;
        lock (sync)
        {
            if (!forceRescan && cachedInventory != null)
            {
                return cachedInventory;
            }

            jobRunning = job != null && job.IsRunning;
        }

        SurveyInventory inventory = ScanInventory(root);
        // The running job writes properties/Allsky itself; reconciling in parallel would only
        // duplicate that work on the same files.
        if (!jobRunning)
        {
            ReconcileServedFiles(root, inventory);
        }

        lock (sync)
        {
            cachedInventory = inventory;
        }

        return inventory;
    }

    private void InvalidateInventory()
    {
        lock (sync)
        {
            cachedInventory = null;
        }
    }

    public static SurveyInventory ScanInventory(string root)
    {
        List<OrderState> orders = new();
        long totalBytes = 0;

        for (int order = MinOrder; order <= MaxOrder; order++)
        {
            OrderState state = new() { Order = order, TileCount = TileCount(order) };
            string orderDir = Path.Combine(root ?? string.Empty, $"Norder{order}");
            if (!string.IsNullOrEmpty(root) && Directory.Exists(orderDir))
            {
                foreach (FileInfo file in new DirectoryInfo(orderDir).EnumerateFiles("Npix*" + TileExtension, SearchOption.AllDirectories))
                {
                    if (file.Length <= 0)
                    {
                        continue;
                    }

                    state.TilesPresent++;
                    state.Bytes += file.Length;
                }
            }

            state.Complete = state.TilesPresent >= state.TileCount;
            totalBytes += state.Bytes;
            orders.Add(state);
        }

        bool hasAllsky = !string.IsNullOrEmpty(root) && TileExists(AllskyPath(root));
        if (hasAllsky)
        {
            totalBytes += new FileInfo(AllskyPath(root)).Length;
        }

        bool hasLegacyTiles = !string.IsNullOrEmpty(root)
            && Directory.Exists(root)
            && Directory.EnumerateFiles(root, "*" + LegacyTileExtension, SearchOption.AllDirectories).Any();

        return new SurveyInventory
        {
            Orders = orders,
            InstalledOrder = ResolveInstalledOrder(orders),
            HasAllsky = hasAllsky,
            HasLegacyTiles = hasLegacyTiles,
            TotalBytes = totalBytes
        };
    }

    /// <summary>
    /// Keeps <c>properties</c> and the Allsky preview in step with the tiles on disk, so a
    /// survey that lost files (or was left by a crashed job) never advertises an order it
    /// cannot serve. With no complete base order the properties file is removed, which is
    /// what tells the app to keep the survey layer off.
    /// </summary>
    private void ReconcileServedFiles(string root, SurveyInventory inventory)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            string propertiesPath = Path.Combine(root, PropertiesFileName);
            if (inventory.InstalledOrder == null)
            {
                if (File.Exists(propertiesPath))
                {
                    File.Delete(propertiesPath);
                }

                return;
            }

            if (ReadAdvertisedOrder(propertiesPath) != inventory.InstalledOrder)
            {
                WriteProperties(root, inventory.InstalledOrder.Value, sourceUrls[0]);
            }

            if (!inventory.HasAllsky)
            {
                BuildAllskyAsync(root, CancellationToken.None).GetAwaiter().GetResult();
                inventory.HasAllsky = true;
                inventory.TotalBytes += new FileInfo(AllskyPath(root)).Length;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[DssSurveyService] Reconcile failed: {ex.Message}");
        }
    }

    private static long? GetFreeBytes(string root)
    {
        try
        {
            string probe = root;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
            }

            if (string.IsNullOrEmpty(probe))
            {
                return null;
            }

            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[DssSurveyService] Free space unavailable: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ types

    private sealed class DownloadJob
    {
        private int tilesDone;
        private int tilesFailed;
        private int failedInOrder;
        private long bytesDownloaded;
        private volatile string state = "running";
        private volatile string error;
        private volatile int currentOrder;

        public DownloadJob(int targetOrder, int tilesTotal)
        {
            TargetOrder = targetOrder;
            TilesTotal = tilesTotal;
            StartedAt = DateTime.UtcNow;
        }

        public int TargetOrder { get; }
        public int TilesTotal { get; }
        public DateTime StartedAt { get; }
        public DateTime? FinishedAt { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; }
        public bool IsRunning => state == "running";

        public int CurrentOrder
        {
            get => currentOrder;
            set => currentOrder = value;
        }

        public int TilesDone
        {
            get => Volatile.Read(ref tilesDone);
            set => Volatile.Write(ref tilesDone, value);
        }

        public int FailedInOrder
        {
            get => Volatile.Read(ref failedInOrder);
            set => Volatile.Write(ref failedInOrder, value);
        }

        public void RecordTile(long bytes)
        {
            Interlocked.Increment(ref tilesDone);
            Interlocked.Add(ref bytesDownloaded, bytes);
        }

        public void RecordFailure()
        {
            Interlocked.Increment(ref tilesFailed);
            Interlocked.Increment(ref failedInOrder);
        }

        public void Finish(string finalState, string message)
        {
            error = message;
            FinishedAt = DateTime.UtcNow;
            state = finalState;
        }

        public SurveyJobStatus Snapshot()
        {
            return new SurveyJobStatus
            {
                State = state,
                TargetOrder = TargetOrder,
                CurrentOrder = currentOrder,
                TilesTotal = TilesTotal,
                TilesDone = TilesDone,
                TilesFailed = Volatile.Read(ref tilesFailed),
                BytesDownloaded = Volatile.Read(ref bytesDownloaded),
                Error = error,
                StartedAt = StartedAt,
                FinishedAt = FinishedAt
            };
        }
    }

    private readonly struct TileResult
    {
        private TileResult(bool success, int bytes, string error)
        {
            Success = success;
            Bytes = bytes;
            Error = error;
        }

        public bool Success { get; }
        public int Bytes { get; }
        public string Error { get; }

        public static TileResult Ok(int bytes) => new(true, bytes, null);
        public static TileResult Fail(string error) => new(false, 0, error);
    }

    private sealed class SurveyDownloadException : Exception
    {
        public SurveyDownloadException(string message) : base(message)
        {
        }
    }

    public sealed class OrderState
    {
        public int Order { get; set; }
        public int TileCount { get; set; }
        public int TilesPresent { get; set; }
        public long Bytes { get; set; }
        public bool Complete { get; set; }
    }

    public sealed class SurveyInventory
    {
        public List<OrderState> Orders { get; set; } = new();
        public int? InstalledOrder { get; set; }
        public bool HasAllsky { get; set; }
        /// <summary>WebP tiles from a plugin version that re-encoded; replaced on the next download.</summary>
        public bool HasLegacyTiles { get; set; }
        public long TotalBytes { get; set; }
    }

    public sealed class SurveyJobStatus
    {
        public string State { get; set; }
        public int TargetOrder { get; set; }
        public int CurrentOrder { get; set; }
        public int TilesTotal { get; set; }
        public int TilesDone { get; set; }
        public int TilesFailed { get; set; }
        public long BytesDownloaded { get; set; }
        public string Error { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    public sealed class SurveyStatus
    {
        public string Path { get; set; }
        public string[] SourceUrls { get; set; }
        public int? InstalledOrder { get; set; }
        public bool HasAllsky { get; set; }
        public bool LegacyFormat { get; set; }
        public long TotalBytes { get; set; }
        public long? FreeBytes { get; set; }
        public List<OrderState> Orders { get; set; }
        public SurveyJobStatus Job { get; set; }
    }

    public sealed class OperationResult
    {
        public bool Success { get; private set; }
        public int StatusCode { get; private set; }
        public string Error { get; private set; }

        public static OperationResult Ok() => new() { Success = true, StatusCode = 200 };

        public static OperationResult Fail(int statusCode, string error) => new()
        {
            Success = false,
            StatusCode = statusCode,
            Error = error
        };
    }
}
