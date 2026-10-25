/* Canvases: the main sketch canvas and PGraphics. Each owns a render target, a style, the model/view/projection
   matrices and their stacks, the shape being built, and draws through the geometry kernel with the shared uniform ABI. */
#include "processing.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PI_F 3.14159265358979323846f

void gfx_style_default(style *s) {
    memset(s, 0, sizeof *s);
    s->fill = (vec4){1, 1, 1, 1}; s->stroke = (vec4){0, 0, 0, 1}; s->tint = (vec4){1, 1, 1, 1};
    s->has_fill = s->has_stroke = true;
    s->weight = 1; s->text_size = 12; s->leading = 14; s->shininess = 16;
    s->ellipse_mode = 2; s->text_vertical = 103; s->cap = 1;
    s->color_max = (vec4){255, 255, 255, 255};
    s->ambient = v3(0.2f, 0.2f, 0.2f); s->material_ambient = v3(1, 1, 1); s->specular = v3(0.25f, 0.25f, 0.25f);
    s->falloff = v3(1, 0, 0);
    strcpy(s->font, "sans-serif");
}

canvas *gfx_canvas_new(processing *p, int width, int height, bool is3d, bool offscreen) {
    canvas *c = calloc(1, sizeof *c);
    c->owner = p;
    c->width = c->pixel_width = width;
    c->height = c->pixel_height = height;
    c->is3d = is3d;
    c->offscreen = offscreen;
    gfx_style_default(&c->style);
    c->model = c->view = c->projection = gfx_m4_identity();
    c->detail = 32; c->sphere_u = 24; c->sphere_v = 16; c->shape_mode = -1;
    c->normal = v3(0, 0, 1);
    c->smooth = true;
    c->color_format = WGPUTextureFormat_RGBA8Unorm;
    gfx_canvas_default_camera(c);
    if (offscreen) {
        c->next = p->canvases;
        if (p->canvases) p->canvases->previous = c;
        p->canvases = c;
    }
    return c;
}

bool gfx_canvas_initialize(aqua_call *call, canvas *c, gpu_device *device) {
    c->target = gpu_target_create(device, c->pixel_width, c->pixel_height, c->color_format);
    if (!c->target) { gfx_fail(call, "wgpu render target creation failed."); return false; }
    if (c->offscreen) {
        pimage *surface = calloc(1, sizeof *surface);
        surface->owner = c->owner;
        surface->width = c->pixel_width; surface->height = c->pixel_height;
        surface->canvas = c;
        surface->texture = gpu_target_color(c->target);
        surface->premultiplied = true;
        c->surface = surface;
    }
    gfx_canvas_bind(c);
    gfx_canvas_background(c, (vec4){0.8f, 0.8f, 0.8f, 1});
    return true;
}

void gfx_canvas_dispose(canvas *c) {
    if (c->disposed) return;
    c->disposed = true;
    c->drawing = false;
    aqua_runtime *rt = c->owner->rt;
    if (c->target) { gpu_target_free(c->target); c->target = NULL; }
    if (c->surface) { c->surface->texture = NULL; c->surface->disposed = true; }
    if (c->custom_ref) { aqua_release(rt, c->custom_ref); c->custom_ref = 0; c->custom = NULL; }
    if (c->texture_ref) { aqua_release(rt, c->texture_ref); c->texture_ref = 0; c->texture = NULL; }
    if (c->pixels) { aqua_release(rt, c->pixels); c->pixels = 0; }
}

void gfx_canvas_free(canvas *c) {
    if (!c) return;
    gfx_canvas_dispose(c);
    processing *p = c->owner;
    if (c->offscreen) {
        if (c->previous) c->previous->next = c->next; else if (p->canvases == c) p->canvases = c->next;
        if (c->next) c->next->previous = c->previous;
    }
    free(c->surface);
    free(c->matrices); free(c->styles); free(c->vertices); free(c->contour_vertices); free(c->contour_starts); free(c->curve);
    free(c);
}

bool gfx_canvas_live(aqua_call *call, canvas *c) {
    if (!c->disposed) return true;
    gfx_fail(call, "Canvas is disposed.");
    return false;
}

bool gfx_canvas_require_readable(aqua_call *call, canvas *c) {
    if (c->disposed) { gfx_fail(call, "Canvas has been disposed."); return false; }
    if (c->shape_mode != -1) { gfx_fail(call, "Finish beginShape() with endShape() before drawing another primitive."); return false; }
    return true;
}

bool gfx_canvas_require_drawing(aqua_call *call, canvas *c) {
    if (!gfx_canvas_require_readable(call, c)) return false;
    if (!c->drawing) { gfx_fail(call, "Call size() for the main canvas, or beginDraw() for PGraphics first."); return false; }
    return true;
}

/* ---- Camera and size ---- */

bool gfx_canvas_ortho(aqua_call *call, float l, float r, float b, float t, float n, float f, mat4 *out) {
    if (l == r || b == t || n == f) { if (call) gfx_fail(call, "Orthographic bounds must have nonzero ranges."); return false; }
    *out = gfx_m4_make(2 / (r - l), 0, 0, 0, 0, 2 / (t - b), 0, 0, 0, 0, -2 / (f - n), 0, -(r + l) / (r - l), -(t + b) / (t - b), -(f + n) / (f - n), 1);
    return true;
}

bool gfx_canvas_perspective(aqua_call *call, float fov, float aspect, float near, float far, mat4 *out) {
    if (fov <= 0 || fov >= PI_F || aspect <= 0 || near <= 0 || far <= near) { if (call) gfx_fail(call, "Invalid perspective parameters."); return false; }
    float f = 1 / tanf(fov / 2);
    *out = gfx_m4_make(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
    return true;
}

void gfx_canvas_default_camera(canvas *c) {
    c->model = gfx_m4_identity();
    if (c->is3d) {
        float z = (float)c->height / 2.0f / tanf(PI_F / 6);
        c->view = gfx_m4_look_at(v3((float)c->width / 2.0f, (float)c->height / 2.0f, z), v3((float)c->width / 2.0f, (float)c->height / 2.0f, 0), v3(0, 1, 0));
        /* Processing uses downwards screen Y, including its default P3D camera. */
        if (gfx_canvas_perspective(NULL, PI_F / 3, (float)c->width / (float)c->height, z / 10, z * 10, &c->projection)) c->projection.m[5] = -c->projection.m[5];
    } else {
        c->view = gfx_m4_identity();
        gfx_canvas_ortho(NULL, 0, (float)c->width, (float)c->height, 0, -10000, 10000, &c->projection);
    }
}

bool gfx_canvas_resize(canvas *c, int width, int height, int pixel_width, int pixel_height) {
    int w = width > 0 && height > 0 ? width : c->width, h = width > 0 && height > 0 ? height : c->height;
    int pw = pixel_width > 0 && pixel_height > 0 ? pixel_width : 0, ph = pixel_width > 0 && pixel_height > 0 ? pixel_height : 0;
    bool logical = w != c->width || h != c->height;
    bool changed = logical || pw != c->pixel_width || ph != c->pixel_height;
    c->width = w; c->height = h; c->pixel_width = pw; c->pixel_height = ph;
    if (logical) gfx_canvas_default_camera(c);
    return changed;
}

void gfx_canvas_bind(canvas *c) {
    if (c->pixel_width <= 0 || c->pixel_height <= 0 || !c->target) return;
    gpu_target_configure(c->target, c->pixel_width, c->pixel_height, c->smooth);
    if (c->surface) { c->surface->texture = gpu_target_color(c->target); c->surface->width = c->pixel_width; c->surface->height = c->pixel_height; }
}

void gfx_canvas_background(canvas *c, vec4 color) {
    gfx_canvas_bind(c);
    if (!c->target) return;
    float rgba[4] = {color.x, color.y, color.z, color.w};
    gpu_target_clear(c->target, rgba);
}

mat4 gfx_canvas_mvp(const canvas *c) { return gfx_m4_multiply(gfx_m4_multiply(c->model, c->view), c->projection); }

void gfx_canvas_set_clip(canvas *c, float x, float y, float width, float height) {
    /* Captures the current drawing transform, like the geometry being clipped; density applies at draw time. */
    mat4 matrix = gfx_canvas_mvp(c);
    float minimum_x = INFINITY, minimum_y = INFINITY, maximum_x = -INFINITY, maximum_y = -INFINITY;
    float corners[4][2] = {{x, y}, {x + width, y}, {x + width, y + height}, {x, y + height}};
    c->has_clip = true;
    for (int i = 0; i < 4; i++) {
        vec4 p = gfx_v4_transform((vec4){corners[i][0], corners[i][1], 0, 1}, matrix);
        if (p.w <= 0 || !isfinite(p.w)) { memset(c->clip, 0, sizeof c->clip); return; }
        float sx = (p.x / p.w + 1) * (float)c->width / 2, sy = (1 - p.y / p.w) * (float)c->height / 2;
        if (!isfinite(sx) || !isfinite(sy)) { memset(c->clip, 0, sizeof c->clip); return; }
        minimum_x = fminf(minimum_x, sx); minimum_y = fminf(minimum_y, sy);
        maximum_x = fmaxf(maximum_x, sx); maximum_y = fmaxf(maximum_y, sy);
    }
    c->clip[0] = minimum_x; c->clip[1] = minimum_y; c->clip[2] = maximum_x - minimum_x; c->clip[3] = maximum_y - minimum_y;
}

/* ---- Uniforms ---- */

static void write_matrix(float *out, int *offset, mat4 m) { memcpy(out + *offset, m.m, sizeof m.m); *offset += 16; }
static void write_vector(float *out, int *offset, vec3 v, float w) { out[(*offset)++] = v.x; out[(*offset)++] = v.y; out[(*offset)++] = v.z; out[(*offset)++] = w; }

static void uniforms(float *data, const canvas *c, bool textured, bool premultiplied, bool lit) {
    memset(data, 0, GPU_UNIFORM_BYTES);
    int o = 0;
    write_matrix(data, &o, c->model); write_matrix(data, &o, c->view); write_matrix(data, &o, c->projection);
    mat4 inverse;
    gfx_m4_invert(gfx_m4_multiply(c->model, c->view), &inverse);
    write_matrix(data, &o, gfx_m4_transpose(inverse));
    write_vector(data, &o, v3(textured ? 1.0f : 0.0f, c->style.lit && lit ? 1.0f : 0.0f, premultiplied ? 1.0f : 0.0f), c->blend_mode == 4 ? 1.0f : 0.0f);
    write_vector(data, &o, c->style.ambient, 0);
    write_vector(data, &o, c->style.specular, 0);
    write_vector(data, &o, c->style.emissive, 0);
    write_vector(data, &o, c->style.material_ambient, c->style.shininess);
    write_vector(data, &o, v3((float)c->style.light_count, 0, 0), 0);
    for (int i = 0; i < c->style.light_count; i++) {
        const light *l = &c->style.lights[i];
        write_vector(data, &o, l->color, (float)l->kind);
        write_vector(data, &o, l->position, 0);
        write_vector(data, &o, l->direction, 0);
        write_vector(data, &o, l->falloff, 0);
        write_vector(data, &o, l->specular, 0);
        write_vector(data, &o, v3(l->cutoff, l->concentration, 0), 0);
    }
}

/* ---- Drawing ---- */

bool gfx_image_upload(pimage *image, gpu_device *device, char *error, size_t size) {
    if (image->disposed) { snprintf(error, size, "Image has been disposed."); return false; }
    if (image->canvas) return true;
    if (image->texture && (gpu_texture_width(image->texture) != image->width || gpu_texture_height(image->texture) != image->height || gpu_texture_device(image->texture) != device)) {
        gpu_texture_free(image->texture);
        image->texture = NULL;
    }
    bool fresh = !image->texture;
    if (fresh) {
        image->texture = gpu_texture_rgba(device, image->width, image->height);
        if (!image->texture) { snprintf(error, size, "wgpu texture creation failed."); return false; }
        image->dirty = true;
    }
    if (!image->dirty) return true;
    gpu_texture_upload(image->texture, image->bytes, fresh);
    image->dirty = false;
    return true;
}

bool gfx_canvas_draw_packed(aqua_call *call, canvas *c, const float *floats, size_t count, pimage *image, bool lit, int stencil) {
    if (count == 0) return true;
    gfx_canvas_bind(c);
    if (!c->target || c->pixel_width <= 0 || c->pixel_height <= 0) return true;
    gpu_device *device = c->owner->device;
    char error[1024];
    if (image) {
        if (!gfx_image_upload(image, device, error, sizeof error)) { gfx_fail(call, "%s", error); return false; }
        if (image->canvas && image->canvas != c && image->canvas->target) gpu_target_flush(image->canvas->target);
    }
    float clip[4];
    const float *scaled = NULL;
    if (c->has_clip) {
        clip[0] = c->clip[0] * (float)c->pixel_width / (float)c->width; clip[1] = c->clip[1] * (float)c->pixel_height / (float)c->height;
        clip[2] = c->clip[2] * (float)c->pixel_width / (float)c->width; clip[3] = c->clip[3] * (float)c->pixel_height / (float)c->height;
        scaled = clip;
    }
    float data[GPU_UNIFORM_BYTES / 4];
    uniforms(data, c, image != NULL, image && image->premultiplied, lit);
    gpu_shader *shader = c->custom ? c->custom->shader : gfx_processing_default_shader(c->owner);
    if (!shader || gpu_shader_released(shader)) { gfx_fail(call, "wgpu shader has been disposed."); return false; }
    gpu_rect scissor = gpu_scissor(gpu_target_width(c->target), gpu_target_height(c->target), scaled);
    if (!gpu_target_draw(c->target, shader, floats, count, data, 4, c->blend_mode, c->is3d ? 1 : 0, 0, &scissor,
                         image ? image->texture : NULL, stencil, error, sizeof error)) {
        gfx_fail(call, "%s", error);
        return false;
    }
    return true;
}

bool gfx_canvas_draw(aqua_call *call, canvas *c, const pvertex *vertices, size_t count, pimage *image, bool lit, int stencil) {
    return gfx_canvas_draw_packed(call, c, (const float *)vertices, count * GPU_FLOATS_PER_VERTEX, image, lit, stencil);
}

pvertex gfx_canvas_vertex(const canvas *c, vec3 position, vec4 color, vec2 uv) {
    pvertex v = {position, c->normal, uv, color};
    return v;
}

/* ---- The geometry kernel ---- */

static void put4(float *target, vec4 c) { target[0] = c.x; target[1] = c.y; target[2] = c.z; target[3] = c.w; }

geo *gfx_canvas_geometry(canvas *c) {
    geo *g = &c->owner->geometry;
    float *s = g->state;
    s[GEO_WEIGHT] = c->style.weight; s[GEO_CAP] = (float)c->style.cap; s[GEO_JOIN] = (float)c->style.join; s[GEO_IS3D] = c->is3d ? 1.0f : 0.0f;
    s[GEO_WIDTH] = (float)c->width; s[GEO_HEIGHT] = (float)c->height;
    put4(s + GEO_STROKE_COLOR, c->style.stroke); put4(s + GEO_FILL_COLOR, c->style.fill);
    s[GEO_NORMAL] = c->normal.x; s[GEO_NORMAL + 1] = c->normal.y; s[GEO_NORMAL + 2] = c->normal.z;
    s[GEO_HAS_FILL] = c->style.has_fill ? 1.0f : 0.0f; s[GEO_HAS_STROKE] = c->style.has_stroke ? 1.0f : 0.0f;
    s[GEO_RECT_MODE] = (float)c->style.rect_mode; s[GEO_ELLIPSE_MODE] = (float)c->style.ellipse_mode;
    s[GEO_DETAIL] = (float)c->detail; s[GEO_TIGHTNESS] = c->tightness;
    mat4 m = gfx_canvas_mvp(c);
    memcpy(s + GEO_MVP, m.m, sizeof m.m);
    gfx_geo_state_changed(g);
    return g;
}

bool gfx_canvas_check(aqua_call *call, int result) {
    if (result >= 0) return true;
    switch (result) {
    case GEO_ZERO_AREA: gfx_fail(call, "Polygon has zero area."); break;
    case GEO_NOT_SIMPLE: gfx_fail(call, "Polygon boundary must be simple and have nonzero area; use beginContour() for holes."); break;
    case GEO_OUT_OF_MEMORY: gfx_fail(call, "Geometry kernel memory exhausted."); break;
    default: gfx_fail(call, "Geometry kernel error %d.", result); break;
    }
    return false;
}

/* Draws the kernel's last output: lit fills, then unlit fills and strokes, in emission order. */
bool gfx_canvas_draw_geometry(aqua_call *call, canvas *c, geo *g, pimage *image) {
    int count = g->range_count;
    if (!count) return true;
    geo_range ranges[64], *list = count <= 64 ? ranges : malloc((size_t)count * sizeof *list);
    memcpy(list, g->ranges, (size_t)count * sizeof *list);
    bool ok = true;
    for (int i = 0; i < count && ok; i++)
        ok = gfx_canvas_draw_packed(call, c, g->vertices + (size_t)list[i].start * GPU_FLOATS_PER_VERTEX, (size_t)list[i].count * GPU_FLOATS_PER_VERTEX,
                                image, list[i].kind == GEO_FILL, 0);
    if (list != ranges) free(list);
    return ok;
}

static bool points_input(geo *g, const vec3 *points, int count, int extra) {
    float *input = gfx_geo_input(g, count * 3 + extra > 0 ? count * 3 + extra : 1);
    if (!input) return false;
    memcpy(input, points, (size_t)count * sizeof *points);
    return true;
}

static bool floats_input(geo *g, const float *values, int count) {
    float *input = gfx_geo_input(g, count);
    if (!input) return false;
    memcpy(input, values, (size_t)count * sizeof *values);
    return true;
}

static bool run(aqua_call *call, canvas *c, geo *g, int result) {
    return gfx_canvas_check(call, result) && gfx_canvas_draw_geometry(call, c, g, NULL);
}

bool gfx_canvas_polygon(aqua_call *call, canvas *c, const vec3 *points, int count, bool close, bool fill) {
    if (!gfx_canvas_require_drawing(call, c)) return false;
    geo *g = gfx_canvas_geometry(c);
    if (!points_input(g, points, count, 0)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_polygon(g, count, close ? 1 : 0, fill ? 1 : 0));
}

bool gfx_canvas_polyline(aqua_call *call, canvas *c, const vec3 *points, int count, bool close) {
    if (!c->style.has_stroke || count < 2) return true;
    geo *g = gfx_canvas_geometry(c);
    if (!points_input(g, points, count, 0)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_polyline(g, count, close ? 1 : 0));
}

bool gfx_canvas_segment(aqua_call *call, canvas *c, vec3 a, vec3 b) {
    if (!c->style.has_stroke || c->style.weight <= 0) return true;
    geo *g = gfx_canvas_geometry(c);
    vec3 points[2] = {a, b};
    if (!points_input(g, points, 2, 0)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_line(g));
}

bool gfx_canvas_point(aqua_call *call, canvas *c, vec3 p) {
    if (!c->style.has_stroke) return true;
    geo *g = gfx_canvas_geometry(c);
    if (!points_input(g, &p, 1, 0)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_point(g));
}

bool gfx_canvas_rect(aqua_call *call, canvas *c, float x, float y, float w, float h, float radius) {
    if (!gfx_canvas_require_drawing(call, c)) return false;
    geo *g = gfx_canvas_geometry(c);
    float values[5] = {x, y, w, h, radius};
    if (!floats_input(g, values, 5)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_rect(g));
}

bool gfx_canvas_ellipse(aqua_call *call, canvas *c, float x, float y, float w, float h, float start, float stop, int mode, bool arc) {
    if (!gfx_canvas_require_drawing(call, c)) return false;
    geo *g = gfx_canvas_geometry(c);
    float values[6] = {x, y, w, h, start, stop};
    if (!floats_input(g, values, 6)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_ellipse(g, mode, arc ? 1 : 0));
}

bool gfx_canvas_curve(aqua_call *call, canvas *c, bool catmull, const vec3 *controls) {
    geo *g = gfx_canvas_geometry(c);
    if (!points_input(g, controls, 4, 0)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_curve(g, catmull ? 1 : 0));
}

int gfx_canvas_curve_points(canvas *c, bool catmull, const vec3 *controls, int from, vec3 *out, int capacity) {
    geo *g = gfx_canvas_geometry(c);
    if (!points_input(g, controls, 4, 0)) return GEO_OUT_OF_MEMORY;
    int count = gfx_geo_curve_points(g, catmull ? 1 : 0, from);
    if (count < 0) return count;
    if (count > capacity) count = capacity;
    memcpy(out, g->input + 12, (size_t)count * sizeof *out);
    return count;
}

bool gfx_canvas_image(aqua_call *call, canvas *c, pimage *image, float x, float y, const float *w, const float *h) {
    if (!gfx_canvas_require_drawing(call, c)) return false;
    if (c->surface == image) { gfx_fail(call, "Cannot draw a canvas into itself."); return false; }
    geo *g = gfx_canvas_geometry(c);
    float width = w ? *w : image->canvas ? (float)image->canvas->width : (float)image->width;
    float height = h ? *h : image->canvas ? (float)image->canvas->height : (float)image->height;
    vec4 t = c->style.tint;
    float values[8] = {x, y, width, height, t.x, t.y, t.z, t.w};
    if (!floats_input(g, values, 8)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    int result = gfx_geo_image(g, c->style.image_mode, image->flipped ? 1 : 0);
    return gfx_canvas_check(call, result) && gfx_canvas_draw_geometry(call, c, g, image);
}

bool gfx_canvas_box(aqua_call *call, canvas *c, float w, float h, float d) {
    geo *g = gfx_canvas_geometry(c);
    float values[3] = {w, h, d};
    if (!floats_input(g, values, 3)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_box(g));
}

bool gfx_canvas_sphere(aqua_call *call, canvas *c, float radius) {
    geo *g = gfx_canvas_geometry(c);
    if (!floats_input(g, &radius, 1)) return gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
    return run(call, c, g, gfx_geo_sphere(g, c->sphere_u, c->sphere_v));
}

/* ---- Shapes being built ---- */

void gfx_canvas_vertices_clear(canvas *c) { c->vertex_count = 0; c->contour_vertex_count = 0; c->contour_count = 0; c->curve_count = 0; c->in_contour = false; }

pvertex *gfx_canvas_active_vertices(canvas *c, size_t *count) {
    if (c->in_contour) {
        size_t start = c->contour_starts[c->contour_count - 1];
        *count = c->contour_vertex_count - start;
        return c->contour_vertices + start;
    }
    *count = c->vertex_count;
    return c->vertices;
}

void gfx_canvas_push_vertex(canvas *c, pvertex v) {
    if (c->in_contour) GFX_PUSH(c->contour_vertices, c->contour_vertex_count, c->contour_vertex_capacity, v);
    else GFX_PUSH(c->vertices, c->vertex_count, c->vertex_capacity, v);
}

/* The triangles of a beginShape kind (POLYGON triangulated by the kernel), keeping each vertex's attributes. */
static pvertex *shape_triangles(aqua_call *call, canvas *c, int kind, const pvertex *vertices, size_t count, size_t *triangles) {
    geo *g = gfx_canvas_geometry(c);
    float *input = gfx_geo_input(g, (int)(count * 3 > 0 ? count * 3 : 1));
    if (!input) { gfx_canvas_check(call, GEO_OUT_OF_MEMORY); return NULL; }
    for (size_t i = 0; i < count; i++) { input[i * 3] = vertices[i].position.x; input[i * 3 + 1] = vertices[i].position.y; input[i * 3 + 2] = vertices[i].position.z; }
    int n = gfx_geo_shape_indices(g, kind, (int)count);
    if (!gfx_canvas_check(call, n)) return NULL;
    pvertex *result = malloc((size_t)(n ? n : 1) * sizeof *result);
    for (int i = 0; i < n; i++) result[i] = vertices[g->indices[i]];
    *triangles = (size_t)n;
    return result;
}

static vec3 *positions_of(const pvertex *vertices, size_t count) {
    vec3 *p = malloc((count ? count : 1) * sizeof *p);
    for (size_t i = 0; i < count; i++) p[i] = vertices[i].position;
    return p;
}

bool gfx_canvas_end_shape(aqua_call *call, canvas *c, bool close) {
    int kind = c->shape_mode;
    c->shape_mode = -1;
    size_t n = c->vertex_count;
    vec3 *positions = positions_of(c->vertices, n);
    bool ok = true;
    if (kind == 0) {
        if (c->style.has_stroke) {
            geo *g = gfx_canvas_geometry(c);
            ok = points_input(g, positions, (int)n, 0) ? run(call, c, g, gfx_geo_points(g, (int)n)) : gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
        }
        free(positions);
        return ok;
    }
    if (kind == 1) {
        if (n % 2) { free(positions); gfx_fail(call, "LINES requires pairs of vertices."); return false; }
        geo *g = gfx_canvas_geometry(c);
        ok = points_input(g, positions, (int)n, 0) ? run(call, c, g, gfx_geo_lines(g, (int)n)) : gfx_canvas_check(call, GEO_OUT_OF_MEMORY);
        free(positions);
        return ok;
    }
    const char *invalid = kind == 4 && n % 3 ? "TRIANGLES requires groups of three vertices." :
                          kind == 7 && n % 4 ? "QUADS requires groups of four vertices." :
                          kind == 8 && n % 2 ? "QUAD_STRIP requires pairs of vertices." : NULL;
    if (invalid) { free(positions); gfx_fail(call, "%s", invalid); return false; }
    /* Unfilled paths only need stroke geometry; they may cross themselves or have zero area. */
    size_t triangle_count = 0;
    pvertex *triangles = NULL;
    if (!(kind == 9 && !(c->style.has_fill && n >= 3))) {
        triangles = shape_triangles(call, c, kind, c->vertices, n, &triangle_count);
        if (!triangles) { free(positions); return false; }
    }
    if (c->style.has_fill && c->contour_count > 0) {
        gfx_canvas_bind(c);
        if (c->target) gpu_target_clear_stencil(c->target);
        ok = gfx_canvas_draw(call, c, triangles, triangle_count, NULL, false, 1);
        for (size_t k = 0; ok && k < c->contour_count; k++) {
            size_t start = c->contour_starts[k], end = k + 1 < c->contour_count ? c->contour_starts[k + 1] : c->contour_vertex_count;
            size_t holes = 0;
            pvertex *hole = shape_triangles(call, c, 9, c->contour_vertices + start, end - start, &holes);
            ok = hole && gfx_canvas_draw(call, c, hole, holes, NULL, false, 2);
            free(hole);
        }
        if (ok) ok = gfx_canvas_draw(call, c, triangles, triangle_count, c->texture, true, 3);
    } else if (c->style.has_fill && triangle_count) ok = gfx_canvas_draw(call, c, triangles, triangle_count, c->texture, true, 0);
    if (ok && c->style.has_stroke) {
        if (kind == 9) ok = gfx_canvas_polyline(call, c, positions, (int)n, close);
        else if (kind == 7) for (size_t i = 0; ok && i < n; i += 4) ok = gfx_canvas_polyline(call, c, positions + i, 4, true);
        else for (size_t i = 0; ok && i + 2 < triangle_count; i += 3) {
            vec3 triangle[3] = {triangles[i].position, triangles[i + 1].position, triangles[i + 2].position};
            ok = gfx_canvas_polyline(call, c, triangle, 3, true);
        }
    }
    if (ok && c->style.has_stroke)
        for (size_t k = 0; ok && k < c->contour_count; k++) {
            size_t start = c->contour_starts[k], end = k + 1 < c->contour_count ? c->contour_starts[k + 1] : c->contour_vertex_count;
            vec3 *hole = positions_of(c->contour_vertices + start, end - start);
            ok = gfx_canvas_polyline(call, c, hole, (int)(end - start), true);
            free(hole);
        }
    free(triangles);
    free(positions);
    gfx_canvas_vertices_clear(c);
    if (c->texture_ref) { aqua_release(c->owner->rt, c->texture_ref); c->texture_ref = 0; }
    c->texture = NULL;
    return ok;
}

/* ---- Pixels ---- */

typedef struct read_request { canvas *canvas; pixels_done done; void *data; } read_request;
static void on_read(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    read_request *r = data;
    r->done(r->data, rgba, width, height, error);
    free(r);
}

void gfx_canvas_read_pixels(canvas *c, pixels_done done, void *data) {
    if (c->disposed || !c->target) { done(data, NULL, 0, 0, "Canvas has been disposed."); return; }
    gfx_canvas_bind(c);
    read_request *r = malloc(sizeof *r);
    r->canvas = c; r->done = done; r->data = data;
    gpu_target_read(c->target, on_read, r);
}

bool gfx_canvas_write_pixels(aqua_call *call, canvas *c, const uint8_t *bytes) {
    pimage image;
    memset(&image, 0, sizeof image);
    image.owner = c->owner;
    image.width = c->pixel_width; image.height = c->pixel_height;
    image.bytes = (uint8_t *)bytes;
    image.dirty = true;
    mat4 model = c->model, view = c->view, projection = c->projection;
    style saved = c->style;
    bool is3d = c->is3d;
    int blend = c->blend_mode;
    c->model = c->view = gfx_m4_identity();
    gfx_canvas_ortho(NULL, 0, (float)c->width, (float)c->height, 0, -10000, 10000, &c->projection);
    gfx_style_default(&c->style);
    c->is3d = false;
    c->blend_mode = 4;
    float w = (float)c->width, h = (float)c->height;
    bool ok = gfx_canvas_image(call, c, &image, 0, 0, &w, &h);
    c->model = model; c->view = view; c->projection = projection; c->style = saved; c->is3d = is3d; c->blend_mode = blend;
    if (image.texture) gpu_texture_free(image.texture);
    gfx_canvas_bind(c);
    return ok;
}
