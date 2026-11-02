/* musl's libm (this folder) under private names: every symbol gets the aqua_graphics_ prefix, since the library is
   linked statically into the runtime beside the C library's own libm. The vendored sources are compiled with this
   header first; library code includes it after <math.h> to call the musl functions by their usual names (Processing
   and GLM math), which are then the guest runtime's 數學 functions bit for bit on every target. */
#ifndef AQUA_GRAPHICS_LIBM_H
#define AQUA_GRAPHICS_LIBM_H
#define sin aqua_graphics_sin
#define cos aqua_graphics_cos
#define tan aqua_graphics_tan
#define asin aqua_graphics_asin
#define acos aqua_graphics_acos
#define atan aqua_graphics_atan
#define atan2 aqua_graphics_atan2
#define exp aqua_graphics_exp
#define log aqua_graphics_log
#define pow aqua_graphics_pow
#define scalbn aqua_graphics_scalbn
#define __sin aqua_graphics___sin
#define __cos aqua_graphics___cos
#define __tan aqua_graphics___tan
#define __rem_pio2 aqua_graphics___rem_pio2
#define __rem_pio2_large aqua_graphics___rem_pio2_large
#define __math_oflow aqua_graphics___math_oflow
#define __math_uflow aqua_graphics___math_uflow
#define __math_xflow aqua_graphics___math_xflow
#define __math_divzero aqua_graphics___math_divzero
#define __math_invalid aqua_graphics___math_invalid
#define __exp_data aqua_graphics___exp_data
#define __log_data aqua_graphics___log_data
#define __pow_log_data aqua_graphics___pow_log_data
double sin(double), cos(double), tan(double), asin(double), acos(double), atan(double), atan2(double, double);
double exp(double), log(double), pow(double, double), scalbn(double, int);
#endif
