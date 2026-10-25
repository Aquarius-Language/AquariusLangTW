/* The opengl library: desktop OpenGL 3.3 core for scripts through the GLFW, GLAD, GL and STBImage modules. This file
 * holds the library entry, the per-runtime state, the native object classes and the helpers every module shares. */
#include "opengl.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

aqua_class *gl_data_class, *gl_pointer_class, *gl_integer64_class;

/* ---- Native objects ---- */

static void data_finalize(aqua_runtime *rt, void *p) { (void)rt; gl_data *data = p; aqua_free(data->bytes); aqua_free(data); }
static void plain_finalize(aqua_runtime *rt, void *p) { (void)rt; aqua_free(p); }

/* Typed bytes keep a 64-byte zeroed tail, so a driver reading or writing a few fixed-size values past a short buffer
   (glVertexAttrib4fv of a 1-float buffer) stays inside the allocation. */
aqua_value gl_data_new(aqua_runtime *rt, uint32_t size, gl_data **out) {
    gl_data *data = aqua_alloc(sizeof *data);
    data->size = size;
    data->bytes = aqua_alloc((size_t)size + 64);
    memset(data->bytes, 0, (size_t)size + 64);
    if (out) *out = data;
    return aqua_object_new(rt, gl_data_class, data);
}
gl_data *gl_data_of(aqua_runtime *rt, aqua_value value) {
    gl_data *data = aqua_object_data(rt, value, gl_data_class);
    return data && data->bytes ? data : NULL;
}
aqua_value gl_pointer_new(aqua_runtime *rt, void *address) {
    gl_pointer *pointer = aqua_alloc(sizeof *pointer);
    pointer->address = address;
    return aqua_object_new(rt, gl_pointer_class, pointer);
}
aqua_value gl_integer64_new(aqua_runtime *rt, uint64_t bits, bool is_signed) {
    gl_integer64 *integer = aqua_alloc(sizeof *integer);
    integer->bits = bits;
    integer->is_signed = is_signed;
    return aqua_object_new(rt, gl_integer64_class, integer);
}

/* GL_INTEGER64 shows its value through `value` (a double, exact below 2^53) and `text` (the exact decimal digits). */
static void integer64_value(aqua_call *call) {
    gl_integer64 *integer = aqua_self(call);
    aqua_return_double(call, integer->is_signed ? (double)(int64_t)integer->bits : (double)integer->bits);
}
static void integer64_text(aqua_call *call) {
    gl_integer64 *integer = aqua_self(call);
    char text[32];
    if (integer->is_signed) snprintf(text, sizeof text, "%lld", (long long)(int64_t)integer->bits);
    else snprintf(text, sizeof text, "%llu", (unsigned long long)integer->bits);
    aqua_return_cstring(call, text);
}

/* ---- Shared helpers ---- */

void opengl_fail(aqua_call *call, const char *function, const char *format, ...) {
    char message[1536];
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(message, sizeof message, format, arguments);
    va_end(arguments);
    aqua_fail(call, "%s: %s", function, message);
}

bool opengl_int(aqua_call *call, const char *function, uint32_t index, int32_t *out) {
    aqua_value v = aqua_arg(call, index);
    if (!aqua_is_number(v) || !isfinite(v.number)) { opengl_fail(call, function, "Expected a finite number."); return false; }
    if (v.number != trunc(v.number)) { opengl_fail(call, function, "Expected an integer."); return false; }
    if (v.number < -2147483648.0 || v.number > 2147483647.0) {
        opengl_fail(call, function, "Argument %u is out of range for an integer.", index + 1);
        return false;
    }
    *out = (int32_t)v.number;
    return true;
}

const char *opengl_text(aqua_call *call, const char *function, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    if (v.type != AQUA_STRING) { opengl_fail(call, function, "Expected a string."); return NULL; }
    return aqua_str_of(call, v).data;
}

opengl_state *opengl_require_init(aqua_call *call, const char *function) {
    opengl_state *state = aqua_state(call);
    if (!state->initialized) { opengl_fail(call, function, "Call GLFW.Init() first."); return NULL; }
    return state;
}

opengl_state *opengl_require_gl(aqua_call *call, const char *function) {
    opengl_state *state = opengl_require_init(call, function);
    if (state && (!state->current || !state->version)) {
        opengl_fail(call, function, "Make a window current and call GLAD.Load() first.");
        return NULL;
    }
    return state;
}

/* ---- The library ---- */

void opengl_terminate(opengl_state *state); /* glfw.c */

static void *state_create(aqua_runtime *rt) {
    opengl_state *state = aqua_alloc(sizeof *state);
    memset(state, 0, sizeof *state);
    state->rt = rt;
    state->hint_visible = state->hint_resizable = state->hint_decorated = 1;
    state->hint_client_api = 0x30001; /* GLFW_OPENGL_API */
    return state;
}
static void state_destroy(aqua_runtime *rt, void *p) {
    (void)rt;
    opengl_terminate(p);
    aqua_free(p);
}

AQUA_LIBRARY(opengl) {
    aqua_library_state(library, state_create, state_destroy);
    gl_data_class = aqua_define_class(library, "GL_DATA", data_finalize);
    gl_pointer_class = aqua_define_class(library, "GL_POINTER", plain_finalize);
    gl_integer64_class = aqua_define_class(library, "GL_INTEGER64", plain_finalize);
    aqua_define_property(gl_integer64_class, "value|值", integer64_value);
    aqua_define_property(gl_integer64_class, "text|文字", integer64_text);
    opengl_register_glfw(library);
    opengl_register_gl(library);
    opengl_register_image(library);
}
