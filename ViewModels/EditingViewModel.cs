using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGreen.Models;
using PhotoGreen.Services;

namespace PhotoGreen.ViewModels;

public partial class EditingViewModel : ObservableObject
{
    private readonly Stack<DevelopSettings> _undoStack = new();
    private readonly Stack<DevelopSettings> _redoStack = new();
    private readonly DispatcherTimer _undoDebounceTimer;
    private DevelopSettings? _preGestureSnapshot;
    private bool _suppressHistory;

    public EditingViewModel()
    {
        _undoDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _undoDebounceTimer.Tick += OnUndoDebounceElapsed;
    }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    /// <summary>
    /// Set by MainViewModel to provide the current linear pixel data for auto-adjust analysis.
    /// Returns (pixels, width, height) or null if no image is loaded.
    /// </summary>
    public Func<(ushort[] pixels, int width, int height)?> ? LinearPixelProvider { get; set; }

    /// <summary>
    /// Provides the current image file path for sidecar save/load.
    /// </summary>
    public Func<string?>? CurrentFilePathProvider { get; set; }

    /// <summary>
    /// Fired when the user requests an export.
    /// </summary>
    public event Action? ExportRequested;

    // --- Tone ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _exposure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _contrast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _highlights;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _shadows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _whites;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _blacks;

    // --- Color ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _temperature = 5500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _tint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _vibrance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _saturation;

    // --- Detail ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _sharpness;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private double _noiseReduction;

    // --- Crop ---

    [ObservableProperty]
    private bool _isCropMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    [NotifyPropertyChangedFor(nameof(HasCrop))]
    private double _cropLeft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    [NotifyPropertyChangedFor(nameof(HasCrop))]
    private double _cropTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    [NotifyPropertyChangedFor(nameof(HasCrop))]
    private double _cropRight = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    [NotifyPropertyChangedFor(nameof(HasCrop))]
    private double _cropBottom = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettings))]
    private CropAspectRatio _cropAspectRatio = CropAspectRatio.Free;

    public bool HasCrop => CropLeft > 0 || CropTop > 0 || CropRight < 1.0 || CropBottom < 1.0;

    public event Action<DevelopSettings>? SettingsChanged;

    public DevelopSettings CurrentSettings => new()
    {
        Exposure = Exposure,
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        Whites = Whites,
        Blacks = Blacks,
        Temperature = Temperature,
        Tint = Tint,
        Vibrance = Vibrance,
        Saturation = Saturation,
        Sharpness = Sharpness,
        NoiseReduction = NoiseReduction,
        Crop = new CropSettings
        {
            Left = CropLeft,
            Top = CropTop,
            Right = CropRight,
            Bottom = CropBottom,
            AspectRatio = CropAspectRatio
        }
    };

    partial void OnExposureChanged(double value) => NotifySettingsChanged();
    partial void OnContrastChanged(double value) => NotifySettingsChanged();
    partial void OnHighlightsChanged(double value) => NotifySettingsChanged();
    partial void OnShadowsChanged(double value) => NotifySettingsChanged();
    partial void OnWhitesChanged(double value) => NotifySettingsChanged();
    partial void OnBlacksChanged(double value) => NotifySettingsChanged();
    partial void OnTemperatureChanged(double value) => NotifySettingsChanged();
    partial void OnTintChanged(double value) => NotifySettingsChanged();
    partial void OnVibranceChanged(double value) => NotifySettingsChanged();
    partial void OnSaturationChanged(double value) => NotifySettingsChanged();
    partial void OnSharpnessChanged(double value) => NotifySettingsChanged();
    partial void OnNoiseReductionChanged(double value) => NotifySettingsChanged();
    partial void OnCropLeftChanged(double value) => NotifySettingsChanged();
    partial void OnCropTopChanged(double value) => NotifySettingsChanged();
    partial void OnCropRightChanged(double value) => NotifySettingsChanged();
    partial void OnCropBottomChanged(double value) => NotifySettingsChanged();
    partial void OnCropAspectRatioChanged(CropAspectRatio value) => NotifySettingsChanged();

    private void NotifySettingsChanged()
    {
        if (IsEnabled)
        {
            if (!_suppressHistory)
            {
                ScheduleUndoCommit();
            }
            SettingsChanged?.Invoke(CurrentSettings);
        }
    }

    /// <summary>
    /// Captures the state before a gesture begins (first change after idle),
    /// then debounces: only commits to the undo stack once changes settle for 400ms.
    /// This groups an entire slider drag into a single undo step.
    /// </summary>
    private void ScheduleUndoCommit()
    {
        if (_preGestureSnapshot == null)
        {
            // First change in a new gesture — snapshot the "before" state
            _preGestureSnapshot = _undoStack.Count > 0 ? _undoStack.Peek() : DevelopSettings.Default;
        }

        _undoDebounceTimer.Stop();
        _undoDebounceTimer.Start();
    }

    private void OnUndoDebounceElapsed(object? sender, EventArgs e)
    {
        _undoDebounceTimer.Stop();
        CommitUndo();
    }

    private void CommitUndo()
    {
        var current = CurrentSettings;

        // Only push if the state actually differs from the pre-gesture snapshot
        if (_preGestureSnapshot != null && _preGestureSnapshot != current)
        {
            // Ensure the pre-gesture state is on the stack as the base
            if (_undoStack.Count == 0 || _undoStack.Peek() != _preGestureSnapshot)
            {
                _undoStack.Push(_preGestureSnapshot);
            }

            _undoStack.Push(current);
            _redoStack.Clear();
            CanUndo = _undoStack.Count > 1;
            CanRedo = false;
        }

        _preGestureSnapshot = null;
    }

    [RelayCommand]
    private void ResetAll()
    {
        ApplySettings(DevelopSettings.Default);
    }

    [RelayCommand]
    private void ToggleCrop()
    {
        IsCropMode = !IsCropMode;
    }

    [RelayCommand]
    private void ResetCrop()
    {
        CropLeft = 0;
        CropTop = 0;
        CropRight = 1.0;
        CropBottom = 1.0;
    }

    public void SetCropRect(double left, double top, double right, double bottom)
    {
        _suppressHistory = true;
        CropLeft = Math.Clamp(left, 0, 1);
        CropTop = Math.Clamp(top, 0, 1);
        CropRight = Math.Clamp(right, 0, 1);
        CropBottom = Math.Clamp(bottom, 0, 1);
        _suppressHistory = false;
        ScheduleUndoCommit();
        SettingsChanged?.Invoke(CurrentSettings);
    }

    /// <summary>
    /// Provides the selection rectangle for region-based auto-adjust.
    /// Returns (x, y, w, h) or null if no selection.
    /// </summary>
    public Func<(int x, int y, int w, int h)?>? SelectionProvider { get; set; }

    [RelayCommand]
    private void AutoAdjust()
    {
        var data = LinearPixelProvider?.Invoke();
        if (data == null)
            return;

        var (pixels, width, height) = data.Value;

        // Use region if selected, otherwise center-weighted full image
        var sel = SelectionProvider?.Invoke();
        var settings = sel != null
            ? AutoAdjustEngine.AnalyzeRegion(pixels, width, height, sel.Value.x, sel.Value.y, sel.Value.w, sel.Value.h)
            : AutoAdjustEngine.Analyze(pixels, width, height);

        ApplySettings(settings);
    }

    [RelayCommand]
    private void AutoTone()
    {
        var data = LinearPixelProvider?.Invoke();
        if (data == null)
            return;

        var (pixels, width, height) = data.Value;

        var sel = SelectionProvider?.Invoke();
        var auto = sel != null
            ? AutoAdjustEngine.AnalyzeRegion(pixels, width, height, sel.Value.x, sel.Value.y, sel.Value.w, sel.Value.h)
            : AutoAdjustEngine.Analyze(pixels, width, height);

        // Only apply tone settings, keep current color/detail
        Exposure = auto.Exposure;
        Contrast = auto.Contrast;
        Highlights = auto.Highlights;
        Shadows = auto.Shadows;
        Whites = auto.Whites;
        Blacks = auto.Blacks;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count < 2)
            return;

        // Current state on top ? move to redo
        _redoStack.Push(_undoStack.Pop());
        var previous = _undoStack.Peek();

        _suppressHistory = true;
        ApplySettings(previous);
        _suppressHistory = false;

        CanUndo = _undoStack.Count > 1;
        CanRedo = _redoStack.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;

        var next = _redoStack.Pop();
        _undoStack.Push(next);

        _suppressHistory = true;
        ApplySettings(next);
        _suppressHistory = false;

        CanUndo = _undoStack.Count > 1;
        CanRedo = _redoStack.Count > 0;
    }

    [RelayCommand]
    private void Export()
    {
        ExportRequested?.Invoke();
    }

    [RelayCommand]
    private void SaveSidecar()
    {
        var filePath = CurrentFilePathProvider?.Invoke();
        if (string.IsNullOrEmpty(filePath))
            return;

        SidecarService.Save(filePath, CurrentSettings);
    }

    [RelayCommand]
    private void LoadSidecar()
    {
        var filePath = CurrentFilePathProvider?.Invoke();
        if (string.IsNullOrEmpty(filePath))
            return;

        var settings = SidecarService.Load(filePath);
        if (settings != null)
        {
            ApplySettings(settings);
        }
    }

    [RelayCommand]
    private void ResetTone()
    {
        Exposure = 0;
        Contrast = 0;
        Highlights = 0;
        Shadows = 0;
        Whites = 0;
        Blacks = 0;
    }

    [RelayCommand]
    private void ResetColor()
    {
        Temperature = 5500;
        Tint = 0;
        Vibrance = 0;
        Saturation = 0;
    }

    [RelayCommand]
    private void ResetDetail()
    {
        Sharpness = 0;
        NoiseReduction = 0;
    }

    public void ApplySettings(DevelopSettings settings)
    {
        Exposure = settings.Exposure;
        Contrast = settings.Contrast;
        Highlights = settings.Highlights;
        Shadows = settings.Shadows;
        Whites = settings.Whites;
        Blacks = settings.Blacks;
        Temperature = settings.Temperature;
        Tint = settings.Tint;
        Vibrance = settings.Vibrance;
        Saturation = settings.Saturation;
        Sharpness = settings.Sharpness;
        NoiseReduction = settings.NoiseReduction;
        CropLeft = settings.Crop.Left;
        CropTop = settings.Crop.Top;
        CropRight = settings.Crop.Right;
        CropBottom = settings.Crop.Bottom;
        CropAspectRatio = settings.Crop.AspectRatio;
    }

    public void Activate()
    {
        _undoDebounceTimer.Stop();
        _preGestureSnapshot = null;
        _undoStack.Clear();
        _redoStack.Clear();
        CanUndo = false;
        CanRedo = false;

        // Try to load sidecar settings for this file
        var filePath = CurrentFilePathProvider?.Invoke();
        if (!string.IsNullOrEmpty(filePath))
        {
            var sidecar = SidecarService.Load(filePath);
            if (sidecar != null)
            {
                _suppressHistory = true;
                ApplySettings(sidecar);
                _suppressHistory = false;
            }
        }

        IsEnabled = true;
        _undoStack.Push(CurrentSettings);
    }

    public void Deactivate()
    {
        _undoDebounceTimer.Stop();
        _preGestureSnapshot = null;
        IsEnabled = false;
        _undoStack.Clear();
        _redoStack.Clear();
        CanUndo = false;
        CanRedo = false;
        ApplySettings(DevelopSettings.Default);
    }
}
