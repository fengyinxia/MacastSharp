using System.Runtime.InteropServices;

namespace Macast.Core.Renderer;

/// <summary>Window operations target only the HWND owned by the renderer's /new process.</summary>
internal static class PotPlayerWindow
{
    internal readonly record struct Bounds(int X, int Y, int Width, int Height);

    internal static Bounds CalculateBounds(Bounds work, int videoWidth, int videoHeight,
        int borderWidth, int borderHeight, int position, int size)
    {
        var ratio = videoWidth > 0 && videoHeight > 0 ? (double)videoWidth / videoHeight : 16d / 9;
        var fraction = size switch { 0 => .15, 1 => .20, 2 => .40, _ => .90 };
        var availableWidth = Math.Max(1, work.Width * fraction - borderWidth);
        var availableHeight = Math.Max(1, work.Height * fraction - borderHeight);
        var width = Math.Min(availableWidth, availableHeight * ratio);
        if (size == 3 && videoWidth > 0) width = Math.Min(width, videoWidth);
        var height = width / ratio;
        var outerWidth = Math.Min(work.Width, Math.Max(1, (int)Math.Round(width) + borderWidth));
        var outerHeight = Math.Min(work.Height, Math.Max(1, (int)Math.Round(height) + borderHeight));
        var gapX = Math.Max(0, work.Width - outerWidth);
        var gapY = Math.Max(0, work.Height - outerHeight);
        var (x, y) = position switch
        {
            0 => (.02, .05), 1 => (.02, .98), 2 => (.98, .05),
            3 => (.98, .98), _ => (.50, .50)
        };
        return new Bounds(work.X + (int)Math.Round(gapX * x), work.Y + (int)Math.Round(gapY * y), outerWidth, outerHeight);
    }

    internal static bool Apply(IntPtr hwnd, int videoWidth, int videoHeight, int position, int size, bool topmost)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return ApplyCore(hwnd, videoWidth, videoHeight, position, size, topmost); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    private static bool ApplyCore(IntPtr hwnd, int videoWidth, int videoHeight, int position, int size, bool topmost)
    {
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor) || !GetWindowRect(hwnd, out var rect)) return false;
        var fullscreen = rect.Left <= monitor.Monitor.Left + 2 && rect.Top <= monitor.Monitor.Top + 2 &&
                         rect.Right >= monitor.Monitor.Right - 2 && rect.Bottom >= monitor.Monitor.Bottom - 2;
        // WM_COMMAND is a toggle: inspect the current window before sending it.
        if ((size == 4) != fullscreen)
        {
            if (!Send(hwnd, 0x0111, 10013, 0, out _)) return false;
            // The caller retries after the player has processed the mode change.
            return false;
        }
        if (size == 4)
            return SetWindowPos(hwnd, topmost ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x13);

        // A maximized/minimized window ignores normal placement until restored.
        if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, 9);
        GetWindowRect(hwnd, out rect);
        GetClientRect(hwnd, out var client);
        var work = monitor.Work;
        var bounds = CalculateBounds(new Bounds(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
            videoWidth, videoHeight, Math.Max(0, rect.Right - rect.Left - client.Right),
            Math.Max(0, rect.Bottom - rect.Top - client.Bottom), position, size);
        var order = topmost ? new IntPtr(-1) : new IntPtr(-2);
        if (!SetWindowPos(hwnd, order, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10)) return false;
        // Skins impose a minimum width and may resize around the requested centre.
        // Reposition using the actual size, and keep its aspect sensible at that minimum.
        if (!GetWindowRect(hwnd, out var actual)) return false;
        var actualWidth = actual.Right - actual.Left;
        if (actualWidth > bounds.Width + 2)
        {
            var ratio = videoWidth > 0 && videoHeight > 0 ? (double)videoWidth / videoHeight : 16d / 9;
            var height = Math.Min(work.Bottom - work.Top, Math.Max(bounds.Height,
                (int)Math.Round((actualWidth - Math.Max(0, rect.Right - rect.Left - client.Right)) / ratio) +
                Math.Max(0, rect.Bottom - rect.Top - client.Bottom)));
            SetWindowPos(hwnd, order, bounds.X, bounds.Y, actualWidth, height, 0x10);
            GetWindowRect(hwnd, out actual);
        }
        var gapX = Math.Max(0, work.Right - work.Left - (actual.Right - actual.Left));
        var gapY = Math.Max(0, work.Bottom - work.Top - (actual.Bottom - actual.Top));
        var (x, y) = position switch
        {
            0 => (.02, .05), 1 => (.02, .98), 2 => (.98, .05),
            3 => (.98, .98), _ => (.50, .50)
        };
        return SetWindowPos(hwnd, order, work.Left + (int)Math.Round(gapX * x),
            work.Top + (int)Math.Round(gapY * y), 0, 0, 0x11);
    }

    internal static bool Command(IntPtr hwnd, int command, long value, out long result) =>
        Send(hwnd, 0x0400, command, value, out result);

    private static bool Send(IntPtr hwnd, uint message, int command, long value, out long result)
    {
        result = 0;
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        // Never block the protocol/UI indefinitely when the external player hangs.
        var ok = SendMessageTimeout(hwnd, message, new IntPtr(command), new IntPtr(value), 0x22, 300, out var response) != IntPtr.Zero;
        result = response.ToInt64();
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
