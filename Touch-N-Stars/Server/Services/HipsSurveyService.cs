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
/// Downloads an Atlas HiPS survey (see <see cref="SurveyDefinition"/>: DSS colour, NSNS
/// narrowband colour and single lines) tile by tile into the persistent Celestia Atlas data directory and keeps the
/// served survey consistent with what is actually on disk. DSS tiles are stored as the
/// source JPEGs, unchanged: re-encoding to WebP cost ~1.3 s per tile on a PINS host and
/// made the download CPU-bound. NSNS ships 8-bit PNGs (up to ~610 kB), which are re-encoded to JPEG q85.
///
/// Only one download job runs at a time across all surveys and its state lives in memory;
/// everything else (installed order, disk usage, resume position) is reconstructed from the
/// files, so a server restart mid-job simply leaves a resumable partial survey behind. An
/// order is advertised in <c>properties</c> only once every one of its tiles exists; for a
/// survey with a coverage map that means every tile inside the coverage.
/// </summary>
public sealed class HipsSurveyService
{
    public const int TileWidth = 512;
    public const double FreeSpaceMargin = 0.10;

    private const string SurveysFolderName = "surveys";
    private const string PropertiesFileName = "properties";
    private const string MocFileName = "Moc.fits";
    private const string TileExtension = ".jpg";
    private const string LegacyTileExtension = ".webp";
    private const int ParallelDownloads = 8;
    private const int TileAttempts = 3;
    private const int MaxConsecutiveFailures = 25;
    private const int AllskyColumns = 27;
    private const int AllskyTileWidth = 64;

    private static readonly JpegEncoder JpegQuality85 = new() { Quality = 85 };

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Lazy<IReadOnlyList<HipsSurveyService>> LazyAll =
        new(() => SurveyDefinition.All.Select(d => new HipsSurveyService(d)).ToArray());

    // Guards the "one job across all surveys" rule; the job itself is per instance.
    private static readonly object StartGate = new();

    /// <summary>One shared service per <see cref="SurveyDefinition.All"/> entry.</summary>
    public static IReadOnlyList<HipsSurveyService> All => LazyAll.Value;

    /// <summary>The service for a survey id; null/empty means DSS, unknown ids return null.</summary>
    public static HipsSurveyService ForId(string id)
    {
        SurveyDefinition definition = SurveyDefinition.Find(id);
        return definition == null ? null : All.First(s => s.Definition == definition);
    }

    private readonly object sync = new();
    private readonly string[] sourceUrls;
    private readonly string rootOverride;
    private readonly IReadOnlyList<HipsSurveyService> peers;
    private SurveyInventory cachedInventory;
    private MocCoverage coverage;
    private DownloadJob job;

    /// <param name="definition">Which survey this instance manages.</param>
    /// <param name="sourceUrlOverride">Tile sources for tests; null uses the environment/defaults.</param>
    /// <param name="rootOverride">Survey folder for tests; null uses the persistent data directory.</param>
    /// <param name="peers">Services whose running job blocks a start here; null means the shared instances.</param>
    public HipsSurveyService(
        SurveyDefinition definition,
        string[] sourceUrlOverride = null,
        string rootOverride = null,
        IReadOnlyList<HipsSurveyService> peers = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        sourceUrls = sourceUrlOverride ?? ResolveSourceUrls(definition);
        this.rootOverride = rootOverride;
        this.peers = peers;
    }

    public SurveyDefinition Definition { get; }

    public bool IsJobRunning
    {
        get
        {
            lock (sync)
            {
                return job != null && job.IsRunning;
            }
        }
    }

    private string LogPrefix => $"[HipsSurveyService:{Definition.Id}]";

    // ------------------------------------------------------------------ HiPS layout helpers

    public static int FullSkyTileCount(int order) => 12 << (2 * order);

    /// <summary>HiPS groups tiles into Dir folders of 10000 (Dir0, Dir10000, ...).</summary>
    public static string TileDirectoryName(int npix) => $"Dir{npix / 10000 * 10000}";

    public static string TileRelativePath(int order, int npix, string extension = TileExtension)
    {
        return Path.Combine($"Norder{order}", TileDirectoryName(npix), $"Npix{npix}{extension}");
    }

    public static string TileUrl(string baseUrl, int order, int npix, string extension)
    {
        return $"{baseUrl.TrimEnd('/')}/Norder{order}/{TileDirectoryName(npix)}/Npix{npix}{extension}";
    }

    /// <summary>
    /// Tiles of one order that exist in this survey: all of them, or those inside the
    /// coverage map. Without the map (not downloaded yet) the expected count is returned and
    /// the list is null.
    /// </summary>
    public static int TileCount(SurveyDefinition definition, MocCoverage coverage, int order)
    {
        if (!definition.UsesCoverageMoc)
        {
            return FullSkyTileCount(order);
        }

        if (coverage != null)
        {
            return coverage.TileCount(order);
        }

        return definition.ExpectedTileCounts != null
            && definition.ExpectedTileCounts.TryGetValue(order, out int expected)
            ? expected
            : FullSkyTileCount(order);
    }

    public static IEnumerable<int> TileIndices(SurveyDefinition definition, MocCoverage coverage, int order)
    {
        if (!definition.UsesCoverageMoc)
        {
            return Enumerable.Range(0, FullSkyTileCount(order));
        }

        if (coverage == null)
        {
            throw new InvalidOperationException("The coverage map must be loaded before tiles are enumerated.");
        }

        return coverage.Tiles(order);
    }

    public int TileCountUpTo(int order)
    {
        MocCoverage current = coverage;
        int total = 0;
        for (int o = Definition.MinOrder; o <= order; o++)
        {
            total += TileCount(Definition, current, o);
        }

        return total;
    }

    public static long EstimateBytes(SurveyDefinition definition, int order, int tileCount)
    {
        return definition.AverageTileBytes.TryGetValue(order, out long perTile) ? perTile * tileCount : 0;
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
    /// Persistent survey folder next to the user landscapes; TNS_&lt;ID&gt;_SURVEY_PATH
    /// overrides it. Same logic on Windows/NINA and PINS.
    /// </summary>
    public static string ResolvePersistentSurveyRoot(SurveyDefinition definition, bool createIfMissing)
    {
        string configured = Environment.GetEnvironmentVariable(definition.PathEnvironmentVariable);
        string root = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                StellariumLandscapeService.ResolvePersistentCelestiaAtlasDataRoot(),
                SurveysFolderName,
                definition.FolderName);

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
            return ResolvePersistentSurveyRoot(Definition, createIfMissing);
        }

        if (createIfMissing)
        {
            Directory.CreateDirectory(rootOverride);
        }

        return rootOverride;
    }

    private static string[] ResolveSourceUrls(SurveyDefinition definition)
    {
        string configured = Environment.GetEnvironmentVariable(definition.SourceUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return definition.DefaultSourceUrls;
        }

        string[] urls = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return urls.Length > 0 ? urls : definition.DefaultSourceUrls;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Touch-N-Stars-HiPS-Survey/1.0");
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
            Survey = Definition.Id,
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
        if (targetOrder < Definition.BaseOrder || targetOrder > Definition.MaxOrder)
        {
            return OperationResult.Fail(400, $"targetOrder must be between {Definition.BaseOrder} and {Definition.MaxOrder}.");
        }

        string root = ResolveRoot(createIfMissing: true);
        SurveyInventory inventory = GetInventory(root, forceRescan: true);

        long missingBytes = 0;
        int missingTiles = 0;
        foreach (OrderState state in inventory.Orders.Where(o => o.Order <= targetOrder))
        {
            int missing = Math.Max(0, state.TileCount - state.TilesPresent);
            missingTiles += missing;
            missingBytes += EstimateBytes(Definition, state.Order, missing);
        }

        long? free = GetFreeBytes(root);
        long required = (long)Math.Ceiling(missingBytes * (1 + FreeSpaceMargin));
        if (free.HasValue && missingTiles > 0 && free.Value < required)
        {
            return OperationResult.Fail(
                507,
                $"Not enough free disk space: about {required / 1_000_000} MB needed, {free.Value / 1_000_000} MB free.");
        }

        lock (StartGate)
        {
            HipsSurveyService busyPeer = (peers ?? All).FirstOrDefault(p => p != this && p.IsJobRunning);
            if (busyPeer != null)
            {
                return OperationResult.Fail(
                    409,
                    $"The {busyPeer.Definition.Id.ToUpperInvariant()} survey is downloading; wait for it or cancel it first.");
            }

            lock (sync)
            {
                if (job != null && job.IsRunning)
                {
                    return OperationResult.Fail(409, "A survey download is already running.");
                }

                DownloadJob newJob = new(targetOrder, TileCountUpTo(targetOrder));
                newJob.TilesDone = inventory.Orders.Where(o => o.Order <= targetOrder).Sum(o => Math.Min(o.TilesPresent, o.TileCount));
                job = newJob;
                newJob.Task = Task.Run(() => RunJobAsync(newJob, root));
            }
        }

        Logger.Info($"{LogPrefix} Download to order {targetOrder} started ({missingTiles} tiles missing).");
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
                coverage = null;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                Logger.Info($"{LogPrefix} Survey deleted from '{root}'.");
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Error($"{LogPrefix} Delete failed: {ex.Message}");
                return OperationResult.Fail(500, $"Failed to delete the survey: {ex.Message}");
            }
        }

        if (keepOrder.Value < Definition.BaseOrder || keepOrder.Value >= Definition.MaxOrder)
        {
            return OperationResult.Fail(400, $"keepOrder must be between {Definition.BaseOrder} and {Definition.MaxOrder - 1}.");
        }

        SurveyInventory inventory = GetInventory(root, forceRescan: true);
        if (inventory.InstalledOrder == null || keepOrder.Value >= inventory.InstalledOrder.Value)
        {
            return OperationResult.Fail(409, $"Order {keepOrder.Value} is not installed above the current order; nothing to delete.");
        }

        try
        {
            for (int order = keepOrder.Value + 1; order <= Definition.MaxOrder; order++)
            {
                string orderDir = Path.Combine(root, $"Norder{order}");
                if (Directory.Exists(orderDir))
                {
                    Directory.Delete(orderDir, recursive: true);
                }
            }

            InvalidateInventory();
            Logger.Info($"{LogPrefix} Survey downgraded to order {keepOrder.Value} in '{root}'.");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Logger.Error($"{LogPrefix} Delete failed: {ex.Message}");
            return OperationResult.Fail(500, $"Failed to delete the survey: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ download job

    private async Task RunJobAsync(DownloadJob current, string root)
    {
        CancellationToken token = current.Cancellation.Token;
        try
        {
            if (Definition.HasLegacyWebp)
            {
                DeleteLegacyTiles(root);
            }

            if (Definition.UsesCoverageMoc)
            {
                await EnsureCoverageAsync(root, token).ConfigureAwait(false);
                current.TilesTotal = TileCountUpTo(current.TargetOrder);
            }

            for (int order = Definition.MinOrder; order <= current.TargetOrder; order++)
            {
                current.CurrentOrder = order;
                await DownloadOrderAsync(current, root, order, token).ConfigureAwait(false);

                if (current.FailedInOrder > 0)
                {
                    throw new SurveyDownloadException(
                        $"{current.FailedInOrder} tiles of order {order} could not be downloaded.");
                }

                if (order == Definition.MinOrder && !File.Exists(AllskyPath(root)))
                {
                    await BuildAllskyAsync(root, token).ConfigureAwait(false);
                }

                WriteProperties(root, order, sourceUrls[0]);
                InvalidateInventory();
            }

            current.Finish("completed", null);
            Logger.Info($"{LogPrefix} Download to order {current.TargetOrder} completed.");
        }
        catch (OperationCanceledException)
        {
            current.Finish("cancelled", "Download cancelled.");
            Logger.Info($"{LogPrefix} Download cancelled.");
        }
        catch (SurveyDownloadException ex)
        {
            current.Finish("failed", ex.Message);
            Logger.Warning($"{LogPrefix} Download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            current.Finish("failed", ex.Message);
            Logger.Error($"{LogPrefix} Download failed: {ex}");
        }
        finally
        {
            InvalidateInventory();
        }
    }

    /// <summary>
    /// Loads the coverage map from disk, or fetches it from the first source that has it.
    /// It is kept in the survey folder so status and resume work offline afterwards.
    /// </summary>
    private async Task EnsureCoverageAsync(string root, CancellationToken token)
    {
        if (LoadCoverage(root) != null)
        {
            return;
        }

        string lastError = "no source answered";
        foreach (string source in sourceUrls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes = await Http
                    .GetByteArrayAsync($"{source.TrimEnd('/')}/{MocFileName}", token)
                    .ConfigureAwait(false);
                MocCoverage parsed = MocCoverage.Parse(bytes);
                WriteAtomically(Path.Combine(root, MocFileName), bytes);
                lock (sync)
                {
                    coverage = parsed;
                }

                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{source}: {ex.Message}";
            }
        }

        throw new SurveyDownloadException($"The survey coverage map could not be downloaded ({lastError}).");
    }

    /// <summary>The cached coverage map, read from the survey folder on first use; null when absent.</summary>
    private MocCoverage LoadCoverage(string root)
    {
        if (!Definition.UsesCoverageMoc)
        {
            return null;
        }

        lock (sync)
        {
            if (coverage != null)
            {
                return coverage;
            }
        }

        string path = string.IsNullOrEmpty(root) ? null : Path.Combine(root, MocFileName);
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            MocCoverage parsed = MocCoverage.Parse(File.ReadAllBytes(path));
            lock (sync)
            {
                coverage = parsed;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            Logger.Warning($"{LogPrefix} Coverage map unreadable, it will be downloaded again: {ex.Message}");
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // the next download overwrites it
            }

            return null;
        }
    }

    private async Task DownloadOrderAsync(DownloadJob current, string root, int order, CancellationToken token)
    {
        List<int> missing = new();
        foreach (int npix in TileIndices(Definition, coverage, order))
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
                        .GetAsync(TileUrl(source, order, npix, Definition.SourceExtension), HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        lastError = $"tile {order}/{npix} not found at {source}";
                        continue;
                    }

                    notFoundEverywhere = false;

                    response.EnsureSuccessStatusCode();
                    byte[] payload = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    byte[] jpeg = Definition.ConvertToJpeg
                        ? ConvertToJpeg(payload, $"tile {order}/{npix} from {source}")
                        : payload;
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

        Logger.Debug($"{LogPrefix} Tile {order}/{npix} failed: {lastError}");
        return TileResult.Fail(lastError);
    }

    /// <summary>A stored tile is never decoded again, so only the JPEG SOI marker guards
    /// against an error page being kept as a tile.</summary>
    internal static bool IsJpeg(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    internal static bool IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>
    /// Re-encodes a source tile (RGBA PNG for NSNS) as JPEG q85, flattened on black so
    /// transparent pixels at the coverage edge stay dark instead of taking random colours.
    /// </summary>
    internal static byte[] ConvertToJpeg(byte[] source, string description)
    {
        if (!IsPng(source) && !IsJpeg(source))
        {
            throw new SurveyDownloadException($"{description} is not an image");
        }

        using Image<Rgba32> image = Image.Load<Rgba32>(source);
        image.Mutate(x => x.BackgroundColor(Color.Black));
        using Image<Rgb24> rgb = image.CloneAs<Rgb24>();
        using MemoryStream output = new();
        rgb.Save(output, JpegQuality85);
        return output.ToArray();
    }

    /// <summary>
    /// Removes the WebP tiles and Allsky of a survey written by an earlier plugin version.
    /// The app only ever sees one tile format, so a JPEG download never starts on top of them.
    /// </summary>
    private void DeleteLegacyTiles(string root)
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
            Logger.Info($"{LogPrefix} Removed {deleted} legacy WebP files before the download.");
        }
    }

    /// <summary>
    /// The order-3 Allsky preview the Atlas expects: 768 tiles at 64 px in 27 columns
    /// (1728 x 1856), built from the tiles already on disk. Tiles outside a partial coverage
    /// stay black. Apart from the NSNS conversion this is the only place tiles are decoded.
    /// </summary>
    private async Task BuildAllskyAsync(string root, CancellationToken token)
    {
        int minOrder = Definition.MinOrder;
        int tileCount = FullSkyTileCount(minOrder);
        int rows = (int)Math.Ceiling(tileCount / (double)AllskyColumns);
        using Image<Rgb24> allsky = new(AllskyColumns * AllskyTileWidth, rows * AllskyTileWidth);

        for (int npix = 0; npix < tileCount; npix++)
        {
            token.ThrowIfCancellationRequested();
            string tilePath = Path.Combine(root, TileRelativePath(minOrder, npix));
            if (Definition.UsesCoverageMoc && !TileExists(tilePath))
            {
                continue;
            }

            using Image<Rgb24> tile = await Image.LoadAsync<Rgb24>(tilePath, token).ConfigureAwait(false);
            tile.Mutate(x => x.Resize(AllskyTileWidth, AllskyTileWidth));
            Point position = new(npix % AllskyColumns * AllskyTileWidth, npix / AllskyColumns * AllskyTileWidth);
            allsky.Mutate(x => x.DrawImage(tile, position, 1f));
        }

        using MemoryStream output = new();
        allsky.Save(output, JpegQuality85);
        WriteAtomically(AllskyPath(root), output.ToArray());
    }

    private string AllskyPath(string root) => AllskyPath(Definition, root);

    private static string AllskyPath(SurveyDefinition definition, string root) =>
        Path.Combine(root, $"Norder{definition.MinOrder}", "Allsky" + TileExtension);

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

    public static string BuildPropertiesFile(SurveyDefinition definition, int installedOrder, string masterUrl, DateTime releaseDateUtc)
    {
        StringBuilder sb = new();
        sb.Append(definition.PropertiesHeader);
        sb.Append("hips_builder         = Touch-N-Stars HipsSurveyService\n");
        sb.Append("hips_version         = 1.4\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_release_date    = {releaseDateUtc:yyyy-MM-dd'T'HH:mm'Z'}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_order           = {installedOrder}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_order_min       = {definition.MinOrder}\n");
        sb.Append("hips_frame           = equatorial\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_tile_width      = {TileWidth}\n");
        sb.Append("hips_tile_format     = jpeg\n");
        sb.Append("hips_status          = private mirror unclonable\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_master_url      = {masterUrl}\n");
        sb.Append(CultureInfo.InvariantCulture, $"hips_service_url     = {definition.Route}\n");
        sb.Append("dataproduct_type     = image\n");
        sb.Append("dataproduct_subtype  = color\n");
        sb.Append(definition.PropertiesFooter);
        return sb.ToString();
    }

    private void WriteProperties(string root, int installedOrder, string masterUrl)
    {
        File.WriteAllText(
            Path.Combine(root, PropertiesFileName),
            BuildPropertiesFile(Definition, installedOrder, masterUrl, DateTime.UtcNow),
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
            Logger.Debug($"[HipsSurveyService] properties unreadable: {ex.Message}");
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

        SurveyInventory inventory = ScanInventory(Definition, root, LoadCoverage(root));
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

    /// <param name="coverage">
    /// Coverage map of a partial-sky survey, or null. Without it such a survey counts as not
    /// installed: completeness cannot be judged without knowing which tiles exist.
    /// </param>
    public static SurveyInventory ScanInventory(SurveyDefinition definition, string root, MocCoverage coverage = null)
    {
        List<OrderState> orders = new();
        long totalBytes = 0;
        bool coverageKnown = !definition.UsesCoverageMoc || coverage != null;

        for (int order = definition.MinOrder; order <= definition.MaxOrder; order++)
        {
            OrderState state = new() { Order = order, TileCount = TileCount(definition, coverage, order) };
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

            state.Complete = coverageKnown && state.TilesPresent >= state.TileCount;
            totalBytes += state.Bytes;
            orders.Add(state);
        }

        bool hasAllsky = !string.IsNullOrEmpty(root) && TileExists(AllskyPath(definition, root));
        if (hasAllsky)
        {
            totalBytes += new FileInfo(AllskyPath(definition, root)).Length;
        }

        bool hasLegacyTiles = definition.HasLegacyWebp
            && !string.IsNullOrEmpty(root)
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
            Logger.Warning($"{LogPrefix} Reconcile failed: {ex.Message}");
        }
    }

    private long? GetFreeBytes(string root)
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
            Logger.Debug($"{LogPrefix} Free space unavailable: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ types

    private sealed class DownloadJob
    {
        private int tilesDone;
        private int tilesTotal;
        private int tilesFailed;
        private int failedInOrder;
        private long bytesDownloaded;
        private volatile string state = "running";
        private volatile string error;
        private volatile int currentOrder;

        public DownloadJob(int targetOrder, int tilesTotal)
        {
            TargetOrder = targetOrder;
            this.tilesTotal = tilesTotal;
            StartedAt = DateTime.UtcNow;
        }

        public int TargetOrder { get; }
        public DateTime StartedAt { get; }
        public DateTime? FinishedAt { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; }
        public bool IsRunning => state == "running";

        /// <summary>Settable: a partial-sky survey knows its exact total only once the coverage map is loaded.</summary>
        public int TilesTotal
        {
            get => Volatile.Read(ref tilesTotal);
            set => Volatile.Write(ref tilesTotal, value);
        }

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
        public string Survey { get; set; }
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
