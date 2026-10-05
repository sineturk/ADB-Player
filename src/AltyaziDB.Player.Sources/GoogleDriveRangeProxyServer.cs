using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Keeps Google Drive confirmation cookies/tokens inside the application's
/// HttpClient session while exposing a localhost byte-range URL to libmpv.
/// Google can bind the short-lived uuid/at download URL to the session that
/// accepted the "Download anyway" page; handing that URL to libmpv directly
/// can therefore return HTML instead of media bytes.
/// </summary>
internal sealed class GoogleDriveRangeProxyServer : IDisposable
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, ProxyResource> _resources = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _acceptLoop;
    private readonly Action<string, Exception?>? _reportError;
    private bool _disposed;

    public GoogleDriveRangeProxyServer(Action<string, Exception?>? reportError = null)
    {
        _reportError = reportError;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public int Port { get; }

    public string Register(
        string fileName,
        Func<string?, CancellationToken, Task<HttpResponseMessage>> openUpstreamAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(openUpstreamAsync);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _resources[token] = new ProxyResource(fileName, openUpstreamAsync);
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
            var stream = client.GetStream();
            var responseStarted = false;

            try
            {
                var request = await ReadRequestAsync(stream, serverToken).ConfigureAwait(false);
                if (request is null) return;

                if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed", serverToken).ConfigureAwait(false);
                    return;
                }

                var token = request.Path.TrimStart('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(token) || !_resources.TryGetValue(token, out var resource))
                {
                    await WriteSimpleResponseAsync(stream, 404, "Not Found", serverToken).ConfigureAwait(false);
                    return;
                }

                request.Headers.TryGetValue("Range", out var rangeHeader);
                using var upstream = await resource.OpenUpstreamAsync(rangeHeader, serverToken).ConfigureAwait(false);

                if (!upstream.IsSuccessStatusCode)
                {
                    await WriteSimpleResponseAsync(
                        stream,
                        (int)upstream.StatusCode,
                        upstream.ReasonPhrase ?? "Bad Gateway",
                        serverToken).ConfigureAwait(false);
                    return;
                }

                var upstreamStatusCode = (int)upstream.StatusCode;
                var upstreamLength = upstream.Content.Headers.ContentLength;
                var emulateRange = false;
                long emulatedStart = 0;
                long emulatedEnd = -1;
                long responseLength = upstreamLength ?? -1;

                if (upstreamStatusCode == 200 &&
                    !string.IsNullOrWhiteSpace(rangeHeader) &&
                    upstreamLength is > 0 &&
                    TryParseRange(rangeHeader, upstreamLength.Value, out var parsedRange))
                {
                    emulateRange = true;
                    emulatedStart = parsedRange.Start;
                    emulatedEnd = parsedRange.End;
                    responseLength = emulatedEnd - emulatedStart + 1;
                }

                var statusCode = emulateRange ? 206 : upstreamStatusCode;
                var reason = emulateRange
                    ? "Partial Content"
                    : upstream.ReasonPhrase ?? (statusCode == 206 ? "Partial Content" : "OK");
                var header = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
                    .Append("Connection: close\r\n")
                    .Append("Cache-Control: no-store\r\n");

                var contentType = upstream.Content.Headers.ContentType?.ToString();
                if (!string.IsNullOrWhiteSpace(contentType))
                    header.Append("Content-Type: ").Append(contentType).Append("\r\n");

                if (responseLength >= 0)
                    header.Append("Content-Length: ").Append(responseLength).Append("\r\n");

                var contentRange = emulateRange
                    ? $"bytes {emulatedStart}-{emulatedEnd}/{upstreamLength}"
                    : upstream.Content.Headers.ContentRange?.ToString();
                if (!string.IsNullOrWhiteSpace(contentRange))
                    header.Append("Content-Range: ").Append(contentRange).Append("\r\n");

                if (emulateRange || upstream.Headers.AcceptRanges.Count > 0 || statusCode == 206)
                    header.Append("Accept-Ranges: bytes\r\n");

                header.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), serverToken).ConfigureAwait(false);
                responseStarted = true;

                if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                    return;

                await using var input = await upstream.Content.ReadAsStreamAsync(serverToken).ConfigureAwait(false);
                if (emulateRange && emulatedStart > 0)
                    await SkipAsync(input, emulatedStart, serverToken).ConfigureAwait(false);

                var remaining = emulateRange ? responseLength : long.MaxValue;
                var buffer = new byte[256 * 1024];
                while (remaining > 0)
                {
                    var wanted = (int)Math.Min(buffer.Length, remaining);
                    var read = await input.ReadAsync(buffer.AsMemory(0, wanted), serverToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, read), serverToken).ConfigureAwait(false);
                    if (emulateRange)
                        remaining -= read;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                // libmpv closes old requests while seeking/re-probing; this is normal.
            }
            catch (Exception exception)
            {
                _reportError?.Invoke("Google Drive yerel akış köprüsü isteği tamamlayamadı.", exception);
                if (!responseStarted)
                {
                    try
                    {
                        await WriteSimpleResponseAsync(stream, 502, "Bad Gateway", serverToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static bool TryParseRange(
        string value,
        long length,
        out (long Start, long End) range)
    {
        range = default;
        if (length <= 0 || string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var first = value[6..].Split(',', 2)[0].Trim();
        var separator = first.IndexOf('-');
        if (separator < 0) return false;

        var left = first[..separator].Trim();
        var right = first[(separator + 1)..].Trim();
        if (left.Length == 0)
        {
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
                return false;
            suffix = Math.Min(suffix, length);
            range = (length - suffix, length - 1);
            return true;
        }

        if (!long.TryParse(left, out var start) || start < 0 || start >= length)
            return false;

        var end = length - 1;
        if (right.Length > 0 && (!long.TryParse(right, out end) || end < start))
            return false;

        range = (start, Math.Min(end, length - 1));
        return true;
    }

    private static async Task SkipAsync(
        Stream input,
        long bytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var remaining = bytes;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("Google Drive akışı istenen byte konumuna ulaşmadan sona erdi.");
            remaining -= read;
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
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
            if (separator > 0)
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return new HttpRequest(requestLine[0], requestLine[1], headers);
    }

    private static bool HasHeaderTerminator(IReadOnlyList<byte> bytes)
    {
        for (var index = 3; index < bytes.Count; index++)
        {
            if (bytes[index - 3] == (byte)'\r' && bytes[index - 2] == (byte)'\n' &&
                bytes[index - 1] == (byte)'\r' && bytes[index] == (byte)'\n')
                return true;
        }

        return false;
    }

    private static Task WriteSimpleResponseAsync(
        NetworkStream stream,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {message}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return WriteResponseAsync(stream, header, body, cancellationToken);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        byte[] header,
        byte[] body,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        _resources.Clear();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _lifetime.Dispose();
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record ProxyResource(
        string FileName,
        Func<string?, CancellationToken, Task<HttpResponseMessage>> OpenUpstreamAsync);
}
