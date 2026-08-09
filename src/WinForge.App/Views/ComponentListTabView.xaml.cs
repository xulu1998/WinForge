using System.Collections;
using System.Collections.ObjectModel;
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
            var items = new ObservableCollection<object>();
            foreach (var item in vm.Items)
            {
                items.Add(item);
            }

            _view = new ListCollectionView(items) { Filter = Filter };
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
