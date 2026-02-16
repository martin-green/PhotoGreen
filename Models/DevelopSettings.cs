namespace PhotoGreen.Models;

public record DevelopSettings
{
    // Tone
    public double Exposure { get; init; }        // -5.0 to +5.0 EV
    public double Contrast { get; init; }        // -100 to +100
    public double Highlights { get; init; }      // -100 to +100
    public double Shadows { get; init; }         // -100 to +100
    public double Whites { get; init; }          // -100 to +100
    public double Blacks { get; init; }          // -100 to +100

    // Color
    public double Temperature { get; init; }     // 2000 to 10000 K
    public double Tint { get; init; }            // -100 to +100
    public double Vibrance { get; init; }        // -100 to +100
    public double Saturation { get; init; }      // -100 to +100

    // Detail
    public double Sharpness { get; init; }       // 0 to 150
    public double NoiseReduction { get; init; }  // 0 to 100

    // Crop (non-destructive, normalized 0–1)
    public CropSettings Crop { get; init; } = CropSettings.Default;

    public static DevelopSettings Default => new()
    {
        Exposure = 0,
        Contrast = 0,
        Highlights = 0,
        Shadows = 0,
        Whites = 0,
        Blacks = 0,
        Temperature = 5500,
        Tint = 0,
        Vibrance = 0,
        Saturation = 0,
        Sharpness = 0,
        NoiseReduction = 0,
        Crop = CropSettings.Default
    };
}
