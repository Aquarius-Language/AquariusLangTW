/* The Android layer of the host: a native activity (android_native_app_glue) whose thread runs the native_app from
   the APK's assets/aquarius/program.wasm. Output goes to logcat (tag "Aquarius"); assets stored beside the module are
   read from assets/aquarius/assets/ on first use. Window and input are in window.c. */
#include <android/asset_manager.h>
#include <android/log.h>
#include <android_native_app_glue.h>
#include <jni.h>
#include <stdlib.h>
#include <string.h>
#include "../../host/host.h"
#include "../posix/posix.h"
#include "android.h"
#include <aqua/android.h>

static struct android_app *native_app;
static aqua_runtime *runtime;

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
    runtime_options options = {app->activity->internalDataPath, NULL, NULL, 0, NULL};
    char *error = NULL;
    runtime = runtime_create(module, length, &options, &error);
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
    runtime_destroy(runtime);
    runtime = NULL;
    ANativeActivity_finish(app->activity);
    while (!app->destroyRequested) android_poll(-1);
}

/* ---- aqua/android.h ---- */
JNIEnv *aqua_android_jni(void) { return android_jni(); }
jobject aqua_android_activity(void) { return android_activity(); }
void *aqua_android_assets(void) { return native_app->activity->assetManager; }
/* Activity results, permissions and insets arrive through the APK's activity (to be added with the Android exporter);
   until then they report unavailable. */
bool aqua_android_start_activity(jobject intent, aqua_android_result done, void *data) {
    (void)intent; (void)done; (void)data;
    return false;
}
void aqua_android_request_permission(const char *permission, void (*done)(bool granted, void *data), void *data) {
    (void)permission;
    done(false, data);
}
void aqua_android_insets(float *left, float *top, float *right, float *bottom, bool *keyboard_visible) {
    *left = *top = *right = *bottom = 0;
    *keyboard_visible = false;
}
