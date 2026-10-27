/* The Android layer of the host: a native activity (android_native_app_glue) whose thread runs the native_app from
   the APK's assets/aquarius/program.wasm. Output goes to logcat (tag "Aquarius"); assets stored beside the module are
   read from assets/aquarius/assets/ on first use. Window and input are in window.c. */
#include <android/asset_manager.h>
#include <android/log.h>
#include <android_native_app_glue.h>
#include <jni.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include "../../host/host.h"
#include "../../host/engine.h"
#include "../posix/posix.h"
#include "android.h"
#include <aqua/android.h>

static struct android_app *native_app;
static aqua_runtime *runtime;
static pthread_mutex_t runtime_lock = PTHREAD_MUTEX_INITIALIZER; /* Java threads posting vs. the runtime ending */

struct android_app *android_application(void) { return native_app; }
JNIEnv *android_jni(void) {
    static __thread JNIEnv *env;
    if (!env) (*native_app->activity->vm)->AttachCurrentThread(native_app->activity->vm, &env, NULL);
    return env;
}
jobject android_activity(void) { return native_app->activity->clazz; }

/* ---- Output: whole lines to logcat ---- */
static char lines[2][4096];
static size_t line_length[2];
static void emit(int stream) {
    /* A CR before the newline (text from CRLF sources) would show as an extra line in logcat. */
    if (line_length[stream] && lines[stream][line_length[stream] - 1] == '\r') line_length[stream]--;
    lines[stream][line_length[stream]] = 0;
    __android_log_write(stream ? ANDROID_LOG_WARN : ANDROID_LOG_INFO, "Aquarius", lines[stream]);
    line_length[stream] = 0;
}
void platform_write(int stream, const char *utf8, size_t length) {
    int s = stream == 2 ? 1 : 0;
    for (size_t i = 0; i < length; i++) {
        if (utf8[i] == '\n' || line_length[s] == sizeof lines[s] - 1) emit(s);
        if (utf8[i] != '\n') lines[s][line_length[s]++] = utf8[i];
    }
}
void platform_flush(void) { for (int s = 0; s < 2; s++) if (line_length[s]) emit(s); }

/* ---- The loop's wake event: the activity thread's looper ---- */
void platform_wake(aqua_runtime *rt) { (void)rt; if (native_app) ALooper_wake(native_app->looper); }
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    android_poll(timeout_ms >= 1e8 ? -1 : (int)(timeout_ms < 1 ? 1 : timeout_ms));
}

bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt; (void)program; (void)arguments; (void)count;
    *error = strdup("execFile is not available on Android.");
    return false;
}

const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) {
    (void)rt;
    char path[1200];
    snprintf(path, sizeof path, "aquarius/assets/%s", name);
    AAsset *asset = AAssetManager_open(native_app->activity->assetManager, path, AASSET_MODE_BUFFER);
    if (!asset) return NULL;
    size_t n = (size_t)AAsset_getLength(asset);
    uint8_t *data = malloc(n ? n : 1);
    memcpy(data, AAsset_getBuffer(asset), n);
    AAsset_close(asset);
    *length = n;
    return data;
}

void platform_runtime_closing(aqua_runtime *rt) { android_windows_close(rt); }

/* <app data>/cache/aquarius: the application's cache directory (Context.getCacheDir), which the system may clear. */
char *platform_cache_directory(void) {
    const char *files = native_app ? native_app->activity->internalDataPath : NULL; /* <app data>/files */
    const char *slash = files ? strrchr(files, '/') : NULL;
    if (!slash) return NULL;
    size_t n = (size_t)(slash - files) + 32;
    char *path = malloc(n);
    snprintf(path, n, "%.*s/cache/aquarius", (int)(slash - files), files);
    return path;
}

static uint8_t *read_asset(const char *path, size_t *length) {
    AAsset *asset = AAssetManager_open(native_app->activity->assetManager, path, AASSET_MODE_BUFFER);
    if (!asset) return NULL;
    size_t n = (size_t)AAsset_getLength(asset);
    uint8_t *data = malloc(n);
    memcpy(data, AAsset_getBuffer(asset), n);
    AAsset_close(asset);
    *length = n;
    return data;
}

/* An integer extra of the launching intent (adb shell am start --ei frames N), or `fallback`. */
static int intent_int(const char *name, int fallback) {
    JNIEnv *env = android_jni();
    jobject activity = android_activity();
    jclass activity_class = (*env)->GetObjectClass(env, activity);
    jobject intent = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, activity_class, "getIntent", "()Landroid/content/Intent;"));
    if (!intent) return fallback;
    jclass intent_class = (*env)->GetObjectClass(env, intent);
    jstring key = (*env)->NewStringUTF(env, name);
    int value = (*env)->CallIntMethod(env, intent, (*env)->GetMethodID(env, intent_class, "getIntExtra", "(Ljava/lang/String;I)I"), key, fallback);
    (*env)->DeleteLocalRef(env, key);
    return value;
}

void android_main(struct android_app *app) {
    native_app = app;
    android_window_start(app);
    /* Wait until the activity is running, so a surface can appear when a library opens a window. */
    int frames = intent_int("frames", 0);
    if (frames > 0) { char text[16]; snprintf(text, sizeof text, "%d", frames); setenv("AQUARIUS_GRAPHICS_FRAMES", text, 1); }
    size_t length = 0;
    uint8_t *module = read_asset("aquarius/program.wasm", &length);
    if (!module) { __android_log_write(ANDROID_LOG_ERROR, "Aquarius", "AQUARIUS_ANDROID_ERROR assets/aquarius/program.wasm is missing"); ANativeActivity_finish(app->activity); return; }
    /* Assets a library needs as files (certificates, models it opens by path) are written under the app's own storage. */
    char materialize[1024];
    snprintf(materialize, sizeof materialize, "%s/aquarius-resources", app->activity->internalDataPath);
    runtime_options options = {app->activity->internalDataPath, NULL, NULL, 0, materialize};
    char *error = NULL;
    aqua_runtime *created = runtime_create(module, length, &options, &error);
    pthread_mutex_lock(&runtime_lock);
    runtime = created;
    pthread_mutex_unlock(&runtime_lock);
    if (!runtime) {
        __android_log_print(ANDROID_LOG_ERROR, "Aquarius", "AQUARIUS_ANDROID_ERROR %s", error ? error : "cannot load the native_app");
        free(module);
        ANativeActivity_finish(app->activity);
        return;
    }
    __android_log_write(ANDROID_LOG_INFO, "Aquarius", "AQUARIUS_ENGINE Wasmtime " AQUA_WASMTIME_VERSION);
    int code = 1;
    if (runtime_start(runtime, NULL)) {
        double wait;
        while (!app->destroyRequested && (wait = runtime_turn(runtime, 16)) >= 0) {
            if (wait > 0) platform_wait(runtime, wait); else android_poll(0);
        }
        code = runtime->exit_code;
    }
    platform_flush();
    __android_log_write(code ? ANDROID_LOG_ERROR : ANDROID_LOG_INFO, "Aquarius", code ? "AQUARIUS_ANDROID_ERROR exit 1" : "AQUARIUS_ANDROID_OK");
    pthread_mutex_lock(&runtime_lock);
    runtime = NULL;
    pthread_mutex_unlock(&runtime_lock);
    runtime_destroy(created);
    ANativeActivity_finish(app->activity);
    while (!app->destroyRequested) android_poll(-1);
}

/* External libraries are packaged in the APK's lib/<abi>/ folder, which the linker searches by file name. */
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
    (void)rt;
    char file[128];
    snprintf(file, sizeof file, "libaqua_%s.so", name);
    return library_load(file, error);
}

/* ---- aqua/android.h ---- */
JNIEnv *aqua_android_jni(void) { return android_jni(); }
jobject aqua_android_activity(void) { return android_activity(); }
void *aqua_android_assets(void) { return native_app->activity->assetManager; }
jmethodID android_activity_method(const char *name, const char *signature) {
    JNIEnv *env = android_jni();
    jclass activity_class = (*env)->GetObjectClass(env, android_activity());
    jmethodID method = (*env)->GetMethodID(env, activity_class, name, signature);
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->DeleteLocalRef(env, activity_class);
    return method;
}

/* ---- Activity results, permissions and input-method text from AquariusActivity.java ----
   Java calls these natives on the UI thread; each posts its event to the runtime thread, where requests live. */
typedef struct request {
    int32_t id;
    aqua_android_result result;
    void (*permission)(bool granted, void *data);
    void *data;
    struct request *next;
} request;
static request *requests;
static int32_t next_request = 1;

typedef struct completion { int32_t id, result; bool granted; jobject intent; } completion;
static void complete(aqua_runtime *rt, void *data) {
    (void)rt;
    completion *c = data;
    for (request **p = &requests; *p; p = &(*p)->next) {
        if ((*p)->id != c->id) continue;
        request *r = *p;
        *p = r->next;
        if (r->result) r->result(c->result, c->intent, r->data);
        else r->permission(c->granted, r->data);
        free(r);
        break;
    }
    if (c->intent) (*android_jni())->DeleteGlobalRef(android_jni(), c->intent);
    free(c);
}
static void post(void (*task)(aqua_runtime *, void *), void *data) {
    pthread_mutex_lock(&runtime_lock);
    if (runtime) aqua_post(runtime, task, data);
    pthread_mutex_unlock(&runtime_lock);
}

JNIEXPORT void JNICALL Java_org_aquariuslang_runtime_AquariusActivity_nativeActivityResult(JNIEnv *env, jclass c, jint id, jint result, jobject intent) {
    (void)c;
    completion *done = calloc(1, sizeof *done);
    done->id = id; done->result = result;
    done->intent = intent ? (*env)->NewGlobalRef(env, intent) : NULL;
    post(complete, done);
}
JNIEXPORT void JNICALL Java_org_aquariuslang_runtime_AquariusActivity_nativePermissionResult(JNIEnv *env, jclass c, jint id, jboolean granted) {
    (void)env; (void)c;
    completion *done = calloc(1, sizeof *done);
    done->id = id; done->granted = granted;
    post(complete, done);
}

typedef struct ime_event { int kind; int32_t value; bool down; char *text; } ime_event;
static void ime(aqua_runtime *rt, void *data) {
    (void)rt;
    ime_event *e = data;
    if (e->kind == 0) android_ime_text(e->text);
    else if (e->kind == 1) android_ime_composition(e->text, e->value);
    else android_ime_key(e->value, e->down);
    free(e->text);
    free(e);
}
static char *java_text(JNIEnv *env, jstring text) {
    if (!text) return NULL;
    const char *utf8 = (*env)->GetStringUTFChars(env, text, NULL);
    char *copy = strdup(utf8);
    (*env)->ReleaseStringUTFChars(env, text, utf8);
    return copy;
}
JNIEXPORT void JNICALL Java_org_aquariuslang_runtime_AquariusActivity_nativeText(JNIEnv *env, jclass c, jstring text) {
    (void)c;
    ime_event *e = calloc(1, sizeof *e);
    e->kind = 0; e->text = java_text(env, text);
    if (e->text) post(ime, e); else free(e);
}
JNIEXPORT void JNICALL Java_org_aquariuslang_runtime_AquariusActivity_nativeComposition(JNIEnv *env, jclass c, jstring text, jint cursor) {
    (void)c;
    ime_event *e = calloc(1, sizeof *e);
    e->kind = 1; e->text = java_text(env, text); e->value = cursor;
    post(ime, e);
}
JNIEXPORT void JNICALL Java_org_aquariuslang_runtime_AquariusActivity_nativeKey(JNIEnv *env, jclass c, jint code, jboolean down) {
    (void)env; (void)c;
    ime_event *e = calloc(1, sizeof *e);
    e->kind = 2; e->value = code; e->down = down;
    post(ime, e);
}

static request *add_request(void) {
    request *r = calloc(1, sizeof *r);
    r->id = next_request++;
    if (next_request > 0xffff) next_request = 1;
    r->next = requests;
    requests = r;
    return r;
}

bool aqua_android_start_activity(jobject intent, aqua_android_result done, void *data) {
    jmethodID start = android_activity_method("startForResult", "(Landroid/content/Intent;I)V");
    if (!start) return false;
    request *r = add_request();
    r->result = done; r->data = data;
    JNIEnv *env = android_jni();
    (*env)->CallVoidMethod(env, android_activity(), start, intent, (jint)r->id);
    return true;
}

void aqua_android_request_permission(const char *permission, void (*done)(bool granted, void *data), void *data) {
    jmethodID ask = android_activity_method("requestPermission", "(Ljava/lang/String;I)V");
    if (!ask) { done(false, data); return; }
    request *r = add_request();
    r->permission = done; r->data = data;
    JNIEnv *env = android_jni();
    jstring name = (*env)->NewStringUTF(env, permission);
    (*env)->CallVoidMethod(env, android_activity(), ask, name, (jint)r->id);
    (*env)->DeleteLocalRef(env, name);
}

void aqua_android_insets(float *left, float *top, float *right, float *bottom, bool *keyboard_visible) {
    *left = *top = *right = *bottom = 0;
    *keyboard_visible = false;
    jmethodID insets = android_activity_method("insets", "()[F");
    if (!insets) return;
    JNIEnv *env = android_jni();
    jfloatArray values = (*env)->CallObjectMethod(env, android_activity(), insets);
    if (!values) return;
    float v[5] = {0};
    (*env)->GetFloatArrayRegion(env, values, 0, 5, v);
    (*env)->DeleteLocalRef(env, values);
    float scale = aqua_window_content_scale(NULL);
    *left = v[0] / scale; *top = v[1] / scale; *right = v[2] / scale; *bottom = v[3] / scale;
    *keyboard_visible = v[4] != 0;
}

/* Android has no runtime JIT yet (runtime/platform/desktop/jit.c): hot loops keep their generic code. */
int32_t platform_jit(engine *e, int32_t loop) { (void)e; (void)loop; return -1; }
