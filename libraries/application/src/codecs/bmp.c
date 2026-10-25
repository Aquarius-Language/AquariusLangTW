/* BMP: Windows and OS/2 bitmaps (core, BITMAPINFOHEADER and V2..V5 headers), 1/4/8-bit indexed (also RLE4/RLE8),
 * 16/24/32-bit direct color with default or explicit bit-field masks, bottom-up or top-down rows. A 32-bit image whose
 * alpha channel is zero everywhere is opaque (as clipboard DIBs are). Written as 24-bit BI_RGB with the resolution in
 * pixels per meter, after images.c flattened any transparency onto the required background. */
#include "codecs.h"
#include <stdlib.h>
#include <string.h>

typedef struct bmp {
    int32_t width, height;
    bool top_down;
    uint32_t bits, compression, header, offset, colors;
    uint32_t masks[4];
    uint32_t palette[256];   /* RGBA packed R<<24|G<<16|B<<8|A */
    uint32_t ppm_x, ppm_y;
} bmp;

static bool invalid(app_error *e, const char *what) { return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", what); }

static bool parse(const uint8_t *d, size_t n, bmp *b, app_error *e) {
    memset(b, 0, sizeof *b);
    if (n < 26) return invalid(e, "truncated BMP header.");
    b->offset = app_get32le(d + 10);
    b->header = app_get32le(d + 14);
    if (b->header != 12 && b->header != 16 && b->header != 40 && b->header != 52 && b->header != 56 && b->header != 64 &&
        b->header != 108 && b->header != 124) return app_error_set(e, APP_UNSUPPORTED, "Image codec failed: unsupported BMP header size %u.", b->header);
    if (14 + (size_t)b->header > n) return invalid(e, "truncated BMP header.");
    const uint8_t *h = d + 14;
    int64_t height;
    if (b->header == 12) {
        b->width = (int32_t)app_get16le(h + 4);
        height = (int16_t)app_get16le(h + 6);
        b->bits = app_get16le(h + 10);
    } else {
        b->width = (int32_t)app_get32le(h + 4);
        height = (int32_t)app_get32le(h + 8);
        b->bits = app_get16le(h + 14);
        if (b->header >= 40) {
            b->compression = app_get32le(h + 16);
            b->ppm_x = app_get32le(h + 24);
            b->ppm_y = app_get32le(h + 28);
            b->colors = app_get32le(h + 32);
        }
    }
    b->top_down = height < 0;
    if (height < 0) height = -height;
    if (b->width <= 0 || height <= 0 || height > 0x7FFFFFFF) return invalid(e, "invalid BMP dimensions.");
    b->height = (int32_t)height;
    if (!app_check_pixels(b->width, b->height, 1, e)) return false;
    if (b->bits != 1 && b->bits != 4 && b->bits != 8 && b->bits != 16 && b->bits != 24 && b->bits != 32)
        return app_error_set(e, APP_UNSUPPORTED, "Image codec failed: unsupported BMP bit depth %u.", b->bits);
    if (b->compression != 0 && b->compression != 1 && b->compression != 2 && b->compression != 3 && b->compression != 6)
        return app_error_set(e, APP_UNSUPPORTED, "Image codec failed: unsupported BMP compression %u.", b->compression);
    if ((b->compression == 1 && b->bits != 8) || (b->compression == 2 && b->bits != 4) ||
        ((b->compression == 3 || b->compression == 6) && b->bits != 16 && b->bits != 32)) return invalid(e, "inconsistent BMP compression.");
    if (b->top_down && (b->compression == 1 || b->compression == 2)) return invalid(e, "top-down RLE bitmaps are not allowed.");
    size_t masks_at = 14 + 40;
    if (b->compression == 3 || b->compression == 6) {
        if (b->header >= 52) { masks_at = 14 + 40; }
        else if (masks_at + (b->compression == 6 ? 16 : 12) > n) return invalid(e, "truncated BMP bit fields.");
        b->masks[0] = app_get32le(d + masks_at);
        b->masks[1] = app_get32le(d + masks_at + 4);
        b->masks[2] = app_get32le(d + masks_at + 8);
        b->masks[3] = (b->header >= 56 || b->compression == 6) ? app_get32le(d + masks_at + 12) : 0;
    } else if (b->bits == 16) {
        b->masks[0] = 0x7C00; b->masks[1] = 0x03E0; b->masks[2] = 0x001F;
    } else if (b->bits == 32) {
        b->masks[0] = 0x00FF0000; b->masks[1] = 0x0000FF00; b->masks[2] = 0x000000FF; b->masks[3] = 0xFF000000;
    }
    if (b->header >= 56 && b->compression != 3 && b->compression != 6 && b->bits == 32 && app_get32le(h + 52)) b->masks[3] = app_get32le(h + 52);
    if (b->bits <= 8) {
        uint32_t count = b->colors ? b->colors : 1u << b->bits;
        if (count > 256) return invalid(e, "invalid BMP palette size.");
        size_t entry = b->header == 12 ? 3 : 4;
        size_t at = 14 + b->header + ((b->compression == 3) && b->header == 40 ? 12 : b->compression == 6 && b->header == 40 ? 16 : 0);
        if (at + count * entry > n) return invalid(e, "truncated BMP palette.");
        for (uint32_t i = 0; i < count; i++) {
            const uint8_t *c = d + at + i * entry;
            b->palette[i] = (uint32_t)c[2] << 24 | (uint32_t)c[1] << 16 | (uint32_t)c[0] << 8 | 255;
        }
        b->colors = count;
    }
    if (b->offset >= n) return invalid(e, "BMP pixel data is missing.");
    return true;
}

static void describe(const bmp *b, app_metadata *m, bool palette, bool alpha) {
    if (b->bits <= 8) app_source_format(m, "Palette", (int)b->bits);
    else app_source_format(m, alpha ? "TrueColorAlpha" : "TrueColor", 8);
    if (b->ppm_x && b->ppm_y) { m->dpi_x = b->ppm_x * 0.0254; m->dpi_y = b->ppm_y * 0.0254; }
    if (palette && b->bits <= 8) {
        m->palette = malloc(b->colors * sizeof *m->palette);
        memcpy(m->palette, b->palette, b->colors * sizeof *m->palette);
        m->palette_count = b->colors;
    }
}

bool app_bmp_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    bmp b;
    if (!parse(data, length, &b, e)) return false;
    info->width = b.width;
    info->height = b.height;
    info->frames = 1;
    describe(&b, &info->metadata, false, b.bits == 32 && b.masks[3]);
    return true;
}

static uint8_t channel(uint32_t value, uint32_t mask) {
    if (!mask) return 0;
    int shift = __builtin_ctz(mask);
    uint32_t bits = mask >> shift, v = (value & mask) >> shift;
    return (uint8_t)(bits == 255 ? v : (v * 255 + bits / 2) / bits);
}

static void put(uint8_t *rgba, uint32_t color) {
    rgba[0] = (uint8_t)(color >> 24); rgba[1] = (uint8_t)(color >> 16); rgba[2] = (uint8_t)(color >> 8); rgba[3] = (uint8_t)color;
}

/* RLE8 / RLE4 into palette indices; skipped pixels keep index 0. */
static bool decode_rle(const bmp *b, const uint8_t *s, size_t n, uint8_t *indices, app_error *e) {
    int32_t x = 0, y = 0;
    size_t p = 0;
    while (p + 2 <= n) {
        uint8_t count = s[p], value = s[p + 1];
        p += 2;
        if (count) {
            for (int i = 0; i < count && x < b->width; i++, x++) {
                uint8_t index = b->bits == 8 ? value : (i % 2 ? value & 15 : value >> 4);
                if (y < b->height) indices[(size_t)y * b->width + x] = index;
            }
            continue;
        }
        if (value == 0) { x = 0; y++; continue; }
        if (value == 1) return true;
        if (value == 2) {
            if (p + 2 > n) break;
            x += s[p]; y += s[p + 1]; p += 2;
            continue;
        }
        size_t bytes = b->bits == 8 ? value : (size_t)(value + 1) / 2;
        if (p + bytes > n) break;
        for (int i = 0; i < value; i++, x++) {
            uint8_t index = b->bits == 8 ? s[p + i] : (i % 2 ? s[p + i / 2] & 15 : s[p + i / 2] >> 4);
            if (x < b->width && y < b->height) indices[(size_t)y * b->width + x] = index;
        }
        p += bytes + (bytes & 1);
    }
    if (y >= b->height) return true;
    return invalid(e, "truncated BMP RLE data.");
}

bool app_bmp_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    bmp b;
    if (!parse(data, length, &b, e) || app_frame_out_of_range(frame, 1, e)) return false;
    size_t w = (size_t)b.width, h = (size_t)b.height;
    uint8_t *rgba = calloc(w * h, 4);
    const uint8_t *s = data + b.offset;
    size_t available = length - b.offset;
    bool alpha_used = false;
    if (b.compression == 1 || b.compression == 2) {
        uint8_t *indices = calloc(w * h, 1);
        if (!decode_rle(&b, s, available, indices, e)) { free(indices); free(rgba); return false; }
        /* RLE rows are bottom-up */
        for (size_t y = 0; y < h; y++)
            for (size_t x = 0; x < w; x++) {
                uint8_t index = indices[y * w + x];
                put(rgba + ((h - 1 - y) * w + x) * 4, index < b.colors ? b.palette[index] : b.palette[0]);
            }
        free(indices);
    } else {
        size_t stride = ((size_t)b.bits * w + 31) / 32 * 4;
        if (stride * h > available) { free(rgba); return invalid(e, "truncated BMP pixel data."); }
        for (size_t row = 0; row < h; row++) {
            const uint8_t *src = s + row * stride;
            uint8_t *dst = rgba + (b.top_down ? row : h - 1 - row) * w * 4;
            for (size_t x = 0; x < w; x++, dst += 4) {
                if (b.bits <= 8) {
                    size_t bit = x * b.bits;
                    uint32_t index = (src[bit / 8] >> (8 - b.bits - bit % 8)) & ((1u << b.bits) - 1);
                    put(dst, index < b.colors ? b.palette[index] : 0xFF);
                } else if (b.bits == 24) {
                    dst[0] = src[x * 3 + 2]; dst[1] = src[x * 3 + 1]; dst[2] = src[x * 3]; dst[3] = 255;
                } else {
                    uint32_t value = b.bits == 16 ? app_get16le(src + x * 2) : app_get32le(src + x * 4);
                    dst[0] = channel(value, b.masks[0]); dst[1] = channel(value, b.masks[1]); dst[2] = channel(value, b.masks[2]);
                    dst[3] = b.masks[3] ? channel(value, b.masks[3]) : 255;
                    if (b.masks[3] && dst[3]) alpha_used = true;
                }
            }
        }
        if (b.masks[3] && !alpha_used) for (size_t i = 3; i < w * h * 4; i += 4) rgba[i] = 255;
    }
    out->width = b.width;
    out->height = b.height;
    out->rgba = rgba;
    describe(&b, &out->metadata, true, alpha_used);
    return true;
}

bool app_bmp_encode(const app_picture *p, const uint8_t *rgba, const app_encode_options *o, app_bytes *out, app_error *e) {
    (void)o; (void)e;
    size_t w = (size_t)p->width, h = (size_t)p->height, stride = (w * 3 + 3) / 4 * 4;
    uint32_t image = (uint32_t)(stride * h);
    uint8_t header[54] = {'B', 'M'};
    app_put32le(header + 2, 54 + image);
    app_put32le(header + 10, 54);
    app_put32le(header + 14, 40);
    app_put32le(header + 18, (uint32_t)p->width);
    app_put32le(header + 22, (uint32_t)p->height);
    app_put16le(header + 26, 1);
    app_put16le(header + 28, 24);
    app_put32le(header + 34, image);
    if (p->metadata.dpi_x > 0) {
        app_put32le(header + 38, (uint32_t)(p->metadata.dpi_x / 0.0254 + 0.5));
        app_put32le(header + 42, (uint32_t)((p->metadata.dpi_y > 0 ? p->metadata.dpi_y : p->metadata.dpi_x) / 0.0254 + 0.5));
    }
    if (!app_bytes_reserve(out, 54 + image)) return app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory.");
    app_bytes_append(out, header, 54);
    uint8_t *row = calloc(stride, 1);
    for (size_t y = h; y-- > 0;) {
        const uint8_t *src = rgba + y * w * 4;
        for (size_t x = 0; x < w; x++) { row[x * 3] = src[x * 4 + 2]; row[x * 3 + 1] = src[x * 4 + 1]; row[x * 3 + 2] = src[x * 4]; }
        app_bytes_append(out, row, stride);
    }
    free(row);
    return true;
}
