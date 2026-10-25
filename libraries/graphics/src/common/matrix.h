/* Single-precision vectors and 4x4 matrices with System.Numerics semantics, which every earlier graphics API (and the
 * uniform ABI, the geometry kernel and the GLM module) is defined by: row vectors (v * M), so a transform composed as
 * T * M applies T first; m[0..15] are M11, M12, ..., M44 in row order. Written to WGSL as is, the same floats read as
 * column-major matrices for column vectors. */
#ifndef GRAPHICS_MATRIX_H
#define GRAPHICS_MATRIX_H

#include <stdbool.h>

typedef struct vec2 { float x, y; } vec2;
typedef struct vec3 { float x, y, z; } vec3;
typedef struct vec4 { float x, y, z, w; } vec4;
typedef struct mat4 { float m[16]; } mat4;

static inline vec3 v3(float x, float y, float z) { vec3 v = {x, y, z}; return v; }
vec3 gfx_v3_add(vec3 a, vec3 b);
vec3 gfx_v3_sub(vec3 a, vec3 b);
vec3 gfx_v3_scale(vec3 a, float s);
float gfx_v3_dot(vec3 a, vec3 b);
vec3 gfx_v3_cross(vec3 a, vec3 b);
float gfx_v3_length_squared(vec3 a);
float gfx_v3_length(vec3 a);
vec3 gfx_v3_normalize(vec3 a);           /* Vector3.Normalize: a / |a| (NaN for zero) */
vec3 gfx_v3_lerp(vec3 a, vec3 b, float t);
float gfx_v3_distance(vec3 a, vec3 b);

mat4 gfx_m4_identity(void);
mat4 gfx_m4_make(float m11, float m12, float m13, float m14, float m21, float m22, float m23, float m24,
             float m31, float m32, float m33, float m34, float m41, float m42, float m43, float m44);
mat4 gfx_m4_multiply(mat4 a, mat4 b);    /* a * b */
mat4 gfx_m4_translation(float x, float y, float z);
mat4 gfx_m4_scale(float x, float y, float z);
mat4 gfx_m4_rotation_x(float radians);
mat4 gfx_m4_rotation_y(float radians);
mat4 gfx_m4_rotation_z(float radians);
mat4 gfx_m4_axis_angle(vec3 axis, float angle);
mat4 gfx_m4_look_at(vec3 eye, vec3 target, vec3 up);
mat4 gfx_m4_transpose(mat4 a);
/* Matrix4x4.Invert: false (and a NaN matrix) when singular. */
bool gfx_m4_invert(mat4 a, mat4 *result);
bool gfx_m4_equal(mat4 a, mat4 b);

vec3 gfx_v3_transform(vec3 v, mat4 m);        /* (v, 1) * M */
vec3 gfx_v3_transform_normal(vec3 v, mat4 m); /* (v, 0) * M */
vec4 gfx_v4_transform(vec4 v, mat4 m);

#endif
