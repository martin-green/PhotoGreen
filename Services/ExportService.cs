using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public enum ExportFormat
{
    Jpeg,
    Png,
    Tiff,
    Jxl
}

public class ExportSettings
{
    public ExportFormat Format { get; set; } = ExportFormat.Jpeg;
    public int JpegQuality { get; set; } = 95;
    public string OutputPath { get; set; } = string.Empty;
}

public class ExportService
{
    public static void Export(DevelopResult developResult, ExportSettings exportSettings, CropSettings? crop = null)
    {
        var bitmap = RawDevelopmentEngine.CreateBitmapFromResult(developResult);
        var source = ApplyCrop(bitmap, developResult.Width, developResult.Height, crop);

        var dir = Path.GetDirectoryName(exportSettings.OutputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (exportSettings.Format == ExportFormat.Jxl)
        {
            ExportJxl(source, exportSettings);
            return;
        }

        BitmapEncoder encoder = exportSettings.Format switch
        {
            ExportFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = exportSettings.JpegQuality },
            ExportFormat.Png => new PngBitmapEncoder(),
            ExportFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new JpegBitmapEncoder { QualityLevel = exportSettings.JpegQuality }
        };

        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(exportSettings.OutputPath);
        encoder.Save(stream);
    }

    private static void ExportJxl(BitmapSource source, ExportSettings exportSettings)
    {
        // WPF has no JPEG XL encoder; hand a lossless PNG to ImageMagick for encoding.
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        png.Save(ms);
        ms.Position = 0;

        using var image = new MagickImage(ms, MagickFormat.Png);
        image.Format = MagickFormat.Jxl;
        image.Quality = (uint)exportSettings.JpegQuality;
        image.Write(exportSettings.OutputPath);
    }

    public static async Task ExportAsync(ushort[] linearPixels, int width, int height,
        DevelopSettings developSettings, ExportSettings exportSettings)
    {
        var result = await Task.Run(() =>
            RawDevelopmentEngine.Process(linearPixels, width, height, developSettings));

        Export(result, exportSettings, developSettings.Crop);
    }

    private static BitmapSource ApplyCrop(WriteableBitmap bitmap, int width, int height, CropSettings? crop)
    {
        if (crop == null || crop.IsDefault)
            return bitmap;

        int x = (int)Math.Round(crop.Left * width);
        int y = (int)Math.Round(crop.Top * height);
        int w = (int)Math.Round((crop.Right - crop.Left) * width);
        int h = (int)Math.Round((crop.Bottom - crop.Top) * height);

        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        w = Math.Clamp(w, 1, width - x);
        h = Math.Clamp(h, 1, height - y);

        return new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h));
    }

    public static string GetDefaultExtension(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Png => ".png",
        ExportFormat.Tiff => ".tif",
        ExportFormat.Jxl => ".jxl",
        _ => ".jpg"
    };

    public static string GetFileFilter() =>
        "JPEG (*.jpg)|*.jpg|PNG (*.png)|*.png|TIFF (*.tif)|*.tif|JPEG XL (*.jxl)|*.jxl";
}
