namespace PhotoGreen.Models;

public class ExifInfo
{
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public string? FocalLength { get; init; }
    public string? Aperture { get; init; }
    public string? ShutterSpeed { get; init; }
    public string? Iso { get; init; }
    public string? ExposureCompensation { get; init; }
    public string? MeteringMode { get; init; }
    public string? FlashMode { get; init; }
    public string? WhiteBalance { get; init; }
    public string? DateTaken { get; init; }
    public string? Dimensions { get; init; }
    public string? FileSize { get; init; }
    public string? ColorSpace { get; init; }
    public string? Software { get; init; }

    public static ExifInfo Empty => new() { CameraMake = "No image loaded" };
}
