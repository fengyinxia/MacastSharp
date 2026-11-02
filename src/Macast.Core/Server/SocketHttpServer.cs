using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Macast.Core.Utils;

namespace Macast.Core.Server;

public sealed record HttpRequestData(string Method, string Path, string Protocol,
    IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public IPAddress? RemoteAddress { get; init; }
    public bool IsNva => Protocol.Equals("NVA/1.0", StringComparison.OrdinalIgnoreCase);
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

public sealed class HttpResponseData
{
    public int Status { get; init; } = 200;
    public string StatusText { get; init; } = "OK";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = Array.Empty<byte>();
}

// Only the small HTTP surface used by DLNA/NVA: one request per connection,
// with NVA allowed to take ownership of the stream after its handshake.
public sealed class SocketHttpServer : IAsyncDisposable
{
    private const int MaxBodySize = 1024 * 1024;
    private readonly Func<HttpRequestData, Stream, CancellationToken, Task<HttpResponseData?>> _handler;
    private readonly bool _loopbackOnly;
    private readonly SemaphoreSlim _connections = new(32, 32);
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public SocketHttpServer(Func<HttpRequestData, Stream, CancellationToken, Task<HttpResponseData?>> handler,
        bool loopbackOnly = false)
    {
        _handler = handler;
        _loopbackOnly = loopbackOnly;
    }

    public int Port { get; private set; }

    public Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? last = null;
        foreach (var port in preferredPort > 0 ? new[] { preferredPort, 0 } : new[] { 0 })
        {
            var listener = new TcpListener(_loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
            try
            {
                listener.Start();
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _acceptTask = AcceptLoopAsync(_cts.Token);
                Log.Info($"HTTP socket listening on {(_loopbackOnly ? "127.0.0.1" : "0.0.0.0")}:{Port}");
                return Task.CompletedTask;
            }
            catch (SocketException ex)
            {
                listener.Stop();
                last = ex;
                Log.Warn($"HTTP listen on port {port} failed: {ex.Message}");
            }
        }
        throw new InvalidOperationException("HTTP server could not bind to any port", last);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }

            if (!_connections.Wait(0))
            {
                client.Dispose();
                continue;
            }
            var task = HandleClientAsync(client, token);
            _clients[client] = task;
            _ = task.ContinueWith(completed => { _clients.TryRemove(client, out _); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var ownedClient = client;
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            HttpRequestData? request;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                request = await ReadRequestAsync(stream, timeout.Token);
            }
            catch (HttpInputException ex)
            {
                await WriteResponseAsync(stream, new HttpRequestData("GET", "/", "HTTP/1.0",
                    new Dictionary<string, string>(), Array.Empty<byte>()),
                    new HttpResponseData { Status = ex.Status, StatusText = ex.Message }, token);
                return;
            }
            if (request is null) return;
            request = request with { RemoteAddress = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address };
            var response = await _handler(request, stream, token);
            if (response is not null)
                await WriteResponseAsync(stream, request, response, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Log.Debug($"HTTP client closed: {ex.Message}");
        }
        catch (Exception ex) { Log.Warn($"HTTP request failed: {ex.Message}"); }
        finally { _connections.Release(); }
    }

    private static async Task<HttpRequestData?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var line = await ReadLineAsync(stream, token);
        if (line is null) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[1].StartsWith('/') ||
            parts[2] is not ("HTTP/1.0" or "HTTP/1.1" or "NVA/1.0"))
            throw new HttpInputException(400, "Bad Request");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = line.Length;
        for (var count = 0; ; count++)
        {
            var header = await ReadLineAsync(stream, token)
                ?? throw new EndOfStreamException("incomplete request headers");
            headerBytes += Encoding.UTF8.GetByteCount(header) + 2;
            if (headerBytes > 32 * 1024 || count > 100)
                throw new HttpInputException(431, "Request Header Fields Too Large");
            if (header.Length == 0) break;
            var colon = header.IndexOf(':');
            if (colon <= 0 || header.Contains((char)13) || !headers.TryAdd(header[..colon].Trim(), header[(colon + 1)..].Trim()))
                throw new HttpInputException(400, "Bad Request");
        }

        var chunked = headers.TryGetValue("Transfer-Encoding", out var transfer);
        var hasLength = headers.TryGetValue("Content-Length", out var lengthText);
        if (chunked && (hasLength || !string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase)))
            throw new HttpInputException(400, "Bad Request");
        var length = 0;
        if (hasLength && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0))
            throw new HttpInputException(400, "Bad Request");
        if (length > MaxBodySize) throw new HttpInputException(413, "Payload Too Large");

        if (headers.TryGetValue("Expect", out var expect))
        {
            if (!expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                throw new HttpInputException(417, "Expectation Failed");
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token);
        }
        var body = chunked ? await ReadChunksAsync(stream, token) : new byte[length];
        if (!chunked) await stream.ReadExactlyAsync(body, token);
        return new HttpRequestData(parts[0].ToUpperInvariant(), parts[1], parts[2], headers, body);
    }

    private static async Task<byte[]> ReadChunksAsync(Stream stream, CancellationToken token)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, token) ?? throw new EndOfStreamException();
            if (!int.TryParse(line.Split(';', 2)[0], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var length) || length < 0)
                throw new HttpInputException(400, "Bad Request");
            if (length > MaxBodySize - body.Length)
                throw new HttpInputException(413, "Payload Too Large");
            if (length == 0)
            {
                var trailerBytes = 0;
                while (true)
                {
                    var trailer = await ReadLineAsync(stream, token) ?? throw new EndOfStreamException();
                    if (trailer.Length == 0) return body.ToArray();
                    trailerBytes += trailer.Length + 2;
                    if (trailerBytes > 32 * 1024) throw new HttpInputException(431, "Request Header Fields Too Large");
                }
            }
            var chunk = new byte[length];
            await stream.ReadExactlyAsync(chunk, token);
            body.Write(chunk);
            if (await ReadLineAsync(stream, token) != string.Empty)
                throw new HttpInputException(400, "Bad Request");
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(128);
        var single = new byte[1];
        while (await stream.ReadAsync(single, token) > 0)
        {
            if (single[0] == '\n')
            {
                if (bytes.Count == 0 || bytes[^1] != '\r') throw new HttpInputException(400, "Bad Request");
                bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(single[0]);
            if (bytes.Count > 8192) throw new HttpInputException(431, "Request Header Fields Too Large");
        }
        if (bytes.Count > 0) throw new EndOfStreamException("incomplete HTTP line");
        return null;
    }

    private static async Task WriteResponseAsync(Stream stream, HttpRequestData request,
        HttpResponseData response, CancellationToken token)
    {
        var head = new StringBuilder(request.IsNva ? "NVA/1.0" : "HTTP/1.1")
            .Append(' ').Append(response.Status).Append(' ').Append(response.StatusText).Append("\r\n")
            .Append("Server: Macast/0.7 UPnP/1.0\r\nDate: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        response.Headers.TryGetValue("Content-Type", out var contentType);
        head.Append("Content-Type: ").Append(contentType ?? "text/xml; charset=utf-8").Append("\r\n");
        foreach (var (key, value) in response.Headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            head.Append(key).Append(": ").Append(value).Append("\r\n");
        }
        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\nConnection: close\r\n\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token);
        if (request.Method != "HEAD" && response.Body.Length > 0)
            await stream.WriteAsync(response.Body, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptTask is not null) await _acceptTask;
        foreach (var client in _clients.Keys) client.Dispose();
        await Task.WhenAll(_clients.Values);
        _listener = null;
        _acceptTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class HttpInputException(int status, string reason) : IOException(reason)
    {
        public int Status { get; } = status;
    }
}
