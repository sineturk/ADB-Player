using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using AltyaziDB.Player.App;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Views.Parts;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.Views.Pages;

/// <summary>
/// Presentation-only Home page. Existing ViewModels own all media operations.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        CinematicArtwork.ApplyIfAvailable(HomeHero, "hero-home.jpg");
    }

    private MainViewModel? Model => DataContext as MainViewModel;
    private const double ResumeCardStep = 264;

    // R5/P3: viewport-aware spacing. The page remains vertically scrollable;
    // narrow layouts stack sources instead of truncating their contents.
    private void HomeView_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (!double.IsFinite(width) || width <= 0)
            return;

        HomeQuickActions.Columns = width < 560 ? 2 : width < 850 ? 3 : 5;

        var stacked = width < 880;
        HomeSourcesFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        HomeSourcesGapColumn.Width = stacked ? new GridLength(0) : new GridLength(14);
        HomeSourcesSecondColumn.Width = stacked
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(HomeConnectionsPanel, stacked ? 1 : 0);
        Grid.SetColumn(HomeConnectionsPanel, stacked ? 0 : 2);
        HomeConnectionsPanel.Margin = stacked
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(0);

        // Telegram quick access follows Home's source-card responsive rules.
        HomeTelegramFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        HomeTelegramGapColumn.Width = stacked ? new GridLength(0) : new GridLength(14);
        HomeTelegramSecondColumn.Width = stacked
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(HomeTelegramFavoritesPanel, stacked ? 1 : 0);
        Grid.SetColumn(HomeTelegramFavoritesPanel, stacked ? 0 : 2);
        HomeTelegramFavoritesPanel.Margin = stacked
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(0);

        // Optional decorative brand art yields to readable first-run text.
        var compactHero = width < 720;
        HomeHero.Height = compactHero ? 274 : 252;
        HomeEmptyHeroBrandCard.Visibility = compactHero
            ? Visibility.Collapsed : Visibility.Visible;
        HomeEmptyHeroCopy.Margin = compactHero
            ? new Thickness(20, 8, 20, 8)
            : new Thickness(30, 8, 245, 8);
    }

    private void HomeCard_OnMouseEnter(object sender, MouseEventArgs e) =>
        AnimateHomeCard(sender, 1.012);

    private void HomeCard_OnMouseLeave(object sender, MouseEventArgs e) =>
        AnimateHomeCard(sender, 1.0);

    private static void AnimateHomeCard(object sender, double target)
    {
        if (sender is not Button button)
            return;

        if (button.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(1, 1);
            button.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            button.RenderTransform = transform;
        }

        // Honor Windows reduced-motion/high-contrast preferences; the static
        // cyan hover/focus outline remains available without any animation.
        if (SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = transform.ScaleY = 1.0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(target > 1 ? 145 : 110);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private MainViewModel? _previewModel;

    // Home is a bounded preview, not another editor for the full source lists.
    // These collections never mutate the ViewModel's persistent account/link data.
    public ObservableCollection<HomeResumeGroup> ResumeGroups { get; } = new();
    public ObservableCollection<HomeCloudTile> CloudPreview { get; } = new();
    public ObservableCollection<HomeConnectionTile> ConnectionPreview { get; } = new();
    // Presentation-only previews. The Telegram VM remains the sole source of
    // media and favorites; this page never creates an independent TDLib index.
    public ObservableCollection<TelegramMediaItem> TelegramRecentPreview { get; } = new();
    public ObservableCollection<TelegramChatItem> TelegramFavoritePreview { get; } = new();

    private void HomeView_OnLoaded(object sender, RoutedEventArgs e) => AttachPreviewModel(Model);

    private void HomeView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            AttachPreviewModel(Model);
    }

    private void AttachPreviewModel(MainViewModel? next)
    {
        if (ReferenceEquals(_previewModel, next))
        {
            UpdateSourcePreviews();
            UpdateResumeGroups();
            UpdateTelegramPreviews();
            return;
        }

        if (_previewModel is { } old)
        {
            ObserveSources(old, subscribe: false);
            old.Sources.PropertyChanged -= Sources_PropertyChanged;
            old.ContinueWatching.CollectionChanged -= ResumeItems_CollectionChanged;
            old.Telegram.PropertyChanged -= TelegramAccount_PropertyChanged;
            old.Telegram.RecentMedia.CollectionChanged -= TelegramPreview_CollectionChanged;
            old.Telegram.FavoriteChats.CollectionChanged -= TelegramPreview_CollectionChanged;
        }

        _previewModel = next;
        if (next is not null)
        {
            ObserveSources(next, subscribe: true);
            next.Sources.PropertyChanged += Sources_PropertyChanged;
            next.ContinueWatching.CollectionChanged += ResumeItems_CollectionChanged;
            next.Telegram.PropertyChanged += TelegramAccount_PropertyChanged;
            next.Telegram.RecentMedia.CollectionChanged += TelegramPreview_CollectionChanged;
            next.Telegram.FavoriteChats.CollectionChanged += TelegramPreview_CollectionChanged;
        }

        UpdateSourcePreviews();
        UpdateResumeGroups();
        UpdateTelegramPreviews();
    }

    private void TelegramAccount_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TelegramPanelViewModel.IsReady)
            or nameof(TelegramPanelViewModel.CurrentAccountId))
        {
            if (Dispatcher.CheckAccess()) UpdateTelegramPreviews();
            else _ = Dispatcher.InvokeAsync(UpdateTelegramPreviews);
        }
    }

    private void TelegramPreview_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateTelegramPreviews);
            return;
        }
        UpdateTelegramPreviews();
    }

    private void UpdateTelegramPreviews()
    {
        TelegramRecentPreview.Clear();
        TelegramFavoritePreview.Clear();
        if (_previewModel is not { } model ||
            !model.Telegram.IsReady || model.Telegram.CurrentAccountId is not > 0)
            return;

        // Session-scoped previews only. Do not fetch chat histories on Home.
        foreach (var item in model.Telegram.RecentMedia.Take(4))
            TelegramRecentPreview.Add(item);
        foreach (var chat in model.Telegram.FavoriteChats.Take(5))
            TelegramFavoritePreview.Add(chat);
    }

    private void ObserveSources(MainViewModel model, bool subscribe)
    {
        Observe(model.Sources.CloudAccounts, subscribe);
        Observe(model.Sources.SavedPublicLinks, subscribe);
        Observe(model.Sources.WebDavProfiles, subscribe);
    }

    private void Observe(object source, bool subscribe)
    {
        if (source is not INotifyCollectionChanged changed)
            return;

        if (subscribe)
            changed.CollectionChanged += SourcePreview_CollectionChanged;
        else
            changed.CollectionChanged -= SourcePreview_CollectionChanged;
    }

    private void SourcePreview_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateSourcePreviews);
            return;
        }
        UpdateSourcePreviews();
    }

    private void Sources_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SourcePanelViewModel.RcloneAvailable)
            or nameof(SourcePanelViewModel.CloudAvailabilityText)))
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateSourcePreviews);
            return;
        }
        UpdateSourcePreviews();
    }

    private void ResumeItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateResumeGroups();

    private void UpdateResumeGroups()
    {
        ResumeGroups.Clear();
        if (_previewModel is not { } model)
            return;

        foreach (var group in HomeResumeGroup.Build(model.ContinueWatching))
            ResumeGroups.Add(group);
    }

    private void UpdateSourcePreviews()
    {
        CloudPreview.Clear();
        ConnectionPreview.Clear();
        if (_previewModel is not { } model)
            return;

        var accounts = model.Sources.CloudAccounts.ToArray();
        var counts = accounts
            .GroupBy(account => HomeSourcePresentation.SafeLabel(
                account.DisplayName, account.ProviderLabel), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var runtime = model.Sources.RcloneAvailable
            ? LocalizationManager.Get("R5.Sources.RuntimeReady")
            : LocalizationManager.Get("R5.Sources.RuntimeUnavailable");
        foreach (var account in accounts.Take(4))
        {
            var key = HomeSourcePresentation.SafeLabel(account.DisplayName, account.ProviderLabel);
            // Short ID-based suffixes are stable across reordering and restarts.
            CloudPreview.Add(HomeSourcePresentation.Cloud(
                account, counts.GetValueOrDefault(key) > 1, runtime));
        }

        var stored = LocalizationManager.Get("R5.Sources.Stored");
        // Public links are already sorted by LastOpenedUtc in SourcePanelViewModel.
        // Show at most five total shortcuts; WebDAV fills remaining slots.
        foreach (var link in model.Sources.SavedPublicLinks.Take(5))
            ConnectionPreview.Add(HomeSourcePresentation.Link(link, stored));
        foreach (var profile in model.Sources.WebDavProfiles
                     .Take(Math.Max(0, 5 - ConnectionPreview.Count)))
            ConnectionPreview.Add(HomeSourcePresentation.WebDav(profile, stored));
    }

    private void ResumePrevious_Click(object sender, RoutedEventArgs e) =>
        ResumeScroll.ScrollToHorizontalOffset(Math.Max(0, ResumeScroll.HorizontalOffset - ResumeCardStep));

    private void ResumeNext_Click(object sender, RoutedEventArgs e) =>
        ResumeScroll.ScrollToHorizontalOffset(
            Math.Min(ResumeScroll.ScrollableWidth, ResumeScroll.HorizontalOffset + ResumeCardStep));

    private void ResumeScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Mouse wheel scrolls the cards only when the carousel actually overflows.
        // Otherwise the outer page keeps normal vertical wheel behavior.
        if (ResumeScroll.ScrollableWidth < 1 || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        ResumeScroll.ScrollToHorizontalOffset(
            Math.Clamp(ResumeScroll.HorizontalOffset - e.Delta, 0, ResumeScroll.ScrollableWidth));
        e.Handled = true;
    }


    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var item = sender switch
        {
            Button { DataContext: ContinueWatchingItem direct } => direct,
            Button { DataContext: HomeResumeGroup group } => group.Latest,
            _ => null
        };

        if (item is not null && Model is { } model)
            await model.PlayContinueWatchingAsync(item);
    }

    private void ShowGroupEpisodes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HomeResumeGroup group } button
            || !group.HasEpisodeChoices)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        // Chronological choices are more readable than metadata lookup order.
        var ordered = group.Episodes
            .OrderBy(item => item.Season ?? int.MaxValue)
            .ThenBy(item => item.Episode ?? int.MaxValue)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var episode = ordered[index];
            // Multiple releases of a single episode must remain individually selectable.
            var variants = ordered.Count(item => item.Season == episode.Season
                                                  && item.Episode == episode.Episode);
            var title = string.IsNullOrWhiteSpace(episode.EpisodeLabel)
                ? episode.Title
                : episode.EpisodeLabel;
            if (variants > 1)
                title += $" · #{index + 1}";
            var choice = new MenuItem
            {
                Header = $"{title}  ·  {episode.ProgressText}",
                Tag = episode
            };
            choice.Click += ResumeEpisodeMenuItem_Click;
            menu.Items.Add(choice);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async void ResumeEpisodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ContinueWatchingItem item }
            && Model is { } model)
            await model.PlayContinueWatchingAsync(item);
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.SelectedSidebarIndex = 4;
        }
    }

    private void ToggleWebLink_Click(object sender, RoutedEventArgs e)
    {
        var shouldOpen = WebLinkPanel.Visibility != Visibility.Visible;
        WebLinkPanel.Visibility = shouldOpen ? Visibility.Visible : Visibility.Collapsed;

        if (!shouldOpen)
        {
            return;
        }

        WebLinkPanel.BringIntoView();
        Dispatcher.InvokeAsync(() =>
        {
            WebLinkUrlBox.Focus();
            Keyboard.Focus(WebLinkUrlBox);
            WebLinkUrlBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseWebLink_Click(object sender, RoutedEventArgs e)
    {
        WebLinkPanel.Visibility = Visibility.Collapsed;
    }

    private void HomeView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachPreviewModel(null);
        WebLinkPanel.Visibility = Visibility.Collapsed;
    }

    // All cloud setup now uses the new Sources workspace rather than the legacy tab.
    private async void OpenTelegramRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TelegramMediaItem media } && Model is { } model)
            await model.Telegram.OpenRecentMediaAsync(media);
    }

    private async void OpenTelegramFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TelegramChatItem chat } || Model is not { } model)
            return;

        // Selecting a favorite should return to the normal channel/topic
        // workspace, not introduce a second Telegram navigation state on Home.
        model.ShowTelegramCommand.Execute(null);
        await model.Telegram.OpenFavoriteChatAsync(chat);
    }

    private void OpenSources_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow host)
        {
            host.ShowModernSources();
        }
    }

    private async void OpenCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HomeCloudTile tile } && Model is { } model)
        {
            await model.Sources.OpenCloudAccountAsync(tile.Account);
        }
    }

    private async void OpenConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HomeConnectionTile tile }
            || Model is not { } model)
            return;

        if (tile.Link is { } link)
            await model.Sources.OpenSavedLinkAsync(link);
        else if (tile.WebDav is { } profile)
            await model.Sources.OpenWebDavProfileAsync(profile);
    }
}

/// <summary>Use release episode tokens, never a guessed episode from TMDB artwork.</summary>
public sealed class HomeEpisodeLabelConverter : IValueConverter
{
    private static readonly Regex EpisodePattern = new(
        @"(?<![A-Za-z0-9])S(?<season>\d{1,2})[._\s-]*E(?<episode>\d{1,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string source)
            return string.Empty;

        var match = EpisodePattern.Match(source);
        return match.Success
            ? $"S{match.Groups["season"].Value.PadLeft(2, '0')} · E{match.Groups["episode"].Value.PadLeft(2, '0')}"
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>Do not print full saved URLs, embedded query secrets or access tokens on Home.</summary>
public sealed class HomeSafeSourceNameConverter : IValueConverter
{
    private static readonly Regex EmbeddedUrl = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = (value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "—";

        var url = EmbeddedUrl.Match(name);
        var candidate = url.Success ? url.Value : name;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
                return uri.Host;

            // Non-web source: avoid printing credentials and file system paths.
            if (candidate.Contains("://", StringComparison.Ordinal))
                return "•••";
        }

        // Preserve readable custom names but never show a full link or query tail.
        var visible = name.Split('?', '#')[0];
        if (visible.Contains("://", StringComparison.Ordinal))
            return "•••";
        return visible.Length <= 43 ? visible : visible[..42] + "…";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
