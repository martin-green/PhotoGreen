using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGreen.Services;
using Windows.Media.Import;

namespace PhotoGreen.ViewModels;

public partial class CameraImportViewModel : ObservableObject
{
    private readonly CameraImportService _importService = new();
    private readonly AppSettingsService _settingsService = new();
    private CancellationTokenSource? _importCts;

    public ObservableCollection<PhotoImportSourceItem> Sources { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    [ObservableProperty]
    private PhotoImportSourceItem? _selectedSource;

    [ObservableProperty]
    private string _destinationFolder = string.Empty;

    [ObservableProperty]
    private string _folderPattern = "{yyyy}/{yyyy}-{MM}-{dd}";

    [ObservableProperty]
    private string _patternPreview = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _importProgress;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private bool _autoImportOnConnect;

    [ObservableProperty]
    private string _manualSourcePath = string.Empty;

    public event Action<string>? ImportCompleted;

    public CameraImportViewModel()
    {
        DestinationFolder = _settingsService.Settings.ImportDestinationFolder;
        FolderPattern = string.IsNullOrEmpty(_settingsService.Settings.ImportFolderPattern)
            ? "{yyyy}/{yyyy}-{MM}-{dd}"
            : _settingsService.Settings.ImportFolderPattern;
        AutoImportOnConnect = _settingsService.Settings.AutoImportOnConnect;

        UpdatePatternPreview();

        _importService.StatusChanged += status =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => StatusText = status);
        _importService.ProgressChanged += p =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => ImportProgress = p);
        _importService.LogMessage += message =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => LogEntries.Add(message));

        _importService.SourceConnected += source =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(async () =>
            {
                Sources.Clear();
                Sources.Add(new PhotoImportSourceItem(source));
                SelectedSource = Sources[0];
                StatusText = $"Camera connected: {source.DisplayName}";

                if (AutoImportOnConnect && !string.IsNullOrEmpty(DestinationFolder))
                {
                    await ImportFromCameraAsync();
                }
            });
        };

        _importService.SourceDisconnected += () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Sources.Clear();
                SelectedSource = null;
                StatusText = "Camera disconnected.";
            });
        };

        _importService.StartWatching();
    }

    partial void OnFolderPatternChanged(string value)
    {
        UpdatePatternPreview();
        SaveSettings();
    }

    partial void OnDestinationFolderChanged(string value) => SaveSettings();
    partial void OnAutoImportOnConnectChanged(bool value) => SaveSettings();

    private void UpdatePatternPreview()
    {
        PatternPreview = CameraImportService.PreviewFolderPattern(FolderPattern);
    }

    private void SaveSettings()
    {
        _settingsService.Settings.ImportDestinationFolder = DestinationFolder;
        _settingsService.Settings.ImportFolderPattern = FolderPattern;
        _settingsService.Settings.AutoImportOnConnect = AutoImportOnConnect;
        _settingsService.Save();
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Import Destination Folder"
        };

        if (!string.IsNullOrEmpty(DestinationFolder) && Directory.Exists(DestinationFolder))
            dialog.InitialDirectory = DestinationFolder;

        if (dialog.ShowDialog() == true)
        {
            DestinationFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        Sources.Clear();
        StatusText = "Scanning for cameras and media devices...";

        try
        {
            var sources = await _importService.FindSourcesAsync();

            foreach (var source in sources)
            {
                Sources.Add(new PhotoImportSourceItem(source));
            }

            StatusText = sources.Count > 0
                ? $"Found {sources.Count} source(s)."
                : "No cameras or media devices found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error scanning for sources: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFromCameraAsync()
    {
        if (SelectedSource == null || string.IsNullOrEmpty(DestinationFolder))
            return;

        IsImporting = true;
        _importCts = new CancellationTokenSource();

        try
        {
            var hashes = _settingsService.Settings.ImportedFileHashes;

            await _importService.ImportFromSourceAsync(
                SelectedSource.Source,
                DestinationFolder,
                FolderPattern,
                hashes,
                _importCts.Token);

            _settingsService.Settings.ImportedFileHashes = hashes;
            _settingsService.Save();

            var folder = _importService.LastImportedFolder ?? DestinationFolder;
            ImportCompleted?.Invoke(folder);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import error: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    [RelayCommand]
    private void BrowseManualSource()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Source Folder (camera card, network path, etc.)"
        };

        if (dialog.ShowDialog() == true)
        {
            ManualSourcePath = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportManual))]
    private async Task ImportFromFolderAsync()
    {
        if (string.IsNullOrEmpty(ManualSourcePath) || string.IsNullOrEmpty(DestinationFolder))
            return;

        IsImporting = true;
        _importCts = new CancellationTokenSource();

        try
        {
            var hashes = _settingsService.Settings.ImportedFileHashes;

            var result = await _importService.ImportManualAsync(
                ManualSourcePath,
                DestinationFolder,
                FolderPattern,
                hashes,
                new Progress<(int processed, int total, string currentFile)>(p =>
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ImportProgress = (double)p.processed / p.total * 100;
                        StatusText = $"Importing {p.processed}/{p.total}: {p.currentFile}";
                    });
                }),
                _importCts.Token);

            _settingsService.Settings.ImportedFileHashes = hashes;
            _settingsService.Save();

            var folder = result.FirstImportedFolder ?? DestinationFolder;
            ImportCompleted?.Invoke(folder);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import error: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        _importCts?.Cancel();
    }

    private bool CanImport() => SelectedSource != null && !string.IsNullOrEmpty(DestinationFolder) && !IsImporting;
    private bool CanImportManual() => !string.IsNullOrEmpty(ManualSourcePath) && !string.IsNullOrEmpty(DestinationFolder) && !IsImporting;

    partial void OnSelectedSourceChanged(PhotoImportSourceItem? value)
    {
        ImportFromCameraCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsImportingChanged(bool value)
    {
        ImportFromCameraCommand.NotifyCanExecuteChanged();
        ImportFromFolderCommand.NotifyCanExecuteChanged();
    }
}

public class PhotoImportSourceItem(PhotoImportSource source)
{
    public PhotoImportSource Source { get; } = source;
    public string DisplayName => Source.DisplayName ?? "Unknown Device";
    public string Description => Source.Description ?? string.Empty;

    public override string ToString() => DisplayName;
}
