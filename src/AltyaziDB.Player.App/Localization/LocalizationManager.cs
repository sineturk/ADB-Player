using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace AltyaziDB.Player.App.Localization;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class LocalizationManager
{
    private const string DictionaryPrefix = "Localization/Strings.";

    private static readonly IReadOnlyDictionary<string, string> EnglishExact =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Oynatma motoru hazırlanıyor…"] = "Preparing the playback engine…",
            ["Hazır."] = "Ready.",
            ["Liste boş"] = "Playlist is empty",
            ["Kütüphane hazırlanıyor…"] = "Preparing the library…",
            ["Video yok"] = "No videos",
            ["Klasör ekleniyor…"] = "Adding folder…",
            ["Kütüphane taranıyor…"] = "Scanning the library…",
            ["Kütüphane taraması başarısız."] = "Library scan failed.",
            ["Klasör kütüphaneden kaldırıldı."] = "Folder removed from the library.",
            ["Klasör ekleyerek kütüphaneyi başlatın."] = "Add a folder to start your library.",
            ["Bağlantı yapıştırın veya bir WebDAV profili oluşturun."] = "Paste a link or connect a network location.",
            ["Önce bir bağlantı girin."] = "Enter a link first.",
            ["Bağlantı çözümleniyor…"] = "Resolving the link…",
            ["Bağlantı açılamadı."] = "The link could not be opened.",
            ["Kaydetmek için geçerli bir bağlantı girin."] = "Enter a valid link to save it.",
            ["Bağlantı kaydedildi."] = "Link saved.",
            ["Önce kayıtlı bir bağlantı seçin."] = "Select a saved link first.",
            ["Kayıtlı bağlantı silindi."] = "Saved link removed.",
            ["Önce bir bulut sağlayıcısı seçin."] = "Select a cloud provider first.",
            ["Bulut hesabı bağlanamadı."] = "The cloud account could not be connected.",
            ["Önce bağlı bir bulut hesabı seçin."] = "Select a connected cloud account first.",
            ["Bulut hesabı açılamadı."] = "The cloud account could not be opened.",
            ["Bulut hesabı kaldırıldı."] = "Cloud account disconnected.",
            ["WebDAV bağlantısı başarısız."] = "The network connection failed.",
            ["WebDAV profili ve parolası güvenli olarak kaydedildi."] = "Network location saved securely.",
            ["Önce bir dosya veya klasör seçin."] = "Select a file or folder first.",
            ["Seçilen öğe video değil."] = "The selected item is not a video.",
            ["Video açılamadı."] = "The video could not be opened.",
            ["Klasör açılamadı."] = "The folder could not be opened.",
            ["Telegram hazırlanıyor…"] = "Preparing Telegram…",
            ["Uygulama Telegram kimliği hazır."] = "Telegram is ready.",
            ["Telegram uygulama kimliği derlemeye eklenmemiş."] = "Telegram has not been configured yet.",
            ["Telegram başlatılamadı."] = "Telegram could not be started.",
            ["Şu anda gönderilecek bir Telegram doğrulama bilgisi yok."] = "There is no Telegram verification step to submit right now.",
            ["Telegram sohbetleri yükleniyor…"] = "Loading Telegram chats…",
            ["Önce bir Telegram sohbeti seçin."] = "Select a Telegram chat first.",
            ["Telegram medya listesi yükleniyor…"] = "Loading Telegram media…",
            ["Telegram genel araması yapılıyor…"] = "Searching Telegram…",
            ["Önce bir Telegram medya dosyası seçin."] = "Select a Telegram media file first.",
            ["Telegram indirmesi tamamlandı."] = "Telegram download completed.",
            ["Torrent oturumu yok"] = "No torrent session",
            ["Magnet kullanımı için yasal kullanım onayını işaretleyin."] = "Accept the lawful-use notice before opening a magnet link.",
            ["Bir magnet bağlantısı girin."] = "Enter a magnet link.",
            ["Torrent metadata bilgisi alınıyor…"] = "Loading torrent metadata…",
            ["Torrent kullanımı için yasal kullanım onayını işaretleyin."] = "Accept the lawful-use notice before opening a torrent.",
            ["Torrent dosyası bulunamadı."] = "Torrent file not found.",
            ["Torrent dosyası okunuyor…"] = "Reading the torrent file…",
            ["Önce torrent içinden bir video seçin."] = "Select a video from the torrent first.",
            ["Torrent videosunun ilk parçaları hazırlanıyor…"] = "Preparing the first video segments…",
            ["Torrent oturumu kapatıldı."] = "Torrent session closed.",
            ["Video açıldığında dosya adı otomatik çözümlenir."] = "The media name will be detected automatically when a video opens.",
            ["API hesabı henüz doğrulanmadı."] = "The API account has not been verified yet.",
            ["Sonuç yok"] = "No results",
            ["Önce bir altyazı sonucu seçin."] = "Select a subtitle result first.",
            ["Sezon paketinden bölüm ayıklanıyor…"] = "Extracting the episode from the season pack…",
            ["Altyazı indiriliyor…"] = "Downloading subtitle…",
            ["Altyazı indirilemedi."] = "Subtitle download failed.",
            ["AltyazıDB API v1.6.2 aranıyor…"] = "Searching AltyazıDB API v1.6.2…",
            ["Uygun altyazı bulunamadı."] = "No matching subtitles were found.",
            ["AltyazıDB araması başarısız."] = "AltyazıDB search failed.",
            ["API hesabı ve kota bilgisi doğrulanıyor…"] = "Checking the API account and quota…",
            ["API doğrulaması başarısız."] = "API verification failed.",
            ["Güncelleme denetlenmedi."] = "Updates have not been checked.",
            ["Güncelleme denetleniyor…"] = "Checking for updates…",
            ["Güncelleme manifesti adresi yapılandırılmadı."] = "Update manifest URL is not configured.",
            ["Güncelleme manifesti geçerli bir HTTPS adresi olmalıdır."] = "The update manifest must use a valid HTTPS URL.",
            ["AltyazıDB Player güncel."] = "ADB Player is up to date.",
            ["Kurulum dosyası indiriliyor…"] = "Downloading the installer…",
            ["Portable mod"] = "Portable mode",
            ["Kurulu / geliştirici mod"] = "Installed / developer mode",
            ["Tümü"] = "All",
            ["Filmler"] = "Movies",
            ["Diziler"] = "Series",
            ["Devam et"] = "Continue watching",
            ["İzlenmedi"] = "Unwatched",
            ["İzlendi"] = "Watched",
            ["En yeni"] = "Newest",
            ["En çok indirilen"] = "Most downloaded",
            ["Yerel"] = "Local",
            ["Bağlantı"] = "Link",
            ["Klasör"] = "Folder",
            ["Video"] = "Video",
            ["Altyazı"] = "Subtitle",
            ["Ses"] = "Audio",
            ["Dosya"] = "File",
            ["Hazır"] = "Ready",
            ["Bağlanıyor"] = "Connecting",
            ["İndiriliyor"] = "Downloading",
            ["Duraklatıldı"] = "Paused",
            ["Tamamlandı"] = "Completed",
            ["libmpv başlatılıyor…"] = "Starting libmpv…",
            ["Hazır · Video seçin, bağlantı açın veya kütüphane klasörü ekleyin."] = "Ready · Open a video, link, or library folder.",
            ["Oynatıcı hazır · Video kütüphanesi başlatılamadı."] = "The player is ready, but the video library could not be started.",
            ["Kütüphane klasörü eklenemedi."] = "The library folder could not be added.",
            ["Önce bir kütüphane klasörü seçin."] = "Select a library folder first.",
            ["Kütüphane klasörü kaldırılamadı."] = "The library folder could not be removed.",
            ["Kütüphane klasörü açılamadı."] = "The library folder could not be opened.",
            ["Oynatma motoru başlatılamadı."] = "The playback engine could not be started.",
            ["Desteklenen video dosyası bulunamadı."] = "No supported video files were found.",
            ["Oynatma listesinin sonuna ulaşıldı."] = "End of playlist reached.",
            ["Oynatma listesi temizlendi."] = "Playlist cleared.",
            ["Ekran görüntüsü için önce bir video açın."] = "Open a video before taking a screenshot.",
            ["Ekran görüntüsü alınamadı."] = "The screenshot could not be taken.",
            ["Oynatma motoru henüz hazır değil."] = "The playback engine is not ready yet.",
            ["Kaynak bulunamadı."] = "The source could not be found.",
            ["Video açılıyor…"] = "Opening video…",
            ["Kaynak açılamadı."] = "The source could not be opened.",
            ["Arabelleğe alınıyor…"] = "Buffering…",
            ["Oynatılıyor"] = "Playing",
            ["Oynatıcı komutu uygulanamadı."] = "The player command could not be applied.",
            ["Salt okunur Drive erişimi"] = "Read-only Drive access",
            ["Dropbox hesabındaki dosyalar"] = "Files in a Dropbox account",
            ["Kişisel veya iş OneDrive hesabı"] = "Personal or work OneDrive account",
            ["ABD bölgesindeki pCloud hesabı"] = "pCloud account in the US region",
            ["Avrupa bölgesindeki pCloud hesabı"] = "pCloud account in the Europe region",
            ["Uzak dosyalar"] = "Remote files",
            ["Bulut hesapları şu anda kullanılamıyor. Uygulamayı güncelleyip yeniden deneyin."] = "Cloud accounts are currently unavailable. Update the app and try again.",
            ["WebDAV sunucusuna bağlanılıyor…"] = "Connecting to the network location…",
            ["WebDAV profili silindi."] = "Network profile removed.",
            ["WebDAV adresini girin."] = "Enter the WebDAV address.",
            ["Uzak video açılamadı."] = "The remote video could not be opened.",
            ["Etkin bulut hesabı bulunamadı."] = "No active cloud account was found.",
            ["WebDAV bağlantı bilgisi bulunamadı."] = "Network connection details were not found.",
            ["Uzak klasör açılamadı."] = "The remote folder could not be opened.",
            ["AltyazıDB API hesabı doğrulanamadı."] = "The AltyazıDB API account could not be verified.",
            ["Telegram şu anda kullanılamıyor."] = "Telegram is currently unavailable.",
            ["Telegram şu anda kullanılamıyor. Uygulamayı güncelleyip yeniden deneyin."] = "Telegram is currently unavailable. Update the app and try again.",
            ["Telefon numarası girin."] = "Enter your phone number.",
            ["Ad alanı gereklidir."] = "First name is required.",
            ["Genel arama için en az iki karakter girin."] = "Enter at least two characters for a global search.",
            ["Telegram oturumu kapatılıyor…"] = "Signing out of Telegram…",
            ["Magnet hazırlanamadı."] = "The magnet link could not be prepared.",
            ["Torrent dosyası hazırlanamadı."] = "The torrent file could not be prepared.",
            ["Torrent videosu başlatılamadı."] = "The torrent video could not be started.",
            ["Tanılama paketi oluşturulamadı."] = "The diagnostics package could not be created.",
            ["Güncelleme denetimi başarısız."] = "Update check failed.",
            ["Güncelleme indirme/kurulum işlemi başarısız."] = "The update could not be downloaded or installed.",
            ["Kapalı"] = "Off",
            ["Henüz taranmadı"] = "Not scanned yet",
            ["Süre bilinmiyor"] = "Duration unknown",
            ["Telegram başlatılmadı."] = "Telegram has not been started.",
            ["Telefon numaranızı uluslararası biçimde girin."] = "Enter your phone number in international format.",
            ["E-posta doğrulama kodunu girin."] = "Enter the email verification code.",
            ["Telegram doğrulama kodunu girin."] = "Enter the Telegram verification code.",
            ["İki aşamalı doğrulama parolanızı girin."] = "Enter your two-step verification password.",
            ["Yeni Telegram hesabı için ad ve soyad girin."] = "Enter a first and last name for the new Telegram account.",
            ["Bu hesap için Telegram Premium doğrulaması gerekiyor."] = "Telegram Premium verification is required for this account.",
            ["Telegram hesabı bağlı."] = "Telegram account connected.",
            ["TDLib kapatılıyor…"] = "Closing Telegram…",
            ["Telegram oturumu kapandı."] = "Telegram session closed.",
            ["Telegram hesabı henüz kullanıma hazır değil."] = "The Telegram account is not ready yet.",
            ["Torrent oturumu bulunamadı."] = "Torrent session not found.",
            ["Torrent motoru başlatılmadı."] = "Torrent engine has not been started.",
            ["Torrent içinde desteklenen bir video dosyası bulunamadı."] = "No supported video file was found in the torrent.",
            ["Magnet bağlantısı açıldı ancak torrent içinde desteklenen bir video dosyası bulunamadı."] = "The magnet opened, but the torrent does not contain a supported video file.",
            ["Oynatılabilir torrent dosyası seçilmedi."] = "No playable torrent file was selected.",
            ["Torrent indirme hatası."] = "Torrent download error.",
            ["Torrent videosunun ilk parçaları zamanında hazırlanamadı."] = "The first video segments were not ready in time.",
            ["Torrent metadata bilgisi zamanında alınamadı."] = "Torrent metadata was not received in time.",
            ["Torrent dosya listesi okunamadı."] = "The torrent file list could not be read.",
            ["Geçerli bir magnet bağlantısı girin."] = "Enter a valid magnet link.",
            ["Bulut hesabı kaldırılamadı."] = "The cloud account could not be disconnected.",
            ["Bulut klasörü listelenemedi."] = "The cloud folder could not be listed.",
            ["Seçilen bulut öğesi video değil."] = "The selected cloud item is not a video.",
            ["Bulut dosyası indirilemedi."] = "The cloud file could not be downloaded.",
            ["Bulut hesabı kaldırılsın mı?"] = "Disconnect the cloud account?",
            ["Telegram oturumunu kapatmak istediğinizden emin misiniz?"] = "Are you sure you want to sign out of Telegram?",
            ["Torrent oturumu kapatılsın ve indirilen geçici dosyalar silinsin mi?"] = "Close the torrent session and delete temporary downloaded files?",
            ["Kurulum başlatılacak ve oynatıcı kapatılacak. Devam edilsin mi?"] = "The installer will start and the player will close. Continue?",
            ["AltyazıDB Player Güncellemesi"] = "AltyazıDB Player Update",
            ["Güncellemeyi Kur"] = "Install Update",
            ["AI çeviri"] = "AI translation",
            ["Yabancı kısımlar"] = "Foreign parts",
            ["pCloud ABD"] = "pCloud US",
            ["Harici ses seçildiğinde otomatik senkron hazırlanır."] = "Automatic sync will be prepared when you select external audio.",
            ["Otomatik ses senkronu kapalı."] = "Automatic audio sync is off.",
            ["Harici ses hazır · otomatik senkron kapalı."] = "External audio ready · automatic sync is off.",
            ["Harici ses hazır · otomatik senkron başlatılıyor…"] = "External audio ready · starting automatic sync…",
            ["Ses senkronu analiz ediliyor…"] = "Analyzing audio synchronization…",
            ["Önce bir harici ses seçin."] = "Select an external audio track first.",
            ["Otomatik ses senkronu tamamlanamadı."] = "Automatic audio sync could not be completed.",
            ["Otomatik ses senkronu hazır."] = "Automatic audio sync is ready.",
            ["Otomatik ses senkronu için FFmpeg bileşeni bulunamadı."] = "FFmpeg is required for automatic audio sync.",
            ["Ses farklı bir kurguya ait olabilir. Otomatik canlı düzeltme uygulanmadı."] = "The audio may belong to a different cut. Live automatic correction was not applied.",
            ["Otomatik senkron sonucu yeterince güvenilir değil; ses değiştirilmedi."] = "The automatic sync result is not reliable enough; audio timing was not changed.",
            ["Sesler arasında güvenilir ortak zaman noktası bulunamadı."] = "No reliable common timing point was found between the audio tracks.",
            ["Video ile harici ses aynı içerik olarak doğrulanamadı."] = "The video and external audio could not be verified as the same content.",
            ["Ses gecikmesi ve zaman kayması otomatik düzeltiliyor."] = "Audio delay and progressive drift are being corrected automatically.",
            ["pCloud Avrupa"] = "pCloud Europe",
        };

    private static readonly (string Turkish, string English)[] EnglishPrefixes =
    [
        ("Açılıyor: ", "Opening: "),
        ("Algılandı: ", "Detected: "),
        ("İçerik düzeltildi: ", "Content corrected: "),
        ("Altyazı indirildi ve eklendi: ", "Subtitle downloaded and attached: "),
        ("Telegram dosyası indiriliyor: ", "Downloading Telegram file: "),
        ("Telegram dosyası hazır: ", "Telegram file ready: "),
        ("Telegram indiriliyor: ", "Telegram download: "),
        ("Torrent oynatılıyor: ", "Playing torrent: "),
        ("Güncelleme denetlenemedi: ", "Update check failed: "),
        ("Güncelleme indirilemedi: ", "Update download failed: "),
        ("Yeni sürüm bulundu: ", "New version available: "),
        ("İndirme doğrulandı: ", "Download verified: "),
        ("Tanılama paketi oluşturuldu: ", "Diagnostics package created: "),
        ("Kaldığınız yerden devam ediliyor: ", "Resuming from: "),
        ("Ekran görüntüsü kaydedildi: ", "Screenshot saved: "),
        ("Bunu mu aramak istediniz? ", "Did you mean? "),
        ("Uzak altyazı indirilemedi: ", "Remote subtitle download failed: "),
        ("Uzak ses indirilemedi: ", "Remote audio download failed: "),
        ("Ses otomatik senkronize edildi (", "Audio automatically synchronized ("),
        ("Ses gecikmesi elle ayarlandı: ", "Audio delay adjusted manually: ")
    ];

    private static readonly IReadOnlyDictionary<string, string> TurkishExact =
        EnglishExact
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    private static readonly (string English, string Turkish)[] TurkishPrefixes =
        EnglishPrefixes.Select(pair => (pair.English, pair.Turkish)).ToArray();

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("tr-TR", "Türkçe"),
        new("en-US", "English")
    ];

    public static string CurrentLanguageCode { get; private set; } = "tr-TR";
    public static bool IsEnglish => CurrentLanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    public static event EventHandler? LanguageChanged;

    public static void ApplyLanguage(string? languageCode)
    {
        var code = SupportedLanguages.Any(item => item.Code.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
            ? SupportedLanguages.First(item => item.Code.Equals(languageCode, StringComparison.OrdinalIgnoreCase)).Code
            : "tr-TR";

        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        if (System.Windows.Application.Current is not null)
        {
            var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(dictionary =>
                dictionary.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
            var replacement = new ResourceDictionary
            {
                Source = new Uri($"{DictionaryPrefix}{code}.xaml", UriKind.Relative)
            };

            if (existing is null)
            {
                dictionaries.Insert(0, replacement);
            }
            else
            {
                var index = dictionaries.IndexOf(existing);
                dictionaries[index] = replacement;
            }
        }

        CurrentLanguageCode = code;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is string value)
        {
            return value;
        }

        return key;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string TranslateMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        if (!IsEnglish)
        {
            return TranslateKnownEnglishToTurkish(value);
        }

        if (EnglishExact.TryGetValue(value, out var exact))
        {
            return exact;
        }

        foreach (var (turkish, english) in EnglishPrefixes)
        {
            if (value.StartsWith(turkish, StringComparison.Ordinal))
            {
                return english + value[turkish.Length..];
            }
        }

        var videoCount = Regex.Match(value, @"^(\d+) video listelendi\.$");
        if (videoCount.Success)
        {
            return $"{videoCount.Groups[1].Value} videos listed.";
        }

        var itemCount = Regex.Match(value, @"^(\d+) öğe bulundu\.$");
        if (itemCount.Success)
        {
            return $"{itemCount.Groups[1].Value} items found.";
        }

        var subtitleCount = Regex.Match(value, @"^(\d+) altyazı$");
        if (subtitleCount.Success)
        {
            return $"{subtitleCount.Groups[1].Value} subtitles";
        }

        var scanComplete = Regex.Match(value, @"^Tarama tamamlandı · (\d+) video$");
        if (scanComplete.Success)
        {
            return $"Scan completed · {scanComplete.Groups[1].Value} videos";
        }

        var scanning = Regex.Match(value, @"^Taranıyor · (\d+) dosya$");
        if (scanning.Success)
        {
            return $"Scanning · {scanning.Groups[1].Value} files";
        }

        var telegramChats = Regex.Match(value, @"^(\d+) Telegram sohbeti yüklendi\.$");
        if (telegramChats.Success)
        {
            return $"{telegramChats.Groups[1].Value} Telegram chats loaded.";
        }

        var telegramMedia = Regex.Match(value, @"^(\d+) medya bulundu: (.+)$");
        if (telegramMedia.Success)
        {
            return $"{telegramMedia.Groups[1].Value} media items found: {telegramMedia.Groups[2].Value}";
        }

        var telegramGlobal = Regex.Match(value, @"^(\d+) genel Telegram sonucu bulundu\.$");
        if (telegramGlobal.Success)
        {
            return $"{telegramGlobal.Groups[1].Value} Telegram results found.";
        }

        var subtitlePage = Regex.Match(value, @"^(\d+) altyazı kaydı · Sayfa (\d+)/(\d+)(.*)$");
        if (subtitlePage.Success)
        {
            return $"{subtitlePage.Groups[1].Value} subtitle records · Page {subtitlePage.Groups[2].Value}/{subtitlePage.Groups[3].Value}{subtitlePage.Groups[4].Value}";
        }

        var providerAuthorization = Regex.Match(value, @"^(.+) yetkilendirmesi için tarayıcı açılıyor…$");
        if (providerAuthorization.Success)
        {
            return $"Opening the browser to authorize {providerAuthorization.Groups[1].Value}…";
        }

        var accountConnected = Regex.Match(value, @"^(.+) hesabı bağlandı\.$");
        if (accountConnected.Success)
        {
            return $"{accountConnected.Groups[1].Value} account connected.";
        }

        var loadingProviderFiles = Regex.Match(value, @"^(.+) dosyaları yükleniyor…$");
        if (loadingProviderFiles.Success)
        {
            return $"Loading {loadingProviderFiles.Groups[1].Value} files…";
        }

        var disconnectAccount = Regex.Match(value, @"^(.+) hesabının bağlantısı kaldırılsın mı\?$");
        if (disconnectAccount.Success)
        {
            return $"Disconnect {disconnectAccount.Groups[1].Value}?";
        }

        var selectRemote = Regex.Match(value, @"^Önce bir (altyazı|ses) dosyası seçin\.$");
        if (selectRemote.Success)
        {
            return selectRemote.Groups[1].Value == "altyazı"
                ? "Select a subtitle file first."
                : "Select an audio file first.";
        }

        var downloadingRemote = Regex.Match(value, @"^Uzak (altyazı|ses) indiriliyor…$");
        if (downloadingRemote.Success)
        {
            return downloadingRemote.Groups[1].Value == "altyazı"
                ? "Downloading remote subtitle…"
                : "Downloading remote audio…";
        }

        var remoteAttached = Regex.Match(value, @"^(altyazı|ses) indirildi ve eklendi: (.+)$");
        if (remoteAttached.Success)
        {
            return remoteAttached.Groups[1].Value == "altyazı"
                ? $"Subtitle downloaded and attached: {remoteAttached.Groups[2].Value}"
                : $"Audio downloaded and attached: {remoteAttached.Groups[2].Value}";
        }

        var track = Regex.Match(value, @"^Parça (\d+)$");
        if (track.Success)
        {
            return $"Track {track.Groups[1].Value}";
        }

        var episode = Regex.Match(value, @"^Bölüm (.+)$");
        if (episode.Success)
        {
            return $"Episode {episode.Groups[1].Value}";
        }

        var apiQuota = Regex.Match(value, @"^(.+) · (.+) · Dakika (.+) · Saat (.+) · Gün (.+)$");
        if (apiQuota.Success)
        {
            return $"{apiQuota.Groups[1].Value} · {apiQuota.Groups[2].Value} · Minute {apiQuota.Groups[3].Value} · Hour {apiQuota.Groups[4].Value} · Day {apiQuota.Groups[5].Value}";
        }

        var removeLibraryFolder = Regex.Match(value, @"^'(.+)' kütüphaneden kaldırılacak\. Dosyalar diskinizden silinmeyecek\. Devam edilsin mi\?$");
        if (removeLibraryFolder.Success)
        {
            return $"Remove '{removeLibraryFolder.Groups[1].Value}' from the library? Files will not be deleted from disk.";
        }

        var updatePrompt = Regex.Match(value, @"^AltyazıDB Player (.+) indirilecek ve kurulum başlatılacak\. Devam edilsin mi\?$");
        if (updatePrompt.Success)
        {
            return $"AltyazıDB Player {updatePrompt.Groups[1].Value} will be downloaded and the installer will start. Continue?";
        }

        var authorization = Regex.Match(value, @"^(NotStarted|WaitingForTdlibParameters|WaitingForPhoneNumber|WaitingForEmailAddress|WaitingForEmailCode|WaitingForCode|WaitingForPassword|WaitingForRegistration|WaitingForOtherDeviceConfirmation|WaitingForPremiumPurchase|Ready|LoggingOut|Closing|Closed): (.+)$");
        if (authorization.Success)
        {
            var stage = authorization.Groups[1].Value switch
            {
                "NotStarted" => "Not started",
                "WaitingForTdlibParameters" => "Preparing",
                "WaitingForPhoneNumber" => "Phone number",
                "WaitingForEmailAddress" => "Email address",
                "WaitingForEmailCode" => "Email code",
                "WaitingForCode" => "Verification code",
                "WaitingForPassword" => "Two-step verification",
                "WaitingForRegistration" => "Registration",
                "WaitingForOtherDeviceConfirmation" => "Device confirmation",
                "WaitingForPremiumPurchase" => "Premium verification",
                "Ready" => "Ready",
                "LoggingOut" => "Signing out",
                "Closing" => "Closing",
                "Closed" => "Closed",
                _ => authorization.Groups[1].Value
            };
            return $"{stage}: {TranslateMessage(authorization.Groups[2].Value)}";
        }

        return value;
    }

    private static string TranslateKnownEnglishToTurkish(string value)
    {
        if (TurkishExact.TryGetValue(value, out var exact))
        {
            return exact;
        }

        foreach (var (english, turkish) in TurkishPrefixes)
        {
            if (value.StartsWith(english, StringComparison.Ordinal))
            {
                return turkish + value[english.Length..];
            }
        }

        var videoCount = Regex.Match(value, @"^(\d+) videos listed\.$");
        if (videoCount.Success)
        {
            return $"{videoCount.Groups[1].Value} video listelendi.";
        }

        var itemCount = Regex.Match(value, @"^(\d+) items found\.$");
        if (itemCount.Success)
        {
            return $"{itemCount.Groups[1].Value} öğe bulundu.";
        }

        var subtitleCount = Regex.Match(value, @"^(\d+) subtitles$");
        if (subtitleCount.Success)
        {
            return $"{subtitleCount.Groups[1].Value} altyazı";
        }

        var scanComplete = Regex.Match(value, @"^Scan completed · (\d+) videos$");
        if (scanComplete.Success)
        {
            return $"Tarama tamamlandı · {scanComplete.Groups[1].Value} video";
        }

        var scanning = Regex.Match(value, @"^Scanning · (\d+) files$");
        if (scanning.Success)
        {
            return $"Taranıyor · {scanning.Groups[1].Value} dosya";
        }

        var telegramChats = Regex.Match(value, @"^(\d+) Telegram chats loaded\.$");
        if (telegramChats.Success)
        {
            return $"{telegramChats.Groups[1].Value} Telegram sohbeti yüklendi.";
        }

        var telegramMedia = Regex.Match(value, @"^(\d+) media items found: (.+)$");
        if (telegramMedia.Success)
        {
            return $"{telegramMedia.Groups[1].Value} medya bulundu: {telegramMedia.Groups[2].Value}";
        }

        var telegramGlobal = Regex.Match(value, @"^(\d+) Telegram results found\.$");
        if (telegramGlobal.Success)
        {
            return $"{telegramGlobal.Groups[1].Value} genel Telegram sonucu bulundu.";
        }

        var track = Regex.Match(value, @"^Track (\d+)$");
        if (track.Success)
        {
            return $"Parça {track.Groups[1].Value}";
        }

        var episode = Regex.Match(value, @"^Episode (.+)$");
        if (episode.Success)
        {
            return $"Bölüm {episode.Groups[1].Value}";
        }

        var providerAuthorization = Regex.Match(value, @"^Opening the browser to authorize (.+)…$");
        if (providerAuthorization.Success)
        {
            return $"{providerAuthorization.Groups[1].Value} yetkilendirmesi için tarayıcı açılıyor…";
        }

        var accountConnected = Regex.Match(value, @"^(.+) account connected\.$");
        if (accountConnected.Success)
        {
            return $"{accountConnected.Groups[1].Value} hesabı bağlandı.";
        }

        var loadingProviderFiles = Regex.Match(value, @"^Loading (.+) files…$");
        if (loadingProviderFiles.Success)
        {
            return $"{loadingProviderFiles.Groups[1].Value} dosyaları yükleniyor…";
        }

        var authorization = Regex.Match(
            value,
            @"^(NotStarted|WaitingForTdlibParameters|WaitingForPhoneNumber|WaitingForEmailAddress|WaitingForEmailCode|WaitingForCode|WaitingForPassword|WaitingForRegistration|WaitingForOtherDeviceConfirmation|WaitingForPremiumPurchase|LoggingOut|Not started|Preparing|Phone number|Email address|Email code|Verification code|Two-step verification|Registration|Device confirmation|Premium verification|Ready|Signing out|Closing|Closed): (.+)$");
        if (authorization.Success)
        {
            var stage = authorization.Groups[1].Value switch
            {
                "NotStarted" or "Not started" => "Başlatılmadı",
                "WaitingForTdlibParameters" or "Preparing" => "Hazırlanıyor",
                "WaitingForPhoneNumber" or "Phone number" => "Telefon numarası",
                "WaitingForEmailAddress" or "Email address" => "E-posta adresi",
                "WaitingForEmailCode" or "Email code" => "E-posta kodu",
                "WaitingForCode" or "Verification code" => "Doğrulama kodu",
                "WaitingForPassword" or "Two-step verification" => "İki aşamalı doğrulama",
                "WaitingForRegistration" or "Registration" => "Kayıt",
                "WaitingForOtherDeviceConfirmation" or "Device confirmation" => "Cihaz doğrulaması",
                "WaitingForPremiumPurchase" or "Premium verification" => "Premium doğrulaması",
                "Ready" => "Hazır",
                "LoggingOut" or "Signing out" => "Oturum kapatılıyor",
                "Closing" => "Kapatılıyor",
                "Closed" => "Kapalı",
                _ => authorization.Groups[1].Value
            };
            return $"{stage}: {TranslateKnownEnglishToTurkish(authorization.Groups[2].Value)}";
        }

        return value;
    }

    public static string ToUserFacingText(string? value)
    {
        var translated = TranslateMessage(value);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return string.Empty;
        }

        var firstParagraph = translated
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? translated;

        if (Regex.IsMatch(firstParagraph, @"(?:[A-Za-z]:\\|/runtimes/|\.dll\b|\.exe\b|System\.[A-Za-z]| at [A-Za-z0-9_.]+\()", RegexOptions.IgnoreCase))
        {
            return Get("Status.TechnicalDetailsHidden");
        }

        return firstParagraph.Length > 260
            ? firstParagraph[..257] + "…"
            : firstParagraph;
    }
}
