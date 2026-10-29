/* The sketch: size()/fullScreen() open its window and device; run(setup, draw) is a frame driver on aqua_schedule
   (aligned with animation frames on the web) that dispatches the frame's events, calls draw, presents and paces to the
   frame rate, while the calling script waits until the sketch ends. beginFrame()/endFrame() drive frames by hand.
   AQUARIUS_GRAPHICS_FRAMES=N ends run() after N drawn frames and AQUARIUS_GRAPHICS_CAPTURE=file saves the last one. */
#include "processing.h"
#include "../graphics.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

bool gfx_processing_valid_key(int code);

bool gfx_require_sketch(aqua_call *call, processing *p) {
    if (!p->window || !p->device || !p->screen || p->screen->disposed || gpu_device_disposed(p->device)) {
        gfx_fail(call, "Call Processing.size() first.");
        return false;
    }
    return true;
}

gpu_shader *gfx_processing_default_shader(processing *p) { return p->default_shader; }

float gfx_processing_content_scale(processing *p) {
    if (!p->window) return 1;
    float scale = aqua_window_content_scale(p->window);
    return isfinite(scale) && scale > 0 ? scale : 1;
}

static double clock_now(processing *p) {
    if (!p->clock_running) return p->clock_stop;
    const char *record = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_RECORD");
    if (record && *record) return (p->frame_count > 0 ? p->frame_count - 1 : 0) * 1000.0 / p->target_fps;
    return aqua_now_ms(p->rt) - p->clock_start;
}

/* ---- Closing ---- */

void gfx_processing_close(processing *p) {
    if (!p->window) return;
    gfx_input_detach(p);
    for (canvas *c = p->canvases; c; c = c->next) gfx_canvas_dispose(c);
    for (pshader *s = p->shaders; s; s = s->next) gfx_shader_dispose(s);
    for (pimage *i = p->images; i; i = i->next) gfx_image_dispose(i);
    gfx_text_clear(p);
    void gfx_texture_object_dispose(aqua_runtime *rt, aqua_value value);
    for (size_t i = 0; i < p->texture_count; i++) { gfx_texture_object_dispose(p->rt, aqua_ref_value(p->rt, p->textures[i])); aqua_release(p->rt, p->textures[i]); }
    p->texture_count = 0;
    if (p->screen) gfx_canvas_dispose(p->screen);
    if (p->default_shader) { gpu_shader_free(p->default_shader); p->default_shader = NULL; }
    if (p->device) { gpu_device_dispose(p->device); gpu_device_release(p->device); p->device = NULL; }
    aqua_window_destroy(p->window);
    p->window = NULL;
    p->clock_stop = clock_now(p);
    p->clock_running = false;
    gfx_input_reset(p);
}

/* The main canvas's pixel size: the window's framebuffer, or the logical size times pixelDensity(). */
static void sketch_pixels(processing *p, int width, int height, int *pw, int *ph) {
    aqua_window_framebuffer_size(p->window, pw, ph);
    if (p->pixel_density > 0 && *pw > 0 && *ph > 0) { *pw = width * p->pixel_density; *ph = height * p->pixel_density; }
}

/* ---- size() ---- */

typedef struct sketch_start { processing *p; bool is3d; int width, height; } sketch_start;

static void started(gfx_async *a) {
    sketch_start *s = gfx_async_data(a);
    processing *p = s->p;
    canvas *c = p->screen;
    c->is3d = s->is3d;
    c->drawing = true;
    int pw = 0, ph = 0;
    sketch_pixels(p, s->width, s->height, &pw, &ph);
    gfx_canvas_resize(c, s->width, s->height, pw, ph);
    gfx_canvas_default_camera(c);
    if (!gfx_canvas_initialize(gfx_async_call(a), c, p->device)) { gfx_processing_close(p); gfx_async_settle(a, false, aqua_void()); return; }
    p->clock_start = aqua_now_ms(p->rt);
    p->clock_running = true;
    memset(p->keys, 0, sizeof p->keys);
    memset(p->buttons, 0, sizeof p->buttons);
    p->input.mouse_pressed = false;
    p->cursor_x = p->cursor_y = 0;
    gfx_async_finish(a, aqua_void());
}

static void shader_ready(gpu_shader *shader, const char *error, void *data) {
    gfx_async *a = data;
    sketch_start *s = gfx_async_data(a);
    if (error || !s->p->device) { gfx_processing_close(s->p); gfx_async_fail(a, "%s", error ? error : "Call Processing.size() first."); return; }
    s->p->default_shader = shader;
    started(a);
}

static void device_ready(gpu_device *device, const char *error, void *data) {
    gfx_async *a = data;
    sketch_start *s = gfx_async_data(a);
    if (error) { gfx_processing_close(s->p); gfx_async_fail(a, "%s", error); return; }
    s->p->device = device;
    gpu_shader_create(device, gpu_wgsl_vertex, gpu_wgsl_fragment, shader_ready, a);
}

static bool renderer(aqua_call *call, uint32_t index, bool *is3d) {
    aqua_str r = gfx_text(call, index);
    if (aqua_failed(call)) return false;
    if (strcmp(r.data, "P2D") != 0 && strcmp(r.data, "P3D") != 0) { gfx_fail(call, "Renderer must be P2D or P3D."); return false; }
    *is3d = strcmp(r.data, "P3D") == 0;
    return true;
}

static void open_sketch(aqua_call *call, processing *p, int width, int height, bool is3d, const char *title, bool fullscreen) {
    if (p->window) { gfx_fail(call, "size() can only be called once per sketch."); return; }
    if (!p->screen || p->screen->disposed) {
        if (p->screen) gfx_canvas_free(p->screen);
        p->screen = gfx_canvas_new(p, 640, 480, false, false);
    }
    char *error = NULL;
    p->window = aqua_window_create(p->rt, width, height, title, fullscreen ? AQUA_WINDOW_FULLSCREEN : 0, &error);
    if (!p->window) { gfx_fail(call, "%s", error ? error : "Cannot create a window."); free(error); return; }
    gfx_input_reset(p);
    gfx_input_attach(p);
    gfx_async *a = gfx_async_begin(call, sizeof(sketch_start));
    sketch_start *s = gfx_async_data(a);
    s->p = p; s->is3d = is3d; s->width = width; s->height = height;
    gpu_device_create(p->rt, p->window, device_ready, a);
    gfx_async_end(a);
}

static void p_size(aqua_call *call, processing *p) {
    if (p->window) { gfx_fail(call, "size() can only be called once per sketch."); return; }
    int w = gfx_dimension(call, 0), h = gfx_dimension(call, 1);
    bool is3d = false;
    if (aqua_argc(call) > 2 && !renderer(call, 2, &is3d)) return;
    aqua_str title = aqua_argc(call) > 3 ? gfx_text(call, 3) : (aqua_str){"Aquarius Processing", 19};
    if (aqua_failed(call)) return;
    open_sketch(call, p, w, h, is3d, title.data, false);
}

static void p_fullScreen(aqua_call *call, processing *p) {
    if (p->window) { gfx_fail(call, "fullScreen() must be called before size()."); return; }
    bool is3d = false;
    if (aqua_argc(call) > 0 && !renderer(call, 0, &is3d)) return;
    aqua_str title = aqua_argc(call) > 1 ? gfx_text(call, 1) : (aqua_str){"Aquarius Processing", 19};
    if (aqua_failed(call)) return;
    int w, h;
    if (!aqua_display_size(p->rt, &w, &h)) { gfx_fail(call, "Primary monitor is unavailable."); return; }
    open_sketch(call, p, w, h, is3d, title.data, true);
}

static void p_resize(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    int w = gfx_dimension(call, 0), h = gfx_dimension(call, 1);
    if (!aqua_failed(call)) aqua_window_set_size(p->window, w, h);
}

static void p_windowMove(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    double x = gfx_number(call, 0), y = gfx_number(call, 1);
    if (aqua_failed(call)) return;
    if (fabs(x) > 100000 || fabs(y) > 100000) { gfx_fail(call, "windowMove expects screen coordinates."); return; }
    aqua_window_set_position(p->window, (int)rint(x), (int)rint(y));
}

static void p_displayDensity(aqua_call *call, processing *p) { aqua_return_double(call, gfx_processing_content_scale(p)); }
/* pixelDensity(n): the main canvas renders n device pixels per unit (presented scaled to the window); 0 follows the
   display again. Takes effect at the next frame. */
static void p_pixelDensity(aqua_call *call, processing *p) {
    int n = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    if (n < 0 || n > 4) { gfx_fail(call, "Pixel density must be 0..4 (0 follows the display)."); return; }
    p->pixel_density = n;
}
static void p_windowTitle(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    aqua_str title = gfx_text(call, 0);
    if (!aqua_failed(call)) aqua_window_set_title(p->window, title.data);
}
static void p_displayScale(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    canvas *c = p->screen;
    double scale = gfx_processing_content_scale(p);
    if (c->pixel_width <= 0 || c->width <= 0) { aqua_return_double(call, scale); return; }
    aqua_return_double(call, rint(scale * c->width / c->pixel_width * 10000) / 10000);
}

/* ---- Frames ---- */

/* The start of a frame: OS events, the window's size (attachments resized before callbacks), the main canvas's
   per-frame reset, and this frame's sketch events. */
static void begin_frame(processing *p) {
    aqua_windows_poll(p->rt);
    if (!p->window) return;
    int pw = 0, ph = 0, w = 0, h = 0;
    aqua_window_size(p->window, &w, &h);
    sketch_pixels(p, w, h, &pw, &ph);
    canvas *c = p->screen;
    bool resized = gfx_canvas_resize(c, w, h, pw, ph);
    c->model = gfx_m4_identity();
    c->matrix_count = 0;
    c->style.lit = false;
    c->style.light_count = 0;
    c->style.ambient = v3(0, 0, 0);
    gfx_canvas_bind(c);
    gfx_input_clear_queue(p);
    if (resized) {
        p->redraw = true;
        if (pw > 0 && ph > 0) {
            sketch_event e = {EVENT_WINDOW_RESIZED, p->input, NULL, 0, 0};
            GFX_PUSH(p->queue, p->queue_count, p->queue_capacity, e);
        }
    }
    gfx_input_collect(p);
}

static bool end_frame(aqua_call *call, processing *p) {
    canvas *c = p->screen;
    if (c->shape_mode != -1) { gfx_fail(call, "Unfinished beginShape() at end of frame."); return false; }
    if (c->pixel_width <= 0 || c->pixel_height <= 0) return true;
    gfx_canvas_bind(c);
    char error[1024];
    if (!gpu_device_present(p->device, c->target, error, sizeof error)) { gfx_fail(call, "%s", error); return false; }
    gfx_text_end_frame(p);
    return true;
}

/* ---- Event dispatch ----
   The frame's queued events run one callback at a time (a callback may wait for asynchronous results). A failing
   callback goes to the "error" handler when there is one; `dispatch_finished` gets NULL or the failure's message. */

static char *failure_text(processing *p, aqua_value result) {
    char *text = aqua_text_dup(p->rt, result, NULL);
    if (!text) text = aqua_display(p->rt, result);
    return text;
}

static void dispatch_continue(processing *p);

static void dispatch_end(processing *p, const char *failure) {
    gfx_input_clear_queue(p);
    p->dispatch_index = 0;
    p->dispatch_waiting = false;
    void (*finished)(processing *, const char *) = p->dispatch_finished;
    p->dispatch_finished = NULL;
    if (finished) finished(p, failure);
}

static void on_error_handled(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    processing *p = data;
    if (!ok) { char *m = failure_text(p, result); dispatch_end(p, m); aqua_free(m); return; }
    p->dispatch_waiting = false;
    dispatch_continue(p);
}

static void on_event_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    processing *p = data;
    if (ok) { p->dispatch_waiting = false; dispatch_continue(p); return; }
    char *message = failure_text(p, result);
    int event = p->failed_event;
    if (event != EVENT_ERROR && p->events[EVENT_ERROR] && p->window) {
        /* Finish interrupted offscreen drawing, then let the error handler recover. */
        for (canvas *c = p->canvases; c; c = c->next)
            if (c->drawing) { if (c->target) gpu_target_flush(c->target); c->drawing = false; c->shape_mode = -1; }
        gfx_canvas_bind(p->screen);
        snprintf(p->error_event, sizeof p->error_event, "%s", gfx_event_names[event]);
        free(p->error_message);
        size_t n = strlen(message);
        p->error_message = malloc(n + 1);
        memcpy(p->error_message, message, n + 1);
        aqua_free(message);
        aqua_invoke_async(p->rt, aqua_ref_value(p->rt, p->events[EVENT_ERROR]), NULL, 0, on_error_handled, p);
        return;
    }
    dispatch_end(p, message);
    aqua_free(message);
}

/* Runs callbacks until one waits; callbacks finishing inside their own invocation continue this loop. */
static void dispatch_continue(processing *p) {
    if (p->dispatching) return;
    p->dispatching = true;
    while (p->dispatch_finished && !p->dispatch_waiting) {
        if (p->dispatch_index >= p->queue_count || !p->window) {
            p->dispatching = false;
            dispatch_end(p, NULL);
            return;
        }
        sketch_event *e = &p->queue[p->dispatch_index++];
        gfx_input_apply(p, e);
        if (e->event == EVENT_APPLY || !p->events[e->event]) continue;
        p->failed_event = e->event;
        p->dispatch_waiting = true;
        aqua_invoke_async(p->rt, aqua_ref_value(p->rt, p->events[e->event]), NULL, 0, on_event_done, p);
    }
    p->dispatching = false;
}

static void dispatch_start(processing *p, void (*finished)(processing *p, const char *failure)) {
    p->dispatch_finished = finished;
    p->dispatch_index = 0;
    p->dispatch_waiting = false;
    dispatch_continue(p);
}

/* ---- Hand-driven frames ---- */

static void manual_events_finished(processing *p, const char *failure) {
    gfx_async *a = p->manual_call;
    p->manual_call = NULL;
    if (!a) return;
    if (failure) gfx_async_fail(a, "%s", failure);
    else gfx_async_finish(a, aqua_void());
}

static void p_beginFrame(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    if (p->dispatch_finished || p->manual_call) { gfx_fail(call, "beginFrame() cannot run while sketch events are dispatched."); return; }
    begin_frame(p);
    gfx_async *a = gfx_async_begin(call, 0);
    p->manual_call = a;
    dispatch_start(p, manual_events_finished);
    gfx_async_end(a);
}

#if defined(AQUA_OS_WEB)
static double frame_yield(aqua_runtime *rt, double now, void *data) {
    (void)rt; (void)now;
    gfx_async_finish(data, aqua_void());
    return -1;
}
#endif

static void p_endFrame(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p) || !end_frame(call, p)) return;
#if defined(AQUA_OS_WEB)
    /* A hand-driven loop gives the browser its animation frame (presentation happens when the task yields). */
    gfx_async *a = gfx_async_begin(call, 0);
    aqua_schedule(p->rt, 0, frame_yield, a);
    gfx_async_end(a);
#endif
}

/* ---- run() ---- */

enum { PHASE_IDLE, PHASE_WAIT, PHASE_DRAW, PHASE_AFTER_DRAW, PHASE_END_AND_FINISH, PHASE_FINISH };

static void pump(processing *p);
static double frame_tick(aqua_runtime *rt, double now, void *data);

static void fatal(processing *p, const char *message) {
    if (!p->failure) {
        size_t n = strlen(message) + 32;
        p->failure = malloc(n);
        snprintf(p->failure, n, "Processing.run: %s", message);
    }
    p->phase = PHASE_FINISH;
}

static void run_events_finished(processing *p, const char *failure) {
    if (failure) fatal(p, failure);
    else p->phase = p->exiting || !p->window ? PHASE_FINISH : PHASE_DRAW;
    pump(p);
}

static void on_draw_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    processing *p = data;
    if (ok) p->phase = PHASE_AFTER_DRAW;
    else { char *m = failure_text(p, result); fatal(p, m); aqua_free(m); }
    pump(p);
}

static void captured(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    processing *p = data;
    const char *path = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_CAPTURE");
    char message[1024];
    if (error) fatal(p, error);
    else if (path && *path && !gfx_image_write_file(path, width, height, rgba, message, sizeof message)) fatal(p, message);
    else p->phase = PHASE_END_AND_FINISH;
    pump(p);
}

/* AQUARIUS_GRAPHICS_RECORD=frames/####.png saves every drawn frame ('#'s become the zero-padded frame number), and
   millis() then advances exactly one frame period per frame, so recordings play back smoothly at frameRate(). */
static void recorded(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    processing *p = data;
    const char *pattern = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_RECORD");
    char path[1100], message[1024];
    const char *hash = strchr(pattern, '#');
    if (hash) {
        size_t count = 0;
        while (hash[count] == '#') count++;
        snprintf(path, sizeof path, "%.*s%0*d%s", (int)(hash - pattern), pattern, (int)count, p->frame_count, hash + count);
    } else snprintf(path, sizeof path, "%s", pattern);
    if (error) fatal(p, error);
    else if (!gfx_image_write_file(path, width, height, rgba, message, sizeof message)) fatal(p, message);
    else p->phase = PHASE_AFTER_DRAW;
    pump(p);
}

/* end_frame inside run(): a failure ends the run with its message. */
static bool end_frame_detached(processing *p) {
    char message[2048];
    gfx_take_detached(message, sizeof message);
    if (end_frame(NULL, p)) return true;
    if (!gfx_take_detached(message, sizeof message)) snprintf(message, sizeof message, "The operation failed.");
    fatal(p, message);
    return false;
}

static void schedule_next(processing *p, double delay) {
    p->phase = PHASE_IDLE;
    aqua_schedule(p->rt, delay > 0 ? delay : 0, frame_tick, p);
}

static void finish_run(processing *p) {
    gfx_async *a = p->run_call;
    p->run_call = NULL;
    p->running = false;
    p->phase = PHASE_IDLE;
    gfx_processing_close(p);
    if (p->setup) { aqua_release(p->rt, p->setup); p->setup = 0; }
    if (p->draw) { aqua_release(p->rt, p->draw); p->draw = 0; }
    char *failure = p->failure;
    p->failure = NULL;
    if (a) {
        if (failure) gfx_async_fail_text(a, failure);
        else gfx_async_finish(a, aqua_void());
    }
    free(failure);
}

static double frame_delay(processing *p) { return 1000 / p->target_fps - (aqua_now_ms(p->rt) - p->frame_start); }

/* One step of the frame's work after its events; false when it waits for a callback or the next tick. */
static bool step(processing *p) {
    switch (p->phase) {
    case PHASE_DRAW: {
        canvas *c = p->screen;
        if (c->pixel_width == 0 || c->pixel_height == 0) { schedule_next(p, 10); return false; }
        if (p->redraw_at >= 0 && aqua_now_ms(p->rt) >= p->redraw_at) { p->redraw_at = -1; p->redraw = true; }
        if (p->looping || p->redraw || p->frame_count == 0) {
            p->redraw = false;
            p->frame_count++;
            p->phase = PHASE_WAIT;
            aqua_invoke_async(p->rt, aqua_ref_value(p->rt, p->draw), NULL, 0, on_draw_done, p);
            return true;
        }
        schedule_next(p, frame_delay(p));
        return false;
    }
    case PHASE_AFTER_DRAW: {
        if (!p->window) { p->phase = PHASE_FINISH; return true; }
        const char *record = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_RECORD");
        if (record && *record && p->recorded_frame != p->frame_count) {
            p->recorded_frame = p->frame_count;
            p->phase = PHASE_WAIT;
            gfx_canvas_read_pixels(p->screen, recorded, p);
            return true;
        }
        if (p->frame_limit > 0 && p->frame_count >= p->frame_limit) {
            const char *capture = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_CAPTURE");
            if (capture && *capture) { p->phase = PHASE_WAIT; gfx_canvas_read_pixels(p->screen, captured, p); return true; }
            p->phase = PHASE_END_AND_FINISH;
            return true;
        }
        if (!end_frame_detached(p)) return true;
        if (p->exiting) { p->phase = PHASE_FINISH; return true; }
        schedule_next(p, frame_delay(p));
        return false;
    }
    case PHASE_END_AND_FINISH:
        if (p->window && !end_frame_detached(p)) return true;
        p->phase = PHASE_FINISH;
        return true;
    case PHASE_FINISH:
        finish_run(p);
        return false;
    default:
        return false;
    }
}

/* Runs steps until the frame waits; callbacks finishing during their own invocation continue the same loop. */
static void pump(processing *p) {
    if (p->pumping) { p->pump_again = true; return; }
    p->pumping = true;
    do {
        p->pump_again = false;
        while (p->phase != PHASE_WAIT && p->phase != PHASE_IDLE && step(p)) {}
    } while (p->pump_again);
    p->pumping = false;
}

static double frame_tick(aqua_runtime *rt, double now, void *data) {
    (void)rt;
    processing *p = data;
    if (!p->running || p->phase != PHASE_IDLE) return -1; /* the run ended, or a newer tick drives it */
    if (p->has_frame_rate && now > p->frame_start) p->actual_frame_rate = 1000 / (now - p->frame_start);
    p->has_frame_rate = true;
    p->frame_start = now;
    if (p->exiting || !p->window || aqua_window_should_close(p->window)) { p->phase = PHASE_FINISH; pump(p); return -1; }
    begin_frame(p);
    p->phase = PHASE_WAIT;
    dispatch_start(p, run_events_finished);
    return -1;
}

static void on_setup_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    processing *p = data;
    if (!ok) { char *m = failure_text(p, result); fatal(p, m); aqua_free(m); pump(p); return; }
    if (!p->window || !p->device) { fatal(p, "Call Processing.size() first."); pump(p); return; }
    schedule_next(p, 0);
}

static bool callback_argument(aqua_call *call, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    if (!aqua_is_callable(v)) { gfx_fail(call, "Expected a function callback."); return false; }
    int arity = aqua_function_arity(aqua_call_runtime(call), v);
    if (arity > 0) { gfx_fail(call, "Sketch callbacks must have zero parameters; read event fields from Processing."); return false; }
    return true;
}

static void p_run(aqua_call *call, processing *p) {
    if (!callback_argument(call, 0) || !callback_argument(call, 1)) return;
    if (p->running) { gfx_fail(call, "run() cannot be nested."); return; }
    const char *frames = aqua_environment(p->rt, "AQUARIUS_GRAPHICS_FRAMES");
    int limit = 0;
    if (frames && *frames) {
        char *end;
        long long n = strtoll(frames, &end, 10);
        while (*end == ' ' || *end == '\t') end++;
        if (*end || n < 0 || n > INT32_MAX) { gfx_fail(call, "AQUARIUS_GRAPHICS_FRAMES must be a nonnegative integer."); return; }
        limit = (int)n;
    }
    p->frame_limit = limit;
    p->running = true;
    p->exiting = false;
    p->frame_count = 0;
    p->recorded_frame = 0;
    p->redraw_at = -1;
    p->has_frame_rate = false;
    free(p->failure);
    p->failure = NULL;
    p->setup = aqua_retain(p->rt, aqua_arg(call, 0));
    p->draw = aqua_retain(p->rt, aqua_arg(call, 1));
    /* setup may finish (or fail) before aqua_invoke_async returns: the run then completes as a direct result. */
    gfx_async *a = gfx_async_begin(call, 0);
    p->run_call = a;
    p->phase = PHASE_WAIT;
    aqua_invoke_async(p->rt, aqua_ref_value(p->rt, p->setup), NULL, 0, on_setup_done, p);
    gfx_async_end(a);
}

/* ---- Loop control and events ---- */

static void p_on(aqua_call *call, processing *p) {
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    int event = -1;
    for (int i = 0; i < EVENT_COUNT; i++) if (strcmp(gfx_event_names[i], name.data) == 0) event = i;
    if (event < 0) { gfx_fail(call, "Unknown event name."); return; }
    if (!callback_argument(call, 1)) return;
    if (p->events[event]) aqua_release(p->rt, p->events[event]);
    p->events[event] = aqua_retain(p->rt, aqua_arg(call, 1));
}
static void p_noLoop(aqua_call *call, processing *p) { (void)call; p->looping = false; }
static void p_loop(aqua_call *call, processing *p) { (void)call; p->looping = true; }
static void p_redraw(aqua_call *call, processing *p) { (void)call; p->redraw = true; }
static void p_redrawAfter(aqua_call *call, processing *p) {
    aqua_value v = aqua_arg(call, 0);
    if (!aqua_is_number(v)) { gfx_fail(call, "redrawAfter expects milliseconds."); return; }
    double ms = v.number;
    if (!isfinite(ms) || ms < 0 || ms > 2147483647.0) { gfx_fail(call, "redrawAfter milliseconds must be 0..2147483647."); return; }
    double due = aqua_now_ms(p->rt) + ms;
    if (p->redraw_at < 0 || due < p->redraw_at) p->redraw_at = due;
}
static void p_exit(aqua_call *call, processing *p) { (void)call; p->exiting = true; }
static void p_close(aqua_call *call, processing *p) {
    (void)call;
    if (p->running) { p->exiting = true; return; } /* run() closes the sketch when its frame ends */
    gfx_processing_close(p);
}
static void p_frameRate(aqua_call *call, processing *p) {
    double fps = gfx_number(call, 0);
    if (aqua_failed(call)) return;
    if (fps <= 0 || fps > 1000) { gfx_fail(call, "Frame rate must be 0..1000 (exclusive zero)."); return; }
    p->target_fps = fps;
}
static void p_millis(aqua_call *call, processing *p) { aqua_return_double(call, clock_now(p)); }

/* ---- PGraphics ---- */

static void p_createGraphics(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    int w = gfx_dimension(call, 0), h = gfx_dimension(call, 1);
    bool is3d = false;
    if (aqua_argc(call) >= 3 && !renderer(call, 2, &is3d)) return;
    WGPUTextureFormat format = WGPUTextureFormat_RGBA8Unorm;
    if (aqua_argc(call) == 4) {
        aqua_str f = gfx_text(call, 3);
        if (aqua_failed(call)) return;
        if (strcmp(f.data, "rgba16float") == 0) format = WGPUTextureFormat_RGBA16Float;
        else if (strcmp(f.data, "rgba8unorm") != 0) { gfx_fail(call, "Render target format must be rgba8unorm or rgba16float."); return; }
    }
    if (aqua_failed(call)) return;
    canvas *c = gfx_canvas_new(p, w, h, is3d, true);
    c->color_format = format;
    if (!gfx_canvas_initialize(call, c, p->device)) { gfx_canvas_free(c); return; }
    gfx_canvas_bind(p->screen);
    aqua_return(call, aqua_object_new(p->rt, gfx_canvas_class, c));
}

static void p_beginDraw(aqua_call *call, processing *p, canvas *c) {
    (void)p;
    if (!gfx_canvas_live(call, c)) return;
    if (c->drawing) { gfx_fail(call, "beginDraw() is already active."); return; }
    c->drawing = true;
    c->model = gfx_m4_identity();
    gfx_canvas_bind(c);
}
static void p_endDraw(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c) || !gfx_canvas_require_drawing(call, c)) return;
    if (c->target) gpu_target_flush(c->target);
    c->drawing = false;
    gfx_canvas_bind(p->screen);
}
static void p_canvas_resize(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_require_sketch(call, p)) return;
    if (c->disposed) { gfx_fail(call, "Canvas is disposed."); return; }
    if (c->drawing) { gfx_fail(call, "Call resize() outside beginDraw()/endDraw()."); return; }
    uint32_t n = aqua_argc(call);
    if (n == 3) { gfx_fail(call, "resize() takes two or four dimensions."); return; }
    int w = gfx_dimension(call, 0), h = gfx_dimension(call, 1);
    int pw = n == 4 ? gfx_dimension(call, 2) : w, ph = n == 4 ? gfx_dimension(call, 3) : h;
    if (aqua_failed(call)) return;
    if ((int64_t)pw * ph > 16 * 1024 * 1024) { gfx_fail(call, "Canvas supports at most 16 million pixels."); return; }
    if (gfx_canvas_resize(c, w, h, pw, ph)) {
        gfx_canvas_bind(c);
        if (c->pixels) { aqua_release(p->rt, c->pixels); c->pixels = 0; }
    }
}

#define LIFECYCLE_MODULE(X) \
    X(size, "size|尺寸", 2, 4) X(fullScreen, "fullScreen|全螢幕", 0, 2) X(resize, "resize|調整尺寸", 2, 2) X(windowMove, "windowMove|移動視窗", 2, 2) \
    X(displayDensity, "displayDensity|顯示密度", 0, 0) X(displayScale, "displayScale|顯示縮放", 0, 0) X(pixelDensity, "pixelDensity|像素密度", 1, 1) \
    X(windowTitle, "windowTitle|視窗標題", 1, 1) X(beginFrame, "beginFrame|開始影格", 0, 0) \
    X(endFrame, "endFrame|結束影格", 0, 0) X(run, "run|執行", 2, 2) X(on, "on|註冊事件", 2, 2) X(noLoop, "noLoop|停止持續繪圖", 0, 0) \
    X(loop, "loop|持續繪圖", 0, 0) X(redraw, "redraw|重繪", 0, 0) X(redrawAfter, "redrawAfter|延遲重繪", 1, 1) X(exit, "exit|結束", 0, 0) \
    X(close, "close|關閉", 0, 0) X(frameRate, "frameRate|影格率", 1, 1) X(millis, "millis|毫秒", 0, 0) \
    X(createGraphics, "createGraphics|建立畫布", 2, 4)
LIFECYCLE_MODULE(MODULE_WRAPPER)

#define LIFECYCLE_CANVAS(X) X(beginDraw, "beginDraw|開始繪圖", 0, 0) X(endDraw, "endDraw|結束繪圖", 0, 0) X(canvas_resize, "resize|調整尺寸", 2, 4)
#define CANVAS_ONLY_WRAPPER(name, names, minimum, maximum) \
    static void object_##name(aqua_call *call) { gfx_enter(call, "Processing.", names); p_##name(call, gfx_processing_of(call), (canvas *)aqua_self(call)); }
#define CANVAS_ONLY_DEFINE(name, names, minimum, maximum) aqua_define_function(canvas, names, object_##name, gfx_signature(minimum, maximum));
LIFECYCLE_CANVAS(CANVAS_ONLY_WRAPPER)

void gfx_lifecycle_register(aqua_class *module, aqua_class *canvas) {
    LIFECYCLE_MODULE(MODULE_DEFINE)
    LIFECYCLE_CANVAS(CANVAS_ONLY_DEFINE)
}
