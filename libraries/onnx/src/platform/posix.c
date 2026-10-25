/* Linux and Android services of the onnx library. Linux loads libonnxruntime.so.1 from the folder of the runtime (or
 * this external library), then from the system's search path; Android loads libonnxruntime.so from the APK's native
 * library folder by name.
 *
 * Linux 與 Android 平台服務：以 dlopen 載入 ONNX Runtime、讀取模型檔。 */
#define _GNU_SOURCE
#include <aqua/aqua.h>
#include "../platform.h"
#include <dlfcn.h>
#include <errno.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#if defined(AQUA_OS_ANDROID)
#include <sys/system_properties.h>
#endif

static char *directory_of(const char *path) {
    char *resolved = path ? realpath(path, NULL) : NULL;
    char *slash = resolved ? strrchr(resolved, '/') : NULL;
    if (slash) *slash = 0;
    return resolved;
}

char *onnx_platform_executable_directory(void) {
    char path[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", path, sizeof path - 1);
    if (n <= 0) return NULL;
    path[n] = 0;
    return directory_of(path);
}

char *onnx_platform_binary_directory(void) {
    Dl_info info;
    char *directory = dladdr((void *)&onnx_platform_binary_directory, &info) && info.dli_fname ? directory_of(info.dli_fname) : NULL;
    return directory ? directory : onnx_platform_executable_directory();
}

bool onnx_platform_file_exists(const char *path) {
    struct stat status;
    return stat(path, &status) == 0 && S_ISREG(status.st_mode);
}

static void *runtime_library;

const OrtApiBase *onnx_platform_runtime(char *failure, size_t size) {
    if (!runtime_library) {
#if defined(AQUA_OS_ANDROID)
        runtime_library = dlopen("libonnxruntime.so", RTLD_NOW | RTLD_LOCAL);
        if (!runtime_library) {
            snprintf(failure, size, "無法載入 ONNX Runtime（libonnxruntime.so）：%s", dlerror());
            return NULL;
        }
#else
        char *directories[2] = {onnx_platform_binary_directory(), onnx_platform_executable_directory()};
        for (int i = 0; i < 2 && !runtime_library; i++) {
            if (!directories[i] || (i == 1 && directories[0] && strcmp(directories[0], directories[1]) == 0)) continue;
            char path[PATH_MAX + 32];
            snprintf(path, sizeof path, "%s/libonnxruntime.so.1", directories[i]);
            if (!onnx_platform_file_exists(path)) continue;
            runtime_library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
            if (!runtime_library) {
                snprintf(failure, size, "無法載入 ONNX Runtime（%s）：%s", path, dlerror());
                free(directories[0]); free(directories[1]);
                return NULL;
            }
        }
        free(directories[0]); free(directories[1]);
        if (!runtime_library) runtime_library = dlopen("libonnxruntime.so.1", RTLD_NOW | RTLD_LOCAL);
        if (!runtime_library) {
            snprintf(failure, size, "找不到 ONNX Runtime（libonnxruntime.so.1）；請將它放在程式旁。");
            return NULL;
        }
#endif
    }
    const OrtApiBase *(*entry)(void) = (const OrtApiBase *(*)(void))dlsym(runtime_library, "OrtGetApiBase");
    if (!entry) {
        snprintf(failure, size, "libonnxruntime 不是有效的 ONNX Runtime 程式庫。");
        return NULL;
    }
    return entry();
}

void *onnx_platform_symbol(const char *name) { return runtime_library ? dlsym(runtime_library, name) : NULL; }

uint8_t *onnx_platform_read_file(const char *path, size_t limit, size_t *length, bool *too_large) {
    *too_large = false;
    FILE *file = fopen(path, "rb");
    if (!file) return NULL;
    uint8_t *bytes = NULL;
    struct stat status;
    if (fstat(fileno(file), &status) == 0 && S_ISREG(status.st_mode)) {
        size_t size = (size_t)status.st_size;
        if (size > limit) *too_large = true;
        else if ((bytes = malloc(size ? size : 1)) != NULL) {
            if (fread(bytes, 1, size, file) != size) { free(bytes); bytes = NULL; }
            else *length = size;
        }
    }
    fclose(file);
    return bytes;
}

ORTCHAR_T *onnx_platform_path(const char *utf8) { return strdup(utf8); }

char *onnx_platform_data_directory(void) {
    const char *data = getenv("XDG_DATA_HOME");
    if (data && data[0] == '/') return strdup(data);
    const char *home = getenv("HOME");
    if (!home || !home[0]) return NULL;
    size_t n = strlen(home) + 16;
    char *path = malloc(n);
    if (path) snprintf(path, n, "%s/.local/share", home);
    return path;
}

bool onnx_platform_make_directories(const char *path) {
    char *copy = strdup(path);
    if (!copy) return false;
    for (char *p = copy + 1; ; p++) {
        if (*p == '/' || *p == 0) {
            char saved = *p;
            *p = 0;
            if (mkdir(copy, 0755) != 0 && errno != EEXIST) { free(copy); return false; }
            *p = saved;
            if (!saved) break;
        }
    }
    free(copy);
    return true;
}

void onnx_platform_prepare_cuda(void) {}

uint32_t onnx_platform_processor_count(void) {
    long count = sysconf(_SC_NPROCESSORS_ONLN);
    return count > 0 ? (uint32_t)count : 1;
}

bool onnx_platform_emulator(void) {
#if defined(AQUA_OS_ANDROID)
    /* The checks of the old Android host: Build.HARDWARE, Build.PRODUCT and Build.FINGERPRINT. */
    char hardware[PROP_VALUE_MAX] = "", product[PROP_VALUE_MAX] = "", fingerprint[PROP_VALUE_MAX] = "";
    __system_property_get("ro.hardware", hardware);
    __system_property_get("ro.product.name", product);
    __system_property_get("ro.build.fingerprint", fingerprint);
    return strstr(hardware, "ranchu") || strstr(hardware, "goldfish") || strstr(product, "sdk") || strncmp(fingerprint, "generic", 7) == 0;
#else
    return false;
#endif
}

static pthread_mutex_t engine_lock = PTHREAD_MUTEX_INITIALIZER;
void onnx_platform_lock(void) { pthread_mutex_lock(&engine_lock); }
void onnx_platform_unlock(void) { pthread_mutex_unlock(&engine_lock); }
