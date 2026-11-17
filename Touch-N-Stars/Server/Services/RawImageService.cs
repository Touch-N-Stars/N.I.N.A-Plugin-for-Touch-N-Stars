using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.WPF.Base.Interfaces.Mediator;

namespace TouchNStars.Server.Services {

    public class RawImageException : Exception {
        public int StatusCode { get; }
        public string Code { get; }

        public RawImageException(int statusCode, string code, string message) : base(message) {
            StatusCode = statusCode;
            Code = code;
        }
    }

    /// <summary>
    /// Serves the real sensor data behind the app's histogram and stretch: the image NINA prepared
    /// last, saved frames from the image history, and Livestack stacks.
    ///
    /// State is static because EmbedIO instantiates controllers per request and the mediator
    /// events have to be subscribed once for the lifetime of the plugin.
    /// </summary>
    public class RawImageService {
        // The Livestack plugin (isbeorn/nina.plugin.livestack) - its settings are read, never written.
        private static readonly Guid LivestackPluginId = Guid.Parse("10bc1716-54af-425e-b307-c0ca1ce10600");
        private static readonly string[] OscChannelFilters = { "R_OSC", "G_OSC", "B_OSC" };
        private const int SavedImageCapacity = 1000;

        private static readonly SemaphoreSlim EncodeGate = new(1, 1);
        private static readonly object StateLock = new();

        private static IImagingMediator imaging;
        private static IImageSaveMediator imageSave;
        private static IImageData preparedImage;
        private static long preparedSequence;

        private sealed class SavedImage {
            public long Sequence;
            public string Path;
            public double Mean;
            public double Median;
        }

        // Frames saved in this session, oldest first. The Advanced API's history payload has no
        // path: the PINS fork sends the file name, the official one only the position in its own
        // list - which follows the same ImageSaved events, so Sequence matches that index.
        private static readonly LinkedList<SavedImage> savedImages = new();
        private static long savedSequence;

        // One-entry cache: reopening the histogram panel for the same image must not re-encode.
        private static string cachedKey;
        private static byte[] cachedPayload;

        public static void Attach(IImagingMediator imagingMediator, IImageSaveMediator imageSaveMediator) {
            Detach();
            imaging = imagingMediator;
            imageSave = imageSaveMediator;
            if (imaging != null) imaging.ImagePrepared += OnImagePrepared;
            if (imageSave != null) imageSave.ImageSaved += OnImageSaved;
        }

        public static void Detach() {
            if (imaging != null) imaging.ImagePrepared -= OnImagePrepared;
            if (imageSave != null) imageSave.ImageSaved -= OnImageSaved;
            imaging = null;
            imageSave = null;
            lock (StateLock) {
                preparedImage = null;
                cachedKey = null;
                cachedPayload = null;
            }
        }

        private static void OnImagePrepared(object sender, ImagePreparedEventArgs e) {
            var raw = e?.RenderedImage?.RawImageData;
            if (raw == null) return;
            lock (StateLock) {
                preparedImage = raw;
                preparedSequence++;
            }
        }

        private static void OnImageSaved(object sender, ImageSavedEventArgs e) {
            if (e == null) return;
            lock (StateLock) {
                // Every event takes a sequence number, even one without a path, so the numbering
                // stays in step with the Advanced API's history list.
                long sequence = savedSequence++;
                string path = e.PathToImage?.LocalPath;
                if (string.IsNullOrEmpty(path)) return;
                savedImages.AddLast(new SavedImage {
                    Sequence = sequence,
                    Path = path,
                    Mean = e.Statistics?.Mean ?? double.NaN,
                    Median = e.Statistics?.Median ?? double.NaN,
                });
                while (savedImages.Count > SavedImageCapacity) savedImages.RemoveFirst();
            }
        }

        public Task<byte[]> GetPreparedAsync(int maxDim, bool gzip, CancellationToken ct) {
            IImageData image;
            long sequence;
            lock (StateLock) {
                image = preparedImage;
                sequence = preparedSequence;
            }
            if (image == null) {
                throw new RawImageException(404, "no-image", "No prepared image available");
            }

            string imageId = $"prepared-{sequence}";
            return EncodeCachedAsync($"{imageId}|{maxDim}|{gzip}", ct,
                () => EncodeImageData(image, imageId, maxDim, gzip));
        }

        /// <summary>
        /// Resolves a history entry by file name when the backend reports one, otherwise by its
        /// index - confirmed against the entry's mean and median, so a history that drifted out
        /// of step (e.g. ninaAPI restarted on its own) fails instead of showing another frame.
        /// </summary>
        public async Task<byte[]> GetHistoryAsync(string fileName, long? index, double? mean, double? median,
                                                  int maxDim, bool gzip, CancellationToken ct) {
            string path;
            if (!string.IsNullOrWhiteSpace(fileName)) {
                // GetInvalidFileNameChars() is nearly empty on Linux, and the name doubles as a
                // search pattern below, so wildcards and path parts are rejected explicitly.
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || fileName.IndexOfAny(new[] { '*', '?', '/', '\\' }) >= 0
                    || fileName.Trim('.').Length == 0) {
                    throw new RawImageException(400, "invalid-filename", "A plain file name is required");
                }
                path = ResolveSavedImage(fileName);
            } else if (index.HasValue && mean.HasValue && median.HasValue) {
                path = ResolveSavedImage(index.Value, mean.Value, median.Value);
            } else {
                throw new RawImageException(400, "invalid-parameters", "filename or index, mean and median are required");
            }

            if (path == null) {
                throw new RawImageException(404, "file-not-found", "Image file not found");
            }

            string imageId = $"file-{path}-{File.GetLastWriteTimeUtc(path).Ticks}";
            return await EncodeCachedAsync($"{imageId}|{maxDim}|{gzip}", ct, async () => {
                IImageData data = await LoadFileAsync(path).ConfigureAwait(false);
                return EncodeImageData(data, imageId, maxDim, gzip);
            }).ConfigureAwait(false);
        }

        public async Task<byte[]> GetLivestackAsync(string target, string filter, int maxDim, bool gzip, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(filter)) {
                throw new RawImageException(400, "invalid-parameters", "target and filter are required");
            }

            var settings = new PluginOptionsAccessor(TouchNStars.Mediators.Profile, LivestackPluginId);
            if (!settings.GetValueBoolean("SaveStackedLights", false)) {
                throw new RawImageException(409, "livestack-save-disabled", "Livestack does not save its stacks to disk");
            }
            string stackFolder = Path.Combine(settings.GetValueString("WorkingDirectory", Path.GetTempPath()), "stacks");

            // The colour combination itself is only saved as a stretched PNG. For OSC stacks the
            // three channel stacks share one alignment, so they recombine into real RGB data;
            // combinations of separate mono filters are aligned inside the Livestack plugin only.
            string[] filters = filter == "RGB" ? OscChannelFilters : new[] { filter };
            string[] paths = filters
                .Select(f => Path.Combine(stackFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{f}.fits")))
                .ToArray();

            if (paths.Any(p => !File.Exists(p))) {
                throw filter == "RGB"
                    ? new RawImageException(409, "livestack-rgb-unsupported", "Only OSC colour stacks can be served as raw data")
                    : new RawImageException(404, "file-not-found", "Stack file not found");
            }

            string imageId = "livestack-" + string.Join("|", paths.Select(p => $"{p}-{File.GetLastWriteTimeUtc(p).Ticks}"));
            return await EncodeCachedAsync($"{imageId}|{maxDim}|{gzip}", ct, async () => {
                var channels = new ushort[paths.Length][];
                int width = 0, height = 0;
                for (int i = 0; i < paths.Length; i++) {
                    // Stacks are normalised float FITS; NINA's FloatConverter maps 0..1 to 0..65535.
                    IImageData data = await LoadFileAsync(paths[i]).ConfigureAwait(false);
                    if (i > 0 && (data.Properties.Width != width || data.Properties.Height != height)) {
                        throw new RawImageException(409, "livestack-rgb-unsupported", "Channel stacks differ in size");
                    }
                    width = data.Properties.Width;
                    height = data.Properties.Height;
                    channels[i] = data.Data.FlatArray;
                }
                return RawImageEncoder.Encode(channels, width, height, 16, maxDim, imageId,
                    width, height, isBayered: false, bayerPattern: null, gzip);
            }).ConfigureAwait(false);
        }

        private static async Task<byte[]> EncodeCachedAsync(string key, CancellationToken ct, Func<Task<byte[]>> encode) {
            lock (StateLock) {
                if (cachedKey == key && cachedPayload != null) return cachedPayload;
            }

            // One encode at a time: a 60 MP frame needs a few hundred MB while it is processed.
            await EncodeGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                lock (StateLock) {
                    if (cachedKey == key && cachedPayload != null) return cachedPayload;
                }
                ct.ThrowIfCancellationRequested();
                byte[] payload = await Task.Run(encode, ct).ConfigureAwait(false);
                lock (StateLock) {
                    cachedKey = key;
                    cachedPayload = payload;
                }
                return payload;
            } finally {
                EncodeGate.Release();
            }
        }

        private static Task<byte[]> EncodeCachedAsync(string key, CancellationToken ct, Func<byte[]> encode) =>
            EncodeCachedAsync(key, ct, () => Task.FromResult(encode()));

        private static byte[] EncodeImageData(IImageData data, string imageId, int maxDim, bool gzip) {
            int width = data.Properties.Width;
            int height = data.Properties.Height;
            ushort[] flat = data.Data.FlatArray;

            if (data.Properties.IsBayered) {
                var pattern = ImagePreviewService.ResolveBayerPattern(data);
                ushort[][] rgb = RawImageEncoder.Superpixel(flat, width, height, pattern.ToString(), out int w, out int h);
                return RawImageEncoder.Encode(rgb, w, h, data.Properties.BitDepth, maxDim, imageId,
                    width, height, isBayered: true, bayerPattern: pattern.ToString(), gzip);
            }

            return RawImageEncoder.Encode(new[] { flat }, width, height, data.Properties.BitDepth, maxDim, imageId,
                width, height, isBayered: false, bayerPattern: null, gzip);
        }

        private static async Task<IImageData> LoadFileAsync(string path) {
            // Same load path as ImagePreviewService: the isBayered flag only forces, the file's
            // own BAYERPAT is restored by ApplyBayerDetection.
            var data = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(path, 16, false, RawConverterEnum.FREEIMAGE)
                .ConfigureAwait(false);
            if (data == null) {
                throw new RawImageException(500, "load-failed", "Failed to load image");
            }
            return ImagePreviewService.ApplyBayerDetection(data);
        }

        private static string ResolveSavedImage(long index, double mean, double median) {
            lock (StateLock) {
                var entry = savedImages.FirstOrDefault(x => x.Sequence == index);
                if (entry == null || !File.Exists(entry.Path)) return null;
                if (!SameStatistic(entry.Mean, mean) || !SameStatistic(entry.Median, median)) {
                    throw new RawImageException(409, "history-mismatch", "History entry does not match the saved frame");
                }
                return entry.Path;
            }
        }

        // Both sides serialise the same doubles, so only JSON round-off has to be tolerated.
        private static bool SameStatistic(double a, double b) =>
            Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(a));

        private static string ResolveSavedImage(string fileName) {
            lock (StateLock) {
                for (var node = savedImages.Last; node != null; node = node.Previous) {
                    if (string.Equals(Path.GetFileName(node.Value.Path), fileName, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(node.Value.Path)) {
                        return node.Value.Path;
                    }
                }
            }

            // Frames saved before this NINA session (a history the backend rebuilt from disk) are
            // looked up below the configured image folder - never anywhere else on the disk.
            string root = TouchNStars.Mediators.Profile.ActiveProfile.ImageFileSettings.FilePath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            try {
                return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
            } catch (Exception ex) {
                Logger.Warning($"[RawImageService] Searching {root} for {fileName} failed: {ex.Message}");
                return null;
            }
        }
    }
}
