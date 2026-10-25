/* PNG: decoded with libspng 0.7.4 (every color type and bit depth, interlacing, tRNS, critical chunk CRCs checked);
 * written by the old RGBA writer: 8-bit RGBA, filter 0, one zlib IDAT, pHYs when a resolution is set and eXIf when the
 * orientation is not 1. */
#include "codecs.h"
#include <spng.h>
#include <stdlib.h>
#include <string.h>

static bool codec_failure(app_error *e, int code) {
    return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", spng_strerror(code));
}

/* Opens the PNG and reads its header within the limits. */
static spng_ctx *open_png(const uint8_t *data, size_t length, struct spng_ihdr *ihdr, app_error *e) {
    spng_ctx *ctx = spng_ctx_new(0);
    if (!ctx) { app_error_set(e, APP_UNAVAILABLE, "Image codec failed: out of memory."); return NULL; }
    spng_set_chunk_limits(ctx, APP_MAX_BYTES, APP_MAX_BYTES);
    spng_set_png_buffer(ctx, data, length);
    int code = spng_get_ihdr(ctx, ihdr);
    if (code) { spng_ctx_free(ctx); codec_failure(e, code); return NULL; }
    if (!app_check_pixels(ihdr->width, ihdr->height, 1, e)) { spng_ctx_free(ctx); return NULL; }
    return ctx;
}

static void describe(spng_ctx *ctx, const struct spng_ihdr *ihdr, app_metadata *m, bool palette) {
    struct spng_trns trns;
    bool has_trns = spng_get_trns(ctx, &trns) == 0;
    const char *type = "TrueColorAlpha";
    switch (ihdr->color_type) {
    case SPNG_COLOR_TYPE_GRAYSCALE: type = has_trns ? "GrayscaleAlpha" : ihdr->bit_depth == 1 ? "Bilevel" : "Grayscale"; break;
    case SPNG_COLOR_TYPE_TRUECOLOR: type = has_trns ? "TrueColorAlpha" : "TrueColor"; break;
    case SPNG_COLOR_TYPE_INDEXED: type = has_trns ? "PaletteAlpha" : "Palette"; break;
    case SPNG_COLOR_TYPE_GRAYSCALE_ALPHA: type = "GrayscaleAlpha"; break;
    default: break;
    }
    app_source_format(m, type, ihdr->color_type == SPNG_COLOR_TYPE_INDEXED ? 8 : ihdr->bit_depth);
    struct spng_phys phys;
    if (spng_get_phys(ctx, &phys) == 0 && phys.unit_specifier == 1 && phys.ppu_x && phys.ppu_y) {
        m->dpi_x = phys.ppu_x * 0.0254;
        m->dpi_y = phys.ppu_y * 0.0254;
    }
    struct spng_exif exif;
    if (spng_get_exif(ctx, &exif) == 0) m->orientation = app_exif_orientation((const uint8_t *)exif.data, exif.length);
    struct spng_plte plte;
    if (palette && ihdr->color_type == SPNG_COLOR_TYPE_INDEXED && spng_get_plte(ctx, &plte) == 0 && plte.n_entries) {
        m->palette = malloc(plte.n_entries * sizeof *m->palette);
        m->palette_count = plte.n_entries;
        for (uint32_t i = 0; i < plte.n_entries; i++) {
            uint32_t alpha = has_trns && i < trns.n_type3_entries ? trns.type3_alpha[i] : 255;
            m->palette[i] = (uint32_t)plte.entries[i].red << 24 | (uint32_t)plte.entries[i].green << 16 | (uint32_t)plte.entries[i].blue << 8 | alpha;
        }
    }
}

bool app_png_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    struct spng_ihdr ihdr;
    spng_ctx *ctx = open_png(data, length, &ihdr, e);
    if (!ctx) return false;
    info->width = (int32_t)ihdr.width;
    info->height = (int32_t)ihdr.height;
    info->frames = 1;
    describe(ctx, &ihdr, &info->metadata, false);
    spng_ctx_free(ctx);
    return true;
}

bool app_png_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    struct spng_ihdr ihdr;
    spng_ctx *ctx = open_png(data, length, &ihdr, e);
    if (!ctx) return false;
    if (app_frame_out_of_range(frame, 1, e)) { spng_ctx_free(ctx); return false; }
    size_t size;
    int code = spng_decoded_image_size(ctx, SPNG_FMT_RGBA8, &size);
    if (!code) {
        out->rgba = malloc(size);
        code = out->rgba ? spng_decode_image(ctx, out->rgba, size, SPNG_FMT_RGBA8, SPNG_DECODE_TRNS) : SPNG_EMEM;
    }
    if (code) { free(out->rgba); out->rgba = NULL; spng_ctx_free(ctx); return codec_failure(e, code); }
    out->width = (int32_t)ihdr.width;
    out->height = (int32_t)ihdr.height;
    describe(ctx, &ihdr, &out->metadata, true);
    spng_ctx_free(ctx);
    return true;
}

static void chunk(app_bytes *out, const char *type, const uint8_t *data, size_t length) {
    uint8_t header[8];
    app_put32be(header, (uint32_t)length);
    memcpy(header + 4, type, 4);
    app_bytes_append(out, header, 8);
    app_bytes_append(out, data, length);
    uint32_t crc = app_crc32(app_crc32(0, (const uint8_t *)type, 4), data, length);
    uint8_t tail[4];
    app_put32be(tail, crc);
    app_bytes_append(out, tail, 4);
}

/* The old writer's level mapping: 0 none, 1..2 fastest, 9 smallest, otherwise zlib's default. */
static int zlib_level(int compression) { return compression == 0 ? 0 : compression <= 2 ? 1 : compression == 9 ? 9 : 6; }

bool app_png_encode(const app_picture *p, const uint8_t *rgba, const app_encode_options *o, app_bytes *out, app_error *e) {
    static const uint8_t signature[8] = {137, 80, 78, 71, 13, 10, 26, 10};
    app_bytes_append(out, signature, 8);
    uint8_t header[13] = {0};
    app_put32be(header, (uint32_t)p->width);
    app_put32be(header + 4, (uint32_t)p->height);
    header[8] = 8;
    header[9] = 6;
    chunk(out, "IHDR", header, 13);
    if (p->metadata.dpi_x > 0) {
        uint8_t phys[9];
        app_put32be(phys, (uint32_t)(p->metadata.dpi_x / 0.0254 + 0.5));
        app_put32be(phys + 4, (uint32_t)((p->metadata.dpi_y > 0 ? p->metadata.dpi_y : p->metadata.dpi_x) / 0.0254 + 0.5));
        phys[8] = 1;
        chunk(out, "pHYs", phys, 9);
    }
    if (p->metadata.orientation != 1) {
        app_bytes exif = {0};
        app_exif_block(&exif, p->metadata.orientation, false);
        chunk(out, "eXIf", exif.data, exif.length);
        app_bytes_free(&exif);
    }
    size_t row = (size_t)p->width * 4;
    app_bytes raw = {0}, packed;
    if (!app_bytes_reserve(&raw, (row + 1) * (size_t)p->height)) return app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory.");
    for (int32_t y = 0; y < p->height; y++) {
        app_bytes_byte(&raw, 0);
        app_bytes_append(&raw, rgba + (size_t)y * row, row);
    }
    bool ok = app_zlib(raw.data, raw.length, zlib_level(o->compression), &packed, e);
    app_bytes_free(&raw);
    if (!ok) return false;
    chunk(out, "IDAT", packed.data, packed.length);
    app_bytes_free(&packed);
    chunk(out, "IEND", NULL, 0);
    return true;
}
