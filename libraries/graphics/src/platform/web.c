/* Web text through the Canvas 2D API (web-glue/canvas): the glue measures a line and draws it white into a canvas whose
   alpha it copies out. The CSS font string, the box (advance plus 2 pixels of overhang, font ascent to descent),
   availability by fallback comparison and parsing the installed-font list happen here. */
#include "platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* metrics: advance width, font ascent, font descent. */
AQUA_WEB_IMPORT(canvas_measure_text) int32_t web_measure_text(const char *font, int32_t font_length, const char *text, int32_t text_length, double *metrics);
AQUA_WEB_IMPORT(canvas_draw_text) int32_t web_draw_text(const char *font, int32_t font_length, const char *text, int32_t text_length,
                                                        int32_t width, int32_t height, double x, double baseline, uint8_t *alpha);
/* Settles with "family\tstyle\n..." (malloc'd; freed here) or ok = 0 and the error message. */
typedef void (*web_fonts_done)(void *data, char *list, int32_t length, int32_t ok);
AQUA_WEB_IMPORT(canvas_query_fonts) void web_query_fonts(web_fonts_done done, void *data);

static bool generic(const char *family) {
    return strcmp(family, "sans-serif") == 0 || strcmp(family, "serif") == 0 || strcmp(family, "monospace") == 0;
}

/* `<size>px <family>[, <fallback>]`, quoting named families as CSS strings. */
static int css_font(char *out, size_t size, const char *family, float pixels, const char *fallback) {
    int n = snprintf(out, size, "%gpx ", pixels);
    if (generic(family)) n += snprintf(out + n, size - (size_t)n, "%s", family);
    else {
        out[n++] = '"';
        for (const char *c = family; *c && (size_t)n + 3 < size; c++) {
            if (*c == '"' || *c == '\\') out[n++] = '\\';
            out[n++] = *c;
        }
        out[n++] = '"';
        out[n] = 0;
    }
    if (fallback) n += snprintf(out + n, size - (size_t)n, ", %s", fallback);
    return n;
}

static bool measure(const char *family, float size, const char *fallback, const char *text, size_t length, double metrics[3]) {
    char font[600];
    int n = css_font(font, sizeof font, family, size, fallback);
    return web_measure_text(font, n, text, (int32_t)length, metrics) != 0;
}

bool gfx_platform_text_box(const char *family, float size, const char *text, size_t length, int *width, int *height) {
    double m[3];
    if (!measure(family, size, NULL, text, length, m)) return false;
    int w = (int)ceil(m[0]) + 2, h = (int)ceil(m[1] + m[2]);
    *width = w > 1 ? w : 1;
    *height = h > 1 ? h : 1;
    return true;
}

bool gfx_platform_text_raster(const char *family, float size, const char *text, size_t length, int width, int height, uint8_t *alpha) {
    double m[3];
    if (!measure(family, size, NULL, text, length, m)) return false;
    char font[600];
    int n = css_font(font, sizeof font, family, size, NULL);
    return web_draw_text(font, n, text, (int32_t)length, width, height, 0, m[1], alpha) != 0;
}

int gfx_platform_text_padding(void) { return 2; }

/* A family is installed when it changes a sample's width against every generic fallback. */
bool gfx_platform_font_available(const char *family) {
    if (generic(family)) return true;
    static const char sample[] = "mmmmmmwwWW\xe6\xbc\xa2\xe5\xad\x97";
    static const char *const fallbacks[] = {"monospace", "serif", "sans-serif"};
    for (int i = 0; i < 3; i++) {
        double with[3], without[3];
        if (!measure(family, 48, fallbacks[i], sample, sizeof sample - 1, with) || !measure(fallbacks[i], 48, NULL, sample, sizeof sample - 1, without)) return false;
        if (with[0] != without[0]) return true;
    }
    return false;
}

typedef struct query { platform_font_add add; void (*done)(void *data, const char *error); void *data; } query;

static void fonts_listed(void *data, char *list, int32_t length, int32_t ok) {
    query *q = data;
    if (!ok) { q->done(q->data, list && *list ? list : "Local font enumeration unavailable"); free(list); free(q); return; }
    for (char *line = list; line && line < list + length;) {
        char *end = memchr(line, '\n', (size_t)(list + length - line));
        if (!end) end = list + length;
        *end = 0;
        char *tab = strchr(line, '\t');
        if (tab) { *tab = 0; q->add(q->data, line, tab + 1); }
        line = end + 1;
    }
    free(list);
    q->done(q->data, NULL);
    free(q);
}

void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data) {
    query *q = malloc(sizeof *q);
    q->add = add; q->done = done; q->data = data;
    web_query_fonts(fonts_listed, q);
}
