/* The Android layer's part of native library outputs (runtime/embed), in place of the native activity (platform.c and
   window.c): a library loads into any app or process, so it has no activity, looper or window. Its output goes to
   logcat (tag "Aquarius") and to standard output (a command-line process run through adb sees it; an app's standard
   output goes nowhere); the loop waits on a POSIX signal; JNI comes from the Java VM that loaded the library through the
   Java binding (runtime/embed/jni.c), when it did. */
#include <android/log.h>
#include <jni.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "../../host/host.h"
#include "../../embed/aqua/embed.h"
#include "../posix/posix.h"
#include <aqua/android.h>

/* Set by jni.c's JNI_OnLoad when the library has the Java binding; absent otherwise. */
extern JavaVM *embed_java_vm __attribute__((weak));

/* ---- Output: whole lines to logcat, and every byte to standard output ---- */
static char lines[2][4096];
static size_t line_length[2];
static void emit(int stream) {
    lines[stream][line_length[stream]] = 0;
    __android_log_write(stream ? ANDROID_LOG_WARN : ANDROID_LOG_INFO, "Aquarius", lines[stream]);
    line_length[stream] = 0;
}
/* Output goes to the browser console or the system log: plain text. */
bool platform_terminal_colors(int stream) { (void)stream; return false; }
void platform_write(int stream, const char *utf8, size_t length) {
    int s = stream == 2 ? 1 : 0;
    for (size_t written = 0; written < length;) {
        ssize_t n = write(stream == 2 ? 2 : 1, utf8 + written, length - written);
        if (n <= 0) break;
        written += (size_t)n;
    }
    for (size_t i = 0; i < length; i++) {
        if (utf8[i] == '\n' || line_length[s] == sizeof lines[s] - 1) emit(s);
        if (utf8[i] != '\n') lines[s][line_length[s]++] = utf8[i];
    }
}
void platform_flush(void) { for (int s = 0; s < 2; s++) if (line_length[s]) emit(s); }
void embed_platform_flush(void) { platform_flush(); }

/* ---- The loop: a signal posted work sets ---- */
static platform_signal *wake_signal;
void platform_wake(aqua_runtime *rt) { (void)rt; if (wake_signal) platform_signal_set(wake_signal); }
void platform_wait(aqua_runtime *rt, double timeout_ms) {
    (void)rt;
    platform_flush();
    if (!wake_signal) wake_signal = platform_signal_create();
    platform_signal_take(wake_signal, timeout_ms >= 1e8 ? -1 : timeout_ms);
}

/* ---- What only an activity has ---- */
bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt; (void)program; (void)arguments; (void)count;
    *error = strdup("execFile is not available on Android.");
    return false;
}
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) { (void)rt; (void)name; (void)length; return NULL; }
void platform_runtime_closing(aqua_runtime *rt) { (void)rt; }
char *platform_cache_directory(void) { return NULL; }
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
    (void)rt;
    const char *text = "Native library outputs cannot load other native libraries yet: ";
    *error = malloc(strlen(text) + strlen(name) + 2);
    strcpy(*error, text); strcat(*error, name); strcat(*error, ".");
    return false;
}

JNIEnv *aqua_android_jni(void) {
    if (!&embed_java_vm || !embed_java_vm) return NULL;
    static __thread JNIEnv *env;
    if (!env) (*embed_java_vm)->AttachCurrentThread(embed_java_vm, &env, NULL);
    return env;
}
jobject aqua_android_activity(void) { return NULL; }
void *aqua_android_assets(void) { return NULL; }
bool aqua_android_start_activity(jobject intent, aqua_android_result done, void *data) { (void)intent; (void)done; (void)data; return false; }
void aqua_android_request_permission(const char *permission, void (*done)(bool granted, void *data), void *data) { (void)permission; done(false, data); }
struct aqua_window *aqua_android_window(void) { return NULL; }

/* The layer's internal API that libraries' Android branches call (android.h): the same JNI, and no activity. */
JNIEnv *android_jni(void) { return aqua_android_jni(); }
jobject android_activity(void) { return NULL; }
jmethodID android_activity_method(const char *name, const char *signature) { (void)name; (void)signature; return NULL; }
