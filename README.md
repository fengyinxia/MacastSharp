# MacastSharp

[Macast](https://github.com/xfangfang/Macast) 的 C# / .NET 8 移植版，独立实现，Python 原项目不受影响。

Windows 上是原生托盘程序 + WinForms 桌面设置界面（不含网页设置页），播放器与协议全部为 C# 插件。

## 能力

| 能力 | 状态 |
|---|---|
| SSDP 发现（多网卡、M-SEARCH、NOTIFY、IP 变化重启） | ✅ |
| DLNA：设备描述 / SCPD / SOAP / GENA 事件 | ✅ |
| NVA（B 站「云视听小电视」投屏） | ✅ |
| 渲染器 MPV / PotPlayer / Live | ✅ |
| 桌面设置：常规 / 播放器 / 插件 / 高级 / 关于 | ✅ |
| 开机自启（HKCU Run） | ✅ |
| 网页设置页 | ❌ 已移除，仅留 DLNA 必需路由与 `/health` |
| 多语言、自动更新 | ❌ 未移植 |

## 插件

| Id | 类型 | 平台 | 说明 |
|---|---|---|---|
| `MPV` | renderer | all | mpv JSON IPC，状态精确同步 |
| `PotPlayer` | renderer | win32 | 注册表 / 默认路径查找，热键驱动控制，位置为估算 |
| `Live` | renderer | all | mpv + IPTV 列表，托盘菜单换台 |
| `DLNA` | protocol | all | 标准 DLNA/UPnP |
| `NVA` | protocol | all | 自有 TCP + 二进制控制通道，登录态解析 playurl，DASH 1080P |

选择结果写入 `Macast_Renderer` / `Macast_Protocol`，可用 `MACAST_RENDERER` / `MACAST_PROTOCOL` 环境变量或 `--renderer` / `--protocol` 覆盖。非当前平台的插件仍在设置界面显示，并标记不可用。

## 构建与运行

需要 .NET 8 SDK+。`Macast.Tray` 是 WinForms 项目，仅 Windows 可构建；macOS / Linux 目前只有 CLI。

```powershell
dotnet build MacastSharp.sln -c Release

dotnet run --project src/Macast.Cli  -c Release -- --no-mpv    # 无 mpv 时走协议模拟
dotnet run --project src/Macast.Cli  -c Release -- --plugins   # 插件与可用状态
dotnet run --project src/Macast.Tray -c Release                # Windows 托盘
dotnet run --project src/Macast.Tray -c Release -- --settings   # 只开设置界面

# 查某个视频能拿到的清晰度（不带 key 即匿名）
dotnet run --project src/Macast.Cli -c Release -- --probe-playurl --aid 80433022 --cid 137649199 --qn 80
```

启动后 `/description.xml`、`/dlna/AVTransport.xml`、`/health` 可用，`/`、`/api` 返回 404。

mpv 查找顺序：可执行文件目录的 `bin/mpv.exe` → 工作目录 `bin/mpv.exe` → `PATH`。发布包内置 mpv 0.41.0 x64；缺 mpv 不会崩溃，MPV 插件转入模拟模式，协议与事件仍可测试。

## 自测与打包

```powershell
dotnet run --project src/Macast.Cli -c Release -- --selftest   # 18 项，临时配置 + 1901 端口
pwsh -File .\pack.ps1                                          # 发布 + zip + SHA256
pwsh -File .\pack.ps1 -SkipZip                                 # 只发布
```

自测覆盖 description / SCPD、SOAP 动作、GENA 初始事件与 SEQ 校验、插件注册表、设置读写、重启与重载、NVA 握手、SSDP。`pack.ps1` 会校验 `bin/mpv.exe`、`Assets/xml` 等必需文件，缺件直接失败。

## 配置兼容

读写原版同一份 `%APPDATA%\Macast\macast_setting.json`，键名保持一致（`USN`、`ApplicationPort`、`DLNA_FriendlyName`、`Macast_Renderer`、`Macast_Protocol`、`Player*` 以及插件自有键），从 Python 版切换不会被识别成新设备。

测试或绿色运行可设置 `MACAST_CONFIG_DIR`，例如 `$env:MACAST_CONFIG_DIR = "$env:TEMP\macast-test"`。

## 目录

```
src/Macast.Core/   MacastApp、Plugins、Renderer、Protocols（DLNA / Nva）、Upnp、Ssdp、Server、Settings、Assets
src/Macast.Cli/    Program、PluginList、SelfTest、EventProbe
src/Macast.Tray/   Program、TrayApp、SettingsForm、TrayIcon、AutoStart
```

## 已知限制

- PotPlayer 无 IPC，控制靠全局热键，播放位置为估算值；找不到 exe 时记录错误并停止。
- NVA 面向 B 站客户端：未登录时 `GetAppInfo` 返回 `CurrentSignedIn=0`，登录走 `LoginWithCode`（access_key 存 `NVA_AccessKey`），AppKey / Signature 为占位值。清晰度匿名 720P、登录 1080P、大会员高码率 / 4K，请求须使用官方 TV 身份，细节见 [docs/NVA-casting.md](docs/NVA-casting.md)。
- macOS / Linux 仅 CLI，托盘需补 Avalonia / AppKit / AppIndicator 宿主；内置 mpv 仅 Windows x64，其他平台需自备。
- 内置 mpv 来自 shinchiro/mpv-winbuild-cmake（2026-10-21 构建，GPLv2+），详见 `bin/mpv-version.txt`。

## License

GPL-3.0，与原项目一致。原始版权归 xfangfang 及 Macast 贡献者所有。
