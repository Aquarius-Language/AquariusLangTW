/* Linux text through Pango and Cairo, as the earlier desktop measured and drew it: Pango lays the line out with
   Fontconfig's fonts and fallback (CJK, Arabic, combining marks), the box is the union of its ink and logical extents,
   and Cairo draws it into an A8-equivalent mask. The libraries are loaded when first needed; without them text uses the
   built-in bitmap font. */
#include "platform.h"
#include <dlfcn.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

typedef struct pango_rectangle { int x, y, width, height; } pango_rectangle;

/* The functions used: library, return type, name, parameters. */
#define TEXT_FUNCTIONS(X) \
    X(cairo, void *, cairo_image_surface_create, (int format, int width, int height)) \
    X(cairo, int, cairo_surface_status, (void *surface)) \
    X(cairo, void, cairo_surface_destroy, (void *surface)) \
    X(cairo, void *, cairo_create, (void *surface)) \
    X(cairo, int, cairo_status, (void *context)) \
    X(cairo, void, cairo_destroy, (void *context)) \
    X(cairo, void, cairo_set_source_rgba, (void *context, double r, double g, double b, double a)) \
    X(cairo, void, cairo_move_to, (void *context, double x, double y)) \
    X(cairo, void, cairo_surface_flush, (void *surface)) \
    X(cairo, int, cairo_image_surface_get_stride, (void *surface)) \
    X(cairo, unsigned char *, cairo_image_surface_get_data, (void *surface)) \
    X(pango_cairo, void *, pango_cairo_create_layout, (void *context)) \
    X(pango_cairo, void, pango_cairo_show_layout, (void *context, void *layout)) \
    X(pango_cairo, void *, pango_cairo_font_map_get_default, (void)) \
    X(pango, void *, pango_font_description_new, (void)) \
    X(pango, void, pango_font_description_free, (void *description)) \
    X(pango, void, pango_font_description_set_family, (void *description, const char *family)) \
    X(pango, void, pango_font_description_set_absolute_size, (void *description, double size)) \
    X(pango, void, pango_layout_set_font_description, (void *layout, void *description)) \
    X(pango, void, pango_layout_set_text, (void *layout, const char *text, int length)) \
    X(pango, void, pango_layout_get_pixel_extents, (void *layout, pango_rectangle *ink, pango_rectangle *logical)) \
    X(pango, void, pango_font_map_list_families, (void *map, void ***families, int *count)) \
    X(pango, const char *, pango_font_family_get_name, (void *family)) \
    X(pango, void, pango_font_family_list_faces, (void *family, void ***faces, int *count)) \
    X(pango, const char *, pango_font_face_get_face_name, (void *face)) \
    X(gobject, void, g_object_unref, (void *object)) \
    X(glib, void, g_free, (void *memory))

static struct {
#define FIELD(library, result, name, parameters) result (*name) parameters;
    TEXT_FUNCTIONS(FIELD)
#undef FIELD
} api;
static int loaded; /* 0 not tried, 1 ready, -1 unavailable */

static bool load(void) {
    if (loaded) return loaded > 0;
    loaded = -1;
    void *cairo = dlopen("libcairo.so.2", RTLD_NOW), *pango = dlopen("libpango-1.0.so.0", RTLD_NOW);
    void *pango_cairo = dlopen("libpangocairo-1.0.so.0", RTLD_NOW), *gobject = dlopen("libgobject-2.0.so.0", RTLD_NOW);
    void *glib = dlopen("libglib-2.0.so.0", RTLD_NOW);
    if (!cairo || !pango || !pango_cairo || !gobject || !glib) return false;
#define RESOLVE(library, result, name, parameters) \
    if (!(*(void **)&api.name = dlsym(library, #name))) return false;
    TEXT_FUNCTIONS(RESOLVE)
#undef RESOLVE
    loaded = 1;
    return true;
}

typedef struct layout { void *surface, *context, *description, *handle; int x, y, width, height; } layout;

static void layout_free(layout *l) {
    if (l->handle) api.g_object_unref(l->handle);
    if (l->description) api.pango_font_description_free(l->description);
    if (l->context) api.cairo_destroy(l->context);
    if (l->surface) api.cairo_surface_destroy(l->surface);
}

static bool layout_make(layout *l, const char *font, float size, const char *text, size_t length) {
    memset(l, 0, sizeof *l);
    if (!load() || length > 4096 * 4) return false;
    l->surface = api.cairo_image_surface_create(0, 1, 1);
    l->context = api.cairo_create(l->surface);
    if (api.cairo_status(l->context) != 0) { layout_free(l); return false; }
    l->handle = api.pango_cairo_create_layout(l->context);
    l->description = api.pango_font_description_new();
    api.pango_font_description_set_family(l->description, font);
    api.pango_font_description_set_absolute_size(l->description, size * 1024.0);
    api.pango_layout_set_font_description(l->handle, l->description);
    api.pango_layout_set_text(l->handle, text, (int)length);
    pango_rectangle ink, logical;
    api.pango_layout_get_pixel_extents(l->handle, &ink, &logical);
    l->x = ink.x < logical.x ? ink.x : logical.x;
    l->y = ink.y < logical.y ? ink.y : logical.y;
    int right = ink.x + ink.width > logical.x + logical.width ? ink.x + ink.width : logical.x + logical.width;
    int bottom = ink.y + ink.height > logical.y + logical.height ? ink.y + ink.height : logical.y + logical.height;
    l->width = right - l->x > 1 ? right - l->x : 1;
    l->height = bottom - l->y > 1 ? bottom - l->y : 1;
    return true;
}

bool gfx_platform_text_box(const char *font, float size, const char *text, size_t length, int *width, int *height) {
    layout l;
    if (!layout_make(&l, font, size, text, length)) return false;
    *width = l.width;
    *height = l.height;
    layout_free(&l);
    return true;
}

bool gfx_platform_text_raster(const char *font, float size, const char *text, size_t length, int width, int height, uint8_t *alpha) {
    layout l;
    if (!layout_make(&l, font, size, text, length)) return false;
    void *surface = api.cairo_image_surface_create(0, width, height);
    bool ok = api.cairo_surface_status(surface) == 0;
    if (ok) {
        void *context = api.cairo_create(surface);
        api.cairo_set_source_rgba(context, 1, 1, 1, 1);
        api.cairo_move_to(context, -l.x, -l.y);
        api.pango_cairo_show_layout(context, l.handle);
        ok = api.cairo_status(context) == 0;
        api.cairo_surface_flush(surface);
        int stride = api.cairo_image_surface_get_stride(surface);
        const unsigned char *data = api.cairo_image_surface_get_data(surface);
        /* CAIRO_FORMAT_ARGB32 is native-endian: alpha is the most significant byte. */
        for (int y = 0; ok && y < height; y++)
            for (int x = 0; x < width; x++) {
                uint32_t pixel;
                memcpy(&pixel, data + (size_t)y * (size_t)stride + (size_t)x * 4, 4);
                alpha[(size_t)y * (size_t)width + (size_t)x] = (uint8_t)(pixel >> 24);
            }
        api.cairo_destroy(context);
    }
    api.cairo_surface_destroy(surface);
    layout_free(&l);
    return ok;
}

int gfx_platform_text_padding(void) { return 0; }

void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data) {
    if (!load()) { done(data, "System font enumeration adapter unavailable; existing bitmap fallback remains usable."); return; }
    void **families = NULL;
    int count = 0;
    api.pango_font_map_list_families(api.pango_cairo_font_map_get_default(), &families, &count);
    for (int i = 0; i < count; i++) {
        const char *family = api.pango_font_family_get_name(families[i]);
        void **faces = NULL;
        int face_count = 0;
        api.pango_font_family_list_faces(families[i], &faces, &face_count);
        for (int j = 0; j < face_count; j++) add(data, family, api.pango_font_face_get_face_name(faces[j]));
        api.g_free(faces);
    }
    api.g_free(families);
    done(data, NULL);
}

typedef struct match { const char *family; bool found; } match;
static void compare(void *data, const char *family, const char *style) {
    (void)style;
    match *m = data;
    if (strcasecmp(m->family, family) == 0) m->found = true;
}
static void finished(void *data, const char *error) { (void)data; (void)error; }

bool gfx_platform_font_available(const char *family) {
    if (strcmp(family, "sans-serif") == 0 || strcmp(family, "monospace") == 0) return true;
    match m = {family, false};
    gfx_platform_fonts(compare, finished, &m);
    return m.found;
}
