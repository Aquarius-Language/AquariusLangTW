/* aqua/window.h on Android: the activity's surface is the application's one window. Touches become pointer events
   (logical units are density-independent pixels), hardware and soft keys become GLFW-coded key events, the back gesture
   becomes AQUA_EVENT_BACK, and pause/resume become lifecycle events. */
#include <android/configuration.h>
#include <android/input.h>
#include <android/keycodes.h>
#include <android/native_window.h>
#include <android_native_app_glue.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "../../host/host.h"
#include "android.h"

typedef struct listener { aqua_window_listener run; void *data; } listener;
struct aqua_window {
    aqua_runtime *rt;
    listener *listeners; uint32_t listener_count, listener_capacity;
    bool should_close, text_enabled, focused;
    double pointer_x, pointer_y;
    uint8_t keys[512];
    int32_t buttons;
};

static struct android_app *app;
static aqua_window *window;     /* the one window, once a library opened it */
static bool resumed;

static double seconds(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + t.tv_nsec / 1e9; }
static float density(void) {
    int32_t dpi = app && app->config ? AConfiguration_getDensity(app->config) : ACONFIGURATION_DENSITY_MEDIUM;
    return dpi > 0 && dpi != ACONFIGURATION_DENSITY_NONE ? dpi / 160.0f : 1.0f;
}
static void emit(aqua_window_event *e) {
    if (!window) return;
    e->time = seconds();
    uint32_t n = window->listener_count;
    listener copy[16];
    listener *list = n <= 16 ? copy : malloc(n * sizeof *list);
    memcpy(list, window->listeners, n * sizeof *list);
    for (uint32_t i = 0; i < n; i++) list[i].run(window, e, list[i].data);
    if (list != copy) free(list);
}

/* Android key codes to GLFW's. */
static int32_t glfw_key(int32_t code) {
    if (code >= AKEYCODE_A && code <= AKEYCODE_Z) return 'A' + (code - AKEYCODE_A);
    if (code >= AKEYCODE_0 && code <= AKEYCODE_9) return '0' + (code - AKEYCODE_0);
    if (code >= AKEYCODE_F1 && code <= AKEYCODE_F12) return 290 + (code - AKEYCODE_F1);
    switch (code) {
    case AKEYCODE_DPAD_UP: return 265; case AKEYCODE_DPAD_DOWN: return 264; case AKEYCODE_DPAD_LEFT: return 263; case AKEYCODE_DPAD_RIGHT: return 262;
    case AKEYCODE_ENTER: return 257; case AKEYCODE_DEL: return 259; case AKEYCODE_ESCAPE: return 256; case AKEYCODE_TAB: return 258;
    case AKEYCODE_FORWARD_DEL: return 261; case AKEYCODE_SHIFT_LEFT: return 340; case AKEYCODE_SHIFT_RIGHT: return 344;
    case AKEYCODE_CTRL_LEFT: return 341; case AKEYCODE_CTRL_RIGHT: return 345; case AKEYCODE_ALT_LEFT: return 342; case AKEYCODE_ALT_RIGHT: return 346;
    case AKEYCODE_SPACE: return 32; case AKEYCODE_COMMA: return 44; case AKEYCODE_PERIOD: return 46; case AKEYCODE_MINUS: return 45;
    case AKEYCODE_EQUALS: return 61; case AKEYCODE_SLASH: return 47; case AKEYCODE_SEMICOLON: return 59; case AKEYCODE_APOSTROPHE: return 39;
    case AKEYCODE_LEFT_BRACKET: return 91; case AKEYCODE_BACKSLASH: return 92; case AKEYCODE_RIGHT_BRACKET: return 93; case AKEYCODE_GRAVE: return 96;
    case AKEYCODE_INSERT: return 260; case AKEYCODE_PAGE_UP: return 266; case AKEYCODE_PAGE_DOWN: return 267;
    case AKEYCODE_MOVE_HOME: return 268; case AKEYCODE_MOVE_END: return 269; case AKEYCODE_CAPS_LOCK: return 280;
    case AKEYCODE_SCROLL_LOCK: return 281; case AKEYCODE_NUM_LOCK: return 282; case AKEYCODE_SYSRQ: return 283; case AKEYCODE_BREAK: return 284;
    case AKEYCODE_NUMPAD_DOT: return 330; case AKEYCODE_NUMPAD_DIVIDE: return 331; case AKEYCODE_NUMPAD_MULTIPLY: return 332;
    case AKEYCODE_NUMPAD_SUBTRACT: return 333; case AKEYCODE_NUMPAD_ADD: return 334; case AKEYCODE_NUMPAD_ENTER: return 335;
    case AKEYCODE_NUMPAD_EQUALS: return 336; case AKEYCODE_META_LEFT: return 343; case AKEYCODE_META_RIGHT: return 347; case AKEYCODE_MENU: return 348;
    }
    if (code >= AKEYCODE_NUMPAD_0 && code <= AKEYCODE_NUMPAD_9) return 320 + (code - AKEYCODE_NUMPAD_0);
    return -1;
}

static int32_t keypad[11];   /* the key each held keypad key pressed (host_keypad_key), so its release matches */

static void key_event(int32_t code, int32_t action, int32_t meta) {
    if (!window) return;
    int32_t key = glfw_key(code);
    if (key >= 320 && key <= 330) {
        int32_t *held = &keypad[key - 320];
        if (action == 1 || !*held) *held = host_keypad_key(key, (meta & AMETA_NUM_LOCK_ON) != 0);
        key = *held;
        if (action == 0) *held = 0;
    }
    aqua_window_event e = {AQUA_EVENT_KEY};
    e.key = key; e.scancode = code; e.action = action;
    e.mods = (meta & AMETA_SHIFT_ON ? 1 : 0) | (meta & AMETA_CTRL_ON ? 2 : 0) | (meta & AMETA_ALT_ON ? 4 : 0) | (meta & AMETA_META_ON ? 8 : 0) |
             (meta & AMETA_CAPS_LOCK_ON ? 16 : 0) | (meta & AMETA_NUM_LOCK_ON ? 32 : 0);
    if (key >= 0 && key < 512) window->keys[key] = action != 0;
    emit(&e);
}

static int32_t on_input(struct android_app *a, AInputEvent *event) {
    (void)a;
    if (!window) return 0;
    float scale = density();
    if (AInputEvent_getType(event) == AINPUT_EVENT_TYPE_MOTION) {
        int32_t action = AMotionEvent_getAction(event), kind = action & AMOTION_EVENT_ACTION_MASK;
        size_t index = (size_t)((action & AMOTION_EVENT_ACTION_POINTER_INDEX_MASK) >> AMOTION_EVENT_ACTION_POINTER_INDEX_SHIFT);
        int32_t tool = AMotionEvent_getToolType(event, index);
        int32_t type = tool == AMOTION_EVENT_TOOL_TYPE_STYLUS || tool == AMOTION_EVENT_TOOL_TYPE_ERASER ? 2 : tool == AMOTION_EVENT_TOOL_TYPE_MOUSE ? 0 : 1;
        aqua_window_event e = {AQUA_EVENT_POINTER};
        e.pointer = (uint32_t)AMotionEvent_getPointerId(event, index);
        e.pointer_type = type;
        e.x = AMotionEvent_getX(event, index) / scale; e.y = AMotionEvent_getY(event, index) / scale;
        e.pressure = AMotionEvent_getPressure(event, index);
        if (kind == AMOTION_EVENT_ACTION_MOVE) {
            for (size_t i = 0; i < AMotionEvent_getPointerCount(event); i++) {
                aqua_window_event m = e;
                m.pointer = (uint32_t)AMotionEvent_getPointerId(event, i);
                m.x = AMotionEvent_getX(event, i) / scale; m.y = AMotionEvent_getY(event, i) / scale;
                m.pressure = AMotionEvent_getPressure(event, i);
                if (m.pointer == 0) { window->pointer_x = m.x; window->pointer_y = m.y; }
                emit(&m);
            }
            return 1;
        }
        bool down = kind == AMOTION_EVENT_ACTION_DOWN || kind == AMOTION_EVENT_ACTION_POINTER_DOWN;
        bool up = kind == AMOTION_EVENT_ACTION_UP || kind == AMOTION_EVENT_ACTION_POINTER_UP || kind == AMOTION_EVENT_ACTION_CANCEL;
        if (!down && !up) return 0;
        if (e.pointer == 0) { window->pointer_x = e.x; window->pointer_y = e.y; }
        emit(&e);
        aqua_window_event b = e;
        b.type = AQUA_EVENT_BUTTON; b.button = 0; b.action = down ? 1 : 0;
        if (e.pointer == 0) window->buttons = down ? 1 : 0;
        emit(&b);
        return 1;
    }
    if (AInputEvent_getType(event) == AINPUT_EVENT_TYPE_KEY) {
        int32_t code = AKeyEvent_getKeyCode(event), action = AKeyEvent_getAction(event);
        if (code == AKEYCODE_BACK) {
            if (action == AKEY_EVENT_ACTION_UP) { aqua_window_event e = {AQUA_EVENT_BACK}; emit(&e); }
            return 1;
        }
        key_event(code, action == AKEY_EVENT_ACTION_DOWN ? (AKeyEvent_getRepeatCount(event) ? 2 : 1) : 0, AKeyEvent_getMetaState(event));
        return 1;
    }
    return 0;
}

/* ---- Input-method text from AquariusActivity's editor, posted to the runtime thread by platform.c ---- */
static bool composing;
void android_ime_text(const char *utf8) {
    aqua_window_event e = {AQUA_EVENT_TEXT};
    e.text = utf8;
    emit(&e);
}
void android_ime_composition(const char *utf8, int32_t cursor) {
    if (!utf8) {
        if (!composing) return;
        composing = false;
        aqua_window_event e = {AQUA_EVENT_COMPOSITION_END};
        e.text = "";
        emit(&e);
        return;
    }
    if (!composing) {
        composing = true;
        aqua_window_event start = {AQUA_EVENT_COMPOSITION_START};
        start.text = "";
        emit(&start);
    }
    aqua_window_event e = {AQUA_EVENT_COMPOSITION_UPDATE};
    e.text = utf8; e.cursor = cursor;
    emit(&e);
}
void android_ime_key(int32_t code, bool down) { key_event(code, down ? 1 : 0, 0); }

static void on_command(struct android_app *a, int32_t command) {
    (void)a;
    aqua_window_event e = {0};
    switch (command) {
    case APP_CMD_RESUME: resumed = true; e.type = AQUA_EVENT_LIFECYCLE; e.action = 1; emit(&e); break;
    case APP_CMD_PAUSE: resumed = false; e.type = AQUA_EVENT_LIFECYCLE; e.action = 0; emit(&e); break;
    case APP_CMD_GAINED_FOCUS: if (window) window->focused = true; e.type = AQUA_EVENT_FOCUS; e.action = 1; emit(&e); break;
    case APP_CMD_LOST_FOCUS: if (window) window->focused = false; e.type = AQUA_EVENT_FOCUS; e.action = 0; emit(&e); break;
    case APP_CMD_INIT_WINDOW: case APP_CMD_WINDOW_RESIZED: case APP_CMD_CONFIG_CHANGED:
        if (app->window) { e.type = AQUA_EVENT_RESIZE; e.x = ANativeWindow_getWidth(app->window) / density(); e.y = ANativeWindow_getHeight(app->window) / density(); emit(&e); }
        e.type = AQUA_EVENT_VISIBLE; e.action = 1; emit(&e);
        break;
    case APP_CMD_TERM_WINDOW: e.type = AQUA_EVENT_VISIBLE; e.action = 0; emit(&e); break;
    case APP_CMD_DESTROY: if (window) window->should_close = true; break;
    }
}

void android_window_start(struct android_app *a) {
    app = a;
    app->onAppCmd = on_command;
    app->onInputEvent = on_input;
}

void android_poll(int timeout_ms) {
    struct android_poll_source *source;
    int events;
    while (ALooper_pollOnce(timeout_ms, NULL, &events, (void **)&source) >= 0) {
        if (source) source->process(app, source);
        if (app->destroyRequested) return;
        timeout_ms = 0;
    }
}

/* ---- window.h ---- */
void aqua_window_hint(int32_t hint, int32_t value) { (void)hint; (void)value; }

aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    (void)width; (void)height; (void)title;
    if (flags & AQUA_WINDOW_OPENGL) { if (error) *error = strdup("OpenGL windows are not available on Android; use WGPU or Processing."); return NULL; }
    if (window) { if (error) *error = strdup("Android applications have one window."); return NULL; }
    /* The surface may not exist yet right after launch: wait for it. */
    for (int i = 0; i < 1000 && !app->window && !app->destroyRequested; i++) android_poll(10);
    if (!app->window) { if (error) *error = strdup("The activity has no surface."); return NULL; }
    window = calloc(1, sizeof *window);
    window->rt = rt; window->focused = true;
    return window;
}
void aqua_window_destroy(aqua_window *w) {
    if (!w) return;
    aqua_window_event e = {AQUA_EVENT_DESTROYED};
    emit(&e);
    free(w->listeners);
    if (w == window) window = NULL;
    free(w);
}
void android_windows_close(aqua_runtime *rt) { if (window && window->rt == rt) aqua_window_destroy(window); }
struct aqua_window *aqua_android_window(void) { return window; }
aqua_runtime *aqua_window_runtime(aqua_window *w) { return w->rt; }
bool aqua_window_should_close(aqua_window *w) { return w->should_close || app->destroyRequested; }
void aqua_window_set_should_close(aqua_window *w, bool close) { w->should_close = close; }
void aqua_window_framebuffer_size(aqua_window *w, int32_t *width, int32_t *height) {
    (void)w;
    *width = app->window ? ANativeWindow_getWidth(app->window) : 0;
    *height = app->window ? ANativeWindow_getHeight(app->window) : 0;
}
void aqua_window_size(aqua_window *w, int32_t *width, int32_t *height) {
    int32_t pw, ph;
    aqua_window_framebuffer_size(w, &pw, &ph);
    float scale = density();
    *width = (int32_t)(pw / scale + 0.5f); *height = (int32_t)(ph / scale + 0.5f);
}
float aqua_window_content_scale(aqua_window *w) { (void)w; return density(); }
void aqua_window_set_size(aqua_window *w, int32_t width, int32_t height) { (void)w; (void)width; (void)height; }
void aqua_window_set_position(aqua_window *w, int32_t x, int32_t y) { (void)w; (void)x; (void)y; }
void aqua_window_set_title(aqua_window *w, const char *title) { (void)w; (void)title; }
bool aqua_window_set_fullscreen(aqua_window *w, bool fullscreen) { (void)w; return fullscreen; }
bool aqua_window_focused(aqua_window *w) { return w->focused; }
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height) {
    (void)rt;
    if (!app->window) return false;
    *width = ANativeWindow_getWidth(app->window); *height = ANativeWindow_getHeight(app->window);
    return true;
}
void aqua_windows_poll(aqua_runtime *rt) { (void)rt; android_poll(0); }
double aqua_window_time(aqua_runtime *rt) { (void)rt; static double start; if (!start) start = seconds(); return seconds() - start; }
int32_t aqua_window_key(aqua_window *w, int32_t key) { return key >= 0 && key < 512 ? w->keys[key] : 0; }
int32_t aqua_window_mouse_button(aqua_window *w, int32_t button) { return button == 0 ? w->buttons : 0; }
void aqua_window_cursor(aqua_window *w, double *x, double *y) { *x = w->pointer_x; *y = w->pointer_y; }
void aqua_window_set_cursor_mode(aqua_window *w, int32_t mode) { (void)w; (void)mode; }
bool aqua_window_set_cursor_shape(aqua_window *w, int32_t shape) { (void)w; (void)shape; return false; }
bool aqua_window_set_cursor_image(aqua_window *w, int32_t width, int32_t height, const uint8_t *rgba, int32_t x, int32_t y) {
    (void)w; (void)width; (void)height; (void)rgba; (void)x; (void)y;
    return false;
}
bool aqua_window_capture_pointer(aqua_window *w, bool capture) { (void)w; (void)capture; return false; }
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

/* The soft keyboard: AquariusActivity's input editor (composition-aware), else InputMethodManager on the decor view. */
static void soft_keyboard(bool show) {
    JNIEnv *env = android_jni();
    jobject activity = android_activity();
    jmethodID show_keyboard = android_activity_method("showKeyboard", "(Z)V");
    if (show_keyboard) { (*env)->CallVoidMethod(env, activity, show_keyboard, (jboolean)show); return; }
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jstring service_name = (*env)->NewStringUTF(env, "input_method");
    jobject manager = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;"), service_name);
    jclass activity_class = (*env)->GetObjectClass(env, activity);
    jobject decor_window = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, activity_class, "getWindow", "()Landroid/view/Window;"));
    jclass window_class = (*env)->FindClass(env, "android/view/Window");
    jobject view = (*env)->CallObjectMethod(env, decor_window, (*env)->GetMethodID(env, window_class, "getDecorView", "()Landroid/view/View;"));
    jclass manager_class = (*env)->FindClass(env, "android/view/inputmethod/InputMethodManager");
    if (show) (*env)->CallBooleanMethod(env, manager, (*env)->GetMethodID(env, manager_class, "showSoftInput", "(Landroid/view/View;I)Z"), view, 0);
    else {
        jclass view_class = (*env)->FindClass(env, "android/view/View");
        jobject token = (*env)->CallObjectMethod(env, view, (*env)->GetMethodID(env, view_class, "getWindowToken", "()Landroid/os/IBinder;"));
        (*env)->CallBooleanMethod(env, manager, (*env)->GetMethodID(env, manager_class, "hideSoftInputFromWindow", "(Landroid/os/IBinder;I)Z"), token, 0);
    }
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->DeleteLocalRef(env, service_name);
}
void aqua_window_text_input(aqua_window *w, bool enabled) {
    if (w->text_enabled == enabled) return;
    w->text_enabled = enabled;
    soft_keyboard(enabled);
}
bool aqua_window_text_input_enabled(aqua_window *w) { return w && w->text_enabled; }
void aqua_window_text_rect(aqua_window *w, int32_t x, int32_t y, int32_t width, int32_t height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
bool aqua_window_composition_supported(aqua_window *w) { (void)w; return android_activity_method("showKeyboard", "(Z)V") != NULL; }
bool aqua_window_surface(aqua_window *w, aqua_surface *s) {
    (void)w;
    memset(s, 0, sizeof *s);
    if (!app->window) return false;
    s->kind = AQUA_SURFACE_ANDROID; s->handle = app->window;
    return true;
}
void aqua_window_make_current(aqua_window *w) { (void)w; }
void aqua_window_swap(aqua_window *w) { (void)w; }
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval) { (void)rt; (void)interval; }
void *aqua_gl_proc(const char *name) { (void)name; return NULL; }

char *aqua_clipboard_text(aqua_runtime *rt) {
    (void)rt;
    JNIEnv *env = android_jni();
    jobject activity = android_activity();
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jstring service_name = (*env)->NewStringUTF(env, "clipboard");
    jobject manager = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;"), service_name);
    jclass manager_class = (*env)->FindClass(env, "android/content/ClipboardManager");
    jobject clip = (*env)->CallObjectMethod(env, manager, (*env)->GetMethodID(env, manager_class, "getPrimaryClip", "()Landroid/content/ClipData;"));
    char *result = NULL;
    if (clip) {
        jclass clip_class = (*env)->FindClass(env, "android/content/ClipData");
        jobject item = (*env)->CallObjectMethod(env, clip, (*env)->GetMethodID(env, clip_class, "getItemAt", "(I)Landroid/content/ClipData$Item;"), 0);
        jclass item_class = (*env)->FindClass(env, "android/content/ClipData$Item");
        jobject text = item ? (*env)->CallObjectMethod(env, item, (*env)->GetMethodID(env, item_class, "coerceToText", "(Landroid/content/Context;)Ljava/lang/CharSequence;"), activity) : NULL;
        if (text) {
            jclass sequence = (*env)->FindClass(env, "java/lang/Object");
            jstring string = (*env)->CallObjectMethod(env, text, (*env)->GetMethodID(env, sequence, "toString", "()Ljava/lang/String;"));
            const char *utf8 = (*env)->GetStringUTFChars(env, string, NULL);
            result = strdup(utf8);
            (*env)->ReleaseStringUTFChars(env, string, utf8);
        }
    }
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->DeleteLocalRef(env, service_name);
    return result;
}
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8) {
    (void)rt;
    JNIEnv *env = android_jni();
    jobject activity = android_activity();
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jstring service_name = (*env)->NewStringUTF(env, "clipboard");
    jobject manager = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;"), service_name);
    jclass clip_class = (*env)->FindClass(env, "android/content/ClipData");
    jstring label = (*env)->NewStringUTF(env, "Aquarius"), text = (*env)->NewStringUTF(env, utf8);
    jobject clip = (*env)->CallStaticObjectMethod(env, clip_class, (*env)->GetStaticMethodID(env, clip_class, "newPlainText", "(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Landroid/content/ClipData;"), label, text);
    jclass manager_class = (*env)->FindClass(env, "android/content/ClipboardManager");
    (*env)->CallVoidMethod(env, manager, (*env)->GetMethodID(env, manager_class, "setPrimaryClip", "(Landroid/content/ClipData;)V"), clip);
    bool ok = !(*env)->ExceptionCheck(env);
    if (!ok) (*env)->ExceptionClear(env);
    return ok;
}
void *aqua_window_native(aqua_window *w) { (void)w; return app->window; }
