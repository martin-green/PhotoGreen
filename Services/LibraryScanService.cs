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

    /// <summary>
    /// Decode/hash workers. Image decoding is CPU bound, but leaving a core free keeps
    /// the UI responsive during a scan.
    /// </summary>
    private static readonly int PrepareParallelism = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>
    /// Vision requests kept in flight against Ollama. Matches the parallel slots we ask
    /// the server for, so the GPU is never idle waiting for the next request to arrive.
    /// </summary>
    private static readonly int AiConcurrency = OllamaProcessService.DefaultNumParallel;

    /// <summary>
    /// How many images the background AI pass analyses between saves. Persisting
    /// periodically means a cancelled or interrupted pass keeps its completed work
    /// instead of starting over.
    /// </summary>
    private const int SaveInterval = 25;

    public LibraryScanService(AIService aiService)
    {
        _aiService = aiService;
        _faceService = new FaceRecognitionService(aiService);
    }

    /// <summary>
    /// Fast pass: enumerate, hash and detect duplicates. Deliberately does no AI work so
    /// duplicate results are available in seconds instead of waiting on the vision model.
    /// Follow it with <see cref="AnalyzeLibraryAsync"/> to fill in tags, descriptions and faces.
    /// </summary>
    public async Task<LibraryData> ScanAsync(
        string rootFolder,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // IsAvailable is a cached flag from the last availability probe. Ollama may have been
        // started (or stopped) since then, so re-probe here. The flag decides whether files are
        // marked as still owing AI work, so a stale value would strand them.
        await _aiService.CheckAvailabilityAsync(cancellationToken);

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
        var skippedCount = 0;

        var updatedImages = new List<LibraryImageInfo>(totalCount);
        var resultsLock = new object();

        await Parallel.ForEachAsync(
            allFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = PrepareParallelism,
                CancellationToken = cancellationToken
            },
            (filePath, ct) =>
            {
                var relativePath = Path.GetRelativePath(rootFolder, filePath);

                PreparedFile? prepared = null;
                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // The file may have been moved or deleted since enumeration;
                    // drop it from the library instead of failing the whole scan.
                    if (fileInfo.Exists)
                        prepared = PrepareFile(filePath, relativePath, fileInfo, existing);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A single unreadable or vanished file must not abort the scan.
                }

                if (prepared == null)
                {
                    Interlocked.Increment(ref skippedCount);
                    return ValueTask.CompletedTask;
                }

                prepared.ImageInfo.AnalyzedUtc = DateTime.UtcNow;

                int done;
                lock (resultsLock)
                {
                    updatedImages.Add(prepared.ImageInfo);
                    done = ++processedCount;
                }

                progress?.Report(new LibraryScanProgress
                {
                    CurrentFile = relativePath,
                    ProcessedCount = done,
                    TotalCount = totalCount,
                    Phase = "Scanning"
                });

                return ValueTask.CompletedTask;
            });

        // Photos can be deleted while the scan is running; reconcile against the file
        // system before saving to avoid resurrecting entries the user just removed.
        updatedImages.RemoveAll(i => !File.Exists(Path.Combine(rootFolder, i.RelativePath)));

        // Parallel completion order is arbitrary; restore a stable order for the UI.
        updatedImages.Sort((a, b) =>
            string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

        data.Images = updatedImages;
        data.LastScanUtc = DateTime.UtcNow;

        // Find duplicates
        data.DuplicateGroups = DuplicateDetectionService.FindDuplicates(updatedImages);

        // Persist
        LibraryDataStore.Save(data);

        progress?.Report(new LibraryScanProgress
        {
            CurrentFile = skippedCount > 0 ? $"Complete ({skippedCount} skipped)" : "Complete",
            ProcessedCount = totalCount,
            TotalCount = totalCount,
            Phase = "Done"
        });

        return data;
    }

    /// <summary>
    /// CPU-bound result for one file: the hashes and whether the file still owes the
    /// background AI pass any work.
    /// </summary>
    private sealed class PreparedFile
    {
        public required LibraryImageInfo ImageInfo { get; init; }
        public required string FilePath { get; init; }
        public required string RelativePath { get; init; }
        public bool NeedsAi { get; init; }
        public bool Decodable { get; init; }
    }

    /// <summary>
    /// Performs the CPU/disk work for a file: file hash plus, when the file is new or
    /// changed, a single decode for the perceptual hash.
    /// </summary>
    private PreparedFile? PrepareFile(
        string filePath,
        string relativePath,
        FileInfo fileInfo,
        Dictionary<string, LibraryImageInfo> existing)
    {
        var fileUnchanged = existing.TryGetValue(relativePath, out var existingInfo) &&
            existingInfo.FileSize == fileInfo.Length &&
            existingInfo.LastModifiedUtc == fileInfo.LastWriteTimeUtc &&
            existingInfo.AnalyzedUtc.HasValue;

        // Skip only when the file is unchanged AND there is nothing left to do. A file
        // processed while AI was unavailable has no AiAnalyzedUtc, so it is retried here
        // rather than being treated as complete forever.
        // A changed file produces a fresh record with no tags/faces, so it must be
        // re-analysed even when the previous record was complete.
        var needsAi = _aiService.IsAvailable && (!fileUnchanged || existingInfo?.AiAnalyzedUtc is null);

        if (fileUnchanged && !needsAi)
        {
            return new PreparedFile
            {
                ImageInfo = existingInfo!,
                FilePath = filePath,
                RelativePath = relativePath,
                NeedsAi = false,
                Decodable = true
            };
        }

        var imageInfo = fileUnchanged
            ? existingInfo!
            : new LibraryImageInfo
            {
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc
            };

        // The file hash reads bytes only, so it never needs a decode.
        if (!fileUnchanged)
        {
            try
            {
                imageInfo.FileHash = DuplicateDetectionService.ComputeFileHash(filePath);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch
            {
                // Non-fatal: the entry simply has no file hash.
            }
        }

        var needsHash = !fileUnchanged;
        if (!needsHash && !needsAi)
        {
            return new PreparedFile
            {
                ImageInfo = imageInfo,
                FilePath = filePath,
                RelativePath = relativePath,
                NeedsAi = false,
                Decodable = true
            };
        }

        var decodable = true;

        if (needsHash)
        {
            try
            {
                using var image = new MagickImage();
                image.Read(filePath);

                // Only the perceptual hash needs a decode here. The thumbnail the vision
                // model wants is built later by the background AI pass, which is bound by
                // inference latency and can absorb the decode for free.
                image.AutoOrient();
                image.Strip();

                imageInfo.PerceptualHash = DuplicateDetectionService
                    .ComputePerceptualHash(image).ToString("x16");
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch
            {
                // Undecodable file (corrupt or unsupported); still keep the entry.
                decodable = false;

                if (!File.Exists(filePath))
                    return null;
            }
        }

        return new PreparedFile
        {
            ImageInfo = imageInfo,
            FilePath = filePath,
            RelativePath = relativePath,
            NeedsAi = needsAi,
            Decodable = decodable
        };
    }

    /// <summary>
    /// Background pass: classifies every image that still owes AI work. Runs after
    /// <see cref="ScanAsync"/> so duplicate detection is never held up by the vision model.
    /// Results are saved periodically, so cancelling or crashing mid-pass keeps the work
    /// already done and the next run resumes from there.
    /// </summary>
    public async Task<LibraryData> AnalyzeLibraryAsync(
        LibraryData data,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _aiService.CheckAvailabilityAsync(cancellationToken);
        if (!_aiService.IsAvailable)
            return data;

        var pending = data.Images.Where(i => i.AiAnalyzedUtc is null).ToList();
        var totalCount = pending.Count;
        if (totalCount == 0)
            return data;

        var rootFolder = data.RootFolder;
        var processedCount = 0;
        var sinceLastSave = 0;
        var saveLock = new object();

        try
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = AiConcurrency,
                    CancellationToken = cancellationToken
                },
                async (imageInfo, ct) =>
                {
                    var filePath = Path.Combine(rootFolder, imageInfo.RelativePath);
                    await AnalyzeImageAsync(imageInfo, filePath, ct);

                    int done;
                    bool shouldSave;
                    lock (saveLock)
                    {
                        done = ++processedCount;
                        shouldSave = ++sinceLastSave >= SaveInterval;
                        if (shouldSave)
                            sinceLastSave = 0;
                    }

                    if (shouldSave)
                        LibraryDataStore.Save(data);

                    progress?.Report(new LibraryScanProgress
                    {
                        CurrentFile = imageInfo.RelativePath,
                        ProcessedCount = done,
                        TotalCount = totalCount,
                        Phase = "Analyzing"
                    });
                });
        }
        finally
        {
            // Cluster over everything that has results so far, so a cancelled pass still
            // leaves usable face groups rather than discarding the work.
            data.FaceClusters = _faceService.ClusterFaces(data.Images);
            LibraryDataStore.Save(data);
        }

        progress?.Report(new LibraryScanProgress
        {
            CurrentFile = "Analysis complete",
            ProcessedCount = totalCount,
            TotalCount = totalCount,
            Phase = "Done"
        });

        return data;
    }

    /// <summary>
    /// Decodes the thumbnail for one image and runs the vision model over it. The decode
    /// happens here rather than in the fast pass because this stage waits on inference
    /// anyway, so the CPU cost overlaps with the GPU instead of delaying duplicates.
    /// </summary>
    private async Task AnalyzeImageAsync(
        LibraryImageInfo imageInfo,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] thumbBytes;
            try
            {
                using var image = new MagickImage();
                image.Read(filePath);
                image.AutoOrient();
                image.Strip();
                image.Resize(new MagickGeometry(512, 512) { Greater = true });
                image.Format = MagickFormat.Jpeg;
                image.Quality = 80;
                thumbBytes = image.ToByteArray();
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch
            {
                // Undecodable file (corrupt or unsupported). Retrying it on every pass is
                // wasted work, so mark it done unless the file simply vanished.
                if (File.Exists(filePath))
                    imageInfo.AiAnalyzedUtc = DateTime.UtcNow;
                return;
            }

            var analysis = await _aiService.AnalyzeImageAsync(thumbBytes, cancellationToken);
            if (analysis != null)
            {
                imageInfo.Description = analysis.Description;
                imageInfo.Tags = analysis.Tags;
                imageInfo.SceneType = analysis.SceneType;
                imageInfo.Faces = analysis.Faces.Select(f => new FaceInfo
                {
                    Description = f.Description,
                    ApproximatePosition = f.ApproximatePosition,
                    Confidence = 0.8
                }).ToList();

                // Only mark AI as done when it actually produced a result.
                imageInfo.AiAnalyzedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // AI failure is non-fatal; AiAnalyzedUtc stays null so it retries next pass.
        }
    }
}
