# Native Portal input and touch

KDE/GNOME Portal hosting requests keyboard, pointer and (when available)
touchscreen together with ScreenCast sources. There is one RemoteDesktop
session and one `Start` consent request. All authorized input uses its
`ConnectToEIS` connection; no Notify* methods are sent after that connection.
The desktop controls the appearance of its consent UI. Controllers reuse the
host session; disconnecting releases contacts and keys without requesting
permission again. Stopping hosting closes the session.

The backend requires `libei.so.1`, RemoteDesktop Portal `ConnectToEIS`, and
matching `mapping_id` metadata for every shared output. It does not need Python
or private KDE interfaces. The former `UREMOTE_NATIVE_TOUCH` opt-in is no longer
needed for this combined Portal path. Niri's existing native keyboard/pointer
path remains separate; this does not add EIS or multitouch support to niri.

Keyboard events preserve evdev codes, including modifiers and Super. Pointer
and touch coordinates use the authorized output's EIS logical region, including
its offset and scaling. Scroll deltas and stop events use the EIS scroll device.
Held keys and buttons are released on disconnect. A paused or removed device
cannot continue a stale touch gesture.

Each remote touch packet produces one complete native frame. Touch references
must survive until after frame submission; releasing them earlier can split
frames. Screen switching releases old contacts; subsequent motion cannot
recreate them on another screen. Unmapped or ambiguous outputs are rejected.

## Text input compatibility

Native committed text is sent through `EI_DEVICE_CAP_TEXT` when advertised by
the compositor, using libei 1.6 UTF-8 support. Library version alone does not
prove the compositor supports text. Without that capability, committed phone
text is rejected with a structural diagnostic instead of dropping the remote
connection. Physical keyboard input still works, including typing through the
controlled desktop's input method. Automatic clipboard-based text injection is
not implemented; it must not be represented as native IME input.

## Verification

Build/run `tests/URemote.ProtocolChecks/URemote.Tests.csproj`.
Build `tests/URemote.PortalChecks/URemote.PortalChecks.csproj` and run its DLL:

- `--input-only`: isolated libeis receiver checks keyboard chords, pointer,
  buttons, scrolling, UTF-8 text, capability refusal, simultaneous touch,
  pinch frames, screen switching, pause/resume, removal and peer lifetimes.
- `--touch-only`: touch-specific socket-pair checks.
- `--touch-lifetime`: authorization reuse and peer cleanup checks.
- Ordinary run: use a dedicated `dbus-run-session` and
  `UREMOTE_ISOLATED_TEST_BUS=1`. Checks include FD ownership and exactly one
  Start request after selecting both sources and all input devices.
- `--input-probe`: live combined authorization and device readiness check;
  sends no input. It may show the desktop chooser and is never an unattended
  test. The test closes its session afterwards.

Libei 1.6 may log `ei_touch_up: device is not emulating` when dropping a touch
reference after a server pause; the isolated receiver verifies no input is
forwarded and stale contacts cannot resume. Actual KDE/GNOME app behavior,
Android gestures and desktop stability require end-to-end testing; passing the
isolated receiver is not proof of compositor/application compatibility.
