/* Fonts: the installed font faces and text measurement with the platform's text system (src/platform), the same one
   Processing draws text with. Measure and Selection return rectangles {x, y, width, height} in pixels at the given
   size; Selection gives one rectangle per line a UTF-16 range touches (lines advance by 1.2 x size). Failures read
   "<Member>: <Kind>: <message>" as the application libraries' do. */
#include "../graphics.h"
#include "../common/hashmap.h"
#include "../common/records.h"
#include "../common/unicode.h"
#include "../platform/platform.h"
#include "../processing/processing.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void fail_kind(aqua_call *call, const char *kind, const char *message) { gfx_fail(call, "%s: %s", kind, message); }

/* Numbers the way records carried them: integral values as integers. */
static aqua_value number(double n) {
    if (n == trunc(n) && n >= -2147483648.0 && n <= 2147483647.0) return aqua_int((int32_t)n);
    return aqua_double(n);
}

static aqua_value rectangle(aqua_runtime *rt, double x, double y, double width, double height) {
    static const char *const names[] = {"x", "y", "width", "height"};
    aqua_value values[4] = {number(x), number(y), number(width), number(height)};
    return gfx_record_of(rt, 4, names, values);
}

static bool text_argument(aqua_call *call, uint32_t index, aqua_str *out) {
    if (aqua_arg(call, index).type != AQUA_STRING) { fail_kind(call, "InvalidData", "Expected a string."); return false; }
    *out = aqua_arg_str(call, index);
    return true;
}

static bool number_argument(aqua_call *call, uint32_t index, double *out) {
    if (!gfx_finite_number(aqua_arg(call, index), out)) { fail_kind(call, "InvalidData", "Expected a finite number."); return false; }
    return true;
}

static bool integer_argument(aqua_call *call, uint32_t index, int32_t *out) {
    double n;
    if (!number_argument(call, index, &n)) return false;
    if (n != trunc(n)) { fail_kind(call, "InvalidData", "Expected an integer."); return false; }
    if (n < -2147483648.0 || n > 2147483647.0) { fail_kind(call, "InvalidData", "Arithmetic operation resulted in an overflow."); return false; }
    *out = (int32_t)n;
    return true;
}

/* The width and height of one line, as Processing's textWidth sees it without the platform box's overhang. */
static bool measure(aqua_call *call, const char *text, size_t length, const char *family, double size, int *width, int *height) {
    if (!isfinite(size) || size <= 0 || size > 512) { fail_kind(call, "InvalidData", "Font size must be 0..512."); return false; }
    int w = 0, h = 0;
    bool platform = gfx_text_measure_line(family, (float)size, text, length, &w, &h);
    *width = length == 0 ? 0 : w - (platform ? gfx_platform_text_padding() : 0);
    *height = h;
    return true;
}

static void fonts_Measure(aqua_call *call) {
    aqua_str text, family;
    double size;
    if (!text_argument(call, 0, &text) || !text_argument(call, 1, &family) || !number_argument(call, 2, &size)) return;
    int width, height;
    if (measure(call, text.data, text.length, family.data, size, &width, &height))
        aqua_return(call, rectangle(aqua_call_runtime(call), 0, 0, width, height));
}

/* Byte offset of UTF-16 position `units` in `text`; *splits when it falls between a surrogate pair's units. */
static size_t byte_offset(const char *text, size_t length, size_t units, bool *splits) {
    size_t i = 0, u = 0;
    *splits = false;
    while (i < length && u < units) {
        size_t start = i;
        uint32_t c = gfx_utf8_next(text, length, &i);
        u += utf16_units(c);
        if (u > units) { *splits = true; return start; }
    }
    return i;
}

static bool position_valid(aqua_call *call, const char *text, size_t length, size_t total, int32_t position) {
    if (position < 0 || (size_t)position > total) { fail_kind(call, "InvalidData", "Specified argument was out of the range of valid values. (Parameter 'position')"); return false; }
    bool splits;
    byte_offset(text, length, (size_t)position, &splits);
    if (splits) { fail_kind(call, "InvalidData", "Position splits a Unicode scalar."); return false; }
    if (!gfx_grapheme_boundary_utf16(text, length, (size_t)position)) { fail_kind(call, "InvalidData", "Position splits a grapheme."); return false; }
    return true;
}

static void fonts_Selection(aqua_call *call) {
    aqua_str text, family;
    int32_t start, end;
    double size;
    if (!text_argument(call, 0, &text) || !integer_argument(call, 1, &start) || !integer_argument(call, 2, &end) ||
        !text_argument(call, 3, &family) || !number_argument(call, 4, &size)) return;
    size_t total = 0;
    for (size_t i = 0; i < text.length;) total += utf16_units(gfx_utf8_next(text.data, text.length, &i));
    if (!position_valid(call, text.data, text.length, total, start) || !position_valid(call, text.data, text.length, total, end)) return;
    if (end < start) { fail_kind(call, "InvalidData", "Selection end precedes start."); return; }
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value result = aqua_array_new(rt, 0);
    int64_t offset = 0;
    int line_index = 0;
    for (size_t line_start = 0;; line_index++) {
        const char *newline = memchr(text.data + line_start, '\n', text.length - line_start);
        size_t line_end = newline ? (size_t)(newline - text.data) : text.length;
        const char *line = text.data + line_start;
        size_t line_bytes = line_end - line_start, line_units = 0;
        for (size_t i = 0; i < line_bytes;) line_units += utf16_units(gfx_utf8_next(line, line_bytes, &i));
        if (start <= offset + (int64_t)line_units && end >= offset) {
            int64_t a = start - offset, b = end - offset;
            size_t first = (size_t)(a < 0 ? 0 : a > (int64_t)line_units ? (int64_t)line_units : a);
            size_t last = (size_t)(b < 0 ? 0 : b > (int64_t)line_units ? (int64_t)line_units : b);
            bool splits;
            int x, ignored, right;
            if (!measure(call, line, byte_offset(line, line_bytes, first, &splits), family.data, size, &x, &ignored) ||
                !measure(call, line, byte_offset(line, line_bytes, last, &splits), family.data, size, &right, &ignored)) return;
            aqua_array_push(rt, result, rectangle(rt, x, line_index * size * 1.2, right - x, size));
        }
        offset += (int64_t)line_units + 1;
        if (!newline) break;
        line_start = line_end + 1;
    }
    aqua_return(call, result);
}

static void fonts_Available(aqua_call *call) {
    aqua_str family;
    if (text_argument(call, 0, &family)) aqua_return_bool(call, gfx_platform_font_available(family.data));
}

/* Enumeration: unique faces sorted by family, then style. */
typedef struct face { char *family, *style; } face;
typedef struct enumeration { gfx_async *async; face *faces; size_t count, capacity; } enumeration;

static void add_face(void *data, const char *family, const char *style) {
    enumeration *e = data;
    if (e->count >= 4096) return;
    for (size_t i = 0; i < e->count; i++) if (strcmp(e->faces[i].family, family) == 0 && strcmp(e->faces[i].style, style) == 0) return;
    face f = {malloc(strlen(family) + 1), malloc(strlen(style) + 1)};
    strcpy(f.family, family);
    strcpy(f.style, style);
    GFX_PUSH(e->faces, e->count, e->capacity, f);
}

static int compare_faces(const void *a, const void *b) {
    const face *x = a, *y = b;
    int c = strcmp(x->family, y->family);
    return c ? c : strcmp(x->style, y->style);
}

static void enumerated(void *data, const char *error) {
    enumeration *e = data;
    gfx_async *a = e->async;
    if (error) gfx_async_fail(a, "Unsupported: %s", error);
    else {
        aqua_runtime *rt = gfx_async_runtime(a);
        qsort(e->faces, e->count, sizeof *e->faces, compare_faces);
        aqua_value array = aqua_array_new(rt, (uint32_t)e->count);
        static const char *const names[] = {"family", "style"};
        for (size_t i = 0; i < e->count; i++) {
            aqua_value values[2] = {aqua_cstring(rt, e->faces[i].family), aqua_cstring(rt, e->faces[i].style)};
            aqua_array_set(rt, array, (uint32_t)i, gfx_record_of(rt, 2, names, values));
        }
        gfx_async_finish(a, array);
    }
    for (size_t i = 0; i < e->count; i++) { free(e->faces[i].family); free(e->faces[i].style); }
    free(e->faces);
    free(e);
}

static void fonts_Enumerate(aqua_call *call) {
    enumeration *e = calloc(1, sizeof *e);
    e->async = gfx_async_begin(call, 0);
    gfx_async *a = e->async;
    gfx_platform_fonts(add_face, enumerated, e);
    gfx_async_end(a);
}

#define FONTS_MODULE(X) \
    X(fonts_Enumerate, "Enumerate|列舉", 0) X(fonts_Available, "Available|可用", 1) X(fonts_Measure, "Measure|測量", 3) \
    X(fonts_Selection, "Selection|選取範圍", 5)
#define WRAPPER(function, names, count) GFX_WRAP("", function, names, function)
FONTS_MODULE(WRAPPER)
#undef WRAPPER

void gfx_fonts_register(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "Fonts");
#define DEFINE(function, names, count) aqua_define_function(module, names, wrapper_##function, gfx_signature(count, count));
    FONTS_MODULE(DEFINE)
#undef DEFINE
}
