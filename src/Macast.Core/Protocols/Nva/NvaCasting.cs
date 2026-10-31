using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Protocols.Nva;

/// <summary>
/// Everything NvaCasting needs from the protocol service: the renderer, the DLNA state
/// table (so NVA state changes are broadcast to GENA subscribers too) and the bilibili API.
/// </summary>
public interface INvaCastingHost
{
    IRenderer? Renderer { get; }

    AppSettings Settings { get; }

    BilibiliApi Bilibili { get; }

    string GetState(string name);

    void SetState(string name, string value);
}

/// <summary>
/// NVA binary framing, taken from nirvana.py NVAConectionBaseHandler:
///
///   response : c0 | all_l | counter:u32 | [ param_l:u32 | json ]
///   command  : e0 | all_l | counter:u32 | n=1 | cmd_l=7 | "Command" | method_l | method
///              | [ param_l:u32 | json ]
///   ping     : e4 | 00 | counter:u32
/// </summary>
internal static class NvaFrame
{
    public const byte SendCmd = 0xE0;
    public const byte RetCmd = 0xC0;
    public const byte PingCmd = 0xE4;

    private static readonly byte[] CommandName = Encoding.ASCII.GetBytes("Command");

    public static byte[] Response(uint counter, string? json)
    {
        if (json is null)
        {
            var empty = new byte[6];
            empty[0] = RetCmd;
            empty[1] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(2, 4), counter);
            return empty;
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[10 + payload.Length];
        frame[0] = RetCmd;
        frame[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), counter);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(6, 4), payload.Length);
        payload.CopyTo(frame, 10);
        return frame;
    }

    public static byte[] Command(uint counter, string command, string? json)
    {
        var method = Encoding.UTF8.GetBytes(command);
        var payload = json is null ? null : Encoding.UTF8.GetBytes(json);
        var length = 16 + method.Length + (payload is null ? 0 : 4 + payload.Length);
        var frame = new byte[length];
        frame[0] = SendCmd;
        frame[1] = (byte)(payload is null ? 2 : 3);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), counter);
        frame[6] = 1;
        frame[7] = 7;
        CommandName.CopyTo(frame, 8);
        frame[15] = (byte)method.Length;
        method.CopyTo(frame, 16);
        if (payload is not null)
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(16 + method.Length, 4), payload.Length);
            payload.CopyTo(frame, 20 + method.Length);
        }

        return frame;
    }

    public static byte[] Ping(uint counter)
    {
        var frame = new byte[6];
        frame[0] = PingCmd;
        frame[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), counter);
        return frame;
    }
}

/// <summary>
/// The NVA control channel manager (nirvana.py NVAProtocol + NVAConectionHandler):
/// keeps one long lived connection per casting device, answers commands, pings every
/// second and rebroadcasts renderer state changes as NVA commands.
/// </summary>
public sealed class NvaCasting : IAsyncDisposable
{
    /// <summary>
    /// OnVolumeChanged / OnMediaChanged are not sent by the python plugin (v0.33); they are
    /// enabled here so a client that knows them keeps its UI in sync. Set to false for an
    /// exact python behaviour.
    /// </summary>
    private const bool EmitExtendedEvents = true;

    public static readonly double[] SupportSpeeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };

    /// <summary>
    /// Used when the phone sends userDesireQn=0 (fresh install / never picked a
    /// quality). 80 is 1080P: the server silently downgrades it to 720P for an
    /// anonymous request instead of failing, so this never regresses the old default.
    /// </summary>
    public const int DefaultDesireQn = 80;

    private readonly INvaCastingHost _host;
    private readonly object _gate = new();
    private readonly List<NvaClient> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(string Command, JsonNode? Data)> _broadcasts =
        Channel.CreateUnbounded<(string, JsonNode?)>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _pump;
    private bool _started;
    private int _connectionSeq;

    private List<NvaPlayItem> _playList = new();
    private int _playIndex;
    private bool _mediaPlaying;
    private string _lastPosition = string.Empty;
    private string _lastDuration = string.Empty;

    public NvaCasting(INvaCastingHost host)
    {
        _host = host;
    }

    internal INvaCastingHost Host => _host;

    internal CancellationToken Token => _cts.Token;

    public int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count(c => !c.Terminated);
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _pump = Task.Run(() => PumpAsync(_cts.Token));
        Log.Info("NVA casting manager started");
    }

    // ---- connection lifecycle (nirvana.py add / restore / remove) -------

    /// <summary>
    /// Runs one NVA control channel until the socket dies. The caller (the HTTP handler)
    /// awaits this so that the connection stays owned by us instead of the HTTP server.
    /// </summary>
    public async Task RunSessionAsync(Stream stream, string session, string uuid, string method,
        CancellationToken cancellationToken)
    {
        var (client, _) = Attach(stream, session, uuid);
        var connectionId = client.ConnectionId;
        Log.Info($"NVA session {NvaClient.Tail(session)} uuid {NvaClient.Tail(uuid)} method {method}");

        try
        {
            if (!string.Equals(method, "SETUP", StringComparison.Ordinal))
            {
                await RestoreAsync();
            }

            await ReadLoopAsync(client, stream, cancellationToken);
        }
        finally
        {
            client.Detach(connectionId);
        }
    }

    private (NvaClient Client, bool Reused) Attach(Stream stream, string session, string uuid)
    {
        NvaClient client;
        var reused = false;

        lock (_gate)
        {
            // Drop clients whose socket already died.
            _clients.RemoveAll(c => c.Terminated);

            var sameSession = _clients.FirstOrDefault(c =>
                string.Equals(c.Uuid, uuid, StringComparison.Ordinal) &&
                string.Equals(c.Session, session, StringComparison.Ordinal));

            if (sameSession is not null)
            {
                // 设备断线重连：恢复之前的 session，把新 socket 接到旧的 handler 上，
                // 播放状态（标题/清晰度/分集）全部保留。
                Log.Info("NVA: 设备断线重连，恢复之前的 session");
                client = sameSession;
                reused = true;
            }
            else
            {
                client = new NvaClient(this, session, uuid);

                // The bilibili app is known to hold two sockets for one device (the
                // cast panel and the background service use different sessions).
                // Terminating the other one makes the phone reconnect in a tight
                // loop, so both stay alive; the newcomer inherits the play state.
                var previous = _clients
                    .Where(c => string.Equals(c.Uuid, uuid, StringComparison.Ordinal))
                    .OrderByDescending(c => c.ConnectionId)
                    .FirstOrDefault();
                if (previous is not null)
                {
                    Log.Info("NVA: 同一设备的并行连接，保留旧连接并继承播放状态");
                    client.CopyPlaybackFrom(previous);
                }
                else
                {
                    Log.Info("NVA: 新设备连接");
                }

                _clients.Add(client);
            }

            var connectionId = Interlocked.Increment(ref _connectionSeq);
            client.Attach(stream, connectionId);
        }

        return (client, reused);
    }

    private async Task RestoreAsync()
    {
        foreach (var client in Snapshot())
        {
            if (client.Terminated)
            {
                continue;
            }

            try
            {
                await client.UpdatePlayStateAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn($"NVA restore failed: {ex.Message}");
            }
        }
    }

    private static async Task ReadLoopAsync(NvaClient client, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Log.Debug($"NVA read loop ended: {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"ERROR received data: {ex.Message}");
                return;
            }

            if (read <= 0)
            {
                Log.Info($"socket received null data (session {NvaClient.Tail(client.Session)})");
                return;
            }

            try
            {
                await client.ParseAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"ERROR received data: {ex.Message}");
                return;
            }
        }
    }

    internal IReadOnlyList<NvaClient> Snapshot()
    {
        lock (_gate)
        {
            return _clients.ToArray();
        }
    }

    // ---- broadcasting --------------------------------------------------

    /// <summary>Drops every connected device without tearing the manager down (protocol stop).</summary>
    public void DisconnectAll()
    {
        foreach (var client in Snapshot())
        {
            client.Terminate();
        }

        lock (_gate)
        {
            _clients.Clear();
        }
    }

    public Task BroadcastAsync(string command, JsonNode? data = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return _broadcasts.Writer.WriteAsync((command, data), cancellationToken).AsTask();
        }
        catch (Exception ex)
        {
            Log.Debug($"NVA broadcast '{command}' dropped: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (command, data) in _broadcasts.Reader.ReadAllAsync(cancellationToken))
            {
                foreach (var client in Snapshot())
                {
                    if (client.Terminated)
                    {
                        continue;
                    }

                    try
                    {
                        await client.SendCommandAsync(command, data, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"NVA broadcast '{command}' to {NvaClient.Tail(client.Session)} failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA broadcast loop stopped: {ex.Message}");
        }
    }

    // ---- state hooks (nirvana.py NVAProtocol.set_state_*) --------------

    /// <summary>
    /// Called by the protocol service for every DLNA state change: the NVA state machine maps
    /// it to the commands the bilibili client expects.
    /// </summary>
    public void OnStateChanged(string name, string value)
    {
        switch (name)
        {
            case "RelativeTimePosition":
                if (!string.Equals(value, _lastPosition, StringComparison.Ordinal))
                {
                    var position = PositionToSecond(value);
                    var duration = PositionToSecond(_host.GetState("CurrentMediaDuration"));
                    if (duration > 0)
                    {
                        BroadcastAsync("OnProgress", ProgressPayload(duration, position));
                    }

                    _lastPosition = value;
                }

                break;

            case "CurrentMediaDuration":
                if (!string.Equals(value, _lastDuration, StringComparison.Ordinal))
                {
                    var duration = PositionToSecond(value);
                    if (duration > 0)
                    {
                        // 播放器初始化时会把 duration 设为 0，此时不发给客户端。
                        var position = PositionToSecond(_host.GetState("RelativeTimePosition"));
                        BroadcastAsync("OnProgress", ProgressPayload(duration, position));
                    }

                    _lastDuration = value;
                }

                break;

            case "TransportState":
                OnTransportState(value);
                break;

            case "TransportStatus":
                if (string.Equals(value, "ERROR_OCCURRED", StringComparison.Ordinal))
                {
                    BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 3 });
                }

                break;

            case "TransportPlaySpeed":
                BroadcastAsync("SpeedChanged", SpeedPayload(ParseDouble(value, 1)));
                break;

            case "DisplayCurrentSubtitle":
                BroadcastAsync("OnDanmakuSwitch", new JsonObject { ["open"] = NvaJson.ParseBool(value, true) });
                break;

            case "Volume" when EmitExtendedEvents:
                BroadcastAsync("OnVolumeChanged", new JsonObject { ["volume"] = ParseInt(value, 0) });
                break;

            case "CurrentTrackTitle" when EmitExtendedEvents:
                BroadcastAsync("OnMediaChanged", new JsonObject { ["title"] = value });
                break;
        }
    }

    private void OnTransportState(string value)
    {
        switch (value)
        {
            case "PLAYING":
                BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 4 });
                if (!_mediaPlaying)
                {
                    // 播放成功
                    BroadcastAsync("PLAY_SUCCESS");
                    _mediaPlaying = true;
                }

                return;

            case "PAUSED_PLAYBACK":
                BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 5 });
                return;

            case "STOPPED":
                BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 7 });
                break;

            case "NO_MEDIA_PRESENT":
                BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 6 });
                _ = Task.Run(PlayNextEpisodeAsync, CancellationToken.None);
                break;
        }

        _mediaPlaying = false;
    }

    private static JsonObject ProgressPayload(int duration, int position) => new()
    {
        ["duration"] = duration,
        ["position"] = position
    };

    private static JsonObject SpeedPayload(double currentSpeed) => new()
    {
        ["currSpeed"] = currentSpeed,
        ["supportSpeedList"] = new JsonArray(SupportSpeeds
            .Select(speed => (JsonNode?)JsonValue.Create(speed))
            .ToArray())
    };

    // ---- auto play next episode (nirvana.py NO_MEDIA_PRESENT branch) ----

    public void SetPlayList(IReadOnlyList<NvaPlayItem> playList, int playIndex)
    {
        _playList = playList.ToList();
        _playIndex = playIndex;
        Log.Info($"NVA play list updated: {_playList.Count} item(s), index {_playIndex}");
    }

    private async Task PlayNextEpisodeAsync()
    {
        try
        {
            _playIndex++;
            if (_playIndex < _playList.Count)
            {
                var next = _playList[_playIndex];
                Log.Info($"准备播放 分集{_playIndex + 1} {next.Title}");
                var client = Snapshot().FirstOrDefault(c => !c.Terminated);
                if (client is null)
                {
                    Log.Error("客户端断开连接，播放结束");
                    return;
                }

                client.ApplyPlayItem(next);
                var resolved = await client.ResolvePlayUrlAsync(_cts.Token);
                if (resolved is null)
                {
                    Log.Warn("NVA auto play: get_video_url returned no url");
                    return;
                }

                await client.SendPlayCmdAsync(resolved.Url!, "0", _cts.Token, resolved.AudioUrl);
            }
            else
            {
                Log.Info($"分集{_playIndex} 没有下一集，播放结束");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA auto play next episode failed: {ex.Message}");
        }
        finally
        {
            _mediaPlaying = false;
        }
    }

    // ---- helpers used by NvaActions ------------------------------------

    public async Task<bool> ApplyQualityAsync(int qn, CancellationToken cancellationToken)
    {
        var client = Snapshot().FirstOrDefault(c => !c.Terminated);
        if (client is null)
        {
            Log.Warn("SwitchQuality: no NVA client connected");
            return false;
        }

        return await client.SwitchQualityAsync(qn, cancellationToken);
    }

    public JsonObject DescribeActivePlay()
    {
        var client = Snapshot().FirstOrDefault(c => !c.Terminated);
        return client?.DescribePlay() ?? new JsonObject();
    }

    public JsonObject DescribeActivePlayTotal()
    {
        var client = Snapshot().FirstOrDefault(c => !c.Terminated);
        return client?.DescribePlayInfoTotal() ?? new JsonObject();
    }

    public static int PositionToSecond(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return 0;
        }

        var parts = position.Trim().Split(':');
        if (parts.Length < 3)
        {
            return 0;
        }

        return ParseInt(parts[0], 0) * 3600 + ParseInt(parts[1], 0) * 60 + ParseInt(parts[2], 0);
    }

    public static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    public static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    public static string FormatSpeed(double speed) => speed.ToString("0.0##", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _broadcasts.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // ignore
        }

        foreach (var client in Snapshot())
        {
            client.Terminate();
        }

        lock (_gate)
        {
            _clients.Clear();
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // ignore
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// One casting device (nirvana.py NVAConectionHandler). It owns the play state, decodes the
/// frames that arrive on its socket and answers commands.
/// </summary>
public sealed class NvaClient
{
    private readonly NvaCasting _manager;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<byte> _cache = new();
    private readonly object _stateGate = new();

    private Stream? _stream;
    private int _counter;
    private int _connectionId;
    private volatile bool _terminated = true;
    private CancellationTokenSource? _pingCts;

    private string _aid = string.Empty;
    private string _oid = string.Empty;
    private string _cid = string.Empty;
    private string _roomId = "0";
    private string _accessKey = string.Empty;
    private string _title = string.Empty;
    private string _titleP = string.Empty;
    private int _epid;
    private int _seasonId;
    private int _currentQn;
    private int _desireQn;
    private int _playUrlType = 1;
    private int _contentType = 1;
    private bool _isPortrait;
    private bool _danmakuSwitchSave = true;
    private double _desireSpeed = 1;
    private JsonObject _qn = new();

    public NvaClient(NvaCasting manager, string session, string uuid)
    {
        _manager = manager;
        Session = session;
        Uuid = uuid;
    }

    /// <summary>
    /// A second socket of the same device starts without any play state; inherit it
    /// so the phone's quality menu / play info keep working over either connection.
    /// </summary>
    internal void CopyPlaybackFrom(NvaClient other)
    {
        lock (other._stateGate)
        {
            _aid = other._aid;
            _oid = other._oid;
            _cid = other._cid;
            _roomId = other._roomId;
            _accessKey = other._accessKey;
            _title = other._title;
            _titleP = other._titleP;
            _epid = other._epid;
            _seasonId = other._seasonId;
            _currentQn = other._currentQn;
            _desireQn = other._desireQn;
            _playUrlType = other._playUrlType;
            _contentType = other._contentType;
            _isPortrait = other._isPortrait;
            _danmakuSwitchSave = other._danmakuSwitchSave;
            _desireSpeed = other._desireSpeed;
            _qn = (JsonObject)other._qn.DeepClone();
        }
    }

    public string Session { get; }

    public string Uuid { get; }

    public int ConnectionId => Volatile.Read(ref _connectionId);

    public bool Terminated => _terminated;

    private INvaCastingHost Host => _manager.Host;

    public static string Tail(string value) => value.Length <= 4 ? value : value[^4..];

    // ---- socket --------------------------------------------------------

    public void Attach(Stream stream, int connectionId)
    {
        _pingCts?.Cancel();
        var previous = Interlocked.Exchange(ref _stream, stream);
        Volatile.Write(ref _connectionId, connectionId);
        _terminated = false;
        Interlocked.Exchange(ref _counter, 0);
        lock (_cache)
        {
            _cache.Clear();
        }

        if (previous is not null)
        {
            try
            {
                previous.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        _pingCts?.Dispose();
        _pingCts = CancellationTokenSource.CreateLinkedTokenSource(_manager.Token);
        var pingToken = _pingCts.Token;
        _ = Task.Run(() => PingLoopAsync(connectionId, pingToken), pingToken);
    }

    public void Detach(int connectionId)
    {
        if (Volatile.Read(ref _connectionId) != connectionId)
        {
            return;
        }

        Terminate();
    }

    public void Terminate()
    {
        _terminated = true;
        var cts = Interlocked.Exchange(ref _pingCts, null);
        try
        {
            cts?.Cancel();
        }
        catch (Exception)
        {
            // ignore
        }

        cts?.Dispose();
        var stream = Interlocked.Exchange(ref _stream, null);
        try
        {
            stream?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private async Task PingLoopAsync(int connectionId, CancellationToken cancellationToken)
    {
        Log.Info("NVA_PING_THREAD START");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                if (_terminated || Volatile.Read(ref _connectionId) != connectionId)
                {
                    break;
                }

                var counter = Interlocked.Increment(ref _counter);
                await WriteAsync(NvaFrame.Ping(unchecked((uint)counter)), cancellationToken);
                Log.Info($"NVA_PING_THREAD send ping {counter}");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA ping loop stopped: {ex.Message}");
        }

        Log.Info("NVA_PING_THREAD DONE");
    }

    private async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // we will handle these socket related errors when receiving data.
            Log.Debug($"NVA send failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA send failed: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendCommandAsync(string command, JsonNode? data, CancellationToken cancellationToken = default)
    {
        var counter = Interlocked.Increment(ref _counter);
        var json = data?.ToJsonString();
        Log.Info($"SEND_CMD{counter} cmd:{command} data:{json}");
        await WriteAsync(NvaFrame.Command(unchecked((uint)counter), command, json), cancellationToken);
    }

    public async Task SendResponseAsync(int counter, JsonNode? data, CancellationToken cancellationToken = default)
    {
        var json = data?.ToJsonString();
        Log.Info($"SEND_RES{counter} data:{json}");
        await WriteAsync(NvaFrame.Response(unchecked((uint)counter), json), cancellationToken);
    }

    // ---- frame decoding (nirvana.py parse_request) ----------------------

    public async Task ParseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[] buffer;
        lock (_cache)
        {
            _cache.AddRange(data.ToArray());
            buffer = _cache.ToArray();
        }

        var index = 0;
        while (index < buffer.Length)
        {
            if (buffer.Length - index < 6)
            {
                break;
            }

            var kind = buffer[index];
            if (kind != NvaFrame.SendCmd && kind != NvaFrame.RetCmd)
            {
                // wrong header
                index++;
                continue;
            }

            var allLength = buffer[index + 1];
            var counter = unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index + 2, 4)));

            if (kind == NvaFrame.SendCmd)
            {
                if (buffer.Length - index - 6 < 10)
                {
                    break;
                }

                var n = buffer[index + 6];
                var cmdLength = buffer[index + 7];
                var cmdStr = Encoding.ASCII.GetString(buffer, index + 8, 7);
                var methodLength = buffer[index + 15];
                if (n != 1 || cmdLength != 7 || !string.Equals(cmdStr, "Command", StringComparison.Ordinal))
                {
                    Log.Warn($"NVA: malformed command header ({n}/{cmdLength}/{cmdStr})");
                    index++;
                    continue;
                }

                if (allLength == 2)
                {
                    if (buffer.Length - index - 16 < methodLength)
                    {
                        break;
                    }

                    var method = Encoding.UTF8.GetString(buffer, index + 16, methodLength);
                    await HandleCommandAsync(method, counter, null, cancellationToken);
                    index += 16 + methodLength;
                }
                else if (allLength == 3)
                {
                    if (buffer.Length - index - 16 < methodLength + 4)
                    {
                        break;
                    }

                    var method = Encoding.UTF8.GetString(buffer, index + 16, methodLength);
                    var paramLength =
                        BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(index + 16 + methodLength, 4));
                    if (paramLength < 0 || buffer.Length - index - 20 - methodLength < paramLength)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, index + 20 + methodLength, paramLength);
                    var parameters = TryParseJson(json);
                    await HandleCommandAsync(method, counter, parameters, cancellationToken);
                    index += 20 + methodLength + paramLength;
                }
                else
                {
                    Log.Warn($"NVA: malformed command length {allLength}");
                    index++;
                }
            }
            else
            {
                if (allLength == 0)
                {
                    Log.Info($"RET{counter} params:<none>");
                    index += 6;
                }
                else if (allLength == 1)
                {
                    if (buffer.Length - index - 6 < 4)
                    {
                        break;
                    }

                    var paramLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(index + 6, 4));
                    if (paramLength < 0 || buffer.Length - index - 10 < paramLength)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, index + 10, paramLength);
                    Log.Info($"RET{counter} params:{json}");
                    index += 10 + paramLength;
                }
                else
                {
                    Log.Warn($"NVA: malformed response length {allLength}");
                    index++;
                }
            }
        }

        lock (_cache)
        {
            if (index > 0)
            {
                _cache.RemoveRange(0, Math.Min(index, _cache.Count));
            }
        }
    }

    private static JsonNode? TryParseJson(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            Log.Warn($"NVA: cannot parse command parameters: {ex.Message}");
            return null;
        }
    }

    // ---- commands (nirvana.py cmd_from_client) -------------------------

    public async Task HandleCommandAsync(string method, int counter, JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        Log.Info($"CMD{counter} method:{method} params:{parameters?.ToJsonString()}");
        var renderer = Host.Renderer;

        switch (method)
        {
            case "GetVolume":
            {
                var volume = NvaCasting.ParseInt(Host.GetState("Volume"), 0);
                await SendResponseAsync(counter, new JsonObject { ["volume"] = volume }, cancellationToken);
                break;
            }

            case "SetVolume":
            {
                var volume = NvaJson.GetInt(parameters?["volume"], -1);
                if (volume is >= 0 and <= 100)
                {
                    renderer?.SetMediaVolume(volume);
                }

                break;
            }

            case "Pause":
                await SendResponseAsync(counter, null, cancellationToken);
                renderer?.SetMediaPause();
                break;

            case "Resume":
                await SendResponseAsync(counter, null, cancellationToken);
                renderer?.SetMediaResume();
                break;

            case "SendDanmaku":
                // todo: 发送弹幕 / 通过附加字幕实现实时显示（与 python 版一致，暂不支持）
                await SendResponseAsync(counter, null, cancellationToken);
                renderer?.SetMediaText($"暂未支持弹幕：{NvaJson.GetString(parameters?["content"])}", 4000);
                break;

            case "SwitchDanmaku":
                await SendResponseAsync(counter, null, cancellationToken);
                var danmakuOpen = NvaJson.GetBool(parameters?["open"], true);
                _danmakuSwitchSave = danmakuOpen;
                // SetState also broadcasts OnDanmakuSwitch (NvaProtocolService.OnStateChanged),
                // so the phone's toggle stays in sync.
                Host.SetState("DisplayCurrentSubtitle", danmakuOpen ? "true" : "false");
                renderer?.SetMediaSubShow(danmakuOpen);
                Log.Info($"NVA SwitchDanmaku: {danmakuOpen}");
                break;

            case "SwitchSpeed":
            {
                var speed = NvaJson.GetDouble(parameters?["speed"], 1);
                renderer?.SetMediaSpeed(speed);
                renderer?.SetMediaText($"修改倍速：{NvaCasting.FormatSpeed(speed)}X", 2000);
                break;
            }

            case "SwitchQn":
                await SendResponseAsync(counter, null, cancellationToken);
                await SwitchQualityAsync(NvaJson.GetInt(parameters?["qn"]), cancellationToken);
                break;

            case "Stop":
                await SendResponseAsync(counter, null, cancellationToken);
                renderer?.SetMediaStop();
                break;

            case "Play":
                await HandlePlayAsync(counter, parameters, cancellationToken);
                break;

            case "PlayUrl":
                await HandlePlayUrlAsync(counter, parameters, cancellationToken);
                break;

            case "Seek":
                await HandleSeekAsync(counter, parameters, cancellationToken);
                break;

            case "GetPlayInfo":
                // The phone sends this right after reconnecting and expects a
                // LinkPlayInfoTotal JSON in the c0 response (quality menu source).
                await SendResponseAsync(counter, DescribePlayInfoTotal(), cancellationToken);
                break;

            default:
                Log.Warn($"NVA: unhandled command '{method}' from session {Tail(Session)}, answering empty like python");
                await SendResponseAsync(counter, null, cancellationToken);
                break;
        }
    }

    private async Task HandlePlayAsync(int counter, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await SendResponseAsync(counter, null, cancellationToken);

        var aid = NvaJson.GetString(parameters?["aid"]);
        if (aid.Length == 0)
        {
            // python raises KeyError here, which stops the manager thread; keep the client alive instead.
            Log.Warn("NVA Play: parameters without aid");
            return;
        }

        _aid = aid;
        _oid = NvaJson.GetString(parameters?["oid"], _aid);
        _cid = NvaJson.GetString(parameters?["cid"]);
        _roomId = NvaJson.GetString(parameters?["roomId"], "0");
        _epid = NvaJson.GetInt(parameters?["epId"]);
        _accessKey = NvaJson.GetString(parameters?["accessKey"]);
        _currentQn = NvaJson.GetInt(parameters?["userDesireQn"]);
        _desireQn = _currentQn > 0 ? _currentQn : NvaCasting.DefaultDesireQn;
        _contentType = NvaJson.GetInt(parameters?["contentType"], 1);
        _seasonId = NvaJson.GetInt(parameters?["seasonId"]);
        _playUrlType = 1;
        _danmakuSwitchSave = NvaJson.GetBool(parameters?["danmakuSwitchSave"], true);
        _desireSpeed = NvaJson.GetDouble(parameters?["userDesireSpeed"], 1);
        _title = string.Empty;

        if (_epid != 0)
        {
            // 番剧
            _oid = _epid.ToString(CultureInfo.InvariantCulture);
            _playUrlType = 2;
        }

        if (NvaCasting.ParseInt(_roomId, 0) != 0)
        {
            _playUrlType = 4;
            _oid = _roomId;
        }

        var resolved = await ResolvePlayUrlAsync(cancellationToken);
        if (resolved is null)
        {
            Log.Warn("NVA Play: get_video_url returned no url");
            return;
        }

        await SendPlayCmdAsync(resolved.Url!, NvaJson.GetString(parameters?["seekTs"], "0"), cancellationToken,
            resolved.AudioUrl);
    }

    private async Task HandlePlayUrlAsync(int counter, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await SendResponseAsync(counter, null, cancellationToken);

        var url = NvaJson.GetString(parameters?["url"]);
        _title = NvaJson.GetString(parameters?["title"]);

        var videoInfo = BilibiliApi.ParseNvaExtension(url);
        var version = NvaJson.GetInt(videoInfo?["ver"], -1);
        if (version != 2)
        {
            Log.Error("Maybe error in url parse");
        }

        var content = videoInfo?["content"] as JsonObject;
        _qn = new JsonObject
        {
            ["curQn"] = 0,
            ["supportQnList"] = new JsonArray(new JsonObject
            {
                ["description"] = string.Empty,
                ["displayDesc"] = string.Empty,
                ["needLogin"] = false,
                ["needVip"] = false,
                ["quality"] = 0,
                ["superscript"] = string.Empty
            }),
            ["userDesireQn"] = 0
        };

        // todo: 开始时 seek（python 版同样未处理 PlayUrl 的额外字段）
        // ver=2 uses long keys (x13.e.a in the phone app); the older NvaExtContent
        // variant uses short ones (key/sid/eid/ct/sk), so accept both.
        _aid = NvaJson.GetString(content?["aid"]);
        _oid = NvaJson.GetString(content?["oid"], _aid);
        _cid = NvaJson.GetString(content?["cid"]);
        _epid = NvaJson.GetInt(content?["epId"]);
        if (_epid == 0)
        {
            _epid = NvaJson.GetInt(content?["eid"]);
        }

        _accessKey = NvaJson.GetString(content?["accessKey"]);
        if (_accessKey.Length == 0)
        {
            _accessKey = NvaJson.GetString(content?["key"]);
        }

        _currentQn = NvaJson.GetInt(content?["userDesireQn"]);
        _desireQn = _currentQn > 0 ? _currentQn : NvaCasting.DefaultDesireQn;
        _contentType = NvaJson.GetInt(content?["contentType"], NvaJson.GetInt(content?["ct"], 1));
        _playUrlType = 1;
        _seasonId = NvaJson.GetInt(content?["seasonId"]);
        if (_seasonId == 0)
        {
            _seasonId = NvaJson.GetInt(content?["sid"]);
        }

        _desireSpeed = NvaJson.GetDouble(content?["userDesireSpeed"], 1);
        _danmakuSwitchSave = NvaJson.GetBool(content?["danmakuSwitchSave"], true);

        if (_epid != 0)
        {
            // 番剧
            _oid = _epid.ToString(CultureInfo.InvariantCulture);
            _playUrlType = 2;
        }

        if (url.Length == 0)
        {
            Log.Warn("NVA PlayUrl: parameters without url");
            return;
        }

        // nirvana.py passes the bilibili:// link straight to the renderer here and never
        // resolves playurl, which leaves the client on qn=0 / an empty supportQnList.
        // Resolve like the Play command so the phone gets the real quality list.
        var resolved = await ResolvePlayUrlAsync(cancellationToken);
        if (resolved is null)
        {
            Log.Warn("NVA PlayUrl: get_video_url returned no url");
            return;
        }

        var seek = content?["seekTs"] is not null
            ? NvaJson.GetString(content?["seekTs"])
            : NvaJson.GetString(content?["sk"], "0");
        await SendPlayCmdAsync(resolved.Url!, seek, cancellationToken, resolved.AudioUrl);
    }

    private async Task HandleSeekAsync(int counter, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await SendResponseAsync(counter, null, cancellationToken);

        var position = NvaJson.GetInt(parameters?["seekTs"]);
        var duration = NvaCasting.PositionToSecond(Host.GetState("CurrentMediaDuration"));
        await _manager.BroadcastAsync("OnProgress", new JsonObject
        {
            ["duration"] = duration,
            ["position"] = position
        }, cancellationToken);

        var text = $"{position / 3600}:{position % 3600 / 60:D2}:{position % 60:D2}";
        Host.Renderer?.SetMediaPosition(text);
    }

    // ---- playback (nirvana.py send_play_cmd / update_play_state) --------

    public async Task<NvaPlayUrlResult?> ResolvePlayUrlAsync(CancellationToken cancellationToken)
    {
        var request = new NvaPlayUrlRequest
        {
            AccessKey = _accessKey,
            Cid = _cid,
            Oid = _oid,
            OgvAid = _aid,
            DesireQn = _desireQn,
            PlayUrlType = _playUrlType,
            // Only mpv-family renderers can attach the separate DASH audio track.
            PreferDash = Host.Renderer is IAudioTrackRenderer
        };

        var result = await Host.Bilibili.GetPlayUrlAsync(request, cancellationToken);
        if (result is null || string.IsNullOrEmpty(result.Url))
        {
            return null;
        }

        _currentQn = result.CurrentQn;
        _qn = result.Qn;
        Log.Info($"NVA playurl: curQn={result.CurrentQn} desireQn={_desireQn} " +
                 $"dash={(result.AudioUrl is not null ? "yes" : "no")} accessKey={(_accessKey.Length > 0 ? "yes" : "no")}");
        return result;
    }

    public async Task SendPlayCmdAsync(string url, string start, CancellationToken cancellationToken,
        string? audioUrl = null)
    {
        Host.SetState("CurrentTrackURI", url);

        var renderer = Host.Renderer;
        if (!string.IsNullOrEmpty(audioUrl) && renderer is IAudioTrackRenderer audioRenderer)
        {
            audioRenderer.SetMediaUrlWithAudio(url, start, audioUrl);
        }
        else
        {
            renderer?.SetMediaUrl(url, start);
        }

        renderer?.SetMediaSpeed(_desireSpeed);
        renderer?.SetMediaTitle($"{_title} {_titleP}");

        // 通知客户端，正在加载
        await _manager.BroadcastAsync("OnPlayState", new JsonObject { ["playState"] = 3 }, cancellationToken);
        await UpdatePlayStateAsync(cancellationToken);
        _ = Task.Run(GetExtraInfoAsync, CancellationToken.None);
    }

    public async Task UpdatePlayStateAsync(CancellationToken cancellationToken)
    {
        // Honour the current switch even when the phone did not ask to persist it,
        // otherwise every new episode silently turns danmaku back on.
        var danmakuOpen = NvaJson.ParseBool(Host.GetState("DisplayCurrentSubtitle"), true);

        Host.Renderer?.SetMediaSubShow(danmakuOpen);
        await _manager.BroadcastAsync("OnDanmakuSwitch", new JsonObject { ["open"] = danmakuOpen },
            cancellationToken);

        await _manager.BroadcastAsync("OnEpisodeSwitch", new JsonObject
        {
            ["playItem"] = new JsonObject
            {
                ["aid"] = _aid,
                ["cid"] = _cid,
                ["contentType"] = _contentType,
                ["epId"] = _epid,
                ["seasonId"] = _seasonId
            },
            ["qnDesc"] = _qn.DeepClone(),
            ["title"] = $"{_title} {_titleP}"
        }, cancellationToken);

        await _manager.BroadcastAsync("OnQnSwitch", _qn.DeepClone(), cancellationToken);

        var currentSpeed = Math.Round(NvaCasting.ParseDouble(Host.GetState("TransportPlaySpeed"), 1), 2);
        await _manager.BroadcastAsync("SpeedChanged", new JsonObject
        {
            ["currSpeed"] = currentSpeed,
            ["supportSpeedList"] = new JsonArray(NvaCasting.SupportSpeeds
                .Select(speed => (JsonNode?)JsonValue.Create(speed))
                .ToArray())
        }, cancellationToken);
    }

    private async Task GetExtraInfoAsync()
    {
        try
        {
            // 直播暂不获取额外的信息
            if (_playUrlType == 4)
            {
                Host.Renderer?.SetMediaTitle($"直播间: {_roomId}");
                return;
            }

            Log.Info("NVA: start get_extra_info");
            var subtitlePath = Path.Combine(AppSettings.ConfigDir, "macast.ass");
            var downloaded = await Host.Bilibili.DownloadDanmakuAsync(_cid, subtitlePath, CancellationToken.None);
            if (downloaded is not null)
            {
                Host.Renderer?.SetMediaSubFile(downloaded, "弹幕");
            }

            // 不获取课程的视频信息
            if (_contentType != 0 && _contentType != 2)
            {
                await LoadVideoInfoAsync();
            }

            Log.Info("NVA: end get_extra_info");
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA get_extra_info failed: {ex.Message}");
        }
    }

    private async Task LoadVideoInfoAsync()
    {
        var info = await Host.Bilibili.GetVideoInfoAsync(_oid, _seasonId, _epid, _cid, CancellationToken.None);
        if (info is null)
        {
            Log.Warn("NVA: get_video_info failed");
            return;
        }

        _title = info.Title;
        _isPortrait = info.IsPortrait;
        if (info.CurrentEpisodeTitle.Length > 0)
        {
            // 注意：python 版这里只更新播放器标题，不写回 self.title_p
            Host.Renderer?.SetMediaTitle($"{_title} {info.CurrentEpisodeTitle}");
        }

        _manager.SetPlayList(info.PlayList, info.PlayIndex);
    }

    public async Task<bool> SwitchQualityAsync(int qn, CancellationToken cancellationToken)
    {
        if (_terminated)
        {
            return false;
        }

        _desireQn = qn;
        var resolved = await ResolvePlayUrlAsync(cancellationToken);
        if (resolved is null)
        {
            Log.Warn($"NVA SwitchQn: get_video_url returned no url for qn {qn}");
            return false;
        }

        var position = Host.GetState("RelativeTimePosition");
        await SendPlayCmdAsync(resolved.Url!, position, cancellationToken, resolved.AudioUrl);
        return true;
    }

    // ---- auto play next episode ----------------------------------------

    public void ApplyPlayItem(NvaPlayItem item)
    {
        _cid = item.Cid;
        _aid = item.Aid;
        _oid = item.Aid;
        if (!IsZeroEpid(item.Epid))
        {
            _oid = item.Epid;
            _epid = NvaCasting.ParseInt(item.Epid, 0);
        }

        _titleP = item.Title;
        _isPortrait = item.IsPortrait;
    }

    private static bool IsZeroEpid(string epid)
    {
        if (epid.Length == 0)
        {
            return true;
        }

        return int.TryParse(epid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value == 0;
    }

    // ---- soap GetPlayInfo ----------------------------------------------

    /// <summary>
    /// The SOAP GetPlayInfo payload. The phone parses it as LinkPlayInfoTotal:
    /// playState (3 loading / 4 playing / 5 paused / 7 stopped), position and
    /// duration in milliseconds, danmakuState, title, volume, playItem, qn.
    /// Without this shape the quality menu on the phone falls back to qn 0.
    /// </summary>
    public JsonObject DescribePlayInfoTotal()
    {
        lock (_stateGate)
        {
            var transport = Host.GetState("TransportState");
            var playState = transport switch
            {
                "PLAYING" => 4,
                "PAUSED_PLAYBACK" => 5,
                "STOPPED" or "NO_MEDIA_PRESENT" => 7,
                _ => 3
            };

            var positionMs = (long)NvaCasting.PositionToSecond(Host.GetState("RelativeTimePosition")) * 1000;
            var durationMs = (long)NvaCasting.PositionToSecond(Host.GetState("CurrentMediaDuration")) * 1000;
            var danmakuState = NvaJson.ParseBool(Host.GetState("DisplayCurrentSubtitle"), true);

            return new JsonObject
            {
                ["playState"] = playState,
                ["position"] = positionMs,
                ["duration"] = durationMs,
                ["danmakuState"] = danmakuState,
                ["title"] = $"{_title} {_titleP}".Trim(),
                ["volume"] = Host.GetState("Volume"),
                ["playItem"] = new JsonObject
                {
                    ["aid"] = _aid,
                    ["cid"] = _cid,
                    ["epId"] = _epid.ToString(CultureInfo.InvariantCulture),
                    ["seasonId"] = _seasonId.ToString(CultureInfo.InvariantCulture)
                },
                ["qn"] = _qn.DeepClone()
            };
        }
    }

    public JsonObject DescribePlay()
    {
        lock (_stateGate)
        {
            return new JsonObject
            {
                ["aid"] = _aid,
                ["oid"] = _oid,
                ["cid"] = _cid,
                ["epId"] = _epid,
                ["seasonId"] = _seasonId,
                ["roomId"] = _roomId,
                ["contentType"] = _contentType,
                ["playUrlType"] = _playUrlType,
                ["curQn"] = _currentQn,
                ["userDesireQn"] = _desireQn,
                ["isPortrait"] = _isPortrait,
                ["title"] = _title,
                ["titleP"] = _titleP,
                ["qnDesc"] = _qn.DeepClone()
            };
        }
    }
}
