/* Canvas members shared by the Processing module (its main canvas) and PGraphics: colors and styles, transforms and
   their stacks, primitives, curves, the camera, lights and materials, and immediate shapes (beginShape...endShape). */
#include "processing.h"
#include <math.h>
#include <stdio.h>
#include <string.h>

#define PI_F 3.14159265358979323846f

/* Checks of the earlier wrappers: Action (live canvas), Draw (also a sketch and an active draw), Transform (no shape
   in progress). */
static bool action(aqua_call *call, canvas *c) { return gfx_canvas_live(call, c); }
static bool draw(aqua_call *call, processing *p, canvas *c) { return gfx_canvas_live(call, c) && gfx_require_sketch(call, p) && gfx_canvas_require_drawing(call, c); }
static bool transform(aqua_call *call, canvas *c) {
    if (!gfx_canvas_live(call, c)) return false;
    if (c->shape_mode != -1) { gfx_fail(call, "Transforms are not allowed between beginShape() and endShape()."); return false; }
    return true;
}
static vec3 point_of(aqua_call *call, uint32_t index, int dimensions) {
    return v3(gfx_float(call, index), gfx_float(call, index + 1), dimensions == 3 ? gfx_float(call, index + 2) : 0);
}
static int detail_of(aqua_call *call, uint32_t index) {
    int d = gfx_int(call, index);
    if (!aqua_failed(call) && (d < 3 || d > 512)) { gfx_fail(call, "Detail must be 3..512."); return 0; }
    return d;
}
static vec3 normalized(aqua_call *call, vec3 v) {
    if (gfx_v3_length_squared(v) < 1e-12f) { gfx_fail(call, "Cannot normalize a zero vector."); return v; }
    return gfx_v3_normalize(v);
}

/* ---- Colors and styles ---- */

static void p_colorMode(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    uint32_t n = aqua_argc(call);
    if (n == 3) { gfx_fail(call, "Use mode[,max] or mode,max1,max2,max3[,maxAlpha]."); return; }
    int mode = GFX_CHOICE(call, 0, 0, 1);
    vec4 max = c->style.color_max;
    if (n == 2) { float m = gfx_positive(call, 1); max = (vec4){m, m, m, m}; }
    if (n >= 4) max = (vec4){gfx_positive(call, 1), gfx_positive(call, 2), gfx_positive(call, 3), n == 5 ? gfx_positive(call, 4) : max.w};
    if (aqua_failed(call)) return;
    c->style.color_mode = mode;
    c->style.color_max = max;
}

static void p_color(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    vec4 color;
    if (gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) aqua_return_double(call, gfx_color_pack(color));
}

static void channel(aqua_call *call, canvas *c, int which) {
    uint32_t packed = gfx_packed(call, 0);
    if (aqua_failed(call)) return;
    vec4 v = gfx_color_unpack(packed);
    vec3 hsb = gfx_color_rgb_to_hsb(v);
    vec4 max = c->style.color_max;
    float values[7] = {v.x * max.x, v.y * max.y, v.z * max.z, v.w * max.w, hsb.x * max.x, hsb.y * max.y, hsb.z * max.z};
    aqua_return_double(call, values[which]);
}
static void p_red(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 0); }
static void p_green(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 1); }
static void p_blue(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 2); }
static void p_alpha(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 3); }
static void p_hue(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 4); }
static void p_saturation(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 5); }
static void p_brightness(aqua_call *call, processing *p, canvas *c) { (void)p; channel(call, c, 6); }

static void p_lerpColor(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    uint32_t a = gfx_packed(call, 0), b = gfx_packed(call, 1);
    float t = gfx_float(call, 2);
    if (aqua_failed(call)) return;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    vec4 x = gfx_color_unpack(a), y = gfx_color_unpack(b);
    if (c->style.color_mode == 1) {
        vec3 hx = gfx_color_rgb_to_hsb(x), hy = gfx_color_rgb_to_hsb(y), h = gfx_v3_lerp(hx, hy, t);
        aqua_return_double(call, gfx_color_pack(gfx_color_hsb(h.x, h.y, h.z, x.w + (y.w - x.w) * t)));
        return;
    }
    vec4 r = {x.x + (y.x - x.x) * t, x.y + (y.y - x.y) * t, x.z + (y.z - x.z) * t, x.w + (y.w - x.w) * t};
    aqua_return_double(call, gfx_color_pack(r));
}

static void p_fill(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    vec4 color;
    if (action(call, c) && gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) { c->style.fill = color; c->style.has_fill = true; }
}
static void p_stroke(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    vec4 color;
    if (action(call, c) && gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) { c->style.stroke = color; c->style.has_stroke = true; }
}
static void p_noFill(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) c->style.has_fill = false; }
static void p_noStroke(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) c->style.has_stroke = false; }
static void p_tint(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    vec4 color;
    if (action(call, c) && gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) c->style.tint = color;
}
static void p_noTint(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) c->style.tint = (vec4){1, 1, 1, 1}; }
static void p_strokeWeight(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    float w = gfx_nonnegative(call, 0);
    if (!aqua_failed(call)) c->style.weight = w;
}
static void p_strokeCap(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int v = GFX_CHOICE(call, 0, 0, 1, 2); if (!aqua_failed(call)) c->style.cap = v; }
static void p_strokeJoin(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int v = GFX_CHOICE(call, 0, 0, 1, 2); if (!aqua_failed(call)) c->style.join = v; }
static void p_rectMode(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int v = GFX_CHOICE(call, 0, 0, 1, 2, 3); if (!aqua_failed(call)) c->style.rect_mode = v; }
static void p_ellipseMode(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int v = GFX_CHOICE(call, 0, 0, 1, 2, 3); if (!aqua_failed(call)) c->style.ellipse_mode = v; }
static void p_imageMode(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int v = GFX_CHOICE(call, 0, 0, 1, 2); if (!aqua_failed(call)) c->style.image_mode = v; }

static void p_background(aqua_call *call, processing *p, canvas *c) {
    vec4 color;
    if (draw(call, p, c) && gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) gfx_canvas_background(c, color);
}
static void p_clear(aqua_call *call, processing *p, canvas *c) { if (draw(call, p, c)) gfx_canvas_background(c, (vec4){0, 0, 0, 0}); }

/* ---- Primitives ---- */

static void p_point(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    vec3 a = point_of(call, 0, (int)aqua_argc(call));
    if (!aqua_failed(call)) gfx_canvas_point(call, c, a);
}
static void p_line(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    if (n != 4 && n != 6) { gfx_fail(call, "Use 4 (2D) or 6 (3D) coordinates."); return; }
    int d = (int)n / 2;
    vec3 a = point_of(call, 0, d), b = point_of(call, (uint32_t)d, d);
    if (!aqua_failed(call)) gfx_canvas_segment(call, c, a, b);
}
static void p_triangle(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    vec3 points[3] = {point_of(call, 0, 2), point_of(call, 2, 2), point_of(call, 4, 2)};
    if (!aqua_failed(call)) gfx_canvas_polygon(call, c, points, 3, true, true);
}
static void p_quad(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    vec3 points[4] = {point_of(call, 0, 2), point_of(call, 2, 2), point_of(call, 4, 2), point_of(call, 6, 2)};
    if (!aqua_failed(call)) gfx_canvas_polygon(call, c, points, 4, true, true);
}
static void p_rect(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), w = gfx_float(call, 2), h = gfx_float(call, 3);
    float r = aqua_argc(call) == 5 ? fmaxf(0, gfx_float(call, 4)) : -1;
    if (!aqua_failed(call)) gfx_canvas_rect(call, c, x, y, w, h, r);
}
static void p_ellipse(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), w = gfx_float(call, 2), h = gfx_float(call, 3);
    if (!aqua_failed(call)) gfx_canvas_ellipse(call, c, x, y, w, h, 0, 6.28318530717958647692f, 2, false);
}
static void p_circle(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), d = gfx_float(call, 2);
    if (!aqua_failed(call)) gfx_canvas_ellipse(call, c, x, y, d, d, 0, 6.28318530717958647692f, 2, false);
}
static void p_square(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), s = gfx_float(call, 2);
    if (!aqua_failed(call)) gfx_canvas_rect(call, c, x, y, s, s, -1);
}
static void p_arc(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), w = gfx_float(call, 2), h = gfx_float(call, 3), start = gfx_float(call, 4), stop = gfx_float(call, 5);
    int mode = aqua_argc(call) == 7 ? GFX_CHOICE(call, 6, 0, 1, 2) : 0;
    if (!aqua_failed(call)) gfx_canvas_ellipse(call, c, x, y, w, h, start, stop, mode, true);
}
static void p_bezierDetail(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int d = detail_of(call, 0); if (!aqua_failed(call)) c->detail = d; }
static void p_curveDetail(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int d = detail_of(call, 0); if (!aqua_failed(call)) c->detail = d; }
static void p_curveTightness(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; float t = gfx_float(call, 0); if (!aqua_failed(call)) c->tightness = t; }
static void curve_call(aqua_call *call, processing *p, canvas *c, bool catmull) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    if (n != 8 && n != 12) { gfx_fail(call, "Use 8 (2D) or 12 (3D) coordinates."); return; }
    int d = (int)n / 4;
    vec3 points[4];
    for (int j = 0; j < 4; j++) points[j] = point_of(call, (uint32_t)(j * d), d);
    if (!aqua_failed(call)) gfx_canvas_curve(call, c, catmull, points);
}
static void p_bezier(aqua_call *call, processing *p, canvas *c) { curve_call(call, p, c, false); }
static void p_curve(aqua_call *call, processing *p, canvas *c) { curve_call(call, p, c, true); }

/* ---- Matrix and style stacks ---- */

static void p_pushMatrix(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) GFX_PUSH(c->matrices, c->matrix_count, c->matrix_capacity, c->model); }
static void p_popMatrix(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (!c->matrix_count) { gfx_fail(call, "Matrix stack is empty."); return; }
    c->model = c->matrices[--c->matrix_count];
}
static void p_resetMatrix(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) c->model = gfx_m4_identity(); }
static void p_pushStyle(aqua_call *call, processing *p, canvas *c) { (void)p; if (action(call, c)) GFX_PUSH(c->styles, c->style_count, c->style_capacity, c->style); }
static void p_popStyle(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (!c->style_count) { gfx_fail(call, "Style stack is empty."); return; }
    c->style = c->styles[--c->style_count];
}
static void p_push(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    GFX_PUSH(c->matrices, c->matrix_count, c->matrix_capacity, c->model);
    GFX_PUSH(c->styles, c->style_count, c->style_capacity, c->style);
}
static void p_pop(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (!c->matrix_count || !c->style_count) { gfx_fail(call, "push()/pop() stack is empty."); return; }
    c->model = c->matrices[--c->matrix_count];
    c->style = c->styles[--c->style_count];
}

/* ---- Transforms ---- */

static void apply(canvas *c, mat4 m) { c->model = gfx_m4_multiply(m, c->model); }
static void p_translate(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!transform(call, c)) return;
    vec3 t = point_of(call, 0, (int)aqua_argc(call));
    if (!aqua_failed(call)) apply(c, gfx_m4_translation(t.x, t.y, t.z));
}
static void p_scale(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!transform(call, c)) return;
    uint32_t n = aqua_argc(call);
    float x = gfx_float(call, 0), y = n == 1 ? x : gfx_float(call, 1), z = n == 1 ? x : n == 3 ? gfx_float(call, 2) : 1;
    if (!aqua_failed(call)) apply(c, gfx_m4_scale(x, y, z));
}
static void p_rotate(aqua_call *call, processing *p, canvas *c) { (void)p; if (!transform(call, c)) return; float r = gfx_float(call, 0); if (!aqua_failed(call)) apply(c, gfx_m4_rotation_z(r)); }
static void p_rotateX(aqua_call *call, processing *p, canvas *c) { (void)p; if (!transform(call, c)) return; float r = gfx_float(call, 0); if (!aqua_failed(call)) apply(c, gfx_m4_rotation_x(r)); }
static void p_rotateY(aqua_call *call, processing *p, canvas *c) { (void)p; if (!transform(call, c)) return; float r = gfx_float(call, 0); if (!aqua_failed(call)) apply(c, gfx_m4_rotation_y(r)); }
static void p_rotateZ(aqua_call *call, processing *p, canvas *c) { (void)p; if (!transform(call, c)) return; float r = gfx_float(call, 0); if (!aqua_failed(call)) apply(c, gfx_m4_rotation_z(r)); }
static void p_shearX(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!transform(call, c)) return;
    float r = gfx_float(call, 0);
    if (aqua_failed(call)) return;
    mat4 m = gfx_m4_identity(); m.m[4] = tanf(r);
    apply(c, m);
}
static void p_shearY(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!transform(call, c)) return;
    float r = gfx_float(call, 0);
    if (aqua_failed(call)) return;
    mat4 m = gfx_m4_identity(); m.m[1] = tanf(r);
    apply(c, m);
}

/* A 16-element column-major array as a matrix (GLM and applyMatrix share it). */
bool gfx_matrix_argument(aqua_call *call, uint32_t index, mat4 *out) {
    aqua_value array = gfx_array(call, index);
    if (aqua_failed(call)) return false;
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_array_length(rt, array) != 16) { gfx_fail(call, "Expected a 16-element column-major matrix."); return false; }
    for (uint32_t i = 0; i < 16; i++) {
        double n;
        if (!gfx_finite_number(aqua_array_get(rt, array, i), &n)) { gfx_fail(call, "Expected a finite number."); return false; }
        out->m[i] = (float)n;
    }
    return true;
}
aqua_value gfx_matrix_value(aqua_runtime *rt, mat4 m) {
    double values[16];
    for (int i = 0; i < 16; i++) values[i] = m.m[i];
    return aqua_array_from_f64(rt, values, 16, AQUA_DOUBLE);
}

static void p_applyMatrix(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!transform(call, c)) return;
    mat4 m;
    if (gfx_matrix_argument(call, 0, &m)) apply(c, m);
}
static void p_getMatrix(aqua_call *call, processing *p, canvas *c) { (void)p; aqua_return(call, gfx_matrix_value(aqua_call_runtime(call), c->model)); }

static void model_axis(aqua_call *call, canvas *c, int axis) {
    vec3 point = point_of(call, 0, (int)aqua_argc(call));
    if (aqua_failed(call)) return;
    vec3 r = gfx_v3_transform(point, c->model);
    aqua_return_double(call, axis == 0 ? r.x : axis == 1 ? r.y : r.z);
}
static void screen_axis(aqua_call *call, canvas *c, int axis) {
    vec3 point = point_of(call, 0, (int)aqua_argc(call));
    if (aqua_failed(call)) return;
    vec4 r = gfx_v4_transform((vec4){point.x, point.y, point.z, 1}, gfx_canvas_mvp(c));
    if (fabs((double)r.w) < 1e-10) { gfx_fail(call, "Point projects to infinity."); return; }
    double value = axis == 0 ? (double)((r.x / r.w + 1) * (float)c->width / 2) : axis == 1 ? (double)((1 - r.y / r.w) * (float)c->height / 2) : (double)((r.z / r.w + 1) / 2);
    aqua_return_double(call, value);
}
static void p_modelX(aqua_call *call, processing *p, canvas *c) { (void)p; model_axis(call, c, 0); }
static void p_modelY(aqua_call *call, processing *p, canvas *c) { (void)p; model_axis(call, c, 1); }
static void p_modelZ(aqua_call *call, processing *p, canvas *c) { (void)p; model_axis(call, c, 2); }
static void p_screenX(aqua_call *call, processing *p, canvas *c) { (void)p; screen_axis(call, c, 0); }
static void p_screenY(aqua_call *call, processing *p, canvas *c) { (void)p; screen_axis(call, c, 1); }
static void p_screenZ(aqua_call *call, processing *p, canvas *c) { (void)p; screen_axis(call, c, 2); }

/* ---- Camera ---- */

static void p_camera(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    if (n == 0) { gfx_canvas_default_camera(c); return; }
    if (n != 9) { gfx_fail(call, "camera() takes zero or nine values."); return; }
    vec3 eye = point_of(call, 0, 3), center = point_of(call, 3, 3), up = point_of(call, 6, 3);
    if (aqua_failed(call)) return;
    if (gfx_v3_length_squared(gfx_v3_cross(gfx_v3_sub(center, eye), up)) < 1e-12f) { gfx_fail(call, "Camera vectors are degenerate."); return; }
    c->view = gfx_m4_look_at(eye, center, up);
}
static void p_perspective(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    mat4 m;
    if (n == 0) { if (gfx_canvas_perspective(call, PI_F / 3, (float)c->width / (float)c->height, 1, 10000, &m)) { m.m[5] = -m.m[5]; c->projection = m; } return; }
    if (n != 4) { gfx_fail(call, "perspective() takes zero or four values."); return; }
    float fov = gfx_float(call, 0), aspect = gfx_float(call, 1), near = gfx_float(call, 2), far = gfx_float(call, 3);
    if (aqua_failed(call)) return;
    if (gfx_canvas_perspective(call, fov, aspect, near, far, &m)) { m.m[5] = -m.m[5]; c->projection = m; }
}
static void p_ortho(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    mat4 m;
    if (n == 0) { if (gfx_canvas_ortho(call, 0, (float)c->width, (float)c->height, 0, -10000, 10000, &m)) c->projection = m; return; }
    if (n != 4 && n != 6) { gfx_fail(call, "ortho() takes zero, four, or six values."); return; }
    float l = gfx_float(call, 0), r = gfx_float(call, 1), b = gfx_float(call, 2), t = gfx_float(call, 3);
    float near = n == 6 ? gfx_float(call, 4) : -10000, far = n == 6 ? gfx_float(call, 5) : 10000;
    if (aqua_failed(call)) return;
    if (gfx_canvas_ortho(call, l, r, b, t, near, far, &m)) c->projection = m;
}

/* ---- Lights and materials ---- */

static void p_lights(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    c->style.lit = true;
    c->style.ambient = v3(0.5f, 0.5f, 0.5f);
    c->style.light_count = 1;
    c->style.lights[0] = (light){0, v3(0.5f, 0.5f, 0.5f), v3(0, 0, 0), v3(0, 0, -1), v3(1, 0, 0), v3(0, 0, 0), 0, 0};
}
static void p_noLights(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    c->style.lit = false; c->style.light_count = 0; c->style.ambient = v3(0, 0, 0);
}
static void p_ambientLight(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    vec4 color;
    if (!action(call, c) || !gfx_color_arguments(call, &c->style, 0, 3, &color)) return;
    if (!c->style.lit) c->style.ambient = v3(0, 0, 0);
    c->style.ambient = gfx_v3_add(c->style.ambient, v3(color.x, color.y, color.z));
    c->style.lit = true;
}
static void add_light(aqua_call *call, canvas *c, int kind) {
    if (!action(call, c)) return;
    if (c->style.light_count == 8) { gfx_fail(call, "A canvas supports at most eight directional, point, or spot lights."); return; }
    vec4 color;
    if (!gfx_color_arguments(call, &c->style, 0, 3, &color)) return;
    vec3 position = v3(0, 0, 0), direction = v3(0, 0, 1);
    if (kind == 0) direction = normalized(call, gfx_v3_transform_normal(point_of(call, 3, 3), c->model));
    else position = gfx_v3_transform(point_of(call, 3, 3), c->model);
    if (kind == 2) direction = normalized(call, gfx_v3_transform_normal(point_of(call, 6, 3), c->model));
    float cutoff = kind == 2 ? cosf(gfx_float(call, 9)) : 0, concentration = kind == 2 ? gfx_positive(call, 10) : 0;
    if (aqua_failed(call)) return;
    if (!c->style.lit) c->style.ambient = v3(0, 0, 0);
    c->style.lit = true;
    c->style.lights[c->style.light_count++] = (light){kind, v3(color.x, color.y, color.z), position, direction, c->style.falloff, c->style.light_specular, cutoff, concentration};
}
static void p_directionalLight(aqua_call *call, processing *p, canvas *c) { (void)p; add_light(call, c, 0); }
static void p_pointLight(aqua_call *call, processing *p, canvas *c) { (void)p; add_light(call, c, 1); }
static void p_spotLight(aqua_call *call, processing *p, canvas *c) { (void)p; add_light(call, c, 2); }
static void p_lightFalloff(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    vec3 v = point_of(call, 0, 3);
    if (aqua_failed(call)) return;
    if (v.x < 0 || v.y < 0 || v.z < 0 || gfx_v3_length_squared(v) == 0) { gfx_fail(call, "Light falloff coefficients must be nonnegative and not all zero."); return; }
    c->style.falloff = v;
}
static void material(aqua_call *call, canvas *c, vec3 *field) {
    vec4 color;
    if (action(call, c) && gfx_color_arguments(call, &c->style, 0, aqua_argc(call), &color)) *field = v3(color.x, color.y, color.z);
}
static void p_lightSpecular(aqua_call *call, processing *p, canvas *c) { (void)p; material(call, c, &c->style.light_specular); }
static void p_ambient(aqua_call *call, processing *p, canvas *c) { (void)p; material(call, c, &c->style.material_ambient); }
static void p_specular(aqua_call *call, processing *p, canvas *c) { (void)p; material(call, c, &c->style.specular); }
static void p_emissive(aqua_call *call, processing *p, canvas *c) { (void)p; material(call, c, &c->style.emissive); }
static void p_shininess(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; float s = gfx_positive(call, 0); if (!aqua_failed(call)) c->style.shininess = s; }

static void p_box(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    if (n != 1 && n != 3) { gfx_fail(call, "box() takes one or three dimensions."); return; }
    float w = gfx_float(call, 0), h = n == 3 ? gfx_float(call, 1) : w, d = n == 3 ? gfx_float(call, 2) : w;
    if (!aqua_failed(call)) gfx_canvas_box(call, c, w, h, d);
}
static void p_sphere(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float r = gfx_positive(call, 0);
    if (!aqua_failed(call)) gfx_canvas_sphere(call, c, r);
}
static void p_sphereDetail(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    int u = detail_of(call, 0), v = aqua_argc(call) == 2 ? detail_of(call, 1) : u;
    if (!aqua_failed(call)) { c->sphere_u = u; c->sphere_v = v; }
}

/* ---- Immediate shapes ---- */

static void p_beginShape(aqua_call *call, processing *p, canvas *c) {
    if (!action(call, c) || !gfx_require_sketch(call, p) || !gfx_canvas_require_drawing(call, c)) return;
    int kind = aqua_argc(call) == 0 ? 9 : GFX_CHOICE(call, 0, 0, 1, 4, 5, 6, 7, 8, 9);
    if (aqua_failed(call)) return;
    c->shape_mode = kind;
    gfx_canvas_vertices_clear(c);
    if (c->texture_ref) { aqua_release(p->rt, c->texture_ref); c->texture_ref = 0; }
    c->texture = NULL;
}
static void p_beginContour(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (c->shape_mode != 9 || c->in_contour) { gfx_fail(call, "beginContour() requires a polygon and cannot be nested."); return; }
    GFX_PUSH(c->contour_starts, c->contour_count, c->contour_capacity, c->contour_vertex_count);
    c->in_contour = true;
}
static void p_endContour(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (!c->in_contour) { gfx_fail(call, "Call beginContour() first."); return; }
    size_t count;
    gfx_canvas_active_vertices(c, &count);
    if (count < 3) { gfx_fail(call, "Contour needs at least three vertices."); return; }
    c->in_contour = false;
}
static void p_vertex(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (c->shape_mode == -1) { gfx_fail(call, "Call beginShape() first."); return; }
    uint32_t n = aqua_argc(call);
    bool uv = n >= 4;
    vec2 t = {0, 0};
    if (uv) { t.x = gfx_float(call, n - 2); t.y = gfx_float(call, n - 1); }
    vec3 position = point_of(call, 0, n == 3 || n == 5 ? 3 : 2);
    if (aqua_failed(call)) return;
    if (uv && c->texture_mode == 1 && c->texture) {
        float w = c->texture->canvas ? (float)c->texture->canvas->width : (float)c->texture->width;
        float h = c->texture->canvas ? (float)c->texture->canvas->height : (float)c->texture->height;
        t.x /= w; t.y /= h;
    }
    gfx_canvas_push_vertex(c, gfx_canvas_vertex(c, position, c->texture ? c->style.tint : c->style.fill, t));
}
static void p_normal(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    vec3 n = point_of(call, 0, 3);
    if (aqua_failed(call)) return;
    n = normalized(call, n);
    if (!aqua_failed(call)) c->normal = n;
}
static void p_texture(aqua_call *call, processing *p, canvas *c) {
    if (!action(call, c)) return;
    if (c->shape_mode == -1) { gfx_fail(call, "texture() belongs inside beginShape()."); return; }
    pimage *image = gfx_image_argument(call, p, 0);
    if (!image) return;
    if (c->texture_ref) aqua_release(p->rt, c->texture_ref);
    c->texture_ref = aqua_retain(p->rt, aqua_arg(call, 0));
    c->texture = image;
}
static void p_textureMode(aqua_call *call, processing *p, canvas *c) { (void)p; if (!action(call, c)) return; int m = GFX_CHOICE(call, 0, 0, 1); if (!aqua_failed(call)) c->texture_mode = m; }

static void bezier_vertex(aqua_call *call, canvas *c, const float *a, int n) {
    size_t count;
    pvertex *active = gfx_canvas_active_vertices(c, &count);
    if (c->shape_mode != 9 || count == 0 || (n != 6 && n != 9)) { gfx_fail(call, "bezierVertex() needs a POLYGON with an initial vertex and 6 or 9 coordinates."); return; }
    int d = n / 3;
    vec3 controls[4] = {active[count - 1].position, v3(a[0], a[1], d == 3 ? a[2] : 0), v3(a[d], a[d + 1], d == 3 ? a[d + 2] : 0),
                        v3(a[2 * d], a[2 * d + 1], d == 3 ? a[2 * d + 2] : 0)};
    vec3 points[520];
    int samples = gfx_canvas_curve_points(c, false, controls, 1, points, 520);
    if (!gfx_canvas_check(call, samples)) return;
    for (int i = 0; i < samples; i++) gfx_canvas_push_vertex(c, gfx_canvas_vertex(c, points[i], c->style.fill, (vec2){0, 0}));
}
static void p_bezierVertex(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    float a[9];
    int n = (int)aqua_argc(call);
    for (int i = 0; i < n; i++) a[i] = gfx_float(call, (uint32_t)i);
    if (!aqua_failed(call)) bezier_vertex(call, c, a, n);
}
static void p_quadraticVertex(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    float a[6];
    int n = (int)aqua_argc(call);
    for (int i = 0; i < n; i++) a[i] = gfx_float(call, (uint32_t)i);
    if (aqua_failed(call)) return;
    size_t count;
    pvertex *active = gfx_canvas_active_vertices(c, &count);
    if (c->shape_mode != 9 || count == 0 || (n != 4 && n != 6)) { gfx_fail(call, "quadraticVertex() needs an initial polygon vertex and 4 or 6 coordinates."); return; }
    int d = n / 2;
    vec3 start = active[count - 1].position, control = v3(a[0], a[1], d == 3 ? a[2] : 0), end = v3(a[d], a[d + 1], d == 3 ? a[d + 2] : 0);
    vec3 b = gfx_v3_add(start, v3((control.x - start.x) * 2 / 3, (control.y - start.y) * 2 / 3, (control.z - start.z) * 2 / 3));
    vec3 e = gfx_v3_add(end, v3((control.x - end.x) * 2 / 3, (control.y - end.y) * 2 / 3, (control.z - end.z) * 2 / 3));
    if (d == 2) { float bez[6] = {b.x, b.y, e.x, e.y, end.x, end.y}; bezier_vertex(call, c, bez, 6); }
    else { float bez[9] = {b.x, b.y, b.z, e.x, e.y, e.z, end.x, end.y, end.z}; bezier_vertex(call, c, bez, 9); }
}
static void p_curveVertex(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (c->shape_mode != 9) { gfx_fail(call, "curveVertex() belongs in a polygon."); return; }
    vec3 point = point_of(call, 0, (int)aqua_argc(call));
    if (aqua_failed(call)) return;
    GFX_PUSH(c->curve, c->curve_count, c->curve_capacity, point);
    if (c->curve_count < 4) return;
    vec3 points[520];
    int samples = gfx_canvas_curve_points(c, true, c->curve + c->curve_count - 4, c->curve_count == 4 ? 0 : 1, points, 520);
    if (!gfx_canvas_check(call, samples)) return;
    for (int i = 0; i < samples; i++) gfx_canvas_push_vertex(c, gfx_canvas_vertex(c, points[i], c->style.fill, (vec2){0, 0}));
}
static void p_endShape(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!action(call, c)) return;
    if (c->shape_mode == -1 || c->in_contour) { gfx_fail(call, "Finish any contour and call beginShape() first."); return; }
    bool close = aqua_argc(call) == 1 && GFX_CHOICE(call, 0, 0, 1) == 1;
    if (!aqua_failed(call)) gfx_canvas_end_shape(call, c, close);
}

static void p_image(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    uint32_t n = aqua_argc(call);
    if (n != 3 && n != 5) { gfx_fail(call, "image() takes image,x,y[,width,height]."); return; }
    pimage *image = gfx_image_argument(call, p, 0);
    if (!image) return;
    float x = gfx_float(call, 1), y = gfx_float(call, 2), w = n == 5 ? gfx_float(call, 3) : 0, h = n == 5 ? gfx_float(call, 4) : 0;
    if (!aqua_failed(call)) gfx_canvas_image(call, c, image, x, y, n == 5 ? &w : NULL, n == 5 ? &h : NULL);
}

static void p_smooth(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    if (aqua_argc(call) == 1) { GFX_CHOICE(call, 0, 2, 4, 8); if (aqua_failed(call)) return; }
    c->smooth = true;
    gfx_canvas_bind(c);
}
static void p_noSmooth(aqua_call *call, processing *p, canvas *c) { if (!draw(call, p, c)) return; c->smooth = false; gfx_canvas_bind(c); }
static void p_clip(aqua_call *call, processing *p, canvas *c) {
    if (!draw(call, p, c)) return;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), w = gfx_positive(call, 2), h = gfx_positive(call, 3);
    if (aqua_failed(call)) return;
    gfx_canvas_set_clip(c, x, y, w, h);
    gfx_canvas_bind(c);
}
static void p_noClip(aqua_call *call, processing *p, canvas *c) { if (!draw(call, p, c)) return; c->has_clip = false; gfx_canvas_bind(c); }
static void p_blendMode(aqua_call *call, processing *p, canvas *c) { if (!draw(call, p, c)) return; int m = GFX_CHOICE(call, 0, 0, 1, 2, 3, 4); if (!aqua_failed(call)) c->blend_mode = m; }

#define DRAWING(X) \
    X(colorMode, "colorMode|色彩模式", 1, 5) X(color, "color|顏色", 1, 4) X(red, "red|紅色分量", 1, 1) X(green, "green|綠色分量", 1, 1) \
    X(blue, "blue|藍色分量", 1, 1) X(alpha, "alpha|透明度", 1, 1) X(hue, "hue|色相", 1, 1) X(saturation, "saturation|飽和度", 1, 1) \
    X(brightness, "brightness|亮度", 1, 1) X(lerpColor, "lerpColor|色彩插值", 3, 3) X(fill, "fill|填色", 1, 4) X(stroke, "stroke|描邊", 1, 4) \
    X(noFill, "noFill|停用填色", 0, 0) X(noStroke, "noStroke|停用描邊", 0, 0) X(tint, "tint|染色", 1, 4) X(noTint, "noTint|停用染色", 0, 0) \
    X(strokeWeight, "strokeWeight|描邊粗細", 1, 1) X(strokeCap, "strokeCap|描邊端點", 1, 1) X(strokeJoin, "strokeJoin|描邊接角", 1, 1) \
    X(rectMode, "rectMode|矩形模式", 1, 1) X(ellipseMode, "ellipseMode|橢圓模式", 1, 1) X(imageMode, "imageMode|圖片模式", 1, 1) \
    X(background, "background|背景", 1, 4) X(clear, "clear|清除", 0, 0) X(point, "point|點", 2, 3) X(line, "line|線段", 4, 6) \
    X(triangle, "triangle|三角形", 6, 6) X(quad, "quad|四邊形", 8, 8) X(rect, "rect|矩形", 4, 5) X(ellipse, "ellipse|橢圓", 4, 4) \
    X(circle, "circle|圓形", 3, 3) X(square, "square|正方形", 3, 3) X(arc, "arc|圓弧", 6, 7) X(bezierDetail, "bezierDetail|貝茲細節", 1, 1) \
    X(curveDetail, "curveDetail|曲線細節", 1, 1) X(curveTightness, "curveTightness|曲線張力", 1, 1) X(bezier, "bezier|貝茲曲線", 8, 12) \
    X(curve, "curve|曲線", 8, 12) X(pushMatrix, "pushMatrix|推入矩陣", 0, 0) X(popMatrix, "popMatrix|彈出矩陣", 0, 0) \
    X(resetMatrix, "resetMatrix|重設矩陣", 0, 0) X(pushStyle, "pushStyle|推入樣式", 0, 0) X(popStyle, "popStyle|彈出樣式", 0, 0) \
    X(push, "push|推入狀態", 0, 0) X(pop, "pop|彈出狀態", 0, 0) X(translate, "translate|平移", 2, 3) X(scale, "scale|縮放", 1, 3) \
    X(rotate, "rotate|旋轉", 1, 1) X(rotateX, "rotateX|旋轉X", 1, 1) X(rotateY, "rotateY|旋轉Y", 1, 1) X(rotateZ, "rotateZ|旋轉Z", 1, 1) \
    X(shearX, "shearX|剪切X", 1, 1) X(shearY, "shearY|剪切Y", 1, 1) X(applyMatrix, "applyMatrix|套用矩陣", 1, 1) \
    X(getMatrix, "getMatrix|取得矩陣", 0, 0) X(modelX, "modelX|模型X", 2, 3) X(modelY, "modelY|模型Y", 2, 3) X(modelZ, "modelZ|模型Z", 2, 3) \
    X(screenX, "screenX|螢幕X", 2, 3) X(screenY, "screenY|螢幕Y", 2, 3) X(screenZ, "screenZ|螢幕Z", 2, 3) X(camera, "camera|攝影機", 0, 9) \
    X(perspective, "perspective|透視", 0, 4) X(ortho, "ortho|正交投影", 0, 6) X(lights, "lights|燈光", 0, 0) X(noLights, "noLights|停用燈光", 0, 0) \
    X(ambientLight, "ambientLight|環境光", 3, 3) X(directionalLight, "directionalLight|方向光", 6, 6) X(pointLight, "pointLight|點光源", 6, 6) \
    X(spotLight, "spotLight|聚光燈", 11, 11) X(lightFalloff, "lightFalloff|光衰減", 3, 3) X(lightSpecular, "lightSpecular|鏡面光", 3, 3) \
    X(ambient, "ambient|環境材質", 1, 3) X(specular, "specular|鏡面材質", 1, 3) X(emissive, "emissive|自發光", 1, 3) X(shininess, "shininess|光澤度", 1, 1) \
    X(box, "box|方塊", 1, 3) X(sphere, "sphere|球體", 1, 1) X(sphereDetail, "sphereDetail|球體細節", 1, 2) X(beginShape, "beginShape|開始圖形", 0, 1) \
    X(beginContour, "beginContour|開始輪廓", 0, 0) X(endContour, "endContour|結束輪廓", 0, 0) X(vertex, "vertex|頂點", 2, 5) \
    X(normal, "normal|法向量", 3, 3) X(texture, "texture|紋理", 1, 1) X(textureMode, "textureMode|紋理模式", 1, 1) \
    X(bezierVertex, "bezierVertex|貝茲頂點", 6, 9) X(quadraticVertex, "quadraticVertex|二次曲線頂點", 4, 6) X(curveVertex, "curveVertex|曲線頂點", 2, 3) \
    X(endShape, "endShape|結束圖形", 0, 1) X(image, "image|圖片", 3, 5) X(smooth, "smooth|平滑", 0, 1) X(noSmooth, "noSmooth|停用平滑", 0, 0) \
    X(clip, "clip|裁切", 4, 4) X(noClip, "noClip|停用裁切", 0, 0) X(blendMode, "blendMode|混色模式", 1, 1)

DRAWING(CANVAS_WRAPPERS)

void gfx_drawing_register(aqua_class *module, aqua_class *canvas) {
    DRAWING(CANVAS_DEFINE)
}
