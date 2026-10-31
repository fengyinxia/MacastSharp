using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Log.LogFile = Path.Combine(AppSettings.ConfigDir, "macast-cs.log");
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
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return;
        }

        using var tray = new TrayApp();
        tray.Start();
        Application.Run();
    }
}
