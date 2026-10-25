/* Float32 storage buffers (compute and shader storage at group 2) and retained vertex buffers, their writes and
   readback, and WGSL compute dispatch with a small per-device cache of compiled kernels. */
#include "gpu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct gpu_buffer {
    gpu_resource link;
    WGPUBuffer handle;
    int length;
    bool vertex, released;
};

gpu_buffer *gpu_buffer_create(gpu_device *device, const float *values, int length, bool vertex, char *error, size_t error_size) {
    uint64_t size = (uint64_t)length * 4;
    if (length < 1 || size > 64u * 1024 * 1024) { snprintf(error, error_size, "wgpu buffer size must be 1..64 MiB."); return NULL; }
    gpu_buffer *b = calloc(1, sizeof *b);
    b->link.next = b->link.previous = &b->link;
    b->length = length;
    b->vertex = vertex;
    WGPUBufferUsage usage = vertex ? WGPUBufferUsage_Vertex : WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc;
    b->handle = gpu_device_buffer(device, size, usage, values);
    gpu_device_link(device, &b->link, GPU_BUFFER);
    const char *failure = gpu_device_take_error(device);
    if (failure || !b->handle) {
        snprintf(error, error_size, "%s", failure ? failure : "wgpu buffer creation failed.");
        gpu_buffer_free(b);
        return NULL;
    }
    return b;
}

void gpu_buffer_release_handles(gpu_buffer *b) {
    if (b->handle) wgpuBufferRelease(b->handle);
    b->handle = NULL;
    b->released = true;
}

void gpu_buffer_free(gpu_buffer *b) {
    if (!b) return;
    if (b->link.device && !gpu_device_disposed(b->link.device) && !b->released) gpu_device_flush(b->link.device);
    gpu_buffer_release_handles(b);
    gpu_device_unlink(&b->link);
    free(b);
}

bool gpu_buffer_released(const gpu_buffer *b) { return !b || b->released; }
int gpu_buffer_length(const gpu_buffer *b) { return b->length; }
bool gpu_buffer_vertex(const gpu_buffer *b) { return b->vertex; }
gpu_device *gpu_buffer_device(const gpu_buffer *b) { return b->link.device; }
WGPUBuffer gpu_buffer_handle(const gpu_buffer *b) { return b->handle; }

void gpu_buffer_write(gpu_buffer *b, int offset, const float *values, int count) {
    if (b->released) return;
    gpu_device *d = b->link.device;
    gpu_device_flush(d); /* draws recorded earlier read the previous contents */
    wgpuQueueWriteBuffer(gpu_device_queue(d), b->handle, (uint64_t)offset * 4, values, (size_t)count * 4);
}

typedef struct read_operation {
    WGPUBuffer staging;
    int count;
    gpu_floats_done done;
    void *data;
    volatile bool finished;
    bool waiting, ok;
} read_operation;

static void deliver(read_operation *r) {
    if (!r->ok) { wgpuBufferRelease(r->staging); r->done(r->data, NULL, 0, "wgpu buffer readback failed: Error"); return; }
    const float *values = wgpuBufferGetConstMappedRange(r->staging, 0, (size_t)r->count * 4);
    float *copy = malloc((size_t)r->count * 4 + 4);
    if (values) memcpy(copy, values, (size_t)r->count * 4);
    wgpuBufferUnmap(r->staging);
    wgpuBufferRelease(r->staging);
    r->done(r->data, values ? copy : NULL, r->count, values ? NULL : "wgpu buffer readback failed: Error");
    free(copy);
}

static void on_mapped(WGPUMapAsyncStatus status, WGPUStringView message, void *user, void *unused) {
    (void)message; (void)unused;
    read_operation *r = user;
    r->ok = status == WGPUMapAsyncStatus_Success;
    r->finished = true;
    if (!r->waiting) { deliver(r); free(r); }
}

void gpu_buffer_read(gpu_buffer *b, int offset, int count, gpu_floats_done done, void *data) {
    if (b->released) { done(data, NULL, 0, "wgpu buffer has been disposed."); return; }
    gpu_device *d = b->link.device;
    gpu_device_flush(d);
    read_operation *r = calloc(1, sizeof *r);
    r->count = count; r->done = done; r->data = data;
    /* MAP_READ is only valid with COPY_DST: copy into a separate staging buffer. */
    WGPUBufferDescriptor descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    descriptor.size = (uint64_t)count * 4;
    descriptor.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    r->staging = wgpuDeviceCreateBuffer(gpu_device_handle(d), &descriptor);
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(gpu_device_handle(d), NULL);
    wgpuCommandEncoderCopyBufferToBuffer(encoder, b->handle, (uint64_t)offset * 4, r->staging, 0, descriptor.size);
    gpu_device_submit(d, encoder);
    WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE; callback.callback = on_mapped; callback.userdata1 = r;
#if defined(AQUA_OS_WEB)
    wgpuBufferMapAsync(r->staging, WGPUMapMode_Read, 0, (size_t)descriptor.size, callback);
#else
    r->waiting = true;
    wgpuBufferMapAsync(r->staging, WGPUMapMode_Read, 0, (size_t)descriptor.size, callback);
    gpu_device_wait(d, &r->finished);
    deliver(r);
    free(r);
#endif
}

/* ---- Compute ---- */

typedef struct kernel { char *source; WGPUBindGroupLayout layout; WGPUComputePipeline pipeline; } kernel;
typedef struct kernels { kernel items[32]; int count; } kernels;

void gpu_device_release_kernels(gpu_device *d) {
    kernels *k = *gpu_device_kernel_slot(d);
    if (!k) return;
    for (int i = 0; i < k->count; i++) {
        free(k->items[i].source);
        if (k->items[i].pipeline) wgpuComputePipelineRelease(k->items[i].pipeline);
        if (k->items[i].layout) wgpuBindGroupLayoutRelease(k->items[i].layout);
    }
    free(k);
    *gpu_device_kernel_slot(d) = NULL;
}

typedef struct dispatch_operation {
    gpu_device *device;
    char *source;
    gpu_buffer *buffer;
    uint32_t groups;
    WGPUShaderModule module;
    gpu_dispatch_done done;
    void *data;
    volatile bool validated;
    bool waiting;
    char failure[1536];
} dispatch_operation;

static void run(gpu_device *d, kernel *k, gpu_buffer *b, uint32_t groups, gpu_dispatch_done done, void *data) {
    WGPUBindGroupEntry binding = WGPU_BIND_GROUP_ENTRY_INIT;
    binding.binding = 0; binding.buffer = b->handle; binding.size = (uint64_t)b->length * 4;
    WGPUBindGroupDescriptor descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    descriptor.layout = k->layout; descriptor.entryCount = 1; descriptor.entries = &binding;
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(gpu_device_handle(d), &descriptor);
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(gpu_device_handle(d), NULL);
    WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(encoder, NULL);
    wgpuComputePassEncoderSetPipeline(pass, k->pipeline);
    wgpuComputePassEncoderSetBindGroup(pass, 0, group, 0, NULL);
    wgpuComputePassEncoderDispatchWorkgroups(pass, groups, 1, 1);
    wgpuComputePassEncoderEnd(pass);
    wgpuComputePassEncoderRelease(pass);
    gpu_device_submit(d, encoder);
    wgpuBindGroupRelease(group);
    done(data, gpu_device_take_error(d));
}

static void complete(dispatch_operation *op) {
    gpu_device *d = op->device;
    if (op->failure[0] || gpu_device_disposed(d) || gpu_buffer_released(op->buffer)) {
        char message[1600];
        if (op->failure[0]) snprintf(message, sizeof message, "Shader compile failed: %s", op->failure);
        else snprintf(message, sizeof message, "wgpu device has been disposed.");
        if (op->module) wgpuShaderModuleRelease(op->module);
        op->done(op->data, message);
    } else {
        WGPUBindGroupLayoutEntry entry = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
        entry.binding = 0; entry.visibility = WGPUShaderStage_Compute; entry.buffer.type = WGPUBufferBindingType_Storage;
        WGPUBindGroupLayoutDescriptor layout_descriptor = WGPU_BIND_GROUP_LAYOUT_DESCRIPTOR_INIT;
        layout_descriptor.entryCount = 1; layout_descriptor.entries = &entry;
        kernel k = {op->source, wgpuDeviceCreateBindGroupLayout(gpu_device_handle(d), &layout_descriptor), NULL};
        WGPUPipelineLayoutDescriptor pipeline_layout = WGPU_PIPELINE_LAYOUT_DESCRIPTOR_INIT;
        pipeline_layout.bindGroupLayoutCount = 1; pipeline_layout.bindGroupLayouts = &k.layout;
        WGPUPipelineLayout layout = wgpuDeviceCreatePipelineLayout(gpu_device_handle(d), &pipeline_layout);
        WGPUComputePipelineDescriptor descriptor = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
        descriptor.layout = layout; descriptor.compute.module = op->module; descriptor.compute.entryPoint = gpu_text("cs_main");
        k.pipeline = wgpuDeviceCreateComputePipeline(gpu_device_handle(d), &descriptor);
        wgpuPipelineLayoutRelease(layout);
        wgpuShaderModuleRelease(op->module);
        const char *failure = gpu_device_take_error(d);
        if (failure || !k.pipeline) {
            if (k.pipeline) wgpuComputePipelineRelease(k.pipeline);
            wgpuBindGroupLayoutRelease(k.layout);
            op->done(op->data, failure ? failure : "wgpu compute pipeline creation failed.");
        } else {
            kernels **slot = (kernels **)gpu_device_kernel_slot(d);
            if (!*slot) *slot = calloc(1, sizeof **slot);
            kernels *list = *slot;
            if (list->count == 32) {
                free(list->items[0].source);
                wgpuComputePipelineRelease(list->items[0].pipeline);
                wgpuBindGroupLayoutRelease(list->items[0].layout);
                memmove(list->items, list->items + 1, 31 * sizeof *list->items);
                list->count--;
            }
            list->items[list->count++] = k;
            op->source = NULL;
            run(d, &list->items[list->count - 1], op->buffer, op->groups, op->done, op->data);
        }
    }
    free(op->source);
    gpu_device_release(d);
    free(op);
}

static void on_scope(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void *user, void *unused) {
    (void)unused;
    dispatch_operation *op = user;
    if (status != WGPUPopErrorScopeStatus_Success) snprintf(op->failure, sizeof op->failure, "wgpu did not complete validation.");
    else if (type != WGPUErrorType_NoError) {
        int length = message.data ? (int)(message.length == WGPU_STRLEN ? strlen(message.data) : message.length) : 0;
        snprintf(op->failure, sizeof op->failure, "%.*s", length > 0 ? length : 1, length > 0 ? message.data : "?");
    }
    op->validated = true;
    if (!op->waiting) complete(op);
}

void gpu_dispatch(gpu_device *d, const char *source, gpu_buffer *b, uint32_t groups, gpu_dispatch_done done, void *data) {
    gpu_device_flush(d);
    kernels *list = *gpu_device_kernel_slot(d);
    if (list) for (int i = 0; i < list->count; i++)
        if (strcmp(list->items[i].source, source) == 0) { run(d, &list->items[i], b, groups, done, data); return; }
    dispatch_operation *op = calloc(1, sizeof *op);
    op->device = d; op->buffer = b; op->groups = groups; op->done = done; op->data = data;
    op->source = malloc(strlen(source) + 1);
    strcpy(op->source, source);
    gpu_device_retain(d);
    wgpuDevicePushErrorScope(gpu_device_handle(d), WGPUErrorFilter_Validation);
    op->module = gpu_device_shader_module(d, source, strlen(source));
    WGPUPopErrorScopeCallbackInfo callback = WGPU_POP_ERROR_SCOPE_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE; callback.callback = on_scope; callback.userdata1 = op;
#if defined(AQUA_OS_WEB)
    wgpuDevicePopErrorScope(gpu_device_handle(d), callback);
#else
    op->waiting = true;
    wgpuDevicePopErrorScope(gpu_device_handle(d), callback);
    gpu_device_wait(d, &op->validated);
    if (!op->validated) snprintf(op->failure, sizeof op->failure, "wgpu did not complete validation.");
    complete(op);
#endif
}
