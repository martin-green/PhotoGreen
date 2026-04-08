namespace PhotoGreen.Models;

public class LibraryImageInfo
{
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public string? PerceptualHash { get; set; }
    public string? FileHash { get; set; }
    public List<FaceInfo> Faces { get; set; } = [];
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime? AnalyzedUtc { get; set; }
}
