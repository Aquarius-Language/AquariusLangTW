/* WGPU (wgpu): devices with an optional window surface, storage and vertex buffers, WGSL shaders with the Processing
 * uniform ABI, render targets, textures, compute dispatch and windows with polled input, on src/gpu.
 *
 * Objects keep what they depend on alive: resources their device, a device its window, a target's texture its target,
 * a shader the textures and buffers bound to it. Dispose releases the GPU objects at once; the struct stays until the
 * object is collected, so a disposed object fails cleanly ("wgpu buffer has been disposed."). Failures read
 * "<Member>: <message>" as before. Reading back (Read, ReadPixels, Save), shader validation, Poll and device creation
 * finish inside the call natively and when the browser's promise settles on the web. */
#include "wgpu.h"
#include "../graphics.h"
#include "../common/codecs.h"
#include "../common/files.h"
#include "../processing/processing.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static aqua_class *device_class, *buffer_class, *vertex_class, *shader_class_w, *target_class, *texture_class, *window_class_w;

typedef struct wgpu_window wgpu_window;
typedef struct device_object { gpu_device *device; wgpu_window *window; aqua_ref window_ref; } device_object;
typedef struct buffer_object { device_object *owner; aqua_ref owner_ref; gpu_buffer *buffer; } buffer_object;
typedef struct binding { char name[64]; aqua_ref ref; } binding;
typedef struct shader_object { device_object *owner; aqua_ref owner_ref; gpu_shader *shader; binding bound[16]; int bound_count; } shader_object;
typedef struct target_object { device_object *owner; aqua_ref owner_ref; gpu_target *target; } target_object;
typedef struct texture_object { gpu_device *device; aqua_ref keep; gpu_texture *texture; gpu_target *target; bool disposed; } texture_object;

typedef struct window_snapshot {
    int width, height, pixel_width, pixel_height;
    double mouse_x, mouse_y, mouse_dx, mouse_dy, wheel;
    bool focused, pointer_locked, should_close, resized;
} window_snapshot;

struct wgpu_window {
    aqua_runtime *rt;
    aqua_window *window;      /* NULL once disposed */
    device_object *device;    /* the device presenting to it */
    bool keys[512], key_pressed[512], key_released[512], pending_pressed[512], pending_released[512];
    bool buttons[8], button_pressed[8], button_released[8], pending_button_pressed[8], pending_button_released[8];
    double wheel, last_x, last_y;
    bool have_last, focused, locked, close_requested;
    window_snapshot state;
    wgpu_window *next;        /* the runtime's open windows */
    wgpu_state *owner;
};

struct wgpu_state { wgpu_window *windows; };

static wgpu_state *state_of(aqua_call *call) {
    graphics *g = gfx_graphics_of(call);
    if (!g->wgpu) g->wgpu = calloc(1, sizeof *g->wgpu);
    return g->wgpu;
}

/* ---- Arguments ---- */

/* "wgpu data needs 1..16 million floats." arrays of finite single-precision numbers (call scratch). */
static float *wgpu_floats(aqua_call *call, uint32_t index, uint32_t *count) {
    aqua_value array = gfx_array(call, index);
    if (aqua_failed(call)) return NULL;
    uint32_t n = aqua_array_length(aqua_call_runtime(call), array);
    if (n == 0 || n > GPU_MAX_FLOATS) { gfx_fail(call, "wgpu data needs 1..16 million floats."); return NULL; }
    return gfx_floats(call, array, count);
}

static bool device_live(aqua_call *call, device_object *d) {
    if (!d || gpu_device_disposed(d->device)) { gfx_fail(call, "wgpu device has been disposed."); return false; }
    return true;
}

static shader_object *shader_argument(aqua_call *call, uint32_t index, device_object *d) {
    shader_object *s = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, index), shader_class_w);
    if (!s || s->owner != d || gpu_shader_released(s->shader)) { gfx_fail(call, "Expected a live shader from this wgpu device."); return NULL; }
    return s;
}

/* ---- Textures ---- */

static void texture_finalize(aqua_runtime *rt, void *data) {
    texture_object *t = data;
    if (!t->target) gpu_texture_free(t->texture);
    if (t->keep) aqua_release(rt, t->keep);
    free(t);
}

aqua_value gfx_texture_object_new(aqua_runtime *rt, gpu_device *device, gpu_texture *texture, gpu_target *target, aqua_value keep) {
    texture_object *t = calloc(1, sizeof *t);
    t->device = device; t->texture = texture; t->target = target;
    if (!aqua_is_nullish(keep)) t->keep = aqua_retain(rt, keep);
    return aqua_object_new(rt, texture_class, t);
}

static texture_object *texture_data(aqua_runtime *rt, aqua_value value) { return aqua_object_data(rt, value, texture_class); }

static bool texture_released(texture_object *t) {
    return t->disposed || (t->target ? gpu_target_released(t->target) : gpu_texture_released(t->texture));
}

gpu_texture *gfx_texture_object_texture(aqua_runtime *rt, aqua_value value) {
    texture_object *t = texture_data(rt, value);
    return t && !texture_released(t) ? t->texture : NULL;
}

void gfx_texture_object_dispose(aqua_runtime *rt, aqua_value value) {
    texture_object *t = texture_data(rt, value);
    if (!t || t->disposed) return;
    t->disposed = true;
    if (t->target) gpu_target_release_handles(t->target);
    else gpu_texture_release_handles(t->texture);
}

static bool half_floats_finite(const uint8_t *bytes, size_t length) {
    for (size_t i = 0; i + 1 < length; i += 2) if ((bytes[i + 1] & 0x7c) == 0x7c) return false;
    return true;
}

bool gfx_texture_load(aqua_call *call, gpu_device *device, uint32_t first, aqua_value keep, aqua_value *result) {
    aqua_runtime *rt = aqua_call_runtime(call);
    gpu_texture_desc desc = {0};
    desc.width = gfx_int(call, first + 1);
    desc.height = gfx_int(call, first + 2);
    aqua_str format = gfx_text(call, first + 3), dimension = gfx_text(call, first + 4);
    desc.mip_levels = gfx_int(call, first + 5);
    if (aqua_failed(call)) return false;
    char error[512];
    bool known = gpu_format_parse(format.data, &desc.format) && (strcmp(dimension.data, "2d") == 0 || strcmp(dimension.data, "cube") == 0);
    desc.cube = strcmp(dimension.data, "cube") == 0;
    size_t expected = known ? gpu_texture_bytes(&desc, error, sizeof error) : 0;
    if (!known) snprintf(error, sizeof error, "Invalid texture dimensions, format, dimension or mip count.");
    if (!expected) { gfx_fail(call, "%s", error); return false; }
    aqua_str path = gfx_text(call, first);
    if (aqua_failed(call)) return false;
    gfx_bytes file;
    if (!gfx_read_resource(rt, path.data, &file, error, sizeof error)) { gfx_fail(call, "%s", error); return false; }
    const uint8_t *bytes = file.data;
    uint8_t *inflated = NULL;
    if (file.length >= 2 && bytes[0] == 31 && bytes[1] == 139) {
        inflated = gfx_codec_gunzip(file.data, file.length, expected);
        bytes = inflated;
    } else if (file.length != expected) bytes = NULL;
    if (!bytes) { gfx_bytes_free(&file); gfx_fail(call, "Texture data length does not match its descriptor."); return false; }
    bool ok = desc.format != GPU_RGBA16FLOAT || half_floats_finite(bytes, expected);
    gpu_texture *texture = NULL;
    if (!ok) snprintf(error, sizeof error, "Texture components must be finite half floats.");
    else texture = gpu_texture_create(device, &desc, bytes, error, sizeof error);
    free(inflated);
    gfx_bytes_free(&file);
    if (!texture) { gfx_fail(call, "%s", error); return false; }
    *result = gfx_texture_object_new(rt, device, texture, NULL, keep);
    return true;
}

static void texture_Dispose(aqua_call *call) { gfx_texture_object_dispose(aqua_call_runtime(call), aqua_self_value(call)); }

/* ---- Buffers ---- */

static void buffer_finalize(aqua_runtime *rt, void *data) {
    buffer_object *b = data;
    gpu_buffer_free(b->buffer);
    aqua_release(rt, b->owner_ref);
    free(b);
}

static aqua_value buffer_object_new(aqua_call *call, aqua_class *cls, device_object *d, gpu_buffer *buffer) {
    aqua_runtime *rt = aqua_call_runtime(call);
    buffer_object *b = calloc(1, sizeof *b);
    b->owner = d; b->owner_ref = aqua_retain(rt, aqua_self_value(call)); b->buffer = buffer;
    return aqua_object_new(rt, cls, b);
}

static bool range_valid(aqua_call *call, int length, int offset, int count) {
    if (offset < 0 || count < 1 || count > length || offset > length - count) { gfx_fail(call, "Invalid buffer range / 緩衝區範圍無效。"); return false; }
    return true;
}

static bool storage_live(aqua_call *call, buffer_object *b) {
    if (!device_live(call, b->owner)) return false;
    if (gpu_buffer_released(b->buffer)) { gfx_fail(call, "wgpu buffer has been disposed."); return false; }
    return true;
}

static void floats_read(void *data, const float *values, int count, const char *error) {
    gfx_async *a = data;
    if (error) { gfx_async_fail(a, "%s", error); return; }
    double *numbers = malloc((size_t)(count ? count : 1) * sizeof *numbers);
    for (int i = 0; i < count; i++) numbers[i] = values[i];
    aqua_value array = gfx_doubles(gfx_async_runtime(a), numbers, (uint32_t)count);
    free(numbers);
    gfx_async_finish(a, array);
}

static void read_floats(aqua_call *call, gpu_buffer *buffer, int offset, int count) {
    gfx_async *a = gfx_async_begin(call, 0);
    gpu_buffer_read(buffer, offset, count, floats_read, a);
    gfx_async_end(a);
}

static void buffer_Write(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (!device_live(call, b->owner)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 0, &n);
    if (!values || !storage_live(call, b)) return;
    if ((int)n != gpu_buffer_length(b->buffer)) { gfx_fail(call, "Buffer Write needs %d floats.", gpu_buffer_length(b->buffer)); return; }
    gpu_buffer_write(b->buffer, 0, values, (int)n);
}

static void buffer_WriteRange(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (!device_live(call, b->owner)) return;
    int offset = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 1, &n);
    if (!values || !storage_live(call, b) || !range_valid(call, gpu_buffer_length(b->buffer), offset, (int)n)) return;
    gpu_buffer_write(b->buffer, offset, values, (int)n);
}

static void buffer_Read(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (storage_live(call, b)) read_floats(call, b->buffer, 0, gpu_buffer_length(b->buffer));
}

static void buffer_ReadRange(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (!device_live(call, b->owner)) return;
    int offset = gfx_int(call, 0), count = gfx_int(call, 1);
    if (aqua_failed(call) || !storage_live(call, b) || !range_valid(call, gpu_buffer_length(b->buffer), offset, count)) return;
    read_floats(call, b->buffer, offset, count);
}

static void buffer_Dispose(aqua_call *call) { gpu_buffer_release_handles(((buffer_object *)aqua_self(call))->buffer); }
static void buffer_Length(aqua_call *call) { aqua_return_int(call, gpu_buffer_length(((buffer_object *)aqua_self(call))->buffer)); }

/* Vertex buffers. */
static bool vertex_live(aqua_call *call, buffer_object *b) {
    if (!device_live(call, b->owner)) return false;
    if (gpu_buffer_released(b->buffer)) { gfx_fail(call, "wgpu vertex buffer has been disposed."); return false; }
    return true;
}

static void vertex_Write(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (!vertex_live(call, b)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 0, &n);
    if (!values) return;
    if ((int)n != gpu_buffer_length(b->buffer)) { gfx_fail(call, "Vertex buffer Write needs %d floats.", gpu_buffer_length(b->buffer)); return; }
    gpu_buffer_write(b->buffer, 0, values, (int)n);
}

static void vertex_WriteRange(aqua_call *call) {
    buffer_object *b = aqua_self(call);
    if (!vertex_live(call, b)) return;
    int offset = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 1, &n);
    if (!values || !range_valid(call, gpu_buffer_length(b->buffer), offset, (int)n)) return;
    gpu_buffer_write(b->buffer, offset, values, (int)n);
}

/* ---- Shaders ---- */

static void shader_finalize_w(aqua_runtime *rt, void *data) {
    shader_object *s = data;
    gpu_shader_free(s->shader);
    for (int i = 0; i < s->bound_count; i++) aqua_release(rt, s->bound[i].ref);
    aqua_release(rt, s->owner_ref);
    free(s);
}

/* Keeps the object bound under `name` alive while the shader may sample it. */
static void bind(aqua_runtime *rt, shader_object *s, const char *name, aqua_value object) {
    int slot = -1;
    for (int i = 0; i < s->bound_count; i++) if (strcmp(s->bound[i].name, name) == 0) slot = i;
    if (slot < 0) {
        if (s->bound_count == 16) return; /* at most 8 textures and 7 buffers exist */
        slot = s->bound_count++;
        snprintf(s->bound[slot].name, sizeof s->bound[slot].name, "%s", name);
    } else aqua_release(rt, s->bound[slot].ref);
    s->bound[slot].ref = aqua_retain(rt, object);
}

static bool shader_live(aqua_call *call, shader_object *s) {
    if (!device_live(call, s->owner)) return false;
    if (gpu_shader_released(s->shader)) { gfx_fail(call, "wgpu shader has been disposed."); return false; }
    return true;
}

static void shader_Set(aqua_call *call) {
    shader_object *s = aqua_self(call);
    if (!shader_live(call, s)) return;
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 1, &n);
    if (!values) return;
    char error[256];
    if (!gpu_shader_set(s->shader, name.data, values, (int)n, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void shader_SetInt(aqua_call *call) {
    shader_object *s = aqua_self(call);
    if (!shader_live(call, s)) return;
    aqua_str name = gfx_text(call, 0);
    int value = gfx_int(call, 1);
    if (aqua_failed(call)) return;
    char error[256];
    if (!gpu_shader_set_int(s->shader, name.data, value, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void shader_SetTexture(aqua_call *call) {
    shader_object *s = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!device_live(call, s->owner)) return;
    texture_object *t = texture_data(rt, aqua_arg(call, 1));
    if (!t || t->device != s->owner->device || texture_released(t)) { gfx_fail(call, "Expected a live texture from this device."); return; }
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char error[256];
    if (!gpu_shader_set_texture(s->shader, name.data, t->texture, error, sizeof error)) { gfx_fail(call, "%s", error); return; }
    bind(rt, s, name.data, aqua_arg(call, 1));
}

static void shader_SetBuffer(aqua_call *call) {
    shader_object *s = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!device_live(call, s->owner)) return;
    buffer_object *b = aqua_object_data(rt, aqua_arg(call, 1), buffer_class);
    if (!b || b->owner != s->owner) { gfx_fail(call, "Expected a live storage buffer from this device."); return; }
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char error[256];
    if (!gpu_shader_set_buffer(s->shader, name.data, b->buffer, error, sizeof error)) { gfx_fail(call, "%s", error); return; }
    bind(rt, s, name.data, aqua_arg(call, 1));
}

static void shader_Dispose(aqua_call *call) { gpu_shader_release_handles(((shader_object *)aqua_self(call))->shader); }

/* ---- Render targets ---- */

static void target_finalize(aqua_runtime *rt, void *data) {
    target_object *t = data;
    gpu_target_free(t->target);
    aqua_release(rt, t->owner_ref);
    free(t);
}

static bool target_live(aqua_call *call, target_object *t) {
    if (!device_live(call, t->owner)) return false;
    if (gpu_target_released(t->target)) { gfx_fail(call, "wgpu render target has been disposed."); return false; }
    return true;
}

static void target_Width(aqua_call *call) { aqua_return_int(call, gpu_target_width(((target_object *)aqua_self(call))->target)); }
static void target_Height(aqua_call *call) { aqua_return_int(call, gpu_target_height(((target_object *)aqua_self(call))->target)); }

static void target_GetTexture(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    aqua_return(call, gfx_texture_object_new(aqua_call_runtime(call), t->owner->device, gpu_target_color(t->target), t->target, aqua_self_value(call)));
}

static void target_Clear(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    uint32_t n = 0;
    float *c = wgpu_floats(call, 0, &n);
    if (!c) return;
    if (n != 4 || c[0] < 0 || c[0] > 1 || c[1] < 0 || c[1] > 1 || c[2] < 0 || c[2] > 1 || c[3] < 0 || c[3] > 1) {
        gfx_fail(call, "Color needs four RGBA values in 0..1.");
        return;
    }
    gpu_target_clear(t->target, c);
}

static void target_Draw(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    shader_object *s = shader_argument(call, 0, t->owner);
    if (!s) return;
    uint32_t n = 0;
    float *vertices = wgpu_floats(call, 1, &n);
    if (!vertices) return;
    if (n % 36 != 0) { gfx_fail(call, "Triangle vertices need a multiple of 36 floats (12 per vertex)."); return; }
    static float identity[GPU_UNIFORM_BYTES / 4];
    if (identity[0] == 0) for (int m = 0; m < 4; m++) for (int i = 0; i < 4; i++) identity[m * 16 + i * 5] = 1;
    gpu_state state = gpu_target_state(t->target);
    char error[1024];
    if (!gpu_target_draw(t->target, s->shader, vertices, n, identity, NULL, 4, state.blend, state.depth, state.cull, NULL, NULL, 0, error, sizeof error))
        gfx_fail(call, "%s", error);
}

static const char *const depth_modes[] = {"none", "less", "read"};
static const char *const blend_modes[] = {"alpha", "add", "multiply", "screen", "replace", "premultiplied"};
static const char *const cull_modes[] = {"none", "back", "front"};

static int mode_code(aqua_call *call, const char *const *modes, int count, const char *value, const char *name) {
    for (int i = 0; i < count; i++) if (strcmp(modes[i], value) == 0) return i;
    char expected[128] = "";
    for (int i = 0; i < count; i++) { if (i) strcat(expected, ", "); strcat(expected, modes[i]); }
    gfx_fail(call, "Unknown %s mode '%s'; expected %s.", name, value, expected);
    return -1;
}

static void target_SetState(aqua_call *call) {
    target_object *t = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!target_live(call, t)) return;
    aqua_value hash = aqua_arg(call, 0);
    if (hash.type != AQUA_HASH) { gfx_fail(call, "SetState expects a hash such as {\"depth\":\"less\",\"blend\":\"replace\",\"cull\":\"back\"}."); return; }
    char *depth = NULL, *blend = NULL, *cull = NULL;
    uint32_t count = aqua_hash_count(rt, hash);
    bool ok = true;
    for (uint32_t i = 0; i < count && ok; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, hash, i, &key, &value);
        if (key.type != AQUA_STRING) { gfx_fail(call, "Draw state keys must be strings."); ok = false; break; }
        if (value.type != AQUA_STRING) { gfx_fail(call, "Draw state values must be strings."); ok = false; break; }
        aqua_str k = aqua_str_of(call, key), v = aqua_str_of(call, value);
        char *copy = aqua_scratch(call, v.length + 1);
        memcpy(copy, v.data, v.length + 1);
        if (strcmp(k.data, "depth") == 0) depth = copy;
        else if (strcmp(k.data, "blend") == 0) blend = copy;
        else if (strcmp(k.data, "cull") == 0) cull = copy;
        else { gfx_fail(call, "Unknown draw state key '%s'; expected depth, blend or cull.", k.data); ok = false; }
    }
    if (!ok) return;
    gpu_state state = {0, 0, 0};
    if (depth && (state.depth = mode_code(call, depth_modes, 3, depth, "depth")) < 0) return;
    if (blend && (state.blend = mode_code(call, blend_modes, 6, blend, "blend")) < 0) return;
    if (cull && (state.cull = mode_code(call, cull_modes, 3, cull, "cull")) < 0) return;
    gpu_target_set_state(t->target, state);
}

static void target_DrawBuffer(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    shader_object *s = shader_argument(call, 0, t->owner);
    if (!s) return;
    buffer_object *b = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, 1), vertex_class);
    if (!b || b->owner != t->owner || gpu_buffer_released(b->buffer)) { gfx_fail(call, "Expected a live vertex buffer from this wgpu device."); return; }
    int vertices = gpu_buffer_length(b->buffer) / 12;
    int first = aqua_argc(call) > 2 ? gfx_int(call, 2) : 0;
    if (aqua_failed(call)) return;
    int count = aqua_argc(call) > 3 ? gfx_int(call, 3) : vertices - first;
    if (aqua_failed(call)) return;
    if (first < 0 || count < 0 || first > vertices - count) { gfx_fail(call, "Vertex range is outside the buffer / 頂點範圍超出緩衝區。"); return; }
    char error[1024];
    if (count > 0 && !gpu_target_draw_retained(t->target, s->shader, b->buffer, first, count, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void target_DrawProcedural(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    shader_object *s = shader_argument(call, 0, t->owner);
    if (!s) return;
    int count = gfx_int(call, 1);
    if (aqua_failed(call)) return;
    int first = aqua_argc(call) > 2 ? gfx_int(call, 2) : 0;
    if (aqua_failed(call)) return;
    if (first < 0 || count < 0 || count > 48 * 1024 * 1024 || first > INT32_MAX - count) {
        gfx_fail(call, "Procedural vertex range must be nonnegative and at most 50331648 vertices / 程序化頂點範圍無效。");
        return;
    }
    char error[1024];
    if (count > 0 && !gpu_target_draw_retained(t->target, s->shader, NULL, first, count, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void pixels_read(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    if (error) { gfx_async_fail(a, "%s", error); return; }
    gfx_async_finish(a, gfx_pixel_array(gfx_async_runtime(a), rgba, width * height));
}

static void target_ReadPixels(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    gfx_async *a = gfx_async_begin(call, 0);
    gpu_target_read(t->target, pixels_read, a);
    gfx_async_end(a);
}

typedef struct save_operation { char *path; } save_operation;

static void pixels_saved(void *data, const uint8_t *rgba, int width, int height, const char *error) {
    gfx_async *a = data;
    save_operation *o = gfx_async_data(a);
    char message[1024];
    if (error) gfx_async_fail(a, "%s", error);
    else if (!gfx_image_write_file(o->path, width, height, rgba, message, sizeof message)) gfx_async_fail(a, "%s", message);
    else { free(o->path); o->path = NULL; gfx_async_finish(a, aqua_null()); return; }
    free(o->path);
    o->path = NULL;
}

static void target_Save(aqua_call *call) {
    target_object *t = aqua_self(call);
    if (!target_live(call, t)) return;
    aqua_str path = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    gfx_async *a = gfx_async_begin(call, sizeof(save_operation));
    save_operation *o = gfx_async_data(a);
    o->path = malloc(path.length + 1);
    memcpy(o->path, path.data, path.length + 1);
    gpu_target_read(t->target, pixels_saved, a);
    gfx_async_end(a);
}

static void target_Dispose(aqua_call *call) { gpu_target_release_handles(((target_object *)aqua_self(call))->target); }

/* ---- Devices ---- */

static void device_finalize(aqua_runtime *rt, void *data) {
    device_object *d = data;
    if (d->window && d->window->device == d) d->window->device = NULL;
    gpu_device_dispose(d->device);
    gpu_device_release(d->device);
    if (d->window_ref) aqua_release(rt, d->window_ref);
    free(d);
}

typedef struct device_request { wgpu_window *window; aqua_ref window_ref; } device_request;

static void device_created(gpu_device *device, const char *error, void *data) {
    gfx_async *a = data;
    device_request *request = gfx_async_data(a);
    aqua_runtime *rt = gfx_async_runtime(a);
    wgpu_window *w = request->window;
    if (!error && w && !w->window) {
        gpu_device_dispose(device);
        gpu_device_release(device);
        error = "The window has been disposed.";
    }
    if (error) {
        if (request->window_ref) aqua_release(rt, request->window_ref);
        gfx_async_fail(a, "%s", error);
        return;
    }
    device_object *d = calloc(1, sizeof *d);
    d->device = device;
    d->window = w;
    d->window_ref = request->window_ref;
    if (w) w->device = d;
    gfx_async_finish(a, aqua_object_new(rt, device_class, d));
}

static void wgpu_CreateDevice(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    wgpu_window *w = NULL;
    if (aqua_argc(call) == 1) {
        w = aqua_object_data(rt, aqua_arg(call, 0), window_class_w);
        if (!w || !w->window) { gfx_fail(call, "Expected a live WGPU window."); return; }
        if (w->device && !gpu_device_disposed(w->device->device)) { gfx_fail(call, "This window already has a wgpu device; dispose it first."); return; }
    }
    gfx_async *a = gfx_async_begin(call, sizeof(device_request));
    device_request *request = gfx_async_data(a);
    request->window = w;
    request->window_ref = w ? aqua_retain(rt, aqua_arg(call, 0)) : 0;
    gpu_device_create(rt, w ? w->window : NULL, device_created, a);
    gfx_async_end(a);
}

static device_object *device_self(aqua_call *call) { return aqua_self(call); }

static void device_Backend(aqua_call *call) { aqua_return_cstring(call, gpu_device_backend(device_self(call)->device)); }
static void device_Dispose(aqua_call *call) { gpu_device_dispose(device_self(call)->device); }

static void polled(void *data, const char *error) {
    gfx_async *a = data;
    if (error) gfx_async_fail(a, "%s", error);
    else gfx_async_finish(a, aqua_null());
}

static void device_Poll(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    gfx_async *a = gfx_async_begin(call, 0);
    gpu_device_poll(d->device, polled, a);
    gfx_async_end(a);
}

static void device_LoadTexture(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    aqua_value texture;
    if (gfx_texture_load(call, d->device, 0, aqua_self_value(call), &texture)) aqua_return(call, texture);
}

static void device_Present(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    if (!d->window) { gfx_fail(call, "This wgpu device has no window surface; create it with WGPU.CreateDevice(window)."); return; }
    target_object *t = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, 0), target_class);
    if (!t || t->owner != d) { gfx_fail(call, "Expected a render target from this wgpu device."); return; }
    if (!d->window->window) { gfx_fail(call, "The window has been disposed."); return; }
    if (!target_live(call, t)) return;
    char error[1024];
    if (!gpu_device_present(d->device, t->target, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void device_CreateVertexBuffer(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    uint32_t n = 0;
    float *values = wgpu_floats(call, 0, &n);
    if (!values) return;
    if (n % 12 != 0) { gfx_fail(call, "Vertex buffers need a multiple of 12 floats per vertex (position, normal, UV, RGBA)."); return; }
    char error[512];
    gpu_buffer *buffer = gpu_buffer_create(d->device, values, (int)n, true, error, sizeof error);
    if (!buffer) { gfx_fail(call, "%s", error); return; }
    aqua_return(call, buffer_object_new(call, vertex_class, d, buffer));
}

static void device_CreateBuffer(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    float *values = NULL;
    uint32_t n = 0;
    if (aqua_arg(call, 0).type == AQUA_ARRAY) {
        values = wgpu_floats(call, 0, &n);
        if (!values) return;
    } else {
        int length = gfx_int(call, 0);
        if (aqua_failed(call)) return;
        if (length < 1 || length > GPU_MAX_FLOATS) { gfx_fail(call, "Buffer length must be 1..16777216 floats / 緩衝區長度必須為 1..16777216。"); return; }
        n = (uint32_t)length;
    }
    char error[512];
    gpu_buffer *buffer = gpu_buffer_create(d->device, values, (int)n, false, error, sizeof error);
    if (!buffer) { gfx_fail(call, "%s", error); return; }
    aqua_return(call, buffer_object_new(call, buffer_class, d, buffer));
}

static void dispatched(void *data, const char *error) {
    gfx_async *a = data;
    if (error) gfx_async_fail(a, "%s", error);
    else gfx_async_finish(a, aqua_null());
}

static void device_Dispatch(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    buffer_object *b = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, 1), buffer_class);
    if (!b || b->owner != d) { gfx_fail(call, "Expected a live buffer from this wgpu device."); return; }
    int groups = gfx_int(call, 2);
    if (aqua_failed(call)) return;
    if (groups < 1 || groups > 65535) { gfx_fail(call, "Workgroup count must be 1..65535."); return; }
    aqua_str source = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    if (gpu_buffer_released(b->buffer)) { gfx_fail(call, "wgpu buffer has been disposed."); return; }
    gfx_async *a = gfx_async_begin(call, 0);
    gpu_dispatch(d->device, source.data, b->buffer, (uint32_t)groups, dispatched, a);
    gfx_async_end(a);
}

typedef struct shader_request { device_object *device; aqua_ref device_ref; } shader_request;

static void shader_created(gpu_shader *shader, const char *error, void *data) {
    gfx_async *a = data;
    shader_request *request = gfx_async_data(a);
    if (error) { aqua_release(gfx_async_runtime(a), request->device_ref); gfx_async_fail(a, "%s", error); return; }
    shader_object *s = calloc(1, sizeof *s);
    s->owner = request->device; s->owner_ref = request->device_ref; s->shader = shader;
    gfx_async_finish(a, aqua_object_new(gfx_async_runtime(a), shader_class_w, s));
}

static void device_CreateShader(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    aqua_str vertex = gfx_text(call, 0), fragment = gfx_text(call, 1);
    if (aqua_failed(call)) return;
    gfx_async *a = gfx_async_begin(call, sizeof(shader_request));
    shader_request *request = gfx_async_data(a);
    request->device = d;
    request->device_ref = aqua_retain(aqua_call_runtime(call), aqua_self_value(call));
    gpu_shader_create(d->device, vertex.data, fragment.data, shader_created, a);
    gfx_async_end(a);
}

static void device_CreateRenderTarget(aqua_call *call) {
    device_object *d = device_self(call);
    if (!device_live(call, d)) return;
    WGPUTextureFormat format = WGPUTextureFormat_RGBA8Unorm;
    if (aqua_argc(call) == 3) {
        aqua_str name = gfx_text(call, 2);
        if (aqua_failed(call)) return;
        if (strcmp(name.data, "rgba16float") == 0) format = WGPUTextureFormat_RGBA16Float;
        else if (strcmp(name.data, "rgba8unorm") != 0) { gfx_fail(call, "Render target format must be rgba8unorm or rgba16float."); return; }
    }
    int width = gfx_dimension(call, 0), height = gfx_dimension(call, 1);
    if (aqua_failed(call)) return;
    gpu_target *target = gpu_target_create(d->device, width, height, format);
    if (!target) { gfx_fail(call, "wgpu render target creation failed."); return; }
    target_object *t = calloc(1, sizeof *t);
    t->owner = d; t->owner_ref = aqua_retain(aqua_call_runtime(call), aqua_self_value(call)); t->target = target;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), target_class, t));
}

/* ---- Windows ---- */

static void release_held_input(wgpu_window *w) {
    for (int i = 0; i < 512; i++) if (w->keys[i]) { w->keys[i] = false; w->pending_released[i] = true; }
    for (int i = 0; i < 8; i++) if (w->buttons[i]) { w->buttons[i] = false; w->pending_button_released[i] = true; }
}

static void set_pointer_lock(wgpu_window *w, bool value) {
    if (value && !w->focused) return;
    aqua_window_set_cursor_mode(w->window, value ? 0x34003 : 0x34001);
    w->locked = value;
    w->have_last = false;
    w->state.pointer_locked = value;
    w->state.mouse_dx = w->state.mouse_dy = 0;
}

static void window_event(aqua_window *window, const aqua_window_event *e, void *data) {
    (void)window;
    wgpu_window *w = data;
    switch (e->type) {
    case AQUA_EVENT_KEY:
        if (e->key < 0 || e->key >= 512) break;
        if (e->action == 1) { w->keys[e->key] = true; w->pending_pressed[e->key] = true; }
        else if (e->action == 0) { w->keys[e->key] = false; w->pending_released[e->key] = true; }
        break;
    case AQUA_EVENT_BUTTON:
        if (e->button < 0 || e->button >= 8) break;
        if (e->action == 1) { w->buttons[e->button] = true; w->pending_button_pressed[e->button] = true; }
        else { w->buttons[e->button] = false; w->pending_button_released[e->button] = true; }
        break;
    case AQUA_EVENT_SCROLL: w->wheel += e->y; break;
    case AQUA_EVENT_FOCUS:
        w->focused = e->action != 0;
        if (!w->focused) { release_held_input(w); if (w->locked) set_pointer_lock(w, false); }
        break;
    case AQUA_EVENT_CLOSE: w->close_requested = true; break;
    case AQUA_EVENT_DESTROYED: w->window = NULL; break;
    }
}

/* Publishes the input gathered since the previous poll. */
static void window_publish(wgpu_window *w) {
    memcpy(w->key_pressed, w->pending_pressed, sizeof w->key_pressed); memset(w->pending_pressed, 0, sizeof w->pending_pressed);
    memcpy(w->key_released, w->pending_released, sizeof w->key_released); memset(w->pending_released, 0, sizeof w->pending_released);
    memcpy(w->button_pressed, w->pending_button_pressed, sizeof w->button_pressed); memset(w->pending_button_pressed, 0, sizeof w->pending_button_pressed);
    memcpy(w->button_released, w->pending_button_released, sizeof w->button_released); memset(w->pending_button_released, 0, sizeof w->pending_button_released);
    int width = 0, height = 0, pw = 0, ph = 0;
    aqua_window_size(w->window, &width, &height);
    aqua_window_framebuffer_size(w->window, &pw, &ph);
    double x = 0, y = 0;
    aqua_window_cursor(w->window, &x, &y);
    double dx = w->have_last ? x - w->last_x : 0, dy = w->have_last ? y - w->last_y : 0;
    w->last_x = x; w->last_y = y; w->have_last = true;
    window_snapshot *s = &w->state;
    bool resized = width != s->width || height != s->height || pw != s->pixel_width || ph != s->pixel_height;
    bool close = w->close_requested || aqua_window_should_close(w->window);
    *s = (window_snapshot){width, height, pw, ph, x, y, dx, dy, w->wheel, w->focused, w->locked, close, resized};
    w->wheel = 0;
}

static wgpu_window *window_self(aqua_call *call) { return aqua_self(call); }

static bool window_live(aqua_call *call, wgpu_window *w) {
    if (!w->window) { gfx_fail(call, "WGPU window has been disposed."); return false; }
    return true;
}

static void window_close_now(wgpu_state *state, wgpu_window *w) {
    if (!w->window) return;
    /* The device's surface goes before the OS window. */
    if (w->device) gpu_device_dispose(w->device->device);
    aqua_window *window = w->window;
    aqua_window_unlisten(window, window_event, w);
    w->window = NULL;
    aqua_window_destroy(window);
    for (wgpu_window **p = &state->windows; *p; p = &(*p)->next) if (*p == w) { *p = w->next; break; }
}

static void window_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    wgpu_window *w = data;
    window_close_now(w->owner, w);
    if (w->device) { w->device->window = NULL; w->device = NULL; }
    free(w);
}

static void wgpu_CreateWindow(aqua_call *call) {
    int width = gfx_dimension(call, 0), height = gfx_dimension(call, 1);
    aqua_str title = gfx_text(call, 2);
    if (aqua_failed(call)) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    char *error = NULL;
    aqua_window *window = aqua_window_create(rt, width, height, title.data, 0, &error);
    if (!window) { gfx_fail(call, "%s", error ? error : "Window creation failed."); aqua_free(error); return; }
    wgpu_state *state = state_of(call);
    wgpu_window *w = calloc(1, sizeof *w);
    w->rt = rt;
    w->window = window;
    w->focused = true;
    aqua_window_listen(window, window_event, w);
    aqua_window_size(window, &w->state.width, &w->state.height);
    aqua_window_framebuffer_size(window, &w->state.pixel_width, &w->state.pixel_height);
    aqua_window_cursor(window, &w->last_x, &w->last_y);
    w->state.mouse_x = w->last_x; w->state.mouse_y = w->last_y;
    w->state.focused = true;
    w->owner = state;
    w->next = state->windows;
    state->windows = w;
    aqua_return(call, aqua_object_new(rt, window_class_w, w));
}

typedef struct poll_wait { wgpu_window *window; aqua_ref self; } poll_wait;

static double poll_tick(aqua_runtime *rt, double now, void *data) {
    (void)now;
    gfx_async *a = data;
    poll_wait *wait = gfx_async_data(a);
    if (wait->window->window) { aqua_windows_poll(rt); if (wait->window->window) window_publish(wait->window); }
    aqua_release(rt, wait->self);
    gfx_async_finish(a, aqua_null());
    return -1;
}

/* Natively the poll is immediate (presentation paces the loop) except while minimized, when nothing presents; on the
   web it waits for the next animation frame so the browser can render and deliver input. */
static void window_Poll(aqua_call *call) {
    wgpu_window *w = window_self(call);
    if (!window_live(call, w)) return;
    aqua_runtime *rt = aqua_call_runtime(call);
#if !defined(AQUA_OS_WEB)
    aqua_windows_poll(rt);
    if (!w->window) return;
    int pw = 0, ph = 0;
    aqua_window_framebuffer_size(w->window, &pw, &ph);
    if (pw > 0 && ph > 0) { window_publish(w); return; }
    double delay = 50;
#else
    double delay = 0;
#endif
    gfx_async *a = gfx_async_begin(call, sizeof(poll_wait));
    poll_wait *wait = gfx_async_data(a);
    wait->window = w;
    wait->self = aqua_retain(rt, aqua_self_value(call));
    aqua_schedule(rt, delay, poll_tick, a);
    gfx_async_end(a);
}

static bool code_argument(aqua_call *call, int *code) {
    *code = gfx_int(call, 0);
    return !aqua_failed(call);
}
#define WINDOW_QUERY(name, array, limit) \
    static void window_##name(aqua_call *call) { \
        wgpu_window *w = window_self(call); int code; \
        if (!window_live(call, w) || !code_argument(call, &code)) return; \
        aqua_return_bool(call, code >= 0 && code < limit && w->array[code]); }
WINDOW_QUERY(KeyDown, keys, 512)
WINDOW_QUERY(KeyPressed, key_pressed, 512)
WINDOW_QUERY(KeyReleased, key_released, 512)
WINDOW_QUERY(MouseDown, buttons, 8)
WINDOW_QUERY(MousePressed, button_pressed, 8)
WINDOW_QUERY(MouseReleased, button_released, 8)

static void window_SetPointerLock(aqua_call *call) {
    wgpu_window *w = window_self(call);
    if (!window_live(call, w)) return;
    bool value = gfx_bool(call, 0);
    if (!aqua_failed(call)) set_pointer_lock(w, value);
}

static void window_SetTitle(aqua_call *call) {
    wgpu_window *w = window_self(call);
    if (!window_live(call, w)) return;
    aqua_str title = gfx_text(call, 0);
    if (!aqua_failed(call)) aqua_window_set_title(w->window, title.data);
}

static void window_Close(aqua_call *call) {
    wgpu_window *w = window_self(call);
    if (!window_live(call, w)) return;
    w->close_requested = true;
    w->state.should_close = true;
}

static void window_Dispose(aqua_call *call) { window_close_now(state_of(call), window_self(call)); }

#define WINDOW_FIELD(name, kind, expression) \
    static void window_get_##name(aqua_call *call) { wgpu_window *w = window_self(call); aqua_return_##kind(call, expression); }
WINDOW_FIELD(Width, int, w->state.width)
WINDOW_FIELD(Height, int, w->state.height)
WINDOW_FIELD(PixelWidth, int, w->state.pixel_width)
WINDOW_FIELD(PixelHeight, int, w->state.pixel_height)
WINDOW_FIELD(MouseX, double, w->state.mouse_x)
WINDOW_FIELD(MouseY, double, w->state.mouse_y)
WINDOW_FIELD(MouseDeltaX, double, w->state.mouse_dx)
WINDOW_FIELD(MouseDeltaY, double, w->state.mouse_dy)
WINDOW_FIELD(Wheel, double, w->state.wheel)
WINDOW_FIELD(Focused, bool, w->state.focused)
WINDOW_FIELD(PointerLocked, bool, w->state.pointer_locked)
WINDOW_FIELD(ShouldClose, bool, w->state.should_close)
WINDOW_FIELD(Resized, bool, w->state.resized)

aqua_window *gfx_wgpu_window_of(aqua_runtime *rt, aqua_value value) {
    wgpu_window *w = aqua_object_data(rt, value, window_class_w);
    if (w) return w->window;
    return aqua_window_of(rt, value);
}

void gfx_wgpu_destroy(wgpu_state *state) {
    if (!state) return;
    while (state->windows) window_close_now(state, state->windows);
    free(state);
}

/* ---- Registration ---- */

#define WGPU_MODULE(X) X(wgpu_CreateDevice, "CreateDevice|建立裝置", 0, 1) X(wgpu_CreateWindow, "CreateWindow|建立視窗", 3, 3)
#define DEVICE_MEMBERS(X) \
    X(device_CreateBuffer, "CreateBuffer|建立緩衝區", 1, 1) X(device_CreateRenderTarget, "CreateRenderTarget|建立繪圖目標", 2, 3) \
    X(device_CreateShader, "CreateShader|建立著色器", 2, 2) X(device_CreateVertexBuffer, "CreateVertexBuffer|建立頂點緩衝區", 1, 1) \
    X(device_Dispatch, "Dispatch|派送", 3, 3) X(device_Dispose, "Dispose|釋放", 0, 0) X(device_LoadTexture, "LoadTexture|載入紋理", 6, 6) \
    X(device_Poll, "Poll|輪詢", 0, 0) X(device_Present, "Present|呈現", 1, 1)
#define BUFFER_MEMBERS(X) \
    X(buffer_Dispose, "Dispose|釋放", 0, 0) X(buffer_Read, "Read|讀取", 0, 0) X(buffer_ReadRange, "ReadRange|讀取範圍", 2, 2) \
    X(buffer_Write, "Write|寫入", 1, 1) X(buffer_WriteRange, "WriteRange|寫入範圍", 2, 2)
#define SHADER_MEMBERS(X) \
    X(shader_Dispose, "Dispose|釋放", 0, 0) X(shader_Set, "Set|設定", 2, 2) X(shader_SetBuffer, "SetBuffer|設定緩衝區", 2, 2) \
    X(shader_SetInt, "SetInt|設定整數", 2, 2) X(shader_SetTexture, "SetTexture|設定紋理", 2, 2)
#define TARGET_MEMBERS(X) \
    X(target_Clear, "Clear|清除", 1, 1) X(target_Dispose, "Dispose|釋放", 0, 0) X(target_Draw, "Draw|繪製", 2, 2) \
    X(target_DrawBuffer, "DrawBuffer|繪製緩衝區", 2, 4) X(target_DrawProcedural, "DrawProcedural|程序化繪製", 2, 3) \
    X(target_GetTexture, "GetTexture|取得紋理", 0, 0) X(target_ReadPixels, "ReadPixels|讀取像素", 0, 0) X(target_Save, "Save|儲存", 1, 1) \
    X(target_SetState, "SetState|設定狀態", 1, 1)
#define TEXTURE_MEMBERS(X) X(texture_Dispose, "Dispose|釋放", 0, 0)
#define VERTEX_MEMBERS(X) X(buffer_Dispose, "Dispose|釋放", 0, 0) X(vertex_Write, "Write|寫入", 1, 1) X(vertex_WriteRange, "WriteRange|寫入範圍", 2, 2)
#define WINDOW_MEMBERS(X) \
    X(window_Close, "Close|關閉", 0, 0) X(window_Dispose, "Dispose|釋放", 0, 0) X(window_KeyDown, "KeyDown|按鍵按住", 1, 1) \
    X(window_KeyPressed, "KeyPressed|按鍵按下", 1, 1) X(window_KeyReleased, "KeyReleased|按鍵放開", 1, 1) \
    X(window_MouseDown, "MouseDown|滑鼠按住", 1, 1) X(window_MousePressed, "MousePressed|滑鼠按下", 1, 1) \
    X(window_MouseReleased, "MouseReleased|滑鼠放開", 1, 1) X(window_Poll, "Poll|輪詢", 0, 0) \
    X(window_SetPointerLock, "SetPointerLock|設定指標鎖定", 1, 1) X(window_SetTitle, "SetTitle|設定標題", 1, 1)

/* Each member runs through a wrapper naming it for failures ("Dispatch: ..."); members shared by classes get one. */
#define WRAPPER(function, names, minimum, maximum) GFX_WRAP("", function, names, function)
WGPU_MODULE(WRAPPER)
DEVICE_MEMBERS(WRAPPER)
BUFFER_MEMBERS(WRAPPER)
SHADER_MEMBERS(WRAPPER)
TARGET_MEMBERS(WRAPPER)
TEXTURE_MEMBERS(WRAPPER)
WINDOW_MEMBERS(WRAPPER)
GFX_WRAP("", vertex_Write, "Write|寫入", vertex_Write)
GFX_WRAP("", vertex_WriteRange, "WriteRange|寫入範圍", vertex_WriteRange)
#undef WRAPPER

void gfx_wgpu_register(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "WGPU|wgpu");
    device_class = aqua_define_class(library, "WGPU.Device", device_finalize);
    buffer_class = aqua_define_class(library, "WGPU.Buffer", buffer_finalize);
    vertex_class = aqua_define_class(library, "WGPU.VertexBuffer", buffer_finalize);
    shader_class_w = aqua_define_class(library, "WGPU.Shader", shader_finalize_w);
    target_class = aqua_define_class(library, "WGPU.Target", target_finalize);
    texture_class = aqua_define_class(library, "WGPU.Texture", texture_finalize);
    window_class_w = aqua_define_class(library, "WGPU.Window", window_finalize);
#if defined(AQUA_OS_WEB)
    aqua_define_string(module, "Backend", "webgpu");
#else
    aqua_define_string(module, "Backend", "wgpu-native");
#endif
#define DEFINE_ON(owner, function, names, minimum, maximum) aqua_define_function(owner, names, wrapper_##function, gfx_signature(minimum, maximum));
#define ON_MODULE(function, names, minimum, maximum) DEFINE_ON(module, function, names, minimum, maximum)
#define ON_DEVICE(function, names, minimum, maximum) DEFINE_ON(device_class, function, names, minimum, maximum)
#define ON_BUFFER(function, names, minimum, maximum) DEFINE_ON(buffer_class, function, names, minimum, maximum)
#define ON_SHADER(function, names, minimum, maximum) DEFINE_ON(shader_class_w, function, names, minimum, maximum)
#define ON_TARGET(function, names, minimum, maximum) DEFINE_ON(target_class, function, names, minimum, maximum)
#define ON_TEXTURE(function, names, minimum, maximum) DEFINE_ON(texture_class, function, names, minimum, maximum)
#define ON_VERTEX(function, names, minimum, maximum) DEFINE_ON(vertex_class, function, names, minimum, maximum)
#define ON_WINDOW(function, names, minimum, maximum) DEFINE_ON(window_class_w, function, names, minimum, maximum)
    WGPU_MODULE(ON_MODULE)
    DEVICE_MEMBERS(ON_DEVICE)
    BUFFER_MEMBERS(ON_BUFFER)
    SHADER_MEMBERS(ON_SHADER)
    TARGET_MEMBERS(ON_TARGET)
    TEXTURE_MEMBERS(ON_TEXTURE)
    VERTEX_MEMBERS(ON_VERTEX)
    WINDOW_MEMBERS(ON_WINDOW)
    aqua_define_property(device_class, "Backend", device_Backend);
    aqua_define_property(buffer_class, "Length", buffer_Length);
    aqua_define_property(vertex_class, "Length", buffer_Length);
    aqua_define_property(target_class, "Width", target_Width);
    aqua_define_property(target_class, "Height", target_Height);
#define WINDOW_PROPERTY(name) aqua_define_property(window_class_w, #name, window_get_##name);
    WINDOW_PROPERTY(Width) WINDOW_PROPERTY(Height) WINDOW_PROPERTY(PixelWidth) WINDOW_PROPERTY(PixelHeight)
    WINDOW_PROPERTY(MouseX) WINDOW_PROPERTY(MouseY) WINDOW_PROPERTY(MouseDeltaX) WINDOW_PROPERTY(MouseDeltaY)
    WINDOW_PROPERTY(Wheel) WINDOW_PROPERTY(Focused) WINDOW_PROPERTY(PointerLocked) WINDOW_PROPERTY(ShouldClose)
    WINDOW_PROPERTY(Resized)
}
