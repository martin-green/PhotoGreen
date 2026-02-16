using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class ImageLoaderService
{
    /// <summary>
    /// Loads an image group at full resolution.
    /// For raw files, loads at 16-bit depth with camera white balance, then converts to display-ready bitmap.
    /// For JPEGs/other formats, loads natively.
    /// </summary>
    public async Task<ImageLoadResult> LoadAsync(ImageFileGroup group)
    {
        return await Task.Run(() => Load(group));
    }

    private static ImageLoadResult Load(ImageFileGroup group)
    {
        var entry = group.PrimaryFile;

        using var image = entry.FileType == ImageFileType.Raw
            ? LoadRaw(entry.FullPath)
            : LoadStandard(entry.FullPath);

        image.AutoOrient();

        // Extract linear pixels FIRST from the unmodified 16-bit image,
        // before CreateDisplayBitmap reduces it to 8-bit in-place.
        var pixelData = ExtractLinearPixels(image);

        // Clone for display bitmap since CreateDisplayBitmap mutates colorspace/depth
        using var displayClone = (MagickImage)image.Clone();
        var displayBitmap = CreateDisplayBitmap(displayClone);

        var histogramData = HistogramData.FromPixels(pixelData, image.Width, image.Height);

        return new ImageLoadResult
        {
            DisplayBitmap = displayBitmap,
            LinearPixels = pixelData,
            PixelWidth = image.Width,
            PixelHeight = image.Height,
            Histogram = histogramData,
            FileName = entry.FileName,
            IsRaw = entry.FileType == ImageFileType.Raw
        };
    }

    private static MagickImage LoadRaw(string filePath)
    {
        var settings = new MagickReadSettings();
        settings.SetDefine("dcraw:use-camera-wb", "true");
        settings.SetDefine("dcraw:output-bps", "16");

        var image = new MagickImage(filePath, settings);
        image.Depth = 16;
        return image;
    }

    private static MagickImage LoadStandard(string filePath)
    {
        return new MagickImage(filePath);
    }

    /// <summary>
    /// Pre-computed LUT that converts 16-bit sRGB gamma values to 16-bit linear.
    /// Applies the inverse sRGB transfer function: linearize the gamma curve.
    /// </summary>
    private static readonly ushort[] SrgbToLinearLut = BuildSrgbToLinearLut();

    private static ushort[] BuildSrgbToLinearLut()
    {
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            double v = i / 65535.0;
            // Inverse sRGB companding
            double linear = v <= 0.04045
                ? v / 12.92
                : Math.Pow((v + 0.055) / 1.055, 2.4);
            lut[i] = (ushort)Math.Clamp(linear * 65535.0 + 0.5, 0, 65535);
        }
        return lut;
    }

    /// <summary>
    /// Extracts pixel data as 16-bit linear RGB (ushort per channel, 3 channels per pixel).
    /// Applies inverse sRGB gamma to convert from Magick.NET's gamma-encoded output to linear.
    /// </summary>
    private static ushort[] ExtractLinearPixels(MagickImage image)
    {
        using var pixels = image.GetPixelsUnsafe();
        var channelCount = (int)image.ChannelCount;
        var width = image.Width;
        var height = image.Height;

        // Get raw pixel values as 16-bit
        var rawPixels = pixels.ToArray();

        if (rawPixels == null)
            return [];

        // We need RGB only (3 channels per pixel)
        var result = new ushort[width * height * 3];

        if (channelCount >= 3)
        {
            for (int i = 0, j = 0; i < rawPixels.Length && j < result.Length; i += channelCount, j += 3)
            {
                result[j] = SrgbToLinearLut[rawPixels[i]];         // R
                result[j + 1] = SrgbToLinearLut[rawPixels[i + 1]]; // G
                result[j + 2] = SrgbToLinearLut[rawPixels[i + 2]]; // B
            }
        }

        return result;
    }

    /// <summary>
    /// Creates an 8-bit sRGB WriteableBitmap for display from the MagickImage.
    /// </summary>
    private static WriteableBitmap CreateDisplayBitmap(MagickImage image)
    {
        // Convert to 8-bit sRGB for display
        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;

        var width = image.Width;
        var height = image.Height;
        var bitmap = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgr32, null);

        bitmap.Lock();
        try
        {
            using var pixels = image.GetPixelsUnsafe();
            var stride = bitmap.BackBufferStride;
            var backBuffer = bitmap.BackBuffer;

            unsafe
            {
                var channelCount = (int)image.ChannelCount;
                var rawPixels = pixels.ToArray();

                if (rawPixels != null)
                {
                    var ptr = (byte*)backBuffer;
                    for (int y = 0; y < (int)height; y++)
                    {
                        var rowPtr = ptr + y * stride;
                        for (int x = 0; x < (int)width; x++)
                        {
                            var srcIdx = (y * (int)width + x) * channelCount;
                            var dstIdx = x * 4;

                            // MagickImage pixels are in RGB order after 8-bit conversion,
                            // but values may still be in 16-bit scale. Normalize.
                            byte r = (byte)(rawPixels[srcIdx] >> 8 == 0 ? rawPixels[srcIdx] : rawPixels[srcIdx] >> 8);
                            byte g = (byte)(rawPixels[srcIdx + 1] >> 8 == 0 ? rawPixels[srcIdx + 1] : rawPixels[srcIdx + 1] >> 8);
                            byte b = (byte)(rawPixels[srcIdx + 2] >> 8 == 0 ? rawPixels[srcIdx + 2] : rawPixels[srcIdx + 2] >> 8);

                            // WPF Bgr32: B, G, R, 0
                            rowPtr[dstIdx] = b;
                            rowPtr[dstIdx + 1] = g;
                            rowPtr[dstIdx + 2] = r;
                            rowPtr[dstIdx + 3] = 255;
                        }
                    }
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)width, (int)height));
        }
        finally
        {
            bitmap.Unlock();
        }

        bitmap.Freeze();
        return bitmap;
    }
}

public class ImageLoadResult
{
    public required WriteableBitmap DisplayBitmap { get; init; }
    public required ushort[] LinearPixels { get; init; }
    public required uint PixelWidth { get; init; }
    public required uint PixelHeight { get; init; }
    public required HistogramData Histogram { get; init; }
    public required string FileName { get; init; }
    public required bool IsRaw { get; init; }
}

public class HistogramData
{
    public const int BinCount = 256;

    public int[] Red { get; } = new int[BinCount];
    public int[] Green { get; } = new int[BinCount];
    public int[] Blue { get; } = new int[BinCount];
    public int[] Luminance { get; } = new int[BinCount];
    public int MaxCount { get; internal set; }

    public static HistogramData FromPixels(ushort[] linearPixels, uint width, uint height)
    {
        var data = new HistogramData();
        var pixelCount = (int)(width * height);

        for (int i = 0; i < pixelCount; i++)
        {
            var idx = i * 3;
            if (idx + 2 >= linearPixels.Length)
                break;

            // Map 16-bit to 8-bit bin
            int r = linearPixels[idx] >> 8;
            int g = linearPixels[idx + 1] >> 8;
            int b = linearPixels[idx + 2] >> 8;

            r = Math.Clamp(r, 0, 255);
            g = Math.Clamp(g, 0, 255);
            b = Math.Clamp(b, 0, 255);

            data.Red[r]++;
            data.Green[g]++;
            data.Blue[b]++;

            // Luminance: standard Rec. 709
            int lum = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
            lum = Math.Clamp(lum, 0, 255);
            data.Luminance[lum]++;
        }

        // Find max for normalization (excluding extremes which often spike)
        int max = 0;
        for (int i = 1; i < BinCount - 1; i++)
        {
            max = Math.Max(max, data.Red[i]);
            max = Math.Max(max, data.Green[i]);
            max = Math.Max(max, data.Blue[i]);
        }
        data.MaxCount = Math.Max(max, 1);

        return data;
    }
}
