# Linux 无人值守被控

被控依赖当前用户已登录的 Wayland 桌面。关闭窗口会隐藏星栈；注销、休眠或没有可采集输出时，不保证桌面远控可用。本实现不创建虚拟显示器。

被控启动、网络及屏幕采集的可恢复故障自动重试。每 3 秒检查输出拓扑，变化后释放旧会话并重新发现显示器；无选中输出时保持等待。屏幕选择按当前排序序号保留。桌面恢复后重新创建会话。手动停止被控保存 HostEnabled=false，重启不会自动开启；旧设置缺省为 true。

本机已配置 ~/.config/systemd/user/asterdock.service：随 graphical-session.target 启动，Restart=always，RestartSec=5，桌面会话结束时停止。服务直接运行安装器生成的 launch.py，保留单实例锁。GUI 程序异常或正常退出都会重新启动；维护时用 systemctl --user stop asterdock.service。

诊断：systemctl --user status asterdock.service；日志位于 ~/.local/share/asterdock/launch.log。日志只记录事件及异常类型，不记录账号令牌或屏幕正文。

验证：Release 构建通过；对主进程发送 SIGTERM 后，NRestarts 增加，新进程输出 ready。尚未实测物理屏幕关机/拔插、注销后重新登录及整机重启。系统休眠不会因本服务被阻止。
