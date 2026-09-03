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
                    // saveColorChannels: true is required for unlinked stretch. DebayeredImage.Stretch()
                    // (NINA.Image/ImageData/DebayeredImage.cs) silently forces unlinked back to false
                    // whenever DebayeredData is null, and BayerFilter16bpp only populates it when
                    // SaveColorChannels/SaveLumChannel is set - without this the "unlinked" checkbox
                    // has no effect no matter what the request asks for.
                    rendered = rendered.Debayer(bayerPattern: ResolveBayerPattern(imageData), saveColorChannels: true);
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

            // The isBayered argument is a *forcing* flag, not a detection hint: FITS.Load and
            // XISF.Load store it verbatim into ImageProperties without ever looking at the file
            // (FITS.cs:163, XISF.cs:224). Passing false here therefore only means "don't force it" -
            // ApplyBayerDetection() below reads the pattern the file actually declares. The raw
            // converter is the exception: LibRawConverter hardcodes isBayered: true either way.
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

            // Corrected once per cache entry rather than per request - the rebuild is cheap, but
            // the cached object is what both the preview and /filesystem/imageinfo report on.
            imageData = ApplyBayerDetection(imageData);

            lock (CacheLock) {
                cache = new CacheEntry { Path = fullPath, ImageData = imageData, CachedAtUtc = DateTime.UtcNow };
            }

            return imageData;
        }

        public static void InvalidateCache() {
            lock (CacheLock) { cache = null; }
        }

        // -------------------------------------------------------------------------
        // Bayer detection - shared with FilesystemController so /filesystem/imageinfo and
        // /filesystem/preview never disagree about whether a file is bayered.
        // -------------------------------------------------------------------------

        /// <summary>
        /// The CFA patterns ImageUtility.Debayer() actually has a filter for
        /// (NINA.Image/ImageAnalysis/ImageUtility.cs). Everything else - Monochrome, Color, CMYG,
        /// CMYG2, LRGB - hits its default branch and throws InvalidImagePropertiesException, so
        /// this has to be checked before every Debayer() call. Note the enum values are not
        /// contiguous: RGGB is 2 while CMYG/CMYG2/LRGB sit at 3/4/5, so a range check won't do.
        /// </summary>
        public static bool IsDebayerablePattern(SensorType sensorType) => sensorType is
            SensorType.RGGB or SensorType.RGBG or SensorType.GRGB or SensorType.GRBG or
            SensorType.GBGR or SensorType.GBRG or SensorType.BGRG or SensorType.BGGR;

        /// <summary>
        /// Restores the IsBayered flag from what the file itself declares.
        ///
        /// FITS.Load/XISF.Load never derive Bayer-ness from the file - they store the caller's
        /// isBayered argument as-is. The file's real pattern goes somewhere else entirely:
        /// FITSHeader/XISFHeader parse the BAYERPAT keyword into MetaData.Camera.SensorType. So
        /// for every FITS/XISF loaded here Properties.IsBayered was false regardless of the
        /// sensor, which is why OSC frames never offered the debayer option.
        ///
        /// ImageProperties.IsBayered is immutable after construction, so the fix is to rebuild the
        /// IImageData. Passing the existing IImageArray reuses the decoded pixel buffer - no second
        /// disk read, no copy.
        /// </summary>
        public static IImageData ApplyBayerDetection(IImageData imageData) {
            if (imageData.Properties.IsBayered || !IsDebayerablePattern(imageData.MetaData.Camera.SensorType)) {
                return imageData;
            }

            return TouchNStars.Mediators.ImageDataFactory.CreateBaseImageData(
                imageData.Data,
                imageData.Properties.Width,
                imageData.Properties.Height,
                imageData.Properties.BitDepth,
                isBayered: true,
                imageData.MetaData);
        }

        /// <summary>
        /// Picks the pattern to debayer with, preferring what the file declares over the active
        /// profile. NINA's own live view does it the other way round (ImageControlVM.cs), but that
        /// renders the connected camera's frame - the file browser routinely shows frames from
        /// another camera, or from a session where the profile has since changed, and there the
        /// file's own BAYERPAT is the more trustworthy source.
        /// </summary>
        public static SensorType ResolveBayerPattern(IImageData imageData) {
            SensorType fromFile = imageData.MetaData.Camera.SensorType;
            if (IsDebayerablePattern(fromFile)) {
                return fromFile;
            }

            // BayerPatternEnum's values are deliberately kept identical to SensorType's, so the
            // cast is the intended way across (see NINA.Core/Enum/BayerPatternEnum.cs).
            BayerPatternEnum profilePattern = TouchNStars.Mediators.Profile.ActiveProfile.CameraSettings.BayerPattern;
            if (IsDebayerablePattern((SensorType)profilePattern)) {
                return (SensorType)profilePattern;
            }

            // Debayer()'s own default - reached when a file is flagged bayered but names no usable
            // pattern anywhere, e.g. a raw whose converter reported no concrete phase.
            return SensorType.RGGB;
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
