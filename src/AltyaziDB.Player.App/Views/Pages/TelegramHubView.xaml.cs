using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.Views.Pages;

/// <summary>P5: WPF-only aggregation; media state remains in the existing ViewModels.</summary>
public partial class TelegramHubView : UserControl
{
    public TelegramHubView()
    {
        InitializeComponent();
        SizeChanged += OnHubSizeChanged;
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private async void OnTelegramHubLoaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible && Model?.Telegram.IsReady == true)
            await Model.Telegram.RefreshFavoriteMediaAsync();
    }

    private async void OnTelegramHubVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && Model?.Telegram.IsReady == true)
            await Model.Telegram.RefreshFavoriteMediaAsync();
    }

    private async void RefreshFavoriteFeed_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady == true)
            await Model.Telegram.RefreshFavoriteMediaAsync(force: true);
    }

    private async void OpenFavoriteMedia_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady == true &&
            sender is Button { DataContext: TelegramMediaItem item } &&
            Model.Telegram.IsFavoriteChat(item.ChatId))
            await Model.Telegram.OpenRecentMediaAsync(item);
    }

    private void OnHubSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TelegramHubLeftColumn is null || !double.IsFinite(e.NewSize.Width)) return;
        var compact = e.NewSize.Width < 780;
        TelegramHubLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        TelegramHubGapColumn.Width = new GridLength(compact ? 0 : 12);
        TelegramHubRightColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(TelegramHubOpenedPanel, compact ? 1 : 0);
        Grid.SetColumn(TelegramHubOpenedPanel, compact ? 0 : 2);
        UpdateSeenColumns(compact);
    }

    private void TelegramHubSeenItems_OnLoaded(object sender, RoutedEventArgs e) =>
        UpdateSeenColumns(ActualWidth < 780);

    private void UpdateSeenColumns(bool compact)
    {
        // The session section is a small, bounded list; its layout can adapt
        // without replacing the data collection or touching Telegram history.
        var grid = FindSeenGrid(TelegramHubSeenItems);
        if (grid is not null) grid.Columns = compact ? 1 : 2;
    }

    private static UniformGrid? FindSeenGrid(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UniformGrid grid) return grid;
            var nested = FindSeenGrid(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void ResumeTelegram_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady == true && sender is Button { DataContext: ContinueWatchingItem item })
            await Model.PlayContinueWatchingAsync(item);
    }

    private async void OpenRecentlyPlayed_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady == true && sender is Button { DataContext: RecentSourceEntry item })
            await Model.PlayRecentAsync(item);
    }

    private async void OpenStarredChat_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady != true ||
            sender is not Button { DataContext: TelegramChatItem chat }) return;
        Model.ShowTelegramCommand.Execute(null);
        await Model.Telegram.OpenFavoriteChatAsync(chat);
    }

    private async void OpenSessionMedia_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Telegram.IsReady == true && sender is Button { DataContext: TelegramMediaItem item })
            await Model.Telegram.OpenRecentMediaAsync(item);
    }
}
