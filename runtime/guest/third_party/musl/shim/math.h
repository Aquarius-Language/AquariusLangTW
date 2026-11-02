/* The declarations the vendored musl libm sources need, in place of musl's include/math.h (which needs its generated
   bits/). The guest runtime is freestanding Wasm: sqrt, fabs, floor, ceil and trunc are the Wasm instructions
   (correctly rounded, so identical everywhere), the other functions are the vendored musl sources. */
#ifndef _MATH_H
#define _MATH_H
#include <float.h>
#include <features.h>
typedef double double_t;
typedef float float_t;
#define NAN __builtin_nanf("")
#define INFINITY __builtin_inff()
#define HUGE_VAL __builtin_huge_val()
#define isnan(x) __builtin_isnan(x)
#define isinf(x) __builtin_isinf(x)
#define isfinite(x) __builtin_isfinite(x)
#define signbit(x) __builtin_signbit(x)
#define sqrt(x) __builtin_sqrt(x)
#define fabs(x) __builtin_fabs(x)
#define floor(x) __builtin_floor(x)
#define ceil(x) __builtin_ceil(x)
#define trunc(x) __builtin_trunc(x)
double sin(double), cos(double), tan(double), asin(double), acos(double), atan(double), atan2(double, double);
double exp(double), expm1(double), log(double), log2(double), log10(double), pow(double, double), cbrt(double);
double hypot(double, double), sinh(double), cosh(double), tanh(double), fmod(double, double), scalbn(double, int);
#endif
