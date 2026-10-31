using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Renderer;

public sealed class MpvRenderer : IRenderer, IAudioTrackRenderer
{
    private readonly AppSettings _settings;
    private readonly IProtocolStateSink _protocol;
    private readonly bool _forceSimulation;
    private readonly string _name;
    private readonly MpvLaunchProfile _profile;
    private readonly string _ipcPath;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<object[]> _pendingCommands = new();
    private readonly ConcurrentDictionary<int, string> _pendingRequests = new();
    private int _requestId;
    private CancellationTokenSource _cts = new();

    private Process? _process;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _processTask;
    private Task? _ipcTask;
    private volatile bool _running;
    private bool _ipcConnected;
    private bool _simulated;
    private bool _playing;
    private bool _paused;
    private string _title = string.Empty;

    public string Name => _name;
    public bool Available => !_simulated;

    public MpvRenderer(RendererContext context, MpvLaunchProfile? profile = null)
    {
        _settings = context.Settings;
        _protocol = context.Protocol;
        _forceSimulation = context.ForceSimulation;
        _profile = profile ?? new MpvLaunchProfile();
        _name = _profile.Name;
        var suffix = Random.Shared.Next(0, 9999);
        _pipeName = $"macast_mpvsocket{suffix}";
        _ipcPath = OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{_pipeName}"
            : $"/tmp/{_pipeName}";
        _title = _settings.FriendlyName;
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        _running = true;
        _simulated = _forceSimulation || !MpvExists();
        if (_simulated)
        {
            Log.Warn("mpv is not available; running in simulated mode (protocol works, no video)");
            _protocol.SetState("Volume", _settings.GetInt("PlayerDefaultVolume", 100));
            _protocol.SetState("Mute", false);
            _protocol.SetState("DisplayCurrentSubtitle", true);
            return;
        }

        _processTask = Task.Run(ProcessLoopAsync);
        _ipcTask = Task.Run(IpcLoopAsync);
    }

    public void Stop()
    {
        if (!_running && _process is null)
        {
            return;
        }

        _running = false;
        try
        {
            if (_writer is not null)
            {
                _ = SendCommandAsync("quit");
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }

        _cts.Cancel();
        CloseStream();

        try
        {
            _processTask?.Wait(TimeSpan.FromSeconds(3));
            _ipcTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        _process?.Dispose();
        _process = null;
        _processTask = null;
        _ipcTask = null;
        _pendingCommands.Clear();
        _simulated = false;
    }

    private bool MpvExists()
    {
        if (!string.IsNullOrWhiteSpace(_profile.ExecutablePath))
        {
            return File.Exists(_profile.ExecutablePath);
        }

        var path = _settings.MpvDefaultPath;
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path);
        }

        if (!string.Equals(path, "mpv", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        var exe = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "bin", exe);
        if (File.Exists(cwdCandidate))
        {
            return true;
        }

        var env = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .Any(File.Exists);
    }

    private async Task ProcessLoopAsync()
    {
        var attempts = 3;
        while (_running && attempts > 0)
        {
            try
            {
                var info = BuildStartInfo();
                _process = Process.Start(info) ?? throw new InvalidOperationException("mpv process did not start");
                _ = PumpOutputAsync(_process.StandardError);
                _ = PumpOutputAsync(_process.StandardOutput);
                Log.Info($"mpv started, pid={_process.Id}, ipc={_ipcPath}");
                await _process.WaitForExitAsync(_cts.Token);
                Log.Info($"mpv exited with code {_process.ExitCode}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"start mpv failed: {ex.Message}");
                _simulated = true;
                return;
            }

            if (!_running)
            {
                break;
            }

            if (_ipcConnected)
            {
                break;
            }

            attempts--;
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (attempts <= 0)
        {
            Log.Error("mpv could not be started after 3 attempts");
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = ResolveMpvPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        void Add(string arg) => info.ArgumentList.Add(arg);

        Add($"{_profile.IpcServerArgument}={_ipcPath}");

        foreach (var extra in _profile.ExtraArguments)
        {
            Add(extra);
        }

        if (!_profile.UseStandardWindowArguments)
        {
            return info;
        }

        Add("--image-display-duration=inf");
        Add("--idle=yes");
        Add("--no-terminal");
        Add("--hwdec=yes");
        Add("--save-position-on-quit=yes");
        Add("--script-opts=osc-timetotal=yes,osc-layout=bottombar,osc-title=${title},osc-showwindowed=yes,osc-seekbarstyle=bar,osc-visibility=auto");

        if (_settings.GetInt("PlayerOntop", 1) != 0)
        {
            Add("--ontop");
        }

        var position = _settings.GetInt("PlayerPosition", 2);
        var positionData = new[] { (2, 5), (2, 98), (98, 5), (98, 98), (50, 50) };
        var (x, y) = positionData[Math.Clamp(position, 0, positionData.Length - 1)];
        Add($"--geometry={x}%:{y}%");

        var size = _settings.GetInt("PlayerSize", 1);
        if (size <= 2)
        {
            Add($"--autofit={15 - 2.5 * size + 7.5 * size * size:0}%");
        }
        else if (size == 3)
        {
            Add("--autofit-larger=90%");
        }
        else if (size == 4)
        {
            Add("--fullscreen");
        }

        if (OperatingSystem.IsMacOS())
        {
            Add("--ontop-level=system");
            Add("--on-all-workspaces");
            Add("--macos-app-activation-policy=accessory");
        }

        var hw = _settings.GetInt("PlayerHW", 1);
        if (hw == 0)
        {
            info.ArgumentList.Remove("--hwdec=yes");
        }
        else if (hw == 2 && OperatingSystem.IsMacOS())
        {
            Add("--macos-force-dedicated-gpu=yes");
        }

        return info;
    }

    private string ResolveMpvPath()
    {
        if (!string.IsNullOrWhiteSpace(_profile.ExecutablePath))
        {
            return _profile.ExecutablePath;
        }

        var configured = _settings.MpvDefaultPath;
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        if (!string.Equals(configured, "mpv", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, configured);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var exe = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        var bundled = Path.Combine(AppContext.BaseDirectory, "bin", exe);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var cwdBundled = Path.Combine(Environment.CurrentDirectory, "bin", exe);
        return File.Exists(cwdBundled) ? cwdBundled : configured;
    }

    private async Task PumpOutputAsync(StreamReader? reader)
    {
        if (reader is null)
        {
            return;
        }

        try
        {
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Log.Debug("mpv: " + line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log.Debug($"mpv output pump stopped: {ex.Message}");
        }
    }

    private async Task IpcLoopAsync()
    {
        while (_running && !_cts.IsCancellationRequested)
        {
            if (_process is null || _process.HasExited)
            {
                try
                {
                    await Task.Delay(500, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await ConnectAsync(_cts.Token);
                _ipcConnected = true;
                Log.Info("mpv ipc connected");
                await ObservePropertiesAsync();
                await ReadLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug($"mpv ipc reconnect: {ex.Message}");
            }
            finally
            {
                _ipcConnected = false;
                CloseStream();
            }

            try
            {
                await Task.Delay(500, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, token);
            _stream = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_ipcPath), token);
            _stream = new NetworkStream(socket, ownsSocket: true);
        }

        _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (_running && _reader is not null)
        {
            var line = await _reader.ReadLineAsync(token);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            HandleMessage(line);
        }
    }

    private void HandleMessage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var propertyId))
            {
                HandleProperty(propertyId, root);
                return;
            }

            if (root.TryGetProperty("event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String)
            {
                HandleEvent(eventElement.GetString() ?? string.Empty, root);
                return;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                var error = errorElement.GetString();
                var requestId = root.TryGetProperty("request_id", out var requestElement) &&
                                requestElement.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;
                _pendingRequests.TryRemove(requestId, out var description);
                if (!string.Equals(error, "success", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"mpv command error: {error} for [{description ?? "unknown"}] ({line})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"invalid mpv message: {ex.Message}: {line}");
        }
    }

    private void HandleProperty(int id, JsonElement root)
    {
        var data = root.TryGetProperty("data", out var value) ? value : default;
        switch (id)
        {
            case 1 when data.ValueKind == JsonValueKind.Number:
                _protocol.SetState("Volume", (int)Math.Round(data.GetDouble()));
                break;
            case 2:
                var position = FormatTime(GetDouble(data));
                _protocol.SetState("RelativeTimePosition", position);
                _protocol.SetState("AbsoluteTimePosition", position);
                break;
            case 3 when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                if (!_playing)
                {
                    return;
                }

                _paused = data.GetBoolean();
                _protocol.SetState("TransportState", _paused ? "PAUSED_PLAYBACK" : "PLAYING");
                break;
            case 4 when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _protocol.SetState("Mute", data.GetBoolean());
                break;
            case 5:
                var duration = FormatTime(GetDouble(data));
                _protocol.SetState("CurrentTrackDuration", duration);
                _protocol.SetState("CurrentMediaDuration", duration);
                break;
            case 6 when data.ValueKind == JsonValueKind.Array:
                var tracks = data.GetArrayLength();
                _protocol.SetState("CurrentTrack", tracks == 0 ? 0 : 1);
                _protocol.SetState("NumberOfTracks", tracks);
                break;
            case 7 when data.ValueKind == JsonValueKind.Number:
                _protocol.SetState("TransportPlaySpeed", data.GetDouble().ToString(CultureInfo.InvariantCulture));
                break;
            case 8 when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _protocol.SetState("DisplayCurrentSubtitle", data.GetBoolean());
                break;
        }
    }

    private void HandleEvent(string name, JsonElement root)
    {
        switch (name)
        {
            case "start-file":
                _playing = true;
                _protocol.SetState("TransportStatus", "OK");
                break;
            case "playback-restart":
                _protocol.SetState("TransportState", _paused ? "PAUSED_PLAYBACK" : "PLAYING");
                break;
            case "end-file":
                _playing = false;
                var reason = root.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : null;
                if (string.Equals(reason, "error", StringComparison.OrdinalIgnoreCase))
                {
                    _protocol.SetState("TransportStatus", "ERROR_OCCURRED");
                    _protocol.SetState("TransportState", "STOPPED");
                }
                else if (string.Equals(reason, "eof", StringComparison.OrdinalIgnoreCase))
                {
                    _protocol.SetState("TransportState", "NO_MEDIA_PRESENT");
                }
                else
                {
                    _protocol.SetState("TransportState", "STOPPED");
                }

                break;
            case "idle":
                _playing = false;
                _protocol.SetState("TransportState", "STOPPED");
                break;
        }
    }

    private static double GetDouble(JsonElement data) =>
        data.ValueKind == JsonValueKind.Number ? data.GetDouble() : 0;

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "00:00:00";
        }

        var total = (int)seconds;
        return $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2}";
    }

    private async Task ObservePropertiesAsync()
    {
        await SendCommandAsync("observe_property", 1, "volume");
        await SendCommandAsync("observe_property", 2, "time-pos");
        await SendCommandAsync("observe_property", 3, "pause");
        await SendCommandAsync("observe_property", 4, "mute");
        await SendCommandAsync("observe_property", 5, "duration");
        await SendCommandAsync("observe_property", 6, "track-list");
        await SendCommandAsync("observe_property", 7, "speed");
        await SendCommandAsync("observe_property", 8, "sub-visibility");
        await SetMediaVolumeAsync(_settings.GetInt("PlayerDefaultVolume", 100));

        while (_pendingCommands.TryDequeue(out var pending))
        {
            await SendCommandAsync(pending);
        }
    }

    private async Task<bool> SendCommandAsync(params object[] command)
    {
        var writer = _writer;
        if (writer is null)
        {
            if (_running && !_simulated)
            {
                _pendingCommands.Enqueue(command);
            }

            return false;
        }

        var id = Interlocked.Increment(ref _requestId);
        var description = string.Join(" ", command.Select(item => item?.ToString() ?? string.Empty));
        _pendingRequests[id] = description;
        var json = JsonSerializer.Serialize(new { command, request_id = id });
        Log.Debug("mpv <- " + json);
        await _writeLock.WaitAsync();
        try
        {
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"send mpv command failed: {ex.Message}");
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void CloseStream()
    {
        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }

        _reader = null;
        _writer = null;
        _stream = null;
    }

    public void SetMediaStop()
    {
        if (_simulated)
        {
            _protocol.SetState("TransportState", "STOPPED");
            return;
        }

        _ = SendCommandAsync("stop");
    }

    public void SetMediaPause()
    {
        if (_simulated)
        {
            _protocol.SetState("TransportState", "PAUSED_PLAYBACK");
            return;
        }

        _ = SendCommandAsync("set_property", "pause", true);
        _ = SendCommandAsync("show-text", "Pause", 1000);
    }

    public void SetMediaResume()
    {
        if (_simulated)
        {
            _protocol.SetState("TransportState", "PLAYING");
            return;
        }

        _ = SendCommandAsync("set_property", "pause", false);
        _ = SendCommandAsync("show-text", "Resume", 1000);
    }

    public void SetMediaVolume(int volume)
    {
        if (_simulated)
        {
            _protocol.SetState("Volume", Math.Clamp(volume, 0, 100));
            return;
        }

        _ = SetMediaVolumeAsync(volume);
        _ = SendCommandAsync("show-text", $"Volume: {volume}", 1000);
    }

    private async Task SetMediaVolumeAsync(int volume) =>
        await SendCommandAsync("set_property", "volume", Math.Clamp(volume, 0, 100));

    public void SetMediaMute(bool mute)
    {
        if (_simulated)
        {
            _protocol.SetState("Mute", mute);
            return;
        }

        _ = SendCommandAsync("set_property", "mute", mute ? "yes" : "no");
        _ = SendCommandAsync("show-text", $"Mute: {mute}", 1000);
    }

    public void SetMediaUrl(string url, string start = "0") => SetMediaUrlCore(url, start, audioUrl: null);

    /// <summary>
    /// Bilibili DASH gives video and audio as two URLs. "audio-add" after loadfile is
    /// unreliable (mpv rejects it while the file is still opening), so the track is
    /// passed atomically in the loadfile options string.
    /// </summary>
    public void SetMediaUrlWithAudio(string url, string start, string audioUrl) =>
        SetMediaUrlCore(url, start, audioUrl);

    private void SetMediaUrlCore(string url, string start, string? audioUrl)
    {
        if (_simulated)
        {
            _protocol.SetState("TransportState", "PLAYING");
            return;
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            _ = SendCommandAsync("loadfile", url, "replace");
        }
        else
        {
            var options = $"audio-file=\"{audioUrl}\"";
            _ = SendCommandAsync("loadfile", url, "replace", -1, options);
        }

        // mpv's loadfile option parser rejects start=0 on recent builds.
        // Load first, then seek if the caller asked for a non-zero position.
        if (!string.IsNullOrWhiteSpace(start) && start != "0" && start != "00:00:00")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await SendCommandAsync("seek", start, "absolute");
            });
        }
    }

    public void SetMediaTitle(string title)
    {
        _title = title;
        if (!_simulated)
        {
            _ = SendCommandAsync("set_property", "title", title);
        }
    }

    public void SetMediaPosition(string position)
    {
        if (_simulated)
        {
            _protocol.SetState("RelativeTimePosition", position);
            _protocol.SetState("AbsoluteTimePosition", position);
            return;
        }

        _ = SendCommandAsync("seek", position, "absolute");
    }

    public void SetMediaSpeed(double speed)
    {
        if (_simulated)
        {
            _protocol.SetState("TransportPlaySpeed", speed.ToString(CultureInfo.InvariantCulture));
            return;
        }

        _ = SendCommandAsync("set_property", "speed", speed);
    }

    public void SetMediaSubFile(string url, string title)
    {
        if (_simulated)
        {
            return;
        }

        _ = SendCommandAsync("sub-add", url, "select", title);
    }

    public void SetMediaSubShow(bool visible)
    {
        if (_simulated)
        {
            _protocol.SetState("DisplayCurrentSubtitle", visible);
            return;
        }

        _ = SendCommandAsync("set_property", "sub-visibility", visible ? "yes" : "no");
    }

    public void SetMediaText(string text, int durationMs = 1000)
    {
        if (_simulated)
        {
            return;
        }

        _ = SendCommandAsync("show-text", text, durationMs);
    }
}
