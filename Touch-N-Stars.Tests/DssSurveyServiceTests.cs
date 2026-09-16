using System;
using System.Collections.Concurrent;
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

public class DssSurveyServiceTests
{
    [Theory]
    [InlineData(3, 768)]
    [InlineData(4, 3072)]
    [InlineData(5, 12288)]
    [InlineData(6, 49152)]
    [InlineData(7, 196608)]
    public void TileCount_FollowsHipsFormula(int order, int expected)
    {
        Assert.Equal(expected, DssSurveyService.TileCount(order));
    }

    [Fact]
    public void TileCountUpTo_SumsFromMinOrder()
    {
        Assert.Equal(768 + 3072, DssSurveyService.TileCountUpTo(4));
        Assert.Equal(768 + 3072 + 12288, DssSurveyService.TileCountUpTo(5));
    }

    [Theory]
    [InlineData(0, "Dir0")]
    [InlineData(9999, "Dir0")]
    [InlineData(10000, "Dir10000")]
    [InlineData(23456, "Dir20000")]
    [InlineData(196607, "Dir190000")]
    public void TileDirectoryName_GroupsBy10000(int npix, string expected)
    {
        Assert.Equal(expected, DssSurveyService.TileDirectoryName(npix));
    }

    [Fact]
    public void TileRelativePath_And_TileUrl_UseHipsLayout()
    {
        Assert.Equal(
            Path.Combine("Norder5", "Dir10000", "Npix12287.jpg"),
            DssSurveyService.TileRelativePath(5, 12287));
        Assert.Equal(
            "https://example.org/DSSColor/Norder5/Dir10000/Npix12287.jpg",
            DssSurveyService.TileUrl("https://example.org/DSSColor/", 5, 12287));
    }

    [Fact]
    public void ResolveInstalledOrder_StopsAtFirstIncompleteOrder()
    {
        var orders = new[]
        {
            new DssSurveyService.OrderState { Order = 3, Complete = true },
            new DssSurveyService.OrderState { Order = 4, Complete = true },
            new DssSurveyService.OrderState { Order = 5, Complete = false },
            new DssSurveyService.OrderState { Order = 6, Complete = true }
        };

        Assert.Equal(4, DssSurveyService.ResolveInstalledOrder(orders));
        Assert.Null(DssSurveyService.ResolveInstalledOrder(new[]
        {
            new DssSurveyService.OrderState { Order = 3, Complete = false }
        }));
    }

    [Fact]
    public void BuildPropertiesFile_AdvertisesInstalledOrderAndLocalServiceUrl()
    {
        string properties = DssSurveyService.BuildPropertiesFile(
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
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, 10, skip: new[] { 3 });
        File.WriteAllBytes(survey.TilePath(4, 3), Array.Empty<byte>());

        DssSurveyService.SurveyInventory inventory = DssSurveyService.ScanInventory(survey.Root);

        DssSurveyService.OrderState order3 = inventory.Orders.Single(o => o.Order == 3);
        DssSurveyService.OrderState order4 = inventory.Orders.Single(o => o.Order == 4);
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
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>(), extension: ".webp");
        File.WriteAllBytes(Path.Combine(survey.Root, "Norder3", "Allsky.webp"), new byte[] { 1 });

        DssSurveyService.SurveyInventory inventory = DssSurveyService.ScanInventory(survey.Root);

        Assert.True(inventory.HasLegacyTiles);
        Assert.Null(inventory.InstalledOrder);
        Assert.False(inventory.HasAllsky);
        Assert.Equal(0, inventory.Orders.Single(o => o.Order == 3).TilesPresent);
    }

    [Fact]
    public void ScanInventory_OnMissingRoot_ReportsNothingInstalled()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tns-dss-missing-{Guid.NewGuid():N}");

        DssSurveyService.SurveyInventory inventory = DssSurveyService.ScanInventory(missing);

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
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, DssSurveyService.TileCount(4), skip: missingOrder4);

        using TileServer server = new();
        DssSurveyService service = new(new[] { server.BaseUrl }, survey.Root);

        DssSurveyService.OperationResult start = service.StartDownload(4);
        Assert.True(start.Success, start.Error);

        await service.WaitForJobAsync();

        DssSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("completed", status.Job?.State);
        Assert.Equal(4, status.InstalledOrder);
        Assert.True(status.HasAllsky);
        Assert.Equal(DssSurveyService.TileCountUpTo(4), status.Job!.TilesDone);
        Assert.Equal(
            missingOrder4.Select(n => $"/Norder4/{DssSurveyService.TileDirectoryName(n)}/Npix{n}.jpg").OrderBy(x => x),
            server.RequestedPaths.OrderBy(x => x));

        foreach (int npix in missingOrder4)
        {
            Assert.True(new FileInfo(survey.TilePath(4, npix)).Length > 0);
        }

        Assert.Equal(4, DssSurveyService.ReadAdvertisedOrder(Path.Combine(survey.Root, "properties")));
        Assert.True(File.Exists(Path.Combine(survey.Root, "Norder3", "Allsky.jpg")));
    }

    [Fact]
    public async Task StartDownload_ReplacesLegacyWebpTilesBeforeDownloading()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>(), extension: ".webp");
        survey.WriteOrder(4, 20, skip: Array.Empty<int>(), extension: ".webp");
        File.WriteAllBytes(Path.Combine(survey.Root, "Norder3", "Allsky.webp"), new byte[] { 1 });

        using TileServer server = new();
        DssSurveyService service = new(new[] { server.BaseUrl }, survey.Root);

        Assert.True(service.GetStatus().LegacyFormat);
        Assert.True(service.StartDownload(4).Success);
        await service.WaitForJobAsync();

        DssSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("completed", status.Job?.State);
        Assert.Equal(4, status.InstalledOrder);
        Assert.False(status.LegacyFormat);
        Assert.Empty(Directory.EnumerateFiles(survey.Root, "*.webp", SearchOption.AllDirectories));
        Assert.Equal(DssSurveyService.TileCountUpTo(4), server.RequestedPaths.Count);
    }

    [Fact]
    public async Task StartDownload_WithUnreachableSource_FailsAndKeepsPartialOrderUnadvertised()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>());

        using TileServer server = new(statusCode: 404);
        DssSurveyService service = new(new[] { server.BaseUrl }, survey.Root);

        Assert.True(service.StartDownload(4).Success);
        await service.WaitForJobAsync();

        DssSurveyService.SurveyStatus status = service.GetStatus();
        Assert.Equal("failed", status.Job?.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Job?.Error));
        Assert.Equal(3, status.InstalledOrder);
        Assert.Equal(3, DssSurveyService.ReadAdvertisedOrder(Path.Combine(survey.Root, "properties")));
    }

    [Fact]
    public void GetStatus_RemovesPropertiesThatAdvertiseAnIncompleteOrder()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, 10, skip: Array.Empty<int>());
        File.WriteAllText(Path.Combine(survey.Root, "properties"), "hips_order = 4\n");

        DssSurveyService service = new(new[] { "http://127.0.0.1:1/unused" }, survey.Root);
        DssSurveyService.SurveyStatus status = service.GetStatus();

        Assert.Null(status.InstalledOrder);
        Assert.False(File.Exists(Path.Combine(survey.Root, "properties")));
    }

    [Fact]
    public void DeleteSurvey_RemovesTheWholeFolder()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, 5, skip: Array.Empty<int>());
        DssSurveyService service = new(new[] { "http://127.0.0.1:1/unused" }, survey.Root);

        Assert.True(service.DeleteSurvey().Success);

        Assert.False(Directory.Exists(survey.Root));
        Assert.Null(service.GetStatus().InstalledOrder);
    }

    [Fact]
    public void DeleteSurvey_WithKeepOrder_RemovesOnlyHigherOrdersAndKeepsTheRest()
    {
        using TempSurvey survey = new();
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, DssSurveyService.TileCount(4), skip: Array.Empty<int>());
        survey.WriteOrder(5, DssSurveyService.TileCount(5), skip: Array.Empty<int>());
        DssSurveyService service = new(new[] { "http://127.0.0.1:1/unused" }, survey.Root);
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
        survey.WriteOrder(3, DssSurveyService.TileCount(3), skip: Array.Empty<int>());
        survey.WriteOrder(4, DssSurveyService.TileCount(4), skip: Array.Empty<int>());
        DssSurveyService service = new(new[] { "http://127.0.0.1:1/unused" }, survey.Root);

        Assert.False(service.DeleteSurvey(keepOrder: 4).Success);
        Assert.False(service.DeleteSurvey(keepOrder: 5).Success);
    }

    /// <summary>Temporary survey folder pre-filled with tiny but valid JPEG tiles.</summary>
    private sealed class TempSurvey : IDisposable
    {
        private static readonly byte[] TinyJpeg = CreateTinyJpeg();

        public TempSurvey()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tns-dss-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string TilePath(int order, int npix) => Path.Combine(Root, DssSurveyService.TileRelativePath(order, npix));

        public void WriteOrder(int order, int count, int[] skip, string extension = ".jpg")
        {
            for (int npix = 0; npix < count; npix++)
            {
                if (Array.IndexOf(skip, npix) >= 0)
                {
                    continue;
                }

                string path = Path.Combine(Root, DssSurveyService.TileRelativePath(order, npix, extension));
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

    /// <summary>Localhost HTTP server answering every tile request with a 512 px JPEG.</summary>
    private sealed class TileServer : IDisposable
    {
        private static readonly byte[] TileJpeg = CreateTileJpeg();
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();

        public TileServer(int statusCode = 200)
        {
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
                    context.Response.StatusCode = statusCode;
                    if (statusCode == 200)
                    {
                        context.Response.ContentType = "image/jpeg";
                        await context.Response.OutputStream.WriteAsync(TileJpeg).ConfigureAwait(false);
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

        private static byte[] CreateTileJpeg()
        {
            using Image<Rgb24> image = new(DssSurveyService.TileWidth, DssSurveyService.TileWidth, new Rgb24(40, 50, 60));
            using MemoryStream stream = new();
            image.SaveAsJpeg(stream);
            return stream.ToArray();
        }
    }
}
