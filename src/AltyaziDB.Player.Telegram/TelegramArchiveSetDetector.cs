using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Telegram;

/// <summary>
/// Telegram belge adlarından çok parçalı arşiv setlerini belirler. Eşleştirme
/// yalnız aynı sohbet ve birebir aynı temel ad içinde yapılır; farklı sohbetler
/// arasında otomatik parça birleştirilmez.
/// </summary>
public static partial class TelegramArchiveSetDetector
{
    public static IReadOnlyList<TelegramMediaItem> Group(IReadOnlyList<TelegramMediaItem> media)
    {
        if (media.Count == 0) return media;

        var descriptors = media
            .Select(item => TryDescribe(item, out var descriptor) ? descriptor : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var consumed = new HashSet<TelegramMediaItem>();
        var aggregates = new List<TelegramMediaItem>();

        foreach (var group in descriptors.GroupBy(
                     item => new ArchiveKey(item.Item.ChatId, item.Format, item.BaseName, item.Scheme),
                     ArchiveKeyComparer.Instance))
        {
            var parts = group.ToArray();
            if (!ShouldAggregate(parts)) continue;
            var aggregate = BuildAggregate(parts);
            aggregates.Add(aggregate);
            foreach (var part in parts) consumed.Add(part.Item);
        }

        return media
            .Where(item => !consumed.Contains(item))
            .Concat(aggregates)
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static TelegramMediaItem BuildManualSet(IReadOnlyList<TelegramMediaItem> selectedParts)
    {
        var grouped = Group(selectedParts);
        var aggregates = grouped.Where(item => item.Kind == TelegramMediaKind.SplitArchive).ToArray();
        var selectedCount = selectedParts.DistinctBy(item => item.FileId).Count();
        if (aggregates.Length != 1 || aggregates[0].ArchiveParts?.DistinctBy(item => item.FileId).Count() != selectedCount)
            throw new InvalidOperationException("Seçilen dosyalar tek bir arşiv seti oluşturmuyor.");
        return aggregates[0];
    }

    public static bool IsArchivePartFileName(string fileName) =>
        TryDescribeFileName(fileName, out _, out _, out _, out _, out _);

    private static bool ShouldAggregate(IReadOnlyList<PartDescriptor> parts)
    {
        if (parts.Count == 0) return false;
        return parts[0].Format switch
        {
            TelegramArchiveFormat.SplitZip => parts.Any(item => !item.IsFinal),
            TelegramArchiveFormat.MultipartRar => parts[0].Scheme == "rar-part" || parts.Count > 1,
            TelegramArchiveFormat.Split7Zip => true,
            _ => false
        };
    }

    private static TelegramMediaItem BuildAggregate(IReadOnlyList<PartDescriptor> descriptors)
    {
        var format = descriptors[0].Format;
        var baseName = descriptors[0].BaseName;
        var normalized = NormalizeSequences(descriptors);
        var duplicateSequence = normalized.GroupBy(item => item.Sequence).Any(group => group.Count() > 1);
        var uniqueSequences = normalized.Select(item => item.Sequence).Distinct().Order().ToArray();
        var contiguous = uniqueSequences.Length > 0 && uniqueSequences[0] == 0 &&
                         uniqueSequences.SequenceEqual(Enumerable.Range(0, uniqueSequences[^1] + 1));
        var finalCount = normalized.Count(item => item.IsFinal);
        var complete = format switch
        {
            TelegramArchiveFormat.SplitZip => contiguous && finalCount == 1,
            TelegramArchiveFormat.MultipartRar => contiguous,
            TelegramArchiveFormat.Split7Zip => contiguous,
            _ => false
        };
        var ambiguous = duplicateSequence || finalCount > 1;
        var orderedParts = normalized
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Item.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Item)
            .ToArray();
        var representative = normalized.FirstOrDefault(item => item.IsFinal)?.Item ?? orderedParts[0];
        var totalSize = orderedParts.Sum(item => Math.Max(0, item.Size));

        return new TelegramMediaItem(
            representative.ChatId,
            orderedParts.Max(item => item.MessageId),
            representative.FileId,
            representative.ChatTitle,
            baseName,
            baseName,
            "application/x-split-archive",
            totalSize,
            0,
            orderedParts.Max(item => item.Date),
            TelegramMediaKind.SplitArchive,
            ArchiveFormat: format,
            ArchiveBaseName: baseName,
            ArchiveParts: orderedParts,
            ArchiveSetComplete: complete,
            ArchiveSetAmbiguous: ambiguous);
    }

    private static IReadOnlyList<PartDescriptor> NormalizeSequences(IReadOnlyList<PartDescriptor> descriptors)
    {
        if (descriptors[0].Format != TelegramArchiveFormat.SplitZip) return descriptors;
        var numbered = descriptors.Where(item => !item.IsFinal).ToArray();
        var finalSequence = numbered.Length == 0 ? 0 : numbered.Max(item => item.Sequence) + 1;
        return descriptors
            .Select(item => item.IsFinal ? item with { Sequence = finalSequence } : item)
            .ToArray();
    }

    private static bool TryDescribe(TelegramMediaItem item, out PartDescriptor descriptor)
    {
        descriptor = null!;
        if (!TryDescribeFileName(item.FileName, out var format, out var baseName, out var sequence, out var isFinal, out var scheme))
            return false;
        descriptor = new PartDescriptor(item, format, baseName, sequence, isFinal, scheme);
        return true;
    }

    private static bool TryDescribeFileName(
        string fileName,
        out TelegramArchiveFormat format,
        out string baseName,
        out int sequence,
        out bool isFinal,
        out string scheme)
    {
        format = TelegramArchiveFormat.None;
        baseName = string.Empty;
        sequence = -1;
        isFinal = false;
        scheme = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var match = SplitZipPartRegex().Match(fileName);
        if (match.Success && TrySequence(match, "number", -1, out sequence))
        {
            format = TelegramArchiveFormat.SplitZip;
            baseName = match.Groups["base"].Value;
            scheme = "zip";
            return true;
        }

        match = SplitZipFinalRegex().Match(fileName);
        if (match.Success)
        {
            format = TelegramArchiveFormat.SplitZip;
            baseName = match.Groups["base"].Value;
            sequence = int.MaxValue;
            isFinal = true;
            scheme = "zip";
            return true;
        }

        match = MultipartRarRegex().Match(fileName);
        if (match.Success && TrySequence(match, "number", -1, out sequence))
        {
            format = TelegramArchiveFormat.MultipartRar;
            baseName = match.Groups["base"].Value;
            scheme = "rar-part";
            return true;
        }

        match = LegacyRarPartRegex().Match(fileName);
        if (match.Success && TrySequence(match, "number", 1, out sequence))
        {
            format = TelegramArchiveFormat.MultipartRar;
            baseName = match.Groups["base"].Value;
            scheme = "rar-legacy";
            return true;
        }

        match = LegacyRarFirstRegex().Match(fileName);
        if (match.Success)
        {
            format = TelegramArchiveFormat.MultipartRar;
            baseName = match.Groups["base"].Value;
            sequence = 0;
            scheme = "rar-legacy";
            return true;
        }

        match = Split7ZipRegex().Match(fileName);
        if (match.Success && TrySequence(match, "number", -1, out sequence))
        {
            format = TelegramArchiveFormat.Split7Zip;
            baseName = match.Groups["base"].Value;
            scheme = "7z";
            return true;
        }

        return false;
    }

    private static bool TrySequence(Match match, string groupName, int adjustment, out int sequence)
    {
        sequence = -1;
        return int.TryParse(match.Groups[groupName].Value, out var number) && number > 0 &&
               (sequence = number + adjustment) >= 0;
    }

    [GeneratedRegex(@"^(?<base>.+)\.z(?<number>\d{2,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitZipPartRegex();

    [GeneratedRegex(@"^(?<base>.+)\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitZipFinalRegex();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<number>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartRarRegex();

    [GeneratedRegex(@"^(?<base>.+)\.r(?<number>\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRarPartRegex();

    [GeneratedRegex(@"^(?<base>.+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRarFirstRegex();

    [GeneratedRegex(@"^(?<base>.+\.7z)\.(?<number>\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Split7ZipRegex();

    private sealed record PartDescriptor(
        TelegramMediaItem Item,
        TelegramArchiveFormat Format,
        string BaseName,
        int Sequence,
        bool IsFinal,
        string Scheme);

    private sealed record ArchiveKey(long ChatId, TelegramArchiveFormat Format, string BaseName, string Scheme);

    private sealed class ArchiveKeyComparer : IEqualityComparer<ArchiveKey>
    {
        public static ArchiveKeyComparer Instance { get; } = new();

        public bool Equals(ArchiveKey? left, ArchiveKey? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            left.ChatId == right.ChatId && left.Format == right.Format &&
            left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
            left.BaseName.Equals(right.BaseName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ArchiveKey key) => HashCode.Combine(
            key.ChatId,
            key.Format,
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Scheme),
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.BaseName));
    }
}
