using Macast.Core.Utils;

namespace Macast.Tray;

/// <summary>HKCU\...\Run entry used by the "start with Windows" checkbox.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Macast";

    /// <summary>
    /// True only when the Run value exists *and* the program it points at still
    /// exists: an install that was moved or deleted must not keep showing as enabled.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value) && CommandExists(value);
        }
        catch (Exception ex)
        {
            Log.Warn($"read autostart failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes or removes the Run entry; false means nothing was changed.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                Log.Warn("autostart: cannot open HKCU Run key");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("autostart disabled");
                return true;
            }

            var command = BuildCommand();
            if (command is null)
            {
                Log.Warn("autostart: cannot determine the current executable");
                return false;
            }

            key.SetValue(ValueName, command);
            Log.Info($"autostart enabled: {command}");
            return true;
        }
        catch (Exception ex)
        {
            // A locked-down policy hive should not crash the settings window.
            Log.Warn($"autostart update failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The command the Run key should hold for the current process.</summary>
    private static string? BuildCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return null;
        }

        // dotnet run / dotnet Macast.Tray.dll reports dotnet.exe as the process;
        // written bare it would start an empty host at login, so keep the dll as an
        // argument in that case.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // GetCommandLineArgs()[0] is the entry assembly (the dll) even when the
            // process is dotnet.exe; Assembly.Location is empty under single-file publish.
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(entry) ||
                !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(entry))
            {
                return null;
            }

            return $"\"{exe}\" \"{entry}\"";
        }

        return $"\"{exe}\"";
    }

    /// <summary>Checks that the stored command can still be started.</summary>
    private static bool CommandExists(string command)
    {
        var parts = SplitCommand(command);
        if (parts.Count == 0 || !File.Exists(parts[0]))
        {
            return false;
        }

        // dotnet + app.dll: the dll has to survive too
        return parts.Count == 1 ||
               !parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               File.Exists(parts[1]);
    }

    /// <summary>Splits a Run command line into its quoted/bare tokens.</summary>
    private static List<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && char.IsWhiteSpace(command[index]))
            {
                index++;
            }

            if (index >= command.Length)
            {
                break;
            }

            if (command[index] == '"')
            {
                var end = command.IndexOf('"', index + 1);
                if (end < 0)
                {
                    parts.Add(command[(index + 1)..]);
                    break;
                }

                parts.Add(command[(index + 1)..end]);
                index = end + 1;
            }
            else
            {
                var end = command.IndexOf(' ', index);
                if (end < 0)
                {
                    parts.Add(command[index..]);
                    break;
                }

                parts.Add(command[index..end]);
                index = end;
            }
        }

        return parts;
    }
}
