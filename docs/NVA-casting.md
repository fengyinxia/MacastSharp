# NVA 投屏协议分析（官方 APK 逆向 + 真机抓包）

分析对象：

- 哔哩哔哩 Android 客户端 `iBiliPlayer-bili.apk`（191 MB，38 dex）——发送端/遥控器
- 云视听小电视 `ystxds_1.8.9_dangbei.apk`（com.xiaodianshi.tv.yst，24 MB）——接收端

工具：jadx 1.5.6 全量反编译 + 自建 NVA 客户端 + 真实手机投屏日志。

## 1. 角色与传输

| 角色 | 组件 | 说明 |
|---|---|---|
| 手机 | `com.bilibili.lib.nirvana` + `com.bilibili.lib.projection` | 发现设备、下发控制命令、遥控器 UI |
| 电视 | 同一套 nirvana 客户端库 + `com.xiaodianshi.tv.yst.*` 播放器 | 响应命令、解析 playurl、上报状态 |

- SSDP 发现，ST 里带 `NirvanaControl`；HTTP 端口同时提供 DLNA 与 NVA。
- 控制通道不是标准 HTTP：请求行协议名是 `NVA/1.0`（`SETUP / NVA/1.0`），
  响应 `NVA/1.0 200 OK` + `Session`/`UUID`/`NvaVersion: 1`，随后同一条 TCP 连接切二进制帧。
- 帧：`e0` 命令 / `c0` 回包 / `e4` 心跳（每秒）。
  命令体 = `counter:u32 | 01 | 07 | "Command" | method_l | method | [param_l | json]`。

## 2. 投屏主流程（手机 → 电视）

1. `SETUP` 建链；断线用 `RESTORE`/`STARTRESTORE`，同 Session 复用会话。
   **真机观察：手机会同时保持两条 `/projection` 连接（会话不同、UUID 相同）。**
2. `Play`（aid/cid/accessKey/userDesireQn…）或 `PlayUrl`（`bilibili://…?nva_ext=<json>`）。
3. `nva_ext`（ver=2）字段：

   | 字段 | 含义 |
   |---|---|
   | `aid`/`cid`/`seasonId`/`epId`/`oid` | 视频与分集 |
   | `userDesireQn` | 手机选择的清晰度 |
   | `accessKey` | **手机账号的 access_key** |
   | `userDesireSpeed` / `seekTs` | 倍速 / 起播秒 |
   | `contentType`/`autoNext`/`danmakuSwitchSave`/`proj_type` | 内容类型等 |

   旧版 `_nva_ext_` 用短键（`key`/`sid`/`eid`/`ct`/`sk`），需兼容。
4. 电视用这个 accessKey 调 `GET https://api.bilibili.com/x/tv/playurl`。
5. 电视回 `OnPlayState`(3 加载/4 播放/5 暂停/7 停止)、`PLAY_SUCCESS`、`OnEpisodeSwitch`、
   `OnQnSwitch`、`SpeedChanged`、`OnDanmakuSwitch`、`OnProgress`。
6. 手机重连后发 `GetPlayInfo`，电视在 `c0` 回 `LinkPlayInfoTotal`：
   `playState / position(ms) / duration(ms) / danmakuState / title / volume /
   playItem{aid,cid,epId,seasonId} / qn{QualityDescInfoV2}`。
7. `SwitchQn {"qn":80}` → 电视重解析 → 广播 `OnQnSwitch`。

## 3. 清晰度：为什么“切 1080P 会回到 720P”

同一个 accessKey、同一个视频（真实手机日志里的 aid=117523945095211）实测：

| platform / mobi_app / build | 返回 curQn |
|---|---|
| `ios` / 空 / `36700100`（nirvana.py 的写法） | **64（720P）**，列表中没有 4K |
| `android_tv_yst` / `android_tv_yst` / `106400`（官方 TV 端） | **80（1080P）**，列表出现 `qn=120（4K）` |

只带 accessKey 不够：**必须用官方 TV 端的 platform/mobi_app/build**，否则服务端按 iOS 客户端
把结果压到 720P，连 `SwitchQn 80` 也只能拿回 64。

清晰度档位：16=360P、32=480P、64=720P（匿名可看）、80=1080P（登录）、
112=1080P 高码率（大会员）、120=4K（大会员）。

官方 TV 端（`TvPlayableParams.getFnVal()`）还默认 `fnval=16`（DASH），
再按 HDR/Dolby/AV1 位或——所以高清晰度必须走 DASH。

## 4. 这轮修掉的问题

| 现象 | 根因 | 修复 |
|---|---|---|
| 切 1080P 自动回 720P | playurl 用 `platform=ios`，服务端按 iOS 限流 | 改用 `platform=android_tv_yst` + `mobi_app=android_tv_yst` + `build=106400` |
| 没声音 | DASH 音轨用 `audio-add` 在 loadfile 后添加，mpv 在文件未就绪时拒绝/丢弃 | `loadfile <video> replace -1 "audio-file=\"<url>\""` 一次性带上音轨（真机验证 mpv 能识别 external aac 并起 AO） |
| 清晰度菜单空/低 | `PlayUrl` 不解析 playurl，qn 恒为 0 | 解析 `nva_ext` → playurl → 再下发；`userDesireQn=0` 时默认请求 80 |
| 重连风暴 | 手机同时开两条连接（不同 Session），旧代码见到“同 UUID 不同 Session”就终止另一条，手机立刻重连，形成死循环并不断广播空状态 | 允许同 UUID 多连接并存；新连接继承旧连接的播放状态，不再互相终止 |
| `GetPlayInfo` 解析失败 | 回的是自定义 JSON | 按手机模型输出 `LinkPlayInfoTotal`（位置/时长毫秒） |
| `GetPlayInfo` 命令无响应 | NVA 命令落到 default 回空 | 增加 `c0` 回包分支 |

弹幕实现现已按用户要求移除：不再下载 XML、转换 ASS 或将 `macast.ass` 加载到播放器。`SwitchDanmaku` / `SetDanmakuSwitch` / `SendDanmaku` / `AppendDanmaku` 仅保留兼容应答，不触发字幕开关或 OSD；播放信息中的 `danmakuState` 固定为 false，`GetDanmaku` 返回空内容。普通字幕功能不受影响。

## 5. 验证

不需要手机也能看清晰度：

    macast-cli --probe-playurl --aid 117523945095211 --cid 43152375905 --qn 80 --access-key <key>
    macast-cli --probe-playurl ... --qn 80 --access-key <key> --platform android_tv_yst --build 106400 --mobi-app android_tv_yst

匿名输出：`curQn=64`，列 112/80/64/32/16；带真实 key + TV 身份：`curQn=80`。

历史本地端到端记录（真实 key + 真实视频 + mpv；弹幕功能现已移除，以下仅保留当时验证证据）：

    NVA playurl: curQn=80 desireQn=80 dash=yes accessKey=yes
    GetPositionInfo → RelTime=0:00:04 / Duration=0:02:03 / NrTracks=3（video+audio+弹幕字幕）

## 6. 剩余风险

- 未在电视端验证 4K/120（需要大会员 + 4K 屏幕；fnval 位或还未按官方方式全开）。
- DASH 只取一路视频 + 最高码率音频；多音轨/杜比未做选择。
- 官方 TV 端还有 `is_switch_qn`、`play_model`、`fromspmid` 等埋点参数，未移植（不影响清晰度）。
- 手机为何常驻两条连接未完全确认；连接现在仅负责收发，共享管理器中的当前播放状态，断线不清空视频信息。

## 7. PotPlayer 高清播放

现场诊断显示 PotPlayer 请求 `qn=80`、携带手机凭据且 TV 身份正确，但非 DASH 请求被接口返回 `quality=64`。此前只有 MPV/Live 声明外置音轨能力。现在 PotPlayer 也请求 DASH，将视频 URL 和音频 URL 一起传入，并使用 `/same` 将它们作为同一播放项的多路流打开，保留起播位置。不修改服务端实际授予的清晰度。用户已在现场复测并确认修复有效。临时请求参数、凭据存在性与清晰度诊断打印已移除，正式版保留必要错误日志。

## 8. 设备名称与弹幕入口

对当前手机 APK 的 `classes16.dex` 定向反编译确认：

- `NirvanaEngine.b.getName()` 取发现设备的 friendly name，而非 `modelDescription`。
- `v03.j.v()` 用名称是否包含 **“我的小电视”** 判断 NVA 设备是否属于 B 站 TV；仅改厂商或写“云视听小电视”不能满足该条件。
- `ProjectionNewUiTvControlWidget.M()` 和 `ProjectionFullScreenFragment.o.b()` 使用该判断决定是否真正调用 `danmakuToggle`。
- `NirvanaEngine` 搜索 `urn:app-bilibili-com:service:NirvanaControl:*`；此前仅在 XML 声明服务，SSDP 未公告。

统一服务将发现名称设为 `我的小电视 · <用户设备名>`，已有标记时不重复添加，不改保存的设备名。所有客户端共用同一描述、UUID 和端口，因此普通 DLNA 客户端也看到此名称。SSDP 同时公告标准 DLNA 服务与 NirvanaControl:3，并接受后者的通配查询。手机可能缓存旧名称，需要重新搜索设备。

设备识别修复保留，但投屏弹幕渲染已按用户要求移除。手机可能仍显示其内置弹幕按钮；接收端不再执行该按钮对应的渲染操作，也不会加载旧弹幕文件。

## 9. DLNA + NVA 自动模式

- 对外只有一个 `Auto` 协议插件；旧 `DLNA` / `NVA` 配置和 CLI 参数均解析到同一自动服务，设置页不再提供协议切换。
- 共用原 NVA socket 监听与内置 DLNA 状态，按 SOAP action 命名空间、NVA 建链方法分流，标准 SOAP/GENA 和 `/health` 保留。
- 标准 `SetAVTransportURI` 中的 `bilibili:` 或有效 NVA 扩展链接转交 B站 playurl 解析；普通 URL 直接播放。无 NVA 长连接也可解析链接、切换清晰度及连播。
- DLNA SOAP 与 NVA 播放命令使用同一串行锁。新播放接管时取消旧元数据和待执行连播，清空旧 B站视频、凭据与清晰度上下文，不重新启动监听或渲染器。
- 普通 DLNA 内容接管后，旧 NVA 连接的暂停、恢复、停止、跳转、倍速和清晰度命令不再操纵该内容；音量仍是共用设备级控制。
- 实际手机/第三方 DLNA 客户端兼容性仍需现场验证；回归覆盖两种协议双向接管、并发提交、SOAP B站链接以及旧配置兼容。

## 10. 状态与播放修复

- 两条控制连接共享当前视频、清晰度及倍速；真正断线后重连也能查询原播放信息。修改播放的命令串行执行，避免两个连接互相覆盖。
- 切视频或清晰度前取消并等待旧元数据任务，停止协议时清理后台任务。弹幕下载与字幕生成任务已删除。
- 连播使用条目的 object ID，普通视频清空旧番剧 epId，普通视频列表不再把 aid 当作 epId。
- 同时支持 `nva_ext` 和 `_nva_ext_`；短键 `key/sid/eid/ct/sk` 保持兼容。
- 手机传入的凭据优先；未携带时使用设备保存且未过期的凭据。新登录记录过期时间，旧配置无过期时间时保留兼容；服务端提前撤销凭据仍会返回接口错误。
- `--probe-playurl` 默认与插件使用相同 TV 身份；可显式传旧客户端参数作对比。
- 多分段 durl 在 MPV 上使用 EDL 拼为单一时间线；不支持分段的播放器明确报错，不再静默只播第一段。Live/IPTV 播放器及频道菜单已移除，不影响 B站 NVA 媒体解析。
- 回归使用伪造 HTTP 响应，不访问用户账号；并未因此验证真实登录、会员清晰度或手机兼容性。
- 投屏弹幕已移除；镜像投屏仍未实现，不宣称支持。
