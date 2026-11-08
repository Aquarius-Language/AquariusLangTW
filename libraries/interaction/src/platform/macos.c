/* macOS: AppKit's defaults (double click 500 ms, drag threshold 4, caret cycle 1000 ms, wheel lines of 10 points),
 * Command as the primary modifier, overlay scroll bars and context menus on press, as Finder's are. Dark mode follows
 * the system appearance (AppleInterfaceStyle in the global domain, read through Core Foundation's preferences). */
#include "../interaction.h"
#include <CoreFoundation/CoreFoundation.h>

static bool dark_mode(void) {
    CFStringRef style = CFPreferencesCopyAppValue(CFSTR("AppleInterfaceStyle"), kCFPreferencesAnyApplication);
    bool dark = style && CFGetTypeID(style) == CFStringGetTypeID() && CFStringCompare(style, CFSTR("Dark"), kCFCompareCaseInsensitive) == kCFCompareEqualTo;
    if (style) CFRelease(style);
    return dark;
}

void platform_conventions(conventions *c) {
    *c = (conventions){
        .form_factor = "desktop", .primary = MOD_SUPER, .apple = true,
        .double_click_ms = 500, .long_press_ms = 500, .drag_threshold = 4, .touch_slop = 8,
        .wheel_step = 40, .caret_blink_ms = 1000, .minimum_target = 24,
        .scrollbars = "overlay", .context_menu_on_release = false,
        .hover = true, .back_button = false, .dark = dark_mode(),
    };
}

void platform_back_unhandled(aqua_window *window) { (void)window; }
void platform_back_guard(bool enabled, void (*back)(void *data), void *data) { (void)enabled; (void)back; (void)data; }
void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard) {
    *left = *top = *right = *bottom = 0;
    *keyboard = false;
}
