using System.Collections.Generic;

namespace TouchNStars.Server.Models;

public class ImageHeaderEntry {
    public string Key { get; set; }
    public string Value { get; set; }
    public string Comment { get; set; }
}

public class ImageInfo {
    public bool Success { get; set; }
    public bool IsSupported { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; }
    public bool IsBayered { get; set; }
    public string BayerPattern { get; set; } = string.Empty;
    public double? FocalLength { get; set; }
    public double? PixelSize { get; set; }
    public string CameraName { get; set; }
    public string TelescopeName { get; set; }
    public string ExposureStart { get; set; }
    public double? ExposureTime { get; set; }
    public string FilterName { get; set; }
    public List<ImageHeaderEntry> Headers { get; set; } = new();
    public string Error { get; set; }
}
