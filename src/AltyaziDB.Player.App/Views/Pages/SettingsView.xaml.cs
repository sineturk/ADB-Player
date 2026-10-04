using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class SettingsView : UserControl
{
    private ApiSettingsPanelViewModel? _settings;
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void SettingsView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachSettings((DataContext as MainViewModel)?.ApiSettings);
    }

    private void SettingsView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachSettings(null);
    }

    private void AttachSettings(ApiSettingsPanelViewModel? settings)
    {
        if (ReferenceEquals(_settings, settings))
        {
            SyncSecretBoxes();
            return;
        }

        if (_settings is not null)
        {
            _settings.PropertyChanged -= Settings_PropertyChanged;
        }

        _settings = settings;

        if (_settings is not null)
        {
            _settings.PropertyChanged += Settings_PropertyChanged;
        }

        SyncSecretBoxes();
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApiSettingsPanelViewModel.TelegramApiHash)
            or nameof(ApiSettingsPanelViewModel.AltyaziDbApiKey)
            or nameof(ApiSettingsPanelViewModel.GoogleClientSecret)
            or nameof(ApiSettingsPanelViewModel.DropboxClientSecret)
            or nameof(ApiSettingsPanelViewModel.OneDriveClientSecret)
            or nameof(ApiSettingsPanelViewModel.PCloudClientSecret))
        {
            if (Dispatcher.CheckAccess()) SyncSecretBoxes();
            else Dispatcher.InvokeAsync(SyncSecretBoxes);
        }
    }

    private void SyncSecretBoxes()
    {
        if (_settings is null) return;

        _syncing = true;
        try
        {
            SetPassword(TelegramApiHashBox, _settings.TelegramApiHash);
            SetPassword(AltyaziDbApiKeyBox, _settings.AltyaziDbApiKey);
            SetPassword(GoogleClientSecretBox, _settings.GoogleClientSecret);
            SetPassword(DropboxClientSecretBox, _settings.DropboxClientSecret);
            SetPassword(OneDriveClientSecretBox, _settings.OneDriveClientSecret);
            SetPassword(PCloudClientSecretBox, _settings.PCloudClientSecret);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SetPassword(PasswordBox box, string value)
    {
        value ??= string.Empty;
        if (!string.Equals(box.Password, value, StringComparison.Ordinal))
        {
            box.Password = value;
        }
    }

    private void TelegramApiHashBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.TelegramApiHash = box.Password;
    }

    private void AltyaziDbApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.AltyaziDbApiKey = box.Password;
    }

    private void GoogleClientSecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.GoogleClientSecret = box.Password;
    }

    private void DropboxClientSecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.DropboxClientSecret = box.Password;
    }

    private void OneDriveClientSecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.OneDriveClientSecret = box.Password;
    }

    private void PCloudClientSecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null && sender is PasswordBox box)
            _settings.PCloudClientSecret = box.Password;
    }
}
