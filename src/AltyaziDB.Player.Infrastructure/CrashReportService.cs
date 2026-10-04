using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Infrastructure;

public sealed class CrashReportService
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private int _isWriting;

    public CrashReportService(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string Write(Exception exception, string origin)
    {
        if (Interlocked.Exchange(ref _isWriting, 1) != 0)
        {
            return string.Empty;
        }

        try
        {
            Directory.CreateDirectory(_paths.CrashDirectory);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            var file = Path.Combine(_paths.CrashDirectory, $"crash-{stamp}.txt");
            var assembly = Assembly.GetEntryAssembly()?.GetName();
            var text = new StringBuilder()
                .AppendLine("ADB Player for Windows - Çökme Raporu")
                .AppendLine($"Zaman: {DateTimeOffset.Now:O}")
                .AppendLine($"Kaynak: {origin}")
                .AppendLine($"Sürüm: {assembly?.Version}")
                .AppendLine($"Dağıtım: {(_paths.IsPortable ? "Portable" : "Kurulu")}")
                .AppendLine($"İşletim sistemi: {Environment.OSVersion}")
                .AppendLine($"İşlem mimarisi: {RuntimeInformation.ProcessArchitecture}")
                .AppendLine($".NET: {Environment.Version}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(file, text, new UTF8Encoding(true));
            _logger.Error($"Çökme raporu yazıldı: {file}", exception);
            return file;
        }
        catch (Exception writeException)
        {
            _logger.Error("Çökme raporu yazılamadı.", writeException);
            return string.Empty;
        }
        finally
        {
            Volatile.Write(ref _isWriting, 0);
        }
    }

    public string CreateDiagnosticsBundle()
    {
        Directory.CreateDirectory(_paths.CrashDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(_paths.CrashDirectory, $"ADB-Player-Diagnostics-{stamp}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        AddDirectory(archive, _paths.LogDirectory, "logs");
        AddDirectory(archive, _paths.CrashDirectory, "crashes", zipPath);

        var environment = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            os = Environment.OSVersion.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = Environment.Version.ToString(),
            portable = _paths.IsPortable,
            baseDirectory = AppContext.BaseDirectory
        };
        var entry = archive.CreateEntry("environment.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true }));
        return zipPath;
    }

    private static void AddDirectory(ZipArchive archive, string directory, string prefix, string? excludedPath = null)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.IsNullOrWhiteSpace(excludedPath) &&
                string.Equals(Path.GetFullPath(file), Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entryName = $"{prefix}/{Path.GetFileName(file)}";
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }
}
