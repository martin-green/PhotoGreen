namespace PhotoGreen.Models;

public class FaceInfo
{
    public string? Label { get; set; }
    public string? Description { get; set; }
    public string? ApproximatePosition { get; set; }
    public double Confidence { get; set; }
    public BoundingBox? BoundingBox { get; set; }
}

public class BoundingBox
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}
