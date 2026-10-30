/* Render targets: color attachments (sampled by other draws as images; usually one, up to four for shaders with several
   outputs, optionally mipmapped), a depth/stencil attachment and, while smooth, a 4x multisampled color attachment
   resolved into the first. Draws record into one open pass; their vertices and
   uniform snapshots are appended to per-target CPU arenas that are written to the GPU buffers when the pass is submitted
   (queue writes are ordered before that submission), so a frame of draws costs one upload per arena. */
#include "gpu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define UNIFORM_ALIGNMENT 256u
#define ARENA_LIMIT (64u * 1024 * 1024)

typedef struct arena { WGPUBuffer buffer; uint8_t *data; size_t used, capacity; } arena;
typedef struct cached_group { WGPUBuffer buffer; WGPUTextureView view; WGPUSampler sampler; WGPUBindGroup group; } cached_group;
/* A Processing draw not yet recorded: later draws with the same pipeline, bindings, scissor and uniform snapshot whose
   vertices follow its own in the arena extend it, so a run of shapes in one style becomes one GPU draw. */
typedef struct pending_draw {
    size_t bytes;                       /* vertex bytes; 0 when there is none */
    WGPURenderPipeline pipeline;
    WGPUBindGroup group, user, textures;
    WGPUTextureView view;
    int sampling;
    WGPUBuffer vertex_buffer;
    size_t vertex_offset;
    uint32_t uniform_offset, stencil;
    const uint8_t *uniforms;            /* the snapshot in its arena (arenas stay allocated until the pass is submitted) */
    gpu_rect scissor;
} pending_draw;

struct gpu_target {
    gpu_resource link;
    gpu_texture *color, *depth, *msaa;
    gpu_texture *extra[3];              /* color attachments 1..colors-1 */
    int colors, mip_levels;
    WGPUTextureFormat format;
    uint32_t samples;
    WGPUCommandEncoder encoder;
    WGPURenderPassEncoder pass;
    arena vertices, uniforms;
    arena *spent; size_t spent_count, spent_capacity;
    WGPUBindGroup *groups; size_t group_count, group_capacity;
    cached_group cache[16]; int cache_count;
    pending_draw pending;
    gpu_state state;
    bool released;
};

static void begin(gpu_target *t, const float *clear, bool clear_depth, bool clear_stencil);

/* Records the pending Processing draw into the open pass. */
static void record_pending(gpu_target *t) {
    pending_draw *p = &t->pending;
    if (!p->bytes) return;
    wgpuRenderPassEncoderSetPipeline(t->pass, p->pipeline);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 0, p->group, 1, &p->uniform_offset);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 1, p->user, 0, NULL);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 2, p->textures, 0, NULL);
    wgpuRenderPassEncoderSetStencilReference(t->pass, p->stencil == 2 ? 0u : 1u);
    wgpuRenderPassEncoderSetVertexBuffer(t->pass, 0, p->vertex_buffer, p->vertex_offset, p->bytes);
    wgpuRenderPassEncoderSetScissorRect(t->pass, (uint32_t)p->scissor.x, (uint32_t)p->scissor.y, (uint32_t)p->scissor.width, (uint32_t)p->scissor.height);
    if (p->scissor.width > 0 && p->scissor.height > 0) wgpuRenderPassEncoderDraw(t->pass, (uint32_t)(p->bytes / (GPU_FLOATS_PER_VERTEX * 4)), 1, 0, 0);
    p->bytes = 0;
}

static void create_attachments(gpu_target *t, int width, int height) {
    gpu_device *d = t->link.device;
    t->color = gpu_texture_attachment(d, width, height, t->format, 1, t->mip_levels);
    gpu_texture_set_source(t->color, t);
    for (int i = 1; i < t->colors; i++) {
        t->extra[i - 1] = gpu_texture_attachment(d, width, height, t->format, 1, t->mip_levels);
        if (t->extra[i - 1]) gpu_texture_set_source(t->extra[i - 1], t);
    }
    t->depth = gpu_texture_attachment(d, width, height, WGPUTextureFormat_Depth24PlusStencil8, 1, 1);
    t->samples = 1;
}

gpu_target *gpu_target_create(gpu_device *device, int width, int height, WGPUTextureFormat format) {
    return gpu_target_create_multiple(device, width, height, format, 1, 1);
}

gpu_target *gpu_target_create_multiple(gpu_device *device, int width, int height, WGPUTextureFormat format, int colors, int mip_levels) {
    gpu_target *t = calloc(1, sizeof *t);
    t->link.next = t->link.previous = &t->link;
    t->format = format;
    t->colors = colors; t->mip_levels = mip_levels;
    gpu_device_link(device, &t->link, GPU_TARGET);
    create_attachments(t, width, height);
    bool complete = t->color && t->depth;
    for (int i = 1; i < colors; i++) complete = complete && t->extra[i - 1];
    if (!complete) { gpu_target_free(t); return NULL; }
    static const float transparent[4] = {0, 0, 0, 0};
    gpu_target_clear(t, transparent);
    return t;
}

static void push_group(gpu_target *t, WGPUBindGroup group) {
    if (t->group_count == t->group_capacity) {
        t->group_capacity = t->group_capacity ? t->group_capacity * 2 : 32;
        t->groups = realloc(t->groups, t->group_capacity * sizeof *t->groups);
    }
    t->groups[t->group_count++] = group;
}

void gpu_target_flush(gpu_target *t) {
    if (!t->pass) return;
    gpu_device *d = t->link.device;
    record_pending(t);
    wgpuRenderPassEncoderEnd(t->pass);
    wgpuRenderPassEncoderRelease(t->pass);
    t->pass = NULL;
    WGPUQueue queue = gpu_device_queue(d);
    for (size_t i = 0; i < t->spent_count; i++) if (t->spent[i].used) wgpuQueueWriteBuffer(queue, t->spent[i].buffer, 0, t->spent[i].data, (t->spent[i].used + 3) & ~(size_t)3);
    if (t->vertices.used) wgpuQueueWriteBuffer(queue, t->vertices.buffer, 0, t->vertices.data, (t->vertices.used + 3) & ~(size_t)3);
    if (t->uniforms.used) wgpuQueueWriteBuffer(queue, t->uniforms.buffer, 0, t->uniforms.data, t->uniforms.used);
    WGPUCommandEncoder encoder = t->encoder;
    t->encoder = NULL;
    gpu_device_submit(d, encoder);
    /* The recorded pass held the bind groups and replaced arenas; release them now that it is submitted. */
    for (size_t i = 0; i < t->group_count; i++) wgpuBindGroupRelease(t->groups[i]);
    t->group_count = 0;
    t->cache_count = 0;
    for (size_t i = 0; i < t->spent_count; i++) { wgpuBufferRelease(t->spent[i].buffer); free(t->spent[i].data); }
    t->spent_count = 0;
    t->vertices.used = 0;
    t->uniforms.used = 0;
}

static void begin(gpu_target *t, const float *clear, bool clear_depth, bool clear_stencil) {
    if (t->pass) return;
    gpu_device *d = t->link.device;
    t->encoder = wgpuDeviceCreateCommandEncoder(gpu_device_handle(d), NULL);
    WGPURenderPassColorAttachment colors[4];
    for (int i = 0; i < t->colors; i++) {
        WGPURenderPassColorAttachment color = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
        color.view = i ? gpu_texture_attachment_view(t->extra[i - 1]) : t->msaa ? gpu_texture_view(t->msaa) : gpu_texture_attachment_view(t->color);
        color.resolveTarget = !i && t->msaa ? gpu_texture_view(t->color) : NULL;
        color.loadOp = clear ? WGPULoadOp_Clear : WGPULoadOp_Load;
        color.storeOp = WGPUStoreOp_Store;
        if (clear) {
            color.clearValue.r = clear[0] * clear[3]; color.clearValue.g = clear[1] * clear[3];
            color.clearValue.b = clear[2] * clear[3]; color.clearValue.a = clear[3];
        }
        colors[i] = color;
    }
    WGPURenderPassDepthStencilAttachment depth = WGPU_RENDER_PASS_DEPTH_STENCIL_ATTACHMENT_INIT;
    depth.view = gpu_texture_view(t->depth);
    depth.depthLoadOp = clear || clear_depth ? WGPULoadOp_Clear : WGPULoadOp_Load;
    depth.depthStoreOp = WGPUStoreOp_Store;
    depth.depthClearValue = 1;
    depth.stencilLoadOp = clear || clear_depth || clear_stencil ? WGPULoadOp_Clear : WGPULoadOp_Load;
    depth.stencilStoreOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor descriptor = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
    descriptor.colorAttachmentCount = (size_t)t->colors; descriptor.colorAttachments = colors;
    descriptor.depthStencilAttachment = &depth;
    t->pass = wgpuCommandEncoderBeginRenderPass(t->encoder, &descriptor);
}

void gpu_target_clear(gpu_target *t, const float rgba[4]) {
    if (t->released) return;
    gpu_target_flush(t);
    begin(t, rgba, false, false);
}

void gpu_target_clear_stencil(gpu_target *t) {
    if (t->released) return;
    gpu_target_flush(t);
    begin(t, NULL, false, true);
}

void gpu_target_configure(gpu_target *t, int width, int height, bool smooth) {
    if (t->released) return;
    gpu_device *d = t->link.device;
    uint32_t wanted = smooth ? 4 : 1;
    if (width != gpu_texture_width(t->color) || height != gpu_texture_height(t->color)) {
        gpu_target_flush(t);
        gpu_texture_free(t->color); gpu_texture_free(t->depth); gpu_texture_free(t->msaa);
        for (int i = 1; i < t->colors; i++) gpu_texture_free(t->extra[i - 1]);
        t->msaa = NULL;
        create_attachments(t, width, height);
        static const float transparent[4] = {0, 0, 0, 0};
        gpu_target_clear(t, transparent);
    }
    if (t->samples == wanted) return;
    gpu_target_flush(t);
    gpu_texture_free(t->depth); gpu_texture_free(t->msaa);
    t->msaa = NULL;
    t->samples = wanted;
    t->depth = gpu_texture_attachment(d, width, height, WGPUTextureFormat_Depth24PlusStencil8, wanted, 1);
    if (wanted == 4) {
        t->msaa = gpu_texture_attachment(d, width, height, t->format, 4, 1);
        gpu_device_blit(d, gpu_texture_view(t->color), gpu_texture_view(t->msaa), t->format, 4);
    }
    begin(t, NULL, true, false);
}

/* Appends `size` bytes at an `alignment` offset of the arena, replacing a full arena with a larger one. */
static bool append(gpu_target *t, arena *a, const void *data, size_t size, size_t alignment, WGPUBufferUsage usage, size_t minimum, size_t *offset) {
    if (size > ARENA_LIMIT) return false;
    size_t at = (a->used + alignment - 1) / alignment * alignment;
    if (!a->buffer || at + size > a->capacity) {
        if (a->buffer) {
            if (t->spent_count == t->spent_capacity) {
                t->spent_capacity = t->spent_capacity ? t->spent_capacity * 2 : 4;
                t->spent = realloc(t->spent, t->spent_capacity * sizeof *t->spent);
            }
            t->spent[t->spent_count++] = *a;
        }
        size_t capacity = a->capacity * 2;
        if (capacity < minimum) capacity = minimum;
        if (capacity < size) capacity = size;
        if (capacity > ARENA_LIMIT) capacity = ARENA_LIMIT;
        a->buffer = gpu_device_buffer(t->link.device, capacity, usage, NULL);
        a->data = malloc(capacity);
        a->capacity = capacity;
        a->used = 0;
        at = 0;
        if (!a->buffer || !a->data) return false;
    }
    memcpy(a->data + at, data, size);
    a->used = at + size;
    *offset = at;
    return true;
}

static WGPUBindGroup drawing_group(gpu_target *t, gpu_texture *texture, int sampling) {
    gpu_device *d = t->link.device;
    WGPUTextureView view = gpu_texture_view(texture ? texture : gpu_device_white(d));
    WGPUSampler sampler = gpu_device_sampler_for(d, sampling);
    for (int i = 0; i < t->cache_count; i++)
        if (t->cache[i].buffer == t->uniforms.buffer && t->cache[i].view == view && t->cache[i].sampler == sampler) return t->cache[i].group;
    WGPUBindGroupEntry entries[3] = {WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT};
    entries[0].binding = 0; entries[0].buffer = t->uniforms.buffer; entries[0].offset = 0; entries[0].size = GPU_UNIFORM_BYTES;
    entries[1].binding = 1; entries[1].textureView = view;
    entries[2].binding = 2; entries[2].sampler = sampler;
    WGPUBindGroupDescriptor descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    descriptor.layout = gpu_device_drawing_layout(d); descriptor.entryCount = 3; descriptor.entries = entries;
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(gpu_device_handle(d), &descriptor);
    push_group(t, group);
    if (t->cache_count == 16) { memmove(t->cache, t->cache + 1, 15 * sizeof *t->cache); t->cache_count = 15; }
    t->cache[t->cache_count++] = (cached_group){t->uniforms.buffer, view, sampler, group};
    return group;
}

gpu_rect gpu_scissor(int width, int height, const float *clip) {
    gpu_rect r = {0, 0, width, height};
    if (!clip) return r;
    float x0 = clip[0] < 0 ? 0 : clip[0] > (float)width ? (float)width : clip[0];
    float y0 = clip[1] < 0 ? 0 : clip[1] > (float)height ? (float)height : clip[1];
    float x1 = clip[0] + clip[2], y1 = clip[1] + clip[3];
    x1 = x1 < 0 ? 0 : x1 > (float)width ? (float)width : x1;
    y1 = y1 < 0 ? 0 : y1 > (float)height ? (float)height : y1;
    r.x = (int)x0; r.y = (int)y0;
    int right = (int)x1, bottom = (int)y1;
    r.width = right - r.x > 0 ? right - r.x : 0;
    r.height = bottom - r.y > 0 ? bottom - r.y : 0;
    return r;
}

static bool live(gpu_target *t, char *error, size_t error_size) {
    if (t->released) { snprintf(error, error_size, "wgpu render target has been disposed."); return false; }
    return true;
}

/* Maps the vertices just appended at `offset` through a row-vector model matrix: positions by it, normals by the
   inverse transpose of its 3x3 part (left unnormalized; shaders normalize). */
static void transform_vertices(uint8_t *at, size_t count, const float *m) {
    float a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
    float c00 = e * i - f * h, c01 = f * g - d * i, c02 = d * h - e * g;
    float c10 = c * h - b * i, c11 = a * i - c * g, c12 = b * g - a * h;
    float c20 = b * f - c * e, c21 = c * d - a * f, c22 = a * e - b * d;
    float det = a * c00 + b * c01 + c * c02, k = det != 0 ? 1 / det : 1;
    for (size_t v = 0; v < count; v++) {
        float *q = (float *)(at + v * GPU_FLOATS_PER_VERTEX * 4);
        float x = q[0], y = q[1], z = q[2], nx = q[3], ny = q[4], nz = q[5];
        q[0] = x * m[0] + y * m[4] + z * m[8] + m[12];
        q[1] = x * m[1] + y * m[5] + z * m[9] + m[13];
        q[2] = x * m[2] + y * m[6] + z * m[10] + m[14];
        q[3] = (nx * c00 + ny * c01 + nz * c02) * k;
        q[4] = (nx * c10 + ny * c11 + nz * c12) * k;
        q[5] = (nx * c20 + ny * c21 + nz * c22) * k;
    }
}

bool gpu_target_draw(gpu_target *t, gpu_shader *shader, const float *vertices, size_t floats, const float *uniforms, const float *model,
                     int mode, int blend, int depth_mode, int cull, const gpu_rect *scissor, gpu_texture *texture, int sampling, int stencil,
                     char *error, size_t error_size) {
    if (!live(t, error, error_size)) return false;
    gpu_device *d = t->link.device;
    if (gpu_shader_device(shader) != d || (texture && gpu_texture_device(texture) != d)) { snprintf(error, error_size, "wgpu resources must belong to the same device."); return false; }
    if (floats == 0 || floats % GPU_FLOATS_PER_VERTEX) { snprintf(error, error_size, "Vertices need 12 floats each: position, normal, UV, RGBA."); return false; }
    WGPUBindGroup textures = gpu_shader_texture_group(shader, t, error, error_size);
    if (!textures) return false;
    WGPURenderPipeline pipeline = gpu_shader_pipeline(shader, mode, blend, depth_mode, t->samples, stencil, t->format, t->colors, cull, true, error, error_size);
    if (!pipeline) return false;
    begin(t, NULL, false, false);
    if (!texture) sampling = 0;
    size_t vertex_offset, uniform_offset;
    if (!append(t, &t->vertices, vertices, floats * 4, 4, WGPUBufferUsage_Vertex, 256 * 1024, &vertex_offset)) {
        snprintf(error, error_size, "wgpu buffer size must be 1..64 MiB.");
        return false;
    }
    if (model) transform_vertices(t->vertices.data + vertex_offset, floats / GPU_FLOATS_PER_VERTEX, model);
    WGPUBindGroup user = gpu_shader_user_group(shader);
    WGPUTextureView view = gpu_texture_view(texture ? texture : gpu_device_white(d));
    gpu_rect r = scissor ? *scissor : (gpu_rect){0, 0, gpu_texture_width(t->color), gpu_texture_height(t->color)};
    pending_draw *p = &t->pending;
    if (p->bytes && !stencil && !p->stencil && p->pipeline == pipeline && p->view == view && p->sampling == sampling && p->user == user && p->textures == textures &&
        p->vertex_buffer == t->vertices.buffer && p->vertex_offset + p->bytes == vertex_offset && memcmp(&p->scissor, &r, sizeof r) == 0 &&
        memcmp(p->uniforms, uniforms, GPU_UNIFORM_BYTES) == 0) {
        p->bytes += floats * 4;
        return true;
    }
    record_pending(t);
    if (!append(t, &t->uniforms, uniforms, GPU_UNIFORM_BYTES, UNIFORM_ALIGNMENT, WGPUBufferUsage_Uniform, 64 * 1024, &uniform_offset)) {
        snprintf(error, error_size, "wgpu buffer size must be 1..64 MiB.");
        return false;
    }
    *p = (pending_draw){floats * 4, pipeline, drawing_group(t, texture, sampling), user, textures, view, sampling, t->vertices.buffer, vertex_offset,
                        (uint32_t)uniform_offset, (uint32_t)stencil, t->uniforms.data + uniform_offset, r};
    const char *failure = gpu_device_take_error(d);
    if (failure) { snprintf(error, error_size, "%s", failure); return false; }
    return true;
}

bool gpu_target_draw_retained(gpu_target *t, gpu_shader *shader, gpu_buffer *vertices, int first, int count, const gpu_rect *scissor,
                              char *error, size_t error_size) {
    if (!live(t, error, error_size)) return false;
    gpu_device *d = t->link.device;
    if (gpu_shader_device(shader) != d || (vertices && gpu_buffer_device(vertices) != d)) { snprintf(error, error_size, "wgpu resources must belong to the same device."); return false; }
    if (vertices && gpu_buffer_released(vertices)) { snprintf(error, error_size, "wgpu vertex buffer has been disposed."); return false; }
    WGPUBindGroup textures = gpu_shader_texture_group(shader, t, error, error_size);
    if (!textures) return false;
    WGPURenderPipeline pipeline = gpu_shader_pipeline(shader, 4, t->state.blend, t->state.depth, t->samples, 0, t->format, t->colors, t->state.cull, vertices != NULL, error, error_size);
    if (!pipeline) return false;
    begin(t, NULL, false, false);
    record_pending(t);
    uint32_t zero = 0;
    wgpuRenderPassEncoderSetPipeline(t->pass, pipeline);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 0, gpu_device_identity_group(d), 1, &zero);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 1, gpu_shader_user_group(shader), 0, NULL);
    wgpuRenderPassEncoderSetBindGroup(t->pass, 2, textures, 0, NULL);
    if (vertices) wgpuRenderPassEncoderSetVertexBuffer(t->pass, 0, gpu_buffer_handle(vertices), 0, (uint64_t)gpu_buffer_length(vertices) * 4);
    gpu_rect r = scissor ? *scissor : (gpu_rect){0, 0, gpu_texture_width(t->color), gpu_texture_height(t->color)};
    wgpuRenderPassEncoderSetScissorRect(t->pass, (uint32_t)r.x, (uint32_t)r.y, (uint32_t)r.width, (uint32_t)r.height);
    if (r.width > 0 && r.height > 0) wgpuRenderPassEncoderDraw(t->pass, (uint32_t)count, 1, (uint32_t)first, 0);
    const char *failure = gpu_device_take_error(d);
    if (failure) { snprintf(error, error_size, "%s", failure); return false; }
    return true;
}

void gpu_target_prepare(gpu_target *t, gpu_shader *shader, bool vertex_buffer, gpu_prepare_done done, void *data) {
    if (t->released) { done(data, "wgpu render target has been disposed."); return; }
    gpu_shader_prepare(shader, 4, t->state.blend, t->state.depth, t->samples, 0, t->format, t->colors, t->state.cull, vertex_buffer, done, data);
}

/* ---- Readback ---- */

typedef struct readback {
    gpu_target *target;
    WGPUBuffer buffer;
    int width, height;
    uint32_t row;
    gpu_pixels_done done;
    void *data;
    volatile bool finished;
    bool waiting, ok;
} readback;

static void deliver(readback *r) {
    if (!r->ok) {
        wgpuBufferRelease(r->buffer);
        r->done(r->data, NULL, 0, 0, "wgpu buffer readback failed.");
        return;
    }
    size_t size = (size_t)r->row * (size_t)r->height;
    const uint8_t *padded = wgpuBufferGetConstMappedRange(r->buffer, 0, size);
    uint8_t *pixels = malloc((size_t)r->width * (size_t)r->height * 4 + 1);
    if (padded) for (int y = 0; y < r->height; y++) memcpy(pixels + (size_t)y * (size_t)r->width * 4, padded + (size_t)y * r->row, (size_t)r->width * 4);
    wgpuBufferUnmap(r->buffer);
    wgpuBufferRelease(r->buffer);
    /* The attachment holds premultiplied colors; scripts see straight alpha. */
    for (size_t i = 0, n = (size_t)r->width * (size_t)r->height * 4; i < n; i += 4) {
        unsigned a = pixels[i + 3];
        if (a > 0 && a < 255) for (int c = 0; c < 3; c++) { unsigned v = (pixels[i + c] * 255u + a / 2) / a; pixels[i + c] = (uint8_t)(v > 255 ? 255 : v); }
    }
    r->done(r->data, padded ? pixels : NULL, r->width, r->height, padded ? NULL : "wgpu buffer readback failed.");
    free(pixels);
}

static void on_mapped(WGPUMapAsyncStatus status, WGPUStringView message, void *user, void *unused) {
    (void)message; (void)unused;
    readback *r = user;
    r->ok = status == WGPUMapAsyncStatus_Success;
    r->finished = true;
    if (!r->waiting) { deliver(r); free(r); }
}

void gpu_target_read(gpu_target *t, gpu_pixels_done done, void *data) {
    if (t->released) { done(data, NULL, 0, 0, "wgpu render target has been disposed."); return; }
    if (t->format != WGPUTextureFormat_RGBA8Unorm) { done(data, NULL, 0, 0, "Tone-map HDR targets into rgba8unorm before reading pixels."); return; }
    gpu_device *d = t->link.device;
    gpu_device_flush(d);
    readback *r = calloc(1, sizeof *r);
    r->target = t; r->done = done; r->data = data;
    r->width = gpu_texture_width(t->color); r->height = gpu_texture_height(t->color);
    r->row = ((uint32_t)r->width * 4 + 255) / 256 * 256;
    WGPUBufferDescriptor descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    descriptor.size = (uint64_t)r->row * (uint64_t)r->height;
    descriptor.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    r->buffer = wgpuDeviceCreateBuffer(gpu_device_handle(d), &descriptor);
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(gpu_device_handle(d), NULL);
    WGPUTexelCopyTextureInfo source = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    source.texture = gpu_texture_handle(t->color); source.aspect = WGPUTextureAspect_All;
    WGPUTexelCopyBufferInfo destination = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    destination.buffer = r->buffer; destination.layout.bytesPerRow = r->row; destination.layout.rowsPerImage = (uint32_t)r->height;
    WGPUExtent3D extent = {(uint32_t)r->width, (uint32_t)r->height, 1};
    wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
    gpu_device_submit(d, encoder);
    WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE; callback.callback = on_mapped; callback.userdata1 = r;
#if defined(AQUA_OS_WEB)
    wgpuBufferMapAsync(r->buffer, WGPUMapMode_Read, 0, (size_t)descriptor.size, callback);
#else
    r->waiting = true;
    wgpuBufferMapAsync(r->buffer, WGPUMapMode_Read, 0, (size_t)descriptor.size, callback);
    gpu_device_wait(d, &r->finished);
    deliver(r);
    free(r);
#endif
}

/* ---- State and lifetime ---- */

void gpu_target_set_state(gpu_target *t, gpu_state state) { t->state = state; }
gpu_state gpu_target_state(const gpu_target *t) { return t->state; }
bool gpu_target_released(const gpu_target *t) { return !t || t->released; }
int gpu_target_width(const gpu_target *t) { return gpu_texture_width(t->color); }
int gpu_target_height(const gpu_target *t) { return gpu_texture_height(t->color); }
WGPUTextureFormat gpu_target_format(const gpu_target *t) { return t->format; }
gpu_texture *gpu_target_color(gpu_target *t) { return t->color; }
gpu_texture *gpu_target_color_at(gpu_target *t, int index) { return index ? t->extra[index - 1] : t->color; }
int gpu_target_colors(const gpu_target *t) { return t->colors; }

void gpu_target_release_handles(gpu_target *t) {
    if (t->released) return;
    gpu_target_flush(t);
    t->released = true;
    if (t->vertices.buffer) wgpuBufferRelease(t->vertices.buffer);
    if (t->uniforms.buffer) wgpuBufferRelease(t->uniforms.buffer);
    free(t->vertices.data); free(t->uniforms.data);
    memset(&t->vertices, 0, sizeof t->vertices);
    memset(&t->uniforms, 0, sizeof t->uniforms);
    /* The attachments are resources of their own on the device list: their handles go with the device or below. */
    if (t->color) gpu_texture_release_handles(t->color);
    if (t->depth) gpu_texture_release_handles(t->depth);
    if (t->msaa) gpu_texture_release_handles(t->msaa);
    for (int i = 1; i < t->colors; i++) if (t->extra[i - 1]) gpu_texture_release_handles(t->extra[i - 1]);
}

void gpu_target_free(gpu_target *t) {
    if (!t) return;
    gpu_target_release_handles(t);
    gpu_texture_free(t->color); gpu_texture_free(t->depth); gpu_texture_free(t->msaa);
    for (int i = 1; i < t->colors; i++) gpu_texture_free(t->extra[i - 1]);
    free(t->spent);
    free(t->groups);
    gpu_device_unlink(&t->link);
    free(t);
}
