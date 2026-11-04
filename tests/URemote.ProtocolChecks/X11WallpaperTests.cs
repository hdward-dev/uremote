using URemote.Linux;

static class X11WallpaperTests
{
    private const string Fixture = """
        <channel name="xfce4-desktop" version="1.0">
          <property name="backdrop" type="empty">
            <property name="screen0" type="empty">
              <property name="monitorVNC-0" type="empty">
                <property name="workspace0" type="empty">
                  <property name="image-style" type="int" value="5"/>
                  <property name="last-image" type="string" value="/tmp/primary-workspace0.png"/>
                </property>
                <property name="workspace1" type="empty">
                  <property name="color-style" type="int" value="1"/>
                  <property name="image-style" type="int" value="0"/>
                  <property name="last-image" type="string" value="/tmp/ignored.png"/>
                  <property name="rgba1" type="array">
                    <value type="double" value="1"/>
                    <value type="double" value="0"/>
                    <value type="double" value="0"/>
                    <value type="double" value="1"/>
                  </property>
                  <property name="rgba2" type="array">
                    <value type="double" value="0"/>
                    <value type="double" value="0"/>
                    <value type="double" value="1"/>
                    <value type="double" value="1"/>
                  </property>
                </property>
              </property>
              <property name="monitor0" type="empty">
                <property name="image-style" type="int" value="5"/>
                <property name="image-path" type="string" value="/tmp/legacy.png"/>
              </property>
            </property>
          </property>
        </channel>
        """;

    public static void Run(Action<bool, string> check)
    {
        DesktopEnvironmentInfo Desktop(Func<IEnumerable<string>>? commands, params (string Key, string? Value)[] values)
            => DesktopEnvironmentInfo.Detect(key => values.FirstOrDefault(item => item.Key == key).Value, commands);
        check(Desktop(null, ("XDG_CURRENT_DESKTOP", "XFCE"), ("XDG_SESSION_TYPE", "x11")).DisplayName == "XFCE / X11",
            "XDG_CURRENT_DESKTOP=XFCE selects XFCE");
        check(Desktop(null, ("XDG_SESSION_DESKTOP", "xfce"), ("DISPLAY", ":5")).DisplayName == "XFCE / X11",
            "XDG_SESSION_DESKTOP=xfce selects XFCE");
        check(Desktop(null, ("DESKTOP_SESSION", "xfce"), ("XDG_SESSION_TYPE", "x11")).Name == "XFCE",
            "DESKTOP_SESSION=xfce selects XFCE");
        check(Desktop(() => ["xfwm4"], ("DISPLAY", ":5")).DisplayName == "XFCE / X11",
            "an X11 session with xfwm4 and no desktop variables is XFCE");
        check(Desktop(() => ["xfdesktop"], ("DISPLAY", ":5")).Name == "XFCE"
            && Desktop(() => ["xfce4-session"], ("XDG_SESSION_TYPE", "x11")).Name == "XFCE",
            "xfdesktop and xfce4-session also identify XFCE");
        check(Desktop(() => ["bash"], ("DISPLAY", ":5")).DisplayName == "未知桌面 / X11",
            "an X11 session without an XFCE process stays unknown");
        check(Desktop(() => ["xfwm4"], ("WAYLAND_DISPLAY", "wayland-0")).DisplayName == "未知桌面 / Wayland",
            "a Wayland session does not become XFCE because xfwm4 is running");
        check(Desktop(null, ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"), ("DISPLAY", ":0")).Name == "GNOME",
            "GNOME detection is unchanged");

        bool Exists(string path) => path is "/tmp/primary-workspace0.png" or "/tmp/legacy.png" or "/tmp/other.png";
        var slots = X11Wallpaper.ParseXml(Fixture);
        var current = X11Wallpaper.Select(slots, "VNC-0", 1, Exists);
        check(current?.Image is null && current?.Color == "#FF0000" && current?.SecondColor == "#0000FF" && current?.Vertical == false,
            "XFCE image-style none uses the workspace color instead of last-image");
        var primary = X11Wallpaper.Select(slots, "VNC-0", 0, Exists);
        check(primary?.Image == "/tmp/primary-workspace0.png", "XFCE prefers the primary output and current workspace");
        const string missingPrimary = """
            /backdrop/screen0/monitorVNC-0/workspace0/image-style  5
            /backdrop/screen0/monitorVNC-0/workspace0/last-image  /tmp/missing.png
            /backdrop/screen0/monitor0/image-style  5
            /backdrop/screen0/monitor0/image-path  /tmp/legacy.png
            """;
        var legacy = X11Wallpaper.Select(X11Wallpaper.ParseQuery(missingPrimary), "VNC-0", 0, path => path == "/tmp/legacy.png");
        check(legacy?.Image == "/tmp/legacy.png", "XFCE falls back to an older monitor image-path when the preferred file is missing");

        const string query = """
            /backdrop/screen0/monitorVNC-0/workspace0/image-style  5
            /backdrop/screen0/monitorVNC-0/workspace0/last-image  /tmp/primary-workspace0.png
            /backdrop/screen0/monitor0/image-path  /tmp/legacy.png
            """;
        var queried = X11Wallpaper.Select(X11Wallpaper.ParseQuery(query), "VNC-0", 0, Exists);
        check(queried?.Image == "/tmp/primary-workspace0.png", "xfconf-query listing prefers the primary monitor image");
    }

    public static async Task NoCaptureAsync(Action<bool, string> check)
    {
        var saved = Environment.GetEnvironmentVariable("UREMOTE_WALLPAPER");
        var home = Directory.CreateTempSubdirectory("uremote-wallpaper-none-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("UREMOTE_WALLPAPER", null);
            var png = await LinuxWallpaper.RenderAsync("/usr/bin/ffmpeg", CancellationToken.None, home, shell: null,
                onSource: null, desktop: new DesktopEnvironmentInfo("未知桌面", "X11"));
            check(png is null, "an X11 desktop with no configured wallpaper returns no preview");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UREMOTE_WALLPAPER", saved);
            Directory.Delete(home, true);
        }
    }
}
