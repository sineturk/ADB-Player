using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using PasswordBox = System.Windows.Controls.PasswordBox;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using AltyaziDB.Player.App;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.Core.Models;
namespace AltyaziDB.Player.App.Views.Pages;
public partial class SourcesView : UserControl
{
    public SourcesView()
    {
        InitializeComponent();
    }

    private RcloneCloudProvider? _pendingProvider;
    private bool? _pendingCredentialsSaved;
    private RcloneCloudProvider _guideProvider = RcloneCloudProvider.GoogleDrive;
    private SourcePanelViewModel? _currentSources;
    public ObservableCollection<SavedCloudAccount> FilteredCloudAccounts { get; } = new();
    public ObservableCollection<SavedPublicLink> FilteredSavedLinks { get; } = new();
    public ObservableCollection<SavedWebDavProfile> FilteredWebDavProfiles { get; } = new();

    private void SourcesView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachSources(Vm?.Sources);
        LocalizationManager.LanguageChanged += OnSourceLanguageChanged;
    }

    private void OnSourceLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                RefreshProviderModalLanguage();
                RefreshCloudGuideLanguage();
            });
            return;
        }
        RefreshProviderModalLanguage();
        RefreshCloudGuideLanguage();
    }

    private void RefreshProviderModalLanguage()
    {
        if (SourcesProviderOverlay.Visibility != Visibility.Visible)
            return;

        if (_pendingProvider is { } provider)
        {
            var name = Vm?.Sources.CloudProviders.FirstOrDefault(p => p.Provider == provider)?.Label;
            SourcesProviderTitle.Text = LocalizationManager.TranslateMessage(name);
            SourcesProviderDescription.Text = LocalizationManager.Get(
                provider == RcloneCloudProvider.GoogleDrive
                    ? "R5164.BYOC.ProviderIntroRequired"
                    : "R5164.BYOC.ProviderIntroRecommended");
            SourcesOAuthCredentialStatus.Text = LocalizationManager.Get(
                _pendingCredentialsSaved switch
                {
                    true => "R5164.BYOC.Stored",
                    false when provider == RcloneCloudProvider.GoogleDrive => "R5164.BYOC.Missing",
                    false => "R5164.BYOC.MissingOptional",
                    _ => "R5164.BYOC.Checking"
                });
        }
        else if (SourcesLinkProviderFields.Visibility == Visibility.Visible)
        {
            SourcesProviderTitle.Text = LocalizationManager.Get("R5.Sources.SharedLink");
            SourcesProviderDescription.Text = LocalizationManager.Get("R5.Sources.SharedLinkHelp");
        }
        else
        {
            SourcesProviderTitle.Text = "WebDAV";
            SourcesProviderDescription.Text = LocalizationManager.Get("R5.Sources.WebDavHelp");
        }
    }

    private void SourcesView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            AttachSources(Vm?.Sources);
    }

    private void SourcesView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        LocalizationManager.LanguageChanged -= OnSourceLanguageChanged;
        AttachSources(null);
    }

    private void AttachSources(SourcePanelViewModel? source)
    {
        if (!ReferenceEquals(_currentSources, source))
        {
            if (_currentSources is not null)
            {
                _currentSources.CloudAccounts.CollectionChanged -= Sources_CollectionChanged;
                _currentSources.SavedPublicLinks.CollectionChanged -= Sources_CollectionChanged;
                _currentSources.WebDavProfiles.CollectionChanged -= Sources_CollectionChanged;
            }

            _currentSources = source;
            if (source is not null)
            {
                source.CloudAccounts.CollectionChanged += Sources_CollectionChanged;
                source.SavedPublicLinks.CollectionChanged += Sources_CollectionChanged;
                source.WebDavProfiles.CollectionChanged += Sources_CollectionChanged;
            }
        }
        RefreshFilteredSources();
    }

    private void Sources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshFilteredSources);
            return;
        }
        RefreshFilteredSources();
        // Only close the link dialog after a genuinely saved link is added.
        // Validation failures keep the dialog open and the draft intact.
        if (e.Action == NotifyCollectionChangedAction.Add &&
            ReferenceEquals(sender, _currentSources?.SavedPublicLinks) &&
            SourcesProviderOverlay.Visibility == Visibility.Visible &&
            SourcesLinkProviderFields.Visibility == Visibility.Visible)
            _ = Dispatcher.BeginInvoke(CloseProviderOverlay, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SourcesSearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshFilteredSources();

    private void RefreshFilteredSources()
    {
        FilteredCloudAccounts.Clear();
        FilteredSavedLinks.Clear();
        FilteredWebDavProfiles.Clear();
        if (_currentSources is not { } source)
            return;

        var query = SourcesSearchBox?.Text?.Trim() ?? string.Empty;
        bool Matches(params string?[] fields) =>
            query.Length == 0 || fields.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        foreach (var account in source.CloudAccounts)
        {
            if (Matches(account.DisplayName, account.ProviderLabel,
                HomeSourcePresentation.StableAccountLabel(account)))
                FilteredCloudAccounts.Add(account);
        }

        foreach (var link in source.SavedPublicLinks)
        {
            if (Matches(link.Name, link.Provider, HomeSourcePresentation.SafeLabel(link.Url, string.Empty)))
                FilteredSavedLinks.Add(link);
        }

        foreach (var profile in source.WebDavProfiles)
        {
            if (Matches(profile.Name, HomeSourcePresentation.SafeLabel(profile.BaseUrl, string.Empty)))
                FilteredWebDavProfiles.Add(profile);
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Saved sources are grouped in two short panels instead of one long page.
    // Search results retain their original source references and never mutate storage.
    private void SelectLinkTabAfterSave()
    {
        SourcesSavedTabs.SelectedIndex = 1;
    }

    private void SourcesView_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || !double.IsFinite(e.NewSize.Width))
            return;

        // Android-like cards: five at desktop width, smaller grids on laptops.
        SourcesProviderCards.Columns = e.NewSize.Width < 670 ? 2 :
            e.NewSize.Width < 1180 ? 3 : 5;
        SourcesOtherCards.Columns = e.NewSize.Width < 670 ? 2 : 3;
    }

    private void SourcesClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SourcesSearchBox.Clear();
        SourcesSearchBox.Focus();
    }


    // OAuth providers do not collect third-party passwords inside ADB Player.
    // Their actual credentials are entered on the provider's secure sign-in page
    // launched by our existing rclone authorization workflow.
    private async void CloudProviderCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string providerName }
            || !Enum.TryParse<RcloneCloudProvider>(providerName, out var provider)
            || Vm is not { } model)
            return;

        var option = model.Sources.CloudProviders.FirstOrDefault(item => item.Provider == provider);
        if (option is null) return;
        model.Sources.SelectedCloudProvider = option;
        _pendingProvider = provider;
        _pendingCredentialsSaved = null;
        SourcesOAuthClientId.Clear();
        SourcesOAuthClientSecret.Clear();
        SourcesProviderTitle.Text = LocalizationManager.TranslateMessage(option.Label);
        SourcesProviderDescription.Text = LocalizationManager.Get(
            provider == RcloneCloudProvider.GoogleDrive
                ? "R5164.BYOC.ProviderIntroRequired"
                : "R5164.BYOC.ProviderIntroRecommended");
        SourcesOAuthProviderFields.Visibility = Visibility.Visible;
        SourcesWebDavProviderFields.Visibility = Visibility.Collapsed;
        SourcesLinkProviderFields.Visibility = Visibility.Collapsed;
        SourcesOAuthCredentialStatus.Text = LocalizationManager.Get("R5164.BYOC.Checking");
        SourcesOAuthExpander.IsExpanded = true;
        SourcesProviderOverlay.Visibility = Visibility.Visible;
        SourcesProviderAccountName.Focus();

        // Both the connection dialog and Settings read the same secure store.
        // Never copy the actual Client Secret into the dialog's PasswordBox.
        var saved = await model.Sources.HasSavedCloudCredentialsAsync(provider).ConfigureAwait(true);
        if (_pendingProvider != provider || SourcesProviderOverlay.Visibility != Visibility.Visible)
            return;
        _pendingCredentialsSaved = saved;
        SourcesOAuthCredentialStatus.Text = LocalizationManager.Get(
            saved ? "R5164.BYOC.Stored"
                  : provider == RcloneCloudProvider.GoogleDrive
                      ? "R5164.BYOC.Missing" : "R5164.BYOC.MissingOptional");
        SourcesOAuthExpander.IsExpanded = !saved;
    }

    private void SourcesOpenCloudGuide_Click(object sender, RoutedEventArgs e)
    {
        _guideProvider = RcloneCloudProvider.GoogleDrive;
        SourcesCloudGuideOverlay.Visibility = Visibility.Visible;
        RefreshCloudGuideLanguage();
    }

    private void SourcesGuideProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse<RcloneCloudProvider>(tag, out var provider))
            return;
        _guideProvider = provider;
        RefreshCloudGuideLanguage();
    }

    private void RefreshCloudGuideLanguage()
    {
        if (SourcesCloudGuideOverlay.Visibility != Visibility.Visible) return;
        var name = _guideProvider switch
        {
            RcloneCloudProvider.GoogleDrive => "GoogleDrive",
            RcloneCloudProvider.Dropbox => "Dropbox",
            RcloneCloudProvider.OneDrive => "OneDrive",
            _ => "PCloud"
        };
        var prefix = $"R5165.Guide.{name}.";
        SourcesGuideProviderTitle.Text = LocalizationManager.Get(prefix + "Title");
        SourcesGuideIntro.Text = LocalizationManager.Get(prefix + "Intro");
        SourcesGuideStep1.Text = LocalizationManager.Get(prefix + "Step1");
        SourcesGuideStep2.Text = LocalizationManager.Get(prefix + "Step2");
        SourcesGuideStep3.Text = LocalizationManager.Get(prefix + "Step3");
        SourcesGuideNote.Text = LocalizationManager.Get(prefix + "Note");
    }

    private void SourcesGuideOpenOfficial_Click(object sender, RoutedEventArgs e)
    {
        var guide = _guideProvider switch
        {
            RcloneCloudProvider.GoogleDrive => "https://rclone.org/drive/#making-your-own-client-id",
            RcloneCloudProvider.Dropbox => "https://rclone.org/dropbox/#get-your-own-dropbox-app-id",
            RcloneCloudProvider.OneDrive => "https://rclone.org/onedrive/#getting-your-own-client-id-and-key",
            RcloneCloudProvider.PCloud or RcloneCloudProvider.PCloudEurope => "https://rclone.org/pcloud/",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(guide)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guide)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            SourcesGuideNote.Text = LocalizationManager.Get("R5164.BYOC.GuideFailed");
        }
    }

    private void SourcesCloseCloudGuide_Click(object sender, RoutedEventArgs e) =>
        SourcesCloudGuideOverlay.Visibility = Visibility.Collapsed;

    private void SourcesCloudGuideOverlay_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        SourcesCloudGuideOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void WebDavProviderCard_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } model) return;
        _pendingProvider = null;
        model.Sources.SelectedProfile = null;
        model.Sources.WebDavName = "WebDAV";
        model.Sources.WebDavUrl = string.Empty;
        model.Sources.WebDavUsername = string.Empty;
        model.Sources.WebDavPassword = string.Empty;
        SourcesWebDavCardPassword.Clear();
        SourcesProviderTitle.Text = "WebDAV";
        SourcesProviderDescription.Text = LocalizationManager.Get("R5.Sources.WebDavHelp");
        SourcesOAuthProviderFields.Visibility = Visibility.Collapsed;
        SourcesWebDavProviderFields.Visibility = Visibility.Visible;
        SourcesLinkProviderFields.Visibility = Visibility.Collapsed;
        SourcesProviderOverlay.Visibility = Visibility.Visible;
    }

    private void SharedLinkProviderCard_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        _pendingProvider = null;
        SourcesProviderTitle.Text = LocalizationManager.Get("R5.Sources.SharedLink");
        SourcesProviderDescription.Text = LocalizationManager.Get("R5.Sources.SharedLinkHelp");
        SourcesOAuthProviderFields.Visibility = Visibility.Collapsed;
        SourcesWebDavProviderFields.Visibility = Visibility.Collapsed;
        SourcesLinkProviderFields.Visibility = Visibility.Visible;
        SourcesProviderOverlay.Visibility = Visibility.Visible;
        SourcesLinkUrlBox.Focus();
    }

    private void TelegramProviderCard_Click(object sender, RoutedEventArgs e)
    {
        // Telegram already owns a dedicated media-center route and login flow.
        // Do not invent a second credential form inside cloud sources.
        if (Vm is { } model)
            model.SelectedSidebarIndex = 5;
    }

    private async void SourcesProviderConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingProvider is not { } provider || Vm is not { } model || model.Sources.IsBusy)
            return;

        if (sender is Button button) button.IsEnabled = false;
        try
        {
            var connected = await model.Sources
                .ConnectCloudProviderAsync(provider, model.Sources.CloudAccountName,
                    SourcesOAuthClientId.Text, SourcesOAuthClientSecret.Password)
                .ConfigureAwait(true);
            if (connected) CloseProviderOverlay();
        }
        finally
        {
            if (sender is Button button2) button2.IsEnabled = true;
        }
    }

    private void SourcesOpenOAuthBrowser_Click(object sender, RoutedEventArgs e)
    {
        Vm?.Sources.OpenOAuthAuthorizationBrowser();
    }

    private void SourcesCopyOAuthLink_Click(object sender, RoutedEventArgs e)
    {
        Vm?.Sources.CopyOAuthAuthorizationLink();
    }

    private void WebDavCardPassword_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && Vm is { } model)
            model.Sources.WebDavPassword = box.Password;
    }

    private async void SourcesWebDavConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } model || model.Sources.IsBusy)
            return;
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            var connected = await model.Sources.ConnectNewWebDavFromCardAsync()
                .ConfigureAwait(true);
            if (connected) CloseProviderOverlay();
        }
        finally
        {
            if (sender is Button button2) button2.IsEnabled = true;
        }
    }

    private void SourcesProviderCancel_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Sources.IsBusy == true)
        {
            // Stop the OAuth subprocess first; do not hide a still-active
            // localhost callback server behind the source list.
            Vm.Sources.CancelCloudAuthorization();
            return;
        }
        CloseProviderOverlay();
    }

    private void SourcesProviderOverlay_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        if (Vm?.Sources.IsBusy == true)
            Vm.Sources.CancelCloudAuthorization();
        else
            CloseProviderOverlay();
        e.Handled = true;
    }

    private void CloseProviderOverlay()
    {
        SourcesProviderOverlay.Visibility = Visibility.Collapsed;
        SourcesWebDavCardPassword.Clear();
        SourcesLinkProviderFields.Visibility = Visibility.Collapsed;
        SourcesOAuthClientSecret.Clear();
        SourcesOAuthClientId.Clear();
        if (Vm is { } model)
        {
            model.Sources.WebDavPassword = string.Empty;
            model.Sources.ClearOAuthAuthorizationLink();
        }
        _pendingProvider = null;
        _pendingCredentialsSaved = null;
    }

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


/// <summary>Friendly stable account identity in the manager; never renders remote secrets.</summary>
public sealed class SavedCloudAccountIdentityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SavedCloudAccount account
            ? HomeSourcePresentation.StableAccountLabel(account)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
