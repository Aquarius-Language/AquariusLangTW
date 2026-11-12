/* aqua/window.h on Windows, Linux and macOS, over GLFW 3.4 (fetched and pinned by runtime/CMakeLists.txt). On Windows the
   window procedure is subclassed for input-method composition (IMM) and for libraries' message hooks (accessibility,
   pointer capture). With AQUARIUS_REMOTE_WINDOW set, windows are remote (window_remote.c): a viewer shows them, and the
   functions below hand each one's work there (an OpenGL one keeps a hidden GLFW window for its context). */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include <aqua/window.h>
#define GLFW_INCLUDE_NONE
#include <GLFW/glfw3.h>
#if defined(_WIN32)
#define GLFW_EXPOSE_NATIVE_WIN32
#include <windows.h>
#include <imm.h>
#else
#include <dlfcn.h>
#ifdef AQUA_GLFW_X11
#define GLFW_EXPOSE_NATIVE_X11
#endif
#ifdef AQUA_GLFW_WAYLAND
#define GLFW_EXPOSE_NATIVE_WAYLAND
#endif
#if defined(__APPLE__)
#define GLFW_EXPOSE_NATIVE_COCOA
/* A CAMetalLayer for the window's content view (metal_layer.m), which WebGPU renders into. */
void *aqua_metal_layer(void *ns_window);
#endif
#endif
#include <GLFW/glfw3native.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "desktop.h"
#include "window_desktop.h"
#include "window_remote.h"
#include "../../host/host.h"

typedef struct listener { aqua_window_listener run; void *data; } listener;
struct aqua_window {
    aqua_runtime *rt;
    GLFWwindow *glfw;
    listener *listeners; uint32_t listener_count, listener_capacity;
    GLFWcursor *cursor;
    double pointer_x, pointer_y; bool has_pointer;
    bool text_enabled, composing;
    int32_t keypad[11];     /* the key each held keypad key pressed (host_keypad_key), so its release matches */
    remote_window *remote;  /* a window a viewer shows (then glfw is NULL, or a hidden window holding an OpenGL context) */
    uint8_t *readback;      /* a remote OpenGL window's last frame, read on swap */
    aqua_window *next;
#if defined(__APPLE__)
    void *metal_layer;      /* made on the first surface request */
#endif
#if defined(_WIN32)
    HWND hwnd;
    WNDPROC previous;
    RECT caret;
    aqua_window_message_hook hooks[8]; void *hook_data[8]; uint32_t hook_count;
#endif
};

static aqua_window *windows;
static bool initialized, failed;

static void ensure_glfw(void) {
    if (initialized || failed) return;
#if defined(__linux__)
    /* WSLg's D3D12 driver registers pthread TLS destructors; keep it loaded until exit so they stay callable. */
    if (getenv("WSL_DISTRO_NAME")) {
        dlopen("/usr/lib/wsl/lib/libd3d12.so", RTLD_NOW | RTLD_NODELETE);
        dlopen("/usr/lib/wsl/lib/libd3d12core.so", RTLD_NOW | RTLD_NODELETE);
    }
#if defined(AQUA_GLFW_X11) || defined(AQUA_GLFW_WAYLAND)
    glfwInitHint(GLFW_PLATFORM, GLFW_ANY_PLATFORM);
#endif
#ifdef AQUA_GLFW_X11
    /* WSLg's Wayland/EGL path can present empty OpenGL frames; its X11 path renders and supports Vulkan surfaces. */
    const char *platform = getenv("AQUARIUS_GLFW_PLATFORM");
    if ((platform && strcmp(platform, "x11") == 0) || (!platform && getenv("WSL_DISTRO_NAME") && getenv("DISPLAY")))
        glfwInitHint(GLFW_PLATFORM, GLFW_PLATFORM_X11);
#endif
#ifdef AQUA_GLFW_WAYLAND
    if (getenv("AQUARIUS_GLFW_PLATFORM") && strcmp(getenv("AQUARIUS_GLFW_PLATFORM"), "wayland") == 0) glfwInitHint(GLFW_PLATFORM, GLFW_PLATFORM_WAYLAND);
#endif
#endif
    initialized = glfwInit() == GLFW_TRUE;
    failed = !initialized;
}

/* ---- Event dispatch ---- */

static aqua_window *of(GLFWwindow *g) { return glfwGetWindowUserPointer(g); }
static void emit(aqua_window *w, aqua_window_event *e) {
    if (!w) return;
    e->time = w->remote ? remote_time() : glfwGetTime();
    /* Listeners may add or remove listeners: iterate a snapshot. */
    uint32_t n = w->listener_count;
    listener copy[16];
    listener *list = n <= 16 ? copy : malloc(n * sizeof *list);
    memcpy(list, w->listeners, n * sizeof *list);
    for (uint32_t i = 0; i < n; i++) list[i].run(w, e, list[i].data);
    if (list != copy) free(list);
}
void desktop_window_emit(aqua_window *w, aqua_window_event *e) { emit(w, e); }
/* A remote OpenGL window renders into its hidden window at the viewer's pixel size. */
void desktop_window_resized(aqua_window *w) {
    if (!w->remote || !w->glfw) return;
    int32_t width, height;
    remote_framebuffer_size(w->remote, &width, &height);
    glfwSetWindowSize(w->glfw, width, height);
}

static void on_key(GLFWwindow *g, int key, int scancode, int action, int mods) {
    aqua_window *w = of(g);
    aqua_window_event e = {AQUA_EVENT_KEY}; e.key = key; e.scancode = scancode; e.action = action; e.mods = mods; e.text = glfwGetKeyName(key, scancode);
    int32_t *held = w && key >= GLFW_KEY_KP_0 && key <= GLFW_KEY_KP_DECIMAL ? &w->keypad[key - GLFW_KEY_KP_0] : NULL;
    if (held) {
        if (action == GLFW_PRESS || !*held) *held = host_keypad_key(key, (mods & GLFW_MOD_NUM_LOCK) != 0);
        if (*held != key) { e.key = *held; e.text = NULL; }
        if (action == GLFW_RELEASE) *held = 0;
    }
    emit(w, &e);
}
static void on_pointer(GLFWwindow *g, double x, double y) {
    aqua_window *w = of(g);
    if (w) { w->pointer_x = x; w->pointer_y = y; w->has_pointer = true; }
    aqua_window_event e = {AQUA_EVENT_POINTER}; e.x = x; e.y = y; e.pressure = 0.5f;
    emit(w, &e);
}
/* Presses carry the pointer position of the event order: on X11 glfwGetCursorPos queries the live pointer, which can be
   past queued motion and misplace fast drags. */
static void on_button(GLFWwindow *g, int button, int action, int mods) {
    aqua_window *w = of(g);
    aqua_window_event e = {AQUA_EVENT_BUTTON}; e.button = button; e.action = action; e.mods = mods;
    if (w && w->has_pointer) { e.x = w->pointer_x; e.y = w->pointer_y; } else glfwGetCursorPos(g, &e.x, &e.y);
    emit(w, &e);
}
static void on_scroll(GLFWwindow *g, double x, double y) { aqua_window_event e = {AQUA_EVENT_SCROLL}; e.x = x; e.y = y; emit(of(g), &e); }
static void on_focus(GLFWwindow *g, int focused) { aqua_window_event e = {AQUA_EVENT_FOCUS}; e.action = focused; emit(of(g), &e); }
static void on_iconify(GLFWwindow *g, int iconified) { aqua_window_event e = {AQUA_EVENT_VISIBLE}; e.action = !iconified; emit(of(g), &e); }
static void on_enter(GLFWwindow *g, int entered) { aqua_window_event e = {AQUA_EVENT_ENTER}; e.action = entered; emit(of(g), &e); }
static void on_drop(GLFWwindow *g, int count, const char **paths) {
    for (int i = 0; i < count; i++) { aqua_window_event e = {AQUA_EVENT_DROP}; e.text = paths[i]; e.index = i; e.count = count; emit(of(g), &e); }
}
static void on_close(GLFWwindow *g) { aqua_window_event e = {AQUA_EVENT_CLOSE}; emit(of(g), &e); }
static void on_resize(GLFWwindow *g, int width, int height) { aqua_window_event e = {AQUA_EVENT_RESIZE}; e.x = width; e.y = height; emit(of(g), &e); }
/* A new content scale (another monitor, a display scaling change) is a resize too: listeners re-read the scale. */
static void on_content_scale(GLFWwindow *g, float x, float y) {
    (void)x; (void)y;
    int width, height;
    glfwGetWindowSize(g, &width, &height);
    on_resize(g, width, height);
}
static void emit_text(aqua_window *w, int32_t type, const char *text, int32_t cursor) {
    aqua_window_event e = {type}; e.text = text; e.cursor = cursor;
    emit(w, &e);
}
static void on_character(GLFWwindow *g, unsigned int c) {
    if (!c || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) return;
    char utf8[5] = {0};
    if (c < 0x80) utf8[0] = (char)c;
    else if (c < 0x800) { utf8[0] = (char)(0xc0 | c >> 6); utf8[1] = (char)(0x80 | (c & 63)); }
    else if (c < 0x10000) { utf8[0] = (char)(0xe0 | c >> 12); utf8[1] = (char)(0x80 | (c >> 6 & 63)); utf8[2] = (char)(0x80 | (c & 63)); }
    else { utf8[0] = (char)(0xf0 | c >> 18); utf8[1] = (char)(0x80 | (c >> 12 & 63)); utf8[2] = (char)(0x80 | (c >> 6 & 63)); utf8[3] = (char)(0x80 | (c & 63)); }
    emit_text(of(g), AQUA_EVENT_TEXT, utf8, 0);
}

/* ---- Windows: input-method composition and message hooks ---- */
#if defined(_WIN32)
static const wchar_t property_name[] = L"Aquarius.Window";

static void position_ime(aqua_window *w) {
    HIMC context = ImmGetContext(w->hwnd);
    if (!context) return;
    COMPOSITIONFORM composition = {0};
    composition.dwStyle = CFS_POINT;
    composition.ptCurrentPos.x = w->caret.left; composition.ptCurrentPos.y = w->caret.top;
    ImmSetCompositionWindow(context, &composition);
    CANDIDATEFORM candidate = {0};
    candidate.dwStyle = CFS_EXCLUDE;
    candidate.ptCurrentPos.x = w->caret.left; candidate.ptCurrentPos.y = w->caret.bottom;
    candidate.rcArea = w->caret;
    ImmSetCandidateWindow(context, &candidate);
    ImmReleaseContext(w->hwnd, context);
}

static void read_composition(aqua_window *w, HIMC context, DWORD flag) {
    LONG bytes = ImmGetCompositionStringW(context, flag, NULL, 0);
    if (bytes < 0 || bytes > 1024 * 1024 || bytes % (LONG)sizeof(wchar_t)) return;
    wchar_t *wide = calloc((size_t)bytes + sizeof(wchar_t), 1);
    if (bytes && ImmGetCompositionStringW(context, flag, wide, (DWORD)bytes) != bytes) { free(wide); return; }
    int count = bytes / (int)sizeof(wchar_t);
    if (flag == GCS_RESULTSTR) {
        /* Deliver the result here rather than letting DefWindowProc generate it again. */
        for (int i = 0; i < count; i++) {
            unsigned int c = wide[i];
            if (c >= 0xd800 && c <= 0xdbff && i + 1 < count && wide[i + 1] >= 0xdc00 && wide[i + 1] <= 0xdfff) c = 0x10000 + ((c - 0xd800) << 10) + (wide[++i] - 0xdc00);
            else if (c >= 0xd800 && c <= 0xdfff) c = 0xfffd;
            on_character(w->glfw, c);
        }
    } else {
        LONG cursor = ImmGetCompositionStringW(context, GCS_CURSORPOS, NULL, 0);
        int length = count ? WideCharToMultiByte(CP_UTF8, 0, wide, count, NULL, 0, NULL, NULL) : 0;
        char *utf8 = malloc((size_t)length + 1);
        if (length) WideCharToMultiByte(CP_UTF8, 0, wide, count, utf8, length, NULL, NULL);
        utf8[length] = 0;
        emit_text(w, AQUA_EVENT_COMPOSITION_UPDATE, utf8, cursor < 0 ? 0 : cursor > count ? count : cursor);
        free(utf8);
    }
    free(wide);
}

static void end_composition(aqua_window *w) {
    if (!w->composing) return;
    w->composing = false;
    emit_text(w, AQUA_EVENT_COMPOSITION_END, "", 0);
}

static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    aqua_window *w = GetPropW(hwnd, property_name);
    for (uint32_t i = 0; i < w->hook_count; i++) {
        bool handled = false;
        intptr_t result = w->hooks[i](w, message, (uintptr_t)wparam, (intptr_t)lparam, &handled, w->hook_data[i]);
        if (handled) return (LRESULT)result;
    }
    if (message == WM_CAPTURECHANGED) { aqua_window_event e = {AQUA_EVENT_CAPTURE_LOST}; emit(w, &e); }
    if (w->text_enabled) {
        switch (message) {
        case WM_IME_SETCONTEXT: lparam &= ~ISC_SHOWUICOMPOSITIONWINDOW; break;
        case WM_IME_STARTCOMPOSITION:
            if (!w->composing) { w->composing = true; emit_text(w, AQUA_EVENT_COMPOSITION_START, "", 0); }
            position_ime(w);
            return 0;
        case WM_IME_COMPOSITION: {
            HIMC context = ImmGetContext(hwnd);
            if (context) {
                if (lparam & GCS_RESULTSTR) read_composition(w, context, GCS_RESULTSTR);
                if (lparam & (GCS_COMPSTR | GCS_CURSORPOS)) {
                    if (!w->composing) { w->composing = true; emit_text(w, AQUA_EVENT_COMPOSITION_START, "", 0); }
                    read_composition(w, context, GCS_COMPSTR);
                } else if (!lparam) emit_text(w, AQUA_EVENT_COMPOSITION_UPDATE, "", 0);
                ImmReleaseContext(hwnd, context);
            }
            return 0;
        }
        case WM_IME_ENDCOMPOSITION: end_composition(w); return 0;
        case WM_IME_CHAR: return 0;
        case WM_KILLFOCUS: end_composition(w); break;
        }
    }
    return CallWindowProcW(w->previous, hwnd, message, wparam, lparam);
}

void aqua_window_add_message_hook(aqua_window *w, aqua_window_message_hook hook, void *data) {
    if (w->hook_count < 8) { w->hooks[w->hook_count] = hook; w->hook_data[w->hook_count++] = data; }
}
void aqua_window_remove_message_hook(aqua_window *w, aqua_window_message_hook hook, void *data) {
    for (uint32_t i = 0; i < w->hook_count; i++)
        if (w->hooks[i] == hook && w->hook_data[i] == data) {
            memmove(w->hooks + i, w->hooks + i + 1, (w->hook_count - i - 1) * sizeof *w->hooks);
            memmove(w->hook_data + i, w->hook_data + i + 1, (w->hook_count - i - 1) * sizeof *w->hook_data);
            w->hook_count--;
            return;
        }
}
#endif

/* ---- The runtime loop waits on window events while windows are open ---- */
static bool wait_events(double timeout_ms) {
    if (!windows) return false;
    glfwWaitEventsTimeout(timeout_ms >= 1e8 ? 3600.0 : timeout_ms / 1000.0);
    return true;
}
static void wake_events(void) { if (initialized && windows) glfwPostEmptyEvent(); }

/* ---- window.h ---- */

void aqua_window_hint(int32_t hint, int32_t value) { ensure_glfw(); if (initialized) glfwWindowHint(hint, value); }

static aqua_window *create_remote(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    aqua_window *w = calloc(1, sizeof *w);
    w->rt = rt;
    if (flags & AQUA_WINDOW_OPENGL) {
        ensure_glfw();
        if (initialized) {
            glfwWindowHint(GLFW_CLIENT_API, GLFW_OPENGL_API);
            glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 3);
            glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 3);
            glfwWindowHint(GLFW_OPENGL_PROFILE, GLFW_OPENGL_CORE_PROFILE);
            glfwWindowHint(GLFW_OPENGL_FORWARD_COMPAT, GLFW_TRUE);
            glfwWindowHint(GLFW_VISIBLE, GLFW_FALSE);
            glfwWindowHint(GLFW_SCALE_FRAMEBUFFER, GLFW_FALSE);
            w->glfw = glfwCreateWindow(width > 0 ? width : 1, height > 0 ? height : 1, title ? title : "", NULL, NULL);
        }
        if (!w->glfw) {
            const char *message = NULL;
            if (initialized) glfwGetError(&message);
            if (error) *error = strdup(message ? message : "A remote OpenGL window needs an OpenGL context, and none is available.");
            free(w);
            return NULL;
        }
        glfwSetWindowUserPointer(w->glfw, w);
    }
    w->remote = remote_open(w, width, height, title ? title : "", flags, error);
    if (!w->remote) { if (w->glfw) glfwDestroyWindow(w->glfw); free(w); return NULL; }
    w->next = windows;
    windows = w;
    return w;
}

aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    if (remote_enabled()) return create_remote(rt, width, height, title, flags, error);
    ensure_glfw();
    if (!initialized) {
        const char *message = NULL;
        glfwGetError(&message);
        if (error) *error = strdup(message ? message : "The window system is unavailable.");
        return NULL;
    }
    if (flags & AQUA_WINDOW_OPENGL) {
        glfwWindowHint(GLFW_CLIENT_API, GLFW_OPENGL_API);
        glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 3);
        glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 3);
        glfwWindowHint(GLFW_OPENGL_PROFILE, GLFW_OPENGL_CORE_PROFILE);
        glfwWindowHint(GLFW_OPENGL_FORWARD_COMPAT, GLFW_TRUE);
    } else glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API);
    glfwWindowHint(GLFW_VISIBLE, flags & AQUA_WINDOW_HIDDEN ? GLFW_FALSE : GLFW_TRUE);
    glfwWindowHint(GLFW_RESIZABLE, flags & AQUA_WINDOW_FIXED_SIZE ? GLFW_FALSE : GLFW_TRUE);
    glfwWindowHint(GLFW_DECORATED, flags & AQUA_WINDOW_UNDECORATED ? GLFW_FALSE : GLFW_TRUE);
    GLFWmonitor *monitor = flags & AQUA_WINDOW_FULLSCREEN ? glfwGetPrimaryMonitor() : NULL;
    GLFWwindow *g = glfwCreateWindow(width, height, title ? title : "", monitor, NULL);
    if (!g) {
        const char *message = NULL;
        glfwGetError(&message);
        if (error) *error = strdup(message ? message : "Cannot create a window.");
        return NULL;
    }
    aqua_window *w = calloc(1, sizeof *w);
    w->rt = rt; w->glfw = g;
    glfwSetWindowUserPointer(g, w);
    glfwSetKeyCallback(g, on_key);
    glfwSetCursorPosCallback(g, on_pointer);
    glfwSetMouseButtonCallback(g, on_button);
    glfwSetScrollCallback(g, on_scroll);
    glfwSetWindowFocusCallback(g, on_focus);
    glfwSetWindowIconifyCallback(g, on_iconify);
    glfwSetCursorEnterCallback(g, on_enter);
    glfwSetDropCallback(g, on_drop);
    glfwSetWindowCloseCallback(g, on_close);
    glfwSetWindowSizeCallback(g, on_resize);
    glfwSetWindowContentScaleCallback(g, on_content_scale);
    glfwSetCharCallback(g, on_character);
    glfwSetInputMode(g, GLFW_LOCK_KEY_MODS, GLFW_TRUE);
    /* A click shorter than a frame is still seen by polled button state. */
    glfwSetInputMode(g, GLFW_STICKY_MOUSE_BUTTONS, GLFW_TRUE);
#if defined(_WIN32)
    w->hwnd = glfwGetWin32Window(g);
    SetPropW(w->hwnd, property_name, w);
    w->previous = (WNDPROC)SetWindowLongPtrW(w->hwnd, GWLP_WNDPROC, (LONG_PTR)window_proc);
#endif
    w->next = windows;
    windows = w;
    desktop_set_window_loop(wait_events, wake_events);
    return w;
}

void aqua_window_destroy(aqua_window *w) {
    if (!w) return;
    aqua_window_event e = {AQUA_EVENT_DESTROYED};
    emit(w, &e);
    for (aqua_window **p = &windows; *p; p = &(*p)->next) if (*p == w) { *p = w->next; break; }
    if (w->remote) {
        remote_close(w->remote);
        if (w->glfw) glfwDestroyWindow(w->glfw);
        free(w->readback);
        free(w->listeners);
        free(w);
        return;
    }
#if defined(_WIN32)
    SetWindowLongPtrW(w->hwnd, GWLP_WNDPROC, (LONG_PTR)w->previous);
    RemovePropW(w->hwnd, property_name);
#endif
    if (w->cursor) glfwDestroyCursor(w->cursor);
    glfwDestroyWindow(w->glfw);
    free(w->listeners);
    free(w);
}

aqua_runtime *aqua_window_runtime(aqua_window *w) { return w->rt; }
bool aqua_window_should_close(aqua_window *w) { return w->remote ? remote_should_close(w->remote) : glfwWindowShouldClose(w->glfw); }
void aqua_window_set_should_close(aqua_window *w, bool close) {
    if (w->remote) remote_set_should_close(w->remote, close); else glfwSetWindowShouldClose(w->glfw, close);
}
void aqua_window_size(aqua_window *w, int32_t *width, int32_t *height) {
    if (w->remote) remote_size(w->remote, width, height); else glfwGetWindowSize(w->glfw, width, height);
}
void aqua_window_framebuffer_size(aqua_window *w, int32_t *width, int32_t *height) {
    if (w->remote && w->glfw) glfwGetFramebufferSize(w->glfw, width, height);   /* the hidden window follows the viewer */
    else if (w->remote) remote_framebuffer_size(w->remote, width, height);
    else glfwGetFramebufferSize(w->glfw, width, height);
}
float aqua_window_content_scale(aqua_window *w) {
    if (w->remote) return remote_scale(w->remote);
    float x = 1, y = 1;
    glfwGetWindowContentScale(w->glfw, &x, &y);
    return x;
}
/* Nothing covers a desktop window (or a remote one, whose viewer shows it inside a window of its own). */
void aqua_window_safe_area(aqua_window *w, float *left, float *top, float *right, float *bottom, bool *keyboard_visible) {
    (void)w;
    *left = *top = *right = *bottom = 0;
    *keyboard_visible = false;
}
void aqua_window_set_size(aqua_window *w, int32_t width, int32_t height) {
    if (w->remote) remote_set_size(w->remote, width, height); else glfwSetWindowSize(w->glfw, width, height);
}
void aqua_window_set_position(aqua_window *w, int32_t x, int32_t y) { if (!w->remote) glfwSetWindowPos(w->glfw, x, y); }
void aqua_window_set_title(aqua_window *w, const char *title) {
    if (w->remote) remote_set_title(w->remote, title); else glfwSetWindowTitle(w->glfw, title);
}
bool aqua_window_set_fullscreen(aqua_window *w, bool fullscreen) {
    if (w->remote) return false;
    GLFWmonitor *monitor = glfwGetPrimaryMonitor();
    const GLFWvidmode *mode = monitor ? glfwGetVideoMode(monitor) : NULL;
    if (!mode) return false;
    /* At the desktop's own mode this is a borderless full-screen window: it stays on screen when another window takes
       the focus instead of minimizing (GLFW's auto-iconify), as borderless windows do. */
    if (fullscreen) glfwSetWindowAttrib(w->glfw, GLFW_AUTO_ICONIFY, GLFW_FALSE);
    if (fullscreen) glfwSetWindowMonitor(w->glfw, monitor, 0, 0, mode->width, mode->height, mode->refreshRate);
    else glfwSetWindowMonitor(w->glfw, NULL, mode->width / 4, mode->height / 4, mode->width / 2, mode->height / 2, 0);
    return true;
}
bool aqua_window_focused(aqua_window *w) { return w->remote ? remote_focused(w->remote) : glfwGetWindowAttrib(w->glfw, GLFW_FOCUSED) != 0; }
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height) {
    (void)rt;
    if (remote_enabled()) return false;   /* the viewer's screen is not this machine's */
    ensure_glfw();
    GLFWmonitor *monitor = initialized ? glfwGetPrimaryMonitor() : NULL;
    const GLFWvidmode *mode = monitor ? glfwGetVideoMode(monitor) : NULL;
    if (!mode) return false;
    *width = mode->width; *height = mode->height;
    return true;
}

void aqua_windows_poll(aqua_runtime *rt) {
    (void)rt;
    if (remote_enabled()) remote_poll();
    if (initialized) glfwPollEvents();
}
double aqua_window_time(aqua_runtime *rt) {
    if (remote_enabled()) return remote_time();
    ensure_glfw();
    return initialized ? glfwGetTime() : aqua_now_ms(rt) / 1000.0;
}

int32_t aqua_window_key(aqua_window *w, int32_t key) {
    if (w->remote) return remote_key(w->remote, key);
    for (int i = 0; i < 11; i++) if (w->keypad[i] == key && key != GLFW_KEY_KP_0 + i) return GLFW_PRESS;   /* a navigation key held on the keypad */
    if (key >= GLFW_KEY_KP_0 && key <= GLFW_KEY_KP_DECIMAL && w->keypad[key - GLFW_KEY_KP_0] != 0 && w->keypad[key - GLFW_KEY_KP_0] != key) return GLFW_RELEASE;
    return glfwGetKey(w->glfw, key);
}
int32_t aqua_window_mouse_button(aqua_window *w, int32_t button) {
    return w->remote ? remote_mouse_button(w->remote, button) : glfwGetMouseButton(w->glfw, button);
}
void aqua_window_cursor(aqua_window *w, double *x, double *y) { if (w->remote) remote_cursor(w->remote, x, y); else glfwGetCursorPos(w->glfw, x, y); }
void aqua_window_set_cursor_mode(aqua_window *w, int32_t mode) {
    /* A viewer can hide its pointer but not lock it: disabled (relative motion) shows as hidden. */
    if (w->remote) { remote_set_cursor(w->remote, mode == GLFW_CURSOR_NORMAL ? 0 : -1); return; }
    glfwSetInputMode(w->glfw, GLFW_CURSOR, mode);
    if (mode == GLFW_CURSOR_DISABLED && glfwRawMouseMotionSupported()) glfwSetInputMode(w->glfw, GLFW_RAW_MOUSE_MOTION, GLFW_TRUE);
}
bool aqua_window_set_cursor_shape(aqua_window *w, int32_t shape) {
    if (w->remote) { remote_set_cursor(w->remote, shape); return true; }
    GLFWcursor *cursor = shape ? glfwCreateStandardCursor(shape) : NULL;
    if (shape && !cursor) return false;
    glfwSetCursor(w->glfw, cursor);
    if (w->cursor) glfwDestroyCursor(w->cursor);
    w->cursor = cursor;
    return true;
}
bool aqua_window_set_cursor_image(aqua_window *w, int32_t width, int32_t height, const uint8_t *rgba, int32_t hot_x, int32_t hot_y) {
    if (w->remote) return false;
    GLFWimage image = {width, height, (unsigned char *)rgba};
    GLFWcursor *cursor = glfwCreateCursor(&image, hot_x, hot_y);
    if (!cursor) return false;
    glfwSetCursor(w->glfw, cursor);
    if (w->cursor) glfwDestroyCursor(w->cursor);
    w->cursor = cursor;
    return true;
}
bool aqua_window_capture_pointer(aqua_window *w, bool capture) {
#if defined(_WIN32)
    if (w->remote) return false;   /* the viewer keeps a pressed pointer's events itself */
    if (capture) { SetCapture(w->hwnd); return GetCapture() == w->hwnd; }
    return ReleaseCapture() != 0;
#else
    (void)w; (void)capture;
    return false;
#endif
}

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

void aqua_window_text_input(aqua_window *w, bool enabled) {
    if (w->text_enabled == enabled) return;
    if (w->remote) { w->text_enabled = enabled; remote_text_input(w->remote, enabled); return; }
#if defined(_WIN32)
    if (!enabled && w->composing) {
        HIMC context = ImmGetContext(w->hwnd);
        if (context) { ImmNotifyIME(context, NI_COMPOSITIONSTR, CPS_CANCEL, 0); ImmReleaseContext(w->hwnd, context); }
    }
#endif
    w->composing = false;
    w->text_enabled = enabled;
}
bool aqua_window_text_input_enabled(aqua_window *w) { return w && w->text_enabled; }
void aqua_window_text_rect(aqua_window *w, int32_t x, int32_t y, int32_t width, int32_t height) {
    if (w->remote) { remote_text_rect(w->remote, x, y, width, height); return; }
#if defined(_WIN32)
    w->caret.left = x; w->caret.top = y; w->caret.right = x + width; w->caret.bottom = y + height;
    if (w->text_enabled) position_ime(w);
#else
    (void)w; (void)x; (void)y; (void)width; (void)height;
#endif
}
bool aqua_window_composition_supported(aqua_window *w) {
    if (w->remote) return true;   /* the viewer's input method composes */
#if defined(_WIN32)
    return true;
#else
    return false;
#endif
}

bool aqua_window_surface(aqua_window *w, aqua_surface *s) {
    memset(s, 0, sizeof *s);
    if (w->remote) {
        if (w->glfw) return false;   /* OpenGL: frames go out on swap */
        s->kind = AQUA_SURFACE_FRAMES; s->handle = (void *)(uintptr_t)remote_id(w->remote);
        return true;
    }
#if defined(_WIN32)
    s->kind = AQUA_SURFACE_WIN32; s->display = GetModuleHandleW(NULL); s->handle = w->hwnd;
    return true;
#else
#ifdef AQUA_GLFW_WAYLAND
    if (glfwGetPlatform() == GLFW_PLATFORM_WAYLAND) { s->kind = AQUA_SURFACE_WAYLAND; s->display = glfwGetWaylandDisplay(); s->handle = glfwGetWaylandWindow(w->glfw); return true; }
#endif
#ifdef AQUA_GLFW_X11
    s->kind = AQUA_SURFACE_X11; s->display = glfwGetX11Display(); s->handle = (void *)(uintptr_t)glfwGetX11Window(w->glfw);
    return true;
#elif defined(__APPLE__)
    if (!w->metal_layer) w->metal_layer = aqua_metal_layer(glfwGetCocoaWindow(w->glfw));
    s->kind = AQUA_SURFACE_METAL; s->handle = w->metal_layer;
    return s->handle != NULL;
#else
    return false;
#endif
#endif
}

void aqua_window_make_current(aqua_window *w) { if (initialized) glfwMakeContextCurrent(w ? w->glfw : NULL); }

/* A remote OpenGL window's frame: the back buffer of its hidden window, read before the swap and turned top-down. */
typedef void (*gl_get_integer)(uint32_t name, int32_t *value);
typedef void (*gl_bind_framebuffer)(uint32_t target, uint32_t framebuffer);
typedef void (*gl_set_int)(uint32_t name, int32_t value);
typedef void (*gl_read_buffer)(uint32_t mode);
typedef void (*gl_read_pixels)(int32_t x, int32_t y, int32_t width, int32_t height, uint32_t format, uint32_t type, void *pixels);
enum { GL_READ_FRAMEBUFFER_ = 0x8CA8, GL_READ_FRAMEBUFFER_BINDING_ = 0x8CAA, GL_PACK_ALIGNMENT_ = 0x0D05, GL_BACK_ = 0x0405,
       GL_READ_BUFFER_ = 0x0C02, GL_RGBA_ = 0x1908, GL_UNSIGNED_BYTE_ = 0x1401 };
static void present_gl(aqua_window *w) {
    gl_get_integer get = (gl_get_integer)glfwGetProcAddress("glGetIntegerv");
    gl_bind_framebuffer bind = (gl_bind_framebuffer)glfwGetProcAddress("glBindFramebuffer");
    gl_set_int pack = (gl_set_int)glfwGetProcAddress("glPixelStorei");
    gl_read_buffer read_buffer = (gl_read_buffer)glfwGetProcAddress("glReadBuffer");
    gl_read_pixels read = (gl_read_pixels)glfwGetProcAddress("glReadPixels");
    if (!get || !bind || !pack || !read_buffer || !read) return;
    int32_t width, height;
    glfwGetFramebufferSize(w->glfw, &width, &height);
    if (width <= 0 || height <= 0) return;
    size_t row = (size_t)width * 4;
    uint8_t *pixels = realloc(w->readback, row * (size_t)height * 2);
    if (!pixels) return;
    w->readback = pixels;
    int32_t framebuffer = 0, alignment = 4, source = GL_BACK_;
    get(GL_READ_FRAMEBUFFER_BINDING_, &framebuffer);
    get(GL_PACK_ALIGNMENT_, &alignment);
    get(GL_READ_BUFFER_, &source);
    bind(GL_READ_FRAMEBUFFER_, 0);
    read_buffer(GL_BACK_);
    pack(GL_PACK_ALIGNMENT_, 1);
    uint8_t *flipped = pixels + row * (size_t)height;
    read(0, 0, width, height, GL_RGBA_, GL_UNSIGNED_BYTE_, flipped);
    pack(GL_PACK_ALIGNMENT_, alignment);
    bind(GL_READ_FRAMEBUFFER_, (uint32_t)framebuffer);
    if (framebuffer) read_buffer((uint32_t)source);
    for (int32_t y = 0; y < height; y++) memcpy(pixels + (size_t)y * row, flipped + (size_t)(height - 1 - y) * row, row);
    remote_present(w->remote, pixels, width, height, (int32_t)row);
}
void aqua_window_swap(aqua_window *w) {
    if (w->remote) { if (w->glfw) { present_gl(w); glfwSwapBuffers(w->glfw); } return; }
    glfwSwapBuffers(w->glfw);
}
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval) {
    (void)rt;
    /* A hidden window has no display to wait for: remote OpenGL windows swap at once. */
    if (initialized) glfwSwapInterval(remote_enabled() ? 0 : interval);
}

bool aqua_window_present(aqua_window *w, const uint8_t *rgba, int32_t width, int32_t height, int32_t stride) {
    return w && w->remote && !w->glfw && remote_present(w->remote, rgba, width, height, stride);
}
void *aqua_gl_proc(const char *name) { return (void *)glfwGetProcAddress(name); }

char *aqua_clipboard_text(aqua_runtime *rt) {
    (void)rt;
    if (remote_enabled()) return remote_clipboard_text();
    ensure_glfw();
    const char *text = initialized ? glfwGetClipboardString(NULL) : NULL;
    return text ? strdup(text) : NULL;
}
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8) {
    (void)rt;
    if (remote_enabled()) return remote_clipboard_set(utf8);
    ensure_glfw();
    if (!initialized) return false;
    glfwSetClipboardString(NULL, utf8);
    return true;
}

void *aqua_window_native(aqua_window *w) {
    if (w->remote) return NULL;
#if defined(_WIN32)
    return w->hwnd;
#elif defined(AQUA_GLFW_X11)
    return (void *)(uintptr_t)glfwGetX11Window(w->glfw);
#elif defined(__APPLE__)
    return glfwGetCocoaWindow(w->glfw);
#else
    (void)w;
    return NULL;
#endif
}

/* Called at runtime shutdown: windows the application left open close with it. */
void desktop_windows_close(aqua_runtime *rt) {
    for (aqua_window *w = windows, *next; w; w = next) { next = w->next; if (w->rt == rt) aqua_window_destroy(w); }
    if (!windows && initialized) { glfwTerminate(); initialized = false; desktop_set_window_loop(NULL, NULL); }
}
