using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Infrastructure;

public sealed class HttpUpdateService : IUpdateService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private bool _disposed;

    public HttpUpdateService(IAppLogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ADB-Player-Windows", CurrentVersion.ToString(3)));
    }

    public Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        string channel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(
                false,
                false,
                CurrentVersion,
                null,
                null,
                "Güncelleme manifesti adresi yapılandırılmadı.");
        }

        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Güncelleme manifesti geçerli bir HTTPS adresi olmalıdır.");
        }

        _logger.Info($"Güncelleme manifesti denetleniyor: {uri}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Güncelleme manifesti okunamadı.");

        ValidateManifest(manifest, channel);
        var available = ParseVersion(manifest.Version);
        var current = CurrentVersion;
        var hasUpdate = available > current;

        if (manifest.MinimumWindowsBuild > 0 &&
            Environment.OSVersion.Version.Build < manifest.MinimumWindowsBuild)
        {
            return new UpdateCheckResult(
                true,
                false,
                current,
                available,
                manifest,
                $"{manifest.Version} sürümü Windows build {manifest.MinimumWindowsBuild} veya sonrasını gerektiriyor.");
        }

        return new UpdateCheckResult(
            true,
            hasUpdate,
            current,
            available,
            manifest,
            hasUpdate
                ? $"Yeni sürüm bulundu: {manifest.Version}"
                : "ADB Player güncel.");
    }

    public Task<UpdateDownloadResult> DownloadInstallerAsync(
        UpdateManifest manifest,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateHttpsDownload(manifest.InstallerUrl, manifest.InstallerSha256);
        var fileName = GetSafeDownloadFileName(
            manifest.InstallerUrl,
            $"ADB-Player-Setup-v{manifest.Version}-x64.exe");
        return DownloadVerifiedAsync(
            manifest.InstallerUrl,
            manifest.InstallerSha256,
            destinationDirectory,
            fileName,
            progress,
            cancellationToken);
    }

    public Task<UpdateDownloadResult> DownloadPortableAsync(
        UpdateManifest manifest,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateHttpsDownload(manifest.PortableUrl, manifest.PortableSha256);
        var fileName = GetSafeDownloadFileName(
            manifest.PortableUrl,
            $"ADB-Player-Portable-v{manifest.Version}-x64.zip");
        return DownloadVerifiedAsync(
            manifest.PortableUrl,
            manifest.PortableSha256,
            destinationDirectory,
            fileName,
            progress,
            cancellationToken);
    }

    private async Task<UpdateDownloadResult> DownloadVerifiedAsync(
        string url,
        string expectedSha256,
        string destinationDirectory,
        string fileName,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        var uri = new Uri(url, UriKind.Absolute);
        var destination = Path.Combine(destinationDirectory, fileName);
        var partial = destination + ".partial";

        try
        {
            using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             partial,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 128,
                             true))
            {
                var buffer = new byte[1024 * 128];
                long written = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (count <= 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    written += count;
                    if (total is > 0)
                    {
                        progress?.Report(Math.Clamp(written * 100d / total.Value, 0, 100));
                    }
                }
            }

            var actualHash = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(NormalizeHash(expectedSha256), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"İndirilen güncelleme dosyasının SHA-256 doğrulaması başarısız. Beklenen: {expectedSha256}, bulunan: {actualHash}");
            }

            File.Move(partial, destination, true);
            _logger.Info($"Güncelleme dosyası indirildi ve doğrulandı: {destination}");
            var info = new FileInfo(destination);
            progress?.Report(100);
            return new UpdateDownloadResult(destination, actualHash, info.Length);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static string GetSafeDownloadFileName(string url, string fallback)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(candidate)
                && candidate.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                return candidate;
            }
        }

        return fallback;
    }

    public void LaunchInstaller(string installerPath, bool silent)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Güncelleme kurulum dosyası bulunamadı.", installerPath);
        }

        var arguments = silent
            ? "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
            : "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory
        });
    }

    private static void ValidateManifest(UpdateManifest manifest, string requestedChannel)
    {
        if (manifest.Schema != 1)
        {
            throw new InvalidDataException($"Desteklenmeyen güncelleme manifesti şeması: {manifest.Schema}");
        }

        _ = ParseVersion(manifest.Version);
        var expectedChannel = string.IsNullOrWhiteSpace(requestedChannel) ? "stable" : requestedChannel.Trim();
        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Manifest kanalı '{manifest.Channel}', seçili kanal '{expectedChannel}' ile eşleşmiyor.");
        }

        ValidateHttpsDownload(manifest.InstallerUrl, manifest.InstallerSha256);
        ValidateHttpsDownload(manifest.PortableUrl, manifest.PortableSha256);
    }

    private static void ValidateHttpsDownload(string url, string sha256)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Kurulum dosyası adresi geçerli bir HTTPS adresi değil.");
        }

        if (NormalizeHash(sha256).Length != 64)
        {
            throw new InvalidDataException("Kurulum dosyası için geçerli bir SHA-256 değeri bulunmuyor.");
        }
    }

    private static Version ParseVersion(string value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var separator = normalized.IndexOfAny(new[] { '-', '+' });
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        if (!Version.TryParse(normalized, out var version))
        {
            throw new InvalidDataException($"Geçersiz sürüm değeri: {value}");
        }

        return version;
    }

    private static string NormalizeHash(string value) =>
        new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Geçici dosya temizliği ana hatayı gölgelememeli.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
