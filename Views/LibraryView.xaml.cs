using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoGreen.Models;
using PhotoGreen.ViewModels;

namespace PhotoGreen.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>Outer width of one thumbnail: 130 px plus 4 px margin on each side.</summary>
    private const double ThumbnailSlotWidth = 138;

    /// <summary>Allowance for the vertical scrollbar so rows never overflow horizontally.</summary>
    private const double ScrollBarAllowance = 20;

    private void OnThumbnailListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || DataContext is not LibraryViewModel vm)
            return;

        var available = e.NewSize.Width - ScrollBarAllowance;
        vm.ItemsPerRow = Math.Max(1, (int)(available / ThumbnailSlotWidth));
    }

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LibraryThumbnailItem item)
        {
            if (DataContext is LibraryViewModel vm)
            {
                // ClickCount is only meaningful on button-down, so the double-click is
                // detected here rather than in a separate mouse-up handler.
                if (e.ClickCount >= 2)
                {
                    vm.SelectItem(item, false, false);
                    vm.OpenInEditorCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                vm.SelectItem(item, ctrl, shift);

                // The thumbnails live in a non-focusable template, so move focus to the
                // owning ListBox to keep arrow/shift keyboard selection working.
                FindParentListBox(fe)?.Focus();
            }
        }
    }

    private static ListBox? FindParentListBox(DependencyObject element)
    {
        while (element != null)
        {
            if (element is ListBox listBox)
                return listBox;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>
    /// Right-click selects the item unless it is already part of the selection, so a
    /// context-menu delete never silently targets something the user cannot see.
    /// </summary>
    private void OnThumbnailRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LibraryThumbnailItem item &&
            DataContext is LibraryViewModel vm && !item.IsSelected)
        {
            vm.SelectItem(item, false, false);
        }
    }

    private void OnThumbnailAreaKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm)
            return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (e.Key == Key.Delete)
        {
            if (vm.DeleteSelectedCommand.CanExecute(null))
                vm.DeleteSelectedCommand.Execute(null);

            e.Handled = true;
        }
        else if (e.Key == Key.A && ctrl)
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Up)
        {
            vm.MoveSelection(LibraryViewModel.SelectionMove.Previous, shift);
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.Down)
        {
            vm.MoveSelection(LibraryViewModel.SelectionMove.Next, shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            vm.MoveSelection(LibraryViewModel.SelectionMove.First, shift);
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            vm.MoveSelection(LibraryViewModel.SelectionMove.Last, shift);
            e.Handled = true;
        }
    }
}
