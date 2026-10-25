/* Processing math: constants, scalar functions, curve math, seeded random numbers and Perlin noise. random() keeps the
   sequences scripts saw before: the seeded generator is .NET's System.Random (Knuth's subtractive method), and noise
   uses the same permutation shuffle and octave sum. */
#include "processing.h"
#include <math.h>
#include <time.h>

/* ---- System.Random (seeded) ---- */

void gfx_random_seed(processing *p, int seed) {
    struct net_random *r = &p->random;
    int subtraction = seed == INT32_MIN ? INT32_MAX : (seed < 0 ? -seed : seed);
    int mj = 161803398 - subtraction;
    r->seed_array[55] = mj;
    int mk = 1, ii = 0;
    for (int i = 1; i < 55; i++) {
        if ((ii += 21) >= 55) ii -= 55;
        r->seed_array[ii] = mk;
        mk = mj - mk;
        if (mk < 0) mk += INT32_MAX;
        mj = r->seed_array[ii];
    }
    for (int k = 1; k < 5; k++)
        for (int i = 1; i < 56; i++) {
            int n = i + 30;
            if (n >= 55) n -= 55;
            r->seed_array[i] -= r->seed_array[1 + n];
            if (r->seed_array[i] < 0) r->seed_array[i] += INT32_MAX;
        }
    r->inext = 0; r->inextp = 21;
    r->seeded = true;
}

static int internal_sample(processing *p) {
    struct net_random *r = &p->random;
    if (!r->seeded) gfx_random_seed(p, (int)((long long)time(NULL) ^ (long long)(aqua_now_ms(p->rt) * 1000)));
    int next = r->inext, nextp = r->inextp;
    if (++next >= 56) next = 1;
    if (++nextp >= 56) nextp = 1;
    int value = r->seed_array[next] - r->seed_array[nextp];
    if (value == INT32_MAX) value--;
    if (value < 0) value += INT32_MAX;
    r->seed_array[next] = value;
    r->inext = next; r->inextp = nextp;
    return value;
}
double gfx_random_next(processing *p) { return internal_sample(p) * (1.0 / INT32_MAX); }
static int random_below(processing *p, int maximum) { return (int)(gfx_random_next(p) * maximum); }

/* ---- Noise ---- */

void gfx_noise_seed(processing *p, int seed) {
    struct net_random saved = p->random;
    gfx_random_seed(p, seed);
    int perm[256];
    for (int i = 0; i < 256; i++) perm[i] = i;
    for (int i = 255; i > 0; i--) { int j = random_below(p, i + 1); int t = perm[i]; perm[i] = perm[j]; perm[j] = t; }
    for (int i = 0; i < 512; i++) p->noise_permutation[i] = perm[i & 255];
    p->random = saved;
}

static double fade(double t) { return t * t * t * (t * (t * 6 - 15) + 10); }
static double mix(double a, double b, double t) { return a + (b - a) * t; }
static double grad(int h, double a, double b, double c) {
    h &= 15;
    double u = h < 8 ? a : b, v = h < 4 ? b : (h == 12 || h == 14) ? a : c;
    return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
}
static double perlin(processing *p, double x, double y, double z) {
    x = fmod(fabs(x), 256); y = fmod(fabs(y), 256); z = fmod(fabs(z), 256);
    int X = (int)floor(x) & 255, Y = (int)floor(y) & 255, Z = (int)floor(z) & 255;
    x -= floor(x); y -= floor(y); z -= floor(z);
    const int *P = p->noise_permutation;
    int A = P[X] + Y, AA = P[A] + Z, AB = P[A + 1] + Z, B = P[X + 1] + Y, BA = P[B] + Z, BB = P[B + 1] + Z;
    double u = fade(x), v = fade(y), w = fade(z);
    return mix(mix(mix(grad(P[AA], x, y, z), grad(P[BA], x - 1, y, z), u), mix(grad(P[AB], x, y - 1, z), grad(P[BB], x - 1, y - 1, z), u), v),
               mix(mix(grad(P[AA + 1], x, y, z - 1), grad(P[BA + 1], x - 1, y, z - 1), u), mix(grad(P[AB + 1], x, y - 1, z - 1), grad(P[BB + 1], x - 1, y - 1, z - 1), u), v), w);
}
static double noise(processing *p, double x, double y, double z) {
    x = fmod(fabs(x), 256); y = fmod(fabs(y), 256); z = fmod(fabs(z), 256);
    double sum = 0, amplitude = .5, total = 0;
    for (int i = 0; i < p->noise_octaves; i++) {
        sum += (perlin(p, x, y, z) + 1) * .5 * amplitude;
        total += amplitude;
        amplitude *= p->noise_falloff;
        x *= 2; y *= 2; z *= 2;
    }
    double r = sum / total;
    return r < 0 ? 0 : r > 1 ? 1 : r;
}

/* ---- Functions ---- */

static void unary(aqua_call *call, double (*f)(double), bool integer) {
    double n = gfx_number(call, 0);
    if (aqua_failed(call)) return;
    n = f(n);
    if (!isfinite(n)) { gfx_fail(call, "Arguments are outside the function's domain."); return; }
    if (integer) {
        if (n < -2147483648.0 || n > 2147483647.0) { gfx_fail(call, "Arithmetic operation resulted in an overflow."); return; }
        aqua_return_int(call, (int32_t)n);
    } else aqua_return_double(call, n);
}
static double round_half_up(double x) { return floor(x + .5); }
static double square(double x) { return x * x; }
static double to_radians(double x) { return x * 3.14159265358979323846 / 180; }
static double to_degrees(double x) { return x * 180 / 3.14159265358979323846; }
static void p_abs(aqua_call *call, processing *p) { (void)p; unary(call, fabs, false); }
static void p_ceil(aqua_call *call, processing *p) { (void)p; unary(call, ceil, true); }
static void p_floor(aqua_call *call, processing *p) { (void)p; unary(call, floor, true); }
static void p_round(aqua_call *call, processing *p) { (void)p; unary(call, round_half_up, true); }
static void p_sqrt(aqua_call *call, processing *p) { (void)p; unary(call, sqrt, false); }
static void p_sq(aqua_call *call, processing *p) { (void)p; unary(call, square, false); }
static void p_exp(aqua_call *call, processing *p) { (void)p; unary(call, exp, false); }
static void p_log(aqua_call *call, processing *p) { (void)p; unary(call, log, false); }
static void p_sin(aqua_call *call, processing *p) { (void)p; unary(call, sin, false); }
static void p_cos(aqua_call *call, processing *p) { (void)p; unary(call, cos, false); }
static void p_tan(aqua_call *call, processing *p) { (void)p; unary(call, tan, false); }
static void p_asin(aqua_call *call, processing *p) { (void)p; unary(call, asin, false); }
static void p_acos(aqua_call *call, processing *p) { (void)p; unary(call, acos, false); }
static void p_atan(aqua_call *call, processing *p) { (void)p; unary(call, atan, false); }
static void p_radians(aqua_call *call, processing *p) { (void)p; unary(call, to_radians, false); }
static void p_degrees(aqua_call *call, processing *p) { (void)p; unary(call, to_degrees, false); }

static void p_pow(aqua_call *call, processing *p) {
    (void)p;
    double a = gfx_number(call, 0), b = gfx_number(call, 1);
    if (aqua_failed(call)) return;
    double n = pow(a, b);
    if (!isfinite(n)) { gfx_fail(call, "Invalid power."); return; }
    aqua_return_double(call, n);
}
static void p_atan2(aqua_call *call, processing *p) {
    (void)p;
    double y = gfx_number(call, 0), x = gfx_number(call, 1);
    if (!aqua_failed(call)) aqua_return_double(call, atan2(y, x));
}
static void extreme(aqua_call *call, bool minimum) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t n = aqua_argc(call);
    bool array = n == 1 && aqua_arg(call, 0).type == AQUA_ARRAY;
    uint32_t count = array ? aqua_array_length(rt, aqua_arg(call, 0)) : n;
    if (count == 0) { gfx_fail(call, "Expected nonempty numbers."); return; }
    double result = 0;
    for (uint32_t i = 0; i < count; i++) {
        double value;
        aqua_value v = array ? aqua_array_get(rt, aqua_arg(call, 0), i) : aqua_arg(call, i);
        if (!gfx_finite_number(v, &value)) { gfx_fail(call, "Expected a finite number."); return; }
        if (i == 0 || (minimum ? value < result : value > result)) result = value;
    }
    aqua_return_double(call, result);
}
static void p_min(aqua_call *call, processing *p) { (void)p; extreme(call, true); }
static void p_max(aqua_call *call, processing *p) { (void)p; extreme(call, false); }
static void p_constrain(aqua_call *call, processing *p) {
    (void)p;
    double v = gfx_number(call, 0), lo = gfx_number(call, 1), hi = gfx_number(call, 2);
    if (aqua_failed(call)) return;
    if (lo > hi) { gfx_fail(call, "Minimum exceeds maximum."); return; }
    aqua_return_double(call, v < lo ? lo : v > hi ? hi : v);
}
static void p_lerp(aqua_call *call, processing *p) {
    (void)p;
    double a = gfx_number(call, 0), b = gfx_number(call, 1), t = gfx_number(call, 2);
    if (!aqua_failed(call)) aqua_return_double(call, a + (b - a) * t);
}
static void p_norm(aqua_call *call, processing *p) {
    (void)p;
    double v = gfx_number(call, 0), lo = gfx_number(call, 1), hi = gfx_number(call, 2);
    if (aqua_failed(call)) return;
    double d = hi - lo;
    if (d == 0) { gfx_fail(call, "Input range is zero."); return; }
    aqua_return_double(call, (v - lo) / d);
}
static void p_map(aqua_call *call, processing *p) {
    (void)p;
    double v = gfx_number(call, 0), lo = gfx_number(call, 1), hi = gfx_number(call, 2), a = gfx_number(call, 3), b = gfx_number(call, 4);
    if (aqua_failed(call)) return;
    double d = hi - lo;
    if (d == 0) { gfx_fail(call, "Input range is zero."); return; }
    aqua_return_double(call, a + (v - lo) / d * (b - a));
}
static void p_dist(aqua_call *call, processing *p) {
    (void)p;
    uint32_t n = aqua_argc(call);
    if (n != 4 && n != 6) { gfx_fail(call, "Expected 4 or 6 coordinates."); return; }
    uint32_t d = n / 2;
    double sum = 0;
    for (uint32_t i = 0; i < d; i++) { double a = gfx_number(call, i), b = gfx_number(call, i + d); sum += pow(a - b, 2); }
    if (!aqua_failed(call)) aqua_return_double(call, sqrt(sum));
}
static void p_mag(aqua_call *call, processing *p) {
    (void)p;
    double sum = 0;
    for (uint32_t i = 0; i < aqua_argc(call); i++) sum += pow(gfx_number(call, i), 2);
    if (!aqua_failed(call)) aqua_return_double(call, sqrt(sum));
}
static void p_randomSeed(aqua_call *call, processing *p) { int seed = gfx_int(call, 0); if (!aqua_failed(call)) gfx_random_seed(p, seed); }
static void p_random(aqua_call *call, processing *p) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t n = aqua_argc(call);
    if (n == 1 && aqua_arg(call, 0).type == AQUA_ARRAY) {
        uint32_t count = aqua_array_length(rt, aqua_arg(call, 0));
        if (!count) { gfx_fail(call, "Cannot select from an empty array."); return; }
        aqua_return(call, aqua_array_get(rt, aqua_arg(call, 0), (uint32_t)random_below(p, (int)count)));
        return;
    }
    double lo = n == 2 ? gfx_number(call, 0) : 0, hi = gfx_number(call, n - 1);
    if (aqua_failed(call)) return;
    if (lo > hi) { gfx_fail(call, "Minimum exceeds maximum."); return; }
    aqua_return_double(call, lo + gfx_random_next(p) * (hi - lo));
}
static void p_randomGaussian(aqua_call *call, processing *p) {
    double a = gfx_random_next(p), b = gfx_random_next(p);
    aqua_return_double(call, sqrt(-2 * log(1 - a)) * cos(6.28318530717958647692 * b));
}
static void p_noiseSeed(aqua_call *call, processing *p) { int seed = gfx_int(call, 0); if (!aqua_failed(call)) gfx_noise_seed(p, seed); }
static void p_noiseDetail(aqua_call *call, processing *p) {
    int n = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    if (n < 1 || n > 16) { gfx_fail(call, "Noise octaves must be 1..16."); return; }
    double falloff = aqua_argc(call) == 2 ? gfx_number(call, 1) : p->noise_falloff;
    if (aqua_failed(call)) return;
    if (falloff <= 0 || falloff > 1) { gfx_fail(call, "Noise falloff must be >0 and <=1."); return; }
    p->noise_octaves = n; p->noise_falloff = falloff;
}
static void p_noise(aqua_call *call, processing *p) {
    uint32_t n = aqua_argc(call);
    double x = gfx_number(call, 0), y = n > 1 ? gfx_number(call, 1) : 0, z = n > 2 ? gfx_number(call, 2) : 0;
    if (!aqua_failed(call)) aqua_return_double(call, noise(p, x, y, z));
}

static float bezier(float a, float b, float c, float d, float t) { float s = 1 - t; return s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d; }
static float bezier_tangent(float a, float b, float c, float d, float t) { float s = 1 - t; return 3 * s * s * (b - a) + 6 * s * t * (c - b) + 3 * t * t * (d - c); }
static float curve(float a, float b, float c, float d, float t) { float s = (1 - 0.0f) / 2; return bezier(b, b + s * (c - a) / 3, c - s * (d - b) / 3, c, t); }
static void read5(aqua_call *call, float *v) { for (uint32_t i = 0; i < 5; i++) v[i] = gfx_float(call, i); }
static void p_bezierPoint(aqua_call *call, processing *p) { (void)p; float v[5]; read5(call, v); if (!aqua_failed(call)) aqua_return_double(call, bezier(v[0], v[1], v[2], v[3], v[4])); }
static void p_bezierTangent(aqua_call *call, processing *p) { (void)p; float v[5]; read5(call, v); if (!aqua_failed(call)) aqua_return_double(call, bezier_tangent(v[0], v[1], v[2], v[3], v[4])); }
static void p_curvePoint(aqua_call *call, processing *p) { (void)p; float v[5]; read5(call, v); if (!aqua_failed(call)) aqua_return_double(call, curve(v[0], v[1], v[2], v[3], v[4])); }
static void p_curveTangent(aqua_call *call, processing *p) {
    (void)p;
    float v[5];
    read5(call, v);
    if (aqua_failed(call)) return;
    float e = .0001f;
    aqua_return_double(call, (curve(v[0], v[1], v[2], v[3], v[4] + e) - curve(v[0], v[1], v[2], v[3], v[4] - e)) / (2 * e));
}

#define MATH(X) \
    X(abs, "abs|絕對值", 1, 1) X(ceil, "ceil|向上取整", 1, 1) X(floor, "floor|向下取整", 1, 1) X(round, "round|四捨五入", 1, 1) \
    X(sqrt, "sqrt|平方根", 1, 1) X(sq, "sq|平方", 1, 1) X(exp, "exp|指數", 1, 1) X(log, "log|對數", 1, 1) X(sin, "sin|正弦", 1, 1) \
    X(cos, "cos|餘弦", 1, 1) X(tan, "tan|正切", 1, 1) X(asin, "asin|反正弦", 1, 1) X(acos, "acos|反餘弦", 1, 1) X(atan, "atan|反正切", 1, 1) \
    X(radians, "radians|弧度", 1, 1) X(degrees, "degrees|角度", 1, 1) X(pow, "pow|次方", 2, 2) X(atan2, "atan2|雙參數反正切", 2, 2) \
    X(min, "min|最小值", 1, 64) X(max, "max|最大值", 1, 64) X(constrain, "constrain|限制範圍", 3, 3) X(lerp, "lerp|線性插值", 3, 3) \
    X(norm, "norm|範圍正規化", 3, 3) X(map, "map|映射", 5, 5) X(dist, "dist|距離", 4, 6) X(mag, "mag|長度", 2, 3) \
    X(randomSeed, "randomSeed|亂數種子", 1, 1) X(random, "random|亂數", 1, 2) X(randomGaussian, "randomGaussian|高斯亂數", 0, 0) \
    X(noiseSeed, "noiseSeed|雜訊種子", 1, 1) X(noiseDetail, "noiseDetail|雜訊細節", 1, 2) X(noise, "noise|雜訊", 1, 3) \
    X(bezierPoint, "bezierPoint|貝茲點", 5, 5) X(bezierTangent, "bezierTangent|貝茲切線", 5, 5) X(curvePoint, "curvePoint|曲線點", 5, 5) \
    X(curveTangent, "curveTangent|曲線切線", 5, 5)
MATH(MODULE_WRAPPER)

void gfx_math_register(aqua_class *module) {
    MATH(MODULE_DEFINE)
    aqua_define_double(module, "PI", 3.14159265358979323846);
    aqua_define_double(module, "TWO_PI", 6.28318530717958647692);
    aqua_define_double(module, "TAU", 6.28318530717958647692);
    aqua_define_double(module, "HALF_PI", 3.14159265358979323846 / 2);
    aqua_define_double(module, "QUARTER_PI", 3.14159265358979323846 / 4);
}
