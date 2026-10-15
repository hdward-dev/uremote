# 被控端桌面快捷操作

官方控制通道 simple action 2 为显示桌面，3 为展开所有窗口。只在已允许输入的连接中执行，观看模式不会执行；不识别的操作编号保持忽略。

- KDE Plasma：通过原生 GIO D-Bus 调用 KGlobalAccel 的 `Show Desktop` / `ExposeAll`，不依赖用户快捷键设置。
- GNOME：通过 Shell 的 `OverviewActive` 属性切换窗口概览；显示桌面读取 `org.gnome.desktop.wm.keybindings` 的 `show-desktop`，由已授权的输入后端发送。不会修改 GSettings。默认空绑定或无法解析的绑定会显示不可用提示。支持 Control/Alt/Super/Shift、字母数字、F1–F12、方向键、Home/End/空格。
- macOS：通过 LaunchServices 启动系统 Mission Control，参数 0 / 1 对应窗口概览 / 显示桌面。进程成功退出仅表示已发起操作，不能当作实际桌面效果已验证。

KDE 实机验证读取 `showingDesktop` 和 `activeEffects`，断言状态确实改变，并恢复初始状态。GNOME 目前仅完成配置解析、模拟输入和取消清理测试，仍需 GNOME 实机联调。

测试：`URemote.PortalChecks --desktop-actions`（无桌面操作）；`--desktop-kde-live`（会切换实际 KDE 桌面并恢复）。
