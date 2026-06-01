using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class StellariumLandscapeServiceTests
{
    [Fact]
    public void SanitizeFolderName_RemovesUnsafeCharacters()
    {
        string sanitized = StellariumLandscapeService.SanitizeFolderName("  My Backyard! #1 / 2026 ");

        Assert.Equal("my_backyard_1_2026", sanitized);
    }

    [Fact]
    public void BuildPropertiesFile_IncludesRequiredKeysAndFolderName()
    {
        DateTime releaseDate = new DateTime(2026, 06, 01, 12, 30, 00, DateTimeKind.Utc);

        string properties = StellariumLandscapeService.BuildPropertiesFile(
            "My Backyard",
            "my_backyard",
            releaseDate,
            "0123456789abcdef0123456789abcdef");

        Assert.Contains("hips_order = 0", properties);
        Assert.Contains("hips_order_min = 0", properties);
        Assert.Contains("hips_tile_width = 512", properties);
        Assert.Contains("hips_tile_format = webp", properties);
        Assert.Contains("dataproduct_type = image", properties);
        Assert.Contains("obs_title = My Backyard", properties);
        Assert.Contains("hips_service_url = /stellarium-data/landscapes/my_backyard", properties);
        Assert.Contains("hips_release_date = 2026-06-01T12:30:00Z", properties);
        Assert.Contains("source_md5 = 0123456789abcdef0123456789abcdef", properties);
        Assert.Contains("type = landscape", properties);
    }

    [Fact]
    public void BuildLandscapeZip_CreatesExpectedStructure()
    {
        const string folder = "my_backyard";

        byte[] allsky = CreateBytes(16);
        var tiles = Enumerable.Range(0, 12).Select(i => CreateBytes(32 + i)).ToArray();

        byte[] zip = StellariumLandscapeService.BuildLandscapeZip(
            folder,
            "hips_order = 0\n",
            "Description\n",
            allsky,
            tiles);

        using MemoryStream ms = new(zip, writable: false);
        using ZipArchive archive = new(ms, ZipArchiveMode.Read);

        string[] expectedEntries =
        {
            $"{folder}/properties",
            $"{folder}/description.en.utf8",
            $"{folder}/Norder0/Allsky.webp",
            $"{folder}/Norder0/Dir0/Npix0.webp",
            $"{folder}/Norder0/Dir0/Npix1.webp",
            $"{folder}/Norder0/Dir0/Npix2.webp",
            $"{folder}/Norder0/Dir0/Npix3.webp",
            $"{folder}/Norder0/Dir0/Npix4.webp",
            $"{folder}/Norder0/Dir0/Npix5.webp",
            $"{folder}/Norder0/Dir0/Npix6.webp",
            $"{folder}/Norder0/Dir0/Npix7.webp",
            $"{folder}/Norder0/Dir0/Npix8.webp",
            $"{folder}/Norder0/Dir0/Npix9.webp",
            $"{folder}/Norder0/Dir0/Npix10.webp",
            $"{folder}/Norder0/Dir0/Npix11.webp"
        };

        var actualEntries = archive.Entries.Select(e => e.FullName).OrderBy(s => s).ToArray();
        var expectedSorted = expectedEntries.OrderBy(s => s).ToArray();

        Assert.Equal(expectedSorted.Length, actualEntries.Length);
        Assert.Equal(expectedSorted, actualEntries);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1600, 1000)]
    [InlineData(3000, 1000)]
    public void IsValidEquirectangularRatio_InvalidRatiosReturnFalse(int width, int height)
    {
        bool isValid = StellariumLandscapeService.IsValidEquirectangularRatio(width, height);

        Assert.False(isValid);
    }

    private static byte[] CreateBytes(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}
