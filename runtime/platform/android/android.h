/* The Android layer's internals shared between platform.c and window.c, and for libraries' Android branches. */
#ifndef AQUA_ANDROID_H
#define AQUA_ANDROID_H
#include <aqua/window.h>
#include <jni.h>

struct android_app;
struct android_app *android_application(void);
/* The runtime thread's JNI environment and the activity, for Android services only reachable through Java. */
JNIEnv *android_jni(void);
jobject android_activity(void);
/* Processes activity, input and window events for up to `timeout_ms` (-1 waits for the next one). */
void android_poll(int timeout_ms);
void android_window_start(struct android_app *app);
void android_windows_close(aqua_runtime *rt);
void platform_flush(void);
/* A method of the running activity's class (AquariusActivity), or NULL when the activity lacks it. */
jmethodID android_activity_method(const char *name, const char *signature);
/* Input-method events, on the runtime thread. A NULL composition ends it. */
void android_ime_text(const char *utf8);
void android_ime_composition(const char *utf8, int32_t cursor);
void android_ime_key(int32_t code, bool down);
#endif
