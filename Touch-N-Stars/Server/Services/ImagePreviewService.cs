using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Core.Utility;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TouchNStars.Server.Services {

    /// <summary>
    /// Renders FITS/XISF/raw/ordinary image files to JPEG/PNG on the server, using NINA's own
    /// ImageDataFactory -> IRenderedImage pipeline instead of decoding on the phone.
    ///
    /// State is static rather than per-instance: EmbedIO instantiates a new FilesystemController
    /// per request, so the render gate and cache have to live above that instantiation.
    /// </summary>
    public class ImagePreviewService {
        private static readonly SemaphoreSlim RenderGate = new(1, 1);
        private static readonly object CacheLock = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private class CacheEntry {
            public string Path;
            public IImageData ImageData;
            public DateTime CachedAtUtc;
        }

        private static CacheEntry cache;

        public async Task<(byte[] Bytes, string ContentType)> RenderPreviewAsync(
            string fullPath,
            int maxWidth,
            int quality,
            double stretchFactor,
            double blackClipping,
            bool unlinked,
            bool debayerRequested,
            int bitDepth,
            CancellationToken ct) {

            // Queue rather than reject: a caller waiting on this is the frontend's active preview
            // request, not a background poll, so it should wait its turn instead of failing.
            await RenderGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ct.ThrowIfCancellationRequested();

                IImageData imageData = await GetOrLoadImageDataAsync(fullPath, bitDepth)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                // RenderImage()/Debayer() are cheap views over the already-decoded pixel data
                // (unlike CreateFromFile, which is the actual disk read + decode), so - like
                // Stretch() below - they are re-run on every request instead of being cached.
                // Baking Debayer() into the cached object was the bug: toggling the "Debayer"
                // checkbox reused the stale cached result and never took effect.
                IRenderedImage rendered = imageData.RenderImage();
                if (imageData.Properties.IsBayered && debayerRequested) {
                    // StringToSensorType falls back to Monochrome for unparseable input, which is
                    // the wrong default for a Debayer() call - fall back to RGGB (Debayer()'s own
                    // default) instead when the pattern is Auto/None/unset.
                    var bayerPattern = imageData.MetaData.Camera.BayerPattern;
                    SensorType pattern = bayerPattern is BayerPatternEnum.Auto or BayerPatternEnum.None
                        ? SensorType.RGGB
                        : imageData.MetaData.StringToSensorType(bayerPattern.ToString());
                    // saveColorChannels: true is required for unlinked stretch. DebayeredImage.Stretch()
                    // (NINA.Image/ImageData/DebayeredImage.cs) silently forces unlinked back to false
                    // whenever DebayeredData is null, and BayerFilter16bpp only populates it when
                    // SaveColorChannels/SaveLumChannel is set - without this the "unlinked" checkbox
                    // has no effect no matter what the request asks for.
                    rendered = rendered.Debayer(bayerPattern: pattern, saveColorChannels: true);
                }

                IRenderedImage stretched = await rendered.Stretch(stretchFactor, blackClipping, unlinked)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                BitmapSource bitmap = ScaleBitmap(stretched.Image, maxWidth);
                BitmapEncoder encoder = GetEncoder(bitmap, quality);

                using var ms = new MemoryStream();
                encoder.Save(ms);
                string contentType = quality < 0 ? "image/png" : "image/jpeg";
                return (ms.ToArray(), contentType);
            } finally {
                RenderGate.Release();
            }
        }

        // No CancellationToken here: the only available CreateFromFile overload (see below)
        // doesn't take one, so the file load itself can't observe cancellation - RenderPreviewAsync
        // still checks ct.ThrowIfCancellationRequested() before and after this call.
        private static async Task<IImageData> GetOrLoadImageDataAsync(string fullPath, int bitDepth) {
            lock (CacheLock) {
                if (cache != null && cache.Path == fullPath && DateTime.UtcNow - cache.CachedAtUtc < CacheTtl) {
                    return cache.ImageData;
                }
            }

            // isBayered here forces raw pixel data to be treated as Bayer-encoded regardless of
            // what the file format itself says - it is NOT how Bayer-ness is detected. The real
            // signal is imageData.Properties.IsBayered, read back after load from the file's own
            // metadata (set by FITS.Load/XISF.Load/RawToImageArray).
            //
            // RawConverterEnum.FREEIMAGE is a no-op (RawConverterFactory always returns
            // LibRawConverter regardless of it), kept only because it's the argument the available
            // overload requires.
            var imageData = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(fullPath, bitDepth, false, RawConverterEnum.FREEIMAGE)
                .ConfigureAwait(false);

            if (imageData == null) {
                throw new InvalidOperationException("Failed to load image");
            }

            lock (CacheLock) {
                cache = new CacheEntry { Path = fullPath, ImageData = imageData, CachedAtUtc = DateTime.UtcNow };
            }

            return imageData;
        }

        public static void InvalidateCache() {
            lock (CacheLock) { cache = null; }
        }

        // -------------------------------------------------------------------------
        // Encoding helpers - same shape as ninaAPI's Utility/BitmapHelper.cs
        // (GetEncoder/ScaleBitmap), copied rather than referenced since ninaAPI is a
        // separate plugin assembly. quality<0 -> PNG, otherwise JPEG at that quality.
        // -------------------------------------------------------------------------

        private static BitmapSource ScaleBitmap(BitmapSource source, int maxWidth) {
            if (maxWidth <= 0 || source.PixelWidth <= 0) return source;
            double scale = Math.Clamp((double)maxWidth / source.PixelWidth, 0.1, 1.0);
            if (scale >= 1.0) return source; // never upscale
            return new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        private static BitmapEncoder GetEncoder(BitmapSource source, int quality) {
            if (quality < 0) {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                return encoder;
            } else {
                var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
                encoder.Frames.Add(BitmapFrame.Create(source));
                return encoder;
            }
        }
    }
}
