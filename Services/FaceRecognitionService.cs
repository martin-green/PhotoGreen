using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class FaceRecognitionService
{
    private readonly AIService _aiService;

    public FaceRecognitionService(AIService aiService)
    {
        _aiService = aiService;
    }

    public List<FaceInfo> ExtractFaces(ImageAnalysisResult? analysis)
    {
        if (analysis == null || analysis.FaceCount == 0)
            return [];

        return analysis.Faces.Select(f => new FaceInfo
        {
            Description = f.Description,
            ApproximatePosition = f.ApproximatePosition,
            Confidence = 0.8
        }).ToList();
    }

    public List<FaceCluster> ClusterFaces(List<LibraryImageInfo> images)
    {
        var clusters = new List<FaceCluster>();
        var clusterIndex = 0;

        // Group faces by description similarity
        var faceEntries = new List<(string imagePath, FaceInfo face)>();

        foreach (var image in images)
        {
            foreach (var face in image.Faces)
            {
                faceEntries.Add((image.RelativePath, face));
            }
        }

        if (faceEntries.Count == 0)
            return clusters;

        // Simple clustering: group by description keywords
        var assigned = new HashSet<int>();

        for (int i = 0; i < faceEntries.Count; i++)
        {
            if (assigned.Contains(i))
                continue;

            var cluster = new FaceCluster
            {
                Id = $"face-{clusterIndex++:D3}",
                Name = faceEntries[i].face.Label ?? faceEntries[i].face.Description,
                RepresentativeImagePath = faceEntries[i].imagePath,
                ImagePaths = [faceEntries[i].imagePath]
            };

            assigned.Add(i);

            var keywordsA = ExtractKeywords(faceEntries[i].face.Description ?? string.Empty);

            for (int j = i + 1; j < faceEntries.Count; j++)
            {
                if (assigned.Contains(j))
                    continue;

                var keywordsB = ExtractKeywords(faceEntries[j].face.Description ?? string.Empty);
                double similarity = ComputeJaccardSimilarity(keywordsA, keywordsB);

                if (similarity >= 0.4)
                {
                    if (!cluster.ImagePaths.Contains(faceEntries[j].imagePath))
                        cluster.ImagePaths.Add(faceEntries[j].imagePath);
                    assigned.Add(j);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static HashSet<string> ExtractKeywords(string description)
    {
        return description
            .ToLowerInvariant()
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
    }

    private static double ComputeJaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return 0;

        int intersection = a.Intersect(b).Count();
        int union = a.Union(b).Count();

        return union == 0 ? 0 : (double)intersection / union;
    }
}
