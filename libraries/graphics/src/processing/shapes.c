/* PShape: retained vertex shapes (createShape), AQM1 binary meshes (loadMesh) and drawing them with shape(). A shape
   keeps its own style and matrix; groups hold children (without cycles). */
#include "processing.h"
#include "../common/files.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

typedef struct pshape {
    processing *owner;
    pvertex *vertices; size_t count, capacity;
    style style;
    mat4 matrix;
    int kind;
    bool building, closed;
    struct pshape **children; aqua_ref *child_refs; size_t child_count, child_capacity;
} pshape;

void gfx_shape_finalize(aqua_runtime *rt, void *data) {
    pshape *s = data;
    for (size_t i = 0; i < s->child_count; i++) aqua_release(rt, s->child_refs[i]);
    free(s->children); free(s->child_refs); free(s->vertices); free(s);
}

static pshape *shape_new(processing *p) {
    pshape *s = calloc(1, sizeof *s);
    s->owner = p;
    gfx_style_default(&s->style);
    s->matrix = gfx_m4_identity();
    s->kind = 9;
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

/* ---- PShape members ---- */

static void p_shape_beginShape(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    if (s->building) { gfx_fail(call, "Shape is already being built."); return; }
    int kind = aqua_argc(call) == 0 ? 9 : GFX_CHOICE(call, 0, 0, 1, 4, 5, 6, 7, 8, 9);
    if (aqua_failed(call)) return;
    s->kind = kind; s->count = 0; s->building = true;
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
    if (!s->building) { gfx_fail(call, "Call beginShape() first."); return; }
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
static void p_shape_setFill(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    uint32_t packed = gfx_packed(call, 0);
    if (aqua_failed(call)) return;
    s->style.fill = gfx_color_unpack(packed);
    s->style.has_fill = true;
    for (size_t i = 0; i < s->count; i++) s->vertices[i].color = s->style.fill;
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
static void p_shape_translate(aqua_call *call, processing *p, pshape *s) {
    (void)p;
    float x = gfx_float(call, 0), y = gfx_float(call, 1), z = aqua_argc(call) == 3 ? gfx_float(call, 2) : 0;
    if (!aqua_failed(call)) s->matrix = gfx_m4_multiply(gfx_m4_translation(x, y, z), s->matrix);
}
static void p_shape_rotate(aqua_call *call, processing *p, pshape *s) { (void)p; float r = gfx_float(call, 0); if (!aqua_failed(call)) s->matrix = gfx_m4_multiply(gfx_m4_rotation_z(r), s->matrix); }
static void p_shape_scale(aqua_call *call, processing *p, pshape *s) { (void)p; float k = gfx_float(call, 0); if (!aqua_failed(call)) s->matrix = gfx_m4_multiply(gfx_m4_scale(k, k, k), s->matrix); }
static void p_shape_resetMatrix(aqua_call *call, processing *p, pshape *s) { (void)call; (void)p; s->matrix = gfx_m4_identity(); }
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

/* ---- Canvas members ---- */

static void p_createShape(aqua_call *call, processing *p, canvas *c) {
    (void)c;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), gfx_shape_class, shape_new(p)));
}

static uint32_t u32_le(const uint8_t *b) { return (uint32_t)b[0] | (uint32_t)b[1] << 8 | (uint32_t)b[2] << 16 | (uint32_t)b[3] << 24; }

static void p_loadMesh(aqua_call *call, processing *p, canvas *c) {
    (void)c;
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
    pshape *s = shape_new(p);
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

static bool render(aqua_call *call, canvas *c, pshape *s) {
    if (s->building) { gfx_fail(call, "Finish the retained shape with endShape()."); return false; }
    style old_style = c->style;
    mat4 old_model = c->model;
    c->style = s->style;
    c->style.lit = old_style.lit;
    c->style.ambient = old_style.ambient;
    memcpy(c->style.lights, old_style.lights, sizeof old_style.lights);
    c->style.light_count = old_style.light_count;
    c->model = gfx_m4_multiply(s->matrix, old_model);
    gfx_canvas_vertices_clear(c);
    for (size_t i = 0; i < s->count; i++) GFX_PUSH(c->vertices, c->vertex_count, c->vertex_capacity, s->vertices[i]);
    c->shape_mode = s->kind;
    bool ok = gfx_canvas_end_shape(call, c, s->closed);
    for (size_t i = 0; ok && i < s->child_count; i++) ok = render(call, c, s->children[i]);
    c->style = old_style;
    c->model = old_model;
    return ok;
}

static void p_shape(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_require_sketch(call, p) || !gfx_canvas_require_drawing(call, c)) return;
    uint32_t n = aqua_argc(call);
    if (n == 2) { gfx_fail(call, "shape() takes a shape[,x,y]."); return; }
    pshape *s = shape_argument(call, p, 0);
    if (!s) return;
    float x = n == 3 ? gfx_float(call, 1) : 0, y = n == 3 ? gfx_float(call, 2) : 0;
    if (aqua_failed(call)) return;
    mat4 old = c->model;
    if (n == 3) c->model = gfx_m4_multiply(gfx_m4_translation(x, y, 0), c->model);
    render(call, c, s);
    c->model = old;
}

#define SHAPE_MEMBERS(X) \
    X(shape_beginShape, "beginShape|開始圖形", 0, 1) X(shape_vertex, "vertex|頂點", 2, 5) X(shape_setNormal, "setNormal|設定法線", 4, 4) \
    X(shape_endShape, "endShape|結束圖形", 0, 1) X(shape_fill, "fill|填色", 1, 4) X(shape_stroke, "stroke|描邊", 1, 4) \
    X(shape_noFill, "noFill|停用填色", 0, 0) X(shape_noStroke, "noStroke|停用描邊", 0, 0) X(shape_strokeWeight, "strokeWeight|描邊粗細", 1, 1) \
    X(shape_setFill, "setFill|設定填色", 1, 1) X(shape_setVertex, "setVertex|設定頂點", 3, 4) X(shape_getVertex, "getVertex|取得頂點", 1, 1) \
    X(shape_getVertexCount, "getVertexCount|取得頂點數量", 0, 0) X(shape_translate, "translate|平移", 2, 3) X(shape_rotate, "rotate|旋轉", 1, 1) \
    X(shape_scale, "scale|縮放", 1, 1) X(shape_resetMatrix, "resetMatrix|重設矩陣", 0, 0) X(shape_addChild, "addChild|加入子圖形", 1, 1)
#define SHAPE_WRAPPER(name, names, minimum, maximum) OBJECT_WRAPPER(pshape, name, names, minimum, maximum)
#define SHAPE_DEFINE(name, names, minimum, maximum) OBJECT_DEFINE(shape, name, names, minimum, maximum)
SHAPE_MEMBERS(SHAPE_WRAPPER)

#define SHAPE_CANVAS(X) X(createShape, "createShape|建立圖形", 0, 0) X(loadMesh, "loadMesh|載入網格", 1, 1) X(shape, "shape|圖形", 1, 3)
SHAPE_CANVAS(CANVAS_WRAPPERS)

void gfx_shapes_register(aqua_class *module, aqua_class *canvas, aqua_class *shape) {
    SHAPE_MEMBERS(SHAPE_DEFINE)
    SHAPE_CANVAS(CANVAS_DEFINE)
}
