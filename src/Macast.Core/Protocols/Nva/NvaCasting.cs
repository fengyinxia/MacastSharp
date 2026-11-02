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
    internal NvaPlaybackState Playback { get; } = new();
    internal SemaphoreSlim Commands { get; } = new(1, 1);
    private CancellationTokenSource? _extraCts;
    private Task _extraTask = Task.CompletedTask;
    private Task _nextTask = Task.CompletedTask;
    private CancellationTokenSource _backgroundCts = new();
    private readonly object _gate = new();
    private readonly List<NvaClient> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(string Command, JsonNode? Data)> _broadcasts =
        Channel.CreateBounded<(string, JsonNode?)>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

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
                await client.UpdatePlayStateAsync(cancellationToken);

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
                // Replace a duplicate live socket; playback lives in the manager.
                Log.Info("NVA: 设备断线重连，恢复之前的 session");
                client = sameSession;
                reused = true;
            }
            else
            {
                client = new NvaClient(this, session, uuid);

                // Both phone sockets share the same playback, including after a disconnect.
                Log.Info("NVA: control connection attached");

                _clients.Add(client);
            }

            var connectionId = Interlocked.Increment(ref _connectionSeq);
            client.Attach(stream, connectionId);
        }

        return (client, reused);
    }

    private static async Task ReadLoopAsync(NvaClient client, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
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

    internal async Task CancelExtraInfoAsync()
    {
        _extraCts?.Cancel();
        await _extraTask;
        _extraCts?.Dispose();
        _extraCts = null;
        _extraTask = Task.CompletedTask;
    }

    internal async Task StartExtraInfoAsync(Func<CancellationToken, Task> work)
    {
        await CancelExtraInfoAsync();
        _extraCts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundCts.Token, _cts.Token);
        var token = _extraCts.Token;
        _extraTask = Task.Run(() => work(token), CancellationToken.None);
    }

    public async Task StopBackgroundAsync()
    {
        _backgroundCts.Cancel();
        await _nextTask;
        await CancelExtraInfoAsync();
        _backgroundCts.Dispose();
        _backgroundCts = new CancellationTokenSource();
        _playList.Clear();
        _mediaPlaying = false;
        _lastPosition = _lastDuration = string.Empty;
    }

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
                Playback.Speed = ParseDouble(value, 1);
                BroadcastAsync("SpeedChanged", SpeedPayload(ParseDouble(value, 1)));
                break;

            case "DisplayCurrentSubtitle":
                Playback.DanmakuOpen = NvaJson.ParseBool(value, true);
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
                if (_nextTask.IsCompleted && !_backgroundCts.IsCancellationRequested)
                {
                    var token = _backgroundCts.Token;
                    _nextTask = Task.Run(() => PlayNextEpisodeAsync(token), CancellationToken.None);
                }
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

    private async Task PlayNextEpisodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Commands.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            await CancelExtraInfoAsync();
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
                var resolved = await client.ResolvePlayUrlAsync(cancellationToken);
                if (resolved is null)
                {
                    Log.Warn("NVA auto play: get_video_url returned no url");
                    return;
                }

                await client.SendPlayCmdAsync(resolved.Url!, "0", cancellationToken, resolved.AudioUrl, resolved.Segments);
            }
            else
            {
                Log.Info($"分集{_playIndex} 没有下一集，播放结束");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"NVA auto play next episode failed: {ex.Message}");
        }
        finally
        {
            Commands.Release();
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

        await Commands.WaitAsync(cancellationToken);
        try { return await client.SwitchQualityAsync(qn, cancellationToken); }
        finally { Commands.Release(); }
    }

    public JsonObject DescribeActivePlay()
    {
        var client = Snapshot().FirstOrDefault(c => !c.Terminated);
        return (client ?? new NvaClient(this, string.Empty, string.Empty)).DescribePlay();
    }

    public JsonObject DescribeActivePlayTotal()
    {
        var client = Snapshot().FirstOrDefault(c => !c.Terminated);
        return (client ?? new NvaClient(this, string.Empty, string.Empty)).DescribePlayInfoTotal();
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
        await StopBackgroundAsync();
        _backgroundCts.Dispose();
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
/// One control connection (nirvana.py NVAConectionHandler). It shares playback state and decodes the
/// frames that arrive on its socket and answers commands.
/// </summary>
public sealed class NvaClient
{
    private readonly NvaCasting _manager;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private const int MaxFrameSize = 1024 * 1024;
    private readonly object _cacheGate = new();
    private byte[] _cache = new byte[4096];
    private int _cacheCount;
    private object _stateGate => _manager.Playback.Gate;

    private Stream? _stream;
    private int _counter;
    private int _connectionId;
    private volatile bool _terminated = true;
    private CancellationTokenSource? _pingCts;

    private ref string _aid => ref _manager.Playback.Aid;
    private ref string _oid => ref _manager.Playback.Oid;
    private ref string _cid => ref _manager.Playback.Cid;
    private ref string _roomId => ref _manager.Playback.RoomId;
    private ref string _accessKey => ref _manager.Playback.AccessKey;
    private ref string _title => ref _manager.Playback.Title;
    private ref string _titleP => ref _manager.Playback.EpisodeTitle;
    private ref int _epid => ref _manager.Playback.Epid;
    private ref int _seasonId => ref _manager.Playback.SeasonId;
    private ref int _currentQn => ref _manager.Playback.CurrentQn;
    private ref int _desireQn => ref _manager.Playback.DesireQn;
    private ref int _playUrlType => ref _manager.Playback.PlayUrlType;
    private ref int _contentType => ref _manager.Playback.ContentType;
    private ref bool _isPortrait => ref _manager.Playback.IsPortrait;
    private ref bool _danmakuSwitchSave => ref _manager.Playback.DanmakuOpen;
    private ref double _desireSpeed => ref _manager.Playback.Speed;
    private ref JsonObject _qn => ref _manager.Playback.Qn;

    public NvaClient(NvaCasting manager, string session, string uuid)
    {
        _manager = manager;
        Session = session;
        Uuid = uuid;
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
        lock (_cacheGate)
        {
            _cache = new byte[4096];
            _cacheCount = 0;
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
                Log.Debug($"NVA ping {counter}");
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(data, timeout.Token);
            await stream.FlushAsync(timeout.Token);
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
        Log.Debug($"SEND_CMD{counter} cmd:{command}");
        await WriteAsync(NvaFrame.Command(unchecked((uint)counter), command, json), cancellationToken);
    }

    public async Task SendResponseAsync(int counter, JsonNode? data, CancellationToken cancellationToken = default)
    {
        var json = data?.ToJsonString();
        Log.Debug($"SEND_RES{counter}");
        await WriteAsync(NvaFrame.Response(unchecked((uint)counter), json), cancellationToken);
    }

    // ---- frame decoding (nirvana.py parse_request) ----------------------

    public async Task ParseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[] buffer;
        int bufferLength;
        lock (_cacheGate)
        {
            var required = _cacheCount + data.Length;
            if (required > MaxFrameSize + 2048)
                throw new InvalidDataException("NVA frame buffer too large");
            if (required > _cache.Length)
                Array.Resize(ref _cache, Math.Max(required, _cache.Length * 2));
            data.CopyTo(_cache.AsMemory(_cacheCount));
            _cacheCount = required;
            buffer = _cache;
            bufferLength = _cacheCount;
        }

        var index = 0;
        while (index < bufferLength)
        {
            if (bufferLength - index < 6)
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
                if (bufferLength - index - 6 < 10)
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
                    if (bufferLength - index - 16 < methodLength)
                    {
                        break;
                    }

                    var method = Encoding.UTF8.GetString(buffer, index + 16, methodLength);
                    await HandleCommandAsync(method, counter, null, cancellationToken);
                    index += 16 + methodLength;
                }
                else if (allLength == 3)
                {
                    if (bufferLength - index - 16 < methodLength + 4)
                    {
                        break;
                    }

                    var method = Encoding.UTF8.GetString(buffer, index + 16, methodLength);
                    var paramLength =
                        BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(index + 16 + methodLength, 4));
                    if (paramLength < 0 || paramLength > MaxFrameSize - 20 - methodLength)
                        throw new InvalidDataException("invalid NVA frame length");
                    if (bufferLength - index - 20 - methodLength < paramLength)
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
                    Log.Debug($"RET{counter}");
                    index += 6;
                }
                else if (allLength == 1)
                {
                    if (bufferLength - index - 6 < 4)
                    {
                        break;
                    }

                    var paramLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(index + 6, 4));
                    if (paramLength < 0 || paramLength > MaxFrameSize - 10)
                        throw new InvalidDataException("invalid NVA frame length");
                    if (bufferLength - index - 10 < paramLength)
                    {
                        break;
                    }

                    Log.Debug($"RET{counter}");
                    index += 10 + paramLength;
                }
                else
                {
                    Log.Warn($"NVA: malformed response length {allLength}");
                    index++;
                }
            }
        }

        lock (_cacheGate)
        {
            if (ReferenceEquals(buffer, _cache) && index > 0)
            {
                _cacheCount -= index;
                Buffer.BlockCopy(buffer, index, _cache, 0, _cacheCount);
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
        await _manager.Commands.WaitAsync(cancellationToken);
        try { await HandleCommandCoreAsync(method, counter, parameters, cancellationToken); }
        finally { _manager.Commands.Release(); }
    }

    private async Task HandleCommandCoreAsync(string method, int counter, JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        Log.Info($"CMD{counter} method:{method}");
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
                if (!NvaCasting.SupportSpeeds.Contains(speed)) break;
                _desireSpeed = speed;
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
                await _manager.CancelExtraInfoAsync();
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

        await _manager.CancelExtraInfoAsync();
        _manager.SetPlayList(Array.Empty<NvaPlayItem>(), 0);
        _titleP = string.Empty;
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
            resolved.AudioUrl, resolved.Segments);
    }

    private async Task HandlePlayUrlAsync(int counter, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await SendResponseAsync(counter, null, cancellationToken);

        var url = NvaJson.GetString(parameters?["url"]);

        var videoInfo = BilibiliApi.ParseNvaExtension(url);
        var version = NvaJson.GetInt(videoInfo?["ver"], -1);
        if (version != 2)
        {
            Log.Error("Maybe error in url parse");
        }

        var content = videoInfo?["content"] as JsonObject;
        if (content is null || NvaJson.GetString(content["cid"]).Length == 0)
        {
            Log.Warn("NVA PlayUrl: missing video content");
            return;
        }
        await _manager.CancelExtraInfoAsync();
        _manager.SetPlayList(Array.Empty<NvaPlayItem>(), 0);
        _roomId = "0";
        _titleP = string.Empty;
        _title = NvaJson.GetString(parameters?["title"]);
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
        await SendPlayCmdAsync(resolved.Url!, seek, cancellationToken, resolved.AudioUrl, resolved.Segments);
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
        var accessKey = _accessKey.Length > 0 ? _accessKey : NvaCredentials.GetAccessKey(Host.Settings);
        var request = new NvaPlayUrlRequest
        {
            AccessKey = accessKey,
            Cid = _cid,
            Oid = _oid,
            OgvAid = _aid,
            DesireQn = _desireQn,
            PlayUrlType = _playUrlType,
            // MPV/Live and PotPlayer can play DASH video with its separate audio.
            PreferDash = Host.Renderer is IAudioTrackRenderer
        };

        var result = await Host.Bilibili.GetPlayUrlAsync(request, cancellationToken);
        if (result is null || string.IsNullOrEmpty(result.Url))
        {
            return null;
        }

        if (result.Segments is { Count: > 1 } && Host.Renderer is not ISegmentedRenderer)
        {
            Log.Warn("NVA: this video has multiple segments; select MPV or Live to play the complete video");
            Host.SetState("TransportStatus", "ERROR_OCCURRED");
            return null;
        }
        _currentQn = result.CurrentQn;
        _qn = result.Qn;
        return result;
    }

    public async Task SendPlayCmdAsync(string url, string start, CancellationToken cancellationToken,
        string? audioUrl = null, IReadOnlyList<MediaSegment>? segments = null)
    {
        Host.SetState("CurrentTrackURI", url);

        var renderer = Host.Renderer;
        if (segments is { Count: > 1 } && renderer is ISegmentedRenderer segmented)
        {
            segmented.SetMediaSegments(segments, start);
        }
        else if (!string.IsNullOrEmpty(audioUrl) && renderer is IAudioTrackRenderer audioRenderer)
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
        await _manager.StartExtraInfoAsync(GetExtraInfoAsync);
    }

    public async Task UpdatePlayStateAsync(CancellationToken cancellationToken)
    {
        // Both SOAP and control-channel switches update the shared preference.
        var danmakuOpen = _danmakuSwitchSave;

        Host.SetState("DisplayCurrentSubtitle", danmakuOpen ? "true" : "false");
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

    private async Task GetExtraInfoAsync(CancellationToken cancellationToken)
    {
        var cid = _cid;
        var oid = _oid;
        var seasonId = _seasonId;
        var epid = _epid;
        var contentType = _contentType;
        var subtitlePath = Path.Combine(AppSettings.ConfigDir, "macast.ass");
        var pendingPath = subtitlePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 直播暂不获取额外的信息
            if (_playUrlType == 4)
            {
                Host.Renderer?.SetMediaTitle($"直播间: {_roomId}");
                return;
            }

            Log.Info("NVA: start get_extra_info");
            var downloaded = await Host.Bilibili.DownloadDanmakuAsync(cid, pendingPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (downloaded is not null)
            {
                File.Move(downloaded, subtitlePath, overwrite: true);
                Host.Renderer?.SetMediaSubFile(subtitlePath, "弹幕");
            }

            if (contentType != 0 && contentType != 2)
                await LoadVideoInfoAsync(oid, seasonId, epid, cid, cancellationToken);

            Log.Info("NVA: end get_extra_info");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"NVA get_extra_info failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
        }
    }

    private async Task LoadVideoInfoAsync(string oid, int seasonId, int epid, string cid, CancellationToken cancellationToken)
    {
        var info = await Host.Bilibili.GetVideoInfoAsync(oid, seasonId, epid, cid, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (info is null)
        {
            Log.Warn("NVA: get_video_info failed");
            return;
        }

        _title = info.Title;
        _isPortrait = info.IsPortrait;
        if (info.CurrentEpisodeTitle.Length > 0)
        {
            _titleP = info.CurrentEpisodeTitle;
            Host.Renderer?.SetMediaTitle($"{_title} {_titleP}");
        }

        _manager.SetPlayList(info.PlayList, info.PlayIndex);
    }

    public async Task<bool> SwitchQualityAsync(int qn, CancellationToken cancellationToken)
    {
        if (_terminated)
        {
            return false;
        }

        if (qn <= 0) return false;
        await _manager.CancelExtraInfoAsync();
        _desireQn = qn;
        var resolved = await ResolvePlayUrlAsync(cancellationToken);
        if (resolved is null)
        {
            Log.Warn($"NVA SwitchQn: get_video_url returned no url for qn {qn}");
            return false;
        }

        var position = Host.GetState("RelativeTimePosition");
        await SendPlayCmdAsync(resolved.Url!, position, cancellationToken, resolved.AudioUrl, resolved.Segments);
        return true;
    }

    // ---- auto play next episode ----------------------------------------

    public void ApplyPlayItem(NvaPlayItem item)
    {
        _cid = item.Cid;
        _aid = item.Aid;
        _oid = string.IsNullOrEmpty(item.Oid) ? item.Aid : item.Oid;
        _epid = 0;
        _roomId = "0";
        _playUrlType = 1;
        if (!IsZeroEpid(item.Epid))
        {
            _oid = item.Epid;
            _epid = NvaCasting.ParseInt(item.Epid, 0);
            _playUrlType = 2;
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
