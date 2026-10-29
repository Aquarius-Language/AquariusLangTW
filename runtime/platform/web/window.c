/* aqua/window.h on the web: each window is a <canvas> the glue's window domain (web-glue/window) creates. DOM input is
   queued by the glue and dispatched to listeners during aqua_windows_poll, in order, like desktop events. */
#include "../../host/host.h"
#include <stdlib.h>
#include <string.h>

#define WEB_IMPORT(name) __attribute__((import_module("aqua_web"), import_name(#name)))
WEB_IMPORT(window_create) int32_t web_window_create(aqua_window *window, int32_t width, int32_t height, const char *title, uint32_t flags);
WEB_IMPORT(window_destroy) void web_window_destroy(int32_t handle);
WEB_IMPORT(window_poll) void web_window_poll(int32_t handle);
WEB_IMPORT(window_size) void web_window_size(int32_t handle, int32_t *size); /* logical w, h, pixel w, h */
WEB_IMPORT(window_scale) double web_window_scale(int32_t handle);
WEB_IMPORT(window_set_size) void web_window_set_size(int32_t handle, int32_t width, int32_t height);
WEB_IMPORT(window_set_title) void web_window_set_title(int32_t handle, const char *title);
WEB_IMPORT(window_set_fullscreen) int32_t web_window_set_fullscreen(int32_t handle, int32_t fullscreen);
WEB_IMPORT(window_focused) int32_t web_window_focused(int32_t handle);
WEB_IMPORT(window_display_size) int32_t web_window_display_size(int32_t *size);
WEB_IMPORT(window_key) int32_t web_window_key(int32_t handle, int32_t key);
WEB_IMPORT(window_button) int32_t web_window_button(int32_t handle, int32_t button);
WEB_IMPORT(window_cursor) void web_window_cursor(int32_t handle, double *position);
WEB_IMPORT(window_set_cursor_mode) void web_window_set_cursor_mode(int32_t handle, int32_t mode);
WEB_IMPORT(window_set_cursor_shape) int32_t web_window_set_cursor_shape(int32_t handle, int32_t shape);
WEB_IMPORT(window_set_cursor_image) int32_t web_window_set_cursor_image(int32_t handle, int32_t width, int32_t height, const uint8_t *rgba, int32_t x, int32_t y);
WEB_IMPORT(window_capture_pointer) int32_t web_window_capture_pointer(int32_t handle, int32_t capture);
WEB_IMPORT(window_text_input) void web_window_text_input(int32_t handle, int32_t enabled);
WEB_IMPORT(window_text_rect) void web_window_text_rect(int32_t handle, int32_t x, int32_t y, int32_t width, int32_t height);
WEB_IMPORT(clipboard_text) char *web_clipboard_text(void);
WEB_IMPORT(clipboard_set_text) int32_t web_clipboard_set_text(const char *text);

typedef struct listener { aqua_window_listener run; void *data; } listener;
struct aqua_window {
    aqua_runtime *rt;
    int32_t handle;
    bool should_close, text_enabled;
    listener *listeners; uint32_t listener_count, listener_capacity;
    aqua_window *next;
};
static aqua_window *windows;

/* The glue dispatches each queued DOM event through this export while aqua_windows_poll runs. */
__attribute__((export_name("aqua_web_window_event")))
void aqua_web_window_event(aqua_window *w, int32_t type, int32_t key, int32_t scancode, int32_t action, int32_t mods, int32_t button,
                           double x, double y, uint32_t pointer, int32_t pointer_type, float pressure, const char *text, int32_t cursor) {
    aqua_window_event e = {0};
    e.type = type; e.time = aqua_window_time(w->rt); e.key = key; e.scancode = scancode; e.action = action; e.mods = mods; e.button = button;
    e.x = x; e.y = y; e.pointer = pointer; e.pointer_type = pointer_type; e.pressure = pressure; e.text = text; e.cursor = cursor;
    if (type == AQUA_EVENT_CLOSE) w->should_close = true;
    uint32_t n = w->listener_count;
    listener copy[16];
    listener *list = n <= 16 ? copy : malloc(n * sizeof *list);
    memcpy(list, w->listeners, n * sizeof *list);
    for (uint32_t i = 0; i < n; i++) list[i].run(w, &e, list[i].data);
    if (list != copy) free(list);
}

void aqua_window_hint(int32_t hint, int32_t value) { (void)hint; (void)value; }
aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    aqua_window *w = calloc(1, sizeof *w);
    w->rt = rt;
    w->handle = web_window_create(w, width, height, title ? title : "", flags);
    if (w->handle <= 0) { free(w); if (error) *error = strdup("This page cannot create a canvas."); return NULL; }
    w->next = windows;
    windows = w;
    return w;
}
void aqua_window_destroy(aqua_window *w) {
    if (!w) return;
    aqua_web_window_event(w, AQUA_EVENT_DESTROYED, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, NULL, 0);
    for (aqua_window **p = &windows; *p; p = &(*p)->next) if (*p == w) { *p = w->next; break; }
    web_window_destroy(w->handle);
    free(w->listeners);
    free(w);
}
void web_windows_close(aqua_runtime *rt) { for (aqua_window *w = windows, *next; w; w = next) { next = w->next; if (w->rt == rt) aqua_window_destroy(w); } }
aqua_runtime *aqua_window_runtime(aqua_window *w) { return w->rt; }
bool aqua_window_should_close(aqua_window *w) { return w->should_close; }
void aqua_window_set_should_close(aqua_window *w, bool close) { w->should_close = close; }
void aqua_window_size(aqua_window *w, int32_t *width, int32_t *height) { int32_t s[4]; web_window_size(w->handle, s); *width = s[0]; *height = s[1]; }
void aqua_window_framebuffer_size(aqua_window *w, int32_t *width, int32_t *height) { int32_t s[4]; web_window_size(w->handle, s); *width = s[2]; *height = s[3]; }
float aqua_window_content_scale(aqua_window *w) { return (float)web_window_scale(w->handle); }
void aqua_window_set_size(aqua_window *w, int32_t width, int32_t height) { web_window_set_size(w->handle, width, height); }
void aqua_window_set_position(aqua_window *w, int32_t x, int32_t y) { (void)w; (void)x; (void)y; }
void aqua_window_set_title(aqua_window *w, const char *title) { web_window_set_title(w->handle, title); }
bool aqua_window_set_fullscreen(aqua_window *w, bool fullscreen) { return web_window_set_fullscreen(w->handle, fullscreen) != 0; }
bool aqua_window_focused(aqua_window *w) { return web_window_focused(w->handle) != 0; }
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height) {
    (void)rt;
    int32_t s[2];
    if (!web_window_display_size(s)) return false;
    *width = s[0]; *height = s[1];
    return true;
}
void aqua_windows_poll(aqua_runtime *rt) { for (aqua_window *w = windows; w; w = w->next) if (w->rt == rt) web_window_poll(w->handle); }
double aqua_window_time(aqua_runtime *rt) { (void)rt; return platform_now_ms() / 1000.0; }
int32_t aqua_window_key(aqua_window *w, int32_t key) { return web_window_key(w->handle, key); }
int32_t aqua_window_mouse_button(aqua_window *w, int32_t button) { return web_window_button(w->handle, button); }
void aqua_window_cursor(aqua_window *w, double *x, double *y) { double p[2]; web_window_cursor(w->handle, p); *x = p[0]; *y = p[1]; }
void aqua_window_set_cursor_mode(aqua_window *w, int32_t mode) { web_window_set_cursor_mode(w->handle, mode); }
bool aqua_window_set_cursor_shape(aqua_window *w, int32_t shape) { return web_window_set_cursor_shape(w->handle, shape) != 0; }
bool aqua_window_set_cursor_image(aqua_window *w, int32_t width, int32_t height, const uint8_t *rgba, int32_t x, int32_t y) {
    return web_window_set_cursor_image(w->handle, width, height, rgba, x, y) != 0;
}
bool aqua_window_capture_pointer(aqua_window *w, bool capture) { return web_window_capture_pointer(w->handle, capture) != 0; }
void aqua_window_listen(aqua_window *w, aqua_window_listener run, void *data) {
    if (w->listener_count == w->listener_capacity) {
        w->listener_capacity = w->listener_capacity ? w->listener_capacity * 2 : 4;
        w->listeners = realloc(w->listeners, w->listener_capacity * sizeof *w->listeners);
    }
    w->listeners[w->listener_count].run = run;
    w->listeners[w->listener_count++].data = data;
}
void aqua_window_unlisten(aqua_window *w, aqua_window_listener run, void *data) {
    for (uint32_t i = 0; i < w->listener_count; i++)
        if (w->listeners[i].run == run && w->listeners[i].data == data) {
            memmove(w->listeners + i, w->listeners + i + 1, (w->listener_count - i - 1) * sizeof *w->listeners);
            w->listener_count--;
            return;
        }
}
void aqua_window_text_input(aqua_window *w, bool enabled) { w->text_enabled = enabled; web_window_text_input(w->handle, enabled); }
bool aqua_window_text_input_enabled(aqua_window *w) { return w && w->text_enabled; }
void aqua_window_text_rect(aqua_window *w, int32_t x, int32_t y, int32_t width, int32_t height) { web_window_text_rect(w->handle, x, y, width, height); }
bool aqua_window_composition_supported(aqua_window *w) { (void)w; return true; }
bool aqua_window_surface(aqua_window *w, aqua_surface *s) {
    memset(s, 0, sizeof *s);
    s->kind = AQUA_SURFACE_CANVAS;
    s->handle = (void *)(uintptr_t)w->handle;
    return true;
}
void aqua_window_make_current(aqua_window *w) { (void)w; } /* WebGL contexts belong to their canvas */
void aqua_window_swap(aqua_window *w) { (void)w; }       /* the browser presents after each animation frame */
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval) { (void)rt; (void)interval; }
void *aqua_gl_proc(const char *name) { (void)name; return NULL; } /* gl* functions are glue imports by name */
char *aqua_clipboard_text(aqua_runtime *rt) { (void)rt; return web_clipboard_text(); }
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8) { (void)rt; return web_clipboard_set_text(utf8) != 0; }
void *aqua_window_native(aqua_window *w) { return (void *)(uintptr_t)w->handle; }
