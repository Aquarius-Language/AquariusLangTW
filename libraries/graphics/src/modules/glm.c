/* GLM: OpenGL-style vector and matrix helpers. Vectors are 3-element arrays; matrices are 16-element arrays in GL
   column-major order. The math is System.Numerics' single precision (common/matrix.h), whose row-vector matrices
   serialize row by row into exactly the column-major GL layout, so Multiply(a, b) composes as b * a there. */
#include "../graphics.h"
#include "../common/matrix.h"
#include <math.h>

#define PI 3.14159265358979323846

static bool vector_argument(aqua_call *call, uint32_t index, vec3 *out) {
    aqua_value array = gfx_array(call, index);
    if (aqua_failed(call)) return false;
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_array_length(rt, array) != 3) { gfx_fail(call, "Expected a 3-element vector."); return false; }
    double c[3];
    for (uint32_t i = 0; i < 3; i++)
        if (!gfx_finite_number(aqua_array_get(rt, array, i), &c[i])) { gfx_fail(call, "Expected a finite number."); return false; }
    *out = v3((float)c[0], (float)c[1], (float)c[2]);
    return true;
}

static bool matrix_argument_16(aqua_call *call, uint32_t index, mat4 *out) {
    aqua_value array = gfx_array(call, index);
    if (aqua_failed(call)) return false;
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_array_length(rt, array) != 16) { gfx_fail(call, "Expected a 16-element column-major matrix."); return false; }
    for (uint32_t i = 0; i < 16; i++) {
        double d;
        if (!gfx_finite_number(aqua_array_get(rt, array, i), &d)) { gfx_fail(call, "Expected a finite number."); return false; }
        out->m[i] = (float)d;
    }
    return true;
}

static bool normalized(aqua_call *call, vec3 v, vec3 *out) {
    if (gfx_v3_length_squared(v) < 1e-12f) { gfx_fail(call, "Cannot normalize a zero vector."); return false; }
    *out = gfx_v3_normalize(v);
    return true;
}

static void return_vector(aqua_call *call, vec3 v) {
    double values[3] = {v.x, v.y, v.z};
    aqua_return(call, gfx_doubles(aqua_call_runtime(call), values, 3));
}

static void return_matrix(aqua_call *call, mat4 m) {
    double values[16];
    for (int i = 0; i < 16; i++) values[i] = m.m[i];
    aqua_return(call, gfx_doubles(aqua_call_runtime(call), values, 16));
}

static void glm_Radians(aqua_call *call) { double n = gfx_number(call, 0); if (!aqua_failed(call)) aqua_return_double(call, n * PI / 180); }
static void glm_Sin(aqua_call *call) { double n = gfx_number(call, 0); if (!aqua_failed(call)) aqua_return_double(call, sin(n)); }
static void glm_Cos(aqua_call *call) { double n = gfx_number(call, 0); if (!aqua_failed(call)) aqua_return_double(call, cos(n)); }
static void glm_Sqrt(aqua_call *call) {
    double n = gfx_number(call, 0);
    if (aqua_failed(call)) return;
    if (n < 0) { gfx_fail(call, "Sqrt requires a nonnegative value."); return; }
    aqua_return_double(call, sqrt(n));
}

static void glm_Normalize(aqua_call *call) {
    vec3 v, n;
    if (vector_argument(call, 0, &v) && normalized(call, v, &n)) return_vector(call, n);
}
static void glm_Dot(aqua_call *call) {
    vec3 a, b;
    if (vector_argument(call, 0, &a) && vector_argument(call, 1, &b)) aqua_return_double(call, gfx_v3_dot(a, b));
}
static void glm_Cross(aqua_call *call) {
    vec3 a, b;
    if (vector_argument(call, 0, &a) && vector_argument(call, 1, &b)) return_vector(call, gfx_v3_cross(a, b));
}
static void glm_Identity(aqua_call *call) { return_matrix(call, gfx_m4_identity()); }
static void glm_Translate(aqua_call *call) { vec3 v; if (vector_argument(call, 0, &v)) return_matrix(call, gfx_m4_translation(v.x, v.y, v.z)); }
static void glm_Scale(aqua_call *call) { vec3 v; if (vector_argument(call, 0, &v)) return_matrix(call, gfx_m4_scale(v.x, v.y, v.z)); }
static void glm_Rotate(aqua_call *call) {
    vec3 axis, n;
    if (!vector_argument(call, 1, &axis) || !normalized(call, axis, &n)) return;
    double angle = gfx_number(call, 0);
    if (!aqua_failed(call)) return_matrix(call, gfx_m4_axis_angle(n, (float)angle));
}
static void glm_Multiply(aqua_call *call) {
    mat4 b, a;
    if (matrix_argument_16(call, 1, &b) && matrix_argument_16(call, 0, &a)) return_matrix(call, gfx_m4_multiply(b, a));
}
static void glm_LookAt(aqua_call *call) {
    vec3 eye, target, up_raw, up, forward;
    if (!vector_argument(call, 0, &eye) || !vector_argument(call, 1, &target) || !vector_argument(call, 2, &up_raw)) return;
    if (!normalized(call, up_raw, &up) || !normalized(call, gfx_v3_sub(target, eye), &forward)) return;
    if (gfx_v3_length_squared(gfx_v3_cross(forward, up)) < 1e-12f) { gfx_fail(call, "LookAt up vector is parallel to view direction."); return; }
    return_matrix(call, gfx_m4_look_at(eye, target, up));
}
static void glm_Perspective(aqua_call *call) {
    double values[4];
    for (uint32_t i = 0; i < 4; i++) values[i] = gfx_number(call, i);
    if (aqua_failed(call)) return;
    float fov = (float)values[0], aspect = (float)values[1], near = (float)values[2], far = (float)values[3];
    if (fov <= 0 || fov >= PI || aspect <= 0 || near <= 0 || far <= near) {
        gfx_fail(call, "Perspective requires 0 < fov < PI, aspect > 0 and 0 < near < far.");
        return;
    }
    float f = 1 / tanf(fov / 2);
    /* OpenGL NDC depth is [-1, 1]. */
    return_matrix(call, gfx_m4_make(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0));
}
static void glm_Transpose(aqua_call *call) { mat4 m; if (matrix_argument_16(call, 0, &m)) return_matrix(call, gfx_m4_transpose(m)); }
static void glm_Inverse(aqua_call *call) {
    mat4 m, inverse;
    if (!matrix_argument_16(call, 0, &m)) return;
    if (!gfx_m4_invert(m, &inverse)) { gfx_fail(call, "Matrix is singular."); return; }
    return_matrix(call, inverse);
}
static void glm_TransformPoint(aqua_call *call) {
    vec3 v;
    mat4 m;
    if (vector_argument(call, 1, &v) && matrix_argument_16(call, 0, &m)) return_vector(call, gfx_v3_transform(v, m));
}

#define GLM_MODULE(X) \
    X(glm_Cos, "Cos|餘弦", 1) X(glm_Cross, "Cross|外積", 2) X(glm_Dot, "Dot|內積", 2) X(glm_Identity, "Identity|單位矩陣", 0) \
    X(glm_Inverse, "Inverse|反矩陣", 1) X(glm_LookAt, "LookAt|看向", 3) X(glm_Multiply, "Multiply|相乘", 2) \
    X(glm_Normalize, "Normalize|正規化", 1) X(glm_Perspective, "Perspective|透視", 4) X(glm_Radians, "Radians|弧度", 1) \
    X(glm_Rotate, "Rotate|旋轉", 2) X(glm_Scale, "Scale|縮放", 1) X(glm_Sin, "Sin|正弦", 1) X(glm_Sqrt, "Sqrt|平方根", 1) \
    X(glm_TransformPoint, "TransformPoint|轉換點", 2) X(glm_Translate, "Translate|平移", 1) X(glm_Transpose, "Transpose|轉置", 1)
#define WRAPPER(function, names, count) GFX_WRAP("", function, names, function)
GLM_MODULE(WRAPPER)
#undef WRAPPER

void gfx_glm_register(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "GLM");
    aqua_define_double(module, "PI", PI);
#define DEFINE(function, names, count) aqua_define_function(module, names, wrapper_##function, gfx_signature(count, count));
    GLM_MODULE(DEFINE)
#undef DEFINE
}
