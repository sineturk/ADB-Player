using System.Globalization;

namespace AltyaziDB.Player.Core.Models;

public sealed record ChapterInfo(long Id, double TimeSeconds, string Title)
{
    public string DisplayName
    {
        get
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, TimeSeconds));
            var formatted = time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

            return $"{formatted} · {Title}";
        }
    }
}
