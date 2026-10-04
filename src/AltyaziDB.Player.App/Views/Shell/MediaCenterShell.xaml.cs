using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using AltyaziDB.Player.App.ViewModels;

namespace AltyaziDB.Player.App.Views.Shell;

/// <summary>
/// R2.3 presentation-only navigation. MainViewModel still owns actual routes,
/// playback mode, persisted state, and all media integration commands.
/// </summary>
public partial class MediaCenterShell : UserControl
{
    private MainViewModel? _model;
    private bool _showSources;

    public MediaCenterShell()
    {
        InitializeComponent();
    }

    private void Shell_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachModel(DataContext as MainViewModel);
    }

    private void Shell_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachModel(e.NewValue as MainViewModel);
    }

    private void Shell_OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachModel(null);
    }

    private void AttachModel(MainViewModel? model)
    {
        if (ReferenceEquals(_model, model))
        {
            UpdateCurrentPage();
            return;
        }

        if (_model is not null)
        {
            _model.PropertyChanged -= Model_PropertyChanged;
            _model.Sources.PropertyChanged -= Sources_PropertyChanged;
        }

        _model = model;

        if (_model is not null)
        {
            _model.PropertyChanged += Model_PropertyChanged;
            _model.Sources.PropertyChanged += Sources_PropertyChanged;
        }

        UpdateCurrentPage();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSidebarIndex))
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateCurrentPage();
            }
            else
            {
                Dispatcher.InvokeAsync(UpdateCurrentPage);
            }
        }
    }

    private void Sources_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AltyaziDB.Player.App.ViewModels.SourcePanelViewModel.IsBrowserVisible))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            if (_model?.Sources.IsBrowserVisible == true)
                _showSources = true;
            UpdateCurrentPage();
        }
        else
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_model?.Sources.IsBrowserVisible == true)
                    _showSources = true;
                UpdateCurrentPage();
            });
        }
    }

    public void ShowSources()
    {
        _showSources = true;
        if (_model is not null)
            _model.SelectedSidebarIndex = 0;
        UpdateCurrentPage();
    }

    public void ShowHome()
    {
        _showSources = false;
        if (_model is not null)
        {
            if (_model.Sources.IsBrowserVisible &&
                _model.Sources.ShowConnectionsHomeCommand.CanExecute(null))
            {
                _model.Sources.ShowConnectionsHomeCommand.Execute(null);
            }
            _model.SelectedSidebarIndex = 0;
        }
        UpdateCurrentPage();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button { Tag: string value }
            || !int.TryParse(value, out var target)
            || target < 0 || target > 12)
        {
            return;
        }

        if (target == 0)
        {
            // Clicking Home always returns to the actual Home page, even when
            // the cloud browser was left open in a previous visit.
            ShowHome();
            return;
        }

        // Every media-center route now has its own modern WPF page.
        // The legacy TabControl remains hidden solely for player-side regression.
        _showSources = false;
        _model.SelectedSidebarIndex = target;
        UpdateCurrentPage();
    }

    private void UpdateCurrentPage()
    {
        var index = _model?.SelectedSidebarIndex ?? 0;

        var sourcesOpen = index == 0 && (_showSources || _model?.Sources.IsBrowserVisible == true);
        HomePage.Visibility = index == 0 && !sourcesOpen ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        TelegramPage.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        AddonsPage.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        TorrentPage.Visibility = index == 8 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPage.Visibility = index == 9 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 10 ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = index == 11 ? Visibility.Visible : Visibility.Collapsed;
        CloudListsPage.Visibility = index == 12 ? Visibility.Visible : Visibility.Collapsed;
        SourcesPage.Visibility = sourcesOpen ? Visibility.Visible : Visibility.Collapsed;

        var normal = (Style)FindResource("R23NavButton");
        var selected = (Style)FindResource("R23NavSelectedButton");

        foreach (var item in NavigationButtons.Children)
        {
            if (item is Button button && button.Tag is string tag
                && int.TryParse(tag, out var buttonIndex))
            {
                button.Style = buttonIndex == index ? selected : normal;
            }
        }
    }
}