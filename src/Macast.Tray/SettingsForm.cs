using Macast.Core;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;
using Button = AntdUI.Button;
using TextBox = AntdUI.Input;
using NumericUpDown = AntdUI.InputNumber;
using CheckBox = AntdUI.Switch;
using ComboBox = AntdUI.Select;
using TabPage = AntdUI.TabPage;

namespace Macast.Tray;

/// <summary>
/// The desktop replacement for the old Vue settings page. Everything that used to
/// be a POST to /api is a normal WinForms control here, and applying the changes
/// calls MacastApp.RestartAsync / ReloadRendererAsync directly instead of reloading a
/// browser tab.
/// </summary>
internal sealed class SettingsForm : AntdUI.Window
{
    private readonly MacastApp _app;
    private readonly TextBox _nameBox = new() { Radius = 8 };
    private readonly NumericUpDown _portBox = new() { Radius = 8 };
    private readonly CheckBox _autoStartBox = new();
    private readonly ComboBox _rendererBox = NewSelect();
    private readonly ComboBox _positionBox = NewSelect();
    private readonly ComboBox _sizeBox = NewSelect();
    private readonly ComboBox _hwBox = NewSelect();
    private readonly CheckBox _ontopBox = new();
    private readonly NumericUpDown _volumeBox = new() { Radius = 8 };
    private readonly NumericUpDown _logRetentionBox = new() { Radius = 8, Minimum = 1, Maximum = 365, SuffixText = "天" };
    private readonly TableLayoutPanel _playerOptions = CreateLayout(1);
    private readonly Panel _windowOptions = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly Panel _mpvOptions = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly Label _statusLabel = new();
    private readonly Button _saveButton = new() { Radius = 8, Type = AntdUI.TTypeMini.Primary };
    private readonly Button _applyButton = new() { Radius = 8 };
    private readonly Dictionary<string, Control> _pluginEditors = new(StringComparer.Ordinal);

    private MacastPlugin? _selectedPlugin;
    private string _sourceName = string.Empty;
    private string _sourceRenderer = string.Empty;
    private int _sourcePort;
    private readonly AppSettings _pluginDraft = new();
    private string _sourcePlayerSettings = string.Empty;
    private int _sourceVolume;
    private bool _sourceAutoStart;
    private bool _busy;

    public SettingsForm(MacastApp app)
    {
        _app = app;
        foreach (var pair in app.Settings.Values)
            _pluginDraft.Set(pair.Key, pair.Value);
        BuildUi();
        LoadSettings();
    }

    private void BuildUi()
    {
        SettingsTheme.Initialize();
        Text = "Macast 设置";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 580);
        Size = new Size(920, 660);
        Font = new Font("Microsoft YaHei UI", 10F);
        Icon = TrayIcon.Load();
        BackColor = SettingsTheme.Surface;
        ForeColor = SettingsTheme.Text;
        FormBorderStyle = FormBorderStyle.None;
        Resizable = true;

        var header = new AntdUI.PageHeader
        {
            Text = "Macast", SubText = "设置", ShowIcon = true,
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 0, 0, 0),
            ShowButton = true, MinimizeBox = false, MaximizeBox = true,
            DividerShow = true
        };
        var tabs = new AntdUI.Tabs
        {
            Name = "settingsTabs", Dock = DockStyle.Fill,
            Alignment = TabAlignment.Left, ItemSize = 112, Gap = 16,
            Padding = new Padding(16, 16, 16, 12), Fill = SettingsTheme.Accent,
            EnableSwitch = true
        };
        tabs.Pages.Add(BuildGeneralTab());
        tabs.Pages.Add(BuildPlayerTab());
        tabs.Pages.Add(BuildAboutTab());
        tabs.SelectedIndex = 0;

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom, Height = 68,
            Padding = new Padding(24, 12, 20, 12), BackColor = SettingsTheme.Surface
        };
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = SettingsTheme.SecondaryText;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Font = new Font(Font.FontFamily, 9F);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoSize = true, Margin = Padding.Empty
        };
        _saveButton.Text = "保存";
        _saveButton.Size = new Size(88, 42);
        _saveButton.Click += async (_, _) => await ApplyAsync(closeAfter: true);
        _applyButton.Text = "应用";
        _applyButton.Size = new Size(88, 42);
        _applyButton.Click += async (_, _) => await ApplyAsync(closeAfter: false);
        var cancel = new Button
        {
            Text = "取消", Size = new Size(88, 42), Radius = 8,
            DialogResult = DialogResult.Cancel
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_applyButton);
        buttons.Controls.Add(_saveButton);
        bottom.Controls.Add(_statusLabel);
        bottom.Controls.Add(buttons);
        AcceptButton = _saveButton;
        CancelButton = cancel;
        Controls.Add(tabs);
        Controls.Add(bottom);
        Controls.Add(header);
    }

    private static ComboBox NewSelect() => new() { List = true, Radius = 8, Height = 42 };

    private static TabPage NewPage(string title) => new()
    {
        Text = title, Padding = new Padding(24, 20, 16, 12),
        BackColor = SettingsTheme.Surface, ForeColor = SettingsTheme.Text, AutoScroll = true
    };

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("常规");
        var layout = CreateLayout(4);
        AddRow(layout, "设备名", _nameBox);
        AddRow(layout, "HTTP 端口", _portBox);
        AddRow(layout, "开机自启", _autoStartBox);
        AddRow(layout, "日志保留时间", _logRetentionBox);
        _portBox.Minimum = 0;
        _portBox.Maximum = 65535;
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildPlayerTab()
    {
        var page = NewPage("播放器");
        var layout = CreateLayout(3);

        AddRow(layout, "播放器", _rendererBox);
        var windowLayout = CreateLayout(3);
        AddRow(windowLayout, "窗口位置", _positionBox);
        AddRow(windowLayout, "窗口大小", _sizeBox);
        AddRow(windowLayout, "窗口置顶", _ontopBox);
        _windowOptions.Controls.Add(windowLayout);
        var mpvLayout = CreateLayout(1);
        AddRow(mpvLayout, "硬件解码", _hwBox);
        _mpvOptions.Controls.Add(mpvLayout);
        var volumeLayout = CreateLayout(1);
        AddRow(volumeLayout, "默认音量", _volumeBox);

        _rendererBox.Dock = DockStyle.Fill;
        _rendererBox.SelectedIndexChanged += (_, _) =>
        {
            CommitPluginEditors();
            ShowPlayerOptions(_rendererBox.SelectedValue as MacastPlugin);
            UpdateStatusText();
        };

        foreach (var (text, value) in new[]
                 {
                     ("左上", 0), ("左下", 1), ("右上", 2), ("右下", 3), ("居中", 4)
                 })
        {
            _positionBox.Items.Add(new ValueItem(text, value));
        }

        foreach (var (text, value) in new[]
                 {
                     ("小", 0), ("标准", 1), ("大", 2), ("自适应", 3), ("全屏", 4)
                 })
        {
            _sizeBox.Items.Add(new ValueItem(text, value));
        }

        foreach (var (text, value) in new[]
                 {
                     ("禁用", 0), ("启用", 1)
                 })
        {
            _hwBox.Items.Add(new ValueItem(text, value));
        }


        _volumeBox.Minimum = 0;
        _volumeBox.Maximum = 100;
        _volumeBox.Increment = 5;

        page.Controls.Add(volumeLayout);
        page.Controls.Add(_mpvOptions);
        page.Controls.Add(_windowOptions);
        page.Controls.Add(_playerOptions);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = NewPage("关于");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var width = 780;
        layout.Controls.Add(new Label
        {
            Text = $"Macast C# {AssetPaths.Version}",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        });

        layout.Controls.Add(new Label
        {
            Text = "DLNA / UPnP 投屏接收端，原版由 xfangfang 以 Python 编写。\r\n" +
                   "本项目是 C# / .NET 8 移植版：Windows 原生托盘 + 桌面设置界面，播放器与协议全部通过 C# 插件实现。",
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, 12)
        });

        var log = new Button { Text = "查看日志", Size = new Size(112, 42), Radius = 8 };
        log.Click += (_, _) => ShowLog();
        layout.Controls.Add(log);
        page.Controls.Add(layout);
        return page;
    }

    private static TableLayoutPanel CreateLayout(int rows)
    {
        var layout = new TableLayoutPanel
        {
            // Top + AutoSize keeps the rows compact: with Dock.Fill the last row
            // absorbed all remaining height and the path picker grew huge.
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, 0, 12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        if (control is CheckBox)
        {
            control.Size = new Size(48, 28);
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(4, 9, 0, 9);
        }
        else
        {
            control.Dock = DockStyle.Fill;
            control.Height = 44;
            control.MinimumSize = new Size(0, 44);
        }
        row.Controls.Add(control);

        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = SettingsTheme.Text,
            Margin = new Padding(0, 8, 16, 8)
        });
        layout.Controls.Add(row);
    }

    private void LoadSettings()
    {
        var settings = _app.Settings;

        _nameBox.Text = settings.GetString("DLNA_FriendlyName", settings.MachineFriendlyName);
        _portBox.Value = Math.Clamp(settings.GetInt("ApplicationPort", 0), 0, 65535);
        _logRetentionBox.Value = Math.Clamp(settings.GetInt("LogRetentionDays", Log.DefaultRetentionDays), 1, 365);

        _autoStartBox.Checked = AutoStart.IsEnabled();
        _sourceAutoStart = _autoStartBox.Checked;

        _rendererBox.Items.Clear();

        foreach (var plugin in PluginRegistry.RendererPlugins)
        {
            _rendererBox.Items.Add(plugin.Metadata);
        }

        SelectByKey(_rendererBox, settings.GetString("Macast_Renderer", PluginRegistry.DefaultRendererId),
            _app.RendererPlugin.Metadata.Id);

        SelectCombo(_positionBox, settings.GetInt("PlayerPosition", 2));
        SelectCombo(_sizeBox, settings.GetInt("PlayerSize", 1));
        // The old value 2 only selected a dedicated GPU on macOS; on Windows it
        // has always meant ordinary hardware decoding. Do not display a fake mode.
        SelectCombo(_hwBox, Math.Clamp(settings.GetInt("PlayerHW", 1), 0, 1));
        _ontopBox.Checked = settings.GetInt("PlayerOntop", 1) != 0;
        _volumeBox.Value = Math.Clamp(settings.GetInt("PlayerDefaultVolume", 100), 0, 100);

        _sourceName = _nameBox.Text.Trim();
        _sourcePort = settings.GetInt("ApplicationPort", 0);
        _sourceRenderer = SelectedPluginId(_rendererBox);

        _sourcePlayerSettings = PlayerSettingsFingerprint();
        _sourceVolume = (int)_volumeBox.Value;
        UpdateStatusText();
    }

    private static void SelectByKey(ComboBox box, string key, string fallbackKey)
    {
        foreach (var item in box.Items)
        {
            if (item is MacastPlugin metadata &&
                (string.Equals(metadata.Id, key, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(metadata.Title, key, StringComparison.OrdinalIgnoreCase)))
            {
                box.SelectedValue = item;
                return;
            }
        }

        foreach (var item in box.Items)
        {
            if (item is MacastPlugin metadata &&
                string.Equals(metadata.Id, fallbackKey, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedValue = item;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static void SelectCombo(ComboBox box, int value)
    {
        foreach (var item in box.Items)
        {
            if (item is ValueItem v && v.Value == value)
            {
                box.SelectedValue = item;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static int ComboValue(ComboBox box, int fallback) =>
        box.SelectedValue is ValueItem item ? item.Value : fallback;

    private static string SelectedPluginId(ComboBox box) =>
        box.SelectedValue is MacastPlugin metadata ? metadata.Id : string.Empty;

    private void ShowPlayerOptions(MacastPlugin? plugin)
    {
        var previous = _playerOptions.Controls.Cast<Control>().ToArray();
        _playerOptions.Controls.Clear();
        foreach (var control in previous) control.Dispose();
        _pluginEditors.Clear();
        _selectedPlugin = plugin;
        _mpvOptions.Visible = plugin?.Id == "MPV";
        if (plugin is null) return;
        foreach (var descriptor in plugin.Settings)
        {
            var editor = CreateEditor(descriptor);
            _pluginEditors[descriptor.Key] = editor;
            AddRow(_playerOptions, "程序路径", editor);
        }
    }

    private Control CreateEditor(PluginSettingDescriptor descriptor)
    {
        var settings = _pluginDraft;
        switch (descriptor.Kind)
        {
            case PluginSettingKind.Boolean:
            {
                var box = new CheckBox
                {
                    Size = new Size(48, 28),
                    Checked = settings.GetBool(descriptor.Key,
                        descriptor.DefaultValue is bool b && b)
                };
                return box;
            }
            case PluginSettingKind.Number:
            {
                var box = new NumericUpDown
                {
                    Radius = 8,
                    Minimum = -1_000_000,
                    Maximum = 1_000_000,
                    Value = Math.Clamp(settings.GetInt(descriptor.Key,
                        descriptor.DefaultValue is int i ? i : 0), -1_000_000, 1_000_000)
                };
                return box;
            }
            case PluginSettingKind.Choice:
            {
                var box = NewSelect();
                var current = settings.GetString(descriptor.Key, descriptor.DefaultValue?.ToString() ?? string.Empty);
                foreach (var choice in descriptor.Choices ?? Array.Empty<PluginSettingChoice>())
                {
                    box.Items.Add(choice);
                    if (string.Equals(choice.Value, current, StringComparison.OrdinalIgnoreCase))
                    {
                        box.SelectedValue = choice;
                    }
                }

                if (box.SelectedValue is null && box.Items.Count > 0)
                {
                    box.SelectedIndex = 0;
                }

                return box;
            }
            case PluginSettingKind.Path:
            {
                var host = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
                host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78));
                host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
                var text = new TextBox
                {
                    Text = settings.GetString(descriptor.Key, descriptor.DefaultValue?.ToString() ?? string.Empty),
                    Radius = 8,
                    Dock = DockStyle.Fill
                };
                var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill, Radius = 8, Height = 44 };
                browse.Click += (_, _) => BrowseForPath(text);
                host.Controls.Add(text, 0, 0);
                host.Controls.Add(browse, 1, 0);
                host.Tag = text;
                return host;
            }
            default:
            {
                return new TextBox
                {
                    Text = settings.GetString(descriptor.Key, descriptor.DefaultValue?.ToString() ?? string.Empty),
                    Radius = 8
                };
            }
        }
    }

    private static void BrowseForPath(TextBox box)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "可执行文件|*.exe;*|所有文件|*.*",
            CheckFileExists = true
        };
        if (File.Exists(box.Text))
        {
            dialog.FileName = box.Text;
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            box.Text = dialog.FileName;
        }
    }

    private void CommitPluginEditors()
    {
        var plugin = _selectedPlugin;
        if (plugin is null)
        {
            return;
        }

        foreach (var descriptor in plugin.Settings)
        {
            if (!_pluginEditors.TryGetValue(descriptor.Key, out var editor))
            {
                continue;
            }

            switch (editor)
            {
                case CheckBox check:
                    _pluginDraft.Set(descriptor.Key, check.Checked);
                    break;
                case NumericUpDown number:
                    _pluginDraft.Set(descriptor.Key, (int)number.Value);
                    break;
                case ComboBox combo when combo.SelectedValue is PluginSettingChoice choice:
                    _pluginDraft.Set(descriptor.Key, choice.Value);
                    break;
                case TextBox text:
                    _pluginDraft.Set(descriptor.Key, text.Text.Trim());
                    break;
                case TableLayoutPanel pathHost when pathHost.Tag is TextBox pathBox:
                    _pluginDraft.Set(descriptor.Key, pathBox.Text.Trim());
                    break;
                case TableLayoutPanel choiceHost when choiceHost.Tag is ComboBox choiceBox &&
                                                        choiceBox.SelectedValue is PluginSettingChoice picked:
                    _pluginDraft.Set(descriptor.Key, picked.Value);
                    break;
            }
        }
    }

    private string PluginSettingsFingerprint(IEnumerable<MacastPlugin> plugins) =>
        string.Join("\n", plugins.SelectMany(p => p.Settings).Select(d => d.Kind switch
        {
            PluginSettingKind.Boolean => _pluginDraft.GetBool(d.Key, d.DefaultValue is bool b && b).ToString(),
            PluginSettingKind.Number => _pluginDraft.GetInt(d.Key, d.DefaultValue is int i ? i : 0).ToString(),
            _ => _pluginDraft.GetString(d.Key, d.DefaultValue?.ToString() ?? string.Empty)
        }));

    private string PlayerSettingsFingerprint() => string.Join("|",
        ComboValue(_positionBox, 2), ComboValue(_sizeBox, 1), ComboValue(_hwBox, 1), _ontopBox.Checked,
        PluginSettingsFingerprint(PluginRegistry.RendererPlugins.Select(p => p.Metadata)));

    private bool CollectSettings()
    {
        var settings = _app.Settings;
        var restartNeeded = false;
        foreach (var descriptor in PluginRegistry.RendererPlugins.Select(p => p.Metadata)
                     .Concat(PluginRegistry.ProtocolPlugins.Select(p => p.Metadata)).SelectMany(p => p.Settings))
        {
            if (_pluginDraft.Values.TryGetValue(descriptor.Key, out var value))
                settings.Set(descriptor.Key, value);
        }

        var name = _nameBox.Text.Trim();
        if (name != _sourceName)
        {
            settings.Set("DLNA_FriendlyName", name.Length > 0 ? name : settings.MachineFriendlyName);
            // A name the user typed wins over the "Macast(1234)" placeholder that
            // StartAsync generates when the configured port was already taken.
            settings.TempFriendlyName = null;
        }

        if ((int)_portBox.Value != _sourcePort)
        {
            restartNeeded = true;
        }

        settings.Set("ApplicationPort", (int)_portBox.Value);
        settings.Set("LogRetentionDays", (int)_logRetentionBox.Value);

        var renderer = SelectedPluginId(_rendererBox);
        if (!string.Equals(renderer, _sourceRenderer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(name, _sourceName, StringComparison.Ordinal))
        {
            restartNeeded = true;
        }

        settings.Set("Macast_Renderer", renderer);
        settings.Set("Macast_Protocol", PluginRegistry.DefaultProtocolId);

        settings.Set("PlayerPosition", ComboValue(_positionBox, 2));
        settings.Set("PlayerSize", ComboValue(_sizeBox, 1));
        settings.Set("PlayerHW", ComboValue(_hwBox, 1));
        settings.Set("PlayerOntop", _ontopBox.Checked ? 1 : 0);
        settings.Set("PlayerDefaultVolume", (int)_volumeBox.Value);

        settings.Save();
        Log.ConfigureRetention(settings.GetInt("LogRetentionDays", Log.DefaultRetentionDays));
        return restartNeeded;
    }

    private async Task ApplyAsync(bool closeAfter)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetBusy(true);
        try
        {
            CommitPluginEditors();
            var restartNeeded = CollectSettings();
            var autoStartOk = _autoStartBox.Checked == _sourceAutoStart || AutoStart.Set(_autoStartBox.Checked);
            if (autoStartOk) _sourceAutoStart = _autoStartBox.Checked;

            if (restartNeeded)
            {
                _statusLabel.Text = "正在重启投屏服务…";
                await _app.RestartAsync();
                _statusLabel.Text = _app.Running
                    ? $"已应用：{_app.Settings.FriendlyName} @ {_app.DeviceUrl()}"
                    : "已应用配置；投屏服务当前处于停止状态。";
            }
            else if (_sourcePlayerSettings != PlayerSettingsFingerprint())
            {
                await _app.ReloadRendererAsync();
                _statusLabel.Text = "已应用播放器设置。";
            }
            else
            {
                if (_sourceVolume != (int)_volumeBox.Value && _app.Running)
                    _app.Renderer.SetMediaVolume((int)_volumeBox.Value);
                _statusLabel.Text = "设置已保存，播放未中断。";
            }

            if (!autoStartOk)
            {
                _statusLabel.Text = "设置已保存，但开机自启写入失败（详见日志）。";
            }

            _sourcePlayerSettings = PlayerSettingsFingerprint();
            _sourceVolume = (int)_volumeBox.Value;
            _sourceName = _nameBox.Text.Trim();
            _sourcePort = _app.Settings.GetInt("ApplicationPort", 0);
            _sourceRenderer = SelectedPluginId(_rendererBox);

            if (closeAfter)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"apply settings failed: {ex}");
            MessageBox.Show(this, "应用设置失败：" + ex.Message, "Macast",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "应用失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _saveButton.Enabled = !busy;
        _applyButton.Enabled = !busy;
        _saveButton.Loading = busy;
        _applyButton.Loading = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void UpdateStatusText()
    {
        if (_busy)
        {
            return;
        }

        var renderer = _rendererBox.SelectedValue as MacastPlugin;
        _statusLabel.Text = $"{_app.Settings.FriendlyName} · {renderer?.Title ?? "-"}";
    }

    private void ShowLog()
    {
        var path = Log.LogFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "还没有日志文件。", "Macast", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var viewer = new Form
        {
            Text = "Macast 日志 - " + path,
            Size = new Size(900, 600),
            StartPosition = FormStartPosition.CenterParent
        };
        var box = new System.Windows.Forms.TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F)
        };

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            box.Text = reader.ReadToEnd();
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
        catch (Exception ex)
        {
            box.Text = "读取日志失败: " + ex.Message;
        }

        viewer.Controls.Add(box);
        viewer.ShowDialog(this);
    }

    private sealed record ValueItem(string Text, int Value)
    {
        public override string ToString() => Text;
    }
}