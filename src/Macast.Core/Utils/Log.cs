using System.Text.RegularExpressions;

namespace Macast.Core.Utils;

public static class Log
{
    private static readonly object Gate = new();
    public const int DefaultRetentionDays = 7;
    public static string? LogFile { get; set; }
    public static int RetentionDays { get; private set; } = DefaultRetentionDays;
    private static DateTime _nextCleanupUtc;

    public static void ConfigureRetention(int days)
    {
        lock (Gate)
        {
            RetentionDays = Math.Clamp(days, 1, 365);
            _nextCleanupUtc = DateTime.MinValue;
            try { CleanupExpired(); }
            catch { /* Logging must not prevent settings or startup. */ }
        }
    }

    private static void CleanupExpired()
    {
        if (string.IsNullOrWhiteSpace(LogFile)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(LogFile))!;
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var name = Path.GetFileName(LogFile);
        foreach (var file in Directory.EnumerateFiles(directory, name + ".*"))
        {
            var fileName = Path.GetFileName(file);
            // Windows wildcard matching can also return the base file for "name.*".
            if (!fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = fileName[(name.Length + 1)..];
            if ((suffix == "1" || Regex.IsMatch(suffix, @"^\d{8}-\d{9}-[a-f0-9]{8}$")) &&
                File.GetLastWriteTimeUtc(file) < cutoff)
                File.Delete(file);
        }
        if (File.Exists(LogFile) && File.GetLastWriteTimeUtc(LogFile) < cutoff)
            File.Delete(LogFile);
        _nextCleanupUtc = DateTime.UtcNow.AddHours(1);
    }
    public static bool Echo { get; set; } = true;
    public static bool DebugEnabled { get; set; } =
        Environment.GetEnvironmentVariable("MACAST_DEBUG") == "1";
    private static readonly Regex Credentials = new(
        """(?i)(access_?key|mobile_access_key|token|authorization)(["\s:=]+)([^"\s&,}]+)""", RegexOptions.Compiled);

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Debug(string message)
    {
        if (DebugEnabled) Write("DEBUG", message);
    }

    private static void Write(string level, string message)
    {
        message = Credentials.Replace(message, "$1$2[redacted]");
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (Echo)
        {
            Console.WriteLine(line);
        }

        if (string.IsNullOrWhiteSpace(LogFile))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (DateTime.UtcNow >= _nextCleanupUtc) CleanupExpired();
                if (File.Exists(LogFile) &&
                    (File.GetLastWriteTime(LogFile).Date < DateTime.Today || new FileInfo(LogFile).Length >= 5 * 1024 * 1024))
                {
                    var archive = LogFile + "." + File.GetLastWriteTime(LogFile).ToString("yyyyMMdd-HHmmssfff") +
                                  "-" + Guid.NewGuid().ToString("N")[..8];
                    File.Move(LogFile, archive);
                }
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }
}
