/* Processing text: fonts, sizes, alignment, wrapping and drawing lines as cached rasters. Each line is rasterized by
   the platform's text system (src/platform) at the scale it appears on screen, cached as an image per text, font and
   raster size, and drawn snapped to device pixels under 2D translate/scale transforms so it stays sharp. Without a
   platform text system, a small built-in bitmap font is used. */
#include "processing.h"
#include "../common/unicode.h"
#include "../platform/platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CACHE_ENTRIES 4096
#define CACHE_IDLE_FRAMES 600
#define CACHE_BYTES (96u * 1024 * 1024)

typedef struct font_object { char name[256]; float size; } font_object;

void gfx_font_finalize(aqua_runtime *rt, void *data) { (void)rt; free(data); }

/* ---- Measuring and rasterizing one line ---- */

static const char *const glyph_keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-:/|+";
static const char *const glyphs[] = {
    "01110100011000111111100011000110001", "11110100011000111110100011000111110", "01111100001000010000100001000001111",
    "11110100011000110001100011000111110", "11111100001000011110100001000011111", "11111100001000011110100001000010000",
    "01111100001000010111100011000101111", "10001100011000111111100011000110001", "11111001000010000100001000010011111",
    "00111000100001000010100101001001100", "10001100101010011000101001001010001", "10000100001000010000100001000011111",
    "10001110111010110101100011000110001", "10001110011010110011100011000110001", "01110100011000110001100011000101110",
    "11110100011000111110100001000010000", "01110100011000110001101011001001101", "11110100011000111110101001001010001",
    "01111100001000001110000010000111110", "11111001000010000100001000010000100", "10001100011000110001100011000101110",
    "10001100011000110001100010101000100", "10001100011000110101101011101110001", "10001100010101000100010101000110001",
    "10001100010101000100001000010000100", "11111000010001000100010001000011111", "01110100011001110101110011000101110",
    "00100011000010000100001000010001110", "01110100010000100010001000100011111", "11110000010000101110000010000111110",
    "00010001100101010010111110001000010", "11111100001000011110000010000111110", "01110100001000011110100011000101110",
    "11111000010001000100010000100001000", "01110100011000101110100011000101110", "01110100011000101111000010000101110",
    "00000000000000000000000000011000110", "00000000000000011111000000000000000", "00000001100011000000001100011000000",
    "00001000100001000100010000100010000", "00100001000010000100001000010000100", "00000001000010011111001000010000000"};

static size_t utf16_length(const char *text, size_t length) {
    size_t units = 0;
    for (size_t i = 0; i < length;) units += utf16_units(gfx_utf8_next(text, length, &i));
    return units;
}

/* The raster box of a line (what textWidth reports), from the platform or the bitmap font. */
static bool line_box(const char *font, float size, const char *text, size_t length, int *w, int *h) {
    if (gfx_platform_text_box(font, size, text, length, w, h)) return true;
    size_t units = utf16_length(text, length);
    int width = (int)ceilf((float)units * size * 6 / 8), height = (int)ceilf(size);
    *w = width > 1 ? width : 1;
    *h = height > 1 ? height : 1;
    return false;
}

static void bitmap_raster(float size, const char *text, size_t length, int w, int h, uint8_t *alpha) {
    float scale = size / 8;
    int index = 0;
    for (size_t i = 0; i < length; index++) {
        uint32_t c = gfx_utf8_next(text, length, &i);
        if (c >= 'a' && c <= 'z') c -= 32;
        if (c == ' ') continue;
        const char *pattern = "11111100011010110001101011000111111";
        const char *k = c < 128 ? strchr(glyph_keys, (int)c) : NULL;
        if (k && c) pattern = glyphs[k - glyph_keys];
        for (int row = 0; row < 7; row++)
            for (int col = 0; col < 5; col++) {
                if (pattern[row * 5 + col] != '1') continue;
                for (int y = (int)(row * scale); y < (int)ceilf((row + 1) * scale) && y < h; y++)
                    for (int x = (int)((index * 6 + col) * scale); x < (int)ceilf((index * 6 + col + 1) * scale) && x < w; x++)
                        alpha[(size_t)y * (size_t)w + (size_t)x] = 255;
            }
    }
}

/* Validates and measures a line ("Text is limited to 4096 characters per call."). */
static bool measure(aqua_call *call, const char *font, float size, const char *text, size_t length, int *w, int *h) {
    if (utf16_length(text, length) > 4096) { gfx_fail(call, "Text is limited to 4096 characters per call."); return false; }
    line_box(font, size, text, length, w, h);
    return true;
}

/* ---- The raster cache ---- */

typedef struct cached { pimage *image; int64_t last_frame; } cached;

static size_t cache_key(char *key, size_t capacity, const char *text, size_t length, const char *font, float size) {
    size_t font_length = strlen(font);
    if (length + font_length + 2 + sizeof size > capacity) return 0;
    memcpy(key, text, length);
    key[length] = 0;
    memcpy(key + length + 1, font, font_length + 1);
    memcpy(key + length + font_length + 2, &size, sizeof size);
    return length + font_length + 2 + sizeof size;
}

static pimage *cached_raster(aqua_call *call, processing *p, const char *text, size_t length, const char *font, float size) {
    char stack[1024];
    size_t capacity = length + strlen(font) + 2 + sizeof size;
    char *key = capacity <= sizeof stack ? stack : malloc(capacity);
    size_t key_length = cache_key(key, capacity, text, length, font, size);
    cached *entry = gfx_map_get(&p->text_cache, key, key_length);
    if (entry && !entry->image->disposed) {
        entry->last_frame = p->text_frame;
        if (key != stack) free(key);
        return entry->image;
    }
    int w, h;
    bool platform = line_box(font, size, text, length, &w, &h);
    if (w > 8192 || h > 8192) { if (key != stack) free(key); gfx_fail(call, "Rendered text is too large."); return NULL; }
    uint8_t *alpha = calloc((size_t)w * (size_t)h, 1);
    if (!platform || !gfx_platform_text_raster(font, size, text, length, w, h, alpha)) bitmap_raster(size, text, length, w, h, alpha);
    pimage *image = gfx_image_detached(p, w, h);
    for (size_t i = 0, n = (size_t)w * (size_t)h; i < n; i++) {
        image->bytes[i * 4] = image->bytes[i * 4 + 1] = image->bytes[i * 4 + 2] = 255;
        image->bytes[i * 4 + 3] = alpha[i];
    }
    free(alpha);
    if (entry) { p->text_bytes -= (size_t)entry->image->width * (size_t)entry->image->height * 4; gfx_image_free(entry->image); free(entry); }
    entry = malloc(sizeof *entry);
    entry->image = image;
    entry->last_frame = p->text_frame;
    gfx_map_put(&p->text_cache, key, key_length, entry);
    p->text_bytes += (size_t)w * (size_t)h * 4;
    if (key != stack) free(key);
    return image;
}

typedef struct eviction { gfx_map_entry *items; size_t count, capacity; } eviction;
static void collect(const gfx_map_entry *entry, void *data) {
    eviction *e = data;
    GFX_PUSH(e->items, e->count, e->capacity, *entry);
}
static int by_age(const void *a, const void *b) {
    int64_t x = ((const cached *)((const gfx_map_entry *)a)->value)->last_frame, y = ((const cached *)((const gfx_map_entry *)b)->value)->last_frame;
    return x < y ? -1 : x > y;
}
static void evict(processing *p, const gfx_map_entry *e) {
    cached *entry = e->value;
    p->text_bytes -= (size_t)entry->image->width * (size_t)entry->image->height * 4;
    gfx_image_free(entry->image);
    free(entry);
    gfx_map_remove(&p->text_cache, e->key, e->length);
}

/* After presenting a frame: entries drawn during the frame that just ended are never evicted. */
void gfx_text_end_frame(processing *p) {
    eviction e = {0};
    gfx_map_each(&p->text_cache, collect, &e);
    size_t kept = 0;
    for (size_t i = 0; i < e.count; i++) {
        cached *entry = e.items[i].value;
        if (p->text_frame - entry->last_frame > CACHE_IDLE_FRAMES || entry->image->disposed) evict(p, &e.items[i]);
        else e.items[kept++] = e.items[i];
    }
    if (p->text_cache.count > CACHE_ENTRIES || p->text_bytes > CACHE_BYTES) {
        qsort(e.items, kept, sizeof *e.items, by_age);
        for (size_t i = 0; i < kept && (p->text_cache.count > CACHE_ENTRIES || p->text_bytes > CACHE_BYTES); i++)
            if (((cached *)e.items[i].value)->last_frame < p->text_frame) evict(p, &e.items[i]);
    }
    free(e.items);
    p->text_frame++;
}

void gfx_text_clear(processing *p) {
    eviction e = {0};
    gfx_map_each(&p->text_cache, collect, &e);
    for (size_t i = 0; i < e.count; i++) { cached *entry = e.items[i].value; gfx_image_free(entry->image); free(entry); }
    free(e.items);
    gfx_map_clear(&p->text_cache);
    p->text_bytes = 0;
}

/* ---- Placement ---- */

/* How many device pixels one logical unit spans at (x, y), clamped to 1..8. */
static float raster_scale(const canvas *c, float x, float y) {
    mat4 m = gfx_canvas_mvp(c);
    vec4 o = gfx_v4_transform((vec4){x, y, 0, 1}, m), dx = gfx_v4_transform((vec4){x + 1, y, 0, 1}, m), dy = gfx_v4_transform((vec4){x, y + 1, 0, 1}, m);
    float ox = o.x / o.w * (float)c->pixel_width / 2, oy = o.y / o.w * (float)c->pixel_height / 2;
    float ax = dx.x / dx.w * (float)c->pixel_width / 2 - ox, ay = dx.y / dx.w * (float)c->pixel_height / 2 - oy;
    float bx = dy.x / dy.w * (float)c->pixel_width / 2 - ox, by = dy.y / dy.w * (float)c->pixel_height / 2 - oy;
    float scale = fmaxf(sqrtf(ax * ax + ay * ay), sqrtf(bx * bx + by * by));
    if (!isfinite(scale)) return 1;
    float rounded = (float)(rint((double)scale * 1000) / 1000); /* MathF.Round(scale, 3) */
    return rounded < 1 ? 1 : rounded > 8 ? 8 : rounded;
}

/* Under 2D translate/scale transforms, snaps the line's corner to a whole device pixel and maps a raster made at the
   projected scale 1:1 onto device pixels; rotated, sheared and 3D text keeps the requested geometry. */
static void align_to_pixels(const canvas *c, float *x, float *y, float *w, float *h, int pixel_w, int pixel_h, float density) {
    if (c->is3d || c->pixel_width <= 0 || c->pixel_height <= 0) return;
    mat4 m = gfx_canvas_mvp(c);
#define PIXEL(px, py, ox, oy) do { vec4 q = gfx_v4_transform((vec4){(px), (py), 0, 1}, m); ox = (q.x / q.w + 1) * (float)c->pixel_width / 2; oy = (1 - q.y / q.w) * (float)c->pixel_height / 2; } while (0)
    float origin_x, origin_y, ex_x, ex_y, ey_x, ey_y;
    PIXEL(*x, *y, origin_x, origin_y);
    PIXEL(*x + 1, *y, ex_x, ex_y);
    PIXEL(*x, *y + 1, ey_x, ey_y);
#undef PIXEL
    ex_x -= origin_x; ex_y -= origin_y; ey_x -= origin_x; ey_y -= origin_y;
    if (!isfinite(origin_x + origin_y + ex_x + ex_y + ey_x + ey_y) || fabsf(ex_y) > 1e-3f || fabsf(ey_x) > 1e-3f || fabsf(ex_x) < 1e-4f || fabsf(ey_y) < 1e-4f) return;
    float sx = ex_x, sy = ey_y;
    float dx = rintf(origin_x) - origin_x, dy = rintf(origin_y) - origin_y;
    float snapped_x = fabsf(dx) < 1e-3f ? *x : *x + dx / sx, snapped_y = fabsf(dy) < 1e-3f ? *y : *y + dy / sy;
    if (fabsf(fabsf(sx) - density) < 0.01f && fabsf(fabsf(sy) - density) < 0.01f) {
        float exact_w = (float)pixel_w / fabsf(sx), exact_h = (float)pixel_h / fabsf(sy);
        /* The raster's own size keeps text crisp while it agrees with the measured box (within the box's overhang).
           Fonts with whole-pixel advances (細明體 at small sizes) render wider at screen scale than at the measured
           size; that text is fitted to its box, so what follows it and carets from textWidth() stay in place. */
        if (fabsf(exact_w - *w) * fabsf(sx) >= 0.01f && fabsf(exact_w - *w) <= 2.0f) *w = exact_w;
        if (fabsf(exact_h - *h) * fabsf(sy) >= 0.01f && fabsf(exact_h - *h) <= 2.0f) *h = exact_h;
    }
    *x = snapped_x; *y = snapped_y;
}

/* ---- Members ---- */

static void p_textSize(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!gfx_canvas_live(call, c)) return;
    float size = gfx_positive(call, 0);
    if (aqua_failed(call)) return;
    if (size > 512) { gfx_fail(call, "Text size must be <=512."); return; }
    c->style.text_size = size;
    c->style.leading = size * 1.2f;
}
static void p_textLeading(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!gfx_canvas_live(call, c)) return;
    float leading = gfx_positive(call, 0);
    if (!aqua_failed(call)) c->style.leading = leading;
}
static void p_textAlign(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!gfx_canvas_live(call, c)) return;
    int horizontal = GFX_CHOICE(call, 0, 0, 1, 2);
    int vertical = aqua_argc(call) == 2 ? GFX_CHOICE(call, 1, 2, 101, 102, 103) : c->style.text_vertical;
    if (aqua_failed(call)) return;
    c->style.text_align = horizontal;
    c->style.text_vertical = vertical;
}
static void p_textFont(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c)) return;
    aqua_value font = aqua_arg(call, 0);
    style s = c->style;
    if (font.type == AQUA_STRING) {
        aqua_str name = aqua_arg_str(call, 0);
        snprintf(s.font, sizeof s.font, "%s", name.data);
    } else {
        font_object *f = aqua_object_data(p->rt, font, gfx_font_class);
        if (!f) { gfx_fail(call, "Expected createFont() result or system font name."); return; }
        snprintf(s.font, sizeof s.font, "%s", f->name);
        s.text_size = f->size;
    }
    if (aqua_argc(call) == 2) s.text_size = gfx_positive(call, 1);
    if (aqua_failed(call)) return;
    if (s.text_size > 512) { gfx_fail(call, "Text size must be <=512."); return; }
    s.leading = s.text_size * 1.2f;
    c->style = s;
}
static void p_textWidth(aqua_call *call, processing *p, canvas *c) {
    aqua_runtime *rt = p->rt;
    aqua_value value = aqua_arg(call, 0);
    char *text = value.type == AQUA_STRING ? aqua_text_dup(rt, value, NULL) : aqua_display(rt, value);
    size_t length = strlen(text);
    int widest = 0;
    bool any = false;
    for (size_t start = 0; start <= length;) {
        size_t end = start;
        while (end < length && text[end] != '\n') end++;
        size_t line_end = end;
        /* "\r" characters are removed from lines */
        char *line = malloc(line_end - start + 1);
        size_t n = 0;
        for (size_t i = start; i < line_end; i++) if (text[i] != '\r') line[n++] = text[i];
        line[n] = 0;
        int w, h;
        bool ok = measure(call, c->style.font, c->style.text_size, line, n, &w, &h);
        free(line);
        if (!ok) { aqua_free(text); return; }
        if (!any || w > widest) widest = w;
        any = true;
        start = end + 1;
    }
    aqua_free(text);
    aqua_return_double(call, widest);
}
static void p_textAscent(aqua_call *call, processing *p, canvas *c) { (void)p; aqua_return_double(call, (double)c->style.text_size * .8); }
static void p_textDescent(aqua_call *call, processing *p, canvas *c) { (void)p; aqua_return_double(call, (double)c->style.text_size * .2); }

typedef struct line { char *text; size_t length; } line;

static void add_line(line **lines, size_t *count, size_t *capacity, const char *text, size_t length) {
    char *copy = malloc(length + 1);
    memcpy(copy, text, length);
    copy[length] = 0;
    line l = {copy, length};
    GFX_PUSH(*lines, *count, *capacity, l);
}

static void p_text(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c) || !gfx_require_sketch(call, p) || !gfx_canvas_require_drawing(call, c)) return;
    if (!c->style.has_fill) return;
    uint32_t n = aqua_argc(call);
    float x = gfx_float(call, 1), y = gfx_float(call, 2);
    float box_w = n == 5 ? gfx_positive(call, 3) : 0, box_h = n == 5 ? gfx_positive(call, 4) : 0;
    float z = n == 4 ? gfx_float(call, 3) : 0;
    if (aqua_failed(call)) return;
    bool boxed = n == 5;
    aqua_value value = aqua_arg(call, 0);
    char *text = value.type == AQUA_STRING ? aqua_text_dup(p->rt, value, NULL) : aqua_display(p->rt, value);
    /* Lines: "\r" removed, split at "\n", wrapped at spaces inside a box. */
    size_t length = 0;
    for (size_t i = 0; text[i]; i++) if (text[i] != '\r') text[length++] = text[i];
    text[length] = 0;
    line *lines = NULL;
    size_t count = 0, capacity = 0;
    bool ok = true;
    for (size_t start = 0; start <= length && ok;) {
        size_t end = start;
        while (end < length && text[end] != '\n') end++;
        if (!boxed) add_line(&lines, &count, &capacity, text + start, end - start);
        else {
            /* Greedy wrapping by words (split on single spaces, like String.Split(' ')). */
            char *current = malloc(end - start + 1);
            size_t current_length = 0;
            for (size_t w = start; w <= end && ok;) {
                size_t word_end = w;
                while (word_end < end && text[word_end] != ' ') word_end++;
                size_t candidate_length = current_length ? current_length + 1 + (word_end - w) : word_end - w;
                char *candidate = malloc(candidate_length + 1);
                if (current_length) { memcpy(candidate, current, current_length); candidate[current_length] = ' '; memcpy(candidate + current_length + 1, text + w, word_end - w); }
                else memcpy(candidate, text + w, word_end - w);
                candidate[candidate_length] = 0;
                int cw = 0, ch = 0;
                if (current_length > 0) ok = measure(call, c->style.font, c->style.text_size, candidate, candidate_length, &cw, &ch);
                if (ok && current_length > 0 && (float)cw > box_w) {
                    add_line(&lines, &count, &capacity, current, current_length);
                    memcpy(current, text + w, word_end - w);
                    current_length = word_end - w;
                } else {
                    char *grown = realloc(current, candidate_length + 1);
                    current = grown;
                    memcpy(current, candidate, candidate_length);
                    current_length = candidate_length;
                }
                free(candidate);
                w = word_end + 1;
            }
            if (ok) add_line(&lines, &count, &capacity, current, current_length);
            free(current);
        }
        start = end + 1;
    }
    float total = (float)(count ? count - 1 : 0) * c->style.leading + c->style.text_size;
    int vertical = c->style.text_vertical;
    float top = boxed ? y : vertical == 101 ? y : vertical == 102 ? y - total : vertical == 2 ? y - total / 2 : y - c->style.text_size * 0.8f;
    mat4 old_model = c->model;
    if (n == 4) c->model = gfx_m4_multiply(gfx_m4_translation(0, 0, z), c->model);
    for (size_t i = 0; ok && i < count; i++) {
        float line_y = top + (float)i * c->style.leading;
        if (boxed && (float)i * c->style.leading + c->style.text_size > box_h) break;
        if (lines[i].length == 0) continue;
        int w, h;
        if (!(ok = measure(call, c->style.font, c->style.text_size, lines[i].text, lines[i].length, &w, &h))) break;
        float density = raster_scale(c, x, line_y);
        pimage *image = cached_raster(call, p, lines[i].text, lines[i].length, c->style.font, c->style.text_size * density);
        if (!(ok = image != NULL)) break;
        style saved = c->style;
        c->style.tint = saved.fill;
        c->style.image_mode = 0;
        int align = c->style.text_align;
        float left = boxed ? x + (align == 2 ? (box_w - (float)w) / 2 : align == 1 ? box_w - (float)w : 0)
                           : x - (align == 2 ? (float)w / 2.0f : align == 1 ? (float)w : 0);
        float draw_x = left, draw_y = line_y, draw_w = (float)w, draw_h = (float)h;
        align_to_pixels(c, &draw_x, &draw_y, &draw_w, &draw_h, image->width, image->height, density);
        ok = gfx_canvas_image(call, c, image, draw_x, draw_y, &draw_w, &draw_h);
        c->style = saved;
    }
    c->model = old_model;
    for (size_t i = 0; i < count; i++) free(lines[i].text);
    free(lines);
    aqua_free(text);
}

static void p_createFont(aqua_call *call, processing *p) {
    aqua_str name = gfx_text(call, 0);
    float size = gfx_positive(call, 1);
    if (aqua_failed(call)) return;
    font_object *f = calloc(1, sizeof *f);
    snprintf(f->name, sizeof f->name, "%s", name.data);
    f->size = size;
    aqua_return(call, aqua_object_new(p->rt, gfx_font_class, f));
}

static void font_name(aqua_call *call) { aqua_return_cstring(call, ((font_object *)aqua_self(call))->name); }
static void font_size(aqua_call *call) { aqua_return(call, aqua_float(((font_object *)aqua_self(call))->size)); }

#define TEXT_CANVAS(X) \
    X(textSize, "textSize|文字大小", 1, 1) X(textLeading, "textLeading|文字行距", 1, 1) X(textAlign, "textAlign|文字對齊", 1, 2) \
    X(textFont, "textFont|文字字型", 1, 2) X(textWidth, "textWidth|文字寬度", 1, 1) X(textAscent, "textAscent|文字上緣高度", 0, 0) \
    X(textDescent, "textDescent|文字下緣高度", 0, 0) X(text, "text|文字", 3, 5)
TEXT_CANVAS(CANVAS_WRAPPERS)
#define TEXT_MODULE(X) X(createFont, "createFont|建立字型", 2, 2)
TEXT_MODULE(MODULE_WRAPPER)

void gfx_text_register(aqua_class *module, aqua_class *canvas, aqua_class *font) {
    TEXT_CANVAS(CANVAS_DEFINE)
    TEXT_MODULE(MODULE_DEFINE)
    aqua_define_property(font, "name", font_name);
    aqua_define_property(font, "size", font_size);
}

/* Fonts.Measure and Fonts.Selection measure with the same layer (modules/fonts.c). */
bool gfx_text_measure_line(const char *font, float size, const char *text, size_t length, int *width, int *height) {
    return line_box(font, size, text, length, width, height);
}
