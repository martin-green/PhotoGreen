using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OllamaSharp;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public partial class AIService
{
    public const string DefaultEndpoint = "http://localhost:11434";
    public const string FallbackModel = "llama3.2-vision";

    private const string EndpointEnvVar = "PHOTOGREEN_AI_ENDPOINT";
    private const string ModelEnvVar = "PHOTOGREEN_AI_MODEL";

    /// <summary>
    /// Name fragments of known vision-capable Ollama models, most preferred first.
    /// Used when no explicit model is configured.
    /// </summary>
    private static readonly string[] KnownVisionModels =
    [
        "llama3.2-vision",
        "qwen3-vl",
        "qwen2.5-vl",
        "qwen2-vl",
        "llava",
        "bakllava",
        "minicpm-v",
        "moondream",
        "gemma3",
        "mistral-small3",
        "granite3.2-vision",
        "internvl",
        "pixtral",
        "glm-4v"
    ];

    private readonly OllamaApiClient _client;
    private readonly string? _configuredModel;
    private bool _isAvailable;

    /// <summary>
    /// Used for image analysis instead of the chat abstraction so we can send Ollama's
    /// "think" flag. Reasoning models such as Qwen3-VL otherwise spend thousands of
    /// tokens per image on reasoning, which dominates scan time.
    /// </summary>
    private static readonly HttpClient AnalysisHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public bool IsAvailable => _isAvailable;
    public string Endpoint { get; }

    /// <summary>Message of the last failure, for diagnostics/logging. Null when the last call succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The model currently in use. Until <see cref="CheckAvailabilityAsync"/> runs this is the
    /// configured model (or the fallback); afterwards it reflects the model actually resolved.
    /// </summary>
    public string Model { get; private set; }

    /// <summary>True when the model was auto-detected rather than explicitly configured.</summary>
    public bool ModelWasAutoDetected { get; private set; }

    /// <summary>
    /// Creates the service. Explicit arguments win, then the PHOTOGREEN_AI_ENDPOINT /
    /// PHOTOGREEN_AI_MODEL environment variables, then persisted app settings.
    /// A null/empty model enables auto-detection of an installed vision model.
    /// </summary>
    public AIService(string? endpoint = null, string? model = null, AppSettings? settings = null)
    {
        Endpoint = FirstNonEmpty(
            endpoint,
            Environment.GetEnvironmentVariable(EndpointEnvVar),
            settings?.AiEndpoint) ?? DefaultEndpoint;

        _configuredModel = FirstNonEmpty(
            model,
            Environment.GetEnvironmentVariable(ModelEnvVar),
            settings?.AiModel);

        Model = _configuredModel ?? FallbackModel;
        ModelWasAutoDetected = _configuredModel is null;

        _client = new OllamaApiClient(new Uri(Endpoint), Model);
    }

    /// <summary>Creates a service configured from persisted application settings.</summary>
    public static AIService FromSettings(AppSettingsService settingsService) =>
        new(settings: settingsService.Settings);

    /// <summary>
    /// Lists the models installed on the Ollama server. Returns an empty list if unreachable.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await _client.ListLocalModelsAsync(cancellationToken);
            return models.Select(m => m.Name).ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await _client.ListLocalModelsAsync(cancellationToken);
            var names = models.Select(m => m.Name).ToList();

            var resolved = ResolveModel(names, _configuredModel);
            if (resolved is null)
            {
                _isAvailable = false;
                return false;
            }

            Model = resolved;
            ModelWasAutoDetected = _configuredModel is null;
            _client.SelectedModel = resolved;
            _isAvailable = true;
            return true;
        }
        catch
        {
            _isAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Picks the model to use. An explicitly configured model must be installed (matched by
    /// exact tag, then base name, then substring). Otherwise the first installed known vision
    /// model is chosen.
    /// </summary>
    internal static string? ResolveModel(IReadOnlyList<string> installed, string? configured)
    {
        if (installed.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var wanted = configured.Trim();

            var exact = installed.FirstOrDefault(
                n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            // "qwen3-vl" should match the installed "qwen3-vl:8b".
            var byBaseName = installed.FirstOrDefault(
                n => BaseName(n).Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (byBaseName is not null)
                return byBaseName;

            return installed.FirstOrDefault(
                n => n.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var candidate in KnownVisionModels)
        {
            var match = installed.FirstOrDefault(
                n => n.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static string BaseName(string modelName)
    {
        var colon = modelName.IndexOf(':');
        return colon < 0 ? modelName : modelName[..colon];
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public async Task<ImageAnalysisResult?> AnalyzeImageAsync(
        byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
            return null;

        LastError = null;

        const string instructions =
            """
                    You are an image analysis assistant. Analyze the image and reply with a single JSON object and nothing else.

                    Schema (all fields required, use exactly these names):
                    {
                      "faceCount": <integer, number of clearly visible human faces, 0 if none>,
                      "faces": [
                        {
                          "description": "<short visual description, e.g. 'adult male, brown hair'>",
                          "approximate_position": "<one of: left, center, right>"
                        }
                      ],
                      "description": "<one sentence describing the scene>",
                      "tags": ["<3 to 10 lowercase keyword tags>"],
                      "scene": "<single best-fit category, see list below>"
                    }

                    Scene categories (choose exactly one):
                    - portrait: a person or people are the main subject
                    - landscape: natural scenery is the main subject
                    - macro: extreme close-up of a small subject
                    - night: shot in darkness or low light
                    - indoor: taken inside a building, no single dominant subject
                    - action: motion or sport is the main subject
                    - architecture: buildings or structures are the main subject
                    - street: candid public scene

                    Rules:
                    - "faces" must contain exactly "faceCount" entries; use [] when faceCount is 0.
                    - Describe only what is visible. Do not guess names or identities.
                    - Do not include markdown, code fences, commentary, or reasoning.
                    - Output must start with '{' and end with '}'.
                    """;

        // Some Ollama vision models (notably llama3.2-vision) return HTTP 500 when an image is
        // combined with a separate system message. Retry once with the instructions inlined into
        // the user message before giving up. An empty reply also triggers the retry, since a
        // blank result is as useless as a failed one.
        var text = await TryGetResponseTextAsync(instructions, imageBytes, useSystemMessage: true, cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
            text = await TryGetResponseTextAsync(instructions, imageBytes, useSystemMessage: false, cancellationToken);

        return string.IsNullOrWhiteSpace(text) ? null : ParseAnalysisResult(text);
    }

    /// <summary>
    /// Shape of the /api/chat reply we care about. Reasoning models may place the answer
    /// in "thinking" rather than "content" when reasoning is suppressed, so both are read.
    /// </summary>
    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }

    private sealed class OllamaChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; }
    }

    private async Task<string?> TryGetResponseTextAsync(
        string instructions, byte[] imageBytes, bool useSystemMessage, CancellationToken cancellationToken)
    {
        try
        {
            // Calls /api/chat directly rather than through IChatClient: the abstraction has
            // no way to express Ollama's "think" flag, and leaving reasoning enabled costs
            // thousands of extra tokens (measured ~20-30s vs ~2s per image on Qwen3-VL).
            var messages = new List<object>();

            if (useSystemMessage)
            {
                messages.Add(new { role = "system", content = instructions });
                messages.Add(new
                {
                    role = "user",
                    content = "Analyze this image.",
                    images = new[] { Convert.ToBase64String(imageBytes) }
                });
            }
            else
            {
                messages.Add(new
                {
                    role = "user",
                    content = instructions + "\n\nAnalyze this image.",
                    images = new[] { Convert.ToBase64String(imageBytes) }
                });
            }

            var request = new
            {
                model = Model,
                messages,
                stream = false,
                // Both flags are required together. Measured on Qwen3-VL, "think" alone
                // still burns ~1850 tokens of reasoning (~28s) and "format" alone ~1820
                // (~19s), but combined the model answers directly in ~217 tokens (~2s).
                think = false,
                format = "json",
                options = new { temperature = 0 }
            };

            var text = await PostChatAsync(request, cancellationToken);

            // Non-reasoning models reject the "think" flag outright; retry without it so
            // those models keep working (they have no reasoning overhead to suppress).
            if (text is null)
            {
                var fallback = new
                {
                    model = Model,
                    messages,
                    stream = false,
                    format = "json",
                    options = new { temperature = 0 }
                };

                text = await PostChatAsync(fallback, cancellationToken);
            }

            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"{Model} @ {Endpoint}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"AIService.AnalyzeImageAsync failed: {LastError}");
            return null;
        }
    }

    /// <summary>
    /// Posts one /api/chat request. Returns null when the server rejects the request,
    /// so the caller can retry with a reduced payload.
    /// </summary>
    private async Task<string?> PostChatAsync(object request, CancellationToken cancellationToken)
    {
        using var httpResponse = await AnalysisHttp.PostAsJsonAsync(
            $"{Endpoint.TrimEnd('/')}/api/chat", request, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            LastError = $"{Model} @ {Endpoint}: HTTP {(int)httpResponse.StatusCode}";
            return null;
        }

        var parsed = await httpResponse.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken);

        // Prefer content, but fall back to thinking: with think=false some models emit
        // the whole answer in the thinking field instead of leaving it empty.
        var content = parsed?.Message?.Content;
        if (!string.IsNullOrWhiteSpace(content))
            return content;

        var thinking = parsed?.Message?.Thinking;
        return string.IsNullOrWhiteSpace(thinking) ? string.Empty : thinking;
    }

    /// <summary>
    /// Classifies the scene. This delegates to <see cref="AnalyzeImageAsync"/>, which returns the
    /// scene category alongside the description, tags and faces in a single request. Prefer calling
    /// <see cref="AnalyzeImageAsync"/> directly when any of that extra data is also needed, so the
    /// image is only sent to the model once.
    /// </summary>
    public async Task<SceneType> ClassifySceneAsync(
        byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeImageAsync(imageBytes, cancellationToken);
        return analysis?.SceneType ?? SceneType.Unknown;
    }

    /// <summary>
    /// Maps a model reply to a <see cref="SceneType"/>. Tolerates surrounding punctuation,
    /// reasoning blocks and short explanatory sentences.
    /// </summary>
    internal static SceneType ParseSceneType(string? text)
    {
        var cleaned = StripModelNoise(text).ToLowerInvariant();
        if (cleaned.Length == 0)
            return SceneType.Unknown;

        var words = cleaned.Split(
            [' ', '\t', '\r', '\n', '.', ',', ':', ';', '!', '?', '"', '\'', '*', '`', '-'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Prefer the last category word: models often end with their conclusion.
        for (var i = words.Length - 1; i >= 0; i--)
        {
            var match = MapSceneWord(words[i]);
            if (match != SceneType.Unknown)
                return match;
        }

        return SceneType.Unknown;
    }

    private static SceneType MapSceneWord(string word) => word switch
    {
        "portrait" or "portraits" => SceneType.Portrait,
        "landscape" or "landscapes" => SceneType.Landscape,
        "macro" or "closeup" => SceneType.Macro,
        "night" or "nighttime" => SceneType.Night,
        "indoor" or "indoors" => SceneType.Indoor,
        "action" or "sport" or "sports" => SceneType.Action,
        "architecture" or "architectural" => SceneType.Architecture,
        "street" => SceneType.Street,
        _ => SceneType.Unknown
    };

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
                    """
                    You compare two face images and judge whether they show the same person.

                    Base the judgement only on stable facial features (face shape, eye spacing,
                    nose and mouth shape, ears). Ignore lighting, pose, expression, age difference,
                    hairstyle, glasses, headwear and image quality.

                    Answer "yes" only if you are confident it is the same person.
                    Answer "no" if they are different people or you are unsure.
                    Reply with exactly one word: yes or no. No explanation or reasoning.
                    """),
                new(ChatRole.User, [
                    new TextContent("First image:"),
                    new DataContent(imageA, "image/jpeg"),
                    new TextContent("Second image:"),
                    new DataContent(imageB, "image/jpeg"),
                    new TextContent("Are these the same person?")
                ])
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);

            return ParseYesNo(response.Text);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Interprets a yes/no reply. Defaults to false unless an affirmative is clearly present,
    /// so ambiguous answers never merge two different people.
    /// </summary>
    internal static bool ParseYesNo(string? text)
    {
        var cleaned = StripModelNoise(text).ToLowerInvariant();
        if (cleaned.Length == 0)
            return false;

        var words = cleaned.Split(
            [' ', '\t', '\r', '\n', '.', ',', ':', ';', '!', '?', '"', '\'', '*', '`'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = words.Length - 1; i >= 0; i--)
        {
            if (words[i] is "yes")
                return true;
            if (words[i] is "no" or "not")
                return false;
        }

        return false;
    }

    /// <summary>
    /// Removes reasoning blocks (e.g. &lt;think&gt;...&lt;/think&gt; emitted by Qwen3-VL and
    /// similar models) and markdown code fences from a model reply.
    /// </summary>
    internal static string StripModelNoise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var result = ReasoningBlockRegex().Replace(text, " ");

        // Drop an unterminated reasoning block (truncated or streamed response).
        var danglingOpen = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (danglingOpen >= 0)
            result = result[..danglingOpen];

        result = result.Replace("```json", " ", StringComparison.OrdinalIgnoreCase)
                       .Replace("```", " ");

        return result.Trim();
    }

    [GeneratedRegex(@"<(think|thinking|reasoning)>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningBlockRegex();

    private static ImageAnalysisResult? ParseAnalysisResult(string text)
    {
        try
        {
            // Remove reasoning blocks and markdown fences before locating the JSON object.
            var cleaned = StripModelNoise(text);

            var jsonStart = cleaned.IndexOf('{');
            var jsonEnd = cleaned.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return null;

            var json = cleaned[jsonStart..(jsonEnd + 1)];

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            var result = JsonSerializer.Deserialize<ImageAnalysisResult>(json, options);
            if (result is null)
                return null;

            Normalize(result);

            // An empty object such as "{}" deserializes successfully but carries no
            // analysis. Treat that as a failure so the image is retried later.
            if (!HasUsableAnalysis(result))
                return null;

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the parsed result contains at least one piece of real analysis.</summary>
    private static bool HasUsableAnalysis(ImageAnalysisResult result) =>
        !string.IsNullOrWhiteSpace(result.Description) ||
        !string.IsNullOrWhiteSpace(result.Scene) ||
        result.Tags.Count > 0 ||
        result.Faces.Count > 0;

    /// <summary>
    /// Cleans up model output: trims tags, drops empty entries, and reconciles
    /// <see cref="ImageAnalysisResult.FaceCount"/> with the returned face list.
    /// </summary>
    private static void Normalize(ImageAnalysisResult result)
    {
        result.Description = result.Description?.Trim() ?? string.Empty;

        result.Scene = result.Scene?.Trim() ?? string.Empty;
        result.SceneType = ParseSceneType(result.Scene);

        result.Tags = result.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        result.Faces = result.Faces
            .Where(f => f is not null)
            .Select(f => new AnalyzedFace
            {
                Description = f.Description?.Trim() ?? string.Empty,
                ApproximatePosition = NormalizePosition(f.ApproximatePosition)
            })
            .ToList();

        // Models frequently disagree with themselves; trust the actual list.
        if (result.Faces.Count > 0 || result.FaceCount < 0)
            result.FaceCount = result.Faces.Count;
    }

    private static string NormalizePosition(string? position)
    {
        var value = position?.Trim().ToLowerInvariant() ?? string.Empty;

        return value switch
        {
            "left" or "center" or "right" => value,
            "centre" or "middle" or "centered" => "center",
            _ when value.Contains("left") => "left",
            _ when value.Contains("right") => "right",
            _ when value.Contains("cent") || value.Contains("middle") => "center",
            _ => string.Empty
        };
    }
}

public class ImageAnalysisResult
{
    [JsonPropertyName("faceCount")]
    public int FaceCount { get; set; }

    [JsonPropertyName("faces")]
    public List<AnalyzedFace> Faces { get; set; } = [];

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>Raw scene category word returned by the model.</summary>
    [JsonPropertyName("scene")]
    public string Scene { get; set; } = string.Empty;

    /// <summary>The parsed scene category. <see cref="SceneType.Unknown"/> if absent or unrecognized.</summary>
    [JsonIgnore]
    public SceneType SceneType { get; set; } = SceneType.Unknown;
}

public class AnalyzedFace
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("approximate_position")]
    public string ApproximatePosition { get; set; } = string.Empty;
}
