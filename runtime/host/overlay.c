/* The application overlay (overlay.h): read and checked without starting a runtime. */
#include <stdlib.h>
#include <string.h>
#include "host.h"
#include "overlay.h"
#include "sha256.h"
uint8_t *platform_read_tail(const char *path, size_t count, size_t *length, uint64_t *size);

#define OVERLAY_FOOTER 64
static const char overlay_magic[16] = {'A', 'Q', 'U', 'A', 'R', 'I', 'U', 'S', '-', 'A', 'P', 'P', '-', 'V', '2', '!'};

static uint32_t overlay_hex4(const char **text, const char *end) {
    if (end - *text < 4) return UINT32_MAX;
    uint32_t value = 0;
    for (int i = 0; i < 4; i++) {
        unsigned char c = (unsigned char)*(*text)++;
        int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
        if (digit < 0) return UINT32_MAX;
        value = (value << 4) | (uint32_t)digit;
    }
    return value;
}

/* Decode the overlay's JSON string, including UTF-16 escapes emitted by System.Text.Json. No runtime is started yet. */
static char *overlay_entry(const char *text, size_t length) {
    const char *end = text + length, *p = NULL;
    for (size_t i = 0; i + 12 <= length; i++) if (memcmp(text + i, "\"EntryPoint\"", 12) == 0) { p = text + i + 12; break; }
    if (!p) return NULL;
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
    if (p == end || *p++ != ':') return NULL;
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
    if (p == end || *p++ != '"') return NULL;
    char *result = malloc((size_t)(end - p) + 1), *out = result;
    if (!result) return NULL;
    while (p < end) {
        unsigned char c = (unsigned char)*p++;
        if (c == '"') { if (out == result) break; *out = 0; return result; }
        if (c < 0x20) break;
        if (c != '\\') { *out++ = (char)c; continue; }
        if (p == end) break;
        c = (unsigned char)*p++;
        if (c == 'u') {
            uint32_t code = overlay_hex4(&p, end);
            if (code >= 0xd800 && code <= 0xdbff) {
                if (end - p < 6 || p[0] != '\\' || p[1] != 'u') break;
                p += 2;
                uint32_t low = overlay_hex4(&p, end);
                if (low < 0xdc00 || low > 0xdfff) break;
                code = 0x10000 + ((code - 0xd800) << 10) + (low - 0xdc00);
            } else if (code >= 0xdc00 && code <= 0xdfff) break;
            if (code == 0 || code == UINT32_MAX) break; /* Entry paths cannot contain NUL. */
            if (code < 0x80) *out++ = (char)code;
            else if (code < 0x800) { *out++ = (char)(0xc0 | (code >> 6)); *out++ = (char)(0x80 | (code & 0x3f)); }
            else if (code < 0x10000) { *out++ = (char)(0xe0 | (code >> 12)); *out++ = (char)(0x80 | ((code >> 6) & 0x3f)); *out++ = (char)(0x80 | (code & 0x3f)); }
            else { *out++ = (char)(0xf0 | (code >> 18)); *out++ = (char)(0x80 | ((code >> 12) & 0x3f)); *out++ = (char)(0x80 | ((code >> 6) & 0x3f)); *out++ = (char)(0x80 | (code & 0x3f)); }
        } else if (c == '"' || c == '\\' || c == '/') *out++ = (char)c;
        else if (c == 'b') *out++ = '\b';
        else if (c == 'f') *out++ = '\f';
        else if (c == 'n') *out++ = '\n';
        else if (c == 'r') *out++ = '\r';
        else if (c == 't') *out++ = '\t';
        else break;
    }
    free(result);
    return NULL;
}

uint8_t *overlay_read(const char *self, size_t *wasm_length, char **entry, bool *damaged) {
    *damaged = false;
    size_t length = 0; uint64_t size = 0;
    uint8_t *footer = platform_read_tail(self, OVERLAY_FOOTER, &length, &size);
    if (!footer || length < OVERLAY_FOOTER || memcmp(footer + 48, overlay_magic, 16) != 0) { free(footer); return NULL; }
    *damaged = true;
    int32_t version, metadata_length; int64_t wasm_size;
    memcpy(&version, footer, 4); memcpy(&metadata_length, footer + 4, 4); memcpy(&wasm_size, footer + 8, 8);
    free(footer);
    if (version != 2 || metadata_length <= 0 || metadata_length > 65536 || wasm_size <= 0 || wasm_size > 272ll * 1024 * 1024 ||
        (uint64_t)wasm_size + (uint64_t)metadata_length + OVERLAY_FOOTER > size) return NULL;
    size_t overlay = (size_t)wasm_size + (size_t)metadata_length + OVERLAY_FOOTER;
    uint8_t *wasm = platform_read_tail(self, overlay, &length, &size);
    if (!wasm || length != overlay) { free(wasm); return NULL; }
    footer = wasm + overlay - OVERLAY_FOOTER;
    uint8_t digest[32];
    sha256(wasm, (size_t)(wasm_size + metadata_length), digest);
    if (memcmp(digest, footer + 16, 32) != 0) { free(wasm); return NULL; }
    /* Metadata: {"EntryPoint":"path.aqua"} */
    *entry = overlay_entry((const char *)(wasm + wasm_size), (size_t)metadata_length);
    if (!*entry) { free(wasm); return NULL; }
    *damaged = false;
    *wasm_length = (size_t)wasm_size; /* the metadata and footer after it are not part of the module */
    return wasm;
}

