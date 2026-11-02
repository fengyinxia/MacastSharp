# MacastSharp

[Macast](https://github.com/xfangfang/Macast) 的 C# / .NET 8 移植版，独立实现，Python 原项目不受影响。

Windows 上是原生托盘程序 + WinForms / AntdUI 桌面设置界面（不含网页设置页），播放器与协议全部为 C# 插件。发布包只包含 GUI，不内置播放器或 .NET 运行时；CLI 源码保留用于开发、诊断与回归。

## 使用发布包

1. 安装 **.NET 8 Desktop Runtime x64**（不是仅安装普通 .NET Runtime）；已有对应 SDK / Desktop Runtime 的机器通常无需重复安装。下载：https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
2. 自备 MPV 或 PotPlayer。在「设置 → 播放器」中选择播放器并浏览设置程序路径，应用后生效。MPV 路径留空时可通过 `PATH` 或程序目录的 `bin/mpv.exe` 自动查找。切换下拉框只编辑草稿，不立即切换播放器。
3. 解压 `dist/MacastSharp-gui-win-x64.zip`，运行 `macast-gui.exe`。DLNA 与 B站 NVA 同时接收，自动识别，无需切换协议。

**无需 ASP.NET Core Runtime，无需管理员权限或 HTTP URL ACL 配置。** DLNA / NVA 共用基于 .NET Socket 的轻量 HTTP 传输。只安装普通 .NET Runtime 不包含 WinForms，不能代替 Desktop Runtime。

设置窗口采用 AntdUI 2.4.18：左侧导航、圆角输入框、下拉选择与开关，跟随 Windows 应用深浅色，保留现有配置和应用逻辑；不显示设置帮助长文或协议选项，不使用 WebView。常规页包含设备名、HTTP 端口、开机自启和日志保留时间；网卡按默认规则自动选择，不再提供屏蔽或强制启用选项，旧配置中的网卡列表不再生效；播放器页合并程序路径与播放选项；关于页可查看日志。重启投屏服务和打开配置目录保留在托盘右键菜单中。

## 能力

| 能力 | 状态 |
|---|---|
| SSDP 发现（多网卡、M-SEARCH、NOTIFY、IP 变化重启） | ✅ |
| DLNA：设备描述 / SCPD / SOAP / GENA 事件 | ✅ |
| NVA（B 站「云视听小电视」投屏） | ✅ |
| 渲染器 MPV / PotPlayer | ✅ |
| 桌面设置：常规 / 播放器 / 关于 | ✅ |
| 开机自启（HKCU Run） | ✅ |
| 网页设置页 | ❌ 已移除，仅留 DLNA 必需路由与 `/health` |
| 多语言、自动更新 | ❌ 未移植 |

## 插件

| Id | 类型 | 平台 | 说明 |
|---|---|---|---|
| `MPV` | renderer | all | mpv JSON IPC，状态精确同步 |
| `PotPlayer` | renderer | win32 | 独立实例、消息控制、窗口位置/大小/置顶与真实进度 |
| `Auto` | protocol | all | 单设备、单端口同时支持 DLNA / NVA，B站登录态解析 playurl、DASH 1080P |

播放器选择写入 `Macast_Renderer`，可用 `MACAST_RENDERER` 或 `--renderer` 覆盖。协议固定为 `Auto`，设置中不再二选一；旧配置、环境变量或 `--protocol` 中的 `DLNA` / `NVA` 均兼容到自动模式。非当前平台的播放器仍在设置界面显示，并标记不可用。

发现名称统一为 `我的小电视 · <设备名>`（已含标记则不重复），以兼容 B站客户端识别；其他 DLNA 客户端也会看到该名称。共用一个 UUID 和播放器，新投屏接管旧内容，不重启服务。普通 DLNA 接管时会清理旧 NVA 元数据、连播和清晰度上下文，避免旧会话抢回播放。

## 构建与运行

开发需要 .NET 8 SDK+。`Macast.Tray` 是 WinForms 项目，仅 Windows 可构建；macOS / Linux 可从源码运行开发用 CLI，发布包仅面向 Windows GUI。

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

MPV 优先使用播放器页配置的 `Mpv_Path`，明确指定的路径不存在时显示未找到，不回退到其他播放器；留空时自动查找程序目录 `bin`、`PATH` 和工作目录 `bin`。发布包不内置 mpv；缺 mpv 不会崩溃，MPV 插件转入模拟模式，但不会实际播放视频。使用 PotPlayer 时在播放器页选择并设置路径；两个播放器均提供窗口位置、大小和置顶，仅 MPV 显示硬件解码选项。Windows 已移除无对应实现的“强制独占 GPU”选项，旧值按普通硬解启用显示。Live/IPTV 播放器及托盘频道菜单已移除，旧 Live 配置回退到 MPV。

## 自测与打包

```powershell
dotnet run --project src/Macast.Cli -c Release -- --selftest   # 协议与边界回归，临时配置 + 1901 端口
dotnet run --project src/Macast.Tray -c Release -- --selftest  # 设置窗口回归，不修改真实配置或自启
pwsh -File .\pack.ps1                                       # GUI 轻量版 + zip + SHA256
pwsh -File .\pack.ps1 -SkipZip                              # 只发布，不重新生成 zip
```

自测覆盖 description / SCPD、SOAP、GENA 初始事件与 SEQ、回调地址与订阅上限、配置并发写入及保存失败、日志脱敏、回环监听、NVA 请求/帧边界、连接清理、并发启停、重启重载和 SSDP，以及 DLNA/NVA 双向接管、B站链接自动解析和跨协议命令串行。设置窗口另有无修改应用、音量即时更新、播放器设置重载及取消操作回归；Windows CI 构建解决方案并运行两组自测。

`pack.ps1` 仅发布 `Macast.Tray`，输出到 `dist/MacastSharp-gui-win-x64/` 和同名 ZIP；不发布 CLI、不复制播放器、不携带 .NET 运行时。先在 `.work/` 完成发布与校验，再替换对应发布目录；旧版 `MacastSharp-win-x64` 完整包不会被自动删除，请勿混淆。发布目录生成 `SHA256SUMS.txt`。

已移除 `Microsoft.AspNetCore.App` 框架引用和 Kestrel；HTTP 支持 SOAP、GENA、HEAD、chunked 请求体和 `100-continue`，不需要 ASP.NET Core Runtime。未启用不兼容的 WinForms 裁剪或 NativeAOT。

## 配置兼容

读写原版同一份 `%APPDATA%\Macast\macast_setting.json`，键名保持一致（`USN`、`ApplicationPort`、`DLNA_FriendlyName`、`Macast_Renderer`、`Macast_Protocol`、`Player*` 以及插件自有键），从 Python 版切换不会被识别成新设备。

测试或绿色运行可设置 `MACAST_CONFIG_DIR`，例如 `$env:MACAST_CONFIG_DIR = "$env:TEMP\macast-test"`。

## 运行边界与日志

- `--loopback` 将 DLNA / NVA 的 HTTP/TCP 监听限制为 `127.0.0.1`，不只限制 SSDP。
- SOAP/NVA 请求体和 NVA 单帧上限为 1 MiB；HTTP 服务最多 32 个连接，请求读取限时 10 秒，NVA 控制通道读取空闲限时 30 秒。HTTP 每次响应后关闭连接，由客户端重连。
- GENA 回调必须使用订阅客户端自身的 IP 字面量，不接受其他主机、域名或重定向；每个 IP 最多 8 个订阅，全局最多 64 个。
- 日志默认不输出 Debug 或完整 NVA 参数；常见凭据字段会脱敏。设置 `MACAST_DEBUG=1` 开启 Debug。日志按天或达到 5 MiB 时归档，按文件最后写入时间清理过期记录；常规页的“日志保留时间”可设 1–365 天，默认 7 天（配置键 `LogRetentionDays`），应用后立即生效，不重启播放。启动及运行中写日志时定期清理，也兼容清理旧 `.log.1`。
- 配置采用同目录临时文件加原子替换，保存失败会通知调用者。设置未变或只改自启时不重载播放器，仅调整默认音量时直接更新当前音量。

## 目录

```
src/Macast.Core/   MacastApp、Plugins、Renderer、Protocols（DLNA / Nva）、Upnp、Ssdp、Server、Settings、Assets
src/Macast.Cli/    Program、PluginList、SelfTest、EventProbe
src/Macast.Tray/   Program、TrayApp、SettingsForm、TrayIcon、AutoStart
```

## 已知限制

- PotPlayer 使用 `/new` 启动独立实例，按进程定位窗口，通过限时窗口消息控制暂停/恢复、精确音量、静音、跳转并读取真实播放进度；不再向前台程序发送全局热键。支持四角/居中、小/标准/大/自适应/全屏及置顶，窗口尺寸受播放器皮肤最小尺寸限制；自适应保留原视频大小且不超出工作区。窗口布局只在加载就绪后有限次应用，不持续覆盖手动拖动。硬件解码沿用 PotPlayer 自身设置；倍速消息仅在版本支持时使用，不支持时记录提示。NVA 使用 DASH，通过 `/same` 同时打开视频与音频流。
- NVA 面向 B 站客户端：未登录或已知凭据过期时 `GetAppInfo` 返回 `CurrentSignedIn=0`，登录走 `LoginWithCode`（凭据存 `NVA_AccessKey`，过期时间存 `NVA_AccessKeyExpiresAt`）；播放优先使用手机凭据，否则使用有效的设备凭据。AppKey / Signature 为占位值。清晰度匿名 720P、登录 1080P、大会员高码率 / 4K，请求须使用官方 TV 身份，细节见 [docs/NVA-casting.md](docs/NVA-casting.md)。
- NVA 双连接共享播放状态，切视频会取消旧元数据任务。已移除 B 站投屏弹幕：不下载、不生成或加载弹幕字幕，手机弹幕命令仅保留兼容应答，不影响普通字幕。MPV 支持多分段 durl，PotPlayer 遇到多分段会提示不支持；镜像投屏尚未实现。
- macOS / Linux 仅可从源码运行 CLI，托盘需补 Avalonia / AppKit / AppIndicator 宿主；当前发布包仅 Windows x64。
- 发布包不再附带 mpv 或其 DLL；用户自行安装播放器并遵循对应许可证。

## License

GPL-3.0，与原项目一致。原始版权归 xfangfang 及 Macast 贡献者所有。
