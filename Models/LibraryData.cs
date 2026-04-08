namespace PhotoGreen.Models;

public class LibraryData
{
    public int Version { get; set; } = 1;
    public string RootFolder { get; set; } = string.Empty;
    public DateTime LastScanUtc { get; set; }
    public List<LibraryImageInfo> Images { get; set; } = [];
    public List<FaceCluster> FaceClusters { get; set; } = [];
    public List<DuplicateGroup> DuplicateGroups { get; set; } = [];
}
