using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using PasswordBox = System.Windows.Controls.PasswordBox;
using AltyaziDB.Player.App.ViewModels;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class TelegramView : UserControl
{
    public TelegramView() => InitializeComponent();
    // P4: View-local responsive layout. No ViewModel, TDLib, playback, or list mutation.
    private const double TelegramSidebarBreakpoint = 1060;
    private const double TelegramSinglePaneBreakpoint = 740;
    private bool _showCompactMedia;

    private void TelegramView_OnLoaded(object sender, RoutedEventArgs e) => ApplyTelegramResponsiveLayout();
    private void TelegramView_OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTelegramResponsiveLayout();

    private void TelegramShowMedia_OnClick(object sender, RoutedEventArgs e)
    {
        _showCompactMedia = true;
        ApplyTelegramResponsiveLayout();
    }

    private void TelegramBackToChats_OnClick(object sender, RoutedEventArgs e)
    {
        _showCompactMedia = false;
        ApplyTelegramResponsiveLayout();
        TelegramChatSearch.Focus();
    }

    private void ApplyTelegramResponsiveLayout()
    {
        // Loaded/SizeChanged may occur during initial XAML construction.
        if (TelegramWorkspaceGrid is null || TelegramFolderColumn is null ||
            TelegramChatColumn is null || TelegramMediaColumn is null ||
            TelegramFolderSurface is null || TelegramChatSurface is null ||
            TelegramMediaSurface is null || TelegramFolderPicker is null ||
            TelegramCompactMenu is null || TelegramShowMediaButton is null ||
            TelegramBackToChatsButton is null) return;

        var width = TelegramWorkspaceGrid.ActualWidth;
        if (width <= 0) width = System.Math.Max(0, ActualWidth - 30);
        var singlePane = width < TelegramSinglePaneBreakpoint;
        var hideSidebar = width < TelegramSidebarBreakpoint;
        var showChats = !singlePane || !_showCompactMedia;
        var showMedia = !singlePane || _showCompactMedia;

        TelegramFolderColumn.Width = new GridLength(hideSidebar ? 0 : 176);
        TelegramFolderGap.Width = new GridLength(hideSidebar ? 0 : 8);
        TelegramFolderSurface.Visibility = hideSidebar ? Visibility.Collapsed : Visibility.Visible;
        TelegramFolderPicker.Visibility = hideSidebar ? Visibility.Visible : Visibility.Collapsed;
        TelegramCompactMenu.Visibility = hideSidebar ? Visibility.Visible : Visibility.Collapsed;

        TelegramChatColumn.Width = showChats
            ? (singlePane ? new GridLength(1, GridUnitType.Star) : new GridLength(306))
            : new GridLength(0);
        TelegramMediaGap.Width = new GridLength(singlePane ? 0 : 8);
        TelegramMediaColumn.Width = showMedia ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TelegramChatSurface.Visibility = showChats ? Visibility.Visible : Visibility.Collapsed;
        TelegramMediaSurface.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
        TelegramShowMediaButton.Visibility = singlePane ? Visibility.Visible : Visibility.Collapsed;
        TelegramBackToChatsButton.Visibility = singlePane ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TelegramFavoriteToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: AltyaziDB.Player.Core.Models.TelegramChatItem chat } &&
            DataContext is MainViewModel vm)
        {
            await vm.Telegram.ToggleFavoriteChatAsync(chat);
            e.Handled = true;
        }
    }

    private async void TelegramFavoriteChat_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: AltyaziDB.Player.Core.Models.TelegramChatItem chat } &&
            DataContext is MainViewModel vm)
            await vm.Telegram.OpenFavoriteChatAsync(chat);
    }

    private void TelegramMediaList_OnSelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // FilteredMedia can hide a selected file without canceling an active
        // stream. Only an actually selected visible row updates SelectedMedia.
        if (e.AddedItems.Count == 0 ||
            sender is not System.Windows.Controls.ListBox { DataContext: TelegramPanelViewModel telegram } ||
            e.AddedItems[0] is not AltyaziDB.Player.Core.Models.TelegramMediaItem media ||
            !telegram.FilteredMedia.Contains(media)) return;

        if (telegram.SelectedMedia?.ChatId != media.ChatId ||
            telegram.SelectedMedia?.MessageId != media.MessageId)
            telegram.SelectedMedia = media;
    }

    private void TelegramChatList_OnSelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // The ListBox SelectedItem binding is OneWay. When a filter hides the
        // currently open chat, its automatic deselection must not change the
        // ViewModel or interrupt the media pane. Only an added visible row is
        // a user-meaningful navigation choice.
        if (e.AddedItems.Count == 0 ||
            sender is not System.Windows.Controls.ListBox { DataContext: TelegramPanelViewModel telegram } ||
            e.AddedItems[0] is not AltyaziDB.Player.Core.Models.TelegramChatItem chat ||
            !telegram.FilteredChats.Contains(chat)) return;

        if (telegram.SelectedChat?.Id != chat.Id)
        {
            telegram.SelectedChat = chat;
            if (TelegramWorkspaceGrid.ActualWidth < TelegramSinglePaneBreakpoint)
            {
                _showCompactMedia = true;
                ApplyTelegramResponsiveLayout();
            }
        }
    }

    private void TelegramView_OnPreviewKeyDown(
        object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.Telegram.IsReady) return;
        if (e.Key == System.Windows.Input.Key.F &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            if (TelegramWorkspaceGrid.ActualWidth < TelegramSinglePaneBreakpoint)
            {
                _showCompactMedia = false;
                ApplyTelegramResponsiveLayout();
            }
            TelegramChatSearch.Focus();
            TelegramChatSearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape &&
                 TelegramChatSearch.IsKeyboardFocusWithin &&
                 !string.IsNullOrWhiteSpace(vm.Telegram.ChatFilterText))
        {
            vm.Telegram.ChatFilterText = string.Empty;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape &&
                 TelegramWorkspaceGrid.ActualWidth < TelegramSinglePaneBreakpoint &&
                 _showCompactMedia)
        {
            _showCompactMedia = false;
            ApplyTelegramResponsiveLayout();
            e.Handled = true;
        }
    }

    private void TelegramPassword_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox input && DataContext is MainViewModel vm)
            vm.Telegram.Password = input.Password;
    }
}