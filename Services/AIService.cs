using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class AIService
{
    private const string DefaultEndpoint = "http://localhost:11434";
    private const string DefaultModel = "llama3.2-vision";

    private readonly OllamaApiClient _client;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;
    public string Endpoint { get; }
    public string Model { get; }

    public AIService(string? endpoint = null, string? model = null)
    {
        Endpoint = endpoint ?? DefaultEndpoint;
        Model = model ?? DefaultModel;
        _client = new OllamaApiClient(new Uri(Endpoint), Model);
    }

    public async Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await _client.ListLocalModelsAsync(cancellationToken);
            _isAvailable = models.Any(m =>
                m.Name.Contains(Model, StringComparison.OrdinalIgnoreCase));
            return _isAvailable;
        }
        catch
        {
            _isAvailable = false;
            return false;
        }
    }

    public async Task<ImageAnalysisResult?> AnalyzeImageAsync(
        byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
            return null;

        try
        {
            IChatClient chatClient = _client;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    """
                    You are an image analysis assistant. Analyze the provided image and return ONLY a valid JSON object with these fields:
                    - "faceCount": number of human faces visible
                    - "faces": array of objects with "description" (e.g. "adult male, brown hair") and "approximate_position" ("left", "center", "right")
                    - "description": one-sentence scene description
                    - "tags": array of 3-10 descriptive keyword tags
                    Return ONLY the JSON object, no other text.
                    """),
                new(ChatRole.User, [
                    new DataContent(imageBytes, "image/jpeg"),
                    new TextContent("Analyze this image.")
                ])
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var text = response.Text ?? string.Empty;

            return ParseAnalysisResult(text);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SceneType> ClassifySceneAsync(
        byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
            return SceneType.Unknown;

        try
        {
            IChatClient chatClient = _client;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    """
                    Classify this photo into exactly one category. Return ONLY one word from this list:
                    portrait, landscape, macro, night, indoor, action, architecture, street
                    """),
                new(ChatRole.User, [
                    new DataContent(imageBytes, "image/jpeg"),
                    new TextContent("What type of photo is this?")
                ])
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var text = response.Text?.Trim().ToLowerInvariant() ?? string.Empty;

            return text switch
            {
                "portrait" => SceneType.Portrait,
                "landscape" => SceneType.Landscape,
                "macro" => SceneType.Macro,
                "night" => SceneType.Night,
                "indoor" => SceneType.Indoor,
                "action" => SceneType.Action,
                "architecture" => SceneType.Architecture,
                "street" => SceneType.Street,
                _ => SceneType.Unknown
            };
        }
        catch
        {
            return SceneType.Unknown;
        }
    }

    public async Task<bool> AreSamePersonAsync(
        byte[] imageA, byte[] imageB, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
            return false;

        try
        {
            IChatClient chatClient = _client;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "Compare the two provided images. Are these the same person? Answer ONLY 'yes' or 'no'."),
                new(ChatRole.User, [
                    new DataContent(imageA, "image/jpeg"),
                    new DataContent(imageB, "image/jpeg"),
                    new TextContent("Are these the same person?")
                ])
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var text = response.Text?.Trim().ToLowerInvariant() ?? string.Empty;

            return text.StartsWith("yes");
        }
        catch
        {
            return false;
        }
    }

    private static ImageAnalysisResult? ParseAnalysisResult(string text)
    {
        try
        {
            // Extract JSON from response (model may include markdown fences)
            var jsonStart = text.IndexOf('{');
            var jsonEnd = text.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return null;

            var json = text[jsonStart..(jsonEnd + 1)];

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<ImageAnalysisResult>(json, options);
        }
        catch
        {
            return null;
        }
    }
}

public class ImageAnalysisResult
{
    public int FaceCount { get; set; }
    public List<AnalyzedFace> Faces { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
}

public class AnalyzedFace
{
    public string Description { get; set; } = string.Empty;
    public string ApproximatePosition { get; set; } = string.Empty;
}
