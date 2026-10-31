using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Macast.Cli;

internal sealed record EventRequest(string Method, Dictionary<string, string> Headers, string Body);

internal sealed class EventProbe : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<EventRequest> _requests = Channel.CreateUnbounded<EventRequest>();
    private readonly Task _loop;

    public int Port { get; }

    public EventProbe()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async Task<EventRequest> NextAsync(TimeSpan timeout)
    {
        return await _requests.Reader.ReadAsync(_cts.Token).AsTask().WaitAsync(timeout);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (ObjectDisposedException)
        {
            // normal
        }
        catch (Exception ex)
        {
            Console.WriteLine("event probe accept failed: " + ex.Message);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var all = new MemoryStream();
                var buffer = new byte[8192];
                var headerEnd = -1;
                var contentLength = 0;

                while (true)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    all.Write(buffer, 0, read);
                    var current = Encoding.UTF8.GetString(all.ToArray());
                    headerEnd = current.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0)
                    {
                        continue;
                    }

                    var headerText = current[..headerEnd];
                    foreach (var line in headerText.Split("\r\n").Skip(1))
                    {
                        var index = line.IndexOf(':');
                        if (index > 0 &&
                            line[..index].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(line[(index + 1)..].Trim(), out var parsed))
                        {
                            contentLength = parsed;
                        }
                    }

                    if (all.Length >= headerEnd + 4 + contentLength)
                    {
                        break;
                    }
                }

                var raw = Encoding.UTF8.GetString(all.ToArray());
                headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = raw[..headerEnd].Split("\r\n");
                var method = lines.FirstOrDefault()?.Split(' ').FirstOrDefault() ?? string.Empty;
                foreach (var line in lines.Skip(1))
                {
                    var index = line.IndexOf(':');
                    if (index > 0)
                    {
                        headers[line[..index].Trim()] = line[(index + 1)..].Trim();
                    }
                }

                var bodyStart = headerEnd + 4;
                var body = raw.Length > bodyStart ? raw[bodyStart..] : string.Empty;
                await _requests.Writer.WriteAsync(new EventRequest(method, headers, body), _cts.Token);

                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine("event probe handle failed: " + ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }
}
