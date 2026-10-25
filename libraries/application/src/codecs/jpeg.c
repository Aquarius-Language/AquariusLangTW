/* JPEG: decoded by stb_image 2.30 (baseline and progressive Huffman JPEG, grayscale, YCbCr and Adobe CMYK) and written
 * by stb_image_write 1.16 (baseline, the requested quality). Metadata comes from the markers: JFIF density, EXIF
 * orientation (and EXIF resolution without JFIF density), components. Written files carry the JFIF density when a
 * resolution is set and always an EXIF orientation, as the old encoder did. Both libraries compile here as static
 * functions, private to this file. */
#include "codecs.h"
#include <stdlib.h>
#include <string.h>

#define STBI_ONLY_JPEG
#define STBI_NO_STDIO
#define STBI_NO_HDR
#define STBI_NO_LINEAR
#if defined(AQUA_OS_WEB)
#define STBI_NO_THREAD_LOCALS
#endif
#define STBI_MAX_DIMENSIONS APP_MAX_DIMENSION
#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-function"
#pragma clang diagnostic ignored "-Wsign-compare"
#endif
#include "stb_image.h"
#define STBI_WRITE_NO_STDIO
#define STB_IMAGE_WRITE_STATIC
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

typedef struct markers { int components; bool cmyk_hint; int jfif_units; double jfif_x, jfif_y; app_metadata exif; bool has_exif_dpi; } markers;

static double exif_rational(const uint8_t *t, size_t n, bool little, uint32_t offset) {
    if ((uint64_t)offset + 8 > n) return 0;
    uint32_t a = little ? app_get32le(t + offset) : app_get32be(t + offset), b = little ? app_get32le(t + offset + 4) : app_get32be(t + offset + 4);
    return b ? (double)a / b : 0;
}

/* EXIF XResolution/YResolution/ResolutionUnit of IFD0. */
static void exif_resolution(const uint8_t *t, size_t n, app_metadata *m, bool *found) {
    if (n < 8) return;
    bool little = t[0] == 'I';
    uint32_t ifd = little ? app_get32le(t + 4) : app_get32be(t + 4);
    if ((uint64_t)ifd + 2 > n) return;
    uint32_t count = little ? app_get16le(t + ifd) : app_get16be(t + ifd), unit = 2;
    double x = 0, y = 0;
    for (uint32_t i = 0; i < count; i++) {
        uint64_t entry = (uint64_t)ifd + 2 + (uint64_t)i * 12;
        if (entry + 12 > n) return;
        uint32_t tag = little ? app_get16le(t + entry) : app_get16be(t + entry);
        uint32_t value = little ? app_get32le(t + entry + 8) : app_get32be(t + entry + 8);
        if (tag == 282) x = exif_rational(t, n, little, value);
        else if (tag == 283) y = exif_rational(t, n, little, value);
        else if (tag == 296) unit = little ? app_get16le(t + entry + 8) : app_get16be(t + entry + 8);
    }
    if (x > 0 && y > 0 && (unit == 2 || unit == 3)) {
        double factor = unit == 3 ? 2.54 : 1;
        m->dpi_x = x * factor;
        m->dpi_y = y * factor;
        *found = true;
    }
}

static void read_markers(const uint8_t *d, size_t n, markers *k) {
    memset(k, 0, sizeof *k);
    k->exif.orientation = 1;
    size_t p = 2;
    while (p + 4 <= n) {
        if (d[p] != 0xFF) break;
        uint8_t marker = d[p + 1];
        if (marker == 0xFF) { p++; continue; }
        if (marker == 0xD9 || marker == 0xDA) break;
        size_t length = app_get16be(d + p + 2);
        if (length < 2 || p + 2 + length > n) break;
        const uint8_t *s = d + p + 4;
        size_t size = length - 2;
        if (marker == 0xE0 && size >= 12 && memcmp(s, "JFIF\0", 5) == 0) {
            k->jfif_units = s[7];
            k->jfif_x = app_get16be(s + 8);
            k->jfif_y = app_get16be(s + 10);
        } else if (marker == 0xE1 && size > 6 && memcmp(s, "Exif\0\0", 6) == 0) {
            k->exif.orientation = app_exif_orientation(s + 6, size - 6);
            exif_resolution(s + 6, size - 6, &k->exif, &k->has_exif_dpi);
        } else if (marker == 0xEE && size >= 5 && memcmp(s, "Adobe", 5) == 0) {
            k->cmyk_hint = true;
        } else if ((marker >= 0xC0 && marker <= 0xCF) && marker != 0xC4 && marker != 0xC8 && marker != 0xCC && size >= 6) {
            k->components = s[5];
        }
        p += 2 + length;
    }
}

static void describe(const uint8_t *data, size_t length, app_metadata *m) {
    markers k;
    read_markers(data, length, &k);
    m->orientation = k.exif.orientation;
    if ((k.jfif_units == 1 || k.jfif_units == 2) && k.jfif_x > 0 && k.jfif_y > 0) {
        double factor = k.jfif_units == 2 ? 2.54 : 1;
        m->dpi_x = k.jfif_x * factor;
        m->dpi_y = k.jfif_y * factor;
    } else if (k.has_exif_dpi) {
        m->dpi_x = k.exif.dpi_x;
        m->dpi_y = k.exif.dpi_y;
    }
    app_source_format(m, k.components == 1 ? "Grayscale" : k.components == 4 ? "ColorSeparation" : "TrueColor", 8);
}

static bool header(const uint8_t *data, size_t length, int *width, int *height, app_error *e) {
    int components;
    if (length > 0x7FFFFFFF || !stbi_info_from_memory(data, (int)length, width, height, &components)) {
        const char *reason = stbi_failure_reason();
        if (reason && strcmp(reason, "too large") == 0) return app_error_set(e, APP_LIMIT_EXCEEDED, "Image dimensions or frame allocation exceeds the limit.");
        return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", reason ? reason : "corrupt JPEG");
    }
    return app_check_pixels(*width, *height, 1, e);
}

bool app_jpeg_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    if (!header(data, length, &info->width, &info->height, e)) return false;
    info->frames = 1;
    describe(data, length, &info->metadata);
    return true;
}

bool app_jpeg_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    int width, height, components;
    if (!header(data, length, &width, &height, e) || app_frame_out_of_range(frame, 1, e)) return false;
    stbi_uc *pixels = stbi_load_from_memory(data, (int)length, &width, &height, &components, 4);
    if (!pixels) {
        const char *reason = stbi_failure_reason();
        return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", reason ? reason : "corrupt JPEG");
    }
    size_t bytes = (size_t)width * (size_t)height * 4;
    out->rgba = malloc(bytes);
    memcpy(out->rgba, pixels, bytes);
    stbi_image_free(pixels);
    out->width = width;
    out->height = height;
    describe(data, length, &out->metadata);
    return true;
}

static void collect(void *context, void *data, int size) { app_bytes_append(context, data, (size_t)size); }

bool app_jpeg_encode(const app_picture *p, const uint8_t *rgba, const app_encode_options *o, app_bytes *out, app_error *e) {
    app_bytes body = {0};
    if (!stbi_write_jpg_to_func(collect, &body, p->width, p->height, 4, rgba, o->quality) || body.length < 20 ||
        memcmp(body.data + 6, "JFIF", 4) != 0) {
        app_bytes_free(&body);
        return app_error_set(e, APP_UNAVAILABLE, "Image codec failed: the JPEG encoder failed.");
    }
    /* The writer's JFIF segment (bytes 2..19): density in dots per inch when a resolution is set. */
    if (p->metadata.dpi_x > 0) {
        double y = p->metadata.dpi_y > 0 ? p->metadata.dpi_y : p->metadata.dpi_x;
        uint32_t dx = (uint32_t)(p->metadata.dpi_x + 0.5), dy = (uint32_t)(y + 0.5);
        body.data[13] = 1;
        body.data[14] = (uint8_t)(dx >> 8); body.data[15] = (uint8_t)dx;
        body.data[16] = (uint8_t)(dy >> 8); body.data[17] = (uint8_t)dy;
    }
    app_bytes_append(out, body.data, 20);
    app_bytes exif = {0};
    app_exif_block(&exif, p->metadata.orientation, true);
    uint8_t segment[4] = {0xFF, 0xE1};
    segment[2] = (uint8_t)((exif.length + 2) >> 8);
    segment[3] = (uint8_t)(exif.length + 2);
    app_bytes_append(out, segment, 4);
    app_bytes_append(out, exif.data, exif.length);
    app_bytes_free(&exif);
    app_bytes_append(out, body.data + 20, body.length - 20);
    app_bytes_free(&body);
    return true;
}
