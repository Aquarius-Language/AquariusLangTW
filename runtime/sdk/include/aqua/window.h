/* aqua/window.h — windows, input and rendering surfaces from the platform layer, part of API version 1.
 * One interface on every target: GLFW windows on Windows and Linux, the activity's surface on Android (one window),
 * <canvas> elements on the web. Key, button and cursor codes are GLFW's on every platform, so libraries and scripts
 * see the same numbers. Graphics libraries (Processing, WGPU, GL) and input libraries build on this instead of OS APIs.
 *
 * 平台視窗、輸入與繪圖表面：桌面使用 GLFW、Android 使用活動畫面、網頁使用 canvas，介面一致。
 */
#ifndef AQUA_WINDOW_H
#define AQUA_WINDOW_H
#include "aqua.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct aqua_window aqua_window;

enum {
    AQUA_WINDOW_OPENGL = 1,      /* an OpenGL 3.3 core context (WebGL 2 on the web); otherwise a surface for WebGPU */
    AQUA_WINDOW_HIDDEN = 2,
    AQUA_WINDOW_FIXED_SIZE = 4,
    AQUA_WINDOW_FULLSCREEN = 8,
    AQUA_WINDOW_UNDECORATED = 16,
};

/* GLFW-numbered creation hints applied to the next aqua_window_create (GLFW.WindowHint); unknown hints are ignored on
   targets without them. */
void aqua_window_hint(int32_t hint, int32_t value);
aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error);
void aqua_window_destroy(aqua_window *window);
aqua_runtime *aqua_window_runtime(aqua_window *window);

bool aqua_window_should_close(aqua_window *window);
void aqua_window_set_should_close(aqua_window *window, bool close);
/* Size in logical units (independent of display density) and the drawable size in pixels. */
void aqua_window_size(aqua_window *window, int32_t *width, int32_t *height);
void aqua_window_framebuffer_size(aqua_window *window, int32_t *width, int32_t *height);
float aqua_window_content_scale(aqua_window *window);
void aqua_window_set_size(aqua_window *window, int32_t width, int32_t height);
void aqua_window_set_position(aqua_window *window, int32_t x, int32_t y);
void aqua_window_set_title(aqua_window *window, const char *title);
bool aqua_window_set_fullscreen(aqua_window *window, bool fullscreen);
bool aqua_window_focused(aqua_window *window);
/* The primary display's size in pixels; false when unknown. */
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height);

/* Processes pending OS events of every window, dispatching them to listeners. Frame drivers call it once per frame. */
void aqua_windows_poll(aqua_runtime *rt);
/* Seconds since the window system started (GLFW.GetTime). */
double aqua_window_time(aqua_runtime *rt);

/* Polled input state, GLFW codes: keys GLFW_KEY_*, buttons 0 left 1 right 2 middle; 1 = pressed. */
int32_t aqua_window_key(aqua_window *window, int32_t key);
int32_t aqua_window_mouse_button(aqua_window *window, int32_t button);
void aqua_window_cursor(aqua_window *window, double *x, double *y);
/* GLFW cursor modes: 0x34001 normal, 0x34002 hidden, 0x34003 disabled (pointer lock with relative motion). */
void aqua_window_set_cursor_mode(aqua_window *window, int32_t mode);
/* Standard cursor shapes are GLFW's (GLFW_ARROW_CURSOR ...); 0 restores the default. */
bool aqua_window_set_cursor_shape(aqua_window *window, int32_t shape);
bool aqua_window_set_cursor_image(aqua_window *window, int32_t width, int32_t height, const uint8_t *rgba, int32_t hot_x, int32_t hot_y);
/* Keeps pointer events flowing to the window while a button is held outside it; false where unsupported. */
bool aqua_window_capture_pointer(aqua_window *window, bool capture);

/* Events, delivered in order to every listener during aqua_windows_poll. Coordinates are logical units. */
typedef enum aqua_event_type {
    AQUA_EVENT_KEY = 1,          /* key, scancode, action (0 release 1 press 2 repeat), mods; text = key name or NULL */
    AQUA_EVENT_POINTER = 2,      /* x, y; pointer id, type (0 mouse 1 touch 2 pen), pressure, tilt */
    AQUA_EVENT_BUTTON = 3,       /* button, action, mods, x, y */
    AQUA_EVENT_SCROLL = 4,       /* x, y: wheel offsets, positive y away from the user */
    AQUA_EVENT_FOCUS = 5,        /* action: 1 focused, 0 lost */
    AQUA_EVENT_VISIBLE = 6,      /* action: 1 visible, 0 hidden or minimized */
    AQUA_EVENT_ENTER = 7,        /* action: 1 pointer entered, 0 left */
    AQUA_EVENT_DROP = 8,         /* text = dropped path, index of count */
    AQUA_EVENT_CLOSE = 9,        /* the user asked to close the window */
    AQUA_EVENT_CAPTURE_LOST = 10,
    AQUA_EVENT_DESTROYED = 11,
    AQUA_EVENT_TEXT = 12,        /* text = committed UTF-8 text */
    AQUA_EVENT_COMPOSITION_START = 13,
    AQUA_EVENT_COMPOSITION_UPDATE = 14, /* text = composition, cursor = caret in UTF-16 units */
    AQUA_EVENT_COMPOSITION_END = 15,
    AQUA_EVENT_RESIZE = 16,      /* width/height in x, y (logical units) */
    AQUA_EVENT_BACK = 17,        /* Android back gesture */
    AQUA_EVENT_LIFECYCLE = 18,   /* action: 1 resumed, 0 paused */
} aqua_event_type;

typedef struct aqua_window_event {
    int32_t type;
    double time;                 /* monotonic seconds */
    int32_t key, scancode, action, mods, button;
    double x, y;
    uint32_t pointer;            /* pointer id: 0 for the mouse */
    int32_t pointer_type;
    float pressure, tilt_x, tilt_y;
    const char *text;
    int32_t cursor, index, count;
} aqua_window_event;

typedef void (*aqua_window_listener)(aqua_window *window, const aqua_window_event *event, void *data);
void aqua_window_listen(aqua_window *window, aqua_window_listener listener, void *data);
void aqua_window_unlisten(aqua_window *window, aqua_window_listener listener, void *data);

/* Text input: while enabled, typed text and input-method composition arrive as TEXT and COMPOSITION events; the
   rectangle (logical units) places the input method's candidate window. On Android this shows the soft keyboard. */
void aqua_window_text_input(aqua_window *window, bool enabled);
void aqua_window_text_rect(aqua_window *window, int32_t x, int32_t y, int32_t width, int32_t height);
bool aqua_window_composition_supported(aqua_window *window);

/* What a WebGPU or OpenGL implementation needs to render into the window. */
typedef enum aqua_surface_kind {
    AQUA_SURFACE_NONE = 0, AQUA_SURFACE_WIN32 = 1, AQUA_SURFACE_X11 = 2, AQUA_SURFACE_WAYLAND = 3, AQUA_SURFACE_METAL = 4,
    AQUA_SURFACE_ANDROID = 5, AQUA_SURFACE_CANVAS = 6,
} aqua_surface_kind;
typedef struct aqua_surface {
    aqua_surface_kind kind;
    void *display;               /* HINSTANCE, X11 Display*, wl_display*, NULL */
    void *handle;                /* HWND, X11 Window, wl_surface*, CAMetalLayer*, ANativeWindow*, canvas id (web) */
} aqua_surface;
bool aqua_window_surface(aqua_window *window, aqua_surface *surface);

/* OpenGL windows. */
void aqua_window_make_current(aqua_window *window);
void aqua_window_swap(aqua_window *window);
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval);
void *aqua_gl_proc(const char *name);

/* Plain-text clipboard; NULL when empty or unavailable. The text is freed with aqua_free. */
char *aqua_clipboard_text(aqua_runtime *rt);
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8);

/* The OS window object for OS-specific code (HWND, X11 Window, ANativeWindow*), or NULL. */
void *aqua_window_native(aqua_window *window);

#if defined(AQUA_OS_WINDOWS)
/* Windows only: sees each message of the window procedure first (WM_GETOBJECT for accessibility, ...). Setting
   *handled returns the hook's result instead of the default processing. Linked libraries only (not in the table). */
typedef intptr_t (*aqua_window_message_hook)(aqua_window *window, uint32_t message, uintptr_t wparam, intptr_t lparam, bool *handled, void *data);
void aqua_window_add_message_hook(aqua_window *window, aqua_window_message_hook hook, void *data);
void aqua_window_remove_message_hook(aqua_window *window, aqua_window_message_hook hook, void *data);
#endif

#ifdef __cplusplus
}
#endif
#endif
