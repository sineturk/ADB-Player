using System.Net.Http.Headers;
using AltyaziDB.Player.Sources;

static void Check(bool success, string message)
{
    if (!success) throw new InvalidOperationException("[RC2 P4.17.1 FAIL] " + message);
    Console.WriteLine("[PASS] " + message);
}

var cd = ContentDispositionHeaderValue.Parse("attachment; filename=\"Dune.Part.Two.2024.1080p.mkv\"");
Check(RemoteMediaFileName.FromContentDisposition(cd) == "Dune.Part.Two.2024.1080p.mkv",
      "Content-Disposition filename is used");
Check(RemoteMediaFileName.FromUrlPath(new Uri("https://cdn.example.org/api/stream?id=x")) is null,
      "Opaque API URLs are not mistaken for media filenames");
Check(RemoteMediaFileName.FromUrlPath(new Uri("https://cdn.example.org/Dune.Part.Two.2024.mkv?token=hidden"))
        == "Dune.Part.Two.2024.mkv", "Query tokens never enter media filename");
Check(RemoteMediaFileName.FromGoogleDriveHtml(
        "<html><head><meta property=\"og:title\" content=\"Dune.Part.Two.2024.1080p.mkv\"></head></html>")
        == "Dune.Part.Two.2024.1080p.mkv", "Public Drive og:title is recognized");
Check(RemoteMediaFileName.FromGoogleDriveHtml(
        "<html><head><meta content='The.Matrix.1999.mkv' property='og:title'></head></html>")
        == "The.Matrix.1999.mkv", "HTML meta attribute order is independent");
Check(RemoteMediaFileName.FromGoogleDriveHtml(
        "<title>Interstellar.2014.1080p.mp4 - Google Drive</title>")
        == "Interstellar.2014.1080p.mp4", "Drive document title suffix is removed");
Check(RemoteMediaFileName.FromGoogleDriveHtml(
        "<title>Sign in - Google Drive</title>") is null, "Google sign-in page never becomes a title");
Check(RemoteMediaFileName.FromGoogleDriveHtml(
        "<title>Google Drive - Virus scan warning</title>") is null,
      "Virus confirmation without filename falls back safely");
Check(RemoteMediaFileName.Clean("video.mkv\r\nInjected-Header: x") is null,
      "Header injection and control characters are rejected");
Check(RemoteMediaFileName.Clean("google-drive-media.mkv") == "google-drive-media.mkv",
      "A real .mkv name remains valid when explicitly provided");
Check(RemoteMediaFileName.FromUrlPath(new Uri("https://google.com/uc?export=download&id=abc")) is null,
      "Generic Drive endpoint is not treated as a filename");
Console.WriteLine("[PASS] RC2 P4.17.1 remote name smoke");
