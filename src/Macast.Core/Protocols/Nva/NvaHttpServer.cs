using System.Net;
using System.Net.Sockets;
using System.Text;
using Macast.Core.Utils;

namespace Macast.Core.Protocols.Nva;

public sealed record NvaRequest(
    string Method,
    string Path,
    string Protocol,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public bool IsNva => Protocol.Contains("NVA", StringComparison.OrdinalIgnoreCase);

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

public sealed class NvaResponse
{
    public int Status { get; init; } = 200;

    public string StatusText { get; init; } = "OK";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public byte[] Body { get; init; } = Array.Empty<byte>();

    public bool CloseConnection { get; init; } = true;
}

/// <summary>
/// Small HTTP/1.0 front end used by the NVA protocol. The Bilibili casting
/// client opens its control channel with an "NVA/1.0" request line, which no
/// stock HTTP server (including Kestrel) accepts, so this plugin owns the
/// listener and parses the request line itself.
/// </summary>
public sealed class NvaHttpServer : IAsyncDisposable
{
    private readonly Func<NvaRequest, Stream, CancellationToken, Task<NvaResponse?>> _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public NvaHttpServer(Func<NvaRequest, Stream, CancellationToken, Task<NvaResponse?>> handler)
    {
        _handler = handler;
    }

    public int Port { get; private set; }

    public async Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        var candidates = preferredPort > 0 ? new[] { preferredPort, 0 } : new[] { 0 };
        Exception? last = null;
        foreach (var port in candidates)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                Log.Info($"NVA socket server listening on 0.0.0.0:{Port}");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"NVA listen on port {port} failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException("NVA server could not bind to any port", last);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"NVA accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, token);
            if (request is null)
            {
                return;
            }

            var response = await _handler(request, stream, token);
            if (response is null)
            {
                // The handler took over the connection (NVA control channel).
                return;
            }

            await WriteResponseAsync(stream, request, response, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            Log.Debug($"NVA client closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA request failed: {ex}");
        }
    }

    private static async Task<NvaRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var line = await ReadLineAsync(stream, token);
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var protocol = parts.Length > 2 ? parts[2] : "HTTP/1.0";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var header = await ReadLineAsync(stream, token);
            if (header is null || header.Length == 0)
            {
                break;
            }

            var colon = header.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, length - read), token);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read < length)
            {
                Array.Resize(ref body, read);
            }
        }

        return new NvaRequest(parts[0].ToUpperInvariant(), parts[1], protocol, headers, body);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(128);
        var single = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(single.AsMemory(0, 1), token);
            if (n <= 0)
            {
                return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (single[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            buffer.Add(single[0]);
            if (buffer.Count > 16 * 1024)
            {
                return null;
            }
        }
    }

    private static async Task WriteResponseAsync(Stream stream, NvaRequest request, NvaResponse response, CancellationToken token)
    {
        var protocol = request.IsNva ? "NVA/1.0" : "HTTP/1.0";
        var head = new StringBuilder();
        head.Append(protocol).Append(' ').Append(response.Status).Append(' ').Append(response.StatusText).Append("\r\n");
        head.Append("Server: Macast/").Append(Environment.OSVersion.VersionString).Append(" UPnP/1.0 Macast/0.7\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");

        response.Headers.TryGetValue("Content-Type", out var contentType);
        head.Append("Content-Type: ").Append(contentType ?? "text/xml; charset=\"utf-8\"").Append("\r\n");
        foreach (var (key, value) in response.Headers)
        {
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(key).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headerBytes, token);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, token);
        }

        await stream.FlushAsync(token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignore
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        }

        _listener = null;
        _acceptTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
