/* PVector (createVector / PVector): x, y, z read-outs and single-precision vector math; mutating methods return the
   vector. Arguments may be vectors or two-/three-element numeric arrays. */
#include "processing.h"
#include <math.h>
#include <stdlib.h>

typedef struct pvector { vec3 v; } pvector;

static aqua_value vector_new(aqua_runtime *rt, vec3 v) {
    pvector *vector = malloc(sizeof *vector);
    vector->v = v;
    return aqua_object_new(rt, gfx_vector_class, vector);
}

static bool read_vector(aqua_call *call, uint32_t index, vec3 *out) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value value = aqua_arg(call, index);
    if (value.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, value);
        if (n != 2 && n != 3) { gfx_fail(call, "Expected two or three vector components."); return false; }
        float c[3] = {0, 0, 0};
        for (uint32_t i = 0; i < n; i++) {
            double d;
            if (!gfx_finite_number(aqua_array_get(rt, value, i), &d)) { gfx_fail(call, "Expected a finite number."); return false; }
            c[i] = (float)d;
            if (!isfinite(c[i])) { gfx_fail(call, "Number exceeds float range."); return false; }
        }
        *out = v3(c[0], c[1], c[2]);
        return true;
    }
    pvector *other = aqua_object_data(rt, value, gfx_vector_class);
    if (!other) { gfx_fail(call, "Expected a PVector or numeric array."); return false; }
    *out = other->v;
    return true;
}

static void mutated(aqua_call *call, pvector *self, vec3 v) { self->v = v; aqua_return(call, aqua_self_value(call)); }

static void p_set(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    vec3 v = v3(gfx_float(call, 0), gfx_float(call, 1), aqua_argc(call) == 3 ? gfx_float(call, 2) : 0);
    if (!aqua_failed(call)) mutated(call, self, v);
}
static void p_add(aqua_call *call, processing *p, pvector *self) { (void)p; vec3 o; if (read_vector(call, 0, &o)) mutated(call, self, gfx_v3_add(self->v, o)); }
static void p_sub(aqua_call *call, processing *p, pvector *self) { (void)p; vec3 o; if (read_vector(call, 0, &o)) mutated(call, self, gfx_v3_sub(self->v, o)); }
static void p_mult(aqua_call *call, processing *p, pvector *self) { (void)p; float n = gfx_float(call, 0); if (!aqua_failed(call)) mutated(call, self, gfx_v3_scale(self->v, n)); }
static void p_div(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    float n = gfx_float(call, 0);
    if (aqua_failed(call)) return;
    if (n == 0) { gfx_fail(call, "Division by zero."); return; }
    mutated(call, self, v3(self->v.x / n, self->v.y / n, self->v.z / n));
}
static void p_normalize(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    mutated(call, self, gfx_v3_length_squared(self->v) == 0 ? self->v : gfx_v3_normalize(self->v));
}
static void p_setMag(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    float n = gfx_float(call, 0);
    if (!aqua_failed(call)) mutated(call, self, gfx_v3_length_squared(self->v) == 0 ? self->v : gfx_v3_scale(gfx_v3_normalize(self->v), n));
}
static void p_limit(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    float max = gfx_positive(call, 0);
    if (!aqua_failed(call)) mutated(call, self, gfx_v3_length(self->v) > max ? gfx_v3_scale(gfx_v3_normalize(self->v), max) : self->v);
}
static void p_rotate(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    float r = gfx_float(call, 0);
    if (!aqua_failed(call)) mutated(call, self, gfx_v3_transform(self->v, gfx_m4_rotation_z(r)));
}
static void p_lerp(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    vec3 o;
    if (!read_vector(call, 0, &o)) return;
    float t = gfx_float(call, 1);
    if (!aqua_failed(call)) mutated(call, self, gfx_v3_lerp(self->v, o, t));
}
static void p_copy(aqua_call *call, processing *p, pvector *self) { (void)p; aqua_return(call, vector_new(aqua_call_runtime(call), self->v)); }
static void p_array(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    double values[3] = {self->v.x, self->v.y, self->v.z};
    aqua_return(call, aqua_array_from_f64(aqua_call_runtime(call), values, 3, AQUA_DOUBLE));
}
static void p_mag(aqua_call *call, processing *p, pvector *self) { (void)p; aqua_return_double(call, gfx_v3_length(self->v)); }
static void p_magSq(aqua_call *call, processing *p, pvector *self) { (void)p; aqua_return_double(call, gfx_v3_length_squared(self->v)); }
static void p_heading(aqua_call *call, processing *p, pvector *self) { (void)p; aqua_return_double(call, atan2((double)self->v.y, (double)self->v.x)); }
static void p_dot(aqua_call *call, processing *p, pvector *self) { (void)p; vec3 o; if (read_vector(call, 0, &o)) aqua_return_double(call, gfx_v3_dot(self->v, o)); }
static void p_cross(aqua_call *call, processing *p, pvector *self) { (void)p; vec3 o; if (read_vector(call, 0, &o)) aqua_return(call, vector_new(aqua_call_runtime(call), gfx_v3_cross(self->v, o))); }
static void p_dist(aqua_call *call, processing *p, pvector *self) { (void)p; vec3 o; if (read_vector(call, 0, &o)) aqua_return_double(call, gfx_v3_distance(self->v, o)); }
static void p_angleBetween(aqua_call *call, processing *p, pvector *self) {
    (void)p;
    vec3 o;
    if (!read_vector(call, 0, &o)) return;
    float d = gfx_v3_length(self->v) * gfx_v3_length(o);
    if (d == 0) { gfx_fail(call, "Angle is undefined for a zero vector."); return; }
    float c = gfx_v3_dot(self->v, o) / d;
    c = c < -1 ? -1 : c > 1 ? 1 : c;
    aqua_return_double(call, acos((double)c));
}

static void p_createVector(aqua_call *call, processing *p) {
    (void)p;
    vec3 v = v3(gfx_float(call, 0), gfx_float(call, 1), aqua_argc(call) == 3 ? gfx_float(call, 2) : 0);
    if (!aqua_failed(call)) aqua_return(call, vector_new(aqua_call_runtime(call), v));
}
static void p_PVector(aqua_call *call, processing *p) { p_createVector(call, p); }

static void x_property(aqua_call *call) { aqua_return(call, aqua_float(((pvector *)aqua_self(call))->v.x)); }
static void y_property(aqua_call *call) { aqua_return(call, aqua_float(((pvector *)aqua_self(call))->v.y)); }
static void z_property(aqua_call *call) { aqua_return(call, aqua_float(((pvector *)aqua_self(call))->v.z)); }

#define VECTOR_MEMBERS(X) \
    X(set, "set|設定", 2, 3) X(add, "add|相加", 1, 1) X(sub, "sub|相減", 1, 1) X(mult, "mult|相乘", 1, 1) X(div, "div|相除", 1, 1) \
    X(normalize, "normalize|正規化", 0, 0) X(setMag, "setMag|設定長度", 1, 1) X(limit, "limit|限制長度", 1, 1) X(rotate, "rotate|旋轉", 1, 1) \
    X(lerp, "lerp|線性插值", 2, 2) X(copy, "copy|複製", 0, 0) X(array, "array|轉陣列", 0, 0) X(mag, "mag|長度", 0, 0) \
    X(magSq, "magSq|長度平方", 0, 0) X(heading, "heading|方向角", 0, 0) X(dot, "dot|內積", 1, 1) X(cross, "cross|外積", 1, 1) \
    X(dist, "dist|距離", 1, 1) X(angleBetween, "angleBetween|夾角", 1, 1)
#define VECTOR_WRAPPER(name, names, minimum, maximum) OBJECT_WRAPPER(pvector, name, names, minimum, maximum)
#define VECTOR_DEFINE(name, names, minimum, maximum) OBJECT_DEFINE(vector, name, names, minimum, maximum)
VECTOR_MEMBERS(VECTOR_WRAPPER)

#define VECTOR_MODULE(X) X(createVector, "createVector|建立向量", 2, 3) X(PVector, "PVector|向量", 2, 3)
VECTOR_MODULE(MODULE_WRAPPER)

void gfx_vector_register(aqua_class *module, aqua_class *vector) {
    VECTOR_MEMBERS(VECTOR_DEFINE)
    aqua_define_property(vector, "x", x_property);
    aqua_define_property(vector, "y", y_property);
    aqua_define_property(vector, "z", z_property);
    VECTOR_MODULE(MODULE_DEFINE)
}
