# U远程 · URemote

C# / .NET 10 编写的 UU 远程协议兼容项目，提供 Linux / Wayland 被控后端、原生主控窗口和 AsterDock 轻应用模块。不是 UU 官方客户端，与官方无隶属关系。

从 [AsterDock](https://github.com/hdward-dev/asterdock) 的 `2f71522` 拆出。此仓库可独立构建；原生界面目前仍作为 AsterDock 插件加载，尚无独立桌面启动器。

## 项目结构

- `URemote.Core`：登录、设备、信令、输入、终端、文件传输协议。
- `URemote.Linux`：Wayland 采集与输入、PTY、系统信息。
- `URemote.Media`：WebRTC、H.264、系统音频。
- `URemote.Host`：被控/主控会话、CLI 诊断入口。
- `URemote.Module`：Avalonia 原生轻应用与远控窗口。
- `AsterDock.Contracts`：随仓库保存的插件接口，构建不依赖外部星栈源码。
- `tests`：协议、媒体和集成检查；依赖完整星栈宿主的 DesktopCheck 留在原仓库。

## 构建与测试

安装 .NET 10 SDK：

```sh
dotnet restore URemote.slnx
dotnet build URemote.slnx -c Release --no-restore -m:1
dotnet run --project tests/URemote.ProtocolChecks -c Release --no-build
```

协议检查不连接真实 UU 账号。媒体与系统集成检查需要额外依赖，参见 [功能和测试说明](docs/u-remote.md)。

查看 CLI 用法（不会自动登录或上线）：

```sh
dotnet run --project src/URemote.Host -c Release --no-build
```

将 `src/URemote.Module/bin/Release/net10.0/` 完整输出放入 AsterDock 的 `Apps/URemote/` 以加载界面。插件清单及第三方声明随模块复制。

## 运行条件与现状

被控需要已登录的 Linux Wayland 桌面，支持 wlr-screencopy、虚拟键鼠协议，以及带 libx264 的 FFmpeg。声音需 PipeWire/pw-cat，剪贴板需 wl-clipboard。Windows/macOS 被控后端尚未实现。

Windows/iOS 官方客户端到 Linux 的桌面、双屏、键鼠及远程终端已有实测。文件传入/取出已实现，接收目录为 `~/Download/uurc`；跨客户端完整兼容性、声音、剪贴板及长期稳定性仍需验证，不能视为完整官方功能替代品。

默认允许被控；本机手动关闭状态会保存。显示器变化及部分网络/采集异常自动恢复，但无虚拟显示器，无登录桌面或系统休眠时不保证可用。[无人值守说明](docs/unattended-linux.md)中的 systemd 服务描述原开发机部署，不会因克隆仓库自动安装。

身份文件必须私密保存，Linux 权限为 600。仓库不包含真实账号、令牌、协助验证码、录屏或诊断捕获数据。

## 第三方声明

协议移植参考 [iola1999/uurc-web](https://github.com/iola1999/uurc-web)（MIT，参考提交 `7d1cccb`）。保留其许可证及 SIPSorcery、Concentus 声明，见 [THIRD-PARTY-NOTICES](src/URemote.Module/THIRD-PARTY-NOTICES.md)。SIPSorcery 包含额外使用限制，不能将整个项目及依赖统称为 MIT。
