# macOS 被控后端

最低 macOS 12.3；支持 Apple Silicon 与 Intel。协议、连接准入及账号登录复用现有 C# 实现，原生屏幕和输入通过小型 C ABI 桥接库提供，不依赖 Python 或 Wayland。

- ScreenCaptureKit 每个显示器一条流，输出 BGRA；仅接收完整帧，保留静态帧，按实际显示器刷新率采集。
- VideoToolbox 硬件 H.264，通过 FFmpeg 的 `h264_videotoolbox` 编码；插入 AUD 以兼容现有帧分包。
- CoreGraphics 键鼠事件，按显示器逻辑坐标映射 Retina 像素；跟踪拖动、修饰键，断连释放按键。
- Unicode 提交文本与文本剪贴板（系统 pbcopy / pbpaste）。
- 原生 macOS 设备身份和系统硬件信息。Mac 不复用其他机器的登录令牌。

首次启用需要在系统设置中授予星栈屏幕录制和辅助功能权限。应用不写 TCC 数据库，不绕过拒绝；授权不足时仍保持在线但关闭被控准入。更换签名或应用路径后系统可能要求重新授权。

当前边界：macOS 系统音频、原生多点触控和远程终端尚未实现，音频选项禁用、终端请求明确拒绝。远程文件传输复用现有实现，尚待 Mac 端到端验证。登录前及 FileVault 解锁界面不属于被控范围。

## 构建

在安装 Apple Command Line Tools 的 Mac 上运行：

```sh
bash scripts/build-uremote-mac.sh arm64
bash scripts/build-uremote-mac.sh x86_64
dotnet build src/URemote.Module -c Release
```

将 `Native/bin/osx-*/liburemote-mac.dylib` 放入插件的 `native/osx-*` 目录。FFmpeg 可安装在 `/opt/homebrew/bin`、`/usr/local/bin`，或随插件放入 `ffmpeg/osx-*/ffmpeg`，需要支持 VideoToolbox、rawvideo、scale/pad 及 h264_metadata。分发时应使用稳定的应用签名，并修正 FFmpeg 动态库的可重定位路径。

`.github/workflows/uremote-macos.yml` 编译两个架构并检查无授权的原生 ABI。开发验证已在 macOS 26.5 arm64 编译双架构桥接库，合成画面的 VideoToolbox H.264 和 AUD 检查通过；完整官方客户端联调另行记录。
