/* Linux: GTK's defaults (double click 400 ms, drag threshold 8, caret cycle 1200 ms), which GNOME and KDE applications
 * follow unless the user changed them; a GTK_THEME ending in ":dark" selects dark mode. Context menus open on press. */
#include "../interaction.h"
#include <stdlib.h>
#include <string.h>

void platform_conventions(conventions *c) {
    const char *theme = getenv("GTK_THEME");
    size_t length = theme ? strlen(theme) : 0;
    *c = (conventions){
        .form_factor = "desktop", .primary = MOD_CONTROL,
        .double_click_ms = 400, .long_press_ms = 500, .drag_threshold = 8, .touch_slop = 8,
        .wheel_step = 48, .caret_blink_ms = 1200, .minimum_target = 24,
        .scrollbars = "classic", .context_menu_on_release = false,
        .hover = true, .back_button = false, .dark = length >= 5 && strcmp(theme + length - 5, ":dark") == 0,
    };
}

void platform_back_unhandled(aqua_window *window) { (void)window; }
void platform_back_guard(bool enabled, void (*back)(void *data), void *data) { (void)enabled; (void)back; (void)data; }
void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard) {
    *left = *top = *right = *bottom = 0;
    *keyboard = false;
}
