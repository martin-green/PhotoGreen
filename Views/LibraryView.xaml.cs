using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoGreen.Models;
using PhotoGreen.ViewModels;

namespace PhotoGreen.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LibraryThumbnailItem item)
        {
            if (DataContext is LibraryViewModel vm)
            {
                vm.SelectedItem = item;
            }
        }
    }

    private void OnThumbnailDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement fe && fe.DataContext is LibraryThumbnailItem item)
        {
            if (DataContext is LibraryViewModel vm)
            {
                vm.SelectedItem = item;
                vm.OpenInEditorCommand.Execute(null);
            }
        }
    }
}
