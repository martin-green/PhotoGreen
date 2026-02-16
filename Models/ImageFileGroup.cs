using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace PhotoGreen.Models;

public class ImageFileGroup : INotifyPropertyChanged
{
    public string Stem { get; }
    public ImageFileEntry PrimaryFile { get; }
    public IReadOnlyList<ImageFileEntry> SidecarFiles { get; }
    public IReadOnlyList<ImageFileEntry> AllFiles { get; }

    public string DisplayName => PrimaryFile.FileName;
    public bool HasSidecars => SidecarFiles.Count > 0;
    public bool IsRawPrimary => PrimaryFile.FileType == ImageFileType.Raw;

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public ImageFileGroup(string stem, IEnumerable<ImageFileEntry> files)
    {
        Stem = stem;

        var fileList = files.ToList();
        AllFiles = fileList;

        // Prefer raw as primary; fall back to first file
        PrimaryFile = fileList.FirstOrDefault(f => f.FileType == ImageFileType.Raw)
                      ?? fileList[0];

        SidecarFiles = fileList.Where(f => f != PrimaryFile).ToList();
    }
}
