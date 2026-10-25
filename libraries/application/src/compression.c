/* Gzip, zlib, raw Deflate (zlib 1.3.2) and Brotli (1.2.1) streams with the documented validation and limits: one gzip
 * member, checked gzip/zlib headers, checksums and lengths, and expansion stopped at the byte limit. */
#include "application.h"
#include <brotli/decode.h>
#include <brotli/encode.h>
#include <stdlib.h>
#include <string.h>
#include <zlib.h>

bool app_compression_of(const char *name, app_compression *out, app_error *e) {
    static const char *const names[] = {"Gzip", "Zlib", "Deflate", "Brotli"};
    for (int i = 0; i < 4; i++) if (app_equals_ignore_case(name, names[i])) { *out = (app_compression)i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Requested value '%s' was not found.", name);
}

bool app_level_of(const char *name, app_level *out, app_error *e) {
    static const char *const names[] = {"Optimal", "Fastest", "SmallestSize", "NoCompression"};
    for (int i = 0; i < 4; i++) if (app_equals_ignore_case(name, names[i])) { *out = (app_level)i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Requested value '%s' was not found.", name);
}

/* zlib's deflate for one container: window bits 15 + 16 (gzip), 15 (zlib) or -15 (raw). */
static bool deflate_stream(const uint8_t *data, size_t length, int window, int level, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    z_stream z;
    memset(&z, 0, sizeof z);
    if (deflateInit2(&z, level, Z_DEFLATED, window, 8, Z_DEFAULT_STRATEGY) != Z_OK)
        return app_error_set(e, APP_UNAVAILABLE, "The compressor could not start.");
    uLong bound = deflateBound(&z, (uLong)length);
    if (!app_bytes_reserve(out, (size_t)bound + 16)) { deflateEnd(&z); return app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory."); }
    z.next_in = (Bytef *)data;
    z.avail_in = (uInt)length;
    z.next_out = out->data;
    z.avail_out = (uInt)out->capacity;
    int status = deflate(&z, Z_FINISH);
    out->length = z.total_out;
    deflateEnd(&z);
    if (status != Z_STREAM_END) { app_bytes_free(out); return app_error_set(e, APP_UNAVAILABLE, "The compressor failed."); }
    return app_check_bytes(out->length, e) || (app_bytes_free(out), false);
}

bool app_zlib(const uint8_t *data, size_t length, int level, app_bytes *out, app_error *e) {
    return deflate_stream(data, length, 15, level, out, e);
}

static bool invalid_container(app_error *e) {
    return app_error_set(e, APP_INVALID_DATA, "Invalid compressed container checksum, header or length (one gzip member is supported).");
}

static bool inflate_stream(const uint8_t *data, size_t length, int window, size_t limit, bool trailing_allowed, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    z_stream z;
    memset(&z, 0, sizeof z);
    if (inflateInit2(&z, window) != Z_OK) return app_error_set(e, APP_UNAVAILABLE, "The decompressor could not start.");
    z.next_in = (Bytef *)data;
    z.avail_in = (uInt)length;
    int status = Z_OK;
    while (status != Z_STREAM_END) {
        if (out->length == out->capacity) {
            if (out->length > limit) break;
            size_t next = out->capacity ? out->capacity * 2 : (length * 4 > 1024 ? length * 4 : 1024);
            if (next > limit + 1) next = limit + 1;
            if (next <= out->capacity || !app_bytes_reserve(out, next)) break;
        }
        z.next_out = out->data + out->length;
        z.avail_out = (uInt)(out->capacity - out->length);
        status = inflate(&z, Z_NO_FLUSH);
        out->length = (size_t)(z.next_out - out->data);
        if (status == Z_BUF_ERROR && z.avail_in == 0) break;            /* truncated */
        if (status != Z_OK && status != Z_STREAM_END && status != Z_BUF_ERROR) break;
    }
    bool trailing = z.avail_in != 0;
    inflateEnd(&z);
    if (out->length > limit) { app_bytes_free(out); return app_error_set(e, APP_LIMIT_EXCEEDED, "Stream exceeds the byte limit."); }
    if (status != Z_STREAM_END) {
        app_bytes_free(out);
        if (status == Z_DATA_ERROR && window != -15) return invalid_container(e);
        return app_error_set(e, APP_INVALID_DATA, status == Z_DATA_ERROR || status == Z_NEED_DICT ? "Found invalid data while decoding." : "Unexpected end of compressed data.");
    }
    if (trailing && !trailing_allowed) { app_bytes_free(out); return invalid_container(e); }
    return true;
}

bool app_compress(const uint8_t *data, size_t length, app_compression format, app_level level, app_bytes *out, app_error *e) {
    if (!app_check_bytes(length, e)) return false;
    if (format == APP_BROTLI) {
        static const int qualities[] = {4, 1, 11, 0};
        size_t size = BrotliEncoderMaxCompressedSize(length);
        if (!size) size = length + 1024;
        memset(out, 0, sizeof *out);
        if (!app_bytes_reserve(out, size)) return app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory.");
        size_t written = out->capacity;
        if (!BrotliEncoderCompress(qualities[level], 22, BROTLI_MODE_GENERIC, length, data, &written, out->data)) {
            app_bytes_free(out);
            return app_error_set(e, APP_UNAVAILABLE, "The compressor failed.");
        }
        out->length = written;
        return app_check_bytes(out->length, e) || (app_bytes_free(out), false);
    }
    static const int levels[] = {6, 1, 9, 0};
    static const int windows[] = {15 + 16, 15, -15};
    return deflate_stream(data, length, windows[format], levels[level], out, e);
}

bool app_decompress(const uint8_t *data, size_t length, app_compression format, size_t limit, app_bytes *out, app_error *e) {
    if (!app_check_bytes(length, e)) return false;
    switch (format) {
    case APP_GZIP:
        if (length < 18 || data[0] != 31 || data[1] != 139 || data[2] != 8 || (data[3] & 0xE0) != 0) return invalid_container(e);
        if (!inflate_stream(data, length, 15 + 16, limit, false, out, e)) return false;
        if (app_get32le(data + length - 8) != app_crc32(0, out->data, out->length) || app_get32le(data + length - 4) != (uint32_t)out->length) {
            app_bytes_free(out);
            return invalid_container(e);
        }
        return true;
    case APP_ZLIB:
        if (length < 6 || (data[0] & 15) != 8 || data[0] >> 4 > 7 || ((data[0] << 8) | data[1]) % 31 != 0 || (data[1] & 32) != 0)
            return invalid_container(e);
        return inflate_stream(data, length, 15, limit, false, out, e);
    case APP_DEFLATE:
        return inflate_stream(data, length, -15, limit, true, out, e);
    case APP_BROTLI: {
        memset(out, 0, sizeof *out);
        BrotliDecoderState *state = BrotliDecoderCreateInstance(NULL, NULL, NULL);
        if (!state) return app_error_set(e, APP_UNAVAILABLE, "The decompressor could not start.");
        size_t available_in = length;
        const uint8_t *next_in = data;
        BrotliDecoderResult result = BROTLI_DECODER_RESULT_NEEDS_MORE_OUTPUT;
        while (result == BROTLI_DECODER_RESULT_NEEDS_MORE_OUTPUT) {
            if (out->length == out->capacity) {
                if (out->length > limit) break;
                size_t next = out->capacity ? out->capacity * 2 : (length * 4 > 1024 ? length * 4 : 1024);
                if (next > limit + 1) next = limit + 1;
                if (next <= out->capacity || !app_bytes_reserve(out, next)) break;
            }
            size_t available_out = out->capacity - out->length;
            uint8_t *next_out = out->data + out->length;
            result = BrotliDecoderDecompressStream(state, &available_in, &next_in, &available_out, &next_out, NULL);
            out->length = (size_t)(next_out - out->data);
        }
        BrotliDecoderDestroyInstance(state);
        if (out->length > limit) { app_bytes_free(out); return app_error_set(e, APP_LIMIT_EXCEEDED, "Stream exceeds the byte limit."); }
        if (result != BROTLI_DECODER_RESULT_SUCCESS || available_in != 0) {
            app_bytes_free(out);
            return app_error_set(e, APP_INVALID_DATA, result == BROTLI_DECODER_RESULT_NEEDS_MORE_INPUT ? "Unexpected end of compressed data." : "Found invalid data while decoding.");
        }
        return true;
    }
    }
    return app_error_set(e, APP_INVALID_DATA, "Unknown compression format.");
}
