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

    [ObservableProperty]
    private ExifInfo _exifInfo = ExifInfo.Empty;

    public MainViewModel()
    {
        FileBrowser.ImageSelected += OnImageSelected;
        Editing.SettingsChanged += OnSettingsChanged;
        Editing.ExportRequested += OnExportRequested;

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
    private void OpenImportDialog()
    {
        var window = new Views.CameraImportWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (window.DataContext is CameraImportViewModel importVm)
        {
            importVm.ImportCompleted += folder =>
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
        }

        window.Show();
    }
}
