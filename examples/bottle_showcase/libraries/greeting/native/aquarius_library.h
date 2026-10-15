#ifndef AQUARIUS_LIBRARY_H
#define AQUARIUS_LIBRARY_H
#include <stdint.h>
#if defined(_WIN32)
#define AQUA_EXPORT __declspec(dllexport)
#define AQUA_CALL __cdecl
#else
#define AQUA_EXPORT __attribute__((visibility("default")))
#define AQUA_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 1: caller owns buffers, <= 1 MiB each; one call, no retained pointers.
 * Return output byte count, or a negative application-defined error code.
 * UTF-8 uses lengths, not NUL termination. No exceptions across this ABI.
 * ABI 一：呼叫端擁有緩衝區；回傳輸出長度或負值錯誤碼。 */
AQUA_EXPORT int32_t AQUA_CALL aqua_library_abi(void);
typedef int32_t (AQUA_CALL *aqua_library_function)(const uint8_t *input, int32_t input_length,
    uint8_t *output, int32_t output_capacity);
/* Wasm also exports memory, aqua_alloc(i32)->i32, aqua_free(i32)->void.
 * Wasm 模組另須匯出記憶體、配置及釋放函式。 */
#ifdef __cplusplus
}
#endif
#endif
