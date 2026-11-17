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

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
