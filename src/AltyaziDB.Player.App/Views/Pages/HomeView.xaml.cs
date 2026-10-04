using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using AltyaziDB.Player.App;
using AltyaziDB.Player.App.ViewModels;
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
    private const double ResumeCardStep = 280;

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
        if (sender is Button { DataContext: ContinueWatchingItem item } && Model is { } model)
        {
            await model.PlayContinueWatchingAsync(item);
        }
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
        // This panel is a transient quick-action workspace. Returning to Home
        // should start clean and only reveal it after an explicit user click.
        WebLinkPanel.Visibility = Visibility.Collapsed;
    }

    // All cloud setup now uses the new Sources workspace rather than the legacy tab.
    private void OpenSources_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow host)
        {
            host.ShowModernSources();
        }
    }

    private async void OpenCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedCloudAccount account } && Model is { } model)
        {
            await model.Sources.OpenCloudAccountAsync(account);
        }
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedPublicLink link } && Model is { } model)
        {
            await model.Sources.OpenSavedLinkAsync(link);
        }
    }

    private async void OpenWebDav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedWebDavProfile profile } && Model is { } model)
        {
            await model.Sources.OpenWebDavProfileAsync(profile);
        }
    }
}