/* aqua.h — the AquariusLang native library API, version 1.
 *
 * Every default library (libraries/<name>) and every external library is written against this header in C, C++ or
 * Rust (through a C binding) and built with the pinned Clang toolchain for each target:
 *   - Windows, Linux and Android: native code. Default libraries link into the runtime; external libraries are shared
 *     libraries the runtime loads, receiving this API through aqua_api_stub.c (linked by aqua_add_library).
 *   - Web: the same source compiled to a WebAssembly side module sharing the application's memory. Web APIs are
 *     reached only through the imports declared in <aqua/web.h>, implemented by the unified glue in web-glue/.
 * Library code never sees the platform's Wasm engine. It reads arguments and builds results through these calls,
 * which work identically on every target.
 *
 * 星泉原生函式庫介面第一版：所有預設與外部函式庫都以此標頭撰寫，同一份原始碼編譯到各平台。
 */
#ifndef AQUA_H
#define AQUA_H

#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define AQUA_API_VERSION 2

/* ---- Target detection: one macro per operating system family, for library preprocessor branches. ------------------ */
#if defined(__wasm__)
#define AQUA_OS_WEB 1
#define AQUA_PLATFORM_NAME "web"
#elif defined(__ANDROID__)
#define AQUA_OS_ANDROID 1
#define AQUA_PLATFORM_NAME "android"
#elif defined(_WIN32)
#define AQUA_OS_WINDOWS 1
#define AQUA_PLATFORM_NAME "windows"
#elif defined(__APPLE__)
#include <TargetConditionals.h>
#if TARGET_OS_IPHONE
#define AQUA_OS_IOS 1
#define AQUA_PLATFORM_NAME "ios"
#else
#define AQUA_OS_MACOS 1
#define AQUA_PLATFORM_NAME "macos"
#endif
#elif defined(__linux__)
#define AQUA_OS_LINUX 1
#define AQUA_PLATFORM_NAME "linux"
#else
#error "Unsupported AquariusLang target: add it to aqua.h, runtime/platform and runtime/sdk/cmake/toolchains."
#endif
#if defined(AQUA_OS_WINDOWS) || defined(AQUA_OS_LINUX) || defined(AQUA_OS_MACOS)
#define AQUA_DESKTOP 1
#endif
#if defined(AQUA_OS_ANDROID) || defined(AQUA_OS_IOS)
#define AQUA_MOBILE 1
#endif

#if defined(AQUA_OS_WINDOWS)
#define AQUA_EXPORT __declspec(dllexport)
#else
#define AQUA_EXPORT __attribute__((visibility("default")))
#endif

/* Web only: declares a function of the unified web glue (web-glue/<domain>), e.g.
       AQUA_WEB_IMPORT(net_fetch) int32_t web_fetch(const char *url, ...);
   C APIs the glue implements under their standard names (webgpu.h, OpenGL ES) need no declaration. A library exports
   the C functions the glue calls back with AQUA_WEB_EXPORT(name). */
#define AQUA_WEB_IMPORT(name) __attribute__((import_module("aqua_web"), import_name(#name)))
#define AQUA_WEB_EXPORT(name) __attribute__((export_name(#name)))

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Values -------------------------------------------------------------------------------------------------------- */

/* The language's value types. The numbering is the guest runtime's (runtime/guest/layout.def). */
typedef enum aqua_type {
    AQUA_VOID = 0, AQUA_INT = 1, AQUA_FLOAT = 2, AQUA_DOUBLE = 3, AQUA_BOOL = 4, AQUA_STRING = 5, AQUA_ARRAY = 6,
    AQUA_HASH = 7, AQUA_FUNCTION = 8, AQUA_MODULE = 9, AQUA_NATIVE = 10, AQUA_NULL = 11, AQUA_BREAK = 12,
    AQUA_ERROR = 13, AQUA_BUILTIN = 14,
    /* A struct value (結構 / struct) and a struct type (callable: it constructs values). */
    AQUA_STRUCT = 15, AQUA_STRUCT_TYPE = 16
} aqua_type;

/* A language value, copied by value. Numbers and booleans live in `number`; strings, arrays, hashes, functions and
   modules refer to guest memory through `ref`; native objects refer to a runtime handle. A value that refers to guest
   memory stays valid while the guest can reach it: arguments for the duration of the call, anything else only while
   retained (aqua_retain). */
typedef struct aqua_value { uint32_t type; uint32_t ref; double number; } aqua_value;

typedef struct aqua_runtime aqua_runtime; /* one running application */
typedef struct aqua_call aqua_call;       /* one call of a library function, valid until it returns */
typedef struct aqua_library aqua_library; /* registration context passed to a library's entry point */
typedef struct aqua_class aqua_class;     /* a module or a class of native objects */

/* UTF-8 text with its byte length; `data` is NUL-terminated. */
typedef struct aqua_str { const char *data; size_t length; } aqua_str;

static inline aqua_value aqua_void(void) { aqua_value v = {AQUA_VOID, 0, 0}; return v; }
static inline aqua_value aqua_null(void) { aqua_value v = {AQUA_NULL, 0, 0}; return v; }
static inline aqua_value aqua_int(int32_t n) { aqua_value v = {AQUA_INT, 0, (double)n}; return v; }
static inline aqua_value aqua_float(float n) { aqua_value v = {AQUA_FLOAT, 0, (double)n}; return v; }
static inline aqua_value aqua_double(double n) { aqua_value v = {AQUA_DOUBLE, 0, n}; return v; }
static inline aqua_value aqua_bool(bool b) { aqua_value v = {AQUA_BOOL, 0, b ? 1.0 : 0.0}; return v; }
static inline bool aqua_is_number(aqua_value v) { return v.type == AQUA_INT || v.type == AQUA_FLOAT || v.type == AQUA_DOUBLE; }
static inline bool aqua_is_nullish(aqua_value v) { return v.type == AQUA_NULL || v.type == AQUA_VOID; }
/* The language's type name: integer, float, double, string, boolean, null, array, hash, function, module, error. */
const char *aqua_type_name(aqua_value v);

/* New guest text from UTF-8 (length in bytes; invalid sequences become U+FFFD) or UTF-16 code units. */
aqua_value aqua_text(aqua_runtime *rt, const char *utf8, size_t length);
aqua_value aqua_cstring(aqua_runtime *rt, const char *utf8);
aqua_value aqua_text16(aqua_runtime *rt, const uint16_t *units, size_t count);
/* Text of a string value as UTF-8 in memory the caller frees with aqua_free, or NULL for a non-string. */
char *aqua_text_dup(aqua_runtime *rt, aqua_value text, size_t *length);
/* UTF-16 length and a borrowed view of a string's code units, valid until the next allocation in the runtime. */
size_t aqua_text16_length(aqua_runtime *rt, aqua_value text);
const uint16_t *aqua_text16_units(aqua_runtime *rt, aqua_value text);
/* Display text of any value as print/印出 shows it, in memory the caller frees with aqua_free. */
char *aqua_display(aqua_runtime *rt, aqua_value v);
bool aqua_equal(aqua_runtime *rt, aqua_value a, aqua_value b);

/* Arrays. Reads see the current elements; writes go straight into the guest array (copy-on-write sharing is resolved
   by the runtime). Indices outside the array return null (get) or false (set). A typed number or Boolean array (int[],
   float[], double[], bool[] in the language) takes only its element type, integers widening into float and double
   arrays: set, push and the write functions return false (writing nothing) for anything else. */
aqua_value aqua_array_new(aqua_runtime *rt, uint32_t length);
uint32_t aqua_array_length(aqua_runtime *rt, aqua_value array);
aqua_value aqua_array_get(aqua_runtime *rt, aqua_value array, uint32_t index);
bool aqua_array_set(aqua_runtime *rt, aqua_value array, uint32_t index, aqua_value item);
bool aqua_array_resize(aqua_runtime *rt, aqua_value array, uint32_t length);
bool aqua_array_push(aqua_runtime *rt, aqua_value array, aqua_value item);
/* Bulk numeric transfer between a guest array and a C buffer, converting each element. Reads fail (false) when an
   element in the range is not a number; writes store `type` (AQUA_INT, AQUA_FLOAT or AQUA_DOUBLE) values. */
bool aqua_array_read_f64(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, double *out);
bool aqua_array_read_f32(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, float *out);
bool aqua_array_read_i32(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, int32_t *out);
/* Bytes: every element must be an integer 0..255. */
bool aqua_array_read_u8(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, uint8_t *out);
bool aqua_array_write_f64(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, const double *in, aqua_type type);
bool aqua_array_write_f32(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, const float *in, aqua_type type);
bool aqua_array_write_i32(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, const int32_t *in);
bool aqua_array_write_u8(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, const uint8_t *in);
aqua_value aqua_array_from_f64(aqua_runtime *rt, const double *items, uint32_t count, aqua_type type);
aqua_value aqua_array_from_i32(aqua_runtime *rt, const int32_t *items, uint32_t count);
aqua_value aqua_array_from_u8(aqua_runtime *rt, const uint8_t *items, uint32_t count);

/* Hashes keep insertion order; keys are integers, floats, doubles, strings or booleans compared as full typed keys. */
aqua_value aqua_hash_new(aqua_runtime *rt, uint32_t capacity);
/* A struct (AQUA_STRUCT) reads like a string-keyed hash: its fields in declaration order, each name with its value. */
uint32_t aqua_hash_count(aqua_runtime *rt, aqua_value hash);
bool aqua_hash_entry(aqua_runtime *rt, aqua_value hash, uint32_t index, aqua_value *key, aqua_value *value);
bool aqua_hash_get(aqua_runtime *rt, aqua_value hash, aqua_value key, aqua_value *value);
bool aqua_hash_set(aqua_runtime *rt, aqua_value hash, aqua_value key, aqua_value value);
/* String-keyed convenience forms. aqua_hash_field also accepts a "name|別名" alias list and returns the first found. */
bool aqua_hash_field(aqua_runtime *rt, aqua_value hash, const char *names, aqua_value *value);
bool aqua_hash_set_field(aqua_runtime *rt, aqua_value hash, const char *name, aqua_value value);

/* ---- Library registration -------------------------------------------------------------------------------------------
 * A library's entry point registers modules and classes; nothing runs yet. A module is imported by any of its names
 * (匯入("Processing") or 匯入("繪圖")) and becomes a native object whose members are its functions, properties and
 * constants. Names are one string of '|'-separated aliases: English first, then Traditional Chinese, then any others,
 * all sharing one identity. Signatures describe the arguments the runtime checks before calling:
 *     n number   i integer (int, or an integral float/double in int32 range)   b boolean   s string   a array
 *     h hash     f function (script or native)   o native object   v any value (including null)
 *     '|' starts the optional arguments; '*' after a kind repeats it (zero or more).  "nn|n", "s|h", "f*", ""        */

typedef void (*aqua_function)(aqua_call *call);

aqua_class *aqua_define_module(aqua_library *library, const char *names);
/* A class of native objects. `name` is the type shown by typeOf/型別 and print; finalize runs once the guest no longer
   reaches an object and the library holds no reference (aqua_retain), on the runtime's thread. */
aqua_class *aqua_define_class(aqua_library *library, const char *name, void (*finalize)(aqua_runtime *rt, void *data));
void aqua_define_function(aqua_class *owner, const char *names, aqua_function function, const char *signature);
/* Like aqua_define_function, for a function that may complete later (aqua_defer): the script gets a promise (Promise /
   承諾) at once instead of waiting, settled by the function's result, failure or deferred result. Name such variants
   ...Async / ...非同步 (GetAsync|非同步取得), next to the waiting function with the same implementation. */
void aqua_define_async(aqua_class *owner, const char *names, aqua_function function, const char *signature);
/* A property is read through its getter on every access (live values such as mouseX). */
void aqua_define_property(aqua_class *owner, const char *names, aqua_function getter);
void aqua_define_int(aqua_class *owner, const char *names, int32_t value);
void aqua_define_double(aqua_class *owner, const char *names, double value);
void aqua_define_bool(aqua_class *owner, const char *names, bool value);
void aqua_define_string(aqua_class *owner, const char *names, const char *utf8);
/* Per-runtime state of a library: created on first use in a runtime, destroyed when the runtime ends, in reverse
   creation order. Functions reach it with aqua_state(call). */
void aqua_library_state(aqua_library *library, void *(*create)(aqua_runtime *rt), void (*destroy)(aqua_runtime *rt, void *state));

/* Defines a library's entry point. Default libraries are linked into the runtime under their name; external libraries
   build as shared modules whose single export is aqua_library_entry.
       AQUA_LIBRARY(sqlite) { aqua_class *m = aqua_define_module(library, "SQLite|資料庫"); ... }               */
#if defined(AQUA_LIBRARY_EXTERNAL)
bool aqua_api_bind(aqua_library *library); /* aqua_api_stub.c (native) or always true (web) */
#if defined(__cplusplus)
#define AQUA_LIBRARY_ENTRY extern "C" AQUA_EXPORT
#else
#define AQUA_LIBRARY_ENTRY AQUA_EXPORT
#endif
#define AQUA_LIBRARY(name) \
    static void aqua_library_##name(aqua_library *library); \
    AQUA_LIBRARY_ENTRY uint32_t aqua_library_entry(aqua_library *library, uint32_t api_version); \
    uint32_t aqua_library_entry(aqua_library *library, uint32_t api_version) { \
        if (api_version < AQUA_API_VERSION || !aqua_api_bind(library)) return 0; \
        aqua_library_##name(library); return AQUA_API_VERSION; } \
    static void aqua_library_##name(aqua_library *library)
#else
#define AQUA_LIBRARY(name) void aqua_library_##name(aqua_library *library)
#endif

/* ---- Calls ---------------------------------------------------------------------------------------------------------- */

aqua_runtime *aqua_call_runtime(aqua_call *call);
uint32_t aqua_argc(aqua_call *call);
aqua_value aqua_arg(aqua_call *call, uint32_t index);
/* Typed reads of arguments the signature already checked. */
double aqua_arg_number(aqua_call *call, uint32_t index);
int32_t aqua_arg_int(aqua_call *call, uint32_t index);
bool aqua_arg_bool(aqua_call *call, uint32_t index);
/* UTF-8 copy of a string argument, valid until the call returns. */
aqua_str aqua_arg_str(aqua_call *call, uint32_t index);
/* The data of a native-object argument of `cls`, or NULL after failing the call with a type error. */
void *aqua_arg_object(aqua_call *call, uint32_t index, aqua_class *cls);
/* Whether optional argument `index` was passed (and is not null). */
bool aqua_has_arg(aqua_call *call, uint32_t index);
/* The object a method or property was read from, or the module's state for module functions. */
void *aqua_self(aqua_call *call);
aqua_value aqua_self_value(aqua_call *call);
/* This library's per-runtime state (aqua_library_state). */
void *aqua_state(aqua_call *call);
/* Scratch memory released when the call returns. */
void *aqua_scratch(aqua_call *call, size_t bytes);
/* UTF-8 copy of any string value into call scratch memory. */
aqua_str aqua_str_of(aqua_call *call, aqua_value text);

void aqua_return(aqua_call *call, aqua_value result);
void aqua_return_int(aqua_call *call, int32_t n);
void aqua_return_double(aqua_call *call, double n);
void aqua_return_bool(aqua_call *call, bool b);
void aqua_return_null(aqua_call *call);
void aqua_return_text(aqua_call *call, const char *utf8, size_t length);
void aqua_return_cstring(aqua_call *call, const char *utf8);
/* Fails the call with a language error (printf format, UTF-8). The function should return right after. */
void aqua_fail(aqua_call *call, const char *format, ...);
void aqua_vfail(aqua_call *call, const char *format, va_list arguments);
bool aqua_failed(aqua_call *call);

/* ---- Native objects --------------------------------------------------------------------------------------------- */

/* A new native object of `cls` wrapping `data` (owned by the class's finalize). */
aqua_value aqua_object_new(aqua_runtime *rt, aqua_class *cls, void *data);
/* The data of an object of `cls`, or NULL when `value` is anything else. */
void *aqua_object_data(aqua_runtime *rt, aqua_value value, aqua_class *cls);
aqua_class *aqua_object_class(aqua_runtime *rt, aqua_value value);
/* A member of a native object or module by any of its names: a method as a function value (to pass as a callback), a
   property's current value, or null when there is no such member. */
aqua_value aqua_object_member(aqua_runtime *rt, aqua_value object, const char *name);

/* ---- References, callbacks and asynchronous results ------------------------------------------------------------- */

/* A reference keeps a value (and everything it reaches) alive beyond the current call. 0 is no reference. */
typedef uint32_t aqua_ref;
aqua_ref aqua_retain(aqua_runtime *rt, aqua_value value);
aqua_value aqua_ref_value(aqua_runtime *rt, aqua_ref ref);
void aqua_release(aqua_runtime *rt, aqua_ref ref);
bool aqua_is_callable(aqua_value v);
/* The parameter count of a script function, or -1 for a native function. */
int32_t aqua_function_arity(aqua_runtime *rt, aqua_value function);

/* Calls a script or native function and runs it to completion. Returns false with the error in *result (when given)
   if it fails, or if it waits for an asynchronous result, which a synchronous call cannot do on the web. */
bool aqua_invoke(aqua_runtime *rt, aqua_value function, const aqua_value *arguments, uint32_t count, aqua_value *result);
/* Calls a function that may wait for asynchronous results (frame callbacks, event handlers). `done` runs on the
   runtime's thread once it finishes, possibly before aqua_invoke_async returns; `ok` is false on failure. A function
   that returns a promise (an async function) finishes when the promise settles: `done` gets its value or rejection. */
typedef void (*aqua_done)(aqua_runtime *rt, bool ok, aqua_value result, void *data);
void aqua_invoke_async(aqua_runtime *rt, aqua_value function, const aqua_value *arguments, uint32_t count, aqua_done done, void *data);

/* A library function that completes later: the calling script waits (other callbacks keep running) until the result
   is delivered on the runtime's thread. */
typedef struct aqua_pending aqua_pending;
aqua_pending *aqua_defer(aqua_call *call);
void aqua_resolve(aqua_pending *pending, aqua_value result);
void aqua_reject(aqua_pending *pending, const char *format, ...);
void aqua_vreject(aqua_pending *pending, const char *format, va_list arguments);
aqua_runtime *aqua_pending_runtime(aqua_pending *pending);

/* Runs `task` on the runtime's thread at its next turn. The only API that may be called from other threads. */
void aqua_post(aqua_runtime *rt, void (*task)(aqua_runtime *rt, void *data), void *data);
/* Calls `tick` on the runtime's thread after `delay_ms`, then again after each delay it returns, until it returns a
   negative delay. Frame drivers (Processing, WGPU windows) return the time to their next frame; on the web ticks align
   with animation frames. While any driver is scheduled the application keeps running after its script finishes. */
typedef double (*aqua_tick)(aqua_runtime *rt, double now_ms, void *data);
void aqua_schedule(aqua_runtime *rt, double delay_ms, aqua_tick tick, void *data);

/* ---- Runtime services ------------------------------------------------------------------------------------------- */

/* Monotonic milliseconds since the runtime started. */
double aqua_now_ms(aqua_runtime *rt);
/* "windows", "linux", "android", "web", ... (AQUA_PLATFORM_NAME of the runtime). */
const char *aqua_platform(aqua_runtime *rt);
/* Writes UTF-8 text to the application's output (stdout, the web page's output, Android's log). */
void aqua_write(aqua_runtime *rt, const char *utf8, size_t length);
void aqua_write_error(aqua_runtime *rt, const char *utf8, size_t length);
/* A packaged asset by its package path (case-insensitive, '/' separated). The bytes stay owned by the runtime. */
const uint8_t *aqua_asset(aqua_runtime *rt, const char *path, size_t *length);
/* A real file system path holding the asset, for native APIs that only open files; NULL on the web. */
const char *aqua_asset_file(aqua_runtime *rt, const char *path);
/* The directory scripts treat as the application root: the folder of the .wasm or executable on desktop. */
const char *aqua_application_directory(aqua_runtime *rt);
/* Package-relative path of the script making the call, e.g. "src/main.aqua". */
const char *aqua_call_script(aqua_call *call);
/* Launch arguments after the application (desktop `app.wasm a b`, documents opened with the app). */
uint32_t aqua_launch_count(aqua_runtime *rt);
const char *aqua_launch_argument(aqua_runtime *rt, uint32_t index);
/* Environment variable (desktop and Android), or NULL. */
const char *aqua_environment(aqua_runtime *rt, const char *name);

void *aqua_alloc(size_t bytes);
void *aqua_realloc(void *pointer, size_t bytes);
void aqua_free(void *pointer);


#ifdef __cplusplus
}
#endif
#endif
