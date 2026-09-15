using System.Collections.Specialized;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using PhotoGreen.ViewModels;

namespace PhotoGreen.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class CameraImportWindow : Window
{
    private readonly CameraImportViewModel _vm;

    public CameraImportWindow(CameraImportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // The view model outlives this window, so the subscription must be removed
        // on close or every reopen would leave a handler pointing at a dead LogList.
        _vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
        Closed += (_, _) => _vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        // Never touch the ItemsControl while the CollectionChanged event is still
        // being dispatched: this handler is attached directly to the source collection
        // and therefore runs *before* the CollectionView / ItemContainerGenerator have
        // seen the change. Calling ScrollIntoView here forces a re-entrant measure and
        // generation pass against a stale item map, which makes the generator's
        // accumulated count drift from Items.Count and throws
        // "An ItemsControl is inconsistent with its items source".
        Dispatcher.InvokeAsync(() =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        }, DispatcherPriority.Background);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
