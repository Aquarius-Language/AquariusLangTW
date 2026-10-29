/* Web: the page's media queries (coarse pointers mean a phone or tablet, hover, dark mode, reduced motion, contrast)
 * through web-glue/mediaquery, and the browser's back button through web-glue/history: while the application handles
 * back, a history entry keeps the browser on the page and its use arrives as a back event; a back no handler takes
 * leaves the page. Browsers give pages no double-click time, so the common default is used. */
#include "../interaction.h"
#include <string.h>

AQUA_WEB_IMPORT(mediaquery_matches) int32_t web_matches(const char *query, int32_t length);
AQUA_WEB_IMPORT(history_guard) void web_history_guard(void (*popped)(void *data), void *data);
AQUA_WEB_IMPORT(history_release) void web_history_release(void);
AQUA_WEB_IMPORT(history_back) void web_history_back(void);

static bool matches(const char *query) { return web_matches(query, (int32_t)strlen(query)) == 1; }

void platform_conventions(conventions *c) {
    bool coarse = matches("(pointer: coarse)");
    *c = (conventions){
        .form_factor = coarse ? "mobile" : "desktop", .primary = MOD_CONTROL, .either_primary = true,
        .double_click_ms = 500, .long_press_ms = 500, .drag_threshold = 4, .touch_slop = 10,
        .wheel_step = 100, .caret_blink_ms = 1000, .minimum_target = coarse ? 44 : 24,
        .scrollbars = coarse ? "overlay" : "classic", .context_menu_on_release = false,
        .hover = matches("(hover: hover)"), .back_button = true, .dark = matches("(prefers-color-scheme: dark)"),
        .reduced_motion = matches("(prefers-reduced-motion: reduce)"),
        .high_contrast = matches("(prefers-contrast: more)") || matches("(forced-colors: active)"),
    };
}

void platform_back_unhandled(aqua_window *window) { (void)window; web_history_back(); }

void platform_back_guard(bool enabled, void (*back)(void *data), void *data) {
    if (enabled) web_history_guard(back, data);
    else web_history_release();
}

void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard) {
    *left = *top = *right = *bottom = 0;
    *keyboard = false;
}
