using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace TouchNStars.Server.Services {

    /// <summary>
    /// Pixel math behind /api/raw-image: full-resolution histogram and statistics, superpixel
    /// debayer, box downsampling and the binary container the app parses.
    ///
    /// Deliberately free of NINA types so it can be unit-tested with synthetic arrays;
    /// RawImageService adapts IImageData to the plain ushort[] planes used here.
    /// </summary>
    public static class RawImageEncoder {
        public const int FormatVersion = 1;
        public const int HistogramBins = ushort.MaxValue + 1;

        public sealed class ChannelStats {
            [JsonProperty("min")] public int Min { get; set; }
            [JsonProperty("max")] public int Max { get; set; }
            [JsonProperty("mean")] public double Mean { get; set; }
            [JsonProperty("median")] public int Median { get; set; }
            [JsonProperty("mad")] public int Mad { get; set; }
            // Fractions (0..1) of pixels sitting at the bottom / at or above the top of the range.
            [JsonProperty("clipLow")] public double ClipLow { get; set; }
            [JsonProperty("clipHigh")] public double ClipHigh { get; set; }
        }

        public sealed class Header {
            [JsonProperty("version")] public int Version { get; set; } = FormatVersion;
            [JsonProperty("imageId")] public string ImageId { get; set; }
            [JsonProperty("width")] public int Width { get; set; }
            [JsonProperty("height")] public int Height { get; set; }
            [JsonProperty("sourceWidth")] public int SourceWidth { get; set; }
            [JsonProperty("sourceHeight")] public int SourceHeight { get; set; }
            [JsonProperty("channels")] public int Channels { get; set; }
            [JsonProperty("bitDepth")] public int BitDepth { get; set; }
            [JsonProperty("isBayered")] public bool IsBayered { get; set; }
            [JsonProperty("bayerPattern")] public string BayerPattern { get; set; }
            [JsonProperty("stats")] public ChannelStats[] Stats { get; set; }
        }

        public static uint[] BuildHistogram(ushort[] plane) {
            var histogram = new uint[HistogramBins];
            for (int i = 0; i < plane.Length; i++) {
                histogram[plane[i]]++;
            }
            return histogram;
        }

        /// <summary>
        /// Exact statistics from a 65536-bin histogram. Median and MAD are integer ADU like
        /// NINA's own ImageStatistics, so the app's auto stretch matches NINA's.
        /// </summary>
        public static ChannelStats ComputeStats(uint[] histogram, int bitDepth) {
            long count = 0;
            double sum = 0;
            int min = -1, max = 0;
            for (int v = 0; v < histogram.Length; v++) {
                uint c = histogram[v];
                if (c == 0) continue;
                if (min < 0) min = v;
                max = v;
                count += c;
                sum += (double)v * c;
            }

            if (count == 0) {
                return new ChannelStats();
            }

            int median = PercentileBin(histogram, count, 0.5);

            var deviations = new uint[HistogramBins];
            for (int v = min; v <= max; v++) {
                uint c = histogram[v];
                if (c != 0) deviations[Math.Abs(v - median)] += c;
            }
            int mad = PercentileBin(deviations, count, 0.5);

            int top = (1 << Math.Clamp(bitDepth, 1, 16)) - 1;
            long high = 0;
            for (int v = top; v < histogram.Length; v++) high += histogram[v];

            return new ChannelStats {
                Min = min,
                Max = max,
                Mean = sum / count,
                Median = median,
                Mad = mad,
                ClipLow = (double)histogram[0] / count,
                ClipHigh = (double)high / count,
            };
        }

        private static int PercentileBin(uint[] histogram, long count, double fraction) {
            // Lower median for even counts, which is what NINA's integer statistics report too.
            long target = (long)Math.Ceiling(count * fraction);
            if (target < 1) target = 1;
            long cumulative = 0;
            for (int v = 0; v < histogram.Length; v++) {
                cumulative += histogram[v];
                if (cumulative >= target) return v;
            }
            return histogram.Length - 1;
        }

        /// <summary>
        /// 2x2 superpixel debayer: one RGB pixel per CFA cell, the two greens averaged. Half the
        /// resolution, but every value is a real sensor sample - no interpolation that would
        /// smear the histogram. The pattern string lists the cell row-major (e.g. "RGGB").
        /// </summary>
        public static ushort[][] Superpixel(ushort[] data, int width, int height, string pattern, out int outWidth, out int outHeight) {
            if (pattern == null || pattern.Length != 4) {
                throw new ArgumentException($"Unsupported bayer pattern '{pattern}'", nameof(pattern));
            }

            int r = pattern.IndexOf('R');
            int b = pattern.IndexOf('B');
            int g1 = pattern.IndexOf('G');
            int g2 = pattern.IndexOf('G', g1 + 1);
            if (r < 0 || b < 0 || g1 < 0 || g2 < 0) {
                throw new ArgumentException($"Unsupported bayer pattern '{pattern}'", nameof(pattern));
            }

            outWidth = width / 2;
            outHeight = height / 2;
            var red = new ushort[outWidth * outHeight];
            var green = new ushort[outWidth * outHeight];
            var blue = new ushort[outWidth * outHeight];

            for (int y = 0; y < outHeight; y++) {
                int row0 = (y * 2) * width;
                int row1 = row0 + width;
                int outRow = y * outWidth;
                for (int x = 0; x < outWidth; x++) {
                    int c = x * 2;
                    // Cell positions 0..3 map to (row0,c) (row0,c+1) (row1,c) (row1,c+1).
                    ushort p0 = data[row0 + c], p1 = data[row0 + c + 1], p2 = data[row1 + c], p3 = data[row1 + c + 1];
                    red[outRow + x] = Pick(r, p0, p1, p2, p3);
                    blue[outRow + x] = Pick(b, p0, p1, p2, p3);
                    green[outRow + x] = (ushort)((Pick(g1, p0, p1, p2, p3) + Pick(g2, p0, p1, p2, p3) + 1) >> 1);
                }
            }

            return new[] { red, green, blue };
        }

        private static ushort Pick(int index, ushort p0, ushort p1, ushort p2, ushort p3) => index switch {
            0 => p0,
            1 => p1,
            2 => p2,
            _ => p3,
        };

        /// <summary>
        /// Integer-factor box average so the longer side fits maxDim. Only used for the preview
        /// planes - averaging narrows the noise distribution, so the histogram is always built
        /// from the full-resolution data before this runs.
        /// </summary>
        public static ushort[] BoxDownsample(ushort[] plane, int width, int height, int maxDim, out int outWidth, out int outHeight) {
            int longest = Math.Max(width, height);
            int factor = maxDim > 0 ? (int)Math.Ceiling(longest / (double)maxDim) : 1;
            if (factor <= 1) {
                outWidth = width;
                outHeight = height;
                return plane;
            }

            outWidth = Math.Max(1, width / factor);
            outHeight = Math.Max(1, height / factor);
            var result = new ushort[outWidth * outHeight];
            int area = factor * factor;
            int half = area / 2;

            for (int y = 0; y < outHeight; y++) {
                int srcY = y * factor;
                for (int x = 0; x < outWidth; x++) {
                    int srcX = x * factor;
                    uint sum = 0;
                    for (int dy = 0; dy < factor; dy++) {
                        int row = (srcY + dy) * width + srcX;
                        for (int dx = 0; dx < factor; dx++) {
                            sum += plane[row + dx];
                        }
                    }
                    result[y * outWidth + x] = (ushort)((sum + half) / area);
                }
            }

            return result;
        }

        /// <summary>
        /// Container layout (little-endian): uint32 header length, UTF-8 JSON header, zero padding
        /// to a 4-byte boundary, one uint32[65536] histogram per channel, one uint16[w*h] plane
        /// per channel. The padding lets the app view both blocks as typed arrays without copying.
        /// </summary>
        public static void WriteContainer(Stream output, Header header, uint[][] histograms, ushort[][] planes) {
            if (!BitConverter.IsLittleEndian) {
                throw new PlatformNotSupportedException("Raw image container requires a little-endian host");
            }

            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(header));
            output.Write(BitConverter.GetBytes((uint)json.Length));
            output.Write(json);
            int padding = (4 - ((4 + json.Length) % 4)) % 4;
            if (padding > 0) output.Write(new byte[padding]);

            foreach (var histogram in histograms) {
                output.Write(MemoryMarshal.AsBytes(histogram.AsSpan()));
            }
            foreach (var plane in planes) {
                output.Write(MemoryMarshal.AsBytes(plane.AsSpan()));
            }
        }

        /// <summary>
        /// Builds the whole payload from full-resolution planes: histograms and statistics first,
        /// then the downsampled preview planes.
        /// </summary>
        public static byte[] Encode(ushort[][] planes, int width, int height, int bitDepth, int maxDim,
                                    string imageId, int sourceWidth, int sourceHeight, bool isBayered, string bayerPattern,
                                    bool gzip) {
            var histograms = new uint[planes.Length][];
            var stats = new ChannelStats[planes.Length];
            int observedMax = 0;
            for (int c = 0; c < planes.Length; c++) {
                histograms[c] = BuildHistogram(planes[c]);
            }
            for (int c = 0; c < planes.Length; c++) {
                for (int v = histograms[c].Length - 1; v > observedMax; v--) {
                    if (histograms[c][v] != 0) { observedMax = v; break; }
                }
            }

            // Some drivers scale 12/14-bit data to 16 bit while the profile still reports the
            // native depth. Values above the declared range would then normalise past 1.0.
            int effectiveBitDepth = Math.Clamp(bitDepth, 1, 16);
            if (observedMax > (1 << effectiveBitDepth) - 1) effectiveBitDepth = 16;

            for (int c = 0; c < planes.Length; c++) {
                stats[c] = ComputeStats(histograms[c], effectiveBitDepth);
            }

            var preview = new ushort[planes.Length][];
            int outWidth = width, outHeight = height;
            for (int c = 0; c < planes.Length; c++) {
                preview[c] = BoxDownsample(planes[c], width, height, maxDim, out outWidth, out outHeight);
            }

            var header = new Header {
                ImageId = imageId,
                Width = outWidth,
                Height = outHeight,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                Channels = planes.Length,
                BitDepth = effectiveBitDepth,
                IsBayered = isBayered,
                BayerPattern = bayerPattern,
                Stats = stats,
            };

            using var ms = new MemoryStream();
            if (gzip) {
                // Fastest: the plane data is sensor noise and barely compresses further at higher
                // levels, while the mostly empty histograms shrink to almost nothing either way.
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) {
                    WriteContainer(gz, header, histograms, preview);
                }
            } else {
                WriteContainer(ms, header, histograms, preview);
            }
            return ms.ToArray();
        }
    }
}
