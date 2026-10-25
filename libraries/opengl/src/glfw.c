/* The GLFW module over <aqua/window.h>: the runtime's window system (GLFW on Windows and Linux), never GLFW itself.
 * Windows given to scripts are the shared window objects (aqua_window_object), so other libraries (TextInput, ...)
 * accept them; window arguments are read back with aqua_window_of. As before, one runtime at a time owns the window
 * system (GLFW.Init) and the module keeps one OpenGL window at a time. */
#include "opengl.h"
#include <stdatomic.h>
#include <string.h>

/* GLFW's numbers (window.h uses GLFW codes on every target). */
enum {
    HINT_RESIZABLE = 0x20003, HINT_VISIBLE = 0x20004, HINT_DECORATED = 0x20005, HINT_CLIENT_API = 0x22001,
    HINT_CONTEXT_VERSION_MAJOR = 0x22002, HINT_CONTEXT_VERSION_MINOR = 0x22003, HINT_OPENGL_FORWARD_COMPAT = 0x22006,
    HINT_OPENGL_PROFILE = 0x22008, OPENGL_CORE_PROFILE = 0x32001, NO_API = 0, OPENGL_API = 0x30001,
    INPUT_CURSOR = 0x33001,
};

/* The runtime whose script called GLFW.Init(), process-wide like the window system itself. */
static _Atomic(opengl_state *) owner;

static void window_event(aqua_window *window, const aqua_window_event *event, void *data) {
    opengl_state *state = data;
    if (event->type != AQUA_EVENT_DESTROYED) return;
    if (state->window == window) state->window = NULL;
    if (state->current == window) { state->current = NULL; state->version = 0; }
}

static void destroy_window(opengl_state *state) {
    aqua_window *window = state->window;
    if (!window) return;
    aqua_window_unlisten(window, window_event, state);
    if (state->current == window) aqua_window_make_current(NULL);
    state->window = NULL;
    state->current = NULL;
    state->version = 0;
    aqua_window_destroy(window);
}

/* GLFW.Terminate and the end of the runtime: the module's window closes and the window system is released. */
void opengl_terminate(opengl_state *state) {
    if (!state->initialized) return;
    destroy_window(state);
    state->current = NULL;
    state->version = 0;
    state->hint_visible = state->hint_resizable = state->hint_decorated = 1;
    state->hint_client_api = OPENGL_API;
    state->initialized = false;
    opengl_state *expected = state;
    atomic_compare_exchange_strong(&owner, &expected, NULL);
}

static const char *const NOT_A_WINDOW = "Expected a live GLFW window owned by this runtime.";

/* Window argument `index`: any live window object, or only the module's own window when `own` (context operations and
   DestroyWindow). */
static aqua_window *window_argument(aqua_call *call, const char *function, uint32_t index, bool own) {
    opengl_state *state = opengl_require_init(call, function);
    if (!state) return NULL;
    aqua_window *window = aqua_window_of(aqua_call_runtime(call), aqua_arg(call, index));
    if (!window || (own && window != state->window)) { opengl_fail(call, function, "%s", NOT_A_WINDOW); return NULL; }
    return window;
}

static void numbers(aqua_call *call, double a, double b) {
    double pair[2] = {a, b};
    aqua_return(call, aqua_array_from_f64(aqua_call_runtime(call), pair, 2, AQUA_DOUBLE));
}

static void init(aqua_call *call) {
    opengl_state *state = aqua_state(call);
    if (!state->initialized) {
        opengl_state *expected = NULL;
        if (!atomic_compare_exchange_strong(&owner, &expected, state)) {
            opengl_fail(call, "Init", "Another Aquarius runtime owns GLFW.");
            return;
        }
        state->initialized = true;
        aqua_window_hint(HINT_CONTEXT_VERSION_MAJOR, 3);
        aqua_window_hint(HINT_CONTEXT_VERSION_MINOR, 3);
        aqua_window_hint(HINT_OPENGL_PROFILE, OPENGL_CORE_PROFILE);
        aqua_window_hint(HINT_OPENGL_FORWARD_COMPAT, 1);
    }
    aqua_return_bool(call, true);
}

static void terminate(aqua_call *call) {
    opengl_terminate(aqua_state(call));
    aqua_return_null(call);
}

/* Visibility, resizing, decoration and the client API become window flags; every other hint goes to the window
   system for the next window. */
static void window_hint(aqua_call *call) {
    opengl_state *state = opengl_require_init(call, "WindowHint");
    int32_t hint, value;
    if (!state || !opengl_int(call, "WindowHint", 0, &hint) || !opengl_int(call, "WindowHint", 1, &value)) return;
    switch (hint) {
    case HINT_VISIBLE: state->hint_visible = value; break;
    case HINT_RESIZABLE: state->hint_resizable = value; break;
    case HINT_DECORATED: state->hint_decorated = value; break;
    case HINT_CLIENT_API: state->hint_client_api = value; break;
    default: aqua_window_hint(hint, value); break;
    }
    aqua_return_null(call);
}

static void create_window(aqua_call *call) {
    opengl_state *state = opengl_require_init(call, "CreateWindow");
    if (!state) return;
    if (state->window) { opengl_fail(call, "CreateWindow", "This runtime supports one OpenGL window at a time."); return; }
    int32_t width, height;
    if (!opengl_int(call, "CreateWindow", 0, &width) || !opengl_int(call, "CreateWindow", 1, &height)) return;
    if (width <= 0 || height <= 0) { opengl_fail(call, "CreateWindow", "Window dimensions must be positive."); return; }
    const char *title = opengl_text(call, "CreateWindow", 2);
    if (!title) return;
    uint32_t flags = state->hint_client_api == NO_API ? 0 : AQUA_WINDOW_OPENGL;
    if (!state->hint_visible) flags |= AQUA_WINDOW_HIDDEN;
    if (!state->hint_resizable) flags |= AQUA_WINDOW_FIXED_SIZE;
    if (!state->hint_decorated) flags |= AQUA_WINDOW_UNDECORATED;
    char *error = NULL;
    aqua_window *window = aqua_window_create(aqua_call_runtime(call), width, height, title, flags, &error);
    if (!window) {
        opengl_fail(call, "CreateWindow", "%s", error ? error : "Cannot create a window.");
        aqua_free(error);
        return;
    }
    state->window = window;
    aqua_window_listen(window, window_event, state);
    aqua_return(call, aqua_window_object(window));
}

static void destroy_window_call(aqua_call *call) {
    if (!window_argument(call, "DestroyWindow", 0, true)) return;
    destroy_window(aqua_state(call));
    aqua_return_null(call);
}

static void make_context_current(aqua_call *call) {
    aqua_window *window = window_argument(call, "MakeContextCurrent", 0, true);
    if (!window) return;
    opengl_state *state = aqua_state(call);
    aqua_window_make_current(window);
    state->current = window;
    state->version = 0; /* entry points belong to a context: GLAD.Load() again */
    aqua_return_null(call);
}

static void window_should_close(aqua_call *call) {
    aqua_window *window = window_argument(call, "WindowShouldClose", 0, false);
    if (window) aqua_return_bool(call, aqua_window_should_close(window));
}

static void set_window_should_close(aqua_call *call) {
    aqua_window *window = window_argument(call, "SetWindowShouldClose", 0, false);
    if (!window) return;
    aqua_value close = aqua_arg(call, 1);
    if (close.type != AQUA_BOOL) { opengl_fail(call, "SetWindowShouldClose", "Expected a Boolean close flag."); return; }
    aqua_window_set_should_close(window, close.number != 0);
    aqua_return_null(call);
}

static void swap_buffers(aqua_call *call) {
    if (!opengl_require_gl(call, "SwapBuffers")) return;
    aqua_window *window = window_argument(call, "SwapBuffers", 0, true);
    if (!window) return;
    aqua_window_swap(window);
    aqua_return_null(call);
}

static void poll_events(aqua_call *call) {
    if (!opengl_require_init(call, "PollEvents")) return;
    aqua_windows_poll(aqua_call_runtime(call));
    aqua_return_null(call);
}

static void swap_interval(aqua_call *call) {
    int32_t interval;
    if (!opengl_require_gl(call, "SwapInterval") || !opengl_int(call, "SwapInterval", 0, &interval)) return;
    aqua_window_swap_interval(aqua_call_runtime(call), interval);
    aqua_return_null(call);
}

static void get_time(aqua_call *call) {
    if (opengl_require_init(call, "GetTime")) aqua_return_double(call, aqua_window_time(aqua_call_runtime(call)));
}

static void get_key(aqua_call *call) {
    aqua_window *window = window_argument(call, "GetKey", 0, false);
    int32_t key;
    if (window && opengl_int(call, "GetKey", 1, &key)) aqua_return_int(call, aqua_window_key(window, key));
}

static void get_mouse_button(aqua_call *call) {
    aqua_window *window = window_argument(call, "GetMouseButton", 0, false);
    int32_t button;
    if (window && opengl_int(call, "GetMouseButton", 1, &button)) aqua_return_int(call, aqua_window_mouse_button(window, button));
}

static void get_cursor_pos(aqua_call *call) {
    aqua_window *window = window_argument(call, "GetCursorPos", 0, false);
    if (!window) return;
    double x = 0, y = 0;
    aqua_window_cursor(window, &x, &y);
    numbers(call, x, y);
}

/* GLFW_CURSOR sets the cursor mode (normal, hidden, disabled with raw motion). Sticky buttons and lock-key modifiers
   are always on in the window system; other modes have no effect. */
static void set_input_mode(aqua_call *call) {
    aqua_window *window = window_argument(call, "SetInputMode", 0, false);
    int32_t mode, value;
    if (!window || !opengl_int(call, "SetInputMode", 1, &mode) || !opengl_int(call, "SetInputMode", 2, &value)) return;
    if (mode == INPUT_CURSOR) aqua_window_set_cursor_mode(window, value);
    aqua_return_null(call);
}

static void get_framebuffer_size(aqua_call *call) {
    aqua_window *window = window_argument(call, "GetFramebufferSize", 0, false);
    if (!window) return;
    int32_t width = 0, height = 0;
    aqua_window_framebuffer_size(window, &width, &height);
    numbers(call, width, height);
}

static void get_window_size(aqua_call *call) {
    aqua_window *window = window_argument(call, "GetWindowSize", 0, false);
    if (!window) return;
    int32_t width = 0, height = 0;
    aqua_window_size(window, &width, &height);
    numbers(call, width, height);
}

static void set_window_size(aqua_call *call) {
    aqua_window *window = window_argument(call, "SetWindowSize", 0, false);
    int32_t width, height;
    if (!window || !opengl_int(call, "SetWindowSize", 1, &width) || !opengl_int(call, "SetWindowSize", 2, &height)) return;
    if (width <= 0 || height <= 0) { opengl_fail(call, "SetWindowSize", "Window dimensions must be positive."); return; }
    aqua_window_set_size(window, width, height);
    aqua_return_null(call);
}

static void get_environment(aqua_call *call) {
    const char *name = opengl_text(call, "GetEnvironment", 0);
    if (!name) return;
    const char *value = aqua_environment(aqua_call_runtime(call), name);
    aqua_return_cstring(call, value ? value : "");
}

void opengl_register_glfw(aqua_library *library) {
    aqua_class *glfw = aqua_define_module(library, "GLFW");
    aqua_define_function(glfw, "CreateWindow|建立視窗", create_window, "vvv");
    aqua_define_function(glfw, "DestroyWindow|銷毀視窗", destroy_window_call, "v");
    aqua_define_function(glfw, "GetCursorPos|取得游標位置", get_cursor_pos, "v");
    aqua_define_function(glfw, "GetEnvironment|取得環境變數", get_environment, "v");
    aqua_define_function(glfw, "GetFramebufferSize|取得影格緩衝區尺寸", get_framebuffer_size, "v");
    aqua_define_function(glfw, "GetKey|取得按鍵", get_key, "vv");
    aqua_define_function(glfw, "GetMouseButton|取得滑鼠按鈕", get_mouse_button, "vv");
    aqua_define_function(glfw, "GetTime|取得時間", get_time, "");
    aqua_define_function(glfw, "GetWindowSize|取得視窗尺寸", get_window_size, "v");
    aqua_define_function(glfw, "Init|初始化", init, "");
    aqua_define_function(glfw, "MakeContextCurrent|設為目前繪圖環境", make_context_current, "v");
    aqua_define_function(glfw, "PollEvents|輪詢事件", poll_events, "");
    aqua_define_function(glfw, "SetInputMode|設定輸入模式", set_input_mode, "vvv");
    aqua_define_function(glfw, "SetWindowShouldClose|設定視窗關閉狀態", set_window_should_close, "vv");
    aqua_define_function(glfw, "SetWindowSize|設定視窗尺寸", set_window_size, "vvv");
    aqua_define_function(glfw, "SwapBuffers|交換緩衝區", swap_buffers, "v");
    aqua_define_function(glfw, "SwapInterval|交換間隔", swap_interval, "v");
    aqua_define_function(glfw, "Terminate|終止", terminate, "");
    aqua_define_function(glfw, "WindowHint|視窗提示", window_hint, "vv");
    aqua_define_function(glfw, "WindowShouldClose|視窗是否關閉", window_should_close, "v");

    static const struct { const char *name; int32_t value; } constants[] = {
        {"GLFW_TRUE", 1}, {"GLFW_FALSE", 0}, {"GLFW_PRESS", 1}, {"GLFW_RELEASE", 0}, {"GLFW_REPEAT", 2},
        {"GLFW_CONTEXT_VERSION_MAJOR", HINT_CONTEXT_VERSION_MAJOR}, {"GLFW_CONTEXT_VERSION_MINOR", HINT_CONTEXT_VERSION_MINOR},
        {"GLFW_OPENGL_PROFILE", HINT_OPENGL_PROFILE}, {"GLFW_OPENGL_CORE_PROFILE", OPENGL_CORE_PROFILE},
        {"GLFW_OPENGL_FORWARD_COMPAT", HINT_OPENGL_FORWARD_COMPAT}, {"GLFW_VISIBLE", HINT_VISIBLE},
        {"GLFW_RESIZABLE", HINT_RESIZABLE}, {"GLFW_SAMPLES", 0x2100D}, {"GLFW_KEY_ESCAPE", 256}, {"GLFW_KEY_SPACE", 32},
        {"GLFW_CLIENT_API", HINT_CLIENT_API}, {"GLFW_NO_API", NO_API}, {"GLFW_OPENGL_API", OPENGL_API},
        {"GLFW_KEY_LEFT", 263}, {"GLFW_KEY_RIGHT", 262}, {"GLFW_KEY_UP", 265}, {"GLFW_KEY_DOWN", 264},
        {"GLFW_KEY_ENTER", 257}, {"GLFW_KEY_TAB", 258}, {"GLFW_KEY_BACKSPACE", 259}, {"GLFW_KEY_DELETE", 261},
        {"GLFW_KEY_HOME", 268}, {"GLFW_KEY_END", 269}, {"GLFW_MOUSE_BUTTON_LEFT", 0}, {"GLFW_MOUSE_BUTTON_RIGHT", 1},
        {"GLFW_CURSOR", INPUT_CURSOR}, {"GLFW_CURSOR_NORMAL", 0x34001}, {"GLFW_CURSOR_DISABLED", 0x34003},
    };
    for (size_t i = 0; i < sizeof constants / sizeof *constants; i++) aqua_define_int(glfw, constants[i].name, constants[i].value);
    char name[16];
    for (char key = 'A'; key <= 'Z'; key++) { memcpy(name, "GLFW_KEY_", 9); name[9] = key; name[10] = 0; aqua_define_int(glfw, name, key); }
    for (char key = '0'; key <= '9'; key++) { memcpy(name, "GLFW_KEY_", 9); name[9] = key; name[10] = 0; aqua_define_int(glfw, name, key); }
}
