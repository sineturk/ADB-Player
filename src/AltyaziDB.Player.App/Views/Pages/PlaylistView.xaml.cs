using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace AltyaziDB.Player.App.Views.Pages;

/// <summary>
/// R5/P4.3 presentation only: all queue mutations, Cloud sync and playback
/// remain in MainViewModel. Do not copy the queue into an alternate view model.
/// </summary>
public partial class PlaylistView : UserControl
{
    public PlaylistView() => InitializeComponent();

    private void Queue_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!double.IsFinite(e.NewSize.Width) || e.NewSize.Width <= 0)
            return;

        var compact = e.NewSize.Width < 830;

        var shortHeight = e.NewSize.Height < 690;

        QueueHeaderSecondRow.Height = compact
            ? GridLength.Auto : new GridLength(0);
        QueueHeaderActionsColumn.Width = compact
            ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(QueueHeaderRight, compact ? 1 : 0);
        Grid.SetColumn(QueueHeaderRight, compact ? 0 : 1);
        Grid.SetColumnSpan(QueueHeaderRight, compact ? 2 : 1);
        QueueHeaderRight.HorizontalAlignment = compact
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;
        QueueHeaderRight.Margin = compact
            ? new Thickness(0, 9, 0, 0) : new Thickness(0);

        QueueCloudActionsRow.Height = compact
            ? GridLength.Auto : new GridLength(0);
        QueueCloudActionsColumn.Width = compact
            ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(QueueCloudActions, compact ? 1 : 0);
        Grid.SetColumn(QueueCloudActions, compact ? 0 : 1);
        Grid.SetColumnSpan(QueueCloudActions, compact ? 2 : 1);
        QueueCloudActions.HorizontalAlignment = compact
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        QueueCloudActions.Margin = compact
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(10, 0, 0, 0);

        QueueFooterSecondRow.Height = compact
            ? GridLength.Auto : new GridLength(0);
        QueueFooterActionsColumn.Width = compact
            ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(QueueFooterButtons, compact ? 1 : 0);
        Grid.SetColumn(QueueFooterButtons, compact ? 0 : 1);
        Grid.SetColumnSpan(QueueFooterButtons, compact ? 2 : 1);
        QueueFooterButtons.HorizontalAlignment = compact
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        QueueFooterButtons.Margin = compact
            ? new Thickness(0, 10, 0, 0) : new Thickness(0);

        QueueListRegion.MinHeight = shortHeight ? 60 : 140;
    }

    private async void Queue_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<Button>(source) is not null)
            return; // row Play already handles its own click

        if (DataContext is MainViewModel vm && QueueList.SelectedItem is not null)
            await vm.PlaySelectedAsync();
    }

    private async void Queue_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is Button)
            return;

        if (DataContext is MainViewModel vm && QueueList.SelectedItem is not null)
        {
            e.Handled = true;
            await vm.PlaySelectedAsync();
        }
    }

    private async void QueueItemPlay_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm ||
            sender is not Button { DataContext: PlaylistItem item })
            return;

        vm.SelectedPlaylistItem = item;
        await vm.PlaySelectedAsync();
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
