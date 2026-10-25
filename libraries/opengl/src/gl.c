/* The GLAD and GL modules. gl_functions.def expands into the loader (GLAD.Load), one wrapper per OpenGL 3.3 core
 * function and their registration; gl_constants.def into the constants. Wrappers convert arguments with the rules of the
 * language's earlier OpenGL host: numbers become the parameter's C type (integral and in range for integer types),
 * pointer parameters take typed data (GL_DATA), driver pointers (GL_POINTER), 0, arrays converted to the pointed-to type
 * (and copied back after the call when the driver writes through them) and strings for GLchar text. */
#include "opengl.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The few constants the helpers use (gl_constants.def has them all for scripts). */
#define GL_UNSIGNED_BYTE_ 0x1401
#define GL_RGB_ 0x1907
#define GL_VERSION_ 0x1F02
#define GL_PACK_ALIGNMENT_ 0x0D05
#define GL_FRAGMENT_SHADER_ 0x8B30
#define GL_VERTEX_SHADER_ 0x8B31
#define GL_COMPILE_STATUS_ 0x8B81
#define GL_LINK_STATUS_ 0x8B82
#define GL_INFO_LOG_LENGTH_ 0x8B84

/* ---- Parameter types, classified at compile time from the C signature ------------------------------------------ */

enum {
    GL_T_VOID = 0, GL_T_I8, GL_T_U8, GL_T_CHAR, GL_T_I16, GL_T_U16, GL_T_I32, GL_T_U32, GL_T_I64, GL_T_U64, GL_T_F32,
    GL_T_F64,
    GL_T_ADDRESS,         /* element of void ** and const void *const *: a pointer */
    GL_T_TEXT,            /* element of const GLchar *const *: a string */
    GL_T_SYNC,            /* GLsync, a driver handle passed as a pointer */
    GL_T_ELEMENT = 0x0f,  /* mask of the scalar or element type */
    GL_T_POINTER = 0x10,  /* a pointer to the element type */
    GL_T_OUTPUT = 0x20,   /* ... that is not const: the driver writes through it */
};
#define GL_IN(element) (GL_T_POINTER | (element))
#define GL_OUT(element) (GL_T_POINTER | GL_T_OUTPUT | (element))
/* Every parameter and return type the table uses; a type missing here is a compile error. GLboolean is GLubyte,
   GLenum and GLbitfield are GLuint, GLsizei is GLint and GLintptr and GLsizeiptr are GLint64 on 64-bit targets. */
#define GL_TYPE(T) _Generic((T){0}, \
    GLbyte: GL_T_I8, GLubyte: GL_T_U8, GLchar: GL_T_CHAR, GLshort: GL_T_I16, GLushort: GL_T_U16, GLint: GL_T_I32, \
    GLuint: GL_T_U32, GLint64: GL_T_I64, GLuint64: GL_T_U64, GLfloat: GL_T_F32, GLdouble: GL_T_F64, GLsync: GL_T_SYNC, \
    const void *: GL_IN(GL_T_VOID), void *: GL_OUT(GL_T_VOID), \
    const GLbyte *: GL_IN(GL_T_I8), const GLubyte *: GL_IN(GL_T_U8), GLubyte *: GL_OUT(GL_T_U8), \
    const GLchar *: GL_IN(GL_T_CHAR), GLchar *: GL_OUT(GL_T_CHAR), \
    const GLshort *: GL_IN(GL_T_I16), const GLushort *: GL_IN(GL_T_U16), \
    const GLint *: GL_IN(GL_T_I32), GLint *: GL_OUT(GL_T_I32), const GLuint *: GL_IN(GL_T_U32), GLuint *: GL_OUT(GL_T_U32), \
    GLint64 *: GL_OUT(GL_T_I64), GLuint64 *: GL_OUT(GL_T_U64), \
    const GLfloat *: GL_IN(GL_T_F32), GLfloat *: GL_OUT(GL_T_F32), const GLdouble *: GL_IN(GL_T_F64), GLdouble *: GL_OUT(GL_T_F64), \
    const void *const *: GL_IN(GL_T_ADDRESS), void **: GL_OUT(GL_T_ADDRESS), const GLchar *const *: GL_IN(GL_T_TEXT))

static uint32_t element_size(uint8_t element) {
    switch (element) {
    case GL_T_I16: case GL_T_U16: return 2;
    case GL_T_I32: case GL_T_U32: case GL_T_F32: return 4;
    case GL_T_I64: case GL_T_U64: case GL_T_F64: return 8;
    case GL_T_ADDRESS: case GL_T_TEXT: return (uint32_t)sizeof(void *);
    default: return 1; /* bytes, characters and void data */
    }
}

/* ---- The parameter list of a table entry as macro arguments ------------------------------------------------------
   GL_LIST(m, (A, B, C)) is m(A, 0), m(B, 1), m(C, 2); GL_ARITY((A, B, C)) is 3. Up to 11 parameters (the most any
   OpenGL 3.3 function has). */
#define GL_UNPAREN(...) __VA_ARGS__
#define GL_JOIN(a, b) GL_JOIN_(a, b)
#define GL_JOIN_(a, b) a##b
#define GL_COUNT(...) GL_COUNT_(_, ##__VA_ARGS__, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0)
#define GL_COUNT_(_, a, b, c, d, e, f, g, h, i, j, k, n, ...) n
#define GL_ARITY(params) GL_ARITY_(GL_UNPAREN params)
#define GL_ARITY_(...) GL_COUNT(__VA_ARGS__)
#define GL_LIST(m, params) GL_LIST_(m, GL_UNPAREN params)
#define GL_LIST_(m, ...) GL_JOIN(GL_LIST_, GL_COUNT(__VA_ARGS__))(m, __VA_ARGS__)
#define GL_LIST_0(m, ...)
#define GL_LIST_1(m, a) m(a, 0)
#define GL_LIST_2(m, a, b) GL_LIST_1(m, a), m(b, 1)
#define GL_LIST_3(m, a, b, c) GL_LIST_2(m, a, b), m(c, 2)
#define GL_LIST_4(m, a, b, c, d) GL_LIST_3(m, a, b, c), m(d, 3)
#define GL_LIST_5(m, a, b, c, d, e) GL_LIST_4(m, a, b, c, d), m(e, 4)
#define GL_LIST_6(m, a, b, c, d, e, f) GL_LIST_5(m, a, b, c, d, e), m(f, 5)
#define GL_LIST_7(m, a, b, c, d, e, f, g) GL_LIST_6(m, a, b, c, d, e, f), m(g, 6)
#define GL_LIST_8(m, a, b, c, d, e, f, g, h) GL_LIST_7(m, a, b, c, d, e, f, g), m(h, 7)
#define GL_LIST_9(m, a, b, c, d, e, f, g, h, i) GL_LIST_8(m, a, b, c, d, e, f, g, h), m(i, 8)
#define GL_LIST_10(m, a, b, c, d, e, f, g, h, i, j) GL_LIST_9(m, a, b, c, d, e, f, g, h, i), m(j, 9)
#define GL_LIST_11(m, a, b, c, d, e, f, g, h, i, j, k) GL_LIST_10(m, a, b, c, d, e, f, g, h, i, j), m(k, 10)
#define GL_MAX_ARGUMENTS 11

/* ---- Argument conversion -------------------------------------------------------------------------------------- */

/* One converted argument: integers and pointers in `integer`, GLfloat and GLdouble in `real`. */
typedef struct gl_slot { int64_t integer; double real; } gl_slot;
/* The argument of parameter type T from slot i, for the call. */
#define GL_ARG(T, i) _Generic((T){0}, GLfloat: (GLfloat)slot[i].real, GLdouble: slot[i].real, default: (T)slot[i].integer)

/* An array argument the driver writes into: copied back into the script's array after the call. */
typedef struct gl_output { aqua_value array; const void *buffer; uint32_t count; uint8_t element; } gl_output;
typedef struct gl_frame { gl_output outputs[GL_MAX_ARGUMENTS]; uint32_t output_count; } gl_frame;

static bool is_integer_type(uint8_t t) { return t >= GL_T_I8 && t <= GL_T_U64; }

/* A number for scalar type `type` (or element type of an array), with the earlier host's messages. */
static bool convert_scalar(aqua_call *call, const char *name, aqua_value v, uint8_t type, gl_slot *out) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (type == GL_T_U8 && v.type == AQUA_BOOL) { out->integer = v.number != 0; return true; }
    if (type == GL_T_I64 || type == GL_T_U64) {
        gl_integer64 *exact = aqua_object_data(rt, v, gl_integer64_class);
        if (exact) {
            bool negative = exact->is_signed && (int64_t)exact->bits < 0;
            bool too_big = type == GL_T_I64 && !exact->is_signed && exact->bits > (uint64_t)INT64_MAX;
            if ((type == GL_T_U64 && negative) || too_big) {
                opengl_fail(call, name, "%s is out of range for its OpenGL type.", exact->is_signed ? "A negative GL_INTEGER64" : "A GL_INTEGER64 above 2^63");
                return false;
            }
            out->integer = (int64_t)exact->bits;
            return true;
        }
    }
    if (!aqua_is_number(v) || !isfinite(v.number)) { opengl_fail(call, name, "Expected a finite number."); return false; }
    double n = v.number;
    if (type == GL_T_F32 || type == GL_T_F64) { out->real = n; return true; }
    if (n != trunc(n)) { opengl_fail(call, name, "Expected an integral OpenGL argument."); return false; }
    double low, high;
    switch (type) {
    case GL_T_I8: low = -128; high = 127; break;
    case GL_T_U8: case GL_T_CHAR: low = 0; high = 255; break;
    case GL_T_I16: low = -32768; high = 32767; break;
    case GL_T_U16: low = 0; high = 65535; break;
    case GL_T_I32: low = -2147483648.0; high = 2147483647.0; break;
    case GL_T_U32: low = 0; high = 4294967295.0; break;
    case GL_T_I64: low = -9223372036854775808.0; high = 9223372036854774784.0; break;
    default: low = 0; high = 18446744073709549568.0; break; /* GL_T_U64 */
    }
    if (n < low || n > high) {
        opengl_fail(call, name, "%.17g is out of range for its OpenGL type.", n);
        return false;
    }
    out->integer = type == GL_T_U64 ? (int64_t)(uint64_t)n : (int64_t)n;
    return true;
}

static void store_element(void *buffer, uint32_t i, uint8_t element, const gl_slot *s) {
    switch (element) {
    case GL_T_I8: ((int8_t *)buffer)[i] = (int8_t)s->integer; break;
    case GL_T_U8: case GL_T_CHAR: ((uint8_t *)buffer)[i] = (uint8_t)s->integer; break;
    case GL_T_I16: ((int16_t *)buffer)[i] = (int16_t)s->integer; break;
    case GL_T_U16: ((uint16_t *)buffer)[i] = (uint16_t)s->integer; break;
    case GL_T_I32: ((int32_t *)buffer)[i] = (int32_t)s->integer; break;
    case GL_T_U32: ((uint32_t *)buffer)[i] = (uint32_t)s->integer; break;
    case GL_T_I64: case GL_T_U64: ((int64_t *)buffer)[i] = s->integer; break;
    case GL_T_F32: ((float *)buffer)[i] = (float)s->real; break;
    case GL_T_F64: ((double *)buffer)[i] = s->real; break;
    default: break;
    }
}

/* Converts the `count` elements of a script array into `buffer` of `element` values. Arrays of plain numbers take one
   bulk read; anything else (booleans, GL_INTEGER64, wrong values) goes element by element for exact messages. */
static bool convert_elements(aqua_call *call, const char *name, aqua_value array, uint32_t count, uint8_t element, void *buffer) {
    aqua_runtime *rt = aqua_call_runtime(call);
    double *numbers = count ? aqua_scratch(call, (size_t)count * sizeof *numbers) : NULL;
    if (count && aqua_array_read_f64(rt, array, 0, count, numbers)) {
        for (uint32_t i = 0; i < count; i++) {
            aqua_value v = aqua_double(numbers[i]);
            gl_slot s;
            if (!convert_scalar(call, name, v, element, &s)) return false;
            store_element(buffer, i, element, &s);
        }
        return true;
    }
    for (uint32_t i = 0; i < count; i++) {
        gl_slot s;
        if (!convert_scalar(call, name, aqua_array_get(rt, array, i), element, &s)) return false;
        store_element(buffer, i, element, &s);
    }
    return true;
}

/* A pointer argument. `offset` allows a nonnegative byte offset into the bound buffer. `capacity` is the number of
   elements the driver may use (0 when unknown); buffers made here hold at least that many, and at least 16 elements,
   so fixed-size reads and writes (glGetIntegerv of GL_VIEWPORT into [0], a mat4 uniform query) stay inside them. */
static bool convert_pointer(aqua_call *call, const char *name, uint32_t index, aqua_value v, uint8_t type, bool offset,
                            uint32_t capacity, gl_slot *out, gl_frame *frame) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint8_t element = type & GL_T_ELEMENT;
    if (aqua_is_nullish(v)) { out->integer = 0; return true; }
    gl_data *data = aqua_object_data(rt, v, gl_data_class);
    if (data) {
        if (!data->bytes) { opengl_fail(call, name, "Buffer is freed or belongs to another runtime."); return false; }
        out->integer = (int64_t)(intptr_t)data->bytes;
        return true;
    }
    gl_pointer *pointer = aqua_object_data(rt, v, gl_pointer_class);
    if (pointer) { out->integer = (int64_t)(intptr_t)pointer->address; return true; }
    if (aqua_is_number(v)) {
        double n = v.number;
        if (!isfinite(n) || n != trunc(n)) { opengl_fail(call, name, "Pointer offsets must be integers."); return false; }
        if (n != 0 && !offset) {
            opengl_fail(call, name, "Use a typed graphics buffer for pointer arguments; only 0 is accepted as a null pointer.");
            return false;
        }
        if (n < 0) { opengl_fail(call, name, "Buffer offsets must be nonnegative."); return false; }
        if (n > 9007199254740992.0) { opengl_fail(call, name, "Buffer offsets must be below 2^53."); return false; }
        out->integer = (int64_t)n;
        return true;
    }
    if (type == GL_T_SYNC) { opengl_fail(call, name, "Expected a sync object (GL_POINTER) or 0."); return false; }
    if (v.type == AQUA_STRING && element == GL_T_CHAR) {
        aqua_str text = aqua_str_of(call, v);
        size_t size = text.length + 1;
        if (size < capacity) size = capacity;
        if (size < 16) size = 16;
        char *copy = aqua_scratch(call, size);
        memset(copy, 0, size);
        memcpy(copy, text.data, text.length);
        out->integer = (int64_t)(intptr_t)copy;
        return true;
    }
    if (v.type != AQUA_ARRAY) { opengl_fail(call, name, "Expected an array."); return false; }
    uint32_t count = aqua_array_length(rt, v);
    if (element == GL_T_VOID) {
        opengl_fail(call, name, "Use GL.FloatData, GL.UIntData, GL.ByteData or GL.Allocate for void* arguments.");
        return false;
    }
    uint32_t room = count > capacity ? count : capacity;
    if (room < 16) room = 16;
    void *buffer = aqua_scratch(call, (size_t)room * element_size(element));
    memset(buffer, 0, (size_t)room * element_size(element));
    if (element == GL_T_TEXT) {
        const char **texts = buffer;
        for (uint32_t i = 0; i < count; i++) {
            aqua_value item = aqua_array_get(rt, v, i);
            if (item.type != AQUA_STRING) { opengl_fail(call, name, "Expected a string."); return false; }
            texts[i] = aqua_str_of(call, item).data;
        }
    } else if (element == GL_T_ADDRESS) {
        void **addresses = buffer;
        for (uint32_t i = 0; i < count; i++) {
            gl_slot s;
            if (!convert_pointer(call, name, index, aqua_array_get(rt, v, i), GL_IN(GL_T_VOID), offset, 0, &s, frame)) return false;
            addresses[i] = (void *)(intptr_t)s.integer;
        }
    } else if (!convert_elements(call, name, v, count, element, buffer)) {
        return false;
    }
    if (type & GL_T_OUTPUT) {
        gl_output *o = &frame->outputs[frame->output_count++];
        o->array = v; o->buffer = buffer; o->count = count; o->element = element;
    }
    out->integer = (int64_t)(intptr_t)buffer;
    return true;
}

/* How many elements an argument provides, for the capacity rules: array elements, or GL_DATA bytes / element size.
   Other pointers (driver pointers, offsets, strings) are not checked, as before. */
static bool check_capacity(aqua_call *call, const char *name, aqua_value v, uint8_t type, uint64_t needed) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) < needed) {
        opengl_fail(call, name, "Pointer array needs at least %llu elements.", (unsigned long long)needed);
        return false;
    }
    gl_data *data = aqua_object_data(rt, v, gl_data_class);
    uint64_t bytes = needed * element_size(type & GL_T_ELEMENT);
    if (data && data->bytes && data->size < bytes) {
        opengl_fail(call, name, "Native buffer needs at least %llu bytes.", (unsigned long long)bytes);
        return false;
    }
    return true;
}

/* The argument kinds of one table entry, read from its kinds string ("n c n p16"). */
typedef struct gl_kinds { char kind[GL_MAX_ARGUMENTS]; uint32_t multiple[GL_MAX_ARGUMENTS]; uint32_t count; } gl_kinds;
static void parse_kinds(const char *text, gl_kinds *k) {
    k->count = 0;
    for (const char *p = text; *p && k->count < GL_MAX_ARGUMENTS;) {
        if (*p == ' ') { p++; continue; }
        k->kind[k->count] = *p++;
        uint32_t multiple = 0;
        while (*p >= '0' && *p <= '9') multiple = multiple * 10 + (uint32_t)(*p++ - '0');
        k->multiple[k->count++] = multiple;
    }
}

/* Converts every argument of a call into slots: numbers and counts first, then the capacity rules, then pointers. */
static bool convert_arguments(aqua_call *call, const char *name, const char *kinds, const uint8_t *types, gl_slot *slot, gl_frame *frame) {
    gl_kinds k;
    parse_kinds(kinds, &k);
    frame->output_count = 0;
    int64_t count = -1;
    for (uint32_t i = 0; i < k.count; i++) {
        if (k.kind[i] != 'n' && k.kind[i] != 'c') continue;
        if (!convert_scalar(call, name, aqua_arg(call, i), types[i], &slot[i])) return false;
        if (k.kind[i] == 'c') {
            count = slot[i].integer;
            if (count < 0) { opengl_fail(call, name, "OpenGL count/size must be nonnegative."); return false; }
        }
    }
    for (uint32_t i = 0; i < k.count; i++)
        if (k.multiple[i] && count >= 0 && !check_capacity(call, name, aqua_arg(call, i), types[i], (uint64_t)count * k.multiple[i])) return false;
    for (uint32_t i = 0; i < k.count; i++) {
        if (k.kind[i] != 'p' && k.kind[i] != 'o') continue;
        uint64_t capacity = k.multiple[i] && count >= 0 ? (uint64_t)count * k.multiple[i] : 0;
        if (capacity > GL_DATA_LIMIT) capacity = GL_DATA_LIMIT;
        if (!convert_pointer(call, name, i, aqua_arg(call, i), types[i], k.kind[i] == 'o', (uint32_t)capacity, &slot[i], frame)) return false;
    }
    return true;
}

/* Copies what the driver wrote into array arguments back into the script's arrays: 64-bit values as GL_INTEGER64,
   pointers as GL_POINTER and every other value as a double, as before. */
static void copy_back(aqua_call *call, const gl_frame *frame) {
    aqua_runtime *rt = aqua_call_runtime(call);
    for (uint32_t o = 0; o < frame->output_count; o++) {
        const gl_output *out = &frame->outputs[o];
        if (out->element == GL_T_I64 || out->element == GL_T_U64) {
            for (uint32_t i = 0; i < out->count; i++)
                aqua_array_set(rt, out->array, i, gl_integer64_new(rt, ((const uint64_t *)out->buffer)[i], out->element == GL_T_I64));
            continue;
        }
        if (out->element == GL_T_ADDRESS) {
            for (uint32_t i = 0; i < out->count; i++) aqua_array_set(rt, out->array, i, gl_pointer_new(rt, ((void *const *)out->buffer)[i]));
            continue;
        }
        double *values = out->count ? aqua_scratch(call, (size_t)out->count * sizeof *values) : NULL;
        for (uint32_t i = 0; i < out->count; i++) {
            const void *b = out->buffer;
            switch (out->element) {
            case GL_T_I8: values[i] = ((const int8_t *)b)[i]; break;
            case GL_T_I16: values[i] = ((const int16_t *)b)[i]; break;
            case GL_T_U16: values[i] = ((const uint16_t *)b)[i]; break;
            case GL_T_I32: values[i] = ((const int32_t *)b)[i]; break;
            case GL_T_U32: values[i] = ((const uint32_t *)b)[i]; break;
            case GL_T_F32: values[i] = ((const float *)b)[i]; break;
            case GL_T_F64: values[i] = ((const double *)b)[i]; break;
            default: values[i] = ((const uint8_t *)b)[i]; break;
            }
        }
        if (out->count) aqua_array_write_f64(rt, out->array, 0, out->count, values, AQUA_DOUBLE);
    }
}

/* The result of a function returning `type`: booleans, numbers (as doubles, as before), strings and driver pointers. */
static void return_result(aqua_call *call, uint8_t type, int64_t value) {
    aqua_runtime *rt = aqua_call_runtime(call);
    switch (type) {
    case GL_T_U8: aqua_return_bool(call, (uint8_t)value != 0); return;
    case GL_T_U32: aqua_return_double(call, (double)(uint32_t)value); return;
    case GL_T_I32: aqua_return_double(call, (double)(int32_t)value); return;
    case GL_IN(GL_T_U8): {
        const char *text = (const char *)(intptr_t)value;
        aqua_return_cstring(call, text ? text : "");
        return;
    }
    default: aqua_return(call, gl_pointer_new(rt, (void *)(intptr_t)value)); return; /* void * and GLsync */
    }
}

/* The state of a call of `name` whose entry point is `available`, with its arguments converted; NULL after failing. */
static opengl_state *gl_enter(aqua_call *call, const char *name, opengl_state *gl, bool available, const char *kinds,
                              const uint8_t *types, gl_slot *slot, gl_frame *frame) {
    if (!gl) return NULL;
    if (!available) { opengl_fail(call, name, "OpenGL function unavailable: %s", name); return NULL; }
    return convert_arguments(call, name, kinds, types, slot, frame) ? gl : NULL;
}

/* ---- The table: parameter types and one wrapper per function -------------------------------------------------- */

/* gl_types_<name>: the result type, then the parameter types. */
#define GL_TYPE_AT(T, i) GL_TYPE(T)
#define GL_ARG_AT(T, i) GL_ARG(T, i)
#define GL_WRAPPER(name, result_type, params, kinds, CALL, RETURN) \
    static const uint8_t gl_types_##name[] = {result_type, GL_LIST(GL_TYPE_AT, params)}; \
    static void gl_call_##name(aqua_call *call) { \
        gl_slot slot[GL_MAX_ARGUMENTS + 1]; \
        gl_frame frame; \
        opengl_state *gl = opengl_require_gl(call, #name); \
        if (!gl_enter(call, #name, gl, gl && gl->api.name, kinds, gl_types_##name + 1, slot, &frame)) return; \
        CALL gl->api.name(GL_LIST(GL_ARG_AT, params)); \
        copy_back(call, &frame); \
        RETURN; \
    }
#define GL_FUNCTION(R, name, alias, params, kinds) \
    GL_WRAPPER(name, GL_TYPE(R), params, kinds, R result =, return_result(call, gl_types_##name[0], (int64_t)(intptr_t)result))
#define GL_VOID(name, alias, params, kinds) GL_WRAPPER(name, GL_T_VOID, params, kinds, , aqua_return_null(call))
#include "gl_functions.def"
#undef GL_FUNCTION
#undef GL_VOID

/* GLAD's loader for a 3.3 core context: every entry point from the window system, then the version from GL_VERSION
   (major * 10000 + minor, GLAD_MAKE_VERSION). */
int32_t opengl_load(gl_api *api) {
    memset(api, 0, sizeof *api);
#define GL_FUNCTION(R, name, alias, params, kinds) api->name = (R (GL_APIENTRY *) params)aqua_gl_proc(#name);
#define GL_VOID(name, alias, params, kinds) api->name = (void (GL_APIENTRY *) params)aqua_gl_proc(#name);
#include "gl_functions.def"
#undef GL_FUNCTION
#undef GL_VOID
    const char *version = api->glGetString ? (const char *)api->glGetString(GL_VERSION_) : NULL;
    if (!version) return 0;
    static const char *const prefixes[] = {"OpenGL ES-CM ", "OpenGL ES-CL ", "OpenGL ES ", "OpenGL SC "};
    for (size_t i = 0; i < sizeof prefixes / sizeof *prefixes; i++)
        if (strncmp(version, prefixes[i], strlen(prefixes[i])) == 0) { version += strlen(prefixes[i]); break; }
    int major = 0, minor = 0;
    if (sscanf(version, "%d.%d", &major, &minor) < 1) return 0;
    return major * 10000 + minor;
}

/* A table entry whose kinds disagree with its C signature would convert arguments wrongly: it is not registered. */
static bool kinds_match(const char *name, const char *kinds, const uint8_t *types, uint32_t arity) {
    gl_kinds k;
    parse_kinds(kinds, &k);
    bool ok = k.count == arity;
    for (uint32_t i = 0; ok && i < k.count; i++) {
        bool pointer = (types[i] & GL_T_POINTER) || types[i] == GL_T_SYNC;
        switch (k.kind[i]) {
        case 'n': ok = !pointer && !k.multiple[i]; break;
        case 'c': ok = is_integer_type(types[i]) && !k.multiple[i]; break;
        case 'p': case 'o': ok = pointer; break;
        default: ok = false; break;
        }
    }
    if (!ok) fprintf(stderr, "opengl: gl_functions.def: the kinds \"%s\" of %s do not match its parameters.\n", kinds, name);
    return ok;
}

/* ---- Data, programs, logs, text and capture --------------------------------------------------------------- */

static aqua_value make_data(aqua_call *call, const char *name, uint8_t element) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value array = aqua_arg(call, 0);
    if (array.type != AQUA_ARRAY) { opengl_fail(call, name, "Expected an array."); return aqua_null(); }
    uint32_t count = aqua_array_length(rt, array);
    uint64_t bytes = (uint64_t)count * element_size(element);
    if (bytes > GL_DATA_LIMIT) { opengl_fail(call, name, "Buffer size must be between 0 and 256 MiB."); return aqua_null(); }
    /* Validate before allocating, so a failed conversion leaves nothing behind. */
    void *buffer = aqua_scratch(call, bytes ? bytes : 1);
    if (!convert_elements(call, name, array, count, element, buffer)) return aqua_null();
    gl_data *data;
    aqua_value value = gl_data_new(rt, (uint32_t)bytes, &data);
    memcpy(data->bytes, buffer, bytes);
    return value;
}
static void float_data(aqua_call *call) { aqua_value v = make_data(call, "FloatData", GL_T_F32); aqua_return(call, v); }
static void uint_data(aqua_call *call) { aqua_value v = make_data(call, "UIntData", GL_T_U32); aqua_return(call, v); }
static void byte_data(aqua_call *call) { aqua_value v = make_data(call, "ByteData", GL_T_U8); aqua_return(call, v); }

static void allocate(aqua_call *call) {
    int32_t size;
    if (!opengl_int(call, "Allocate", 0, &size)) return;
    if (size < 0 || (uint32_t)size > GL_DATA_LIMIT) { opengl_fail(call, "Allocate", "Buffer size must be between 0 and 256 MiB."); return; }
    aqua_return(call, gl_data_new(aqua_call_runtime(call), (uint32_t)size, NULL));
}

static gl_data *data_argument(aqua_call *call, const char *name) {
    gl_data *data = gl_data_of(aqua_call_runtime(call), aqua_arg(call, 0));
    if (!data) opengl_fail(call, name, "Expected a live graphics buffer owned by this runtime.");
    return data;
}
static void byte_length(aqua_call *call) {
    gl_data *data = data_argument(call, "ByteLength");
    if (data) aqua_return_int(call, (int32_t)data->size);
}
static void free_data(aqua_call *call) {
    gl_data *data = data_argument(call, "FreeData");
    if (!data) return;
    aqua_free(data->bytes);
    data->bytes = NULL;
    data->size = 0;
    aqua_return_null(call);
}
static void read_bytes(aqua_call *call) {
    gl_data *data = data_argument(call, "ReadBytes");
    if (data) aqua_return(call, aqua_array_from_u8(aqua_call_runtime(call), data->bytes, data->size));
}

/* A shader or program number argument, as GL functions take it. */
static bool object_argument(aqua_call *call, const char *name, uint32_t index, GLuint *out) {
    gl_slot s;
    if (!convert_scalar(call, name, aqua_arg(call, index), GL_T_U32, &s)) return false;
    *out = (GLuint)s.integer;
    return true;
}

/* The info log of a shader or program, NUL-terminated, in call scratch memory. */
static const char *info_log(aqua_call *call, opengl_state *gl, GLuint object, bool shader) {
    GLint length = 0;
    if (shader) gl->api.glGetShaderiv(object, GL_INFO_LOG_LENGTH_, &length);
    else gl->api.glGetProgramiv(object, GL_INFO_LOG_LENGTH_, &length);
    if (length < 1) length = 1;
    char *log = aqua_scratch(call, (size_t)length + 1);
    memset(log, 0, (size_t)length + 1);
    if (shader) gl->api.glGetShaderInfoLog(object, length, NULL, log);
    else gl->api.glGetProgramInfoLog(object, length, NULL, log);
    return log;
}

static bool program_api(aqua_call *call, const char *name, opengl_state *gl) {
    gl_api *a = &gl->api;
    if (a->glCreateShader && a->glShaderSource && a->glCompileShader && a->glGetShaderiv && a->glGetShaderInfoLog &&
        a->glDeleteShader && a->glCreateProgram && a->glAttachShader && a->glLinkProgram && a->glGetProgramiv &&
        a->glGetProgramInfoLog && a->glDeleteProgram)
        return true;
    opengl_fail(call, name, "OpenGL function unavailable: shader and program entry points");
    return false;
}

static GLuint compile(aqua_call *call, opengl_state *gl, GLenum type, const char *source) {
    GLuint shader = gl->api.glCreateShader(type);
    gl->api.glShaderSource(shader, 1, &source, NULL);
    gl->api.glCompileShader(shader);
    GLint status = 0;
    gl->api.glGetShaderiv(shader, GL_COMPILE_STATUS_, &status);
    if (!status) {
        opengl_fail(call, "CreateProgram", "Shader compile failed: %s", info_log(call, gl, shader, true));
        gl->api.glDeleteShader(shader);
        return 0;
    }
    return shader;
}

static void create_program(aqua_call *call) {
    opengl_state *gl = opengl_require_gl(call, "CreateProgram");
    if (!gl || !program_api(call, "CreateProgram", gl)) return;
    const char *vertex_source = opengl_text(call, "CreateProgram", 0);
    const char *fragment_source = vertex_source ? opengl_text(call, "CreateProgram", 1) : NULL;
    if (!fragment_source) return;
    GLuint vertex = compile(call, gl, GL_VERTEX_SHADER_, vertex_source);
    if (!vertex) return;
    GLuint fragment = compile(call, gl, GL_FRAGMENT_SHADER_, fragment_source);
    if (!fragment) { gl->api.glDeleteShader(vertex); return; }
    GLuint program = gl->api.glCreateProgram();
    gl->api.glAttachShader(program, vertex);
    gl->api.glAttachShader(program, fragment);
    gl->api.glLinkProgram(program);
    GLint status = 0;
    gl->api.glGetProgramiv(program, GL_LINK_STATUS_, &status);
    if (!status) {
        opengl_fail(call, "CreateProgram", "Program link failed: %s", info_log(call, gl, program, false));
        gl->api.glDeleteProgram(program);
    } else {
        aqua_return_double(call, program);
    }
    gl->api.glDeleteShader(vertex);
    gl->api.glDeleteShader(fragment);
}

static void object_log(aqua_call *call, const char *name, bool shader) {
    opengl_state *gl = opengl_require_gl(call, name);
    GLuint object;
    if (!gl || !program_api(call, name, gl) || !object_argument(call, name, 0, &object)) return;
    aqua_return_cstring(call, info_log(call, gl, object, shader));
}
static void shader_log(aqua_call *call) { object_log(call, "ShaderLog", true); }
static void program_log(aqua_call *call) { object_log(call, "ProgramLog", false); }

/* Saves the bottom-left `width` x `height` pixels of the read framebuffer as a binary PPM, top row first. */
static void save_ppm(aqua_call *call) {
    opengl_state *gl = opengl_require_gl(call, "SavePPM");
    if (!gl) return;
    const char *path = opengl_text(call, "SavePPM", 0);
    int32_t width, height;
    if (!path || !opengl_int(call, "SavePPM", 1, &width) || !opengl_int(call, "SavePPM", 2, &height)) return;
    if (width <= 0 || height <= 0) { opengl_fail(call, "SavePPM", "Capture dimensions must be positive."); return; }
    uint64_t row = (uint64_t)width * 3, size = row * (uint64_t)height;
    if (size > GL_DATA_LIMIT) { opengl_fail(call, "SavePPM", "Capture dimensions are too large."); return; }
    if (!gl->api.glReadPixels || !gl->api.glPixelStorei || !gl->api.glGetIntegerv) {
        opengl_fail(call, "SavePPM", "OpenGL function unavailable: glReadPixels");
        return;
    }
    char header[64];
    int header_length = snprintf(header, sizeof header, "P6\n%d %d\n255\n", width, height);
    uint8_t *pixels = aqua_alloc((size_t)size), *file = aqua_alloc((size_t)size + (size_t)header_length);
    GLint alignment = 4;
    gl->api.glGetIntegerv(GL_PACK_ALIGNMENT_, &alignment);
    gl->api.glPixelStorei(GL_PACK_ALIGNMENT_, 1);
    gl->api.glReadPixels(0, 0, width, height, GL_RGB_, GL_UNSIGNED_BYTE_, pixels);
    gl->api.glPixelStorei(GL_PACK_ALIGNMENT_, alignment);
    memcpy(file, header, (size_t)header_length);
    for (int32_t y = 0; y < height; y++)
        memcpy(file + header_length + (size_t)y * row, pixels + (size_t)(height - 1 - y) * row, (size_t)row);
    char *error = NULL;
    if (!opengl_write(path, file, (size_t)size + (size_t)header_length, &error)) {
        opengl_fail(call, "SavePPM", "%s", error ? error : "Cannot write the file.");
        aqua_free(error);
    } else {
        aqua_return_null(call);
    }
    aqua_free(pixels);
    aqua_free(file);
}

static void read_text(aqua_call *call) {
    const char *path = opengl_text(call, "ReadText", 0);
    if (!path) return;
    size_t length;
    char *error = NULL;
    uint8_t *bytes = opengl_read(aqua_call_runtime(call), path, &length, &error);
    if (!bytes) { opengl_fail(call, "ReadText", "%s", error); aqua_free(error); return; }
    size_t skip = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
    aqua_return_text(call, (const char *)bytes + skip, length - skip);
    aqua_free(bytes);
}

/* Decimal integers as .NET's int.TryParse read them: surrounding white space, an optional sign, int32 range. */
static void parse_integer(aqua_call *call) {
    const char *text = opengl_text(call, "ParseInteger", 0);
    if (!text) return;
    const char *p = text;
    while (*p == ' ' || (*p >= '\t' && *p <= '\r')) p++;
    bool negative = *p == '-';
    if (*p == '-' || *p == '+') p++;
    int64_t value = 0;
    const char *digits = p;
    while (*p >= '0' && *p <= '9' && value <= 2147483648LL) value = value * 10 + (*p++ - '0');
    while (*p == ' ' || (*p >= '\t' && *p <= '\r')) p++;
    if (negative) value = -value;
    if (p == digits || *p || value < INT32_MIN || value > INT32_MAX) {
        opengl_fail(call, "ParseInteger", "Expected a decimal integer string.");
        return;
    }
    aqua_return_int(call, (int32_t)value);
}

static void timeout_ignored(aqua_call *call) { aqua_return(call, gl_integer64_new(aqua_call_runtime(call), UINT64_MAX, false)); }

static void glad_load(aqua_call *call) {
    opengl_state *gl = opengl_require_init(call, "Load");
    if (!gl) return;
    if (!gl->current) { opengl_fail(call, "Load", "Call GLFW.MakeContextCurrent(window) before GLAD.Load()."); return; }
    int32_t version = opengl_load(&gl->api);
    if (version < 30003) {
        memset(&gl->api, 0, sizeof gl->api);
        gl->version = 0;
        opengl_fail(call, "Load", "The GPU/driver must support OpenGL 3.3 core or later.");
        return;
    }
    gl->version = version;
    aqua_return_int(call, version);
}

void opengl_register_gl(aqua_library *library) {
    aqua_class *glad = aqua_define_module(library, "GLAD");
    aqua_define_function(glad, "Load|載入", glad_load, "");

    aqua_class *gl = aqua_define_module(library, "GL");
    aqua_define_function(gl, "Allocate|配置", allocate, "v");
    aqua_define_function(gl, "ByteData|位元組資料", byte_data, "v");
    aqua_define_function(gl, "ByteLength|位元組長度", byte_length, "v");
    aqua_define_function(gl, "CreateProgram|建立程式", create_program, "vv");
    aqua_define_function(gl, "FloatData|浮點資料", float_data, "v");
    aqua_define_function(gl, "FreeData|釋放資料", free_data, "v");
    aqua_define_function(gl, "ParseInteger|解析整數", parse_integer, "v");
    aqua_define_function(gl, "ProgramLog|程式紀錄", program_log, "v");
    aqua_define_function(gl, "ReadBytes|讀取位元組", read_bytes, "v");
    aqua_define_function(gl, "ReadText|讀取文字", read_text, "v");
    aqua_define_function(gl, "SavePPM|儲存PPM", save_ppm, "vvv");
    aqua_define_function(gl, "ShaderLog|著色器紀錄", shader_log, "v");
    aqua_define_function(gl, "UIntData|無號整數資料", uint_data, "v");
    static const char any[GL_MAX_ARGUMENTS + 1] = "vvvvvvvvvvv";
#define GL_FUNCTION(R, name, alias, params, kinds) \
    if (kinds_match(#name, kinds, gl_types_##name + 1, GL_ARITY(params))) \
        aqua_define_function(gl, #name "|" alias, gl_call_##name, any + GL_MAX_ARGUMENTS - GL_ARITY(params));
#define GL_VOID(name, alias, params, kinds) GL_FUNCTION(void, name, alias, params, kinds)
#include "gl_functions.def"
#undef GL_FUNCTION
#undef GL_VOID
#define GL_CONSTANT(name, value) aqua_define_double(gl, #name, (double)(value));
#include "gl_constants.def"
#undef GL_CONSTANT
    aqua_define_property(gl, "GL_TIMEOUT_IGNORED", timeout_ignored);
}
