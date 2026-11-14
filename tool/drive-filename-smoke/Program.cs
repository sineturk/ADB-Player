using System.Net;
using System.Text;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Sources;

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("[P4.17.5 FAIL] " + name);
    Console.WriteLine("[PASS] " + name);
}

// All HTTP requests are handled in-memory. No Google account, file, network,
// login or API credentials are used in these tests.
var primary = new FakeConfirmationHandler();
var preview = new FakePreviewHandler("<html><head><title>Sample.Movie.2024.1080p.mkv - Google Drive</title></head></html>");
var logger = new MemoryLogger();
using (var service = new RemoteSourceService(
           logger, handler: primary, publicPreviewHandler: preview))
{
    var result = await service.ResolvePublicLinkAsync(
        "https://drive.google.com/file/d/FAKE_TEST_FILE_123456/view");
    Check(result.DisplayName == "Sample.Movie.2024.1080p.mkv",
          "Filename comes from anonymous preview title");
    Check(result.DirectOpen?.DisplayName == "Sample.Movie.2024.1080p.mkv",
          "Original filename reaches the playback/history request");
    Check(result.DirectOpen?.Source.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) == true,
          "Existing local playback proxy remains unchanged");
    Check(primary.Calls > 0 && primary.PreviewCalls == 0,
          "Playback confirmation handler is never used for preview GET");
    Check(preview.Calls == 1 && preview.SawNoAuthHeaders,
          "Preview uses independent anonymous request with no credentials");
    Check(logger.Messages.All(m => !m.Contains("Sample.Movie", StringComparison.Ordinal)),
          "Private media filename is not exposed in service logs");
}

var deniedPreview = new FakePreviewHandler(null);
using (var service = new RemoteSourceService(
           new MemoryLogger(),
           handler: new FakeConfirmationHandler(),
           publicPreviewHandler: deniedPreview))
{
    var result = await service.ResolvePublicLinkAsync(
        "https://drive.google.com/file/d/FAKE_TEST_FILE_123456/view");
    Check(result.DisplayName == "Google Drive dosyası",
          "Missing preview metadata safely preserves existing playback title fallback");
    Check(result.DirectOpen is not null,
          "Preview HTTP failure never blocks the existing Google Drive playback");
}
Console.WriteLine("[PASS] P4.17.5 Google Drive isolated filename metadata smoke");

internal sealed class FakeConfirmationHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public int PreviewCalls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri?.Host == "drive.google.com" &&
            request.RequestUri.AbsolutePath.Contains("/file/d/", StringComparison.Ordinal))
            PreviewCalls++;
        const string html =
            "<html><form method='get' action='https://drive.usercontent.google.com/download'>" +
            "<input type='hidden' name='id' value='FAKE_TEST_FILE_123456' />" +
            "<input type='hidden' name='confirm' value='t' />" +
            "<input type='hidden' name='uuid' value='PRIVATE_TEST_TOKEN' />" +
            "</form></html>";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
            RequestMessage = request
        });
    }
}

internal sealed class FakePreviewHandler(string? html) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool SawNoAuthHeaders { get; private set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        SawNoAuthHeaders &= request.Headers.Authorization is null &&
                            !request.Headers.Contains("Cookie") &&
                            request.RequestUri?.Host == "drive.google.com" &&
                            request.RequestUri.AbsolutePath.StartsWith(
                                "/file/d/FAKE_TEST_FILE_123456/view", StringComparison.Ordinal);
        return Task.FromResult(new HttpResponseMessage(
            html is null ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        {
            Content = new StringContent(html ?? "", Encoding.UTF8, "text/html"),
            RequestMessage = request
        });
    }
}

internal sealed class MemoryLogger : IAppLogger
{
    public List<string> Messages { get; } = new();
    public void Info(string message) => Messages.Add(message);
    public void Warning(string message) => Messages.Add(message);
    public void Error(string message, Exception? exception = null) => Messages.Add(message);
}
