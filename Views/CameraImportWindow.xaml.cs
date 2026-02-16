using System.Collections.Specialized;
using System.Runtime.Versioning;
using System.Windows;
using PhotoGreen.ViewModels;

namespace PhotoGreen.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class CameraImportWindow : Window
{
    public CameraImportWindow()
    {
        InitializeComponent();
        var vm = new CameraImportViewModel();
        DataContext = vm;

        vm.LogEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
