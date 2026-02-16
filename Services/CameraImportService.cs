using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Windows.Devices.Enumeration;
using Windows.Media.Import;

namespace PhotoGreen.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public class CameraImportService : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".tif", ".tiff",
        ".png"
    };

    // WPD (Windows Portable Devices) interface class GUID — covers MTP/PTP cameras
    private const string WpdInterfaceClass = "{6AC27878-A6FA-4155-BA85-F98F491D4F33}";

    public event Action<string>? StatusChanged;
    public event Action<double>? ProgressChanged;
    public event Action<PhotoImportSource>? SourceConnected;
    public event Action? SourceDisconnected;
    public event Action<string>? LogMessage;

    private DeviceWatcher? _deviceWatcher;
    private PhotoImportFindItemsResult? _lastFindResult;

    public string? LastImportedFolder { get; private set; }

    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        LogMessage?.Invoke(message);
        StatusChanged?.Invoke(message);
    }

    public async Task<List<PhotoImportSource>> FindSourcesAsync()
    {
        var result = await PhotoImportManager.FindAllSourcesAsync();
        return result.ToList();
    }

    public void StartWatching()
    {
        if (_deviceWatcher != null)
            return;

        _deviceWatcher = DeviceInformation.CreateWatcher(
            DeviceClass.PortableStorageDevice);

        _deviceWatcher.Added += async (_, args) =>
        {
            StatusChanged?.Invoke($"Device connected: {args.Name}");
            try
            {
                var sources = await FindSourcesAsync();
                if (sources.Count > 0)
                    SourceConnected?.Invoke(sources[0]);
            }
            catch
            {
                // Device may not be ready yet
            }
        };

        _deviceWatcher.Removed += (_, _) =>
        {
            StatusChanged?.Invoke("Device disconnected.");
            SourceDisconnected?.Invoke();
        };

        _deviceWatcher.Start();
    }

    public void StopWatching()
    {
        if (_deviceWatcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
        {
            _deviceWatcher.Stop();
        }
        _deviceWatcher = null;
    }

    public void Dispose()
    {
        StopWatching();
    }

    public async Task<PhotoImportImportItemsResult?> ImportFromSourceAsync(
        PhotoImportSource source,
        string destinationFolder,
        string folderPattern,
        HashSet<string> importedHashes,
        CancellationToken cancellationToken = default)
    {
        Log($"[Camera] Starting import from: {source.DisplayName}");
        Log($"[Camera] Destination: {destinationFolder}");
        Log($"[Camera] Folder pattern: {folderPattern}");
        ProgressChanged?.Invoke(0);

        var session = source.CreateImportSession();
        Log("[Camera] Import session created, finding items...");

        var findOperation = session.FindItemsAsync(
            PhotoImportContentTypeFilter.ImagesAndVideos,
            PhotoImportItemSelectionMode.SelectAll);

        _lastFindResult = await findOperation;

        var foundItems = _lastFindResult.FoundItems;
        Log($"[Camera] Found {foundItems.Count} items on device.");

        if (foundItems.Count == 0)
        {
            Log("[Camera] No items found, aborting.");
            return null;
        }

        int skipped = 0;
        int toImport = 0;

        foreach (var item in foundItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(item.Name);
            if (!ImageExtensions.Contains(ext))
            {
                item.IsSelected = false;
                skipped++;
                Log($"[Camera]   Skip (not image): {item.Name}");
                continue;
            }

            var hash = ComputeItemIdentity(item);
            if (importedHashes.Contains(hash))
            {
                item.IsSelected = false;
                skipped++;
                Log($"[Camera]   Skip (duplicate): {item.Name}");
            }
            else
            {
                item.IsSelected = true;
                toImport++;
                Log($"[Camera]   Selected: {item.Name} ({item.SizeInBytes} bytes, {item.Date})");
            }
        }

        Log($"[Camera] Selection: {toImport} to import, {skipped} skipped.");

        if (toImport == 0)
        {
            Log("[Camera] Nothing new to import.");
            StatusChanged?.Invoke("No new images to import.");
            return null;
        }

        var stagingDir = PrepareDestination(destinationFolder, folderPattern);
        Log($"[Camera] Staging dir: {stagingDir}");

        var importOperation = _lastFindResult.ImportItemsAsync();

        importOperation.Progress = (info, progress) =>
        {
            ProgressChanged?.Invoke(progress.ImportProgress);
        };

        using var registration = cancellationToken.Register(() => importOperation.Cancel());
        Log("[Camera] WMI import started...");
        var importResult = await importOperation;
        Log($"[Camera] WMI import HasSucceeded={importResult.HasSucceeded}, imported {importResult.ImportedItems.Count} items.");

        if (importResult.HasSucceeded)
        {
            var importedItems = importResult.ImportedItems;
            int organized = 0;
            string? firstFolder = null;

            foreach (var imported in importedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var date = imported.Date.DateTime;
                var subFolder = ApplyFolderPattern(folderPattern, date);
                var targetDir = Path.Combine(destinationFolder, subFolder);
                Directory.CreateDirectory(targetDir);
                firstFolder ??= targetDir;

                var targetPath = Path.Combine(targetDir, imported.Name);
                targetPath = GetUniqueFilePath(targetPath);

                var importedPath = Path.Combine(stagingDir, imported.Name);
                Log($"[Camera]   Organizing: {imported.Name}");
                Log($"[Camera]     Staging path: {importedPath} (exists={File.Exists(importedPath)})");
                Log($"[Camera]     Target path:  {targetPath}");

                try
                {
                    if (File.Exists(importedPath))
                    {
                        File.Move(importedPath, targetPath);
                        Log($"[Camera]     Moved OK.");
                    }
                    else
                    {
                        Log($"[Camera]     WARNING: staging file not found. Checking target dir...");
                        var existing = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
                        Log($"[Camera]     Files in staging: {string.Join(", ", existing.Select(Path.GetFileName))}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Camera]     ERROR moving file: {ex.Message}");
                }

                var hash = ComputeItemIdentity(imported.Name, imported.SizeInBytes, date);
                importedHashes.Add(hash);
                organized++;
            }

            LastImportedFolder = firstFolder;
            Log($"[Camera] Import complete: {organized} organized. FirstFolder={firstFolder}");
            StatusChanged?.Invoke($"Import complete: {organized} images imported.");
        }
        else
        {
            Log("[Camera] Import failed or was cancelled.");
            StatusChanged?.Invoke("Import failed or was cancelled.");
        }

        return importResult;
    }

    public async Task<ImportResult> ImportManualAsync(
        string sourceFolderPath,
        string destinationFolder,
        string folderPattern,
        HashSet<string> importedHashes,
        IProgress<(int processed, int total, string currentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log($"[Manual] Starting import from: {sourceFolderPath}");
        Log($"[Manual] Destination: {destinationFolder}");
        Log($"[Manual] Folder pattern: {folderPattern}");
        Log($"[Manual] Known hashes: {importedHashes.Count}");

        var allFiles = Directory.EnumerateFiles(sourceFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        Log($"[Manual] Found {allFiles.Count} image files in source.");

        if (allFiles.Count == 0)
        {
            Log($"[Manual] No image files found. Extensions scanned: {string.Join(", ", ImageExtensions)}");
            var allAny = Directory.GetFiles(sourceFolderPath, "*.*", SearchOption.AllDirectories);
            Log($"[Manual] Total files in source (any type): {allAny.Length}");
            if (allAny.Length > 0 && allAny.Length <= 20)
            {
                foreach (var f in allAny)
                    Log($"[Manual]   {Path.GetFileName(f)} ({Path.GetExtension(f)})");
            }
        }

        int imported = 0;
        int skipped = 0;
        string? firstFolder = null;

        for (int i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = allFiles[i];
            var hash = await ComputeFileHashAsync(file, cancellationToken);

            if (importedHashes.Contains(hash))
            {
                skipped++;
                Log($"[Manual]   Skip (duplicate): {Path.GetFileName(file)}");
                progress?.Report((i + 1, allFiles.Count, Path.GetFileName(file)));
                continue;
            }

            var fileDate = File.GetLastWriteTime(file);
            var subFolder = ApplyFolderPattern(folderPattern, fileDate);
            var targetDir = Path.Combine(destinationFolder, subFolder);
            Directory.CreateDirectory(targetDir);
            firstFolder ??= targetDir;

            var targetPath = Path.Combine(targetDir, Path.GetFileName(file));
            targetPath = GetUniqueFilePath(targetPath);

            Log($"[Manual]   Copy: {file}");
            Log($"[Manual]     -> {targetPath}");

            try
            {
                await CopyFileAsync(file, targetPath, cancellationToken);
                Log($"[Manual]     OK ({new FileInfo(file).Length} bytes)");
            }
            catch (Exception ex)
            {
                Log($"[Manual]     ERROR: {ex.Message}");
                throw;
            }

            importedHashes.Add(hash);
            imported++;

            progress?.Report((i + 1, allFiles.Count, Path.GetFileName(file)));
            ProgressChanged?.Invoke((double)(i + 1) / allFiles.Count * 100);
        }

        Log($"[Manual] Done: {imported} imported, {skipped} duplicates skipped. FirstFolder={firstFolder}");
        StatusChanged?.Invoke($"Import complete: {imported} imported, {skipped} duplicates skipped.");
        return new ImportResult(imported, skipped, allFiles.Count, firstFolder);
    }

    public static string ApplyFolderPattern(string pattern, DateTime date)
    {
        return pattern
            .Replace("{yyyy}", date.ToString("yyyy"))
            .Replace("{MM}", date.ToString("MM"))
            .Replace("{dd}", date.ToString("dd"))
            .Replace("{HH}", date.ToString("HH"))
            .Replace("{mm}", date.ToString("mm"));
    }

    public static string PreviewFolderPattern(string pattern)
    {
        return ApplyFolderPattern(pattern, DateTime.Now);
    }

    private static string PrepareDestination(string destinationFolder, string folderPattern)
    {
        var today = DateTime.Now;
        var subFolder = ApplyFolderPattern(folderPattern, today);
        var targetDir = Path.Combine(destinationFolder, subFolder);
        Directory.CreateDirectory(targetDir);
        return targetDir;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{stem}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static string ComputeItemIdentity(PhotoImportItem item)
    {
        return ComputeItemIdentity(item.Name, item.SizeInBytes, item.Date.DateTime);
    }

    private static string ComputeItemIdentity(string name, ulong sizeInBytes, DateTime date)
    {
        var identity = $"{name}|{sizeInBytes}|{date:O}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        var fi = new FileInfo(filePath);
        var identity = $"{fi.Name}|{(ulong)fi.Length}|{fi.LastWriteTimeUtc:O}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        var hash = SHA256.HashData(bytes);
        return Task.FromResult(Convert.ToHexString(hash));
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        const int bufferSize = 81920;
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var destStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, true);
        await sourceStream.CopyToAsync(destStream, bufferSize, ct);
    }
}

public record ImportResult(int ImportedCount, int SkippedCount, int TotalFound, string? FirstImportedFolder = null);
