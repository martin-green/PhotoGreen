using System.IO;
using System.Text.Json;

namespace PhotoGreen.Services;

public class AppSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoGreen");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    /// <summary>
    /// Shared instance. Settings live in one file, so every view model must observe the
    /// same in-memory copy; otherwise each holds a stale snapshot and the last one to
    /// call <see cref="Save"/> silently reverts the others' changes.
    /// </summary>
    public static AppSettingsService Instance { get; } = new();

    private readonly Lock _saveLock = new();

    public AppSettings Settings { get; private set; } = new();

    public AppSettingsService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            lock (_saveLock)
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
        }
        catch
        {
            // Silently ignore write failures
        }
    }
}

public class AppSettings
{
    public string LastDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Root folder of the photo library, restored on the next start.
    /// Empty means no library has been chosen yet.
    /// </summary>
    public string LibraryRootFolder { get; set; } = string.Empty;

    public List<string> ExpandedFolders { get; set; } = [];
    public string ImportDestinationFolder { get; set; } = string.Empty;
    public string ImportFolderPattern { get; set; } = "{yyyy}/{yyyy}-{MM}-{dd}";
    public bool AutoImportOnConnect { get; set; } = false;
    public HashSet<string> ImportedFileHashes { get; set; } = [];

    /// <summary>Ollama server endpoint. Empty means the default http://localhost:11434.</summary>
    public string AiEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Ollama model tag to use for vision analysis. Empty means auto-detect:
    /// the first locally installed vision-capable model is used.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;
}
