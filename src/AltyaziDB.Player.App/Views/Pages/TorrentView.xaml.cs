using System.ComponentModel;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class TorrentView : UserControl
{

    private TorrentPanelViewModel? _boundTorrent;

    public TorrentView() => InitializeComponent();

    private async void TorrentView_OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        var torrent = DataContext switch
        {
            MainViewModel main => main.Torrent,
            TorrentPanelViewModel panel => panel,
            _ => null
        };
        if (torrent is null) return;

        if (!ReferenceEquals(_boundTorrent, torrent))
        {
            if (_boundTorrent is not null)
                _boundTorrent.PropertyChanged -= Torrent_OnPropertyChanged;
            _boundTorrent = torrent;
            torrent.PropertyChanged += Torrent_OnPropertyChanged;
        }

        // Recheck saved credentials each time the user opens the Torrent page.
        // A successful connection is never assumed solely from saved settings.
        await torrent.RefreshQBittorrentConnectionAsync();
    }

    private void TorrentView_OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_boundTorrent is null) return;
        _boundTorrent.PropertyChanged -= Torrent_OnPropertyChanged;
        _boundTorrent = null;
    }

    private void Torrent_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TorrentPanelViewModel.QBittorrentPasswordInput) &&
            _boundTorrent is { QBittorrentPasswordInput.Length: 0 } &&
            QbPasswordBox.Password.Length > 0)
        {
            // The new password was saved in DPAPI; clear the visible draft.
            QbPasswordBox.Clear();
        }
    }

    private void QbPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.Torrent.QBittorrentPasswordInput = QbPasswordBox.Password;
        else if (DataContext is TorrentPanelViewModel torrent)
            torrent.QBittorrentPasswordInput = QbPasswordBox.Password;
    }
}
