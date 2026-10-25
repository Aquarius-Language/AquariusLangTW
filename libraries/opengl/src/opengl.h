/* Internal declarations shared by the opengl library's modules: GLFW (glfw.c), GLAD and GL (gl.c), STBImage (image.c).
 * One per-runtime state holds the window system ownership, the GLFW module's window, the current context and the loaded
 * OpenGL function table. */
#ifndef OPENGL_H
#define OPENGL_H
#include <aqua/aqua.h>
#include <aqua/window.h>
#include "gl_types.h"

/* The driver's entry points, loaded by GLAD.Load() for the current context. */
typedef struct gl_api {
#define GL_FUNCTION(R, name, alias, params, kinds) R (GL_APIENTRY *name) params;
#define GL_VOID(name, alias, params, kinds) void (GL_APIENTRY *name) params;
#include "gl_functions.def"
#undef GL_FUNCTION
#undef GL_VOID
} gl_api;

typedef struct opengl_state {
    aqua_runtime *rt;
    bool initialized;            /* GLFW.Init() made this runtime the owner of the window system */
    aqua_window *window;         /* the window of GLFW.CreateWindow (one at a time, as before) */
    aqua_window *current;        /* the window whose OpenGL context is current */
    int32_t version;             /* GLAD.Load(): major * 10000 + minor; 0 until the current context is loaded */
    int32_t hint_visible, hint_resizable, hint_decorated, hint_client_api;
    gl_api api;
} opengl_state;

/* Native object classes (registered once by the library entry in glfw.c). */
extern aqua_class *gl_data_class;      /* GL_DATA: typed bytes for pointer arguments */
extern aqua_class *gl_pointer_class;   /* GL_POINTER: an address the driver returned */
extern aqua_class *gl_integer64_class; /* GL_INTEGER64: an exact 64-bit query result */

typedef struct gl_data { uint8_t *bytes; uint32_t size; } gl_data;
typedef struct gl_pointer { void *address; } gl_pointer;
typedef struct gl_integer64 { uint64_t bits; bool is_signed; } gl_integer64;

/* GL_DATA larger than this is refused, as before. */
#define GL_DATA_LIMIT (256u * 1024u * 1024u)
aqua_value gl_data_new(aqua_runtime *rt, uint32_t size, gl_data **out);
gl_data *gl_data_of(aqua_runtime *rt, aqua_value value);   /* a live GL_DATA, or NULL */
aqua_value gl_pointer_new(aqua_runtime *rt, void *address);
aqua_value gl_integer64_new(aqua_runtime *rt, uint64_t bits, bool is_signed);

/* Fails the call with "<function>: <message>", the form of the library's earlier errors. */
void opengl_fail(aqua_call *call, const char *function, const char *format, ...);
/* Reads argument `index` as an int32 with the earlier messages ("Expected a finite number.", "Expected an integer."). */
bool opengl_int(aqua_call *call, const char *function, uint32_t index, int32_t *out);
/* The string argument `index`, or NULL after failing with "Expected a string.". */
const char *opengl_text(aqua_call *call, const char *function, uint32_t index);

/* GLFW.Init() was called by this runtime / a window is current and GLAD.Load() succeeded. Fail the call otherwise. */
opengl_state *opengl_require_init(aqua_call *call, const char *function);
opengl_state *opengl_require_gl(aqua_call *call, const char *function);

/* The bytes of a script path: a packaged asset of the application under that path, else the file. Free with aqua_free.
   NULL with a message in *error (freed by the caller with aqua_free) when neither can be read. */
uint8_t *opengl_read(aqua_runtime *rt, const char *path, size_t *length, char **error);
/* Writes a file, creating its parent directories. */
bool opengl_write(const char *path, const void *bytes, size_t length, char **error);

/* Module registration (glfw.c, gl.c, image.c). */
void opengl_register_glfw(aqua_library *library);
void opengl_register_gl(aqua_library *library);
void opengl_register_image(aqua_library *library);
/* Loads the function table for the current context; returns the version (major * 10000 + minor) or 0. */
int32_t opengl_load(gl_api *api);

#endif
