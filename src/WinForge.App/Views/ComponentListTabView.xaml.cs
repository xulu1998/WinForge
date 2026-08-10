using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinForge.App.ViewModels;

namespace WinForge.App.Views;

/// <summary>
/// Renders one filtered list for a Customize tab (Apps / Windows components /
/// Services). The list of <see cref="ISelectableItem"/>s comes from the shared
/// <see cref="ComponentsViewModel"/> discovery pass. Protected entries are shown
/// disabled; the "Show protected entries" toggle (Services only) reveals them.
/// </summary>
public partial class ComponentListTabView : UserControl
{
    private ListCollectionView? _view;
    private bool _showProtected;

    public ComponentListTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ComponentListTabViewModel vm)
        {
            // Bind to the LIVE discovery collection (not a snapshot copy). The
            // shared ComponentsViewModel mutates its ObservableCollection in place
            // when discovery completes (Clear + Add), and ListCollectionView
            // subscribes to INotifyCollectionChanged on that exact instance, so
            // the ListView refreshes immediately — without requiring the user to
            // switch tabs and force a view recreation. Snapshotting into a
            // separate ObservableCollection here was Defect 1: the copy never
            // received the post-discovery CollectionChanged, so the active Apps
            // tab stayed empty until navigated away and back.
            _view = new ListCollectionView(vm.Items) { Filter = Filter };
            ListView.ItemsSource = _view;
            ShowProtectedCheck.Visibility = vm.ShowProtectedVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _view = null;
            ListView.ItemsSource = null;
            ShowProtectedCheck.Visibility = Visibility.Collapsed;
        }
    }

    private bool Filter(object item) => _showProtected || (item is ISelectableItem si && si.CanSelect);

    private void ShowProtectedCheck_OnChecked(object sender, RoutedEventArgs e)
    {
        _showProtected = true;
        _view?.Refresh();
    }

    private void ShowProtectedCheck_OnUnchecked(object sender, RoutedEventArgs e)
    {
        _showProtected = false;
        _view?.Refresh();
    }
}
