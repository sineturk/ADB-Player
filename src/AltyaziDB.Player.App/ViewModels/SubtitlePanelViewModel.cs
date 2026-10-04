using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Infrastructure;
using AltyaziDB.Player.Subtitles.Generated;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class SubtitlePanelViewModel : ObservableObject, IDisposable
{
    private const string ApiKeySecretName = "altyazidb:api-key";
    private readonly ISubtitleService _service;
    private readonly ISecretStore _secretStore;
    private readonly ReleaseParser _parser;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Action<string> _attachSubtitle;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private MediaIdentity _identity = new(string.Empty, "movie", string.Empty);
    private string? _currentSource;
    private double _durationSeconds;
    private string _title = string.Empty;
    private string _year = string.Empty;
    private string _season = string.Empty;
    private string _episode = string.Empty;
    private string _apiKey = string.Empty;
    private string _versionGroup = string.Empty;
    private string _sort = "date";
    private string _statusText = "Video açıldığında dosya adı otomatik çözümlenir.";
    private string _apiStatusText = "API hesabı henüz doğrulanmadı.";
    private string _candidateText = string.Empty;
    private SubtitleMediaCandidate? _candidate;
    private SubtitleSearchItem? _selectedResult;
    private bool _isBusy;

    public SubtitlePanelViewModel(
        ISubtitleService service,
        ISecretStore secretStore,
        ReleaseParser parser,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Action<string> attachSubtitle,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _service = service;
        _secretStore = secretStore;
        _parser = parser;
        _settings = settings;
        _saveSettings = saveSettings;
        _attachSubtitle = attachSubtitle;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;

        Results = new ObservableCollection<SubtitleSearchItem>();
        VersionGroups = new ObservableCollection<string> { string.Empty };
        SortOptions = new Dictionary<string, string>
        {
            ["date"] = "En yeni",
            ["downloads"] = "En çok indirilen"
        };

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        TestApiCommand = new AsyncRelayCommand(TestApiAsync);
        DownloadAndAttachCommand = new AsyncRelayCommand(DownloadAndAttachSelectedAsync);
        ApplyCandidateCommand = new RelayCommand(ApplyCandidate);
        OpenSubtitleFolderCommand = new RelayCommand(OpenSubtitleFolder);

        if (string.IsNullOrWhiteSpace(_settings.AltyaziDbApiUrl))
        {
            _settings.AltyaziDbApiUrl = GeneratedAltyaziDbCredentials.ApiUrl;
        }
        InitializeApiKeyAsync().GetAwaiter().GetResult();
    }

    public ObservableCollection<SubtitleSearchItem> Results { get; }
    public ObservableCollection<string> VersionGroups { get; }
    public IReadOnlyDictionary<string, string> SortOptions { get; }
    public ICommand SearchCommand { get; }
    public ICommand TestApiCommand { get; }
    public ICommand DownloadAndAttachCommand { get; }
    public ICommand ApplyCandidateCommand { get; }
    public ICommand OpenSubtitleFolderCommand { get; }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Year { get => _year; set => SetProperty(ref _year, value); }
    public string Season { get => _season; set => SetProperty(ref _season, value); }
    public string Episode { get => _episode; set => SetProperty(ref _episode, value); }
    public string ApiUrl { get => _settings.AltyaziDbApiUrl; set { if (_settings.AltyaziDbApiUrl != value) { _settings.AltyaziDbApiUrl = value; OnPropertyChanged(); } } }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string VersionGroup { get => _versionGroup; set => SetProperty(ref _versionGroup, value); }
    public string Sort { get => _sort; set => SetProperty(ref _sort, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ApiStatusText { get => _apiStatusText; private set => SetProperty(ref _apiStatusText, value); }
    public string CandidateText { get => _candidateText; private set => SetProperty(ref _candidateText, value); }
    public bool HasCandidate => _candidate is not null;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public SubtitleSearchItem? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }
    public string ResultSummary => Results.Count == 0 ? "Sonuç yok" : $"{Results.Count} altyazı";

    public void UpdateCurrentMedia(string? source, double durationSeconds)
    {
        _durationSeconds = Math.Max(0, durationSeconds);
        if (string.IsNullOrWhiteSpace(source)) return;
        if (string.Equals(_currentSource, source, StringComparison.OrdinalIgnoreCase)) return;
        _currentSource = source;
        var releaseName = File.Exists(source)
            ? Path.GetFileName(source)
            : Uri.TryCreate(source, UriKind.Absolute, out var uri)
                ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))
                : source;
        _identity = _parser.Parse(releaseName, source);
        ApplyIdentityToFields(_identity);
        Results.Clear();
        OnPropertyChanged(nameof(ResultSummary));
        _candidate = null;
        CandidateText = string.Empty;
        OnPropertyChanged(nameof(HasCandidate));
        StatusText = _identity.IsSeries && !string.IsNullOrWhiteSpace(_identity.EpisodeLabel)
            ? $"Algılandı: {_identity.Title} · {_identity.EpisodeLabel}"
            : $"Algılandı: {_identity.Title}";
    }

    public async Task DownloadAndAttachSelectedAsync()
    {
        if (SelectedResult is null) { StatusText = "Önce bir altyazı sonucu seçin."; return; }
        try
        {
            IsBusy = true;
            await SaveApiConfigurationAsync().ConfigureAwait(true);
            StatusText = SelectedResult.IsPackage ? "Sezon paketinden bölüm ayıklanıyor…" : "Altyazı indiriliyor…";
            var bytes = await _service.DownloadAsync(SelectedResult, BuildOptions()).ConfigureAwait(true);
            var folder = Path.Combine(_paths.SubtitleDirectory, SafeFileName(Title));
            Directory.CreateDirectory(folder);
            var episode = !string.IsNullOrWhiteSpace(_identity.EpisodeLabel) ? "-" + _identity.EpisodeLabel : string.Empty;
            var path = Path.Combine(folder, $"{SafeFileName(Title)}{episode}-{SelectedResult.Id}{SelectedResult.Extension}");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            _attachSubtitle(path);
            StatusText = $"Altyazı indirildi ve eklendi: {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            _logger.Error("Altyazı indirilemedi.", exception);
            StatusText = "Altyazı indirilemedi.";
            _dialogs.ShowError($"Altyazı indirilemedi.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task SearchAsync()
    {
        try
        {
            IsBusy = true;
            Results.Clear();
            OnPropertyChanged(nameof(ResultSummary));
            _identity = BuildIdentityFromFields();
            StatusText = "AltyazıDB API v1.6.2 aranıyor…";
            await SaveApiConfigurationAsync().ConfigureAwait(true);
            var response = await _service.SearchAsync(_identity, BuildOptions()).ConfigureAwait(true);
            foreach (var item in response.Results) Results.Add(item);
            OnPropertyChanged(nameof(ResultSummary));
            SelectedResult = Results.FirstOrDefault();
            _candidate = response.Candidate;
            CandidateText = _candidate is null ? string.Empty : $"Bunu mu aramak istediniz? {_candidate.DisplayLabel}";
            OnPropertyChanged(nameof(HasCandidate));
            VersionGroups.Clear();
            VersionGroups.Add(string.Empty);
            foreach (var group in response.AvailableVersionGroups) VersionGroups.Add(group);
            var method = string.IsNullOrWhiteSpace(response.SearchMethod) ? string.Empty : $" · {response.SearchMethod}";
            StatusText = Results.Count == 0
                ? "Uygun altyazı bulunamadı."
                : $"{response.Pagination.TotalRecords} altyazı kaydı · Sayfa {response.Pagination.CurrentPage}/{response.Pagination.TotalPages}{method}";
        }
        catch (Exception exception)
        {
            _logger.Error("AltyazıDB araması başarısız.", exception);
            StatusText = "AltyazıDB araması başarısız.";
            _dialogs.ShowError($"Altyazı araması yapılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task TestApiAsync()
    {
        try
        {
            IsBusy = true;
            ApiStatusText = "API hesabı ve kota bilgisi doğrulanıyor…";
            await SaveApiConfigurationAsync().ConfigureAwait(true);
            var status = await _service.GetAccountStatusAsync(BuildOptions()).ConfigureAwait(true);
            ApiStatusText = status.Summary;
        }
        catch (Exception exception)
        {
            _logger.Error("AltyazıDB API hesabı doğrulanamadı.", exception);
            ApiStatusText = "API doğrulaması başarısız.";
            _dialogs.ShowError($"API hesabı doğrulanamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task InitializeApiKeyAsync()
    {
        await ReloadApiConfigurationAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_settings.AltyaziDbApiKey))
        {
            await _secretStore.SetAsync(ApiKeySecretName, _settings.AltyaziDbApiKey).ConfigureAwait(false);
            _settings.AltyaziDbApiKey = string.Empty;
            await _saveSettings().ConfigureAwait(false);
        }
    }

    public async Task ReloadApiConfigurationAsync()
    {
        var stored = await _secretStore.GetAsync(ApiKeySecretName).ConfigureAwait(false);
        var environment = Environment.GetEnvironmentVariable("ALTYAZIDB_API_KEY");
        var fallback = GeneratedAltyaziDbCredentials.ApiKey;

        _apiKey = !string.IsNullOrWhiteSpace(stored)
            ? stored
            : !string.IsNullOrWhiteSpace(environment)
                ? environment
                : !string.IsNullOrWhiteSpace(fallback)
                    ? fallback
                    : _settings.AltyaziDbApiKey;

        if (string.IsNullOrWhiteSpace(_settings.AltyaziDbApiUrl))
        {
            _settings.AltyaziDbApiUrl = GeneratedAltyaziDbCredentials.ApiUrl;
        }

        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(ApiUrl));
    }

    private async Task SaveApiConfigurationAsync()
    {
        _settings.AltyaziDbApiKey = string.Empty;
        await _secretStore.SetAsync(
            ApiKeySecretName,
            string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim()).ConfigureAwait(true);
        await _saveSettings().ConfigureAwait(true);
    }

    private void ApplyCandidate()
    {
        if (_candidate is null) return;
        _identity = _candidate.ApplyTo(BuildIdentityFromFields());
        ApplyIdentityToFields(_identity);
        StatusText = $"İçerik düzeltildi: {_identity.Title}";
    }

    private MediaIdentity BuildIdentityFromFields() => new(
        Title.Trim(),
        int.TryParse(Season, out _) || int.TryParse(Episode, out _) ? "series" : _identity.ContentType,
        _identity.ReleaseName,
        int.TryParse(Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
        int.TryParse(Season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season) ? season : null,
        int.TryParse(Episode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode) ? episode : null,
        _identity.ImdbId,
        _identity.TmdbId);

    private SubtitleSearchOptions BuildOptions() => new(
        ApiUrl.Trim(), ApiKey.Trim(), _settings.SubtitleLanguage, _durationSeconds,
        1, 100, Sort, string.IsNullOrWhiteSpace(VersionGroup) ? null : VersionGroup);

    private void ApplyIdentityToFields(MediaIdentity identity)
    {
        Title = identity.Title;
        Year = identity.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Season = identity.Season?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Episode = identity.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void OpenSubtitleFolder()
    {
        Directory.CreateDirectory(_paths.SubtitleDirectory);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{_paths.SubtitleDirectory}\"", UseShellExecute = true });
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "altyazi" : safe;
    }

    public void Dispose() => _service.Dispose();
}
