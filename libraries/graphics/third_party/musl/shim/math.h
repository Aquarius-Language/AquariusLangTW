/* The declarations the vendored musl libm sources need, in place of musl's include/math.h (which needs its generated
   bits/). sqrt, fabs, floor, ceil and trunc are the compiler's builtins (correctly rounded on every target); the other
   functions are the vendored sources, renamed by aqua_libm.h (included first). */
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
#include "../aqua_libm.h"
#endif
