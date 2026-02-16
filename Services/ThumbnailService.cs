using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class ThumbnailService
{
    private const int ThumbnailSize = 120;

    public async Task<BitmapSource?> GetThumbnailAsync(ImageFileGroup group)
    {
        return await Task.Run(() => GetThumbnail(group));
    }

    private static BitmapSource? GetThumbnail(ImageFileGroup group)
    {
        try
        {
            // For raw files, try to extract the embedded JPEG thumbnail first
            if (group.IsRawPrimary)
            {
                var embedded = ExtractEmbeddedThumbnail(group.PrimaryFile.FullPath);
                if (embedded != null)
                    return embedded;
            }

            // If there's a JPEG sidecar, use it for faster thumbnail
            var jpegSidecar = group.SidecarFiles
                .FirstOrDefault(f => f.FileType == ImageFileType.Jpeg);
            if (jpegSidecar != null)
            {
                return LoadAndResizeThumbnail(jpegSidecar.FullPath);
            }

            // Fall back to full decode + resize
            return LoadAndResizeThumbnail(group.PrimaryFile.FullPath);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? ExtractEmbeddedThumbnail(string filePath)
    {
        try
        {
            using var image = new MagickImage(filePath);
            var exifProfile = image.GetExifProfile();
            var thumbnailBytes = exifProfile?.CreateThumbnail()?.ToByteArray();

            if (thumbnailBytes == null || thumbnailBytes.Length == 0)
                return null;

            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(thumbnailBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelHeight = ThumbnailSize;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadAndResizeThumbnail(string filePath)
    {
        try
        {
            var settings = new MagickReadSettings
            {
                Width = ThumbnailSize * 2,
                Height = ThumbnailSize * 2
            };

            using var image = new MagickImage(filePath, settings);
            image.Thumbnail((uint)ThumbnailSize, (uint)ThumbnailSize);

            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream())
            {
                image.Write(ms, MagickFormat.Png);
                ms.Position = 0;

                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
