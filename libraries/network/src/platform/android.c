/* Android services of the native engine, through Java where only Java has them: the trusted roots of the
 * AndroidCAStore key store (system certificates minus disabled ones, plus user-installed ones; the certificate
 * directories when Java is unavailable) and the app's network security policy for cleartext traffic, which
 * generated projects configure (HTTPS by default; HTTP for localhost, 127.0.0.1 and the emulator host 10.0.2.2).
 * Worker threads attach to the Java VM when they first need it and detach when they end. */
#include "../native/native.h"
#include <aqua/android.h>
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static JavaVM *vm;
static _Thread_local bool attached;

void platform_runtime_start(void) {
    JNIEnv *env = aqua_android_jni();
    if (env && !vm) (*env)->GetJavaVM(env, &vm);
}

static JNIEnv *java(void) {
    if (!vm) return NULL;
    JNIEnv *env = NULL;
    if ((*vm)->GetEnv(vm, (void **)&env, JNI_VERSION_1_6) == JNI_OK) return env;
    if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return NULL;
    attached = true;
    return env;
}

void platform_thread_exit(void) {
    if (attached && vm) { (*vm)->DetachCurrentThread(vm); attached = false; }
}

static bool failed(JNIEnv *env) {
    if (!(*env)->ExceptionCheck(env)) return false;
    (*env)->ExceptionClear(env);
    return true;
}

static bool java_roots(void (*add)(void *, const uint8_t *, size_t), void *context) {
    JNIEnv *env = java();
    if (!env || (*env)->PushLocalFrame(env, 32) != 0) return false;
    bool ok = false;
    jclass key_store = (*env)->FindClass(env, "java/security/KeyStore");
    jclass enumeration = (*env)->FindClass(env, "java/util/Enumeration");
    jclass certificate = (*env)->FindClass(env, "java/security/cert/Certificate");
    if (failed(env) || !key_store || !enumeration || !certificate) goto end;
    jmethodID get_instance = (*env)->GetStaticMethodID(env, key_store, "getInstance", "(Ljava/lang/String;)Ljava/security/KeyStore;");
    jmethodID load = (*env)->GetMethodID(env, key_store, "load", "(Ljava/security/KeyStore$LoadStoreParameter;)V");
    jmethodID aliases = (*env)->GetMethodID(env, key_store, "aliases", "()Ljava/util/Enumeration;");
    jmethodID get_certificate = (*env)->GetMethodID(env, key_store, "getCertificate", "(Ljava/lang/String;)Ljava/security/cert/Certificate;");
    jmethodID has_more = (*env)->GetMethodID(env, enumeration, "hasMoreElements", "()Z");
    jmethodID next = (*env)->GetMethodID(env, enumeration, "nextElement", "()Ljava/lang/Object;");
    jmethodID encoded = (*env)->GetMethodID(env, certificate, "getEncoded", "()[B");
    if (failed(env)) goto end;
    jobject store = (*env)->CallStaticObjectMethod(env, key_store, get_instance, (*env)->NewStringUTF(env, "AndroidCAStore"));
    if (failed(env) || !store) goto end;
    (*env)->CallVoidMethod(env, store, load, NULL);
    if (failed(env)) goto end;
    jobject names = (*env)->CallObjectMethod(env, store, aliases);
    if (failed(env) || !names) goto end;
    while ((*env)->CallBooleanMethod(env, names, has_more) && !failed(env)) {
        if ((*env)->PushLocalFrame(env, 8) != 0) break;
        jobject alias = (*env)->CallObjectMethod(env, names, next);
        jobject cert = alias && !failed(env) ? (*env)->CallObjectMethod(env, store, get_certificate, alias) : NULL;
        jbyteArray der = cert && !failed(env) ? (*env)->CallObjectMethod(env, cert, encoded) : NULL;
        if (der && !failed(env)) {
            jsize length = (*env)->GetArrayLength(env, der);
            jbyte *bytes = (*env)->GetByteArrayElements(env, der, NULL);
            if (bytes) { add(context, (const uint8_t *)bytes, (size_t)length); (*env)->ReleaseByteArrayElements(env, der, bytes, JNI_ABORT); ok = true; }
        }
        (*env)->PopLocalFrame(env, NULL);
    }
end:
    failed(env);
    (*env)->PopLocalFrame(env, NULL);
    return ok;
}

static void add_directory(const char *folder, void (*add)(void *, const uint8_t *, size_t), void *context) {
    DIR *directory = opendir(folder);
    if (!directory) return;
    for (struct dirent *entry; (entry = readdir(directory)) != NULL;) {
        if (entry->d_name[0] == '.') continue;
        char path[512];
        snprintf(path, sizeof path, "%s/%s", folder, entry->d_name);
        FILE *f = fopen(path, "rb");
        if (!f) continue;
        net_buffer b = {0};
        uint8_t chunk[8192];
        size_t n;
        while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) buffer_append(&b, chunk, n);
        fclose(f);
        if (b.length) add(context, b.data, b.length + 1);
        buffer_free(&b);
    }
    closedir(directory);
}

bool platform_trusted_roots(void (*add)(void *context, const uint8_t *der, size_t length), void *context) {
    if (java_roots(add, context)) return true;
    DIR *apex = opendir("/apex/com.android.conscrypt/cacerts");
    if (apex) { closedir(apex); add_directory("/apex/com.android.conscrypt/cacerts", add, context); }
    else add_directory("/system/etc/security/cacerts", add, context);
    return true;
}

bool platform_cleartext_permitted(const char *host) {
    JNIEnv *env = java();
    if (!env || (*env)->PushLocalFrame(env, 8) != 0) return true;
    bool permitted = true;
    jclass policy = (*env)->FindClass(env, "android/security/NetworkSecurityPolicy");
    if (!failed(env) && policy) {
        jmethodID get_instance = (*env)->GetStaticMethodID(env, policy, "getInstance", "()Landroid/security/NetworkSecurityPolicy;");
        jmethodID check = (*env)->GetMethodID(env, policy, "isCleartextTrafficPermitted", "(Ljava/lang/String;)Z");
        if (!failed(env)) {
            jobject instance = (*env)->CallStaticObjectMethod(env, policy, get_instance);
            if (!failed(env) && instance) {
                jboolean result = (*env)->CallBooleanMethod(env, instance, check, (*env)->NewStringUTF(env, host));
                if (!failed(env)) permitted = result;
            }
        }
    }
    failed(env);
    (*env)->PopLocalFrame(env, NULL);
    return permitted;
}
