using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoGreen.Services;

namespace PhotoGreen.Views;

public class HistogramControl : Control
{
    public static readonly DependencyProperty HistogramProperty =
        DependencyProperty.Register(nameof(Histogram), typeof(HistogramData), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public HistogramData? Histogram
    {
        get => (HistogramData?)GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    static HistogramControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HistogramControl),
            new FrameworkPropertyMetadata(typeof(HistogramControl)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
            return;

        // Background
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)), null,
            new Rect(0, 0, width, height));

        var data = Histogram;
        if (data == null)
            return;

        var binWidth = width / HistogramData.BinCount;
        var maxVal = (double)data.MaxCount;

        // Draw each channel with transparency so overlaps blend
        DrawChannel(dc, data.Blue, maxVal, width, height, binWidth,
            Color.FromArgb(120, 80, 140, 255));
        DrawChannel(dc, data.Green, maxVal, width, height, binWidth,
            Color.FromArgb(120, 80, 255, 80));
        DrawChannel(dc, data.Red, maxVal, width, height, binWidth,
            Color.FromArgb(120, 255, 80, 80));

        // Luminance outline
        DrawChannelOutline(dc, data.Luminance, maxVal, width, height, binWidth,
            Color.FromArgb(180, 220, 220, 220));
    }

    private static void DrawChannel(DrawingContext dc, int[] bins, double maxVal,
        double width, double height, double binWidth, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, height), true, true);

            for (int i = 0; i < HistogramData.BinCount; i++)
            {
                double x = i * binWidth;
                double barHeight = (bins[i] / maxVal) * height;
                barHeight = Math.Min(barHeight, height);
                ctx.LineTo(new Point(x, height - barHeight), true, false);
            }

            ctx.LineTo(new Point(width, height), true, false);
        }

        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawChannelOutline(DrawingContext dc, int[] bins, double maxVal,
        double width, double height, double binWidth, Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double y0 = height - Math.Min((bins[0] / maxVal) * height, height);
            ctx.BeginFigure(new Point(0, y0), false, false);

            for (int i = 1; i < HistogramData.BinCount; i++)
            {
                double x = i * binWidth;
                double barHeight = (bins[i] / maxVal) * height;
                barHeight = Math.Min(barHeight, height);
                ctx.LineTo(new Point(x, height - barHeight), true, false);
            }
        }

        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
