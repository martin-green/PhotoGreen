using System.IO;

namespace PhotoGreen.Models;

public enum ImageFileType
{
    Jpeg,
    Raw,
    Tiff,
    Png,
    Other
}

public class ImageFileEntry
{
    public string FullPath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public string Stem { get; }
    public ImageFileType FileType { get; }
    public long FileSize { get; }
    public DateTime LastModified { get; }

    public ImageFileEntry(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Extension = Path.GetExtension(fullPath).ToLowerInvariant();
        Stem = Path.GetFileNameWithoutExtension(fullPath);
        FileType = ClassifyExtension(Extension);

        var info = new FileInfo(fullPath);
        FileSize = info.Exists ? info.Length : 0;
        LastModified = info.Exists ? info.LastWriteTime : DateTime.MinValue;
    }

    private static ImageFileType ClassifyExtension(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => ImageFileType.Jpeg,
        ".arw" or ".cr2" or ".cr3" or ".nef" or ".dng" or ".raf" or ".orf" or ".rw2" or ".pef" or ".srw" => ImageFileType.Raw,
        ".tif" or ".tiff" => ImageFileType.Tiff,
        ".png" => ImageFileType.Png,
        _ => ImageFileType.Other
    };

    public static bool IsSupportedExtension(string ext) =>
        ClassifyExtension(ext.ToLowerInvariant()) != ImageFileType.Other;
}
