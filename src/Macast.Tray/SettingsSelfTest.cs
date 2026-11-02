using System.Reflection;
using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Plugins;
using Macast.Core.Plugins.Builtin;
using Macast.Core.Renderer;
using System.Diagnostics;

namespace Macast.Tray;

internal static class SettingsSelfTest
{
    public static int Run()
    {
        var previous = Environment.GetEnvironmentVariable("MACAST_CONFIG_DIR");
        var directory = Path.Combine(Path.GetTempPath(), "macast-settings-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", directory);
        MacastApp? app = null;
        try
        {
            app = new MacastApp(noMpv: true, loopbackOnly: true);
            Task.Run(() => app.StartAsync(0)).GetAwaiter().GetResult();
            var renderer = app.Renderer;
            using (var form = new SettingsForm(app))
            {
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "unchanged settings reloaded renderer");
                Console.WriteLine("PASS  settings no-op preserves renderer");

                Field<NumericUpDown>(form, "_volumeBox").Value = 42;
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "volume change reloaded renderer");
                Require(app.Protocol.GetState("Volume") == "42", "volume was not applied live");
                Console.WriteLine("PASS  volume applies without reload");

                var position = Field<ComboBox>(form, "_positionBox");
                position.SelectedIndex = (position.SelectedIndex + 1) % position.Items.Count;
                Apply(form);
                Require(!ReferenceEquals(renderer, app.Renderer), "player change did not reload renderer");
                renderer = app.Renderer;
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "second apply reloaded renderer");
                Console.WriteLine("PASS  player change reloads once");
            }

            var executable = Path.Combine(directory, "custom mpv.exe");
            File.WriteAllText(executable, "path resolution test, not an executable");
            using (var form = new SettingsForm(app))
            {
                var plugins = Field<ListBox>(form, "_pluginList");
                plugins.SelectedItem = plugins.Items.Cast<MacastPlugin>().Single(p => p.Id == "MPV");
                var editors = Field<Dictionary<string, Control>>(form, "_pluginEditors");
                var pathBox = (TextBox)((TableLayoutPanel)editors["Mpv_Path"]).Tag!;
                pathBox.Text = executable;
                Apply(form);
                Require(AppSettings.Load().GetString("Mpv_Path") == executable, "MPV path did not persist");
                Require(MpvRendererPlugin.ResolveMpv(app.Settings) == executable, "configured path ignored");
                var startInfo = (ProcessStartInfo)typeof(MpvRenderer).GetMethod("BuildStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(app.Renderer, null)!;
                Require(startInfo.FileName == executable, "MPV launch uses a different executable");
                Require(PluginRegistry.FindRenderer("Live")!.IsUsable(app.Settings), "Live did not share MPV path");
                renderer = app.Renderer;
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "unchanged MPV path reloaded renderer");
            }
            app.Settings.Set("Mpv_Path", executable + ".missing");
            Require(MpvRendererPlugin.ResolveMpv(app.Settings) is null, "invalid explicit path fell back silently");
            app.Settings.Set("Mpv_Path", executable);
            Console.WriteLine("PASS  MPV path editor, persistence, launch and Live sharing");

            var before = app.Settings.GetInt("PlayerDefaultVolume");
            using (var cancelled = new SettingsForm(app))
                Field<NumericUpDown>(cancelled, "_volumeBox").Value = 13;
            Require(app.Settings.GetInt("PlayerDefaultVolume") == before, "cancel changed live settings");
            Console.WriteLine("PASS  cancel keeps settings unchanged");
            Console.WriteLine("SETTINGS SELFTEST PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SETTINGS SELFTEST FAIL: " + ex);
            return 1;
        }
        finally
        {
            if (app is not null) Task.Run(async () => await app.DisposeAsync()).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static T Field<T>(SettingsForm form, string name) =>
        (T)typeof(SettingsForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void Apply(SettingsForm form) =>
        ((Task)typeof(SettingsForm).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { false })!).GetAwaiter().GetResult();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
