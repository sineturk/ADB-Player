using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AltyaziDB.Player.App.Localization;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
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
    public static readonly DependencyProperty FeaturedItemProperty =
        DependencyProperty.Register(nameof(FeaturedItem), typeof(CatalogItem),
            typeof(DiscoverView), new PropertyMetadata(null));

    public CatalogItem? FeaturedItem
    {
        get => (CatalogItem?)GetValue(FeaturedItemProperty);
        private set
        {
            if (ReferenceEquals(GetValue(FeaturedItemProperty), value))
                return;
            SetValue(FeaturedItemProperty, value);
            RequestFeaturedOverview();
        }
    }

    public static readonly DependencyProperty FeaturedOverviewProperty =
        DependencyProperty.Register(nameof(FeaturedOverview), typeof(string),
            typeof(DiscoverView), new PropertyMetadata(string.Empty));

    public string FeaturedOverview
    {
        get => (string)GetValue(FeaturedOverviewProperty);
        private set
        {
            SetValue(FeaturedOverviewProperty, value);
            _featuredOverviewExpanded = false;
            UpdateFeaturedOverviewPresentation(ActualWidth);
        }
    }

    // The language token invalidates MultiBindings even when addon genre strings
    // themselves have not changed; source catalog metadata is never rewritten.
    public static readonly DependencyProperty LanguageRevisionProperty =
        DependencyProperty.Register(nameof(LanguageRevision), typeof(int),
            typeof(DiscoverView), new PropertyMetadata(0));

    public int LanguageRevision
    {
        get => (int)GetValue(LanguageRevisionProperty);
        private set => SetValue(LanguageRevisionProperty, value);
    }

    private bool _featuredOverviewExpanded;
    private CancellationTokenSource? _featuredOverviewCancellation;

    private void DiscoverToggleOverview_Click(object sender, RoutedEventArgs e)
    {
        _featuredOverviewExpanded = !_featuredOverviewExpanded;
        UpdateFeaturedOverviewPresentation(ActualWidth);
    }

    private void UpdateFeaturedOverviewPresentation(double width)
    {
        if (!IsInitialized)
            return;

        // The teaser stays compact, but its entire original/localized synopsis
        // must remain accessible without opening a separate details panel.
        var threshold = width < 550 ? 70 : width < 850 ? 115 : width < 1150 ? 160 : 200;
        var isLong = FeaturedOverview.Length > threshold;
        DiscoverReadMoreButton.Visibility = isLong
            ? Visibility.Visible : Visibility.Collapsed;
        var actionKey = _featuredOverviewExpanded
            ? "R5.Discover.ReadLess"
            : "R5.Discover.ReadMore";
        var label = LocalizationManager.Get(actionKey);
        DiscoverReadMoreButton.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(DiscoverReadMoreButton, label);

        DiscoverFeaturedDescription.MaxHeight = _featuredOverviewExpanded
            ? double.PositiveInfinity : 60;
        DiscoverFeaturedDescription.TextTrimming = _featuredOverviewExpanded
            ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        DiscoverFeaturedDescription.Visibility =
            width < 550 && !_featuredOverviewExpanded
                ? Visibility.Collapsed : Visibility.Visible;
        var compactHeight = width < 550 ? 210 : 228;
        DiscoverSpotlight.MinHeight = compactHeight;
        DiscoverSpotlight.Height = _featuredOverviewExpanded
            ? double.NaN : compactHeight;
    }

    private void RequestFeaturedOverview()
    {
        _featuredOverviewCancellation?.Cancel();
        _featuredOverviewCancellation?.Dispose();
        _featuredOverviewCancellation = null;

        var item = FeaturedItem;
        FeaturedOverview = item?.Description ?? string.Empty;
        if (item is null || _addons is null || LocalizationManager.IsEnglish)
            return;

        var language = LocalizationManager.CurrentLanguageCode;
        _featuredOverviewCancellation = new CancellationTokenSource();
        _ = LoadFeaturedOverviewAsync(item, language, _featuredOverviewCancellation.Token);
    }

    private async Task LoadFeaturedOverviewAsync(CatalogItem item, string language,
        CancellationToken token)
    {
        try
        {
            var text = await _addons!.GetPreferredOverviewAsync(item, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && ReferenceEquals(FeaturedItem, item) &&
                string.Equals(language, LocalizationManager.CurrentLanguageCode,
                    StringComparison.OrdinalIgnoreCase))
                FeaturedOverview = text;
        }
        catch (OperationCanceledException)
        {
            // Item or language changed before authenticated metadata finished.
        }
    }

    private void LocalizedMetadataChanged(object? sender, EventArgs e) =>
        RequestFeaturedOverview();

    private void DiscoverLanguageChanged(object? sender, EventArgs e)
    {
        LanguageRevision++;
        RequestFeaturedOverview();
    }

    private static bool IsPopularCatalogItem(CatalogItem item)
    {
        var catalog = item.OriginCatalogId ?? string.Empty;
        return catalog.Contains("popular", StringComparison.OrdinalIgnoreCase)
            || catalog.Contains("trending", StringComparison.OrdinalIgnoreCase)
            || catalog.Contains("top", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFeaturedItem()
    {
        if (_addons is null || _addons.Items.Count == 0)
        {
            FeaturedItem = null;
            return;
        }

        // Prefer catalog-provider popular/trending entries when available.
        // Otherwise the first item with artwork is editorial featured, not
        // presented as an externally verified popularity ranking.
        var candidates = _addons.Items.Take(80).ToList();
        if (FeaturedItem is not null && candidates.Any(item => item.Key == FeaturedItem.Key))
            return; // avoid changing the spotlight with each catalog batch

        FeaturedItem = candidates.FirstOrDefault(item =>
                IsPopularCatalogItem(item) && !string.IsNullOrWhiteSpace(item.PosterUrl))
            ?? candidates.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.BackgroundUrl) &&
                !string.IsNullOrWhiteSpace(item.PosterUrl))
            ?? candidates.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PosterUrl))
            ?? candidates[0];
    }

    private void DiscoverFeaturedDetails_Click(object sender, RoutedEventArgs e)
    {
        if (FeaturedItem is null || _addons is null)
            return;

        OpenDetails(FeaturedItem);
    }



    private void Discover_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDiscoverLayout(e.NewSize.Width);

    private void UpdateDiscoverLayout(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
            return;

        // The detail dialog overlays an unchanged full-width poster catalog.
        var compactSearch = width < 690;

        DiscoverFeaturedPoster.Visibility = width < 750
            ? Visibility.Collapsed : Visibility.Visible;
        DiscoverFeaturedCopy.Margin = width < 750
            ? new Thickness(19, 12, 17, 12)
            : new Thickness(32, 18, 22, 18);
        UpdateFeaturedOverviewPresentation(width);

        DiscoverSearchSecondRow.Height = compactSearch
            ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(DiscoverSearchHost, compactSearch ? 3 : 1);
        Grid.SetRow(DiscoverTypeFilter, compactSearch ? 1 : 0);
        Grid.SetColumn(DiscoverTypeFilter, compactSearch ? 0 : 1);
        Grid.SetRow(DiscoverRefreshButton, compactSearch ? 1 : 0);
        Grid.SetColumn(DiscoverRefreshButton, 2);
        DiscoverSearchHost.Margin = compactSearch
            ? new Thickness(0, 0, 0, 8)
            : new Thickness(0, 0, 9, 0);
        DiscoverTypeFilter.Width = compactSearch ? 210 : double.NaN;
        DiscoverTypeFilter.HorizontalAlignment = compactSearch
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Stretch;

        // Never resize catalog posters when the modal opens.
        DiscoverCatalogSurface.Height = double.NaN;
        DiscoverCatalogLayout.Height = 685;

        var modalWidth = Math.Max(320, width - 32);
        DiscoverModalCard.Width = Math.Min(1040, modalWidth);
        DiscoverModalCard.Height = Math.Max(280, Math.Min(770, ActualHeight - 32));
        // Cinema-first body is full-width. Only the Play CTA moves below source
        // selection on narrow windows; Cloud controls always receive full width.
        var stackedPlay = modalWidth < 750;
        DiscoverModalPlayGap.Width = stackedPlay ? new GridLength(0) : new GridLength(16);
        DiscoverModalPlayColumn.Width = stackedPlay ? new GridLength(0) : new GridLength(210);
        DiscoverModalPlaySecondRow.Height = stackedPlay
            ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(DiscoverModalPlayButton, stackedPlay ? 0 : 2);
        Grid.SetRow(DiscoverModalPlayButton, stackedPlay ? 1 : 0);
        DiscoverModalPlayButton.Margin = stackedPlay
            ? new Thickness(0, 12, 0, 0) : new Thickness(0, 10, 0, 0);
        DiscoverModalHero.Height = modalWidth < 570 ? 195 : 250;
        DiscoverModalBackdrop.Height = DiscoverModalHero.Height;
        DiscoverModalPoster.Visibility = modalWidth < 570
            ? Visibility.Collapsed : Visibility.Visible;
        DiscoverModalContent.Margin = modalWidth < 570
            ? new Thickness(18, 12, 18, 22)
            : new Thickness(32, 18, 32, 27);
    }

    private void DiscoverClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_addons is not null)
            _addons.SearchText = string.Empty;
        CatalogSearchBox.Focus();
    }

    private void CatalogCard_OnMouseEnter(object sender, MouseEventArgs e) =>
        AnimateCatalogCard(sender, 1.035);

    private void CatalogCard_OnMouseLeave(object sender, MouseEventArgs e) =>
        AnimateCatalogCard(sender, 1.0);

    private static void AnimateCatalogCard(object sender, double targetScale)
    {
        if (sender is not ListBoxItem card)
            return;

        if (card.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(1, 1);
            card.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            card.RenderTransform = transform;
        }

        // Respect OS reduced-motion/high-contrast settings; selection and
        // keyboard-focus borders remain visible without animation.
        if (SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = transform.ScaleY = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(targetScale > 1 ? 160 : 110);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }


    private AddonsPanelViewModel? _addons;
    private IInputElement? _focusBeforeModal;
    private bool _refreshQueued;
    private int _loadedCount = BatchSize;

    public ObservableCollection<CatalogItem> VisibleItems { get; } = new();

    public DiscoverView() => InitializeComponent();

    private void Discover_OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationManager.LanguageChanged -= DiscoverLanguageChanged;
        LocalizationManager.LanguageChanged += DiscoverLanguageChanged;
        DiscoverModalOverlay.Visibility = Visibility.Collapsed;
        DiscoverPageScrollViewer.IsHitTestVisible = true;
        AttachSource((DataContext as MainViewModel)?.Addons);
        RequestFeaturedOverview();
        UpdateDiscoverLayout(ActualWidth);
    }

    private void Discover_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachSource((e.NewValue as MainViewModel)?.Addons);
    }

    private void Discover_OnUnloaded(object sender, RoutedEventArgs e)
    {
        LocalizationManager.LanguageChanged -= DiscoverLanguageChanged;
        DiscoverModalOverlay.Visibility = Visibility.Collapsed;
        DiscoverPageScrollViewer.IsHitTestVisible = true;
        AttachSource(null);
        _featuredOverviewCancellation?.Cancel();
        _featuredOverviewCancellation?.Dispose();
        _featuredOverviewCancellation = null;
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
            _addons.LocalizedMetadataRefreshRequested -= LocalizedMetadataChanged;
        }

        _addons = newSource;
        _loadedCount = BatchSize;

        if (_addons is not null)
        {
            _addons.Items.CollectionChanged += Catalog_CollectionChanged;
            _addons.LocalizedMetadataRefreshRequested += LocalizedMetadataChanged;
        }

        RequestFeaturedOverview();
        ScheduleRefresh();
    }

    private void Catalog_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            FeaturedItem = null;
            _loadedCount = BatchSize;
            DiscoverModalOverlay.Visibility = Visibility.Collapsed;
            DiscoverPageScrollViewer.IsHitTestVisible = true;
            UpdateDiscoverLayout(ActualWidth);
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
                FeaturedItem = null;
                return;
            }

            RefreshFeaturedItem();
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

        if (_addons?.SelectedItem is { } item)
            OpenDetails(item);
    }

    private void Catalog_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((e.Key != Key.Enter && e.Key != Key.Space) ||
            _addons?.SelectedItem is not { } item)
            return;

        OpenDetails(item);
        e.Handled = true;
    }

    private void OpenDetails(CatalogItem item)
    {
        if (_addons is null)
            return;

        if (DiscoverModalOverlay.Visibility != Visibility.Visible)
            _focusBeforeModal = Keyboard.FocusedElement;

        _addons.SelectedItem = item;
        DiscoverModalOverlay.Visibility = Visibility.Visible;
        DiscoverPageScrollViewer.IsHitTestVisible = false;
        UpdateDiscoverLayout(ActualWidth);
        DiscoverModalScroll.ScrollToTop();
        Dispatcher.BeginInvoke(() => DiscoverModalCloseButton.Focus(),
            DispatcherPriority.Input);
    }

    private void DiscoverModal_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DiscoverModalOverlay.Visibility != Visibility.Visible)
            return;

        CloseDetails();
        e.Handled = true;
    }

    private void DiscoverModalOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DiscoverModalOverlay.Visibility != Visibility.Visible ||
            e.OriginalSource is not DependencyObject hit)
            return;

        if (ReferenceEquals(hit, DiscoverModalCard) || DiscoverModalCard.IsAncestorOf(hit))
            return;

        CloseDetails();
        e.Handled = true;
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void CloseDetails()
    {
        if (DiscoverModalOverlay.Visibility != Visibility.Visible)
            return;

        DiscoverModalOverlay.Visibility = Visibility.Collapsed;
        DiscoverPageScrollViewer.IsHitTestVisible = true;
        if (_addons is not null)
            _addons.SelectedItem = null;

        // Restore selected poster focus and keep the existing page scroll offset.
        if (_focusBeforeModal is UIElement element && element.IsVisible && element.IsEnabled)
            element.Focus();
        else
            CatalogList.Focus();
        _focusBeforeModal = null;
    }

    private void ManageAddons_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.SelectedSidebarIndex = 7;
        }
    }
}
/// <summary>Localized content badge for existing catalog enum; no metadata mutation.</summary>
public sealed class DiscoverMediaKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CatalogContentType.Movie => LocalizationManager.Get("Addons.Filter.Movie"),
            CatalogContentType.Series => LocalizationManager.Get("Addons.Filter.Series"),
            CatalogContentType.Anime => LocalizationManager.Get("Addons.Filter.Anime"),
            _ => string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

 
/// <summary>
/// Transliterate only recognized genre tokens on screen. The original addon genre
/// values and search filters remain untouched, and English is always verbatim.
/// </summary>
public sealed class DiscoverLocalizedGenreConverter : IMultiValueConverter
{
    private static readonly Dictionary<string, string> TurkishGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Aksiyon",
        ["Adventure"] = "Macera",
        ["Animation"] = "Animasyon",
        ["Biography"] = "Biyografi",
        ["Comedy"] = "Komedi",
        ["Crime"] = "Suç",
        ["Documentary"] = "Belgesel",
        ["Drama"] = "Dram",
        ["Family"] = "Aile",
        ["Fantasy"] = "Fantastik",
        ["History"] = "Tarih",
        ["Horror"] = "Korku",
        ["Kids"] = "Çocuk",
        ["Music"] = "Müzik",
        ["Musical"] = "Müzikal",
        ["Mystery"] = "Gizem",
        ["Reality"] = "Reality",
        ["Romance"] = "Romantik",
        ["Sci-Fi"] = "Bilim Kurgu",
        ["Sci-Fi & Fantasy"] = "Bilim Kurgu ve Fantastik",
        ["Science Fiction"] = "Bilim Kurgu",
        ["Short"] = "Kısa Film",
        ["Sport"] = "Spor",
        ["Suspense"] = "Gerilim",
        ["Talk"] = "Söyleşi",
        ["Thriller"] = "Gerilim",
        ["TV Movie"] = "Televizyon Filmi",
        ["War"] = "Savaş",
        ["Western"] = "Western"
    };

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = values.Length > 0 && values[0] is string text ? text : string.Empty;
        if (LocalizationManager.IsEnglish || string.IsNullOrEmpty(raw))
            return raw;

        // GenreLabel and SelectedItemMetadata both use the middle-dot separator.
        // Translate exact segments only, never provider names or title substrings.
        return string.Join(" · ", raw.Split('·').Select(segment =>
        {
            var trimmed = segment.Trim();
            return TurkishGenres.TryGetValue(trimmed, out var localized) ? localized : trimmed;
        }));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
