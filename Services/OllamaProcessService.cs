using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace PhotoGreen.Services;

/// <summary>
/// Detects whether a local Ollama server is running and can start it on demand,
/// optionally preloading a model so the first analysis request is not slowed by
/// the model load.
/// </summary>
public class OllamaProcessService
{
    /// <summary>Short timeout: used for liveness probes only.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Loading a multi-gigabyte model from disk can take a while.</summary>
    private static readonly HttpClient LoadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Concurrent request slots requested from Ollama when we start it. Each slot costs
    /// additional KV-cache VRAM, so this stays modest to avoid pushing a vision model
    /// out of VRAM and into far slower CPU inference.
    /// </summary>
    public const int DefaultNumParallel = 32;

    private readonly string _endpoint;

    public OllamaProcessService(string? endpoint = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? AIService.DefaultEndpoint : endpoint.Trim();
    }

    /// <summary>True when the endpoint points at this machine, so we are able to launch the server.</summary>
    public bool IsLocalEndpoint
    {
        get
        {
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri))
                return false;

            return uri.IsLoopback
                || string.Equals(uri.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Returns true when the Ollama HTTP API responds at the configured endpoint.</summary>
    public async Task<bool> IsServerRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                new Uri(new Uri(_endpoint), "/api/tags"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Locates the ollama executable, or null when it is not installed / not on PATH.</summary>
    public static string? FindExecutable()
    {
        var candidates = new List<string>();

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "ollama.exe"));
            }
            catch
            {
                // Ignore malformed PATH entries
            }
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ollama", "ollama.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Ensures the server is running, starting "ollama serve" if needed, then waits for the
    /// API to respond. Returns the outcome and a message suitable for display.
    /// </summary>
    public async Task<(bool success, string message)> EnsureServerRunningAsync(
        CancellationToken cancellationToken = default)
    {
        if (await IsServerRunningAsync(cancellationToken))
            return (true, "Ollama is already running.");

        if (!IsLocalEndpoint)
            return (false, $"Ollama is not reachable at {_endpoint}, and it is not a local address so it cannot be started from here.");

        var exe = FindExecutable();
        if (exe is null)
            return (false, "Could not find ollama.exe. Install Ollama from https://ollama.com/download.");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Ollama defaults to a single in-flight request per model, which leaves the
            // GPU idle between our vision calls. Allowing a few concurrent slots lets one
            // request be decoded/prefilled while another is generating. Only set these
            // when the user has not configured them explicitly.
            if (Environment.GetEnvironmentVariable("OLLAMA_NUM_PARALLEL") is null)
                startInfo.Environment["OLLAMA_NUM_PARALLEL"] = DefaultNumParallel.ToString();

            if (Environment.GetEnvironmentVariable("OLLAMA_MAX_LOADED_MODELS") is null)
                startInfo.Environment["OLLAMA_MAX_LOADED_MODELS"] = "1";

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, "Failed to start the Ollama server process.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start Ollama: {ex.Message}");
        }

        // The server needs a moment to bind its port.
        if (await WaitForServerAsync(TimeSpan.FromSeconds(30), cancellationToken))
            return (true, "Ollama server started.");

        return (false, "Started Ollama but the server did not respond in time.");
    }

    private async Task<bool> WaitForServerAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            if (await IsServerRunningAsync(cancellationToken))
                return true;

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks Ollama to load the model into memory without generating anything, so the first
    /// real request is fast. Returns false if the model could not be loaded.
    /// </summary>
    public async Task<bool> PreloadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        try
        {
            // An empty prompt tells Ollama to load the model and return immediately.
            var payload = new StringContent(
                $$"""{"model":{{System.Text.Json.JsonSerializer.Serialize(model)}},"keep_alive":"15m"}""",
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await LoadHttp.PostAsync(
                new Uri(new Uri(_endpoint), "/api/generate"), payload, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
