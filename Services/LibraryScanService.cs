using System.IO;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class LibraryScanProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public double Percentage => TotalCount == 0 ? 0 : (double)ProcessedCount / TotalCount * 100;
    public string Phase { get; set; } = string.Empty;
}

public class LibraryScanService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".tif", ".tiff",
        ".png"
    };

    private readonly AIService _aiService;
    private readonly FaceRecognitionService _faceService;

    public LibraryScanService(AIService aiService)
    {
        _aiService = aiService;
        _faceService = new FaceRecognitionService(aiService);
    }

    public async Task<LibraryData> ScanAsync(
        string rootFolder,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var data = LibraryDataStore.Load(rootFolder);
        data.RootFolder = rootFolder;

        // Build lookup of existing analyzed images
        var existing = data.Images.ToDictionary(
            i => i.RelativePath, i => i, StringComparer.OrdinalIgnoreCase);

        // Enumerate all image files recursively
        var allFiles = Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var totalCount = allFiles.Count;
        var processedCount = 0;

        var updatedImages = new List<LibraryImageInfo>();

        foreach (var filePath in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootFolder, filePath);
            var fileInfo = new FileInfo(filePath);

            progress?.Report(new LibraryScanProgress
            {
                CurrentFile = relativePath,
                ProcessedCount = processedCount,
                TotalCount = totalCount,
                Phase = "Scanning"
            });

            // Check if we can skip (already analyzed and unchanged)
            if (existing.TryGetValue(relativePath, out var existingInfo) &&
                existingInfo.FileSize == fileInfo.Length &&
                existingInfo.LastModifiedUtc == fileInfo.LastWriteTimeUtc &&
                existingInfo.AnalyzedUtc.HasValue)
            {
                updatedImages.Add(existingInfo);
                processedCount++;
                continue;
            }

            // Create or update the image info
            var imageInfo = new LibraryImageInfo
            {
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc
            };

            // Phase 1: Compute hashes
            try
            {
                var hashes = DuplicateDetectionService.ComputeHashes(filePath);
                imageInfo.FileHash = hashes.FileHash;
                imageInfo.PerceptualHash = hashes.PerceptualHash.ToString("x16");
            }
            catch
            {
                // Skip hash failures
            }

            // Phase 2: AI analysis (if available)
            if (_aiService.IsAvailable)
            {
                try
                {
                    var thumbBytes = GenerateThumbnailBytes(filePath);
                    if (thumbBytes != null)
                    {
                        var analysis = await _aiService.AnalyzeImageAsync(thumbBytes, cancellationToken);
                        if (analysis != null)
                        {
                            imageInfo.Description = analysis.Description;
                            imageInfo.Tags = analysis.Tags;
                            imageInfo.Faces = analysis.Faces.Select(f => new FaceInfo
                            {
                                Description = f.Description,
                                ApproximatePosition = f.ApproximatePosition,
                                Confidence = 0.8
                            }).ToList();
                        }
                    }
                }
                catch
                {
                    // AI failure is non-fatal
                }
            }

            imageInfo.AnalyzedUtc = DateTime.UtcNow;
            updatedImages.Add(imageInfo);
            processedCount++;
        }

        data.Images = updatedImages;
        data.LastScanUtc = DateTime.UtcNow;

        // Find duplicates
        data.DuplicateGroups = DuplicateDetectionService.FindDuplicates(updatedImages);

        // Cluster faces
        if (_aiService.IsAvailable)
        {
            data.FaceClusters = _faceService.ClusterFaces(updatedImages);
        }

        // Persist
        LibraryDataStore.Save(data);

        progress?.Report(new LibraryScanProgress
        {
            CurrentFile = "Complete",
            ProcessedCount = totalCount,
            TotalCount = totalCount,
            Phase = "Done"
        });

        return data;
    }

    private static byte[]? GenerateThumbnailBytes(string filePath)
    {
        try
        {
            using var image = new MagickImage(filePath);
            image.AutoOrient();
            image.Resize(new MagickGeometry(512, 512) { Greater = true });
            image.Format = MagickFormat.Jpeg;
            image.Quality = 80;
            return image.ToByteArray();
        }
        catch
        {
            return null;
        }
    }
}
