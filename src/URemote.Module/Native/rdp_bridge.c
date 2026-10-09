/* FreeRDP owns all protocol state; this bridge exposes only frames and input,
 * so managed code never depends on the layout of FreeRDP's ABI structures. */
#include <freerdp/freerdp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/synch.h>
#include <stdlib.h>

#ifdef _WIN32
#define API __declspec(dllexport)
#else
#define API __attribute__((visibility("default")))
#endif

typedef void (*frame_fn)(const BYTE*, int, int, int);
typedef int (*certificate_fn)(const char*, const char*, const char*, const char*, int);
typedef struct {
    rdpContext context; /* Must be first. */
    frame_fn frame;
    certificate_fn certificate;
    int buttons;
} local_context;

static BOOL begin_paint(rdpContext* context) { (void)context; return TRUE; }
static BOOL end_paint(rdpContext* context) {
    local_context* local = (local_context*)context;
    rdpGdi* gdi = context->gdi;
    if (!gdi || !gdi->primary_buffer || gdi->width < 1 || gdi->height < 1 ||
        gdi->width > 8192 || gdi->height > 8192 ||
        (INT64)gdi->width * gdi->height > 16 * 1024 * 1024) return FALSE;
    local->frame(gdi->primary_buffer, gdi->width, gdi->height, gdi->stride);
    return TRUE;
}
static BOOL resize_desktop(rdpContext* context) {
    UINT32 w = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth);
    UINT32 h = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight);
    if (!w || !h || w > 8192 || h > 8192 || (UINT64)w * h > 16 * 1024 * 1024)
        return FALSE;
    return gdi_resize(context->gdi, w, h);
}
static BOOL post_connect(freerdp* instance) {
    if (!gdi_init(instance, PIXEL_FORMAT_BGRA32)) return FALSE;
    instance->context->update->BeginPaint = begin_paint;
    instance->context->update->EndPaint = end_paint;
    instance->context->update->DesktopResize = resize_desktop;
    return TRUE;
}
static BOOL authenticate(freerdp* instance, char** user, char** password, char** domain,
                         rdp_auth_reason reason) {
    (void)instance; (void)user; (void)password; (void)domain; (void)reason;
    /* Credentials were supplied once by the UI. Never prompt on a terminal. */
    return FALSE;
}
static DWORD verify(freerdp* instance, const char* host, UINT16 port, const char* name,
                    const char* subject, const char* issuer, const char* fingerprint, DWORD flags) {
    (void)port; (void)name; (void)flags;
    local_context* local = (local_context*)instance->context;
    return local->certificate(host, subject, issuer, fingerprint, 0) ? 2 : 0;
}
static DWORD verify_changed(freerdp* instance, const char* host, UINT16 port, const char* name,
    const char* subject, const char* issuer, const char* fingerprint,
    const char* old_subject, const char* old_issuer, const char* old_fingerprint, DWORD flags) {
    (void)port; (void)name; (void)old_subject; (void)old_issuer; (void)old_fingerprint; (void)flags;
    local_context* local = (local_context*)instance->context;
    return local->certificate(host, subject, issuer, fingerprint, 1) ? 2 : 0;
}

API freerdp* urdp_create(const char* host, UINT32 port, const char* user,
    const char* password, const char* domain, frame_fn frame, certificate_fn certificate) {
    freerdp* instance = freerdp_new();
    if (!instance) return NULL;
    instance->ContextSize = sizeof(local_context);
    instance->PostConnect = post_connect;
    instance->AuthenticateEx = authenticate;
    instance->VerifyCertificateEx = verify;
    instance->VerifyChangedCertificateEx = verify_changed;
    if (!freerdp_context_new(instance)) { freerdp_free(instance); return NULL; }
    local_context* local = (local_context*)instance->context;
    local->frame = frame; local->certificate = certificate;
    rdpSettings* s = instance->context->settings;
    BOOL ok = freerdp_settings_set_string(s, FreeRDP_ServerHostname, host) &&
        freerdp_settings_set_uint32(s, FreeRDP_ServerPort, port) &&
        freerdp_settings_set_string(s, FreeRDP_Username, user) &&
        freerdp_settings_set_string(s, FreeRDP_Password, password) &&
        freerdp_settings_set_string(s, FreeRDP_Domain, domain) &&
        freerdp_settings_set_uint32(s, FreeRDP_DesktopWidth, 1280) &&
        freerdp_settings_set_uint32(s, FreeRDP_DesktopHeight, 800) &&
        freerdp_settings_set_uint32(s, FreeRDP_ColorDepth, 32) &&
        freerdp_settings_set_uint32(s, FreeRDP_TcpConnectTimeout, 20000) &&
        freerdp_settings_set_bool(s, FreeRDP_SoftwareGdi, TRUE) &&
        freerdp_settings_set_bool(s, FreeRDP_NSCodec, TRUE) &&
        freerdp_settings_set_bool(s, FreeRDP_RemoteFxCodec, TRUE) &&
        freerdp_settings_set_bool(s, FreeRDP_SupportGraphicsPipeline, FALSE) &&
        freerdp_settings_set_bool(s, FreeRDP_IgnoreCertificate, FALSE) &&
        freerdp_settings_set_bool(s, FreeRDP_AutoAcceptCertificate, FALSE) &&
        freerdp_settings_set_bool(s, FreeRDP_NlaSecurity, TRUE) &&
        freerdp_settings_set_bool(s, FreeRDP_TlsSecurity, TRUE) &&
        freerdp_settings_set_bool(s, FreeRDP_RdpSecurity, TRUE);
    if (!ok) { freerdp_context_free(instance); freerdp_free(instance); return NULL; }
    return instance;
}
API int urdp_connect(freerdp* instance) { return freerdp_connect(instance); }
API UINT32 urdp_error(freerdp* instance) { return freerdp_get_last_error(instance->context); }
API int urdp_poll(freerdp* instance) {
    HANDLE handles[64];
    if (freerdp_shall_disconnect_context(instance->context)) return 0;
    DWORD count = freerdp_get_event_handles(instance->context, handles, 64);
    if (!count || count > 64) return -1;
    DWORD status = WaitForMultipleObjects(count, handles, FALSE, 10);
    if (status == WAIT_FAILED) return -1;
    if (status != WAIT_TIMEOUT && !freerdp_check_event_handles(instance->context)) return -1;
    return 1;
}
API void urdp_abort(freerdp* instance) {
    BOOL ignored = freerdp_abort_connect_context(instance->context); (void)ignored;
}
API int urdp_pointer(freerdp* instance, UINT16 x, UINT16 y, int buttons, int wheel) {
    local_context* local = (local_context*)instance->context;
    rdpInput* input = instance->context->input;
    if (!freerdp_input_send_mouse_event(input, PTR_FLAGS_MOVE, x, y)) return 0;
    UINT16 flags[3] = { PTR_FLAGS_BUTTON1, PTR_FLAGS_BUTTON3, PTR_FLAGS_BUTTON2 };
    for (int i = 0; i < 3; i++) {
        int mask = 1 << i;
        if ((buttons & mask) != (local->buttons & mask) &&
            !freerdp_input_send_mouse_event(input, flags[i] | ((buttons & mask) ? PTR_FLAGS_DOWN : 0), x, y)) return 0;
    }
    local->buttons = buttons;
    if (wheel && !freerdp_input_send_mouse_event(input, PTR_FLAGS_WHEEL |
        (wheel > 0 ? 120 : PTR_FLAGS_WHEEL_NEGATIVE | (0x100 - 120)), 0, 0)) return 0;
    return 1;
}
API int urdp_key(freerdp* instance, UINT32 code, int down) {
    UINT16 flags = down ? KBD_FLAGS_DOWN : KBD_FLAGS_RELEASE;
    if (code & 0x100) flags |= KBD_FLAGS_EXTENDED;
    return freerdp_input_send_keyboard_event(instance->context->input, flags, (UINT16)(code & 0xff));
}
API void urdp_free(freerdp* instance) {
    if (!instance) return;
    BOOL ignored = freerdp_disconnect(instance); (void)ignored;
    if (instance->context->gdi) gdi_free(instance);
    freerdp_context_free(instance); freerdp_free(instance);
}
