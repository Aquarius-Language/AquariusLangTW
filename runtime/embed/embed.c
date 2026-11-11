/* The embed runtime of native library outputs (docs/native-libraries.md): one application instance per process, run by
   the host core on the ahead-of-time engine (host/engine_aot.c linked with the library's wasm2c translation). The first
   call (or <prefix>initialize) creates the instance and runs the entry module's top-level code once; every exported
   function is then called through aqua_invoke (the guest's invoke export) and runs to completion. One lock serializes
   calls; a thread-local flag refuses a call made from inside a call; the last error is kept per thread. */
#include "../host/host.h"
#include "aqua/embed.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
static SRWLOCK lock = SRWLOCK_INIT;
static void lock_take(void) { AcquireSRWLockExclusive(&lock); }
static void lock_give(void) { ReleaseSRWLockExclusive(&lock); }
#else
#include <pthread.h>
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static void lock_take(void) { pthread_mutex_lock(&lock); }
static void lock_give(void) { pthread_mutex_unlock(&lock); }
#endif

void platform_wait(aqua_runtime *rt, double timeout_ms); /* platform layer */

/* ---- Per thread: the last error and whether a call is running on this thread ---- */

static _Thread_local char error_text[2048];
/* `refused`: the error is a reentrant call's (made from a signal handler, say, while this thread's call runs). The
   running call replaces it with its own outcome, so it reports only its own failures. */
static _Thread_local bool error_set, refused, inside;

static void fail(const char *message) {
    if (error_set && !refused) return; /* the first reason is the one to report */
    snprintf(error_text, sizeof error_text, "%s", message);
    error_set = true; refused = false;
}
const char *aqua_embed_last_error(void) { return error_set ? error_text : NULL; }

/* Refuses a call made while this thread's call runs, with no allocation and no lock (it may run in a signal handler). */
static void refuse(void) {
    static const char message[] = "A library function was called while another one runs on the same thread (reentrant call).";
    memcpy(error_text, message, sizeof message);
    error_set = true; refused = true;
}

/* ---- The instance (guarded by the lock) ---- */

typedef struct instance {
    aqua_runtime *rt;
    aqua_ref scope;          /* the entry module's scope, while its exports are looked up */
    aqua_ref *functions;     /* each exported function's value */
    aqua_ref *constructors;  /* each exported struct's hidden constructor */
    bool entry_failed; char *entry_error;
} instance;
static instance *current;

/* The current call's arguments and result, retained while the call runs. */
static aqua_value *arguments; static aqua_ref *argument_refs; static uint32_t argument_count, argument_capacity;
static aqua_value result; static aqua_ref result_ref;
static uint32_t calling;     /* the function being called */
static bool conversion_failed;

static void entry_done(aqua_runtime *rt, bool ok, aqua_value value, void *data) {
    instance *i = data;
    if (!ok) { i->entry_failed = true; i->entry_error = format_value(rt, value); rt->exiting = true; }
    rt->finished = true;
}

/* Runs the runtime's loop until nothing is left to run now; with `wait`, until the application has finished (the
   entry module, which may wait for timers or deferred calls). */
static void pump(aqua_runtime *rt, bool wait) {
    double next;
    while ((next = runtime_turn(rt, 50)) >= 0) {
        embed_platform_flush();
        if (next > 0) { if (!wait) break; platform_wait(rt, next); }
    }
    embed_platform_flush();
}

/* An exported binding of the entry module's scope, retained; 0 (with the error set) when it is not a function. */
static aqua_ref lookup(aqua_runtime *rt, uint32_t scope, const char *name, const char *what) {
    aqua_value text = aqua_cstring(rt, name);
    uint32_t cell = guest(rt, GUEST_scope_get, scope, text.ref, 0, 0);
    aqua_value value = cell ? guest_read_value(rt, cell) : aqua_null();
    if (value.type != AQUA_FUNCTION) {
        char message[512];
        snprintf(message, sizeof message, "The library's %s %s was not defined when its top-level code finished.", what, name);
        fail(message);
        return 0;
    }
    return aqua_retain(rt, value);
}

static void destroy(instance *i) {
    if (!i) return;
    free(i->functions); free(i->constructors); free(i->entry_error);
    if (i->rt) runtime_destroy(i->rt);
    free(i);
}

/* Creates the instance and runs the entry module (with the lock held). */
static bool start(void) {
    if (current) return true;
    const aqua_embed_table *table = &aqua_embed_exports;
    instance *i = calloc(1, sizeof *i);
    uint8_t *module = malloc(table->module_size);
    memcpy(module, table->module, table->module_size);
    runtime_options options = {0};
    options.application_directory = "";
    options.done = entry_done; options.done_data = i;
    char *error = NULL;
    i->rt = runtime_create(module, table->module_size, &options, &error);
    if (!i->rt) { fail(error ? error : "Cannot create the library's runtime."); free(error); free(module); free(i); return false; }
    if (!runtime_start(i->rt, NULL)) { fail("Cannot start the library's top-level code."); destroy(i); return false; }
    /* Kept until the exports are looked up: the entry execution ends before that, and its scope must survive it. */
    aqua_value scope = {AQUA_MODULE, i->rt->entry_scope, {0}};
    i->scope = aqua_retain(i->rt, scope);
    pump(i->rt, true);
    if (i->entry_failed) { fail(i->entry_error ? i->entry_error : "The library's top-level code failed."); destroy(i); return false; }
    i->functions = calloc(table->function_count + 1, sizeof *i->functions);
    i->constructors = calloc(table->struct_count + 1, sizeof *i->constructors);
    bool ok = true;
    for (uint32_t f = 0; f < table->function_count && ok; f++) ok = (i->functions[f] = lookup(i->rt, i->rt->entry_scope, table->functions[f], "function")) != 0;
    for (uint32_t s = 0; s < table->struct_count && ok; s++) {
        char name[512];
        snprintf(name, sizeof name, "export@%s", table->structs[s]);
        ok = (i->constructors[s] = lookup(i->rt, i->rt->entry_scope, name, "struct")) != 0;
    }
    aqua_release(i->rt, i->scope);
    if (!ok) { destroy(i); return false; }
    current = i;
    return true;
}

bool aqua_embed_initialize(void) {
    if (inside) { refuse(); return false; }
    error_set = false;
    inside = true;
    lock_take();
    bool ok = start();
    lock_give();
    inside = false;
    return ok;
}

void aqua_embed_shutdown(void) {
    if (inside) return; /* from inside a call: the instance is in use */
    inside = true;
    lock_take();
    destroy(current);
    current = NULL;
    lock_give();
    inside = false;
}

/* ---- Calls ---- */

bool aqua_embed_begin(uint32_t function) {
    if (inside) { refuse(); return false; }
    error_set = false;
    /* Marked before the lock is taken and cleared after it is given back, so a call from a signal handler on this
       thread is refused rather than waiting for the lock this thread holds. */
    inside = true;
    lock_take();
    if (!start()) { lock_give(); inside = false; return false; }
    calling = function; argument_count = 0; conversion_failed = false; result = aqua_void(); result_ref = 0;
    return true;
}

static void put(aqua_value value) {
    if (argument_count == argument_capacity) {
        argument_capacity = argument_capacity ? argument_capacity * 2 : 16;
        arguments = realloc(arguments, argument_capacity * sizeof *arguments);
        argument_refs = realloc(argument_refs, argument_capacity * sizeof *argument_refs);
    }
    /* Retained: building a later argument (a struct) runs guest code that may collect. */
    argument_refs[argument_count] = aqua_retain(current->rt, value);
    arguments[argument_count++] = value;
}
static void conversion_error(const char *message) { if (!conversion_failed) fail(message); conversion_failed = true; put(aqua_null()); }

void aqua_embed_put_int(int32_t value) { put(aqua_int(value)); }
void aqua_embed_put_long(int64_t value) { put(aqua_long(value)); }
void aqua_embed_put_float(float value) { put(aqua_float(value)); }
void aqua_embed_put_double(double value) { put(aqua_double(value)); }
void aqua_embed_put_bool(bool value) { put(aqua_bool(value)); }

/* Well-formed UTF-8: no overlong forms, surrogates or code points past U+10FFFF. */
static bool valid_utf8(const unsigned char *s, size_t n) {
    for (size_t i = 0; i < n;) {
        unsigned char c = s[i];
        size_t length = c < 0x80 ? 1 : (c & 0xe0) == 0xc0 ? 2 : (c & 0xf0) == 0xe0 ? 3 : (c & 0xf8) == 0xf0 ? 4 : 0;
        if (!length || i + length > n || (length == 2 && c < 0xc2)) return false;
        uint32_t code = length == 1 ? c : c & (0xff >> (length + 1));
        for (size_t k = 1; k < length; k++) {
            if ((s[i + k] & 0xc0) != 0x80) return false;
            code = code << 6 | (s[i + k] & 0x3f);
        }
        if ((length == 3 && (code < 0x800 || (code >= 0xd800 && code <= 0xdfff))) || (length == 4 && (code < 0x10000 || code > 0x10ffff)))
            return false;
        i += length;
    }
    return true;
}

void aqua_embed_put_string(const char *utf8) {
    if (!utf8) { conversion_error("A string argument is NULL."); return; }
    size_t n = strlen(utf8);
    if (!valid_utf8((const unsigned char *)utf8, n)) { conversion_error("A string argument is not valid UTF-8."); return; }
    put(aqua_text(current->rt, utf8, n));
}

void aqua_embed_put_array(const void *data, size_t count, aqua_embed_element element) {
    if (!data && count) { conversion_error("An array argument is NULL with a nonzero count."); return; }
    if (count > UINT32_MAX / 16) { conversion_error("An array argument is too long."); return; }
    aqua_runtime *rt = current->rt;
    uint32_t n = (uint32_t)count;
    aqua_value array;
    switch (element) {
    case AQUA_EMBED_INT: array = aqua_array_from_i32(rt, data, n); break;
    case AQUA_EMBED_DOUBLE: array = aqua_array_from_f64(rt, data, n, AQUA_DOUBLE); break;
    default:
        array = aqua_array_new(rt, n);
        for (uint32_t k = 0; k < n; k++) {
            aqua_value item = element == AQUA_EMBED_LONG ? aqua_long(((const int64_t *)data)[k])
                : element == AQUA_EMBED_FLOAT ? aqua_float(((const float *)data)[k]) : aqua_bool(((const bool *)data)[k]);
            aqua_array_set(rt, array, k, item);
        }
        break;
    }
    put(array);
}

void aqua_embed_put_struct(uint32_t structure) {
    uint32_t fields = aqua_embed_exports.field_counts[structure];
    uint32_t first = argument_count - fields;
    aqua_value value = aqua_null();
    if (!conversion_failed) {
        aqua_value made;
        if (aqua_invoke(current->rt, aqua_ref_value(current->rt, current->constructors[structure]), arguments + first, fields, &made)) value = made;
        else {
            char *text = format_value(current->rt, made);
            fail(text); free(text);
            conversion_failed = true;
        }
    }
    for (uint32_t k = first; k < argument_count; k++) aqua_release(current->rt, argument_refs[k]);
    argument_count = first;
    put(value);
}

bool aqua_embed_invoke(void) {
    if (conversion_failed) return false;
    aqua_runtime *rt = current->rt;
    aqua_value outcome;
    bool ok = aqua_invoke(rt, aqua_ref_value(rt, current->functions[calling]), arguments, argument_count, &outcome);
    result_ref = aqua_retain(rt, outcome);
    result = outcome;
    if (!ok) { char *text = format_value(rt, outcome); fail(text); free(text); }
    /* Work the call left (posted results, tasks it started) runs now; timers wait for a later call. */
    pump(rt, false);
    return ok;
}

/* ---- Results ---- */

static aqua_value value_of(int32_t field) {
    if (field < 0) return result;
    aqua_value key, value;
    return aqua_hash_entry(current->rt, result, (uint32_t)field, &key, &value) ? value : aqua_null();
}
static double number(aqua_value v) { return v.type == AQUA_LONG ? (double)v.integer : v.number; }
int32_t aqua_embed_int(int32_t field) { aqua_value v = value_of(field); return v.type == AQUA_LONG ? (int32_t)v.integer : (int32_t)v.number; }
int64_t aqua_embed_long(int32_t field) { aqua_value v = value_of(field); return v.type == AQUA_LONG ? v.integer : (int64_t)v.number; }
float aqua_embed_float(int32_t field) { return (float)number(value_of(field)); }
double aqua_embed_double(int32_t field) { return number(value_of(field)); }
bool aqua_embed_bool(int32_t field) { return value_of(field).number != 0; }

char *aqua_embed_string(int32_t field) {
    aqua_value v = value_of(field);
    if (v.type != AQUA_STRING) return NULL;
    return aqua_text_dup(current->rt, v, NULL);
}

void *aqua_embed_array(size_t *count, aqua_embed_element element) {
    static const size_t sizes[] = {sizeof(int32_t), sizeof(int64_t), sizeof(float), sizeof(double), sizeof(bool)};
    aqua_runtime *rt = current->rt;
    *count = 0;
    if (result.type != AQUA_ARRAY) return NULL;
    uint32_t n = aqua_array_length(rt, result);
    if (n == 0) return NULL;
    void *data = malloc((size_t)n * sizes[element]);
    switch (element) {
    case AQUA_EMBED_INT: aqua_array_read_i32(rt, result, 0, n, data); break;
    case AQUA_EMBED_FLOAT: aqua_array_read_f32(rt, result, 0, n, data); break;
    case AQUA_EMBED_DOUBLE: aqua_array_read_f64(rt, result, 0, n, data); break;
    case AQUA_EMBED_LONG:
        for (uint32_t k = 0; k < n; k++) { aqua_value v = aqua_array_get(rt, result, k); ((int64_t *)data)[k] = v.type == AQUA_LONG ? v.integer : (int64_t)v.number; }
        break;
    case AQUA_EMBED_BOOL:
        for (uint32_t k = 0; k < n; k++) ((bool *)data)[k] = aqua_array_get(rt, result, k).number != 0;
        break;
    }
    *count = n;
    return data;
}

void aqua_embed_end(void) {
    aqua_runtime *rt = current->rt;
    for (uint32_t k = 0; k < argument_count; k++) aqua_release(rt, argument_refs[k]);
    aqua_release(rt, result_ref);
    argument_count = 0; result_ref = 0; result = aqua_void();
    embed_platform_flush();
    if (refused) error_set = refused = false; /* a reentrant call's refusal is not this call's outcome */
    lock_give();
    inside = false;
}
