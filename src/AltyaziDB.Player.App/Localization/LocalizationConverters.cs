using System.Globalization;
using System.Windows.Data;

namespace AltyaziDB.Player.App.Localization;

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Some view-model values are resource keys (for example
        // Home.ContinueWatching.Remote). Resolve those keys in every UI language
        // instead of only passing them through the English message translator.
        var localized = LocalizationManager.Get(text);
        return !string.Equals(localized, text, StringComparison.Ordinal)
            ? localized
            : LocalizationManager.TranslateMessage(text);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class UserFacingTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LocalizationManager.ToUserFacingText(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class UpdateChannelTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var channel = value?.ToString()?.Trim().ToLowerInvariant();
        return channel switch
        {
            "stable" => LocalizationManager.Get("Update.Channel.Stable"),
            "preview" => LocalizationManager.Get("Update.Channel.Preview"),
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class CloudMetadataTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var token = raw.ToLowerInvariant();
        var facet = parameter?.ToString();
        var key = facet switch
        {
            "mediaType" => token switch
            {
                "movie" => "Cloud.Metadata.MediaType.Movie",
                "series" => "Cloud.Metadata.MediaType.Series",
                "episode" => "Cloud.Metadata.MediaType.Episode",
                "anime" => "Cloud.Metadata.MediaType.Anime",
                "unknown" => "Cloud.Metadata.MediaType.Unknown",
                _ => null
            },
            "sourceKind" => token switch
            {
                "catalog" => "Cloud.Metadata.Source.Catalog",
                "local" => "Cloud.Metadata.Source.Local",
                "cloud" => "Cloud.Metadata.Source.Cloud",
                "web" or "link" => "Cloud.Metadata.Source.Web",
                "addon" => "Cloud.Metadata.Source.Addon",
                "torrent" => "Cloud.Metadata.Source.Torrent",
                "telegram" => "Cloud.Metadata.Source.Telegram",
                "webdav" or "network" => "Cloud.Metadata.Source.Network",
                "unknown" => "Cloud.Metadata.Source.Unknown",
                _ => null
            },
            _ => null
        };

        return key is null ? raw : LocalizationManager.Get(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class RemoteItemIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▰" : "●";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
