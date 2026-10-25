/* Strict text encodings for Files.ReadText/SaveText and friends: utf-8, utf-16le, utf-16be and ascii. Encoding never
 * writes a byte order mark; decoding keeps one (as U+FEFF). Unpaired surrogates and invalid byte sequences fail. */
#include "application.h"
#include <string.h>

bool app_encoding_of(const char *name, app_encoding *out, app_error *e) {
    static const char *const names[] = {"utf-8", "utf-16le", "utf-16be", "ascii"};
    for (int i = 0; i < 4; i++) if (app_equals_ignore_case(name, names[i])) { *out = (app_encoding)i; return true; }
    return app_error_set(e, APP_UNSUPPORTED, "Encoding must be utf-8, utf-16le, utf-16be or ascii.");
}

uint16_t *app_text16(aqua_runtime *rt, aqua_value text, size_t *count) {
    if (text.type != AQUA_STRING) { *count = 0; return NULL; }
    size_t n = aqua_text16_length(rt, text);
    uint16_t *copy = aqua_alloc((n ? n : 1) * sizeof *copy);
    if (n) memcpy(copy, aqua_text16_units(rt, text), n * sizeof *copy);
    *count = n;
    return copy;
}

bool app_utf8_valid(const uint8_t *p, size_t n) {
    size_t i = 0;
    while (i < n) {
        uint8_t c = p[i];
        if (c < 0x80) { i++; continue; }
        int length = c >= 0xC2 && c <= 0xDF ? 2 : c >= 0xE0 && c <= 0xEF ? 3 : c >= 0xF0 && c <= 0xF4 ? 4 : 0;
        if (!length || n - i < (size_t)length) return false;
        for (int k = 1; k < length; k++) if ((p[i + k] & 0xC0) != 0x80) return false;
        if (c == 0xE0 && p[i + 1] < 0xA0) return false;   /* overlong */
        if (c == 0xED && p[i + 1] >= 0xA0) return false;  /* surrogate */
        if (c == 0xF0 && p[i + 1] < 0x90) return false;   /* overlong */
        if (c == 0xF4 && p[i + 1] >= 0x90) return false;  /* beyond U+10FFFF */
        i += (size_t)length;
    }
    return true;
}

bool app_text_encode(aqua_runtime *rt, aqua_value text, app_encoding encoding, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    size_t count;
    uint16_t *units = app_text16(rt, text, &count);
    if (!units) return app_error_set(e, APP_INVALID_DATA, "Expected a string.");
    bool ok = true;
    for (size_t i = 0; i < count && ok; i++) {
        uint32_t c = units[i];
        if (c >= 0xD800 && c <= 0xDBFF) {
            if (i + 1 >= count || units[i + 1] < 0xDC00 || units[i + 1] > 0xDFFF) { ok = false; break; }
            uint32_t scalar = 0x10000 + ((c - 0xD800) << 10) + (units[i + 1] - 0xDC00);
            if (encoding == APP_ASCII) { ok = false; break; }
            if (encoding == APP_UTF8) {
                uint8_t b[4] = {(uint8_t)(0xF0 | scalar >> 18), (uint8_t)(0x80 | (scalar >> 12 & 63)), (uint8_t)(0x80 | (scalar >> 6 & 63)), (uint8_t)(0x80 | (scalar & 63))};
                app_bytes_append(out, b, 4);
            } else {
                for (int k = 0; k < 2; k++) {
                    uint32_t u = units[i + k];
                    uint8_t b[2] = {(uint8_t)(encoding == APP_UTF16LE ? u : u >> 8), (uint8_t)(encoding == APP_UTF16LE ? u >> 8 : u)};
                    app_bytes_append(out, b, 2);
                }
            }
            i++;
            continue;
        }
        if (c >= 0xDC00 && c <= 0xDFFF) { ok = false; break; }
        switch (encoding) {
        case APP_ASCII:
            if (c > 127) ok = false;
            else app_bytes_byte(out, (uint8_t)c);
            break;
        case APP_UTF8:
            if (c < 0x80) app_bytes_byte(out, (uint8_t)c);
            else if (c < 0x800) { uint8_t b[2] = {(uint8_t)(0xC0 | c >> 6), (uint8_t)(0x80 | (c & 63))}; app_bytes_append(out, b, 2); }
            else { uint8_t b[3] = {(uint8_t)(0xE0 | c >> 12), (uint8_t)(0x80 | (c >> 6 & 63)), (uint8_t)(0x80 | (c & 63))}; app_bytes_append(out, b, 3); }
            break;
        default: {
            uint8_t b[2] = {(uint8_t)(encoding == APP_UTF16LE ? c : c >> 8), (uint8_t)(encoding == APP_UTF16LE ? c >> 8 : c)};
            app_bytes_append(out, b, 2);
        }
        }
        if (out->length > APP_MAX_BYTES) break;
    }
    aqua_free(units);
    if (!ok) {
        app_bytes_free(out);
        return app_error_set(e, APP_INVALID_DATA, encoding == APP_ASCII ? "The text contains characters outside ASCII."
                                                                         : "The text contains an unpaired surrogate.");
    }
    if (!app_check_bytes(out->length, e)) { app_bytes_free(out); return false; }
    return true;
}

bool app_text_decode(aqua_runtime *rt, const uint8_t *data, size_t length, app_encoding encoding, aqua_value *out, app_error *e) {
    switch (encoding) {
    case APP_ASCII:
        for (size_t i = 0; i < length; i++)
            if (data[i] > 127) return app_error_set(e, APP_INVALID_DATA, "Unable to translate byte 0x%02X at index %zu to Unicode.", data[i], i);
        *out = aqua_text(rt, (const char *)data, length);
        return true;
    case APP_UTF8:
        if (!app_utf8_valid(data, length)) return app_error_set(e, APP_INVALID_DATA, "The bytes are not valid UTF-8.");
        *out = aqua_text(rt, (const char *)data, length);
        return true;
    default: {
        if (length % 2) return app_error_set(e, APP_INVALID_DATA, "The bytes are not valid UTF-16 (odd length).");
        size_t count = length / 2;
        uint16_t *units = aqua_alloc((count ? count : 1) * sizeof *units);
        for (size_t i = 0; i < count; i++)
            units[i] = (uint16_t)(encoding == APP_UTF16LE ? data[2 * i] | data[2 * i + 1] << 8 : data[2 * i] << 8 | data[2 * i + 1]);
        for (size_t i = 0; i < count; i++) {
            if (units[i] >= 0xD800 && units[i] <= 0xDBFF && i + 1 < count && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) { i++; continue; }
            if (units[i] >= 0xD800 && units[i] <= 0xDFFF) {
                aqua_free(units);
                return app_error_set(e, APP_INVALID_DATA, "The bytes are not valid UTF-16 (unpaired surrogate).");
            }
        }
        *out = aqua_text16(rt, units, count);
        aqua_free(units);
        return true;
    }
    }
}
