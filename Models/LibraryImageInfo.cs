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
    public SceneType SceneType { get; set; } = SceneType.Unknown;

    /// <summary>When the file's hashes and metadata were last processed.</summary>
    public DateTime? AnalyzedUtc { get; set; }

    /// <summary>
    /// When AI analysis last succeeded for this file. Null means the AI step never ran or
    /// failed, so the file should be retried on the next scan while AI is available.
    /// </summary>
    public DateTime? AiAnalyzedUtc { get; set; }
}
