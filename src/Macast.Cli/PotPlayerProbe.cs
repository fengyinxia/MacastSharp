using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Macast.Core.Plugins;
using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Cli;

/// <summary>
/// Real-machine PotPlayer check: launches the player through the renderer, drives
/// pause / resume / volume / mute / seek, screenshots the player window at every
/// step and proves "playing" vs "paused" by comparing those screenshots.
///
///   macast-cli --potplayer [--media FILE] [--path EXE] [--shots DIR] [--keep-open]
/// </summary>
internal static class PotPlayerProbe
{
    private static readonly List<(string Name, bool Ok, string Detail)> Checks = new();
    private static readonly StringBuilder Report = new();

    public static int Run(string[] args)
    {
        SetProcessDpiAware();

        var media = Arg(args, "--media") ?? Path.Combine(Path.GetTempPath(), "macast-pot-test", "pot-test-10m.mp4");
        var exe = Arg(args, "--path");
        var shots = Arg(args, "--shots") ?? Path.Combine(Path.GetTempPath(), "macast-pot-shots");
        var keepOpen = args.Contains("--keep-open");
        Directory.CreateDirectory(shots);

        var logFile = Path.Combine(AppSettings.ConfigDir, "macast-cs.log");
        Log.LogFile = logFile;

        Say("== PotPlayer real-machine probe ==");
        Say($"config dir : {AppSettings.ConfigDir}");
        Say($"log        : {logFile}");
        Say($"media      : {media} (exists={File.Exists(media)})");
        Say($"shots      : {shots}");

        if (!File.Exists(media))
        {
            Say("FAIL: media file not found; pass --media <file>");
            return 2;
        }

        var startAt = Arg(args, "--start") ?? "0";

        DegradedCheck();
        var ok = PlayerCheck(media, exe, shots, keepOpen, startAt, args.Contains("--controls-only"));

        Say("");
        Say("== summary ==");
        foreach (var (name, passed, detail) in Checks)
        {
            Say($"{(passed ? "PASS" : "FAIL")}  {name,-28} {detail}");
        }

        var reportPath = Path.Combine(shots, "report.txt");
        try
        {
            File.WriteAllText(reportPath, Report.ToString());
            Say($"report ; {reportPath}");
        }
        catch (Exception ex)
        {
            Say($"report write failed: {ex.Message}");
        }

        return ok && Checks.All(c => c.Ok) ? 0 : 1;
    }

    private static void DegradedCheck()
    {
        Say("");
        Say("-- degraded path (player not installed) --");
        var settings = AppSettings.Load();
        Environment.SetEnvironmentVariable("MACAST_POTPLAYER_PATH", "none");
        try
        {
            var sink = new TraceSink();
            using var renderer = new PotPlayerRenderer(new RendererContext(settings, sink));
            renderer.Start();
            var available = renderer.Available;
            renderer.SetMediaUrl("http://127.0.0.1:1/degraded.mp4", "0");
            var status = sink.GetState("TransportStatus");
            var state = sink.GetState("TransportState");
            var pid = renderer.PlayerProcessId;
            var ok = !available && status == "ERROR_OCCURRED" && state == "STOPPED" && pid is null;
            Check("degraded:not-available", !available, $"Available={available}");
            Check("degraded:error-state", ok, $"TransportStatus={status} TransportState={state} pid={pid?.ToString() ?? "none"}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MACAST_POTPLAYER_PATH", null);
        }
    }

    private static bool PlayerCheck(string media, string? exeHint, string shots, bool keepOpen, string startAt, bool controlsOnly)
    {
        var settings = AppSettings.Load();
        var resolved = PotPlayerRenderer.ResolveExecutable(settings);
        if (!string.IsNullOrWhiteSpace(exeHint))
        {
            settings.Set("Potplayer_Path", exeHint);
            resolved = PotPlayerRenderer.ResolveExecutable(settings);
        }

        Say("");
        Say("-- resolve --");
        Say($"configured : {settings.GetString("Potplayer_Path", "(empty)")}");
        Say($"resolved   : {resolved ?? "(not found)"}");
        Check("resolve:executable", resolved is not null, resolved ?? "not found");
        if (resolved is null)
        {
            return false;
        }

        var sink = new TraceSink();
        var renderer = new PotPlayerRenderer(new RendererContext(settings, sink));
        try
        {
            renderer.Start();
            Say("");
            Say("-- launch --");
            renderer.SetMediaUrl(media, startAt);
            var hwnd = IntPtr.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                hwnd = renderer.PlayerWindow;
                if (hwnd != IntPtr.Zero && renderer.PlayerProcessId is not null)
                {
                    break;
                }

                Thread.Sleep(300);
            }

            var pid = renderer.PlayerProcessId;
            Say($"pid        : {pid?.ToString() ?? "(none)"}");
            Say($"hwnd       : {(hwnd == IntPtr.Zero ? "(none)" : "0x" + hwnd.ToInt64().ToString("X"))}");
            Say($"transport  : {sink.GetState("TransportState")} / {sink.GetState("TransportStatus")}");
            Check("launch:window", hwnd != IntPtr.Zero && pid is not null, $"pid={pid?.ToString() ?? "-"} hwnd={(hwnd == IntPtr.Zero ? "-" : "0x" + hwnd.ToInt64().ToString("X"))}");
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            Focus(hwnd);
            Thread.Sleep(4000);

            if (controlsOnly)
            {
                VerifyControls(renderer, sink, hwnd);
                return Checks.All(c => c.Ok);
            }

            if (startAt != "0")
            {
                Say("");
                Say($"-- launch with /seek={startAt} --");
                Shot(hwnd, shots, "00-launch-seek");
                var started = MediaTime.ToSeconds(sink.GetState("RelativeTimePosition"));
                var asked = MediaTime.ToSeconds(startAt);
                Check("launch:seek-arg", started >= asked - 2 && started <= asked + 15,
                    $"asked {startAt}, estimate now {sink.GetState("RelativeTimePosition")}");
            }

            Say("");
            Say("-- playing (2 shots 2s apart, must differ) --");
            var p1 = Shot(hwnd, shots, "01-playing-a");
            Thread.Sleep(2000);
            var p2 = Shot(hwnd, shots, "01-playing-b");
            Check("play:moving", p1.Sha != p2.Sha, $"{p1.Sha} vs {p2.Sha}");

            Say("");
            Say("-- pause --");
            renderer.SetMediaPause();
            Thread.Sleep(2500);
            var q1 = Shot(hwnd, shots, "02-paused-a");
            Thread.Sleep(2000);
            var q2 = Shot(hwnd, shots, "02-paused-b");
            Say($"transport  : {sink.GetState("TransportState")}");
            Check("pause:frozen", q1.Sha == q2.Sha, $"{q1.Sha} vs {q2.Sha}");
            var posWhilePaused = sink.GetState("RelativeTimePosition");
            Thread.Sleep(1100);
            var posStillPaused = sink.GetState("RelativeTimePosition");
            Check("pause:position-stopped", posWhilePaused == posStillPaused, $"{posWhilePaused} -> {posStillPaused}");

            Say("");
            Say("-- pause again (idempotent: must NOT resume) --");
            renderer.SetMediaPause();
            Thread.Sleep(1500);
            var q3 = Shot(hwnd, shots, "03-paused-twice");
            Check("pause:idempotent", q2.Sha == q3.Sha, $"{q2.Sha} vs {q3.Sha}");

            Say("");
            Say("-- resume (2 shots 2s apart, must differ) --");
            renderer.SetMediaResume();
            Thread.Sleep(2000);
            var r1 = Shot(hwnd, shots, "04-resumed-a");
            Thread.Sleep(2000);
            var r2 = Shot(hwnd, shots, "04-resumed-b");
            Say($"transport  : {sink.GetState("TransportState")}");
            Check("resume:moving", r1.Sha != r2.Sha, $"{r1.Sha} vs {r2.Sha}");
            var posAfterResume = sink.GetState("RelativeTimePosition");
            Thread.Sleep(2600);
            var posAfterResume2 = sink.GetState("RelativeTimePosition");
            Check("resume:position-running", posAfterResume != posAfterResume2, $"{posAfterResume} -> {posAfterResume2}");

            Say("");
            Say("-- volume 100 -> 60 (OSD shot) --");
            renderer.SetMediaVolume(60);
            Thread.Sleep(500);
            Shot(hwnd, shots, "05-volume-60");
            Say($"state      : Volume={sink.GetState("Volume")}");
            renderer.SetMediaVolume(98);
            Say($"state      : Volume={sink.GetState("Volume")} (exact message control)");
            renderer.SetMediaVolume(60);
            Check("volume:state", sink.GetState("Volume") == "60", $"Volume={sink.GetState("Volume")}");

            Say("");
            Say("-- mute on (OSD shot) --");
            renderer.SetMediaMute(true);
            Thread.Sleep(600);
            Shot(hwnd, shots, "06-muted");
            Say($"state      : Mute={sink.GetState("Mute")}");
            Say("-- mute on again (idempotent: OSD must stay muted, not flip back) --");
            renderer.SetMediaMute(true);
            Thread.Sleep(600);
            Shot(hwnd, shots, "06b-muted-twice");
            Check("mute:state", sink.GetState("Mute") == "true", $"Mute={sink.GetState("Mute")}");
            renderer.SetMediaMute(false);
            Thread.Sleep(600);
            Shot(hwnd, shots, "07-unmuted");
            Check("mute:off-state", sink.GetState("Mute") == "false", $"Mute={sink.GetState("Mute")}");

            Say("");
            Say("-- seek to 00:05:00 (OSD / seek bar shot) --");
            renderer.SetMediaPosition("00:05:00");
            Thread.Sleep(2500);
            Shot(hwnd, shots, "08-seek-000500");
            var seeked = MediaTime.ToSeconds(sink.GetState("RelativeTimePosition"));
            Say($"state      : position={sink.GetState("RelativeTimePosition")}");
            Check("seek:state", seeked >= 300 && seeked <= 315, $"RelativeTimePosition={sink.GetState("RelativeTimePosition")} (asked 0:05:00)");

            Thread.Sleep(800);
            var titles = PlayerWindowTitles(pid ?? 0);
            Say("player windows: " + string.Join(" | ", titles));
            Check("seek:dialog-closed", !titles.Any(t => t.Contains("移动位置") || t.Contains("定位", StringComparison.Ordinal)),
                string.Join(" | ", titles));

            Say("");
            Say("-- stop --");
            renderer.SetMediaStop();
            Thread.Sleep(1500);
            var exited = renderer.PlayerProcessId is null;
            Check("stop:process-exit", exited, $"transport={sink.GetState("TransportState")} pid={(exited ? "gone" : "alive")}");

            Say("");
            Say("-- state trace --");
            foreach (var line in sink.Log)
            {
                Say("   " + line);
            }

            if (keepOpen && renderer.PlayerProcessId is not null)
            {
                Focus(hwnd);
            }

            return Checks.All(c => c.Ok);
        }
        finally
        {
            if (!keepOpen)
            {
                renderer.Dispose();
            }
        }
    }

    private static void VerifyControls(PotPlayerRenderer renderer, TraceSink sink, IntPtr hwnd)
    {
        long Read(int command)
        {
            if (SendMessageTimeout(hwnd, 0x400, new IntPtr(command), IntPtr.Zero, 0x22, 1000, out var value) == IntPtr.Zero)
                throw new InvalidOperationException($"message 0x{command:X} timed out");
            return value.ToInt64();
        }
        GetWindowRect(hwnd, out var launchRect);
        var launchLayout = (int)typeof(PotPlayerRenderer).GetField("_layoutPasses", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(renderer)!;
        Check("window:startup-layout", launchLayout == 0, $"remaining layout passes={launchLayout}, rect={launchRect.Left},{launchRect.Top},{launchRect.Right},{launchRect.Bottom}");
        renderer.SetMediaPause();
        renderer.SetMediaPause();
        Check("message:pause-idempotent", Read(0x5006) == 1, "status=" + Read(0x5006));
        foreach (var volume in new[] { 0, 37, 98, 60 })
        {
            renderer.SetMediaVolume(volume);
            Check("message:volume-" + volume, Read(0x5000) == volume, "actual=" + Read(0x5000));
        }
        renderer.SetMediaMute(true);
        renderer.SetMediaMute(true);
        Check("message:mute-idempotent", Read(0x5011) == 1, "mute=" + Read(0x5011));
        renderer.SetMediaMute(false);
        Check("message:unmute", Read(0x5011) == 0, "mute=" + Read(0x5011));
        renderer.SetMediaPosition("00:05:00");
        Thread.Sleep(500);
        Check("message:seek", Math.Abs(Read(0x5004) - 300000) < 2000, "position-ms=" + Read(0x5004));
        if (Read(0x5015) >= 200)
        {
            renderer.SetMediaSpeed(1.5);
            Check("message:speed", Read(0x5015) == 1500, "speed=" + Read(0x5015));
            renderer.SetMediaSpeed(1);
        }
        else Say("INFO  playback speed interface unavailable in this installed version");
        renderer.SetMediaResume();
        renderer.SetMediaResume();
        Check("message:resume-idempotent", Read(0x5006) == 2, "status=" + Read(0x5006));
        Thread.Sleep(1500);
        Check("message:progress-readback", Math.Abs(MediaTime.ToSeconds(sink.GetState("RelativeTimePosition")) - Read(0x5004) / 1000d) < 2,
            "reported=" + sink.GetState("RelativeTimePosition") + " actual-ms=" + Read(0x5004));

        var apply = typeof(PotPlayerRenderer).Assembly.GetType("Macast.Core.Renderer.PotPlayerWindow")!
            .GetMethod("Apply", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Place(int position, int size, bool topmost)
        {
            var ok = false;
            for (var i = 0; i < 4 && !ok; i++)
            {
                ok = (bool)apply.Invoke(null, new object[] { hwnd, (int)Read(0x6030), (int)Read(0x6031), position, size, topmost })!;
                Thread.Sleep(300);
            }
            return ok;
        }
        var monitor = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor);
        foreach (var position in new[] { 0, 1, 2, 3, 4 })
        {
            var ok = Place(position, 1, true);
            GetWindowRect(hwnd, out var r);
            var left = r.Left < (monitor.Work.Left + monitor.Work.Right - (r.Right - r.Left)) / 2;
            var upper = r.Top < (monitor.Work.Top + monitor.Work.Bottom - (r.Bottom - r.Top)) / 2;
            var centered = Math.Abs((r.Left + r.Right) - (monitor.Work.Left + monitor.Work.Right)) <= 4;
            Check("window:position-" + position, ok && position switch { 0 => left && upper, 1 => left && !upper, 2 => !left && upper, 3 => !left && !upper, _ => centered },
                $"rect={r.Left},{r.Top},{r.Right},{r.Bottom}");
        }
        var previousWidth = 0;
        foreach (var size in new[] { 0, 1, 2, 3 })
        {
            var ok = Place(4, size, false);
            GetWindowRect(hwnd, out var r);
            var width = r.Right - r.Left;
            Check("window:size-" + size, ok && (size == 3 || width >= previousWidth) && r.Left >= monitor.Work.Left && r.Right <= monitor.Work.Right && r.Top >= monitor.Work.Top && r.Bottom <= monitor.Work.Bottom,
                $"size={width}x{r.Bottom - r.Top}");
            previousWidth = width;
        }
        Check("window:topmost", Place(4, 1, true) && (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0, "topmost on");
        Check("window:notopmost", Place(4, 1, false) && (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) == 0, "topmost off");
        var fullOk = Place(4, 4, false);
        GetWindowRect(hwnd, out var full);
        Check("window:fullscreen", fullOk && full.Left <= monitor.Monitor.Left + 2 && full.Right >= monitor.Monitor.Right - 2, "fullscreen rect");
        Check("window:fullscreen-idempotent", Place(4, 4, false), "repeat fullscreen");
        Check("window:exit-fullscreen", Place(4, 1, false), "return to normal");
        Place(0, 2, false);
        GetWindowRect(hwnd, out var manual);
        Thread.Sleep(2200);
        GetWindowRect(hwnd, out var unchanged);
        Check("window:manual-layout-preserved", manual.Left == unchanged.Left && manual.Top == unchanged.Top && manual.Right == unchanged.Right, "timer does not override later manual moves");
        renderer.SetMediaStop();
        Check("stop:owned-process-exit", renderer.PlayerProcessId is null, "dedicated process closed");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void Check(string name, bool ok, string detail)
    {
        Checks.Add((name, ok, detail));
        Say($"{(ok ? "PASS" : "FAIL")}  {name,-26} {detail}");
    }

    private static void Say(string line)
    {
        Console.WriteLine(line);
        Report.AppendLine(line);
    }

    private static (int Width, int Height, string Sha) Shot(IntPtr hwnd, string dir, string name)
    {
        var path = Path.Combine(dir, name + ".bmp");
        var (w, h, sha) = CaptureWindow(hwnd, path);
        Say($"shot       : {path} ({w}x{h}, {sha})");
        return (w, h, sha);
    }

    private static List<string> PlayerWindowTitles(int pid)
    {
        var titles = new List<string>();
        if (pid <= 0)
        {
            return titles;
        }

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == pid && IsWindowVisible(hwnd))
            {
                var buffer = new StringBuilder(256);
                GetWindowTextW(hwnd, buffer, buffer.Capacity);
                var title = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    titles.Add(title);
                }
            }

            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    private static void Focus(IntPtr hwnd)
    {
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        Thread.Sleep(200);
    }

    // ---- screen capture (GDI) ------------------------------------------------

    private static void SetProcessDpiAware()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        }
        catch
        {
            try
            {
                SetProcessDPIAware();
            }
            catch
            {
                // capture then runs in virtualized coordinates
            }
        }
    }

    private static (int Width, int Height, string Sha) CaptureWindow(IntPtr hwnd, string path)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return (0, 0, "nogdi");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
        {
            return (0, 0, "badrect");
        }

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(mem, bitmap);
        BitBlt(mem, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 | 0x40000000);

        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            GetDIBits(mem, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref info, 0);
        }
        finally
        {
            handle.Free();
            SelectObject(mem, previous);
            DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }

        try
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(14 + 40 + pixels.Length);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(-height);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(pixels.Length);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            writer.Write(pixels);
        }
        catch (Exception ex)
        {
            Say($"shot write failed: {ex.Message}");
        }

        return (width, height, Convert.ToHexString(SHA256.HashData(pixels))[..12]);
    }

    private sealed class TraceSink : IProtocolStateSink
    {
        private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
        public List<string> Log { get; } = new();

        public string GetState(string name) => _states.TryGetValue(name, out var value) ? value : string.Empty;

        public Dictionary<string, string> Snapshot() => new(_states, StringComparer.Ordinal);

        public void SetState(string name, string value)
        {
            _states[name] = value;
            Log.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + name + "=" + value);
        }

        public void SetState(string name, bool value) => SetState(name, value ? "true" : "false");

        public void SetState(string name, int value) => SetState(name, value.ToString(CultureInfo.InvariantCulture));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BITMAPINFO info, uint usage);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
