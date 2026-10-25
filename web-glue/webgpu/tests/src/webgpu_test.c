/* A test-only library for web-glue/webgpu: one webgpu.h code path on wgpu-native (Windows, Linux, Android) and on
 * the browser's WebGPU. Each step prints one line, so a test's output is the same on every target.
 *   WebGPUTest.run()     adapter, device, a WGSL compute shader mapped back, a triangle rendered into a window for a
 *                        few frames, then rendered into a texture whose pixels are read back. Every target.
 *   WebGPUTest.extras()  the webgpu.h functions wgpu-native leaves unimplemented (asynchronous pipelines, compilation
 *                        info, map state, Read/WriteMappedRange, WaitAny, labels, lost futures, WGSL features) and
 *                        device lost / uncaptured error callbacks. Web only.
 * Both are deferred calls: a tick scheduled with aqua_schedule processes WebGPU events and advances the steps. */
#include <aqua/aqua.h>
#include <aqua/window.h>
#include <webgpu/webgpu.h>
#if defined(AQUA_OS_WEB)
#include <webgpu_web.h>
#endif
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

enum { FRAMES = 3, SIZE = 64, ROW_BYTES = SIZE * 4 };

typedef struct test test;
typedef bool (*step)(test *t); /* true when done: the next step runs in the same tick */

struct test {
    aqua_runtime *rt;
    aqua_pending *pending;
    const step *steps;
    char failure[512];
    bool expect_uncaptured;
    WGPUInstance instance;
    WGPUAdapter adapter;
    WGPUDevice device;
    WGPUQueue queue;
    /* run() */
    WGPUBuffer storage, readback, pixels, vertices, color;
    int mapped, work_done, popped;          /* callbacks seen */
    WGPUErrorType popped_type;
    aqua_window *window;
    WGPUSurface surface;
    WGPUTextureFormat format;
    uint32_t surface_width, surface_height, surface_row; /* surface_row: bytes per row of surface_pixels */
    WGPUBuffer surface_pixels;
    int surface_mapped;
    WGPUBindGroup bind_group;
    WGPURenderPipeline pipeline;
    WGPUTexture target;
    int frames;
    /* extras() */
    WGPUComputePipeline compute_async;
    WGPURenderPipeline render_async;
    int pipelines, compilation_line, uncaptured, lost;
    WGPUErrorType uncaptured_type;
    WGPUDeviceLostReason lost_reason;
    WGPUBuffer upload, download;
    WGPUFuture download_future;
};
static test state; /* one test at a time; static so late callbacks (device lost on release) find it */

static WGPUStringView text(const char *s) { return (WGPUStringView){s, WGPU_STRLEN}; }
static void say(test *t, const char *format, ...) {
    char line[512];
    va_list arguments;
    va_start(arguments, format);
    int n = vsnprintf(line, sizeof line - 1, format, arguments);
    va_end(arguments);
    if (n < 0) return;
    if (n > (int)sizeof line - 2) n = (int)sizeof line - 2;
    line[n++] = '\n';
    aqua_write(t->rt, line, (size_t)n);
}
static void fail(test *t, const char *what, WGPUStringView message) {
    if (t->failure[0]) return;
    int length = message.length == WGPU_STRLEN ? (message.data ? (int)strlen(message.data) : 0) : (int)message.length;
    snprintf(t->failure, sizeof t->failure, "%s: %.*s", what, length, message.data ? message.data : "");
}
static const char *error_name(WGPUErrorType type) {
    switch (type) {
    case WGPUErrorType_NoError: return "no error";
    case WGPUErrorType_Validation: return "validation";
    case WGPUErrorType_OutOfMemory: return "out of memory";
    case WGPUErrorType_Internal: return "internal";
    default: return "unknown";
    }
}
static WGPUShaderModule shader(test *t, const char *code) {
    WGPUShaderSourceWGSL source = WGPU_SHADER_SOURCE_WGSL_INIT;
    source.code = text(code);
    WGPUShaderModuleDescriptor descriptor = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
    descriptor.nextInChain = &source.chain;
    return wgpuDeviceCreateShaderModule(t->device, &descriptor);
}
static WGPUBuffer buffer(test *t, WGPUBufferUsage usage, uint64_t size, bool mapped) {
    WGPUBufferDescriptor descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    descriptor.usage = usage;
    descriptor.size = size;
    descriptor.mappedAtCreation = mapped;
    return wgpuDeviceCreateBuffer(t->device, &descriptor);
}
static void submit(test *t, WGPUCommandEncoder encoder) {
    WGPUCommandBuffer commands = wgpuCommandEncoderFinish(encoder, NULL);
    wgpuQueueSubmit(t->queue, 1, &commands);
    wgpuCommandBufferRelease(commands);
    wgpuCommandEncoderRelease(encoder);
}

/* ---- Callbacks: they only record results; the steps print. ---------------------------------------------------- */

static void on_adapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status == WGPURequestAdapterStatus_Success) t->adapter = adapter;
    else fail(t, "no adapter", message);
}
static void on_device(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status == WGPURequestDeviceStatus_Success) t->device = device;
    else fail(t, "no device", message);
}
static void on_lost(WGPUDevice const *device, WGPUDeviceLostReason reason, WGPUStringView message, void *data, void *unused) {
    (void)device; (void)message; (void)unused;
    test *t = data;
    t->lost++;
    t->lost_reason = reason;
}
static void on_uncaptured_error(WGPUDevice const *device, WGPUErrorType type, WGPUStringView message, void *data, void *unused) {
    (void)device; (void)unused;
    test *t = data;
    t->uncaptured++;
    t->uncaptured_type = type;
    if (!t->expect_uncaptured) fail(t, "uncaptured error", message);
}
static void on_mapped(WGPUMapAsyncStatus status, WGPUStringView message, void *data, void *counter) {
    test *t = data;
    if (status == WGPUMapAsyncStatus_Success) (*(int *)counter)++;
    else fail(t, "mapping failed", message);
}
static void on_work_done(WGPUQueueWorkDoneStatus status, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status == WGPUQueueWorkDoneStatus_Success) t->work_done++;
    else fail(t, "queue work failed", message);
}
static void on_popped(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status != WGPUPopErrorScopeStatus_Success) { fail(t, "pop error scope", message); return; }
    t->popped++;
    t->popped_type = type;
}
static WGPUBufferMapCallbackInfo map_callback(test *t, int *counter, WGPUCallbackMode mode) {
    WGPUBufferMapCallbackInfo info = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    info.mode = mode;
    info.callback = on_mapped;
    info.userdata1 = t;
    info.userdata2 = counter;
    return info;
}
static WGPUFuture pop_error_scope(test *t) {
    WGPUPopErrorScopeCallbackInfo info = WGPU_POP_ERROR_SCOPE_CALLBACK_INFO_INIT;
    info.mode = WGPUCallbackMode_AllowProcessEvents;
    info.callback = on_popped;
    info.userdata1 = t;
    return wgpuDevicePopErrorScope(t->device, info);
}

/* ---- Steps shared by run() and extras() ------------------------------------------------------------------------ */

static bool request_adapter(test *t) {
    WGPURequestAdapterOptions options = WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    options.powerPreference = WGPUPowerPreference_HighPerformance;
    WGPURequestAdapterCallbackInfo info = WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    info.mode = WGPUCallbackMode_AllowProcessEvents;
    info.callback = on_adapter;
    info.userdata1 = t;
    wgpuInstanceRequestAdapter(t->instance, &options, info);
    return true;
}
static bool request_device(test *t) {
    if (!t->adapter) return false;
    say(t, "adapter: ok");
    WGPULimits limits = WGPU_LIMITS_INIT;
    if (wgpuAdapterGetLimits(t->adapter, &limits) != WGPUStatus_Success) { fail(t, "adapter limits", text("")); return false; }
    say(t, "limits: maxBindGroups >= 4 %s, maxComputeWorkgroupSizeX >= 64 %s", limits.maxBindGroups >= 4 ? "yes" : "no",
        limits.maxComputeWorkgroupSizeX >= 64 ? "yes" : "no");
    WGPUSupportedFeatures features = WGPU_SUPPORTED_FEATURES_INIT;
    wgpuAdapterGetFeatures(t->adapter, &features);
    bool consistent = true;
    for (size_t i = 0; i < features.featureCount; i++) consistent &= wgpuAdapterHasFeature(t->adapter, features.features[i]) != 0;
    say(t, "features: %s", consistent ? "consistent" : "inconsistent");
    wgpuSupportedFeaturesFreeMembers(features);
    WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
    if (wgpuAdapterGetInfo(t->adapter, &info) == WGPUStatus_Success) say(t, "adapter info: ok");
    wgpuAdapterInfoFreeMembers(info);

    WGPUDeviceDescriptor descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    descriptor.label = text("WebGPUTest device");
    descriptor.deviceLostCallbackInfo.mode = WGPUCallbackMode_AllowSpontaneous;
    descriptor.deviceLostCallbackInfo.callback = on_lost;
    descriptor.deviceLostCallbackInfo.userdata1 = t;
    descriptor.uncapturedErrorCallbackInfo.callback = on_uncaptured_error;
    descriptor.uncapturedErrorCallbackInfo.userdata1 = t;
    WGPURequestDeviceCallbackInfo callback = WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowProcessEvents;
    callback.callback = on_device;
    callback.userdata1 = t;
    wgpuAdapterRequestDevice(t->adapter, &descriptor, callback);
    return true;
}
static bool device_ready(test *t) {
    if (!t->device) return false;
    t->queue = wgpuDeviceGetQueue(t->device);
    WGPULimits limits = WGPU_LIMITS_INIT;
    say(t, "device: %s", wgpuDeviceGetLimits(t->device, &limits) == WGPUStatus_Success && limits.maxBindGroups >= 4 ? "ok" : "bad limits");
    return true;
}

/* ---- run(): compute ---------------------------------------------------------------------------------------------- */

static const char *COMPUTE_SHADER =
    "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
    "@compute @workgroup_size(8) fn main(@builtin(global_invocation_id) id: vec3u) {\n"
    "    data[id.x] = data[id.x] * 2u + 1u;\n"
    "}\n";

static bool compute(test *t) {
    const uint64_t bytes = 8 * sizeof(uint32_t);
    t->storage = buffer(t, WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc, bytes, true);
    uint32_t *input = wgpuBufferGetMappedRange(t->storage, 0, bytes);
    for (uint32_t i = 0; i < 8; i++) input[i] = i;
    wgpuBufferUnmap(t->storage);
    t->readback = buffer(t, WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst, bytes, false);

    WGPUShaderModule module = shader(t, COMPUTE_SHADER);
    WGPUComputePipelineDescriptor descriptor = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
    descriptor.compute.module = module;
    descriptor.compute.entryPoint = text("main");
    WGPUComputePipeline pipeline = wgpuDeviceCreateComputePipeline(t->device, &descriptor);
    wgpuShaderModuleRelease(module);
    /* wgpu-native reports the error at once and treats submitting an invalid pipeline as fatal: stop here instead. */
    if (t->failure[0]) { wgpuComputePipelineRelease(pipeline); return false; }
    WGPUBindGroupLayout layout = wgpuComputePipelineGetBindGroupLayout(pipeline, 0);
    WGPUBindGroupEntry entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.binding = 0;
    entry.buffer = t->storage;
    entry.size = bytes;
    WGPUBindGroupDescriptor group_descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    group_descriptor.layout = layout;
    group_descriptor.entryCount = 1;
    group_descriptor.entries = &entry;
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(t->device, &group_descriptor);

    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(t->device, NULL);
    WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(encoder, NULL);
    wgpuComputePassEncoderSetPipeline(pass, pipeline);
    wgpuComputePassEncoderSetBindGroup(pass, 0, group, 0, NULL);
    wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
    wgpuComputePassEncoderEnd(pass);
    wgpuComputePassEncoderRelease(pass);
    wgpuCommandEncoderCopyBufferToBuffer(encoder, t->storage, 0, t->readback, 0, bytes);
    submit(t, encoder);

    WGPUQueueWorkDoneCallbackInfo done = WGPU_QUEUE_WORK_DONE_CALLBACK_INFO_INIT;
    done.mode = WGPUCallbackMode_AllowSpontaneous;
    done.callback = on_work_done;
    done.userdata1 = t;
    wgpuQueueOnSubmittedWorkDone(t->queue, done);
    /* An explicit size: wgpu-native 29 takes WGPU_WHOLE_MAP_SIZE literally here. */
    wgpuBufferMapAsync(t->readback, WGPUMapMode_Read, 0, bytes, map_callback(t, &t->mapped, WGPUCallbackMode_AllowProcessEvents));

    wgpuBindGroupRelease(group);
    wgpuBindGroupLayoutRelease(layout);
    wgpuComputePipelineRelease(pipeline);
    return true;
}
static bool compute_done(test *t) {
    if (t->mapped < 1 || !t->work_done) return false;
    const uint32_t *output = wgpuBufferGetConstMappedRange(t->readback, 0, 8 * sizeof(uint32_t));
    char line[128];
    int n = snprintf(line, sizeof line, "compute:");
    for (int i = 0; i < 8 && output; i++) n += snprintf(line + n, sizeof line - (size_t)n, " %u", output[i]);
    say(t, "%s", output ? line : "compute: no mapped range");
    wgpuBufferUnmap(t->readback);
    say(t, "queue work done: ok");
    return true;
}

/* ---- run(): error scopes --------------------------------------------------------------------------------------- */

static bool error_scope(test *t) {
    wgpuDevicePushErrorScope(t->device, WGPUErrorFilter_Validation);
    WGPUBuffer invalid = buffer(t, WGPUBufferUsage_MapRead | WGPUBufferUsage_Storage, 16, false); /* not allowed together */
    wgpuBufferRelease(invalid);
    pop_error_scope(t);
    return true;
}
static bool error_scope_done(test *t) {
    if (!t->popped) return false;
    say(t, "error scope: %s", error_name(t->popped_type));
    return true;
}

/* ---- run(): rendering into a window and into a texture -------------------------------------------------------- */

static const char *RENDER_SHADER =
    "@group(0) @binding(0) var<uniform> color: vec4f;\n"
    "@vertex fn vs(@location(0) position: vec2f) -> @builtin(position) vec4f { return vec4f(position, 0.0, 1.0); }\n"
    "@fragment fn fs() -> @location(0) vec4f { return color; }\n";

/* The window's surface from aqua_window_surface: the chained source of each platform. */
static WGPUSurface create_surface(test *t) {
    aqua_surface s;
    if (!aqua_window_surface(t->window, &s)) return NULL;
    WGPUSurfaceDescriptor descriptor = WGPU_SURFACE_DESCRIPTOR_INIT;
    switch (s.kind) {
#if defined(AQUA_OS_WEB)
    case AQUA_SURFACE_CANVAS: {
        char selector[32];
        snprintf(selector, sizeof selector, "#aqua-window-%u", (unsigned)(uintptr_t)s.handle);
        WGPUEmscriptenSurfaceSourceCanvasHTMLSelector source = WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
        source.selector = text(selector);
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(t->instance, &descriptor);
    }
#endif
    case AQUA_SURFACE_WIN32: {
        WGPUSurfaceSourceWindowsHWND source = WGPU_SURFACE_SOURCE_WINDOWS_HWND_INIT;
        source.hinstance = s.display;
        source.hwnd = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(t->instance, &descriptor);
    }
    case AQUA_SURFACE_X11: {
        WGPUSurfaceSourceXlibWindow source = WGPU_SURFACE_SOURCE_XLIB_WINDOW_INIT;
        source.display = s.display;
        source.window = (uint64_t)(uintptr_t)s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(t->instance, &descriptor);
    }
    case AQUA_SURFACE_WAYLAND: {
        WGPUSurfaceSourceWaylandSurface source = WGPU_SURFACE_SOURCE_WAYLAND_SURFACE_INIT;
        source.display = s.display;
        source.surface = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(t->instance, &descriptor);
    }
    case AQUA_SURFACE_ANDROID: {
        WGPUSurfaceSourceAndroidNativeWindow source = WGPU_SURFACE_SOURCE_ANDROID_NATIVE_WINDOW_INIT;
        source.window = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(t->instance, &descriptor);
    }
    default: return NULL;
    }
}
static void configure(test *t) {
    int32_t width, height;
    aqua_window_framebuffer_size(t->window, &width, &height);
    t->surface_width = (uint32_t)width;
    t->surface_height = (uint32_t)height;
    WGPUSurfaceConfiguration configuration = WGPU_SURFACE_CONFIGURATION_INIT;
    configuration.device = t->device;
    configuration.format = t->format;
    configuration.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc; /* CopySrc: the last frame is read back */
    configuration.width = t->surface_width;
    configuration.height = t->surface_height;
    configuration.presentMode = WGPUPresentMode_Fifo;
    wgpuSurfaceConfigure(t->surface, &configuration);
}
/* The triangle pipeline's descriptor and the structs it points to. */
typedef struct render_pipeline {
    WGPUVertexAttribute attribute;
    WGPUVertexBufferLayout vertex_buffer;
    WGPUColorTargetState target;
    WGPUFragmentState fragment;
    WGPURenderPipelineDescriptor descriptor;
} render_pipeline;
static WGPURenderPipelineDescriptor *describe_pipeline(render_pipeline *p, WGPUTextureFormat format, WGPUShaderModule module, WGPUPipelineLayout layout) {
    p->attribute = WGPU_VERTEX_ATTRIBUTE_INIT;
    p->attribute.format = WGPUVertexFormat_Float32x2;
    p->vertex_buffer = WGPU_VERTEX_BUFFER_LAYOUT_INIT;
    p->vertex_buffer.stepMode = WGPUVertexStepMode_Vertex;
    p->vertex_buffer.arrayStride = 2 * sizeof(float);
    p->vertex_buffer.attributeCount = 1;
    p->vertex_buffer.attributes = &p->attribute;
    p->target = WGPU_COLOR_TARGET_STATE_INIT;
    p->target.format = format;
    p->fragment = WGPU_FRAGMENT_STATE_INIT;
    p->fragment.module = module;
    p->fragment.entryPoint = text("fs");
    p->fragment.targetCount = 1;
    p->fragment.targets = &p->target;
    p->descriptor = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
    p->descriptor.layout = layout; /* NULL: the automatic layout */
    p->descriptor.vertex.module = module;
    p->descriptor.vertex.entryPoint = text("vs");
    p->descriptor.vertex.bufferCount = 1;
    p->descriptor.vertex.buffers = &p->vertex_buffer;
    p->descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    p->descriptor.fragment = &p->fragment;
    return &p->descriptor;
}
static bool open_window(test *t) {
    char *error = NULL;
    t->window = aqua_window_create(t->rt, 160, 120, "WebGPU test", AQUA_WINDOW_FIXED_SIZE, &error);
    if (!t->window) { fail(t, "window", text(error ? error : "")); aqua_free(error); return false; }
    t->surface = create_surface(t);
    if (!t->surface) { fail(t, "surface", text("not created")); return false; }
    WGPUSurfaceCapabilities capabilities = WGPU_SURFACE_CAPABILITIES_INIT;
    if (wgpuSurfaceGetCapabilities(t->surface, t->adapter, &capabilities) != WGPUStatus_Success || !capabilities.formatCount
        || !(capabilities.usages & WGPUTextureUsage_CopySrc)) {
        fail(t, "surface", text("no capabilities, or its textures cannot be copied"));
        return false;
    }
    t->format = capabilities.formats[0];
    wgpuSurfaceCapabilitiesFreeMembers(capabilities);
    configure(t);
    say(t, "surface: configured");

    /* An explicit bind group layout and pipeline layout (compute() uses the pipeline's automatic layout). */
    WGPUBindGroupLayoutEntry layout_entry = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
    layout_entry.binding = 0;
    layout_entry.visibility = WGPUShaderStage_Fragment;
    layout_entry.buffer.type = WGPUBufferBindingType_Uniform;
    layout_entry.buffer.minBindingSize = 16;
    WGPUBindGroupLayoutDescriptor layout_descriptor = WGPU_BIND_GROUP_LAYOUT_DESCRIPTOR_INIT;
    layout_descriptor.entryCount = 1;
    layout_descriptor.entries = &layout_entry;
    WGPUBindGroupLayout group_layout = wgpuDeviceCreateBindGroupLayout(t->device, &layout_descriptor);
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor = WGPU_PIPELINE_LAYOUT_DESCRIPTOR_INIT;
    pipeline_layout_descriptor.bindGroupLayoutCount = 1;
    pipeline_layout_descriptor.bindGroupLayouts = &group_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(t->device, &pipeline_layout_descriptor);
    WGPUShaderModule module = shader(t, RENDER_SHADER);
    render_pipeline description;
    t->pipeline = wgpuDeviceCreateRenderPipeline(t->device, describe_pipeline(&description, t->format, module, pipeline_layout));

    const float red[4] = {1, 0, 0, 1}, triangle[6] = {0.0f, 0.8f, -0.8f, -0.8f, 0.8f, -0.8f};
    t->color = buffer(t, WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst, sizeof red, false);
    wgpuQueueWriteBuffer(t->queue, t->color, 0, red, sizeof red);
    t->vertices = buffer(t, WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst, sizeof triangle, false);
    wgpuQueueWriteBuffer(t->queue, t->vertices, 0, triangle, sizeof triangle);
    WGPUBindGroupEntry entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.buffer = t->color;
    WGPUBindGroupDescriptor group_descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    group_descriptor.layout = group_layout;
    group_descriptor.entryCount = 1;
    group_descriptor.entries = &entry;
    t->bind_group = wgpuDeviceCreateBindGroup(t->device, &group_descriptor);

    wgpuShaderModuleRelease(module);
    wgpuPipelineLayoutRelease(pipeline_layout);
    wgpuBindGroupLayoutRelease(group_layout);
    return true;
}
/* Clears `view` to blue and draws the red triangle. */
static WGPUCommandEncoder draw(test *t, WGPUTextureView view) {
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(t->device, NULL);
    WGPURenderPassColorAttachment attachment = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
    attachment.view = view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = (WGPUColor){0, 0, 1, 1};
    WGPURenderPassDescriptor descriptor = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
    descriptor.colorAttachmentCount = 1;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
    wgpuRenderPassEncoderSetPipeline(pass, t->pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0, t->bind_group, 0, NULL);
    wgpuRenderPassEncoderSetVertexBuffer(pass, 0, t->vertices, 0, WGPU_WHOLE_SIZE);
    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return encoder;
}
/* Copies a width x height texture into a new mappable buffer (rows of `row` bytes) and maps it, counting in `mapped`. */
static WGPUBuffer copy_pixels(test *t, WGPUCommandEncoder encoder, WGPUTexture texture, uint32_t width, uint32_t height, uint32_t row, int *mapped) {
    WGPUBuffer pixels = buffer(t, WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead, (uint64_t)row * height, false);
    WGPUTexelCopyTextureInfo source = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    source.texture = texture;
    WGPUTexelCopyBufferInfo destination = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    destination.buffer = pixels;
    destination.layout.bytesPerRow = row;
    destination.layout.rowsPerImage = height;
    WGPUExtent3D extent = {width, height, 1};
    wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
    submit(t, encoder);
    wgpuBufferMapAsync(pixels, WGPUMapMode_Read, 0, (size_t)row * height, map_callback(t, mapped, WGPUCallbackMode_AllowProcessEvents));
    return pixels;
}
/* Prints the RGBA bytes of the pixels at the center and near the top left corner of mapped copy_pixels output. */
static void print_pixels(test *t, const char *name, WGPUBuffer buffer, uint32_t width, uint32_t height, uint32_t row) {
    const uint8_t *pixels = wgpuBufferGetConstMappedRange(buffer, 0, (size_t)row * height);
    if (!pixels) { fail(t, name, text("no mapped range")); return; }
    bool bgra = t->format == WGPUTextureFormat_BGRA8Unorm || t->format == WGPUTextureFormat_BGRA8UnormSrgb;
    const uint32_t points[2][2] = {{width / 2, height / 2}, {1, 1}};
    for (int i = 0; i < 2; i++) {
        const uint8_t *p = pixels + points[i][1] * row + points[i][0] * 4;
        say(t, "%s %s: %u %u %u %u", name, i ? "corner" : "center", p[bgra ? 2 : 0], p[1], p[bgra ? 0 : 2], p[3]);
    }
    wgpuBufferUnmap(buffer);
}
/* One frame per tick; the last one is also copied for surface_read. */
static bool render_frames(test *t) {
    aqua_windows_poll(t->rt);
    WGPUSurfaceTexture frame = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(t->surface, &frame);
    if (frame.status != WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal && frame.status != WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal) {
        if (frame.texture) wgpuTextureRelease(frame.texture);
        configure(t); /* outdated or lost: configure again and try the next tick */
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(frame.texture, NULL);
    WGPUCommandEncoder encoder = draw(t, view);
    if (++t->frames < FRAMES) submit(t, encoder);
    else {
        t->surface_row = (wgpuTextureGetWidth(frame.texture) * 4 + 255) / 256 * 256;
        t->surface_pixels = copy_pixels(t, encoder, frame.texture, wgpuTextureGetWidth(frame.texture), wgpuTextureGetHeight(frame.texture),
            t->surface_row, &t->surface_mapped);
    }
    wgpuSurfacePresent(t->surface);
    wgpuTextureViewRelease(view);
    wgpuTextureRelease(frame.texture);
    if (t->frames < FRAMES) return false;
    say(t, "frames: %d", t->frames);
    return true;
}
static bool surface_read(test *t) {
    if (!t->surface_mapped) return false;
    print_pixels(t, "surface", t->surface_pixels, t->surface_width, t->surface_height, t->surface_row);
    return true;
}
static bool read_texture(test *t) {
    WGPUTextureDescriptor descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    descriptor.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
    descriptor.size = (WGPUExtent3D){SIZE, SIZE, 1};
    descriptor.format = t->format;
    t->target = wgpuDeviceCreateTexture(t->device, &descriptor);
    say(t, "texture: %ux%u, %s format", wgpuTextureGetWidth(t->target), wgpuTextureGetHeight(t->target),
        wgpuTextureGetFormat(t->target) == t->format ? "surface" : "other");
    WGPUTextureView view = wgpuTextureCreateView(t->target, NULL);
    t->pixels = copy_pixels(t, draw(t, view), t->target, SIZE, SIZE, ROW_BYTES, &t->mapped);
    wgpuTextureViewRelease(view);
    return true;
}
static bool texture_read(test *t) {
    if (t->mapped < 2) return false;
    print_pixels(t, "texture", t->pixels, SIZE, SIZE, ROW_BYTES);
    return true;
}

/* ---- extras(): web only, the functions wgpu-native does not implement ------------------------------------------- */

static void on_compute_pipeline(WGPUCreatePipelineAsyncStatus status, WGPUComputePipeline pipeline, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status != WGPUCreatePipelineAsyncStatus_Success) { fail(t, "compute pipeline", message); return; }
    t->compute_async = pipeline;
    t->pipelines++;
}
static void on_render_pipeline(WGPUCreatePipelineAsyncStatus status, WGPURenderPipeline pipeline, WGPUStringView message, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status != WGPUCreatePipelineAsyncStatus_Success) { fail(t, "render pipeline", message); return; }
    t->render_async = pipeline;
    t->pipelines++;
}
static void on_compilation(WGPUCompilationInfoRequestStatus status, struct WGPUCompilationInfo const *info, void *data, void *unused) {
    (void)unused;
    test *t = data;
    if (status != WGPUCompilationInfoRequestStatus_Success) { fail(t, "compilation info", text("")); return; }
    t->compilation_line = -1;
    for (size_t i = 0; i < info->messageCount; i++)
        if (info->messages[i].type == WGPUCompilationMessageType_Error && info->messages[i].message.length) { t->compilation_line = (int)info->messages[i].lineNum; break; }
}

static bool extras_queries(test *t) {
    WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
    wgpuDeviceGetAdapterInfo(t->device, &info);
    say(t, "device adapter info: backend %s", info.backendType == WGPUBackendType_WebGPU ? "WebGPU" : "other");
    wgpuAdapterInfoFreeMembers(info);
    WGPUSupportedWGSLLanguageFeatures wgsl = WGPU_SUPPORTED_WGSL_LANGUAGE_FEATURES_INIT;
    wgpuInstanceGetWGSLLanguageFeatures(t->instance, &wgsl);
    bool consistent = true;
    for (size_t i = 0; i < wgsl.featureCount; i++) consistent &= wgpuInstanceHasWGSLLanguageFeature(t->instance, wgsl.features[i]) != 0;
    say(t, "wgsl language features: %s", consistent ? "consistent" : "inconsistent");
    wgpuSupportedWGSLLanguageFeaturesFreeMembers(wgsl);
    WGPUSupportedInstanceFeatures instance_features = WGPU_SUPPORTED_INSTANCE_FEATURES_INIT;
    wgpuGetInstanceFeatures(&instance_features);
    WGPUInstanceLimits instance_limits = WGPU_INSTANCE_LIMITS_INIT;
    wgpuGetInstanceLimits(&instance_limits);
    say(t, "instance features: %u, timed wait any: %s", (unsigned)instance_features.featureCount,
        wgpuHasInstanceFeature(WGPUInstanceFeatureName_TimedWaitAny) || instance_limits.timedWaitAnyMaxCount ? "yes" : "no");
    wgpuSupportedInstanceFeaturesFreeMembers(instance_features);
    say(t, "lost future: %s", wgpuDeviceGetLostFuture(t->device).id ? "ok" : "none");
    wgpuDeviceSetLabel(t->device, text("labelled device"));
    wgpuQueueSetLabel(t->queue, (WGPUStringView){"queue with an explicit length", 5});
    return true;
}
static bool extras_pipelines(test *t) {
    WGPUShaderModule compute_module = shader(t, COMPUTE_SHADER);
    WGPUComputePipelineDescriptor compute_descriptor = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
    compute_descriptor.label = text("async compute");
    compute_descriptor.compute.module = compute_module; /* entry point left null: the module's only one */
    WGPUCreateComputePipelineAsyncCallbackInfo compute_info = WGPU_CREATE_COMPUTE_PIPELINE_ASYNC_CALLBACK_INFO_INIT;
    compute_info.mode = WGPUCallbackMode_AllowProcessEvents;
    compute_info.callback = on_compute_pipeline;
    compute_info.userdata1 = t;
    wgpuDeviceCreateComputePipelineAsync(t->device, &compute_descriptor, compute_info);
    wgpuShaderModuleRelease(compute_module);

    WGPUShaderModule render_module = shader(t, RENDER_SHADER);
    WGPUCreateRenderPipelineAsyncCallbackInfo render_info = WGPU_CREATE_RENDER_PIPELINE_ASYNC_CALLBACK_INFO_INIT;
    render_info.mode = WGPUCallbackMode_AllowSpontaneous;
    render_info.callback = on_render_pipeline;
    render_info.userdata1 = t;
    render_pipeline description;
    wgpuDeviceCreateRenderPipelineAsync(t->device, describe_pipeline(&description, WGPUTextureFormat_RGBA8Unorm, render_module, NULL), render_info);
    wgpuShaderModuleRelease(render_module);

    wgpuDevicePushErrorScope(t->device, WGPUErrorFilter_Validation);
    WGPUShaderModule broken = shader(t, "@compute @workgroup_size(1) fn main() {\n    no_such_function();\n}\n");
    pop_error_scope(t);
    WGPUCompilationInfoCallbackInfo compilation = WGPU_COMPILATION_INFO_CALLBACK_INFO_INIT;
    compilation.mode = WGPUCallbackMode_AllowProcessEvents;
    compilation.callback = on_compilation;
    compilation.userdata1 = t;
    wgpuShaderModuleGetCompilationInfo(broken, compilation);
    wgpuShaderModuleRelease(broken);
    return true;
}
static bool extras_pipelines_done(test *t) {
    if (t->pipelines < 2 || !t->compilation_line || !t->popped) return false;
    say(t, "async pipelines: ok");
    say(t, "compilation info: error on line %d", t->compilation_line);
    say(t, "shader error scope: %s", error_name(t->popped_type));
    wgpuComputePipelineRelease(t->compute_async);
    wgpuRenderPipelineRelease(t->render_async);
    return true;
}
static bool extras_mapping(test *t) {
    const uint32_t values[4] = {7, 8, 9, 10};
    t->upload = buffer(t, WGPUBufferUsage_CopySrc, sizeof values, true);
    wgpuBufferWriteMappedRange(t->upload, 0, values, sizeof values);
    /* Overlapping ranges, which WebGPU's getMappedRange forbids and webgpu.h allows. */
    const uint32_t *first = wgpuBufferGetConstMappedRange(t->upload, 0, 8);
    uint32_t *second = wgpuBufferGetMappedRange(t->upload, 4, WGPU_WHOLE_MAP_SIZE);
    second[2] = 42;
    say(t, "overlapping ranges: %u %u / %u %u %u", first[0], first[1], second[0], second[1], second[2]);
    wgpuBufferUnmap(t->upload);
    t->download = buffer(t, WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead, sizeof values, false);
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(t->device, NULL);
    wgpuCommandEncoderCopyBufferToBuffer(encoder, t->upload, 0, t->download, 0, WGPU_WHOLE_SIZE);
    submit(t, encoder);
    t->download_future = wgpuBufferMapAsync(t->download, WGPUMapMode_Read, 0, sizeof values, map_callback(t, &t->mapped, WGPUCallbackMode_WaitAnyOnly));
    say(t, "map state: %s", wgpuBufferGetMapState(t->download) == WGPUBufferMapState_Pending ? "pending" : "other");
    WGPUFutureWaitInfo wait = {t->download_future, false};
    say(t, "wait any with a timeout: %s", wgpuInstanceWaitAny(t->instance, 1, &wait, 1000000) == WGPUWaitStatus_Error ? "error" : "other");
    return true;
}
static bool extras_mapping_done(test *t) {
    WGPUFutureWaitInfo wait = {t->download_future, false};
    WGPUWaitStatus status = wgpuInstanceWaitAny(t->instance, 1, &wait, 0); /* polls */
    if (status != WGPUWaitStatus_Success) return false;
    uint32_t values[4] = {0};
    WGPUStatus read = wgpuBufferReadMappedRange(t->download, 0, values, sizeof values);
    say(t, "wait any: %s, map state: %s", wait.completed && t->mapped == 1 ? "completed" : "not completed",
        wgpuBufferGetMapState(t->download) == WGPUBufferMapState_Mapped ? "mapped" : "other");
    say(t, "read mapped range: %s %u %u %u %u", read == WGPUStatus_Success ? "ok" : "error", values[0], values[1], values[2], values[3]);
    wgpuBufferUnmap(t->download);
    return true;
}
static bool extras_uncaptured(test *t) {
    t->expect_uncaptured = true;
    WGPUBuffer invalid = buffer(t, WGPUBufferUsage_MapRead | WGPUBufferUsage_Storage, 16, false);
    wgpuBufferRelease(invalid);
    /* Browsers report uncaptured errors once the device processes queue work. */
    wgpuQueueSubmit(t->queue, 0, NULL);
    WGPUQueueWorkDoneCallbackInfo done = WGPU_QUEUE_WORK_DONE_CALLBACK_INFO_INIT;
    done.mode = WGPUCallbackMode_AllowProcessEvents;
    done.callback = on_work_done;
    done.userdata1 = t;
    wgpuQueueOnSubmittedWorkDone(t->queue, done);
    return true;
}
static bool extras_uncaptured_done(test *t) {
    if (!t->uncaptured) return false;
    say(t, "uncaptured error: %s", error_name(t->uncaptured_type));
    wgpuDeviceDestroy(t->device);
    return true;
}
static bool extras_lost(test *t) {
    if (!t->lost) return false;
    say(t, "device lost: %s", t->lost_reason == WGPUDeviceLostReason_Destroyed ? "destroyed" : "other");
    return true;
}

/* ---- Running steps ----------------------------------------------------------------------------------------------- */

static const step RUN_STEPS[] = {request_adapter, request_device, device_ready, compute, compute_done, error_scope,
    error_scope_done, open_window, render_frames, surface_read, read_texture, texture_read, NULL};
static const step EXTRA_STEPS[] = {request_adapter, request_device, device_ready, extras_queries, extras_pipelines,
    extras_pipelines_done, extras_mapping, extras_mapping_done, extras_uncaptured, extras_uncaptured_done, extras_lost, NULL};

static void release_all(test *t) {
    t->expect_uncaptured = true; /* nothing is checked any more */
    if (t->pixels) wgpuBufferRelease(t->pixels);
    if (t->surface_pixels) wgpuBufferRelease(t->surface_pixels);
    if (t->target) wgpuTextureRelease(t->target);
    if (t->bind_group) wgpuBindGroupRelease(t->bind_group);
    if (t->pipeline) wgpuRenderPipelineRelease(t->pipeline);
    if (t->vertices) wgpuBufferRelease(t->vertices);
    if (t->color) wgpuBufferRelease(t->color);
    if (t->surface) { wgpuSurfaceUnconfigure(t->surface); wgpuSurfaceRelease(t->surface); }
    if (t->window) aqua_window_destroy(t->window);
    if (t->storage) wgpuBufferRelease(t->storage);
    if (t->readback) wgpuBufferRelease(t->readback);
    if (t->upload) wgpuBufferRelease(t->upload);
    if (t->download) wgpuBufferRelease(t->download);
    if (t->queue) wgpuQueueRelease(t->queue);
    if (t->device) wgpuDeviceRelease(t->device);
    if (t->adapter) wgpuAdapterRelease(t->adapter);
    if (t->instance) wgpuInstanceRelease(t->instance);
}
/* Delivers the result as posted work: the runtime's loop does not notice a script resumed from inside a tick. */
static void finish(aqua_runtime *rt, void *data) {
    (void)rt;
    test *t = data;
    aqua_pending *pending = t->pending;
    release_all(t);
    t->pending = NULL;
    if (t->failure[0]) aqua_reject(pending, "%s", t->failure);
    else aqua_resolve(pending, aqua_void());
}
static double tick(aqua_runtime *rt, double now, void *data) {
    (void)now;
    test *t = data;
    wgpuInstanceProcessEvents(t->instance);
    while (!t->failure[0] && *t->steps && (*t->steps)(t)) t->steps++;
    if (!t->failure[0] && *t->steps) return 1; /* waiting for a callback or the next frame */
    aqua_post(rt, finish, t);
    return -1;
}
static void start(aqua_call *call, const step *steps) {
    test *t = &state;
    if (t->pending) { aqua_fail(call, "A WebGPUTest is already running."); return; }
    memset(t, 0, sizeof *t);
    t->rt = aqua_call_runtime(call);
    t->steps = steps;
    t->pending = aqua_defer(call);
    t->instance = wgpuCreateInstance(NULL);
    aqua_schedule(t->rt, 0, tick, t);
}
static void run(aqua_call *call) { start(call, RUN_STEPS); }
static void extras(aqua_call *call) {
#if defined(AQUA_OS_WEB)
    start(call, EXTRA_STEPS);
#else
    (void)EXTRA_STEPS;
    aqua_fail(call, "WebGPUTest.extras needs the web: wgpu-native does not implement these webgpu.h functions.");
#endif
}

AQUA_LIBRARY(webgpu_test) {
    aqua_class *module = aqua_define_module(library, "WebGPUTest");
    aqua_define_function(module, "run", run, "");
    aqua_define_function(module, "extras", extras, "");
}
