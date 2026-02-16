using System.Collections.ObjectModel;
using System.IO;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class FileGroupingService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".tif", ".tiff",
        ".png"
    };

    public ObservableCollection<ImageFileGroup> Groups { get; } = new();

    public void ScanDirectory(string directoryPath)
    {
        Groups.Clear();

        if (!Directory.Exists(directoryPath))
            return;

        var files = Directory.EnumerateFiles(directoryPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new ImageFileEntry(f))
            .ToList();

        var grouped = files
            .GroupBy(f => f.Stem, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            Groups.Add(new ImageFileGroup(group.Key, group));
        }
    }

    public static List<string> GetSubDirectories(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return [];

        return Directory.GetDirectories(directoryPath)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DriveInfo> GetAvailableDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .ToList();
    }
}
