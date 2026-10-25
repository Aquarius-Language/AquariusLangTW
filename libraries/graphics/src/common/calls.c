#include "calls.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Calls run on their runtime's thread; Thread workers run their own runtimes, so the record is per thread. */
#if defined(AQUA_OS_WEB)
#define GFX_THREAD_LOCAL
#else
#define GFX_THREAD_LOCAL _Thread_local
#endif
static GFX_THREAD_LOCAL const char *current_prefix = "";
static GFX_THREAD_LOCAL const char *current_names = "";

void gfx_enter(aqua_call *call, const char *prefix, const char *names) {
    (void)call;
    current_prefix = prefix;
    current_names = names;
}

static void prefix_text(char *out, size_t size) {
    const char *bar = strchr(current_names, '|');
    int length = bar ? (int)(bar - current_names) : (int)strlen(current_names);
    snprintf(out, size, "%s%.*s: ", current_prefix, length, current_names);
}

void gfx_message(char *out, size_t size, const char *format, va_list arguments) {
    prefix_text(out, size);
    size_t used = strlen(out);
    if (used < size) vsnprintf(out + used, size - used, format, arguments);
}

/* Failures of work continuing after its call returned (call == NULL): kept unprefixed for gfx_async_settle. */
static GFX_THREAD_LOCAL char detached[2048];
static GFX_THREAD_LOCAL bool detached_failed;

void gfx_vfail(aqua_call *call, const char *format, va_list arguments) {
    if (!call) {
        if (!detached_failed) { vsnprintf(detached, sizeof detached, format, arguments); detached_failed = true; }
        return;
    }
    if (aqua_failed(call)) return;
    char message[2048];
    gfx_message(message, sizeof message, format, arguments);
    aqua_fail(call, "%s", message);
}

void gfx_fail(aqua_call *call, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    gfx_vfail(call, format, arguments);
    va_end(arguments);
}

bool gfx_take_detached(char *out, size_t size) {
    bool failed = detached_failed;
    if (failed && size) snprintf(out, size, "%s", detached);
    detached_failed = false;
    return failed;
}

const char *gfx_signature(int minimum, int maximum) {
    static GFX_THREAD_LOCAL char text[160];
    int n = 0;
    for (int i = 0; i < minimum && n < 150; i++) text[n++] = 'v';
    if (maximum > minimum) {
        text[n++] = '|';
        for (int i = minimum; i < maximum && n < 158; i++) text[n++] = 'v';
    }
    text[n] = 0;
    return text;
}

bool gfx_finite_number(aqua_value v, double *out) {
    if (!aqua_is_number(v) || !isfinite(v.number)) return false;
    *out = v.number;
    return true;
}

double gfx_number(aqua_call *call, uint32_t index) {
    double n;
    if (gfx_finite_number(aqua_arg(call, index), &n)) return n;
    gfx_fail(call, "Expected a finite number.");
    return 0;
}

float gfx_float(aqua_call *call, uint32_t index) {
    double n = gfx_number(call, index);
    float f = (float)n;
    if (!isfinite(f)) { gfx_fail(call, "Number exceeds float range."); return 0; }
    return f;
}

double gfx_integral(aqua_call *call, uint32_t index) {
    double n = gfx_number(call, index);
    if (n != trunc(n)) { gfx_fail(call, "Expected an integer."); return 0; }
    return n;
}

int32_t gfx_int(aqua_call *call, uint32_t index) {
    double n = gfx_integral(call, index);
    if (n < -2147483648.0 || n > 2147483647.0) { gfx_fail(call, "Arithmetic operation resulted in an overflow."); return 0; }
    return (int32_t)n;
}

bool gfx_bool(aqua_call *call, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    if (v.type != AQUA_BOOL) { gfx_fail(call, "Expected a Boolean."); return false; }
    return v.number != 0;
}

aqua_str gfx_text(aqua_call *call, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    if (v.type != AQUA_STRING) { gfx_fail(call, "Expected a string."); aqua_str empty = {"", 0}; return empty; }
    return aqua_arg_str(call, index);
}

aqua_value gfx_array(aqua_call *call, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    if (v.type != AQUA_ARRAY) { gfx_fail(call, "Expected an array."); return aqua_null(); }
    return v;
}

float gfx_positive(aqua_call *call, uint32_t index) {
    float n = gfx_float(call, index);
    if (!aqua_failed(call) && n <= 0) gfx_fail(call, "Expected a positive number.");
    return n;
}

float gfx_nonnegative(aqua_call *call, uint32_t index) {
    float n = gfx_float(call, index);
    if (!aqua_failed(call) && n < 0) gfx_fail(call, "Expected a nonnegative number.");
    return n;
}

int32_t gfx_dimension(aqua_call *call, uint32_t index) {
    int32_t d = gfx_int(call, index);
    if (!aqua_failed(call) && (d <= 0 || d > 8192)) { gfx_fail(call, "Dimension must be 1..8192."); return 0; }
    return d;
}

int32_t gfx_choice(aqua_call *call, uint32_t index, int count, const int32_t *options) {
    int32_t n = gfx_int(call, index);
    if (aqua_failed(call)) return 0;
    for (int i = 0; i < count; i++) if (options[i] == n) return n;
    gfx_fail(call, "Invalid mode constant.");
    return 0;
}

uint32_t gfx_packed_value(aqua_call *call, aqua_value v) {
    double n;
    if (!gfx_finite_number(v, &n)) { gfx_fail(call, "Expected a finite number."); return 0; }
    if (n != trunc(n)) { gfx_fail(call, "Expected an integer."); return 0; }
    if (n < 0 || n > 4294967295.0) { gfx_fail(call, "Expected an ARGB color in 0..4294967295."); return 0; }
    return (uint32_t)n;
}

uint32_t gfx_packed(aqua_call *call, uint32_t index) { return gfx_packed_value(call, aqua_arg(call, index)); }

float *gfx_floats(aqua_call *call, aqua_value array, uint32_t *count) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t n = aqua_array_length(rt, array);
    double *values = aqua_scratch(call, (size_t)(n ? n : 1) * sizeof *values);
    float *result = aqua_scratch(call, (size_t)(n ? n : 1) * sizeof *result);
    if (n && !aqua_array_read_f64(rt, array, 0, n, values)) { gfx_fail(call, "Expected a finite number."); return NULL; }
    for (uint32_t i = 0; i < n; i++) {
        if (!isfinite(values[i])) { gfx_fail(call, "Expected a finite number."); return NULL; }
        result[i] = (float)values[i];
        if (!isfinite(result[i])) { gfx_fail(call, "Number exceeds float range."); return NULL; }
    }
    *count = n;
    return result;
}

aqua_value gfx_doubles(aqua_runtime *rt, const double *values, uint32_t count) {
    return aqua_array_from_f64(rt, values, count, AQUA_DOUBLE);
}

/* ---- Calls that may complete later ---- */

struct gfx_async {
    aqua_call *call;       /* the starting call, until gfx_async_end */
    aqua_pending *pending; /* once the script waits */
    aqua_runtime *rt;
    bool finished;
    char prefix[192];
    _Alignas(16) unsigned char data[];
};

gfx_async *gfx_async_begin(aqua_call *call, size_t extra) {
    gfx_async *a = calloc(1, sizeof *a + extra);
    a->call = call;
    a->rt = aqua_call_runtime(call);
    prefix_text(a->prefix, sizeof a->prefix);
    return a;
}

void *gfx_async_data(gfx_async *a) { return a->data; }
aqua_runtime *gfx_async_runtime(gfx_async *a) { return a->rt; }
const char *gfx_async_prefix(gfx_async *a) { return a->prefix; }

void gfx_async_end(gfx_async *a) {
    if (a->finished) { free(a); return; }
    a->pending = aqua_defer(a->call);
    a->call = NULL;
}

aqua_call *gfx_async_call(gfx_async *a) {
    if (!a->call) detached_failed = false;
    return a->call;
}

void gfx_async_settle(gfx_async *a, bool ok, aqua_value result) {
    if (ok) { gfx_async_finish(a, result); return; }
    if (a->call) { if (!aqua_failed(a->call)) aqua_fail(a->call, "%sThe operation failed.", a->prefix); a->finished = true; return; }
    gfx_async_fail(a, "%s", detached_failed ? detached : "The operation failed.");
    detached_failed = false;
}

void gfx_async_finish(gfx_async *a, aqua_value result) {
    if (a->finished) return;
    a->finished = true;
    if (a->call) { aqua_return(a->call, result); return; } /* freed by gfx_async_end */
    aqua_resolve(a->pending, result);
    free(a);
}

void gfx_async_fail_text(gfx_async *a, const char *message) {
    if (a->finished) return;
    a->finished = true;
    if (a->call) { aqua_fail(a->call, "%s", message); return; }
    aqua_reject(a->pending, "%s", message);
    free(a);
}

void gfx_async_fail(gfx_async *a, const char *format, ...) {
    char message[2048];
    snprintf(message, sizeof message, "%s", a->prefix);
    size_t used = strlen(message);
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(message + used, sizeof message - used, format, arguments);
    va_end(arguments);
    gfx_async_fail_text(a, message);
}
