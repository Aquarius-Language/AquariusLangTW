/* Pixel conversion shared by every platform: a delivered frame (RGB, BGR, packed YUV 4:2:2, planar or interleaved YUV
 * 4:2:0, gray; any stride, bottom-up included) becomes upright opaque RGBA, then a separable triangle filter scales it
 * to the requested size: averaging when shrinking, bilinear when enlarging. YUV uses BT.601 limited range. */
#include "camera.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

static inline uint8_t clamp(int32_t v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

static inline void yuv(uint8_t *out, int32_t y, int32_t u, int32_t v) {
    int32_t c = 298 * (y - 16), d = u - 128, e = v - 128;
    out[0] = clamp((c + 409 * e + 128) >> 8);
    out[1] = clamp((c - 100 * d - 208 * e + 128) >> 8);
    out[2] = clamp((c + 516 * d + 128) >> 8);
    out[3] = 255;
}

static bool reserve(void **buffer, size_t *capacity, size_t bytes) {
    if (*capacity >= bytes) return true;
    void *grown = realloc(*buffer, bytes);
    if (!grown) return false;
    *buffer = grown;
    *capacity = bytes;
    return true;
}

/* Converts one source row into upright RGBA: `out` is the destination pixel of x = 0 and `step` the distance in pixels
   between consecutive x, which is how the rotation is applied. */
static void convert_row(const camera_source *s, int32_t y, uint8_t *out, ptrdiff_t step) {
    const uint8_t *row = s->planes[0] + (ptrdiff_t)y * s->strides[0];
    ptrdiff_t d = step * 4;
    switch (s->format) {
    case CAMERA_RGBA:
        for (int32_t x = 0; x < s->width; x++, out += d, row += 4) { out[0] = row[0]; out[1] = row[1]; out[2] = row[2]; out[3] = 255; }
        break;
    case CAMERA_BGRA:
        for (int32_t x = 0; x < s->width; x++, out += d, row += 4) { out[0] = row[2]; out[1] = row[1]; out[2] = row[0]; out[3] = 255; }
        break;
    case CAMERA_RGB24:
        for (int32_t x = 0; x < s->width; x++, out += d, row += 3) { out[0] = row[0]; out[1] = row[1]; out[2] = row[2]; out[3] = 255; }
        break;
    case CAMERA_BGR24:
        for (int32_t x = 0; x < s->width; x++, out += d, row += 3) { out[0] = row[2]; out[1] = row[1]; out[2] = row[0]; out[3] = 255; }
        break;
    case CAMERA_YUYV:
    case CAMERA_UYVY: {
        int32_t y0 = s->format == CAMERA_YUYV ? 0 : 1, u = s->format == CAMERA_YUYV ? 1 : 0;
        for (int32_t x = 0; x < s->width; x++, out += d) {
            const uint8_t *pair = row + (x / 2) * 4;
            yuv(out, pair[y0 + (x & 1) * 2], pair[u], pair[u + 2]);
        }
        break;
    }
    case CAMERA_YUV420: {
        const uint8_t *us = s->planes[1] + (ptrdiff_t)(y / 2) * s->strides[1], *vs = s->planes[2] + (ptrdiff_t)(y / 2) * s->strides[2];
        int32_t pu = s->pixel_strides[1] ? s->pixel_strides[1] : 1, pv = s->pixel_strides[2] ? s->pixel_strides[2] : 1;
        for (int32_t x = 0; x < s->width; x++, out += d) yuv(out, row[x], us[(x / 2) * pu], vs[(x / 2) * pv]);
        break;
    }
    case CAMERA_GRAY:
        for (int32_t x = 0; x < s->width; x++, out += d) { out[0] = out[1] = out[2] = row[x]; out[3] = 255; }
        break;
    }
}

/* The source as RGBA, rotated upright, at its own resolution. */
static uint8_t *upright(camera_converter *c, const camera_source *s, int32_t *width, int32_t *height) {
    int32_t w = s->width, h = s->height, r = s->rotation;
    bool turned = r == 90 || r == 270;
    int32_t uw = turned ? h : w, uh = turned ? w : h;
    if (!reserve((void **)&c->upright, &c->upright_capacity, (size_t)uw * (size_t)uh * 4)) return NULL;
    for (int32_t y = 0; y < h; y++) {
        ptrdiff_t start, step;
        switch (r) {
        case 90: start = h - 1 - y; step = uw; break;                       /* (x, y) -> (h-1-y, x) */
        case 180: start = (ptrdiff_t)(h - 1 - y) * uw + (w - 1); step = -1; break;
        case 270: start = (ptrdiff_t)(w - 1) * uw + y; step = -uw; break;    /* (x, y) -> (y, w-1-x) */
        default: start = (ptrdiff_t)y * uw; step = 1; break;
        }
        convert_row(s, y, c->upright + start * 4, step);
    }
    *width = uw;
    *height = uh;
    return c->upright;
}

/* Triangle-filter taps of one axis (support widened to the shrink factor), weights summing to 1 << 14. */
static bool axis_build(camera_axis *a, int32_t source, int32_t target) {
    if (a->source == source && a->target == target && a->index) return true;
    free(a->index); free(a->weight);
    a->index = NULL; a->weight = NULL;
    double scale = (double)target / source, support = scale < 1 ? 1 / scale : 1;
    int32_t taps = (int32_t)ceil(support * 2) + 1;
    a->index = malloc((size_t)target * (size_t)taps * sizeof *a->index);
    a->weight = calloc((size_t)target * (size_t)taps, sizeof *a->weight);
    double *w = malloc((size_t)taps * sizeof *w);
    if (!a->index || !a->weight || !w) { free(w); return false; }
    for (int32_t i = 0; i < target; i++) {
        double center = (i + 0.5) / scale - 0.5, total = 0;
        int32_t first = (int32_t)ceil(center - support);
        for (int32_t k = 0; k < taps; k++) {
            double distance = fabs(first + k - center) / support;
            w[k] = distance < 1 ? 1 - distance : 0;
            total += w[k];
        }
        int32_t sum = 0, largest = 0;
        for (int32_t k = 0; k < taps; k++) {
            int32_t j = first + k;
            a->index[i * taps + k] = j < 0 ? 0 : j >= source ? source - 1 : j;
            int16_t q = total > 0 ? (int16_t)lround(w[k] / total * 16384) : (int16_t)(k == 0 ? 16384 : 0);
            a->weight[i * taps + k] = q;
            sum += q;
            if (q > a->weight[i * taps + largest]) largest = k;
        }
        a->weight[i * taps + largest] = (int16_t)(a->weight[i * taps + largest] + (16384 - sum));
    }
    free(w);
    a->source = source;
    a->target = target;
    a->taps = taps;
    return true;
}

/* Scales RGBA `src` (sw x sh) into `dst` (dw x dh, rows `pitch` bytes apart). */
static bool resample(camera_converter *c, const uint8_t *src, int32_t sw, int32_t sh, uint8_t *dst, int32_t dw, int32_t dh, size_t pitch) {
    if (sw == dw && sh == dh) {
        for (int32_t y = 0; y < dh; y++) memcpy(dst + (size_t)y * pitch, src + (size_t)y * sw * 4, (size_t)dw * 4);
        return true;
    }
    if (!axis_build(&c->x, sw, dw) || !axis_build(&c->y, sh, dh)) return false;
    if (!reserve((void **)&c->middle, &c->middle_capacity, (size_t)dw * (size_t)sh * 4) ||
        !reserve((void **)&c->sums, &c->sums_capacity, (size_t)dw * 3 * sizeof *c->sums)) return false;
    const camera_axis *ax = &c->x, *ay = &c->y;
    for (int32_t y = 0; y < sh; y++) {
        const uint8_t *in = src + (size_t)y * sw * 4;
        uint8_t *out = c->middle + (size_t)y * dw * 4;
        for (int32_t x = 0; x < dw; x++, out += 4) {
            int32_t r = 0, g = 0, b = 0;
            const int32_t *index = ax->index + x * ax->taps;
            const int16_t *weight = ax->weight + x * ax->taps;
            for (int32_t k = 0; k < ax->taps; k++) {
                const uint8_t *p = in + index[k] * 4;
                r += weight[k] * p[0]; g += weight[k] * p[1]; b += weight[k] * p[2];
            }
            out[0] = clamp((r + 8192) >> 14); out[1] = clamp((g + 8192) >> 14); out[2] = clamp((b + 8192) >> 14); out[3] = 255;
        }
    }
    for (int32_t y = 0; y < dh; y++) {
        int32_t *sums = c->sums;
        memset(sums, 0, (size_t)dw * 3 * sizeof *sums);
        const int32_t *index = ay->index + y * ay->taps;
        const int16_t *weight = ay->weight + y * ay->taps;
        for (int32_t k = 0; k < ay->taps; k++) {
            if (!weight[k]) continue;
            const uint8_t *in = c->middle + (size_t)index[k] * dw * 4;
            for (int32_t x = 0; x < dw; x++) {
                sums[x * 3] += weight[k] * in[x * 4];
                sums[x * 3 + 1] += weight[k] * in[x * 4 + 1];
                sums[x * 3 + 2] += weight[k] * in[x * 4 + 2];
            }
        }
        uint8_t *out = dst + (size_t)y * pitch;
        for (int32_t x = 0; x < dw; x++, out += 4) {
            out[0] = clamp((sums[x * 3] + 8192) >> 14); out[1] = clamp((sums[x * 3 + 1] + 8192) >> 14);
            out[2] = clamp((sums[x * 3 + 2] + 8192) >> 14); out[3] = 255;
        }
    }
    return true;
}

static bool valid(const camera_source *s) {
    if (s->width < 1 || s->height < 1 || s->width > 8192 || s->height > 8192 || !s->planes[0]) return false;
    if (s->rotation != 0 && s->rotation != 90 && s->rotation != 180 && s->rotation != 270) return false;
    if (s->format == CAMERA_YUV420 && (!s->planes[1] || !s->planes[2])) return false;
    return true;
}

bool camera_convert(camera_converter *c, const camera_source *source, int32_t width, int32_t height, bool letterbox, uint8_t *output) {
    if (!valid(source) || width < 1 || height < 1) return false;
    int32_t uw, uh;
    const uint8_t *pixels;
    if (source->format == CAMERA_RGBA && source->rotation == 0 && source->strides[0] == source->width * 4) {
        pixels = source->planes[0]; /* already upright RGBA: scale it directly */
        uw = source->width; uh = source->height;
    } else if (!(pixels = upright(c, source, &uw, &uh))) return false;
    int32_t cw = width, ch = height, ox = 0, oy = 0;
    if (letterbox) {
        double scale = fmin((double)width / uw, (double)height / uh);
        cw = (int32_t)lround(uw * scale); ch = (int32_t)lround(uh * scale);
        cw = cw < 1 ? 1 : cw > width ? width : cw;
        ch = ch < 1 ? 1 : ch > height ? height : ch;
        ox = (width - cw) / 2; oy = (height - ch) / 2;
        if (cw != width || ch != height) {
            for (size_t i = 0, n = (size_t)width * (size_t)height; i < n; i++) {
                output[i * 4] = output[i * 4 + 1] = output[i * 4 + 2] = 0;
                output[i * 4 + 3] = 255;
            }
        }
    }
    size_t pitch = (size_t)width * 4;
    return resample(c, pixels, uw, uh, output + (size_t)oy * pitch + (size_t)ox * 4, cw, ch, pitch);
}

double camera_mode_cost(const camera_session *s, int32_t width, int32_t height, double fps, bool raw) {
    if (width < 1 || height < 1) return INFINITY;
    double cost = fps > 0 && fps + 0.5 < s->fps ? (s->fps - fps) * 100 : fps > s->fps ? (fps - s->fps) * 0.5 : 0;
    double fit = fmin((double)s->width / width, (double)s->height / height);
    cost += fit > 1 ? log(fit) * 300 : -log(fit) * 20;
    cost += fabs(log(((double)width / height) / ((double)s->width / s->height))) * 200;
    return raw ? cost : cost + 30;
}

void camera_converter_free(camera_converter *c) {
    free(c->upright); free(c->middle); free(c->sums);
    free(c->x.index); free(c->x.weight); free(c->y.index); free(c->y.weight);
    memset(c, 0, sizeof *c);
}
