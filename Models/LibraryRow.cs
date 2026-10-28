namespace PhotoGreen.Models;

/// <summary>
/// A single row in the virtualized library list. Groups are flattened into a header
/// row followed by fixed-width thumbnail rows so only visible rows get realized.
/// </summary>
public abstract class LibraryRow;

public sealed class LibraryHeaderRow(LibraryGroup group) : LibraryRow
{
    public string Name => group.Name;
    public int Count => group.Count;
}

public sealed class LibraryThumbnailRow(IReadOnlyList<LibraryThumbnailItem> items) : LibraryRow
{
    public IReadOnlyList<LibraryThumbnailItem> Items { get; } = items;
}
