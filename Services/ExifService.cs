using System.IO;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public static class ExifService
{
    public static async Task<ExifInfo> ExtractAsync(string filePath)
    {
        return await Task.Run(() => Extract(filePath));
    }

    private static ExifInfo Extract(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            IExifProfile? exif = null;
            uint imgWidth = 0, imgHeight = 0;
            string colorSpace = "Unknown";

            // Try reading EXIF without full pixel decoding.
            // For raw files, Ping() is much faster and preserves the original EXIF.
            try
            {
                using var pinged = new MagickImage();
                pinged.Ping(filePath);
                exif = pinged.GetExifProfile();
                imgWidth = pinged.Width;
                imgHeight = pinged.Height;
                colorSpace = pinged.ColorSpace.ToString();
            }
            catch
            {
                // Ping can fail on some raw formats — fall back to full read
            }

            // If Ping didn't yield EXIF, try full read
            if (exif == null || exif.Values.Count() == 0)
            {
                try
                {
                    using var image = new MagickImage(filePath);
                    exif = image.GetExifProfile();
                    imgWidth = image.Width;
                    imgHeight = image.Height;
                    colorSpace = image.ColorSpace.ToString();
                }
                catch
                {
                    // Full read also failed
                }
            }

            // If we still have no EXIF and there's a JPEG sidecar with the same stem, try that
            if (exif == null || exif.Values.Count() == 0)
            {
                var dir = Path.GetDirectoryName(filePath) ?? "";
                var stem = Path.GetFileNameWithoutExtension(filePath);
                var jpegPath = FindCompanionJpeg(dir, stem);
                if (jpegPath != null)
                {
                    try
                    {
                        using var jpeg = new MagickImage();
                        jpeg.Ping(jpegPath);
                        exif = jpeg.GetExifProfile();
                    }
                    catch { }
                }
            }

            return new ExifInfo
            {
                CameraMake = GetExifValue(exif, ExifTag.Make),
                CameraModel = GetExifValue(exif, ExifTag.Model),
                LensModel = GetExifValue(exif, ExifTag.LensModel),
                FocalLength = FormatRational(exif, ExifTag.FocalLength, "mm"),
                Aperture = FormatFNumber(exif),
                ShutterSpeed = FormatExposureTime(exif),
                Iso = GetExifValues(exif, ExifTag.ISOSpeedRatings),
                ExposureCompensation = FormatRational(exif, ExifTag.ExposureBiasValue, "EV"),
                MeteringMode = FormatMeteringMode(exif),
                FlashMode = FormatFlash(exif),
                WhiteBalance = FormatWhiteBalance(exif),
                DateTaken = FormatDateTime(exif),
                Dimensions = imgWidth > 0 ? $"{imgWidth} x {imgHeight}" : "Unknown",
                FileSize = FormatFileSize(fileInfo.Length),
                ColorSpace = colorSpace,
                Software = GetExifValue(exif, ExifTag.Software)
            };
        }
        catch
        {
            return new ExifInfo
            {
                CameraMake = "Unable to read EXIF",
                Dimensions = "Unknown"
            };
        }
    }

    private static string? FindCompanionJpeg(string directory, string stem)
    {
        string[] extensions = [".jpg", ".jpeg", ".JPG", ".JPEG"];
        foreach (var ext in extensions)
        {
            var path = Path.Combine(directory, stem + ext);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? GetExifValue<T>(IExifProfile? exif, ExifTag<T> tag)
    {
        if (exif == null) return null;
        var value = exif.GetValue(tag);
        return value?.Value?.ToString()?.Trim();
    }

    private static string? GetExifValues(IExifProfile? exif, ExifTag<ushort[]> tag)
    {
        if (exif == null) return null;
        var value = exif.GetValue(tag);
        if (value?.Value == null || value.Value.Length == 0) return null;
        return string.Join(", ", value.Value);
    }

    private static string? FormatRational(IExifProfile? exif, ExifTag<Rational> tag, string suffix)
    {
        if (exif == null) return null;
        var value = exif.GetValue(tag);
        if (value == null) return null;
        if (value.Value.Denominator == 0) return null;
        var d = (double)value.Value.Numerator / value.Value.Denominator;
        return $"{d:0.#} {suffix}";
    }

    private static string? FormatRational(IExifProfile? exif, ExifTag<SignedRational> tag, string suffix)
    {
        if (exif == null) return null;
        var value = exif.GetValue(tag);
        if (value == null || value.Value.Denominator == 0) return null;
        var d = (double)value.Value.Numerator / value.Value.Denominator;
        return d >= 0 ? $"+{d:0.#} {suffix}" : $"{d:0.#} {suffix}";
    }

    private static string? FormatFNumber(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.FNumber);
        if (value == null) return null;
        if (value.Value.Denominator == 0) return null;
        var f = (double)value.Value.Numerator / value.Value.Denominator;
        return $"f/{f:0.#}";
    }

    private static string? FormatExposureTime(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.ExposureTime);
        if (value == null) return null;
        if (value.Value.Denominator == 0) return null;
        double seconds = (double)value.Value.Numerator / value.Value.Denominator;
        if (seconds >= 1)
            return $"{seconds:0.#}s";
        return $"1/{1.0 / seconds:0}s";
    }

    private static string? FormatMeteringMode(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.MeteringMode);
        if (value == null) return null;
        return value.Value switch
        {
            0 => "Unknown",
            1 => "Average",
            2 => "Center-weighted",
            3 => "Spot",
            4 => "Multi-spot",
            5 => "Multi-segment",
            6 => "Partial",
            _ => $"Other ({value.Value})"
        };
    }

    private static string? FormatFlash(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.Flash);
        if (value == null) return null;
        return (value.Value & 1) == 1 ? "Fired" : "No flash";
    }

    private static string? FormatWhiteBalance(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.WhiteBalance);
        if (value == null) return null;
        return value.Value switch
        {
            0 => "Auto",
            1 => "Manual",
            _ => $"Other ({value.Value})"
        };
    }

    private static string? FormatDateTime(IExifProfile? exif)
    {
        if (exif == null) return null;
        var value = exif.GetValue(ExifTag.DateTimeOriginal);
        return value?.Value?.Trim();
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB"
    };
}
