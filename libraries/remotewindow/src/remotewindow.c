/* RemoteWindow / 遠端視窗: the remote window protocol (aqua/remote_window.h) for scripts, on every target.
 *
 *   Viewer  shows the windows of an application started with AQUARIUS_REMOTE_WINDOW: it decodes what the application
 *           sends into frames (an image, or a UI draw list that updates only the regions that changed) and encodes the
 *           viewer's input (raw window events, or Interaction's events as UI libraries pass them on) back.
 *   Relay   sits between the two (a notebook server): it forwards whole messages each way, reports the windows opening
 *           and closing, and gives a viewer that joins later the windows' state.
 *   Sender  the application side for scripts: any program can show images on a viewer (and tests use it).
 *
 * The wire format and its encoders and decoders are the header's, shared with the desktop runtime's sender
 * (runtime/platform/desktop/window_remote.c), so the two ends cannot drift apart. Everything here is plain C on bytes
 * the script moves over whatever transport it has (TCP, a WebSocket in a browser). */
#include <aqua/aqua.h>
#include <aqua/image.h>
#include <aqua/remote_window.h>
#include <aqua/window.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static aqua_class *viewer_class, *relay_class, *sender_class;

/* The renderers' texture ids this library's draw lists use (ImGui's start at 1 << 20; floats hold these exactly). */
static int32_t next_texture = 6 << 20;

/* Window event types by name: aqua/window.h's events as Interaction.Feed and Sender.Feed spell them. */
static const struct { const char *name; int32_t type; } event_names[] = {
    {"key", AQUA_EVENT_KEY}, {"pointer", AQUA_EVENT_POINTER}, {"button", AQUA_EVENT_BUTTON}, {"scroll", AQUA_EVENT_SCROLL},
    {"focus", AQUA_EVENT_FOCUS}, {"visible", AQUA_EVENT_VISIBLE}, {"enter", AQUA_EVENT_ENTER}, {"drop", AQUA_EVENT_DROP},
    {"close", AQUA_EVENT_CLOSE}, {"captureLost", AQUA_EVENT_CAPTURE_LOST}, {"text", AQUA_EVENT_TEXT},
    {"composition", AQUA_EVENT_COMPOSITION_UPDATE}, {"resize", AQUA_EVENT_RESIZE}, {"back", AQUA_EVENT_BACK},
    {"lifecycle", AQUA_EVENT_LIFECYCLE}};
/* GLFW's standard cursor shapes (0x36001 + index) by Interaction's names. */
static const char *const cursor_names[] = {"arrow", "text", "crosshair", "hand", "resizeHorizontal", "resizeVertical",
                                           "resizeDiagonal", "resizeAntiDiagonal", "move", "notAllowed"};

/* ---- Bytes in and out ---- */

/* A growing buffer of bytes received and not yet parsed. */
typedef struct inbox { uint8_t *data; size_t length, capacity; } inbox;

static bool inbox_add(aqua_call *call, inbox *in, aqua_value bytes, const char *function) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t n = aqua_array_length(rt, bytes);
    if (in->length + n > in->capacity) {
        size_t capacity = in->capacity ? in->capacity : 65536;
        while (capacity < in->length + n) capacity *= 2;
        uint8_t *grown = realloc(in->data, capacity);
        if (!grown) { aqua_fail(call, "RemoteWindow.%s: Out of memory.", function); return false; }
        in->data = grown; in->capacity = capacity;
    }
    if (!aqua_array_read_u8(rt, bytes, 0, n, in->data + in->length)) {
        aqua_fail(call, "RemoteWindow.%s: Expected bytes (integers 0..255).", function);
        return false;
    }
    in->length += n;
    return true;
}
static void inbox_consume(inbox *in, size_t used) {
    memmove(in->data, in->data + used, in->length - used);
    in->length -= used;
}

/* The bytes of a buffer as a script byte array, emptying it. */
static aqua_value take_bytes(aqua_runtime *rt, aqua_remote_buffer *b) {
    aqua_value bytes = aqua_array_from_u8(rt, b->data, (uint32_t)b->length);
    b->length = 0;
    return bytes;
}

static void set_int(aqua_runtime *rt, aqua_value hash, const char *name, double n) { aqua_hash_set_field(rt, hash, name, aqua_int((int32_t)n)); }
static void set_string(aqua_runtime *rt, aqua_value hash, const char *name, const char *text) {
    aqua_hash_set_field(rt, hash, name, text ? aqua_cstring(rt, text) : aqua_null());
}
static aqua_value record(aqua_runtime *rt, const char *type, uint32_t window) {
    aqua_value r = aqua_hash_new(rt, 8);
    set_string(rt, r, "type", type);
    if (window) set_int(rt, r, "window", window);
    return r;
}
static const char *cursor_name(int32_t shape) {
    if (shape < 0) return "hidden";
    if (shape >= 0x36001 && shape < 0x36001 + 10) return cursor_names[shape - 0x36001];
    return "arrow";
}

/* ---- Window state both the viewer and the relay keep ---- */

typedef struct rw_window {
    uint32_t id, flags;
    int32_t width, height;           /* logical units */
    float scale;
    char *title;
    int32_t cursor;                  /* GLFW shape, 0 default, -1 hidden */
    bool text_enabled, closed;
    int32_t text[4];
    /* Viewer only: the frame, the frames shown, the renderer texture and the tiles changed since it was painted. */
    uint8_t *frame;
    int32_t frame_width, frame_height;
    uint32_t number, acknowledged;
    int32_t texture, texture_width, texture_height;
    uint8_t *dirty;
    double painted_width, painted_height;
    struct rw_window *next;
} rw_window;

static rw_window *find_window(rw_window *list, uint32_t id) {
    for (rw_window *w = list; w; w = w->next) if (w->id == id) return w;
    return NULL;
}
static void free_windows(rw_window *w) {
    while (w) {
        rw_window *next = w->next;
        free(w->title); free(w->frame); free(w->dirty); free(w);
        w = next;
    }
}

/* Applies one application message (anything but a frame's pixels) to the windows and returns its record, or a null
   value for messages scripts need not hear of. *error is set for a malformed or unknown-version stream. */
static aqua_value apply_message(aqua_runtime *rt, rw_window **windows, uint8_t type, aqua_remote_reader *m, const char *token,
                                const char **error) {
    if (type == AQUA_REMOTE_HELLO) {
        uint32_t magic = aqua_remote_read_u32(m);
        uint16_t version = aqua_remote_read_u16(m);
        char *given = aqua_remote_read_string(m);
        if (m->failed || magic != AQUA_REMOTE_WINDOW_MAGIC) *error = "The stream is not a remote window stream.";
        else if (version != AQUA_REMOTE_WINDOW_VERSION) *error = "The application speaks another version of the remote window protocol.";
        else if (token && strcmp(token, given) != 0) *error = "The application's token does not match.";
        aqua_value r = record(rt, "hello", 0);
        set_int(rt, r, "version", version);
        set_string(rt, r, "token", given);
        free(given);
        return r;
    }
    if (type == AQUA_REMOTE_CLIPBOARD) {
        char *text = aqua_remote_read_string(m);
        aqua_value r = record(rt, "clipboard", 0);
        set_string(rt, r, "text", text);
        free(text);
        if (m->failed) *error = "The stream is malformed.";
        return r;
    }
    uint32_t id = aqua_remote_read_u32(m);
    rw_window *w = find_window(*windows, id);
    aqua_value r = aqua_null();
    switch (type) {
    case AQUA_REMOTE_OPEN: {
        int32_t width = aqua_remote_read_i32(m), height = aqua_remote_read_i32(m);
        uint32_t flags = aqua_remote_read_u32(m);
        char *title = aqua_remote_read_string(m);
        if (m->failed || w || width <= 0 || height <= 0) { free(title); break; }
        w = calloc(1, sizeof *w);
        if (!w) { free(title); *error = "Out of memory."; return r; }
        w->id = id; w->width = width; w->height = height; w->flags = flags; w->title = title; w->scale = 1;
        w->next = *windows;
        *windows = w;
        r = record(rt, "open", id);
        set_int(rt, r, "width", width); set_int(rt, r, "height", height); set_int(rt, r, "flags", flags);
        set_string(rt, r, "title", title);
        break;
    }
    case AQUA_REMOTE_CLOSE:
        if (!w || w->closed) break;
        w->closed = true;
        r = record(rt, "close", id);
        break;
    case AQUA_REMOTE_TITLE: {
        char *title = aqua_remote_read_string(m);
        if (!w || m->failed) { free(title); break; }
        free(w->title);
        w->title = title;
        r = record(rt, "title", id);
        set_string(rt, r, "title", title);
        break;
    }
    case AQUA_REMOTE_SIZE: {
        int32_t width = aqua_remote_read_i32(m), height = aqua_remote_read_i32(m);
        if (!w || m->failed || width <= 0 || height <= 0) break;
        w->width = width; w->height = height;
        r = record(rt, "size", id);
        set_int(rt, r, "width", width); set_int(rt, r, "height", height);
        break;
    }
    case AQUA_REMOTE_CURSOR: {
        int32_t shape = aqua_remote_read_i32(m);
        if (!w || m->failed) break;
        w->cursor = shape;
        r = record(rt, "cursor", id);
        set_string(rt, r, "cursor", cursor_name(shape));
        break;
    }
    case AQUA_REMOTE_TEXT: {
        bool enabled = aqua_remote_read_u8(m) != 0;
        int32_t rect[4];
        for (int i = 0; i < 4; i++) rect[i] = aqua_remote_read_i32(m);
        if (!w || m->failed) break;
        w->text_enabled = enabled;
        memcpy(w->text, rect, sizeof rect);
        r = record(rt, "text", id);
        aqua_hash_set_field(rt, r, "enabled", aqua_bool(enabled));
        set_int(rt, r, "x", rect[0]); set_int(rt, r, "y", rect[1]); set_int(rt, r, "width", rect[2]); set_int(rt, r, "height", rect[3]);
        break;
    }
    default: break;   /* a newer application's message this library does not know */
    }
    if (m->failed) *error = "The stream is malformed.";
    return r;
}

static void append_record(aqua_runtime *rt, aqua_value list, aqua_value r) { if (r.type != AQUA_NULL) aqua_array_push(rt, list, r); }

/* ---- Viewer ---- */

typedef struct viewer {
    inbox in;
    aqua_remote_buffer out;
    char *token;                      /* the token HELLO must carry, or NULL */
    bool greeted, broken;
    rw_window *windows;
} viewer;

static void viewer_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    viewer *v = data;
    free(v->in.data);
    aqua_remote_free(&v->out);
    free(v->token);
    free_windows(v->windows);
    free(v);
}

static viewer *live_viewer(aqua_call *call, const char *function) {
    viewer *v = aqua_self(call);
    if (v->broken) { aqua_fail(call, "RemoteWindow.Viewer.%s: The stream failed earlier; open a new viewer.", function); return NULL; }
    return v;
}

static rw_window *window_argument(aqua_call *call, viewer *v, const char *function) {
    rw_window *w = find_window(v->windows, (uint32_t)aqua_arg_int(call, 0));
    if (!w) aqua_fail(call, "RemoteWindow.Viewer.%s: No window %d.", function, aqua_arg_int(call, 0));
    return w;
}

/* A FRAME: its tiles into the window's frame, marking them changed. */
static bool decode_frame(aqua_runtime *rt, viewer *v, aqua_remote_reader *m, aqua_value *out) {
    uint32_t id = aqua_remote_read_u32(m), number = aqua_remote_read_u32(m);
    int32_t width = aqua_remote_read_i32(m), height = aqua_remote_read_i32(m);
    float scale = aqua_remote_read_f32(m);
    uint32_t tiles = aqua_remote_read_u32(m);
    if (m->failed || width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
    rw_window *w = find_window(v->windows, id);
    if (!w || w->closed) return true;
    const int32_t T = AQUA_REMOTE_WINDOW_TILE;
    int32_t across = (width + T - 1) / T, down = (height + T - 1) / T;
    if (!w->frame || w->frame_width != width || w->frame_height != height) {
        uint8_t *frame = calloc((size_t)width * (size_t)height, 4), *dirty = calloc((size_t)across * (size_t)down, 1);
        if (!frame || !dirty) { free(frame); free(dirty); return false; }
        free(w->frame); free(w->dirty);
        w->frame = frame; w->dirty = dirty;
        w->frame_width = width; w->frame_height = height;
    }
    for (uint32_t i = 0; i < tiles; i++) {
        int32_t x, y, tw, th;
        if (!aqua_remote_read_tile(m, w->frame, width, height, &x, &y, &tw, &th)) return false;
        for (int32_t ty = y / T; ty <= (y + th - 1) / T; ty++)
            for (int32_t tx = x / T; tx <= (x + tw - 1) / T; tx++) w->dirty[ty * across + tx] = 1;
    }
    w->number = number;
    if (scale > 0) w->scale = scale;
    *out = record(rt, "frame", id);
    set_int(rt, *out, "number", number); set_int(rt, *out, "width", width); set_int(rt, *out, "height", height);
    aqua_hash_set_field(rt, *out, "scale", aqua_double(w->scale));
    return true;
}

static void viewer_feed(aqua_call *call) {
    viewer *v = live_viewer(call, "Feed");
    if (!v || !inbox_add(call, &v->in, aqua_arg(call, 0), "Viewer.Feed")) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    size_t used = 0;
    const char *error = NULL;
    for (;;) {
        uint8_t type;
        aqua_remote_reader m;
        long n = aqua_remote_next(v->in.data + used, v->in.length - used, &type, &m);
        if (n < 0) { error = "The stream is malformed."; break; }
        if (n == 0) break;
        used += (size_t)n;
        if (!v->greeted && type != AQUA_REMOTE_HELLO) { error = "The stream does not start with HELLO."; break; }
        if (type == AQUA_REMOTE_HELLO) v->greeted = true;
        aqua_value r = aqua_null();
        if (type == AQUA_REMOTE_FRAME) { if (!decode_frame(rt, v, &m, &r)) error = "A frame is malformed."; }
        else r = apply_message(rt, &v->windows, type, &m, v->token, &error);
        if (error) break;
        append_record(rt, list, r);
    }
    inbox_consume(&v->in, used);
    if (error) { v->broken = true; aqua_fail(call, "RemoteWindow.Viewer.Feed: %s", error); return; }
    aqua_return(call, list);
}

static void viewer_outgoing(aqua_call *call) { aqua_return(call, take_bytes(aqua_call_runtime(call), &((viewer *)aqua_self(call))->out)); }

static aqua_value window_record(aqua_runtime *rt, const rw_window *w) {
    aqua_value r = aqua_hash_new(rt, 16);
    set_int(rt, r, "window", w->id);
    set_string(rt, r, "title", w->title);
    set_int(rt, r, "width", w->width); set_int(rt, r, "height", w->height);
    aqua_hash_set_field(rt, r, "scale", aqua_double(w->scale));
    set_int(rt, r, "flags", w->flags);
    set_string(rt, r, "cursor", cursor_name(w->cursor));
    aqua_hash_set_field(rt, r, "textInput", aqua_bool(w->text_enabled));
    set_int(rt, r, "frameWidth", w->frame ? w->frame_width : 0); set_int(rt, r, "frameHeight", w->frame ? w->frame_height : 0);
    set_int(rt, r, "frame", w->number);
    aqua_hash_set_field(rt, r, "closed", aqua_bool(w->closed));
    return r;
}

/* The windows oldest first (the list keeps the newest first). */
static void viewer_windows(aqua_call *call) {
    viewer *v = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t count = 0;
    for (rw_window *w = v->windows; w; w = w->next) count++;
    aqua_value list = aqua_array_new(rt, count);
    uint32_t i = count;
    for (rw_window *w = v->windows; w; w = w->next) aqua_array_set(rt, list, --i, window_record(rt, w));
    aqua_return(call, list);
}
static void viewer_window(aqua_call *call) {
    viewer *v = aqua_self(call);
    rw_window *w = find_window(v->windows, (uint32_t)aqua_arg_int(call, 0));
    if (w) aqua_return(call, window_record(aqua_call_runtime(call), w));
    else aqua_return_null(call);
}

/* The frame was shown: acknowledge it, so the application sends the next one. */
static void acknowledge(viewer *v, rw_window *w) {
    if (w->closed || w->number == w->acknowledged) return;
    w->acknowledged = w->number;
    size_t at = aqua_remote_begin(&v->out, AQUA_REMOTE_ACK);
    aqua_remote_u32(&v->out, w->id);
    aqua_remote_u32(&v->out, w->number);
    aqua_remote_end(&v->out, at);
}

static void viewer_image(aqua_call *call) {
    viewer *v = aqua_self(call);
    rw_window *w = window_argument(call, v, "Image");
    if (!w) return;
    if (!w->frame) { aqua_return_null(call); return; }
    acknowledge(v, w);
    aqua_return(call, aqua_image_new(aqua_call_runtime(call), w->frame_width, w->frame_height, w->frame, AQUA_ALPHA_OPAQUE));
}

/* ---- Painting: a UI draw list (libraries/saturn/README.md) ---- */

typedef struct numbers { double *data; size_t length, capacity; } numbers;
static void push(numbers *n, const double *values, size_t count) {
    if (n->length + count > n->capacity) {
        size_t capacity = n->capacity ? n->capacity * 2 : 64;
        while (capacity < n->length + count) capacity *= 2;
        double *grown = realloc(n->data, capacity * sizeof *grown);
        if (!grown) return;
        n->data = grown; n->capacity = capacity;
    }
    memcpy(n->data + n->length, values, count * sizeof *values);
    n->length += count;
}

/* The RGBA bytes of a region of the frame, as a script byte array. */
static aqua_value region_bytes(aqua_runtime *rt, const rw_window *w, int32_t x, int32_t y, int32_t width, int32_t height) {
    size_t row = (size_t)width * 4;
    uint8_t *bytes = malloc(row * (size_t)height);
    if (!bytes) return aqua_array_new(rt, 0);
    for (int32_t i = 0; i < height; i++) memcpy(bytes + (size_t)i * row, w->frame + (((size_t)(y + i) * (size_t)w->frame_width) + (size_t)x) * 4, row);
    aqua_value v = aqua_array_from_u8(rt, bytes, (uint32_t)(row * (size_t)height));
    free(bytes);
    return v;
}

static void texture_op(aqua_runtime *rt, numbers *commands, aqua_value images, const rw_window *w, int32_t x, int32_t y, int32_t width, int32_t height) {
    uint32_t index = aqua_array_length(rt, images);
    aqua_array_push(rt, images, region_bytes(rt, w, x, y, width, height));
    double op[] = {11, w->texture, w->frame_width, w->frame_height, index, x, y, width, height};
    push(commands, op, 9);
}

/* Paint(window, x, y, width, height): the window's frame drawn in that rectangle, as a draw list for any UI renderer.
   The renderer keeps the frame as a texture: the first list carries all of it, later ones only the regions that
   changed (rows of changed tiles). */
static void viewer_paint(aqua_call *call) {
    viewer *v = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    rw_window *w = window_argument(call, v, "Paint");
    if (!w) return;
    double x = aqua_arg_number(call, 1), y = aqua_arg_number(call, 2), width = aqua_arg_number(call, 3), height = aqua_arg_number(call, 4);
    numbers commands = {0}, vertices = {0};
    aqua_value images = aqua_array_new(rt, 0);
    int32_t indices[6] = {0, 1, 2, 0, 2, 3};
    bool drawn = false;
    if (w->frame && width > 0 && height > 0) {
        const int32_t T = AQUA_REMOTE_WINDOW_TILE;
        int32_t across = (w->frame_width + T - 1) / T, down = (w->frame_height + T - 1) / T;
        if (!w->texture) w->texture = next_texture++;
        if (w->texture_width != w->frame_width || w->texture_height != w->frame_height) {
            texture_op(rt, &commands, images, w, 0, 0, w->frame_width, w->frame_height);
            w->texture_width = w->frame_width; w->texture_height = w->frame_height;
        } else
            for (int32_t ty = 0; ty < down; ty++)
                for (int32_t tx = 0; tx < across; tx++) {
                    if (!w->dirty[ty * across + tx]) continue;
                    int32_t end = tx;
                    while (end + 1 < across && w->dirty[ty * across + end + 1]) end++;
                    int32_t rx = tx * T, ry = ty * T;
                    int32_t rw = ((end + 1) * T < w->frame_width ? (end + 1) * T : w->frame_width) - rx;
                    int32_t rh = ((ty + 1) * T < w->frame_height ? (ty + 1) * T : w->frame_height) - ry;
                    texture_op(rt, &commands, images, w, rx, ry, rw, rh);
                    tx = end;
                }
        memset(w->dirty, 0, (size_t)across * (size_t)down);
        double quad[] = {x, y, 0, 0, 255, 255, 255, 255, x + width, y, 1, 0, 255, 255, 255, 255,
                         x + width, y + height, 1, 1, 255, 255, 255, 255, x, y + height, 0, 1, 255, 255, 255, 255};
        push(&vertices, quad, 32);
        double mesh[] = {10, 0, 4, 0, 6, w->texture};
        push(&commands, mesh, 6);
        drawn = true;
        w->painted_width = width; w->painted_height = height;
        acknowledge(v, w);
    }
    aqua_value list = aqua_hash_new(rt, 6);
    set_int(rt, list, "version", 1);
    aqua_hash_set_field(rt, list, "commands", aqua_array_from_f64(rt, commands.data, (uint32_t)commands.length, AQUA_FLOAT));
    aqua_hash_set_field(rt, list, "strings", aqua_array_new(rt, 0));
    aqua_hash_set_field(rt, list, "images", images);
    aqua_hash_set_field(rt, list, "vertices", aqua_array_from_f64(rt, vertices.data, (uint32_t)vertices.length, AQUA_FLOAT));
    aqua_hash_set_field(rt, list, "indices", aqua_array_from_i32(rt, indices, drawn ? 6 : 0));
    free(commands.data);
    free(vertices.data);
    aqua_return(call, list);
}

/* ---- Input: events to the application ---- */

static void send_event(viewer *v, uint32_t window, const aqua_window_event *e) {
    size_t at = aqua_remote_begin(&v->out, AQUA_REMOTE_EVENT);
    aqua_remote_u32(&v->out, window);
    aqua_remote_i32(&v->out, e->type);
    aqua_remote_i32(&v->out, e->key); aqua_remote_i32(&v->out, e->scancode); aqua_remote_i32(&v->out, e->action);
    aqua_remote_i32(&v->out, e->mods); aqua_remote_i32(&v->out, e->button);
    aqua_remote_f64(&v->out, e->x); aqua_remote_f64(&v->out, e->y);
    aqua_remote_u32(&v->out, e->pointer); aqua_remote_i32(&v->out, e->pointer_type);
    aqua_remote_f32(&v->out, e->pressure); aqua_remote_f32(&v->out, e->tilt_x); aqua_remote_f32(&v->out, e->tilt_y);
    aqua_remote_i32(&v->out, e->cursor); aqua_remote_i32(&v->out, e->index); aqua_remote_i32(&v->out, e->count);
    aqua_remote_string(&v->out, e->text);
    aqua_remote_end(&v->out, at);
}

static bool field_number(aqua_runtime *rt, aqua_value hash, const char *name, double *out) {
    aqua_value value;
    if (!aqua_hash_field(rt, hash, name, &value) || !aqua_is_numeric(value)) return false;
    *out = aqua_number_of(value);
    return true;
}
static const char *field_string(aqua_call *call, aqua_value hash, const char *name) {
    aqua_value value;
    if (!aqua_hash_field(aqua_call_runtime(call), hash, name, &value) || value.type != AQUA_STRING) return NULL;
    return aqua_str_of(call, value).data;
}
static int32_t pointer_type_of(const char *name) {
    return !name ? 0 : strcmp(name, "touch") == 0 ? 1 : strcmp(name, "pen") == 0 ? 2 : 0;
}

/* Send(window, event): one event for the window, as a hash in either shape:
   - raw, as aqua/window.h has it (what Interaction.Feed takes and Sender.Feed gives): "type" pointer | button | scroll |
     key | text | composition | focus | enter | close | ..., numeric "action", "mods", "x", "y", ...;
   - Interaction's (what a UI library passes on, SaturnUI's Immediate widgets): press, release, move, drag, dragStart,
     dragEnd, scroll and zoom (with the event's conventions), key (a string "action"), text, composition, focus.
   x and y are relative to the rectangle the window was last painted in, so they scale to the window's own units. Events
   Interaction derives (click, longPress, command, ...) are not input and are left out: false. */
static void viewer_send(aqua_call *call) {
    viewer *v = live_viewer(call, "Send");
    if (!v) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    rw_window *w = window_argument(call, v, "Send");
    if (!w) return;
    aqua_value hash = aqua_arg(call, 1);
    const char *type = field_string(call, hash, "type");
    if (!type) { aqua_fail(call, "RemoteWindow.Viewer.Send: An event needs a \"type\" string."); return; }
    if (w->closed) { aqua_return_bool(call, false); return; }
    aqua_window_event e = {0};
    aqua_value value;
    double n, x = 0, y = 0;
    bool has_x = field_number(rt, hash, "x", &x), has_y = field_number(rt, hash, "y", &y);
    e.text = field_string(call, hash, "text");
    e.pointer_type = pointer_type_of(field_string(call, hash, "pointerType"));
    if (field_number(rt, hash, "pointerId", &n) || field_number(rt, hash, "pointer", &n)) e.pointer = (uint32_t)n;
    if (field_number(rt, hash, "button", &n)) e.button = (int32_t)n;
    if (field_number(rt, hash, "key", &n)) e.key = (int32_t)n;
    if (field_number(rt, hash, "scancode", &n)) e.scancode = (int32_t)n;
    if (field_number(rt, hash, "cursor", &n)) e.cursor = (int32_t)n;
    if (field_number(rt, hash, "mods", &n) || field_number(rt, hash, "modifiers", &n)) e.mods = (int32_t)n;
    if (field_number(rt, hash, "pressure", &n)) e.pressure = (float)n;
    bool numeric_action = aqua_hash_field(rt, hash, "action", &value) && aqua_is_numeric(value);
    if (numeric_action) e.action = (int32_t)aqua_number_of(value);
    const char *action = numeric_action ? NULL : field_string(call, hash, "action");
    bool position = true;   /* x and y are a position (scaled), not wheel offsets */

    if (strcmp(type, "press") == 0 || strcmp(type, "release") == 0) {
        e.type = AQUA_EVENT_BUTTON;
        e.action = type[0] == 'p';
    } else if (strcmp(type, "move") == 0 || strcmp(type, "drag") == 0 || strcmp(type, "dragStart") == 0 || strcmp(type, "dragEnd") == 0) {
        e.type = AQUA_EVENT_POINTER;
    } else if (strcmp(type, "zoom") == 0 || (strcmp(type, "scroll") == 0 && (aqua_hash_field(rt, hash, "dx", &value) || aqua_hash_field(rt, hash, "dy", &value)))) {
        /* Interaction turned wheel steps into dx = -x * wheelStep (and primary + wheel into zoom = 1.1^y): back to steps
           through the event's own conventions. */
        double step = 0, dx = 0, dy = 0, scale = 1;
        aqua_value conventions;
        if (aqua_hash_field(rt, hash, "conventions", &conventions) && !aqua_is_nullish(conventions)) {
            aqua_value member = conventions.type == AQUA_HASH ? aqua_null() : aqua_object_member(rt, conventions, "wheelStep");
            if (aqua_is_numeric(member)) step = aqua_number_of(member);
            else field_number(rt, conventions, "wheelStep", &step);
        }
        if (!(step > 0)) step = 40;
        e.type = AQUA_EVENT_SCROLL;
        position = false;
        if (type[0] == 'z') { field_number(rt, hash, "scale", &scale); x = 0; y = scale > 0 ? round(log(scale) / log(1.1) * 1e6) / 1e6 : 0; }
        else { field_number(rt, hash, "dx", &dx); field_number(rt, hash, "dy", &dy); x = dx ? -dx / step : 0; y = dy ? -dy / step : 0; }
    } else if (strcmp(type, "key") == 0 && action) {
        e.type = AQUA_EVENT_KEY;
        e.action = strcmp(action, "release") == 0 ? 0 : strcmp(action, "repeat") == 0 ? 2 : 1;
    } else if (strcmp(type, "focus") == 0 && !numeric_action && aqua_hash_field(rt, hash, "state", &value)) {
        e.type = AQUA_EVENT_FOCUS;
        e.action = value.type == AQUA_BOOL && value.number != 0;
    } else if (strcmp(type, "composition") == 0) {
        const char *phase = field_string(call, hash, "phase");
        e.type = phase && strcmp(phase, "start") == 0 ? AQUA_EVENT_COMPOSITION_START
               : phase && strcmp(phase, "end") == 0 ? AQUA_EVENT_COMPOSITION_END : AQUA_EVENT_COMPOSITION_UPDATE;
    } else {
        /* Raw events by aqua/window.h's names; sizes go in View. */
        for (size_t i = 0; i < sizeof event_names / sizeof *event_names; i++) if (strcmp(event_names[i].name, type) == 0) e.type = event_names[i].type;
        if (!e.type || e.type == AQUA_EVENT_RESIZE) { aqua_return_bool(call, false); return; }
        position = e.type != AQUA_EVENT_SCROLL;
    }
    if (position) {
        e.x = has_x ? x * (w->painted_width > 0 ? w->width / w->painted_width : 1) : 0;
        e.y = has_y ? y * (w->painted_height > 0 ? w->height / w->painted_height : 1) : 0;
    } else { e.x = x; e.y = y; }
    send_event(v, w->id, &e);
    aqua_return_bool(call, true);
}

/* View(window, width, height, scale): the size the viewer shows the window at (logical units) and its pixel density;
   also how a viewer attaches, so the application sends the next frame whole. */
static void viewer_view(aqua_call *call) {
    viewer *v = live_viewer(call, "View");
    if (!v) return;
    rw_window *w = window_argument(call, v, "View");
    if (!w) return;
    int32_t width = aqua_arg_int(call, 1), height = aqua_arg_int(call, 2);
    double scale = aqua_has_arg(call, 3) ? aqua_arg_number(call, 3) : 1;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || !(scale >= 0.25 && scale <= 8)) {
        aqua_fail(call, "RemoteWindow.Viewer.View: The size must be 1..16384 and the scale 0.25..8.");
        return;
    }
    size_t at = aqua_remote_begin(&v->out, AQUA_REMOTE_VIEW);
    aqua_remote_u32(&v->out, w->id);
    aqua_remote_i32(&v->out, width);
    aqua_remote_i32(&v->out, height);
    aqua_remote_f32(&v->out, (float)scale);
    aqua_remote_end(&v->out, at);
    w->texture_width = w->texture_height = 0;   /* the whole frame comes again */
    aqua_return_null(call);
}

static void viewer_paste(aqua_call *call) {
    viewer *v = live_viewer(call, "Paste");
    if (!v) return;
    size_t at = aqua_remote_begin(&v->out, AQUA_REMOTE_PASTE);
    aqua_remote_string(&v->out, aqua_arg_str(call, 0).data);
    aqua_remote_end(&v->out, at);
    aqua_return_null(call);
}

/* Close(window): the viewer's user asks the window to close, as its close button would. */
static void viewer_close(aqua_call *call) {
    viewer *v = live_viewer(call, "Close");
    if (!v) return;
    rw_window *w = window_argument(call, v, "Close");
    if (!w) return;
    aqua_window_event e = {AQUA_EVENT_CLOSE};
    if (!w->closed) send_event(v, w->id, &e);
    aqua_return_null(call);
}

/* ImeRect(window): where the window's text caret is, relative to the rectangle it was last painted in, while it takes
   text input; null otherwise. */
static void viewer_ime_rect(aqua_call *call) {
    viewer *v = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    rw_window *w = window_argument(call, v, "ImeRect");
    if (!w) return;
    if (!w->text_enabled || w->closed) { aqua_return_null(call); return; }
    double sx = w->painted_width > 0 ? w->painted_width / w->width : 1, sy = w->painted_height > 0 ? w->painted_height / w->height : 1;
    double rect[4] = {w->text[0] * sx, w->text[1] * sy, w->text[2] * sx, w->text[3] * sy};
    aqua_return(call, aqua_array_from_f64(rt, rect, 4, AQUA_DOUBLE));
}

/* Forget(window): drops a closed window (and frees its texture in the renderer the returned draw list goes to). */
static void viewer_forget(aqua_call *call) {
    viewer *v = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t id = (uint32_t)aqua_arg_int(call, 0);
    numbers commands = {0};
    for (rw_window **p = &v->windows; *p; p = &(*p)->next) {
        rw_window *w = *p;
        if (w->id != id) continue;
        if (w->texture) { double op[] = {11, w->texture, 0, 0, -1, 0, 0, 0, 0}; push(&commands, op, 9); }
        *p = w->next;
        w->next = NULL;
        free_windows(w);
        break;
    }
    aqua_value list = aqua_hash_new(rt, 6);
    set_int(rt, list, "version", 1);
    aqua_hash_set_field(rt, list, "commands", aqua_array_from_f64(rt, commands.data, (uint32_t)commands.length, AQUA_FLOAT));
    aqua_hash_set_field(rt, list, "strings", aqua_array_new(rt, 0));
    aqua_hash_set_field(rt, list, "images", aqua_array_new(rt, 0));
    aqua_hash_set_field(rt, list, "vertices", aqua_array_new(rt, 0));
    aqua_hash_set_field(rt, list, "indices", aqua_array_new(rt, 0));
    free(commands.data);
    aqua_return(call, list);
}

/* Viewer([token]): with a token, the application's HELLO must carry it. */
static void module_viewer(aqua_call *call) {
    viewer *v = calloc(1, sizeof *v);
    if (aqua_has_arg(call, 0)) v->token = strdup(aqua_arg_str(call, 0).data);
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), viewer_class, v));
}

/* ---- Relay ---- */

typedef struct relay {
    inbox from_application, from_viewer;
    aqua_remote_buffer forward;
    char *token;
    bool greeted, broken;
    rw_window *windows;
} relay;

static void relay_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    relay *r = data;
    free(r->from_application.data);
    free(r->from_viewer.data);
    aqua_remote_free(&r->forward);
    free(r->token);
    free_windows(r->windows);
    free(r);
}

/* FromApplication(bytes): {"forward": the whole messages received so far, "messages": records of everything but
   frames}. The relay keeps the windows' state for Snapshot. */
static void relay_from_application(aqua_call *call) {
    relay *r = aqua_self(call);
    if (r->broken) { aqua_fail(call, "RemoteWindow.Relay.FromApplication: The stream failed earlier."); return; }
    if (!inbox_add(call, &r->from_application, aqua_arg(call, 0), "Relay.FromApplication")) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    size_t used = 0;
    const char *error = NULL;
    for (;;) {
        uint8_t type;
        aqua_remote_reader m;
        long n = aqua_remote_next(r->from_application.data + used, r->from_application.length - used, &type, &m);
        if (n < 0) { error = "The stream is malformed."; break; }
        if (n == 0) break;
        if (!r->greeted && type != AQUA_REMOTE_HELLO) { error = "The stream does not start with HELLO."; break; }
        if (type == AQUA_REMOTE_HELLO) r->greeted = true;
        aqua_remote_put(&r->forward, r->from_application.data + used, (size_t)n);
        used += (size_t)n;
        if (type == AQUA_REMOTE_FRAME) continue;
        aqua_value record_value = apply_message(rt, &r->windows, type, &m, NULL, &error);
        if (error) break;
        if (type == AQUA_REMOTE_HELLO) {
            aqua_value token;
            aqua_hash_field(rt, record_value, "token", &token);
            char *text = aqua_text_dup(rt, token, NULL);
            free(r->token);
            r->token = strdup(text ? text : "");
            aqua_free(text);
        }
        if (type == AQUA_REMOTE_CLOSE) {   /* closed windows are gone for viewers joining later */
            for (rw_window **p = &r->windows; *p; p = &(*p)->next)
                if ((*p)->closed) { rw_window *w = *p; *p = w->next; w->next = NULL; free_windows(w); break; }
        }
        append_record(rt, list, record_value);
    }
    inbox_consume(&r->from_application, used);
    if (error) { r->broken = true; aqua_fail(call, "RemoteWindow.Relay.FromApplication: %s", error); return; }
    aqua_value result = aqua_hash_new(rt, 2);
    aqua_hash_set_field(rt, result, "forward", take_bytes(rt, &r->forward));
    aqua_hash_set_field(rt, result, "messages", list);
    aqua_return(call, result);
}

/* FromViewer(bytes): the whole viewer messages received so far (EVENT, VIEW, ACK, PASTE), to forward; anything else
   fails. */
static void relay_from_viewer(aqua_call *call) {
    relay *r = aqua_self(call);
    if (!inbox_add(call, &r->from_viewer, aqua_arg(call, 0), "Relay.FromViewer")) return;
    size_t used = 0;
    for (;;) {
        uint8_t type;
        aqua_remote_reader m;
        long n = aqua_remote_next(r->from_viewer.data + used, r->from_viewer.length - used, &type, &m);
        if (n == 0) break;
        if (n < 0 || type < AQUA_REMOTE_EVENT) {
            r->from_viewer.length = 0;
            aqua_fail(call, "RemoteWindow.Relay.FromViewer: The viewer's stream is malformed.");
            return;
        }
        aqua_remote_put(&r->forward, r->from_viewer.data + used, (size_t)n);
        used += (size_t)n;
    }
    inbox_consume(&r->from_viewer, used);
    aqua_return(call, take_bytes(aqua_call_runtime(call), &r->forward));
}

/* Snapshot(): what a viewer joining now needs before the stream: HELLO and every open window with its title, cursor
   and text input. The viewer's View then has the application send each window's frame whole. */
static void relay_snapshot(aqua_call *call) {
    relay *r = aqua_self(call);
    aqua_remote_buffer b = {0};
    if (r->greeted) {
        size_t at = aqua_remote_begin(&b, AQUA_REMOTE_HELLO);
        aqua_remote_u32(&b, AQUA_REMOTE_WINDOW_MAGIC);
        aqua_remote_u16(&b, AQUA_REMOTE_WINDOW_VERSION);
        aqua_remote_string(&b, r->token);
        aqua_remote_end(&b, at);
        rw_window *order[1024];
        size_t count = 0;
        for (rw_window *w = r->windows; w && count < 1024; w = w->next) order[count++] = w;
        while (count--) {
            rw_window *w = order[count];   /* oldest first, as they opened */
            at = aqua_remote_begin(&b, AQUA_REMOTE_OPEN);
            aqua_remote_u32(&b, w->id); aqua_remote_i32(&b, w->width); aqua_remote_i32(&b, w->height); aqua_remote_u32(&b, w->flags);
            aqua_remote_string(&b, w->title);
            aqua_remote_end(&b, at);
            at = aqua_remote_begin(&b, AQUA_REMOTE_CURSOR);
            aqua_remote_u32(&b, w->id); aqua_remote_i32(&b, w->cursor);
            aqua_remote_end(&b, at);
            at = aqua_remote_begin(&b, AQUA_REMOTE_TEXT);
            aqua_remote_u32(&b, w->id); aqua_remote_u8(&b, w->text_enabled);
            for (int i = 0; i < 4; i++) aqua_remote_i32(&b, w->text[i]);
            aqua_remote_end(&b, at);
        }
    }
    aqua_return(call, take_bytes(aqua_call_runtime(call), &b));
    aqua_remote_free(&b);
}

static void relay_windows(aqua_call *call) {
    relay *r = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    for (rw_window *w = r->windows; w; w = w->next) aqua_array_push(rt, list, window_record(rt, w));
    aqua_return(call, list);
}

static void module_relay(aqua_call *call) {
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), relay_class, calloc(1, sizeof(relay))));
}

/* ---- Sender ---- */

typedef struct sent_window {
    uint32_t id;
    float scale;
    uint8_t *sent;
    int32_t sent_width, sent_height;
    uint32_t number, acknowledged;
    struct sent_window *next;
} sent_window;

typedef struct sender {
    inbox in;
    aqua_remote_buffer out;
    uint32_t next_id;
    sent_window *windows;
} sender;

static void sender_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    sender *s = data;
    free(s->in.data);
    aqua_remote_free(&s->out);
    for (sent_window *w = s->windows, *next; w; w = next) { next = w->next; free(w->sent); free(w); }
    free(s);
}

static sent_window *sent_argument(aqua_call *call, sender *s, const char *function) {
    uint32_t id = (uint32_t)aqua_arg_int(call, 0);
    for (sent_window *w = s->windows; w; w = w->next) if (w->id == id) return w;
    aqua_fail(call, "RemoteWindow.Sender.%s: No window %u.", function, id);
    return NULL;
}

/* Open(width, height, title[, flags]) → the window's id. */
static void sender_open(aqua_call *call) {
    sender *s = aqua_self(call);
    int32_t width = aqua_arg_int(call, 0), height = aqua_arg_int(call, 1);
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) { aqua_fail(call, "RemoteWindow.Sender.Open: The size must be 1..16384."); return; }
    sent_window *w = calloc(1, sizeof *w);
    w->id = ++s->next_id;
    w->scale = 1;
    w->next = s->windows;
    s->windows = w;
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_OPEN);
    aqua_remote_u32(&s->out, w->id); aqua_remote_i32(&s->out, width); aqua_remote_i32(&s->out, height);
    aqua_remote_u32(&s->out, aqua_has_arg(call, 3) ? (uint32_t)aqua_arg_int(call, 3) : 0);
    aqua_remote_string(&s->out, aqua_arg_str(call, 2).data);
    aqua_remote_end(&s->out, at);
    aqua_return_int(call, (int32_t)w->id);
}

/* Frame(window, image[, scale]) or Frame(window, rgba bytes, width, height[, scale]) → the frame's number, or 0 when
   nothing changed (nothing is sent). Only the tiles that changed since the last frame go out. Behind(window) says how
   many frames the viewer has not shown yet, so a program can skip frames while it catches up. */
static void sender_frame(aqua_call *call) {
    sender *s = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    sent_window *w = sent_argument(call, s, "Frame");
    if (!w) return;
    aqua_value source = aqua_arg(call, 1);
    aqua_image *image = aqua_image_of(rt, source);
    int32_t width, height;
    uint32_t scale_index = 2;
    if (image) { width = image->width; height = image->height; }
    else if (source.type == AQUA_ARRAY && aqua_has_arg(call, 2) && aqua_has_arg(call, 3)) {
        width = (int32_t)aqua_arg_number(call, 2); height = (int32_t)aqua_arg_number(call, 3);
        scale_index = 4;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || aqua_array_length(rt, source) != (uint32_t)width * (uint32_t)height * 4) {
            aqua_fail(call, "RemoteWindow.Sender.Frame: Expected width x height x 4 bytes of a size 1..16384.");
            return;
        }
    } else { aqua_fail(call, "RemoteWindow.Sender.Frame: Expected an image, or RGBA bytes with their width and height."); return; }
    if (aqua_has_arg(call, scale_index)) w->scale = (float)aqua_arg_number(call, scale_index);
    bool resized = !w->sent || w->sent_width != width || w->sent_height != height;
    size_t bytes = (size_t)width * (size_t)height * 4;
    uint8_t *opaque = malloc(bytes);
    if (!opaque) { aqua_fail(call, "RemoteWindow.Sender.Frame: Out of memory."); return; }
    if (image) memcpy(opaque, image->rgba, bytes);
    else if (!aqua_array_read_u8(rt, source, 0, (uint32_t)bytes, opaque)) {
        free(opaque);
        aqua_fail(call, "RemoteWindow.Sender.Frame: Expected bytes (integers 0..255).");
        return;
    }
    for (size_t i = 3; i < bytes; i += 4) opaque[i] = 255;   /* a window is opaque */
    if (!aqua_remote_frame(&s->out, w->id, w->number + 1, opaque, width, height, w->scale, resized ? NULL : w->sent)) {
        free(opaque);
        aqua_return_int(call, 0);
        return;
    }
    free(w->sent);
    w->sent = opaque;
    w->sent_width = width; w->sent_height = height;
    aqua_return_int(call, (int32_t)++w->number);
}

static void sender_behind(aqua_call *call) {
    sent_window *w = sent_argument(call, aqua_self(call), "Behind");
    if (w) aqua_return_int(call, (int32_t)(w->number - w->acknowledged));
}

static void sender_title(aqua_call *call) {
    sender *s = aqua_self(call);
    sent_window *w = sent_argument(call, s, "Title");
    if (!w) return;
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_TITLE);
    aqua_remote_u32(&s->out, w->id);
    aqua_remote_string(&s->out, aqua_arg_str(call, 1).data);
    aqua_remote_end(&s->out, at);
    aqua_return_null(call);
}

static void sender_cursor(aqua_call *call) {
    sender *s = aqua_self(call);
    sent_window *w = sent_argument(call, s, "Cursor");
    if (!w) return;
    const char *name = aqua_arg_str(call, 1).data;
    int32_t shape = strcmp(name, "hidden") == 0 ? -1 : -2;
    for (int i = 0; i < 10 && shape == -2; i++) if (strcmp(name, cursor_names[i]) == 0) shape = i ? 0x36001 + i : 0;
    if (shape == -2) { aqua_fail(call, "RemoteWindow.Sender.Cursor: Unknown cursor '%s'.", name); return; }
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_CURSOR);
    aqua_remote_u32(&s->out, w->id);
    aqua_remote_i32(&s->out, shape);
    aqua_remote_end(&s->out, at);
    aqua_return_null(call);
}

/* TextInput(window, enabled[, x, y, width, height]): text input on or off, and the caret's box. */
static void sender_text_input(aqua_call *call) {
    sender *s = aqua_self(call);
    sent_window *w = sent_argument(call, s, "TextInput");
    if (!w) return;
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_TEXT);
    aqua_remote_u32(&s->out, w->id);
    aqua_remote_u8(&s->out, aqua_arg_bool(call, 1));
    for (uint32_t i = 2; i < 6; i++) aqua_remote_i32(&s->out, aqua_has_arg(call, i) ? aqua_arg_int(call, i) : 0);
    aqua_remote_end(&s->out, at);
    aqua_return_null(call);
}

static void sender_resize(aqua_call *call) {
    sender *s = aqua_self(call);
    sent_window *w = sent_argument(call, s, "Resize");
    if (!w) return;
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_SIZE);
    aqua_remote_u32(&s->out, w->id);
    aqua_remote_i32(&s->out, aqua_arg_int(call, 1));
    aqua_remote_i32(&s->out, aqua_arg_int(call, 2));
    aqua_remote_end(&s->out, at);
    aqua_return_null(call);
}

static void sender_clipboard(aqua_call *call) {
    sender *s = aqua_self(call);
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_CLIPBOARD);
    aqua_remote_string(&s->out, aqua_arg_str(call, 0).data);
    aqua_remote_end(&s->out, at);
    aqua_return_null(call);
}

static void sender_close(aqua_call *call) {
    sender *s = aqua_self(call);
    uint32_t id = (uint32_t)aqua_arg_int(call, 0);
    for (sent_window **p = &s->windows; *p; p = &(*p)->next) {
        if ((*p)->id != id) continue;
        sent_window *w = *p;
        *p = w->next;
        free(w->sent);
        free(w);
        size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_CLOSE);
        aqua_remote_u32(&s->out, id);
        aqua_remote_end(&s->out, at);
        aqua_return_bool(call, true);
        return;
    }
    aqua_return_bool(call, false);
}

static void sender_outgoing(aqua_call *call) { aqua_return(call, take_bytes(aqua_call_runtime(call), &((sender *)aqua_self(call))->out)); }

/* A received event as a raw event hash (Interaction.Feed's shape). */
static aqua_value raw_event(aqua_runtime *rt, const aqua_window_event *e) {
    const char *name = "unknown";
    for (size_t i = 0; i < sizeof event_names / sizeof *event_names; i++) if (event_names[i].type == e->type) name = event_names[i].name;
    if (e->type == AQUA_EVENT_COMPOSITION_START || e->type == AQUA_EVENT_COMPOSITION_END) name = "composition";
    aqua_value h = aqua_hash_new(rt, 16);
    set_string(rt, h, "type", name);
    if (e->type == AQUA_EVENT_COMPOSITION_START || e->type == AQUA_EVENT_COMPOSITION_UPDATE || e->type == AQUA_EVENT_COMPOSITION_END)
        set_string(rt, h, "phase", e->type == AQUA_EVENT_COMPOSITION_START ? "start" : e->type == AQUA_EVENT_COMPOSITION_END ? "end" : "update");
    aqua_hash_set_field(rt, h, "x", aqua_double(e->x));
    aqua_hash_set_field(rt, h, "y", aqua_double(e->y));
    set_int(rt, h, "action", e->action); set_int(rt, h, "mods", e->mods); set_int(rt, h, "button", e->button);
    set_int(rt, h, "key", e->key); set_int(rt, h, "scancode", e->scancode);
    set_int(rt, h, "pointer", e->pointer);
    set_string(rt, h, "pointerType", e->pointer_type == 1 ? "touch" : e->pointer_type == 2 ? "pen" : "mouse");
    aqua_hash_set_field(rt, h, "pressure", aqua_double(e->pressure));
    set_int(rt, h, "cursor", e->cursor);
    set_string(rt, h, "text", e->text);
    return h;
}

/* Feed(bytes): the viewer's messages as records: {"type": "event", "window", "event": a raw event hash},
   {"type": "view", "window", "width", "height", "scale"}, {"type": "paste", "text"}. Acknowledgements are kept
   (Behind); a VIEW means the viewer (re)attached, so the next frame goes out whole. */
static void sender_feed(aqua_call *call) {
    sender *s = aqua_self(call);
    if (!inbox_add(call, &s->in, aqua_arg(call, 0), "Sender.Feed")) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    size_t used = 0;
    for (;;) {
        uint8_t type;
        aqua_remote_reader m;
        long n = aqua_remote_next(s->in.data + used, s->in.length - used, &type, &m);
        if (n < 0) { s->in.length = 0; aqua_fail(call, "RemoteWindow.Sender.Feed: The viewer's stream is malformed."); return; }
        if (n == 0) break;
        used += (size_t)n;
        if (type == AQUA_REMOTE_PASTE) {
            char *text = aqua_remote_read_string(&m);
            aqua_value r = record(rt, "paste", 0);
            set_string(rt, r, "text", text);
            free(text);
            aqua_array_push(rt, list, r);
            continue;
        }
        uint32_t id = aqua_remote_read_u32(&m);
        sent_window *w = NULL;
        for (sent_window *x = s->windows; x; x = x->next) if (x->id == id) w = x;
        if (type == AQUA_REMOTE_ACK) {
            uint32_t number = aqua_remote_read_u32(&m);
            if (w && !m.failed && number <= w->number && number > w->acknowledged) w->acknowledged = number;
        } else if (type == AQUA_REMOTE_VIEW) {
            int32_t width = aqua_remote_read_i32(&m), height = aqua_remote_read_i32(&m);
            float scale = aqua_remote_read_f32(&m);
            if (m.failed) continue;
            if (w) { w->acknowledged = w->number; free(w->sent); w->sent = NULL; }
            aqua_value r = record(rt, "view", id);
            set_int(rt, r, "width", width); set_int(rt, r, "height", height);
            aqua_hash_set_field(rt, r, "scale", aqua_double(scale));
            aqua_array_push(rt, list, r);
        } else if (type == AQUA_REMOTE_EVENT) {
            aqua_window_event e = {0};
            e.type = aqua_remote_read_i32(&m);
            e.key = aqua_remote_read_i32(&m); e.scancode = aqua_remote_read_i32(&m); e.action = aqua_remote_read_i32(&m);
            e.mods = aqua_remote_read_i32(&m); e.button = aqua_remote_read_i32(&m);
            e.x = aqua_remote_read_f64(&m); e.y = aqua_remote_read_f64(&m);
            e.pointer = aqua_remote_read_u32(&m); e.pointer_type = aqua_remote_read_i32(&m);
            e.pressure = aqua_remote_read_f32(&m); e.tilt_x = aqua_remote_read_f32(&m); e.tilt_y = aqua_remote_read_f32(&m);
            e.cursor = aqua_remote_read_i32(&m); e.index = aqua_remote_read_i32(&m); e.count = aqua_remote_read_i32(&m);
            char *text = aqua_remote_read_string(&m);
            e.text = text;
            if (!m.failed) {
                aqua_value r = record(rt, "event", id);
                aqua_hash_set_field(rt, r, "event", raw_event(rt, &e));
                aqua_array_push(rt, list, r);
            }
            free(text);
        }
    }
    inbox_consume(&s->in, used);
    aqua_return(call, list);
}

/* Sender([token]): starts the stream with HELLO. */
static void module_sender(aqua_call *call) {
    sender *s = calloc(1, sizeof *s);
    size_t at = aqua_remote_begin(&s->out, AQUA_REMOTE_HELLO);
    aqua_remote_u32(&s->out, AQUA_REMOTE_WINDOW_MAGIC);
    aqua_remote_u16(&s->out, AQUA_REMOTE_WINDOW_VERSION);
    aqua_remote_string(&s->out, aqua_has_arg(call, 0) ? aqua_arg_str(call, 0).data : "");
    aqua_remote_end(&s->out, at);
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), sender_class, s));
}

AQUA_LIBRARY(remotewindow) {
    aqua_class *module = aqua_define_module(library, "RemoteWindow|遠端視窗");
    aqua_define_function(module, "Viewer|檢視器", module_viewer, "|s");
    aqua_define_function(module, "Relay|轉送", module_relay, "");
    aqua_define_function(module, "Sender|傳送端", module_sender, "|s");
    aqua_define_int(module, "VERSION|版本", AQUA_REMOTE_WINDOW_VERSION);

    viewer_class = aqua_define_class(library, "RemoteWindow.Viewer", viewer_finalize);
    aqua_define_function(viewer_class, "Feed|輸入", viewer_feed, "a");
    aqua_define_function(viewer_class, "Outgoing|待送", viewer_outgoing, "");
    aqua_define_function(viewer_class, "Windows|視窗清單", viewer_windows, "");
    aqua_define_function(viewer_class, "Window|視窗", viewer_window, "i");
    aqua_define_function(viewer_class, "Image|影像", viewer_image, "i");
    aqua_define_function(viewer_class, "Paint|繪製", viewer_paint, "innnn");
    aqua_define_function(viewer_class, "Send|傳送事件", viewer_send, "ih");
    aqua_define_function(viewer_class, "View|檢視", viewer_view, "iii|n");
    aqua_define_function(viewer_class, "Paste|貼上", viewer_paste, "s");
    aqua_define_function(viewer_class, "Close|關閉", viewer_close, "i");
    aqua_define_function(viewer_class, "ImeRect|輸入法位置", viewer_ime_rect, "i");
    aqua_define_function(viewer_class, "Forget|移除", viewer_forget, "i");

    relay_class = aqua_define_class(library, "RemoteWindow.Relay", relay_finalize);
    aqua_define_function(relay_class, "FromApplication|來自應用程式", relay_from_application, "a");
    aqua_define_function(relay_class, "FromViewer|來自檢視器", relay_from_viewer, "a");
    aqua_define_function(relay_class, "Snapshot|快照", relay_snapshot, "");
    aqua_define_function(relay_class, "Windows|視窗清單", relay_windows, "");

    sender_class = aqua_define_class(library, "RemoteWindow.Sender", sender_finalize);
    aqua_define_function(sender_class, "Open|開啟", sender_open, "iis|i");
    aqua_define_function(sender_class, "Frame|畫面", sender_frame, "iv|nnn");
    aqua_define_function(sender_class, "Behind|落後", sender_behind, "i");
    aqua_define_function(sender_class, "Title|標題", sender_title, "is");
    aqua_define_function(sender_class, "Cursor|游標", sender_cursor, "is");
    aqua_define_function(sender_class, "TextInput|文字輸入", sender_text_input, "ib|iiii");
    aqua_define_function(sender_class, "Resize|調整大小", sender_resize, "iii");
    aqua_define_function(sender_class, "Clipboard|剪貼簿", sender_clipboard, "s");
    aqua_define_function(sender_class, "Close|關閉", sender_close, "i");
    aqua_define_function(sender_class, "Outgoing|待送", sender_outgoing, "");
    aqua_define_function(sender_class, "Feed|輸入", sender_feed, "a");
}
