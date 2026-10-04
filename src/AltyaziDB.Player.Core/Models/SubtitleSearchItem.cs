namespace AltyaziDB.Player.Core.Models;

public sealed record SubtitleSearchItem(
    long Id,
    string Language,
    int? Season,
    string? Episode,
    string? Fps,
    string? Duration,
    string Translator,
    string Uploader,
    string StreamUrl,
    string DownloadUrl,
    string ArchiveUrl,
    string? ContentType,
    int Downloads,
    bool IsPackage,
    bool HearingImpaired,
    bool Forced,
    bool AiTranslation,
    bool ForeignParts,
    IReadOnlyList<string> Releases,
    IReadOnlyList<string> VersionGroups,
    IReadOnlyList<SubtitleReleaseVersion> Versions,
    string? TranslatorNote,
    DateTimeOffset? Date,
    double CompatibilityScore)
{
    public string Title
    {
        get
        {
            var version = Versions.FirstOrDefault();
            if (version is not null && !string.IsNullOrWhiteSpace(version.File)) return version.File;
            var release = Releases.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(release)) return release;
            var groups = string.Join(", ", VersionGroups);
            return string.IsNullOrWhiteSpace(groups) ? $"Altyazı #{Id}" : groups;
        }
    }

    public string EpisodeLabel
    {
        get
        {
            if (IsPackage) return Season is null ? "Sezon paketi" : $"S{Season.Value:00} sezon paketi";
            return Season is not null && int.TryParse(Episode, out var episode)
                ? $"S{Season.Value:00}E{episode:00}"
                : string.IsNullOrWhiteSpace(Episode) ? string.Empty : $"Bölüm {Episode}";
        }
    }

    public string Flags
    {
        get
        {
            var values = new List<string>();
            if (Forced) values.Add("Forced");
            if (HearingImpaired) values.Add("CC");
            if (AiTranslation) values.Add("AI çeviri");
            if (ForeignParts) values.Add("Yabancı kısımlar");
            if (IsPackage) values.Add("Paket");
            return string.Join(" · ", values);
        }
    }

    public string MetaLabel
    {
        get
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(EpisodeLabel)) values.Add(EpisodeLabel);
            if (!string.IsNullOrWhiteSpace(Duration)) values.Add(Duration);
            if (!string.IsNullOrWhiteSpace(Fps)) values.Add($"{Fps} FPS");
            values.Add($"{Downloads:N0} indirme");
            if (!string.IsNullOrWhiteSpace(Flags)) values.Add(Flags);
            return string.Join(" · ", values);
        }
    }

    public string Extension
    {
        get
        {
            foreach (var value in new[] { DownloadUrl, StreamUrl })
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) continue;
                var extension = System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (extension is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub") return extension;
            }
            return ".srt";
        }
    }
}
