using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using PhotoGreen.ViewModels;

namespace PhotoGreen.Views;

public partial class ImageViewerView : UserControl
{
    private Point _panOrigin;
    private Point _panStart;
    private bool _isPanning;
    private bool _isDragSelecting;
    private Point _selectionStartImage;

    public ImageViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ImageScrollViewer.SizeChanged += (_, _) => ApplyFitIfNeeded();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ImageViewerViewModel oldVm)
            oldVm.PropertyChanged -= ViewModel_PropertyChanged;

        if (e.NewValue is ImageViewerViewModel newVm)
            newVm.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ImageViewerViewModel vm)
            return;

        if (e.PropertyName == nameof(ImageViewerViewModel.ZoomLevel))
        {
            ApplyZoom(vm.ZoomLevel);
        }
        else if (e.PropertyName == nameof(ImageViewerViewModel.DisplayImage))
        {
            // When a new image is loaded, re-apply fit
            ApplyFitIfNeeded();
        }
    }

    private void ApplyFitIfNeeded()
    {
        if (DataContext is ImageViewerViewModel vm && vm.ZoomLevel == 1.0)
        {
            ApplyZoom(1.0);
        }
    }

    private double CalculateFitScale()
    {
        if (DataContext is not ImageViewerViewModel vm || vm.DisplayImage == null)
            return 1.0;

        var viewWidth = ImageScrollViewer.ViewportWidth;
        var viewHeight = ImageScrollViewer.ViewportHeight;
        var imgWidth = vm.DisplayImage.PixelWidth;
        var imgHeight = vm.DisplayImage.PixelHeight;

        if (viewWidth <= 0 || viewHeight <= 0 || imgWidth <= 0 || imgHeight <= 0)
            return 1.0;

        var scaleX = viewWidth / imgWidth;
        var scaleY = viewHeight / imgHeight;
        return Math.Min(scaleX, scaleY);
    }

    private void ApplyZoom(double zoomLevel)
    {
        if (zoomLevel < 0)
        {
            // Sentinel for "actual pixels" — 1 image pixel = 1 screen pixel
            MainImage.Stretch = System.Windows.Media.Stretch.None;
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;
            return;
        }

        MainImage.Stretch = System.Windows.Media.Stretch.None;

        if (zoomLevel == 1.0)
        {
            // "Fit" mode — calculate scale to fit the image in the viewport
            var fitScale = CalculateFitScale();
            ImageScale.ScaleX = fitScale;
            ImageScale.ScaleY = fitScale;
        }
        else
        {
            ImageScale.ScaleX = zoomLevel;
            ImageScale.ScaleY = zoomLevel;
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;

            if (DataContext is ImageViewerViewModel vm)
            {
                // If currently in "fit" mode, switch to the actual fit scale as the base
                // so zooming in/out is relative to visible size
                if (vm.ZoomLevel == 1.0)
                {
                    var fitScale = CalculateFitScale();
                    vm.ZoomLevel = fitScale;
                }

                if (e.Delta > 0)
                    vm.ZoomInCommand.Execute(null);
                else
                    vm.ZoomOutCommand.Execute(null);
            }
        }
    }

    private void ImageScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ImageViewerViewModel vm && vm.IsSelecting && e.ClickCount == 1)
        {
            _isDragSelecting = true;
            _selectionStartImage = GetImagePixelPosition(e);
            SelectionRectangle.Visibility = Visibility.Visible;
            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;
            ImageScrollViewer.CaptureMouse();
            ImageScrollViewer.Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _isPanning = true;
            _panStart = e.GetPosition(ImageScrollViewer);
            _panOrigin = new Point(ImageScrollViewer.HorizontalOffset, ImageScrollViewer.VerticalOffset);
            ImageScrollViewer.Cursor = Cursors.Hand;
            ImageScrollViewer.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ImageScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragSelecting)
        {
            _isDragSelecting = false;
            ImageScrollViewer.ReleaseMouseCapture();
            ImageScrollViewer.Cursor = Cursors.Arrow;

            if (DataContext is ImageViewerViewModel vm)
            {
                var endImage = GetImagePixelPosition(e);
                int x = (int)Math.Min(_selectionStartImage.X, endImage.X);
                int y = (int)Math.Min(_selectionStartImage.Y, endImage.Y);
                int w = (int)Math.Abs(endImage.X - _selectionStartImage.X);
                int h = (int)Math.Abs(endImage.Y - _selectionStartImage.Y);

                vm.SetSelection(x, y, w, h);

                if (!vm.HasSelection)
                    SelectionRectangle.Visibility = Visibility.Collapsed;

                vm.IsSelecting = false;
            }

            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            ImageScrollViewer.Cursor = Cursors.Arrow;
            ImageScrollViewer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void ImageScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragSelecting)
        {
            var currentImage = GetImagePixelPosition(e);
            UpdateSelectionVisual(_selectionStartImage, currentImage);
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            var pos = e.GetPosition(ImageScrollViewer);
            var dx = pos.X - _panStart.X;
            var dy = pos.Y - _panStart.Y;

            ImageScrollViewer.ScrollToHorizontalOffset(_panOrigin.X - dx);
            ImageScrollViewer.ScrollToVerticalOffset(_panOrigin.Y - dy);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Gets the image pixel position from a mouse event by using MainImage's coordinate space.
    /// MainImage is inside the LayoutTransform, so GetPosition(MainImage) gives untransformed
    /// coordinates that map directly to image pixels.
    /// </summary>
    private Point GetImagePixelPosition(MouseEventArgs e)
    {
        if (DataContext is not ImageViewerViewModel vm || vm.DisplayImage == null)
            return new Point(0, 0);

        // GetPosition relative to MainImage gives coordinates in the image's local space
        // (before the LayoutTransform), which maps directly to pixel coordinates.
        var pos = e.GetPosition(MainImage);
        double imgX = Math.Clamp(pos.X, 0, vm.PixelWidth);
        double imgY = Math.Clamp(pos.Y, 0, vm.PixelHeight);
        return new Point(imgX, imgY);
    }

    /// <summary>
    /// Updates the selection rectangle visual on the canvas.
    /// Converts image pixel coordinates ? screen coordinates via MainImage.TranslatePoint.
    /// </summary>
    private void UpdateSelectionVisual(Point startImg, Point endImg)
    {
        double x1 = Math.Min(startImg.X, endImg.X);
        double y1 = Math.Min(startImg.Y, endImg.Y);
        double x2 = Math.Max(startImg.X, endImg.X);
        double y2 = Math.Max(startImg.Y, endImg.Y);

        // TranslatePoint from MainImage (pre-transform pixel space) to the overlay canvas
        var topLeft = MainImage.TranslatePoint(new Point(x1, y1), SelectionCanvas);
        var bottomRight = MainImage.TranslatePoint(new Point(x2, y2), SelectionCanvas);

        double left = Math.Max(0, topLeft.X);
        double top = Math.Max(0, topLeft.Y);
        double width = Math.Max(0, bottomRight.X - topLeft.X);
        double height = Math.Max(0, bottomRight.Y - topLeft.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }
}
