using System.Reflection;
using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Plugins;
using Macast.Core.Plugins.Builtin;
using Macast.Core.Renderer;
using System.Diagnostics;
using NumericUpDown = AntdUI.InputNumber;
using ComboBox = AntdUI.Select;
using TextBox = AntdUI.Input;
using Macast.Core.Utils;

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
            app.Settings.Set("Macast_Protocol", "DLNA");
            using (var form = new SettingsForm(app))
            {
                Require(form is AntdUI.Window && form.FormBorderStyle == FormBorderStyle.None, "AntdUI window not configured");
                Require(Descendants(form).OfType<AntdUI.Tabs>().Single().Pages.Count == 3, "settings navigation incomplete");
                Require(Field<AntdUI.Switch>(form, "_autoStartBox") is not null && Field<AntdUI.Switch>(form, "_ontopBox") is not null, "AntdUI switches missing");
                Console.WriteLine("PASS  AntdUI window, navigation and controls");
                VerifySidebarClicks(form);
                Console.WriteLine("PASS  sidebar mouse clicks switch all settings pages");
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "unchanged settings reloaded renderer");
                Console.WriteLine("PASS  settings no-op preserves renderer");
                Require(app.Protocol.Id == "Auto" && app.Settings.GetString("Macast_Protocol") == "Auto", "legacy protocol was not normalized");
                Require(typeof(SettingsForm).GetField("_protocolBox", BindingFlags.Instance | BindingFlags.NonPublic) is null, "protocol selector still exists");
                Require(!Descendants(form).Any(c => c.Text.Contains("投屏协议", StringComparison.Ordinal) || c.Text.Contains("DLNA + B站 NVA", StringComparison.Ordinal)), "protocol still shown in settings UI");
                Console.WriteLine("PASS  automatic protocol stays active without protocol UI");
                var plugins = Field<ComboBox>(form, "_rendererBox");
                foreach (var plugin in plugins.Items.Cast<MacastPlugin>().ToArray())
                {
                    plugins.SelectedValue = plugin;
                    var texts = Descendants(form).OfType<Label>().Select(c => c.Text).ToArray();
                    Require(string.IsNullOrWhiteSpace(plugin.Description) || !texts.Contains(plugin.Description), "plugin description still displayed");
                    Require(!plugin.Settings.Any(s => !string.IsNullOrWhiteSpace(s.Help) && texts.Contains(s.Help)), "setting help still displayed");
                }
                plugins.SelectedValue = plugins.Items.Cast<MacastPlugin>().Single(p => p.Id == app.RendererPlugin.Metadata.Id);
                Require(!Descendants(form).OfType<Label>().Any(c => c.Text.Contains("仅 mpv", StringComparison.Ordinal) || c.Text.Contains("默认只启用", StringComparison.Ordinal) || c.Text.Contains("0 表示", StringComparison.Ordinal)), "setting explanation still displayed");
                Console.WriteLine("PASS  setting help and plugin descriptions removed");

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
                var plugins = Field<ComboBox>(form, "_rendererBox");
                plugins.SelectedValue = plugins.Items.Cast<MacastPlugin>().Single(p => p.Id == "MPV");
                var editors = Field<Dictionary<string, Control>>(form, "_pluginEditors");
                var pathBox = (TextBox)((TableLayoutPanel)editors["Mpv_Path"]).Tag!;
                pathBox.Text = executable;
                Apply(form);
                Require(AppSettings.Load().GetString("Mpv_Path") == executable, "MPV path did not persist");
                Require(MpvRendererPlugin.ResolveMpv(app.Settings) == executable, "configured path ignored");
                var startInfo = (ProcessStartInfo)typeof(MpvRenderer).GetMethod("BuildStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(app.Renderer, null)!;
                Require(startInfo.FileName == executable, "MPV launch uses a different executable");
                renderer = app.Renderer;
                Apply(form);
                Require(ReferenceEquals(renderer, app.Renderer), "unchanged MPV path reloaded renderer");
            }
            app.Settings.Set("Mpv_Path", executable + ".missing");
            Require(MpvRendererPlugin.ResolveMpv(app.Settings) is null, "invalid explicit path fell back silently");
            app.Settings.Set("Mpv_Path", executable);
            Console.WriteLine("PASS  merged MPV path editor, persistence and launch");

            var before = app.Settings.GetInt("PlayerDefaultVolume");
            using (var cancelled = new SettingsForm(app))
                Field<NumericUpDown>(cancelled, "_volumeBox").Value = 13;
            Require(app.Settings.GetInt("PlayerDefaultVolume") == before, "cancel changed live settings");
            Console.WriteLine("PASS  cancel keeps settings unchanged");

            var originalPotPath = app.Settings.GetString("Potplayer_Path");
            var originalMpvPath = app.Settings.GetString("Mpv_Path");
            using (var draft = new SettingsForm(app))
            {
                draft.Show();
                Application.DoEvents();
                var tabs = Descendants(draft).OfType<AntdUI.Tabs>().Single();
                tabs.SelectedIndex = 1;
                var players = Field<ComboBox>(draft, "_rendererBox");
                var mpv = players.Items.Cast<MacastPlugin>().Single(p => p.Id == "MPV");
                var pot = players.Items.Cast<MacastPlugin>().Single(p => p.Id == "PotPlayer");
                var editors = Field<Dictionary<string, Control>>(draft, "_pluginEditors");
                var currentRenderer = app.Renderer;
                ((TextBox)((TableLayoutPanel)editors["Mpv_Path"]).Tag!).Text = "mpv-draft.exe";
                players.SelectedValue = pot;
                Require(!Field<Panel>(draft, "_mpvOptions").Visible, "MPV-only settings visible for PotPlayer");
                Require(Field<Panel>(draft, "_windowOptions").Visible, "PotPlayer window settings missing");
                Require(Field<ComboBox>(draft, "_hwBox").Items.Count == 2, "unsupported Windows GPU option still present");
                var position = Field<ComboBox>(draft, "_positionBox");
                position.SelectedIndex = 4;
                Field<ComboBox>(draft, "_sizeBox").SelectedIndex = 4;
                Field<AntdUI.Switch>(draft, "_ontopBox").Checked = false;
                ((TextBox)((TableLayoutPanel)editors["Potplayer_Path"]).Tag!).Text = "pot-draft.exe";
                players.SelectedValue = mpv;
                Require(Field<Panel>(draft, "_mpvOptions").Visible, "MPV settings did not return");
                Require(position.SelectedIndex == 4 && Field<ComboBox>(draft, "_sizeBox").SelectedIndex == 4 && !Field<AntdUI.Switch>(draft, "_ontopBox").Checked, "shared window drafts lost");
                Require(((TextBox)((TableLayoutPanel)editors["Mpv_Path"]).Tag!).Text == "mpv-draft.exe", "MPV draft lost");
                players.SelectedValue = pot;
                Require(((TextBox)((TableLayoutPanel)editors["Potplayer_Path"]).Tag!).Text == "pot-draft.exe", "PotPlayer draft lost");
                Require(ReferenceEquals(app.Renderer, currentRenderer), "browsing player settings interrupted playback");
                draft.Hide();
            }
            Require(app.Settings.GetString("Potplayer_Path") == originalPotPath && app.Settings.GetString("Mpv_Path") == originalMpvPath, "cancel saved player path drafts");
            Console.WriteLine("PASS  player selection preserves drafts and cancel without interrupting playback");

            using (var form = new SettingsForm(app))
            {
                var rendererBefore = app.Renderer;
                var protocolBefore = app.Protocol;
                Field<NumericUpDown>(form, "_logRetentionBox").Value = 14;
                Apply(form);
                Require(AppSettings.Load().GetInt("LogRetentionDays") == 14 && Log.RetentionDays == 14, "log retention not applied");
                Require(ReferenceEquals(rendererBefore, app.Renderer) && ReferenceEquals(protocolBefore, app.Protocol), "log retention restarted playback");
                var tabs = Descendants(form).OfType<AntdUI.Tabs>().Single();
                Require(tabs.Pages.Select(p => p.Text).SequenceEqual(new[] { "常规", "播放器", "关于" }), "unexpected settings pages");
                Require(!Descendants(form).Any(c => c is CheckedListBox || c.Name == "networkSettings" || c.Text.Contains("网卡", StringComparison.Ordinal)), "manual network settings still present");
                Require(Descendants(tabs.Pages[2]).Any(c => c.Text == "查看日志"), "log viewer not in About");
                Require(!Descendants(form).Any(c => c.Text is "重启投屏服务" or "打开配置目录" or "打开目录"), "maintenance action duplicated in settings");
            }
            Console.WriteLine("PASS  automatic network selection, General log retention and About log viewer");
            using (var tray = new TrayApp())
            {
                var menu = (ContextMenuStrip)typeof(TrayApp).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                Require(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "重启投屏服务"), "tray restart missing");
                Require(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "打开配置目录"), "tray config directory missing");
                Require(!menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "直播频道"), "Live still appears in tray");
            }
            Console.WriteLine("PASS  tray restart and config directory without Live");

            using (var preview = new SettingsForm(app))
            {
                foreach (var dark in new[] { false, true })
                {
                    SettingsTheme.Apply(dark);
                    preview.BackColor = SettingsTheme.Surface;
                    preview.ForeColor = SettingsTheme.Text;
                    var tabs = Descendants(preview).OfType<AntdUI.Tabs>().Single();
                    foreach (var page in tabs.Pages)
                    {
                        page.BackColor = SettingsTheme.Surface;
                        page.ForeColor = SettingsTheme.Text;
                        tabs.SelectedTab = page;
                        preview.Size = preview.MinimumSize;
                        preview.CreateControl();
                        preview.PerformLayout();
                        using var bitmap = new Bitmap(preview.Width, preview.Height);
                        preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    }
                    Require(AntdUI.Config.Mode == (dark ? AntdUI.TMode.Dark : AntdUI.TMode.Light), "theme mode mismatch");
                }
            }
            SettingsTheme.Initialize();
            Console.WriteLine("PASS  AntdUI pages render in light and dark at minimum size");
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

    private static void VerifySidebarClicks(SettingsForm form)
    {
        form.Show();
        Application.DoEvents();
        var tabs = Descendants(form).OfType<AntdUI.Tabs>().Single();
        Require(tabs.EnableSwitch, "sidebar switching is disabled");
        var down = typeof(AntdUI.Tabs).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var up = typeof(AntdUI.Tabs).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var index in new[] { 1, 2, 0 })
        {
            Point? target = null;
            for (var y = 0; y < tabs.Height && target is null; y += 2)
                for (var x = 0; x < Math.Min(tabs.Width, 160); x += 2)
                    if (ReferenceEquals(tabs.ContainsTabPage(x, y), tabs.Pages[index]))
                    {
                        target = new Point(x, y);
                        break;
                    }
            Require(target.HasValue, "sidebar click target missing: " + tabs.Pages[index].Text);
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, target!.Value.X, target.Value.Y, 0);
            down.Invoke(tabs, new object[] { mouse });
            up.Invoke(tabs, new object[] { mouse });
            Application.DoEvents();
            Require(tabs.SelectedIndex == index, "sidebar click did not switch to " + tabs.Pages[index].Text);
        }
        form.Hide();
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

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
