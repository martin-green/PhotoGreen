using System.Collections.ObjectModel;

namespace PhotoGreen.Models;

public class LibraryGroup
{
    public string Name { get; set; } = string.Empty;
    public int Count => Items.Count;
    public ObservableCollection<LibraryThumbnailItem> Items { get; } = [];
}
