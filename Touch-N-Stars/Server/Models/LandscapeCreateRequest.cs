namespace TouchNStars.Server.Models;

public class LandscapeCreateRequest
{
    public byte[] ImageBytes { get; set; }

    public string Name { get; set; }

    public string FolderName { get; set; }

    public double NorthOffsetDeg { get; set; }

    public string Description { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? Altitude { get; set; }

    public string Author { get; set; }

    public string OriginalFileName { get; set; }

    public string ContentType { get; set; }
}
