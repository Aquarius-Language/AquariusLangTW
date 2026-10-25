/* Colors: packed ARGB (exact unsigned numbers, not signed Java integers), HSB conversion and the argument forms of
   fill(), stroke(), background(), tint(), color() and the material colors. */
#include "processing.h"
#include <math.h>

static float clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

vec4 gfx_color_unpack(uint32_t c) {
    vec4 v = {(float)(c >> 16 & 255) / 255.0f, (float)(c >> 8 & 255) / 255.0f, (float)(c & 255) / 255.0f, (float)(c >> 24) / 255.0f};
    return v;
}

/* MathF.Round rounds half to even, as rintf does in the default rounding mode. */
static uint32_t channel(float v) {
    float r = rintf(v * 255);
    int n = r != r ? 0 : r < 0 ? 0 : r > 255 ? 255 : (int)r;
    return (uint32_t)n;
}
uint32_t gfx_color_pack(vec4 c) { return channel(c.w) << 24 | channel(c.x) << 16 | channel(c.y) << 8 | channel(c.z); }

vec4 gfx_color_hsb(float h, float s, float b, float a) {
    h = (h - floorf(h)) * 6;
    s = clamp01(s); b = clamp01(b);
    float f = fmodf(h, 1), p = b * (1 - s), q = b * (1 - s * f), t = b * (1 - s * (1 - f));
    vec4 r;
    switch ((int)h) {
    case 0: r = (vec4){b, t, p, a}; break;
    case 1: r = (vec4){q, b, p, a}; break;
    case 2: r = (vec4){p, b, t, a}; break;
    case 3: r = (vec4){p, q, b, a}; break;
    case 4: r = (vec4){t, p, b, a}; break;
    default: r = (vec4){b, p, q, a}; break;
    }
    return r;
}

vec3 gfx_color_rgb_to_hsb(vec4 c) {
    float max = fmaxf(c.x, fmaxf(c.y, c.z)), min = fminf(c.x, fminf(c.y, c.z)), d = max - min;
    float h = d == 0 ? 0 : max == c.x ? fmodf((c.y - c.z) / d + 6, 6) : max == c.y ? (c.z - c.x) / d + 2 : (c.x - c.y) / d + 4;
    return v3(h / 6, max == 0 ? 0 : d / max, max);
}

bool gfx_color_arguments(aqua_call *call, const style *s, uint32_t start, uint32_t count, vec4 *out) {
    if (count < 1 || count > 4) { gfx_fail(call, "Expected grayscale[,alpha], RGB/HSB[,alpha], or packed color."); return false; }
    float alpha = 1;
    if (count == 2 || count == 4) alpha = gfx_float(call, start + count - 1) / s->color_max.w;
    if (aqua_failed(call)) return false;
    if (count <= 2) {
        double n = gfx_number(call, start);
        if (aqua_failed(call)) return false;
        if (n > s->color_max.z) {
            uint32_t packed = gfx_packed(call, start);
            if (aqua_failed(call)) return false;
            vec4 c = gfx_color_unpack(packed);
            if (count == 2) c.w = alpha;
            *out = c;
            return true;
        }
        float gray = (float)n / s->color_max.z;
        *out = (vec4){clamp01(gray), clamp01(gray), clamp01(gray), clamp01(alpha)};
        return true;
    }
    vec4 v = {gfx_float(call, start) / s->color_max.x, gfx_float(call, start + 1) / s->color_max.y, gfx_float(call, start + 2) / s->color_max.z, alpha};
    if (aqua_failed(call)) return false;
    if (s->color_mode == 1) v = gfx_color_hsb(v.x, v.y, v.z, v.w);
    *out = (vec4){clamp01(v.x), clamp01(v.y), clamp01(v.z), clamp01(v.w)};
    return true;
}
