using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AltyaziDB.Player.Telegram;

/// <summary>
/// TDLib'in parca indirme API'sini libmpv'nin anlayacagi, yalnizca localhost'a
/// acik bir HTTP Range kaynagina donusturur. Dosya disariya paylasilmaz ve her
/// kaynak tahmin edilemez bir oturum anahtariyla korunur.
/// </summary>
internal sealed class TelegramRangeStreamServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int ReadChunkBytes = 256 * 1024;
    private readonly ConcurrentDictionary<string, StreamResource> _resources = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _acceptLoop;
    private readonly Action<string, Exception?>? _reportError;

    public TelegramRangeStreamServer(Action<string, Exception?>? reportError = null)
    {
        _reportError = reportError;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public int Port { get; }

    public string Register(
        string fileName,
        string contentType,
        long length,
        Func<long, int, CancellationToken, Task<byte[]>> readAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(readAsync);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _resources[token] = new StreamResource(fileName, contentType, length, readAsync);
        return $"http://127.0.0.1:{Port}/{token}/{Uri.EscapeDataString(fileName)}";
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            var stream = client.GetStream();
            var responseStarted = false;

            try
            {
                var request = await ReadRequestAsync(stream, requestLifetime.Token).ConfigureAwait(false);
                if (request is null) return;
                if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed", requestLifetime.Token).ConfigureAwait(false);
                    return;
                }

                var token = request.Path.TrimStart('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(token) || !_resources.TryGetValue(token, out var resource))
                {
                    await WriteSimpleResponseAsync(stream, 404, "Not Found", requestLifetime.Token).ConfigureAwait(false);
                    return;
                }

                var range = ParseRange(request.Headers.TryGetValue("Range", out var header) ? header : null, resource.Length);
                if (range is null)
                {
                    await WriteRangeNotSatisfiableAsync(stream, resource.Length, requestLifetime.Token).ConfigureAwait(false);
                    return;
                }

                using var activeRequest = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token);
                var start = range.Value.Start;
                var end = range.Value.End;
                var partial = request.Headers.ContainsKey("Range");
                var responseLength = end - start + 1;

                // libmpv acilis sirasinda ayni dosya icin arka arkaya birkac Range
                // istegi yapabilir. Yaniti veri hazir olmadan gondermek ve yeni istekte
                // oncekini iptal etmek, oynaticinin eksik govdeli HTTP yanitlari arasinda
                // sonsuz tekrar yapmasina yol aciyordu. Ilk medya blogunu once hazirla;
                // boylece libmpv 200/206 yanitiyla birlikte hemen okunabilir veri alir.
                byte[]? firstBytes = null;
                if (!request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    var firstWanted = (int)Math.Min(ReadChunkBytes, responseLength);
                    firstBytes = await resource.ReadAsync(start, firstWanted, activeRequest.Token).ConfigureAwait(false);
                    if (firstBytes.Length == 0)
                        throw new EndOfStreamException("Telegram akisi ilk medya blogunu dondurmedi.");
                }

                var response = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(partial ? "206 Partial Content" : "200 OK").Append("\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("Content-Type: ").Append(resource.ContentType).Append("\r\n")
                    .Append("Content-Length: ").Append(responseLength).Append("\r\n")
                    .Append("Cache-Control: no-store\r\n")
                    .Append("Connection: close\r\n");
                if (partial)
                    response.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(resource.Length).Append("\r\n");
                response.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), activeRequest.Token).ConfigureAwait(false);
                responseStarted = true;

                if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

                var firstCount = Math.Min(firstBytes!.Length, (int)Math.Min(int.MaxValue, responseLength));
                await stream.WriteAsync(firstBytes.AsMemory(0, firstCount), activeRequest.Token).ConfigureAwait(false);
                var position = start + firstCount;
                while (position <= end)
                {
                    activeRequest.Token.ThrowIfCancellationRequested();
                    var wanted = (int)Math.Min(ReadChunkBytes, end - position + 1);
                    var bytes = await resource.ReadAsync(position, wanted, activeRequest.Token).ConfigureAwait(false);
                    if (bytes.Length == 0) throw new EndOfStreamException("Telegram akisi beklenmedik sekilde sonlandi.");
                    var count = Math.Min(bytes.Length, wanted);
                    await stream.WriteAsync(bytes.AsMemory(0, count), activeRequest.Token).ConfigureAwait(false);
                    position += count;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or EndOfStreamException)
            {
                // libmpv seek sirasinda onceki HTTP istegini kapatir; bu normaldir.
            }
            catch (Exception exception)
            {
                _reportError?.Invoke("Telegram yerel akış köprüsü isteği tamamlayamadı.", exception);
                if (!responseStarted)
                {
                    try
                    {
                        await WriteSimpleResponseAsync(stream, 504, "Gateway Timeout", requestLifetime.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // İstemci bağlantıyı kapattıysa ikinci hata kullanıcıya yansıtılmaz.
                    }
                }
            }
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(2048);
        var buffer = new byte[1024];
        while (bytes.Count < MaximumHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            if (HasHeaderTerminator(bytes)) break;
        }
        if (bytes.Count >= MaximumHeaderBytes) return null;

        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.FirstOrDefault()?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine is not { Length: >= 2 }) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return new HttpRequest(requestLine[0], requestLine[1], headers);
    }

    private static bool HasHeaderTerminator(IReadOnlyList<byte> bytes)
    {
        for (var index = 3; index < bytes.Count; index++)
        {
            if (bytes[index - 3] == (byte)'\r' && bytes[index - 2] == (byte)'\n' &&
                bytes[index - 1] == (byte)'\r' && bytes[index] == (byte)'\n') return true;
        }
        return false;
    }

    private static (long Start, long End)? ParseRange(string? value, long length)
    {
        if (length <= 0) return null;
        if (string.IsNullOrWhiteSpace(value)) return (0, length - 1);
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var range = value[6..].Split(',', 2)[0].Trim();
        var separator = range.IndexOf('-');
        if (separator < 0) return null;
        var left = range[..separator].Trim();
        var right = range[(separator + 1)..].Trim();

        if (left.Length == 0)
        {
            if (!long.TryParse(right, out var suffix) || suffix <= 0) return null;
            suffix = Math.Min(suffix, length);
            return (length - suffix, length - 1);
        }
        if (!long.TryParse(left, out var start) || start < 0 || start >= length) return null;
        var end = length - 1;
        if (right.Length > 0 && (!long.TryParse(right, out end) || end < start)) return null;
        return (start, Math.Min(end, length - 1));
    }

    private static Task WriteSimpleResponseAsync(NetworkStream stream, int code, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {message}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return WriteResponseAsync(stream, header, body, cancellationToken);
    }

    private static Task WriteRangeNotSatisfiableAsync(NetworkStream stream, long length, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return stream.WriteAsync(header, cancellationToken).AsTask();
    }

    private static async Task WriteResponseAsync(NetworkStream stream, byte[] header, byte[] body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _resources.Clear();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }

    private sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    private sealed class StreamResource(
        string fileName,
        string contentType,
        long length,
        Func<long, int, CancellationToken, Task<byte[]>> readAsync)
    {
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public long Length { get; } = length;
        public Func<long, int, CancellationToken, Task<byte[]>> ReadAsync { get; } = readAsync;

    }
}
