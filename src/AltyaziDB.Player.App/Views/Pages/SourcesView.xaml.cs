using System.Windows;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using PasswordBox = System.Windows.Controls.PasswordBox;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Models;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class SourcesView : UserControl
{
    public SourcesView() => InitializeComponent();
    private MainViewModel? Vm => DataContext as MainViewModel;
    private void ReturnHome_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.ShowModernHome();
    }
    private void WebDavPassword_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && Vm is { } model)
            model.Sources.WebDavPassword = pb.Password;
    }
    private async void CloudOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedCloudAccount value } && Vm is { } model)
            await model.Sources.OpenCloudAccountAsync(value);
    }
    private async void CloudDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedCloudAccount value } && Vm is { } model)
            await model.Sources.DisconnectCloudAsync(value);
    }
    private async void LinkOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedPublicLink value } && Vm is { } model)
            await model.Sources.OpenSavedLinkAsync(value);
    }
    private async void LinkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedPublicLink value } && Vm is { } model)
            await model.Sources.DeleteSavedLinkAsync(value);
    }
    private async void WebDavOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedWebDavProfile value } && Vm is { } model)
            await model.Sources.OpenWebDavProfileAsync(value);
    }
    private async void WebDavRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedWebDavProfile value } && Vm is { } model)
            await model.Sources.DeleteWebDavProfileAsync(value);
    }
    private async void Browser_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } model && SourceBrowserItems.SelectedItem is not null)
            await model.Sources.OpenSelectedAsync();
    }
}
