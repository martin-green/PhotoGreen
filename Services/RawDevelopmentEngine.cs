using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class RawDevelopmentEngine
{
    private const int LutSize = 65536;

    /// <summary>
    /// Processes 16-bit linear RGB pixel data through the full develop pipeline
    /// and produces an 8-bit sRGB WriteableBitmap plus updated histogram.
    /// </summary>
    public static DevelopResult Process(ushort[] linearPixels, int width, int height, DevelopSettings settings)
    {
        var pixelCount = width * height;
        var output = new byte[pixelCount * 3];

        // Pre-compute white balance multipliers
        var (wbR, wbG, wbB) = ComputeWhiteBalanceMultipliers(settings.Temperature, settings.Tint);

        // Pre-compute exposure multiplier
        double exposureMul = Math.Pow(2.0, settings.Exposure);

        // Build the tone curve LUT (maps 0..65535 ? 0..65535)
        var toneLut = BuildToneCurveLut(settings);

        // Saturation / vibrance parameters
        double satMul = 1.0 + settings.Saturation / 100.0;
        double vibAmount = settings.Vibrance / 100.0;

        // Process pixels in parallel by scanline
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int srcIdx = (y * width + x) * 3;
                int dstIdx = (y * width + x) * 3;

                // 1. Read linear 16-bit
                double r = linearPixels[srcIdx];
                double g = linearPixels[srcIdx + 1];
                double b = linearPixels[srcIdx + 2];

                // 2. White balance (channel multiply)
                r *= wbR;
                g *= wbG;
                b *= wbB;

                // 3. Exposure (EV multiply)
                r *= exposureMul;
                g *= exposureMul;
                b *= exposureMul;

                // Clamp to 16-bit range
                r = Math.Clamp(r, 0, 65535);
                g = Math.Clamp(g, 0, 65535);
                b = Math.Clamp(b, 0, 65535);

                // 4. Tone curve LUT (highlights, shadows, contrast, whites, blacks)
                r = toneLut[(int)r];
                g = toneLut[(int)g];
                b = toneLut[(int)b];

                // 5. Saturation & Vibrance (in linear space before gamma)
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;

                // Vibrance: adaptive saturation - boost less-saturated colors more
                double maxC = Math.Max(r, Math.Max(g, b));
                double minC = Math.Min(r, Math.Min(g, b));
                double currentSat = maxC > 0 ? (maxC - minC) / maxC : 0;
                double vibSat = 1.0 + vibAmount * (1.0 - currentSat);

                double totalSat = satMul * vibSat;

                r = lum + (r - lum) * totalSat;
                g = lum + (g - lum) * totalSat;
                b = lum + (b - lum) * totalSat;

                r = Math.Clamp(r, 0, 65535);
                g = Math.Clamp(g, 0, 65535);
                b = Math.Clamp(b, 0, 65535);

                // 6. Linear ? sRGB gamma
                output[dstIdx] = LinearToSrgb(r / 65535.0);
                output[dstIdx + 1] = LinearToSrgb(g / 65535.0);
                output[dstIdx + 2] = LinearToSrgb(b / 65535.0);
            }
        });

        // 7. Sharpening (unsharp mask on the 8-bit result)
        if (settings.Sharpness > 0)
        {
            ApplyUnsharpMask(output, width, height, settings.Sharpness);
        }

        // Build display bitmap on the calling thread (must be STA for WPF)
        // We return the raw bytes and let the caller create the bitmap on the UI thread
        var histogram = ComputeHistogramFromOutput(output, pixelCount);

        return new DevelopResult
        {
            OutputPixels = output,
            Width = width,
            Height = height,
            Histogram = histogram
        };
    }

    /// <summary>
    /// Creates a frozen WriteableBitmap from processed output bytes. Must be called on the UI thread.
    /// </summary>
    public static WriteableBitmap CreateBitmapFromResult(DevelopResult result)
    {
        var bitmap = new WriteableBitmap(result.Width, result.Height, 96, 96, PixelFormats.Bgr32, null);
        bitmap.Lock();
        try
        {
            unsafe
            {
                var ptr = (byte*)bitmap.BackBuffer;
                var stride = bitmap.BackBufferStride;

                Parallel.For(0, result.Height, y =>
                {
                    var rowPtr = ptr + y * stride;
                    for (int x = 0; x < result.Width; x++)
                    {
                        int srcIdx = (y * result.Width + x) * 3;
                        int dstIdx = x * 4;
                        rowPtr[dstIdx] = result.OutputPixels[srcIdx + 2];     // B
                        rowPtr[dstIdx + 1] = result.OutputPixels[srcIdx + 1]; // G
                        rowPtr[dstIdx + 2] = result.OutputPixels[srcIdx];     // R
                        rowPtr[dstIdx + 3] = 255;
                    }
                });
            }
            bitmap.AddDirtyRect(new Int32Rect(0, 0, result.Width, result.Height));
        }
        finally
        {
            bitmap.Unlock();
        }
        bitmap.Freeze();
        return bitmap;
    }

    #region Tone Curve LUT

    private static ushort[] BuildToneCurveLut(DevelopSettings s)
    {
        var lut = new ushort[LutSize];

        double contrast = s.Contrast / 100.0;
        double highlights = s.Highlights / 100.0;
        double shadows = s.Shadows / 100.0;
        double whites = s.Whites / 100.0;
        double blacks = s.Blacks / 100.0;

        for (int i = 0; i < LutSize; i++)
        {
            double v = i / 65535.0; // normalized 0..1

            // Blacks: lift or crush the dark end
            v = v + blacks * 0.1 * (1.0 - v) * (1.0 - v);

            // Shadows: affect lower-mid tones (centered around 0.25)
            double shadowWeight = 1.0 - SmootherStep(v, 0.0, 0.5);
            v += shadows * 0.15 * shadowWeight;

            // Highlights: affect upper-mid tones (centered around 0.75)
            double highlightWeight = SmootherStep(v, 0.5, 1.0);
            v -= highlights * 0.15 * highlightWeight;

            // Whites: push or pull the bright end
            v = v + whites * 0.1 * v * v;

            // Contrast: S-curve centered at midpoint
            v = Math.Clamp(v, 0, 1);
            if (contrast != 0)
            {
                // Apply S-curve via adjusted sigmoid
                double midpoint = 0.5;
                double factor = 1.0 + contrast;
                v = midpoint + (v - midpoint) * factor / (1.0 + Math.Abs(v - midpoint) * contrast);
            }

            v = Math.Clamp(v, 0, 1);
            lut[i] = (ushort)(v * 65535.0);
        }

        return lut;
    }

    private static double SmootherStep(double x, double edge0, double edge1)
    {
        x = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
    }

    #endregion

    #region White Balance

    private static (double r, double g, double b) ComputeWhiteBalanceMultipliers(double tempK, double tint)
    {
        // Compute relative shift from D55 (5500K) daylight reference
        // Temperature: blue ? amber (affects B vs R)
        // Tint: green ? magenta (affects G)
        double refTemp = 5500.0;
        double tempShift = (tempK - refTemp) / refTemp;

        double rMul = 1.0 + tempShift * 0.3;
        double gMul = 1.0 - tint / 100.0 * 0.3;
        double bMul = 1.0 - tempShift * 0.3;

        // Normalize so green channel is at ~1.0 base
        double norm = 1.0 / gMul;
        return (rMul * norm, 1.0, bMul * norm);
    }

    #endregion

    #region sRGB Gamma

    private static readonly byte[] SrgbLut = BuildSrgbLut();

    private static byte[] BuildSrgbLut()
    {
        // Pre-compute 4096-entry LUT for fast linear?sRGB
        var lut = new byte[4096];
        for (int i = 0; i < 4096; i++)
        {
            double v = i / 4095.0;
            double srgb = v <= 0.0031308
                ? v * 12.92
                : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp(srgb * 255.0 + 0.5, 0, 255);
        }
        return lut;
    }

    private static byte LinearToSrgb(double linear)
    {
        int idx = (int)(Math.Clamp(linear, 0, 1) * 4095.0);
        return SrgbLut[idx];
    }

    #endregion

    #region Unsharp Mask

    private static void ApplyUnsharpMask(byte[] pixels, int width, int height, double amount)
    {
        // Simple 3×3 box-blur based unsharp mask
        double strength = amount / 100.0;
        var blurred = new byte[pixels.Length];

        // Box blur (luminance-aware on each channel)
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            sum += pixels[((y + dy) * width + (x + dx)) * 3 + c];
                        }
                    }
                    blurred[(y * width + x) * 3 + c] = (byte)(sum / 9);
                }
            }
        });

        // Apply: sharpened = original + strength * (original - blurred)
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int idx = (y * width + x) * 3 + c;
                    double val = pixels[idx] + strength * (pixels[idx] - blurred[idx]);
                    pixels[idx] = (byte)Math.Clamp(val, 0, 255);
                }
            }
        });
    }

    #endregion

    #region Histogram

    private static HistogramData ComputeHistogramFromOutput(byte[] output, int pixelCount)
    {
        var data = new HistogramData();
        for (int i = 0; i < pixelCount; i++)
        {
            int idx = i * 3;
            int r = output[idx];
            int g = output[idx + 1];
            int b = output[idx + 2];

            data.Red[r]++;
            data.Green[g]++;
            data.Blue[b]++;

            int lum = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
            lum = Math.Clamp(lum, 0, 255);
            data.Luminance[lum]++;
        }

        int max = 0;
        for (int i = 1; i < HistogramData.BinCount - 1; i++)
        {
            max = Math.Max(max, data.Red[i]);
            max = Math.Max(max, data.Green[i]);
            max = Math.Max(max, data.Blue[i]);
        }
        data.MaxCount = Math.Max(max, 1);

        return data;
    }

    #endregion
}

public class DevelopResult
{
    public required byte[] OutputPixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required HistogramData Histogram { get; init; }
}
