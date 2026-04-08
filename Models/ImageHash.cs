namespace PhotoGreen.Models;

public class ImageHash
{
    public ulong PerceptualHash { get; set; }
    public string FileHash { get; set; } = string.Empty;

    public static int HammingDistance(ulong a, ulong b)
    {
        return (int)ulong.PopCount(a ^ b);
    }
}
