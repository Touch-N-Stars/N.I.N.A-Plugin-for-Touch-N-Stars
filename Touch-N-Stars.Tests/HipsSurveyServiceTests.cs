using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class HipsSurveyServiceTests
{
    [Theory]
    [InlineData(3, 768)]
    [InlineData(4, 3072)]
    [InlineData(5, 12288)]
    [InlineData(6, 49152)]
    [InlineData(7, 196608)]
    public void TileCount_FollowsHipsFormula(int order, int expected)
    {
        Assert.Equal(expected, HipsSurveyService.FullSkyTileCount(order));
    }

    [Fact]
    public void TileCountUpTo_SumsFromMinOrder()
    {
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { "http://127.0.0.1:1/unused" });
        Assert.Equal(768 + 3072, service.TileCountUpTo(4));
        Assert.Equal(768 + 3072 + 12288, service.TileCountUpTo(5));
    }

    [Theory]
    [InlineData(0, "Dir0")]
    [InlineData(9999, "Dir0")]
    [InlineData(10000, "Dir10000")]
    [InlineData(23456, "Dir20000")]
    [InlineData(196607, "Dir190000")]
    public void TileDirectoryName_GroupsBy10000(int npix, string expected)
    {
        Assert.Equal(expected, HipsSurveyService.TileDirectoryName(npix));
    }

    [Fact]
    public void TileRelativePath_And_TileUrl_UseHipsLayout()
    {
        Assert.Equal(
            Path.Combine("Norder5", "Dir10000", "Npix12287.jpg"),
            HipsSurveyService.TileRelativePath(5, 12287));
        Assert.Equal(
            "https://example.org/DSSColor/Norder5/Dir10000/Npix12287.jpg",
            HipsSurveyService.TileUrl("https://example.org/DSSColor/", 5, 12287, ".jpg"));
    }

    [Fact]
    public void ResolveInstalledOrder_StopsAtFirstIncompleteOrder()
    {
        var orders = new[]
        {
            new HipsSurveyService.OrderState { Order = 3, Complete = true },
            new HipsSurveyService.OrderState { Order = 4, Complete = true },
            new HipsSurveyService.OrderState { Order = 5, Complete = false },
            new HipsSurveyService.OrderState { Order = 6, Complete = true }
        };

        Assert.Equal(4, HipsSurveyService.ResolveInstalledOrder(orders));
        Assert.Null(HipsSurveyService.ResolveInstalledOrder(new[]
        {
            new HipsSurveyService.OrderState { Order = 3, Complete = false }
        }));
    }

    [Fact]
    public void BuildPropertiesFile_AdvertisesInstalledOrderAndLocalServiceUrl()
    {
        string properties = HipsSurveyService.BuildPropertiesFile(
            SurveyDefinition.Dss,
            5,
            "https://alasky.cds.unistra.fr/DSS/DSSColor",
            new DateTime(2026, 9, 13, 20, 15, 0, DateTimeKind.Utc));

        Assert.Contains("hips_order           = 5\n", properties);
        Assert.Contains("hips_order_min       = 3\n", properties);
        Assert.Contains("hips_tile_width      = 512\n", properties);
        Assert.Contains("hips_tile_format     = jpeg\n", properties);
        Assert.Contains("hips_service_url     = /celestia-atlas-data/surveys/dss\n", properties);
        Assert.Contains("hips_master_url      = https://alasky.cds.unistra.fr/DSS/DSSColor\n", properties);
        Assert.Contains("hips_release_date    = 2026-09-13T20:15Z\n", properties);
        Assert.Contains("obs_copyright        = Digitized Sky Survey - STScI/NASA", properties);
        Assert.DoesNotContain("hips_service_url     = http", properties);
    }

    [Fact]
    public void ScanInventory_CountsOnlyNonEmptyTilesAndDetectsCompleteOrders()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, 10, skip: new[] { 3 });
        File.WriteAllBytes(survey.TilePath(4, 3), Array.Empty<byte>());

        HipsSurveyService.SurveyInventory inventory = HipsSurveyService.ScanInventory(SurveyDefinition.Dss, survey.Root);

        HipsSurveyService.OrderState order3 = inventory.Orders.Single(o => o.Order == 3);
        HipsSurveyService.OrderState order4 = inventory.Orders.Single(o => o.Order == 4);
        Assert.True(order3.Complete);
        Assert.Equal(768, order3.TilesPresent);
        Assert.False(order4.Complete);
        Assert.Equal(9, order4.TilesPresent);
        Assert.Equal(3, inventory.InstalledOrder);
        Assert.False(inventory.HasAllsky);
        Assert.False(inventory.HasLegacyTiles);
        Assert.Equal(order3.Bytes + order4.Bytes, inventory.TotalBytes);
    }

    [Fact]
    public void ScanInventory_ReportsWebpTilesOfAnOlderPluginAsLegacyOnly()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>(), extension: ".webp");
        File.WriteAllBytes(Path.Combine(survey.Root, "Norder3", "Allsky.webp"), new byte[] { 1 });

        HipsSurveyService.SurveyInventory inventory = HipsSurveyService.ScanInventory(SurveyDefinition.Dss, survey.Root);

        Assert.True(inventory.HasLegacyTiles);
        Assert.Null(inventory.InstalledOrder);
        Assert.False(inventory.HasAllsky);
        Assert.Equal(0, inventory.Orders.Single(o => o.Order == 3).TilesPresent);
    }

    [Fact]
    public void ScanInventory_OnMissingRoot_ReportsNothingInstalled()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tns-hips-missing-{Guid.NewGuid():N}");

        HipsSurveyService.SurveyInventory inventory = HipsSurveyService.ScanInventory(SurveyDefinition.Dss, missing);

        Assert.Null(inventory.InstalledOrder);
        Assert.All(inventory.Orders, o => Assert.Equal(0, o.TilesPresent));
        Assert.Equal(0, inventory.TotalBytes);
    }

    [Fact]
    public async Task StartDownload_ResumesOnlyMissingTiles_AndPublishesCompletedOrders()
    {
        using TempSurvey survey = new();
        // Everything of orders 3-4 except five tiles is already on disk from an earlier run.
        int[] missingOrder4 = { 0, 1, 2000, 3070, 3071 };
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, HipsSurveyService.FullSkyTileCount(4), skip: missingOrder4);

        using TileServer server = new();
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { server.BaseUrl }, survey.Root);

        HipsSurveyService.OperationResult start = service.StartDownload(4);
        Assert.True(start.Success, start.Error);

        await service.WaitForJobAsync();

        HipsSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("completed", status.Job?.State);
        Assert.Equal(4, status.InstalledOrder);
        Assert.True(status.HasAllsky);
        Assert.Equal(service.TileCountUpTo(4), status.Job!.TilesDone);
        Assert.Equal(
            missingOrder4.Select(n => $"/Norder4/{HipsSurveyService.TileDirectoryName(n)}/Npix{n}.jpg").OrderBy(x => x),
            server.RequestedPaths.OrderBy(x => x));

        foreach (int npix in missingOrder4)
        {
            Assert.True(new FileInfo(survey.TilePath(4, npix)).Length > 0);
        }

        Assert.Equal(4, HipsSurveyService.ReadAdvertisedOrder(Path.Combine(survey.Root, "properties")));
        Assert.True(File.Exists(Path.Combine(survey.Root, "Norder3", "Allsky.jpg")));
    }

    [Fact]
    public async Task StartDownload_ReplacesLegacyWebpTilesBeforeDownloading()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>(), extension: ".webp");
        survey.WriteOrder(4, 20, skip: Array.Empty<int>(), extension: ".webp");
        File.WriteAllBytes(Path.Combine(survey.Root, "Norder3", "Allsky.webp"), new byte[] { 1 });

        using TileServer server = new();
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { server.BaseUrl }, survey.Root);

        Assert.True(service.GetStatus().LegacyFormat);
        Assert.True(service.StartDownload(4).Success);
        await service.WaitForJobAsync();

        HipsSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("completed", status.Job?.State);
        Assert.Equal(4, status.InstalledOrder);
        Assert.False(status.LegacyFormat);
        Assert.Empty(Directory.EnumerateFiles(survey.Root, "*.webp", SearchOption.AllDirectories));
        Assert.Equal(service.TileCountUpTo(4), server.RequestedPaths.Count);
    }

    [Fact]
    public async Task StartDownload_WithUnreachableSource_FailsAndKeepsPartialOrderUnadvertised()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>());

        using TileServer server = new(statusCode: 404);
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { server.BaseUrl }, survey.Root);

        Assert.True(service.StartDownload(4).Success);
        await service.WaitForJobAsync();

        HipsSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("failed", status.Job?.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Job?.Error));
        Assert.Equal(3, status.InstalledOrder);
        Assert.Equal(3, HipsSurveyService.ReadAdvertisedOrder(Path.Combine(survey.Root, "properties")));
    }

    [Fact]
    public void GetStatus_RemovesPropertiesThatAdvertiseAnIncompleteOrder()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, 10, skip: Array.Empty<int>());
        File.WriteAllText(Path.Combine(survey.Root, "properties"), "hips_order = 4\n");

        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { "http://127.0.0.1:1/unused" }, survey.Root);
        HipsSurveyService.SurveyStatus status = service.GetStatus();

        Assert.Null(status.InstalledOrder);
        Assert.False(File.Exists(Path.Combine(survey.Root, "properties")));
    }

    [Fact]
    public void DeleteSurvey_RemovesTheWholeFolder()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, 5, skip: Array.Empty<int>());
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { "http://127.0.0.1:1/unused" }, survey.Root);

        Assert.True(service.DeleteSurvey().Success);

        Assert.False(Directory.Exists(survey.Root));
        Assert.Null(service.GetStatus().InstalledOrder);
    }

    [Fact]
    public void DeleteSurvey_WithKeepOrder_RemovesOnlyHigherOrdersAndKeepsTheRest()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, HipsSurveyService.FullSkyTileCount(4), skip: Array.Empty<int>());
        survey.WriteOrder(5, HipsSurveyService.FullSkyTileCount(5), skip: Array.Empty<int>());
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { "http://127.0.0.1:1/unused" }, survey.Root);
        Assert.Equal(5, service.GetStatus().InstalledOrder);

        Assert.True(service.DeleteSurvey(keepOrder: 4).Success);

        Assert.True(Directory.Exists(Path.Combine(survey.Root, "Norder4")));
        Assert.False(Directory.Exists(Path.Combine(survey.Root, "Norder5")));
        Assert.Equal(4, service.GetStatus().InstalledOrder);
    }

    [Fact]
    public void DeleteSurvey_WithKeepOrderAtOrAboveInstalled_Fails()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, HipsSurveyService.FullSkyTileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, HipsSurveyService.FullSkyTileCount(4), skip: Array.Empty<int>());
        HipsSurveyService service = new(SurveyDefinition.Dss, new[] { "http://127.0.0.1:1/unused" }, survey.Root);

        Assert.False(service.DeleteSurvey(keepOrder: 4).Success);
        Assert.False(service.DeleteSurvey(keepOrder: 5).Success);
    }

    [Theory]
    [InlineData(4L, 0, 0L)]
    [InlineData(15L, 0, 11L)]
    [InlineData(16L, 1, 0L)]
    [InlineData(1024L, 4, 0L)]
    [InlineData(4L * 4096 + 31871L, 6, 31871L)]
    public void DecodeNuniq_SplitsOrderAndPixel(long nuniq, int order, long npix)
    {
        Assert.Equal((order, npix), MocCoverage.DecodeNuniq(nuniq));
    }

    [Fact]
    public void MocCoverage_ExpandsCoarseCellsAndCollapsesFineOnes()
    {
        // Order-0 cell 0 plus one order-5 cell inside base pixel 11.
        long fineNpix = (11L << 10) + 5;
        MocCoverage coverage = MocCoverage.Parse(BuildMocFits(4 + 0, (4L << 10) + fineNpix));

        Assert.Equal(64 + 1, coverage.TileCount(3));
        Assert.Equal(Enumerable.Range(0, 64).Append(11 * 64), coverage.Tiles(3));
        Assert.Equal(256 + 1, coverage.TileCount(4));
        Assert.Equal(1024 + 1, coverage.TileCount(5));
        Assert.Equal(4096 + 4, coverage.TileCount(6));
    }

    [Fact]
    public void MocCoverage_RejectsNonFitsInput()
    {
        Assert.Throws<FormatException>(() => MocCoverage.Parse(new byte[3000]));
    }

    [Fact]
    public void ConvertToJpeg_FlattensTransparentPngOnBlack()
    {
        byte[] jpeg = HipsSurveyService.ConvertToJpeg(TileServer.CreateTilePng(transparent: true), "test tile");

        Assert.True(HipsSurveyService.IsJpeg(jpeg));
        using Image<Rgb24> decoded = Image.Load<Rgb24>(jpeg);
        Assert.Equal(HipsSurveyService.TileWidth, decoded.Width);
        Rgb24 pixel = decoded[10, 10];
        Assert.True(pixel.R < 8 && pixel.G < 8 && pixel.B < 8, $"expected black, got {pixel}");
    }

    [Fact]
    public void SurveyDefinition_Find_DefaultsToDssAndRejectsUnknownIds()
    {
        Assert.Same(SurveyDefinition.Dss, SurveyDefinition.Find(null));
        Assert.Same(SurveyDefinition.Dss, SurveyDefinition.Find(""));
        Assert.Same(SurveyDefinition.Nsns, SurveyDefinition.Find("NSNS"));
        Assert.Null(SurveyDefinition.Find("panstarrs"));
        Assert.Equal("/celestia-atlas-data/surveys/nsns", SurveyDefinition.Nsns.Route);
    }

    [Fact]
    public void NsnsSingleLineProducts_AreSeparateSurveysWithTheirOwnSourceAndFolder()
    {
        Assert.Same(SurveyDefinition.NsnsHalpha, SurveyDefinition.Find("nsns-ha"));
        Assert.Same(SurveyDefinition.NsnsOiii, SurveyDefinition.Find("nsns-oiii"));
        Assert.Same(SurveyDefinition.NsnsSii, SurveyDefinition.Find("nsns-sii"));
        Assert.Equal("https://www.simg.de/nebulae3/dr0_2/halpha8", SurveyDefinition.NsnsHalpha.DefaultSourceUrls[0]);
        Assert.Equal("/celestia-atlas-data/surveys/nsns-oiii", SurveyDefinition.NsnsOiii.Route);
        Assert.Equal("TNS_NSNS_SII_SURVEY_SOURCE_URL", SurveyDefinition.NsnsSii.SourceUrlEnvironmentVariable);
        Assert.Equal(SurveyDefinition.All.Count, SurveyDefinition.All.Select(d => d.Id).Distinct().Count());
        Assert.Equal(SurveyDefinition.All.Count, HipsSurveyService.All.Count);

        string properties = HipsSurveyService.BuildPropertiesFile(SurveyDefinition.NsnsHalpha, 6, "https://example.org", DateTime.UtcNow);
        Assert.Contains("creator_did          = ivo://simg.de/P/NSNS/DR0_2/halpha8\n", properties);
        Assert.Contains("hips_service_url     = /celestia-atlas-data/surveys/nsns-ha\n", properties);
    }

    [Fact]
    public void NsnsContinuum_StopsAtOrderFiveAndLoadsRgb8()
    {
        Assert.Same(SurveyDefinition.NsnsContinuum, SurveyDefinition.Find("nsns-cont"));
        Assert.Equal("https://www.simg.de/nebulae3/dr0_2/rgb8", SurveyDefinition.NsnsContinuum.DefaultSourceUrls[0]);
        Assert.Equal(5, SurveyDefinition.NsnsContinuum.MaxOrder);
        Assert.Equal(6, SurveyDefinition.Nsns.MaxOrder);
        Assert.Equal("TNS_NSNS_CONT_SURVEY_PATH", SurveyDefinition.NsnsContinuum.PathEnvironmentVariable);

        string properties = HipsSurveyService.BuildPropertiesFile(SurveyDefinition.NsnsContinuum, 5, "https://example.org", DateTime.UtcNow);
        Assert.Contains("creator_did          = ivo://simg.de/P/NSNS/DR0_2/rgb8\n", properties);
        Assert.Contains("moc_sky_fraction     = 0.651\n", properties);
    }

    [Fact]
    public void NsnsStatus_WithoutCoverageMap_UsesExpectedCountsAndIsNotInstalled()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, 528, skip: Array.Empty<int>());
        HipsSurveyService service = new(SurveyDefinition.Nsns, new[] { "http://127.0.0.1:1/unused" }, survey.Root, Array.Empty<HipsSurveyService>());

        HipsSurveyService.SurveyStatus status = service.GetStatus();

        Assert.Equal("nsns", status.Survey);
        Assert.Null(status.InstalledOrder);
        Assert.Equal(528, status.Orders.Single(o => o.Order == 3).TileCount);
        Assert.Equal(31872, status.Orders.Single(o => o.Order == 6).TileCount);
    }

    [Fact]
    public async Task NsnsDownload_FetchesOnlyCoveredTiles_ConvertsThemAndPublishesTheOrder()
    {
        using TempSurvey survey = new();
        // Coverage: base pixel 0 only -> 64 tiles at order 3, 256 at order 4.
        byte[] moc = BuildMocFits(4);
        using TileServer server = new(mocFits: moc);
        HipsSurveyService service = new(SurveyDefinition.Nsns, new[] { server.BaseUrl }, survey.Root, Array.Empty<HipsSurveyService>());

        Assert.True(service.StartDownload(4).Success);
        await service.WaitForJobAsync();

        HipsSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("completed", status.Job?.State);
        Assert.Equal(4, status.InstalledOrder);
        Assert.Equal(64 + 256, status.Job!.TilesTotal);
        Assert.Equal(64 + 256, status.Job.TilesDone);
        Assert.Equal(256, status.Orders.Single(o => o.Order == 4).TileCount);
        Assert.Contains("/Moc.fits", server.RequestedPaths);
        Assert.All(server.RequestedPaths.Where(p => p != "/Moc.fits"), p => Assert.EndsWith(".png", p));
        Assert.DoesNotContain("/Norder3/Dir0/Npix64.png", server.RequestedPaths);
        Assert.Empty(Directory.EnumerateFiles(survey.Root, "*.png", SearchOption.AllDirectories));
        Assert.True(HipsSurveyService.IsJpeg(File.ReadAllBytes(survey.TilePath(4, 255))));
        Assert.True(File.Exists(Path.Combine(survey.Root, "Moc.fits")));
        Assert.True(File.Exists(Path.Combine(survey.Root, "Norder3", "Allsky.jpg")));

        string properties = File.ReadAllText(Path.Combine(survey.Root, "properties"));
        Assert.Contains("hips_order           = 4\n", properties);
        Assert.Contains("hips_service_url     = /celestia-atlas-data/surveys/nsns\n", properties);
        Assert.Contains("CC-BY-NC-SA", properties);
    }

    [Fact]
    public async Task StartDownload_IsRefusedWhileAnotherSurveyDownloads()
    {
        using TempSurvey dssRoot = new();
        using TempSurvey nsnsRoot = new();
        using TileServer server = new(delayMs: 200);
        List<HipsSurveyService> services = new();
        HipsSurveyService dss = new(SurveyDefinition.Dss, new[] { server.BaseUrl }, dssRoot.Root, services);
        HipsSurveyService nsns = new(SurveyDefinition.Nsns, new[] { server.BaseUrl }, nsnsRoot.Root, services);
        services.Add(dss);
        services.Add(nsns);

        Assert.True(dss.StartDownload(4).Success);
        HipsSurveyService.OperationResult second = nsns.StartDownload(4);

        Assert.False(second.Success);
        Assert.Contains("DSS", second.Error);
        Assert.True(dss.CancelDownload().Success);
        await dss.WaitForJobAsync();
    }

    /// <summary>Minimal MOC 2.0 FITS file: empty primary HDU and a 1J NUNIQ binary table.</summary>
    private static byte[] BuildMocFits(params long[] nuniqs)
    {
        static byte[] Header(params string[] cards)
        {
            string text = string.Concat(cards.Append("END").Select(c => c.PadRight(80)));
            int size = (text.Length + 2879) / 2880 * 2880;
            return System.Text.Encoding.ASCII.GetBytes(text.PadRight(size));
        }

        byte[] primary = Header("SIMPLE  =                    T", "BITPIX  =                    8", "NAXIS   =                    0", "EXTEND  =                    T");
        byte[] table = Header(
            "XTENSION= 'BINTABLE'",
            "BITPIX  =                    8",
            "NAXIS   =                    2",
            "NAXIS1  =                    4",
            $"NAXIS2  = {nuniqs.Length,20}",
            "PCOUNT  =                    0",
            "GCOUNT  =                    1",
            "TFIELDS =                    1",
            "TFORM1  = '1J      '",
            "TTYPE1  = 'UNIQ    '",
            "ORDERING= 'NUNIQ   '");
        byte[] data = new byte[(nuniqs.Length * 4 + 2879) / 2880 * 2880];
        for (int i = 0; i < nuniqs.Length; i++)
        {
            int value = (int)nuniqs[i];
            data[i * 4] = (byte)(value >> 24);
            data[i * 4 + 1] = (byte)(value >> 16);
            data[i * 4 + 2] = (byte)(value >> 8);
            data[i * 4 + 3] = (byte)value;
        }

        return primary.Concat(table).Concat(data).ToArray();
    }

    /// <summary>Temporary survey folder pre-filled with tiny but valid JPEG tiles.</summary>
    private sealed class TempSurvey : IDisposable
    {
        private static readonly byte[] TinyJpeg = CreateTinyJpeg();

        public TempSurvey()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tns-hips-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string TilePath(int order, int npix) => Path.Combine(Root, HipsSurveyService.TileRelativePath(order, npix));

        public void WriteOrder(int order, int count, int[] skip, string extension = ".jpg")
        {
            for (int npix = 0; npix < count; npix++)
            {
                if (Array.IndexOf(skip, npix) >= 0)
                {
                    continue;
                }

                string path = Path.Combine(Root, HipsSurveyService.TileRelativePath(order, npix, extension));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, TinyJpeg);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }

        private static byte[] CreateTinyJpeg()
        {
            using Image<Rgb24> image = new(2, 2, new Rgb24(10, 20, 30));
            using MemoryStream stream = new();
            image.SaveAsJpeg(stream);
            return stream.ToArray();
        }
    }

    /// <summary>Localhost HTTP server answering tile requests with a 512 px JPEG or PNG and Moc.fits with the given map.</summary>
    private sealed class TileServer : IDisposable
    {
        private static readonly byte[] TileJpeg = CreateTileJpeg();
        private static readonly byte[] TilePng = CreateTilePng(transparent: false);
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();

        private readonly byte[]? mocFits;
        private readonly int delayMs;

        public TileServer(int statusCode = 200, byte[]? mocFits = null, int delayMs = 0)
        {
            this.mocFits = mocFits;
            this.delayMs = delayMs;
            int port = FindFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/survey";
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(() => ServeAsync(statusCode));
        }

        public string BaseUrl { get; }

        public ConcurrentBag<string> RequestedPaths { get; } = new();

        public void Dispose()
        {
            stop.Cancel();
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // best effort
            }
        }

        private async Task ServeAsync(int statusCode)
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                string path = context.Request.Url!.AbsolutePath;
                RequestedPaths.Add(path.Substring("/survey".Length));
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }

                    byte[]? body = path.EndsWith("/Moc.fits", StringComparison.Ordinal) ? mocFits
                        : path.EndsWith(".png", StringComparison.Ordinal) ? TilePng
                        : TileJpeg;
                    context.Response.StatusCode = body == null ? 404 : statusCode;
                    if (body != null && statusCode == 200)
                    {
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    }
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        private static int FindFreePort()
        {
            var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        public static byte[] CreateTilePng(bool transparent)
        {
            Rgba32 colour = transparent ? new Rgba32(250, 250, 250, 0) : new Rgba32(200, 40, 60, 255);
            using Image<Rgba32> image = new(HipsSurveyService.TileWidth, HipsSurveyService.TileWidth, colour);
            using MemoryStream stream = new();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        private static byte[] CreateTileJpeg()
        {
            using Image<Rgb24> image = new(HipsSurveyService.TileWidth, HipsSurveyService.TileWidth, new Rgb24(40, 50, 60));
            using MemoryStream stream = new();
            image.SaveAsJpeg(stream);
            return stream.ToArray();
        }
    }
}
