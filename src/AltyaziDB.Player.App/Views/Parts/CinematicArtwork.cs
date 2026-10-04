using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AltyaziDB.Player.App.Views.Parts;

/// <summary>
/// R2.3 scenic backgrounds are decorative JPEG files distributed separately
/// until approved binary assets are pushed into the private repository.
/// Their absence must never prevent a clean clone from opening the player.
/// </summary>
internal static class CinematicArtwork
{
    public static void ApplyIfAvailable(Border hero, string filename)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", "hero", filename);
        if (!File.Exists(fullPath))
        {
            return; // Existing R23HeroBrush is a safe, fully usable fallback.
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1920;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            hero.Background = brush;
        }
        catch (Exception)
        {
            // An invalid optional illustration cannot take down local playback
            // or any external account/stream integration.
        }
    }
}