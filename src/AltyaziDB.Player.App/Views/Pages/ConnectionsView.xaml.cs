using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using PasswordBox = System.Windows.Controls.PasswordBox;
using AltyaziDB.Player.App.ViewModels;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();
    private void ApiKey_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox input && DataContext is MainViewModel vm)
            vm.Connections.ApiKey = input.Password;
    }
}