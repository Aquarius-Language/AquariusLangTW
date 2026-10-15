#include "aquarius_library.h"
#if defined(_WIN32)
int __stdcall _DllMainCRTStartup(void* module, unsigned long reason, void* reserved) { return 1; }
#endif

AQUA_EXPORT int32_t AQUA_CALL aqua_library_abi(void) { return 1; }
AQUA_EXPORT int32_t AQUA_CALL echo(const uint8_t* input, int32_t length, uint8_t* output, int32_t capacity) {
    if (length < 0 || length > capacity) return -1;
    for (int32_t i = 0; i < length; i++) output[i] = input[i];
    return length;
}
AQUA_EXPORT int32_t AQUA_CALL greeting(const uint8_t* input, int32_t length, uint8_t* output, int32_t capacity) {
    (void)input; (void)length;
    const uint8_t text[] = "Hello from a dynamic library! / 動態函式庫向你問好！";
    return echo(text, sizeof(text) - 1, output, capacity);
}
AQUA_EXPORT int32_t AQUA_CALL sum(const uint8_t* input, int32_t length, uint8_t* output, int32_t capacity) {
    if (length < 0 || capacity < 1) return -1;
    uint8_t value = 0; for (int32_t i = 0; i < length; i++) value += input[i];
    output[0] = value; return 1;
}
static uint8_t calls;
AQUA_EXPORT int32_t AQUA_CALL counter(const uint8_t* input, int32_t length, uint8_t* output, int32_t capacity) {
    (void)input; (void)length; if (capacity < 1) return -1; output[0] = ++calls; return 1;
}
/* Error fixtures also demonstrate the ABI's explicit negative-result contract. */
AQUA_EXPORT int32_t AQUA_CALL failure(const uint8_t* i, int32_t n, uint8_t* o, int32_t c) { return -7; }
AQUA_EXPORT int32_t AQUA_CALL oversized(const uint8_t* i, int32_t n, uint8_t* o, int32_t c) { return c + 1; }
AQUA_EXPORT int32_t AQUA_CALL invalid_utf8(const uint8_t* i, int32_t n, uint8_t* o, int32_t c) { if (c < 1) return -1; o[0] = 255; return 1; }

#if defined(__wasm__)
/* Small LIFO allocator for the two host-owned call buffers; no libc or imports. */
static uint8_t arena[2 * 1024 * 1024 + 32];
static uint32_t top;
AQUA_EXPORT uint32_t aqua_alloc(uint32_t length) {
    uint32_t size = (length + 7u) & ~7u;
    if (!length || length > 1024 * 1024 || top + size + 8 > sizeof(arena)) return 0;
    uint32_t previous = top; top += 8 + size;
    *(uint32_t*)(arena + previous) = previous;
    return (uint32_t)(arena + previous + 8);
}
AQUA_EXPORT void aqua_free(uint32_t pointer) { if (pointer) top = *(uint32_t*)(pointer - 8); }
#endif
