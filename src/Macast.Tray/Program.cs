using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--selftest")) return SettingsSelfTest.Run();
        Log.LogFile = Path.Combine(AppSettings.ConfigDir, "macast-cs.log");
        Log.ConfigureRetention(AppSettings.Load().GetInt("LogRetentionDays", Log.DefaultRetentionDays));
        Log.Info($"Macast tray starting, version {AssetPaths.Version}");

        // "--settings" opens the desktop settings window directly (used by the
        // tray shortcut and by UI smoke tests) without starting the service.
        if (args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            var app = new MacastApp();
            try
            {
                using var form = new SettingsForm(app);
                Application.Run(form);
            }
            finally
            {
                Task.Run(async () => await app.DisposeAsync()).GetAwaiter().GetResult();
            }

            return 0;
        }

        using var tray = new TrayApp();
        tray.Start();
        Application.Run();
        return 0;
    }
}
