#include "codecs.h"
#include <stdlib.h>
#include <string.h>

/* Private copies of stb: every symbol static to this file, so another library vendoring stb never clashes. */
#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_NO_STDIO
#define STBI_FAILURE_USERMSG
#define STB_IMAGE_WRITE_STATIC
#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STBI_WRITE_NO_STDIO
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-function"
#pragma clang diagnostic ignored "-Wunused-parameter"
#pragma clang diagnostic ignored "-Wsign-compare"
#endif
#include "stb_image.h"
#include "stb_image_write.h"
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

uint8_t *gfx_codec_decode(const uint8_t *bytes, size_t length, int *width, int *height, const char **reason) {
    if (length > 0x7FFFFFFF) { *reason = "Image is too large."; return NULL; }
    int channels;
    uint8_t *pixels = stbi_load_from_memory(bytes, (int)length, width, height, &channels, 4);
    if (!pixels) { *reason = stbi_failure_reason(); return NULL; }
    /* stb allocates with malloc (STBI_MALLOC), so callers free() the result. */
    return pixels;
}

typedef struct png_output { uint8_t *data; size_t length, capacity; } png_output;
static void write_png(void *context, void *data, int size) {
    png_output *o = context;
    if (o->length + (size_t)size > o->capacity) {
        size_t capacity = (o->length + (size_t)size) * 2;
        uint8_t *grown = realloc(o->data, capacity);
        if (!grown) return;
        o->data = grown; o->capacity = capacity;
    }
    memcpy(o->data + o->length, data, (size_t)size);
    o->length += (size_t)size;
}

uint8_t *gfx_codec_png(const uint8_t *rgba, int width, int height, size_t *length) {
    png_output o = {0};
    if (!stbi_write_png_to_func(write_png, &o, width, height, 4, rgba, width * 4)) { free(o.data); return NULL; }
    *length = o.length;
    return o.data;
}

uint8_t *gfx_codec_gunzip(const uint8_t *bytes, size_t length, size_t expected) {
    /* RFC 1952 header: magic, method 8, flags, mtime, xfl, os, then optional fields. */
    if (length < 18 || bytes[0] != 31 || bytes[1] != 139 || bytes[2] != 8) return NULL;
    int flags = bytes[3];
    size_t at = 10;
    if (flags & 4) { if (at + 2 > length) return NULL; at += 2 + (size_t)(bytes[at] | bytes[at + 1] << 8); }
    if (flags & 8) { while (at < length && bytes[at]) at++; at++; }
    if (flags & 16) { while (at < length && bytes[at]) at++; at++; }
    if (flags & 2) at += 2;
    if (at + 8 > length || expected > 0x7FFFFFFF) return NULL;
    uint32_t size = (uint32_t)bytes[length - 4] | (uint32_t)bytes[length - 3] << 8 | (uint32_t)bytes[length - 2] << 16 | (uint32_t)bytes[length - 1] << 24;
    if (size != (uint32_t)expected) return NULL;
    uint8_t *out = malloc(expected ? expected : 1);
    if (!out) return NULL;
    int n = stbi_zlib_decode_noheader_buffer((char *)out, (int)expected, (const char *)bytes + at, (int)(length - at - 8));
    if (n != (int)expected) { free(out); return NULL; }
    /* CRC-32 of the output. */
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < expected; i++) {
        crc ^= out[i];
        for (int k = 0; k < 8; k++) crc = crc >> 1 ^ (0xEDB88320u & (0u - (crc & 1)));
    }
    crc = ~crc;
    uint32_t stored = (uint32_t)bytes[length - 8] | (uint32_t)bytes[length - 7] << 8 | (uint32_t)bytes[length - 6] << 16 | (uint32_t)bytes[length - 5] << 24;
    if (crc != stored) { free(out); return NULL; }
    return out;
}
