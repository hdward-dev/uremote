# Remote assistance handshake

Verified on 2026-10-09 against the official Windows client 4.42.0.2770 in the existing KVM lab. A Mac controller entered the Windows host's assistance ID and displayed the password prompt after the following exchange:

1. Signaling event `bmsg_push`, with `type: get_control_mode` and `data.control_id` (no salt).
2. Host posts `/api/v2/room/share/upload_control_mode` with `allow_control` (Boolean), `control_id` (string), and `control_mode: by_password`.
3. Server accepts with HTTP 200 and application code 0.

This exchange advertises password authentication; it must not grant a session or rotate the temporary password. The local assistance switch governs `allow_control`. The subsequent salted challenge still uploads the temporary/custom signatures through `/api/v2/room/share/upload_sign`.

A separate official Mac 4.41.0 host sample showed `bmsg_push` / `remote_control` with `control_id` and `salt`, then `upload_sign`, then `be-controlled`. That sample did not show the Windows mode-negotiation step. The prior host implementation accepted only `push` challenges and did not implement `get_control_mode`, explaining failure before the password prompt when advertising a Windows identity.

Tests cover bounded event parsing, unrelated/malformed events, the exact signed mode-response schema, and enabling/disabling assistance without bypassing password authentication. Capture logs retain field structure and protocol enums, not credentials or challenge values.
