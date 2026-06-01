namespace TouchNStars.Server.Models;

public class LandscapeValidationResult
{
    public bool IsValid => string.IsNullOrWhiteSpace(ErrorMessage);

    public string ErrorMessage { get; set; }

    public string SanitizedFolderName { get; set; }
}
