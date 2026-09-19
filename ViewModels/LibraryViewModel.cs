using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly AIService _aiService = AIService.FromSettings(AppSettingsService.Instance);
    private readonly AppSettingsService _settingsService = AppSettingsService.Instance;
    private LibraryScanService? _scanService;
    private LibraryData? _libraryData;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _analyzeCts;
    private Task? _analyzeTask;
    private CancellationTokenSource? _thumbnailCts;
    private ThumbnailCacheService? _thumbnailCache;

    [ObservableProperty]
    private string _rootFolder = string.Empty;

    [ObservableProperty]
    private LibraryGroupMode _groupMode = LibraryGroupMode.Folder;

    [ObservableProperty]
    private LibrarySortMode _sortMode = LibrarySortMode.NameAscending;

    /// <summary>
    /// Case-insensitive substring applied to file names; empty shows everything.
    /// </summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>True while the background AI classification pass is running.</summary>
    [ObservableProperty]
    private bool _isAnalyzing;

    /// <summary>Progress of the background AI pass, reported separately from the scan.</summary>
    [ObservableProperty]
    private double _analyzeProgress;

    [ObservableProperty]
    private string _analyzeStatusText = string.Empty;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanStatusText = string.Empty;

    [ObservableProperty]
    private bool _aiAvailable;

    /// <summary>Human-readable AI connection detail shown next to the status indicator.</summary>
    [ObservableProperty]
    private string _aiStatusText = "Checking...";

    /// <summary>True while the Ollama server is being started, to disable the button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAiCommand))]
    private bool _isStartingAi;

    [ObservableProperty]
    private LibraryThumbnailItem? _selectedItem;

    /// <summary>Backing store for the multi-selection, in the order items were selected.</summary>
    private readonly List<LibraryThumbnailItem> _selectedItems = [];

    /// <summary>Anchor used as the fixed end of a shift-click range.</summary>
    private LibraryThumbnailItem? _selectionAnchor;

    /// <summary>Number of currently selected thumbnails.</summary>
    [ObservableProperty]
    private int _selectedCount;

    /// <summary>Display text such as "3 selected"; empty when nothing is selected.</summary>
    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    /// <summary>The selected thumbnails, in selection order.</summary>
    public IReadOnlyList<LibraryThumbnailItem> SelectedItems => _selectedItems;

    public ObservableCollection<LibraryGroup> Groups { get; } = [];

    // Detail panel data
    [ObservableProperty]
    private string _detailFileName = string.Empty;

    [ObservableProperty]
    private string _detailDescription = string.Empty;

    [ObservableProperty]
    private string _detailTags = string.Empty;

    [ObservableProperty]
    private string _detailFaces = string.Empty;

    [ObservableProperty]
    private string _detailDuplicateInfo = string.Empty;

    public event Action<string>? OpenInEditorRequested;

    public LibraryViewModel()
    {
        RestoreLastLibraryFolder();
        _ = CheckAIAsync();
    }

    /// <summary>
    /// Reopens the library that was in use when the app last exited. A folder that has
    /// since been deleted or unmounted is ignored so startup still succeeds.
    /// </summary>
    private void RestoreLastLibraryFolder()
    {
        var folder = _settingsService.Settings.LibraryRootFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        RootFolder = folder;
        ScanStatusText = "Opening library...";

        // LoadLibrary walks the whole tree and fills UI-bound collections, so it has to
        // run on the UI thread. Queue it at Background priority so the window renders
        // first instead of appearing to hang on a large library.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            LoadLibraryRestoringErrors();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, LoadLibraryRestoringErrors);
    }

    /// <summary>
    /// Wraps <see cref="LoadLibrary"/> so a failure to reopen the previous library is
    /// reported in the status bar rather than taking down startup.
    /// </summary>
    private void LoadLibraryRestoringErrors()
    {
        try
        {
            LoadLibrary();
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Could not open last library: {ex.Message}";
        }
    }

    private async Task CheckAIAsync()
    {
        AiAvailable = await _aiService.CheckAvailabilityAsync();
        AiStatusText = AiAvailable
            ? _aiService.Model
            : await DescribeUnavailableReasonAsync();
    }

    /// <summary>
    /// Works out why the AI is unavailable so the UI can tell the user whether to start
    /// the server, install a model, or fix the endpoint.
    /// </summary>
    private async Task<string> DescribeUnavailableReasonAsync()
    {
        var process = new OllamaProcessService(_aiService.Endpoint);

        if (!await process.IsServerRunningAsync())
            return "Ollama not running";

        var models = await _aiService.ListAvailableModelsAsync();
        return models.Count == 0
            ? "No models installed"
            : "No vision model found";
    }

    /// <summary>
    /// Starts the Ollama server if it is not already running, then loads the vision model
    /// so the first analysis is not delayed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAi))]
    private async Task StartAi()
    {
        IsStartingAi = true;
        try
        {
            var process = new OllamaProcessService(_aiService.Endpoint);

            AiStatusText = "Starting Ollama...";
            var (started, message) = await process.EnsureServerRunningAsync();
            if (!started)
            {
                AiStatusText = message;
                AiAvailable = false;
                return;
            }

            AiStatusText = "Looking for a vision model...";
            if (!await _aiService.CheckAvailabilityAsync())
            {
                AiAvailable = false;
                AiStatusText = await DescribeUnavailableReasonAsync();
                return;
            }

            AiStatusText = $"Loading {_aiService.Model}...";
            await process.PreloadModelAsync(_aiService.Model);

            AiAvailable = true;
            AiStatusText = _aiService.Model;
        }
        catch (Exception ex)
        {
            AiAvailable = false;
            AiStatusText = $"Failed to start: {ex.Message}";
        }
        finally
        {
            IsStartingAi = false;
        }
    }

    private bool CanStartAi() => !IsStartingAi;

    partial void OnSelectedItemChanged(LibraryThumbnailItem? value)
    {
        if (value == null)
        {
            DetailFileName = string.Empty;
            DetailDescription = string.Empty;
            DetailTags = string.Empty;
            DetailFaces = string.Empty;
            DetailDuplicateInfo = string.Empty;
            return;
        }

        DetailFileName = value.FileName;

        var info = value.ImageInfo;
        if (info != null)
        {
            DetailDescription = info.Description ?? "Not analyzed";
            DetailTags = info.Tags.Count > 0 ? string.Join(", ", info.Tags) : "None";
            DetailFaces = info.Faces.Count > 0
                ? string.Join("\n", info.Faces.Select(f => f.Description ?? "Unknown"))
                : "No faces detected";

            // Check duplicates
            if (_libraryData != null)
            {
                var dupGroup = _libraryData.DuplicateGroups
                    .FirstOrDefault(g => g.ImagePaths.Contains(info.RelativePath));
                if (dupGroup != null)
                {
                    DetailDuplicateInfo = $"{dupGroup.Type} — {dupGroup.ImagePaths.Count} copies";
                }
                else
                {
                    DetailDuplicateInfo = "No duplicates";
                }
            }
        }
        else
        {
            DetailDescription = "Not analyzed";
            DetailTags = "None";
            DetailFaces = "Not analyzed";
            DetailDuplicateInfo = "Not analyzed";
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Library Root Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            SetRootFolder(dialog.FolderName);
        }
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            return;

        IsScanning = true;
        ScanProgress = 0;
        ScanStatusText = "Starting scan...";

        _scanCts = new CancellationTokenSource();
        _scanService = new LibraryScanService(_aiService);

        var progressHandler = new Progress<LibraryScanProgress>(p =>
        {
            ScanProgress = p.Percentage;
            ScanStatusText = $"{p.Phase}: {p.CurrentFile} ({p.ProcessedCount}/{p.TotalCount})";
        });

        try
        {
            // Fast pass only: hashes and duplicates. AI classification runs afterwards in
            // the background so duplicate results appear without waiting on the GPU.
            _libraryData = await Task.Run(
                () => _scanService.ScanAsync(RootFolder, progressHandler, _scanCts.Token));
            BuildGroups();

            AiAvailable = _aiService.IsAvailable;
            var pendingAi = _libraryData.Images.Count(i => i.AiAnalyzedUtc is null);

            ScanStatusText = AiAvailable
                ? $"Scan complete — {_libraryData.Images.Count} images"
                : $"Scan complete — {_libraryData.Images.Count} images (AI unavailable, no tags or faces)";

            IsScanning = false;

            if (AiAvailable && pendingAi > 0)
                StartBackgroundAnalysis(_libraryData);
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            AiStatusText = AiAvailable ? _aiService.Model : await DescribeUnavailableReasonAsync();
        }
    }

    /// <summary>
    /// Runs AI classification over the scanned library without blocking the caller. The
    /// pass saves as it goes, so cancelling it or closing mid-way keeps completed work.
    /// </summary>
    private void StartBackgroundAnalysis(LibraryData data)
    {
        _analyzeCts?.Cancel();
        _analyzeCts = new CancellationTokenSource();
        var token = _analyzeCts.Token;
        var service = _scanService!;

        IsAnalyzing = true;
        AnalyzeProgress = 0;
        AnalyzeStatusText = "Starting AI analysis...";

        var analyzeProgress = new Progress<LibraryScanProgress>(p =>
        {
            AnalyzeProgress = p.Percentage;
            AnalyzeStatusText = $"Analyzing: {p.CurrentFile} ({p.ProcessedCount}/{p.TotalCount})";
        });

        _analyzeTask = Task.Run(async () =>
        {
            try
            {
                await service.AnalyzeLibraryAsync(data, analyzeProgress, token);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel; partial results are already persisted.
            }
            catch (Exception ex)
            {
                // Marshal back to the UI thread before touching bound state.
                App.Current?.Dispatcher.Invoke(() =>
                    AnalyzeStatusText = $"AI analysis failed: {ex.Message}");
                return;
            }
            finally
            {
                App.Current?.Dispatcher.Invoke(() => IsAnalyzing = false);
            }

            App.Current?.Dispatcher.Invoke(() =>
            {
                AnalyzeStatusText = token.IsCancellationRequested
                    ? "AI analysis cancelled"
                    : "AI analysis complete";

                // Tags, scenes and face clusters changed, so rebuild any view that uses them.
                BuildGroups();
            });
        }, token);
    }

    /// <summary>
    /// Cancels whichever pass is running. The AI pass outlives the scan, so cancelling
    /// must reach it too or the button would appear to do nothing once scanning ends.
    /// </summary>
    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
        _analyzeCts?.Cancel();
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedItem != null)
        {
            OpenInEditorRequested?.Invoke(SelectedItem.FullPath);
        }
    }

    /// <summary>
    /// Applies a click to the selection. Ctrl toggles, Shift extends from the anchor,
    /// and a plain click replaces the selection.
    /// </summary>
    public void SelectItem(LibraryThumbnailItem item, bool isCtrlDown, bool isShiftDown)
    {
        // Fall back to the current item so a shift-click works even when no anchor has
        // been established yet (for example the first click after a rescan).
        var anchor = _selectionAnchor ?? SelectedItem;

        if (isShiftDown && anchor != null)
        {
            var flat = FlattenedItems();
            var start = flat.IndexOf(anchor);
            var end = flat.IndexOf(item);

            if (start >= 0 && end >= 0)
            {
                _selectionAnchor = anchor;

                if (start > end)
                    (start, end) = (end, start);

                if (!isCtrlDown)
                    ClearSelection();

                for (var i = start; i <= end; i++)
                    Select(flat[i]);

                SelectedItem = item;
                UpdateSelectionState();
                return;
            }
        }

        if (isCtrlDown)
        {
            if (item.IsSelected)
            {
                item.IsSelected = false;
                _selectedItems.Remove(item);
                SelectedItem = _selectedItems.LastOrDefault();
            }
            else
            {
                Select(item);
                SelectedItem = item;
            }

            _selectionAnchor = item;
            UpdateSelectionState();
            return;
        }

        ClearSelection();
        Select(item);
        SelectedItem = item;
        _selectionAnchor = item;
        UpdateSelectionState();
    }

    private void Select(LibraryThumbnailItem item)
    {
        if (item.IsSelected)
            return;

        item.IsSelected = true;
        _selectedItems.Add(item);
    }

    private void ClearSelection()
    {
        foreach (var selected in _selectedItems)
            selected.IsSelected = false;

        _selectedItems.Clear();
    }

    /// <summary>Selects every item currently shown, respecting the active filter and grouping.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        ClearSelection();

        foreach (var item in FlattenedItems())
            Select(item);

        SelectedItem = _selectedItems.LastOrDefault();
        UpdateSelectionState();
    }

    /// <summary>The visible items in display order, used for shift-range selection.</summary>
    private List<LibraryThumbnailItem> FlattenedItems() =>
        Groups.SelectMany(g => g.Items).ToList();

    /// <summary>Direction for keyboard-driven selection movement.</summary>
    public enum SelectionMove
    {
        Previous,
        Next,
        First,
        Last
    }

    /// <summary>
    /// Moves the caret item with the keyboard. When <paramref name="extend"/> is true the
    /// selection grows as a contiguous range from the anchor, matching Explorer behaviour.
    /// </summary>
    public void MoveSelection(SelectionMove move, bool extend)
    {
        var flat = FlattenedItems();
        if (flat.Count == 0)
            return;

        // Without a current item, start from whichever end the user is heading towards.
        var current = SelectedItem != null ? flat.IndexOf(SelectedItem) : -1;

        var target = move switch
        {
            SelectionMove.Previous => current < 0 ? flat.Count - 1 : current - 1,
            SelectionMove.Next => current < 0 ? 0 : current + 1,
            SelectionMove.First => 0,
            SelectionMove.Last => flat.Count - 1,
            _ => current
        };

        target = Math.Clamp(target, 0, flat.Count - 1);
        var item = flat[target];

        if (extend)
        {
            // Anchor the range on first extend so the fixed end stays put while the
            // caret moves, allowing the range to shrink as well as grow.
            _selectionAnchor ??= SelectedItem ?? item;

            var start = flat.IndexOf(_selectionAnchor);
            if (start < 0)
            {
                _selectionAnchor = item;
                start = target;
            }

            var end = target;
            if (start > end)
                (start, end) = (end, start);

            ClearSelection();

            for (var i = start; i <= end; i++)
                Select(flat[i]);

            SelectedItem = item;
            UpdateSelectionState();
            return;
        }

        ClearSelection();
        Select(item);
        SelectedItem = item;
        _selectionAnchor = item;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = _selectedItems.Count;
        SelectionSummary = SelectedCount switch
        {
            0 => string.Empty,
            1 => "1 selected",
            _ => $"{SelectedCount} selected"
        };

        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteSelected() => _selectedItems.Count > 0;

    /// <summary>
    /// Moves every selected photo to the Windows Recycle Bin after confirmation, then
    /// removes the entries from the library index.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (_selectedItems.Count == 0 || _libraryData == null)
            return;

        var targets = _selectedItems.ToList();

        var message = targets.Count == 1
            ? $"Move \"{targets[0].FileName}\" to the Recycle Bin?"
            : $"Move {targets.Count} photos to the Recycle Bin?";

        var confirm = MessageBox.Show(
            message,
            "Delete photos",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
            return;

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var item in targets)
        {
            try
            {
                if (File.Exists(item.FullPath))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        item.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }

                deleted.Add(item.RelativePath);
            }
            catch (Exception ex)
            {
                failures.Add($"{item.FileName}: {ex.Message}");
            }
        }

        if (deleted.Count > 0)
        {
            _libraryData.Images.RemoveAll(i => deleted.Contains(i.RelativePath));

            foreach (var group in _libraryData.DuplicateGroups)
                group.ImagePaths.RemoveAll(deleted.Contains);

            _libraryData.DuplicateGroups.RemoveAll(g => g.ImagePaths.Count < 2);

            LibraryDataStore.Save(_libraryData);
        }

        ClearSelection();
        SelectedItem = null;
        _selectionAnchor = null;
        UpdateSelectionState();

        BuildGroups();

        if (failures.Count > 0)
        {
            MessageBox.Show(
                $"Could not delete {failures.Count} file(s):\n\n{string.Join("\n", failures.Take(10))}",
                "Delete photos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void DeleteDuplicates()
    {
        if (SelectedItem?.ImageInfo == null || _libraryData == null)
            return;

        var dupGroup = _libraryData.DuplicateGroups
            .FirstOrDefault(g => g.ImagePaths.Contains(SelectedItem.ImageInfo.RelativePath));

        if (dupGroup == null || dupGroup.ImagePaths.Count < 2)
            return;

        // Keep the first (prefer raw), delete the rest
        var toKeep = dupGroup.ImagePaths
            .OrderByDescending(p => IsRawExtension(Path.GetExtension(p)))
            .ThenBy(p => p)
            .First();

        foreach (var path in dupGroup.ImagePaths.Where(p => p != toKeep))
        {
            var fullPath = Path.Combine(RootFolder, path);
            try
            {
                if (File.Exists(fullPath))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        fullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch
            {
                // Ignore delete failures
            }
        }

        // Remove deleted entries from data
        var deletedPaths = dupGroup.ImagePaths.Where(p => p != toKeep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _libraryData.Images.RemoveAll(i => deletedPaths.Contains(i.RelativePath));
        _libraryData.DuplicateGroups.Remove(dupGroup);
        LibraryDataStore.Save(_libraryData);

        BuildGroups();
    }

    partial void OnGroupModeChanged(LibraryGroupMode value)
    {
        BuildGroups();
    }

    partial void OnSortModeChanged(LibrarySortMode value)
    {
        BuildGroups();
    }

    partial void OnFilterTextChanged(string value)
    {
        BuildGroups();
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".tif", ".tiff",
        ".png"
    };

    private void LoadLibrary()
    {
        if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            return;

        // A background AI pass still running against the previous folder would keep
        // saving into that library and race this load, so stop it first.
        _analyzeCts?.Cancel();
        IsAnalyzing = false;
        AnalyzeStatusText = string.Empty;

        _libraryData = LibraryDataStore.Load(RootFolder);

        _thumbnailCache?.Dispose();
        _thumbnailCache = new ThumbnailCacheService(RootFolder);

        // Build a lookup of images already known from a prior scan
        var known = _libraryData.Images
            .ToDictionary(i => i.RelativePath, i => i, StringComparer.OrdinalIgnoreCase);

        // Discover all image files on disk and add stubs for any that aren't tracked yet
        var allFiles = Directory.EnumerateFiles(RootFolder, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(RootFolder, filePath);
            if (!known.ContainsKey(relativePath))
            {
                var fi = new FileInfo(filePath);
                _libraryData.Images.Add(new LibraryImageInfo
                {
                    RelativePath = relativePath,
                    FileSize = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                });
            }
        }

        BuildGroups();

        var analyzedCount = _libraryData.Images.Count(i => i.AnalyzedUtc.HasValue);
        if (analyzedCount > 0)
        {
            ScanStatusText = $"{_libraryData.Images.Count} images ({analyzedCount} analyzed)";
        }
        else
        {
            ScanStatusText = $"{_libraryData.Images.Count} images found — click Scan to analyze";
        }
    }

    /// <summary>
    /// Opens a library folder and remembers it so the next run reopens the same library.
    /// </summary>
    public void SetRootFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        RootFolder = folder;
        LoadLibrary();

        // Persist immediately rather than on exit, so the choice survives a crash or a
        // kill from the debugger.
        _settingsService.Settings.LibraryRootFolder = folder;
        _settingsService.Save();
    }

    private void BuildGroups()
    {
        // Item instances are recreated below, so any held selection would become stale.
        _selectedItems.Clear();
        _selectionAnchor = null;
        UpdateSelectionState();

        Groups.Clear();

        if (_libraryData == null || _libraryData.Images.Count == 0)
            return;

        switch (GroupMode)
        {
            case LibraryGroupMode.Folder:
                BuildFolderGroups();
                break;
            case LibraryGroupMode.Faces:
                BuildFaceGroups();
                break;
            case LibraryGroupMode.Duplicates:
                BuildDuplicateGroups();
                break;
            case LibraryGroupMode.Tags:
                BuildTagGroups();
                break;
        }

        // Cancel any previous thumbnail loading and start fresh
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        _ = LoadGroupThumbnailsAsync(_thumbnailCts.Token);
    }

    private void BuildFolderGroups()
    {
        var byFolder = FilterAndSort(_libraryData!.Images)
            .GroupBy(i => Path.GetDirectoryName(i.RelativePath) ?? string.Empty)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in byFolder)
        {
            var group = new LibraryGroup
            {
                Name = string.IsNullOrEmpty(folder.Key) ? "(root)" : folder.Key
            };

            foreach (var image in folder)
            {
                group.Items.Add(CreateThumbnailItem(image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }
    }

    /// <summary>
    /// Applies the file-name substring filter and the active sort order.
    /// </summary>
    private IEnumerable<LibraryImageInfo> FilterAndSort(IEnumerable<LibraryImageInfo> images)
    {
        var filter = FilterText?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            images = images.Where(i => Path.GetFileName(i.RelativePath)
                .Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return SortMode switch
        {
            LibrarySortMode.NameDescending =>
                images.OrderByDescending(i => i.RelativePath, StringComparer.OrdinalIgnoreCase),
            LibrarySortMode.DateNewest =>
                images.OrderByDescending(i => i.LastModifiedUtc)
                      .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase),
            LibrarySortMode.DateOldest =>
                images.OrderBy(i => i.LastModifiedUtc)
                      .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => images.OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void BuildFaceGroups()
    {
        if (_libraryData!.FaceClusters.Count == 0)
        {
            // No clusters — show all images with faces vs without
            var withFaces = FilterAndSort(_libraryData.Images.Where(i => i.Faces.Count > 0)).ToList();
            var withoutFaces = FilterAndSort(_libraryData.Images.Where(i => i.Faces.Count == 0)).ToList();

            if (withFaces.Count > 0)
            {
                var group = new LibraryGroup { Name = "With Faces" };
                foreach (var img in withFaces)
                    group.Items.Add(CreateThumbnailItem(img));
                Groups.Add(group);
            }

            if (withoutFaces.Count > 0)
            {
                var group = new LibraryGroup { Name = "No Faces" };
                foreach (var img in withoutFaces)
                    group.Items.Add(CreateThumbnailItem(img));
                Groups.Add(group);
            }

            return;
        }

        foreach (var cluster in _libraryData.FaceClusters)
        {
            var group = new LibraryGroup
            {
                Name = cluster.Name ?? $"Person {cluster.Id}"
            };

            var imageSet = new HashSet<string>(cluster.ImagePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var image in FilterAndSort(_libraryData.Images.Where(i => imageSet.Contains(i.RelativePath))))
            {
                group.Items.Add(CreateThumbnailItem(image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }

        // Unassigned images
        var allClustered = _libraryData.FaceClusters
            .SelectMany(c => c.ImagePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unassigned = FilterAndSort(_libraryData.Images
                .Where(i => i.Faces.Count > 0 && !allClustered.Contains(i.RelativePath)))
            .ToList();

        if (unassigned.Count > 0)
        {
            var group = new LibraryGroup { Name = "Unknown Faces" };
            foreach (var img in unassigned)
                group.Items.Add(CreateThumbnailItem(img));
            Groups.Add(group);
        }
    }

    private void BuildDuplicateGroups()
    {
        foreach (var dupGroup in _libraryData!.DuplicateGroups)
        {
            var label = dupGroup.Type == DuplicateType.Exact
                ? "Exact Duplicates"
                : $"Near Duplicates (distance: {dupGroup.HammingDistance})";

            var group = new LibraryGroup { Name = label };

            var paths = new HashSet<string>(dupGroup.ImagePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var image in FilterAndSort(_libraryData.Images.Where(i => paths.Contains(i.RelativePath))))
            {
                group.Items.Add(CreateThumbnailItem(image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }
    }

    private void BuildTagGroups()
    {
        var byTag = FilterAndSort(_libraryData!.Images)
            .SelectMany(i => i.Tags.Select(t => (Tag: t.ToLowerInvariant(), Image: i)))
            .GroupBy(x => x.Tag)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key);

        foreach (var tagGroup in byTag)
        {
            var group = new LibraryGroup { Name = tagGroup.Key };
            foreach (var entry in tagGroup)
            {
                group.Items.Add(CreateThumbnailItem(entry.Image));
            }

            if (group.Items.Count > 0)
                Groups.Add(group);
        }
    }

    private LibraryThumbnailItem CreateThumbnailItem(LibraryImageInfo imageInfo)
    {
        var fullPath = Path.Combine(RootFolder, imageInfo.RelativePath);
        return new LibraryThumbnailItem
        {
            RelativePath = imageInfo.RelativePath,
            FullPath = fullPath,
            FileName = Path.GetFileName(imageInfo.RelativePath),
            ImageInfo = imageInfo
        };
    }

    private async Task LoadGroupThumbnailsAsync(CancellationToken ct)
    {
        // Collect all items that need thumbnails
        var pending = Groups
            .SelectMany(g => g.Items)
            .Where(item => item.Thumbnail == null && File.Exists(item.FullPath))
            .ToList();

        if (pending.Count == 0)
            return;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var maxParallelism = Environment.ProcessorCount;
        var cache = _thumbnailCache;

        await Task.Run(async () =>
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
                async (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var thumb = LoadThumbnail(item.FullPath, item.ImageInfo, cache);
                    if (thumb != null)
                    {
                        // Marshal back to UI thread to set the property
                        await dispatcher.InvokeAsync(() => item.Thumbnail = thumb,
                            DispatcherPriority.Background, token);
                    }
                });
        }, ct);
    }

    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    private static BitmapSource? LoadThumbnail(string filePath, LibraryImageInfo? imageInfo, ThumbnailCacheService? cache)
    {
        try
        {
            // Try loading from the SQLite thumbnail cache first
            if (cache != null && imageInfo != null)
            {
                var cached = cache.Get(imageInfo.RelativePath, imageInfo.FileSize, imageInfo.LastModifiedUtc);
                if (cached != null)
                    return DecodeBitmapFromBytes(cached);
            }

            byte[]? jpegBytes = null;

            // For JPEG files, use WPF's built-in decoder — no ImageMagick needed
            if (JpegExtensions.Contains(Path.GetExtension(filePath)))
            {
                var bitmap = new BitmapImage();
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelHeight = 120;
                    bitmap.StreamSource = fs;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                jpegBytes = EncodeBitmapToJpeg(bitmap);
                StoreThumbnail(cache, imageInfo, jpegBytes);
                return bitmap;
            }

            // For RAW/TIFF/PNG: try EXIF thumbnail first (metadata-only read)
            using var probe = new MagickImage();
            probe.Ping(filePath);
            var exifProfile = probe.GetExifProfile();
            var thumbnailBytes = exifProfile?.CreateThumbnail()?.ToByteArray();

            if (thumbnailBytes is { Length: > 0 })
            {
                // EXIF thumbnails are already JPEG — let WPF decode them natively
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(thumbnailBytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelHeight = 120;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();

                jpegBytes = EncodeBitmapToJpeg(bmp);
                StoreThumbnail(cache, imageInfo, jpegBytes);
                return bmp;
            }

            // Fallback: decode with size hint to reduce memory usage
            var settings = new MagickReadSettings { Width = 240, Height = 240 };
            using var image = new MagickImage(filePath, settings);
            image.AutoOrient();
            image.Resize(new MagickGeometry(120, 120) { Greater = true });
            image.Format = MagickFormat.Jpeg;
            image.Quality = 80;
            jpegBytes = image.ToByteArray();

            StoreThumbnail(cache, imageInfo, jpegBytes);
            return DecodeBitmapFromBytes(jpegBytes);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource DecodeBitmapFromBytes(byte[] data)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(data))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    private static byte[] EncodeBitmapToJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void StoreThumbnail(ThumbnailCacheService? cache, LibraryImageInfo? imageInfo, byte[]? jpegBytes)
    {
        if (cache != null && imageInfo != null && jpegBytes is { Length: > 0 })
            cache.Put(imageInfo.RelativePath, imageInfo.FileSize, imageInfo.LastModifiedUtc, jpegBytes);
    }

    private static bool IsRawExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".arw" or ".cr2" or ".cr3" or ".nef" or ".dng" or ".raf" or ".orf" or ".rw2" or ".pef" or ".srw" => true,
        _ => false
    };
}
