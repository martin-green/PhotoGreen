using System.Collections.Concurrent;
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

        // Steps 2-4 (white balance -> exposure -> clamp -> tone curve) depend only on the
        // 16-bit input value, so they collapse into one lookup table per channel. This
        // removes two multiplies, a clamp and a float->int conversion per channel per pixel.
        var stageR = BuildChannelLut(toneLut, wbR * exposureMul);
        var stageG = BuildChannelLut(toneLut, wbG * exposureMul);
        var stageB = BuildChannelLut(toneLut, wbB * exposureMul);

        // When vibrance is off the saturation factor is the same constant for every pixel,
        // so the per-pixel min/max and the divide in the vibrance term are not needed.
        bool hasVibrance = vibAmount != 0.0;

        // With neutral saturation and vibrance, step 5 reduces to r = lum + (r - lum) * 1,
        // i.e. the identity. The whole pipeline then becomes a single byte lookup per
        // channel, so fuse the gamma step into the tables as well.
        bool isIdentitySaturation = !hasVibrance && satMul == 1.0;

        if (isIdentitySaturation)
        {
            var byteR = BuildGammaFusedLut(stageR);
            var byteG = BuildGammaFusedLut(stageG);
            var byteB = BuildGammaFusedLut(stageB);

            Parallel.For(0, height, y =>
            {
                var src = linearPixels.AsSpan(y * width * 3, width * 3);
                var dst = output.AsSpan(y * width * 3, width * 3);

                for (int i = 0; i + 2 < src.Length; i += 3)
                {
                    dst[i] = byteR[src[i]];
                    dst[i + 1] = byteG[src[i + 1]];
                    dst[i + 2] = byteB[src[i + 2]];
                }
            });
        }
        else
        {
            Parallel.For(0, height, y =>
            {
                var src = linearPixels.AsSpan(y * width * 3, width * 3);
                var dst = output.AsSpan(y * width * 3, width * 3);

                for (int i = 0; i + 2 < src.Length; i += 3)
                {
                    // 1-4. Fused white balance, exposure and tone curve
                    double r = stageR[src[i]];
                    double g = stageG[src[i + 1]];
                    double b = stageB[src[i + 2]];

                    // 5. Saturation & Vibrance (in linear space before gamma)
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;

                    double totalSat = satMul;
                    if (hasVibrance)
                    {
                        // Vibrance: adaptive saturation - boost less-saturated colors more
                        double maxC = Math.Max(r, Math.Max(g, b));
                        double minC = Math.Min(r, Math.Min(g, b));
                        double currentSat = maxC > 0 ? (maxC - minC) / maxC : 0;
                        totalSat *= 1.0 + vibAmount * (1.0 - currentSat);
                    }

                    r = lum + (r - lum) * totalSat;
                    g = lum + (g - lum) * totalSat;
                    b = lum + (b - lum) * totalSat;

                    // 6. Linear -> sRGB gamma
                    dst[i] = LinearToSrgb(r * (1.0 / 65535.0));
                    dst[i + 1] = LinearToSrgb(g * (1.0 / 65535.0));
                    dst[i + 2] = LinearToSrgb(b * (1.0 / 65535.0));
                }
            });
        }

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

    /// <summary>
    /// Folds the per-channel white balance and exposure multiplier into the tone curve,
    /// producing a direct 16-bit input -> 16-bit toned output table.
    /// </summary>
    private static ushort[] BuildChannelLut(ushort[] toneLut, double multiplier)
    {
        var lut = new ushort[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            // Matches the original per-pixel order: scale, clamp to 16-bit, then tone curve.
            var scaled = (int)Math.Clamp(i * multiplier, 0, 65535);
            lut[i] = toneLut[scaled];
        }
        return lut;
    }

    /// <summary>
    /// Collapses a 16-bit channel table and the sRGB gamma step into a single
    /// 16-bit input -> 8-bit output table. Only valid when saturation and vibrance
    /// are neutral, since those depend on all three channels together.
    /// </summary>
    private static byte[] BuildGammaFusedLut(ushort[] channelLut)
    {
        var lut = new byte[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            lut[i] = LinearToSrgb(channelLut[i] * (1.0 / 65535.0));
        }
        return lut;
    }

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
        // Unsharp mask built on a separable 3x3 box blur. Needs a full 3x3 neighbourhood,
        // so a sliver of an image has no interior to sharpen.
        if (width < 3 || height < 3)
            return;

        double strength = amount / 100.0;
        var blurred = new byte[pixels.Length];

        BoxBlur3x3(pixels, blurred, width, height);

        // Apply: sharpened = original + strength * (original - blurred).
        // Done in 16.16 fixed point to keep the inner loop off the FPU.
        int strengthFixed = (int)Math.Round(strength * 65536.0);

        Parallel.For(1, height - 1, y =>
        {
            int rowStart = y * width * 3;
            // Interior pixels only, matching the blurred region produced above.
            int from = rowStart + 3;
            int to = rowStart + (width - 1) * 3;

            for (int idx = from; idx < to; idx++)
            {
                int original = pixels[idx];
                int delta = original - blurred[idx];
                // Adding half a unit before the arithmetic shift rounds to nearest,
                // matching the intent of the original floating-point expression.
                int val = original + ((delta * strengthFixed + 32768) >> 16);
                pixels[idx] = (byte)Math.Clamp(val, 0, 255);
            }
        });
    }

    /// <summary>
    /// Separable 3x3 box blur writing the interior pixels of <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// The naive form reads 9 source bytes per channel per pixel. Separating it into a
    /// horizontal pass feeding a vertical pass means each row's horizontal sums are
    /// computed once and reused by the three output rows that need them, via a three-row
    /// ring buffer. Extra memory stays proportional to the row width, not the image.
    /// </remarks>
    private static void BoxBlur3x3(byte[] source, byte[] destination, int width, int height)
    {
        int stride = width * 3;

        Parallel.ForEach(
            Partitioner.Create(1, height - 1),
            () => new ushort[3][],
            (range, _, ring) =>
            {
                ring[0] ??= new ushort[stride];
                ring[1] ??= new ushort[stride];
                ring[2] ??= new ushort[stride];

                int first = range.Item1;

                // Prime the ring with the row above the first output row.
                HorizontalSums(source, ring[(first - 1) % 3], first - 1, stride);
                HorizontalSums(source, ring[first % 3], first, stride);

                for (int y = first; y < range.Item2; y++)
                {
                    HorizontalSums(source, ring[(y + 1) % 3], y + 1, stride);

                    var above = ring[(y - 1) % 3];
                    var middle = ring[y % 3];
                    var below = ring[(y + 1) % 3];

                    int rowStart = y * stride;
                    for (int i = 3; i < stride - 3; i++)
                    {
                        destination[rowStart + i] = (byte)((above[i] + middle[i] + below[i]) / 9);
                    }
                }

                return ring;
            },
            _ => { });
    }

    /// <summary>
    /// Fills <paramref name="sums"/> with the horizontal 3-tap sum for one row. Entries
    /// for the first and last pixel of the row are left at zero and are never read.
    /// </summary>
    private static void HorizontalSums(byte[] source, ushort[] sums, int y, int stride)
    {
        var row = source.AsSpan(y * stride, stride);

        for (int i = 3; i < stride - 3; i++)
        {
            sums[i] = (ushort)(row[i - 3] + row[i] + row[i + 3]);
        }
    }

    #endregion

    #region Histogram

    private static HistogramData ComputeHistogramFromOutput(byte[] output, int pixelCount)
        => HistogramData.FromBytes(output, pixelCount);

    #endregion
}

public class DevelopResult
{
    public required byte[] OutputPixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required HistogramData Histogram { get; init; }
}
