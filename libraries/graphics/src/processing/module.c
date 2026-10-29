/* Processing (繪圖) registration: the module and its object classes, constants, and the live fields scripts read
   (width, mouseX, key, frameCount, ...). Each area's file registers its own members (processing.h lists them). */
#include "processing.h"
#include <stdlib.h>
#include <string.h>

aqua_class *gfx_processing_module_class, *gfx_canvas_class, *gfx_image_class, *gfx_shape_class, *gfx_shader_class, *gfx_vector_class, *gfx_font_class;

processing *gfx_processing_create(aqua_runtime *rt) {
    processing *p = calloc(1, sizeof *p);
    p->rt = rt;
    p->screen = gfx_canvas_new(p, 640, 480, false, false);
    p->looping = true;
    p->target_fps = 60;
    p->redraw_at = -1;
    p->noise_octaves = 4;
    p->noise_falloff = 0.5;
    gfx_noise_seed(p, 0); /* random() stays unseeded (time-based) until randomSeed() */
    gfx_input_reset(p);
    return p;
}

processing *gfx_processing_of(aqua_call *call) {
    graphics *g = gfx_graphics_of(call);
    if (!g->processing) g->processing = gfx_processing_create(g->rt);
    return g->processing;
}

void gfx_processing_destroy(processing *p) {
    if (!p) return;
    p->running = false;
    gfx_processing_close(p);
    while (p->canvases) gfx_canvas_free(p->canvases);
    while (p->images) { pimage *i = p->images; gfx_image_dispose(i); p->images = i->next; i->previous = i->next = NULL; i->owner = NULL; }
    gfx_canvas_free(p->screen);
    for (int i = 0; i < EVENT_COUNT; i++) if (p->events[i]) aqua_release(p->rt, p->events[i]);
    if (p->setup) aqua_release(p->rt, p->setup);
    if (p->draw) aqua_release(p->rt, p->draw);
    gfx_input_detach(p);
    free(p->raw); free(p->queue); free(p->textures);
    free(p->input_text); free(p->composition_text); free(p->error_message); free(p->failure);
    gfx_text_clear(p);
    gfx_map_clear(&p->text_cache);
    gfx_geo_free(&p->geometry);
    free(p);
}

static void canvas_finalize(aqua_runtime *rt, void *data) { (void)rt; gfx_canvas_free(data); }
static void vector_finalize(aqua_runtime *rt, void *data) { (void)rt; free(data); }

/* ---- Live fields ---- */

#define P (gfx_processing_of(call))
static void f_width(aqua_call *call) { aqua_return_int(call, P->screen->width); }
static void f_height(aqua_call *call) { aqua_return_int(call, P->screen->height); }
static void f_pixel_width(aqua_call *call) { aqua_return_int(call, P->screen->pixel_width); }
static void f_pixel_height(aqua_call *call) { aqua_return_int(call, P->screen->pixel_height); }
static void f_frame_count(aqua_call *call) { aqua_return_int(call, P->frame_count); }
static void display(aqua_call *call, bool width) {
    int w = 0, h = 0;
    aqua_display_size(aqua_call_runtime(call), &w, &h);
    aqua_return_int(call, width ? w : h);
}
static void f_display_width(aqua_call *call) { display(call, true); }
static void f_display_height(aqua_call *call) { display(call, false); }
static void f_mouse_x(aqua_call *call) { aqua_return_double(call, P->input.mouse_x); }
static void f_mouse_y(aqua_call *call) { aqua_return_double(call, P->input.mouse_y); }
static void f_pmouse_x(aqua_call *call) { aqua_return_double(call, P->input.pmouse_x); }
static void f_pmouse_y(aqua_call *call) { aqua_return_double(call, P->input.pmouse_y); }
static void f_key_code(aqua_call *call) { aqua_return_int(call, P->input.key_code); }
static void f_mouse_button(aqua_call *call) { aqua_return_int(call, P->input.mouse_button); }
static void f_wheel_count(aqua_call *call) { aqua_return_double(call, P->input.wheel_count); }
static void f_mouse_pressed(aqua_call *call) { aqua_return_bool(call, P->input.mouse_pressed); }
static void f_key_pressed(aqua_call *call) { aqua_return_bool(call, P->input.key_pressed); }
static void f_key(aqua_call *call) { aqua_return_cstring(call, P->input.key); }
static void f_key_timestamp(aqua_call *call) { aqua_return_double(call, P->input.key_timestamp); }
static void f_actual_frame_rate(aqua_call *call) { processing *p = P; aqua_return_double(call, p->has_frame_rate ? p->actual_frame_rate : p->target_fps); }
static void f_text_input_enabled(aqua_call *call) { aqua_return_bool(call, P->text_input_enabled); }
static void f_input_text(aqua_call *call) { aqua_return_cstring(call, P->input_text ? P->input_text : ""); }
static void f_input_timestamp(aqua_call *call) { aqua_return_double(call, P->input_timestamp); }
static void f_composition_text(aqua_call *call) { aqua_return_cstring(call, P->composition_text ? P->composition_text : ""); }
static void f_composition_cursor(aqua_call *call) { aqua_return_int(call, P->composition_cursor); }
static void f_is_composing(aqua_call *call) { aqua_return_bool(call, P->is_composing); }
static void f_error_event(aqua_call *call) { aqua_return_cstring(call, P->error_event); }
static void f_error_message(aqua_call *call) { aqua_return_cstring(call, P->error_message ? P->error_message : ""); }
static void f_focused(aqua_call *call) { processing *p = P; aqua_return_bool(call, p->window && aqua_window_focused(p->window)); }
/* The sketch's window as the shared window object, for libraries that work with any window (Interaction, TextInput). */
static void f_platform_window(aqua_call *call) {
    processing *p = P;
    if (p->window) aqua_return(call, aqua_window_object(p->window));
    else aqua_return_null(call);
}
static void f_pixels(aqua_call *call) {
    processing *p = P;
    if (p->screen && p->screen->pixels) aqua_return(call, aqua_ref_value(p->rt, p->screen->pixels));
    else aqua_return_null(call);
}
#undef P

#define MODULE_FIELDS(X) \
    X("width|寬度", f_width) X("height|高度", f_height) X("pixelWidth", f_pixel_width) X("pixelHeight", f_pixel_height) \
    X("frameCount", f_frame_count) X("displayWidth|顯示寬度", f_display_width) X("displayHeight|顯示高度", f_display_height) \
    X("mouseX", f_mouse_x) X("mouseY", f_mouse_y) X("pmouseX", f_pmouse_x) X("pmouseY", f_pmouse_y) \
    X("keyCode", f_key_code) X("mouseButton", f_mouse_button) X("wheelCount", f_wheel_count) \
    X("isMousePressed", f_mouse_pressed) X("isKeyPressed", f_key_pressed) X("key", f_key) \
    X("keyTimestamp", f_key_timestamp) X("actualFrameRate", f_actual_frame_rate) \
    X("textInputEnabled", f_text_input_enabled) X("inputText", f_input_text) X("inputTimestamp", f_input_timestamp) \
    X("compositionText", f_composition_text) X("compositionCursor", f_composition_cursor) X("isComposing", f_is_composing) \
    X("errorEvent", f_error_event) X("errorMessage", f_error_message) X("pixels", f_pixels) X("focused|已聚焦", f_focused) \
    X("platformWindow|平台視窗", f_platform_window)
#define DEFINE_FIELD(names, getter) aqua_define_property(module, names, getter);

/* PGraphics fields. */
static void c_width(aqua_call *call) { aqua_return_int(call, ((canvas *)aqua_self(call))->width); }
static void c_height(aqua_call *call) { aqua_return_int(call, ((canvas *)aqua_self(call))->height); }
static void c_pixel_width(aqua_call *call) { aqua_return_int(call, ((canvas *)aqua_self(call))->pixel_width); }
static void c_pixel_height(aqua_call *call) { aqua_return_int(call, ((canvas *)aqua_self(call))->pixel_height); }
static void c_pixels(aqua_call *call) {
    canvas *c = aqua_self(call);
    if (c->pixels) aqua_return(call, aqua_ref_value(aqua_call_runtime(call), c->pixels));
    else aqua_return_null(call);
}

/* Processing's integer constants, as the earlier implementation defined them. */
#define PROCESSING_CONSTANTS(X) \
    X(CORNER, 0) X(CORNERS, 1) X(CENTER, 2) X(RADIUS, 3) X(RGB, 0) X(HSB, 1) X(ARGB, 2) X(OPEN, 0) X(CHORD, 1) X(PIE, 2) \
    X(CLOSE, 1) X(POINTS, 0) X(LINES, 1) X(TRIANGLES, 4) X(TRIANGLE_STRIP, 5) X(TRIANGLE_FAN, 6) X(QUADS, 7) X(QUAD_STRIP, 8) \
    X(POLYGON, 9) X(LEFT, 0) X(RIGHT, 1) X(TOP, 101) X(BOTTOM, 102) X(BASELINE, 103) X(ROUND, 1) X(SQUARE, 0) X(PROJECT, 2) \
    X(MITER, 0) X(BEVEL, 2) X(BLEND, 0) X(ADD, 1) X(MULTIPLY, 2) X(SCREEN, 3) X(REPLACE, 4) X(GRAY, 0) X(INVERT, 1) \
    X(THRESHOLD, 2) X(POSTERIZE, 3) X(OPAQUE, 4) X(BLUR, 5) X(NORMAL, 0) X(IMAGE, 1) X(ESC, 256) X(SPACE, 32) \
    X(SUBTRACT, 6) X(LIGHTEST, 7) X(DARKEST, 8) X(EXCLUSION, 9) X(DIFFERENCE, 10) X(OVERLAY, 11) X(HARD_LIGHT, 12) \
    X(SOFT_LIGHT, 13) X(DODGE, 14) X(BURN, 15) X(DILATE, 6) X(ERODE, 7) X(CLAMP, 0) X(REPEAT, 1) \
    X(GROUP, 0) X(POINT, 2) X(LINE, 4) X(TRIANGLE, 8) X(QUAD, 16) X(RECT, 30) X(ELLIPSE, 31) X(ARC, 32) X(SPHERE, 40) X(BOX, 41) \
    X(DISABLE_DEPTH_TEST, 2) X(ENABLE_DEPTH_TEST, -2) X(ENABLE_DEPTH_SORT, 3) X(DISABLE_DEPTH_SORT, -3) \
    X(DISABLE_DEPTH_MASK, 5) X(ENABLE_DEPTH_MASK, -5) X(DISABLE_OPTIMIZED_STROKE, 6) X(ENABLE_OPTIMIZED_STROKE, -6) \
    X(ENABLE_STROKE_PERSPECTIVE, 7) X(DISABLE_STROKE_PERSPECTIVE, -7) X(DISABLE_TEXTURE_MIPMAPS, 8) X(ENABLE_TEXTURE_MIPMAPS, -8) \
    X(UP, 265) X(DOWN, 264) X(UP_ARROW, 265) X(DOWN_ARROW, 264) X(LEFT_ARROW, 263) X(RIGHT_ARROW, 262) X(SHIFT, 340) \
    X(CONTROL, 341) X(ALT, 342) X(ENTER, 257) X(RETURN, 257) X(BACKSPACE, 259) X(TAB, 258) X(DELETE, 261) \
    X(INSERT, 260) X(HOME, 268) X(END, 269) X(PAGE_UP, 266) X(PAGE_DOWN, 267) \
    X(ARROW, 0) X(CROSS, 1) X(TEXT, 2) X(WAIT, 3) X(HAND, 12) X(MOVE, 13)
#define DEFINE_CONSTANT(name, value) aqua_define_int(module, #name, value);

void gfx_processing_register(aqua_library *library) {
    aqua_class *module = gfx_processing_module_class = aqua_define_module(library, "Processing|繪圖");
    aqua_class *canvas = gfx_canvas_class = aqua_define_class(library, "Processing.Canvas", canvas_finalize);
    aqua_class *image = gfx_image_class = aqua_define_class(library, "Processing.Image", gfx_image_finalize);
    aqua_class *shape = gfx_shape_class = aqua_define_class(library, "Processing.Shape", gfx_shape_finalize);
    aqua_class *shader = gfx_shader_class = aqua_define_class(library, "Processing.Shader", gfx_shader_finalize);
    aqua_class *vector = gfx_vector_class = aqua_define_class(library, "Processing.Vector", vector_finalize);
    aqua_class *font = gfx_font_class = aqua_define_class(library, "Processing.Font", gfx_font_finalize);

    aqua_define_string(module, "backend", "wgpu");
    aqua_define_string(module, "P2D", "P2D");
    aqua_define_string(module, "P3D", "P3D");
    PROCESSING_CONSTANTS(DEFINE_CONSTANT)
    MODULE_FIELDS(DEFINE_FIELD)
    aqua_define_property(canvas, "width", c_width);
    aqua_define_property(canvas, "height", c_height);
    aqua_define_property(canvas, "pixelWidth", c_pixel_width);
    aqua_define_property(canvas, "pixelHeight", c_pixel_height);
    aqua_define_property(canvas, "pixels", c_pixels);

    gfx_drawing_register(module, canvas);
    gfx_image_register(module, canvas, image);
    gfx_shapes_register(module, canvas, shape);
    gfx_shaders_register(module, canvas, shader);
    gfx_text_register(module, canvas, font);
    gfx_math_register(module);
    gfx_data_register(module);
    gfx_vector_register(module, vector);
    gfx_lifecycle_register(module, canvas);
    gfx_input_register(module);
}
