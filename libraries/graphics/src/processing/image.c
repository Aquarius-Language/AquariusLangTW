/* PImage and pixels: images in CPU memory (uploaded when drawn), the pixel members of images and canvases, filters,
   PNG/PPM saving and decoding files with stb_image. Canvas pixels come from the GPU, so the members reading them
   complete asynchronously on the web (see common/calls.h). Pixels are packed ARGB numbers, top-down. */
#include "processing.h"
#include "../common/codecs.h"
#include "../common/files.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- Images ---- */

static pimage *create(processing *p, int width, int height, bool linked) {
    if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (int64_t)width * height > 16 * 1024 * 1024) return NULL;
    pimage *image = calloc(1, sizeof *image);
    image->owner = p;
    image->width = width; image->height = height;
    image->bytes = calloc((size_t)width * (size_t)height * 4, 1);
    image->dirty = true;
    if (linked) {
        image->next = p->images;
        if (p->images) p->images->previous = image;
        p->images = image;
    }
    return image;
}

pimage *gfx_image_new(processing *p, int width, int height) { return create(p, width, height, true); }
pimage *gfx_image_detached(processing *p, int width, int height) { return create(p, width, height, false); }

void gfx_image_dispose(pimage *image) {
    if (image->disposed) return;
    image->disposed = true;
    if (image->texture && !image->canvas) gpu_texture_free(image->texture);
    image->texture = NULL;
}

static void unlink_image(pimage *image) {
    processing *p = image->owner;
    if (image->previous) image->previous->next = image->next;
    else if (p && p->images == image) p->images = image->next;
    if (image->next) image->next->previous = image->previous;
    image->previous = image->next = NULL;
}

void gfx_image_free(pimage *image) {
    if (!image) return;
    gfx_image_dispose(image);
    unlink_image(image);
    if (image->pixels && image->owner) aqua_release(image->owner->rt, image->pixels);
    free(image->bytes);
    free(image);
}

void gfx_image_finalize(aqua_runtime *rt, void *data) { (void)rt; gfx_image_free(data); }

aqua_value gfx_image_object(aqua_runtime *rt, pimage *image) { return aqua_object_new(rt, gfx_image_class, image); }

pimage *gfx_image_of_value(processing *p, aqua_value value) {
    aqua_runtime *rt = p->rt;
    pimage *image = aqua_object_data(rt, value, gfx_image_class);
    if (image) return image->owner == p && !image->disposed ? image : NULL;
    canvas *c = aqua_object_data(rt, value, gfx_canvas_class);
    if (c && c->owner == p && !c->disposed && c->surface) return c->surface;
    return NULL;
}

pimage *gfx_image_argument(aqua_call *call, processing *p, uint32_t index) {
    pimage *image = gfx_image_of_value(p, aqua_arg(call, index));
    if (!image) gfx_fail(call, "Expected a live PImage or PGraphics from this runtime.");
    return image;
}

typedef struct image_read { pimage *image; pixels_done done; void *data; } image_read;
void gfx_image_pixels(pimage *image, pixels_done done, void *data) {
    if (image->canvas) gfx_canvas_read_pixels(image->canvas, done, data);
    else done(data, image->bytes, image->width, image->height, NULL);
}

/* ---- Pixel helpers ---- */

uint32_t gfx_pixel_at(const uint8_t *bytes, int width, int height, int x, int y) {
    if (x < 0 || y < 0 || x >= width || y >= height) return 0;
    const uint8_t *p = bytes + ((size_t)y * (size_t)width + (size_t)x) * 4;
    return (uint32_t)p[3] << 24 | (uint32_t)p[0] << 16 | (uint32_t)p[1] << 8 | p[2];
}

void gfx_pixel_set(uint8_t *bytes, int width, int height, int x, int y, uint32_t c) {
    if (x < 0 || y < 0 || x >= width || y >= height) return;
    uint8_t *p = bytes + ((size_t)y * (size_t)width + (size_t)x) * 4;
    p[0] = (uint8_t)(c >> 16); p[1] = (uint8_t)(c >> 8); p[2] = (uint8_t)c; p[3] = (uint8_t)(c >> 24);
}

/* A pixel of an image at logical coordinates (canvases are sampled at their pixel density). */
static uint32_t image_pixel_at(const pimage *image, const uint8_t *bytes, int x, int y) {
    int width = image->canvas ? image->canvas->width : image->width, height = image->canvas ? image->canvas->height : image->height;
    if (x < 0 || y < 0 || x >= width || y >= height) return 0;
    return gfx_pixel_at(bytes, image->width, image->height, (int)((int64_t)x * image->width / width), (int)((int64_t)y * image->height / height));
}

aqua_value gfx_pixel_array(aqua_runtime *rt, const uint8_t *bytes, int count) {
    double *values = malloc((size_t)(count ? count : 1) * sizeof *values);
    for (int i = 0; i < count; i++) {
        const uint8_t *p = bytes + (size_t)i * 4;
        values[i] = (double)((uint32_t)p[3] << 24 | (uint32_t)p[0] << 16 | (uint32_t)p[1] << 8 | p[2]);
    }
    aqua_value array = aqua_array_from_f64(rt, values, (uint32_t)count, AQUA_DOUBLE);
    free(values);
    return array;
}

/* Rewrites a published pixels array in place when it still has `count` elements (scripts keep their reference). */
static bool refresh_pixel_array(aqua_runtime *rt, aqua_value array, const uint8_t *bytes, int count) {
    if (array.type != AQUA_ARRAY || (int)aqua_array_length(rt, array) != count) return false;
    double *values = malloc((size_t)(count ? count : 1) * sizeof *values);
    for (int i = 0; i < count; i++) {
        const uint8_t *p = bytes + (size_t)i * 4;
        values[i] = (double)((uint32_t)p[3] << 24 | (uint32_t)p[0] << 16 | (uint32_t)p[1] << 8 | p[2]);
    }
    bool ok = count == 0 || aqua_array_write_f64(rt, array, 0, (uint32_t)count, values, AQUA_DOUBLE);
    free(values);
    return ok;
}

bool gfx_pixel_bytes(aqua_call *call, aqua_value array, int count, uint8_t *out) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if ((int)aqua_array_length(rt, array) != count) { gfx_fail(call, "pixels must contain %d packed ARGB colors.", count); return false; }
    /* One bulk read of the numbers; other elements (or a failed read) take the per-element path and its messages. */
    double *numbers = malloc((size_t)(count ? count : 1) * sizeof *numbers);
    bool bulk = count > 0 && aqua_array_read_f64(rt, array, 0, (uint32_t)count, numbers);
    for (int i = 0; i < count; i++) {
        double n = bulk ? numbers[i] : -1;
        uint32_t c = n >= 0 && n <= 4294967295.0 && n == (double)(uint32_t)n ? (uint32_t)n : gfx_packed_value(call, aqua_array_get(rt, array, (uint32_t)i));
        if (aqua_failed(call)) { free(numbers); return false; }
        uint8_t *p = out + (size_t)i * 4;
        p[0] = (uint8_t)(c >> 16); p[1] = (uint8_t)(c >> 8); p[2] = (uint8_t)c; p[3] = (uint8_t)(c >> 24);
    }
    free(numbers);
    return true;
}

static uint8_t clamp_byte(int v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

void gfx_image_filter(uint8_t *bytes, int width, int height, int mode, float parameter) {
    size_t n = (size_t)width * (size_t)height * 4;
    if (mode == 5) {
        int radius = (int)ceilf(parameter);
        radius = radius < 1 ? 1 : radius > 32 ? 32 : radius;
        /* Horizontal then vertical box sums over edge-clamped samples, as running sums (cost independent of radius). */
        uint8_t *tmp = malloc(n);
        int span = 2 * radius + 1;
        for (int pass = 0; pass < 2; pass++) {
            const uint8_t *from = pass == 0 ? bytes : tmp;
            uint8_t *to = pass == 0 ? tmp : bytes;
            int lines = pass == 0 ? height : width, length = pass == 0 ? width : height;
            size_t step = pass == 0 ? 4 : (size_t)width * 4, line_step = pass == 0 ? (size_t)width * 4 : 4;
            for (int l = 0; l < lines; l++) {
                const uint8_t *src = from + (size_t)l * line_step;
                uint8_t *dst = to + (size_t)l * line_step;
                for (int ch = 0; ch < 4; ch++) {
                    int sum = 0;
                    for (int d = -radius; d <= radius; d++) { int i = d < 0 ? 0 : d > length - 1 ? length - 1 : d; sum += src[(size_t)i * step + (size_t)ch]; }
                    for (int i = 0; i < length; i++) {
                        dst[(size_t)i * step + (size_t)ch] = (uint8_t)(sum / span);
                        int out = i - radius < 0 ? 0 : i - radius, in = i + radius + 1 > length - 1 ? length - 1 : i + radius + 1;
                        sum += src[(size_t)in * step + (size_t)ch] - src[(size_t)out * step + (size_t)ch];
                    }
                }
            }
        }
        free(tmp);
        return;
    }
    double levels = floor((double)parameter) - 1;
    for (size_t i = 0; i < n; i += 4) {
        uint8_t gray = clamp_byte((int)rint(bytes[i] * .299 + bytes[i + 1] * .587 + bytes[i + 2] * .114));
        if (mode == 0) bytes[i] = bytes[i + 1] = bytes[i + 2] = gray;
        if (mode == 1) for (int k = 0; k < 3; k++) bytes[i + k] = (uint8_t)(255 - bytes[i + k]);
        if (mode == 2) bytes[i] = bytes[i + 1] = bytes[i + 2] = (float)gray >= parameter * 255 ? 255 : 0;
        if (mode == 3) for (int k = 0; k < 3; k++) bytes[i + k] = (uint8_t)(rint(bytes[i + k] / 255.0 * levels) * 255 / levels);
        if (mode == 4) bytes[i + 3] = 255;
    }
}

static bool filter_arguments(aqua_call *call, int *mode, float *parameter) {
    *mode = GFX_CHOICE(call, 0, 0, 1, 2, 3, 4, 5);
    *parameter = aqua_argc(call) == 2 ? gfx_float(call, 1) : 0.5f;
    if (aqua_failed(call)) return false;
    if (*mode == 3 && (*parameter < 2 || *parameter > 255)) { gfx_fail(call, "POSTERIZE levels must be 2..255."); return false; }
    return true;
}

bool gfx_image_write_file(const char *path, int width, int height, const uint8_t *rgba, char *error, size_t size) {
    char extension[16];
    gfx_extension(path, extension, sizeof extension);
    if (strcmp(extension, ".png") != 0 && strcmp(extension, ".ppm") != 0) { snprintf(error, size, "save supports .png and .ppm."); return false; }
    if (strcmp(extension, ".ppm") == 0) {
        char header[64];
        int h = snprintf(header, sizeof header, "P6\n%d %d\n255\n", width, height);
        size_t n = (size_t)width * (size_t)height;
        uint8_t *data = malloc((size_t)h + n * 3);
        memcpy(data, header, (size_t)h);
        for (size_t i = 0; i < n; i++) memcpy(data + h + i * 3, rgba + i * 4, 3);
        bool ok = gfx_write_file(path, data, (size_t)h + n * 3, error, size);
        free(data);
        return ok;
    }
    size_t length;
    uint8_t *png = gfx_codec_png(rgba, width, height, &length);
    if (!png) { snprintf(error, size, "PNG encoding failed."); return false; }
    bool ok = gfx_write_file(path, png, length, error, size);
    free(png);
    return ok;
}

pimage *gfx_image_load(aqua_call *call, processing *p, const char *path) {
    char error[1024];
    gfx_bytes file;
    if (!gfx_read_resource(aqua_call_runtime(call), path, &file, error, sizeof error)) { gfx_fail(call, "%s", error); return NULL; }
    int width, height;
    const char *reason = NULL;
    uint8_t *pixels = gfx_codec_decode(file.data, file.length, &width, &height, &reason);
    gfx_bytes_free(&file);
    if (!pixels) { gfx_fail(call, "%s", reason ? reason : "Image decode failed."); return NULL; }
    pimage *image = gfx_image_new(p, width, height);
    if (!image) { free(pixels); gfx_fail(call, "Image dimensions must be 1..8192 with at most 16 million pixels."); return NULL; }
    memcpy(image->bytes, pixels, (size_t)width * (size_t)height * 4);
    free(pixels);
    return image;
}

/* loadPixels() and friends: the published array is refreshed in place when its size still fits, as Processing keeps one
   pixels[] per image (no new array per frame); otherwise a new one replaces it. */
static void publish_pixels(aqua_runtime *rt, aqua_ref *slot, const uint8_t *bytes, int count) {
    if (*slot && refresh_pixel_array(rt, aqua_ref_value(rt, *slot), bytes, count)) return;
    if (*slot) aqua_release(rt, *slot);
    *slot = aqua_retain(rt, gfx_pixel_array(rt, bytes, count));
}

/* ---- PImage members ---- */

static void p_image_loadPixels(aqua_call *call, processing *p, pimage *image) {
    (void)p;
    aqua_runtime *rt = aqua_call_runtime(call);
    publish_pixels(rt, &image->pixels, image->bytes, image->width * image->height);
}

static void p_image_updatePixels(aqua_call *call, processing *p, pimage *image) {
    (void)p;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value array = image->pixels ? aqua_ref_value(rt, image->pixels) : aqua_null();
    if (array.type != AQUA_ARRAY) { gfx_fail(call, "Expected an array."); return; }
    int count = image->width * image->height;
    uint8_t *bytes = malloc((size_t)count * 4);
    if (gfx_pixel_bytes(call, array, count, bytes)) { memcpy(image->bytes, bytes, (size_t)count * 4); image->dirty = true; }
    free(bytes);
}

static pimage *copy_region(aqua_call *call, processing *p, const pimage *image, int x, int y, int w, int h) {
    pimage *copy = gfx_image_new(p, w, h);
    if (!copy) { gfx_fail(call, "Image dimensions must be 1..8192 with at most 16 million pixels."); return NULL; }
    for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) gfx_pixel_set(copy->bytes, w, h, i, j, gfx_pixel_at(image->bytes, image->width, image->height, x + i, y + j));
    return copy;
}

static void p_image_get(aqua_call *call, processing *p, pimage *image) {
    uint32_t n = aqua_argc(call);
    if (n == 2) {
        int x = gfx_int(call, 0), y = gfx_int(call, 1);
        if (!aqua_failed(call)) aqua_return_double(call, gfx_pixel_at(image->bytes, image->width, image->height, x, y));
        return;
    }
    if (n != 0 && n != 4) { gfx_fail(call, "get() takes zero, two, or four arguments."); return; }
    int x = n ? gfx_int(call, 0) : 0, y = n ? gfx_int(call, 1) : 0, w = n ? gfx_dimension(call, 2) : image->width, h = n ? gfx_dimension(call, 3) : image->height;
    if (aqua_failed(call)) return;
    pimage *copy = copy_region(call, p, image, x, y, w, h);
    if (copy) aqua_return(call, gfx_image_object(aqua_call_runtime(call), copy));
}

static void p_image_set(aqua_call *call, processing *p, pimage *image) {
    (void)p;
    int x = gfx_int(call, 0), y = gfx_int(call, 1);
    uint32_t c = gfx_packed(call, 2);
    if (aqua_failed(call)) return;
    gfx_pixel_set(image->bytes, image->width, image->height, x, y, c);
    image->dirty = true;
}

static void p_image_save(aqua_call *call, processing *p, pimage *image) {
    (void)p;
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char error[1024];
    if (!gfx_image_write_file(path.data, image->width, image->height, image->bytes, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void p_image_filter(aqua_call *call, processing *p, pimage *image) {
    (void)p;
    int mode; float parameter;
    if (!filter_arguments(call, &mode, &parameter)) return;
    gfx_image_filter(image->bytes, image->width, image->height, mode, parameter);
    image->dirty = true;
}

typedef struct mask_operation { pimage *image; aqua_ref source; } mask_operation;
static void masked(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    mask_operation *m = gfx_async_data(a);
    aqua_release(gfx_async_runtime(a), m->source);
    (void)width; (void)height;
    if (error) { gfx_async_fail(a, "%s", error); return; }
    pimage *image = m->image;
    for (size_t i = 0, n = (size_t)image->width * (size_t)image->height * 4; i < n; i += 4) image->bytes[i + 3] = rgba[i + 2];
    image->dirty = true;
    gfx_async_finish(a, aqua_void());
}
static void p_image_mask(aqua_call *call, processing *p, pimage *image) {
    pimage *mask = gfx_image_argument(call, p, 0);
    if (!mask) return;
    if (mask->width != image->width || mask->height != image->height) { gfx_fail(call, "Mask dimensions must match."); return; }
    gfx_async *a = gfx_async_begin(call, sizeof(mask_operation));
    mask_operation *m = gfx_async_data(a);
    m->image = image;
    m->source = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 0));
    gfx_image_pixels(mask, masked, a);
    gfx_async_end(a);
}

static void p_image_resize(aqua_call *call, processing *p, pimage *image) {
    int w = gfx_int(call, 0), h = gfx_int(call, 1);
    if (aqua_failed(call)) return;
    if (w == 0 && h > 0) { int r = (int)rint((double)image->width * h / image->height); w = r > 1 ? r : 1; }
    if (h == 0 && w > 0) { int r = (int)rint((double)image->height * w / image->width); h = r > 1 ? r : 1; }
    pimage *resized = gfx_image_detached(p, w, h);
    if (!resized) { gfx_fail(call, "Image dimensions must be 1..8192 with at most 16 million pixels."); return; }
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            gfx_pixel_set(resized->bytes, w, h, x, y, gfx_pixel_at(image->bytes, image->width, image->height, (int)((int64_t)x * image->width / w), (int)((int64_t)y * image->height / h)));
    free(image->bytes);
    image->bytes = resized->bytes;
    resized->bytes = NULL;
    gfx_image_free(resized);
    image->width = w; image->height = h;
    image->dirty = true;
    aqua_runtime *rt = aqua_call_runtime(call);
    publish_pixels(rt, &image->pixels, image->bytes, w * h);
}

typedef struct copy_operation {
    pimage *source_image, *target_image;
    canvas *target_canvas;
    aqua_ref source;
    int sx, sy, sw, sh, dx, dy, dw, dh;
    float fx, fy, fw, fh;
} copy_operation;

static void copied_into_image(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    copy_operation *o = gfx_async_data(a);
    aqua_release(gfx_async_runtime(a), o->source);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    (void)width; (void)height;
    pimage *image = o->target_image;
    size_t bytes = (size_t)o->source_image->width * (size_t)o->source_image->height * 4;
    uint8_t *snapshot = malloc(bytes);
    memcpy(snapshot, rgba, bytes);
    for (int y = 0; y < o->dh; y++)
        for (int x = 0; x < o->dw; x++)
            gfx_pixel_set(image->bytes, image->width, image->height, o->dx + x, o->dy + y,
                      image_pixel_at(o->source_image, snapshot, o->sx + (int)((int64_t)x * o->sw / o->dw), o->sy + (int)((int64_t)y * o->sh / o->dh)));
    free(snapshot);
    image->dirty = true;
    gfx_async_finish(a, aqua_void());
}

static bool copy_arguments(aqua_call *call, processing *p, copy_operation *o) {
    o->source_image = gfx_image_argument(call, p, 0);
    if (!o->source_image) return false;
    o->sx = gfx_int(call, 1); o->sy = gfx_int(call, 2); o->sw = gfx_dimension(call, 3); o->sh = gfx_dimension(call, 4);
    return !aqua_failed(call);
}

static void p_image_copy(aqua_call *call, processing *p, pimage *image) {
    copy_operation o = {0};
    if (!copy_arguments(call, p, &o)) return;
    o.dx = gfx_int(call, 5); o.dy = gfx_int(call, 6); o.dw = gfx_dimension(call, 7); o.dh = gfx_dimension(call, 8);
    if (aqua_failed(call)) return;
    o.target_image = image;
    gfx_async *a = gfx_async_begin(call, sizeof o);
    o.source = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 0));
    *(copy_operation *)gfx_async_data(a) = o;
    gfx_image_pixels(o.source_image, copied_into_image, a);
    gfx_async_end(a);
}

/* ---- Canvas pixel members ---- */

static bool pixel_read_checks(aqua_call *call, processing *p, canvas *c) { return gfx_require_sketch(call, p) && gfx_canvas_require_readable(call, c); }
static bool pixel_draw_checks(aqua_call *call, processing *p, canvas *c) { return gfx_require_sketch(call, p) && gfx_canvas_require_drawing(call, c); }

static void loaded(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    canvas *c = *(canvas **)gfx_async_data(a);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    aqua_runtime *rt = gfx_async_runtime(a);
    publish_pixels(rt, &c->pixels, rgba, width * height);
    gfx_async_finish(a, aqua_void());
}
static void p_loadPixels(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_read_checks(call, p, c)) return;
    gfx_async *a = gfx_async_begin(call, sizeof c);
    *(canvas **)gfx_async_data(a) = c;
    gfx_canvas_read_pixels(c, loaded, a);
    gfx_async_end(a);
}

static void p_updatePixels(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_draw_checks(call, p, c)) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value array = c->pixels ? aqua_ref_value(rt, c->pixels) : aqua_null();
    if (array.type != AQUA_ARRAY) { gfx_fail(call, "Call loadPixels() first."); return; }
    int count = c->pixel_width * c->pixel_height;
    uint8_t *bytes = malloc((size_t)(count ? count : 1) * 4);
    if (gfx_pixel_bytes(call, array, count, bytes)) gfx_canvas_write_pixels(call, c, bytes);
    free(bytes);
}

typedef struct get_operation { canvas *canvas; int arguments; float fx, fy; int x, y, w, h; processing *p; } get_operation;
static void got(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    get_operation *o = gfx_async_data(a);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    canvas *c = o->canvas;
    (void)width; (void)height;
    if (o->arguments == 2) {
        gfx_async_finish(a, aqua_double(gfx_pixel_at(rgba, c->pixel_width, c->pixel_height, (int)(o->fx * (float)c->pixel_width / (float)c->width),
                                                 (int)(o->fy * (float)c->pixel_height / (float)c->height))));
        return;
    }
    pimage *image = gfx_image_new(o->p, o->w, o->h);
    if (!image) { gfx_async_fail(a, "Image dimensions must be 1..8192 with at most 16 million pixels."); return; }
    for (int j = 0; j < o->h; j++)
        for (int i = 0; i < o->w; i++)
            gfx_pixel_set(image->bytes, o->w, o->h, i, j, gfx_pixel_at(rgba, c->pixel_width, c->pixel_height,
                      (int)((int64_t)(o->x + i) * c->pixel_width / c->width), (int)((int64_t)(o->y + j) * c->pixel_height / c->height)));
    gfx_async_finish(a, gfx_image_object(gfx_async_runtime(a), image));
}
static void p_get(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_read_checks(call, p, c)) return;
    get_operation o = {c, (int)aqua_argc(call), 0, 0, 0, 0, c->width, c->height, p};
    if (o.arguments == 2) { o.fx = gfx_float(call, 0); o.fy = gfx_float(call, 1); }
    else if (o.arguments == 0 || o.arguments == 4) {
        if (o.arguments == 4) { o.x = gfx_int(call, 0); o.y = gfx_int(call, 1); o.w = gfx_dimension(call, 2); o.h = gfx_dimension(call, 3); }
    } else { gfx_fail(call, "get() takes zero, two, or four arguments."); return; }
    if (aqua_failed(call)) return;
    gfx_async *a = gfx_async_begin(call, sizeof o);
    *(get_operation *)gfx_async_data(a) = o;
    gfx_canvas_read_pixels(c, got, a);
    gfx_async_end(a);
}

static void p_set(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_draw_checks(call, p, c)) return;
    if (aqua_arg(call, 2).type == AQUA_NATIVE) {
        pimage *image = gfx_image_argument(call, p, 2);
        float x = gfx_float(call, 0), y = gfx_float(call, 1);
        if (image && !aqua_failed(call)) gfx_canvas_image(call, c, image, x, y, NULL, NULL);
        return;
    }
    int x = gfx_int(call, 0), y = gfx_int(call, 1);
    uint32_t color = gfx_packed(call, 2);
    if (aqua_failed(call) || x < 0 || y < 0 || x >= c->width || y >= c->height) return;
    gfx_canvas_set_pixel(call, c, (int)((int64_t)x * c->pixel_width / c->width), (int)((int64_t)y * c->pixel_height / c->height), color);
}

typedef struct save_operation { char path[1024]; } save_operation;
static void save_read(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    save_operation *o = gfx_async_data(a);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    char message[1024];
    if (!gfx_image_write_file(o->path, width, height, rgba, message, sizeof message)) { gfx_async_fail(a, "%s", message); return; }
    gfx_async_finish(a, aqua_void());
}
static void save_canvas(aqua_call *call, canvas *c, const char *path) {
    gfx_async *a = gfx_async_begin(call, sizeof(save_operation));
    snprintf(((save_operation *)gfx_async_data(a))->path, sizeof(((save_operation *)0)->path), "%s", path);
    gfx_canvas_read_pixels(c, save_read, a);
    gfx_async_end(a);
}
static void p_save(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_read_checks(call, p, c)) return;
    aqua_str path = gfx_text(call, 0);
    if (!aqua_failed(call)) save_canvas(call, c, path.data);
}
static void p_saveFrame(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_read_checks(call, p, c)) return;
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char resolved[1100];
    const char *hash = strchr(path.data, '#');
    if (hash) {
        size_t count = 0;
        while (hash[count] == '#') count++;
        snprintf(resolved, sizeof resolved, "%.*s%0*d%s", (int)(hash - path.data), path.data, (int)count, p->frame_count, hash + count);
    } else snprintf(resolved, sizeof resolved, "%s", path.data);
    save_canvas(call, c, resolved);
}

typedef struct filter_operation { canvas *canvas; int mode; float parameter; } filter_operation;
static void filter_read(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    filter_operation *o = gfx_async_data(a);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    size_t n = (size_t)width * (size_t)height * 4;
    uint8_t *bytes = malloc(n);
    memcpy(bytes, rgba, n);
    gfx_image_filter(bytes, width, height, o->mode, o->parameter);
    bool ok = gfx_canvas_write_pixels(gfx_async_call(a), o->canvas, bytes);
    free(bytes);
    gfx_async_settle(a, ok, aqua_void());
}
static void p_filter(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_draw_checks(call, p, c)) return;
    filter_operation o = {c, 0, 0};
    if (!filter_arguments(call, &o.mode, &o.parameter)) return;
    gfx_async *a = gfx_async_begin(call, sizeof o);
    *(filter_operation *)gfx_async_data(a) = o;
    gfx_canvas_read_pixels(c, filter_read, a);
    gfx_async_end(a);
}

static void copied_into_canvas(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    copy_operation *o = gfx_async_data(a);
    aqua_release(gfx_async_runtime(a), o->source);
    if (error) { gfx_async_fail(a, "%s", error); return; }
    (void)width; (void)height;
    processing *p = o->target_canvas->owner;
    pimage *cropped = gfx_image_detached(p, o->sw, o->sh);
    for (int y = 0; y < o->sh; y++)
        for (int x = 0; x < o->sw; x++) gfx_pixel_set(cropped->bytes, o->sw, o->sh, x, y, image_pixel_at(o->source_image, rgba, o->sx + x, o->sy + y));
    float w = o->fw, h = o->fh;
    bool ok = gfx_canvas_image(gfx_async_call(a), o->target_canvas, cropped, o->fx, o->fy, &w, &h);
    gfx_image_free(cropped);
    gfx_async_settle(a, ok, aqua_void());
}
static void p_copy(aqua_call *call, processing *p, canvas *c) {
    if (!pixel_draw_checks(call, p, c)) return;
    copy_operation o = {0};
    if (!copy_arguments(call, p, &o)) return;
    o.fx = gfx_float(call, 5); o.fy = gfx_float(call, 6); o.fw = gfx_float(call, 7); o.fh = gfx_float(call, 8);
    if (aqua_failed(call)) return;
    o.target_canvas = c;
    gfx_async *a = gfx_async_begin(call, sizeof o);
    o.source = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 0));
    *(copy_operation *)gfx_async_data(a) = o;
    gfx_image_pixels(o.source_image, copied_into_canvas, a);
    gfx_async_end(a);
}

/* ---- Module members ---- */

static void p_createImage(aqua_call *call, processing *p) {
    int format = aqua_argc(call) == 3 ? GFX_CHOICE(call, 2, 0, 2) : 2;
    int w = gfx_dimension(call, 0), h = gfx_dimension(call, 1);
    if (aqua_failed(call)) return;
    pimage *image = gfx_image_new(p, w, h);
    if (!image) { gfx_fail(call, "Image dimensions must be 1..8192 with at most 16 million pixels."); return; }
    if (format == 0) for (size_t i = 3, n = (size_t)w * (size_t)h * 4; i < n; i += 4) image->bytes[i] = 255;
    aqua_return(call, gfx_image_object(aqua_call_runtime(call), image));
}

static void p_loadImage(aqua_call *call, processing *p) {
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    pimage *image = gfx_image_load(call, p, path.data);
    if (image) aqua_return(call, gfx_image_object(aqua_call_runtime(call), image));
}

/* ---- Properties ---- */

static void image_width(aqua_call *call) { aqua_return_int(call, ((pimage *)aqua_self(call))->width); }
static void image_height(aqua_call *call) { aqua_return_int(call, ((pimage *)aqua_self(call))->height); }
static void image_pixels_property(aqua_call *call) {
    pimage *image = aqua_self(call);
    if (image->pixels) aqua_return(call, aqua_ref_value(aqua_call_runtime(call), image->pixels));
    else aqua_return_null(call);
}

#define IMAGE_MEMBERS(X) \
    X(image_loadPixels, "loadPixels|載入像素", 0, 0) X(image_updatePixels, "updatePixels|更新像素", 0, 0) X(image_get, "get|取得", 0, 4) \
    X(image_set, "set|設定", 3, 3) X(image_save, "save|儲存", 1, 1) X(image_filter, "filter|濾鏡", 1, 2) X(image_mask, "mask|遮罩", 1, 1) \
    X(image_resize, "resize|調整尺寸", 2, 2) X(image_copy, "copy|複製", 9, 9)
#define IMAGE_WRAPPER(name, names, minimum, maximum) OBJECT_WRAPPER(pimage, name, names, minimum, maximum)
#define IMAGE_DEFINE(name, names, minimum, maximum) OBJECT_DEFINE(image, name, names, minimum, maximum)
IMAGE_MEMBERS(IMAGE_WRAPPER)

#define CANVAS_PIXELS(X) \
    X(loadPixels, "loadPixels|載入像素", 0, 0) X(updatePixels, "updatePixels|更新像素", 0, 0) X(get, "get|取得", 0, 4) X(set, "set|設定", 3, 3) \
    X(save, "save|儲存", 1, 1) X(saveFrame, "saveFrame|儲存影格", 1, 1) X(filter, "filter|濾鏡", 1, 2) X(copy, "copy|複製", 9, 9)
CANVAS_PIXELS(CANVAS_WRAPPERS)

#define IMAGE_MODULE(X) X(createImage, "createImage|建立圖片", 2, 3) X(loadImage, "loadImage|載入圖片", 1, 1)
IMAGE_MODULE(MODULE_WRAPPER)

void gfx_image_register(aqua_class *module, aqua_class *canvas, aqua_class *image) {
    IMAGE_MEMBERS(IMAGE_DEFINE)
    aqua_define_property(image, "width", image_width);
    aqua_define_property(image, "height", image_height);
    aqua_define_property(image, "pixels", image_pixels_property);
    CANVAS_PIXELS(CANVAS_DEFINE)
    IMAGE_MODULE(MODULE_DEFINE)
}
