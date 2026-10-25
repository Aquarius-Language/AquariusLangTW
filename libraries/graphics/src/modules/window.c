/* Window: application integration for graphics windows, and conversions between Processing images and the shared
 * image objects (aqua/image.h) that Images, cameras and inference use.
 *
 * Window.Attach(window) / Window.Current() return the window's integration object (one per window): its events as
 * records (keys, pointer, wheel, focus, visibility, file drops, close requests) gathered between polls, title and
 * cursor control, pointer capture and close interception: a close request leaves the window open until
 * ResolveClose("accept"). A fileDrop event's `files` are the dropped paths (strings): scripts pass them to the
 * application library's Files.Resolve for file resources. On the web a page cannot hold its closing: ResolveClose
 * ("cancel") asks the browser to confirm leaving (beforeunload), "accept" stops asking. Failures of the integration read "<Member>: <Kind>: <message>" as the application
 * libraries' do. */
#include "../graphics.h"
#include "../common/records.h"
#include "../processing/processing.h"
#include "wgpu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define EVENT_LIMIT 4096

typedef struct integration {
    aqua_runtime *rt;
    aqua_window *window;      /* NULL once destroyed */
    window_state *owner;
    graphics *graphics;
    aqua_ref self;            /* the integration object, held while the window exists */
    aqua_ref *events; size_t count, capacity;
    aqua_ref drop;            /* the array of files of a drop in progress */
    bool overflow, close_pending, composing;
    int modifiers;
    struct integration *next;
} integration;
struct window_state { integration *list; };

static aqua_class *integration_class;

static window_state *state_of(aqua_call *call) {
    graphics *g = gfx_graphics_of(call);
    if (!g->windows) g->windows = calloc(1, sizeof *g->windows);
    return g->windows;
}

/* "<Member>: <Kind>: <message>". */
static void fail_kind(aqua_call *call, const char *kind, const char *message) { gfx_fail(call, "%s: %s", kind, message); }

/* ---- Events ---- */

static const char *const event_fields[] = {"type", "timestamp", "logicalKey", "physicalKey", "modifiers", "deviceId", "device",
                                           "button", "wheelX", "wheelY", "wheelUnit", "samples", "contact", "eraser", "files", "state"};
enum { F_TYPE, F_TIMESTAMP, F_LOGICAL, F_PHYSICAL, F_MODIFIERS, F_DEVICE_ID, F_DEVICE, F_BUTTON, F_WHEEL_X, F_WHEEL_Y, F_WHEEL_UNIT,
       F_SAMPLES, F_CONTACT, F_ERASER, F_FILES, F_STATE, F_COUNT };

typedef struct event_values { aqua_value v[F_COUNT]; } event_values;

static event_values event_start(aqua_runtime *rt, const char *type, double time) {
    event_values e;
    for (int i = 0; i < F_COUNT; i++) e.v[i] = aqua_null();
    e.v[F_TYPE] = aqua_cstring(rt, type);
    e.v[F_TIMESTAMP] = aqua_double(time);
    e.v[F_MODIFIERS] = aqua_int(0);
    return e;
}

static void push(integration *in, const event_values *e) {
    if (in->count >= EVENT_LIMIT) { in->overflow = true; return; }
    aqua_runtime *rt = in->rt;
    aqua_value hash = aqua_hash_new(rt, F_COUNT);
    for (int i = 0; i < F_COUNT; i++) aqua_hash_set_field(rt, hash, event_fields[i], e->v[i]);
    GFX_PUSH(in->events, in->count, in->capacity, aqua_retain(rt, gfx_record_new(rt, hash)));
}

static void mouse(event_values *e, const aqua_window_event *ev, aqua_runtime *rt) {
    const char *device = ev->pointer_type == 1 ? "touch" : ev->pointer_type == 2 ? "pen" : "mouse";
    e->v[F_DEVICE_ID] = aqua_int((int32_t)ev->pointer);
    e->v[F_DEVICE] = aqua_cstring(rt, device);
}

static aqua_value samples(aqua_runtime *rt, const aqua_window_event *ev) {
    aqua_value sample = aqua_hash_new(rt, 6);
    aqua_hash_set_field(rt, sample, "x", aqua_double(ev->x));
    aqua_hash_set_field(rt, sample, "y", aqua_double(ev->y));
    aqua_hash_set_field(rt, sample, "timestamp", aqua_double(ev->time));
    bool pen = ev->pointer_type == 2;
    aqua_hash_set_field(rt, sample, "pressure", pen || ev->pointer_type == 1 ? aqua_double(ev->pressure) : aqua_null());
    aqua_hash_set_field(rt, sample, "tiltX", pen ? aqua_double(ev->tilt_x) : aqua_null());
    aqua_hash_set_field(rt, sample, "tiltY", pen ? aqua_double(ev->tilt_y) : aqua_null());
    aqua_value array = aqua_array_new(rt, 1);
    aqua_array_set(rt, array, 0, sample);
    return array;
}

static const char *logical_key(int key, const char *text, char *buffer, size_t size) {
    switch (key) {
    case 256: return "Escape"; case 257: return "Enter"; case 258: return "Tab"; case 259: return "Backspace";
    case 261: return "Delete"; case 262: return "ArrowRight"; case 263: return "ArrowLeft"; case 264: return "ArrowDown";
    case 265: return "ArrowUp"; case 268: return "Home"; case 269: return "End"; case 32: return " ";
    }
    if (text) return text;
    snprintf(buffer, size, "key:%d", key);
    return buffer;
}

static void request_redraw(integration *in);

static void on_event(aqua_window *window, const aqua_window_event *ev, void *data) {
    integration *in = data;
    aqua_runtime *rt = in->rt;
    size_t before = in->count;
    event_values e;
    char buffer[32];
    switch (ev->type) {
    case AQUA_EVENT_KEY:
        in->modifiers = ev->mods;
        e = event_start(rt, ev->action == 0 ? "keyReleased" : ev->action == 2 ? "keyRepeat" : "keyPressed", ev->time);
        e.v[F_LOGICAL] = aqua_cstring(rt, logical_key(ev->key, ev->text, buffer, sizeof buffer));
        snprintf(buffer, sizeof buffer, "scan:%d", ev->scancode);
        e.v[F_PHYSICAL] = aqua_cstring(rt, buffer);
        e.v[F_MODIFIERS] = aqua_int(ev->mods);
        if (in->composing) e.v[F_STATE] = aqua_bool(true);
        push(in, &e);
        break;
    case AQUA_EVENT_POINTER:
        e = event_start(rt, "pointerMoved", ev->time);
        e.v[F_MODIFIERS] = aqua_int(in->modifiers);
        mouse(&e, ev, rt);
        e.v[F_SAMPLES] = samples(rt, ev);
        push(in, &e);
        break;
    case AQUA_EVENT_BUTTON:
        in->modifiers = ev->mods;
        e = event_start(rt, ev->action == 0 ? "pointerReleased" : "pointerPressed", ev->time);
        e.v[F_MODIFIERS] = aqua_int(ev->mods);
        mouse(&e, ev, rt);
        e.v[F_BUTTON] = aqua_int(ev->button);
        e.v[F_SAMPLES] = samples(rt, ev);
        e.v[F_CONTACT] = aqua_bool(ev->action != 0);
        push(in, &e);
        break;
    case AQUA_EVENT_SCROLL:
        e = event_start(rt, "wheel", ev->time);
        e.v[F_MODIFIERS] = aqua_int(in->modifiers);
        e.v[F_DEVICE_ID] = aqua_int(0);
        e.v[F_DEVICE] = aqua_cstring(rt, "mouse");
        e.v[F_WHEEL_X] = aqua_double(ev->x);
        e.v[F_WHEEL_Y] = aqua_double(-ev->y);
        e.v[F_WHEEL_UNIT] = aqua_cstring(rt, "line");
        push(in, &e);
        break;
    case AQUA_EVENT_FOCUS: {
        if (ev->action == 0) {
            in->modifiers = 0;
            event_values cancelled = event_start(rt, "inputCancelled", ev->time);
            push(in, &cancelled);
        }
        event_values activation = event_start(rt, "activation", ev->time);
        activation.v[F_STATE] = aqua_bool(ev->action != 0);
        push(in, &activation);
        e = event_start(rt, "focus", ev->time);
        e.v[F_STATE] = aqua_bool(ev->action != 0);
        push(in, &e);
        break;
    }
    case AQUA_EVENT_VISIBLE:
        e = event_start(rt, "visibility", ev->time);
        e.v[F_STATE] = aqua_bool(ev->action != 0);
        push(in, &e);
        break;
    case AQUA_EVENT_ENTER:
        e = event_start(rt, ev->action == 0 ? "pointerLeft" : "pointerEntered", ev->time);
        e.v[F_DEVICE_ID] = aqua_int(0);
        e.v[F_DEVICE] = aqua_cstring(rt, "mouse");
        push(in, &e);
        break;
    case AQUA_EVENT_DROP:
        if (ev->index == 0 || !in->drop) {
            if (in->drop) aqua_release(rt, in->drop);
            in->drop = aqua_retain(rt, aqua_array_new(rt, 0));
        }
        aqua_array_push(rt, aqua_ref_value(rt, in->drop), aqua_cstring(rt, ev->text ? ev->text : ""));
        if (ev->index == ev->count - 1) {
            e = event_start(rt, "fileDrop", ev->time);
            e.v[F_FILES] = aqua_ref_value(rt, in->drop);
            push(in, &e);
            aqua_release(rt, in->drop);
            in->drop = 0;
        }
        break;
    case AQUA_EVENT_CLOSE:
        /* The window stays open until the script resolves the request. */
        aqua_window_set_should_close(window, false);
        in->close_pending = true;
        e = event_start(rt, "closeRequested", ev->time);
        push(in, &e);
        break;
    case AQUA_EVENT_CAPTURE_LOST:
        e = event_start(rt, "captureLost", ev->time);
        e.v[F_DEVICE_ID] = aqua_int(0);
        push(in, &e);
        break;
    case AQUA_EVENT_DESTROYED:
        e = event_start(rt, "destroyed", ev->time);
        push(in, &e);
        in->window = NULL;
        if (in->self) { aqua_ref self = in->self; in->self = 0; aqua_release(rt, self); }
        break;
    case AQUA_EVENT_COMPOSITION_START: in->composing = true; break;
    case AQUA_EVENT_COMPOSITION_END: in->composing = false; break;
    default: break;
    }
    if (in->count != before || in->overflow) request_redraw(in);
}

/* A paused sketch draws once more so it can drain application events (an intercepted close stays pending otherwise). */
static void request_redraw(integration *in) {
    processing *p = in->graphics ? in->graphics->processing : NULL;
    if (p && in->window && p->window == in->window) p->redraw = true;
}

/* ---- The integration object ---- */

static void integration_finalize(aqua_runtime *rt, void *data) {
    integration *in = data;
    if (in->window) aqua_window_unlisten(in->window, on_event, in);
    for (size_t i = 0; i < in->count; i++) aqua_release(rt, in->events[i]);
    if (in->drop) aqua_release(rt, in->drop);
    if (in->owner)
        for (integration **p = &in->owner->list; *p; p = &(*p)->next) if (*p == in) { *p = in->next; break; }
    free(in->events);
    free(in);
}

/* Drops the object's hold on itself once its window is gone (it is then collected with the script's references). */
static void forget_closed(window_state *state) {
    for (integration *in = state->list; in; in = in->next)
        if (!in->window && in->self) { aqua_ref self = in->self; in->self = 0; aqua_release(in->rt, self); }
}

static aqua_value attach(aqua_call *call, aqua_window *window) {
    window_state *state = state_of(call);
    forget_closed(state);
    for (integration *in = state->list; in; in = in->next)
        if (in->window == window && in->self) return aqua_ref_value(in->rt, in->self);
    aqua_runtime *rt = aqua_call_runtime(call);
    integration *in = calloc(1, sizeof *in);
    in->rt = rt;
    in->window = window;
    in->owner = state;
    in->graphics = gfx_graphics_of(call);
    in->next = state->list;
    state->list = in;
    aqua_window_listen(window, on_event, in);
    aqua_value object = aqua_object_new(rt, integration_class, in);
    in->self = aqua_retain(rt, object);
    return object;
}

static integration *live(aqua_call *call) {
    integration *in = aqua_self(call);
    if (!in->window) { fail_kind(call, "InvalidData", "Expected a live GLFW window owned by this runtime."); return NULL; }
    return in;
}

static aqua_value capability(aqua_runtime *rt, bool available, const char *restriction) {
    aqua_value hash = aqua_hash_new(rt, 2);
    aqua_hash_set_field(rt, hash, "available", aqua_bool(available));
    aqua_hash_set_field(rt, hash, "restriction", restriction ? aqua_cstring(rt, restriction) : aqua_null());
    return hash;
}

static void window_Capabilities(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    static const char *const names[] = {"events", "fileDrop", "closeInterception", "customCursor", "capture", "touch", "pen"};
    aqua_value values[7];
#if defined(AQUA_OS_WEB)
    values[0] = capability(rt, true, NULL);
    values[1] = capability(rt, true, NULL);
    values[2] = capability(rt, false, "beforeunload only requests a browser confirmation; asynchronous defer is unavailable");
    values[3] = capability(rt, true, "Browser may impose cursor dimensions");
    values[4] = capability(rt, true, "An active pointer is required");
    values[5] = capability(rt, true, NULL);
    values[6] = capability(rt, true, "Optional properties are provided by PointerEvent");
#elif defined(AQUA_OS_ANDROID)
    values[0] = capability(rt, true, NULL);
    values[1] = capability(rt, false, "Android delivers files through activities.");
    values[2] = capability(rt, false, "Android closes activities itself.");
    values[3] = capability(rt, false, "Android has no pointer cursor.");
    values[4] = capability(rt, false, "Touches stay with their view.");
    values[5] = capability(rt, true, NULL);
    values[6] = capability(rt, true, "Optional properties are provided by MotionEvent");
#else
    values[0] = capability(rt, true, NULL);
    values[1] = capability(rt, true, NULL);
    values[2] = capability(rt, true, NULL);
    values[3] = capability(rt, true, NULL);
#if defined(AQUA_OS_WINDOWS)
    values[4] = capability(rt, true, "GLFW has no portable absolute pointer-capture API.");
#else
    values[4] = capability(rt, false, "GLFW has no portable absolute pointer-capture API.");
#endif
    values[5] = capability(rt, false, "GLFW supplies mouse/keyboard events.");
    values[6] = capability(rt, false, "GLFW does not expose pen properties.");
#endif
    aqua_return(call, gfx_record_of(rt, 7, names, values));
}

static void window_SetTitle(aqua_call *call) {
    integration *in = live(call);
    if (!in) return;
    if (aqua_arg(call, 0).type != AQUA_STRING) { fail_kind(call, "InvalidData", "Expected a string."); return; }
    aqua_window_set_title(in->window, aqua_arg_str(call, 0).data);
}

static const char *const cursor_names[] = {"Arrow", "Text", "Crosshair", "Hand", "ResizeHorizontal", "ResizeVertical",
                                           "ResizeDiagonal1", "ResizeDiagonal2", "Move", "NotAllowed"};

static bool same_ignoring_case(const char *a, const char *b) {
    for (; *a && *b; a++, b++) {
        char x = *a >= 'A' && *a <= 'Z' ? (char)(*a + 32) : *a, y = *b >= 'A' && *b <= 'Z' ? (char)(*b + 32) : *b;
        if (x != y) return false;
    }
    return *a == *b;
}

static void window_SetCursor(aqua_call *call) {
    integration *in = live(call);
    if (!in) return;
    if (aqua_arg(call, 0).type != AQUA_STRING) { fail_kind(call, "InvalidData", "Expected a string."); return; }
    const char *name = aqua_arg_str(call, 0).data;
    int shape = -1;
    for (int i = 0; i < 10; i++) if (same_ignoring_case(name, cursor_names[i])) shape = i;
    if (shape < 0) { fail_kind(call, "InvalidData", "Requested value is not a cursor shape."); return; }
    if (!aqua_window_set_cursor_shape(in->window, 0x36001 + shape)) fail_kind(call, "Unsupported", "Cursor unavailable on this window system.");
}

static bool integer_argument(aqua_call *call, uint32_t index, int32_t *out) {
    double n;
    if (!gfx_finite_number(aqua_arg(call, index), &n)) { fail_kind(call, "InvalidData", "Expected a finite number."); return false; }
    if (n != (double)(int64_t)n) { fail_kind(call, "InvalidData", "Expected an integer."); return false; }
    if (n < -2147483648.0 || n > 2147483647.0) { fail_kind(call, "InvalidData", "Arithmetic operation resulted in an overflow."); return false; }
    *out = (int32_t)n;
    return true;
}

static void window_SetCustomCursor(aqua_call *call) {
    integration *in = live(call);
    if (!in) return;
    aqua_image *image = aqua_image_of(aqua_call_runtime(call), aqua_arg(call, 0));
    if (!image) { fail_kind(call, "InvalidData", "Expected a resource owned by this application runtime."); return; }
    int32_t x, y;
    if (!integer_argument(call, 1, &x) || !integer_argument(call, 2, &y)) return;
    if (x < 0 || y < 0 || x >= image->width || y >= image->height) { fail_kind(call, "InvalidData", "Hotspot must be inside cursor image."); return; }
    size_t n = (size_t)image->width * (size_t)image->height * 4;
    uint8_t *rgba = malloc(n);
    memcpy(rgba, image->rgba, n);
    if (image->alpha == AQUA_ALPHA_PREMULTIPLIED)
        for (size_t i = 0; i < n; i += 4) {
            unsigned a = rgba[i + 3];
            if (a > 0 && a < 255) for (int c = 0; c < 3; c++) { unsigned v = (rgba[i + c] * 255u + a / 2) / a; rgba[i + c] = (uint8_t)(v > 255 ? 255 : v); }
        }
    bool ok = aqua_window_set_cursor_image(in->window, image->width, image->height, rgba, x, y);
    free(rgba);
    if (!ok) fail_kind(call, "Unsupported", "Custom cursor unavailable.");
}

static void capture(aqua_call *call, bool on) {
    integration *in = live(call);
    if (!in) return;
    int32_t device;
    if (!integer_argument(call, 0, &device)) return;
    if (device != 0) { fail_kind(call, "InvalidData", "GLFW mouse id is 0."); return; }
    if (!aqua_window_capture_pointer(in->window, on))
        fail_kind(call, on ? "Unsupported" : "Unavailable", on ? "Pointer capture unavailable." : "Pointer capture could not be released.");
}
static void window_CapturePointer(aqua_call *call) { capture(call, true); }
static void window_ReleasePointer(aqua_call *call) { capture(call, false); }

#if defined(AQUA_OS_WEB)
AQUA_WEB_IMPORT(canvas_close_guard) void web_close_guard(int32_t enabled);
#endif

static void window_ResolveClose(aqua_call *call) {
    integration *in = live(call);
    if (!in) return;
    if (aqua_arg(call, 0).type != AQUA_STRING) { fail_kind(call, "InvalidData", "Expected a string."); return; }
    const char *decision = aqua_arg_str(call, 0).data;
    int d = same_ignoring_case(decision, "accept") ? 0 : same_ignoring_case(decision, "defer") ? 1 : same_ignoring_case(decision, "cancel") ? 2 : -1;
    if (d < 0) { fail_kind(call, "InvalidData", "Requested value is not a close decision."); return; }
#if defined(AQUA_OS_WEB)
    if (d == 1) { fail_kind(call, "Unsupported", "Browser cannot defer page closure."); return; }
    web_close_guard(d == 2);
#else
    if (!in->close_pending) { fail_kind(call, "Conflict", "No pending close request."); return; }
    if (d == 1) return;
    in->close_pending = false;
    aqua_window_set_should_close(in->window, d == 0);
#endif
}

static void window_Poll(aqua_call *call) {
    integration *in = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    if (in->overflow) {
        in->overflow = false;
        for (size_t i = 0; i < in->count; i++) aqua_release(rt, in->events[i]);
        in->count = 0;
        fail_kind(call, "LimitExceeded", "Window event queue overflow; poll more often.");
        return;
    }
    aqua_value array = aqua_array_new(rt, (uint32_t)in->count);
    for (size_t i = 0; i < in->count; i++) {
        aqua_array_set(rt, array, (uint32_t)i, aqua_ref_value(rt, in->events[i]));
        aqua_release(rt, in->events[i]);
    }
    in->count = 0;
    aqua_return(call, array);
}

/* ---- Window module ---- */

static void window_Attach(aqua_call *call) {
    aqua_window *w = gfx_wgpu_window_of(aqua_call_runtime(call), aqua_arg(call, 0));
    if (!w) { gfx_fail(call, "Expected a live GLFW window owned by this runtime."); return; }
    aqua_return(call, attach(call, w));
}

static void window_Current(aqua_call *call) {
    graphics *g = gfx_graphics_of(call);
    if (!g->processing || !g->processing->window) { gfx_fail(call, "Create a Processing window first."); return; }
    aqua_return(call, attach(call, g->processing->window));
}

static void converted(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    if (error) { gfx_async_fail(a, "%s", error); return; }
    gfx_async_finish(a, aqua_image_new(gfx_async_runtime(a), width, height, rgba, AQUA_ALPHA_STRAIGHT));
}

static void window_FromProcessingImage(aqua_call *call) {
    processing *p = gfx_processing_of(call);
    pimage *image = gfx_image_argument(call, p, 0);
    if (!image) return;
    gfx_async *a = gfx_async_begin(call, 0);
    gfx_image_pixels(image, converted, a);
    gfx_async_end(a);
}

static void window_ToProcessingImage(aqua_call *call) {
    processing *p = gfx_processing_of(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_image *source = aqua_image_of(rt, aqua_arg(call, 0));
    if (!source) { gfx_fail(call, "Expected a resource owned by this application runtime."); return; }
    pimage *target = NULL;
    if (aqua_argc(call) == 2) {
        target = gfx_image_argument(call, p, 1);
        if (!target) return;
        if (target->canvas || target->width != source->width || target->height != source->height) {
            gfx_fail(call, "The reusable Processing image must be a regular image with matching dimensions.");
            return;
        }
    } else {
        target = gfx_image_new(p, source->width, source->height);
        if (!target) { gfx_fail(call, "Image dimensions must be 1..8192 with at most 16 million pixels."); return; }
    }
    size_t n = (size_t)source->width * (size_t)source->height * 4;
    memcpy(target->bytes, source->rgba, n);
    if (source->alpha == AQUA_ALPHA_PREMULTIPLIED)
        for (size_t i = 0; i < n; i += 4) {
            unsigned alpha = target->bytes[i + 3];
            if (alpha > 0 && alpha < 255)
                for (int c = 0; c < 3; c++) { unsigned v = (target->bytes[i + c] * 255u + alpha / 2) / alpha; target->bytes[i + c] = (uint8_t)(v > 255 ? 255 : v); }
        }
    target->dirty = true;
    aqua_return(call, aqua_argc(call) == 2 ? aqua_arg(call, 1) : gfx_image_object(rt, target));
}

void gfx_window_destroy(window_state *state) {
    if (!state) return;
    for (integration *in = state->list; in; in = in->next) {
        if (in->window) aqua_window_unlisten(in->window, on_event, in);
        in->window = NULL;
        in->owner = NULL;
    }
    free(state);
}

#define WINDOW_MODULE(X) \
    X("", window_Attach, "Attach|附加", 1, 1) X("", window_Current, "Current|目前視窗", 0, 0) \
    X("", window_FromProcessingImage, "FromProcessingImage|從Processing圖片", 1, 1) \
    X("Processing.", window_ToProcessingImage, "ToProcessingImage|轉為Processing圖片", 1, 2)
#define INTEGRATION_MEMBERS(X) \
    X("", window_Capabilities, "Capabilities|功能支援", 0, 0) X("", window_SetTitle, "SetTitle|設定標題", 1, 1) \
    X("", window_SetCursor, "SetCursor|設定游標", 1, 1) X("", window_SetCustomCursor, "SetCustomCursor|設定自訂游標", 3, 3) \
    X("", window_CapturePointer, "CapturePointer|擷取指標", 1, 1) X("", window_ReleasePointer, "ReleasePointer|釋放指標", 1, 1) \
    X("", window_ResolveClose, "ResolveClose|決定關閉", 1, 1) X("", window_Poll, "Poll|輪詢", 0, 0)
#define WRAPPER(prefix, function, names, minimum, maximum) GFX_WRAP(prefix, function, names, function)
WINDOW_MODULE(WRAPPER)
INTEGRATION_MEMBERS(WRAPPER)
#undef WRAPPER

void gfx_window_register(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "Window");
    integration_class = aqua_define_class(library, "Window.Integration", integration_finalize);
#define ON_MODULE(prefix, function, names, minimum, maximum) aqua_define_function(module, names, wrapper_##function, gfx_signature(minimum, maximum));
#define ON_INTEGRATION(prefix, function, names, minimum, maximum) aqua_define_function(integration_class, names, wrapper_##function, gfx_signature(minimum, maximum));
    WINDOW_MODULE(ON_MODULE)
    INTEGRATION_MEMBERS(ON_INTEGRATION)
#undef ON_MODULE
#undef ON_INTEGRATION
}
