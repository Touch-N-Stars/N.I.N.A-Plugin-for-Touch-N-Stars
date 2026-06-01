using System;
using System.IO;
using System.Threading.Tasks;
using NINA.Core.Utility;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using TouchNStars.PHD2;
using System.Linq;
using NINA.Image.Interfaces;
using NINA.Core.Enum;
using System.Windows.Media.Imaging;
using NINA.WPF.Base.Utility;

namespace TouchNStars.Server.Services
{
    public class PHD2ImageService : IDisposable
    {
        private readonly PHD2Service phd2Service;
        private readonly object lockObject = new object();
        private readonly string cacheDirectory;
        private string currentImagePath;
        private DateTime lastImageTime = DateTime.MinValue;
        private string lastError;
        private volatile bool isRefreshing = false; // Prevent overlapping refreshes
        private volatile bool isDisposed = false;

        public string LastError => lastError;
        public bool HasCurrentImage => !string.IsNullOrEmpty(currentImagePath) && File.Exists(currentImagePath);
        public DateTime LastImageTime => lastImageTime;

        public PHD2ImageService(PHD2Service phd2Service)
        {
            this.phd2Service = phd2Service;
            
            // Create cache directory
            cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "TnsCache", "phd2_images"
            );
            Directory.CreateDirectory(cacheDirectory);
        }

        public async Task<bool> RefreshImageAsync(double gamma = 0.5)
        {
            if (isDisposed) return false;
            
            // Prevent overlapping refresh operations
            if (isRefreshing) 
            {
                Logger.Debug("Refresh already in progress, skipping");
                return false;
            }
            
            isRefreshing = true;
            try
            {
                // Wait for any ongoing connection attempt
                await phd2Service.WaitForConnectionAsync();

                lock (lockObject)
                {
                    if (!phd2Service.IsConnected)
                    {
                        lastError = "PHD2 is not connected";
                        Logger.Warning("PHD2 refresh failed: PHD2 is not connected");
                        return false;
                    }
                }

                // Get image from PHD2
                string fitsFilePath = await phd2Service.SaveImageAsync();
                
                if (string.IsNullOrEmpty(fitsFilePath))
                {
                    lastError = "PHD2 returned empty file path";
                    Logger.Error("PHD2 returned empty file path");
                    return false;
                }

                if (!File.Exists(fitsFilePath))
                {
                    lastError = $"FITS file does not exist: {fitsFilePath}";
                    Logger.Error($"FITS file does not exist: {fitsFilePath}");
                    return false;
                }

                // Convert FITS to JPG and cache it
                string jpgPath = await ConvertFitsToJpgAsync(fitsFilePath, gamma);
                
                if (!string.IsNullOrEmpty(jpgPath))
                {
                    lock (lockObject)
                    {
                        // Clean up old cached image
                        if (!string.IsNullOrEmpty(currentImagePath) && File.Exists(currentImagePath))
                        {
                            try { File.Delete(currentImagePath); } catch { }
                        }
                        
                        currentImagePath = jpgPath;
                        lastImageTime = DateTime.Now;
                        lastError = null;
                    }
                    
                    // Clean up the FITS file
                    try { File.Delete(fitsFilePath); } catch { }
                    
                    return true;
                }
                else
                {
                    Logger.Warning("FITS to JPG conversion failed");
                    // Clean up the FITS file
                    try { File.Delete(fitsFilePath); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                lock (lockObject)
                {
                    lastError = ex.Message;
                }
                
                // "no image available" is expected when PHD2 hasn't captured an image yet
                if (ex.Message.Contains("no image available"))
                {
                    Logger.Debug($"PHD2 image not available yet: {ex.Message}");
                }
                else
                {
                    Logger.Error($"Failed to refresh PHD2 image: {ex}");
                    Logger.Error($"Stack trace: {ex.StackTrace}");
                }
                return false;
            }
            finally
            {
                isRefreshing = false;
            }
        }

        private async Task<string> ConvertFitsToJpgAsync(string fitsFilePath, double gamma = 0.5)
        {
            try
            {
                // Generate unique filename for the JPG
                string jpgFileName = $"phd2_image_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                string jpgPath = Path.Combine(cacheDirectory, jpgFileName);

                // Check if NINA's ImageDataFactory is available
                if (TouchNStars.Mediators?.ImageDataFactory != null)
                {
                    try
                    {
                        // Use NINA's ImageDataFactory to load the FITS file
                        var imageDataFactory = TouchNStars.Mediators.ImageDataFactory;
                        IImageData imageData = await imageDataFactory.CreateFromFile(fitsFilePath, 16, false, RawConverterEnum.FREEIMAGE);

                        if (imageData != null)
                        {
                            // Render the image
                            IRenderedImage renderedImage = imageData.RenderImage();

                            // Map gamma (0.1–1.0) to stretch factor: gamma 0.1 → factor 5.0, gamma 1.0 → factor 1.0
                            double stretchFactor = 1.0 + (1.0 - gamma) * 4.0;
                            renderedImage = await renderedImage.Stretch(stretchFactor, 0.1, false);

                            // Convert to bitmap and save as JPG
                            BitmapSource bitmapSource = renderedImage.Image;
                            
                            // Create JPG encoder
                            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
                            encoder.QualityLevel = 95;
                            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

                            // Save to file
                            using (FileStream fileStream = new FileStream(jpgPath, FileMode.Create))
                            {
                                encoder.Save(fileStream);
                            }

                            return jpgPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"NINA ImageDataFactory failed: {ex.Message}, using fallback");
                    }
                }

                // Fallback: Try to read and convert FITS file manually
                return await ConvertFitsManuallyAsync(fitsFilePath, jpgPath, gamma);
            }
            catch (Exception ex)
            {
                lastError = $"Failed to convert FITS to JPG: {ex.Message}";
                Logger.Error($"FITS conversion error: {ex}");
                return null;
            }
        }

        private async Task<string> ConvertFitsManuallyAsync(string fitsFilePath, string jpgPath, double gamma = 0.5)
        {
            try
            {
                var fitsData = await ReadFitsFileAsync(fitsFilePath);
                if (fitsData == null)
                {
                    Logger.Debug("FITS reading failed, creating placeholder");
                    return await CreatePlaceholderImageAsync(jpgPath);
                }

                var result = await ConvertFitsDataToJpgAsync(fitsData, jpgPath, gamma);
                
                if (string.IsNullOrEmpty(result))
                {
                    Logger.Debug("FITS to JPG conversion failed, creating placeholder");
                    return await CreatePlaceholderImageAsync(jpgPath);
                }
                
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Manual FITS conversion failed: {ex.Message}");
                return await CreatePlaceholderImageAsync(jpgPath);
            }
        }

        private class SimpleFitsData
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public float[,] ImageData { get; set; }
            public int BitPix { get; set; }
        }

        private async Task<SimpleFitsData> ReadFitsFileAsync(string fitsFilePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var fileStream = new FileStream(fitsFilePath, FileMode.Open, FileAccess.Read))
                    using (var reader = new BinaryReader(fileStream))
                    {
                        // Read FITS header
                        int width = 0, height = 0, bitpix = 0;
                        long dataStart = 0;
                        int headerBlockCount = 0;

                        // Read header blocks (2880 bytes each)
                        while (dataStart < fileStream.Length)
                        {
                            fileStream.Seek(dataStart, SeekOrigin.Begin);
                            byte[] block = reader.ReadBytes(2880);
                            string headerText = System.Text.Encoding.ASCII.GetString(block);
                            headerBlockCount++;

                            // Parse key FITS keywords from the entire header block
                            if (width == 0) width = ParseFitsKeyword(headerText, "NAXIS1");
                            if (height == 0) height = ParseFitsKeyword(headerText, "NAXIS2");
                            if (bitpix == 0) bitpix = ParseFitsKeyword(headerText, "BITPIX");

                            // Check for END keyword
                            if (headerText.Contains("END     "))
                            {
                                dataStart += 2880;
                                break;
                            }
                            dataStart += 2880;
                        }

                        if (width <= 0 || height <= 0)
                        {
                            Logger.Error($"Invalid FITS dimensions: {width}x{height}");
                            return null;
                        }

                        if (bitpix == 0)
                        {
                            Logger.Error("BITPIX not found in FITS header");
                            return null;
                        }

                        // Safety check for extremely large images
                        const long MAX_PIXELS = 50_000_000; // 50 megapixels
                        long totalPixels = (long)width * height;
                        if (totalPixels > MAX_PIXELS)
                        {
                            Logger.Error($"Image too large: {width}x{height} = {totalPixels:N0} pixels (max: {MAX_PIXELS:N0})");
                            return null;
                        }

                        // Calculate expected data size
                        int bytesPerPixel = Math.Abs(bitpix) / 8;
                        long expectedDataSize = (long)width * height * bytesPerPixel;
                        long availableData = fileStream.Length - dataStart;
                        
                        if (availableData < expectedDataSize)
                        {
                            Logger.Error($"FITS file truncated. Expected {expectedDataSize} bytes but only {availableData} available");
                            return null;
                        }

                        // Read image data
                        fileStream.Seek(dataStart, SeekOrigin.Begin);
                        var imageData = new float[width, height];

                        if (bitpix == -32) // 32-bit float
                        {
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    byte[] bytes = reader.ReadBytes(4);
                                    if (bytes.Length < 4)
                                    {
                                        Logger.Error($"Unexpected end of file at pixel ({x},{y})");
                                        return null;
                                    }
                                    if (BitConverter.IsLittleEndian)
                                    {
                                        Array.Reverse(bytes); // FITS is big-endian
                                    }
                                    imageData[x, y] = BitConverter.ToSingle(bytes, 0);
                                }
                            }
                        }
                        else if (bitpix == 16) // 16-bit signed integer
                        {
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    byte[] bytes = reader.ReadBytes(2);
                                    if (bytes.Length < 2)
                                    {
                                        Logger.Error($"Unexpected end of file at pixel ({x},{y})");
                                        return null;
                                    }
                                    if (BitConverter.IsLittleEndian)
                                    {
                                        Array.Reverse(bytes);
                                    }
                                    imageData[x, y] = BitConverter.ToInt16(bytes, 0);
                                }
                            }
                        }
                        else if (bitpix == -64) // 64-bit double
                        {
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    byte[] bytes = reader.ReadBytes(8);
                                    if (bytes.Length < 8)
                                    {
                                        Logger.Error($"Unexpected end of file at pixel ({x},{y})");
                                        return null;
                                    }
                                    if (BitConverter.IsLittleEndian)
                                    {
                                        Array.Reverse(bytes);
                                    }
                                    imageData[x, y] = (float)BitConverter.ToDouble(bytes, 0);
                                }
                            }
                        }
                        else
                        {
                            Logger.Error($"Unsupported BITPIX: {bitpix}");
                            return null;
                        }

                        return new SimpleFitsData
                        {
                            Width = width,
                            Height = height,
                            ImageData = imageData,
                            BitPix = bitpix
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error reading FITS file '{fitsFilePath}': {ex.Message}");
                    Logger.Error($"Stack trace: {ex.StackTrace}");
                    return null;
                }
            });
        }

        private int ParseFitsKeyword(string headerText, string keyword)
        {
            try
            {
                // FITS keywords are at the start of 80-character lines
                // Format: "KEYWORD = value / comment" or "KEYWORD = value"
                string[] lines = new string[headerText.Length / 80];
                for (int i = 0; i < headerText.Length; i += 80)
                {
                    int lineEnd = Math.Min(i + 80, headerText.Length);
                    lines[i / 80] = headerText.Substring(i, lineEnd - i).TrimEnd('\0', ' ');
                }

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    // Check if this line starts with our keyword
                    if (line.StartsWith(keyword))
                    {
                        // Look for the equals sign
                        int equalsIndex = line.IndexOf('=');
                        if (equalsIndex < 0) continue;
                        
                        // Extract value part (between = and / or end of meaningful content)
                        string valuePart = line.Substring(equalsIndex + 1);
                        
                        // Find comment separator or end
                        int commentIndex = valuePart.IndexOf('/');
                        if (commentIndex >= 0)
                        {
                            valuePart = valuePart.Substring(0, commentIndex);
                        }
                        
                        // Clean up the value
                        string valueText = valuePart.Trim();
                        
                        if (int.TryParse(valueText, out int value))
                        {
                            return value;
                        }
                    }
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error parsing FITS keyword {keyword}: {ex.Message}");
                return 0;
            }
        }

        private async Task<string> ConvertFitsDataToJpgAsync(SimpleFitsData fitsData, string jpgPath, double gamma = 0.5)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Find min/max values for scaling
                    float minVal = float.MaxValue;
                    float maxVal = float.MinValue;

                    for (int y = 0; y < fitsData.Height; y++)
                    {
                        for (int x = 0; x < fitsData.Width; x++)
                        {
                            float value = fitsData.ImageData[x, y];
                            if (!float.IsNaN(value) && !float.IsInfinity(value))
                            {
                                minVal = Math.Min(minVal, value);
                                maxVal = Math.Max(maxVal, value);
                            }
                        }
                    }

                    // Avoid division by zero
                    if (Math.Abs(maxVal - minVal) < 1e-10f)
                    {
                        maxVal = minVal + 1.0f;
                    }

                    // Create bitmap and convert - using LockBits for much better performance
                    using (var bitmap = new Bitmap(fitsData.Width, fitsData.Height, PixelFormat.Format24bppRgb))
                    {
                        var bitmapData = bitmap.LockBits(new Rectangle(0, 0, fitsData.Width, fitsData.Height),
                            ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                        
                        try
                        {
                            unsafe
                            {
                                byte* ptr = (byte*)bitmapData.Scan0;
                                int stride = bitmapData.Stride;
                                
                                for (int y = 0; y < fitsData.Height; y++)
                                {
                                    for (int x = 0; x < fitsData.Width; x++)
                                    {
                                        float value = fitsData.ImageData[x, y];
                                        
                                        // Handle NaN/Infinity values
                                        if (float.IsNaN(value) || float.IsInfinity(value))
                                        {
                                            value = minVal;
                                        }

                                        // Apply simple linear stretch
                                        float normalized = (value - minVal) / (maxVal - minVal);

                                        // Apply gamma correction for visibility
                                        normalized = (float)Math.Pow(normalized, gamma);
                                        
                                        byte intensity = (byte)Math.Round(255.0f * Math.Max(0, Math.Min(1, normalized)));

                                        // Use Y coordinate directly (no flipping needed)
                                        byte* pixel = ptr + (y * stride) + (x * 3);
                                        
                                        // Set RGB values (grayscale, so all same)
                                        pixel[0] = intensity; // Blue
                                        pixel[1] = intensity; // Green  
                                        pixel[2] = intensity; // Red
                                    }
                                }
                            }
                        }
                        finally
                        {
                            bitmap.UnlockBits(bitmapData);
                        }
                        
                        // Save as JPG
                        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                        
                        if (jpegEncoder != null)
                        {
                            var encoderParams = new EncoderParameters(1);
                            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                            bitmap.Save(jpgPath, jpegEncoder, encoderParams);
                        }
                        else
                        {
                            bitmap.Save(jpgPath, ImageFormat.Jpeg);
                        }
                    }

                    return jpgPath;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to convert FITS data to JPG: {ex}");
                    throw;
                }
            });
        }

        private async Task<string> CreatePlaceholderImageAsync(string jpgPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Create a 640x480 placeholder image
                    using (var bitmap = new Bitmap(640, 480, PixelFormat.Format24bppRgb))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        // Fill with dark gray background
                        graphics.Clear(Color.FromArgb(32, 32, 32));
                        
                        // Add text indicating this is a placeholder
                        using (var font = new Font("Arial", 16, FontStyle.Bold))
                        using (var brush = new SolidBrush(Color.White))
                        {
                            string text = $"PHD2 Image\n{DateTime.Now:HH:mm:ss}\n\nFITS conversion not available\nPlaceholder image";
                            var textBounds = graphics.MeasureString(text, font);
                            float x = (bitmap.Width - textBounds.Width) / 2;
                            float y = (bitmap.Height - textBounds.Height) / 2;
                            
                            graphics.DrawString(text, font, brush, x, y);
                        }
                        
                        // Draw a simple border
                        using (var pen = new Pen(Color.Gray, 2))
                        {
                            graphics.DrawRectangle(pen, 10, 10, bitmap.Width - 20, bitmap.Height - 20);
                        }
                        
                        // Save as JPG
                        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                        
                        if (jpegEncoder != null)
                        {
                            var encoderParams = new EncoderParameters(1);
                            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                            bitmap.Save(jpgPath, jpegEncoder, encoderParams);
                        }
                        else
                        {
                            bitmap.Save(jpgPath, ImageFormat.Jpeg);
                        }
                    }

                    return jpgPath;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to create placeholder image: {ex}");
                    throw;
                }
            });
        }


        public async Task<byte[]> GetCurrentImageBytesAsync(double gamma = 0.5)
        {
            // Always fetch a fresh image on demand
            await RefreshImageAsync(gamma);
            
            string imagePath;
            lock (lockObject)
            {
                if (string.IsNullOrEmpty(currentImagePath) || !File.Exists(currentImagePath))
                {
                    return null;
                }
                imagePath = currentImagePath;
            }

            try
            {
                return await File.ReadAllBytesAsync(imagePath);
            }
            catch (Exception ex)
            {
                lastError = $"Failed to read cached image: {ex.Message}";
                Logger.Error($"Error reading cached image: {ex}");
                return null;
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            
            isDisposed = true;
            
            // Wait for any ongoing refresh to complete
            while (isRefreshing)
            {
                Thread.Sleep(50);
            }
            
            lock (lockObject)
            {
                // Clean up cached image
                if (!string.IsNullOrEmpty(currentImagePath) && File.Exists(currentImagePath))
                {
                    try 
                    { 
                        File.Delete(currentImagePath); 
                    } 
                    catch { }
                }
            }
        }
    }
}