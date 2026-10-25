/* Images: identification by signature, inspection, decoding of an explicitly selected frame or page, and encoding
 * with the documented options. Pixels are owned, straight, top-down RGBA8; metadata carries the source pixel format,
 * EXIF orientation (pixels stay as stored), resolution and indexed palettes. */
#include "application.h"
#include "codecs/codecs.h"
#include "platform/platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

const char *const app_format_names[APP_FORMAT_COUNT] = {"Png", "Jpeg", "Bmp", "Gif", "Tiff"};

bool app_identify(const uint8_t *d, size_t n, app_format *format, app_error *e) {
    static const uint8_t png[8] = {137, 80, 78, 71, 13, 10, 26, 10};
    if (n >= 8 && memcmp(d, png, 8) == 0) *format = APP_PNG;
    else if (n >= 3 && d[0] == 255 && d[1] == 216 && d[2] == 255) *format = APP_JPEG;
    else if (n >= 2 && d[0] == 'B' && d[1] == 'M') *format = APP_BMP;
    else if (n >= 6 && (memcmp(d, "GIF87a", 6) == 0 || memcmp(d, "GIF89a", 6) == 0)) *format = APP_GIF;
    else if (n >= 4 && (memcmp(d, "II*\0", 4) == 0 || memcmp(d, "MM\0*", 4) == 0)) *format = APP_TIFF;
    else return app_error_set(e, APP_UNSUPPORTED, "Unknown or unsupported image signature.");
    return true;
}

void app_source_format(app_metadata *m, const char *color_type, int depth) {
    snprintf(m->source, sizeof m->source, "%s, %d bits/channel", color_type, depth);
}

bool app_frame_out_of_range(int32_t frame, int32_t frames, app_error *e) {
    if (frame >= 0 && frame < frames) return false;
    app_error_set(e, APP_INVALID_DATA, "Select a valid frame/page explicitly. (Parameter 'frame')");
    return true;
}

/* ---- EXIF orientation ---- */

int32_t app_exif_orientation(const uint8_t *t, size_t n) {
    if (n < 8) return 1;
    bool little = t[0] == 'I' && t[1] == 'I';
    if (!little && !(t[0] == 'M' && t[1] == 'M')) return 1;
#define U16(p) (little ? app_get16le(p) : app_get16be(p))
#define U32(p) (little ? app_get32le(p) : app_get32be(p))
    if (U16(t + 2) != 42) return 1;
    uint32_t ifd = U32(t + 4);
    if ((uint64_t)ifd + 2 > n) return 1;
    uint32_t count = U16(t + ifd);
    for (uint32_t i = 0; i < count; i++) {
        uint64_t entry = (uint64_t)ifd + 2 + (uint64_t)i * 12;
        if (entry + 12 > n) return 1;
        if (U16(t + entry) == 274 && U16(t + entry + 2) == 3) {
            uint32_t value = U16(t + entry + 8);
            return value >= 1 && value <= 8 ? (int32_t)value : 1;
        }
    }
#undef U16
#undef U32
    return 1;
}

void app_exif_block(app_bytes *out, int32_t orientation, bool header) {
    if (header) app_bytes_append(out, "Exif\0\0", 6);
    uint8_t block[26] = {'I', 'I', 42, 0, 8, 0, 0, 0, 1, 0};
    app_put16le(block + 10, 274);
    app_put16le(block + 12, 3);
    app_put32le(block + 14, 1);
    app_put32le(block + 18, (uint32_t)orientation);
    app_put32le(block + 22, 0);
    app_bytes_append(out, block, sizeof block);
}

/* ---- Pictures and metadata ---- */

void app_picture_free(app_picture *p) {
    free(p->rgba);
    free(p->metadata.palette);
    p->rgba = NULL;
    p->metadata.palette = NULL;
}

aqua_value app_metadata_value(aqua_runtime *rt, const app_metadata *m, bool palette) {
    aqua_value hash = aqua_hash_new(rt, 5);
    app_set(rt, hash, "orientation", aqua_int(m->orientation));
    app_set(rt, hash, "dpiX", m->dpi_x > 0 ? app_number(m->dpi_x) : aqua_null());
    app_set(rt, hash, "dpiY", m->dpi_y > 0 ? app_number(m->dpi_y) : aqua_null());
    app_set_text(rt, hash, "sourcePixelFormat", m->source[0] ? m->source : "RGBA8");
    if (palette && m->palette && m->palette_count) {
        aqua_value colors = aqua_array_new(rt, m->palette_count);
        for (uint32_t i = 0; i < m->palette_count; i++) aqua_array_set(rt, colors, i, app_number(m->palette[i]));
        app_set(rt, hash, "palette", colors);
    } else app_set(rt, hash, "palette", aqua_null());
    return hash;
}

aqua_value app_picture_value(aqua_runtime *rt, app_picture *p) {
    aqua_value value = aqua_image_new(rt, p->width, p->height, p->rgba, AQUA_ALPHA_STRAIGHT);
    aqua_image *image = aqua_image_of(rt, value);
    if (image) aqua_image_describe(rt, image, app_metadata_value(rt, &p->metadata, true), "RGBA8");
    app_picture_free(p);
    return value;
}

static bool metadata_number(aqua_runtime *rt, aqua_value hash, const char *name, double *out) {
    aqua_value v;
    *out = 0;
    if (!aqua_hash_field(rt, hash, name, &v) || aqua_is_nullish(v)) return true;
    if (!aqua_is_number(v) || !isfinite(v.number) || v.number <= 0) return false;
    *out = v.number;
    return true;
}

bool app_picture_of(aqua_runtime *rt, aqua_value value, app_picture *out, app_error *e) {
    memset(out, 0, sizeof *out);
    aqua_image *image = aqua_image_of(rt, value);
    if (!image) return app_error_set(e, APP_INVALID_DATA, "Expected a resource owned by this application runtime.");
    if (!app_check_pixels(image->width, image->height, 1, e)) return false;
    size_t bytes = (size_t)image->width * (size_t)image->height * 4;
    out->width = image->width;
    out->height = image->height;
    out->rgba = malloc(bytes);
    memcpy(out->rgba, image->rgba, bytes);
    if (image->alpha == AQUA_ALPHA_PREMULTIPLIED)
        for (size_t i = 0; i < bytes; i += 4) {
            uint32_t a = out->rgba[i + 3];
            for (int c = 0; c < 3; c++) {
                uint32_t v = a == 0 ? 0 : (out->rgba[i + c] * 255u + a / 2) / a;
                out->rgba[i + c] = (uint8_t)(v > 255 ? 255 : v);
            }
        }
    out->metadata.orientation = 1;
    if (image->metadata) {
        aqua_value hash = aqua_ref_value(rt, image->metadata), v;
        if (aqua_hash_field(rt, hash, "orientation", &v) && !aqua_is_nullish(v)) {
            if (!aqua_is_number(v) || v.number != floor(v.number) || v.number < 1 || v.number > 8) {
                app_picture_free(out);
                return app_error_set(e, APP_INVALID_DATA, "Invalid orientation or resolution.");
            }
            out->metadata.orientation = (int32_t)v.number;
        }
        if (!metadata_number(rt, hash, "dpiX", &out->metadata.dpi_x) || !metadata_number(rt, hash, "dpiY", &out->metadata.dpi_y)) {
            app_picture_free(out);
            return app_error_set(e, APP_INVALID_DATA, "Invalid orientation or resolution.");
        }
        /* A single supplied resolution axis applies to both axes. */
        if (out->metadata.dpi_x <= 0) out->metadata.dpi_x = out->metadata.dpi_y;
        if (out->metadata.dpi_y <= 0) out->metadata.dpi_y = out->metadata.dpi_x;
    }
    return true;
}

/* ---- Codec dispatch ---- */

typedef struct codec {
    bool (*inspect)(const uint8_t *, size_t, app_image_info *, app_error *);
    bool (*decode)(const uint8_t *, size_t, int32_t, app_picture *, app_error *);
    bool (*encode)(const app_picture *, const uint8_t *, const app_encode_options *, app_bytes *, app_error *);
} codec;
static const codec codecs[APP_FORMAT_COUNT] = {
    {app_png_inspect, app_png_decode, app_png_encode}, {app_jpeg_inspect, app_jpeg_decode, app_jpeg_encode},
    {app_bmp_inspect, app_bmp_decode, app_bmp_encode}, {app_gif_inspect, app_gif_decode, app_gif_encode},
    {app_tiff_inspect, app_tiff_decode, app_tiff_encode},
};

bool app_image_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    memset(info, 0, sizeof *info);
    if (!app_check_bytes(length, e) || !app_identify(data, length, &info->format, e)) return false;
    app_format format = info->format;
    info->metadata.orientation = 1;
    if (!codecs[format].inspect(data, length, info, e)) return false;
    info->format = format;
    return true;
}

bool app_image_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    memset(out, 0, sizeof *out);
    app_format format;
    if (!app_check_bytes(length, e) || !app_identify(data, length, &format, e)) return false;
    out->metadata.orientation = 1;
    return codecs[format].decode(data, length, frame, out, e);
}

void app_encode_defaults(app_encode_options *o) { memset(o, 0, sizeof *o); o->quality = 90; o->compression = 6; }

static bool option_integer(aqua_runtime *rt, aqua_value hash, const char *name, double low, double high, double *out, bool *present, app_error *e) {
    aqua_value v;
    *present = false;
    if (!app_field(rt, hash, name, &v)) return true;
    if (aqua_is_nullish(v) && strcmp(name, "background") == 0) return true;
    if (!aqua_is_number(v) || v.number != floor(v.number) || v.number < low || v.number > high)
        return app_error_set(e, APP_INVALID_DATA, "The JSON value of '%s' could not be converted to an integer.", name);
    *out = v.number;
    *present = true;
    return true;
}

bool app_encode_options_of(aqua_runtime *rt, aqua_value value, app_encode_options *o, app_error *e) {
    app_encode_defaults(o);
    if (aqua_is_nullish(value)) return true;
    if (value.type != AQUA_HASH) return app_error_set(e, APP_INVALID_DATA, "Invalid encoding options.");
    double n;
    bool present;
    if (!option_integer(rt, value, "quality", -2147483648.0, 2147483647.0, &n, &present, e)) return false;
    if (present) o->quality = (int32_t)n;
    if (!option_integer(rt, value, "compression", -2147483648.0, 2147483647.0, &n, &present, e)) return false;
    if (present) o->compression = (int32_t)n;
    if (!option_integer(rt, value, "background", 0, 4294967295.0, &n, &present, e)) return false;
    if (present) { o->has_background = true; o->background = (uint32_t)n; }
    aqua_value v;
    if (app_field(rt, value, "allowMetadataLoss", &v)) {
        if (v.type != AQUA_BOOL) return app_error_set(e, APP_INVALID_DATA, "The JSON value of 'allowMetadataLoss' could not be converted to a Boolean.");
        o->allow_metadata_loss = v.number != 0;
    }
    return true;
}

bool app_image_encode(const app_picture *p, app_format format, const app_encode_options *o, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    if (o->quality < 1 || o->quality > 100 || o->compression < 0 || o->compression > 9)
        return app_error_set(e, APP_INVALID_DATA, "Quality must be 1..100; compression must be 0..9.");
    if (!app_check_pixels(p->width, p->height, 1, e)) return false;
    size_t bytes = (size_t)p->width * (size_t)p->height * 4;
    uint8_t *pixels = malloc(bytes);
    memcpy(pixels, p->rgba, bytes);
    bool transparent = false, fractional = false;
    for (size_t i = 3; i < bytes; i += 4) {
        if (pixels[i] < 255) transparent = true;
        if (pixels[i] > 0 && pixels[i] < 255) fractional = true;
    }
    /* BMP interoperability and GIF's binary alpha cannot preserve arbitrary alpha. */
    bool loses_alpha = format == APP_JPEG || format == APP_BMP || (format == APP_GIF && fractional);
    if (transparent && loses_alpha) {
        if (!o->has_background) { free(pixels); return app_error_set(e, APP_INVALID_DATA, "This conversion requires an explicit opaque RGB background."); }
        for (size_t i = 0; i < bytes; i += 4) {
            uint32_t a = pixels[i + 3];
            for (int c = 0; c < 3; c++)
                pixels[i + c] = (uint8_t)((pixels[i + c] * a + ((o->background >> (16 - c * 8)) & 255) * (255 - a) + 127) / 255);
            pixels[i + 3] = 255;
        }
    }
    if (!o->allow_metadata_loss && (format == APP_BMP || format == APP_GIF) && p->metadata.orientation != 1) {
        free(pixels);
        return app_error_set(e, APP_INVALID_DATA, "Target format cannot preserve orientation; allow metadata loss explicitly.");
    }
    if (!o->allow_metadata_loss && format == APP_GIF && p->metadata.dpi_x > 0) {
        free(pixels);
        return app_error_set(e, APP_INVALID_DATA, "GIF cannot preserve physical resolution; allow metadata loss explicitly.");
    }
    bool ok = codecs[format].encode(p, pixels, o, out, e);
    free(pixels);
    if (!ok) { app_bytes_free(out); return false; }
    app_format written;
    if (!app_check_bytes(out->length, e)) { app_bytes_free(out); return false; }
    if (!app_identify(out->data, out->length, &written, NULL) || written != format) {
        app_bytes_free(out);
        return app_error_set(e, APP_INVALID_DATA, "Encoder returned a different format.");
    }
    return true;
}

static bool format_of(const char *name, app_format *out, app_error *e) {
    for (int i = 0; i < APP_FORMAT_COUNT; i++) if (app_equals_ignore_case(name, app_format_names[i])) { *out = (app_format)i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Requested value '%s' was not found.", name);
}

/* ---- Module functions ---- */

void app_images_Formats(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value array = aqua_array_new(rt, APP_FORMAT_COUNT);
    for (uint32_t i = 0; i < APP_FORMAT_COUNT; i++) aqua_array_set(rt, array, i, aqua_cstring(rt, app_format_names[i]));
    aqua_return(call, array);
}

void app_images_Inspect(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_bytes bytes;
    app_image_info info;
    if (app_cancelled(call)) return;
    if (!app_value_bytes(rt, aqua_arg(call, 0), &bytes, &e)) { app_fail(call, &e); return; }
    bool ok = app_image_inspect(bytes.data, bytes.length, &info, &e);
    app_bytes_free(&bytes);
    if (!ok) { app_fail(call, &e); return; }
    aqua_value record = aqua_hash_new(rt, 5);
    app_set(rt, record, "format", aqua_int((int32_t)info.format));
    app_set(rt, record, "width", aqua_int(info.width));
    app_set(rt, record, "height", aqua_int(info.height));
    app_set(rt, record, "frames", aqua_int(info.frames));
    app_set(rt, record, "metadata", app_metadata_value(rt, &info.metadata, false));
    free(info.metadata.palette);
    aqua_return(call, app_record(rt, record));
}

void app_images_Create(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_bytes bytes;
    int32_t width = aqua_arg_int(call, 0), height = aqua_arg_int(call, 1);
    if (!app_value_bytes(rt, aqua_arg(call, 2), &bytes, &e) || !app_check_pixels(width, height, 1, &e)) {
        app_bytes_free(&bytes);
        app_fail(call, &e);
        return;
    }
    if (bytes.length != (size_t)width * (size_t)height * 4) {
        app_bytes_free(&bytes);
        app_fail_with(call, APP_INVALID_DATA, "RGBA byte count must equal width * height * 4.");
        return;
    }
    app_picture picture = {width, height, bytes.data, {.orientation = 1}};
    aqua_return(call, app_picture_value(rt, &picture));
}

void app_images_Decode(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_bytes bytes;
    app_picture picture;
    if (app_cancelled(call)) return;
    if (!app_value_bytes(rt, aqua_arg(call, 0), &bytes, &e)) { app_fail(call, &e); return; }
    bool ok = app_image_decode(bytes.data, bytes.length, aqua_arg_int(call, 1), &picture, &e);
    app_bytes_free(&bytes);
    if (!ok) { app_fail(call, &e); return; }
    if (app_cancelled(call)) { app_picture_free(&picture); return; }
    aqua_return(call, app_picture_value(rt, &picture));
}

void app_images_Encode(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_format format;
    app_encode_options options;
    app_picture picture;
    app_bytes out;
    if (app_cancelled(call)) return;
    if (!format_of(aqua_arg_str(call, 1).data, &format, &e) || !app_encode_options_of(rt, aqua_arg(call, 2), &options, &e) ||
        !app_picture_of(rt, aqua_arg(call, 0), &picture, &e)) { app_fail(call, &e); return; }
    bool ok = app_image_encode(&picture, format, &options, &out, &e);
    app_picture_free(&picture);
    if (!ok) { app_fail(call, &e); return; }
    aqua_return(call, app_bytes_value(rt, out.data, out.length));
    app_bytes_free(&out);
}

/* Load and Save go through the files backend, which may wait (web storage and selected files). */
typedef struct image_task { app_task task; int32_t frame; app_bytes encoded; } image_task;

static void image_cleanup(app_task *task) { app_bytes_free(&((image_task *)task)->encoded); }

static void loaded(app_task *task, const app_error *e, app_bytes *data) {
    image_task *t = (image_task *)task;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    app_picture picture;
    bool ok = app_image_decode(data->data, data->length, t->frame, &picture, &error);
    if (!ok) { app_task_fail(task, &error); return; }
    app_task_return(task, app_picture_value(task->rt, &picture));
}

void app_images_Load(aqua_call *call) {
    app_resource *resource = app_resource_arg(call, 0);
    if (!resource || app_cancelled(call)) return;
    image_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = image_cleanup;
    t->frame = aqua_arg_int(call, 1);
    app_fs_read(&t->task, resource, loaded);
    app_task_yield(&t->task);
}

static void saved(app_task *task, const app_error *e, bool atomic) {
    if (e) app_task_fail(task, e);
    else app_task_return(task, aqua_bool(atomic));
}

void app_images_Save(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_resource *resource = app_resource_arg(call, 0);
    if (!resource || app_cancelled(call)) return;
    app_error e = {0};
    app_format format;
    app_encode_options options;
    app_picture picture;
    app_bytes out;
    if (!format_of(aqua_arg_str(call, 2).data, &format, &e) || !app_encode_options_of(rt, aqua_arg(call, 3), &options, &e) ||
        !app_picture_of(rt, aqua_arg(call, 1), &picture, &e)) { app_fail(call, &e); return; }
    bool ok = app_image_encode(&picture, format, &options, &out, &e);
    app_picture_free(&picture);
    if (!ok) { app_fail(call, &e); return; }
    image_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = image_cleanup;
    t->encoded = out;
    app_fs_replace(&t->task, resource, out.data, out.length, false, saved);
    app_task_yield(&t->task);
}
