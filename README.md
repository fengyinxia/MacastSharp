# MacastSharp

Macast 的 C# / .NET 8 移植版。原始 Python 项目保持不变，本目录是一套独立的实现。

Windows 上是原生托盘程序 + 原生桌面设置界面，不再托管网页设置页；播放器与协议全部由 C# 插件实现。

## 当前状态

| 能力 | 状态 | 说明 |
|---|---|---|
| SSDP 发现 | ✅ | 多网卡 join、M-SEARCH 应答、NOTIFY alive/byebye、IP 变化重启 |
| 设备描述 / SCPD | ✅ | 复用原项目的 `Description.xml` 与三份 DLNA service XML |
| SOAP action | ✅ | SCPD 驱动分发；AVTransport / RenderingControl / ConnectionManager 常用 action 全部实现 |
| GENA 事件 | ✅ | SUBSCRIBE / UNSUBSCRIBE、初始全量事件、LastChange、SID/SEQ/TIMEOUT、失败重试与过期清理 |
| 插件框架 | ✅ | `IRendererPlugin` / `IProtocolPlugin` + 注册表 + 设置描述符 |
| 渲染器插件 | ✅ | MPV、PotPlayer、Live（三个全部 C# 实现） |
| 协议插件 | ✅ | DLNA、NVA（B 站“云视听小电视”投屏协议） |
| 桌面设置界面 | ✅ | WinForms 五个分页：常规 / 播放器 / 插件 / 高级 / 关于 |
| 网页设置页 | ❌ 已移除 | `GET /`、`/api`、`/assets` 全部删除；只保留 DLNA 必需路由 |
| 开机自启 | ✅ | HKCU\Software\Microsoft\Windows\CurrentVersion\Run |
| 多语言 | ❌ | 托盘与设置界面目前中文 |
| 自动更新 | ❌ | 未移植 |

## 插件

插件注册表在 `src/Macast.Core/Plugins/PluginRegistry.cs`，插件实现位于 `src/Macast.Core/Plugins/Builtin` 与 `src/Macast.Core/Renderer`、`src/Macast.Core/Protocols`。

| Id | 类型 | 平台 | 说明 |
|---|---|---|---|
| `MPV` | renderer | all | 内置播放器，mpv JSON IPC，状态精确同步 |
| `PotPlayer` | renderer | win32 | 查找注册表 / 默认安装路径，热键驱动暂停/静音/跳转，位置为估算 |
| `Live` | renderer | all | mpv + IPTV 播放列表，托盘菜单可直接换台 |
| `DLNA` | protocol | all | 标准 DLNA/UPnP |
| `NVA` | protocol | all | B 站 NVA（NirvanaControl）协议，自有 TCP 监听 + 二进制控制通道；带登录态解析 playurl，支持 DASH 1080P/高码率 |

选择插件写入配置键 `Macast_Renderer` / `Macast_Protocol`，与 Python 版共享同一个 `macast_setting.json`。
运行时可用 `MACAST_RENDERER` / `MACAST_PROTOCOL` 环境变量或 `--renderer` / `--protocol` 参数覆盖。

不在当前平台的插件仍然会出现在设置界面里，并明确标记“当前平台不可用”。

## 构建与运行

需要 .NET 8 SDK 或更高版本。Windows 直接构建整个 solution；macOS / Linux 目前只有 CLI（`Macast.Tray` 是 WinForms 项目）。

```powershell
cd MacastSharp
dotnet build MacastSharp.sln -c Release

# 命令行模式（没有 mpv 时用 --no-mpv 走协议模拟，方便联调）
dotnet run --project src/Macast.Cli -c Release -- --no-mpv

# 列出插件与可用状态
dotnet run --project src/Macast.Cli -c Release -- --plugins

# 检查某个视频在某个账号下能拿到什么清晰度（不带 key 即匿名）
dotnet run --project src/Macast.Cli -c Release -- --probe-playurl --aid 80433022 --cid 137649199 --qn 80

# Windows 托盘
dotnet run --project src/Macast.Tray -c Release

# 直接打开桌面设置（不启动投屏）
dotnet run --project src/Macast.Tray -c Release -- --settings
```

启动后：

- 设备描述：`http://IP:PORT/description.xml`
- SCPD：`http://IP:PORT/dlna/AVTransport.xml`
- 健康检查：`http://127.0.0.1:PORT/health`
- `GET /`、`/api`：404（网页设置页已删除）

### 桌面设置界面

托盘菜单“设置”或双击托盘图标打开，五个分页：

1. **常规**：设备名、HTTP 端口（0 = 自动）、投屏协议、开机自启、配置文件目录。
2. **播放器**：渲染器选择、窗口位置/大小、硬件解码、置顶、默认音量。
3. **插件**：渲染器与协议插件列表，每个插件的元信息、可用状态与参数（由插件自己声明）。
4. **高级**：屏蔽网卡 / 强制启用网卡、查看日志、重启服务、打开配置目录。
5. **关于**：版本、许可证、插件清单、mpv 版本、配置文件路径。

应用策略：

- 设备名、端口、网卡、插件、协议变化 → `MacastApp.RestartAsync()`（重建协议 + 渲染器并重新绑定端口）；
- 只有播放器窗口参数变化 → `MacastApp.ReloadRenderer()`（不打断网络）；
- 开机自启 → 立即写注册表。

### mpv

发布包已经内置 `bin/mpv.exe`（mpv 0.41.0 x64，含 `d3dcompiler_43.dll`），解压即可播放。

从源码运行时，按以下顺序查找：可执行文件目录的 `bin/mpv.exe` → 当前工作目录的 `bin/mpv.exe` → 系统 `PATH`。
没有 mpv 时不会崩溃：MPV 插件进入模拟模式，DLNA 协议与事件仍可测试。

## 自测

```powershell
dotnet run --project src/Macast.Cli -c Release -- --selftest
```

覆盖 18 项：description.xml、网页设置页已删除（`/`、`/api` 均 404、`/dlna` 仍可用）、插件注册表、设置读写往返、`/health`、SetAVTransportURI、Play、GetTransportInfo、GetPositionInfo、RenderingControl、ConnectionManager、GENA 初始事件与状态变化（SEQ 校验）、SUBSCRIBE/UNSUBSCRIBE、停止/重启生命周期、ReloadRenderer/RestartAsync、插件环境变量覆盖、NVA 协议（描述、SCPD、GetAppInfo、NVA 握手）、SSDP M-SEARCH。

自测使用临时配置目录（`MACAST_CONFIG_DIR`）和 1901 端口，不会污染正常配置，也不会和 Windows 的 SSDP 服务抢 1900。

真实播放验证（无测试视频时可用 mpv 的 lavfi 源）：

```powershell
# 在客户端侧发标准 SOAP 即可，例如 CurrentURI 用 av://lavfi:testsrc=duration=8
```

## 打包

```powershell
pwsh -File .\pack.ps1            # 发布 + 打包 zip + 打印 SHA256
pwsh -File .\pack.ps1 -SkipZip   # 只发布
```

`pack.ps1` 会校验 `bin/mpv.exe`、`Assets/xml/Description.xml` 等必需文件，缺文件直接失败，避免打出半成品包。

## 目录

```
MacastSharp/
├── MacastSharp.sln
├── pack.ps1
└── src/
    ├── Macast.Core/
    │   ├── MacastApp.cs                # 生命周期编排（插件解析 / 重启 / 重载）
    │   ├── Plugins/                    # 插件契约与注册表
    │   │   ├── MacastPlugin.cs
    │   │   ├── IRendererPlugin.cs
    │   │   ├── IProtocolPlugin.cs
    │   │   ├── PluginSettingDescriptor.cs
    │   │   ├── PluginRegistry.cs
    │   │   └── Builtin/                # 每个插件的元数据与设置声明
    │   ├── Renderer/
    │   │   ├── IRenderer.cs
    │   │   ├── MpvRenderer.cs          # mpv 进程 + JSON IPC
    │   │   ├── PotPlayerRenderer.cs
    │   │   ├── LiveRenderer.cs
    │   │   └── RendererChannel.cs      # 直播频道模型
    │   ├── Protocols/
    │   │   ├── DlnaProtocolService.cs  # DLNA 协议服务（Kestrel）
    │   │   └── Nva/                    # NVA 协议（自有 TCP 监听 + 二进制控制通道）
    │   ├── Settings/AppSettings.cs
    │   ├── Net/NetworkUtils.cs
    │   ├── Ssdp/SsdpServer.cs
    │   ├── Upnp/                        # SCPD / SOAP / GENA / 设备描述
    │   ├── Server/MacastHttpServer.cs   # 只保留 DLNA 必需路由
    │   └── Assets/                      # XML / 图标 / version.txt
    ├── Macast.Cli/
    │   ├── Program.cs
    │   ├── PluginList.cs
    │   ├── SelfTest.cs
    │   └── EventProbe.cs
    └── Macast.Tray/
        ├── Program.cs                   # 托盘 / --settings
        ├── TrayApp.cs                   # 托盘菜单、直播频道
        ├── SettingsForm.cs              # 桌面设置界面
        ├── TrayIcon.cs
        └── AutoStart.cs
```

## 与 Python 版的关键对应

| Python | C# |
|---|---|
| `importlib` 插件（renderer.py / protocol.py） | `IRendererPlugin` / `IProtocolPlugin` + `PluginRegistry` |
| 网页设置（Vue + Element UI + `/api`） | WinForms `SettingsForm`，直接调用 `MacastApp` |
| `lxml.etree` / `QName` | `XDocument` / `XNamespace` |
| CherryPy + `MethodDispatcher` | Kestrel Minimal API（DLNA） / 裸 TCP + 手写请求行解析（NVA） |
| `netifaces` | `NetworkInterface` |
| `_winapi.CreateFile` + `PipeConnection` | `NamedPipeClientStream` |
| Unix `AF_UNIX` | `UnixDomainSocketEndPoint` |
| `pystray` / `rumps` | Windows `NotifyIcon`；macOS/Linux 托盘待补 |
| `pywin32`（PotPlayer 热键） | `SendInput` / `EnumWindows` P/Invoke |
| `appdirs` | `ApplicationData` + `MACAST_CONFIG_DIR` 覆盖 |

## 配置兼容

默认读取和写入与原版相同的文件：

`%APPDATA%\Macast\macast_setting.json`

键名保持 `USN`、`ApplicationPort`、`DLNA_FriendlyName`、`Macast_Renderer`、`Macast_Protocol`、`Blocked_Interfaces`、`Additional_Interfaces`、`PlayerSize`、`PlayerPosition`、`PlayerHW`、`PlayerOntop`、`PlayerDefaultVolume`，以及插件自己的 `Potplayer_Path`、`Live_PlaylistUrl`。这样从 Python 版切换过来时，设备不会被识别成一台新设备。

测试或绿色运行可以设置：

```powershell
$env:MACAST_CONFIG_DIR = "$env:TEMP\macast-test"
```

## 已知限制

- PotPlayer 没有 IPC，暂停/静音/跳转靠全局热键，播放位置是估算值；找不到 exe 时会在日志里报错并停止。
- NVA 协议面向 B 站客户端；没有真实客户端时只能验证控制通道与 SOAP 动作。未登录时 `GetAppInfo` 返回 `CurrentSignedIn=0`，登录走 `LoginWithCode`（access_key 存到配置 `NVA_AccessKey`），AppKey/Signature 为占位值。
- NVA 清晰度：手机投屏时会把账号 `accessKey` 放进 `nva_ext`，接收端用它请求 `x/tv/playurl`；匿名最高 720P，登录 1080P，大会员 1080P 高码率/4K。请求必须使用官方 TV 身份（`platform/mobi_app=android_tv_yst`、`build=106400`），用 `platform=ios` 会被限到 720P。mpv 系渲染器请求 DASH（`fnval=16`），并通过 `loadfile ... audio-file=<audio>` 一次性挂上音轨。协议细节见 [docs/NVA-casting.md](docs/NVA-casting.md)。
- macOS/Linux 目前只有 CLI；托盘需要补 Avalonia/AppKit/AppIndicator 宿主。
- 内置的 mpv 仅覆盖 Windows x64；macOS / Linux 需要自备对应平台的 mpv。
- 内置 mpv 来自 shinchiro/mpv-winbuild-cmake（2026-10-21 构建），遵循其上游 GPLv2+ 许可；详见 `bin/mpv-version.txt`。

## License

与原项目一致，GPL-3.0。原始版权归 xfangfang 及 Macast 贡献者所有。
