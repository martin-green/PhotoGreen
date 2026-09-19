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

    private static readonly Lock SaveLock = new();

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

    /// <summary>
    /// Writes the library file. The background AI pass, the scan and user edits can all
    /// save concurrently, so writes are serialised and staged through a temp file: a
    /// direct overwrite that fails part way through would leave a truncated, unreadable
    /// library behind.
    /// </summary>
    public static void Save(LibraryData data)
    {
        if (string.IsNullOrEmpty(data.RootFolder))
            return;

        var path = GetLibraryFilePath(data.RootFolder);
        lock (SaveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(data, JsonOptions);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
            }
            catch
            {
                // Silently ignore write failures
            }
        }
    }
}
