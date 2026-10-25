#include "matrix.h"
#include <math.h>
#include <string.h>

vec3 gfx_v3_add(vec3 a, vec3 b) { return v3(a.x + b.x, a.y + b.y, a.z + b.z); }
vec3 gfx_v3_sub(vec3 a, vec3 b) { return v3(a.x - b.x, a.y - b.y, a.z - b.z); }
vec3 gfx_v3_scale(vec3 a, float s) { return v3(a.x * s, a.y * s, a.z * s); }
/* Vector3.Dot is a four-lane dot product (DPPS) whose zero fourth lane is summed pairwise. */
float gfx_v3_dot(vec3 a, vec3 b) { return (a.x * b.x + a.y * b.y) + (a.z * b.z + 0.0f); }
vec3 gfx_v3_cross(vec3 a, vec3 b) { return v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
float gfx_v3_length_squared(vec3 a) { return gfx_v3_dot(a, a); }
float gfx_v3_length(vec3 a) { return sqrtf(gfx_v3_length_squared(a)); }
vec3 gfx_v3_normalize(vec3 a) { float l = gfx_v3_length(a); return v3(a.x / l, a.y / l, a.z / l); }
vec3 gfx_v3_lerp(vec3 a, vec3 b, float t) { return v3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
float gfx_v3_distance(vec3 a, vec3 b) { return gfx_v3_length(gfx_v3_sub(a, b)); }

mat4 gfx_m4_make(float m11, float m12, float m13, float m14, float m21, float m22, float m23, float m24,
             float m31, float m32, float m33, float m34, float m41, float m42, float m43, float m44) {
    mat4 r = {{m11, m12, m13, m14, m21, m22, m23, m24, m31, m32, m33, m34, m41, m42, m43, m44}};
    return r;
}

mat4 gfx_m4_identity(void) { return gfx_m4_make(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); }

mat4 gfx_m4_multiply(mat4 a, mat4 b) {
    mat4 r;
    for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
            r.m[row * 4 + column] = a.m[row * 4] * b.m[column] + a.m[row * 4 + 1] * b.m[4 + column] +
                                    a.m[row * 4 + 2] * b.m[8 + column] + a.m[row * 4 + 3] * b.m[12 + column];
    return r;
}

mat4 gfx_m4_translation(float x, float y, float z) { return gfx_m4_make(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1); }
mat4 gfx_m4_scale(float x, float y, float z) { return gfx_m4_make(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1); }

mat4 gfx_m4_rotation_x(float radians) {
    float c = cosf(radians), s = sinf(radians);
    return gfx_m4_make(1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1);
}
mat4 gfx_m4_rotation_y(float radians) {
    float c = cosf(radians), s = sinf(radians);
    return gfx_m4_make(c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1);
}
mat4 gfx_m4_rotation_z(float radians) {
    float c = cosf(radians), s = sinf(radians);
    return gfx_m4_make(c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
}

mat4 gfx_m4_axis_angle(vec3 axis, float angle) {
    float x = axis.x, y = axis.y, z = axis.z, sa = sinf(angle), ca = cosf(angle);
    float xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z;
    return gfx_m4_make(xx + ca * (1 - xx), xy - ca * xy + sa * z, xz - ca * xz - sa * y, 0,
                   xy - ca * xy - sa * z, yy + ca * (1 - yy), yz - ca * yz + sa * x, 0,
                   xz - ca * xz + sa * y, yz - ca * yz - sa * x, zz + ca * (1 - zz), 0,
                   0, 0, 0, 1);
}

/* Matrix4x4.CreateLookAt (right-handed, as XMMatrixLookToRH), with its negations kept for signed zeros. */
mat4 gfx_m4_look_at(vec3 eye, vec3 target, vec3 up) {
    vec3 direction = gfx_v3_sub(target, eye);
    vec3 z = gfx_v3_normalize(v3(-direction.x, -direction.y, -direction.z));
    vec3 x = gfx_v3_normalize(gfx_v3_cross(up, z));
    vec3 y = gfx_v3_cross(z, x);
    vec3 negative = v3(-eye.x, -eye.y, -eye.z);
    return gfx_m4_make(x.x, y.x, z.x, 0, x.y, y.y, z.y, 0, x.z, y.z, z.z, 0,
                       gfx_v3_dot(x, negative), gfx_v3_dot(y, negative), gfx_v3_dot(z, negative), 1);
}

mat4 gfx_m4_transpose(mat4 a) {
    mat4 r;
    for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++) r.m[row * 4 + column] = a.m[column * 4 + row];
    return r;
}

/* Matrix4x4.Invert as .NET computes it on x64 (its SSE path, after DirectXMath's XMMatrixInverse), so results match
   to the last bit, signed zeros included. Four-lane operations are spelled out on q4 values. */
typedef struct q4 { float v[4]; } q4;
static q4 q4_shuffle(q4 a, q4 b, unsigned control) {
    q4 r = {{a.v[control & 3], a.v[(control >> 2) & 3], b.v[(control >> 4) & 3], b.v[(control >> 6) & 3]}};
    return r;
}
static q4 q4_permute(q4 a, unsigned control) { return q4_shuffle(a, a, control); }
static q4 q4_mul(q4 a, q4 b) { q4 r; for (int i = 0; i < 4; i++) r.v[i] = a.v[i] * b.v[i]; return r; }
static q4 q4_sub(q4 a, q4 b) { q4 r; for (int i = 0; i < 4; i++) r.v[i] = a.v[i] - b.v[i]; return r; }
static q4 q4_add(q4 a, q4 b) { q4 r; for (int i = 0; i < 4; i++) r.v[i] = a.v[i] + b.v[i]; return r; }

bool gfx_m4_invert(mat4 matrix, mat4 *result) {
    q4 row1, row2, row3, row4;
    memcpy(row1.v, matrix.m, 16); memcpy(row2.v, matrix.m + 4, 16); memcpy(row3.v, matrix.m + 8, 16); memcpy(row4.v, matrix.m + 12, 16);
    /* Transpose. */
    q4 t1 = q4_shuffle(row1, row2, 0x44), t3 = q4_shuffle(row1, row2, 0xEE), t2 = q4_shuffle(row3, row4, 0x44), t4 = q4_shuffle(row3, row4, 0xEE);
    row1 = q4_shuffle(t1, t2, 0x88); row2 = q4_shuffle(t1, t2, 0xDD); row3 = q4_shuffle(t3, t4, 0x88); row4 = q4_shuffle(t3, t4, 0xDD);

    q4 v00 = q4_permute(row3, 0x50), v10 = q4_permute(row4, 0xEE), v01 = q4_permute(row1, 0x50), v11 = q4_permute(row2, 0xEE);
    q4 v02 = q4_shuffle(row3, row1, 0x88), v12 = q4_shuffle(row4, row2, 0xDD);
    q4 d0 = q4_mul(v00, v10), d1 = q4_mul(v01, v11), d2 = q4_mul(v02, v12);
    v00 = q4_permute(row3, 0xEE); v10 = q4_permute(row4, 0x50); v01 = q4_permute(row1, 0xEE); v11 = q4_permute(row2, 0x50);
    v02 = q4_shuffle(row3, row1, 0xDD); v12 = q4_shuffle(row4, row2, 0x88);
    d0 = q4_sub(d0, q4_mul(v00, v10)); d1 = q4_sub(d1, q4_mul(v01, v11)); d2 = q4_sub(d2, q4_mul(v02, v12));

    v11 = q4_shuffle(d0, d2, 0x5D); v00 = q4_permute(row2, 0x49); v10 = q4_shuffle(v11, d0, 0x32); v01 = q4_permute(row1, 0x12);
    v11 = q4_shuffle(v11, d0, 0x99);
    q4 v13 = q4_shuffle(d1, d2, 0xFD); v02 = q4_permute(row4, 0x49); v12 = q4_shuffle(v13, d1, 0x32);
    q4 v03 = q4_permute(row3, 0x12); v13 = q4_shuffle(v13, d1, 0x99);
    q4 c0 = q4_mul(v00, v10), c2 = q4_mul(v01, v11), c4 = q4_mul(v02, v12), c6 = q4_mul(v03, v13);

    v11 = q4_shuffle(d0, d2, 0x04); v00 = q4_permute(row2, 0x9E); v10 = q4_shuffle(d0, v11, 0x93); v01 = q4_permute(row1, 0x7B);
    v11 = q4_shuffle(d0, v11, 0x26);
    v13 = q4_shuffle(d1, d2, 0xA4); v02 = q4_permute(row4, 0x9E); v12 = q4_shuffle(d1, v13, 0x93); v03 = q4_permute(row3, 0x7B);
    v13 = q4_shuffle(d1, v13, 0x26);
    c0 = q4_sub(c0, q4_mul(v00, v10)); c2 = q4_sub(c2, q4_mul(v01, v11)); c4 = q4_sub(c4, q4_mul(v02, v12)); c6 = q4_sub(c6, q4_mul(v03, v13));

    v00 = q4_permute(row2, 0x33);
    v10 = q4_permute(q4_shuffle(d0, d2, 0x4A), 0x2C);
    v01 = q4_permute(row1, 0x8D);
    v11 = q4_permute(q4_shuffle(d0, d2, 0x4C), 0x93);
    v02 = q4_permute(row4, 0x33);
    v12 = q4_permute(q4_shuffle(d1, d2, 0xEA), 0x2C);
    v03 = q4_permute(row3, 0x8D);
    v13 = q4_permute(q4_shuffle(d1, d2, 0xEC), 0x93);
    v00 = q4_mul(v00, v10); v01 = q4_mul(v01, v11); v02 = q4_mul(v02, v12); v03 = q4_mul(v03, v13);

    q4 c1 = q4_sub(c0, v00); c0 = q4_add(c0, v00);
    q4 c3 = q4_add(c2, v01); c2 = q4_sub(c2, v01);
    q4 c5 = q4_sub(c4, v02); c4 = q4_add(c4, v02);
    q4 c7 = q4_add(c6, v03); c6 = q4_sub(c6, v03);
    c0 = q4_permute(q4_shuffle(c0, c1, 0xD8), 0xD8);
    c2 = q4_permute(q4_shuffle(c2, c3, 0xD8), 0xD8);
    c4 = q4_permute(q4_shuffle(c4, c5, 0xD8), 0xD8);
    c6 = q4_permute(q4_shuffle(c6, c7, 0xD8), 0xD8);

    /* Vector4.Dot sums pairwise (DPPS). */
    float det = (c0.v[0] * row1.v[0] + c0.v[1] * row1.v[1]) + (c0.v[2] * row1.v[2] + c0.v[3] * row1.v[3]);
    if (fabsf(det) < 1.401298e-45f) {
        for (int index = 0; index < 16; index++) result->m[index] = NAN;
        return false;
    }
    float inverse = 1.0f / det;
    for (int i = 0; i < 4; i++) {
        result->m[i] = c0.v[i] * inverse;
        result->m[4 + i] = c2.v[i] * inverse;
        result->m[8 + i] = c4.v[i] * inverse;
        result->m[12 + i] = c6.v[i] * inverse;
    }
    return true;
}

bool gfx_m4_equal(mat4 a, mat4 b) {
    for (int i = 0; i < 16; i++) if (a.m[i] != b.m[i]) return false;
    return true;
}

vec3 gfx_v3_transform(vec3 v, mat4 m) {
    const float *a = m.m;
    return v3(v.x * a[0] + v.y * a[4] + v.z * a[8] + a[12], v.x * a[1] + v.y * a[5] + v.z * a[9] + a[13],
              v.x * a[2] + v.y * a[6] + v.z * a[10] + a[14]);
}

vec3 gfx_v3_transform_normal(vec3 v, mat4 m) {
    const float *a = m.m;
    return v3(v.x * a[0] + v.y * a[4] + v.z * a[8], v.x * a[1] + v.y * a[5] + v.z * a[9], v.x * a[2] + v.y * a[6] + v.z * a[10]);
}

vec4 gfx_v4_transform(vec4 v, mat4 m) {
    const float *a = m.m;
    vec4 r = {v.x * a[0] + v.y * a[4] + v.z * a[8] + v.w * a[12], v.x * a[1] + v.y * a[5] + v.z * a[9] + v.w * a[13],
              v.x * a[2] + v.y * a[6] + v.z * a[10] + v.w * a[14], v.x * a[3] + v.y * a[7] + v.z * a[11] + v.w * a[15]};
    return r;
}
