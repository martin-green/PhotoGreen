namespace PhotoGreen.Models;

public class FaceCluster
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? RepresentativeImagePath { get; set; }
    public List<string> ImagePaths { get; set; } = [];
}
