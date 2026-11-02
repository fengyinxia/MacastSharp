using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Renderer;

/// <summary>
/// Starts a dedicated PotPlayer instance and controls it through bounded WM_USER
/// messages. Window placement is scoped to that instance; decoder preferences
/// remain under PotPlayer's own control.
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
    private readonly Timer _timer;
    private readonly object _gate = new();
    private Process? _player;
    private IntPtr _window;
    private double _position;
    private volatile bool _playing;
    private int _volume;
    private bool _muted;
    private int _ticking;
    private int _layoutPasses;
    private DateTime _layoutDeadline;
    private bool _initialStateApplied;
    private double _speed = 1;

    public PotPlayerRenderer(RendererContext context)
    {
        _settings = context.Settings;
        _protocol = context.Protocol;
        _volume = _settings.GetInt("PlayerDefaultVolume", 100);
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
    public IntPtr PlayerWindow => FindPlayerWindow(TimeSpan.Zero);

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
        if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            lock (_gate) TickCore();
        }
        catch (Exception ex) { Log.Debug($"potplayer status query failed: {ex.Message}"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void TickCore()
    {
        var player = _player;
        if (player is null) return;
        if (player.HasExited)
        {
            _playing = false;
            _window = IntPtr.Zero;
            _protocol.SetState("TransportState", "STOPPED");
            return;
        }
        var hwnd = FindPlayerWindow(TimeSpan.Zero);
        if (hwnd == IntPtr.Zero || !PotPlayerWindow.Command(hwnd, 0x5006, 0, out var status) ||
            !PotPlayerWindow.Command(hwnd, 0x5002, 0, out var duration) || (duration <= 0 && status != 2)) return;
        if (!_initialStateApplied)
        {
            PotPlayerWindow.Command(hwnd, 0x5001, _volume, out _);
            PotPlayerWindow.Command(hwnd, 0x5012, _muted ? 1 : 0, out _);
            if (PotPlayerWindow.Command(hwnd, 0x5015, 0, out var currentSpeed) && currentSpeed >= 200)
                PotPlayerWindow.Command(hwnd, 0x5016, (long)(_speed * 1000), out _);
            _initialStateApplied = true;
        }
        if (_layoutPasses > 0 && DateTime.UtcNow < _layoutDeadline &&
            PotPlayerWindow.Command(hwnd, 0x6030, 0, out var width) && width > 0 &&
            PotPlayerWindow.Command(hwnd, 0x6031, 0, out var height) && height > 0)
        {
            if (PotPlayerWindow.Apply(hwnd, (int)width, (int)height,
                    _settings.GetInt("PlayerPosition", 2), _settings.GetInt("PlayerSize", 1),
                    _settings.GetInt("PlayerOntop", 1) != 0)) _layoutPasses--;
        }
        // Do not publish a response belonging to a process replaced while querying.
        if (!ReferenceEquals(player, _player)) return;
        _playing = status == 2;
        _protocol.SetState("TransportState", status switch { 1 => "PAUSED_PLAYBACK", 2 => "PLAYING", _ => "STOPPED" });
        _protocol.SetState("CurrentTrackDuration", MediaTime.FromSeconds(duration / 1000d));
        if (PotPlayerWindow.Command(hwnd, 0x5004, 0, out var position))
        {
            lock (_gate) _position = position / 1000d;
            var text = MediaTime.FromSeconds(position / 1000d);
            _protocol.SetState("RelativeTimePosition", text);
            _protocol.SetState("AbsoluteTimePosition", text);
        }
        if (PotPlayerWindow.Command(hwnd, 0x5000, 0, out var volume) && volume is >= 0 and <= 100)
        {
            _volume = (int)volume;
            _protocol.SetState("Volume", _volume);
        }
        if (PotPlayerWindow.Command(hwnd, 0x5011, 0, out var mute))
        {
            _muted = mute != 0;
            _protocol.SetState("Mute", _muted);
        }
    }

    public void SetMediaUrl(string url, string start = "0") => SetMediaUrlCore(url, start, null);

    public void SetMediaUrlWithAudio(string url, string start, string audioUrl) =>
        SetMediaUrlCore(url, start, audioUrl);

    private void SetMediaUrlCore(string url, string start, string? audioUrl)
    {
        lock (_gate) SetMediaUrlLocked(url, start, audioUrl);
    }

    private void SetMediaUrlLocked(string url, string start, string? audioUrl)
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
            _initialStateApplied = false;
            _layoutPasses = 2;
            _layoutDeadline = DateTime.UtcNow.AddSeconds(30);
            _player = Process.Start(info);
            _playing = true;
            _protocol.SetState("TransportStatus", "OK");
            _protocol.SetState("TransportState", "PLAYING");
            Log.Info($"potplayer started, pid={_player?.Id}, exe={exe}, externalAudio={!string.IsNullOrWhiteSpace(audioUrl)}, seek={MediaTime.FromSeconds(startSeconds)}");

            _window = FindPlayerWindow(WindowWait);
            if (_window == IntPtr.Zero)
            {
                Log.Warn("potplayer window not found after launch; commands will retry on demand");
            }
            else
            {
                Log.Info($"potplayer window hwnd=0x{_window.ToInt64():X}");
            }

            // Loading can recreate/resize the window; apply placement once ready and
            // confirm it on the following tick, never fight later manual window moves.
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
        info.ArgumentList.Add("/new"); // Never attach to a user's unrelated instance.
        info.ArgumentList.Add("/autoplay");
        info.ArgumentList.Add($"/volume={_volume}");
        if (startSeconds > 0)
            info.ArgumentList.Add($"/seek={MediaTime.FromSeconds(startSeconds)}");
        var title = _settings.GetString("DLNA_FriendlyName", "Macast");
        if (!string.IsNullOrWhiteSpace(title)) info.ArgumentList.Add($"/title={title}");
        return info;
    }

    private void KillPlayer()
    {
        lock (_gate) KillPlayerLocked();
    }

    private void KillPlayerLocked()
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

    private bool Command(int command, long value, out long result)
    {
        var hwnd = FindPlayerWindow(TimeSpan.Zero);
        var ok = PotPlayerWindow.Command(hwnd, command, value, out result);
        if (!ok && _player is not null) Log.Warn($"potplayer command 0x{command:X} failed (window missing, blocked or unresponsive)");
        return ok;
    }

    public void SetMediaPause()
    {
        if (!Command(0x5007, 1, out _)) return;
        _playing = false;
        _protocol.SetState("TransportState", "PAUSED_PLAYBACK");
    }

    public void SetMediaResume()
    {
        if (!Command(0x5007, 2, out _)) return;
        _playing = true;
        _protocol.SetState("TransportState", "PLAYING");
    }

    public void SetMediaVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (_player is not null && !Command(0x5001, volume, out _)) return;
        _volume = volume;
        _protocol.SetState("Volume", volume);
    }

    public void SetMediaMute(bool mute)
    {
        if (_player is not null && !Command(0x5012, mute ? 1 : 0, out _)) return;
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
        var seconds = Math.Max(0, MediaTime.ToSeconds(position));
        if (!Command(0x5005, (long)(seconds * 1000), out _)) return;
        lock (_gate) _position = seconds;
        var text = MediaTime.FromSeconds(seconds);
        _protocol.SetState("RelativeTimePosition", text);
        _protocol.SetState("AbsoluteTimePosition", text);
    }

    public void SetMediaSpeed(double speed)
    {
        speed = Math.Clamp(speed, .2, 12);
        if (_player is not null)
        {
            if (!Command(0x5015, 0, out var current) || current < 200)
            {
                Log.Warn("this PotPlayer version does not expose playback speed control");
                return;
            }
            if (!Command(0x5016, (long)(speed * 1000), out _)) return;
        }
        _speed = speed;
        _protocol.SetState("TransportPlaySpeed", speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetMediaSubFile(string url, string title)
    {
        // PotPlayer only accepts a subtitle file path on the command line.
        Log.Debug("potplayer subtitle switch requires a local file; ignored");
    }

    public void SetMediaSubShow(bool visible)
    {
        if (_protocol.GetState("DisplayCurrentSubtitle") == (visible ? "true" : "false")) return;
        // Route Alt+H to the dedicated HWND, never through global keyboard input.
        if (Command(0x5010, 0x0400 | 'H', out _))
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
            if (windowPid == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == IntPtr.Zero && GetWindowTextLength(hwnd) > 0)
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

}
