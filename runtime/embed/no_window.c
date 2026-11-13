/* <aqua/window.h> for native library outputs (runtime/embed), which have no window system yet (docs/native-libraries.md):
   creating a window fails with a reason, and nothing else is ever called with a window. The host core links these
   because its function table (api.c) and window objects (window_object.c) name them. */
#include "../host/host.h"
#include <aqua/window.h>
#include <stdlib.h>
#include <string.h>

aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    (void)rt; (void)width; (void)height; (void)title; (void)flags;
    const char *text = "Native library outputs cannot open windows yet.";
    if (error) { *error = malloc(strlen(text) + 1); strcpy(*error, text); }
    return NULL;
}
void aqua_window_hint(int32_t hint, int32_t value) { (void)hint; (void)value; }
void aqua_window_destroy(aqua_window *window) { (void)window; }
aqua_runtime *aqua_window_runtime(aqua_window *window) { (void)window; return NULL; }
bool aqua_window_should_close(aqua_window *window) { (void)window; return true; }
void aqua_window_set_should_close(aqua_window *window, bool close) { (void)window; (void)close; }
void aqua_window_size(aqua_window *window, int32_t *width, int32_t *height) { (void)window; *width = 0; *height = 0; }
void aqua_window_framebuffer_size(aqua_window *window, int32_t *width, int32_t *height) { (void)window; *width = 0; *height = 0; }
float aqua_window_content_scale(aqua_window *window) { (void)window; return 1; }
void aqua_window_set_size(aqua_window *window, int32_t width, int32_t height) { (void)window; (void)width; (void)height; }
void aqua_window_set_position(aqua_window *window, int32_t x, int32_t y) { (void)window; (void)x; (void)y; }
void aqua_window_set_title(aqua_window *window, const char *title) { (void)window; (void)title; }
bool aqua_window_set_fullscreen(aqua_window *window, bool fullscreen) { (void)window; (void)fullscreen; return false; }
bool aqua_window_focused(aqua_window *window) { (void)window; return false; }
void aqua_windows_poll(aqua_runtime *rt) { (void)rt; }
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height) { (void)rt; *width = 0; *height = 0; return false; }
double aqua_window_time(aqua_runtime *rt) { return aqua_now_ms(rt) / 1000; }
int32_t aqua_window_key(aqua_window *window, int32_t key) { (void)window; (void)key; return 0; }
int32_t aqua_window_mouse_button(aqua_window *window, int32_t button) { (void)window; (void)button; return 0; }
void aqua_window_cursor(aqua_window *window, double *x, double *y) { (void)window; *x = 0; *y = 0; }
void aqua_window_set_cursor_mode(aqua_window *window, int32_t mode) { (void)window; (void)mode; }
bool aqua_window_set_cursor_shape(aqua_window *window, int32_t shape) { (void)window; (void)shape; return false; }
bool aqua_window_set_cursor_image(aqua_window *window, int32_t width, int32_t height, const uint8_t *rgba, int32_t hot_x, int32_t hot_y) {
    (void)window; (void)width; (void)height; (void)rgba; (void)hot_x; (void)hot_y; return false;
}
bool aqua_window_capture_pointer(aqua_window *window, bool capture) { (void)window; (void)capture; return false; }
void aqua_window_listen(aqua_window *window, aqua_window_listener listener, void *data) { (void)window; (void)listener; (void)data; }
void aqua_window_unlisten(aqua_window *window, aqua_window_listener listener, void *data) { (void)window; (void)listener; (void)data; }
void aqua_window_text_input(aqua_window *window, bool enabled) { (void)window; (void)enabled; }
bool aqua_window_text_input_enabled(aqua_window *window) { (void)window; return false; }
void aqua_window_text_rect(aqua_window *window, int32_t x, int32_t y, int32_t width, int32_t height) { (void)window; (void)x; (void)y; (void)width; (void)height; }
bool aqua_window_composition_supported(aqua_window *window) { (void)window; return false; }
void aqua_window_safe_area(aqua_window *window, float *left, float *top, float *right, float *bottom, bool *keyboard_visible) {
    (void)window; *left = *top = *right = *bottom = 0; if (keyboard_visible) *keyboard_visible = false;
}
bool aqua_window_surface(aqua_window *window, aqua_surface *surface) { (void)window; surface->kind = AQUA_SURFACE_NONE; surface->display = surface->handle = NULL; return false; }
bool aqua_window_present(aqua_window *window, const uint8_t *rgba, int32_t width, int32_t height, int32_t stride) {
    (void)window; (void)rgba; (void)width; (void)height; (void)stride; return false;
}
void aqua_window_make_current(aqua_window *window) { (void)window; }
void aqua_window_swap(aqua_window *window) { (void)window; }
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval) { (void)rt; (void)interval; }
void *aqua_gl_proc(const char *name) { (void)name; return NULL; }
char *aqua_clipboard_text(aqua_runtime *rt) { (void)rt; return NULL; }
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8) { (void)rt; (void)utf8; return false; }
void *aqua_window_native(aqua_window *window) { (void)window; return NULL; }
#if defined(AQUA_OS_WINDOWS)
void aqua_window_add_message_hook(aqua_window *window, aqua_window_message_hook hook, void *data) { (void)window; (void)hook; (void)data; }
void aqua_window_remove_message_hook(aqua_window *window, aqua_window_message_hook hook, void *data) { (void)window; (void)hook; (void)data; }
#endif
/* Monitors and display modes: none on this platform (see aqua/window.h). */
int32_t aqua_monitor_count(aqua_runtime *rt) { (void)rt; return 0; }
bool aqua_monitor_info_at(aqua_runtime *rt, int32_t index, aqua_monitor_info *info) { (void)rt; (void)index; (void)info; return false; }
int32_t aqua_monitor_modes(aqua_runtime *rt, int32_t index, aqua_display_mode *modes, int32_t capacity) { (void)rt; (void)index; (void)modes; (void)capacity; return 0; }
int32_t aqua_window_monitor(aqua_window *w) { (void)w; return -1; }
void aqua_window_position(aqua_window *w, int32_t *x, int32_t *y) { (void)w; *x = 0; *y = 0; }
bool aqua_window_set_display_mode(aqua_window *w, int32_t mode, int32_t monitor, int32_t x, int32_t y, int32_t width, int32_t height, int32_t refresh_hz) {
    (void)w; (void)mode; (void)monitor; (void)x; (void)y; (void)width; (void)height; (void)refresh_hz; return false;
}
int32_t aqua_window_display_mode(aqua_window *w) { (void)w; return 0; }
