/* Library calls: the name failures report, typed argument reads with the old messages, and calls that complete later.
 *
 * Every registered function runs through a small wrapper (the X(...) tables of each module) that records the module
 * prefix and the member's names. Failures then read like the earlier implementation: "Processing.rect: Polygon has
 * zero area." or "Dispatch: Workgroup count must be 1..65535." Argument readers fail the call with the same
 * messages and return 0; read every argument, then check aqua_failed(call) once. */
#ifndef GRAPHICS_CALLS_H
#define GRAPHICS_CALLS_H

#include <aqua/aqua.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Records the prefix ("Processing." or "") and the '|'-separated names of the member being called. */
void gfx_enter(aqua_call *call, const char *prefix, const char *names);
/* Fails the call with "<prefix><English name>: <message>". */
void gfx_fail(aqua_call *call, const char *format, ...) __attribute__((format(printf, 2, 3)));
void gfx_vfail(aqua_call *call, const char *format, va_list arguments);
/* Takes the failure library code recorded while running without a call (gfx_fail(NULL, ...)): true with the
   unprefixed message in `out`, false when there was none. Clears it either way. */
bool gfx_take_detached(char *out, size_t size);
/* Formats "<prefix><English name>: <message>" for the current call into `out`. */
void gfx_message(char *out, size_t size, const char *format, va_list arguments);

/* "vv|vv" for two required and two optional arguments of any kind: the runtime checks the count, the functions check
   the kinds themselves. The result stays valid until the next call. */
const char *gfx_signature(int minimum, int maximum);

/* Typed reads of argument `index`; each fails the call (and returns 0, false or an empty string) on a wrong kind. */
double gfx_number(aqua_call *call, uint32_t index);       /* "Expected a finite number." */
float gfx_float(aqua_call *call, uint32_t index);         /* also "Number exceeds float range." */
double gfx_integral(aqua_call *call, uint32_t index);     /* "Expected an integer." */
int32_t gfx_int(aqua_call *call, uint32_t index);         /* also the int32 overflow */
bool gfx_bool(aqua_call *call, uint32_t index);           /* "Expected a Boolean." */
aqua_str gfx_text(aqua_call *call, uint32_t index);       /* "Expected a string." */
aqua_value gfx_array(aqua_call *call, uint32_t index);    /* "Expected an array." */
/* Value checks on already read numbers, with the same messages. */
bool gfx_finite_number(aqua_value v, double *out);
float gfx_positive(aqua_call *call, uint32_t index);      /* "Expected a positive number." */
float gfx_nonnegative(aqua_call *call, uint32_t index);   /* "Expected a nonnegative number." */
int32_t gfx_dimension(aqua_call *call, uint32_t index);   /* "Dimension must be 1..8192." */
/* One of `count` allowed integer constants, else "Invalid mode constant." */
int32_t gfx_choice(aqua_call *call, uint32_t index, int count, const int32_t *options);
#define GFX_CHOICE(call, index, ...) gfx_choice((call), (index), (int)(sizeof((int32_t[]){__VA_ARGS__}) / sizeof(int32_t)), (int32_t[]){__VA_ARGS__})
/* Packed ARGB colors: an integral number in 0..4294967295, else "Expected an ARGB color in 0..4294967295." */
uint32_t gfx_packed(aqua_call *call, uint32_t index);
uint32_t gfx_packed_value(aqua_call *call, aqua_value v);
/* The floats of numeric array elements ("Expected a finite number." for others); NULL after failing. Call scratch. */
float *gfx_floats(aqua_call *call, aqua_value array, uint32_t *count);

/* Arrays of numbers as results. */
aqua_value gfx_doubles(aqua_runtime *rt, const double *values, uint32_t count);

/* ---- Calls that may complete later --------------------------------------------------------------------------------
 * GPU readbacks, device requests and shader validation finish immediately on native targets and asynchronously on the
 * web. A function begins an operation with gfx_async_begin, starts the work, then calls gfx_async_end: if the work
 * already finished the call returns its result as usual, otherwise the calling script waits (aqua_defer) until
 * gfx_async_finish or gfx_async_fail delivers it. `extra` bytes of zeroed space follow the header for the
 * operation's own data (gfx_async_data). */
typedef struct gfx_async gfx_async;
gfx_async *gfx_async_begin(aqua_call *call, size_t extra);
void *gfx_async_data(gfx_async *a);
aqua_runtime *gfx_async_runtime(gfx_async *a);
void gfx_async_end(gfx_async *a);
void gfx_async_finish(gfx_async *a, aqua_value result);
void gfx_async_fail(gfx_async *a, const char *format, ...) __attribute__((format(printf, 2, 3)));
/* Fails with an already complete message (no prefix added). */
void gfx_async_fail_text(gfx_async *a, const char *message);
/* The "<prefix><name>: " the operation's failures carry. */
const char *gfx_async_prefix(gfx_async *a);
/* The starting call while it runs, else NULL: library code given a NULL call records its failure (gfx_fail) for
   gfx_async_settle, which delivers `result` when `ok` and otherwise that failure. */
aqua_call *gfx_async_call(gfx_async *a);
void gfx_async_settle(gfx_async *a, bool ok, aqua_value result);

#endif
