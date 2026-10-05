using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;
using AltyaziDB.Player.Telegram;
using AltyaziDB.Player.Telegram.Generated;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class TelegramPanelViewModel : ObservableObject, IAsyncDisposable
{
    private const string DatabaseKeySecretKey = "telegram:database_key";
    private const string ApiHashSecretKey = ApiSettingsPanelViewModel.TelegramApiHashSecretKey;
    private readonly ITelegramService _service;
    private readonly ISecretStore _secrets;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<RemoteOpenRequest, Task> _openSource;
    private readonly Func<RemoteOpenRequest, Task> _attachAudio;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly Task _loadSecretsTask;
    private string _databaseEncryptionKey = string.Empty;
    private int _telegramApiId;
    private string _telegramApiHash = string.Empty;
    private string _phoneNumber = string.Empty;
    private string _emailAddress = string.Empty;
    private string _code = string.Empty;
    private string _password = string.Empty;
    private string _firstName = string.Empty;
    private string _lastName = string.Empty;
    private string _searchText = string.Empty;
    private string _statusText = "Telegram hazırlanıyor…";
    private TelegramAuthorizationSnapshot _authorization = TelegramAuthorizationSnapshot.NotStarted;
    private TelegramChatItem? _selectedChat;
    private TelegramChatFolderItem? _selectedFolder;
    private TelegramForumTopicItem? _selectedTopic;
    private TelegramMediaItem? _selectedMedia;
    private bool _isBusy;
    private bool _submitPhoneWhenRequested;
    private bool _phoneSubmissionInProgress;
    private bool _serviceStarted;
    private bool _suppressFolderReload;
    private bool _suppressChatReload;
    private bool _suppressTopicReload;
    private double _downloadPercent;
    private bool _isAudioSelectionMode;

    public TelegramPanelViewModel(
        ITelegramService service,
        ISecretStore secrets,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<RemoteOpenRequest, Task> openSource,
        Func<RemoteOpenRequest, Task> attachAudio,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _service = service;
        _secrets = secrets;
        _settings = settings;
        _saveSettings = saveSettings;
        _openSource = openSource;
        _attachAudio = attachAudio;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _phoneNumber = settings.TelegramPhoneNumber;
        Chats = new ObservableCollection<TelegramChatItem>();
        ChatFolders = new ObservableCollection<TelegramChatFolderItem>();
        ForumTopics = new ObservableCollection<TelegramForumTopicItem>();
        Media = new ObservableCollection<TelegramMediaItem>();
        StartCommand = new AsyncRelayCommand(StartAsync);
        SubmitAuthorizationCommand = new AsyncRelayCommand(SubmitAuthorizationAsync);
        LoadChatsCommand = new AsyncRelayCommand(LoadChatsAsync);
        LoadMediaCommand = new AsyncRelayCommand(LoadMediaAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        GlobalSearchCommand = new AsyncRelayCommand(GlobalSearchAsync);
        ShowAllTopicsCommand = new AsyncRelayCommand(ShowAllTopicsAsync);
        DownloadAndOpenCommand = new AsyncRelayCommand(DownloadAndOpenAsync);
        RepairArchivePartsCommand = new AsyncRelayCommand(RepairArchivePartsAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        OpenTelegramFolderCommand = new RelayCommand(() => OpenFolder(_paths.TelegramFilesDirectory));
        _service.AuthorizationChanged += OnAuthorizationChanged;
        _service.DownloadProgressChanged += OnDownloadProgressChanged;
        _service.ChatFoldersChanged += OnChatFoldersChanged;
        _logger.Info($"TDLib durumu: {_service.NativeDiagnostic}");
        StatusText = AvailabilityText;
        _loadSecretsTask = LoadSecretAsync();
        _ = RestoreSavedSessionAsync();
    }

    public ObservableCollection<TelegramChatItem> Chats { get; }
    public ObservableCollection<TelegramChatFolderItem> ChatFolders { get; }
    public ObservableCollection<TelegramForumTopicItem> ForumTopics { get; }
    public ObservableCollection<TelegramMediaItem> Media { get; }
    public ICommand StartCommand { get; }
    public ICommand SubmitAuthorizationCommand { get; }
    public ICommand LoadChatsCommand { get; }
    public ICommand LoadMediaCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand GlobalSearchCommand { get; }
    public ICommand ShowAllTopicsCommand { get; }
    public ICommand DownloadAndOpenCommand { get; }
    public ICommand RepairArchivePartsCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand OpenTelegramFolderCommand { get; }
    public string PhoneNumber { get => _phoneNumber; set => SetProperty(ref _phoneNumber, value); }
    public string EmailAddress { get => _emailAddress; set => SetProperty(ref _emailAddress, value); }
    public string Code { get => _code; set => SetProperty(ref _code, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string FirstName { get => _firstName; set => SetProperty(ref _firstName, value); }
    public string LastName { get => _lastName; set => SetProperty(ref _lastName, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public TelegramAuthorizationSnapshot Authorization
    {
        get => _authorization;
        private set
        {
            if (!SetProperty(ref _authorization, value)) return;
            OnPropertyChanged(nameof(AuthorizationLabel));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsPhoneEntryVisible));
            OnPropertyChanged(nameof(IsEmailEntryVisible));
            OnPropertyChanged(nameof(IsCodeEntryVisible));
            OnPropertyChanged(nameof(IsPasswordEntryVisible));
            OnPropertyChanged(nameof(IsRegistrationEntryVisible));
            OnPropertyChanged(nameof(IsOtherConfirmationVisible));
            OnPropertyChanged(nameof(IsTelegramContentVisible));
            OnPropertyChanged(nameof(IsLoginPanelVisible));
        }
    }
    public string AuthorizationLabel => Authorization.Message;
    public bool IsReady => Authorization.IsReady;
    public bool IsPhoneEntryVisible => Authorization.Stage is
        TelegramAuthorizationStage.NotStarted or TelegramAuthorizationStage.Starting or
        TelegramAuthorizationStage.WaitingForParameters or TelegramAuthorizationStage.WaitingForPhoneNumber or
        TelegramAuthorizationStage.Closed or TelegramAuthorizationStage.Error;
    public bool IsEmailEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForEmailAddress;
    public bool IsCodeEntryVisible => Authorization.Stage is TelegramAuthorizationStage.WaitingForCode or TelegramAuthorizationStage.WaitingForEmailCode;
    public bool IsPasswordEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForPassword;
    public bool IsRegistrationEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForRegistration;
    public bool IsOtherConfirmationVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForOtherDeviceConfirmation;
    public bool IsTelegramContentVisible => Authorization.IsReady;
    public bool IsLoginPanelVisible => !Authorization.IsReady;
    public bool NativeAvailable => _service.IsNativeAvailable;
    public string AvailabilityText => LocalizationManager.Get(NativeAvailable && CredentialsConfigured ? "Panel.TelegramReady" : "Panel.TelegramUnavailable");
    public string NativeDiagnostic => _service.NativeDiagnostic;
    public bool CredentialsConfigured => _telegramApiId > 0 && !string.IsNullOrWhiteSpace(_telegramApiHash);
    public string CredentialDiagnostic => CredentialsConfigured
        ? LocalizationManager.Get("Settings.Telegram.Configured")
        : LocalizationManager.Get("Settings.Telegram.Missing");
    public TelegramChatFolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value) || !IsReady || _suppressFolderReload) return;
            SearchText = string.Empty;
            _ = LoadChatsAsync();
        }
    }
    public TelegramChatItem? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (!SetProperty(ref _selectedChat, value)) return;
            SearchText = string.Empty;
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));
            if (!IsReady || _suppressChatReload) return;
            _ = LoadChatContextAsync();
        }
    }

    public TelegramForumTopicItem? SelectedTopic
    {
        get => _selectedTopic;
        set
        {
            if (!SetProperty(ref _selectedTopic, value)) return;
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));
            if (!IsReady || _suppressTopicReload || SelectedChat is null) return;
            _ = LoadMediaAsync();
        }
    }

    public bool HasForumTopics => ForumTopics.Count > 0;
    public string CurrentContextTitle => SelectedTopic?.Name ?? SelectedChat?.Title ?? LocalizationManager.Get("Telegram.Media.Title");
    public string CurrentContextPath
    {
        get
        {
            var folder = SelectedFolder?.Name ?? LocalizationManager.Get("Telegram.AllChats");
            if (SelectedChat is null) return folder;
            return SelectedTopic is null
                ? $"{folder}  ›  {SelectedChat.Title}"
                : $"{folder}  ›  {SelectedChat.Title}  ›  {SelectedTopic.Name}";
        }
    }
    public TelegramMediaItem? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (!SetProperty(ref _selectedMedia, value)) return;
            OnPropertyChanged(nameof(IsArchiveSelected));
        }
    }
    public bool IsArchiveSelected => SelectedMedia?.IsArchiveSet == true;
    public bool IsAudioSelectionMode
    {
        get => _isAudioSelectionMode;
        private set
        {
            if (!SetProperty(ref _isAudioSelectionMode, value)) return;
            OnPropertyChanged(nameof(OpenActionText));
        }
    }
    public string OpenActionText => LocalizationManager.Get(IsAudioSelectionMode ? "Player.AudioSelection.Attach" : "Action.Open");
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public double DownloadPercent { get => _downloadPercent; private set => SetProperty(ref _downloadPercent, value); }

    public void BeginAudioSelection()
    {
        IsAudioSelectionMode = true;
        StatusText = IsReady
            ? "Telegram'dan bir ses dosyası veya ses içeren arşiv seçin."
            : "Telegram oturumunuz açıldığında ses dosyalarınızı seçebilirsiniz.";
    }

    public void EndAudioSelection() => IsAudioSelectionMode = false;

    private async Task LoadSecretAsync()
    {
        try
        {
            _databaseEncryptionKey = await _secrets.GetAsync(DatabaseKeySecretKey).ConfigureAwait(true) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_databaseEncryptionKey))
            {
                _databaseEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await _secrets.SetAsync(DatabaseKeySecretKey, _databaseEncryptionKey).ConfigureAwait(true);
            }

            await ReloadCredentialsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram güvenli ayarları yüklenemedi.", exception);
            StatusText = "Telegram güvenli ayarları yüklenemedi.";
        }
    }

    public async Task ReloadCredentialsAsync()
    {
        var storedHash = await _secrets.GetAsync(ApiHashSecretKey).ConfigureAwait(true);
        var hasUserOverride = _settings.TelegramApiId > 0 || !string.IsNullOrWhiteSpace(storedHash);

        if (hasUserOverride)
        {
            // Treat the Telegram application credential pair as atomic. Mixing a
            // user API ID with a packaged API hash (or vice versa) produces a
            // misleading TDLib authentication failure.
            _telegramApiId = _settings.TelegramApiId;
            _telegramApiHash = storedHash ?? string.Empty;
        }
        else
        {
            _telegramApiId = GeneratedTelegramCredentials.ApiId;
            _telegramApiHash = GeneratedTelegramCredentials.ApiHash;
        }

        OnPropertyChanged(nameof(CredentialsConfigured));
        OnPropertyChanged(nameof(CredentialDiagnostic));
        OnPropertyChanged(nameof(AvailabilityText));

        StatusText = _serviceStarted
            ? LocalizationManager.Get("Settings.Telegram.RestartHint")
            : AvailabilityText;
    }

    private async Task RestoreSavedSessionAsync()
    {
        await _loadSecretsTask.ConfigureAwait(true);
        if (!NativeAvailable || !CredentialsConfigured || !HasStoredSession())
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Telegram oturumu açılıyor…";
            await EnsureServiceStartedAsync(submitSavedPhone: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram kayıtlı oturumu açılamadı: {exception.Message}");
            StatusText = AvailabilityText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasStoredSession()
    {
        try
        {
            return Directory.Exists(_paths.TelegramDatabaseDirectory) &&
                   Directory.EnumerateFileSystemEntries(_paths.TelegramDatabaseDirectory).Any();
        }
        catch
        {
            return false;
        }
    }

    private async Task StartAsync()
    {
        await _loadSecretsTask.ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(PhoneNumber))
        {
            StatusText = "Telefon numaranızı ülke koduyla girin.";
            return;
        }

        if (!CredentialsConfigured)
        {
            StatusText = LocalizationManager.Get("Settings.Telegram.Missing");
            _logger.Warning("Telegram uygulama kimliği eksik.");
            _dialogs.ShowError(LocalizationManager.Get("Settings.Telegram.MissingHelp"));
            return;
        }

        try
        {
            IsBusy = true;
            _settings.TelegramPhoneNumber = PhoneNumber.Trim();
            await _saveSettings().ConfigureAwait(true);
            await EnsureServiceStartedAsync(submitSavedPhone: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram başlatılamadı.", exception);
            StatusText = "Telegram başlatılamadı.";
            _dialogs.ShowError($"Telegram başlatılamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureServiceStartedAsync(bool submitSavedPhone)
    {
        _submitPhoneWhenRequested = submitSavedPhone;
        if (!_serviceStarted)
        {
            _serviceStarted = true;
            try
            {
                await _service.StartAsync(new TelegramClientConfiguration(
                    _telegramApiId,
                    _telegramApiHash,
                    _paths.TelegramDatabaseDirectory,
                    _paths.TelegramFilesDirectory,
                    _databaseEncryptionKey)).ConfigureAwait(true);
            }
            catch
            {
                _serviceStarted = false;
                throw;
            }
        }

        Authorization = _service.Authorization;
        StatusText = Authorization.Message;
        await SubmitPendingPhoneAsync().ConfigureAwait(true);
    }

    private async Task SubmitAuthorizationAsync()
    {
        try
        {
            IsBusy = true;
            switch (Authorization.Stage)
            {
                case TelegramAuthorizationStage.WaitingForPhoneNumber:
                    if (string.IsNullOrWhiteSpace(PhoneNumber)) throw new InvalidOperationException("Telefon numarası girin.");
                    _settings.TelegramPhoneNumber = PhoneNumber.Trim();
                    await _saveSettings().ConfigureAwait(true);
                    await _service.SubmitPhoneNumberAsync(PhoneNumber).ConfigureAwait(true);
                    break;
                case TelegramAuthorizationStage.WaitingForEmailAddress:
                    await _service.SubmitEmailAddressAsync(EmailAddress).ConfigureAwait(true);
                    break;
                case TelegramAuthorizationStage.WaitingForEmailCode:
                    await _service.SubmitEmailCodeAsync(Code).ConfigureAwait(true);
                    Code = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForCode:
                    await _service.SubmitCodeAsync(Code).ConfigureAwait(true);
                    Code = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForPassword:
                    await _service.SubmitPasswordAsync(Password).ConfigureAwait(true);
                    Password = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForRegistration:
                    if (string.IsNullOrWhiteSpace(FirstName)) throw new InvalidOperationException("Ad alanı gereklidir.");
                    await _service.RegisterUserAsync(FirstName, LastName).ConfigureAwait(true);
                    break;
                default:
                    StatusText = "Şu anda gönderilecek bir Telegram doğrulama bilgisi yok.";
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram doğrulama bilgisi gönderilemedi.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Telegram doğrulaması başarısız.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task LoadChatsAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingChats");
            var chats = await _service.GetChatsAsync(
                chatFolderId: SelectedFolder?.Id).ConfigureAwait(true);

            var previousChatId = SelectedChat?.Id;
            Chats.Clear();
            foreach (var chat in chats) Chats.Add(chat);

            var next = previousChatId is > 0
                ? Chats.FirstOrDefault(item => item.Id == previousChatId)
                : null;

            try
            {
                _suppressChatReload = true;
                SelectedChat = next ?? Chats.FirstOrDefault();
            }
            finally
            {
                _suppressChatReload = false;
            }

            if (SelectedChat is null)
            {
                ForumTopics.Clear();
                Media.Clear();
                OnPropertyChanged(nameof(HasForumTopics));
                OnPropertyChanged(nameof(CurrentContextTitle));
                OnPropertyChanged(nameof(CurrentContextPath));
                StatusText = LocalizationManager.Get("Telegram.Status.NoChats");
            }
            else
            {
                StatusText = string.Format(
                    LocalizationManager.Get("Telegram.Status.ChatCount"),
                    Chats.Count,
                    SelectedFolder?.Name ?? LocalizationManager.Get("Telegram.AllChats"));
                await LoadChatContextAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbetleri alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadChatContextAsync()
    {
        if (SelectedChat is null)
        {
            ForumTopics.Clear();
            Media.Clear();
            OnPropertyChanged(nameof(HasForumTopics));
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingChat");

            var topics = await _service.GetForumTopicsAsync(SelectedChat.Id).ConfigureAwait(true);
            try
            {
                _suppressTopicReload = true;
                ForumTopics.Clear();
                foreach (var topic in topics) ForumTopics.Add(topic);
                SelectedTopic = null;
            }
            finally
            {
                _suppressTopicReload = false;
            }

            OnPropertyChanged(nameof(HasForumTopics));
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));

            var media = await _service.GetChatMediaAsync(
                SelectedChat.Id,
                query: string.Empty,
                forumTopicId: null).ConfigureAwait(true);
            ReplaceMedia(media);

            StatusText = topics.Count > 0
                ? string.Format(LocalizationManager.Get("Telegram.Status.TopicsAndMedia"), topics.Count, media.Count)
                : string.Format(LocalizationManager.Get("Telegram.Status.MediaCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbet içeriği alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadMediaAsync()
    {
        if (SelectedChat is null)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SelectChat");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingMedia");
            var media = await _service.GetChatMediaAsync(
                SelectedChat.Id,
                query: string.Empty,
                forumTopicId: SelectedTopic?.Id).ConfigureAwait(true);
            ReplaceMedia(media);
            StatusText = string.Format(LocalizationManager.Get("Telegram.Status.MediaCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram medyası alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task SearchAsync()
    {
        if (SelectedChat is null)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SelectChat");
            return;
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            await LoadMediaAsync().ConfigureAwait(true);
            return;
        }

        if (query.Length < 2)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SearchMin");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.SearchingCurrent");
            var media = await _service.GetChatMediaAsync(
                SelectedChat.Id,
                query,
                forumTopicId: SelectedTopic?.Id).ConfigureAwait(true);
            ReplaceMedia(media);
            StatusText = string.Format(LocalizationManager.Get("Telegram.Status.SearchCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbet araması başarısız.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task GlobalSearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length < 2)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SearchMin");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.SearchingAll");
            var media = await _service.SearchMediaAsync(query).ConfigureAwait(true);
            ReplaceMedia(media);
            StatusText = string.Format(LocalizationManager.Get("Telegram.Status.GlobalSearchCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram genel araması başarısız.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task ShowAllTopicsAsync()
    {
        if (SelectedChat is null) return;
        try
        {
            _suppressTopicReload = true;
            SelectedTopic = null;
        }
        finally
        {
            _suppressTopicReload = false;
        }
        OnPropertyChanged(nameof(CurrentContextTitle));
        OnPropertyChanged(nameof(CurrentContextPath));
        await LoadMediaAsync().ConfigureAwait(true);
    }

    private async Task DownloadAndOpenAsync()
    {
        if (SelectedMedia is null) { StatusText = "Önce bir Telegram medya dosyası seçin."; return; }
        try
        {
            IsBusy = true;
            DownloadPercent = 0;
            if (SelectedMedia.IsArchiveSet)
            {
                await OpenArchiveAsync(SelectedMedia).ConfigureAwait(true);
                return;
            }
            if (SelectedMedia.Kind == TelegramMediaKind.ArchivePart)
                throw new InvalidOperationException("Bu dosya bir arşiv parçası. Aynı sete ait tüm parçaları yükleyip yenileyin.");
            if (IsAudioSelectionMode && SelectedMedia.Kind != TelegramMediaKind.Audio)
                throw new InvalidOperationException("Harici ses için bir ses dosyası veya ses içeren arşiv seçin.");
            StatusText = $"Telegram akışı açılıyor: {SelectedMedia.FileName}";
            var request = await _service.CreateStreamRequestAsync(SelectedMedia).ConfigureAwait(true);
            if (SelectedMedia.Kind == TelegramMediaKind.Audio)
            {
                await _attachAudio(request).ConfigureAwait(true);
                IsAudioSelectionMode = false;
                StatusText = $"Telegram harici sesi eklendi: {SelectedMedia.FileName}";
            }
            else
            {
                await _openSource(request).ConfigureAwait(true);
                StatusText = $"Telegram akışı oynatılıyor: {SelectedMedia.FileName}";
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram akışı açılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError(exception.Message, "Telegram akışı açılamadı");
        }
        finally { IsBusy = false; }
    }

    private async Task OpenArchiveAsync(TelegramMediaItem archive)
    {
        if (archive.NeedsArchiveRepair)
            throw new InvalidOperationException("Arşiv parçaları eksik veya belirsiz. Önce Parçaları düzenle seçeneğini kullanın.");

        StatusText = $"Arşiv yapısı denetleniyor: {archive.DisplayTitle}";
        var inspection = await _service.InspectArchiveAsync(archive).ConfigureAwait(true);
        if (!inspection.IsComplete) throw new InvalidOperationException(inspection.StatusMessage);
        var availableEntries = IsAudioSelectionMode
            ? inspection.Entries.Where(entry => IsAudioFile(entry.Name)).ToArray()
            : inspection.Entries.ToArray();
        if (availableEntries.Length == 0)
            throw new InvalidOperationException(IsAudioSelectionMode
                ? "Arşivde desteklenen bir ses dosyası bulunamadı."
                : inspection.StatusMessage);

        var entryIndex = 0;
        if (availableEntries.Length > 1)
        {
            var selected = _dialogs.SelectItems(
                IsAudioSelectionMode ? "Arşivden ses dosyası seçin" : LocalizationManager.Get("Dialog.ArchiveMediaTitle"),
                availableEntries.Select(entry => entry.DisplayLabel).ToArray());
            if (selected.Count == 0)
            {
                StatusText = "Arşiv medya seçimi iptal edildi.";
                return;
            }
            entryIndex = selected[0];
        }

        var entry = availableEntries[entryIndex];
        if (!entry.CanInstantStream)
            throw new InvalidOperationException("Arşivdeki medya şifreli. Şifreli arşiv akışı henüz desteklenmiyor.");

        StatusText = entry.UsesDirectRange
            ? $"Arşiv akışı açılıyor: {entry.Name}"
            : $"Arşiv çözülüyor ve oynatma hazırlanıyor: {entry.Name}";
        var request = await _service.CreateArchiveStreamRequestAsync(archive, entry).ConfigureAwait(true);
        if (IsAudioFile(entry.Name))
        {
            await _attachAudio(request).ConfigureAwait(true);
            IsAudioSelectionMode = false;
            StatusText = $"Arşivden harici ses eklendi: {entry.Name}";
        }
        else
        {
            await _openSource(request).ConfigureAwait(true);
            StatusText = $"Arşivden oynatılıyor: {entry.Name}";
        }
    }

    private Task RepairArchivePartsAsync()
    {
        if (SelectedMedia is not { IsArchiveSet: true } current)
        {
            StatusText = "Önce çok parçalı bir arşiv seçin.";
            return Task.CompletedTask;
        }

        try
        {
            var candidates = Media
                .SelectMany(item => item.ArchiveParts is { Count: > 0 } parts
                    ? parts.AsEnumerable()
                    : item.Kind == TelegramMediaKind.ArchivePart
                        ? new[] { item }
                        : Enumerable.Empty<TelegramMediaItem>())
                .Where(item => item.ChatId == current.ChatId)
                .DistinctBy(item => item.FileId)
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedIds = (current.ArchiveParts ?? []).Select(item => item.FileId).ToHashSet();
            var initial = candidates
                .Select((item, index) => new { item.FileId, Index = index })
                .Where(item => selectedIds.Contains(item.FileId))
                .Select(item => item.Index)
                .ToArray();
            var selected = _dialogs.SelectItems(
                LocalizationManager.Get("Dialog.ArchivePartsTitle"),
                candidates.Select(item => $"{item.FileName} · {item.MetaLabel}").ToArray(),
                initial,
                allowMultiple: true);
            if (selected.Count == 0) return Task.CompletedTask;

            var rebuilt = TelegramArchiveSetDetector.BuildManualSet(selected.Select(index => candidates[index]).ToArray());
            var position = Media.IndexOf(current);
            if (position >= 0) Media[position] = rebuilt;
            else Media.Insert(0, rebuilt);
            SelectedMedia = rebuilt;
            StatusText = rebuilt.NeedsArchiveRepair
                ? "Seçilen parçalarda eksik sıra veya çakışma var."
                : $"{rebuilt.ArchiveParts?.Count ?? 0} arşiv parçası doğrulandı.";
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram arşiv parçaları düzenlenemedi.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Arşiv parçaları düzenlenemedi.\n\n{exception.Message}");
        }
        return Task.CompletedTask;
    }

    private static bool IsAudioFile(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is
        ".mka" or ".mp3" or ".flac" or ".aac" or ".ac3" or ".eac3" or ".dts" or
        ".dtshd" or ".thd" or ".truehd" or ".m4a" or ".opus" or ".ogg" or ".wav";

    private async Task LogoutAsync()
    {
        if (!_dialogs.Confirm("Telegram oturumunu kapatmak istediğinizden emin misiniz?")) return;
        try { await _service.SignOutAsync().ConfigureAwait(true); StatusText = "Telegram oturumu kapatılıyor…"; }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    private void ReplaceMedia(IReadOnlyList<TelegramMediaItem> media)
    {
        Media.Clear();
        foreach (var item in media) Media.Add(item);
        SelectedMedia = Media.FirstOrDefault();
    }

    private void OnAuthorizationChanged(object? sender, TelegramAuthorizationSnapshot snapshot) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wasReady = Authorization.IsReady;
                Authorization = snapshot;
                StatusText = snapshot.Message;
                await SubmitPendingPhoneAsync().ConfigureAwait(true);
                if (snapshot.IsReady && !wasReady)
                {
                    ApplyChatFolders(_service.ChatFolders);
                    await LoadChatsAsync().ConfigureAwait(true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("Telegram giriş adımı tamamlanamadı.", exception);
                StatusText = exception.Message;
            }
        });

    private async Task SubmitPendingPhoneAsync()
    {
        if (!_submitPhoneWhenRequested || _phoneSubmissionInProgress ||
            Authorization.Stage != TelegramAuthorizationStage.WaitingForPhoneNumber) return;
        _phoneSubmissionInProgress = true;
        _submitPhoneWhenRequested = false;
        try
        {
            StatusText = "Telefon numarası Telegram'a gönderiliyor…";
            await _service.SubmitPhoneNumberAsync(PhoneNumber.Trim()).ConfigureAwait(true);
        }
        finally
        {
            _phoneSubmissionInProgress = false;
        }
    }

    private void OnChatFoldersChanged(object? sender, IReadOnlyList<TelegramChatFolderItem> folders) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            ApplyChatFolders(folders);
            if (IsReady) await LoadChatsAsync().ConfigureAwait(true);
        });

    private void ApplyChatFolders(IReadOnlyList<TelegramChatFolderItem> folders)
    {
        var selectedId = SelectedFolder?.Id;
        ChatFolders.Clear();
        foreach (var folder in folders)
        {
            var displayName = folder.Name switch
            {
                "Telegram.AllChats" => LocalizationManager.Get("Telegram.AllChats"),
                "Telegram.Archive" => LocalizationManager.Get("Telegram.Archive"),
                _ => folder.Name
            };
            ChatFolders.Add(folder with { Name = displayName });
        }
        if (ChatFolders.Count == 0)
        {
            ChatFolders.Add(new TelegramChatFolderItem(null, LocalizationManager.Get("Telegram.AllChats")));
            ChatFolders.Add(new TelegramChatFolderItem(-1, LocalizationManager.Get("Telegram.Archive"), int.MaxValue));
        }
        try
        {
            _suppressFolderReload = true;
            SelectedFolder = ChatFolders.FirstOrDefault(item => item.Id == selectedId) ?? ChatFolders.FirstOrDefault();
        }
        finally
        {
            _suppressFolderReload = false;
        }
    }

    private void OnDownloadProgressChanged(object? sender, TelegramDownloadProgress progress) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (SelectedMedia?.FileId != progress.FileId &&
                SelectedMedia?.ArchiveParts?.Any(part => part.FileId == progress.FileId) != true) return;
            DownloadPercent = progress.Percent;
            StatusText = progress.IsCompleted ? "Telegram indirmesi tamamlandı." : $"Telegram indiriliyor: %{progress.Percent:0.0}";
        });

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(AuthorizationLabel));
        OnPropertyChanged(nameof(CredentialDiagnostic));
        OnPropertyChanged(nameof(CurrentContextTitle));
        OnPropertyChanged(nameof(CurrentContextPath));
        ApplyChatFolders(_service.ChatFolders);
    }

    public async ValueTask DisposeAsync()
    {
        _service.AuthorizationChanged -= OnAuthorizationChanged;
        _service.DownloadProgressChanged -= OnDownloadProgressChanged;
        _service.ChatFoldersChanged -= OnChatFoldersChanged;
        await _service.DisposeAsync().ConfigureAwait(false);
    }
}
