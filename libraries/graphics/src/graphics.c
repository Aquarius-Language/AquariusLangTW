/* The graphics library's entry point: registers Processing, WGPU, GLM, TextInput, Window and Fonts, and owns the
   per-runtime state each module creates on first use. The runtime destroys the state before closing the platform's
   windows, so sketches and WGPU windows release their surfaces first. */
#include "graphics.h"
#include "common/records.h"
#include <stdlib.h>

static void *create_state(aqua_runtime *rt) {
    graphics *g = calloc(1, sizeof *g);
    g->rt = rt;
    return g;
}

static void destroy_state(aqua_runtime *rt, void *data) {
    (void)rt;
    graphics *g = data;
    gfx_window_destroy(g->windows);
    gfx_textinput_destroy(g->text_input);
    gfx_processing_destroy(g->processing);
    gfx_wgpu_destroy(g->wgpu);
    free(g);
}

graphics *gfx_graphics_of(aqua_call *call) { return aqua_state(call); }

AQUA_LIBRARY(graphics) {
    aqua_library_state(library, create_state, destroy_state);
    gfx_records_register(library);
    gfx_processing_register(library);
    gfx_wgpu_register(library);
    gfx_glm_register(library);
    gfx_textinput_register(library);
    gfx_window_register(library);
    gfx_fonts_register(library);
}
