using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private async void History_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && HistoryList.SelectedItem is HistoryItem item)
            await vm.PlayHistoryAsync(item);
    }

    private async void History_PlayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && HistoryList.SelectedItem is HistoryItem item)
            await vm.PlayHistoryAsync(item);
    }
}
