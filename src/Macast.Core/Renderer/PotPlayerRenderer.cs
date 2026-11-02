using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Renderer;

/// <summary>
/// Port of the PotPlayer plugin. PotPlayer has no IPC, so the C# port keeps the
/// Python plugin's model: the process is launched with the URL, pause / mute /
/// seek are driven by the player's own hotkeys, and the position is an estimate
/// that only advances while playback is considered active.
/// </summary>
public sealed class PotPlayerRenderer : IRenderer, IAudioTrackRenderer
{
    private const string Path64 = @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe";
    private const string Path32 = @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini.exe";

    private static readonly string[] ExeNames =
    {
        "PotPlayerMini64.exe",
        "PotPlayerMini.exe",
        "PotPlayer64.exe",
        "PotPlayer.exe"
    };

    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(8);

    private readonly AppSettings _settings;
    private readonly IProtocolStateSink _protocol;
    private readonly string _subtitlePath;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private Process? _player;
    private IntPtr _window;
    private double _position;
    private volatile bool _playing;
    private int _volume;
    private int _volumeApplied = 100;
    private int _volumeRemainder;
    private bool _volumeSynced;
    private bool _muted;

    public PotPlayerRenderer(RendererContext context)
    {
        _settings = context.Settings;
        _protocol = context.Protocol;
        _subtitlePath = Path.Combine(context.ConfigDir, "macast.ass");
        _volume = _settings.GetInt("PlayerDefaultVolume", 100);
        _volumeApplied = _volume;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Name => "PotPlayer";

    public bool Available => ResolveExecutable(_settings) is not null;

    /// <summary>PID of the player process this renderer started (diagnostics / --potplayer probe).</summary>
    public int? PlayerProcessId
    {
        get
        {
            var player = _player;
            try
            {
                return player is null || player.HasExited ? null : player.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Window handle found for the running player, or <see cref="IntPtr.Zero"/>.</summary>
    public IntPtr PlayerWindow => _window != IntPtr.Zero ? _window : FindPlayerWindow(TimeSpan.Zero);

    /// <summary>
    /// Reads Potplayer_Path, then the Windows registry (DAUM keys and the
    /// uninstall entry, which is the only trace a portable install leaves),
    /// then the default install dirs, then PATH.
    /// </summary>
    public static string? ResolveExecutable(AppSettings settings)
    {
        // Diagnostics switch: MACAST_POTPLAYER_PATH=none pretends the player is not
        // installed (used by --potplayer to verify the degraded path); a path forces it.
        var forced = Environment.GetEnvironmentVariable("MACAST_POTPLAYER_PATH");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            forced = forced.Trim();
            if (forced.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                forced.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(forced) ? forced : null;
        }

        var configured = settings.GetString("Potplayer_Path", string.Empty);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in RegistryProgramPaths())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            foreach (var candidate in UninstallCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var dir in InstallationDirs())
        {
            foreach (var name in ExeNames)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // malformed install dir
                }
            }
        }

        foreach (var name in ExeNames)
        {
            var onPath = ExecutableLocator.OnPath(name);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryProgramPaths()
    {
        var keys = new[]
        {
            @"HKEY_CURRENT_USER\Software\DAUM\PotPlayer64",
            @"HKEY_CURRENT_USER\Software\DAUM\PotPlayer",
            @"HKEY_LOCAL_MACHINE\Software\DAUM\PotPlayer64",
            @"HKEY_LOCAL_MACHINE\Software\DAUM\PotPlayer",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\DAUM\PotPlayer64",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\DAUM\PotPlayer"
        };

        foreach (var key in keys)
        {
            string? value = null;
            try
            {
                value = Registry.GetValue(key, "ProgramPath", null) as string;
            }
            catch
            {
                // key missing or unreadable
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// A portable PotPlayer writes no DAUM key; the uninstall entry still carries
    /// DisplayIcon (the exe) and sometimes InstallLocation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> UninstallCandidates()
    {
        var roots = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, path) in roots)
        {
            string[] names;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var root = baseKey.OpenSubKey(path);
                if (root is null)
                {
                    continue;
                }

                names = root.GetSubKeyNames();
            }
            catch
            {
                continue;
            }

            foreach (var name in names)
            {
                string? displayName = null;
                string? displayIcon = null;
                string? installLocation = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(path + @"\" + name);
                    displayName = key?.GetValue("DisplayName") as string;
                    displayIcon = key?.GetValue("DisplayIcon") as string;
                    installLocation = key?.GetValue("InstallLocation") as string;
                }
                catch
                {
                    continue;
                }

                if (displayName is null || !displayName.Contains("PotPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    foreach (var exe in ExeNames)
                    {
                        yield return Path.Combine(installLocation.Trim('"'), exe);
                    }
                }

                var icon = CleanDisplayIcon(displayIcon);
                if (icon is not null)
                {
                    yield return icon;
                }
            }
        }
    }

    /// <summary>Splits the ",0" icon index off a DisplayIcon value.</summary>
    private static string? CleanDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        var comma = value.LastIndexOf(',');
        if (comma > 2)
        {
            value = value[..comma];
        }

        return value.Trim().Trim('"');
    }

    private static IEnumerable<string> InstallationDirs()
    {
        var dirs = new List<string>();
        Add(Environment.GetEnvironmentVariable("ProgramFiles"), Path.Combine("DAUM", "PotPlayer"));
        Add(Environment.GetEnvironmentVariable("ProgramW6432"), Path.Combine("DAUM", "PotPlayer"));
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Path.Combine("DAUM", "PotPlayer"));
        Add(Environment.GetEnvironmentVariable("ProgramFiles"), "PotPlayer");
        Add(Environment.GetEnvironmentVariable("ProgramW6432"), "PotPlayer");
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "PotPlayer");
        dirs.Add(@"D:\PotPlayer");
        dirs.Add(@"C:\PotPlayer");
        return dirs;

        void Add(string? root, string suffix)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    dirs.Add(Path.Combine(root, suffix));
                }
                catch
                {
                    // malformed root
                }
            }
        }
    }

    public void Start()
    {
        _protocol.SetState("Volume", _volume);
        _protocol.SetState("Mute", false);
        _protocol.SetState("DisplayCurrentSubtitle", false);
        _timer.Change(1000, 1000);
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        KillPlayer();
        _playing = false;
    }

    private void Tick()
    {
        var player = _player;
        if (player is not null)
        {
            bool exited;
            try
            {
                exited = player.HasExited;
            }
            catch
            {
                exited = true;
            }

            if (exited)
            {
                // the user closed the player window; report it instead of faking progress
                lock (_gate)
                {
                    _playing = false;
                }

                _window = IntPtr.Zero;
                _protocol.SetState("TransportState", "STOPPED");
                Log.Info("potplayer exited; transport stopped");
                return;
            }
        }

        lock (_gate)
        {
            if (!_playing)
            {
                return;
            }

            _position += 1;
        }

        var text = MediaTime.FromSeconds(_position);
        _protocol.SetState("RelativeTimePosition", text);
        _protocol.SetState("AbsoluteTimePosition", text);
    }

    public void SetMediaUrl(string url, string start = "0") => SetMediaUrlCore(url, start, null);

    public void SetMediaUrlWithAudio(string url, string start, string audioUrl) =>
        SetMediaUrlCore(url, start, audioUrl);

    private void SetMediaUrlCore(string url, string start, string? audioUrl)
    {
        KillPlayer();
        var startSeconds = MediaTime.ToSeconds(start);
        lock (_gate)
        {
            _position = startSeconds;
        }

        var exe = ResolveExecutable(_settings);
        if (exe is null)
        {
            Log.Error($"potplayer not found; set Potplayer_Path (looked for {string.Join(", ", ExeNames)}, " +
                      $"DAUM registry keys and the uninstall entry)");
            _protocol.SetState("TransportStatus", "ERROR_OCCURRED");
            _protocol.SetState("TransportState", "STOPPED");
            return;
        }

        try
        {
            var info = BuildStartInfo(exe, url, startSeconds, audioUrl);
            _player = Process.Start(info);
            _playing = true;
            _protocol.SetState("TransportStatus", "OK");
            _protocol.SetState("TransportState", "PLAYING");
            Log.Info($"potplayer started, pid={_player?.Id}, exe={exe}, externalAudio={!string.IsNullOrWhiteSpace(audioUrl)}, seek={MediaTime.FromSeconds(startSeconds)}");

            _window = FindPlayerWindow(WindowWait);
            if (_window == IntPtr.Zero)
            {
                Log.Warn("potplayer window not found after launch; hotkeys will retry on demand");
            }
            else
            {
                Log.Info($"potplayer window hwnd=0x{_window.ToInt64():X}");
            }

            // PotPlayer restores its own volume between sessions and exposes no way to
            // read it, so the hotkey base is unknown until the first calibration.
            _volumeSynced = false;
        }
        catch (Exception ex)
        {
            Log.Error($"start potplayer failed: {ex.Message}");
            _protocol.SetState("TransportStatus", "ERROR_OCCURRED");
            _protocol.SetState("TransportState", "STOPPED");
        }
    }

    private ProcessStartInfo BuildStartInfo(string exe, string url, double startSeconds, string? audioUrl)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(url);
        if (!string.IsNullOrWhiteSpace(audioUrl))
        {
            info.ArgumentList.Add(audioUrl);
            // /same loads the URLs as streams of one item, not two playlist entries.
            info.ArgumentList.Add("/same");
            info.ArgumentList.Add("/referer=https://www.bilibili.com/");
        }
        info.ArgumentList.Add("/autoplay");
        if (startSeconds > 0)
            info.ArgumentList.Add($"/seek={MediaTime.FromSeconds(startSeconds)}");
        var title = _settings.GetString("DLNA_FriendlyName", "Macast");
        if (!string.IsNullOrWhiteSpace(title)) info.ArgumentList.Add($"/title={title}");
        if (File.Exists(_subtitlePath)) info.ArgumentList.Add($"/sub={_subtitlePath}");
        return info;
    }

    private void KillPlayer()
    {
        var player = _player;
        _player = null;
        _window = IntPtr.Zero;
        if (player is null)
        {
            return;
        }

        try
        {
            if (!player.HasExited)
            {
                player.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"kill potplayer failed: {ex.Message}");
        }

        try
        {
            player.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    public void SetMediaStop()
    {
        KillPlayer();
        lock (_gate)
        {
            _playing = false;
            _position = 0;
        }

        _protocol.SetState("TransportState", "STOPPED");
    }

    public void SetMediaPause()
    {
        lock (_gate)
        {
            if (!_playing)
            {
                // already paused: sending space again would resume playback
                _protocol.SetState("TransportState", "PAUSED_PLAYBACK");
                return;
            }

            _playing = false;
        }

        if (!SendKey(' '))
        {
            lock (_gate)
            {
                _playing = true;
            }

            _protocol.SetState("TransportState", "PLAYING");
            return;
        }

        _protocol.SetState("TransportState", "PAUSED_PLAYBACK");
    }

    public void SetMediaResume()
    {
        lock (_gate)
        {
            if (_playing)
            {
                _protocol.SetState("TransportState", "PLAYING");
                return;
            }

            if (_player is null)
            {
                // nothing loaded; pretend nothing happened
                _protocol.SetState("TransportState", "STOPPED");
                return;
            }

            _playing = true;
        }

        if (!SendKey(' '))
        {
            lock (_gate)
            {
                _playing = false;
            }

            _protocol.SetState("TransportState", "PAUSED_PLAYBACK");
            return;
        }

        _protocol.SetState("TransportState", "PLAYING");
    }

    public void SetMediaVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        var previous = _volume;

        if (!_volumeSynced && !CalibrateVolume())
        {
            _volume = volume;
            _protocol.SetState("Volume", volume);
            Log.Warn($"volume {previous} -> {volume}: calibration failed, value reported but not applied");
            return;
        }

        _volume = volume;
        _protocol.SetState("Volume", volume);

        // PotPlayer changes volume in 5% steps with the arrow keys, so only whole
        // steps can be applied; the remainder is carried into the next request.
        var delta = volume - _volumeApplied + _volumeRemainder;
        var steps = delta / 5;
        if (steps == 0)
        {
            _volumeRemainder = delta;
            Log.Debug($"volume {previous} -> {volume}: below the 5% PotPlayer step, deferred");
            return;
        }

        _volumeRemainder = delta - steps * 5;
        _volumeApplied = Math.Clamp(_volumeApplied + steps * 5, 0, 100);

        var arrow = steps > 0 ? (char)0x26 : (char)0x28;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            if (!SendKey(arrow))
            {
                _volumeApplied = previous;
                _volumeRemainder = 0;
                Log.Warn($"volume {previous} -> {volume}: PotPlayer did not accept the arrow keys");
                return;
            }

            // PotPlayer drops one key of a burst sent back-to-back, so pace them.
            Thread.Sleep(40);
        }

        Log.Debug($"volume {previous} -> {volume}: sent {steps} arrow step(s), applied {_volumeApplied}");
    }

    /// <summary>
    /// PotPlayer keeps its previous session's volume and only accepts relative hotkey
    /// changes, so the hotkey base is unknown. Press the volume down button until the
    /// player bottoms out, which makes every later step predictable.
    /// </summary>
    private bool CalibrateVolume()
    {
        for (var i = 0; i < 24; i++)
        {
            if (!SendKey((char)0x28))
            {
                return false;
            }

            Thread.Sleep(20);
        }

        _volumeApplied = 0;
        _volumeRemainder = 0;
        _volumeSynced = true;
        Log.Info("potplayer volume calibrated to 0; applying the requested level");
        return true;
    }

    public void SetMediaMute(bool mute)
    {
        if (mute == _muted)
        {
            // 'm' toggles; sending it again would flip the player back
            _protocol.SetState("Mute", mute);
            return;
        }

        if (!SendKey('m'))
        {
            Log.Warn($"mute {mute}: PotPlayer did not accept the 'm' key");
            _protocol.SetState("Mute", _muted);
            return;
        }

        _muted = mute;
        _protocol.SetState("Mute", mute);
    }

    public void SetMediaTitle(string title)
    {
        // The window title is fixed at launch (/title); DLNA metadata is not injectable mid-play.
        _protocol.SetState("CurrentTrackMetaData", title);
    }

    public void SetMediaPosition(string position)
    {
        var seconds = MediaTime.ToSeconds(position);
        lock (_gate)
        {
            _position = seconds;
        }

        var text = MediaTime.FromSeconds(seconds);
        SeekViaHotkey(text);
        _protocol.SetState("RelativeTimePosition", text);
        _protocol.SetState("AbsoluteTimePosition", text);
    }

    private void SeekViaHotkey(string position)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = FindPlayerWindow(TimeSpan.FromSeconds(3));
        if (hwnd == IntPtr.Zero)
        {
            Log.Warn("potplayer window not found; seek skipped");
            return;
        }

        if (!FocusWindow(hwnd))
        {
            Log.Warn("seek: could not bring the player to the foreground; the time dialog may not receive the text");
        }

        SendKey('g');
        Thread.Sleep(250);
        SendText(position);
        Thread.Sleep(150);
        SendVirtualKey(0x0D); // Enter applies the position
        Thread.Sleep(250);
        // PotPlayer keeps the "直接输入移动位置" dialog open after Enter; leaving it
        // open would swallow every later hotkey (space / m / arrow).
        SendVirtualKey(0x1B); // Esc closes it
        Thread.Sleep(150);
        Log.Info($"potplayer seek -> {position}");
    }

    public void SetMediaSpeed(double speed)
    {
        _protocol.SetState("TransportPlaySpeed", speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetMediaSubFile(string url, string title)
    {
        // PotPlayer only accepts a subtitle file path on the command line.
        Log.Debug("potplayer subtitle switch requires a local file; ignored");
    }

    public void SetMediaSubShow(bool visible)
    {
        // Alt+H toggles subtitles in PotPlayer (the Python advanced plugin did the same).
        SendKeySequence(0x12, 'h');
        _protocol.SetState("DisplayCurrentSubtitle", visible);
    }

    public void SetMediaText(string text, int durationMs = 1000)
    {
        // no OSD API
    }

    private IntPtr FindPlayerWindow(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var hwnd = FindPlayerWindowOnce();
            if (hwnd != IntPtr.Zero)
            {
                _window = hwnd;
                return hwnd;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return IntPtr.Zero;
            }

            Thread.Sleep(200);
        }
    }

    private IntPtr FindPlayerWindowOnce()
    {
        var player = _player;
        if (player is null || !OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        int pid;
        try
        {
            if (player.HasExited)
            {
                return IntPtr.Zero;
            }

            pid = player.Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == pid && IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0)
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private bool SendKey(char ch) => SendKeySequence(0, ch);

    private bool SendKeySequence(int modifier, char ch)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = FindPlayerWindow(TimeSpan.FromSeconds(3));
        if (hwnd == IntPtr.Zero)
        {
            Log.Warn("potplayer window not found; hotkey dropped");
            return false;
        }

        var focused = FocusWindow(hwnd);
        if (focused)
        {
            // The key that activates a window is often swallowed by the focus
            // transition; give the player a moment before the real key arrives.
            Thread.Sleep(120);
        }

        var inputs = new List<Input>();
        if (modifier != 0)
        {
            inputs.Add(KeyDown((ushort)modifier));
        }

        inputs.Add(KeyDown(ch));
        inputs.Add(KeyUp(ch));

        if (modifier != 0)
        {
            inputs.Add(KeyUp((ushort)modifier));
        }

        if (!focused)
        {
            // SetForegroundWindow is refused when another app owns the focus: post
            // the key straight to the player instead of leaking it to that app.
            Log.Debug("potplayer focus refused; posting the key to hwnd");
            PostVirtualKey(hwnd, (ushort)char.ToUpperInvariant(ch), modifier != 0);
            return true;
        }

        return Send(inputs) > 0;
    }

    private bool SendVirtualKey(ushort vk)
    {
        var hwnd = FindPlayerWindow(TimeSpan.FromSeconds(1));
        if (hwnd != IntPtr.Zero && GetForegroundWindow() != hwnd)
        {
            PostVirtualKey(hwnd, vk, false);
            return true;
        }

        return Send(new List<Input> { KeyDown(vk), KeyUp(vk) }) > 0;
    }

    private bool SendText(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = FindPlayerWindow(TimeSpan.FromSeconds(1));
        if (hwnd != IntPtr.Zero && GetForegroundWindow() != hwnd)
        {
            // PostMessage WM_CHAR so the text lands in the time dialog even without focus.
            foreach (var ch in text)
            {
                PostMessage(hwnd, 0x0102, new IntPtr(ch), new IntPtr(1));
            }

            return true;
        }

        foreach (var ch in text)
        {
            Send(new List<Input>
            {
                new()
                {
                    Type = 1,
                    Union = new InputUnion { Keyboard = new KeyboardInput { Scan = ch, Flags = 0x0004 } }
                },
                new()
                {
                    Type = 1,
                    Union = new InputUnion { Keyboard = new KeyboardInput { Scan = ch, Flags = 0x0004 | 0x0002 } }
                }
            });
        }

        return true;
    }

    private static void PostVirtualKey(IntPtr hwnd, ushort vk, bool withAlt)
    {
        try
        {
            PostMessage(hwnd, 0x0100, new IntPtr(vk), new IntPtr(1));
            PostMessage(hwnd, 0x0101, new IntPtr(vk), unchecked((IntPtr)0xC0000001));
            if (withAlt)
            {
                Log.Debug("modifier hotkey could not be posted; only the base key was sent");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"post key failed: {ex.Message}");
        }
    }

    private static Input KeyDown(char ch) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)char.ToUpperInvariant(ch) } }
    };

    private static Input KeyUp(char ch) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)char.ToUpperInvariant(ch), Flags = 0x0002 } }
    };

    private static Input KeyDown(ushort vk) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk } }
    };

    private static Input KeyUp(ushort vk) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = 0x0002 } }
    };

    private static int Send(List<Input> inputs)
    {
        try
        {
            if (inputs.Count == 0)
            {
                return 0;
            }

            var sent = (int)SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
            if (sent == 0)
            {
                Log.Warn($"SendInput rejected the key (size={Marshal.SizeOf<Input>()}, error={Marshal.GetLastWin32Error()})");
            }

            return sent;
        }
        catch (Exception ex)
        {
            Log.Debug($"send input failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Windows refuses SetForegroundWindow when another process owns the focus.
    /// Attaching to the foreground thread's input queue lifts that restriction.
    /// </summary>
    private static bool FocusWindow(IntPtr hwnd)
    {
        try
        {
            if (GetForegroundWindow() == hwnd)
            {
                return true;
            }

            ShowWindow(hwnd, 9); // SW_RESTORE
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            var attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);

            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }

            Thread.Sleep(50);
            return GetForegroundWindow() == hwnd;
        }
        catch (Exception ex)
        {
            Log.Debug($"focus window failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // INPUT is a union whose largest member is MOUSEINPUT (32 bytes on x64), so the
    // union must be sized explicitly: declaring only KeyboardInput makes the struct
    // 32 bytes instead of 40 and SendInput then fails with ERROR_INVALID_PARAMETER
    // (which looks exactly like "the hotkey did nothing").
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
