using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Sources;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException("[P4.17.4 FAIL] " + message);
    Console.WriteLine("[PASS] " + message);
}

// No actual Google account/network/secret involved. Simulates the browser's
// public download interstitial with POST, followed by a successful binary body.
var logger = new MemoryLogger();
using var handler = new FakeDriveHandler();
using var resolver = new RemoteSourceService(logger, handler: handler);
var result = await resolver.ResolvePublicLinkAsync(
    "https://drive.google.com/file/d/FAKE_TEST_FILE_123456/view");
Check(result.DirectOpen is not null, "Public Google Drive confirmation produces proxy URL");
Check(result.DirectOpen!.Source.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
      "Private localhost proxy remains active");
using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
using var get = new HttpRequestMessage(HttpMethod.Get, result.DirectOpen.Source);
get.Headers.Range = new RangeHeaderValue(0, 15);
using var response = await browser.SendAsync(get);
var bytes = await response.Content.ReadAsByteArrayAsync();
Check(response.StatusCode == HttpStatusCode.PartialContent, "POST media served as local HTTP 206");
Check(bytes.Length == 16 && bytes.All(x => x == 0x4D), "Correct media bytes returned, not HTML");
Check(handler.PostCount == 1, "POST confirmation form submitted exactly once for initial Range");
Check(logger.Messages.Any(x => x.Contains("method: POST", StringComparison.Ordinal)),
      "Only form method is logged");
Check(logger.Messages.All(x => !x.Contains("SENSITIVE_TEST_UUID", StringComparison.Ordinal)),
      "Confirmation tokens are never written to logs");
Console.WriteLine("[PASS] P4.17.4 Google Drive POST confirmation synthetic smoke");

internal sealed class MemoryLogger : IAppLogger
{
    public List<string> Messages { get; } = new();
    public void Info(string message) => Messages.Add(message);
    public void Warning(string message) => Messages.Add(message);
    public void Error(string message, Exception? exception = null) => Messages.Add(message);
}

internal sealed class FakeDriveHandler : HttpMessageHandler
{
    public int PostCount { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            PostCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (!body.Contains("id=FAKE_TEST_FILE_123456", StringComparison.Ordinal) ||
                !body.Contains("confirm=t", StringComparison.Ordinal))
                throw new InvalidOperationException("Form body lost Google fields");
            var reply = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(Enumerable.Repeat((byte)0x4D, 64).ToArray()),
                RequestMessage = request
            };
            reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return reply;
        }

        const string form = "<html><form method=\"post\" action=\"https://drive.usercontent.google.com/download\">"
            + "<input type=\"hidden\" name=\"id\" value=\"FAKE_TEST_FILE_123456\" />"
            + "<input type=\"hidden\" name=\"confirm\" value=\"t\" />"
            + "<input type=\"hidden\" name=\"uuid\" value=\"SENSITIVE_TEST_UUID\" />"
            + "</form></html>";
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(form, Encoding.UTF8, "text/html"),
            RequestMessage = request
        };
    }
}
