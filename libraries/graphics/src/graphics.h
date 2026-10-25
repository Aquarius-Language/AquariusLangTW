/* The graphics library's shared declarations: its per-runtime state, the classes every module registers, and the
 * X-macro wrappers that give each registered function its error prefix.
 *
 * Module tables are written as X(c_name, "English|中文", minimum, maximum) entries. GFX_FUNCTION_WRAPPER generates a
 * wrapper per entry that records the prefix and names (common/calls.h) and calls the implementation. */
#ifndef GRAPHICS_GRAPHICS_H
#define GRAPHICS_GRAPHICS_H

#include <aqua/aqua.h>
#include <aqua/image.h>
#include <aqua/window.h>
#include "common/calls.h"

typedef struct processing processing;
typedef struct wgpu_state wgpu_state;
typedef struct window_state window_state;
typedef struct textinput_state textinput_state;

/* Per-runtime state (aqua_library_state); each module's part is created on first use. */
typedef struct graphics {
    aqua_runtime *rt;
    processing *processing;
    wgpu_state *wgpu;
    window_state *windows;
    textinput_state *text_input;
} graphics;

graphics *gfx_graphics_of(aqua_call *call);

/* Module registration (each in its own file) and the per-runtime parts' teardown. */
void gfx_processing_register(aqua_library *library);
void gfx_wgpu_register(aqua_library *library);
void gfx_glm_register(aqua_library *library);
void gfx_textinput_register(aqua_library *library);
void gfx_window_register(aqua_library *library);
void gfx_fonts_register(aqua_library *library);
void gfx_processing_destroy(processing *p);
void gfx_wgpu_destroy(wgpu_state *w);
void gfx_window_destroy(window_state *w);
void gfx_textinput_destroy(textinput_state *t);

/* Defines `wrapper_<name>` calling `implementation(call)` with the prefix of `module_prefix` and the names. */
#define GFX_WRAP(prefix, name, names, implementation) \
    static void wrapper_##name(aqua_call *call) { gfx_enter(call, prefix, names); implementation(call); }

#endif
