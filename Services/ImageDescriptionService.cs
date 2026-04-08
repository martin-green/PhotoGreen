using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class ImageDescriptionService
{
    private readonly AIService _aiService;

    public ImageDescriptionService(AIService aiService)
    {
        _aiService = aiService;
    }

    public (string? description, List<string> tags, List<FaceInfo> faces) ExtractFromAnalysis(
        ImageAnalysisResult? analysis)
    {
        if (analysis == null)
            return (null, [], []);

        var faces = analysis.Faces.Select(f => new FaceInfo
        {
            Description = f.Description,
            ApproximatePosition = f.ApproximatePosition,
            Confidence = 0.8
        }).ToList();

        return (analysis.Description, analysis.Tags, faces);
    }
}
