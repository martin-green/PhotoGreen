using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public FileBrowserViewModel FileBrowser { get; } = new();
    public ImageViewerViewModel ImageViewer { get; } = new();
    public EditingViewModel Editing { get; } = new();
    public LibraryViewModel Library { get; } = new();

    /// <summary>
    /// Owned by the shell rather than by the import window so that camera connections
    /// are detected for the whole application lifetime, not only while the dialog is open.
    /// </summary>
    public CameraImportViewModel CameraImport { get; } = new();

    private Views.CameraImportWindow? _importWindow;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ExifInfo _exifInfo = ExifInfo.Empty;

    public MainViewModel()
    {
        FileBrowser.ImageSelected += OnImageSelected;
        Editing.SettingsChanged += OnSettingsChanged;
        Editing.ExportRequested += OnExportRequested;

        // Bring the import UI up so the user can watch progress when a connected
        // camera kicks off an automatic import.
        CameraImport.AutoImportStarted += () =>
            System.Windows.Application.Current.Dispatcher.Invoke(ShowImportWindow);

        CameraImport.ImportCompleted += folder =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                FileBrowser.NavigateToDirectoryCommand.Execute(folder);
                if (FileBrowser.ImageGroups.Count > 0)
                {
                    FileBrowser.SelectedGroup = FileBrowser.ImageGroups[0];
                }
            });
        };

        Editing.PropertyChanged += OnEditingPropertyChanged;

        Editing.LinearPixelProvider = () =>
        {
            var pixels = ImageViewer.LinearPixels;
            if (pixels == null || pixels.Length == 0)
                return null;
            return (pixels, (int)ImageViewer.PixelWidth, (int)ImageViewer.PixelHeight);
        };

        Editing.CurrentFilePathProvider = () =>
            ImageViewer.CurrentGroup?.PrimaryFile.FullPath;

        Editing.SelectionProvider = () =>
        {
            if (!ImageViewer.HasSelection)
                return null;
            var r = ImageViewer.SelectionRect;
            return (r.X, r.Y, r.Width, r.Height);
        };

        Editing.ThumbnailBytesProvider = () =>
        {
            var group = ImageViewer.CurrentGroup;
            if (group == null) return null;
            try
            {
                using var image = new ImageMagick.MagickImage(group.PrimaryFile.FullPath);
                image.AutoOrient();
                image.Resize(new ImageMagick.MagickGeometry(512, 512) { Greater = true });
                image.Format = ImageMagick.MagickFormat.Jpeg;
                image.Quality = 80;
                return image.ToByteArray();
            }
            catch { return null; }
        };

        // Library → Edit integration
        Library.OpenInEditorRequested += OnOpenInEditorRequested;
    }

    private void OnEditingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditingViewModel.IsCropMode))
        {
            ImageViewer.IsCropMode = Editing.IsCropMode;
        }
        else if (e.PropertyName is nameof(EditingViewModel.CropLeft) or
                 nameof(EditingViewModel.CropTop) or
                 nameof(EditingViewModel.CropRight) or
                 nameof(EditingViewModel.CropBottom))
        {
            ImageViewer.CropLeft = Editing.CropLeft;
            ImageViewer.CropTop = Editing.CropTop;
            ImageViewer.CropRight = Editing.CropRight;
            ImageViewer.CropBottom = Editing.CropBottom;
        }
    }

    private async void OnImageSelected(ImageFileGroup group)
    {
        ImageViewer.LoadGroup(group);

        Editing.ApplySettings(DevelopSettings.Default);
        Editing.Activate();

        ExifInfo = ExifInfo.Empty;
        ExifInfo = await ExifService.ExtractAsync(group.PrimaryFile.FullPath);
    }

    private void OnSettingsChanged(DevelopSettings settings)
    {
        ImageViewer.ApplyDevelopSettings(settings);
    }

    private async void OnExportRequested()
    {
        var pixels = ImageViewer.LinearPixels;
        if (pixels == null || pixels.Length == 0)
            return;

        var currentGroup = ImageViewer.CurrentGroup;
        if (currentGroup == null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Image",
            Filter = ExportService.GetFileFilter(),
            FileName = Path.GetFileNameWithoutExtension(currentGroup.PrimaryFile.FullPath),
            DefaultExt = ".jpg"
        };

        if (dialog.ShowDialog() != true)
            return;

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var format = ext switch
        {
            ".png" => ExportFormat.Png,
            ".tif" or ".tiff" => ExportFormat.Tiff,
            _ => ExportFormat.Jpeg
        };

        var exportSettings = new ExportSettings
        {
            Format = format,
            JpegQuality = 95,
            OutputPath = dialog.FileName
        };

        ImageViewer.StatusText = "Exporting...";

        try
        {
            await ExportService.ExportAsync(
                pixels, (int)ImageViewer.PixelWidth, (int)ImageViewer.PixelHeight,
                Editing.CurrentSettings, exportSettings);

            ImageViewer.StatusText = $"Exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ImageViewer.StatusText = $"Export failed: {ex.Message}";
        }
    }

    // Keyboard shortcut relay commands
    [RelayCommand]
    private void KeyUndo() => Editing.UndoCommand.Execute(null);

    [RelayCommand]
    private void KeyRedo() => Editing.RedoCommand.Execute(null);

    [RelayCommand]
    private void KeyReset() => Editing.ResetAllCommand.Execute(null);

    [RelayCommand]
    private void KeyAuto() => Editing.AutoAdjustCommand.Execute(null);

    [RelayCommand]
    private void KeyExport() => Editing.ExportCommand.Execute(null);

    [RelayCommand]
    private void KeySaveSidecar() => Editing.SaveSidecarCommand.Execute(null);

    [RelayCommand]
    private void OpenImportDialog() => ShowImportWindow();

    private void ShowImportWindow()
    {
        if (_importWindow is { IsLoaded: true })
        {
            if (_importWindow.WindowState == System.Windows.WindowState.Minimized)
                _importWindow.WindowState = System.Windows.WindowState.Normal;

            _importWindow.Activate();
            return;
        }

        _importWindow = new Views.CameraImportWindow(CameraImport)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        _importWindow.Closed += (_, _) => _importWindow = null;
        _importWindow.Show();
    }

    private void OnOpenInEditorRequested(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder))
            return;

        // Navigate the file browser to the image's folder
        FileBrowser.NavigateToDirectoryCommand.Execute(folder);

        // Find and select the matching image group
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var group = FileBrowser.ImageGroups
            .FirstOrDefault(g => string.Equals(g.Stem, fileName, StringComparison.OrdinalIgnoreCase));
        if (group != null)
        {
            FileBrowser.SelectedGroup = group;
        }

        // Switch to Edit tab
        SelectedTabIndex = 0;
    }
}
