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
#endif
