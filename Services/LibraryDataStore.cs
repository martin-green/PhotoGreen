using System.IO;
using System.Text.Json;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public class LibraryDataStore
{
    private const string LibraryFileName = ".photogreen-library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetLibraryFilePath(string rootFolder)
    {
        return Path.Combine(rootFolder, LibraryFileName);
    }

    public static LibraryData Load(string rootFolder)
    {
        var path = GetLibraryFilePath(rootFolder);
        if (!File.Exists(path))
            return new LibraryData { RootFolder = rootFolder };

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<LibraryData>(json, JsonOptions);
            return data ?? new LibraryData { RootFolder = rootFolder };
        }
        catch
        {
            return new LibraryData { RootFolder = rootFolder };
        }
    }

    public static void Save(LibraryData data)
    {
        if (string.IsNullOrEmpty(data.RootFolder))
            return;

        var path = GetLibraryFilePath(data.RootFolder);
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Silently ignore write failures
        }
    }
}
