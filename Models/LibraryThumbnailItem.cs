using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGreen.Models;

public partial class LibraryThumbnailItem : ObservableObject
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    public LibraryImageInfo? ImageInfo { get; set; }
}
