using System.Windows.Threading;
using PhotoGreen.Models;

namespace PhotoGreen.Services;

/// <summary>
/// Debounces develop settings changes and runs the pixel pipeline on a background thread.
/// </summary>
public class DevelopmentPipelineService
{
    private readonly DispatcherTimer _debounceTimer;
    private DevelopSettings? _pendingSettings;
    private ushort[]? _linearPixels;
    private int _width;
    private int _height;
    private CancellationTokenSource? _cts;

    public event Action<DevelopResult>? PipelineCompleted;
    public event Action? PipelineStarted;

    public DevelopmentPipelineService()
    {
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _debounceTimer.Tick += OnDebounceElapsed;
    }

    public void SetSourcePixels(ushort[] linearPixels, int width, int height)
    {
        _linearPixels = linearPixels;
        _width = width;
        _height = height;
    }

    public void RequestProcess(DevelopSettings settings)
    {
        _pendingSettings = settings;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Cancel()
    {
        _debounceTimer.Stop();
        _cts?.Cancel();
        _pendingSettings = null;
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        var settings = _pendingSettings;
        var pixels = _linearPixels;

        if (settings == null || pixels == null || pixels.Length == 0)
            return;

        // Cancel any in-flight processing
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        PipelineStarted?.Invoke();

        int width = _width;
        int height = _height;

        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return RawDevelopmentEngine.Process(pixels, width, height, settings);
            }, cts.Token);

            if (!cts.Token.IsCancellationRequested)
            {
                PipelineCompleted?.Invoke(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when superseded by a newer request
        }
    }
}
