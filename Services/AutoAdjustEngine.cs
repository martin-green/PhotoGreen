using PhotoGreen.Models;

namespace PhotoGreen.Services;

/// <summary>
/// Lightroom-style auto adjustment engine.
/// 
/// Strategy:
/// 1. Exposure: bring midtones to a proper level using a robust percentile average
/// 2. Highlights: aggressively pull down to protect bright detail (always positive in our convention)
/// 3. Shadows: lift to reveal dark detail (always positive)
/// 4. Whites: small push to ensure clean white point after highlight pulldown
/// 5. Blacks: small negative to ensure deep blacks after shadow lift
/// 6. Contrast: modest S-curve for punch
/// 
/// The tone curve in RawDevelopmentEngine applies:
///   shadows slider * 0.15 ? lifts lower tones (positive = lift)
///   highlights slider * 0.15 ? pulled from upper tones (positive = pull down)
///   whites slider * 0.1 * v² ? pushes bright end
///   blacks slider * 0.1 * (1-v)² ? lifts/crushes dark end
/// Then sRGB gamma: linear 0.05?sRGB 0.24, 0.10?0.35, 0.18?0.46, 0.50?0.74
/// </summary>
public static class AutoAdjustEngine
{
    public static DevelopSettings Analyze(ushort[] linearPixels, int width, int height)
    {
        return AnalyzeWeighted(linearPixels, width, height, 0, 0, width, height, useCenter: true);
    }

    public static DevelopSettings Analyze(ushort[] linearPixels, int width, int height, SceneHint? sceneHint)
    {
        var settings = Analyze(linearPixels, width, height);
        return ApplySceneHint(settings, sceneHint);
    }

    public static DevelopSettings AnalyzeRegion(ushort[] linearPixels, int width, int height,
        int rx, int ry, int rw, int rh)
    {
        rx = Math.Clamp(rx, 0, width - 1);
        ry = Math.Clamp(ry, 0, height - 1);
        rw = Math.Clamp(rw, 1, width - rx);
        rh = Math.Clamp(rh, 1, height - ry);
        return AnalyzeWeighted(linearPixels, width, height, rx, ry, rw, rh, useCenter: false);
    }

    public static DevelopSettings AnalyzeRegion(ushort[] linearPixels, int width, int height,
        int rx, int ry, int rw, int rh, SceneHint? sceneHint)
    {
        var settings = AnalyzeRegion(linearPixels, width, height, rx, ry, rw, rh);
        return ApplySceneHint(settings, sceneHint);
    }

    private static DevelopSettings ApplySceneHint(DevelopSettings settings, SceneHint? hint)
    {
        if (hint == null || hint.SceneType == SceneType.Unknown)
            return settings;

        return settings with
        {
            Exposure = Math.Clamp(settings.Exposure + hint.ExposureBias, -5.0, 5.0),
            Contrast = Math.Clamp(settings.Contrast + hint.ContrastBias, -100, 100),
            Temperature = Math.Clamp(settings.Temperature + hint.TemperatureBias, 2000, 10000),
            Saturation = Math.Clamp(settings.Saturation + hint.SaturationBias, -100, 100),
            Shadows = Math.Clamp(settings.Shadows + hint.ShadowBias, -100, 100)
        };
    }

    private static DevelopSettings AnalyzeWeighted(ushort[] linearPixels, int stride, int totalHeight,
        int rx, int ry, int rw, int rh, bool useCenter)
    {
        if (rw * rh == 0)
            return DevelopSettings.Default;

        double cx = rx + rw / 2.0;
        double cy = ry + rh / 2.0;
        double sigmaX = rw / 3.0;
        double sigmaY = rh / 3.0;

        var lumHistogram = new double[65536];
        double totalWeight = 0;
        double sumR = 0, sumG = 0, sumB = 0;

        for (int y = ry; y < ry + rh; y++)
        {
            for (int x = rx; x < rx + rw; x++)
            {
                int idx = (y * stride + x) * 3;
                int r = linearPixels[idx];
                int g = linearPixels[idx + 1];
                int b = linearPixels[idx + 2];

                double w = 1.0;
                if (useCenter)
                {
                    double dx = (x - cx) / sigmaX;
                    double dy = (y - cy) / sigmaY;
                    w = Math.Max(Math.Exp(-0.5 * (dx * dx + dy * dy)), 0.1);
                }

                sumR += r * w;
                sumG += g * w;
                sumB += b * w;
                totalWeight += w;

                int lum = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
                lum = Math.Clamp(lum, 0, 65535);
                lumHistogram[lum] += w;
            }
        }

        if (totalWeight == 0)
            return DevelopSettings.Default;

        // Gather key percentiles
        double p01 = FindWeightedPercentile(lumHistogram, totalWeight, 0.01);
        double p05 = FindWeightedPercentile(lumHistogram, totalWeight, 0.05);
        double p25 = FindWeightedPercentile(lumHistogram, totalWeight, 0.25);
        double p50 = FindWeightedPercentile(lumHistogram, totalWeight, 0.50);
        double p75 = FindWeightedPercentile(lumHistogram, totalWeight, 0.75);
        double p95 = FindWeightedPercentile(lumHistogram, totalWeight, 0.95);
        double p99 = FindWeightedPercentile(lumHistogram, totalWeight, 0.99);

        // Normalize to 0..1
        double np01 = p01 / 65535.0;
        double np05 = p05 / 65535.0;
        double np25 = p25 / 65535.0;
        double np50 = p50 / 65535.0;
        double np75 = p75 / 65535.0;
        double np95 = p95 / 65535.0;
        double np99 = p99 / 65535.0;

        // =====================================================================
        // 1. EXPOSURE — bring midtones to proper level
        // =====================================================================
        // Use average of p40–p60 for stability (avoids median being skewed
        // by a bimodal distribution like backlit subjects).
        double midtoneAvg = FindWeightedPercentileAverage(lumHistogram, totalWeight, 0.40, 0.60) / 65535.0;

        // After exposure + tone curve + sRGB gamma, we want midtones around
        // sRGB 40–45% (102–115/255). Working backwards through sRGB gamma:
        //   sRGB 0.42 ? linear ~0.145
        // So target the midtone average at ~0.145 in linear before tone curve.
        // The tone curve with default settings is identity, so target 0.145.
        // But shadows/highlights adjustments will shift things, so we target
        // a bit lower and let the shadow lift bring it up.
        double targetMidtone = 0.10;

        double exposureEV = 0;
        if (midtoneAvg > 0.0001)
        {
            exposureEV = Math.Log2(targetMidtone / midtoneAvg);
        }

        // Scene classification: detect severe underexposure
        // If median is very dark (< 2% linear), the image is severely underexposed
        // Allow more aggressive correction
        double maxEV = np50 < 0.02 ? 3.5 : 2.5;
        exposureEV = Math.Clamp(exposureEV, -2.5, maxEV);
        exposureEV = Math.Round(exposureEV * 20.0) / 20.0;

        double exposureMul = Math.Pow(2.0, exposureEV);

        // Corrected percentiles after exposure
        double cp05 = Math.Min(np05 * exposureMul, 1.0);
        double cp25 = Math.Min(np25 * exposureMul, 1.0);
        double cp75 = Math.Min(np75 * exposureMul, 1.0);
        double cp95 = Math.Min(np95 * exposureMul, 1.0);
        double cp99 = Math.Min(np99 * exposureMul, 1.0);
        double cp01 = Math.Min(np01 * exposureMul, 1.0);

        // =====================================================================
        // 2. HIGHLIGHTS — aggressively protect bright detail (Lightroom style)
        // =====================================================================
        // In our pipeline, positive Highlights = pull down bright areas.
        // Lightroom aggressively reduces highlights to prevent clipping.
        // If p95 after exposure is above 0.6 linear (maps to ~80% sRGB),
        // pull down proportionally. More clipping = more pulldown.
        double highlights = 0;
        if (cp95 > 0.4)
        {
            // Scale: cp95 at 0.4 ? 0, cp95 at 1.0 ? +70
            highlights = (cp95 - 0.4) / 0.6 * 70.0;
        }
        // Even moderate bright areas get some protection
        if (cp99 > 0.7)
        {
            highlights = Math.Max(highlights, (cp99 - 0.7) / 0.3 * 80.0);
        }
        highlights = Math.Clamp(Math.Round(highlights), 0, 80);

        // =====================================================================
        // 3. SHADOWS — lift to reveal dark detail
        // =====================================================================
        // In our pipeline, positive Shadows = lift dark areas.
        // If shadows are very dark after exposure, lift them.
        // The darker the p25 is, the more we lift.
        // Keep this moderate — the goal is to reveal detail, not flatten.
        double shadows = 0;
        if (cp25 < 0.12)
        {
            // Scale: cp25 at 0.12 ? 0, cp25 at 0.0 ? +45
            shadows = (0.12 - cp25) / 0.12 * 45.0;
        }
        shadows = Math.Clamp(Math.Round(shadows), 0, 45);

        // =====================================================================
        // 4. WHITES — ensure clean white point after highlight pulldown
        // =====================================================================
        double whites = 0;
        double effectiveBright = cp99 - highlights / 100.0 * 0.15;
        if (effectiveBright < 0.6)
        {
            whites = (0.6 - effectiveBright) / 0.6 * 40.0;
        }
        else if (effectiveBright > 0.9)
        {
            whites = -(effectiveBright - 0.9) / 0.1 * 20.0;
        }
        whites = Math.Clamp(Math.Round(whites), -20, 40);

        // =====================================================================
        // 5. BLACKS — anchor the black point
        // =====================================================================
        // The tone curve applies:  shadows * 0.15 * weight  at the dark end
        //                          blacks  * 0.10 * (1-v)²  at the dark end
        // At v=0: shadow lift = shadows/100 * 0.15
        //         black crush = blacks/100  * 0.10
        // To fully cancel shadow lift at the black point:
        //   blacks = -(shadows * 0.15 / 0.10) = -(shadows * 1.5)
        // We go slightly beyond to ensure true blacks stay black.
        double blacks = 0;
        if (shadows > 5)
        {
            blacks = -(shadows * 1.6);
        }
        // If the dark point is already elevated even without shadow lift, crush it
        if (cp01 > 0.02)
        {
            blacks = Math.Min(blacks, -(cp01 - 0.02) / 0.02 * 30.0);
        }
        blacks = Math.Clamp(Math.Round(blacks), -80, 5);

        // =====================================================================
        // 6. CONTRAST — modest punch based on tonal range
        // =====================================================================
        // After shadows/highlights/blacks/whites adjustments, add contrast
        // to give the image "punch". Lightroom typically adds slight contrast.
        double dynamicRange = cp95 - cp05;
        double contrast = 0;
        if (dynamicRange < 0.5)
        {
            // Low dynamic range ? needs more contrast
            contrast = (0.5 - dynamicRange) / 0.5 * 20.0;
        }
        // Always add a small baseline contrast for punch
        contrast = Math.Max(contrast, 5);
        contrast = Math.Clamp(Math.Round(contrast), 0, 25);

        // =====================================================================
        // 7. WHITE BALANCE — gray-world with center weight
        // =====================================================================
        double avgR = sumR / totalWeight;
        double avgG = sumG / totalWeight;
        double avgB = sumB / totalWeight;

        double temperature = 5500.0;
        double tint = 0;

        if (avgG > 0)
        {
            double rgRatio = avgR / avgG;
            double bgRatio = avgB / avgG;

            double rbBalance = (bgRatio - rgRatio) / (bgRatio + rgRatio);
            temperature = 5500.0 + rbBalance * 3000.0;
            temperature = Math.Clamp(Math.Round(temperature / 50.0) * 50.0, 3500, 8500);

            double gStrength = avgG / ((avgR + avgB) / 2.0);
            tint = (1.0 - gStrength) * 60.0;
            tint = Math.Clamp(Math.Round(tint), -40, 40);
        }

        return new DevelopSettings
        {
            Exposure = exposureEV,
            Contrast = contrast,
            Highlights = highlights,
            Shadows = shadows,
            Whites = whites,
            Blacks = blacks,
            Temperature = temperature,
            Tint = tint,
            Vibrance = 12,
            Saturation = 0,
            Sharpness = 0,
            NoiseReduction = 0
        };
    }

    private static double FindWeightedPercentile(double[] histogram, double totalWeight, double percentile)
    {
        double target = totalWeight * percentile;
        double cumulative = 0;

        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
                return i;
        }

        return 65535;
    }

    /// <summary>
    /// Returns the average luminance value between two percentiles.
    /// More robust than a single percentile for midtone estimation.
    /// </summary>
    private static double FindWeightedPercentileAverage(double[] histogram, double totalWeight,
        double lowPercentile, double highPercentile)
    {
        double lowTarget = totalWeight * lowPercentile;
        double highTarget = totalWeight * highPercentile;
        double cumulative = 0;
        double sum = 0;
        double count = 0;

        for (int i = 0; i < histogram.Length; i++)
        {
            double prev = cumulative;
            cumulative += histogram[i];

            if (cumulative >= lowTarget && prev < highTarget)
            {
                // This bin contributes to the percentile range
                double lo = Math.Max(0, lowTarget - prev);
                double hi = Math.Min(histogram[i], highTarget - prev);
                double contribution = Math.Max(0, hi - lo);
                sum += i * contribution;
                count += contribution;
            }
        }

        return count > 0 ? sum / count : FindWeightedPercentile(histogram, totalWeight, 0.5);
    }
}
