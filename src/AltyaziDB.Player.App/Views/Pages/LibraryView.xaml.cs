using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Models;
using System.Linq;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class LibraryView : UserControl
{
    private bool _artworkPageLoading;

    private void Library_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateLibraryLayout(e.NewSize.Width);

    private void UpdateLibraryLayout(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
            return;

        var compact = width < 1220;
        var veryCompact = width < 930;
        var overlayDetails = width < 1260;
        var folderWidth = width < 850 ? 190 : width < 1140 ? 210 : 240;

        LibraryFolderColumn.Width = new GridLength(folderWidth);
        LibraryColumnGap.Width = new GridLength(width < 850 ? 10 : 14);

        LibraryHeaderActions.Visibility = width < 1080
            ? Visibility.Collapsed : Visibility.Visible;
        // All quick actions also remain available in the folder workspace.
        LibraryHeroCopy.Margin = new Thickness(4, 0, 12, 0);
        LibraryHero.Height = 94;

        // Two-line toolbar at laptop widths; at the narrowest sizes the
        // view switch and refresh action use a third line instead of clipping.
        LibraryFilterSecondRow.Height = compact
            ? GridLength.Auto : new GridLength(0);
        LibraryFilterThirdRow.Height = veryCompact
            ? GridLength.Auto : new GridLength(0);
        LibraryFilterTypeColumn.Width = new GridLength(compact ? 190 : 155);
        LibraryFilterSortColumn.Width = compact
            ? new GridLength(0) : new GridLength(190);
        LibraryFilterViewColumn.Width = veryCompact
            ? new GridLength(0) : GridLength.Auto;
        LibraryFilterRefreshColumn.Width = veryCompact
            ? new GridLength(0) : GridLength.Auto;

        Grid.SetRow(LibrarySearchHost, 0);
        Grid.SetColumn(LibrarySearchHost, 0);
        Grid.SetColumnSpan(LibrarySearchHost, compact ? 5 : 1);
        LibrarySearchHost.Margin = compact
            ? new Thickness(0, 0, 0, 8)
            : new Thickness(0, 0, 8, 0);

        Grid.SetRow(LibraryTypeFilter, compact ? 1 : 0);
        Grid.SetColumn(LibraryTypeFilter, compact ? 0 : 1);
        LibraryTypeFilter.Margin = veryCompact
            ? new Thickness(0, 0, 8, 8)
            : new Thickness(0, 0, 8, 0);
        Grid.SetRow(LibrarySortFilter, compact ? 1 : 0);
        Grid.SetColumn(LibrarySortFilter, compact ? 1 : 2);
        LibrarySortFilter.Margin = veryCompact
            ? new Thickness(0, 0, 0, 8)
            : new Thickness(0, 0, 8, 0);

        Grid.SetRow(LibraryViewSwitch, veryCompact ? 2 : compact ? 1 : 0);
        Grid.SetColumn(LibraryViewSwitch, veryCompact ? 0 : 3);
        LibraryViewSwitch.HorizontalAlignment = veryCompact
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Stretch;
        Grid.SetRow(LibraryRefreshButton, veryCompact ? 2 : compact ? 1 : 0);
        Grid.SetColumn(LibraryRefreshButton, veryCompact ? 1 : 4);

        LibrarySmartSearchHint.Visibility = width < 1050
            ? Visibility.Collapsed : Visibility.Visible;

        // A selected movie/series appears as a panel on wide screens and
        // overlays the catalog on compact screens. A hidden pane never
        // reserves 390/420px and shrinks poster cards out of sight.
        LibraryDetailsColumn.Width = overlayDetails
            ? new GridLength(0) : GridLength.Auto;
        var overlayWidth = Math.Max(225, Math.Min(420, width - folderWidth - 88));
        AdaptDetailsPanel(LibrarySeriesDetails, 420, overlayDetails, overlayWidth);
        AdaptDetailsPanel(LibraryItemDetails, 390, overlayDetails, overlayWidth);
    }

    private static void AdaptDetailsPanel(
        System.Windows.Controls.Border panel, double normalWidth,
        bool overlay, double overlayWidth)
    {
        Grid.SetColumn(panel, overlay ? 0 : 1);
        panel.Width = overlay ? Math.Min(normalWidth, overlayWidth) : normalWidth;
        panel.Margin = overlay
            ? new Thickness(0) : new Thickness(14, 0, 0, 0);
        panel.HorizontalAlignment = overlay
            ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Stretch;
        System.Windows.Controls.Panel.SetZIndex(panel, overlay ? 6 : 0);
    }

    private void LibraryClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
            model.Library.SearchText = string.Empty;
        LibrarySearchBox.Focus();
    }

    private void LibraryResetFilters_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
            return;

        var library = main.Library;
        library.SearchText = string.Empty;

        var allMedia = library.Filters.FirstOrDefault(
            filter => filter.Value == LibraryMediaFilter.All);
        if (allMedia is not null)
            library.SelectedFilter = allMedia;

        var allFolders = library.ScopeChoices.FirstOrDefault(
            scope => scope.Value == LibraryBrowseScope.AllFolders);
        if (allFolders is not null)
            library.SelectedScope = allFolders;

        LibrarySearchBox.Focus();
    }

    private void LibraryPoster_OnMouseEnter(object sender, MouseEventArgs e) =>
        AnimateLibraryPoster(sender, 1.012);

    private void LibraryPoster_OnMouseLeave(object sender, MouseEventArgs e) =>
        AnimateLibraryPoster(sender, 1.0);

    private static void AnimateLibraryPoster(object sender, double targetScale)
    {
        if (sender is not ListBoxItem card)
            return;

        if (card.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            card.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            card.RenderTransform = scale;
        }

        // Respect Windows high-contrast / reduce-motion, as Home and Explore do.
        if (SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1.0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(targetScale > 1 ? 145 : 110);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }



    public LibraryView() => InitializeComponent();

    private async void Library_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is System.Windows.Controls.ListBox listBox
            && string.Equals(listBox.Name, nameof(LibraryGridItems), StringComparison.Ordinal))
        {
            await vm.Library.PlaySelectedGridAsync();
            return;
        }

        await vm.Library.PlaySelectedAsync();
    }

    private async void SeriesEpisode_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.Library.PlaySelectedAsync();
    }

    private async void LibraryItems_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_artworkPageLoading
            || e.OriginalSource is not ScrollViewer viewer
            || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        var isGrid = sender is System.Windows.Controls.ListBox listBox
                     && string.Equals(listBox.Name, nameof(LibraryGridItems), StringComparison.Ordinal);

        if (isGrid)
        {
            if (e.VerticalChange <= 0 || viewer.VerticalOffset < 120)
                return;
        }
        else if (viewer.VerticalOffset < viewer.ScrollableHeight - 260)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
            return;

        try
        {
            _artworkPageLoading = true;
            await vm.Library.LoadMoreArtworkAsync();
        }
        finally
        {
            _artworkPageLoading = false;
        }
    }
}
