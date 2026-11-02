using System.Diagnostics;
using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Tray;

internal sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _restartItem;
    private MacastApp? _app;
    private bool _busy;

    public TrayApp()
    {
        _icon = new NotifyIcon
        {
            Icon = TrayIcon.Load(),
            Visible = true,
            Text = "Macast"
        };

        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("正在启动...") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("停止投屏", null, async (_, _) => await ToggleAsync());
        _settingsItem = new ToolStripMenuItem("设置", null, (_, _) => OpenSettings());
        _restartItem = new ToolStripMenuItem("重启投屏服务", null, async (_, _) => await RestartAsync());
        var configItem = new ToolStripMenuItem("打开配置目录", null, (_, _) => OpenFolder(AppSettings.ConfigDir));
        var exitItem = new ToolStripMenuItem("退出", null, async (_, _) => await ExitAsync());

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(configItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => OpenSettings();

        try
        {
            _app = new MacastApp();
            UpdateState();
        }
        catch (Exception ex)
        {
            Log.Error($"create app failed: {ex}");
            _statusItem.Text = "初始化失败";
            MessageBox.Show("Macast 初始化失败：" + ex.Message, "Macast",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void Start()
    {
        _ = StartAsync(showBalloon: true);
    }

    private async Task StartAsync(bool showBalloon)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _app ??= new MacastApp();
            await _app.StartAsync();
            UpdateState();
            if (showBalloon)
            {
                _icon.ShowBalloonTip(2000, "Macast", "投屏服务已启动", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"start failed: {ex}");
            _icon.ShowBalloonTip(3000, "Macast", "启动失败: " + ex.Message, ToolTipIcon.Error);
            _statusItem.Text = "启动失败";
            _toggleItem.Text = "重试启动";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ToggleAsync()
    {
        if (_busy || _app is null)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_app.Running)
            {
                await _app.StopAsync();
                UpdateState();
                _icon.ShowBalloonTip(1500, "Macast", "投屏服务已停止", ToolTipIcon.Info);
            }
            else
            {
                await _app.StartAsync();
                UpdateState();
                _icon.ShowBalloonTip(1500, "Macast", "投屏服务已启动", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"toggle failed: {ex}");
            _icon.ShowBalloonTip(3000, "Macast", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void UpdateState()
    {
        _restartItem.Enabled = !_busy && _app is not null;
        if (_app is { Running: true })
        {
            _statusItem.Text = $"{_app.Settings.FriendlyName}  {_app.DeviceUrl()}";
            _toggleItem.Text = "停止投屏";
            _icon.Text = Truncate(_app.Settings.FriendlyName, 63);
            _settingsItem.Enabled = true;
        }
        else
        {
            _statusItem.Text = _app is null ? "初始化失败" : "已停止";
            _toggleItem.Text = "启动投屏";
            _icon.Text = "Macast";
            _settingsItem.Enabled = _app is not null;
        }
    }

    private void OpenSettings()
    {
        if (_app is null)
        {
            return;
        }

        using var form = new SettingsForm(_app);
        form.ShowDialog();
        UpdateState();
    }

    private async Task RestartAsync()
    {
        if (_busy || _app is null) return;
        _busy = true;
        _restartItem.Enabled = _toggleItem.Enabled = _settingsItem.Enabled = false;
        try
        {
            await _app.RestartAsync();
            _icon.ShowBalloonTip(1500, "Macast", "投屏服务已重启", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error($"restart failed: {ex}");
            _icon.ShowBalloonTip(3000, "Macast", "重启失败：" + ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            _toggleItem.Enabled = true;
            UpdateState();
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"open folder failed: {ex.Message}");
        }
    }

    private async Task ExitAsync()
    {
        try
        {
            if (_app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"stop before exit failed: {ex.Message}");
        }
        finally
        {
            _icon.Visible = false;
            Application.Exit();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        try
        {
            var app = _app;
            if (app is not null)
            {
                // Run off the UI thread: DisposeAsync awaits and must not
                // resume on a WinForms message loop that is shutting down.
                Task.Run(async () => await app.DisposeAsync()).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // ignore
        }

        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
