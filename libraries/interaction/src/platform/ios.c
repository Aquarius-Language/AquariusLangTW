/* iOS and iPadOS: UIKit's conventions (double tap 350 ms, long press 500 ms, touch slop 10 points, 44-point targets),
 * Command as the primary modifier for hardware keyboards, overlay scroll bars, and context menus on long press. Dark
 * mode follows the system appearance as Core Foundation's preferences report it. The safe area is not reported yet. */
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
        .form_factor = "mobile", .primary = MOD_SUPER, .apple = true,
        .double_click_ms = 350, .long_press_ms = 500, .drag_threshold = 10, .touch_slop = 10,
        .wheel_step = 40, .caret_blink_ms = 1000, .minimum_target = 44,
        .scrollbars = "overlay", .context_menu_on_release = false,
        .hover = false, .back_button = false, .dark = dark_mode(),
    };
}

void platform_back_unhandled(aqua_window *window) { (void)window; }
void platform_back_guard(bool enabled, void (*back)(void *data), void *data) { (void)enabled; (void)back; (void)data; }
