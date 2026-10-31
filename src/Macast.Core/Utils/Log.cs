namespace Macast.Core.Utils;

public static class Log
{
    private static readonly object Gate = new();
    public static string? LogFile { get; set; }
    public static bool Echo { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
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
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }
}
