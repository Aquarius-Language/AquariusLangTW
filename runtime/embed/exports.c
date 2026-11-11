/* The per-library half of a native library output (docs/native-libraries.md): the <prefix>* functions, expanded from the
   compiler-written X-macro table exports.def, plus the table and application module the embed runtime (embed.c)
   reads. aqua build compiles it with:
     AQUA_EMBED_PREFIX   the C symbol prefix (geo_)          AQUA_EMBED_HEADER  the library's C header ("geometry.h")
     AQUA_EMBED_MODULE   the application .wasm ("app.wasm"), embedded with #embed
   and the header's <NAME>_STATIC, so its declarations carry no dllimport here.

   exports.def rows (types: INT LONG FLOAT DOUBLE BOOL STRING, <element>_ARRAY, STRUCT(name), VOID for no result):
     AQUA_STRUCT(index, name, "aquarius name", field count)   AQUA_FIELD(struct, index, TYPE, field)   AQUA_STRUCT_END(name, index)
     AQUA_FUNCTION(index, name, "aquarius name", RESULT)  then  AQUA_NO_PARAMETERS or AQUA_PARAMETER(FIRST|NEXT, TYPE, name) ...
     AQUA_CALL(index, RESULT)  AQUA_ARGUMENT(TYPE, name) ...  AQUA_RETURN(RESULT)
   Each pass below gives the rows it needs a meaning and the others none. */
#include <stdlib.h>
#include "aqua/embed.h"
#include AQUA_EMBED_HEADER

#define EMBED_JOIN2(a, b) a##b
#define EMBED_JOIN(a, b) EMBED_JOIN2(a, b)
#define EXPORTED(name) EMBED_JOIN(AQUA_EMBED_PREFIX, name)
#if defined(_WIN32)
#define EMBED_PUBLIC /* the module definition file lists the exports */
#else
#define EMBED_PUBLIC __attribute__((visibility("default")))
#endif

/* ---- Arguments: one aqua_embed_put_* per value ---- */
#define PUT_INT(n) aqua_embed_put_int(n)
#define PUT_LONG(n) aqua_embed_put_long(n)
#define PUT_FLOAT(n) aqua_embed_put_float(n)
#define PUT_DOUBLE(n) aqua_embed_put_double(n)
#define PUT_BOOL(n) aqua_embed_put_bool(n)
#define PUT_STRING(n) aqua_embed_put_string(n)
#define PUT_INT_ARRAY(n) aqua_embed_put_array(n, n##_count, AQUA_EMBED_INT)
#define PUT_LONG_ARRAY(n) aqua_embed_put_array(n, n##_count, AQUA_EMBED_LONG)
#define PUT_FLOAT_ARRAY(n) aqua_embed_put_array(n, n##_count, AQUA_EMBED_FLOAT)
#define PUT_DOUBLE_ARRAY(n) aqua_embed_put_array(n, n##_count, AQUA_EMBED_DOUBLE)
#define PUT_BOOL_ARRAY(n) aqua_embed_put_array(n, n##_count, AQUA_EMBED_BOOL)
#define PUT_STRUCT(s) EMBED_JOIN(put_struct_, s)

/* ---- Results and fields: the getter of each type (field < 0 is the result itself) ---- */
#define TAKE_INT(f) aqua_embed_int(f)
#define TAKE_LONG(f) aqua_embed_long(f)
#define TAKE_FLOAT(f) aqua_embed_float(f)
#define TAKE_DOUBLE(f) aqua_embed_double(f)
#define TAKE_BOOL(f) aqua_embed_bool(f)
#define TAKE_STRING(f) aqua_embed_string(f)

/* Pass 1: a struct argument is its fields followed by the struct. */
#define AQUA_STRUCT(index, s, binding, count) static inline void EMBED_JOIN(put_struct_, s)(EXPORTED(s) value) {
#define AQUA_FIELD(s, index, T, f) PUT_##T(value.f);
#define AQUA_STRUCT_END(s, index) aqua_embed_put_struct(index); }
#define AQUA_FUNCTION(index, name, binding, R)
#define AQUA_NO_PARAMETERS
#define AQUA_PARAMETER(SEPARATOR, T, n)
#define AQUA_CALL(index, R)
#define AQUA_ARGUMENT(T, n)
#define AQUA_RETURN(R)
#include "exports.def"
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END

/* Pass 2: a struct result, field by field. */
#define AQUA_STRUCT(index, s, binding, count) static inline EXPORTED(s) EMBED_JOIN(take_struct_, s)(void) { EXPORTED(s) value;
#define AQUA_FIELD(s, index, T, f) value.f = TAKE_##T(index);
#define AQUA_STRUCT_END(s, index) return value; }
#include "exports.def"
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END
#undef AQUA_FUNCTION
#undef AQUA_NO_PARAMETERS
#undef AQUA_PARAMETER
#undef AQUA_CALL
#undef AQUA_ARGUMENT
#undef AQUA_RETURN

/* Pass 3: the functions. Each one begins a call, puts its arguments, invokes and takes its result. */
#define RESULT_INT int32_t
#define RESULT_LONG int64_t
#define RESULT_FLOAT float
#define RESULT_DOUBLE double
#define RESULT_BOOL bool
#define RESULT_STRING char *
#define RESULT_INT_ARRAY EXPORTED(int_array)
#define RESULT_LONG_ARRAY EXPORTED(long_array)
#define RESULT_FLOAT_ARRAY EXPORTED(float_array)
#define RESULT_DOUBLE_ARRAY EXPORTED(double_array)
#define RESULT_BOOL_ARRAY EXPORTED(bool_array)
#define RESULT_STRUCT(s) EXPORTED(s)
#define RESULT_VOID void

#define SEPARATOR_FIRST
#define SEPARATOR_NEXT ,
#define DECLARE_NAME(n) n
#define DECLARE_INT(n) int32_t n
#define DECLARE_LONG(n) int64_t n
#define DECLARE_FLOAT(n) float n
#define DECLARE_DOUBLE(n) double n
#define DECLARE_BOOL(n) bool n
#define DECLARE_STRING(n) const char *n
#define DECLARE_INT_ARRAY(n) const int32_t *n, size_t n##_count
#define DECLARE_LONG_ARRAY(n) const int64_t *n, size_t n##_count
#define DECLARE_FLOAT_ARRAY(n) const float *n, size_t n##_count
#define DECLARE_DOUBLE_ARRAY(n) const double *n, size_t n##_count
#define DECLARE_BOOL_ARRAY(n) const bool *n, size_t n##_count
#define DECLARE_STRUCT(s) EXPORTED(s) DECLARE_NAME

/* The result variable (zero until the call succeeds), how a successful call fills it, and what the function returns. */
#define DEFINE_VOID
#define DEFINE_INT int32_t result = 0;
#define DEFINE_LONG int64_t result = 0;
#define DEFINE_FLOAT float result = 0;
#define DEFINE_DOUBLE double result = 0;
#define DEFINE_BOOL bool result = false;
#define DEFINE_STRING char *result = NULL;
#define DEFINE_INT_ARRAY EXPORTED(int_array) result = {0};
#define DEFINE_LONG_ARRAY EXPORTED(long_array) result = {0};
#define DEFINE_FLOAT_ARRAY EXPORTED(float_array) result = {0};
#define DEFINE_DOUBLE_ARRAY EXPORTED(double_array) result = {0};
#define DEFINE_BOOL_ARRAY EXPORTED(bool_array) result = {0};
#define DEFINE_STRUCT(s) EXPORTED(s) result = {0};
#define STORE_VOID
#define STORE_INT result = TAKE_INT(-1);
#define STORE_LONG result = TAKE_LONG(-1);
#define STORE_FLOAT result = TAKE_FLOAT(-1);
#define STORE_DOUBLE result = TAKE_DOUBLE(-1);
#define STORE_BOOL result = TAKE_BOOL(-1);
#define STORE_STRING result = TAKE_STRING(-1);
#define STORE_INT_ARRAY result.data = aqua_embed_array(&result.count, AQUA_EMBED_INT);
#define STORE_LONG_ARRAY result.data = aqua_embed_array(&result.count, AQUA_EMBED_LONG);
#define STORE_FLOAT_ARRAY result.data = aqua_embed_array(&result.count, AQUA_EMBED_FLOAT);
#define STORE_DOUBLE_ARRAY result.data = aqua_embed_array(&result.count, AQUA_EMBED_DOUBLE);
#define STORE_BOOL_ARRAY result.data = aqua_embed_array(&result.count, AQUA_EMBED_BOOL);
#define STORE_STRUCT(s) result = EMBED_JOIN(take_struct_, s)();
#define VALUE_VOID
#define VALUE_INT result
#define VALUE_LONG result
#define VALUE_FLOAT result
#define VALUE_DOUBLE result
#define VALUE_BOOL result
#define VALUE_STRING result
#define VALUE_INT_ARRAY result
#define VALUE_LONG_ARRAY result
#define VALUE_FLOAT_ARRAY result
#define VALUE_DOUBLE_ARRAY result
#define VALUE_BOOL_ARRAY result
#define VALUE_STRUCT(s) result

#define AQUA_STRUCT(index, s, binding, count)
#define AQUA_FIELD(s, index, T, f)
#define AQUA_STRUCT_END(s, index)
#define AQUA_FUNCTION(index, name, binding, R) EMBED_PUBLIC RESULT_##R EXPORTED(name)(
#define AQUA_NO_PARAMETERS void
#define AQUA_PARAMETER(SEPARATOR, T, n) SEPARATOR_##SEPARATOR DECLARE_##T(n)
#define AQUA_CALL(index, R) ) { DEFINE_##R if (!aqua_embed_begin(index)) return VALUE_##R;
#define AQUA_ARGUMENT(T, n) PUT_##T(n);
#define AQUA_RETURN(R) if (aqua_embed_invoke()) { STORE_##R } aqua_embed_end(); return VALUE_##R; }
#include "exports.def"
#undef AQUA_FUNCTION
#undef AQUA_NO_PARAMETERS
#undef AQUA_PARAMETER
#undef AQUA_CALL
#undef AQUA_ARGUMENT
#undef AQUA_RETURN
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END

/* Pass 4: the table embed.c reads (the Aquarius names to look up and each struct's field count). An empty list holds
   one unused entry, since C has no empty arrays. */
#define AQUA_STRUCT(index, s, binding, count)
#define AQUA_FIELD(s, index, T, f)
#define AQUA_STRUCT_END(s, index)
#define AQUA_NO_PARAMETERS
#define AQUA_PARAMETER(SEPARATOR, T, n)
#define AQUA_CALL(index, R)
#define AQUA_ARGUMENT(T, n)
#define AQUA_RETURN(R)
#define AQUA_FUNCTION(index, name, binding, R) binding,
static const char *const functions[] = {
#include "exports.def"
    NULL};
#undef AQUA_FUNCTION
#define AQUA_FUNCTION(index, name, binding, R)
#undef AQUA_STRUCT
#define AQUA_STRUCT(index, s, binding, count) binding,
static const char *const structs[] = {
#include "exports.def"
    NULL};
#undef AQUA_STRUCT
#define AQUA_STRUCT(index, s, binding, count) count,
static const uint32_t field_counts[] = {
#include "exports.def"
    0};

/* The default libraries linked into this library (AQUA_EMBED_DEFAULT_LIBRARIES: X(name) entries). */
#ifndef AQUA_EMBED_DEFAULT_LIBRARIES
#define AQUA_EMBED_DEFAULT_LIBRARIES
#endif
#define X(name) void aqua_library_##name(aqua_library *library);
AQUA_EMBED_DEFAULT_LIBRARIES
#undef X
static const aqua_embed_library libraries[] = {
#define X(name) {#name, aqua_library_##name},
    AQUA_EMBED_DEFAULT_LIBRARIES
#undef X
    {NULL, NULL}};

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wc23-extensions"
static const unsigned char module[] = {
#embed AQUA_EMBED_MODULE
};
#pragma clang diagnostic pop

static const aqua_embed_table table = {
    functions, sizeof functions / sizeof *functions - 1, structs, field_counts, sizeof structs / sizeof *structs - 1,
    libraries, sizeof libraries / sizeof *libraries - 1, module, sizeof module
};
const aqua_embed_table *aqua_embed_current_table(void) { return &table; }

/* ---- What every library defines ---- */
EMBED_PUBLIC bool EXPORTED(initialize)(void) { return aqua_embed_initialize(); }
EMBED_PUBLIC void EXPORTED(shutdown)(void) { aqua_embed_shutdown(); }
EMBED_PUBLIC const char *EXPORTED(last_error)(void) { return aqua_embed_last_error(); }
EMBED_PUBLIC void EXPORTED(free)(void *memory) { free(memory); }
EMBED_PUBLIC uint32_t EXPORTED(abi_version)(void) { return AQUA_EMBED_ABI_VERSION; }
