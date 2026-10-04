using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using PasswordBox = System.Windows.Controls.PasswordBox;
using AltyaziDB.Player.App.ViewModels;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class TelegramView : UserControl
{
    public TelegramView() => InitializeComponent();
    private void TelegramPassword_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox input && DataContext is MainViewModel vm)
            vm.Telegram.Password = input.Password;
    }
}