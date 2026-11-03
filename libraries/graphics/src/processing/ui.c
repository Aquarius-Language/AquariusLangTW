/* drawUI(list): draws a UI draw list on a Processing canvas in its current transform, and uiTextMetrics() measures
 * text the way drawUI draws it (for layout engines' text measurers). The list is the plain-data format the UI libraries
 * produce (libraries/saturn/README.md, "The UI draw list"): rounded rectangles, borders, shadows, gradients, lines,
 * text, images, triangle meshes and the textures they sample, nested clips, transforms and opacity. Everything is
 * drawn as unlit triangles through the canvas's batch, so a whole interface is a handful of GPU draws. */
#include "processing.h"
#include "../platform/platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum {
    OP_RECT = 1, OP_BORDER, OP_SHADOW, OP_TEXT, OP_IMAGE, OP_CLIP_PUSH, OP_CLIP_POP, OP_TRANSFORM_PUSH, OP_TRANSFORM_POP,
    OP_MESH, OP_TEXTURE, OP_LINE, OP_GRADIENT, OP_OPACITY_PUSH, OP_OPACITY_POP, OP_LIMIT
};
/* The floats after each op code. */
static const int op_sizes[OP_LIMIT] = {0, 12, 16, 14, 14, 17, 8, 0, 6, 0, 5, 8, 9, 20, 1, 0};

#define STACK_LIMIT 64
#define CORNER_POINTS 10

struct ui_texture { int id; pimage *image; };

typedef struct ui_list {
    float *commands, *vertices;
    int32_t *indices;
    uint32_t command_count, vertex_count, index_count;
    aqua_value strings, images;
} ui_list;

typedef struct ui_painter {
    aqua_call *call;
    processing *p;
    canvas *c;
    float opacity;
    mat4 models[STACK_LIMIT]; int model_depth;
    struct { bool has; float clip[4]; } clips[STACK_LIMIT]; int clip_depth;
    float opacities[STACK_LIMIT]; int opacity_depth;
    pvertex *scratch; size_t count, capacity;
} ui_painter;

/* ---- Textures (OP_TEXTURE) ---- */

static struct ui_texture *find_texture(processing *p, int id) {
    for (size_t i = 0; i < p->ui_texture_count; i++) if (p->ui_textures[i].id == id) return &p->ui_textures[i];
    return NULL;
}

void gfx_ui_clear(processing *p) {
    for (size_t i = 0; i < p->ui_texture_count; i++) gfx_image_free(p->ui_textures[i].image);
    free(p->ui_textures);
    p->ui_textures = NULL;
    p->ui_texture_count = p->ui_texture_capacity = 0;
}

static bool texture_op(ui_painter *u, const ui_list *l, const float *a) {
    processing *p = u->p;
    int id = (int)a[0], width = (int)a[1], height = (int)a[2], source = (int)a[3];
    struct ui_texture *t = find_texture(p, id);
    if (source < 0) {
        if (t) { gfx_image_free(t->image); *t = p->ui_textures[--p->ui_texture_count]; }
        return true;
    }
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) { gfx_fail(u->call, "drawUI: texture %d has an invalid size.", id); return false; }
    if (t && (t->image->width != width || t->image->height != height)) { gfx_image_free(t->image); t->image = NULL; }
    if (!t) {
        if (p->ui_texture_count == p->ui_texture_capacity) {
            size_t capacity = p->ui_texture_capacity ? p->ui_texture_capacity * 2 : 8;
            struct ui_texture *grown = realloc(p->ui_textures, capacity * sizeof *grown);
            if (!grown) { gfx_fail(u->call, "drawUI: out of memory."); return false; }
            p->ui_textures = grown;
            p->ui_texture_capacity = capacity;
        }
        t = &p->ui_textures[p->ui_texture_count++];
        t->id = id;
        t->image = NULL;
    }
    if (!t->image && !(t->image = gfx_image_detached(p, width, height))) { p->ui_texture_count--; gfx_fail(u->call, "drawUI: texture %d is too large.", id); return false; }
    int x = (int)a[4], y = (int)a[5], w = (int)a[6], h = (int)a[7];
    if (x < 0 || y < 0 || w < 0 || h < 0 || x + w > width || y + h > height) { gfx_fail(u->call, "drawUI: texture %d update is outside it.", id); return false; }
    aqua_value bytes = aqua_array_get(p->rt, l->images, (uint32_t)source);
    size_t row = (size_t)w * 4;
    if (bytes.type != AQUA_ARRAY || aqua_array_length(p->rt, bytes) != row * (size_t)h) {
        gfx_fail(u->call, "drawUI: texture %d needs %d x %d RGBA bytes.", id, w, h);
        return false;
    }
    uint8_t *pixels = malloc(row * (size_t)h + 1);
    if (!pixels || !aqua_array_read_u8(p->rt, bytes, 0, (uint32_t)(row * (size_t)h), pixels)) {
        free(pixels);
        gfx_fail(u->call, "drawUI: texture bytes must be integers 0..255.");
        return false;
    }
    for (int r = 0; r < h; r++) memcpy(t->image->bytes + ((size_t)(y + r) * (size_t)width + (size_t)x) * 4, pixels + (size_t)r * row, row);
    free(pixels);
    t->image->dirty = true;
    return true;
}

/* ---- Triangles ---- */

static vec4 color_of(const ui_painter *u, const float *rgba) {
    return (vec4){rgba[0] / 255.0f, rgba[1] / 255.0f, rgba[2] / 255.0f, rgba[3] / 255.0f * u->opacity};
}

static bool put(ui_painter *u, float x, float y, float s, float t, vec4 color) {
    if (u->count == u->capacity) {
        size_t capacity = u->capacity ? u->capacity * 2 : 1024;
        pvertex *grown = realloc(u->scratch, capacity * sizeof *grown);
        if (!grown) { gfx_fail(u->call, "drawUI: out of memory."); return false; }
        u->scratch = grown;
        u->capacity = capacity;
    }
    u->scratch[u->count++] = gfx_canvas_vertex(u->c, v3(x, y, 0), color, (vec2){s, t});
    return true;
}

static bool flush(ui_painter *u, pimage *image) {
    if (!u->count) return true;
    bool ok = gfx_canvas_draw(u->call, u->c, u->scratch, u->count, image, false, 0);
    u->count = 0;
    return ok;
}

/* The outline of a rounded rectangle, clockwise from the top-left corner, CORNER_POINTS points per corner. */
static void rounded(float x, float y, float w, float h, const float *radii, vec2 *out) {
    static const float starts[4] = {3.14159265f, 4.71238898f, 0, 1.57079633f};
    float limit = fmaxf(0, fminf(w, h) / 2);
    for (int corner = 0; corner < 4; corner++) {
        float r = fminf(fmaxf(radii[corner], 0), limit);
        float cx = corner == 0 || corner == 3 ? x + r : x + w - r, cy = corner < 2 ? y + r : y + h - r;
        for (int i = 0; i < CORNER_POINTS; i++) {
            float angle = starts[corner] + 1.57079633f * (float)i / (CORNER_POINTS - 1);
            out[corner * CORNER_POINTS + i] = (vec2){cx + r * cosf(angle), cy + r * sinf(angle)};
        }
    }
}

typedef vec4 (*shade)(const ui_painter *u, const float *a, float x, float y);

static vec4 flat(const ui_painter *u, const float *a, float x, float y) { (void)x; (void)y; return color_of(u, a); }

/* A filled rounded rectangle as a fan around its center; `paint` colors each vertex. */
static bool fill_rounded(ui_painter *u, float x, float y, float w, float h, const float *radii, shade paint, const float *a) {
    if (w <= 0 || h <= 0) return true;
    vec2 outline[4 * CORNER_POINTS];
    rounded(x, y, w, h, radii, outline);
    float cx = x + w / 2, cy = y + h / 2;
    vec4 center = paint(u, a, cx, cy);
    for (int i = 0; i < 4 * CORNER_POINTS; i++) {
        vec2 p0 = outline[i], p1 = outline[(i + 1) % (4 * CORNER_POINTS)];
        if (!put(u, cx, cy, 0, 0, center) || !put(u, p0.x, p0.y, 0, 0, paint(u, a, p0.x, p0.y)) || !put(u, p1.x, p1.y, 0, 0, paint(u, a, p1.x, p1.y)))
            return false;
    }
    return true;
}

/* The band between two outlines of the same point count, colored `outer_color` outside and `inner_color` inside. */
static bool ring(ui_painter *u, const vec2 *outer, const vec2 *inner, int count, vec4 outer_color, vec4 inner_color) {
    for (int i = 0; i < count; i++) {
        int j = (i + 1) % count;
        if (!put(u, outer[i].x, outer[i].y, 0, 0, outer_color) || !put(u, outer[j].x, outer[j].y, 0, 0, outer_color) ||
            !put(u, inner[j].x, inner[j].y, 0, 0, inner_color) || !put(u, outer[i].x, outer[i].y, 0, 0, outer_color) ||
            !put(u, inner[j].x, inner[j].y, 0, 0, inner_color) || !put(u, inner[i].x, inner[i].y, 0, 0, inner_color))
            return false;
    }
    return true;
}

static bool border_op(ui_painter *u, const float *a) {
    float x = a[0], y = a[1], w = a[2], h = a[3];
    const float *radii = a + 4, *widths = a + 8;
    if (w <= 0 || h <= 0) return true;
    vec2 outer[4 * CORNER_POINTS], inner[4 * CORNER_POINTS];
    rounded(x, y, w, h, radii, outer);
    float ix = x + widths[0], iy = y + widths[1], iw = w - widths[0] - widths[2], ih = h - widths[1] - widths[3];
    float inner_radii[4];
    for (int i = 0; i < 4; i++) {
        float side = i == 0 ? fmaxf(widths[0], widths[1]) : i == 1 ? fmaxf(widths[1], widths[2]) : i == 2 ? fmaxf(widths[2], widths[3]) : fmaxf(widths[3], widths[0]);
        inner_radii[i] = fmaxf(0, radii[i] - side);
    }
    if (iw <= 0 || ih <= 0) return fill_rounded(u, x, y, w, h, radii, flat, a + 12);
    rounded(ix, iy, iw, ih, inner_radii, inner);
    vec4 color = color_of(u, a + 12);
    return ring(u, outer, inner, 4 * CORNER_POINTS, color, color);
}

/* A blurred shadow: solid inside the box shrunk by half the blur, fading to nothing at the box grown by half of it. */
static bool shadow_op(ui_painter *u, const float *a) {
    float x = a[0], y = a[1], w = a[2], h = a[3], blur = fmaxf(0, a[8]);
    const float *radii = a + 4;
    if (blur <= 0) return fill_rounded(u, x, y, w, h, radii, flat, a + 10);
    float half = blur / 2, shrink = fminf(half, fminf(w, h) / 2 - 0.01f);
    float inner_radii[4], outer_radii[4];
    for (int i = 0; i < 4; i++) { inner_radii[i] = fmaxf(0, radii[i] - shrink); outer_radii[i] = radii[i] + half; }
    vec2 outer[4 * CORNER_POINTS], inner[4 * CORNER_POINTS];
    rounded(x + shrink, y + shrink, w - 2 * shrink, h - 2 * shrink, inner_radii, inner);
    rounded(x - half, y - half, w + 2 * half, h + 2 * half, outer_radii, outer);
    vec4 color = color_of(u, a + 10), clear = color;
    clear.w = 0;
    return fill_rounded(u, x + shrink, y + shrink, w - 2 * shrink, h - 2 * shrink, inner_radii, flat, a + 10) &&
           ring(u, outer, inner, 4 * CORNER_POINTS, clear, color);
}

static vec4 gradient(const ui_painter *u, const float *a, float x, float y) {
    float x1 = a[8], y1 = a[9], dx = a[10] - x1, dy = a[11] - y1, length = dx * dx + dy * dy;
    float t = length > 0 ? ((x - x1) * dx + (y - y1) * dy) / length : 0;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    vec4 from = color_of(u, a + 12), to = color_of(u, a + 16);
    return (vec4){from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t, from.z + (to.z - from.z) * t, from.w + (to.w - from.w) * t};
}

static bool line_op(ui_painter *u, const float *a) {
    float dx = a[2] - a[0], dy = a[3] - a[1], length = sqrtf(dx * dx + dy * dy), half = a[4] / 2;
    if (length <= 0 || half <= 0) return true;
    float nx = -dy / length * half, ny = dx / length * half;
    vec4 c = color_of(u, a + 5);
    return put(u, a[0] + nx, a[1] + ny, 0, 0, c) && put(u, a[2] + nx, a[3] + ny, 0, 0, c) && put(u, a[2] - nx, a[3] - ny, 0, 0, c) &&
           put(u, a[0] + nx, a[1] + ny, 0, 0, c) && put(u, a[2] - nx, a[3] - ny, 0, 0, c) && put(u, a[0] - nx, a[1] - ny, 0, 0, c);
}

static bool image_op(ui_painter *u, const ui_list *l, const float *a) {
    pimage *image = gfx_image_of_value(u->p, aqua_array_get(u->p->rt, l->images, (uint32_t)a[4]));
    if (!image) return true;   /* not a Processing image: another renderer's */
    float x = a[0], y = a[1], w = a[2], h = a[3], u0 = a[5], v0 = a[6], u1 = a[7], v1 = a[8];
    if (image->flipped) { v0 = 1 - v0; v1 = 1 - v1; }
    vec4 tint = color_of(u, a + 13);
    if (!flush(u, NULL)) return false;
    /* A rounded image is a fan whose texture coordinates follow the positions. */
    vec2 outline[4 * CORNER_POINTS];
    rounded(x, y, w, h, a + 9, outline);
    float cx = x + w / 2, cy = y + h / 2;
#define UV(px, py) (w > 0 ? u0 + (u1 - u0) * ((px) - x) / w : u0), (h > 0 ? v0 + (v1 - v0) * ((py) - y) / h : v0)
    for (int i = 0; i < 4 * CORNER_POINTS; i++) {
        vec2 p0 = outline[i], p1 = outline[(i + 1) % (4 * CORNER_POINTS)];
        if (!put(u, cx, cy, UV(cx, cy), tint) || !put(u, p0.x, p0.y, UV(p0.x, p0.y), tint) || !put(u, p1.x, p1.y, UV(p1.x, p1.y), tint)) return false;
    }
#undef UV
    return flush(u, image);
}

static bool mesh_op(ui_painter *u, const ui_list *l, const float *a) {
    uint32_t first_vertex = (uint32_t)a[0], vertex_count = (uint32_t)a[1], first_index = (uint32_t)a[2], index_count = (uint32_t)a[3];
    int texture = (int)a[4];
    if ((uint64_t)first_vertex + vertex_count > l->vertex_count / 8 || (uint64_t)first_index + index_count > l->index_count) {
        gfx_fail(u->call, "drawUI: a mesh refers outside the list's vertices.");
        return false;
    }
    pimage *image = NULL;
    if (texture >= 0) {
        struct ui_texture *t = find_texture(u->p, texture);
        image = t ? t->image : NULL;
    } else if (texture <= -2) image = gfx_image_of_value(u->p, aqua_array_get(u->p->rt, l->images, (uint32_t)(-texture - 2)));
    if (!flush(u, NULL)) return false;
    for (uint32_t i = 0; i < index_count; i++) {
        int32_t index = l->indices[first_index + i];
        if (index < 0 || (uint32_t)index >= vertex_count) { gfx_fail(u->call, "drawUI: a mesh index is outside its vertices."); return false; }
        const float *v = l->vertices + (size_t)(first_vertex + (uint32_t)index) * 8;
        if (!put(u, v[0], v[1], v[2], v[3], color_of(u, v + 4))) return false;
    }
    return flush(u, image);
}

/* ---- State ---- */

static bool push_clip(ui_painter *u, const float *a) {
    if (u->clip_depth == STACK_LIMIT) { gfx_fail(u->call, "drawUI: clips nest more than %d deep.", STACK_LIMIT); return false; }
    if (!flush(u, NULL)) return false;
    canvas *c = u->c;
    u->clips[u->clip_depth].has = c->has_clip;
    memcpy(u->clips[u->clip_depth].clip, c->clip, sizeof c->clip);
    u->clip_depth++;
    bool had = c->has_clip;
    float previous[4];
    memcpy(previous, c->clip, sizeof previous);
    gfx_canvas_set_clip(c, a[0], a[1], fmaxf(0, a[2]), fmaxf(0, a[3]));
    if (had) {
        float x0 = fmaxf(previous[0], c->clip[0]), y0 = fmaxf(previous[1], c->clip[1]);
        float x1 = fminf(previous[0] + previous[2], c->clip[0] + c->clip[2]), y1 = fminf(previous[1] + previous[3], c->clip[1] + c->clip[3]);
        c->clip[0] = x0; c->clip[1] = y0; c->clip[2] = fmaxf(0, x1 - x0); c->clip[3] = fmaxf(0, y1 - y0);
    }
    return true;
}
static bool pop_clip(ui_painter *u) {
    if (!u->clip_depth) return true;
    if (!flush(u, NULL)) return false;
    u->clip_depth--;
    u->c->has_clip = u->clips[u->clip_depth].has;
    memcpy(u->c->clip, u->clips[u->clip_depth].clip, sizeof u->c->clip);
    return true;
}
static bool push_transform(ui_painter *u, const float *a) {
    if (u->model_depth == STACK_LIMIT) { gfx_fail(u->call, "drawUI: transforms nest more than %d deep.", STACK_LIMIT); return false; }
    if (!flush(u, NULL)) return false;
    u->models[u->model_depth++] = u->c->model;
    /* Row vectors (System.Numerics): x' = a x + c y + e, y' = b x + d y + f. */
    mat4 m = gfx_m4_make(a[0], a[1], 0, 0, a[2], a[3], 0, 0, 0, 0, 1, 0, a[4], a[5], 0, 1);
    u->c->model = gfx_m4_multiply(m, u->c->model);
    return true;
}
static bool pop_transform(ui_painter *u) {
    if (!u->model_depth) return true;
    if (!flush(u, NULL)) return false;
    u->c->model = u->models[--u->model_depth];
    return true;
}

/* ---- Text ---- */

/* The canvas font name for a family, weight and slant ("Segoe UI:700:italic"; platform.h). */
static void font_name(char *out, size_t size, const char *family, int weight, bool italic) {
    char number[16] = "";
    if (weight != 400) snprintf(number, sizeof number, ":%d", weight);
    snprintf(out, size, "%s%s%s", family && *family ? family : "sans-serif", number, italic ? ":italic" : "");
}
static bool text_op(ui_painter *u, const ui_list *l, const float *a) {
    aqua_runtime *rt = u->p->rt;
    aqua_value text = aqua_array_get(rt, l->strings, (uint32_t)a[4]);
    if (text.type != AQUA_STRING) return true;
    char *family = NULL;
    if (a[5] >= 0) {
        aqua_value f = aqua_array_get(rt, l->strings, (uint32_t)a[5]);
        if (f.type == AQUA_STRING) family = aqua_text_dup(rt, f, NULL);
    }
    char font[300];
    font_name(font, sizeof font, family, (int)a[7], a[8] != 0);
    aqua_free(family);
    size_t length;
    char *utf8 = aqua_text_dup(rt, text, &length);
    bool ok = flush(u, NULL) && gfx_text_draw_line(u->call, u->p, u->c, font, a[6], utf8, length, a[0], a[1], color_of(u, a + 10));
    aqua_free(utf8);
    return ok;
}

/* uiTextMetrics(text, family, size | weight, italic) -> [width, ascent, descent, advance]: the box drawUI draws text in,
   and the advance to the text's end. */
static void p_uiTextMetrics(aqua_call *call, processing *p) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value text = aqua_arg(call, 0), family = aqua_arg(call, 1);
    double size = aqua_is_numeric(aqua_arg(call, 2)) ? aqua_number_of(aqua_arg(call, 2)) : 0;
    if (text.type != AQUA_STRING || !(size > 0)) { gfx_fail(call, "uiTextMetrics expects text, a font family and a positive size."); return; }
    int weight = aqua_argc(call) > 3 && aqua_is_numeric(aqua_arg(call, 3)) ? (int)aqua_number_of(aqua_arg(call, 3)) : 400;
    bool italic = aqua_argc(call) > 4 && aqua_arg(call, 4).type == AQUA_BOOL && aqua_arg(call, 4).number != 0;
    char *name = family.type == AQUA_STRING ? aqua_text_dup(rt, family, NULL) : NULL;
    char font[300];
    font_name(font, sizeof font, name, weight, italic);
    aqua_free(name);
    size_t length;
    char *utf8 = aqua_text_dup(rt, text, &length);
    int w = 0, h = 0;
    if (length) gfx_text_measure_line(font, (float)size, utf8, length, &w, &h);
    else gfx_text_measure_line(font, (float)size, " ", 1, &w, &h), w = 0;
    /* The advance: where the next character starts, as drawUI really draws the text (rasterized at the screen's pixel
       density, without the box's overhang padding), so carets and selections built from it sit between the drawn
       characters at any display scaling. */
    double density = 1;
    if (p && p->screen && p->screen->width > 0 && p->screen->pixel_width > 0) {
        density = rint((double)p->screen->pixel_width / p->screen->width * 1000) / 1000;
        if (density < 1) density = 1;
        if (density > 8) density = 8;
    }
    double advance = 0;
    if (length) {
        int dw = 0, dh = 0;
        if (gfx_platform_text_advance(font, (float)(size * density), utf8, length, &advance)) advance /= density;
        else { gfx_text_measure_line(font, (float)(size * density), utf8, length, &dw, &dh); advance = dw / density; }
        if (advance < 0) advance = 0;
    }
    aqua_free(utf8);
    double metrics[4] = {w, h * 0.8, h * 0.2, advance};
    aqua_return(call, aqua_array_from_f64(rt, metrics, 4, AQUA_DOUBLE));
}

/* ---- The list ---- */

static bool read_list(aqua_call *call, aqua_runtime *rt, aqua_value list, ui_list *l) {
    memset(l, 0, sizeof *l);
    aqua_value commands, vertices, indices;
    if (list.type != AQUA_HASH || !aqua_hash_field(rt, list, "commands", &commands) || commands.type != AQUA_ARRAY) {
        gfx_fail(call, "drawUI expects a UI draw list (a hash with commands, strings and images).");
        return false;
    }
    if (!aqua_hash_field(rt, list, "strings", &l->strings)) l->strings = aqua_null();
    if (!aqua_hash_field(rt, list, "images", &l->images)) l->images = aqua_null();
    l->command_count = aqua_array_length(rt, commands);
    l->commands = malloc(((size_t)l->command_count + 1) * sizeof *l->commands);
    bool ok = l->commands && (!l->command_count || aqua_array_read_f32(rt, commands, 0, l->command_count, l->commands));
    if (ok && aqua_hash_field(rt, list, "vertices", &vertices) && vertices.type == AQUA_ARRAY) {
        l->vertex_count = aqua_array_length(rt, vertices);
        l->vertices = malloc(((size_t)l->vertex_count + 1) * sizeof *l->vertices);
        ok = l->vertices && (!l->vertex_count || aqua_array_read_f32(rt, vertices, 0, l->vertex_count, l->vertices));
    }
    if (ok && aqua_hash_field(rt, list, "indices", &indices) && indices.type == AQUA_ARRAY) {
        l->index_count = aqua_array_length(rt, indices);
        l->indices = malloc(((size_t)l->index_count + 1) * sizeof *l->indices);
        ok = l->indices && (!l->index_count || aqua_array_read_i32(rt, indices, 0, l->index_count, l->indices));
    }
    if (!ok) gfx_fail(call, "drawUI: the list's commands, vertices and indices must be numbers.");
    return ok;
}

static void p_drawUI(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c) || !gfx_require_sketch(call, p) || !gfx_canvas_require_drawing(call, c)) return;
    ui_list l;
    if (!read_list(call, p->rt, aqua_arg(call, 0), &l)) { free(l.commands); free(l.vertices); free(l.indices); return; }
    ui_painter u = {call, p, c, 1, .model_depth = 0};
    mat4 model = c->model;
    bool had_clip = c->has_clip;
    float clip[4];
    memcpy(clip, c->clip, sizeof clip);
    int blend = c->blend_mode;
    c->blend_mode = 0;   /* BLEND */
    bool ok = true;
    for (uint32_t at = 0; ok && at < l.command_count;) {
        int op = (int)l.commands[at];
        if (op <= 0 || op >= OP_LIMIT || at + 1 + (uint32_t)op_sizes[op] > l.command_count) { gfx_fail(call, "drawUI: malformed command at %u.", at); ok = false; break; }
        const float *a = l.commands + at + 1;
        switch (op) {
        case OP_RECT: ok = fill_rounded(&u, a[0], a[1], a[2], a[3], a + 4, flat, a + 8); break;
        case OP_BORDER: ok = border_op(&u, a); break;
        case OP_SHADOW: ok = shadow_op(&u, a); break;
        case OP_GRADIENT: ok = fill_rounded(&u, a[0], a[1], a[2], a[3], a + 4, gradient, a); break;
        case OP_LINE: ok = line_op(&u, a); break;
        case OP_TEXT: ok = text_op(&u, &l, a); break;
        case OP_IMAGE: ok = image_op(&u, &l, a); break;
        case OP_MESH: ok = mesh_op(&u, &l, a); break;
        case OP_TEXTURE: ok = flush(&u, NULL) && texture_op(&u, &l, a); break;
        case OP_CLIP_PUSH: ok = push_clip(&u, a); break;
        case OP_CLIP_POP: ok = pop_clip(&u); break;
        case OP_TRANSFORM_PUSH: ok = push_transform(&u, a); break;
        case OP_TRANSFORM_POP: ok = pop_transform(&u); break;
        case OP_OPACITY_PUSH:
            if (u.opacity_depth == STACK_LIMIT) { gfx_fail(call, "drawUI: opacity nests more than %d deep.", STACK_LIMIT); ok = false; break; }
            u.opacities[u.opacity_depth++] = u.opacity;
            u.opacity *= a[0] < 0 ? 0 : a[0] > 1 ? 1 : a[0];
            break;
        case OP_OPACITY_POP: if (u.opacity_depth) u.opacity = u.opacities[--u.opacity_depth]; break;
        default: break;
        }
        at += 1 + (uint32_t)op_sizes[op];
    }
    if (ok) flush(&u, NULL);
    c->model = model;
    c->has_clip = had_clip;
    memcpy(c->clip, clip, sizeof clip);
    c->blend_mode = blend;
    free(u.scratch);
    free(l.commands); free(l.vertices); free(l.indices);
}

#define UI_CANVAS(X) X(drawUI, "drawUI|繪製介面", 1, 1)
UI_CANVAS(CANVAS_WRAPPERS)
#define UI_MODULE(X) X(uiTextMetrics, "uiTextMetrics|介面文字度量", 3, 5)
UI_MODULE(MODULE_WRAPPER)

void gfx_ui_register(aqua_class *module, aqua_class *canvas) {
    UI_CANVAS(CANVAS_DEFINE)
    UI_MODULE(MODULE_DEFINE)
}
