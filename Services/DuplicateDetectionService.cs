using System.IO;
using System.Security.Cryptography;
using ImageMagick;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class DuplicateDetectionService
{
    private const int HashSize = 8;
    private const int NearDuplicateThreshold = 5;

    public static ImageHash ComputeHashes(string filePath)
    {
        var fileHash = ComputeFileHash(filePath);
        var perceptualHash = ComputePerceptualHash(filePath);

        return new ImageHash
        {
            FileHash = fileHash,
            PerceptualHash = perceptualHash
        };
    }

    public static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return $"sha256:{Convert.ToHexStringLower(hashBytes)}";
    }

    public static ulong ComputePerceptualHash(string filePath)
    {
        using var image = new MagickImage(filePath);
        return ComputePerceptualHash(image);
    }

    /// <summary>
    /// Computes the perceptual hash from an already-decoded image. The caller keeps
    /// ownership of <paramref name="source"/>; this works on a private clone so the
    /// source is left untouched and a single decode can feed several consumers.
    /// </summary>
    public static ulong ComputePerceptualHash(IMagickImage<ushort> source)
    {
        using var image = source.Clone();
        image.AutoOrient();
        image.Resize(new MagickGeometry(HashSize, HashSize) { IgnoreAspectRatio = true });
        image.ColorSpace = ColorSpace.Gray;
        image.Depth = 8;

        var pixels = image.GetPixels();
        var values = new byte[HashSize * HashSize];
        int idx = 0;
        for (int y = 0; y < HashSize; y++)
        {
            for (int x = 0; x < HashSize; x++)
            {
                var pixel = pixels.GetPixel(x, y);
                values[idx++] = (byte)pixel.GetChannel(0);
            }
        }

        double mean = values.Average(v => (double)v);

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (values[i] >= mean)
                hash |= 1UL << i;
        }

        return hash;
    }

    public static List<DuplicateGroup> FindDuplicates(List<LibraryImageInfo> images)
    {
        var groups = new List<DuplicateGroup>();
        var usedInGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exact duplicates by file hash
        var byFileHash = images
            .Where(i => !string.IsNullOrEmpty(i.FileHash))
            .GroupBy(i => i.FileHash!)
            .Where(g => g.Count() > 1);

        int groupId = 0;
        foreach (var group in byFileHash)
        {
            var paths = group.Select(i => i.RelativePath).ToList();
            groups.Add(new DuplicateGroup
            {
                Id = $"dup-{groupId++:D3}",
                Type = DuplicateType.Exact,
                ImagePaths = paths,
                HammingDistance = 0
            });

            foreach (var p in paths)
                usedInGroup.Add(p);
        }

        // Near duplicates by perceptual hash
        var withPHash = images
            .Where(i => !string.IsNullOrEmpty(i.PerceptualHash) && !usedInGroup.Contains(i.RelativePath))
            .ToList();

        // Parse each perceptual hash exactly once up front. Parsing inside the
        // comparison loops turned an O(n^2) scan into O(n^2) string parses, which
        // dominated the cost for large libraries.
        var phashes = new ulong[withPHash.Count];
        for (int i = 0; i < withPHash.Count; i++)
            phashes[i] = ulong.Parse(withPHash[i].PerceptualHash!, System.Globalization.NumberStyles.HexNumber);

        var processed = new bool[withPHash.Count];
        var nearGroup = new List<int>();
        for (int i = 0; i < withPHash.Count; i++)
        {
            if (processed[i])
                continue;

            var hashA = phashes[i];
            nearGroup.Clear();
            nearGroup.Add(i);

            for (int j = i + 1; j < withPHash.Count; j++)
            {
                if (processed[j])
                    continue;

                if (ImageHash.HammingDistance(hashA, phashes[j]) <= NearDuplicateThreshold)
                    nearGroup.Add(j);
            }

            if (nearGroup.Count > 1)
            {
                var paths = nearGroup.Select(idx => withPHash[idx].RelativePath).ToList();

                // Compute max distance within group for reporting
                int maxDist = 0;
                for (int a = 0; a < nearGroup.Count; a++)
                    for (int b = a + 1; b < nearGroup.Count; b++)
                        maxDist = Math.Max(maxDist, ImageHash.HammingDistance(phashes[nearGroup[a]], phashes[nearGroup[b]]));

                groups.Add(new DuplicateGroup
                {
                    Id = $"dup-{groupId++:D3}",
                    Type = DuplicateType.NearDuplicate,
                    ImagePaths = paths,
                    HammingDistance = maxDist
                });

                foreach (var idx in nearGroup)
                    processed[idx] = true;
            }
        }

        return groups;
    }
}
