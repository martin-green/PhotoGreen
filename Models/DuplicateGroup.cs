namespace PhotoGreen.Models;

public enum DuplicateType
{
    Exact,
    NearDuplicate
}

public class DuplicateGroup
{
    public string Id { get; set; } = string.Empty;
    public DuplicateType Type { get; set; }
    public List<string> ImagePaths { get; set; } = [];
    public int HammingDistance { get; set; }
}
