using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class FileBrowserViewModel : ObservableObject
{
    private readonly FileGroupingService _fileGroupingService = new();
    private readonly ThumbnailService _thumbnailService = new();
    private readonly AppSettingsService _settingsService = new();

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    [ObservableProperty]
    private ImageFileGroup? _selectedGroup;

    [ObservableProperty]
    private FolderNode? _selectedFolder;

    public ObservableCollection<ImageFileGroup> ImageGroups => _fileGroupingService.Groups;
    public ObservableCollection<FolderNode> RootFolders { get; } = new();

    public event Action<ImageFileGroup>? ImageSelected;

    public FileBrowserViewModel()
    {
        LoadDrives();
        RestoreExpandedFolders();
        RestoreLastDirectory();
    }

    private void RestoreLastDirectory()
    {
        var lastDir = _settingsService.Settings.LastDirectory;
        if (!string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir))
        {
            NavigateToDirectory(lastDir);
        }
    }

    private void RestoreExpandedFolders()
    {
        var expandedPaths = _settingsService.Settings.ExpandedFolders;
        if (expandedPaths.Count == 0)
            return;

        var pathSet = new HashSet<string>(expandedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var root in RootFolders)
        {
            ExpandMatchingNodes(root, pathSet);
        }
    }

    private static void ExpandMatchingNodes(FolderNode node, HashSet<string> pathSet)
    {
        if (string.IsNullOrEmpty(node.FullPath))
            return;

        if (pathSet.Any(p => p.StartsWith(node.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (!node.IsExpanded)
            {
                node.IsExpanded = true;
            }

            foreach (var child in node.Children)
            {
                ExpandMatchingNodes(child, pathSet);
            }
        }
    }

    private void SaveExpandedFolders()
    {
        var expanded = new List<string>();
        foreach (var root in RootFolders)
        {
            CollectExpandedPaths(root, expanded);
        }
        _settingsService.Settings.ExpandedFolders = expanded;
        _settingsService.Save();
    }

    private static void CollectExpandedPaths(FolderNode node, List<string> result)
    {
        if (string.IsNullOrEmpty(node.FullPath))
            return;

        if (node.IsExpanded)
        {
            result.Add(node.FullPath);
            foreach (var child in node.Children)
            {
                CollectExpandedPaths(child, result);
            }
        }
    }

    private void LoadDrives()
    {
        RootFolders.Clear();
        foreach (var drive in FileGroupingService.GetAvailableDrives())
        {
            var node = new FolderNode(drive.RootDirectory.FullName, drive.Name);
            node.ExpandChanged += SaveExpandedFolders;
            node.LoadSubFolders();
            RootFolders.Add(node);
        }
    }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        if (value != null)
        {
            NavigateToDirectory(value.FullPath);
        }
    }

    partial void OnSelectedGroupChanged(ImageFileGroup? value)
    {
        if (value != null)
        {
            ImageSelected?.Invoke(value);
        }
    }

    [RelayCommand]
    private void NavigateToDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        CurrentDirectory = path;
        _fileGroupingService.ScanDirectory(path);
        OnPropertyChanged(nameof(ImageGroups));

        _settingsService.Settings.LastDirectory = path;
        SaveExpandedFolders();

        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        foreach (var group in ImageGroups.ToList())
        {
            if (group.Thumbnail == null)
            {
                group.Thumbnail = await _thumbnailService.GetThumbnailAsync(group);
            }
        }
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(CurrentDirectory))
            return;

        var parent = Directory.GetParent(CurrentDirectory);
        if (parent != null)
        {
            NavigateToDirectory(parent.FullName);
        }
    }
}

public partial class FolderNode : ObservableObject
{
    public string FullPath { get; }
    public string DisplayName { get; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    private bool _hasLoadedChildren;

    public event Action? ExpandChanged;

    public FolderNode(string fullPath, string? displayName = null)
    {
        FullPath = fullPath;
        DisplayName = displayName ?? Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(DisplayName))
            DisplayName = fullPath;

        // Only add a placeholder for real folders, not for placeholders themselves
        if (!string.IsNullOrEmpty(fullPath))
        {
            Children.Add(new FolderNode("", "Loading..."));
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_hasLoadedChildren)
        {
            LoadSubFolders();
        }
        ExpandChanged?.Invoke();
    }

    public void LoadSubFolders()
    {
        _hasLoadedChildren = true;
        Children.Clear();

        try
        {
            foreach (var dir in FileGroupingService.GetSubDirectories(FullPath))
            {
                var child = new FolderNode(dir);
                child.ExpandChanged += ExpandChanged;
                Children.Add(child);
            }
        }
        catch
        {
            // Access denied or other IO error — leave empty
        }
    }
}
