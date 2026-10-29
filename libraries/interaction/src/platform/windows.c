/* Windows: the user's own settings (double-click time, drag rectangle, wheel lines, caret blink, light or dark apps,
 * animations, high contrast). Context menus open when the right button is released, as Explorer's do. */
#include "../interaction.h"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static bool dark_apps(void) {
    DWORD light = 1, size = sizeof light;
    LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                                  L"AppsUseLightTheme", RRF_RT_REG_DWORD, NULL, &light, &size);
    return status == ERROR_SUCCESS && light == 0;
}

void platform_conventions(conventions *c) {
    UINT lines = 3;
    BOOL animations = TRUE;
    HIGHCONTRASTW contrast = {sizeof contrast};
    SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animations, 0);
    bool high_contrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof contrast, &contrast, 0) && (contrast.dwFlags & HCF_HIGHCONTRASTON);
    UINT blink = GetCaretBlinkTime();
    /* WHEEL_PAGESCROLL asks for a page per notch: about 20 lines. */
    double step = (lines == WHEEL_PAGESCROLL ? 20 : lines) * 16.0;
    *c = (conventions){
        .form_factor = "desktop", .primary = MOD_CONTROL,
        .double_click_ms = GetDoubleClickTime(), .long_press_ms = 500,
        .drag_threshold = GetSystemMetrics(SM_CXDRAG) > 0 ? GetSystemMetrics(SM_CXDRAG) : 4, .touch_slop = 8,
        .wheel_step = step, .caret_blink_ms = blink == INFINITE ? 0 : blink, .minimum_target = 24,
        .scrollbars = "classic", .context_menu_on_release = true,
        .hover = true, .back_button = false, .dark = dark_apps(), .reduced_motion = !animations, .high_contrast = high_contrast,
    };
}

void platform_back_unhandled(aqua_window *window) { (void)window; }
void platform_back_guard(bool enabled, void (*back)(void *data), void *data) { (void)enabled; (void)back; (void)data; }
void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard) {
    *left = *top = *right = *bottom = 0;
    *keyboard = false;
}
