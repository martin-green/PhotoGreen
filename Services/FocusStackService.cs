using System.IO;
using ImageMagick;

namespace PhotoGreen.Services;

/// <summary>
/// Combines a series of shots of the same subject taken at different focus distances
/// into one image, keeping the sharpest pixels from each source.
/// Each frame is aligned to the first one (translation and scale, to compensate for
/// handheld drift and focus breathing), a local contrast measure is computed, and the
/// output takes every pixel from the frame with the highest smoothed sharpness.
/// </summary>
public static class FocusStackService
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw"
    };

    public static Task<string> StackAsync(
        IReadOnlyList<string> filePaths,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Stack(filePaths, outputPath, progress, cancellationToken), cancellationToken);
    }

    private static string Stack(
        IReadOnlyList<string> filePaths,
        string outputPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (filePaths.Count < 2)
            throw new ArgumentException("At least two images are required for focus stacking.", nameof(filePaths));

        progress?.Report($"Focus stack: loading 1/{filePaths.Count}...");
        using var reference = LoadImage(filePaths[0]);
        var width = (int)reference.Width;
        var height = (int)reference.Height;
        var pixelCount = width * height;

        var refPyramid = BuildPyramid(reference);

        var result = ExtractRgb(reference);
        var best = ComputeSharpness(result, width, height);
        var colorProfile = reference.GetColorProfile();

        // Each frame starts its search from the previous frame's transform: when the camera
        // creeps towards the subject the magnification drifts steadily across the series.
        var previous = new Transform(1.0, 0, 0, 0);

        for (int i = 1; i < filePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Focus stack: aligning {i + 1}/{filePaths.Count}...");

            using var image = LoadImage(filePaths[i]);
            if (image.Width != reference.Width || image.Height != reference.Height)
                image.Resize(new MagickGeometry(reference.Width, reference.Height) { IgnoreAspectRatio = true });

            var transform = Align(refPyramid, BuildPyramid(image), previous);
            previous = transform;

            ct.ThrowIfCancellationRequested();

            var fullFactor = (double)width / refPyramid[0].Width;
            var s = transform.Scale;
            var tx = transform.Tx * fullFactor;
            var ty = transform.Ty * fullFactor;
            var angleDeg = transform.Angle * 180.0 / Math.PI;

            if (Math.Abs(s - 1.0) > 1e-6 || Math.Abs(angleDeg) > 1e-4 || Math.Abs(tx) > 0.25 || Math.Abs(ty) > 0.25)
            {
                // Sampling maps reference p to source c + s·R(θ)(p - c) + t; the distortion is
                // the inverse (forward source-to-reference) mapping.
                var cx = width / 2.0;
                var cy = height / 2.0;
                image.VirtualPixelMethod = VirtualPixelMethod.Edge;
                image.Distort(DistortMethod.ScaleRotateTranslate, cx + tx, cy + ty, 1.0 / s, -angleDeg, cx, cy);
                if (image.Width != reference.Width || image.Height != reference.Height)
                    image.Crop(new MagickGeometry(0, 0, reference.Width, reference.Height));
                image.ResetPage();
            }

            progress?.Report($"Focus stack: merging {i + 1}/{filePaths.Count}...");
            var rgb = ExtractRgb(image);
            var sharpness = ComputeSharpness(rgb, width, height);

            Parallel.For(0, height, y =>
            {
                var row = y * width;
                for (int x = 0; x < width; x++)
                {
                    var p = row + x;
                    if (sharpness[p] > best[p])
                    {
                        best[p] = sharpness[p];
                        var k = p * 3;
                        result[k] = rgb[k];
                        result[k + 1] = rgb[k + 1];
                        result[k + 2] = rgb[k + 2];
                    }
                }
            });
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report("Focus stack: saving...");

        using var output = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        output.Depth = 16;
        using (var pixels = output.GetPixels())
        {
            var channels = (int)output.ChannelCount;
            var data = new ushort[pixelCount * channels];
            for (int p = 0, k = 0, d = 0; p < pixelCount; p++, k += 3, d += channels)
            {
                data[d] = result[k];
                data[d + 1] = result[k + 1];
                data[d + 2] = result[k + 2];
                if (channels > 3)
                    data[d + 3] = ushort.MaxValue;
            }
            pixels.SetPixels(data);
        }

        if (colorProfile != null)
            output.SetProfile(colorProfile);

        output.Format = MagickFormat.Tiff;
        output.Settings.Compression = CompressionMethod.Zip;
        output.Write(outputPath);

        progress?.Report($"Focus stack saved: {Path.GetFileName(outputPath)}");
        return outputPath;
    }

    /// <summary>Width of the finest alignment level; full-resolution precision is ~2 px.</summary>
    private const int FineWidth = 2560;

    /// <summary>Width at which the pyramid stops halving.</summary>
    private const int MinLevelWidth = 80;

    private sealed record Level(float[] Data, int Width, int Height);

    /// <summary>
    /// Builds a grayscale pyramid with one fast box downscale of the full image, then
    /// cheap 2x halving in float space. Index 0 is the finest level.
    /// </summary>
    private static List<Level> BuildPyramid(MagickImage image)
    {
        var w = (int)Math.Min(FineWidth, image.Width);
        var h = Math.Max(1, (int)Math.Round((double)image.Height * w / image.Width));

        float[] gray;
        using (var small = (MagickImage)image.Clone())
        {
            // Scale is a box average: much faster than Resize and fine for alignment.
            small.Scale(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            var rgb = ExtractRgb(small);
            gray = new float[w * h];
            for (int p = 0; p < gray.Length; p++)
                gray[p] = (rgb[p * 3] + rgb[p * 3 + 1] + rgb[p * 3 + 2]) / (3f * 65535f);
        }

        // Blurring makes frames with different focus comparable.
        var tmp = new float[gray.Length];
        BoxBlur(gray, tmp, w, h, 2);

        // Normalize brightness so small exposure differences don't bias alignment.
        var mean = gray.Average();
        if (mean > 1e-6f)
            for (int p = 0; p < gray.Length; p++)
                gray[p] /= mean;

        var levels = new List<Level> { new(gray, w, h) };
        while (w / 2 >= MinLevelWidth && h / 2 >= 8)
        {
            var nw = w / 2;
            var nh = h / 2;
            var next = new float[nw * nh];
            var src = levels[^1].Data;
            var sw = w;
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    var i = 2 * y * sw + 2 * x;
                    next[y * nw + x] = 0.25f * (src[i] + src[i + 1] + src[i + sw] + src[i + sw + 1]);
                }
            levels.Add(new Level(next, nw, nh));
            w = nw;
            h = nh;
        }

        return levels;
    }

    /// <summary>
    /// Similarity transform mapping reference coordinates to frame coordinates around the
    /// image centre: src = c + Scale·R(Angle)(p - c) + (Tx, Ty). Angle in radians, shift in
    /// finest-pyramid-level pixels.
    /// </summary>
    private readonly record struct Transform(double Scale, double Angle, double Tx, double Ty);

    private const double Degree = Math.PI / 180.0;

    /// <summary>
    /// Coarse-to-fine search: a wide search at the smallest level around the initial guess,
    /// then a local refinement of scale, rotation and shift at each finer level.
    /// </summary>
    private static Transform Align(List<Level> reference, List<Level> frame, Transform initial)
    {
        var top = reference.Count - 1;
        var topFactor = (double)reference[top].Width / reference[0].Width;
        var scaleStep = 0.01;
        var angleStep = 0.75 * Degree;
        var r = reference[top];

        var t = Search(r, frame[top],
            initial with { Tx = initial.Tx * topFactor, Ty = initial.Ty * topFactor },
            scaleRange: 0.08, scaleStep: scaleStep,
            angleRange: 3 * Degree, angleStep: angleStep,
            shiftRange: 5, shiftStep: 1);

        for (int l = top - 1; l >= 0; l--)
        {
            r = reference[l];
            var factor = (double)r.Width / reference[l + 1].Width;
            t = Search(r, frame[l], t with { Tx = t.Tx * factor, Ty = t.Ty * factor },
                scaleRange: scaleStep, scaleStep: scaleStep / 2,
                angleRange: angleStep, angleStep: angleStep / 2,
                shiftRange: 1.5, shiftStep: 0.5);
            scaleStep /= 2;
            angleStep /= 2;
        }

        // Extra passes at the finest level for sub-pixel precision; residual misalignment
        // of even a couple of pixels shows up as doubled edges in the stack.
        var shiftStep = 0.25;
        for (int pass = 0; pass < 2; pass++)
        {
            t = Search(reference[0], frame[0], t,
                scaleRange: scaleStep, scaleStep: scaleStep / 2,
                angleRange: angleStep, angleStep: angleStep / 2,
                shiftRange: shiftStep * 2, shiftStep: shiftStep);
            scaleStep /= 2;
            angleStep /= 2;
            shiftStep /= 2;
        }

        return t;
    }

    /// <summary>Sub-samples large levels so each candidate costs ~15k samples.</summary>
    private static int StrideFor(Level level) =>
        Math.Max(1, (int)Math.Sqrt(level.Width * level.Height / 15000.0));

    private static MagickImage LoadImage(string path)
    {
        MagickImage image;
        if (RawExtensions.Contains(Path.GetExtension(path)))
        {
            var settings = new MagickReadSettings();
            settings.SetDefine("dcraw:use-camera-wb", "true");
            settings.SetDefine("dcraw:output-bps", "16");
            image = new MagickImage(path, settings);
        }
        else
        {
            image = new MagickImage(path);
        }

        image.AutoOrient();
        image.Depth = 16;
        if (image.ColorSpace != ColorSpace.sRGB)
            image.ColorSpace = ColorSpace.sRGB;
        image.HasAlpha = false;
        return image;
    }

    /// <summary>Extracts interleaved 16-bit RGB values.</summary>
    private static ushort[] ExtractRgb(MagickImage image)
    {
        using var pixels = image.GetPixelsUnsafe();
        var raw = pixels.ToArray() ?? [];
        var channels = (int)image.ChannelCount;
        var count = (int)(image.Width * image.Height);
        var rgb = new ushort[count * 3];

        for (int p = 0, s = 0, d = 0; p < count && s + 2 < raw.Length; p++, s += channels, d += 3)
        {
            rgb[d] = raw[s];
            rgb[d + 1] = channels >= 3 ? raw[s + 1] : raw[s];
            rgb[d + 2] = channels >= 3 ? raw[s + 2] : raw[s];
        }

        return rgb;
    }

    /// <summary>
    /// Grid search for the similarity transform that best maps the reference onto the
    /// frame, measured as mean absolute difference. Samples falling outside the frame are
    /// ignored so large zoom differences are not penalised by the borders.
    /// </summary>
    private static Transform Search(
        Level referenceLevel, Level frameLevel, Transform center,
        double scaleRange, double scaleStep, double angleRange, double angleStep,
        double shiftRange, double shiftStep)
    {
        var reference = referenceLevel.Data;
        var frame = frameLevel.Data;
        var w = referenceLevel.Width;
        var h = referenceLevel.Height;
        var stride = StrideFor(referenceLevel);

        var candidates = new List<Transform>();
        for (var s = center.Scale - scaleRange; s <= center.Scale + scaleRange + 1e-9; s += scaleStep)
            for (var a = center.Angle - angleRange; a <= center.Angle + angleRange + 1e-9; a += angleStep)
                for (var ty = center.Ty - shiftRange; ty <= center.Ty + shiftRange + 1e-9; ty += shiftStep)
                    for (var tx = center.Tx - shiftRange; tx <= center.Tx + shiftRange + 1e-9; tx += shiftStep)
                        candidates.Add(new Transform(s, a, tx, ty));

        var scores = new double[candidates.Count];
        var cx = w / 2.0;
        var cy = h / 2.0;
        var marginX = (int)(w * 0.05);
        var marginY = (int)(h * 0.05);
        var minSamples = ((w - 2 * marginX) / stride) * ((h - 2 * marginY) / stride) / 4;

        Parallel.For(0, candidates.Count, i =>
        {
            var c = candidates[i];
            var cos = c.Scale * Math.Cos(c.Angle);
            var sin = c.Scale * Math.Sin(c.Angle);
            double sum = 0;
            int n = 0;
            for (int y = marginY; y < h - marginY; y += stride)
            {
                var dy = y - cy;
                for (int x = marginX; x < w - marginX; x += stride)
                {
                    var dx = x - cx;
                    var sx = cx + cos * dx - sin * dy + c.Tx;
                    var sy = cy + sin * dx + cos * dy + c.Ty;
                    if (sx < 0 || sy < 0 || sx > w - 1.001 || sy > h - 1.001)
                        continue;
                    sum += Math.Abs(reference[y * w + x] - Sample(frame, w, h, sx, sy));
                    n++;
                }
            }
            scores[i] = n >= minSamples && n > 0 ? sum / n : double.MaxValue;
        });

        var bestIndex = 0;
        for (int i = 1; i < scores.Length; i++)
            if (scores[i] < scores[bestIndex])
                bestIndex = i;

        return candidates[bestIndex];
    }

    private static float Sample(float[] img, int w, int h, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1.001);
        y = Math.Clamp(y, 0, h - 1.001);
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);
        var i = y0 * w + x0;
        var top = img[i] + (img[i + 1] - img[i]) * fx;
        var bottom = img[i + w] + (img[i + w + 1] - img[i + w]) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>
    /// Per-pixel sharpness: absolute Laplacian of luminance, smoothed so the selection
    /// follows regions rather than individual noisy pixels.
    /// </summary>
    private static float[] ComputeSharpness(ushort[] rgb, int w, int h)
    {
        var gray = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0, p = y * w; x < w; x++, p++)
                gray[p] = 0.299f * rgb[p * 3] + 0.587f * rgb[p * 3 + 1] + 0.114f * rgb[p * 3 + 2];
        });

        var measure = new float[w * h];
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1, p = y * w + 1; x < w - 1; x++, p++)
            {
                var lap = 4 * gray[p] - gray[p - 1] - gray[p + 1] - gray[p - w] - gray[p + w];
                measure[p] = lap * lap;
            }
        });

        // A wide window lets the in-focus frame's strong edge energy dominate the soft halo
        // that out-of-focus frames spread around the same edge, avoiding ghost outlines.
        var radius = Math.Max(4, w / 250);
        BoxBlur(measure, gray, w, h, radius);
        BoxBlur(measure, gray, w, h, radius);
        BoxBlur(measure, gray, w, h, radius);
        return measure;
    }

    /// <summary>Separable box blur in place; <paramref name="tmp"/> must match the size.</summary>
    private static void BoxBlur(float[] data, float[] tmp, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            double sum = 0;
            for (int x = -r; x <= r; x++)
                sum += data[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (float)(sum / (2 * r + 1));
                sum += data[row + Math.Min(x + r + 1, w - 1)] - data[row + Math.Max(x - r, 0)];
            }
        });

        Parallel.For(0, w, x =>
        {
            double sum = 0;
            for (int y = -r; y <= r; y++)
                sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                data[y * w + x] = (float)(sum / (2 * r + 1));
                sum += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Max(y - r, 0) * w + x];
            }
        });
    }
}
