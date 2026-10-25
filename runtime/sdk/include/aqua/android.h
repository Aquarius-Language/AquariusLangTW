/* aqua/android.h — Android services only reachable through Java, for libraries' AQUA_OS_ANDROID branches. The runtime
   runs on the activity's native thread, which is attached to the Java VM. */
#ifndef AQUA_ANDROID_API_H
#define AQUA_ANDROID_API_H
#include <jni.h>
#ifdef __cplusplus
extern "C" {
#endif
/* The runtime thread's JNI environment and the running android.app.Activity. */
JNIEnv *aqua_android_jni(void);
jobject aqua_android_activity(void);
/* The android.content.res.AssetManager's native peer (AAssetManager*), for reading APK assets directly. */
void *aqua_android_assets(void);
#ifdef __cplusplus
}
#endif
#endif
