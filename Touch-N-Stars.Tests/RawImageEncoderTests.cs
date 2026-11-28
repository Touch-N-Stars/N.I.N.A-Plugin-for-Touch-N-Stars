using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class RawImageEncoderTests
{
    [Fact]
    public void Histogram_CountsEveryPixel()
    {
        ushort[] plane = { 0, 5, 5, 65535, 100 };
        uint[] histogram = RawImageEncoder.BuildHistogram(plane);

        Assert.Equal(65536, histogram.Length);
        Assert.Equal(2u, histogram[5]);
        Assert.Equal(1u, histogram[65535]);
        Assert.Equal((long)plane.Length, histogram.Sum(c => (long)c));
    }

    [Fact]
    public void Stats_MatchDirectComputation()
    {
        ushort[] plane = { 10, 12, 12, 13, 20, 100, 4095 };
        var stats = RawImageEncoder.ComputeStats(RawImageEncoder.BuildHistogram(plane), 12);

        Assert.Equal(10, stats.Min);
        Assert.Equal(4095, stats.Max);
        Assert.Equal(13, stats.Median);
        // |v - 13| = 3,1,1,0,7,87,4082 -> sorted 0,1,1,3,7,87,4082 -> median 3
        Assert.Equal(3, stats.Mad);
        Assert.Equal(plane.Average(v => (double)v), stats.Mean, 9);
        Assert.Equal(0.0, stats.ClipLow);
        Assert.Equal(1.0 / 7, stats.ClipHigh, 9);
    }

    [Fact]
    public void Stats_EmptyHistogram_IsZero()
    {
        var stats = RawImageEncoder.ComputeStats(new uint[RawImageEncoder.HistogramBins], 16);
        Assert.Equal(0, stats.Median);
        Assert.Equal(0, stats.Max);
    }

    [Theory]
    [InlineData("RGGB", 1, 2, 3, 4, 1, 3, 4)]   // R G / G B
    [InlineData("BGGR", 1, 2, 3, 4, 4, 3, 1)]   // B G / G R
    [InlineData("GRBG", 1, 2, 3, 4, 2, 3, 3)]   // G R / B G
    [InlineData("GBRG", 1, 2, 3, 4, 3, 3, 2)]   // G B / R G
    public void Superpixel_PicksChannelsByPattern(string pattern, int p0, int p1, int p2, int p3, int r, int g, int b)
    {
        // Single 2x2 cell: row-major p0 p1 / p2 p3. Green is the rounded mean of both greens.
        ushort[] cell = { (ushort)p0, (ushort)p1, (ushort)p2, (ushort)p3 };
        ushort[][] rgb = RawImageEncoder.Superpixel(cell, 2, 2, pattern, out int w, out int h);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(r, rgb[0][0]);
        Assert.Equal(g, rgb[1][0]);
        Assert.Equal(b, rgb[2][0]);
    }

    [Fact]
    public void Superpixel_RejectsNonBayerPattern()
    {
        Assert.Throws<ArgumentException>(() =>
            RawImageEncoder.Superpixel(new ushort[4], 2, 2, "Monochrome", out _, out _));
    }

    [Fact]
    public void BoxDownsample_AveragesBlocks()
    {
        // 4x2 image, maxDim 2 -> factor 2 -> 2x1
        ushort[] plane = { 0, 2, 10, 10, 4, 6, 20, 30 };
        ushort[] result = RawImageEncoder.BoxDownsample(plane, 4, 2, 2, out int w, out int h);

        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(new ushort[] { 3, 18 }, result);
    }

    [Fact]
    public void BoxDownsample_KeepsSmallImagesUntouched()
    {
        ushort[] plane = { 1, 2, 3, 4 };
        ushort[] result = RawImageEncoder.BoxDownsample(plane, 2, 2, 1600, out int w, out int h);
        Assert.Same(plane, result);
        Assert.Equal(2, w);
        Assert.Equal(2, h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encode_ProducesParsableContainer(bool gzip)
    {
        int width = 8, height = 4;
        ushort[] plane = Enumerable.Range(0, width * height).Select(i => (ushort)(i * 100)).ToArray();

        byte[] payload = RawImageEncoder.Encode(new[] { plane }, width, height, 16, 4, "test-1",
            width, height, isBayered: false, bayerPattern: null, gzip);
        byte[] bytes = gzip ? Gunzip(payload) : payload;

        int headerLength = BitConverter.ToInt32(bytes, 0);
        JObject header = JObject.Parse(Encoding.UTF8.GetString(bytes, 4, headerLength));
        Assert.Equal("test-1", (string)header["imageId"]);
        Assert.Equal(4, (int)header["width"]);
        Assert.Equal(2, (int)header["height"]);
        Assert.Equal(1, (int)header["channels"]);

        int offset = 4 + headerLength;
        offset += (4 - offset % 4) % 4;
        long total = 0;
        for (int i = 0; i < RawImageEncoder.HistogramBins; i++)
        {
            total += BitConverter.ToUInt32(bytes, offset + i * 4);
        }
        // The histogram covers the full-resolution data, not the downsampled preview.
        Assert.Equal(width * height, total);

        offset += RawImageEncoder.HistogramBins * 4;
        Assert.Equal(offset + 4 * 2 * 2, bytes.Length);
        // First preview pixel: mean of (0,100,800,900) = 450
        Assert.Equal(450, BitConverter.ToUInt16(bytes, offset));
    }

    [Fact]
    public void Encode_RaisesBitDepthWhenDataExceedsIt()
    {
        ushort[] plane = { 100, 60000 };
        byte[] bytes = RawImageEncoder.Encode(new[] { plane }, 2, 1, 12, 1600, "x", 2, 1, false, null, gzip: false);
        int headerLength = BitConverter.ToInt32(bytes, 0);
        JObject header = JObject.Parse(Encoding.UTF8.GetString(bytes, 4, headerLength));
        Assert.Equal(16, (int)header["bitDepth"]);
    }

    // Mono frame with distinct values: pixel (x, y) = 100 * y + x.
    private static ushort[] Ramp(int width, int height)
    {
        var plane = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                plane[y * width + x] = (ushort)(100 * y + x);
        return plane;
    }

    [Fact]
    public void RegionStats_Mono_MatchDirectComputation()
    {
        ushort[] plane = Ramp(6, 4);
        plane[1 * 6 + 2] = 4095; // one clipped pixel inside the region

        var result = RawImageEncoder.ComputeRegionStats(new[] { plane }, 6, 4, 12, null, 1, 1, 3, 2, "img");

        // Region x 1..3, y 1..2: 101 4095 103 / 201 202 203
        ushort[] inside = { 101, 4095, 103, 201, 202, 203 };
        var s = Assert.Single(result.Stats);
        Assert.Equal("L", s.Channel);
        Assert.Equal(6, s.Count);
        Assert.Equal(101, s.Min);
        Assert.Equal(4095, s.Max);
        Assert.Equal(inside.Average(v => (double)v), s.Mean, 9);
        Assert.Equal(201, s.Median); // lower median of 101 103 201 202 203 4095
        Assert.Equal(2, s.Mad);      // deviations from 201: 0 1 2 98 100 3894, lower median 2
        Assert.Equal(1, s.CountAtMin);
        Assert.Equal(1, s.CountAtMax);
        Assert.Equal(1.0 / 6, s.ClipHigh, 9);
        Assert.Equal("img", result.ImageId);
        Assert.Equal(12, result.BitDepth);
        Assert.False(result.IsBayered);
        Assert.Equal((1, 1, 3, 2), (result.Region.X, result.Region.Y, result.Region.W, result.Region.H));
    }

    [Fact]
    public void RegionStats_CountsRepeatedExtremes()
    {
        ushort[] plane = { 7, 7, 9, 9, 9, 8 };
        var s = Assert.Single(RawImageEncoder.ComputeRegionStats(new[] { plane }, 3, 2, 16, null, 0, 0, 3, 2, "img").Stats);
        Assert.Equal(2, s.CountAtMin);
        Assert.Equal(3, s.CountAtMax);
    }

    [Fact]
    public void RegionStats_ClampsToImage()
    {
        var result = RawImageEncoder.ComputeRegionStats(new[] { Ramp(6, 4) }, 6, 4, 16, null, -2, 2, 5, 10, "img");

        Assert.Equal((0, 2, 3, 2), (result.Region.X, result.Region.Y, result.Region.W, result.Region.H));
        var s = Assert.Single(result.Stats);
        Assert.Equal(6, s.Count);
        Assert.Equal(200, s.Min);
        Assert.Equal(302, s.Max);
    }

    [Fact]
    public void RegionStats_HugeSizeClampsInsteadOfOverflowing()
    {
        // x + w overflows int; added in 64 bit it clamps to the image like any region past the edge.
        var result = RawImageEncoder.ComputeRegionStats(new[] { Ramp(6, 4) }, 6, 4, 16, null, 1, 0, int.MaxValue, int.MaxValue, "img");

        Assert.Equal((1, 0, 5, 4), (result.Region.X, result.Region.Y, result.Region.W, result.Region.H));
        Assert.Equal(20, Assert.Single(result.Stats).Count);
    }

    [Theory]
    [InlineData(6, 0, 1, 1)]   // right of the image
    [InlineData(0, -5, 2, 5)]  // ends exactly at the top edge
    [InlineData(0, 0, 0, 2)]   // zero width
    [InlineData(0, 0, 2, -1)]  // negative height
    public void RegionStats_RejectsRegionsWithoutPixels(int x, int y, int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RawImageEncoder.ComputeRegionStats(new[] { Ramp(6, 4) }, 6, 4, 16, null, x, y, w, h, "img"));
    }

    [Theory]
    [InlineData("RGGB")]
    [InlineData("BGGR")]
    [InlineData("GRBG")]
    [InlineData("GBRG")]
    public void RegionStats_Bayer_SplitsSamplesByCfaSite(string pattern)
    {
        // 4x4 CFA frame: every sample carries its site's color in the value (R=1000s, G=2000s,
        // B=3000s), so a sample counted in the wrong channel breaks that channel's range.
        const int width = 4, height = 4;
        var plane = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                char site = pattern[(y % 2) * 2 + x % 2];
                int band = site == 'R' ? 1000 : site == 'G' ? 2000 : 3000;
                plane[y * width + x] = (ushort)(band + y * width + x);
            }

        var result = RawImageEncoder.ComputeRegionStats(new[] { plane }, width, height, 16, pattern, 0, 0, 4, 4, "img");

        Assert.True(result.IsBayered);
        Assert.Equal(pattern, result.BayerPattern);
        Assert.Equal(new[] { "R", "G", "B" }, result.Stats.Select(s => s.Channel));
        Assert.Equal(new long[] { 4, 8, 4 }, result.Stats.Select(s => s.Count)); // greens pooled
        for (int c = 0; c < 3; c++)
        {
            int band = 1000 * (c + 1);
            Assert.InRange(result.Stats[c].Min, band, band + 15);
            Assert.InRange(result.Stats[c].Max, band, band + 15);
        }
    }

    [Fact]
    public void RegionStats_Bayer_FollowsAbsolutePhaseForOddOrigin()
    {
        // RGGB, region starting at (1, 1): its first sample sits on a B site, not an R site.
        var plane = new ushort[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                plane[y * 4 + x] = (ushort)((y % 2, x % 2) switch { (0, 0) => 1, (1, 1) => 3, _ => 2 });

        var result = RawImageEncoder.ComputeRegionStats(new[] { plane }, 4, 4, 16, "RGGB", 1, 1, 1, 1, "img");

        Assert.Equal(new long[] { 0, 0, 1 }, result.Stats.Select(s => s.Count));
        Assert.Equal(3, result.Stats[2].Max);
        Assert.Equal(0, result.Stats[0].CountAtMax); // empty channel stays zero
    }

    [Fact]
    public void RegionStats_Bayer_ShowsSingleClippedGreenThatSuperpixelHides()
    {
        // RGGB 2x2 cell with one green site clipped.
        ushort[] cell = { 1000, 65535, 1200, 900 };

        ushort[][] superpixel = RawImageEncoder.Superpixel(cell, 2, 2, "RGGB", out _, out _);
        Assert.True(superpixel[1][0] < 65535); // the averaged green hides the clipped site

        var green = RawImageEncoder.ComputeRegionStats(new[] { cell }, 2, 2, 16, "RGGB", 0, 0, 2, 2, "img").Stats[1];
        Assert.Equal(2, green.Count);
        Assert.Equal(65535, green.Max);
        Assert.Equal(1, green.CountAtMax);
        Assert.Equal(0.5, green.ClipHigh, 9);
    }

    [Fact]
    public void RegionStats_SeparatePlanes_OneRowPerPlane()
    {
        ushort[] r = { 1, 2, 3, 4 }, g = { 10, 20, 30, 40 }, b = { 100, 200, 300, 400 };

        var result = RawImageEncoder.ComputeRegionStats(new[] { r, g, b }, 2, 2, 16, null, 1, 0, 1, 2, "img");

        Assert.False(result.IsBayered);
        Assert.Equal(new[] { "R", "G", "B" }, result.Stats.Select(s => s.Channel));
        Assert.Equal(new[] { 2, 20, 200 }, result.Stats.Select(s => s.Min));
        Assert.Equal(new[] { 4, 40, 400 }, result.Stats.Select(s => s.Max));
    }

    [Fact]
    public void RegionStats_RaisesBitDepthWhenDataExceedsIt()
    {
        ushort[] plane = { 100, 60000 };
        var result = RawImageEncoder.ComputeRegionStats(new[] { plane }, 2, 1, 12, null, 0, 0, 2, 1, "img");
        Assert.Equal(16, result.BitDepth);
    }

    [Fact]
    public void RegionStats_DarkRegionKeepsTheFrameBitDepth()
    {
        // A camera reporting 14 bit whose driver scales to 16 bit: the frame has data above
        // 16383, the dark top row does not. The region must still be measured against 16 bit,
        // like the whole-image payload, so its 16383 is not reported as clipped.
        ushort[] plane = { 16383, 1000, 1000, 60000 };
        int frameBitDepth = RawImageEncoder.FrameBitDepth(new[] { plane }, 14);
        var result = RawImageEncoder.ComputeRegionStats(new[] { plane }, 2, 2, frameBitDepth, null, 0, 0, 2, 1, "img");

        Assert.Equal(16, frameBitDepth);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal(0, result.Stats[0].ClipHigh);
    }

    [Theory]
    [InlineData(14, 16383, 14)] // native 14-bit data at full scale
    [InlineData(14, 16384, 16)] // scaled to 16 bit by the driver
    [InlineData(12, 0, 12)]
    [InlineData(16, 65535, 16)]
    [InlineData(20, 100, 16)]   // declared depth is clamped to 1..16, as in Encode
    public void FrameBitDepth_RaisesOnlyWhenDataExceedsTheDeclaredRange(int declared, int brightest, int expected)
    {
        ushort[] plane = { 0, 7, (ushort)brightest, 3 };
        Assert.Equal(expected, RawImageEncoder.FrameBitDepth(new[] { new ushort[] { 1, 2 }, plane }, declared));
    }

    [Fact]
    public void RegionStats_RejectsBayerPatternWithSeveralPlanes()
    {
        // A pattern describes a single CFA plane; with three planes the caller mixed up formats.
        var planes = new[] { new ushort[4], new ushort[4], new ushort[4] };
        Assert.Throws<ArgumentException>(() =>
            RawImageEncoder.ComputeRegionStats(planes, 2, 2, 16, "RGGB", 0, 0, 2, 2, "img"));
    }

    [Theory]
    [InlineData("RRGB")]
    [InlineData("RGB")]
    [InlineData("")]
    public void RegionStats_RejectsUnsupportedBayerPattern(string pattern)
    {
        Assert.Throws<ArgumentException>(() =>
            RawImageEncoder.ComputeRegionStats(new[] { new ushort[4] }, 2, 2, 16, pattern, 0, 0, 2, 2, "img"));
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
