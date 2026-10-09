# Native touch validation

`UREMOTE_NATIVE_TOUCH=1` opts in to the libei touch backend while compositor
compatibility is being validated. It requires `libei.so.1`, a RemoteDesktop
Portal advertising touchscreen (device type 4) and `ConnectToEIS`, and matching
`mapping_id` metadata for every shared ScreenCast output. No Python input
bridge or private KDE NotifyTouch calls are used.

Touch has its own Portal authorization session. Keyboard/mouse retain their
existing session: the Portal forbids Notify* after ConnectToEIS on the same
session. A refused or unavailable touch session does not disable keyboard/mouse.
The touch authorization may display an additional desktop consent dialog.

Remote normalized coordinates are unletterboxed, then mapped into the EIS
region identified by the authorized stream's mapping ID. Missing or ambiguous
regions are rejected. Each remote packet produces one native frame. Touch
references must survive until after the frame is submitted; releasing them
sooner can split the frame. Screen switching and peer teardown release all
contacts. Moves after a reset cannot recreate a stale gesture.

## Verification

Build `tests/URemote.ProtocolChecks/URemote.Tests.csproj` and run its DLL.
Build `tests/URemote.PortalChecks/URemote.PortalChecks.csproj` and run its DLL
with `--touch-only`, with libei and libeis on the library search path. This uses
an isolated socket pair and synthetic regions, never the desktop. It checks
actual received frames for simultaneous contacts, pinch motion, screen
switching, scaling, edge coordinates, pause/resume, removal, and cleanup.
Libei 1.6 may log `ei_touch_up: device is not emulating` when dropping a native
touch reference after a server pause; the isolated test verifies no input is
forwarded and the old gesture cannot resume.

Run ordinary Portal checks on a dedicated `dbus-run-session` with
`UREMOTE_ISOLATED_TEST_BUS=1`. These include ConnectToEIS FD list ownership.

`--touch-probe <mapping-id> ...` is a separate live check: it requests desktop
authorization and verifies region availability, but sends no input. It must
not be included in unattended test suites. Real Android single/multitouch,
display switching, disconnect/reconnect, and Plasma stability still require
end-to-end validation before making this backend default.
