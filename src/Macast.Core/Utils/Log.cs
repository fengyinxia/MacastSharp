using System.Text.RegularExpressions;

namespace Macast.Core.Utils;

public static class Log
{
    private static readonly object Gate = new();
    public static string? LogFile { get; set; }
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
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length >= 5 * 1024 * 1024)
                    File.Move(LogFile, LogFile + ".1", overwrite: true);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }
}
