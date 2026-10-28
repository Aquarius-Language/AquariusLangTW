/* PShape: retained vertex shapes (createShape()), primitive shapes (createShape(RECT, ...)), groups
   (createShape(GROUP)), OBJ models (loadShape), AQM1 binary meshes (loadMesh) and drawing them with shape(). A shape
   keeps its own style (taken from the canvas when it is created) and matrix; groups hold children (without cycles). */
#include "processing.h"
#include "../common/files.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FAMILY_GROUP, FAMILY_PATH, FAMILY_PRIMITIVE };

typedef struct pshape {
    processing *owner;
    int family, primitive;    /* primitive: POINT 2, LINE 4, TRIANGLE 8, QUAD 16, RECT 30, ELLIPSE 31, ARC 32, SPHERE 40, BOX 41 */
    float params[12]; int param_count;
    pvertex *vertices; size_t count, capacity;
    size_t *contours; size_t contour_count, contour_capacity; bool in_contour;
    style style;
    mat4 matrix;
    int kind;
    bool building, closed, visible, styled;
    struct pshape **children; aqua_ref *child_refs; size_t child_count, child_capacity;
} pshape;

void gfx_shape_finalize(aqua_runtime *rt, void *data) {
    pshape *s = data;
    for (size_t i = 0; i < s->child_count; i++) aqua_release(rt, s->child_refs[i]);
    free(s->children); free(s->child_refs); free(s->vertices); free(s->contours); free(s);
}

/* A new shape with the canvas's current style (Processing's createShape takes fill, stroke and modes from it). */
static pshape *shape_new(processing *p, const canvas *c, int family) {
    pshape *s = calloc(1, sizeof *s);
    s->owner = p;
    s->family = family;
    if (c) {
        s->style = c->style;
        s->style.light_count = 0; s->style.lit = false;
    } else gfx_style_default(&s->style);
    s->matrix = gfx_m4_identity();
    s->kind = 9;
    s->visible = s->styled = true;
    return s;
}

static pshape *shape_argument(aqua_call *call, processing *p, uint32_t index) {
    pshape *s = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, index), gfx_shape_class);
    if (!s || s->owner != p) { gfx_fail(call, "Expected a PShape from this runtime."); return NULL; }
    return s;
}

static bool vertex_index(aqua_call *call, pshape *s, int *index) {
    *index = gfx_int(call, 0);
    if (aqua_failed(call)) return false;
    if (*index < 0 || (size_t)*index >= s->count) { gfx_fail(call, "Vertex index out of range."); return false; }
    return true;
}

static bool path(aqua_call *call, pshape *s) {
    if (s->family == FAMILY_PATH) return true;
    gfx_fail(call, "Only shapes made by createShape() have vertices.");
    return false;
}

/* The shape's own width and height: its vertices' bounds, or the primitive's size. */
static void shape_size(const pshape *s, float *w, float *h) {
    *w = *h = 0;
    if (s->family == FAMILY_PRIMITIVE) {
        const float *a = s->params;
        switch (s->primitive) {
        case 30: case 31: case 32: *w = fabsf(a[2]); *h = fabsf(a[3]); break;
        case 40: *w = *h = 2 * a[0]; break;
        case 41: *w = a[0]; *h = s->param_count == 3 ? a[1] : a[0]; break;
        default: {
            float x0 = INFINITY, y0 = INFINITY, x1 = -INFINITY, y1 = -INFINITY;
            for (int i = 0; i + 1 < s->param_count; i += 2) { x0 = fminf(x0, a[i]); x1 = fmaxf(x1, a[i]); y0 = fminf(y0, a[i + 1]); y1 = fmaxf(y1, a[i + 1]); }
            if (s->param_count >= 2) { *w = x1 - x0; *h = y1 - y0; }
        }
        }
        return;
    }
    float x0 = INFINITY, y0 = INFINITY, x1 = -INFINITY, y1 = -INFINITY;
    for (size_t i = 0; i < s->count; i++) {
        vec3 q = s->vertices[i].position;
        x0 = fminf(x0, q.x); x1 = fmaxf(x1, q.x); y0 = fminf(y0, q.y); y1 = fmaxf(y1, q.y);
    }
    for (size_t i = 0; i < s->child_count; i++) {
        float cw, ch;
        shape_size(s->children[i], &cw, &ch);
        x1 = fmaxf(x1, (x0 == INFINITY ? 0 : x0) + cw); y1 = fmaxf(y1, (y0 == INFINITY ? 0 : y0) + ch);
        if (x0 == INFINITY) x0 = 0;
        if (y0 == INFINITY) y0 = 0;
    }
    if (x0 != INFINITY) { *w = x1 - x0; *h = y1 - y0; }
}

/* ---- PShape members ---- */

static void p_shape_beginShape(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (!path(call, s)) return;
    if (s->building) { gfx_fail(call, "Shape is already being built."); return; }
    int kind = aqua_argc(call) == 0 ? 9 : GFX_CHOICE(call, 0, 0, 1, 4, 5, 6, 7, 8, 9);
    if (aqua_failed(call)) return;
    s->kind = kind; s->count = 0; s->contour_count = 0; s->building = true; s->in_contour = false;
}
static void p_shape_vertex(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (!s->building) { gfx_fail(call, "Call beginShape() first."); return; }
    uint32_t n = aqua_argc(call);
    if (n == 4) { gfx_fail(call, "vertex requires xy, xyz or xyzuv."); return; }
    pvertex v = {v3(gfx_float(call, 0), gfx_float(call, 1), n >= 3 ? gfx_float(call, 2) : 0), v3(0, 0, 1),
                 {n == 5 ? gfx_float(call, 3) : 0, n == 5 ? gfx_float(call, 4) : 0}, s->style.fill};
    if (!aqua_failed(call)) GFX_PUSH(s->vertices, s->count, s->capacity, v);
}
static void p_shape_beginContour(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (!s->building || s->kind != 9 || s->in_contour) { gfx_fail(call, "beginContour() requires a polygon being built and cannot be nested."); return; }
    GFX_PUSH(s->contours, s->contour_count, s->contour_capacity, s->count);
    s->in_contour = true;
}
static void p_shape_endContour(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (!s->in_contour) { gfx_fail(call, "Call beginContour() first."); return; }
    if (s->count - s->contours[s->contour_count - 1] < 3) { gfx_fail(call, "Contour needs at least three vertices."); return; }
    s->in_contour = false;
}
static void p_shape_setNormal(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    int i;
    if (!vertex_index(call, s, &i)) return;
    vec3 normal = v3(gfx_float(call, 1), gfx_float(call, 2), gfx_float(call, 3));
    if (aqua_failed(call)) return;
    if (gfx_v3_length_squared(normal) < 1e-20f) { gfx_fail(call, "Normal must be nonzero."); return; }
    s->vertices[i].normal = gfx_v3_normalize(normal);
}
static void p_shape_endShape(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (!s->building || s->in_contour) { gfx_fail(call, "Finish any contour and call beginShape() first."); return; }
    bool closed = aqua_argc(call) == 1 && GFX_CHOICE(call, 0, 0, 1) == 1;
    if (aqua_failed(call)) return;
    s->building = false;
    s->closed = closed;
}
static void p_shape_fill(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    vec4 color;
    if (gfx_color_arguments(call, &s->style, 0, aqua_argc(call), &color)) { s->style.fill = color; s->style.has_fill = true; }
}
static void p_shape_stroke(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    vec4 color;
    if (gfx_color_arguments(call, &s->style, 0, aqua_argc(call), &color)) { s->style.stroke = color; s->style.has_stroke = true; }
}
static void p_shape_noFill(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->style.has_fill = false; }
static void p_shape_noStroke(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->style.has_stroke = false; }
static void p_shape_strokeWeight(aqua_call *call, processing *p, pshape *s) { (void)p; float w = gfx_nonnegative(call, 0); if (!aqua_failed(call)) s->style.weight = w; }
/* setFill(color) recolors the whole shape (and its vertices); setFill(index, color) one vertex. setFill(false) and
   setStroke(false) turn fill or stroke off. */
static void p_shape_setFill(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (aqua_argc(call) == 2) {
        int i;
        if (!vertex_index(call, s, &i)) return;
        uint32_t packed = gfx_packed(call, 1);
        if (!aqua_failed(call)) s->vertices[i].color = gfx_color_unpack(packed);
        return;
    }
    if (aqua_arg(call, 0).type == AQUA_BOOL) { s->style.has_fill = (aqua_arg(call, 0).number != 0); return; }
    uint32_t packed = gfx_packed(call, 0);
    if (aqua_failed(call)) return;
    s->style.fill = gfx_color_unpack(packed);
    s->style.has_fill = true;
    for (size_t i = 0; i < s->count; i++) s->vertices[i].color = s->style.fill;
    for (size_t i = 0; i < s->child_count; i++) { pshape *child = s->children[i]; child->style.fill = s->style.fill; child->style.has_fill = true; }
}
static void p_shape_setStroke(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (aqua_arg(call, 0).type == AQUA_BOOL) { s->style.has_stroke = (aqua_arg(call, 0).number != 0); return; }
    uint32_t packed = gfx_packed(call, 0);
    if (aqua_failed(call)) return;
    s->style.stroke = gfx_color_unpack(packed);
    s->style.has_stroke = true;
    for (size_t i = 0; i < s->child_count; i++) { pshape *child = s->children[i]; child->style.stroke = s->style.stroke; child->style.has_stroke = true; }
}
static void p_shape_setStrokeWeight(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    float w = gfx_nonnegative(call, 0);
    if (aqua_failed(call)) return;
    s->style.weight = w;
    for (size_t i = 0; i < s->child_count; i++) s->children[i]->style.weight = w;
}
static void p_shape_setVertex(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    int i;
    if (!vertex_index(call, s, &i)) return;
    vec3 position = v3(gfx_float(call, 1), gfx_float(call, 2), aqua_argc(call) == 4 ? gfx_float(call, 3) : 0);
    if (!aqua_failed(call)) s->vertices[i].position = position;
}
static void p_shape_getVertex(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    int i;
    if (!vertex_index(call, s, &i)) return;
    double values[3] = {s->vertices[i].position.x, s->vertices[i].position.y, s->vertices[i].position.z};
    aqua_return(call, aqua_array_from_f64(aqua_call_runtime(call), values, 3, AQUA_DOUBLE));
}
static void p_shape_getVertexCount(aqua_call *call, processing *p, pshape *s) { (void)p; aqua_return_int(call, (int32_t)s->count); }
static void transform_shape(pshape *s, mat4 m) { s->matrix = gfx_m4_multiply(m, s->matrix); }
static void p_shape_translate(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), z = aqua_argc(call) == 3 ? gfx_float(call, 2) : 0;
    if (!aqua_failed(call)) transform_shape(s, gfx_m4_translation(x, y, z));
}
static void p_shape_rotate(aqua_call *call, processing *p, pshape *s) { (void)p; float r = gfx_float(call, 0); if (!aqua_failed(call)) transform_shape(s, gfx_m4_rotation_z(r)); }
static void p_shape_rotateX(aqua_call *call, processing *p, pshape *s) { (void)p; float r = gfx_float(call, 0); if (!aqua_failed(call)) transform_shape(s, gfx_m4_rotation_x(r)); }
static void p_shape_rotateY(aqua_call *call, processing *p, pshape *s) { (void)p; float r = gfx_float(call, 0); if (!aqua_failed(call)) transform_shape(s, gfx_m4_rotation_y(r)); }
static void p_shape_scale(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    uint32_t n = aqua_argc(call);
    float x = gfx_float(call, 0), y = n >= 2 ? gfx_float(call, 1) : x, z = n == 3 ? gfx_float(call, 2) : n == 1 ? x : 1;
    if (!aqua_failed(call)) transform_shape(s, gfx_m4_scale(x, y, z));
}
static void p_shape_resetMatrix(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->matrix = gfx_m4_identity(); }
static void p_shape_setVisible(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    aqua_value v = aqua_arg(call, 0);
    if (v.type != AQUA_BOOL) { gfx_fail(call, "Expected true or false."); return; }
    s->visible = v.number != 0;
}
static void p_shape_isVisible(aqua_call *call, processing *p, pshape *s) { (void)p; aqua_return_bool(call, s->visible); }
static void p_shape_disableStyle(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->styled = false; }
static void p_shape_enableStyle(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->styled = true; }
static void p_shape_getWidth(aqua_call *call, processing *p, pshape *s) { (void)p; float w, h; shape_size(s, &w, &h); aqua_return_double(call, w); }
static void p_shape_getHeight(aqua_call *call, processing *p, pshape *s) { (void)p; float w, h; shape_size(s, &w, &h); aqua_return_double(call, h); }
static bool contains(const pshape *group, const pshape *target) {
    if (group == target) return true;
    for (size_t i = 0; i < group->child_count; i++) if (contains(group->children[i], target)) return true;
    return false;
}
static void p_shape_addChild(aqua_call *call, processing *p, pshape *s) {
    pshape *child = shape_argument(call, p, 0);
    if (!child) return;
    if (contains(child, s)) { gfx_fail(call, "Shape groups cannot contain cycles."); return; }
    aqua_ref ref = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 0));
    GFX_RESERVE(s->children, s->child_capacity, s->child_count + 1);
    s->child_refs = realloc(s->child_refs, s->child_capacity * sizeof *s->child_refs);
    s->children[s->child_count] = child;
    s->child_refs[s->child_count++] = ref;
}
static void p_shape_getChild(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    int i = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    if (i < 0 || (size_t)i >= s->child_count) { gfx_fail(call, "Child index out of range."); return; }
    aqua_return(call, aqua_ref_value(aqua_call_runtime(call), s->child_refs[i]));
}
static void p_shape_getChildCount(aqua_call *call, processing *p, pshape *s) { (void)p; aqua_return_int(call, (int32_t)s->child_count); }

/* ---- Creating shapes ---- */

/* createShape(), createShape(GROUP) or createShape(kind, parameters...) for POINT, LINE, TRIANGLE, QUAD, RECT,
   ELLIPSE, ARC, SPHERE and BOX. */
static void p_createShape(aqua_call *call, processing *p, canvas *c) {
    uint32_t n = aqua_argc(call);
    if (n == 0) { aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, shape_new(p, c, FAMILY_PATH))); return; }
    int kind = GFX_CHOICE(call, 0, 0, 2, 4, 8, 16, 30, 31, 32, 40, 41);
    if (aqua_failed(call)) return;
    if (kind == 0) {
        if (n != 1) { gfx_fail(call, "createShape(GROUP) takes no parameters."); return; }
        aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, shape_new(p, c, FAMILY_GROUP)));
        return;
    }
    uint32_t count = n - 1;
    bool fits = kind == 2 ? count == 2 || count == 3 : kind == 4 ? count == 4 || count == 6 : kind == 8 ? count == 6 : kind == 16 ? count == 8 :
                kind == 30 ? count == 4 || count == 5 : kind == 31 ? count == 4 : kind == 32 ? count == 6 || count == 7 : kind == 40 ? count == 1 :
                count == 1 || count == 3;
    if (!fits) { gfx_fail(call, "Wrong number of parameters for this shape kind."); return; }
    pshape *s = shape_new(p, c, FAMILY_PRIMITIVE);
    s->primitive = kind;
    s->param_count = (int)count;
    for (uint32_t i = 0; i < count; i++) s->params[i] = gfx_float(call, i + 1);
    if (kind == 32 && count == 7) s->params[6] = (float)GFX_CHOICE(call, 7, 0, 1, 2);
    if (aqua_failed(call)) { gfx_shape_finalize(aqua_call_runtime(call), s); return; }
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, s));
}

static uint32_t u32_le(const uint8_t *b) { return (uint32_t)b[0] | (uint32_t)b[1] << 8 | (uint32_t)b[2] << 16 | (uint32_t)b[3] << 24; }

static void p_loadMesh(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_require_sketch(call, p)) return;
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char error[1024];
    gfx_bytes file;
    if (!gfx_read_resource(aqua_call_runtime(call), path.data, &file, error, sizeof error)) { gfx_fail(call, "%s", error); return; }
    const uint8_t *b = file.data;
    if (file.length < 16) { gfx_bytes_free(&file); gfx_fail(call, "Invalid mesh header."); return; }
    uint32_t count = u32_le(b + 4);
    if (u32_le(b) != 0x314d5141u || u32_le(b + 8) != 8 || u32_le(b + 12) != 0 || count == 0 || count > 1000000 || count % 3 != 0 ||
        (uint64_t)file.length != 16ull + (uint64_t)count * 32ull) {
        gfx_bytes_free(&file); gfx_fail(call, "Invalid mesh header or length."); return;
    }
    pshape *s = shape_new(p, NULL, FAMILY_PATH);
    (void)c;
    s->kind = 4;
    s->style.has_stroke = false;
    s->vertices = malloc((size_t)count * sizeof *s->vertices);
    s->count = s->capacity = count;
    for (uint32_t i = 0; i < count; i++) {
        float f[8];
        memcpy(f, b + 16 + (size_t)i * 32, sizeof f);
        for (int k = 0; k < 8; k++) if (!isfinite(f[k])) { gfx_bytes_free(&file); gfx_shape_finalize(aqua_call_runtime(call), s); gfx_fail(call, "Mesh components must be finite."); return; }
        double nx = f[3], ny = f[4], nz = f[5];
        if (nx * nx + ny * ny + nz * nz < 1e-20) { gfx_bytes_free(&file); gfx_shape_finalize(aqua_call_runtime(call), s); gfx_fail(call, "Mesh normal must be nonzero."); return; }
        s->vertices[i] = (pvertex){v3(f[0], f[1], f[2]), gfx_v3_normalize(v3(f[3], f[4], f[5])), {f[6], f[7]}, {1, 1, 1, 1}};
    }
    gfx_bytes_free(&file);
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, s));
}

/* ---- OBJ models (loadShape) ---- */

typedef struct obj_lists { vec3 *positions, *normals; vec2 *uvs; size_t np, nn, nt, cp, cn, ct; } obj_lists;

/* An OBJ index (1-based, negative from the end) into a list of `count`; -1 when absent or out of range. */
static long obj_index(const char *text, size_t count) {
    if (!*text) return -1;
    long i = strtol(text, NULL, 10);
    if (i < 0) i += (long)count;
    else i -= 1;
    return i >= 0 && (size_t)i < count ? i : -1;
}

/* Parses v, vt, vn and f lines (polygons as fans; normals computed per face when the file has none). Other
   statements (materials, groups, smoothing) are ignored. */
static bool parse_obj(pshape *s, char *text, char *error, size_t size) {
    obj_lists l = {0};
    bool ok = true;
    for (char *line = text; line && ok;) {
        char *next = strchr(line, '\n');
        if (next) *next++ = 0;
        while (*line == ' ' || *line == '\t') line++;
        if (line[0] == 'v' && (line[1] == ' ' || line[1] == '\t')) {
            vec3 v = v3(0, 0, 0);
            sscanf(line + 2, "%f %f %f", &v.x, &v.y, &v.z);
            GFX_PUSH(l.positions, l.np, l.cp, v);
        } else if (line[0] == 'v' && line[1] == 'n') {
            vec3 v = v3(0, 0, 1);
            sscanf(line + 2, "%f %f %f", &v.x, &v.y, &v.z);
            GFX_PUSH(l.normals, l.nn, l.cn, v);
        } else if (line[0] == 'v' && line[1] == 't') {
            vec2 t = {0, 0};
            sscanf(line + 2, "%f %f", &t.x, &t.y);
            t.y = 1 - t.y; /* OBJ's v grows upwards; images are top-down */
            GFX_PUSH(l.uvs, l.nt, l.ct, t);
        } else if (line[0] == 'f' && (line[1] == ' ' || line[1] == '\t')) {
            pvertex corners[64];
            int n = 0;
            for (char *token = strtok(line + 2, " \t\r"); token && n < 64; token = strtok(NULL, " \t\r")) {
                char *slash1 = strchr(token, '/'), *slash2 = slash1 ? strchr(slash1 + 1, '/') : NULL;
                if (slash1) *slash1 = 0;
                if (slash2) *slash2 = 0;
                long vi = obj_index(token, l.np), ti = slash1 ? obj_index(slash1 + 1, l.nt) : -1, ni = slash2 ? obj_index(slash2 + 1, l.nn) : -1;
                if (vi < 0) { snprintf(error, size, "OBJ face refers to a missing vertex."); ok = false; break; }
                corners[n++] = (pvertex){l.positions[vi], ni >= 0 ? l.normals[ni] : v3(0, 0, 0), ti >= 0 ? l.uvs[ti] : (vec2){0, 0}, s->style.fill};
            }
            if (ok && n >= 3) {
                vec3 face = gfx_v3_cross(gfx_v3_sub(corners[1].position, corners[0].position), gfx_v3_sub(corners[2].position, corners[0].position));
                if (gfx_v3_length_squared(face) > 1e-20f) face = gfx_v3_normalize(face);
                else face = v3(0, 0, 1);
                for (int i = 0; i < n; i++) if (gfx_v3_length_squared(corners[i].normal) < 1e-20f) corners[i].normal = face;
                for (int i = 1; i + 1 < n; i++) {
                    GFX_PUSH(s->vertices, s->count, s->capacity, corners[0]);
                    GFX_PUSH(s->vertices, s->count, s->capacity, corners[i]);
                    GFX_PUSH(s->vertices, s->count, s->capacity, corners[i + 1]);
                }
            }
        }
        line = next;
    }
    free(l.positions); free(l.normals); free(l.uvs);
    if (ok && s->count == 0) { snprintf(error, size, "OBJ file has no faces."); ok = false; }
    return ok;
}

static void p_loadShape(aqua_call *call, processing *p, canvas *c) {
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char extension[16], error[1024];
    gfx_extension(path.data, extension, sizeof extension);
    if (strcmp(extension, ".obj") != 0) { gfx_fail(call, "loadShape() reads .obj models."); return; }
    gfx_bytes file;
    if (!gfx_read_resource(aqua_call_runtime(call), path.data, &file, error, sizeof error)) { gfx_fail(call, "%s", error); return; }
    char *text = malloc(file.length + 1);
    memcpy(text, file.data, file.length);
    text[file.length] = 0;
    gfx_bytes_free(&file);
    pshape *s = shape_new(p, c, FAMILY_PATH);
    s->kind = 4;
    s->style.has_stroke = false;
    bool ok = parse_obj(s, text, error, sizeof error);
    free(text);
    if (!ok) { gfx_shape_finalize(aqua_call_runtime(call), s); gfx_fail(call, "%s", error); return; }
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, s));
}

/* ---- Drawing shapes ---- */

static bool render_primitive(aqua_call *call, canvas *c, const pshape *s) {
    const float *a = s->params;
    switch (s->primitive) {
    case 2: return gfx_canvas_point(call, c, v3(a[0], a[1], s->param_count == 3 ? a[2] : 0));
    case 4: return s->param_count == 6 ? gfx_canvas_segment(call, c, v3(a[0], a[1], a[2]), v3(a[3], a[4], a[5]))
                                        : gfx_canvas_segment(call, c, v3(a[0], a[1], 0), v3(a[2], a[3], 0));
    case 8: { vec3 q[3] = {v3(a[0], a[1], 0), v3(a[2], a[3], 0), v3(a[4], a[5], 0)}; return gfx_canvas_polygon(call, c, q, 3, true, true); }
    case 16: { vec3 q[4] = {v3(a[0], a[1], 0), v3(a[2], a[3], 0), v3(a[4], a[5], 0), v3(a[6], a[7], 0)}; return gfx_canvas_polygon(call, c, q, 4, true, true); }
    case 30: return gfx_canvas_rect(call, c, a[0], a[1], a[2], a[3], s->param_count == 5 ? fmaxf(0, a[4]) : -1);
    case 31: return gfx_canvas_ellipse(call, c, a[0], a[1], a[2], a[3], 0, 6.28318530717958647692f, 2, false);
    case 32: return gfx_canvas_ellipse(call, c, a[0], a[1], a[2], a[3], a[4], a[5], s->param_count == 7 ? (int)a[6] : 0, true);
    case 40: return gfx_canvas_sphere(call, c, a[0]);
    default: return gfx_canvas_box(call, c, a[0], s->param_count == 3 ? a[1] : a[0], s->param_count == 3 ? a[2] : a[0]);
    }
}

static bool render(aqua_call *call, canvas *c, pshape *s) {
    if (s->building) { gfx_fail(call, "Finish the retained shape with endShape()."); return false; }
    if (!s->visible) return true;
    style old_style = c->style;
    mat4 old_model = c->model;
    if (s->styled) {
        c->style = s->style;
        c->style.lit = old_style.lit;
        c->style.ambient = old_style.ambient;
        memcpy(c->style.lights, old_style.lights, sizeof old_style.lights);
        c->style.light_count = old_style.light_count;
    }
    c->model = gfx_m4_multiply(s->matrix, old_model);
    bool ok = true;
    if (s->family == FAMILY_PRIMITIVE) ok = render_primitive(call, c, s);
    else if (s->family == FAMILY_PATH && s->count) {
        gfx_canvas_vertices_clear(c);
        /* The boundary (vertices before the first contour), then each contour as a hole. */
        size_t boundary = s->contour_count ? s->contours[0] : s->count;
        for (size_t i = 0; i < boundary; i++) {
            pvertex v = s->vertices[i];
            if (!s->styled) v.color = c->style.fill;
            GFX_PUSH(c->vertices, c->vertex_count, c->vertex_capacity, v);
        }
        for (size_t k = 0; k < s->contour_count; k++) {
            GFX_PUSH(c->contour_starts, c->contour_count, c->contour_capacity, c->contour_vertex_count);
            size_t end = k + 1 < s->contour_count ? s->contours[k + 1] : s->count;
            for (size_t i = s->contours[k]; i < end; i++) GFX_PUSH(c->contour_vertices, c->contour_vertex_count, c->contour_vertex_capacity, s->vertices[i]);
        }
        c->shape_mode = s->kind;
        ok = gfx_canvas_end_shape(call, c, s->closed);
    }
    for (size_t i = 0; ok && i < s->child_count; i++) ok = render(call, c, s->children[i]);
    c->style = old_style;
    c->model = old_model;
    return ok;
}

/* shape(s[, x, y[, w, h]]) placed by shapeMode(): CORNER (x, y the corner), CORNERS (w, h the far corner), CENTER. */
static void p_shape(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_require_sketch(call, p) || !gfx_canvas_require_drawing(call, c)) return;
    uint32_t n = aqua_argc(call);
    if (n != 1 && n != 3 && n != 5) { gfx_fail(call, "shape() takes a shape[, x, y[, width, height]]."); return; }
    pshape *s = shape_argument(call, p, 0);
    if (!s) return;
    float x = n >= 3 ? gfx_float(call, 1) : 0, y = n >= 3 ? gfx_float(call, 2) : 0;
    float w = n == 5 ? gfx_float(call, 3) : 0, h = n == 5 ? gfx_float(call, 4) : 0;
    if (aqua_failed(call)) return;
    float sw, sh;
    shape_size(s, &sw, &sh);
    int mode = c->style.pshape_mode;
    mat4 old = c->model;
    if (n == 5) {
        if (mode == 1) { w -= x; h -= y; }
        else if (mode == 2) { x -= w / 2; y -= h / 2; }
        mat4 placement = gfx_m4_translation(x, y, 0);
        if (sw > 0 && sh > 0) placement = gfx_m4_multiply(gfx_m4_scale(w / sw, h / sh, 1), placement);
        c->model = gfx_m4_multiply(placement, c->model);
    } else if (n == 3) {
        if (mode == 2) { x -= sw / 2; y -= sh / 2; }
        c->model = gfx_m4_multiply(gfx_m4_translation(x, y, 0), c->model);
    }
    render(call, c, s);
    c->model = old;
}

#define SHAPE_MEMBERS(X) \
    X(shape_beginShape, "beginShape|開始圖形", 0, 1) X(shape_vertex, "vertex|頂點", 2, 5) X(shape_setNormal, "setNormal|設定法線", 4, 4) \
    X(shape_beginContour, "beginContour|開始輪廓", 0, 0) X(shape_endContour, "endContour|結束輪廓", 0, 0) \
    X(shape_endShape, "endShape|結束圖形", 0, 1) X(shape_fill, "fill|填色", 1, 4) X(shape_stroke, "stroke|描邊", 1, 4) \
    X(shape_noFill, "noFill|停用填色", 0, 0) X(shape_noStroke, "noStroke|停用描邊", 0, 0) X(shape_strokeWeight, "strokeWeight|描邊粗細", 1, 1) \
    X(shape_setFill, "setFill|設定填色", 1, 2) X(shape_setStroke, "setStroke|設定描邊", 1, 1) X(shape_setStrokeWeight, "setStrokeWeight|設定描邊粗細", 1, 1) \
    X(shape_setVertex, "setVertex|設定頂點", 3, 4) X(shape_getVertex, "getVertex|取得頂點", 1, 1) \
    X(shape_getVertexCount, "getVertexCount|取得頂點數量", 0, 0) X(shape_translate, "translate|平移", 2, 3) X(shape_rotate, "rotate|旋轉", 1, 1) \
    X(shape_rotateX, "rotateX|旋轉X", 1, 1) X(shape_rotateY, "rotateY|旋轉Y", 1, 1) X(shape_rotateZ, "rotateZ|旋轉Z", 1, 1) \
    X(shape_scale, "scale|縮放", 1, 3) X(shape_resetMatrix, "resetMatrix|重設矩陣", 0, 0) X(shape_addChild, "addChild|加入子圖形", 1, 1) \
    X(shape_getChild, "getChild|取得子圖形", 1, 1) X(shape_getChildCount, "getChildCount|取得子圖形數量", 0, 0) \
    X(shape_setVisible, "setVisible|設定可見", 1, 1) X(shape_isVisible, "isVisible|是否可見", 0, 0) \
    X(shape_disableStyle, "disableStyle|停用樣式", 0, 0) X(shape_enableStyle, "enableStyle|啟用樣式", 0, 0) \
    X(shape_getWidth, "getWidth|取得寬度", 0, 0) X(shape_getHeight, "getHeight|取得高度", 0, 0)
#define SHAPE_WRAPPER(name, names, minimum, maximum) OBJECT_WRAPPER(pshape, name, names, minimum, maximum)
#define SHAPE_DEFINE(name, names, minimum, maximum) OBJECT_DEFINE(shape, name, names, minimum, maximum)
static void p_shape_rotateZ(aqua_call *call, processing *p, pshape *s) { p_shape_rotate(call, p, s); }
SHAPE_MEMBERS(SHAPE_WRAPPER)

#define SHAPE_CANVAS(X) \
    X(createShape, "createShape|建立圖形", 0, 12) X(loadShape, "loadShape|載入圖形", 1, 1) X(loadMesh, "loadMesh|載入網格", 1, 1) \
    X(shape, "shape|圖形", 1, 5)
SHAPE_CANVAS(CANVAS_WRAPPERS)

void gfx_shapes_register(aqua_class *module, aqua_class *canvas, aqua_class *shape) {
    SHAPE_MEMBERS(SHAPE_DEFINE)
    SHAPE_CANVAS(CANVAS_DEFINE)
}
