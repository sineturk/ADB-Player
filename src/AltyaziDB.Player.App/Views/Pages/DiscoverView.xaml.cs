using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using System.Windows.Threading;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.App.Views.Parts;

namespace AltyaziDB.Player.App.Views.Pages;

/// <summary>
/// Incremental catalog presentation; the existing AddonsPanelViewModel remains
/// the sole owner of search, item selection, cancellation and stream playback.
/// </summary>
public partial class DiscoverView : UserControl
{
    private const int BatchSize = 30;
    private AddonsPanelViewModel? _addons;
    private bool _refreshQueued;
    private int _loadedCount = BatchSize;

    public ObservableCollection<CatalogItem> VisibleItems { get; } = new();

    public DiscoverView()
    {
        InitializeComponent();
        CinematicArtwork.ApplyIfAvailable(DiscoverHero, "hero-discover.jpg");
    }

    private void Discover_OnLoaded(object sender, RoutedEventArgs e)
    {
        DetailsPanel.Visibility = Visibility.Collapsed;
        AttachSource((DataContext as MainViewModel)?.Addons);
    }

    private void Discover_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachSource((e.NewValue as MainViewModel)?.Addons);
    }

    private void Discover_OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachSource(null);
    }

    private void AttachSource(AddonsPanelViewModel? newSource)
    {
        if (ReferenceEquals(_addons, newSource))
        {
            return;
        }

        if (_addons is not null)
        {
            _addons.Items.CollectionChanged -= Catalog_CollectionChanged;
        }

        _addons = newSource;
        _loadedCount = BatchSize;

        if (_addons is not null)
        {
            _addons.Items.CollectionChanged += Catalog_CollectionChanged;
        }

        ScheduleRefresh();
    }

    private void Catalog_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _loadedCount = BatchSize;
            DetailsPanel.Visibility = Visibility.Collapsed;
        }
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _refreshQueued = false;
            if (_addons is null)
            {
                VisibleItems.Clear();
                return;
            }

            var count = Math.Min(_loadedCount, _addons.Items.Count);
            // An unchanged visible prefix should not be cleared/reselected on
            // every catalog batch, which would re-trigger expensive stream search.
            var prefixStable = VisibleItems.Count <= count;
            if (prefixStable)
            {
                for (var i = 0; i < VisibleItems.Count; i++)
                {
                    if (!ReferenceEquals(VisibleItems[i], _addons.Items[i]))
                    {
                        prefixStable = false;
                        break;
                    }
                }
            }

            if (!prefixStable)
            {
                VisibleItems.Clear();
            }

            for (var i = VisibleItems.Count; i < count; i++)
            {
                VisibleItems.Add(_addons.Items[i]);
            }
        }, DispatcherPriority.Background);
    }

    private void Catalog_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_addons is null || _loadedCount >= _addons.Items.Count)
        {
            return;
        }

        if (e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 190)
        {
            return;
        }

        var previous = _loadedCount;
        _loadedCount = Math.Min(_addons.Items.Count, _loadedCount + BatchSize);
        foreach (var item in _addons.Items.Skip(previous).Take(_loadedCount - previous))
        {
            VisibleItems.Add(item);
        }
    }

    private void Catalog_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(CatalogList, source) is not ListBoxItem ||
            CatalogList.SelectedItem is null)
        {
            return;
        }

        DetailsPanel.Visibility = Visibility.Visible;
    }

    private void Catalog_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DetailsPanel.Visibility == Visibility.Visible)
        {
            CloseDetails();
            e.Handled = true;
            return;
        }

        if ((e.Key != Key.Enter && e.Key != Key.Space) || CatalogList.SelectedItem is null)
        {
            return;
        }

        DetailsPanel.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        CloseDetails();
    }

    private void CloseDetails()
    {
        DetailsPanel.Visibility = Visibility.Collapsed;
        if (_addons is not null)
        {
            _addons.SelectedItem = null;
        }

        CatalogList.Focus();
    }

    private void ManageAddons_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.SelectedSidebarIndex = 7;
        }
    }
}