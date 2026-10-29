/* Android: ViewConfiguration's timeouts and touch slop, night mode, the animator duration scale (0 when the user
 * turned animations off), system bar insets, and a back gesture no handler took closes the activity, as Android's own
 * applications do on their first screen. */
#include "../interaction.h"
#include <aqua/android.h>

static bool failed(JNIEnv *env) {
    if (!(*env)->ExceptionCheck(env)) return false;
    (*env)->ExceptionClear(env);
    return true;
}

static int32_t static_int(JNIEnv *env, jclass cls, const char *name, int32_t fallback) {
    jmethodID method = (*env)->GetStaticMethodID(env, cls, name, "()I");
    if (failed(env) || !method) return fallback;
    int32_t value = (*env)->CallStaticIntMethod(env, cls, method);
    return failed(env) ? fallback : value;
}

static jobject resources(JNIEnv *env) {
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jobject result = (*env)->CallObjectMethod(env, aqua_android_activity(),
        (*env)->GetMethodID(env, context, "getResources", "()Landroid/content/res/Resources;"));
    (*env)->DeleteLocalRef(env, context);
    return failed(env) ? NULL : result;
}

static float density(JNIEnv *env, jobject res) {
    jclass cls = (*env)->GetObjectClass(env, res);
    jobject metrics = (*env)->CallObjectMethod(env, res, (*env)->GetMethodID(env, cls, "getDisplayMetrics", "()Landroid/util/DisplayMetrics;"));
    (*env)->DeleteLocalRef(env, cls);
    if (failed(env) || !metrics) return 1;
    jclass metrics_class = (*env)->GetObjectClass(env, metrics);
    float value = (*env)->GetFloatField(env, metrics, (*env)->GetFieldID(env, metrics_class, "density", "F"));
    (*env)->DeleteLocalRef(env, metrics_class);
    (*env)->DeleteLocalRef(env, metrics);
    return failed(env) || value <= 0 ? 1 : value;
}

static bool night(JNIEnv *env, jobject res) {
    jclass cls = (*env)->GetObjectClass(env, res);
    jobject configuration = (*env)->CallObjectMethod(env, res, (*env)->GetMethodID(env, cls, "getConfiguration", "()Landroid/content/res/Configuration;"));
    (*env)->DeleteLocalRef(env, cls);
    if (failed(env) || !configuration) return false;
    jclass configuration_class = (*env)->GetObjectClass(env, configuration);
    int32_t mode = (*env)->GetIntField(env, configuration, (*env)->GetFieldID(env, configuration_class, "uiMode", "I"));
    (*env)->DeleteLocalRef(env, configuration_class);
    (*env)->DeleteLocalRef(env, configuration);
    return !failed(env) && (mode & 0x30) == 0x20;   /* UI_MODE_NIGHT_MASK, UI_MODE_NIGHT_YES */
}

static float scaled_touch_slop(JNIEnv *env, jclass view_configuration) {
    jobject config = (*env)->CallStaticObjectMethod(env, view_configuration,
        (*env)->GetStaticMethodID(env, view_configuration, "get", "(Landroid/content/Context;)Landroid/view/ViewConfiguration;"), aqua_android_activity());
    if (failed(env) || !config) return -1;
    int32_t slop = (*env)->CallIntMethod(env, config, (*env)->GetMethodID(env, view_configuration, "getScaledTouchSlop", "()I"));
    (*env)->DeleteLocalRef(env, config);
    return failed(env) ? -1 : (float)slop;
}

static bool animations_off(JNIEnv *env) {
    jclass context = (*env)->FindClass(env, "android/content/Context");
    jobject resolver = (*env)->CallObjectMethod(env, aqua_android_activity(),
        (*env)->GetMethodID(env, context, "getContentResolver", "()Landroid/content/ContentResolver;"));
    (*env)->DeleteLocalRef(env, context);
    if (failed(env) || !resolver) return false;
    jclass settings = (*env)->FindClass(env, "android/provider/Settings$Global");
    jstring name = (*env)->NewStringUTF(env, "animator_duration_scale");
    float scale = (*env)->CallStaticFloatMethod(env, settings,
        (*env)->GetStaticMethodID(env, settings, "getFloat", "(Landroid/content/ContentResolver;Ljava/lang/String;F)F"), resolver, name, 1.0f);
    bool off = !failed(env) && scale == 0;
    (*env)->DeleteLocalRef(env, name);
    (*env)->DeleteLocalRef(env, settings);
    (*env)->DeleteLocalRef(env, resolver);
    return off;
}

void platform_conventions(conventions *c) {
    JNIEnv *env = aqua_android_jni();
    jclass view_configuration = (*env)->FindClass(env, "android/view/ViewConfiguration");
    jobject res = resources(env);
    float scale = res ? density(env, res) : 1, slop = scaled_touch_slop(env, view_configuration);
    *c = (conventions){
        .form_factor = "mobile", .primary = MOD_CONTROL,
        .double_click_ms = static_int(env, view_configuration, "getDoubleTapTimeout", 300),
        .long_press_ms = static_int(env, view_configuration, "getLongPressTimeout", 400),
        .drag_threshold = 8, .touch_slop = slop > 0 ? slop / scale : 8,
        .wheel_step = 64, .caret_blink_ms = 1000, .minimum_target = 48,
        .scrollbars = "overlay", .context_menu_on_release = false,
        .hover = false, .back_button = true, .dark = res && night(env, res), .reduced_motion = animations_off(env),
    };
    if (res) (*env)->DeleteLocalRef(env, res);
    (*env)->DeleteLocalRef(env, view_configuration);
}

void platform_back_unhandled(aqua_window *window) {
    if (window) aqua_window_set_should_close(window, true);
    JNIEnv *env = aqua_android_jni();
    jobject activity = aqua_android_activity();
    jclass cls = (*env)->GetObjectClass(env, activity);
    (*env)->CallVoidMethod(env, activity, (*env)->GetMethodID(env, cls, "finish", "()V"));
    failed(env);
    (*env)->DeleteLocalRef(env, cls);
}

void platform_back_guard(bool enabled, void (*back)(void *data), void *data) { (void)enabled; (void)back; (void)data; }

void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard) {
    aqua_android_insets(left, top, right, bottom, keyboard);
}
