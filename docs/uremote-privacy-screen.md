# 被控端防窥屏

当前实现适用于 **KDE Plasma Wayland + 原生 Portal 采集**，并要求 KWin 的窗口 API 提供可写的 `excludeFromCapture`。能力在每次远控会话建立时检测；其他桌面、X11 采集或缺少接口的 KWin 不宣告支持。

主控端开启普通防窥屏后，本机所有已连接的显示器显示在 UU 中为本机选择的屏保，远端仍接收原来的桌面，并能继续操作。原生 layer-shell 遮罩不抢键盘焦点，鼠标和触控事件穿透。创建时先提交透明缓冲，只有 KWin 确认遮罩已排除采集后才切换为不透明的内容。图片尚未准备好时先用纯黑色遮挡，下载失败仍保持遮挡。

屏保读取本机设备的 `/api/v1/device/{id}/screensaver/detail`，使用 `selected_image_url`，支持预设与自定义图片。首次连接预取；收到官方 `UpdateScreenSaverRequest` 时重新读取云端选择。已经开启防窥时可直接更换图片。仅在图片获取、解码成功，且已启用的遮罩完成合成器同步后，才返回更新成功；失败返回错误并保留已有遮罩。请求处理与键鼠输入独立，不会等待网络下载。

图片只在内存保存；会话内图片在断开时清除，供后续连接复用的解码缓存随本机被控服务结束清除。API 授权头不发送给图片服务器。支持 PNG、JPEG、WebP；透明区域合成到黑色背景，不能透出本机桌面。官方预设可能达到 11520×6480，解码后按比例限制最长边为 4096 像素，再以原生 viewport 居中裁切适配各显示器。同一预设图片缓存解码结果；后续更新仍重新查询配置，自定义图片即使地址不变也会重新下载。

关闭防窥屏、断开控制、停止被控或进程退出后，遮罩随 Wayland 连接关闭而移除。不修改亮度、DPMS、分辨率、窗口规则或系统快捷键。显示器插拔使当前覆盖失效时会撤销遮罩，并向主控报告防窥屏已关闭。

这是普通遮罩模式，不阻止本机键鼠，也不承诺遮挡合成器的安全锁屏层。暂未支持“关闭物理显示器”模式，相关请求返回失败并保留当前实际状态。不会把调暗亮度或停止传屏作为防窥屏成功。

协议使用 RPC request 7 / response 3、FeatureFlag 4（版本 1）、SystemStateChange 5 和 SettingMetric 4/7。屏保更新为 RPC request 25 / response 21，请求没有图片字段，它触发云端配置刷新。防窥相关回复同时兼容 CONTROL 与 TEXT 通道；TEXT 尚未打开时按会话排队，最多 64 条，设置状态只保留最新一条。

验证命令：

```sh
dotnet run --project tests/URemote.ProtocolChecks/URemote.Tests.csproj -c Release
dotnet run --project tests/URemote.Checks -c Release -- --privacy-checks
dotnet run --project tests/URemote.Checks -c Release -- --privacy-image-decode-checks /path/to/ffmpeg
dotnet run --project tests/URemote.PortalChecks -c Release -- --privacy-crop-checks
# 本机 KDE 实测：64 像素局部遮罩，自动恢复，不保存画面
dotnet run --project tests/URemote.PortalChecks -c Release -- --privacy-probe
dotnet run --project tests/URemote.PortalChecks -c Release -- --privacy-capture-live
dotnet run --project tests/URemote.PortalChecks -c Release -- --privacy-image-capture-live
```

本机 KDE Plasma 6.7.5 已验证双屏局部及整屏遮罩、遮罩尺寸覆盖完整输出、Portal 画面不含遮罩、不抢键盘焦点和移除窗口后的恢复。强制结束专用测试进程后，也已确认 KWin 会移除全部遮罩。官方客户端已成功触发普通防窥模式。已验证云端预设图片读取和 12K 图片解码、双屏图片更新、恢复纯黑及图片排除采集；官方客户端更改屏保后的实际效果仍需客户端联调确认。

2026-11-05，用户通过官方客户端实测确认：双屏防窥、远端继续操作、关闭防窥以及断开后本机恢复均正常。此确认不包含更换屏保图片后的官方客户端联调。
