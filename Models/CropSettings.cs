namespace PhotoGreen.Models;

public enum CropAspectRatio
{
    Free,
    Original,
    Square,     // 1:1
    OneByTwo    // 1:2
}

public record CropSettings
{
    /// <summary>Normalized left edge (0.0–1.0)</summary>
    public double Left { get; init; }

    /// <summary>Normalized top edge (0.0–1.0)</summary>
    public double Top { get; init; }

    /// <summary>Normalized right edge (0.0–1.0)</summary>
    public double Right { get; init; } = 1.0;

    /// <summary>Normalized bottom edge (0.0–1.0)</summary>
    public double Bottom { get; init; } = 1.0;

    public CropAspectRatio AspectRatio { get; init; } = CropAspectRatio.Free;

    public bool IsDefault => Left == 0 && Top == 0 && Right >= 1.0 && Bottom >= 1.0;

    public static CropSettings Default => new();
}
