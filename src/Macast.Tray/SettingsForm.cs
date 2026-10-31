using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Macast.Core;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Tray;

/// <summary>
/// The desktop replacement for the old Vue settings page. Everything that used to
/// be a POST to /api is a normal WinForms control here, and applying the changes
/// calls MacastApp.RestartAsync / ReloadRenderer directly instead of reloading a
/// browser tab.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly MacastApp _app;
    private readonly TextBox _nameBox = new();
    private readonly NumericUpDown _portBox = new();
    private readonly CheckBox _autoStartBox = new();
    private readonly ComboBox _protocolBox = new();
    private readonly ComboBox _rendererBox = new();
    private readonly ComboBox _positionBox = new();
    private readonly ComboBox _sizeBox = new();
    private readonly ComboBox _hwBox = new();
    private readonly CheckBox _ontopBox = new();
    private readonly NumericUpDown _volumeBox = new();
    private readonly CheckedListBox _blockedList = new();
    private readonly CheckedListBox _additionalList = new();
    private readonly ListBox _pluginList = new();
    private readonly Panel _pluginDetail = new();
    private readonly Label _statusLabel = new();
    private readonly Button _saveButton = new();
    private readonly Button _applyButton = new();
    private readonly Dictionary<string, Control> _pluginEditors = new(StringComparer.Ordinal);
    private SplitContainer? _pluginSplit;

    private MacastPlugin? _selectedPlugin;
    private string _sourceName = string.Empty;
    private string _sourceRenderer = string.Empty;
    private string _sourceProtocol = string.Empty;
    private int _sourcePort;
    private string[] _sourceBlocked = Array.Empty<string>();
    private string[] _sourceAdditional = Array.Empty<string>();
    private bool _busy;

    public SettingsForm(MacastApp app)
    {
        _app = app;
        BuildUi();
        LoadSettings();
    }

    private void BuildUi()
    {
        Text = "Macast 设置";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 640);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = TrayIcon.Load();

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildPlayerTab());
        tabs.TabPages.Add(BuildPluginTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        tabs.TabPages.Add(BuildAboutTab());

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 10, 12, 10) };
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = SystemColors.GrayText;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };

        _saveButton.Text = "保存";
        _saveButton.Width = 88;
        _saveButton.Click += async (_, _) => await ApplyAsync(closeAfter: true);

        _applyButton.Text = "应用";
        _applyButton.Width = 88;
        _applyButton.Click += async (_, _) => await ApplyAsync(closeAfter: false);

        var cancel = new Button { Text = "取消", Width = 88, DialogResult = DialogResult.Cancel };

        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_applyButton);
        buttons.Controls.Add(cancel);

        bottom.Controls.Add(_statusLabel);
        bottom.Controls.Add(buttons);

        AcceptButton = _saveButton;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(bottom);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规") { Padding = new Padding(16) };
        var layout = CreateLayout(3);

        AddRow(layout, "设备名", _nameBox, "DLNA 中显示的设备名称，留空则使用 Macast(计算机名)。");
        AddRow(layout, "HTTP 端口", _portBox, "0 表示自动选择可用端口；修改后需要重启投屏服务。");
        AddRow(layout, "投屏协议", _protocolBox, "NVA 会让本机同时以“云视听小电视”的身份出现在 B 站客户端里。");
        AddRow(layout, "开机自启", _autoStartBox, "写入 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run。");

        var configRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true };
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var configBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = AppSettings.ConfigDir };
        var openConfig = new Button { Text = "打开目录", Dock = DockStyle.Fill };
        openConfig.Click += (_, _) => OpenFolder(AppSettings.ConfigDir);
        configRow.Controls.Add(configBox, 0, 0);
        configRow.Controls.Add(openConfig, 1, 0);
        layout.Controls.Add(new Label { Text = "配置文件", AutoSize = true, Anchor = AnchorStyles.Left });
        layout.Controls.Add(configRow);

        _portBox.Minimum = 0;
        _portBox.Maximum = 65535;
        _nameBox.Dock = DockStyle.Fill;
        _protocolBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _protocolBox.Dock = DockStyle.Fill;
        _protocolBox.SelectedIndexChanged += (_, _) => UpdateStatusText();
        _autoStartBox.Text = "登录 Windows 后自动启动 Macast";

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildPlayerTab()
    {
        var page = new TabPage("播放器") { Padding = new Padding(16) };
        var layout = CreateLayout(3);

        AddRow(layout, "渲染器", _rendererBox, "更换渲染器会重启投屏服务；窗口参数只重载播放器。");
        AddRow(layout, "窗口位置", _positionBox, "仅 mpv 系渲染器生效。");
        AddRow(layout, "窗口大小", _sizeBox, "仅 mpv 系渲染器生效。");
        AddRow(layout, "硬件解码", _hwBox, "禁用可解决部分显卡的绿屏/黑屏问题。");
        AddRow(layout, "窗口置顶", _ontopBox, "让播放窗口始终显示在最前面。");
        AddRow(layout, "默认音量", _volumeBox, "投屏开始时应用的音量（0-100）。");

        _rendererBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _rendererBox.Dock = DockStyle.Fill;
        _rendererBox.SelectedIndexChanged += (_, _) => UpdateStatusText();

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
                     ("禁用", 0), ("启用", 1), ("强制独占 GPU", 2)
                 })
        {
            _hwBox.Items.Add(new ValueItem(text, value));
        }

        _positionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _sizeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _hwBox.DropDownStyle = ComboBoxStyle.DropDownList;

        _ontopBox.Text = "播放时窗口置顶";
        _volumeBox.Minimum = 0;
        _volumeBox.Maximum = 100;
        _volumeBox.Increment = 5;

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildPluginTab()
    {
        var page = new TabPage("插件") { Padding = new Padding(16) };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        _pluginSplit = split;

        _pluginList.Dock = DockStyle.Fill;
        _pluginList.IntegralHeight = false;
        _pluginList.SelectedIndexChanged += (_, _) =>
        {
            CommitPluginEditors();
            ShowPluginDetail(_pluginList.SelectedItem as MacastPlugin);
        };

        _pluginDetail.Dock = DockStyle.Fill;
        _pluginDetail.AutoScroll = true;

        split.Panel1.Controls.Add(_pluginList);
        split.Panel2.Controls.Add(_pluginDetail);
        page.Controls.Add(split);
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage("高级") { Padding = new Padding(16) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _blockedList.Dock = DockStyle.Fill;
        _blockedList.CheckOnClick = true;
        _additionalList.Dock = DockStyle.Fill;
        _additionalList.CheckOnClick = true;

        layout.Controls.Add(new Label { Text = "屏蔽的网卡（不参与投屏发现）", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "强制启用的网卡（默认只启用带网关的网卡）", AutoSize = true }, 1, 0);
        layout.Controls.Add(_blockedList, 0, 1);
        layout.Controls.Add(_additionalList, 1, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };

        var log = new Button { Text = "查看日志", AutoSize = true };
        log.Click += (_, _) => ShowLog();
        buttons.Controls.Add(log);

        var restart = new Button { Text = "重启投屏服务", AutoSize = true };
        restart.Click += async (_, _) => await RestartServiceAsync();
        buttons.Controls.Add(restart);

        var folder = new Button { Text = "打开配置目录", AutoSize = true };
        folder.Click += (_, _) => OpenFolder(AppSettings.ConfigDir);
        buttons.Controls.Add(folder);

        layout.Controls.Add(new Label { Text = "维护", AutoSize = true }, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("关于") { Padding = new Padding(16) };
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

        var plugins = string.Join("、", PluginRegistry.RendererPlugins.Select(p => p.Metadata.Title)
            .Concat(PluginRegistry.ProtocolPlugins.Select(p => p.Metadata.Title)));
        layout.Controls.Add(new Label
        {
            Text = "许可证：GPL-3.0（与上游一致）\r\n" +
                   "插件：" + plugins,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, 12)
        });

        layout.Controls.Add(new Label
        {
            Text = "mpv：" + DescribeMpv(),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, 12)
        });

        layout.Controls.Add(new Label
        {
            Text = "配置文件：" + AppSettings.SettingPath,
            AutoSize = true,
            MaximumSize = new Size(width, 0)
        });

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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control, string? help = null)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        control.Dock = DockStyle.Fill;
        row.Controls.Add(control);
        if (!string.IsNullOrWhiteSpace(help))
        {
            row.Controls.Add(new Label
            {
                Text = help,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0)
            });
        }

        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0)
        });
        layout.Controls.Add(row);
    }

    private void LoadSettings()
    {
        var settings = _app.Settings;

        _nameBox.Text = settings.GetString("DLNA_FriendlyName", settings.MachineFriendlyName);
        _portBox.Value = Math.Clamp(settings.GetInt("ApplicationPort", 0), 0, 65535);

        _autoStartBox.Checked = AutoStart.IsEnabled();

        _rendererBox.Items.Clear();
        _protocolBox.Items.Clear();
        _pluginList.Items.Clear();

        foreach (var plugin in PluginRegistry.RendererPlugins)
        {
            _rendererBox.Items.Add(plugin.Metadata);
            _pluginList.Items.Add(plugin.Metadata);
        }

        foreach (var plugin in PluginRegistry.ProtocolPlugins)
        {
            _protocolBox.Items.Add(plugin.Metadata);
            _pluginList.Items.Add(plugin.Metadata);
        }

        SelectByKey(_rendererBox, settings.GetString("Macast_Renderer", PluginRegistry.DefaultRendererId),
            _app.RendererPlugin.Metadata.Id);
        SelectByKey(_protocolBox, settings.GetString("Macast_Protocol", PluginRegistry.DefaultProtocolId),
            _app.ProtocolPlugin.Metadata.Id);

        SelectCombo(_positionBox, settings.GetInt("PlayerPosition", 2));
        SelectCombo(_sizeBox, settings.GetInt("PlayerSize", 1));
        SelectCombo(_hwBox, settings.GetInt("PlayerHW", 1));
        _ontopBox.Checked = settings.GetInt("PlayerOntop", 1) != 0;
        _volumeBox.Value = Math.Clamp(settings.GetInt("PlayerDefaultVolume", 100), 0, 100);

        var blocked = settings.GetStringList("Blocked_Interfaces");
        var additional = settings.GetStringList("Additional_Interfaces");
        _blockedList.Items.Clear();
        _additionalList.Items.Clear();
        foreach (var nic in EnumerateInterfaces())
        {
            _blockedList.Items.Add(nic, blocked.Contains(nic.Name, StringComparer.OrdinalIgnoreCase));
            _additionalList.Items.Add(nic, additional.Contains(nic.Name, StringComparer.OrdinalIgnoreCase));
        }

        _sourceName = settings.FriendlyName;
        _sourcePort = settings.GetInt("ApplicationPort", 0);
        _sourceRenderer = SelectedPluginId(_rendererBox);
        _sourceProtocol = SelectedPluginId(_protocolBox);
        _sourceBlocked = blocked.ToArray();
        _sourceAdditional = additional.ToArray();

        if (_pluginList.Items.Count > 0)
        {
            _pluginList.SelectedIndex = 0;
        }

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
                box.SelectedItem = item;
                return;
            }
        }

        foreach (var item in box.Items)
        {
            if (item is MacastPlugin metadata &&
                string.Equals(metadata.Id, fallbackKey, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
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
                box.SelectedItem = item;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static int ComboValue(ComboBox box, int fallback) =>
        box.SelectedItem is ValueItem item ? item.Value : fallback;

    private static string SelectedPluginId(ComboBox box) =>
        box.SelectedItem is MacastPlugin metadata ? metadata.Id : string.Empty;

    private void ShowPluginDetail(MacastPlugin? plugin)
    {
        _pluginDetail.Controls.Clear();
        _pluginEditors.Clear();
        _selectedPlugin = plugin;
        if (plugin is null)
        {
            return;
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new Label
        {
            Text = $"{plugin.Title}  v{plugin.Version}",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            AutoSize = true
        };
        layout.Controls.Add(header);
        layout.SetColumnSpan(header, 2);

        var meta = new Label
        {
            Text = $"{plugin.Id} · {plugin.Author} · " +
                   $"{(plugin.Kind == PluginKind.Renderer ? "渲染器" : "协议")} · " +
                   $"{plugin.StatusText(_app.Settings)}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        layout.Controls.Add(meta);
        layout.SetColumnSpan(meta, 2);

        if (!string.IsNullOrWhiteSpace(plugin.Description))
        {
            var description = new Label
            {
                Text = plugin.Description,
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Margin = new Padding(0, 6, 0, 10)
            };
            layout.Controls.Add(description);
            layout.SetColumnSpan(description, 2);
        }

        if (plugin.Settings.Count == 0)
        {
            var empty = new Label
            {
                Text = "该插件没有可配置项。",
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            layout.Controls.Add(empty);
            layout.SetColumnSpan(empty, 2);
        }
        else
        {
            foreach (var descriptor in plugin.Settings)
            {
                layout.Controls.Add(new Label
                {
                    Text = descriptor.Label,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 6, 8, 6)
                });

                var editor = CreateEditor(descriptor);
                _pluginEditors[descriptor.Key] = editor;
                var host = new TableLayoutPanel
                {
                    ColumnCount = 1,
                    AutoSize = true,
                    Dock = DockStyle.Fill
                };
                editor.Dock = DockStyle.Fill;
                host.Controls.Add(editor);
                if (!string.IsNullOrWhiteSpace(descriptor.Help))
                {
                    host.Controls.Add(new Label
                    {
                        Text = descriptor.Help,
                        AutoSize = true,
                        ForeColor = SystemColors.GrayText,
                        Margin = new Padding(0, 2, 0, 0)
                    });
                }

                layout.Controls.Add(host);
            }
        }

        _pluginDetail.Controls.Add(layout);
    }

    private Control CreateEditor(PluginSettingDescriptor descriptor)
    {
        var settings = _app.Settings;
        switch (descriptor.Kind)
        {
            case PluginSettingKind.Boolean:
            {
                var box = new CheckBox
                {
                    Text = "启用",
                    Checked = settings.GetBool(descriptor.Key,
                        descriptor.DefaultValue is bool b && b),
                    AutoSize = true
                };
                return box;
            }
            case PluginSettingKind.Number:
            {
                var box = new NumericUpDown
                {
                    Minimum = -1_000_000,
                    Maximum = 1_000_000,
                    Value = Math.Clamp(settings.GetInt(descriptor.Key,
                        descriptor.DefaultValue is int i ? i : 0), -1_000_000, 1_000_000)
                };
                return box;
            }
            case PluginSettingKind.Choice:
            {
                var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                var current = settings.GetString(descriptor.Key, descriptor.DefaultValue?.ToString() ?? string.Empty);
                foreach (var choice in descriptor.Choices ?? Array.Empty<PluginSettingChoice>())
                {
                    box.Items.Add(choice);
                    if (string.Equals(choice.Value, current, StringComparison.OrdinalIgnoreCase))
                    {
                        box.SelectedItem = choice;
                    }
                }

                if (box.SelectedItem is null && box.Items.Count > 0)
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
                    Dock = DockStyle.Fill
                };
                var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
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
                    Text = settings.GetString(descriptor.Key, descriptor.DefaultValue?.ToString() ?? string.Empty)
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
                    _app.Settings.Set(descriptor.Key, check.Checked);
                    break;
                case NumericUpDown number:
                    _app.Settings.Set(descriptor.Key, (int)number.Value);
                    break;
                case ComboBox combo when combo.SelectedItem is PluginSettingChoice choice:
                    _app.Settings.Set(descriptor.Key, choice.Value);
                    break;
                case TextBox text:
                    _app.Settings.Set(descriptor.Key, text.Text.Trim());
                    break;
                case TableLayoutPanel pathHost when pathHost.Tag is TextBox pathBox:
                    _app.Settings.Set(descriptor.Key, pathBox.Text.Trim());
                    break;
                case TableLayoutPanel choiceHost when choiceHost.Tag is ComboBox choiceBox &&
                                                        choiceBox.SelectedItem is PluginSettingChoice picked:
                    _app.Settings.Set(descriptor.Key, picked.Value);
                    break;
            }
        }
    }

    private bool CollectSettings()
    {
        CommitPluginEditors();
        var settings = _app.Settings;
        var restartNeeded = false;

        var name = _nameBox.Text.Trim();
        if (name.Length > 0)
        {
            settings.Set("DLNA_FriendlyName", name);
            // A name the user typed wins over the "Macast(1234)" placeholder that
            // StartAsync generates when the configured port was already taken.
            settings.TempFriendlyName = null;
        }

        if ((int)_portBox.Value != _sourcePort)
        {
            restartNeeded = true;
        }

        settings.Set("ApplicationPort", (int)_portBox.Value);

        var renderer = SelectedPluginId(_rendererBox);
        var protocol = SelectedPluginId(_protocolBox);
        if (!string.Equals(renderer, _sourceRenderer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(protocol, _sourceProtocol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(name, _sourceName, StringComparison.Ordinal))
        {
            restartNeeded = true;
        }

        settings.Set("Macast_Renderer", renderer);
        settings.Set("Macast_Protocol", protocol);

        settings.Set("PlayerPosition", ComboValue(_positionBox, 2));
        settings.Set("PlayerSize", ComboValue(_sizeBox, 1));
        settings.Set("PlayerHW", ComboValue(_hwBox, 1));
        settings.Set("PlayerOntop", _ontopBox.Checked ? 1 : 0);
        settings.Set("PlayerDefaultVolume", (int)_volumeBox.Value);

        var blocked = _blockedList.CheckedItems.Cast<NicItem>()
            .Select(item => item.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var additional = _additionalList.CheckedItems.Cast<NicItem>()
            .Select(item => item.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!blocked.SequenceEqual(_sourceBlocked, StringComparer.OrdinalIgnoreCase) ||
            !additional.SequenceEqual(_sourceAdditional, StringComparer.OrdinalIgnoreCase))
        {
            restartNeeded = true;
        }

        settings.Set("Blocked_Interfaces", blocked);
        settings.Set("Additional_Interfaces", additional);

        settings.Save();
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
            var restartNeeded = CollectSettings();
            AutoStart.Set(_autoStartBox.Checked);

            if (restartNeeded)
            {
                _statusLabel.Text = "正在重启投屏服务…";
                await _app.RestartAsync();
                _statusLabel.Text = _app.Running
                    ? $"已应用：{_app.Settings.FriendlyName} @ {_app.DeviceUrl()}"
                    : "已应用配置；投屏服务当前处于停止状态。";
            }
            else
            {
                _app.ReloadRenderer();
                _statusLabel.Text = "已应用播放器设置。";
            }

            _sourceName = _app.Settings.FriendlyName;
            _sourcePort = _app.Settings.GetInt("ApplicationPort", 0);
            _sourceRenderer = SelectedPluginId(_rendererBox);
            _sourceProtocol = SelectedPluginId(_protocolBox);
            _sourceBlocked = _app.Settings.GetStringList("Blocked_Interfaces").ToArray();
            _sourceAdditional = _app.Settings.GetStringList("Additional_Interfaces").ToArray();

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
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void UpdateStatusText()
    {
        if (_busy)
        {
            return;
        }

        var renderer = _rendererBox.SelectedItem as MacastPlugin;
        var protocol = _protocolBox.SelectedItem as MacastPlugin;
        _statusLabel.Text = $"{_app.Settings.FriendlyName} · {renderer?.Title ?? "-"} · {protocol?.Title ?? "-"}";
    }

    private async Task RestartServiceAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetBusy(true);
        try
        {
            _statusLabel.Text = "正在重启投屏服务…";
            await _app.RestartAsync();
            _statusLabel.Text = _app.Running ? "服务已重启：" + _app.DeviceUrl() : "服务已停止。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重启失败：" + ex.Message, "Macast", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
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
        var box = new TextBox
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

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"open folder failed: {ex.Message}");
        }
    }

    private static string DescribeMpv()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "bin", OperatingSystem.IsWindows() ? "mpv.exe" : "mpv");
        if (!File.Exists(exe))
        {
            return "未找到内置 mpv（可使用系统 PATH 中的 mpv）";
        }

        var versionFile = Path.Combine(AppContext.BaseDirectory, "bin", "mpv-version.txt");
        if (File.Exists(versionFile))
        {
            try
            {
                return File.ReadAllText(versionFile).Trim();
            }
            catch
            {
                // fall through
            }
        }

        return exe;
    }

    private static IEnumerable<NicItem> EnumerateInterfaces()
    {
        var list = new List<NicItem>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            string address = string.Empty;
            try
            {
                address = string.Join(",", nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()));
            }
            catch
            {
                // ignore inaccessible adapters
            }

            list.Add(new NicItem(nic.Name, string.IsNullOrEmpty(address) ? nic.Name : $"{nic.Name} ({address})"));
        }

        return list;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_pluginSplit is not null)
        {
            try
            {
                _pluginSplit.SplitterDistance = 260;
            }
            catch (InvalidOperationException)
            {
                // window too narrow during first layout; the default distance is fine
            }
        }
    }

    private sealed record NicItem(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ValueItem(string Text, int Value)
    {
        public override string ToString() => Text;
    }
}