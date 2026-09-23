# U远程 · 星栈远程控制插件

## 界面预览

![U远程当前界面](assets/uremote-ui.png)

实际界面的宽屏验证截图，使用示例设备、壁纸、协助码和连接信息。状态与“本机被控”标题同行，FPS 位于对应屏幕下方，当前连接显示控制端、连接时长和断开按钮。

状态按实际输入活动判断：连接后显示“正在屏幕共享”，首次有效键鼠操作后显示“正在被控”，断开恢复“被控已开启”；不代表官方主控的模式开关状态。


请先安装并启动 [星栈（AsterDock）](https://github.com/hdward-dev/asterdock)，再从轻应用列表打开 U远程。本仓库提供插件，不提供独立桌面启动器。

U远程作为 Avalonia `IApplicationModule` 运行在 AsterDock 内，不启动网页或 GTK 窗口。
模块入口为 `URemote.Module.URemoteApplicationModule`，清单 ID 为 `u-remote`。
在星栈宿主中可打包到 `Apps/URemote`；本仓库直接构建模块输出，不包含星栈打包脚本。

## 当前能力

- 原生界面分为“我的设备”“本机被控”“账号与设置”，沿用 AsterDock 深浅主题；停止被控始终可见。
- 设备列表直接使用当前 UU 登录身份读取，支持名称/编号/平台搜索、在线/设备类型筛选、本机标记、本地收藏、详情及复制编号。
  设备页每 60 秒刷新，失败时保留上次结果；仅收藏编号存放于本机，不保存完整设备列表。在线且允许被控的其他电脑可点击“远程控制”，打开原生独立窗口；重复点击激活已有窗口。

- 默认打开模块后启用被控；停止不会自动重新启用。可选择屏幕、键鼠、系统声音、文本剪贴板和开放时长。
- 退出 AsterDock 时取消会话并清理虚拟键鼠、编码器、音频进程与剪贴板所有者。
  宿主关闭按钮可能隐藏到托盘；需点击停止被控或从托盘退出才能终止。
- 首次登录可在模块内输入手机号和短信验证码。只有点击发送按钮才发送短信，不自动重发。
  保存的登录令牌存放在私有身份文件（Linux 600 权限），不记录手机号和验证码。
- Linux/niri 双屏由 wlr-screencopy 采集，持续 FFmpeg/libx264 编码，原始分辨率 / 默认目标 30 FPS（客户端可请求最高 60 FPS）。
  采用 CPU/SHM 管线，非 GPU 零拷贝；没有实现动态码率和即时关键帧反馈，丢包恢复依赖每秒关键帧。
- 系统音频通过 PipeWire `pw-cat` 的输出监视器获取，48 kHz 双声道 / 20 ms Opus 帧，经 SRTP 发送。不采集麦克风。
- 文本剪贴板使用 `wl-copy` / `wl-paste`，支持 V3 文本通知及 V4 格式读取响应；UU 传输文本使用 UTF-8。
  当前上限 64 KiB，已加入 Windows Unicode 格式协商及 V4 分块接收；不支持文件/图片。Windows/iOS 官方客户端兼容性需实测。
- 远程终端已接入官方 4.41.0 握手、环境检查、会话列表、新建/恢复、输入输出、窗口调整和关闭。
  使用当前用户的原生 Linux PTY；与键鼠共用本机允许开关。远端断开保留会话，本机停止被控或退出宿主清理会话。
  最多 8 个终端，每个仅在内存保留最多 64 KiB 输出供重连恢复；暂不支持终端重命名，同时只允许一个主控连接。
  本机加密完整回环、Ctrl+C、恢复 Shell 状态测试通过；官方客户端已由用户确认成功创建会话，`uname -a` 正确返回；Ctrl+C 和恢复会话仍待官方客户端实测。
- 当前被控后端只支持 Linux/Wayland；Windows/Mac 的 AsterDock 可加载模块并显示平台说明。

## 验证状态

已确认的此前实测：Windows/iOS 官方客户端连接，Windows 键鼠控制及双屏对应正确。
新增本机验证：110 项协议检查、8 项 WebRTC 回环检查（含 Opus）、连续 H264 编码及解码、剪贴板协议样例、真实 AsterDock 宿主渲染。
本机双屏纯采集约 43 / 37 FPS；该数值不等于远端实际帧率。实时发送日志曾测得一屏约 30–35 FPS、另一屏约 24–26 FPS；不是客户端渲染 FPS。用户已确认画面清晰度、内存单位和退出后再次连接正常。画质切换与剪贴板修复后等待复测，声音尚未验证。

## 设备信息和连接生命周期

本机名称统一为 `aster-{系统主机名}`；每次上线同步 UU 设备显示别名，保留原设备身份。

CPU 来自 /proc/cpuinfo，内存从 MemTotal 转为纯数字 MB（UU 界面自动追加单位），网卡 MAC 来自活动物理接口。
系统版本按用户要求使用 /proc/sys/kernel/osrelease 的内核版本，不替换为发行版。
每次上线使用原有 client_id / system_id 更新设备资料，响应必须保持同一 device_id。
主控端退出仅结束当前媒体会话，本机继续等待连接；信令断开会退避重建房间。仅本机停止或退出宿主（或显式定时到期）停止被控。

## 本机依赖与配置

.NET 10、支持 screencopy/虚拟键鼠的 Wayland 桌面、带 libx264 的 FFmpeg；音频需要 PipeWire 和 `pw-cat`，剪贴板需要 `wl-clipboard`。
默认身份位于模块数据目录 `identity.json`，编码器从 PATH 查找；高级设置可修改。
开发时可使用 `UREMOTE_IDENTITY`、`UREMOTE_FFMPEG`。`UREMOTE_NO_AUTO_START=1` 仅用于不联网的界面检查。
请勿把实际身份文件、验证码、原始录屏、音频或剪贴板数据加入源码或 appbundle。

## 构建与检查

```sh
dotnet build src/URemote.Module -m:1
dotnet run --project tests/URemote.ProtocolChecks
dotnet run --project tests/URemote.Checks -- /path/to/ffmpeg
dotnet run --project tests/URemote.Media.Tests -- /path/to/ffmpeg
```

`--capture-benchmark` 额外测量真实显示器采集，像素用后清空，不保存或发送。
`URemote.DesktopCheck` 用真实 AsterDock 窗口验证模块渲染；需提供测试 Apps 目录及桌面运行环境。

协议移植参考 MIT 的 iola1999/uurc-web；许可证见模块 `licenses`。SIPSorcery 包含额外使用限制，不能把所有依赖统称 MIT。
音频采集参数参考 https://docs.pipewire.org/page_man_pw-cat_1.html 和 https://docs.pipewire.org/group__pw__keys.html 。
编码参数参考 https://www.ffmpeg.org/ffmpeg-all.html 。

## 尚未完成

主控声音/剪贴板、主控画质设置、设备管理全功能、文件传输兼容性完善、中文 IME、高分辨率/高帧率自适应、完善重连、多观看者、长期稳定性和其他桌面平台被控后端。

## 界面参考

信息结构参考 AnyDesk 通讯录（搜索、收藏与设备详情）和 TeamViewer 设备管理（设备分类），采用 AsterDock 原生控件与主题，不复用其图形资产。
- https://support.anydesk.com/address-book
- https://www.teamviewer.com/en/global/support/knowledge-base/teamviewer-remote/devices/device-groups-explained/


## 原生主控窗口（0.4.0）

- C# / Avalonia 独立窗口，通过现有 UU 账号直接加入设备房间，WebRTC 接收 H264，FFmpeg 在内存中解码绘制。
- 支持鼠标移动、左右中键、滚轮、常用键盘按键、全屏和断开；失焦释放按键，Ctrl+Alt+Esc 将焦点移出远控画面。
- 设备未在线或不允许被控时禁用连接按钮；不强制挤掉其他主控。
- 关闭窗口仅断开其对应设备，本机被控继续运行。不同设备可打开各自的窗口。
- Windows 与 Mac 官方被控端均已实际连接并解码 30 帧 1920×1080；独立原生窗口实际绘制已验证。降低解码缓冲后，一次 Windows 实测首帧约 0.7 秒，非延迟保证。
- 本地回环测试验证键盘封包传输与合成画面的解码尺寸、颜色；实际官方设备键鼠效果待用户交互验证。
- 当前主控不接收声音，不同步剪贴板；显示屏选择提供入口，官方多屏切换待验证。

## 主页集成与远程协助

容器启动时默认显示内置主页，同时在后台初始化已安装的 U远程。主页代码和 XAML 编译在 AsterDock.Host 中，不再作为独立轻应用分发。

本机被控页面包含服务端分配的协助码、本机生成的 8 位临时验证码、显示/隐藏、复制和换码，以及独立的“允许远程协助”开关。开关状态保存到模块设置，关闭会撤销协助会话权限并更新验证码；同账号被控开关保持独立。临时验证码仅存在当前进程内存，重启后更换。协助请求的挑战响应已接入，官方客户端完整协助连接仍需联调验证。

主控端桌面键鼠消息使用 CONTROL_DATA_CHANNEL 的原始 JSON 文本及 WebRTC String PPID，已通过本机加密回环检查；真实远端控制修复后的效果待用户确认。
