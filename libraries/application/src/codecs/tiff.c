/* TIFF: pages read with libtiff 4.7.2 from memory (uncompressed, PackBits, LZW, Deflate, CCITT, NeXT, ThunderScan,
 * LogLuv, PixarLog; strips and tiles; gray, palette, RGB, CMYK, YCbCr, CIE Lab; 1..16-bit samples). 8-bit chunky gray
 * and RGB pages keep their exact samples; others go through libtiff's RGBA conversion. Pixels stay in stored
 * orientation. BigTIFF is not identified (its signature differs). Written by the old baseline RGBA writer (one strip,
 * unassociated alpha, Deflate unless compression is 0, orientation and resolution tags). */
#include "codecs.h"
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <tiffio.h>

typedef struct memory { const uint8_t *data; size_t length; uint64_t position; char error[400]; } memory;

static tmsize_t read_proc(thandle_t h, void *buffer, tmsize_t size) {
    memory *m = (memory *)h;
    if (m->position >= m->length) return 0;
    uint64_t n = (uint64_t)size;
    if (n > m->length - m->position) n = m->length - m->position;
    memcpy(buffer, m->data + m->position, (size_t)n);
    m->position += n;
    return (tmsize_t)n;
}
static tmsize_t write_proc(thandle_t h, void *buffer, tmsize_t size) { (void)h; (void)buffer; (void)size; return -1; }
static toff_t seek_proc(thandle_t h, toff_t offset, int whence) {
    memory *m = (memory *)h;
    uint64_t base = whence == SEEK_CUR ? m->position : whence == SEEK_END ? m->length : 0;
    m->position = base + offset;
    return m->position;
}
static int close_proc(thandle_t h) { (void)h; return 0; }
static toff_t size_proc(thandle_t h) { return ((memory *)h)->length; }
static int map_proc(thandle_t h, void **base, toff_t *size) { memory *m = (memory *)h; *base = (void *)m->data; *size = m->length; return 1; }
static void unmap_proc(thandle_t h, void *base, toff_t size) { (void)h; (void)base; (void)size; }

static int on_error(TIFF *tif, void *data, const char *module, const char *format, va_list arguments) {
    (void)tif; (void)module;
    memory *m = data;
    if (!m->error[0]) vsnprintf(m->error, sizeof m->error, format, arguments);
    return 1;
}
static int on_warning(TIFF *tif, void *data, const char *module, const char *format, va_list arguments) {
    (void)tif; (void)data; (void)module; (void)format; (void)arguments;
    return 1;
}

static TIFF *open_tiff(memory *m, app_error *e) {
    TIFFOpenOptions *options = TIFFOpenOptionsAlloc();
    TIFFOpenOptionsSetErrorHandlerExtR(options, on_error, m);
    TIFFOpenOptionsSetWarningHandlerExtR(options, on_warning, m);
    TIFFOpenOptionsSetMaxSingleMemAlloc(options, APP_MAX_BYTES);
    TIFFOpenOptionsSetMaxCumulatedMemAlloc(options, (tmsize_t)APP_MAX_BYTES * 2);
    TIFF *tif = TIFFClientOpenExt("image", "rm", (thandle_t)m, read_proc, write_proc, seek_proc, close_proc, size_proc,
                                  map_proc, unmap_proc, options);
    TIFFOpenOptionsFree(options);
    if (!tif) app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", m->error[0] ? m->error : "invalid TIFF.");
    return tif;
}

static bool page_size(TIFF *tif, uint32_t *width, uint32_t *height, app_error *e) {
    if (!TIFFGetField(tif, TIFFTAG_IMAGEWIDTH, width) || !TIFFGetField(tif, TIFFTAG_IMAGELENGTH, height))
        return app_error_set(e, APP_INVALID_DATA, "Image codec failed: a page has no dimensions.");
    return app_check_pixels(*width, *height, 1, e);
}

/* Counts the pages within the limits, leaving the first one current. */
static int count_pages(TIFF *tif, uint32_t *first_width, uint32_t *first_height, app_error *e) {
    int pages = 0;
    int64_t pixels = 0;
    do {
        uint32_t width, height;
        if (pages >= APP_MAX_FRAMES) { app_error_set(e, APP_LIMIT_EXCEEDED, "Image frame count exceeds the limit."); return -1; }
        if (!page_size(tif, &width, &height, e)) return -1;
        if (pages == 0) { *first_width = width; *first_height = height; }
        pixels += (int64_t)width * height;
        if (pixels > APP_MAX_PIXELS) { app_error_set(e, APP_LIMIT_EXCEEDED, "Image frame allocation exceeds the limit."); return -1; }
        pages++;
    } while (TIFFReadDirectory(tif));
    TIFFSetDirectory(tif, 0);
    return pages;
}

static void describe(TIFF *tif, app_metadata *m, bool palette) {
    uint16_t orientation = 1, photometric = PHOTOMETRIC_MINISBLACK, bits = 1, samples = 1, extra = 0, unit = RESUNIT_INCH;
    uint16_t *extra_types = NULL;
    TIFFGetFieldDefaulted(tif, TIFFTAG_ORIENTATION, &orientation);
    TIFFGetField(tif, TIFFTAG_PHOTOMETRIC, &photometric);
    TIFFGetFieldDefaulted(tif, TIFFTAG_BITSPERSAMPLE, &bits);
    TIFFGetFieldDefaulted(tif, TIFFTAG_SAMPLESPERPIXEL, &samples);
    TIFFGetFieldDefaulted(tif, TIFFTAG_EXTRASAMPLES, &extra, &extra_types);
    m->orientation = orientation >= 1 && orientation <= 8 ? orientation : 1;
    float x = 0, y = 0;
    TIFFGetFieldDefaulted(tif, TIFFTAG_RESOLUTIONUNIT, &unit);
    if (unit != RESUNIT_NONE && TIFFGetField(tif, TIFFTAG_XRESOLUTION, &x) && TIFFGetField(tif, TIFFTAG_YRESOLUTION, &y) && x > 0 && y > 0) {
        double factor = unit == RESUNIT_CENTIMETER ? 2.54 : 1;
        m->dpi_x = x * factor;
        m->dpi_y = y * factor;
    }
    bool alpha = extra > 0;
    const char *type;
    switch (photometric) {
    case PHOTOMETRIC_MINISWHITE: case PHOTOMETRIC_MINISBLACK: type = alpha ? "GrayscaleAlpha" : bits == 1 ? "Bilevel" : "Grayscale"; break;
    case PHOTOMETRIC_PALETTE: type = alpha ? "PaletteAlpha" : "Palette"; break;
    case PHOTOMETRIC_SEPARATED: type = alpha ? "ColorSeparationAlpha" : "ColorSeparation"; break;
    default: type = alpha ? "TrueColorAlpha" : "TrueColor"; break;
    }
    app_source_format(m, type, bits);
    uint16_t *red, *green, *blue;
    if (palette && photometric == PHOTOMETRIC_PALETTE && bits <= 8 && TIFFGetField(tif, TIFFTAG_COLORMAP, &red, &green, &blue)) {
        m->palette_count = 1u << bits;
        m->palette = malloc(m->palette_count * sizeof *m->palette);
        for (uint32_t i = 0; i < m->palette_count; i++)
            m->palette[i] = (uint32_t)(red[i] / 257) << 24 | (uint32_t)(green[i] / 257) << 16 | (uint32_t)(blue[i] / 257) << 8 | 255;
    }
}

bool app_tiff_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    memory m = {data, length, 0, {0}};
    TIFF *tif = open_tiff(&m, e);
    if (!tif) return false;
    uint32_t width = 0, height = 0;
    int pages = count_pages(tif, &width, &height, e);
    if (pages > 0) {
        info->width = (int32_t)width;
        info->height = (int32_t)height;
        info->frames = pages;
        describe(tif, &info->metadata, false);
    }
    TIFFClose(tif);
    return pages > 0;
}

static void unpremultiply(uint8_t *rgba, size_t count) {
    for (size_t i = 0; i < count; i++, rgba += 4) {
        uint32_t a = rgba[3];
        for (int c = 0; c < 3; c++) {
            uint32_t v = a == 0 ? 0 : (rgba[c] * 255u + a / 2) / a;
            rgba[c] = (uint8_t)(v > 255 ? 255 : v);
        }
    }
}

/* 8-bit chunky gray or RGB strips, with an optional first extra sample as alpha: the samples exactly as stored. */
static bool read_direct(TIFF *tif, uint32_t width, uint32_t height, uint8_t *rgba, bool *handled, app_error *e) {
    uint16_t photometric = 0xFFFF, bits = 0, samples = 1, planar = PLANARCONFIG_CONTIG, extra = 0;
    uint16_t *extra_types = NULL;
    TIFFGetField(tif, TIFFTAG_PHOTOMETRIC, &photometric);
    TIFFGetFieldDefaulted(tif, TIFFTAG_BITSPERSAMPLE, &bits);
    TIFFGetFieldDefaulted(tif, TIFFTAG_SAMPLESPERPIXEL, &samples);
    TIFFGetFieldDefaulted(tif, TIFFTAG_PLANARCONFIG, &planar);
    TIFFGetFieldDefaulted(tif, TIFFTAG_EXTRASAMPLES, &extra, &extra_types);
    bool gray = photometric == PHOTOMETRIC_MINISBLACK || photometric == PHOTOMETRIC_MINISWHITE;
    uint16_t colors = gray ? 1 : 3;
    *handled = false;
    if (TIFFIsTiled(tif) || bits != 8 || planar != PLANARCONFIG_CONTIG || !(gray || photometric == PHOTOMETRIC_RGB) || samples != colors + extra) return true;
    bool has_alpha = samples > colors && extra > 0;
    bool associated = has_alpha && extra_types && extra_types[0] == EXTRASAMPLE_ASSOCALPHA;
    tmsize_t line_size = TIFFScanlineSize(tif);
    if (line_size < (tmsize_t)width * samples) return true;
    uint8_t *line = malloc((size_t)line_size);
    for (uint32_t y = 0; y < height; y++) {
        if (TIFFReadScanline(tif, line, y, 0) < 0) { free(line); return app_error_set(e, APP_INVALID_DATA, "Image codec failed: unreadable TIFF strip."); }
        uint8_t *out = rgba + (size_t)y * width * 4;
        for (uint32_t x = 0; x < width; x++, out += 4) {
            const uint8_t *s = line + (size_t)x * samples;
            if (gray) {
                uint8_t v = photometric == PHOTOMETRIC_MINISWHITE ? (uint8_t)(255 - s[0]) : s[0];
                out[0] = out[1] = out[2] = v;
            } else { out[0] = s[0]; out[1] = s[1]; out[2] = s[2]; }
            out[3] = has_alpha ? s[colors] : 255;
        }
    }
    free(line);
    if (associated) unpremultiply(rgba, (size_t)width * height);
    *handled = true;
    return true;
}

bool app_tiff_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    memory m = {data, length, 0, {0}};
    TIFF *tif = open_tiff(&m, e);
    if (!tif) return false;
    uint32_t width = 0, height = 0;
    int pages = count_pages(tif, &width, &height, e);
    if (pages <= 0 || app_frame_out_of_range(frame, pages, e) || !TIFFSetDirectory(tif, (tdir_t)frame) || !page_size(tif, &width, &height, e)) {
        if (pages > 0 && frame >= 0 && frame < pages && !e->failed) app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", m.error[0] ? m.error : "unreadable page.");
        TIFFClose(tif);
        return false;
    }
    uint8_t *rgba = malloc((size_t)width * height * 4);
    bool handled = false;
    if (!read_direct(tif, width, height, rgba, &handled, e)) { free(rgba); TIFFClose(tif); return false; }
    if (!handled) {
        char message[1024] = {0};
        TIFFRGBAImage image;
        if (!TIFFRGBAImageOK(tif, message) || !TIFFRGBAImageBegin(&image, tif, 1, message)) {
            free(rgba);
            TIFFClose(tif);
            return app_error_set(e, APP_UNSUPPORTED, "Image codec failed: %s", message[0] ? message : "unsupported TIFF variant.");
        }
        image.req_orientation = image.orientation; /* keep the stored row order */
        int ok = TIFFRGBAImageGet(&image, (uint32_t *)rgba, width, height);
        bool alpha = image.alpha != 0;
        TIFFRGBAImageEnd(&image);
        if (!ok) {
            free(rgba);
            TIFFClose(tif);
            return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", m.error[0] ? m.error : "unreadable TIFF page.");
        }
        /* libtiff's raster is associated alpha, packed A<<24|B<<16|G<<8|R: RGBA bytes on little-endian hosts. */
        if (alpha) unpremultiply(rgba, (size_t)width * height);
    }
    out->width = (int32_t)width;
    out->height = (int32_t)height;
    out->rgba = rgba;
    describe(tif, &out->metadata, true);
    TIFFClose(tif);
    return true;
}

static void entry(uint8_t *p, uint16_t tag, uint16_t type, uint32_t count, uint32_t value) {
    app_put16le(p, tag); app_put16le(p + 2, type); app_put32le(p + 4, count); app_put32le(p + 8, value);
}

bool app_tiff_encode(const app_picture *p, const uint8_t *rgba, const app_encode_options *o, app_bytes *out, app_error *e) {
    size_t bytes = (size_t)p->width * (size_t)p->height * 4;
    app_bytes packed = {0};
    const uint8_t *encoded = rgba;
    size_t encoded_length = bytes;
    if (o->compression != 0) {
        int level = o->compression <= 2 ? 1 : o->compression == 9 ? 9 : 6;
        if (!app_zlib(rgba, bytes, level, &packed, e)) return false;
        encoded = packed.data;
        encoded_length = packed.length;
    }
    enum { COUNT = 15, EXTRA = 8 + 2 + COUNT * 12 + 4, PIXELS = EXTRA + 24 };
    uint8_t head[PIXELS] = {'I', 'I', 42, 0, 8, 0, 0, 0};
    app_put16le(head + 8, COUNT);
    uint8_t *t = head + 10;
    entry(t, 256, 4, 1, (uint32_t)p->width); t += 12;
    entry(t, 257, 4, 1, (uint32_t)p->height); t += 12;
    entry(t, 258, 3, 4, EXTRA); t += 12;
    entry(t, 259, 3, 1, o->compression == 0 ? 1u : 8u); t += 12;
    entry(t, 262, 3, 1, 2); t += 12;
    entry(t, 273, 4, 1, PIXELS); t += 12;
    entry(t, 274, 3, 1, (uint32_t)p->metadata.orientation); t += 12;
    entry(t, 277, 3, 1, 4); t += 12;
    entry(t, 278, 4, 1, (uint32_t)p->height); t += 12;
    entry(t, 279, 4, 1, (uint32_t)encoded_length); t += 12;
    entry(t, 282, 5, 1, EXTRA + 8); t += 12;
    entry(t, 283, 5, 1, EXTRA + 16); t += 12;
    entry(t, 284, 3, 1, 1); t += 12;
    entry(t, 296, 3, 1, 2); t += 12;
    entry(t, 338, 3, 1, 2); t += 12;
    app_put32le(t, 0);
    for (int i = 0; i < 4; i++) app_put16le(head + EXTRA + i * 2, 8);
    double dpi_x = p->metadata.dpi_x > 0 ? p->metadata.dpi_x : 72, dpi_y = p->metadata.dpi_y > 0 ? p->metadata.dpi_y : dpi_x;
    app_put32le(head + EXTRA + 8, (uint32_t)(dpi_x * 1000 + 0.5));
    app_put32le(head + EXTRA + 12, 1000);
    app_put32le(head + EXTRA + 16, (uint32_t)(dpi_y * 1000 + 0.5));
    app_put32le(head + EXTRA + 20, 1000);
    bool ok = app_bytes_reserve(out, sizeof head + encoded_length);
    if (ok) { app_bytes_append(out, head, sizeof head); app_bytes_append(out, encoded, encoded_length); }
    app_bytes_free(&packed);
    return ok || app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory.");
}
