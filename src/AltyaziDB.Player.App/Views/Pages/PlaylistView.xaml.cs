using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class PlaylistView : UserControl
{
    public PlaylistView() => InitializeComponent();
    private async void Queue_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && QueueList.SelectedItem is not null)
            await vm.PlaySelectedAsync();
    }
}