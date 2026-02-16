using System.IO;
using System.Text.Json;

namespace PhotoGreen.Services;

public class AppSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoGreen");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

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
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
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
    public List<string> ExpandedFolders { get; set; } = [];
    public string ImportDestinationFolder { get; set; } = string.Empty;
    public string ImportFolderPattern { get; set; } = "{yyyy}/{yyyy}-{MM}-{dd}";
    public bool AutoImportOnConnect { get; set; } = false;
    public HashSet<string> ImportedFileHashes { get; set; } = [];
}
