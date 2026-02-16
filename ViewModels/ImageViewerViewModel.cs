using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class ImageViewerViewModel : ObservableObject
{
    private readonly ImageLoaderService _imageLoaderService = new();
    private readonly DevelopmentPipelineService _pipeline = new();

    [ObservableProperty]
    private BitmapSource? _displayImage;

    [ObservableProperty]
    private ImageFileGroup? _currentGroup;

    [ObservableProperty]
    private string _statusText = "No image loaded";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private HistogramData? _histogram;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private bool _showHistogram = true;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isSelecting;

    [ObservableProperty]
    private Int32Rect _selectionRect;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isCropMode;

    [ObservableProperty]
    private double _cropLeft;

    [ObservableProperty]
    private double _cropTop;

    [ObservableProperty]
    private double _cropRight = 1.0;

    [ObservableProperty]
    private double _cropBottom = 1.0;

    /// <summary>
    /// The raw 16-bit linear pixel data for the currently loaded image.
    /// Will be used by the editing pipeline in later phases.
    /// </summary>
    public ushort[]? LinearPixels { get; private set; }
    public uint PixelWidth { get; private set; }
    public uint PixelHeight { get; private set; }
    public bool IsCurrentRaw { get; private set; }

    public ImageViewerViewModel()
    {
        _pipeline.PipelineCompleted += OnPipelineCompleted;
        _pipeline.PipelineStarted += () => IsProcessing = true;
    }

    private void OnPipelineCompleted(DevelopResult result)
    {
        var bitmap = RawDevelopmentEngine.CreateBitmapFromResult(result);
        this.DisplayImage = bitmap;
        this.Histogram = result.Histogram;
        this.IsProcessing = false;
    }

    public async void LoadGroup(ImageFileGroup group)
    {
        this.CurrentGroup = group;
        this.IsLoading = true;
        this.ZoomLevel = 1.0;
        this.StatusText = $"Loading: {group.DisplayName}...";

        // Show thumbnail immediately while full image loads
        this.DisplayImage = group.Thumbnail;

        try
        {
            var result = await _imageLoaderService.LoadAsync(group);

            this.DisplayImage = result.DisplayBitmap;
            this.Histogram = result.Histogram;
            this.LinearPixels = result.LinearPixels;
            this.PixelWidth = result.PixelWidth;
            this.PixelHeight = result.PixelHeight;
            this.IsCurrentRaw = result.IsRaw;

            // Feed the pipeline so sliders can re-process immediately
            _pipeline.SetSourcePixels(result.LinearPixels, (int)result.PixelWidth, (int)result.PixelHeight);

            // Run the pipeline with default settings immediately so the initial display
            // matches what the engine produces — this ensures undo-to-initial is consistent.
            _pipeline.RequestProcess(DevelopSettings.Default);

            var rawIndicator = result.IsRaw ? " [RAW 16-bit]" : "";
            this.StatusText = $"{result.FileName}  —  {result.PixelWidth}×{result.PixelHeight}{rawIndicator}";
        }
        catch (Exception ex)
        {
            this.StatusText = $"Error loading {group.DisplayName}: {ex.Message}";
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    public void ApplyDevelopSettings(DevelopSettings settings)
    {
        if (LinearPixels == null || LinearPixels.Length == 0)
            return;

        _pipeline.RequestProcess(settings);
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomLevel = Math.Min(ZoomLevel * 1.25, 10.0);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.25, 0.1);
    }

    [RelayCommand]
    private void ZoomFit()
    {
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    private void ZoomActual()
    {
        // Set zoom so 1 image pixel = 1 screen pixel (handled in view via binding)
        ZoomLevel = -1.0; // Sentinel: view interprets this as "actual pixels"
    }

    [RelayCommand]
    private void ToggleHistogram()
    {
        ShowHistogram = !ShowHistogram;
    }

    [RelayCommand]
    private void ToggleSelect()
    {
        IsSelecting = !IsSelecting;
        if (!IsSelecting)
        {
            ClearSelection();
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectionRect = default;
        HasSelection = false;
    }

    public void SetSelection(int x, int y, int w, int h)
    {
        SelectionRect = new Int32Rect(x, y, w, h);
        HasSelection = w > 10 && h > 10;
    }

    public void Clear()
    {
        _pipeline.Cancel();
        this.DisplayImage = null;
        this.CurrentGroup = null;
        this.Histogram = null;
        this.LinearPixels = null;
        this.StatusText = "No image loaded";
        this.ZoomLevel = 1.0;
        ClearSelection();
        IsSelecting = false;
        IsCropMode = false;
        CropLeft = 0;
        CropTop = 0;
        CropRight = 1.0;
        CropBottom = 1.0;
    }
}
