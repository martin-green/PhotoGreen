using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly AIService _aiService = new();
    private LibraryScanService? _scanService;
    private LibraryData? _libraryData;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _thumbnailCts;
    private ThumbnailCacheService? _thumbnailCache;

    [ObservableProperty]
    private string _rootFolder = string.Empty;

    [ObservableProperty]
    private LibraryGroupMode _groupMode = LibraryGroupMode.Folder;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanStatusText = string.Empty;

    [ObservableProperty]
    private bool _aiAvailable;

    [ObservableProperty]
    private LibraryThumbnailItem? _selectedItem;

    public ObservableCollection<LibraryGroup> Groups { get; } = [];

    // Detail panel data
    [ObservableProperty]
    private string _detailFileName = string.Empty;

    [ObservableProperty]
    private string _detailDescription = string.Empty;

    [ObservableProperty]
    private string _detailTags = string.Empty;

    [ObservableProperty]
    private string _detailFaces = string.Empty;

    [ObservableProperty]
    private string _detailDuplicateInfo = string.Empty;

    public event Action<string>? OpenInEditorRequested;

    public LibraryViewModel()
    {
        _ = CheckAIAsync();
    }

    private async Task CheckAIAsync()
    {
        AiAvailable = await _aiService.CheckAvailabilityAsync();
    }

    partial void OnSelectedItemChanged(LibraryThumbnailItem? value)
    {
        if (value == null)
        {
            DetailFileName = string.Empty;
            DetailDescription = string.Empty;
            DetailTags = string.Empty;
            DetailFaces = string.Empty;
            DetailDuplicateInfo = string.Empty;
            return;
        }

        DetailFileName = value.FileName;

        var info = value.ImageInfo;
        if (info != null)
        {
            DetailDescription = info.Description ?? "Not analyzed";
            DetailTags = info.Tags.Count > 0 ? string.Join(", ", info.Tags) : "None";
            DetailFaces = info.Faces.Count > 0
                ? string.Join("\n", info.Faces.Select(f => f.Description ?? "Unknown"))
                : "No faces detected";

            // Check duplicates
            if (_libraryData != null)
            {
                var dupGroup = _libraryData.DuplicateGroups
                    .FirstOrDefault(g => g.ImagePaths.Contains(info.RelativePath));
                if (dupGroup != null)
                {
                    DetailDuplicateInfo = $"{dupGroup.Type} — {dupGroup.ImagePaths.Count} copies";
                }
                else
                {
                    DetailDuplicateInfo = "No duplicates";
                }
            }
        }
        else
        {
            DetailDescription = "Not analyzed";
            DetailTags = "None";
            DetailFaces = "Not analyzed";
            DetailDuplicateInfo = "Not analyzed";
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Library Root Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            RootFolder = dialog.FolderName;
            LoadLibrary();
        }
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            return;

        IsScanning = true;
        ScanProgress = 0;
        ScanStatusText = "Starting scan...";

        _scanCts = new CancellationTokenSource();
        _scanService = new LibraryScanService(_aiService);

        var progressHandler = new Progress<LibraryScanProgress>(p =>
        {
            ScanProgress = p.Percentage;
            ScanStatusText = $"{p.Phase}: {p.CurrentFile} ({p.ProcessedCount}/{p.TotalCount})";
        });

        try
        {
            _libraryData = await Task.Run(
                () => _scanService.ScanAsync(RootFolder, progressHandler, _scanCts.Token));
            BuildGroups();
            ScanStatusText = $"Scan complete — {_libraryData.Images.Count} images";
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedItem != null)
        {
            OpenInEditorRequested?.Invoke(SelectedItem.FullPath);
        }
    }

    [RelayCommand]
    private void DeleteDuplicates()
    {
        if (SelectedItem?.ImageInfo == null || _libraryData == null)
            return;

        var dupGroup = _libraryData.DuplicateGroups
            .FirstOrDefault(g => g.ImagePaths.Contains(SelectedItem.ImageInfo.RelativePath));

        if (dupGroup == null || dupGroup.ImagePaths.Count < 2)
            return;

        // Keep the first (prefer raw), delete the rest
        var toKeep = dupGroup.ImagePaths
            .OrderByDescending(p => IsRawExtension(Path.GetExtension(p)))
            .ThenBy(p => p)
            .First();

        foreach (var path in dupGroup.ImagePaths.Where(p => p != toKeep))
        {
            var fullPath = Path.Combine(RootFolder, path);
            try
            {
                if (File.Exists(fullPath))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        fullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch
            {
                // Ignore delete failures
            }
        }

        // Remove deleted entries from data
        var deletedPaths = dupGroup.ImagePaths.Where(p => p != toKeep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _libraryData.Images.RemoveAll(i => deletedPaths.Contains(i.RelativePath));
        _libraryData.DuplicateGroups.Remove(dupGroup);
        LibraryDataStore.Save(_libraryData);

        BuildGroups();
    }

    partial void OnGroupModeChanged(LibraryGroupMode value)
    {
        BuildGroups();
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".tif", ".tiff",
        ".png"
    };

    private void LoadLibrary()
    {
        if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            return;

        _libraryData = LibraryDataStore.Load(RootFolder);

        _thumbnailCache?.Dispose();
        _thumbnailCache = new ThumbnailCacheService(RootFolder);

        // Build a lookup of images already known from a prior scan
        var known = _libraryData.Images
            .ToDictionary(i => i.RelativePath, i => i, StringComparer.OrdinalIgnoreCase);

        // Discover all image files on disk and add stubs for any that aren't tracked yet
        var allFiles = Directory.EnumerateFiles(RootFolder, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(RootFolder, filePath);
            if (!known.ContainsKey(relativePath))
            {
                var fi = new FileInfo(filePath);
                _libraryData.Images.Add(new LibraryImageInfo
                {
                    RelativePath = relativePath,
                    FileSize = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                });
            }
        }

        BuildGroups();

        var analyzedCount = _libraryData.Images.Count(i => i.AnalyzedUtc.HasValue);
        if (analyzedCount > 0)
        {
            ScanStatusText = $"{_libraryData.Images.Count} images ({analyzedCount} analyzed)";
        }
        else
        {
            ScanStatusText = $"{_libraryData.Images.Count} images found — click Scan to analyze";
        }
    }

    public void SetRootFolder(string folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            RootFolder = folder;
            LoadLibrary();
        }
    }

    private void BuildGroups()
    {
        Groups.Clear();

        if (_libraryData == null || _libraryData.Images.Count == 0)
            return;

        switch (GroupMode)
        {
            case LibraryGroupMode.Folder:
                BuildFolderGroups();
                break;
            case LibraryGroupMode.Faces:
                BuildFaceGroups();
                break;
            case LibraryGroupMode.Duplicates:
                BuildDuplicateGroups();
                break;
            case LibraryGroupMode.Tags:
                BuildTagGroups();
                break;
        }

        // Cancel any previous thumbnail loading and start fresh
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        _ = LoadGroupThumbnailsAsync(_thumbnailCts.Token);
    }

    private void BuildFolderGroups()
    {
        var byFolder = _libraryData!.Images
            .GroupBy(i => Path.GetDirectoryName(i.RelativePath) ?? string.Empty)
            .OrderBy(g => g.Key);

        foreach (var folder in byFolder)
        {
            var group = new LibraryGroup
            {
                Name = string.IsNullOrEmpty(folder.Key) ? "(root)" : folder.Key
            };

            foreach (var image in folder.OrderBy(i => i.RelativePath))
            {
                group.Items.Add(CreateThumbnailItem(image));
            }

            Groups.Add(group);
        }
    }

    private void BuildFaceGroups()
    {
        if (_libraryData!.FaceClusters.Count == 0)
        {
            // No clusters — show all images with faces vs without
            var withFaces = _libraryData.Images.Where(i => i.Faces.Count > 0).ToList();
            var withoutFaces = _libraryData.Images.Where(i => i.Faces.Count == 0).ToList();

            if (withFaces.Count > 0)
            {
                var group = new LibraryGroup { Name = "With Faces" };
                foreach (var img in withFaces)
                    group.Items.Add(CreateThumbnailItem(img));
                Groups.Add(group);
            }

            if (withoutFaces.Count > 0)
            {
                var group = new LibraryGroup { Name = "No Faces" };
                foreach (var img in withoutFaces)
                    group.Items.Add(CreateThumbnailItem(img));
                Groups.Add(group);
            }

            return;
        }

        foreach (var cluster in _libraryData.FaceClusters)
        {
            var group = new LibraryGroup
            {
                Name = cluster.Name ?? $"Person {cluster.Id}"
            };

            var imageSet = new HashSet<string>(cluster.ImagePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var image in _libraryData.Images.Where(i => imageSet.Contains(i.RelativePath)))
            {
                group.Items.Add(CreateThumbnailItem(image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }

        // Unassigned images
        var allClustered = _libraryData.FaceClusters
            .SelectMany(c => c.ImagePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unassigned = _libraryData.Images
            .Where(i => i.Faces.Count > 0 && !allClustered.Contains(i.RelativePath))
            .ToList();

        if (unassigned.Count > 0)
        {
            var group = new LibraryGroup { Name = "Unknown Faces" };
            foreach (var img in unassigned)
                group.Items.Add(CreateThumbnailItem(img));
            Groups.Add(group);
        }
    }

    private void BuildDuplicateGroups()
    {
        foreach (var dupGroup in _libraryData!.DuplicateGroups)
        {
            var label = dupGroup.Type == DuplicateType.Exact
                ? "Exact Duplicates"
                : $"Near Duplicates (distance: {dupGroup.HammingDistance})";

            var group = new LibraryGroup { Name = label };

            foreach (var path in dupGroup.ImagePaths)
            {
                var image = _libraryData.Images
                    .FirstOrDefault(i => string.Equals(i.RelativePath, path, StringComparison.OrdinalIgnoreCase));
                if (image != null)
                    group.Items.Add(CreateThumbnailItem(image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }
    }

    private void BuildTagGroups()
    {
        var byTag = _libraryData!.Images
            .SelectMany(i => i.Tags.Select(t => (Tag: t.ToLowerInvariant(), Image: i)))
            .GroupBy(x => x.Tag)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key);

        foreach (var tagGroup in byTag)
        {
            var group = new LibraryGroup { Name = tagGroup.Key };
            foreach (var entry in tagGroup)
            {
                group.Items.Add(CreateThumbnailItem(entry.Image));
            }
            Groups.Add(group);
        }
    }

    private LibraryThumbnailItem CreateThumbnailItem(LibraryImageInfo imageInfo)
    {
        var fullPath = Path.Combine(RootFolder, imageInfo.RelativePath);
        return new LibraryThumbnailItem
        {
            RelativePath = imageInfo.RelativePath,
            FullPath = fullPath,
            FileName = Path.GetFileName(imageInfo.RelativePath),
            ImageInfo = imageInfo
        };
    }

    private async Task LoadGroupThumbnailsAsync(CancellationToken ct)
    {
        // Collect all items that need thumbnails
        var pending = Groups
            .SelectMany(g => g.Items)
            .Where(item => item.Thumbnail == null && File.Exists(item.FullPath))
            .ToList();

        if (pending.Count == 0)
            return;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var maxParallelism = Environment.ProcessorCount;
        var cache = _thumbnailCache;

        await Task.Run(async () =>
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
                async (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var thumb = LoadThumbnail(item.FullPath, item.ImageInfo, cache);
                    if (thumb != null)
                    {
                        // Marshal back to UI thread to set the property
                        await dispatcher.InvokeAsync(() => item.Thumbnail = thumb,
                            DispatcherPriority.Background, token);
                    }
                });
        }, ct);
    }

    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    private static BitmapSource? LoadThumbnail(string filePath, LibraryImageInfo? imageInfo, ThumbnailCacheService? cache)
    {
        try
        {
            // Try loading from the SQLite thumbnail cache first
            if (cache != null && imageInfo != null)
            {
                var cached = cache.Get(imageInfo.RelativePath, imageInfo.FileSize, imageInfo.LastModifiedUtc);
                if (cached != null)
                    return DecodeBitmapFromBytes(cached);
            }

            byte[]? jpegBytes = null;

            // For JPEG files, use WPF's built-in decoder — no ImageMagick needed
            if (JpegExtensions.Contains(Path.GetExtension(filePath)))
            {
                var bitmap = new BitmapImage();
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelHeight = 120;
                    bitmap.StreamSource = fs;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                jpegBytes = EncodeBitmapToJpeg(bitmap);
                StoreThumbnail(cache, imageInfo, jpegBytes);
                return bitmap;
            }

            // For RAW/TIFF/PNG: try EXIF thumbnail first (metadata-only read)
            using var probe = new MagickImage();
            probe.Ping(filePath);
            var exifProfile = probe.GetExifProfile();
            var thumbnailBytes = exifProfile?.CreateThumbnail()?.ToByteArray();

            if (thumbnailBytes is { Length: > 0 })
            {
                // EXIF thumbnails are already JPEG — let WPF decode them natively
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(thumbnailBytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelHeight = 120;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();

                jpegBytes = EncodeBitmapToJpeg(bmp);
                StoreThumbnail(cache, imageInfo, jpegBytes);
                return bmp;
            }

            // Fallback: decode with size hint to reduce memory usage
            var settings = new MagickReadSettings { Width = 240, Height = 240 };
            using var image = new MagickImage(filePath, settings);
            image.AutoOrient();
            image.Resize(new MagickGeometry(120, 120) { Greater = true });
            image.Format = MagickFormat.Jpeg;
            image.Quality = 80;
            jpegBytes = image.ToByteArray();

            StoreThumbnail(cache, imageInfo, jpegBytes);
            return DecodeBitmapFromBytes(jpegBytes);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource DecodeBitmapFromBytes(byte[] data)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(data))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    private static byte[] EncodeBitmapToJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void StoreThumbnail(ThumbnailCacheService? cache, LibraryImageInfo? imageInfo, byte[]? jpegBytes)
    {
        if (cache != null && imageInfo != null && jpegBytes is { Length: > 0 })
            cache.Put(imageInfo.RelativePath, imageInfo.FileSize, imageInfo.LastModifiedUtc, jpegBytes);
    }

    private static bool IsRawExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".arw" or ".cr2" or ".cr3" or ".nef" or ".dng" or ".raf" or ".orf" or ".rw2" or ".pef" or ".srw" => true,
        _ => false
    };
}
