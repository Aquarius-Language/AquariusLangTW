/* The native part of the sparkle example library: one module, SparkleNative / 閃耀原生, registered through the SDK.
   The same file builds for every target (aqua library build --targets windows-x64,linux-x64,android-*,web). */
#include <aqua/aqua.h>
#include <stdio.h>
#include <string.h>

/* checksum(text): the 32-bit FNV-1a hash of the text's UTF-8 bytes, as eight hexadecimal digits. */
static void checksum(aqua_call *call) {
    aqua_str text = aqua_arg_str(call, 0);
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < text.length; i++) {
        hash ^= (uint8_t)text.data[i];
        hash *= 16777619u;
    }
    char digits[9];
    snprintf(digits, sizeof digits, "%08x", hash);
    aqua_return_cstring(call, digits);
}

/* repeat(text, count): the text repeated count times (0 to 1000). */
static void repeat(aqua_call *call) {
    aqua_str text = aqua_arg_str(call, 0);
    int32_t count = aqua_arg_int(call, 1);
    if (count < 0 || count > 1000) { aqua_fail(call, "SparkleNative.repeat: count must be 0 to 1000, got %d.", count); return; }
    char *result = aqua_scratch(call, text.length * (size_t)count + 1);
    for (int32_t i = 0; i < count; i++) memcpy(result + text.length * (size_t)i, text.data, text.length);
    aqua_return_text(call, result, text.length * (size_t)count);
}

AQUA_LIBRARY(sparkle) {
    aqua_class *module = aqua_define_module(library, "SparkleNative|閃耀原生");
    aqua_define_function(module, "checksum|指紋", checksum, "s");
    aqua_define_function(module, "repeat|重複", repeat, "si");
    aqua_define_int(module, "VERSION|版本", 1);
}
