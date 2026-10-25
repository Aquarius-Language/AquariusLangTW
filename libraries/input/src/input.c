/* AndroidInput / Android輸入: touches, the back gesture, insets, the soft keyboard and vibration on Android, built on
 * aqua/window.h events of the activity's window and aqua/android.h. Other targets register the same module with
 * IsSupported() false; their other functions validate arguments and then report that Android input is unavailable. */
#include <aqua/aqua.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_ANDROID)
#include <aqua/android.h>
#include <aqua/window.h>
#endif

static void fail(aqua_call *call, const char *function, const char *message) {
    aqua_fail(call, "AndroidInput.%s: %s", function, message);
}

#if defined(AQUA_OS_ANDROID)
/* ---- Records: script objects whose fields are fixed values (snapshots, touches, insets) ---- */

#define RECORD_FIELDS 8
typedef struct record { uint32_t count; aqua_value values[RECORD_FIELDS]; aqua_ref refs[RECORD_FIELDS]; } record;

static void record_finalize(aqua_runtime *rt, void *data) {
    record *r = data;
    for (uint32_t i = 0; i < r->count; i++) aqua_release(rt, r->refs[i]);
    free(r);
}
#define FIELD(N) static void field_##N(aqua_call *call) { \
        record *r = aqua_self(call); \
        aqua_return(call, r->refs[N] ? aqua_ref_value(aqua_call_runtime(call), r->refs[N]) : r->values[N]); }
FIELD(0) FIELD(1) FIELD(2) FIELD(3) FIELD(4) FIELD(5) FIELD(6) FIELD(7)
static const aqua_function fields[RECORD_FIELDS] = {field_0, field_1, field_2, field_3, field_4, field_5, field_6, field_7};

static aqua_class *define_record(aqua_library *library, const char *name, const char *const *names, uint32_t count) {
    aqua_class *c = aqua_define_class(library, name, record_finalize);
    for (uint32_t i = 0; i < count; i++) aqua_define_property(c, names[i], fields[i]);
    return c;
}

static aqua_class *snapshot_class, *insets_class, *touch_class;

/* Numbers and booleans are stored as they are; strings, arrays and objects are held until the record is collected. */
static aqua_value new_record(aqua_runtime *rt, aqua_class *cls, const aqua_value *values, uint32_t count) {
    record *r = calloc(1, sizeof *r);
    r->count = count;
    for (uint32_t i = 0; i < count; i++) {
        r->values[i] = values[i];
        if (!aqua_is_number(values[i]) && values[i].type != AQUA_BOOL && !aqua_is_nullish(values[i])) r->refs[i] = aqua_retain(rt, values[i]);
    }
    return aqua_object_new(rt, cls, r);
}

/* ---- Events of the activity's window, gathered between polls ---- */

#define TOUCH_LIMIT 1024   /* the newest touches kept when a script stops polling */
#define POINTER_LIMIT 32

typedef struct touch { uint32_t id; const char *phase; double x, y, pressure, timestamp; bool pen; } touch;

typedef struct input_state {
    aqua_window *window;               /* listened to, or NULL */
    touch *touches; uint32_t count, capacity;
    int32_t fresh;                     /* index of the touch the latest pointer event added, else -1 */
    bool back;
    struct { uint32_t id; double x, y; bool pen; } active[POINTER_LIMIT];
    uint32_t active_count;
} input_state;

static void add_touch(input_state *s, touch t) {
    if (s->count == TOUCH_LIMIT) {
        memmove(s->touches, s->touches + 1, (TOUCH_LIMIT - 1) * sizeof *s->touches);
        s->count--;
        if (s->fresh >= 0) s->fresh--;
    }
    if (s->count == s->capacity) {
        s->capacity = s->capacity ? s->capacity * 2 : 32;
        s->touches = realloc(s->touches, s->capacity * sizeof *s->touches);
    }
    s->touches[s->count++] = t;
}

static void track(input_state *s, const touch *t, bool down) {
    for (uint32_t i = 0; i < s->active_count; i++) {
        if (s->active[i].id != t->id) continue;
        if (down) { s->active[i].x = t->x; s->active[i].y = t->y; return; }
        s->active[i] = s->active[--s->active_count];
        return;
    }
    if (down && s->active_count < POINTER_LIMIT) {
        s->active[s->active_count].id = t->id; s->active[s->active_count].x = t->x; s->active[s->active_count].y = t->y;
        s->active[s->active_count++].pen = t->pen;
    }
}

static void on_event(aqua_window *window, const aqua_window_event *e, void *data) {
    (void)window;
    input_state *s = data;
    int32_t fresh = -1;
    switch (e->type) {
    case AQUA_EVENT_POINTER: { /* a move, or the position of the press or release the button event that follows reports */
        touch t = {e->pointer, "move", e->x, e->y, e->pressure, e->time, e->pointer_type == 2};
        add_touch(s, t);
        fresh = (int32_t)s->count - 1;
        for (uint32_t i = 0; i < s->active_count; i++) if (s->active[i].id == t.id) { s->active[i].x = t.x; s->active[i].y = t.y; }
        break;
    }
    case AQUA_EVENT_BUTTON: {
        touch t = {e->pointer, e->action ? "down" : "up", e->x, e->y, e->pressure, e->time, e->pointer_type == 2};
        if (s->fresh >= 0 && s->touches[s->fresh].id == e->pointer) s->touches[s->fresh].phase = t.phase;
        else add_touch(s, t);
        track(s, &t, e->action != 0);
        break;
    }
    case AQUA_EVENT_BACK: s->back = true; break;
    case AQUA_EVENT_LIFECYCLE:
        /* Pausing cancels the touches in progress: their releases will never arrive. */
        if (e->action == 0) {
            for (uint32_t i = 0; i < s->active_count; i++) {
                touch t = {s->active[i].id, "cancel", s->active[i].x, s->active[i].y, 0, e->time, s->active[i].pen};
                add_touch(s, t);
            }
            s->active_count = 0;
        }
        break;
    case AQUA_EVENT_DESTROYED: s->window = NULL; s->active_count = 0; break;
    }
    s->fresh = fresh;
}

/* Listens to the activity's window once a library created it. */
static input_state *attach(aqua_call *call) {
    input_state *s = aqua_state(call);
    aqua_window *window = aqua_android_window();
    if (window && window != s->window && aqua_window_runtime(window) == aqua_call_runtime(call)) {
        s->window = window;
        aqua_window_listen(window, on_event, s);
    }
    return s;
}

static void *state_create(aqua_runtime *rt) {
    (void)rt;
    input_state *s = calloc(1, sizeof *s);
    s->fresh = -1;
    return s;
}
static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    input_state *s = data;
    if (s->window) aqua_window_unlisten(s->window, on_event, s);
    free(s->touches);
    free(s);
}

/* ---- JNI helpers ---- */

static bool jni_failed(JNIEnv *env) {
    if (!(*env)->ExceptionCheck(env)) return false;
    (*env)->ExceptionClear(env);
    return true;
}

static jobject system_service(JNIEnv *env, const char *name) {
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jmethodID get = (*env)->GetMethodID(env, context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
    jstring service = (*env)->NewStringUTF(env, name);
    jobject result = (*env)->CallObjectMethod(env, aqua_android_activity(), get, service);
    if (jni_failed(env)) result = NULL;
    (*env)->DeleteLocalRef(env, service);
    (*env)->DeleteLocalRef(env, context);
    return result;
}

static bool vibrate(int32_t milliseconds) {
    JNIEnv *env = aqua_android_jni();
    jobject vibrator = system_service(env, "vibrator");
    if (!vibrator) return false;
    jclass effects = (*env)->FindClass(env, "android/os/VibrationEffect");
    jmethodID one_shot = (*env)->GetStaticMethodID(env, effects, "createOneShot", "(JI)Landroid/os/VibrationEffect;");
    jobject effect = (*env)->CallStaticObjectMethod(env, effects, one_shot, (jlong)milliseconds, (jint)-1 /* DEFAULT_AMPLITUDE */);
    bool ok = !jni_failed(env) && effect;
    if (ok) {
        jclass vibrators = (*env)->FindClass(env, "android/os/Vibrator");
        (*env)->CallVoidMethod(env, vibrator, (*env)->GetMethodID(env, vibrators, "vibrate", "(Landroid/os/VibrationEffect;)V"), effect);
        ok = !jni_failed(env);
        (*env)->DeleteLocalRef(env, vibrators);
    }
    if (effect) (*env)->DeleteLocalRef(env, effect);
    (*env)->DeleteLocalRef(env, effects);
    (*env)->DeleteLocalRef(env, vibrator);
    return ok;
}

/* Activity methods by name; the activity class is AquariusActivity, whose JNI natives live in the runtime. */
static void call_activity(const char *name, const char *signature, bool flag, bool with_flag) {
    JNIEnv *env = aqua_android_jni();
    jobject activity = aqua_android_activity();
    jclass cls = (*env)->GetObjectClass(env, activity);
    jmethodID method = (*env)->GetMethodID(env, cls, name, signature);
    if (jni_failed(env)) method = NULL;
    if (method) {
        if (with_flag) (*env)->CallVoidMethod(env, activity, method, (jboolean)flag);
        else (*env)->CallVoidMethod(env, activity, method);
        jni_failed(env);
    }
    (*env)->DeleteLocalRef(env, cls);
}
#endif

/* ---- Functions ---- */

static void is_supported(aqua_call *call) {
#if defined(AQUA_OS_ANDROID)
    aqua_return_bool(call, true);
#else
    aqua_return_bool(call, false);
#endif
}

static void poll_input(aqua_call *call) {
#if defined(AQUA_OS_ANDROID)
    aqua_runtime *rt = aqua_call_runtime(call);
    input_state *s = attach(call);
    float left, top, right, bottom;
    bool keyboard;
    aqua_android_insets(&left, &top, &right, &bottom, &keyboard);
    int32_t width = 0, height = 0;
    bool known = aqua_display_size(rt, &width, &height);
    aqua_value touches = aqua_array_new(rt, s->count);
    for (uint32_t i = 0; i < s->count; i++) {
        const touch *t = &s->touches[i];
        aqua_value values[8] = {aqua_double(t->id), aqua_cstring(rt, t->phase), aqua_double(t->x), aqua_double(t->y),
                                aqua_double(t->pressure), aqua_cstring(rt, t->pen ? "pen" : "touch"), aqua_double(t->timestamp),
                                aqua_array_new(rt, 0)};
        aqua_array_set(rt, touches, i, new_record(rt, touch_class, values, 8));
    }
    s->count = 0;
    s->fresh = -1;
    aqua_value insets[4] = {aqua_double(left), aqua_double(top), aqua_double(right), aqua_double(bottom)};
    aqua_value snapshot[5] = {aqua_cstring(rt, known && width > height ? "landscape" : "portrait"), aqua_bool(keyboard),
                              aqua_bool(s->back), new_record(rt, insets_class, insets, 4), touches};
    s->back = false;
    aqua_return(call, new_record(rt, snapshot_class, snapshot, 5));
#else
    fail(call, "Poll", "Android input is unavailable on this platform.");
#endif
}

static void show_keyboard(aqua_call *call) {
    aqua_value visible = aqua_arg(call, 0);
    if (visible.type != AQUA_BOOL) { fail(call, "ShowKeyboard", "Expected a boolean."); return; }
#if defined(AQUA_OS_ANDROID)
    input_state *s = attach(call);
    if (s->window) aqua_window_text_input(s->window, visible.number != 0);
    else call_activity("showKeyboard", "(Z)V", visible.number != 0, true);
#else
    fail(call, "ShowKeyboard", "Android input is unavailable on this platform.");
#endif
}

static void vibrate_for(aqua_call *call) {
    aqua_value duration = aqua_arg(call, 0);
    double n = duration.number;
    if (!aqua_is_number(duration) || !(n >= 1 && n <= 10000) || n != (double)(int32_t)n) {
        fail(call, "Vibrate", "Vibration must be 1..10000 milliseconds.");
        return;
    }
#if defined(AQUA_OS_ANDROID)
    if (!vibrate((int32_t)n)) fail(call, "Vibrate", "Vibration is unavailable (the application needs android.permission.VIBRATE).");
#else
    fail(call, "Vibrate", "Android input is unavailable on this platform.");
#endif
}

static void resolve_back(aqua_call *call) {
    aqua_value decision = aqua_arg(call, 0);
    aqua_str text = decision.type == AQUA_STRING ? aqua_str_of(call, decision) : (aqua_str){"", 0};
    bool accept = strcmp(text.data, "Accept") == 0;
    if (decision.type != AQUA_STRING || (!accept && strcmp(text.data, "Defer") != 0 && strcmp(text.data, "Cancel") != 0)) {
        fail(call, "ResolveBack", "Expected Accept, Defer or Cancel.");
        return;
    }
#if defined(AQUA_OS_ANDROID)
    /* Accept closes the application, as the back gesture does when nothing handles it; Defer and Cancel keep it. */
    if (accept) {
        input_state *s = attach(call);
        if (s->window) aqua_window_set_should_close(s->window, true);
        call_activity("finish", "()V", false, false);
    }
#else
    fail(call, "ResolveBack", "Android input is unavailable on this platform.");
#endif
}

AQUA_LIBRARY(input) {
    aqua_class *module = aqua_define_module(library, "AndroidInput|Android輸入");
    aqua_define_function(module, "IsSupported|是否支援", is_supported, "");
    aqua_define_function(module, "Poll|讀取輸入", poll_input, "");
    /* Kinds are checked here, with the old messages. */
    aqua_define_function(module, "ShowKeyboard|顯示鍵盤", show_keyboard, "v");
    aqua_define_function(module, "Vibrate|震動", vibrate_for, "v");
    aqua_define_function(module, "ResolveBack|處理返回", resolve_back, "v");
#if defined(AQUA_OS_ANDROID)
    aqua_library_state(library, state_create, state_destroy);
    static const char *const snapshot_fields[] = {"orientation", "keyboardVisible", "backRequested", "insets", "touches"};
    static const char *const insets_fields[] = {"left", "top", "right", "bottom"};
    static const char *const touch_fields[] = {"id", "phase", "x", "y", "pressure", "tool", "timestamp", "history"};
    snapshot_class = define_record(library, "AndroidInput.Snapshot", snapshot_fields, 5);
    insets_class = define_record(library, "AndroidInput.Insets", insets_fields, 4);
    touch_class = define_record(library, "AndroidInput.Touch", touch_fields, 8);
#endif
}
