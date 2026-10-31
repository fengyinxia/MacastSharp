using System.Net;
using System.Net.Sockets;
using System.Text;
using Macast.Core.Net;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Ssdp;

public sealed class SsdpServer : IAsyncDisposable
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    public const int DefaultSsdpPort = 1900;
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings;
    private readonly List<(InterfaceAddress Address, Socket Socket)> _senders = new();
    private readonly Dictionary<string, SsdpDevice> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Socket? _receiver;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _notifyTask;
    private List<InterfaceAddress> _addresses = new();
    private int _httpPort;
    private readonly int _ssdpPort;
    private bool _running;

    public SsdpServer(AppSettings settings, int ssdpPort = DefaultSsdpPort)
    {
        _settings = settings;
        _ssdpPort = ssdpPort;
    }

    public bool Running => _running;
    public int HttpPort => _httpPort;
    public int SsdpPort => _ssdpPort;

    public async Task StartAsync(int httpPort, IReadOnlyList<InterfaceAddress> addresses,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_running)
            {
                await StopCoreAsync(sendByeBye: false);
            }

            _httpPort = httpPort;
            _addresses = addresses.ToList();
            BuildDevices();
            CreateReceiver();
            CreateSenders();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running = true;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
            _notifyTask = Task.Run(() => NotifyLoopAsync(_cts.Token), _cts.Token);
            SendNotifyAll("ssdp:alive", repeat: 2);
            Log.Info($"SSDP started on port {_ssdpPort}, http port {_httpPort}, " +
                     $"interfaces: {NetworkUtils.Format(_addresses)}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(int httpPort, IReadOnlyList<InterfaceAddress> addresses,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(sendByeBye: false, cancellationToken);
        await StartAsync(httpPort, addresses, cancellationToken);
    }

    public async Task StopAsync(bool sendByeBye = true, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(sendByeBye);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(bool sendByeBye)
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        if (sendByeBye)
        {
            try
            {
                SendNotifyAll("ssdp:byebye", repeat: 2);
            }
            catch (Exception ex)
            {
                Log.Debug($"send byebye failed: {ex.Message}");
            }
        }

        _cts?.Cancel();
        try
        {
            if (_receiveTask is not null)
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (_notifyTask is not null)
            {
                await _notifyTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (TimeoutException)
        {
            // socket shutdown will finish it
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            Log.Debug($"SSDP stop: {ex.Message}");
        }

        CloseSockets();
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _notifyTask = null;
        Log.Info("SSDP stopped");
    }

    private void BuildDevices()
    {
        _devices.Clear();
        var uuid = _settings.GetUsn();
        var baseUsn = $"uuid:{uuid}";
        AddDevice(baseUsn, "upnp:rootdevice", $"{baseUsn}::upnp:rootdevice");
        AddDevice(baseUsn, baseUsn, baseUsn);
        AddDevice("urn:schemas-upnp-org:device:MediaRenderer:1",
            "urn:schemas-upnp-org:device:MediaRenderer:1",
            $"{baseUsn}::urn:schemas-upnp-org:device:MediaRenderer:1");
        AddDevice("urn:schemas-upnp-org:service:RenderingControl:1",
            "urn:schemas-upnp-org:service:RenderingControl:1",
            $"{baseUsn}::urn:schemas-upnp-org:service:RenderingControl:1");
        AddDevice("urn:schemas-upnp-org:service:ConnectionManager:1",
            "urn:schemas-upnp-org:service:ConnectionManager:1",
            $"{baseUsn}::urn:schemas-upnp-org:service:ConnectionManager:1");
        AddDevice("urn:schemas-upnp-org:service:AVTransport:1",
            "urn:schemas-upnp-org:service:AVTransport:1",
            $"{baseUsn}::urn:schemas-upnp-org:service:AVTransport:1");
    }

    private void AddDevice(string key, string st, string usn)
    {
        _devices[key] = new SsdpDevice(st, usn, "max-age=66");
    }

    private void CreateReceiver()
    {
        _receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        TryEnableReusePort(_receiver);
        _receiver.Bind(new IPEndPoint(IPAddress.Any, _ssdpPort));
        // Allow same-host control points and local discovery tests to reach us.
        // Outgoing NOTIFY uses the per-interface sender sockets below, so this
        // does not create a notification loop.
        _receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

        foreach (var address in _addresses)
        {
            try
            {
                _receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddress, address.Address));
                Log.Debug($"SSDP join {address.Address} ({address.Name})");
            }
            catch (Exception ex)
            {
                Log.Warn($"SSDP join {address.Address} failed: {ex.Message}");
            }
        }
    }

    private static void TryEnableReusePort(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Linux SO_REUSEPORT = 15, macOS/BSD = 0x0200.
            var option = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 0x0200 : 15;
            socket.SetRawSocketOption((int)SocketOptionLevel.Socket, option, BitConverter.GetBytes(1));
        }
        catch
        {
            // SO_REUSEADDR above is enough on many systems.
        }
    }

    private void CreateSenders()
    {
        _senders.Clear();
        foreach (var address in _addresses)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(address.Address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    address.Address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                _senders.Add((address, socket));
            }
            catch (Exception ex)
            {
                Log.Warn($"SSDP sender for {address.Address} failed: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested && _receiver is not null)
        {
            try
            {
                var result = await _receiver.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
                var text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                HandleDatagram(text, (IPEndPoint)result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug($"SSDP receive: {ex.Message}");
            }
        }
    }

    private void HandleDatagram(string text, IPEndPoint remote)
    {
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var header = headerEnd >= 0 ? text[..headerEnd] : text;
        var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return;
        }

        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 3)
        {
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            headers[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        if (!string.Equals(first[0], "M-SEARCH", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!headers.TryGetValue("ST", out var searchTarget))
        {
            return;
        }

        Log.Debug($"M-SEARCH from {remote} for {searchTarget}");
        RespondToSearch(searchTarget, remote);
    }

    private void RespondToSearch(string searchTarget, IPEndPoint remote)
    {
        var locationIp = ChooseLocationIp(remote.Address);
        var location = $"http://{locationIp}:{_httpPort}/description.xml";

        foreach (var device in _devices.Values)
        {
            if (!string.Equals(device.St, searchTarget, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(searchTarget, "ssdp:all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var response = new StringBuilder()
                .Append("HTTP/1.1 200 OK\r\n")
                .Append("CACHE-CONTROL: ").Append(device.CacheControl).Append("\r\n")
                .Append("DATE: ").Append(DateTime.UtcNow.ToString("r")).Append("\r\n")
                .Append("EXT:\r\n")
                .Append("LOCATION: ").Append(location).Append("\r\n")
                .Append("SERVER: ").Append(ServerInfo()).Append("\r\n")
                .Append("ST: ").Append(device.St).Append("\r\n")
                .Append("USN: ").Append(device.Usn).Append("\r\n")
                .Append("\r\n")
                .ToString();

            SendTo(response, remote, locationIp);
        }
    }

    private IPAddress ChooseLocationIp(IPAddress remote)
    {
        if (IPAddress.IsLoopback(remote))
        {
            return IPAddress.Loopback;
        }

        foreach (var address in _addresses)
        {
            if (NetworkUtils.SameSubnet(address.Address, remote, address.Mask))
            {
                return address.Address;
            }
        }

        return _addresses.FirstOrDefault()?.Address ?? IPAddress.Loopback;
    }

    private void SendNotifyAll(string nts, int repeat)
    {
        if (!_running && nts != "ssdp:byebye")
        {
            return;
        }

        foreach (var sender in _senders)
        {
            var location = $"http://{sender.Address.Address}:{_httpPort}/description.xml";
            foreach (var device in _devices.Values)
            {
                var payload = new StringBuilder()
                    .Append("NOTIFY * HTTP/1.1\r\n")
                    .Append("HOST: ").Append(MulticastAddress).Append(':').Append(_ssdpPort).Append("\r\n")
                    .Append("NT: ").Append(device.St).Append("\r\n")
                    .Append("NTS: ").Append(nts).Append("\r\n")
                    .Append("CACHE-CONTROL: ").Append(device.CacheControl).Append("\r\n")
                    .Append("LOCATION: ").Append(location).Append("\r\n")
                    .Append("SERVER: ").Append(ServerInfo()).Append("\r\n")
                    .Append("USN: ").Append(device.Usn).Append("\r\n")
                    .Append("\r\n")
                    .ToString();

                for (var i = 0; i < repeat; i++)
                {
                    try
                    {
                        sender.Socket.SendTo(Encoding.UTF8.GetBytes(payload),
                            new IPEndPoint(MulticastAddress, _ssdpPort));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"SSDP notify {device.St} failed: {ex.Message}");
                    }
                }
            }
        }
    }

    private void SendTo(string payload, IPEndPoint remote, IPAddress sourceIp)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);

        // Replying from the socket bound to :1900 matches the behaviour of the
        // Python implementation and keeps the response source port stable.
        try
        {
            if (_receiver is not null)
            {
                _receiver.SendTo(bytes, remote);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"SSDP response via receiver to {remote} failed: {ex.Message}");
        }

        var sender = _senders.FirstOrDefault(s => s.Address.Address.Equals(sourceIp));
        try
        {
            sender.Socket?.SendTo(bytes, remote);
        }
        catch (Exception ex)
        {
            Log.Debug($"SSDP response via sender to {remote} failed: {ex.Message}");
        }
    }

    private async Task NotifyLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(NotifyInterval, token);
                if (_running)
                {
                    SendNotifyAll("ssdp:alive", repeat: 1);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
    }

    private string ServerInfo()
    {
        var os = Environment.OSVersion;
        return $"{os.Platform}/{os.Version} UPnP/1.0 Macast/{_settings.Version}";
    }

    private void CloseSockets()
    {
        try
        {
            _receiver?.Close();
        }
        catch
        {
            // ignore
        }

        _receiver = null;
        foreach (var sender in _senders)
        {
            try
            {
                sender.Socket.Close();
            }
            catch
            {
                // ignore
            }
        }

        _senders.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(sendByeBye: false);
        _lifecycle.Dispose();
    }

    private sealed record SsdpDevice(string St, string Usn, string CacheControl);
}
