using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AltyaziDB.Player.App.Converters;

public sealed class LocalizedDateTimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is null || values[0] == DependencyProperty.UnsetValue)
            return string.Empty;

        DateTime localTime;
        switch (values[0])
        {
            case DateTimeOffset offset:
                localTime = offset.LocalDateTime;
                break;
            case DateTime dateTime:
                localTime = dateTime.Kind == DateTimeKind.Utc ? dateTime.ToLocalTime() : dateTime;
                break;
            default:
                return string.Empty;
        }

        var languageCode = values.Length > 1 ? values[1] as string : null;
        CultureInfo displayCulture;
        try
        {
            displayCulture = CultureInfo.GetCultureInfo(
                string.IsNullOrWhiteSpace(languageCode) ? "tr-TR" : languageCode);
        }
        catch (CultureNotFoundException)
        {
            displayCulture = CultureInfo.GetCultureInfo("tr-TR");
        }

        return localTime.ToString("g", displayCulture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
