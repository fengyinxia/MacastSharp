namespace Macast.Tray;

/// <summary>HKCU\...\Run entry used by the "start with Windows" checkbox.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Macast";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // A locked-down policy hive should not crash the settings window.
        }
    }
}
