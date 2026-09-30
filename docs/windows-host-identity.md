# Windows-compatible device identity

Windows host registration was observed in our own KVM test VM using the signed official client 4.42.0.2770. Only field shapes and non-secret protocol metadata were retained.

- Registration: `POST /api/v1/device/windows/init`.
- Control availability: `POST /api/v1/device/controllable`, Boolean `controllable`.
- Platform header: `X-Param-PLAT: 1`; version `4.42.0.2770`, build `2770`.
- Windows country header: `zh-CN`; operator header: empty.
- Registration fields: `base_board`, `client_id`, `controllable`, `cpu`, `mac`, `machine_guid`, `memory`, `name`, `os`, `platform`, `screen`, `system_id`, `video`.
- Memory is an integer number of MiB (displayed as MB by clients). Screen is a resolution string such as `1280x800`.

The plugin keeps its independently generated client/system identity and actual Linux hardware information. `os` contains the real kernel version; it does not claim that Windows is installed locally. Normal refresh rejects a changed device ID or a response that does not confirm platform 1.

The server assigns a different device ID when moving an existing Mac identity to Windows. Live verification rejected reuse of the old token with that ID (HTTP 400). Migration therefore requires a fresh login saved to a separate private pending identity. The old host stays online during login. After verifying the new login belongs to the same account and platform 1, the caller stops the old host and explicitly adopts the pending identity. Migration never runs automatically at startup. Private `.before-windows` backups preserve the previous identity and custom assistance settings; the custom code is rebound to the new device. Device IDs and server-issued assistance IDs can change.

Windows virtual key codes are normalized to the existing Wayland physical-key backend. Pointer coordinates remain normalized, as used by our controller against Windows. Registration/signed requests and key routing have offline coverage; live compatibility must also be checked with official controllers.

`UREMOTE_HOST_PLATFORM=mac` preserves the previous host API and keyboard interpretation as a diagnostic rollback option. Existing installations default to `mac`; enable `UREMOTE_HOST_PLATFORM=windows` only with a freshly authenticated Windows identity. The historical API class names remain for binary compatibility.
