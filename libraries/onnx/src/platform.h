/* The operating-system services of the onnx library: finding and loading ONNX Runtime and its plugin libraries, and
 * reading model files. platform/windows.c (wide-character Win32 paths), platform/posix.c (Linux, Android; dlopen)
 * and platform/web.c (ONNX Runtime linked into the side module) implement them; everything else is portable.
 *
 * 推論引擎函式庫的作業系統服務：載入 ONNX Runtime 與外掛、讀取模型檔。 */
#ifndef ONNX_PLATFORM_H
#define ONNX_PLATFORM_H
#include <onnxruntime_c_api.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(AQUA_OS_WINDOWS)
#define ONNX_LIBRARY_FILE(base) base ".dll"
#define ONNX_PATH_SEPARATOR ';'
#else
#define ONNX_LIBRARY_FILE(base) "lib" base ".so"
#define ONNX_PATH_SEPARATOR ':'
#endif

/* ONNX Runtime's entry point, loading the library on first use; NULL with the reason in `failure` when it is missing. */
const OrtApiBase *onnx_platform_runtime(char *failure, size_t size);
/* A symbol of the loaded ONNX Runtime library (NNAPI's append function), or NULL. */
void *onnx_platform_symbol(const char *name);
/* The directory of the binary containing this library (the runtime, or an external library) and of the executable,
   with '/' separators and no trailing one; malloc'd, NULL when unknown. */
char *onnx_platform_binary_directory(void);
char *onnx_platform_executable_directory(void);
bool onnx_platform_file_exists(const char *path);
/* The whole file, malloc'd; NULL when missing or unreadable, or with *too_large set when it exceeds `limit`. */
uint8_t *onnx_platform_read_file(const char *path, size_t limit, size_t *length, bool *too_large);
/* A UTF-8 path as ONNX Runtime's path type (UTF-16 on Windows), malloc'd. */
ORTCHAR_T *onnx_platform_path(const char *utf8);
/* The per-user application data root (LocalAppData, $XDG_DATA_HOME or ~/.local/share), malloc'd. */
char *onnx_platform_data_directory(void);
bool onnx_platform_make_directories(const char *path);
/* Windows: puts the folders holding NVIDIA's CUDA 13, cuBLAS and cuDNN 9 libraries on this process's search path
   before the CUDA provider loads (no-op elsewhere, where the system loader finds them). */
void onnx_platform_prepare_cuda(void);
uint32_t onnx_platform_processor_count(void);
/* Android: whether this is the emulator, where accelerated providers are only used on explicit request. */
bool onnx_platform_emulator(void);
/* Serializes the process-wide engine state (device probing, plugin registration) between runtimes. */
void onnx_platform_lock(void);
void onnx_platform_unlock(void);
#endif
