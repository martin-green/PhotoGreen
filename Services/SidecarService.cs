using System.IO;
using System.Text.Json;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

public static class SidecarService
{
    private const string SidecarExtension = ".pgr";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string GetSidecarPath(string imageFilePath)
    {
        return Path.ChangeExtension(imageFilePath, SidecarExtension);
    }

    public static bool SidecarExists(string imageFilePath)
    {
        return File.Exists(GetSidecarPath(imageFilePath));
    }

    public static void Save(string imageFilePath, DevelopSettings settings)
    {
        var sidecarPath = GetSidecarPath(imageFilePath);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(sidecarPath, json);
    }

    public static DevelopSettings? Load(string imageFilePath)
    {
        var sidecarPath = GetSidecarPath(imageFilePath);
        if (!File.Exists(sidecarPath))
            return null;

        try
        {
            var json = File.ReadAllText(sidecarPath);
            return JsonSerializer.Deserialize<DevelopSettings>(json);
        }
        catch
        {
            return null;
        }
    }
}
