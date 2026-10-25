/* Web services of the onnx library: ONNX Runtime is compiled from source into this side module (CMakeLists.txt), so
 * its entry point is linked directly. There are no plugin libraries, dynamic loading or file system paths; models come
 * from the application package or as bytes.
 *
 * 網頁平台服務：ONNX Runtime 由原始碼編譯進此側模組。 */
#include <aqua/aqua.h>
#include "../platform.h"
#include <stdlib.h>
#include <string.h>

const OrtApiBase *onnx_platform_runtime(char *failure, size_t size) {
    (void)failure;
    (void)size;
    return OrtGetApiBase();
}

void *onnx_platform_symbol(const char *name) { (void)name; return NULL; }
char *onnx_platform_binary_directory(void) { return NULL; }
char *onnx_platform_executable_directory(void) { return NULL; }
bool onnx_platform_file_exists(const char *path) { (void)path; return false; }

uint8_t *onnx_platform_read_file(const char *path, size_t limit, size_t *length, bool *too_large) {
    (void)path; (void)limit; (void)length;
    *too_large = false;
    return NULL;
}

ORTCHAR_T *onnx_platform_path(const char *utf8) { return strdup(utf8); }
char *onnx_platform_data_directory(void) { return NULL; }
bool onnx_platform_make_directories(const char *path) { (void)path; return false; }
void onnx_platform_prepare_cuda(void) {}
uint32_t onnx_platform_processor_count(void) { return 1; }
bool onnx_platform_emulator(void) { return false; }
void onnx_platform_lock(void) {}
void onnx_platform_unlock(void) {}
