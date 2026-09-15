using System.IO;
using System.Threading.Channels;
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
    /// How many prepared files may wait for AI. Each holds a ~512px JPEG (a few hundred
    /// KB), so a generous buffer costs little and stops fast CPU workers from stalling
    /// on a full queue while the slower AI stage drains it.
    /// </summary>
    private const int PrepareQueueDepth = 64;

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
        // IsAvailable is a cached flag from the last availability probe. Ollama may have been
        // started (or stopped) since then, so re-probe here. Without this a scan started after
        // the app launched with Ollama down would silently skip all AI analysis and still
        // persist the files as scanned.
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

        var updatedImages = new List<LibraryImageInfo>();

        // Bounded queue so CPU workers stay a little ahead of AI without decoding the
        // whole library into memory.
        var channel = Channel.CreateBounded<PreparedFile>(new BoundedChannelOptions(PrepareQueueDepth)
        {
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Producer: decode, hash and thumbnail in parallel across cores.
        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    allFiles,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = PrepareParallelism,
                        CancellationToken = cancellationToken
                    },
                    async (filePath, ct) =>
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

                        if (prepared != null)
                            await channel.Writer.WriteAsync(prepared, ct);
                        else
                            Interlocked.Increment(ref skippedCount);
                    });

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Consumer: several AI requests are kept in flight so the GPU always has work
        // queued. Ollama interleaves prefill and generation across its parallel slots,
        // which a strictly one-at-a-time loop leaves idle between calls.
        var resultsLock = new object();

        var consumers = new Task[AiConcurrency];
        for (var c = 0; c < AiConcurrency; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                await foreach (var prepared in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await AnalyzeAsync(prepared, cancellationToken);

                    int done;
                    lock (resultsLock)
                    {
                        updatedImages.Add(prepared.ImageInfo);
                        done = ++processedCount;
                    }

                    progress?.Report(new LibraryScanProgress
                    {
                        CurrentFile = prepared.RelativePath,
                        ProcessedCount = done,
                        TotalCount = totalCount,
                        Phase = prepared.NeedsAi ? "Analyzing" : "Scanning"
                    });
                }
            }, cancellationToken);
        }

        await Task.WhenAll(consumers);
        await producer;

        // Photos can be deleted while the scan is running. Prepared records are captured
        // before AI analysis completes, so reconcile against the file system before saving
        // to avoid resurrecting entries the user just removed.
        updatedImages.RemoveAll(i => !File.Exists(Path.Combine(rootFolder, i.RelativePath)));

        // Parallel completion order is arbitrary; restore a stable order for the UI.
        updatedImages.Sort((a, b) =>
            string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

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
            CurrentFile = skippedCount > 0 ? $"Complete ({skippedCount} skipped)" : "Complete",
            ProcessedCount = totalCount,
            TotalCount = totalCount,
            Phase = "Done"
        });

        return data;
    }

    /// <summary>
    /// CPU-bound result for one file: hashes plus the downscaled JPEG used for AI.
    /// Produced on worker threads so decoding overlaps with GPU inference.
    /// </summary>
    private sealed class PreparedFile
    {
        public required LibraryImageInfo ImageInfo { get; init; }
        public required string FilePath { get; init; }
        public required string RelativePath { get; init; }
        public byte[]? ThumbnailBytes { get; init; }
        public bool NeedsAi { get; init; }
        public bool Decodable { get; init; }
    }

    /// <summary>
    /// Performs all CPU/disk work for a file in one pass. The image is decoded a single
    /// time and reused for both the perceptual hash and the AI thumbnail, which halves
    /// the decode cost that dominates scanning of large camera JPEGs.
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

        byte[]? thumbBytes = null;
        var decodable = true;

        try
        {
            using var image = new MagickImage();
            image.Read(filePath);

            // Both consumers only need a small image, so normalise once up front and
            // reuse this single decode for the hash and the thumbnail.
            image.AutoOrient();
            image.Strip();

            if (needsHash)
                imageInfo.PerceptualHash = DuplicateDetectionService
                    .ComputePerceptualHash(image).ToString("x16");

            if (needsAi)
            {
                using var thumb = image.Clone();
                thumb.Resize(new MagickGeometry(512, 512) { Greater = true });
                thumb.Format = MagickFormat.Jpeg;
                thumb.Quality = 80;
                thumbBytes = thumb.ToByteArray();
            }
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

        return new PreparedFile
        {
            ImageInfo = imageInfo,
            FilePath = filePath,
            RelativePath = relativePath,
            ThumbnailBytes = thumbBytes,
            NeedsAi = needsAi,
            Decodable = decodable
        };
    }

    /// <summary>
    /// Runs AI analysis for an already-prepared file. Kept separate from <see cref="PrepareFile"/>
    /// so the network/GPU wait does not block the CPU decode workers.
    /// </summary>
    private async Task AnalyzeAsync(PreparedFile prepared, CancellationToken cancellationToken)
    {
        var imageInfo = prepared.ImageInfo;

        if (!prepared.NeedsAi)
        {
            imageInfo.AnalyzedUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            if (prepared.ThumbnailBytes == null)
            {
                // The file cannot be decoded, so retrying it every scan is wasted work.
                if (!prepared.Decodable)
                    imageInfo.AiAnalyzedUtc = DateTime.UtcNow;
            }
            else
            {
                var analysis = await _aiService.AnalyzeImageAsync(prepared.ThumbnailBytes, cancellationToken);
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // AI failure is non-fatal; AiAnalyzedUtc stays null so it retries next scan.
        }

        imageInfo.AnalyzedUtc = DateTime.UtcNow;
    }
}
