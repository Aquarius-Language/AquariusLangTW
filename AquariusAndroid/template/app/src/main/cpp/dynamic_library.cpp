#include <jni.h>
#include <dlfcn.h>
#include <cstdint>
#include <vector>
#include <string>

static constexpr int maxBytes = 1024 * 1024;
static void failure(JNIEnv* env, const char* message) {
    env->ThrowNew(env->FindClass("java/lang/IllegalStateException"), message);
}
extern "C" JNIEXPORT jlong JNICALL
Java_org_aquariuslang_android_DynamicLibraries_open(JNIEnv* env, jclass, jstring name) {
    const char* path = env->GetStringUTFChars(name, nullptr);
    if (!path) return 0;
    void* handle = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    env->ReleaseStringUTFChars(name, path);
    if (!handle) { failure(env, dlerror()); return 0; }
    auto version = reinterpret_cast<int32_t(*)()>(dlsym(handle, "aqua_library_abi"));
    if (!version || version() != 1) { dlclose(handle); failure(env, "Unsupported library ABI / 不支援的函式庫介面版本"); return 0; }
    return reinterpret_cast<jlong>(handle);
}
extern "C" JNIEXPORT jboolean JNICALL
Java_org_aquariuslang_android_DynamicLibraries_has(JNIEnv* env, jclass, jlong handle, jstring symbol) {
    const char* name = env->GetStringUTFChars(symbol, nullptr);
    if (!name) return false;
    bool found = dlsym(reinterpret_cast<void*>(handle), name) != nullptr;
    env->ReleaseStringUTFChars(symbol, name); return found;
}
extern "C" JNIEXPORT jbyteArray JNICALL
Java_org_aquariuslang_android_DynamicLibraries_invoke(JNIEnv* env, jclass, jlong handle, jstring symbol, jbyteArray input) {
    int length = env->GetArrayLength(input);
    if (length > maxBytes) { failure(env, "Input exceeds 1 MiB"); return nullptr; }
    const char* name = env->GetStringUTFChars(symbol, nullptr);
    if (!name) return nullptr;
    auto function = reinterpret_cast<int32_t(*)(const uint8_t*, int32_t, uint8_t*, int32_t)>(dlsym(reinterpret_cast<void*>(handle), name));
    env->ReleaseStringUTFChars(symbol, name);
    if (!function) { failure(env, "Missing library symbol / 找不到函式庫符號"); return nullptr; }
    try {
        // Reused per thread: avoids zero-filling a fresh 1 MiB vector on every call.
        thread_local std::vector<uint8_t> source, output;
        if (source.size() < (size_t)(length ? length : 1)) source.resize(length ? length : 1);
        if (output.size() < (size_t)maxBytes) output.resize(maxBytes);
        env->GetByteArrayRegion(input, 0, length, reinterpret_cast<jbyte*>(source.data()));
        if (env->ExceptionCheck()) return nullptr;
        int count = function(source.data(), length, output.data(), maxBytes);
        if (count < 0 || count > maxBytes) { failure(env, "Library call failed or result exceeds 1 MiB / 函式庫呼叫失敗或輸出超過上限"); return nullptr; }
        auto result = env->NewByteArray(count);
        if (result) env->SetByteArrayRegion(result, 0, count, reinterpret_cast<jbyte*>(output.data()));
        return result;
    } catch (...) { failure(env, "Native library failure / 原生函式庫錯誤"); return nullptr; }
}
extern "C" JNIEXPORT void JNICALL
Java_org_aquariuslang_android_DynamicLibraries_release(JNIEnv*, jclass, jlong handle) { if (handle) dlclose(reinterpret_cast<void*>(handle)); }
