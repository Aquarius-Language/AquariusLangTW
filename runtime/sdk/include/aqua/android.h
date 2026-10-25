/* aqua/android.h — Android services only reachable through Java, for libraries' AQUA_OS_ANDROID branches. The runtime
   runs on the activity's native thread, which is attached to the Java VM. */
#ifndef AQUA_ANDROID_API_H
#define AQUA_ANDROID_API_H
#include <jni.h>
#include <stdbool.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* The runtime thread's JNI environment and the running android.app.Activity. */
JNIEnv *aqua_android_jni(void);
jobject aqua_android_activity(void);
/* The android.content.res.AssetManager's native peer (AAssetManager*), for reading APK assets directly. */
void *aqua_android_assets(void);

/* Starts an activity for a result (pickers, sharing, settings screens). `done` runs on the runtime's thread with the
   result code (android.app.Activity.RESULT_OK is -1) and the result Intent (a local reference, or NULL). */
typedef void (*aqua_android_result)(int32_t result_code, jobject intent, void *data);
bool aqua_android_start_activity(jobject intent, aqua_android_result done, void *data);
/* Asks for a runtime permission ("android.permission.CAMERA"); `done` runs on the runtime's thread. Already granted
   permissions complete immediately. */
void aqua_android_request_permission(const char *permission, void (*done)(bool granted, void *data), void *data);
/* Window insets of the system bars and display cutout in logical units, and whether the soft keyboard is visible. */
void aqua_android_insets(float *left, float *top, float *right, float *bottom, bool *keyboard_visible);
#ifdef __cplusplus
}
#endif
#endif
