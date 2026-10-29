/* The Processing geometry kernel (formerly geometry.wasm, built from AquariusCore/graphics/geometry.c), with its state
   in a `geo` context. A float port of the desktop renderer's geometry, so its pixels stay put: System.Numerics
   semantics (row vectors, clip = v * M), ear clipping of simple polygons in either winding, holes bridged into the
   boundary, mitered strips for translucent 2D strokes and projected constant-width 3D strokes. */
#include "geometry.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define TAU 6.28318530717958647692f
#define PI_F 3.14159265358979323846f

typedef struct { float x, y, z; } V3;
typedef struct { float x, y; } V2;
typedef struct { float x, y, z, w; } V4;

void gfx_geo_free(geo *g) {
    free(g->input); free(g->vertices); free(g->ranges); free(g->indices);
    memset(g, 0, sizeof *g);
}
void gfx_geo_state_changed(geo *g) { g->inverse_valid = 0; }
float *gfx_geo_input(geo *g, int floats) {
    if (floats > g->input_capacity) {
        float *next = realloc(g->input, (size_t)floats * sizeof(float));
        if (!next) return 0;
        g->input = next; g->input_capacity = floats;
    }
    return g->input;
}

static int I(geo *g, int field) { return (int)g->state[field]; }
static float weight(geo *g) { return g->state[GEO_WEIGHT]; }

/* ---- output ---- */
static void reset(geo *g) { g->vertex_count = 0; g->range_count = 0; g->index_count = 0; g->failed = 0; }
static void begin(geo *g, int kind) {
    if (g->range_count && g->ranges[g->range_count - 1].kind == kind && g->ranges[g->range_count - 1].start + g->ranges[g->range_count - 1].count == g->vertex_count) return;
    if (g->range_count == g->range_capacity) {
        int capacity = g->range_capacity ? g->range_capacity * 2 : 16;
        geo_range *next = realloc(g->ranges, (size_t)capacity * sizeof(geo_range));
        if (!next) { g->failed = 1; return; }
        g->ranges = next; g->range_capacity = capacity;
    }
    g->ranges[g->range_count++] = (geo_range){g->vertex_count, 0, kind};
}
static void vertex(geo *g, V3 p, const float *color, V2 uv, const float *normal) {
    if (g->failed) return;
    if (g->vertex_count == g->vertex_capacity) {
        int capacity = g->vertex_capacity ? g->vertex_capacity * 2 : 1024;
        float *next = realloc(g->vertices, (size_t)capacity * 12 * sizeof(float));
        if (!next) { g->failed = 1; return; }
        g->vertices = next; g->vertex_capacity = capacity;
    }
    float *v = g->vertices + (size_t)g->vertex_count * 12;
    const float *n = normal ? normal : g->state + GEO_NORMAL;
    v[0] = p.x; v[1] = p.y; v[2] = p.z; v[3] = n[0]; v[4] = n[1]; v[5] = n[2]; v[6] = uv.x; v[7] = uv.y;
    v[8] = color[0]; v[9] = color[1]; v[10] = color[2]; v[11] = color[3];
    g->vertex_count++; g->ranges[g->range_count - 1].count++;
}
static void emit(geo *g, int kind, V3 p, const float *color, const float *normal) { begin(g, kind); vertex(g, p, color, (V2){0, 0}, normal); }
static void push_index(geo *g, int i) {
    if (g->failed) return;
    if (g->index_count == g->index_capacity) {
        int capacity = g->index_capacity ? g->index_capacity * 2 : 256;
        int *next = realloc(g->indices, (size_t)capacity * sizeof(int));
        if (!next) { g->failed = 1; return; }
        g->indices = next; g->index_capacity = capacity;
    }
    g->indices[g->index_count++] = i;
}
static int finish(geo *g, int result) { return g->failed ? GEO_OUT_OF_MEMORY : result; }

/* ---- vectors and matrices (System.Numerics semantics: row vectors, clip = v * M) ---- */
static V3 v3(float x, float y, float z) { return (V3){x, y, z}; }
static V3 add(V3 a, V3 b) { return v3(a.x + b.x, a.y + b.y, a.z + b.z); }
static V3 sub(V3 a, V3 b) { return v3(a.x - b.x, a.y - b.y, a.z - b.z); }
static V3 scale(V3 a, float s) { return v3(a.x * s, a.y * s, a.z * s); }
static float len2_2(V2 a) { return a.x * a.x + a.y * a.y; }
static V2 normalize2(V2 a) { float l = sqrtf(len2_2(a)); return (V2){a.x / l, a.y / l}; }
static float dot2(V2 a, V2 b) { return a.x * b.x + a.y * b.y; }
static V4 transform(V4 v, const float *m) {
    return (V4){ v.x * m[0] + v.y * m[4] + v.z * m[8] + v.w * m[12], v.x * m[1] + v.y * m[5] + v.z * m[9] + v.w * m[13],
                 v.x * m[2] + v.y * m[6] + v.z * m[10] + v.w * m[14], v.x * m[3] + v.y * m[7] + v.z * m[11] + v.w * m[15] };
}
/* Matrix4x4.Invert. */
static int invert(const float *m, float *r) {
    float a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], gg = m[6], h = m[7];
    float i = m[8], j = m[9], k = m[10], l = m[11], mm = m[12], n = m[13], o = m[14], p = m[15];
    float kp_lo = k * p - l * o, jp_ln = j * p - l * n, jo_kn = j * o - k * n, ip_lm = i * p - l * mm, io_km = i * o - k * mm, in_jm = i * n - j * mm;
    float a11 = +(f * kp_lo - gg * jp_ln + h * jo_kn), a12 = -(e * kp_lo - gg * ip_lm + h * io_km);
    float a13 = +(e * jp_ln - f * ip_lm + h * in_jm), a14 = -(e * jo_kn - f * io_km + gg * in_jm);
    float det = a * a11 + b * a12 + c * a13 + d * a14;
    if (fabsf(det) < 1.401298e-45f) return 0;
    float inv = 1.0f / det;
    r[0] = a11 * inv; r[4] = a12 * inv; r[8] = a13 * inv; r[12] = a14 * inv;
    r[1] = -(b * kp_lo - c * jp_ln + d * jo_kn) * inv; r[5] = +(a * kp_lo - c * ip_lm + d * io_km) * inv;
    r[9] = -(a * jp_ln - b * ip_lm + d * in_jm) * inv; r[13] = +(a * jo_kn - b * io_km + c * in_jm) * inv;
    float gp_ho = gg * p - h * o, fp_hn = f * p - h * n, fo_gn = f * o - gg * n, ep_hm = e * p - h * mm, eo_gm = e * o - gg * mm, en_fm = e * n - f * mm;
    r[2] = +(b * gp_ho - c * fp_hn + d * fo_gn) * inv; r[6] = -(a * gp_ho - c * ep_hm + d * eo_gm) * inv;
    r[10] = +(a * fp_hn - b * ep_hm + d * en_fm) * inv; r[14] = -(a * fo_gn - b * eo_gm + c * en_fm) * inv;
    float gl_hk = gg * l - h * k, fl_hj = f * l - h * j, fk_gj = f * k - gg * j, el_hi = e * l - h * i, ek_gi = e * k - gg * i, ej_fi = e * j - f * i;
    r[3] = -(b * gl_hk - c * fl_hj + d * fk_gj) * inv; r[7] = +(a * gl_hk - c * el_hi + d * ek_gi) * inv;
    r[11] = -(a * fl_hj - b * el_hi + d * ej_fi) * inv; r[15] = +(a * fk_gj - b * ek_gi + c * ej_fi) * inv;
    return 1;
}
/* Offsets a point by (x, y) screen pixels in clip space and maps it back; the point itself when not invertible. */
static V3 stroke_offset(geo *g, V3 p, float x, float y) {
    if (!g->inverse_valid) { g->inverse_ok = invert(g->state + GEO_MVP, g->inverse); g->inverse_valid = 1; }
    if (!g->inverse_ok) return p;
    V4 clip = transform((V4){p.x, p.y, p.z, 1}, g->state + GEO_MVP);
    clip.x += x * 2 / g->state[GEO_WIDTH] * clip.w; clip.y += y * 2 / g->state[GEO_HEIGHT] * clip.w;
    V4 world = transform(clip, g->inverse);
    return v3(world.x / world.w, world.y / world.w, world.z / world.w);
}

/* ---- strokes ---- */
static void disk(geo *g, V3 center, float radius, const float *color) {
    for (int i = 0; i < 16; i++) {
        float a = i * TAU / 16, b = (i + 1) * TAU / 16;
        V3 pa = I(g, GEO_IS3D) ? stroke_offset(g, center, cosf(a) * radius, sinf(a) * radius) : add(center, v3(cosf(a) * radius, sinf(a) * radius, 0));
        V3 pb = I(g, GEO_IS3D) ? stroke_offset(g, center, cosf(b) * radius, sinf(b) * radius) : add(center, v3(cosf(b) * radius, sinf(b) * radius, 0));
        emit(g, GEO_STROKE, center, color, 0); emit(g, GEO_STROKE, pa, color, 0); emit(g, GEO_STROKE, pb, color, 0);
    }
}
static void segment(geo *g, V3 a, V3 b, int cap) {
    const float *color = g->state + GEO_STROKE_COLOR; float w = weight(g);
    if (!I(g, GEO_HAS_STROKE) || w <= 0) return;
    if (I(g, GEO_IS3D)) {
        V4 ca = transform((V4){a.x, a.y, a.z, 1}, g->state + GEO_MVP), cb = transform((V4){b.x, b.y, b.z, 1}, g->state + GEO_MVP);
        if (ca.w <= 0 || cb.w <= 0) return;
        V2 d2 = {(cb.x / cb.w - ca.x / ca.w) * g->state[GEO_WIDTH] / 2, (cb.y / cb.w - ca.y / ca.w) * g->state[GEO_HEIGHT] / 2};
        float length = sqrtf(len2_2(d2));
        if (length < 1e-6f) { disk(g, a, w / 2, color); return; }
        V2 n2 = {-d2.y / length * w / 2, d2.x / length * w / 2};
        V3 ap = stroke_offset(g, a, n2.x, n2.y), am = stroke_offset(g, a, -n2.x, -n2.y), bp = stroke_offset(g, b, n2.x, n2.y), bm = stroke_offset(g, b, -n2.x, -n2.y);
        emit(g, GEO_STROKE, ap, color, 0); emit(g, GEO_STROKE, am, color, 0); emit(g, GEO_STROKE, bp, color, 0);
        emit(g, GEO_STROKE, bp, color, 0); emit(g, GEO_STROKE, am, color, 0); emit(g, GEO_STROKE, bm, color, 0);
        if (cap == 1) { disk(g, a, w / 2, color); disk(g, b, w / 2, color); }
        return;
    }
    V3 d = sub(b, a); float len = sqrtf(d.x * d.x + d.y * d.y);
    if (len < 1e-6f) { disk(g, a, w / 2, color); return; }
    V3 n = v3(-d.y / len * w / 2, d.x / len * w / 2, 0);
    if (cap == 2) { V3 e = scale(d, 1 / len * w / 2); a = sub(a, e); b = add(b, e); }
    emit(g, GEO_STROKE, add(a, n), color, 0); emit(g, GEO_STROKE, sub(a, n), color, 0); emit(g, GEO_STROKE, add(b, n), color, 0);
    emit(g, GEO_STROKE, add(b, n), color, 0); emit(g, GEO_STROKE, sub(a, n), color, 0); emit(g, GEO_STROKE, sub(b, n), color, 0);
    if (cap == 1) { disk(g, a, w / 2, color); disk(g, b, w / 2, color); }
}
static V2 direction(geo *g, V3 a, V3 b) {
    if (!I(g, GEO_IS3D)) return (V2){b.x - a.x, b.y - a.y};
    V4 p = transform((V4){a.x, a.y, a.z, 1}, g->state + GEO_MVP), q = transform((V4){b.x, b.y, b.z, 1}, g->state + GEO_MVP);
    if (p.w <= 0 || q.w <= 0) return (V2){0, 0};
    return (V2){(q.x / q.w - p.x / p.w) * g->state[GEO_WIDTH] / 2, (q.y / q.w - p.y / p.w) * g->state[GEO_HEIGHT] / 2};
}
static V3 offset(geo *g, V3 p, V2 delta) { return I(g, GEO_IS3D) ? stroke_offset(g, p, delta.x, delta.y) : add(p, v3(delta.x, delta.y, 0)); }
static V2 perp(V2 d) { return (V2){-d.y, d.x}; }
/* A 2D polyline as one non-overlapping mitered strip, so translucent strokes have no darker dots at vertices.
   Returns 0 (emitting nothing) for sharp corners beyond the miter limit; the general path handles those. */
static int miter_strip(geo *g, const V3 *source, int count, int close) {
    V2 *points = malloc((size_t)count * sizeof(V2)); int n = 0;
    if (!points) { g->failed = 1; return 1; }
    for (int i = 0; i < count; i++) {
        V2 q = {source[i].x, source[i].y};
        if (!n || (points[n - 1].x - q.x) * (points[n - 1].x - q.x) + (points[n - 1].y - q.y) * (points[n - 1].y - q.y) > 1e-4f) points[n++] = q;
    }
    if (close && n > 2 && (points[0].x - points[n - 1].x) * (points[0].x - points[n - 1].x) + (points[0].y - points[n - 1].y) * (points[0].y - points[n - 1].y) <= 1e-4f) n--;
    if (n < 2 || (close && n < 3)) { free(points); return 0; }
    int segments = close ? n : n - 1; float half = weight(g) / 2;
    V2 *directions = malloc((size_t)segments * sizeof(V2)), *left = malloc((size_t)n * sizeof(V2)), *right = malloc((size_t)n * sizeof(V2));
    int ok = directions && left && right;
    for (int k = 0; ok && k < segments; k++) directions[k] = normalize2((V2){points[(k + 1) % n].x - points[k].x, points[(k + 1) % n].y - points[k].y});
    for (int i = 0; ok && i < n; i++) {
        int has_prev = close || i > 0, has_next = close || i < n - 1;
        V2 off;
        if (has_prev && has_next) {
            V2 n1 = perp(directions[(i - 1 + segments) % segments]), n2 = perp(directions[i % segments]);
            V2 middle = {n1.x + n2.x, n1.y + n2.y};
            if (len2_2(middle) < 1e-8f) { ok = 0; break; }
            middle = normalize2(middle);
            /* Miter longer than 2x the weight, or a round join at a visible corner: use the general path. */
            float denom = dot2(middle, n2);
            if (denom < (I(g, GEO_JOIN) == 1 ? 0.966f : 0.5f)) { ok = 0; break; }
            off = (V2){middle.x * half / denom, middle.y * half / denom};
        } else { V2 p = perp(directions[has_next ? i : segments - 1]); off = (V2){p.x * half, p.y * half}; }
        V2 point = points[i];
        if (!close && I(g, GEO_CAP) == 2) {
            if (i == 0) { point.x -= directions[0].x * half; point.y -= directions[0].y * half; }
            if (i == n - 1) { point.x += directions[segments - 1].x * half; point.y += directions[segments - 1].y * half; }
        }
        left[i] = (V2){point.x + off.x, point.y + off.y}; right[i] = (V2){point.x - off.x, point.y - off.y};
    }
    if (ok) {
        const float *color = g->state + GEO_STROKE_COLOR;
        for (int k = 0; k < segments; k++) {
            int j = (k + 1) % n;
            V3 l0 = v3(left[k].x, left[k].y, 0), r0 = v3(right[k].x, right[k].y, 0), l1 = v3(left[j].x, left[j].y, 0), r1 = v3(right[j].x, right[j].y, 0);
            emit(g, GEO_STROKE, l0, color, 0); emit(g, GEO_STROKE, r0, color, 0); emit(g, GEO_STROKE, l1, color, 0);
            emit(g, GEO_STROKE, l1, color, 0); emit(g, GEO_STROKE, r0, color, 0); emit(g, GEO_STROKE, r1, color, 0);
        }
        if (!close && I(g, GEO_CAP) == 1) { disk(g, source[0], half, color); disk(g, source[count - 1], half, color); }
    }
    free(points); free(directions); free(left); free(right);
    return ok;
}
static void polyline(geo *g, const V3 *points, int n, int close) {
    if (!I(g, GEO_HAS_STROKE) || n < 2) return;
    if (!I(g, GEO_IS3D) && miter_strip(g, points, n, close)) return;
    const float *color = g->state + GEO_STROKE_COLOR; float w = weight(g); int cap = I(g, GEO_CAP);
    for (int i = 0; i < n - (close ? 0 : 1); i++) segment(g, points[i], points[(i + 1) % n], 0);
    for (int i = close ? 0 : 1; i < (close ? n : n - 1); i++) {
        V3 p = points[i];
        if (I(g, GEO_JOIN) == 1) { disk(g, p, w / 2, color); continue; }
        V2 a = direction(g, points[(i + n - 1) % n], p), b = direction(g, p, points[(i + 1) % n]);
        if (len2_2(a) < 1e-12f || len2_2(b) < 1e-12f) continue;
        a = normalize2(a); b = normalize2(b);
        float turn = a.x * b.y - a.y * b.x; if (fabsf(turn) < 1e-6f) continue;
        float sign = turn >= 0 ? -1.0f : 1.0f;
        V2 n1 = {-a.y * sign, a.x * sign}, n2 = {-b.y * sign, b.x * sign};
        V2 edge1 = {n1.x * w / 2, n1.y * w / 2}, edge2 = {n2.x * w / 2, n2.y * w / 2};
        V2 middle = normalize2((V2){n1.x + n2.x, n1.y + n2.y}); float denom = dot2(middle, n2);
        V2 miter = {middle.x * w / 2 / denom, middle.y * w / 2 / denom};
        V3 center = I(g, GEO_JOIN) == 0 && sqrtf(len2_2(miter)) <= w * 2 ? offset(g, p, miter) : p;
        emit(g, GEO_STROKE, offset(g, p, edge1), color, 0); emit(g, GEO_STROKE, center, color, 0); emit(g, GEO_STROKE, offset(g, p, edge2), color, 0);
    }
    if (!close && cap == 1) { disk(g, points[0], w / 2, color); disk(g, points[n - 1], w / 2, color); }
    if (!close && cap == 2) {
        V2 first = direction(g, points[0], points[1]), last = direction(g, points[n - 2], points[n - 1]);
        if (len2_2(first) > 0) { V2 d = normalize2(first); segment(g, points[0], offset(g, points[0], (V2){-d.x * w / 2, -d.y * w / 2}), cap); }
        if (len2_2(last) > 0) { V2 d = normalize2(last); segment(g, points[n - 1], offset(g, points[n - 1], (V2){d.x * w / 2, d.y * w / 2}), cap); }
    }
}

/* ---- triangulation: ear clipping of a simple polygon, either winding, projected onto its dominant plane ---- */
static float cross3(const V3 *v, int a, int b, int c) { return (v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[b].y - v[a].y) * (v[c].x - v[a].x); }
/* Projects `total` points onto the dominant plane of the polygon formed by the first `count`. */
static void project(const V3 *source, int count, int total, V3 *v) {
    V3 normal = {0, 0, 0};
    for (int i = 0; i < count; i++) {
        V3 a = source[i], b = source[(i + 1) % count];
        normal = add(normal, v3((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y)));
    }
    for (int i = 0; i < total; i++) {
        V3 p = source[i];
        if (fabsf(normal.x) > fabsf(normal.z) && fabsf(normal.x) >= fabsf(normal.y)) v[i] = v3(p.y, p.z, 0);
        else if (fabsf(normal.y) > fabsf(normal.z)) v[i] = v3(p.x, p.z, 0);
        else v[i] = p;
    }
}
static int same(V3 a, V3 b) { return a.x == b.x && a.y == b.y; }
/* Clips the ring ids[0..n) of projected points, pushing triangles of original indices. A bridged ring (holes spliced
   in) visits each bridge's endpoints twice; those copies never block an ear, and they are kept even where a bridge is
   collinear with the edges it meets (zero-area vertices are dropped only when no ear remains). */
static int ear_clip(geo *g, const V3 *v, int *ids, int n, int bridged) {
    int removed = 1;
    while (removed && n > 3) {
        removed = 0;
        for (int i = 0; i < n; i++) {
            int a = ids[(i + n - 1) % n], b = ids[i], c = ids[(i + 1) % n];
            V3 d = sub(v[a], v[b]);
            if (d.x * d.x + d.y * d.y + d.z * d.z < 1e-12f || (!bridged && fabsf(cross3(v, a, b, c)) < 1e-6f)) {
                memmove(ids + i, ids + i + 1, (size_t)(n - i - 1) * sizeof(int)); n--; removed = 1; break;
            }
        }
    }
    float area = 0;
    for (int i = 0; i < n; i++) { V3 a = v[ids[i]], b = v[ids[(i + 1) % n]]; area += a.x * b.y - b.x * a.y; }
    /* Nothing to fill (a rectangle of zero width when a window gets small, a flat polygon): no triangles, no error. */
    if (fabsf(area) < 1e-6f) return 0;
    int result = 0;
    float sign = area >= 0 ? 1.0f : -1.0f;
    while (!result && n > 3) {
        int found = 0;
        for (int i = 0; i < n; i++) {
            int a = ids[(i + n - 1) % n], b = ids[i], c = ids[(i + 1) % n];
            if (sign * cross3(v, a, b, c) <= 1e-6f) continue;
            int inside = 0;
            for (int q = 0; q < n && !inside; q++) {
                int p = ids[q];
                inside = p != a && p != b && p != c && !(bridged && (same(v[p], v[a]) || same(v[p], v[b]) || same(v[p], v[c])))
                    && sign * cross3(v, a, b, p) >= -1e-6f && sign * cross3(v, b, c, p) >= -1e-6f && sign * cross3(v, c, a, p) >= -1e-6f;
            }
            if (inside) continue;
            push_index(g, a); push_index(g, b); push_index(g, c);
            memmove(ids + i, ids + i + 1, (size_t)(n - i - 1) * sizeof(int)); n--; found = 1; break;
        }
        /* Bridges are collinear with edges they meet; a bridged ring left with only zero-area ears drops one. */
        if (!found && bridged) for (int i = 0; i < n && !found; i++) {
            if (fabsf(cross3(v, ids[(i + n - 1) % n], ids[i], ids[(i + 1) % n])) < 1e-6f) { memmove(ids + i, ids + i + 1, (size_t)(n - i - 1) * sizeof(int)); n--; found = 1; }
        }
        if (!found) result = GEO_NOT_SIMPLE;
    }
    if (!result && n == 3) { push_index(g, ids[0]); push_index(g, ids[1]); push_index(g, ids[2]); }
    return result;
}
static int triangulate(geo *g, const V3 *source, int count) {
    if (count < 3) return 0;
    V3 *v = malloc((size_t)count * sizeof(V3)); int *ids = malloc((size_t)count * sizeof(int));
    if (!v || !ids) { free(v); free(ids); g->failed = 1; return GEO_OUT_OF_MEMORY; }
    project(source, count, count, v);
    for (int i = 0; i < count; i++) ids[i] = i;
    int result = ear_clip(g, v, ids, count, 0);
    free(v); free(ids);
    return result;
}

/* Holes are spliced into the boundary through a zero-width bridge from each hole's rightmost vertex to a boundary
   vertex it can see, as earcut does (mirrored), and the bridged ring is ear clipped. */
static float ring_area(const V3 *v, int from, int to) {
    float area = 0;
    for (int i = from; i < to; i++) { V3 a = v[i], b = v[i + 1 < to ? i + 1 : from]; area += a.x * b.y - b.x * a.y; }
    return area;
}
static float turn(V3 a, V3 b, V3 c) { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }
static int locally_inside(const V3 *v, const int *ring, int n, int i, V3 h) {
    V3 prev = v[ring[(i + n - 1) % n]], p = v[ring[i]], next = v[ring[(i + 1) % n]];
    float to_next = turn(p, next, h), from_prev = turn(prev, p, h);
    return turn(prev, p, next) >= 0 ? to_next >= 0 && from_prev >= 0 : to_next >= 0 || from_prev >= 0;
}
static int in_triangle(V3 a, V3 b, V3 c, V3 p) {
    float d1 = turn(a, b, p), d2 = turn(b, c, p), d3 = turn(c, a, p);
    return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
}
static int bridge(const V3 *v, const int *ring, int n, V3 m) {
    float qx = INFINITY; int best = -1;
    for (int i = 0; i < n; i++) {
        V3 a = v[ring[i]], b = v[ring[(i + 1) % n]];
        if (a.y == b.y || !((a.y <= m.y && m.y <= b.y) || (b.y <= m.y && m.y <= a.y))) continue;
        float x = a.x + (m.y - a.y) * (b.x - a.x) / (b.y - a.y);
        if (x >= m.x && x < qx) {
            qx = x; best = a.x > b.x ? i : (i + 1) % n;
            if (x == m.x) { if (m.y == a.y) return i; if (m.y == b.y) return (i + 1) % n; }
        }
    }
    if (best < 0) return -1;
    V3 hit = v3(qx, m.y, 0), candidate = v[ring[best]]; float tan_min = INFINITY; int chosen = best;
    for (int i = 0; i < n; i++) {
        V3 q = v[ring[i]];
        if (q.x <= m.x || q.x > candidate.x || !in_triangle(m, hit, candidate, q)) continue;
        float tan = fabsf(m.y - q.y) / (q.x - m.x);
        if (locally_inside(v, ring, n, i, m) && (tan < tan_min || (tan == tan_min && q.x > v[ring[chosen]].x))) { chosen = i; tan_min = tan; }
    }
    return chosen;
}
static int triangulate_holes(geo *g, const V3 *source, int total, const int *starts, int holes) {
    int outer = holes ? starts[0] : total;
    if (outer < 3) return 0;
    V3 *v = malloc((size_t)total * sizeof(V3)); int *ring = malloc((size_t)(total + 2 * holes) * sizeof(int));
    int *order = malloc((size_t)(holes ? holes : 1) * sizeof(int)), *right = malloc((size_t)(holes ? holes : 1) * sizeof(int));
    if (!v || !ring || !order || !right) { free(v); free(ring); free(order); free(right); g->failed = 1; return GEO_OUT_OF_MEMORY; }
    project(source, outer, total, v);
    int n = outer, result = 0;
    int ccw = ring_area(v, 0, outer) >= 0;
    for (int i = 0; i < outer; i++) ring[i] = ccw ? i : outer - 1 - i;
    for (int h = 0; h < holes; h++) {
        int from = starts[h], to = h + 1 < holes ? starts[h + 1] : total; right[h] = from;
        for (int i = from + 1; i < to; i++) if (v[i].x > v[right[h]].x || (v[i].x == v[right[h]].x && v[i].y > v[right[h]].y)) right[h] = i;
        int j = h; while (j > 0 && v[right[order[j - 1]]].x < v[right[h]].x) { order[j] = order[j - 1]; j--; } order[j] = h;
    }
    for (int k = 0; k < holes && !result; k++) {
        int h = order[k], from = starts[h], to = h + 1 < holes ? starts[h + 1] : total, count = to - from, m = right[h];
        if (count < 3) continue;
        int at = bridge(v, ring, n, v[m]);
        if (at < 0) { result = GEO_NOT_SIMPLE; break; }
        int clockwise = ring_area(v, from, to) < 0, w = at + 1;
        memmove(ring + at + 1 + count + 2, ring + at + 1, (size_t)(n - at - 1) * sizeof(int));
        for (int j = 0; j < count; j++) { int step = clockwise ? j : -j; ring[w++] = from + ((m - from + step) % count + count) % count; }
        ring[w++] = m; ring[w++] = ring[at];
        n += count + 2;
    }
    if (!result) result = ear_clip(g, v, ring, n, 1);
    free(v); free(ring); free(order); free(right);
    return result;
}

/* ---- primitives ---- */
static const V3 *points_in(geo *g) { return (const V3 *)g->input; }
static int polygon(geo *g, const V3 *points, int n, int close, int fill) {
    if (I(g, GEO_HAS_FILL) && fill && n >= 3) {
        int start = g->index_count, result = triangulate(g, points, n);
        if (result < 0) return result;
        for (int i = start; i < g->index_count; i++) emit(g, GEO_FILL, points[g->indices[i]], g->state + GEO_FILL_COLOR, 0);
        g->index_count = start;
    }
    if (I(g, GEO_HAS_STROKE)) polyline(g, points, n, close);
    return 0;
}
static void bounds(float *x, float *y, float *w, float *h, int mode) {
    if (mode == 1) { *w -= *x; *h -= *y; }
    else if (mode == 2) { *x -= *w / 2; *y -= *h / 2; }
    else if (mode == 3) { *x -= *w; *y -= *h; *w *= 2; *h *= 2; }
}

/* Input: n points (xyz). A filled and/or stroked polygon. */
int gfx_geo_polygon(geo *g, int n, int close, int fill) { reset(g); return finish(g, polygon(g, points_in(g), n, close, fill)); }
/* Input: n points (xyz), the boundary followed by each hole, then the `holes` hole start indices (as floats). */
int gfx_geo_polygon_holes(geo *g, int n, int holes, int close, int fill) {
    reset(g);
    if (holes < 0 || n < 0) return GEO_NOT_SIMPLE;
    int *starts = malloc((size_t)(holes ? holes : 1) * sizeof(int));
    if (!starts) return GEO_OUT_OF_MEMORY;
    for (int h = 0; h < holes; h++) {
        starts[h] = (int)g->input[n * 3 + h];
        if (starts[h] < (h ? starts[h - 1] : 0) || starts[h] > n) { free(starts); return GEO_NOT_SIMPLE; }
    }
    const V3 *points = points_in(g); int outer = holes ? starts[0] : n, result = 0;
    if (I(g, GEO_HAS_FILL) && fill && outer >= 3) {
        int start = g->index_count; result = triangulate_holes(g, points, n, starts, holes);
        if (!result) for (int i = start; i < g->index_count; i++) emit(g, GEO_FILL, points[g->indices[i]], g->state + GEO_FILL_COLOR, 0);
        g->index_count = start;
    }
    if (!result && I(g, GEO_HAS_STROKE)) {
        polyline(g, points, outer, close);
        for (int h = 0; h < holes; h++) { int from = starts[h], to = h + 1 < holes ? starts[h + 1] : n; polyline(g, points + from, to - from, 1); }
    }
    free(starts);
    return finish(g, result);
}
/* Input: n points (xyz). A stroke. */
int gfx_geo_polyline(geo *g, int n, int close) { reset(g); polyline(g, points_in(g), n, close); return finish(g, 0); }
/* Input: two points (xyz). */
int gfx_geo_line(geo *g) { reset(g); segment(g, points_in(g)[0], points_in(g)[1], I(g, GEO_CAP)); return finish(g, 0); }
/* Input: one point. A stroke-colored disk of half the stroke weight. */
int gfx_geo_point(geo *g) { reset(g); if (I(g, GEO_HAS_STROKE)) disk(g, points_in(g)[0], weight(g) / 2, g->state + GEO_STROKE_COLOR); return finish(g, 0); }
/* Input: x, y, w, h, r. rect() in the current rect mode; r < 0 means no radius argument. */
int gfx_geo_rect(geo *g) {
    float x = g->input[0], y = g->input[1], w = g->input[2], h = g->input[3], r = g->input[4];
    reset(g); bounds(&x, &y, &w, &h, I(g, GEO_RECT_MODE));
    float radius = r < 0 ? 0 : fminf(fmaxf(r, 0), fminf(fabsf(w), fabsf(h)) / 2);
    if (radius <= 0) { V3 p[4] = {v3(x, y, 0), v3(x + w, y, 0), v3(x + w, y + h, 0), v3(x, y + h, 0)}; return finish(g, polygon(g, p, 4, 1, 1)); }
    /* When r is half a side (a pill or circle), adjacent corner arcs meet at one point; emit it once. */
    float eps = 1e-4f * fmaxf(1.0f, fmaxf(fabsf(w), fabsf(h)));
    int flat_w = fabsf(w) - 2 * radius <= eps, flat_h = fabsf(h) - 2 * radius <= eps;
    V3 p[36]; int n = 0;
    for (int corner = 0; corner < 4; corner++) {
        float cx = corner == 0 || corner == 1 ? x + w - radius : x + radius, cy = corner == 1 || corner == 2 ? y + h - radius : y + radius;
        int skip_first = corner == 1 ? flat_h : corner == 2 ? flat_w : corner == 3 ? flat_h : 0, skip_last = corner == 3 && flat_w;
        for (int i = skip_first ? 1 : 0; i <= (skip_last ? 7 : 8); i++) {
            float angle = (-PI_F / 2 + corner * PI_F / 2) + i * PI_F / 16;
            p[n++] = v3(cx + radius * cosf(angle), cy + radius * sinf(angle), 0);
        }
    }
    return finish(g, polygon(g, p, n, 1, 1));
}
/* Input: x, y, w, h, start, stop. ellipse()/arc() in the current ellipse mode; arc mode 0 OPEN, 1 CHORD, 2 PIE. */
int gfx_geo_ellipse(geo *g, int mode, int arc) {
    float x = g->input[0], y = g->input[1], w = g->input[2], h = g->input[3], start = g->input[4], stop = g->input[5];
    reset(g); bounds(&x, &y, &w, &h, I(g, GEO_ELLIPSE_MODE));
    float rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry;
    if (stop <= start) return 0;
    stop = fminf(stop, start + TAU);
    int count = (int)ceilf(I(g, GEO_DETAIL) * (stop - start) / TAU); if (count < 2) count = 2;
    V3 *p = malloc((size_t)(count + 2) * sizeof(V3)); if (!p) return GEO_OUT_OF_MEMORY;
    p[0] = v3(cx, cy, 0);
    V3 *points = p + 1;
    for (int i = 0; i <= count; i++) { float a = start + (stop - start) * i / count; points[i] = v3(cx + rx * cosf(a), cy + ry * sinf(a), 0); }
    int result = 0;
    if (!arc || mode == 2) {
        if (I(g, GEO_HAS_FILL)) for (int i = 0; i < count; i++) { emit(g, GEO_FILL, p[0], g->state + GEO_FILL_COLOR, 0); emit(g, GEO_FILL, points[i], g->state + GEO_FILL_COLOR, 0); emit(g, GEO_FILL, points[i + 1], g->state + GEO_FILL_COLOR, 0); }
        if (I(g, GEO_HAS_STROKE)) { if (arc) polyline(g, p, count + 2, 1); else polyline(g, points, count + 1, 0); }
    } else {
        if (I(g, GEO_HAS_FILL)) {
            int start_index = g->index_count; result = triangulate(g, points, count + 1);
            if (!result) for (int i = start_index; i < g->index_count; i++) emit(g, GEO_FILL, points[g->indices[i]], g->state + GEO_FILL_COLOR, 0);
            g->index_count = start_index;
        }
        if (!result) polyline(g, points, count + 1, mode == 1);
    }
    free(p);
    return finish(g, result);
}
static float bezier(float a, float b, float c, float d, float t) { float s = 1 - t; return s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d; }
static float curve(float a, float b, float c, float d, float t, float tightness) {
    float s = (1 - tightness) / 2; return bezier(b, b + s * (c - a) / 3, c - s * (d - b) / 3, c, t);
}
static V3 curve_point(geo *g, int catmull, const V3 *p, float t) {
    if (!catmull) return v3(bezier(p[0].x, p[1].x, p[2].x, p[3].x, t), bezier(p[0].y, p[1].y, p[2].y, p[3].y, t), bezier(p[0].z, p[1].z, p[2].z, p[3].z, t));
    float k = g->state[GEO_TIGHTNESS];
    return v3(curve(p[0].x, p[1].x, p[2].x, p[3].x, t, k), curve(p[0].y, p[1].y, p[2].y, p[3].y, t, k), curve(p[0].z, p[1].z, p[2].z, p[3].z, t, k));
}
/* Input: four control points. bezier() (catmull 0) or curve() (catmull 1) as a stroke of detail segments. */
int gfx_geo_curve(geo *g, int catmull) {
    reset(g); int detail = I(g, GEO_DETAIL);
    V3 *p = malloc((size_t)(detail + 1) * sizeof(V3)); if (!p) return GEO_OUT_OF_MEMORY;
    for (int i = 0; i <= detail; i++) p[i] = curve_point(g, catmull, points_in(g), (float)i / detail);
    polyline(g, p, detail + 1, 0); free(p);
    return finish(g, 0);
}
/* Input: four control points. Writes the sampled points (xyz) from index `from` to detail into the input buffer
   after the controls and returns how many there are. */
int gfx_geo_curve_points(geo *g, int catmull, int from) {
    int detail = I(g, GEO_DETAIL), count = detail - from + 1;
    if (!gfx_geo_input(g, 12 + count * 3)) return GEO_OUT_OF_MEMORY;
    V3 controls[4]; memcpy(controls, g->input, sizeof(controls));
    for (int i = from; i <= detail; i++) ((V3 *)(g->input + 12))[i - from] = curve_point(g, catmull, controls, (float)i / detail);
    return count;
}
static const float box_normals[6][3] = {{0, 0, 1}, {0, 0, -1}, {-1, 0, 0}, {1, 0, 0}, {0, 1, 0}, {0, -1, 0}};
static const int box_faces[6][4] = {{4, 5, 6, 7}, {1, 0, 3, 2}, {0, 4, 7, 3}, {5, 1, 2, 6}, {3, 7, 6, 2}, {0, 1, 5, 4}};
/* Input: w, h, d. */
int gfx_geo_box(geo *g) {
    float w = g->input[0], h = g->input[1], d = g->input[2];
    reset(g);
    V3 p[8] = {v3(-w / 2, -h / 2, -d / 2), v3(w / 2, -h / 2, -d / 2), v3(w / 2, h / 2, -d / 2), v3(-w / 2, h / 2, -d / 2),
               v3(-w / 2, -h / 2, d / 2), v3(w / 2, -h / 2, d / 2), v3(w / 2, h / 2, d / 2), v3(-w / 2, h / 2, d / 2)};
    static const int corners[6] = {0, 1, 2, 0, 2, 3};
    if (I(g, GEO_HAS_FILL)) for (int f = 0; f < 6; f++) for (int i = 0; i < 6; i++) emit(g, GEO_FILL, p[box_faces[f][corners[i]]], g->state + GEO_FILL_COLOR, box_normals[f]);
    if (I(g, GEO_HAS_STROKE)) for (int f = 0; f < 6; f++) { V3 face[4]; for (int i = 0; i < 4; i++) face[i] = p[box_faces[f][i]]; polyline(g, face, 4, 1); }
    return finish(g, 0);
}
static V3 sphere_point(int u, int v, int us, int vs) {
    float a = TAU * u / us, b = PI_F * v / vs;
    return v3(sinf(b) * cosf(a), cosf(b), sinf(b) * sinf(a));
}
/* Input: radius. Strokes (when enabled) first, then the lit fill. */
int gfx_geo_sphere(geo *g, int us, int vs) {
    float radius = g->input[0];
    reset(g);
    if (I(g, GEO_HAS_STROKE)) for (int v = 0; v < vs; v++) for (int u = 0; u < us; u++) {
        V3 a = sphere_point(u, v, us, vs), b = sphere_point(u + 1, v, us, vs), d = sphere_point(u, v + 1, us, vs);
        segment(g, scale(a, radius), scale(b, radius), I(g, GEO_CAP)); segment(g, scale(a, radius), scale(d, radius), I(g, GEO_CAP));
    }
    if (I(g, GEO_HAS_FILL)) for (int v = 0; v < vs; v++) for (int u = 0; u < us; u++) {
        V3 n[4] = {sphere_point(u, v, us, vs), sphere_point(u + 1, v, us, vs), sphere_point(u + 1, v + 1, us, vs), sphere_point(u, v + 1, us, vs)};
        static const int order[6] = {0, 1, 2, 0, 2, 3};
        for (int i = 0; i < 6; i++) { float normal[3] = {n[order[i]].x, n[order[i]].y, n[order[i]].z}; emit(g, GEO_FILL, scale(n[order[i]], radius), g->state + GEO_FILL_COLOR, normal); }
    }
    return finish(g, 0);
}
/* Input: n positions. beginShape(POINTS): a stroke-colored disk at each. */
int gfx_geo_points(geo *g, int n) { reset(g); if (I(g, GEO_HAS_STROKE)) for (int i = 0; i < n; i++) disk(g, points_in(g)[i], weight(g) / 2, g->state + GEO_STROKE_COLOR); return finish(g, 0); }
/* Input: n positions (pairs). beginShape(LINES): a segment per pair. */
int gfx_geo_lines(geo *g, int n) { reset(g); for (int i = 0; i + 1 < n; i += 2) segment(g, points_in(g)[i], points_in(g)[i + 1], I(g, GEO_CAP)); return finish(g, 0); }
/* Input: n shape vertex positions (xyz). Writes triangle vertex indices for beginShape kinds 4 TRIANGLES,
   5 TRIANGLE_STRIP, 6 TRIANGLE_FAN, 7 QUADS, 8 QUAD_STRIP and 9 POLYGON (triangulated); returns how many. */
int gfx_geo_shape_indices(geo *g, int kind, int n) {
    reset(g);
    if (kind == 4) for (int i = 0; i + 2 < n; i += 3) { push_index(g, i); push_index(g, i + 1); push_index(g, i + 2); }
    if (kind == 5) for (int i = 2; i < n; i++) { if (i % 2 == 0) { push_index(g, i - 2); push_index(g, i - 1); push_index(g, i); } else { push_index(g, i - 1); push_index(g, i - 2); push_index(g, i); } }
    if (kind == 6) for (int i = 2; i < n; i++) { push_index(g, 0); push_index(g, i - 1); push_index(g, i); }
    if (kind == 7) for (int i = 0; i + 3 < n; i += 4) { push_index(g, i); push_index(g, i + 1); push_index(g, i + 2); push_index(g, i); push_index(g, i + 2); push_index(g, i + 3); }
    if (kind == 8) for (int i = 2; i + 1 < n; i += 2) { push_index(g, i - 2); push_index(g, i - 1); push_index(g, i + 1); push_index(g, i - 2); push_index(g, i + 1); push_index(g, i); }
    if (kind == 9 && n >= 3) { int result = triangulate(g, points_in(g), n); if (result < 0) return result; }
    return g->failed ? GEO_OUT_OF_MEMORY : g->index_count;
}
/* Input: x, y, w, h, then the tint (rgba). image() quad in the given image mode, texture rows flipped or not. */
int gfx_geo_image(geo *g, int mode, int flipped) {
    float x = g->input[0], y = g->input[1], w = g->input[2], h = g->input[3], tint[4]; memcpy(tint, g->input + 4, sizeof(tint));
    reset(g); bounds(&x, &y, &w, &h, mode);
    float t = flipped ? 1 : 0, bt = 1 - t;
    V3 a = v3(x, y, 0), b = v3(x + w, y, 0), c = v3(x + w, y + h, 0), d = v3(x, y + h, 0);
    begin(g, GEO_UNLIT_FILL);
    vertex(g, a, tint, (V2){0, t}, 0); vertex(g, b, tint, (V2){1, t}, 0); vertex(g, c, tint, (V2){1, bt}, 0);
    vertex(g, a, tint, (V2){0, t}, 0); vertex(g, c, tint, (V2){1, bt}, 0); vertex(g, d, tint, (V2){0, bt}, 0);
    return finish(g, 0);
}
