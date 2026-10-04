using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure.Generated;

namespace AltyaziDB.Player.Infrastructure;

public sealed class NeonCloudAccountService : ICloudAccountService, IPlayerApiAuthProvider, IDisposable
{
    private const string CookieSecretKey = "adb-cloud:auth-cookies";
    private const string DeviceKeySecretKey = "adb-cloud:device-key";

    private readonly ISecretStore _secrets;
    private readonly IAppLogger _logger;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Uri? _baseUri;
    private readonly Uri? _apiBaseUri;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private string? _deviceKey;
    private bool _disposed;

    public NeonCloudAccountService(ISecretStore secrets, IAppLogger logger)
    {
        _secrets = secrets;
        _logger = logger;
        _baseUri = NormalizeHttpsUri(GeneratedAdbCloudConfig.AuthBaseUrl);
        _apiBaseUri = NormalizeHttpsUri(GeneratedAdbCloudConfig.ApiBaseUrl);

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ADB-Player/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool IsConfigured => _baseUri is not null;
    public bool IsCloudCoreConfigured => _baseUri is not null && _apiBaseUri is not null;
    public string AuthBaseUrl => _baseUri?.AbsoluteUri ?? string.Empty;
    public string ApiBaseUrl => _apiBaseUri?.AbsoluteUri ?? string.Empty;

    public async Task<PlayerApiAuthContext?> GetPlayerApiAuthAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var deviceKey = await GetOrCreateDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(deviceKey)
            ? null
            : new PlayerApiAuthContext(token, deviceKey);
    }

    public async Task<CloudAccountSnapshot?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_baseUri is null) return null;

        try
        {
            var storedCookies = await _secrets.GetAsync(CookieSecretKey, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(storedCookies))
            {
                try
                {
                    _cookies.SetCookies(_baseUri, storedCookies);
                }
                catch (CookieException exception)
                {
                    _logger.Warning($"ADB Cloud oturum çerezi geri yüklenemedi: {exception.Message}");
                    await _secrets.RemoveAsync(CookieSecretKey, cancellationToken).ConfigureAwait(false);
                }
            }

            using var response = await _http.GetAsync(new Uri(_baseUri, "get-session"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await ClearStoredCookiesAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.Warning($"ADB Cloud oturumu doğrulanamadı: {(int)response.StatusCode} {error}");
                return null;
            }

            var snapshot = await ParseSnapshotAsync(response, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                await ClearStoredCookiesAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await PersistCookiesAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Warning($"ADB Cloud oturum geri yükleme başarısız: {exception.Message}");
            return null;
        }
    }

    public async Task<CloudAccountResult> SignUpAsync(
        string displayName,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (_baseUri is null) return CloudAccountResult.Fail("ADB Cloud henüz yapılandırılmadı.");

        var validation = ValidateCredentials(email, password);
        if (validation is not null) return CloudAccountResult.Fail(validation);
        if (string.IsNullOrWhiteSpace(displayName))
            return CloudAccountResult.Fail("Görünen ad boş bırakılamaz.");

        try
        {
            using var response = await _http.PostAsJsonAsync(
                new Uri(_baseUri, "sign-up/email"),
                new
                {
                    name = displayName.Trim(),
                    email = email.Trim(),
                    password,
                    callbackURL = _baseUri.AbsoluteUri
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var snapshot = await ParseSnapshotAsync(response, cancellationToken).ConfigureAwait(false)
                           ?? await RestoreSessionAsync(cancellationToken).ConfigureAwait(false);

            if (snapshot is null)
                return CloudAccountResult.Fail("Hesap oluşturuldu ancak oturum açılamadı.");

            await PersistCookiesAsync(cancellationToken).ConfigureAwait(false);
            return CloudAccountResult.Ok(snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Warning($"ADB Cloud kayıt isteği başarısız: {exception.Message}");
            return CloudAccountResult.Fail("ADB Cloud hizmetine ulaşılamadı.");
        }
    }

    public async Task<CloudAccountResult> SignInAsync(
        string email,
        string password,
        bool rememberMe = true,
        CancellationToken cancellationToken = default)
    {
        if (_baseUri is null) return CloudAccountResult.Fail("ADB Cloud henüz yapılandırılmadı.");

        var validation = ValidateCredentials(email, password);
        if (validation is not null) return CloudAccountResult.Fail(validation);

        try
        {
            using var response = await _http.PostAsJsonAsync(
                new Uri(_baseUri, "sign-in/email"),
                new
                {
                    email = email.Trim(),
                    password,
                    rememberMe,
                    callbackURL = _baseUri.AbsoluteUri
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var snapshot = await ParseSnapshotAsync(response, cancellationToken).ConfigureAwait(false)
                           ?? await RestoreSessionAsync(cancellationToken).ConfigureAwait(false);

            if (snapshot is null)
                return CloudAccountResult.Fail("Oturum açıldı ancak kullanıcı bilgisi alınamadı.");

            await PersistCookiesAsync(cancellationToken).ConfigureAwait(false);
            return CloudAccountResult.Ok(snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Warning($"ADB Cloud giriş isteği başarısız: {exception.Message}");
            return CloudAccountResult.Fail("ADB Cloud hizmetine ulaşılamadı.");
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_baseUri is not null)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(
                    new Uri(_baseUri, "sign-out"),
                    new { },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                _logger.Warning($"ADB Cloud çıkış isteği tamamlanamadı: {exception.Message}");
            }
        }

        await ClearStoredCookiesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudAccountCoreResult> BootstrapAccountCoreAsync(
        CloudAccountSnapshot account,
        string locale,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisi henüz yapılandırılmadı.");

        try
        {
            var device = await GetDevicePayloadAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Post,
                "v1/account/bootstrap",
                new
                {
                    displayName = account.User.DisplayName,
                    avatarUrl = account.User.ImageUrl,
                    locale = NormalizeLocale(locale),
                    device
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountCoreResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var snapshot = await ReadCoreSnapshotAsync(response, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? CloudAccountCoreResult.Fail("ADB Cloud profil yanıtı okunamadı.")
                : CloudAccountCoreResult.Ok(snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud profil/cihaz eşitlemesi başarısız: {exception.Message}");
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisine ulaşılamadı.");
        }
    }

    public async Task<CloudAccountCoreResult> RefreshAccountCoreAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                "v1/account",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountCoreResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var snapshot = await ReadCoreSnapshotAsync(response, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? CloudAccountCoreResult.Fail("ADB Cloud profil yanıtı okunamadı.")
                : CloudAccountCoreResult.Ok(snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud profil yenileme başarısız: {exception.Message}");
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisine ulaşılamadı.");
        }
    }

    public async Task<CloudAccountCoreResult> UpdateProfileAsync(
        string displayName,
        string? avatarUrl,
        string locale,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisi henüz yapılandırılmadı.");

        if (string.IsNullOrWhiteSpace(displayName))
            return CloudAccountCoreResult.Fail("Görünen ad boş bırakılamaz.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Patch,
                "v1/profile",
                new
                {
                    displayName = displayName.Trim(),
                    avatarUrl,
                    locale = NormalizeLocale(locale)
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountCoreResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud profil güncelleme başarısız: {exception.Message}");
            return CloudAccountCoreResult.Fail("ADB Cloud profili güncellenemedi.");
        }
    }

    public async Task<CloudAccountCoreResult> RemoveDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudAccountCoreResult.Fail("ADB Cloud profil ve cihaz servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Delete,
                $"v1/devices/{deviceId:D}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudAccountCoreResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud cihaz kaldırma başarısız: {exception.Message}");
            return CloudAccountCoreResult.Fail("Cihaz ADB Cloud hesabından kaldırılamadı.");
        }
    }






    public async Task<CloudPreferencesResult> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudPreferencesResult.Fail("ADB Cloud tercih senkronu henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                "v1/preferences",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudPreferencesResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadPreferencesAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud tercihleri alınamadı: {exception.Message}");
            return CloudPreferencesResult.Fail("ADB Cloud tercihlerine ulaşılamadı.");
        }
    }

    public async Task<CloudPreferencesResult> SavePreferencesAsync(
        CloudUserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!IsCloudCoreConfigured)
            return CloudPreferencesResult.Fail("ADB Cloud tercih senkronu henüz yapılandırılmadı.");

        try
        {
            var deviceKey = await GetOrCreateDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Put,
                "v1/preferences",
                new
                {
                    deviceKey,
                    clientUpdatedAt = preferences.UpdatedAt,
                    preferences.UiLanguage,
                    preferences.SubtitleLanguage,
                    preferences.AutoPlayNext,
                    preferences.AutoSubtitleSync,
                    preferences.SubtitleSyncMaxOffsetSeconds,
                    preferences.SeekShortSeconds,
                    preferences.SeekMediumSeconds,
                    preferences.SeekLongSeconds,
                    preferences.PrimarySubtitleScale,
                    preferences.SecondarySubtitleScale,
                    preferences.PrimarySubtitlePosition,
                    preferences.SecondarySubtitlePosition,
                    preferences.PreservePrimaryAssStyle,
                    preferences.PreserveSecondaryAssStyle,
                    preferences.TimeDisplayPrecision,
                    addonStates = preferences.Addons
                        .Where(item => !string.IsNullOrWhiteSpace(item.AddonId))
                        .Take(100)
                        .GroupBy(item => item.AddonId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.Last().IsEnabled, StringComparer.OrdinalIgnoreCase),
                    addonProfiles = preferences.Addons
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.AddonId) &&
                            !string.IsNullOrWhiteSpace(item.ManifestUrl))
                        .Take(100)
                        .Select(item => new
                        {
                            addonId = item.AddonId,
                            manifestUrl = item.ManifestUrl,
                            isEnabled = item.IsEnabled,
                            allowLocalHttp = item.AllowLocalHttp
                        })
                        .ToArray()
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudPreferencesResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadPreferencesAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud tercihleri kaydedilemedi: {exception.Message}");
            return CloudPreferencesResult.Fail("ADB Cloud tercihleri kaydedilemedi.");
        }
    }

    private async Task<CloudPreferencesResult> ReadPreferencesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<PreferencesEnvelopeDto>(_json, cancellationToken).ConfigureAwait(false);
        var value = envelope?.Preferences;
        if (value is null)
            return CloudPreferencesResult.Ok(null, envelope?.Accepted ?? true);

        var addons = value.AddonProfilesSynced
            ? (value.AddonProfiles ?? new List<AddonProfileDto>())
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.AddonId) &&
                    !string.IsNullOrWhiteSpace(item.ManifestUrl))
                .Take(100)
                .Select(item => new CloudAddonPreference(
                    item.AddonId!,
                    item.IsEnabled,
                    item.ManifestUrl,
                    item.AllowLocalHttp))
                .ToArray()
            : (value.AddonStates ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Take(100)
                .Select(pair => new CloudAddonPreference(pair.Key, pair.Value))
                .ToArray();

        return CloudPreferencesResult.Ok(new CloudUserPreferences(
            string.IsNullOrWhiteSpace(value.UiLanguage) ? "tr-TR" : value.UiLanguage,
            string.IsNullOrWhiteSpace(value.SubtitleLanguage) ? "tr" : value.SubtitleLanguage,
            value.AutoPlayNext,
            value.AutoSubtitleSync,
            Math.Clamp(value.SubtitleSyncMaxOffsetSeconds, 5, 600),
            Math.Clamp(value.SeekShortSeconds, 1, 120),
            Math.Clamp(value.SeekMediumSeconds, 1, 600),
            Math.Clamp(value.SeekLongSeconds, 1, 1800),
            Math.Clamp(value.PrimarySubtitleScale, 0.5, 2.0),
            Math.Clamp(value.SecondarySubtitleScale, 0.5, 2.0),
            Math.Clamp(value.PrimarySubtitlePosition, 0, 150),
            Math.Clamp(value.SecondarySubtitlePosition, 0, 150),
            value.PreservePrimaryAssStyle,
            value.PreserveSecondaryAssStyle,
            Math.Clamp(value.TimeDisplayPrecision, 0, 2),
            addons,
            value.UpdatedAt ?? DateTimeOffset.UtcNow,
            value.AddonProfilesSynced),
            envelope?.Accepted ?? true);
    }

    public async Task<CloudPlaylistResult> GetPlaylistAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudPlaylistResult.Fail("ADB Cloud oynatma listesi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                "v1/playlist",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudPlaylistResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadPlaylistAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud oynatma listesi alınamadı: {exception.Message}");
            return CloudPlaylistResult.Fail("ADB Cloud oynatma listesine ulaşılamadı.");
        }
    }

    public async Task<CloudPlaylistResult> SavePlaylistAsync(
        IReadOnlyList<CloudLibraryManifestItem> items,
        string? currentMediaKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudPlaylistResult.Fail("ADB Cloud oynatma listesi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Put,
                "v1/playlist",
                new
                {
                    items = items.Take(500).Select(item => new
                    {
                        item.MediaKey,
                        item.Title,
                        item.MediaType,
                        item.Year,
                        item.Season,
                        item.Episode,
                        item.ImdbId,
                        item.TmdbId,
                        item.SourceKind
                    }).ToArray(),
                    currentMediaKey
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudPlaylistResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadPlaylistAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud oynatma listesi kaydedilemedi: {exception.Message}");
            return CloudPlaylistResult.Fail("ADB Cloud oynatma listesi kaydedilemedi.");
        }
    }

    private async Task<CloudPlaylistResult> ReadPlaylistAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<PlaylistEnvelopeDto>(_json, cancellationToken).ConfigureAwait(false);
        if (envelope?.Playlist is null)
            return CloudPlaylistResult.Fail("ADB Cloud oynatma listesi yanıtı okunamadı.");

        var items = (envelope.Playlist.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.MediaKey) && !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new CloudPlaylistItem(
                Math.Max(0, item.Position),
                item.MediaKey!,
                item.Title!,
                string.IsNullOrWhiteSpace(item.MediaType) ? "unknown" : item.MediaType!,
                item.Year,
                item.Season,
                item.Episode,
                item.ImdbId,
                item.TmdbId,
                string.IsNullOrWhiteSpace(item.SourceKind) ? "unknown" : item.SourceKind!))
            .OrderBy(item => item.Position)
            .ToArray();

        return CloudPlaylistResult.Ok(new CloudPlaylistSnapshot(
            items,
            envelope.Playlist.CurrentMediaKey,
            envelope.Playlist.UpdatedAt ?? DateTimeOffset.UtcNow));
    }

    public async Task<CloudCollectionsResult> GetCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                "v1/collections",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return CloudCollectionsResult.Ok(await ReadCollectionsAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud koleksiyonları alınamadı: {exception.Message}");
            return CloudCollectionsResult.Fail("ADB Cloud koleksiyonlarına ulaşılamadı.");
        }
    }

    public async Task<CloudCollectionsResult> CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");
        if (string.IsNullOrWhiteSpace(name))
            return CloudCollectionsResult.Fail("Liste adı boş bırakılamaz.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Post,
                "v1/collections",
                new { name = name.Trim() },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return CloudCollectionsResult.Ok(await ReadCollectionsAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud koleksiyonu oluşturulamadı: {exception.Message}");
            return CloudCollectionsResult.Fail("ADB Cloud listesi oluşturulamadı.");
        }
    }

    public async Task<CloudCollectionsResult> RenameCollectionAsync(
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");
        if (string.IsNullOrWhiteSpace(name))
            return CloudCollectionsResult.Fail("Liste adı boş bırakılamaz.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Patch,
                $"v1/collections/{collectionId:D}",
                new { name = name.Trim() },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return CloudCollectionsResult.Ok(await ReadCollectionsAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud koleksiyonu yeniden adlandırılamadı: {exception.Message}");
            return CloudCollectionsResult.Fail("ADB Cloud listesi yeniden adlandırılamadı.");
        }
    }

    public async Task<CloudCollectionsResult> DeleteCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Delete,
                $"v1/collections/{collectionId:D}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return CloudCollectionsResult.Ok(await ReadCollectionsAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud koleksiyonu silinemedi: {exception.Message}");
            return CloudCollectionsResult.Fail("ADB Cloud listesi silinemedi.");
        }
    }

    public async Task<CloudCollectionItemsResult> GetCollectionItemsAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionItemsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                $"v1/collections/{collectionId:D}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionItemsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadCollectionItemsAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud liste içeriği alınamadı: {exception.Message}");
            return CloudCollectionItemsResult.Fail("ADB Cloud liste içeriğine ulaşılamadı.");
        }
    }

    public async Task<CloudCollectionItemsResult> AddCollectionItemAsync(
        Guid collectionId,
        CloudLibraryManifestItem item,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionItemsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Post,
                $"v1/collections/{collectionId:D}/items",
                new
                {
                    item.MediaKey,
                    item.Title,
                    item.MediaType,
                    item.Year,
                    item.Season,
                    item.Episode,
                    item.ImdbId,
                    item.TmdbId,
                    item.SourceKind
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionItemsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadCollectionItemsAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud liste öğesi eklenemedi: {exception.Message}");
            return CloudCollectionItemsResult.Fail("İçerik ADB Cloud listesine eklenemedi.");
        }
    }

    public async Task<CloudCollectionItemsResult> RemoveCollectionItemAsync(
        Guid collectionId,
        string mediaKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudCollectionItemsResult.Fail("ADB Cloud koleksiyon servisi henüz yapılandırılmadı.");
        if (string.IsNullOrWhiteSpace(mediaKey))
            return CloudCollectionItemsResult.Fail("Medya kimliği bulunamadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Delete,
                $"v1/collections/{collectionId:D}/items/{Uri.EscapeDataString(mediaKey)}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudCollectionItemsResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            return await ReadCollectionItemsAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud liste öğesi kaldırılamadı: {exception.Message}");
            return CloudCollectionItemsResult.Fail("İçerik ADB Cloud listesinden kaldırılamadı.");
        }
    }

    private async Task<IReadOnlyList<CloudCollection>> ReadCollectionsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<CollectionsEnvelopeDto>(_json, cancellationToken).ConfigureAwait(false);
        if (envelope?.Collections is null) return Array.Empty<CloudCollection>();

        return envelope.Collections
            .Select(MapCollection)
            .Where(item => item is not null)
            .Cast<CloudCollection>()
            .ToArray();
    }

    private async Task<CloudCollectionItemsResult> ReadCollectionItemsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<CollectionItemsEnvelopeDto>(_json, cancellationToken).ConfigureAwait(false);
        var collection = MapCollection(envelope?.Collection);
        if (collection is null)
            return CloudCollectionItemsResult.Fail("ADB Cloud listesi yanıtı okunamadı.");

        var items = (envelope?.Items ?? [])
            .Select(item => MapCollectionItem(collection.Id, item))
            .Where(item => item is not null)
            .Cast<CloudCollectionItem>()
            .ToArray();

        return CloudCollectionItemsResult.Ok(collection, items);
    }

    private static CloudCollection? MapCollection(CollectionDto? collection)
    {
        if (collection is null || !Guid.TryParse(collection.Id, out var id) || string.IsNullOrWhiteSpace(collection.Name))
            return null;

        return new CloudCollection(
            id,
            collection.Name,
            string.IsNullOrWhiteSpace(collection.Kind) ? "custom" : collection.Kind,
            Math.Max(0, collection.ItemCount),
            collection.CreatedAt ?? DateTimeOffset.UtcNow,
            collection.UpdatedAt ?? collection.CreatedAt ?? DateTimeOffset.UtcNow);
    }

    private static CloudCollectionItem? MapCollectionItem(Guid collectionId, CollectionItemDto? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.MediaKey) || string.IsNullOrWhiteSpace(item.Title))
            return null;

        return new CloudCollectionItem(
            collectionId,
            item.MediaKey,
            item.Title,
            string.IsNullOrWhiteSpace(item.MediaType) ? "unknown" : item.MediaType,
            item.Year,
            item.Season,
            item.Episode,
            item.ImdbId,
            item.TmdbId,
            string.IsNullOrWhiteSpace(item.SourceKind) ? "unknown" : item.SourceKind,
            item.AddedAt ?? DateTimeOffset.UtcNow);
    }

    public async Task<CloudWatchSyncResult> SyncLibraryAsync(
        IReadOnlyList<CloudLibraryManifestItem> items,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudWatchSyncResult.Fail("ADB Cloud kütüphane senkronu henüz yapılandırılmadı.");

        if (items.Count == 0)
            return await GetWatchProgressAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Post,
                "v1/sync/library",
                new
                {
                    items = items.Take(100).Select(item => new
                    {
                        item.MediaKey,
                        item.Title,
                        item.MediaType,
                        item.Year,
                        item.Season,
                        item.Episode,
                        item.ImdbId,
                        item.TmdbId,
                        item.SourceKind
                    }).ToArray()
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudWatchSyncResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var entries = await ReadWatchProgressAsync(response, cancellationToken).ConfigureAwait(false);
            return CloudWatchSyncResult.Ok(entries);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud kütüphane senkronu başarısız: {exception.Message}");
            return CloudWatchSyncResult.Fail("ADB Cloud kütüphanesi eşitlenemedi.");
        }
    }

    public async Task<CloudWatchSyncResult> SyncWatchProgressAsync(
        IReadOnlyList<CloudWatchProgressMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudWatchSyncResult.Fail("ADB Cloud izleme senkronu henüz yapılandırılmadı.");

        if (mutations.Count == 0)
            return await GetWatchProgressAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var deviceKey = await GetOrCreateDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Post,
                "v1/sync/watch-progress",
                new
                {
                    deviceKey,
                    items = mutations.Take(100).Select(item => new
                    {
                        item.MediaKey,
                        item.Title,
                        item.MediaType,
                        item.Year,
                        item.Season,
                        item.Episode,
                        item.ImdbId,
                        item.TmdbId,
                        item.SourceKind,
                        item.PositionSeconds,
                        item.DurationSeconds,
                        updatedAt = item.UpdatedAtUtc
                    }).ToArray()
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudWatchSyncResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var entries = await ReadWatchProgressAsync(response, cancellationToken).ConfigureAwait(false);
            return CloudWatchSyncResult.Ok(entries);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud izleme senkronu başarısız: {exception.Message}");
            return CloudWatchSyncResult.Fail("ADB Cloud izleme senkronu tamamlanamadı.");
        }
    }

    public async Task<CloudWatchSyncResult> GetWatchProgressAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsCloudCoreConfigured)
            return CloudWatchSyncResult.Fail("ADB Cloud izleme senkronu henüz yapılandırılmadı.");

        try
        {
            using var response = await SendAuthorizedApiAsync(
                HttpMethod.Get,
                "v1/sync/watch-progress",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CloudWatchSyncResult.Fail(await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            var entries = await ReadWatchProgressAsync(response, cancellationToken).ConfigureAwait(false);
            return CloudWatchSyncResult.Ok(entries);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warning($"ADB Cloud izleme listesi alınamadı: {exception.Message}");
            return CloudWatchSyncResult.Fail("ADB Cloud izleme listesine ulaşılamadı.");
        }
    }

    private async Task<IReadOnlyList<CloudWatchProgressEntry>> ReadWatchProgressAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<WatchSyncEnvelopeDto>(_json, cancellationToken).ConfigureAwait(false);
        if (envelope?.Entries is null) return Array.Empty<CloudWatchProgressEntry>();

        return envelope.Entries
            .Where(item => !string.IsNullOrWhiteSpace(item.MediaKey) && !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new CloudWatchProgressEntry(
                item.MediaKey!,
                item.Title!,
                string.IsNullOrWhiteSpace(item.MediaType) ? "unknown" : item.MediaType!,
                item.Year,
                item.Season,
                item.Episode,
                item.ImdbId,
                item.TmdbId,
                string.IsNullOrWhiteSpace(item.SourceKind) ? "unknown" : item.SourceKind!,
                item.HasProgress,
                Math.Max(0, item.PositionSeconds),
                Math.Max(0, item.DurationSeconds),
                item.Completed,
                item.ClientUpdatedAt,
                item.ServerUpdatedAt ?? item.ClientUpdatedAt ?? DateTimeOffset.UtcNow))
            .ToArray();
    }

    private async Task<HttpResponseMessage> SendAuthorizedApiAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        if (_apiBaseUri is null)
            throw new InvalidOperationException("ADB Cloud API endpoint tanımlı değil.");

        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("ADB Cloud erişim belirteci alınamadı.");

        var request = new HttpRequestMessage(method, new Uri(_apiBaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _json);
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null) return null;

        using var response = await _http.GetAsync(new Uri(_baseUri, "token"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warning($"ADB Cloud JWT alınamadı: {(int)response.StatusCode}");
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenDto>(_json, cancellationToken).ConfigureAwait(false);
        return payload?.Token;
    }

    private async Task<object> GetDevicePayloadAsync(CancellationToken cancellationToken)
    {
        var deviceKey = await GetOrCreateDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var versionText = version?.ToString() ?? "dev";

        return new
        {
            deviceKey,
            displayName = string.IsNullOrWhiteSpace(Environment.MachineName) ? "Windows PC" : Environment.MachineName,
            platform = OperatingSystem.IsWindows() ? "windows" : "desktop",
            appVersion = versionText
        };
    }

    private async Task<string> GetOrCreateDeviceKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_deviceKey)) return _deviceKey;

        var stored = await _secrets.GetAsync(DeviceKeySecretKey, cancellationToken).ConfigureAwait(false);
        if (Guid.TryParse(stored, out var parsed))
        {
            _deviceKey = parsed.ToString("D");
            return _deviceKey;
        }

        _deviceKey = Guid.NewGuid().ToString("D");
        await _secrets.SetAsync(DeviceKeySecretKey, _deviceKey, cancellationToken).ConfigureAwait(false);
        return _deviceKey;
    }

    private async Task<CloudAccountCoreSnapshot?> ReadCoreSnapshotAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<AccountCoreDto>(_json, cancellationToken).ConfigureAwait(false);
        if (envelope is null) return null;

        var currentDeviceKey = await GetOrCreateDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
        var profile = MapProfile(envelope.Profile);

        var devices = (envelope.Devices ?? [])
            .Select(item => MapDevice(item, currentDeviceKey))
            .Where(item => item is not null)
            .Cast<CloudDevice>()
            .ToArray();

        var current = MapDevice(envelope.CurrentDevice, currentDeviceKey)
                      ?? devices.FirstOrDefault(item => item.IsCurrentDevice);

        return new CloudAccountCoreSnapshot(profile, devices, current);
    }

    private static CloudProfile? MapProfile(ProfileDto? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.UserId)) return null;
        return new CloudProfile(
            profile.UserId,
            profile.DisplayName ?? string.Empty,
            profile.AvatarUrl,
            string.IsNullOrWhiteSpace(profile.Locale) ? "tr-TR" : profile.Locale,
            profile.CreatedAt ?? DateTimeOffset.UtcNow,
            profile.UpdatedAt ?? profile.CreatedAt ?? DateTimeOffset.UtcNow);
    }

    private static CloudDevice? MapDevice(DeviceDto? device, string currentDeviceKey)
    {
        if (device is null
            || !Guid.TryParse(device.Id, out var id)
            || string.IsNullOrWhiteSpace(device.UserId)
            || string.IsNullOrWhiteSpace(device.DeviceKey))
            return null;

        return new CloudDevice(
            id,
            device.UserId,
            device.DeviceKey,
            device.DisplayName ?? "Windows PC",
            device.Platform ?? "windows",
            device.AppVersion,
            device.LastSeenAt ?? DateTimeOffset.UtcNow,
            device.CreatedAt ?? DateTimeOffset.UtcNow,
            device.UpdatedAt ?? device.LastSeenAt ?? DateTimeOffset.UtcNow,
            string.Equals(device.DeviceKey, currentDeviceKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLocale(string? locale)
    {
        var normalized = locale?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return "tr-TR";
        return normalized[..Math.Min(normalized.Length, 16)];
    }

    private static Uri? NormalizeHttpsUri(string? raw)
    {
        var value = raw?.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

        var normalized = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed.AbsoluteUri
            : parsed.AbsoluteUri + "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string? ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return "Geçerli bir e-posta adresi girin.";
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return "Parola en az 8 karakter olmalıdır.";
        return null;
    }

    private async Task PersistCookiesAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null) return;
        var header = _cookies.GetCookieHeader(_baseUri);
        if (string.IsNullOrWhiteSpace(header))
            await _secrets.RemoveAsync(CookieSecretKey, cancellationToken).ConfigureAwait(false);
        else
            await _secrets.SetAsync(CookieSecretKey, header, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearStoredCookiesAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is not null)
        {
            foreach (Cookie cookie in _cookies.GetCookies(_baseUri))
            {
                cookie.Expired = true;
            }
        }

        await _secrets.RemoveAsync(CookieSecretKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CloudAccountSnapshot?> ParseSnapshotAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        JsonElement userElement;
        if (root.TryGetProperty("user", out var directUser))
        {
            userElement = directUser;
        }
        else if (root.TryGetProperty("data", out var data)
                 && data.ValueKind == JsonValueKind.Object
                 && data.TryGetProperty("user", out var nestedUser))
        {
            userElement = nestedUser;
            root = data;
        }
        else
        {
            return null;
        }

        var id = ReadString(userElement, "id");
        var email = ReadString(userElement, "email");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email)) return null;

        var name = ReadString(userElement, "name");
        var image = ReadNullableString(userElement, "image");
        var verified = ReadBool(userElement, "emailVerified") || ReadBool(userElement, "email_verified");

        CloudAccountSession? session = null;
        if (root.TryGetProperty("session", out var sessionElement) && sessionElement.ValueKind == JsonValueKind.Object)
        {
            var sessionId = ReadString(sessionElement, "id");
            var userId = ReadString(sessionElement, "userId");
            if (string.IsNullOrWhiteSpace(userId)) userId = ReadString(sessionElement, "user_id");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                session = new CloudAccountSession(
                    sessionId,
                    string.IsNullOrWhiteSpace(userId) ? id : userId,
                    ReadDateTimeOffset(sessionElement, "expiresAt") ?? ReadDateTimeOffset(sessionElement, "expires_at"));
            }
        }

        return new CloudAccountSnapshot(
            new CloudAccountUser(
                id,
                email,
                string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name,
                image,
                verified),
            session);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return $"İstek başarısız oldu ({(int)response.StatusCode}).";

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            foreach (var key in new[] { "message", "error", "detail" })
            {
                if (root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? $"İstek başarısız oldu ({(int)response.StatusCode}).";
            }
        }
        catch
        {
            // Fall through to status code.
        }

        return $"İstek başarısız oldu ({(int)response.StatusCode}).";
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? ReadNullableString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }





    private sealed class PreferencesEnvelopeDto
    {
        public PreferencesDto? Preferences { get; init; }
        public bool? Accepted { get; init; }
    }

    private sealed class PreferencesDto
    {
        public string? UiLanguage { get; init; }
        public string? SubtitleLanguage { get; init; }
        public bool AutoPlayNext { get; init; }
        public bool AutoSubtitleSync { get; init; }
        public int SubtitleSyncMaxOffsetSeconds { get; init; } = 120;
        public int SeekShortSeconds { get; init; } = 10;
        public int SeekMediumSeconds { get; init; } = 30;
        public int SeekLongSeconds { get; init; } = 60;
        public double PrimarySubtitleScale { get; init; } = 1.0;
        public double SecondarySubtitleScale { get; init; } = 0.85;
        public double PrimarySubtitlePosition { get; init; } = 100;
        public double SecondarySubtitlePosition { get; init; } = 10;
        public bool PreservePrimaryAssStyle { get; init; } = true;
        public bool PreserveSecondaryAssStyle { get; init; } = true;
        public int TimeDisplayPrecision { get; init; }
        public Dictionary<string, bool>? AddonStates { get; init; }
        public List<AddonProfileDto>? AddonProfiles { get; init; }
        public bool AddonProfilesSynced { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class AddonProfileDto
    {
        public string? AddonId { get; init; }
        public string? ManifestUrl { get; init; }
        public bool IsEnabled { get; init; } = true;
        public bool AllowLocalHttp { get; init; }
    }

    private sealed class PlaylistEnvelopeDto
    {
        public PlaylistDto? Playlist { get; init; }
    }

    private sealed class PlaylistDto
    {
        public List<PlaylistItemDto>? Items { get; init; }
        public string? CurrentMediaKey { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class PlaylistItemDto
    {
        public int Position { get; init; }
        public string? MediaKey { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public int? Year { get; init; }
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public string? ImdbId { get; init; }
        public string? TmdbId { get; init; }
        public string? SourceKind { get; init; }
    }

    private sealed class CollectionsEnvelopeDto
    {
        public List<CollectionDto>? Collections { get; init; }
    }

    private sealed class CollectionItemsEnvelopeDto
    {
        public CollectionDto? Collection { get; init; }
        public List<CollectionItemDto>? Items { get; init; }
    }

    private sealed class CollectionDto
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Kind { get; init; }
        public int ItemCount { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class CollectionItemDto
    {
        public string? CollectionId { get; init; }
        public string? MediaKey { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public int? Year { get; init; }
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public string? ImdbId { get; init; }
        public string? TmdbId { get; init; }
        public string? SourceKind { get; init; }
        public DateTimeOffset? AddedAt { get; init; }
    }

    private sealed class WatchSyncEnvelopeDto
    {
        public List<WatchProgressDto>? Entries { get; init; }
    }

    private sealed class WatchProgressDto
    {
        public string? MediaKey { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public int? Year { get; init; }
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public string? ImdbId { get; init; }
        public string? TmdbId { get; init; }
        public string? SourceKind { get; init; }
        public bool HasProgress { get; init; }
        public double PositionSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public bool Completed { get; init; }
        public DateTimeOffset? ClientUpdatedAt { get; init; }
        public DateTimeOffset? ServerUpdatedAt { get; init; }
    }

    private sealed class TokenDto
    {
        public string? Token { get; init; }
    }

    private sealed class AccountCoreDto
    {
        public ProfileDto? Profile { get; init; }
        public DeviceDto? CurrentDevice { get; init; }
        public List<DeviceDto>? Devices { get; init; }
    }

    private sealed class ProfileDto
    {
        public string? UserId { get; init; }
        public string? DisplayName { get; init; }
        public string? AvatarUrl { get; init; }
        public string? Locale { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class DeviceDto
    {
        public string? Id { get; init; }
        public string? UserId { get; init; }
        public string? DeviceKey { get; init; }
        public string? DisplayName { get; init; }
        public string? Platform { get; init; }
        public string? AppVersion { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}
