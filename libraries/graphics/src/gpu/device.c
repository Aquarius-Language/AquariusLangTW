/* Devices: adapter and device requests, the shared bind group layouts, sampler and white texture, uncaptured errors,
   window presentation through a linear blit, and retiring objects still referenced by unsubmitted passes. */
#include "gpu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if !defined(AQUA_OS_WEB)
#include <webgpu/wgpu.h>
#endif
#if defined(AQUA_OS_ANDROID)
#include <sys/system_properties.h>
#endif
#if defined(AQUA_OS_WINDOWS)
#include <windows.h>
#elif !defined(AQUA_OS_WEB)
#include <time.h>
#endif

typedef struct blit_pipeline { WGPUTextureFormat format; uint32_t samples; WGPURenderPipeline pipeline; } blit_pipeline;

struct gpu_device {
    aqua_runtime *rt;
    int references;
    bool disposed;
    WGPUInstance instance;
    WGPUAdapter adapter;
    WGPUDevice device;
    WGPUQueue queue;
    aqua_window *window;
    void *surface_handle;    /* the OS surface it was made for: Android replaces it across pause/resume */
    WGPUSurface surface;
    WGPUSurfaceConfiguration config;
    bool configured;
    WGPUBindGroupLayout drawing_layout, user_layout, blit_layout;
    WGPUPipelineLayout blit_pipeline_layout;
    WGPUShaderModule blit_module;
    blit_pipeline blits[8];
    int blit_count;
    WGPUSampler sampler, samplers[4]; /* samplers[0] is `sampler` */
    gpu_texture *white;
    WGPUBuffer identity_buffer;
    WGPUBindGroup identity_group;
    WGPUBindGroup *retired_groups;
    WGPUBuffer *retired_buffers;
    size_t retired_group_count, retired_group_capacity, retired_buffer_count, retired_buffer_capacity;
    gpu_resource resources;  /* list head */
    bool has_error;
    char error[1024], taken[1024];
    char backend[32];
    void *kernels;           /* buffer.c: compiled compute kernels */
    /* creation */
    gpu_device_done done;
    void *done_data;
    volatile bool created;
};

static void free_struct(gpu_device *d) {
    free(d->retired_groups);
    free(d->retired_buffers);
    free(d);
}

void gpu_device_retain(gpu_device *d) { d->references++; }
void gpu_device_release(gpu_device *d) {
    if (!d || --d->references > 0) return;
    d->references = 1; /* resources released during disposal must not free the struct again */
    gpu_device_dispose(d);
    free_struct(d);
}
bool gpu_device_disposed(const gpu_device *d) { return d->disposed; }
void **gpu_device_kernel_slot(gpu_device *d) { return &d->kernels; }
WGPUInstance gpu_device_instance(gpu_device *d) { return d->instance; }
const char *gpu_device_backend(const gpu_device *d) { return d->backend; }
aqua_window *gpu_device_window(const gpu_device *d) { return d->window; }
WGPUDevice gpu_device_handle(gpu_device *d) { return d->device; }
WGPUQueue gpu_device_queue(gpu_device *d) { return d->queue; }
WGPUBindGroupLayout gpu_device_drawing_layout(gpu_device *d) { return d->drawing_layout; }
WGPUBindGroupLayout gpu_device_user_layout(gpu_device *d) { return d->user_layout; }
WGPUSampler gpu_device_sampler(gpu_device *d) { return d->sampler; }
WGPUSampler gpu_device_sampler_for(gpu_device *d, int sampling) { return d->samplers[sampling & 3] ? d->samplers[sampling & 3] : d->sampler; }
gpu_texture *gpu_device_white(gpu_device *d) { return d->white; }

void gpu_device_link(gpu_device *d, gpu_resource *r, gpu_kind kind) {
    r->kind = kind; r->device = d;
    r->next = d->resources.next; r->previous = &d->resources;
    d->resources.next->previous = r; d->resources.next = r;
    gpu_device_retain(d);
}
void gpu_device_unlink(gpu_resource *r) {
    if (!r->device) return;
    gpu_device *d = r->device;
    r->previous->next = r->next; r->next->previous = r->previous;
    r->next = r->previous = r; r->device = NULL;
    gpu_device_release(d);
}

/* ---- Errors ---- */

static const char *error_type(WGPUErrorType type) {
    switch (type) {
    case WGPUErrorType_Validation: return "Validation";
    case WGPUErrorType_OutOfMemory: return "OutOfMemory";
    case WGPUErrorType_Internal: return "Internal";
    case WGPUErrorType_NoError: return "NoError";
    default: return "Unknown";
    }
}

void gpu_device_error(gpu_device *d, const char *message) {
    if (d->has_error) return;
    snprintf(d->error, sizeof d->error, "%s", message);
    d->has_error = true;
}

static void on_uncaptured(WGPUDevice const *device, WGPUErrorType type, WGPUStringView message, void *user, void *unused) {
    (void)device; (void)unused;
    gpu_device *d = user;
    if (!d || d->has_error) return;
    snprintf(d->error, sizeof d->error, "%s: %.*s", error_type(type), (int)(message.length == WGPU_STRLEN ? strlen(message.data ? message.data : "") : message.length), message.data ? message.data : "");
    d->has_error = true;
}

static void on_lost(WGPUDevice const *device, WGPUDeviceLostReason reason, WGPUStringView message, void *user, void *unused) {
    (void)device; (void)unused;
    gpu_device *d = user;
    if (!d || d->disposed || reason == WGPUDeviceLostReason_Destroyed || reason == WGPUDeviceLostReason_CallbackCancelled) return;
    if (!d->has_error) {
        snprintf(d->error, sizeof d->error, "WebGPU device lost: %.*s", (int)(message.length == WGPU_STRLEN ? strlen(message.data ? message.data : "") : message.length), message.data ? message.data : "");
        d->has_error = true;
    }
}

const char *gpu_device_take_error(gpu_device *d) {
    if (!d->has_error) return NULL;
    memcpy(d->taken, d->error, sizeof d->taken);
    d->has_error = false;
    return d->taken;
}

/* ---- Waiting for callbacks ---- */

static void pause_briefly(void) {
#if defined(AQUA_OS_WINDOWS)
    Sleep(1);
#elif !defined(AQUA_OS_WEB)
    struct timespec t = {0, 500000};
    nanosleep(&t, NULL);
#endif
}

void gpu_instance_wait(WGPUInstance instance, WGPUDevice device, const volatile bool *flag) {
#if defined(AQUA_OS_WEB)
    (void)instance; (void)device; (void)flag; /* the browser delivers the callback later */
#else
    for (int i = 0; i < 20000 && !*flag; i++) {
        if (device) wgpuDevicePoll(device, true, NULL);
        wgpuInstanceProcessEvents(instance);
        if (!*flag) pause_briefly();
    }
#endif
}

void gpu_device_wait(gpu_device *d, const volatile bool *flag) { gpu_instance_wait(d->instance, d->device, flag); }

/* ---- Creation ---- */

static const char *backend_name(WGPUBackendType type) {
    switch (type) {
    case WGPUBackendType_Null: return "Null";
    case WGPUBackendType_WebGPU: return "WebGpu";
    case WGPUBackendType_D3D11: return "D3D11";
    case WGPUBackendType_D3D12: return "D3D12";
    case WGPUBackendType_Metal: return "Metal";
    case WGPUBackendType_Vulkan: return "Vulkan";
    case WGPUBackendType_OpenGL: return "OpenGL";
    case WGPUBackendType_OpenGLES: return "OpenGLES";
    default: return "Undefined";
    }
}

static void finish_creation(gpu_device *d, const char *error) {
    gpu_device_done done = d->done;
    void *data = d->done_data;
    d->done = NULL;
    d->created = true;
    if (error) {
        char message[1024];
        snprintf(message, sizeof message, "%s", error);
        gpu_device_release(d);
        if (done) done(NULL, message, data);
        return;
    }
    if (done) done(d, NULL, data);
}

WGPUShaderModule gpu_device_shader_module(gpu_device *d, const char *source, size_t length) {
    WGPUShaderSourceWGSL wgsl = WGPU_SHADER_SOURCE_WGSL_INIT;
    wgsl.code.data = source; wgsl.code.length = length;
    WGPUShaderModuleDescriptor descriptor = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
    descriptor.nextInChain = &wgsl.chain;
    return wgpuDeviceCreateShaderModule(d->device, &descriptor);
}

WGPUBuffer gpu_device_buffer(gpu_device *d, uint64_t size, WGPUBufferUsage usage, const void *data) {
    WGPUBufferDescriptor descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    descriptor.size = (size + 3) & ~(uint64_t)3;
    descriptor.usage = usage | WGPUBufferUsage_CopyDst;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(d->device, &descriptor);
    if (buffer && data) wgpuQueueWriteBuffer(d->queue, buffer, 0, data, (size_t)size);
    return buffer;
}

void gpu_device_submit(gpu_device *d, WGPUCommandEncoder encoder) {
    WGPUCommandBuffer commands = wgpuCommandEncoderFinish(encoder, NULL);
    wgpuQueueSubmit(d->queue, 1, &commands);
    wgpuCommandBufferRelease(commands);
    wgpuCommandEncoderRelease(encoder);
}

static bool create_shared(gpu_device *d) {
    WGPUBindGroupLayoutEntry entries[3] = {WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT, WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT, WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT};
    entries[0].binding = 0;
    entries[0].visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    entries[0].buffer.type = WGPUBufferBindingType_Uniform;
    entries[0].buffer.hasDynamicOffset = true;
    entries[0].buffer.minBindingSize = GPU_UNIFORM_BYTES;
    entries[1].binding = 1;
    entries[1].visibility = WGPUShaderStage_Fragment;
    entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[2].binding = 2;
    entries[2].visibility = WGPUShaderStage_Fragment;
    entries[2].sampler.type = WGPUSamplerBindingType_Filtering;
    WGPUBindGroupLayoutDescriptor layout = WGPU_BIND_GROUP_LAYOUT_DESCRIPTOR_INIT;
    layout.entryCount = 3; layout.entries = entries;
    d->drawing_layout = wgpuDeviceCreateBindGroupLayout(d->device, &layout);

    WGPUBindGroupLayoutEntry user = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
    user.binding = 0;
    user.visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    user.buffer.type = WGPUBufferBindingType_Uniform;
    user.buffer.minBindingSize = GPU_USER_BYTES;
    layout.entryCount = 1; layout.entries = &user;
    d->user_layout = wgpuDeviceCreateBindGroupLayout(d->device, &layout);

    WGPUBindGroupLayoutEntry blit[2] = {entries[1], entries[2]};
    blit[0].binding = 0; blit[1].binding = 1;
    layout.entryCount = 2; layout.entries = blit;
    d->blit_layout = wgpuDeviceCreateBindGroupLayout(d->device, &layout);
    WGPUPipelineLayoutDescriptor pipeline = WGPU_PIPELINE_LAYOUT_DESCRIPTOR_INIT;
    pipeline.bindGroupLayoutCount = 1; pipeline.bindGroupLayouts = &d->blit_layout;
    d->blit_pipeline_layout = wgpuDeviceCreatePipelineLayout(d->device, &pipeline);

    WGPUSamplerDescriptor sampler = WGPU_SAMPLER_DESCRIPTOR_INIT;
    sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler.magFilter = WGPUFilterMode_Linear;
    sampler.minFilter = WGPUFilterMode_Linear;
    sampler.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler.lodMaxClamp = 32;
    sampler.maxAnisotropy = 1;
    d->sampler = wgpuDeviceCreateSampler(d->device, &sampler);
    /* Processing's textureWrap(REPEAT) and noSmooth() images: GPU_SAMPLE_REPEAT | GPU_SAMPLE_NEAREST variants. */
    d->samplers[0] = d->sampler;
    for (int i = 1; i < 4; i++) {
        sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = i & GPU_SAMPLE_REPEAT ? WGPUAddressMode_Repeat : WGPUAddressMode_ClampToEdge;
        sampler.magFilter = sampler.minFilter = i & GPU_SAMPLE_NEAREST ? WGPUFilterMode_Nearest : WGPUFilterMode_Linear;
        d->samplers[i] = wgpuDeviceCreateSampler(d->device, &sampler);
    }
    d->blit_module = gpu_device_shader_module(d, gpu_wgsl_blit, strlen(gpu_wgsl_blit));
    d->white = gpu_texture_rgba(d, 1, 1);
    static const uint8_t white[4] = {255, 255, 255, 255};
    if (d->white) gpu_texture_upload(d->white, white, true);
    return d->drawing_layout && d->user_layout && d->blit_layout && d->sampler && d->blit_module && d->white;
}

static void configure_surface_format(gpu_device *d) {
    WGPUSurfaceCapabilities caps = WGPU_SURFACE_CAPABILITIES_INIT;
    if (wgpuSurfaceGetCapabilities(d->surface, d->adapter, &caps) != WGPUStatus_Success || caps.formatCount == 0) {
        gpu_device_error(d, "wgpu surface has no supported formats.");
        return;
    }
    WGPUSurfaceConfiguration config = WGPU_SURFACE_CONFIGURATION_INIT;
    config.device = d->device;
    config.format = caps.formats[0];
    /* Prefer a linear format: Processing colors are already display values. */
    for (size_t i = 0; i < caps.formatCount; i++)
        if (caps.formats[i] == WGPUTextureFormat_BGRA8Unorm || caps.formats[i] == WGPUTextureFormat_RGBA8Unorm) { config.format = caps.formats[i]; break; }
    config.usage = WGPUTextureUsage_RenderAttachment;
    config.alphaMode = caps.alphaModeCount ? caps.alphaModes[0] : WGPUCompositeAlphaMode_Auto;
    config.presentMode = WGPUPresentMode_Fifo;
    d->config = config;
    wgpuSurfaceCapabilitiesFreeMembers(caps);
}

static void on_device(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void *user, void *unused) {
    (void)unused;
    gpu_device *d = user;
    if (status != WGPURequestDeviceStatus_Success || !device) {
        char error[512];
        snprintf(error, sizeof error, "wgpu device creation failed: %.*s", (int)(message.data ? (message.length == WGPU_STRLEN ? strlen(message.data) : message.length) : 0), message.data ? message.data : "");
        finish_creation(d, error);
        return;
    }
    d->device = device;
    d->queue = wgpuDeviceGetQueue(device);
    WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
#if defined(AQUA_OS_WEB)
    snprintf(d->backend, sizeof d->backend, "webgpu");
#else
    if (wgpuAdapterGetInfo(d->adapter, &info) == WGPUStatus_Success) {
        snprintf(d->backend, sizeof d->backend, "%s", backend_name(info.backendType));
        wgpuAdapterInfoFreeMembers(info);
    }
#endif
    (void)info;
    if (!create_shared(d)) { finish_creation(d, "wgpu device setup failed."); return; }
    if (d->surface) configure_surface_format(d);
    const char *error = gpu_device_take_error(d);
    finish_creation(d, error);
}

static void on_adapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void *user, void *unused) {
    (void)unused;
    gpu_device *d = user;
    if (status != WGPURequestAdapterStatus_Success || !adapter) {
        char error[512];
        snprintf(error, sizeof error, "wgpu adapter unavailable: %.*s", (int)(message.data ? (message.length == WGPU_STRLEN ? strlen(message.data) : message.length) : 0), message.data ? message.data : "");
        finish_creation(d, error);
        return;
    }
    d->adapter = adapter;
    WGPUDeviceDescriptor descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    descriptor.uncapturedErrorCallbackInfo.callback = on_uncaptured;
    descriptor.uncapturedErrorCallbackInfo.userdata1 = d;
    descriptor.deviceLostCallbackInfo.mode = GPU_CALLBACK_MODE;
    descriptor.deviceLostCallbackInfo.callback = on_lost;
    descriptor.deviceLostCallbackInfo.userdata1 = d;
    WGPURequestDeviceCallbackInfo callback = WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE;
    callback.callback = on_device;
    callback.userdata1 = d;
    wgpuAdapterRequestDevice(adapter, &descriptor, callback);
}

#if defined(AQUA_OS_ANDROID)
/* The Android emulator (ro.hardware ranchu or goldfish). */
static bool android_emulator(void) {
    char hardware[PROP_VALUE_MAX] = {0};
    __system_property_get("ro.hardware", hardware);
    return strcmp(hardware, "ranchu") == 0 || strcmp(hardware, "goldfish") == 0;
}
#endif

void gpu_device_create(aqua_runtime *rt, aqua_window *window, gpu_device_done done, void *data) {
    gpu_device *d = calloc(1, sizeof *d);
    d->rt = rt;
    d->references = 1;
    d->resources.next = d->resources.previous = &d->resources;
    d->done = done; d->done_data = data;
    WGPUInstanceDescriptor descriptor = WGPU_INSTANCE_DESCRIPTOR_INIT;
#if defined(AQUA_OS_LINUX) || defined(AQUA_OS_ANDROID)
    /* Linux's accelerated GLES adapter can outrank a Vulkan CPU adapter while lacking compute and storage features. */
    WGPUInstanceExtras extras = {0};
    extras.chain.sType = (WGPUSType)WGPUSType_InstanceExtras;
    extras.backends = WGPUInstanceBackend_Vulkan;
#if defined(AQUA_OS_ANDROID)
    /* Emulators (GFXStream) advertise incomplete conformance: permit their adapter there only. Their driver's
       submission bug is handled in src/platform/android_vulkan.c. */
    if (android_emulator()) extras.flags = WGPUInstanceFlag_AllowUnderlyingNoncompliantAdapter;
#endif
    descriptor.nextInChain = &extras.chain;
#endif
    d->instance = wgpuCreateInstance(&descriptor);
    if (!d->instance) { finish_creation(d, "wgpu instance creation failed."); return; }
    if (window) {
        d->window = window;
        aqua_surface s;
        if (aqua_window_surface(window, &s)) d->surface_handle = s.handle;
        d->surface = gpu_surface_create(d->instance, window);
        if (!d->surface) { finish_creation(d, "wgpu surface creation failed."); return; }
    }
    WGPURequestAdapterOptions options = WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    options.compatibleSurface = d->surface;
    options.powerPreference = WGPUPowerPreference_HighPerformance;
#if defined(AQUA_OS_ANDROID)
    options.backendType = WGPUBackendType_Vulkan;
#endif
    WGPURequestAdapterCallbackInfo callback = WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE;
    callback.callback = on_adapter;
    callback.userdata1 = d;
    /* The device struct may be freed by a failing creation inside the wait: keep it until we are done. */
    gpu_device_retain(d);
    wgpuInstanceRequestAdapter(d->instance, &options, callback);
#if !defined(AQUA_OS_WEB)
    gpu_instance_wait(d->instance, NULL, &d->created);
    if (!d->created) finish_creation(d, "wgpu adapter request did not complete.");
#endif
    gpu_device_release(d);
}

/* ---- Retired objects ---- */

void gpu_device_retire_group(gpu_device *d, WGPUBindGroup group) {
    if (!group) return;
    if (d->retired_group_count == d->retired_group_capacity) {
        d->retired_group_capacity = d->retired_group_capacity ? d->retired_group_capacity * 2 : 16;
        d->retired_groups = realloc(d->retired_groups, d->retired_group_capacity * sizeof *d->retired_groups);
    }
    d->retired_groups[d->retired_group_count++] = group;
}
void gpu_device_retire_buffer(gpu_device *d, WGPUBuffer buffer) {
    if (!buffer) return;
    if (d->retired_buffer_count == d->retired_buffer_capacity) {
        d->retired_buffer_capacity = d->retired_buffer_capacity ? d->retired_buffer_capacity * 2 : 16;
        d->retired_buffers = realloc(d->retired_buffers, d->retired_buffer_capacity * sizeof *d->retired_buffers);
    }
    d->retired_buffers[d->retired_buffer_count++] = buffer;
}
void gpu_device_release_retired(gpu_device *d) {
    if (!d->retired_group_count && !d->retired_buffer_count) return;
    gpu_device_flush(d);
    for (size_t i = 0; i < d->retired_group_count; i++) wgpuBindGroupRelease(d->retired_groups[i]);
    for (size_t i = 0; i < d->retired_buffer_count; i++) wgpuBufferRelease(d->retired_buffers[i]);
    d->retired_group_count = d->retired_buffer_count = 0;
}

void gpu_device_flush(gpu_device *d) {
    if (d->disposed) return;
    for (gpu_resource *r = d->resources.next; r != &d->resources; r = r->next)
        if (r->kind == GPU_TARGET) gpu_target_flush((gpu_target *)r);
}

typedef struct poll_wait { volatile bool done; void (*callback)(void *, const char *); void *data; } poll_wait;
static void on_work_done(WGPUQueueWorkDoneStatus status, WGPUStringView message, void *user, void *unused) {
    (void)message; (void)unused;
    poll_wait *w = user;
    w->done = true;
    if (w->callback) w->callback(w->data, status == WGPUQueueWorkDoneStatus_Success ? NULL : "wgpu device poll failed.");
    w->callback = NULL;
#if defined(AQUA_OS_WEB)
    free(w);
#endif
}

void gpu_device_poll(gpu_device *d, void (*done)(void *data, const char *error), void *data) {
    gpu_device_flush(d);
#if defined(AQUA_OS_WEB)
    poll_wait *w = calloc(1, sizeof *w);
    w->callback = done; w->data = data;
    WGPUQueueWorkDoneCallbackInfo callback = WGPU_QUEUE_WORK_DONE_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE; callback.callback = on_work_done; callback.userdata1 = w;
    wgpuQueueOnSubmittedWorkDone(d->queue, callback);
#else
    (void)on_work_done;
    wgpuDevicePoll(d->device, true, NULL);
    wgpuInstanceProcessEvents(d->instance);
    gpu_device_release_retired(d);
    done(data, gpu_device_take_error(d));
#endif
}

/* ---- Blits and presentation ---- */

void gpu_device_blit(gpu_device *d, WGPUTextureView source, WGPUTextureView destination, WGPUTextureFormat format, uint32_t samples) {
    WGPURenderPipeline pipeline = NULL;
    for (int i = 0; i < d->blit_count; i++) if (d->blits[i].format == format && d->blits[i].samples == samples) pipeline = d->blits[i].pipeline;
    if (!pipeline) {
        WGPUColorTargetState color = WGPU_COLOR_TARGET_STATE_INIT;
        color.format = format;
        color.writeMask = WGPUColorWriteMask_All;
        WGPUFragmentState fragment = WGPU_FRAGMENT_STATE_INIT;
        fragment.module = d->blit_module; fragment.entryPoint = gpu_text("fs_main");
        fragment.targetCount = 1; fragment.targets = &color;
        WGPURenderPipelineDescriptor descriptor = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
        descriptor.layout = d->blit_pipeline_layout;
        descriptor.vertex.module = d->blit_module; descriptor.vertex.entryPoint = gpu_text("vs_main");
        descriptor.fragment = &fragment;
        descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
        descriptor.multisample.count = samples;
        descriptor.multisample.mask = 0xFFFFFFFFu;
        pipeline = wgpuDeviceCreateRenderPipeline(d->device, &descriptor);
        if (!pipeline) return;
        if (d->blit_count < 8) d->blits[d->blit_count++] = (blit_pipeline){format, samples, pipeline};
    }
    WGPUBindGroupEntry entries[2] = {WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT};
    entries[0].binding = 0; entries[0].textureView = source;
    entries[1].binding = 1; entries[1].sampler = d->sampler;
    WGPUBindGroupDescriptor group_descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    group_descriptor.layout = d->blit_layout; group_descriptor.entryCount = 2; group_descriptor.entries = entries;
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(d->device, &group_descriptor);
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(d->device, NULL);
    WGPURenderPassColorAttachment attachment = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
    attachment.view = destination;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor pass_descriptor = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
    pass_descriptor.colorAttachmentCount = 1; pass_descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(encoder, &pass_descriptor);
    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, NULL);
    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    gpu_device_submit(d, encoder);
    wgpuBindGroupRelease(group);
}

static void configure(gpu_device *d) {
    wgpuSurfaceConfigure(d->surface, &d->config);
    d->configured = true;
}

bool gpu_device_present(gpu_device *d, gpu_target *target, char *error, size_t error_size) {
    if (!d->window) { snprintf(error, error_size, "This wgpu device has no window surface; create it with WGPU.CreateDevice(window)."); return false; }
    gpu_target_flush(target);
    /* A window whose OS surface was replaced (Android after a pause) gets a new webgpu.h surface. */
    aqua_surface s;
    if (!aqua_window_surface(d->window, &s)) return true;
    if (!d->surface || s.handle != d->surface_handle) {
        if (d->surface) { if (d->configured) wgpuSurfaceUnconfigure(d->surface); wgpuSurfaceRelease(d->surface); }
        d->configured = false;
        d->surface_handle = s.handle;
        d->surface = gpu_surface_create(d->instance, d->window);
        if (!d->surface) return true;
        configure_surface_format(d);
    }
    int width = 0, height = 0;
    if (d->window) aqua_window_framebuffer_size(d->window, &width, &height);
    if (width <= 0 || height <= 0) return true; /* minimized */
    if (!d->configured || d->config.width != (uint32_t)width || d->config.height != (uint32_t)height) {
        d->config.width = (uint32_t)width; d->config.height = (uint32_t)height;
        configure(d);
    }
    WGPUSurfaceTexture frame = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(d->surface, &frame);
    if (frame.status == WGPUSurfaceGetCurrentTextureStatus_Outdated || frame.status == WGPUSurfaceGetCurrentTextureStatus_Lost) {
        if (frame.texture) wgpuTextureRelease(frame.texture);
        configure(d);
        frame = (WGPUSurfaceTexture)WGPU_SURFACE_TEXTURE_INIT;
        wgpuSurfaceGetCurrentTexture(d->surface, &frame);
    }
    if (frame.status == WGPUSurfaceGetCurrentTextureStatus_Timeout || frame.status == WGPUSurfaceGetCurrentTextureStatus_Outdated ||
        frame.status == WGPUSurfaceGetCurrentTextureStatus_Lost) {
        if (frame.texture) wgpuTextureRelease(frame.texture);
        return true;
    }
    if ((frame.status != WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal && frame.status != WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal) || !frame.texture) {
        if (frame.texture) wgpuTextureRelease(frame.texture);
        snprintf(error, error_size, "wgpu presentation failed: %d", (int)frame.status);
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(frame.texture, NULL);
    gpu_device_blit(d, gpu_texture_view(gpu_target_color(target)), view, d->config.format, 1);
#if !defined(AQUA_OS_WEB)
    wgpuSurfacePresent(d->surface); /* the browser presents when the task yields */
#endif
    wgpuTextureViewRelease(view);
    wgpuTextureRelease(frame.texture);
    if (frame.status == WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal) d->configured = false;
    gpu_device_release_retired(d);
    const char *failure = gpu_device_take_error(d);
    if (failure) { snprintf(error, error_size, "%s", failure); return false; }
    return true;
}

/* Group 0 for WGPU-module draws: identity Processing matrices, the white texture and the shared sampler. */
WGPUBindGroup gpu_device_identity_group(gpu_device *d) {
    if (d->identity_group) return d->identity_group;
    float uniforms[GPU_UNIFORM_BYTES / 4] = {0};
    for (int m = 0; m < 4; m++) for (int i = 0; i < 4; i++) uniforms[m * 16 + i * 5] = 1;
    d->identity_buffer = gpu_device_buffer(d, GPU_UNIFORM_BYTES, WGPUBufferUsage_Uniform, uniforms);
    WGPUBindGroupEntry entries[3] = {WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT};
    entries[0].binding = 0; entries[0].buffer = d->identity_buffer; entries[0].size = GPU_UNIFORM_BYTES;
    entries[1].binding = 1; entries[1].textureView = gpu_texture_view(d->white);
    entries[2].binding = 2; entries[2].sampler = d->sampler;
    WGPUBindGroupDescriptor descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    descriptor.layout = d->drawing_layout; descriptor.entryCount = 3; descriptor.entries = entries;
    d->identity_group = wgpuDeviceCreateBindGroup(d->device, &descriptor);
    return d->identity_group;
}

/* ---- Disposal ---- */

void gpu_device_dispose(gpu_device *d) {
    if (d->disposed) return;
    d->references++; /* resources unlink themselves below */
    gpu_device_flush(d);
    for (gpu_resource *r = d->resources.next; r != &d->resources; r = r->next) {
        switch (r->kind) {
        case GPU_TEXTURE: gpu_texture_release_handles((gpu_texture *)r); break;
        case GPU_TARGET: gpu_target_release_handles((gpu_target *)r); break;
        case GPU_SHADER: gpu_shader_release_handles((gpu_shader *)r); break;
        case GPU_BUFFER: gpu_buffer_release_handles((gpu_buffer *)r); break;
        }
    }
    d->disposed = true;
    gpu_device_release_kernels(d);
#if !defined(AQUA_OS_WEB)
    if (d->device) wgpuDevicePoll(d->device, true, NULL);
#endif
    for (size_t i = 0; i < d->retired_group_count; i++) wgpuBindGroupRelease(d->retired_groups[i]);
    for (size_t i = 0; i < d->retired_buffer_count; i++) wgpuBufferRelease(d->retired_buffers[i]);
    d->retired_group_count = d->retired_buffer_count = 0;
    if (d->identity_group) wgpuBindGroupRelease(d->identity_group);
    if (d->identity_buffer) wgpuBufferRelease(d->identity_buffer);
    d->identity_group = NULL; d->identity_buffer = NULL;
    if (d->white) { gpu_texture_free(d->white); d->white = NULL; }
    for (int i = 0; i < d->blit_count; i++) wgpuRenderPipelineRelease(d->blits[i].pipeline);
    d->blit_count = 0;
    if (d->surface) { if (d->configured) wgpuSurfaceUnconfigure(d->surface); wgpuSurfaceRelease(d->surface); d->surface = NULL; }
    if (d->blit_module) wgpuShaderModuleRelease(d->blit_module);
    if (d->sampler) wgpuSamplerRelease(d->sampler);
    for (int i = 1; i < 4; i++) if (d->samplers[i]) { wgpuSamplerRelease(d->samplers[i]); d->samplers[i] = NULL; }
    d->samplers[0] = NULL;
    if (d->blit_pipeline_layout) wgpuPipelineLayoutRelease(d->blit_pipeline_layout);
    if (d->drawing_layout) wgpuBindGroupLayoutRelease(d->drawing_layout);
    if (d->user_layout) wgpuBindGroupLayoutRelease(d->user_layout);
    if (d->blit_layout) wgpuBindGroupLayoutRelease(d->blit_layout);
    if (d->queue) wgpuQueueRelease(d->queue);
    if (d->device) { wgpuDeviceDestroy(d->device); wgpuDeviceRelease(d->device); }
    if (d->adapter) wgpuAdapterRelease(d->adapter);
    if (d->instance) wgpuInstanceRelease(d->instance);
    d->blit_module = NULL; d->sampler = NULL; d->blit_pipeline_layout = NULL; d->drawing_layout = d->user_layout = d->blit_layout = NULL;
    d->queue = NULL; d->device = NULL; d->adapter = NULL; d->instance = NULL; d->window = NULL;
    d->references--; /* the retain above; freeing is gpu_device_release's */
}
