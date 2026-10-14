#include <android/log.h>
#include <android/native_window_jni.h>
#include <jni.h>
#include <sys/system_properties.h>
#include <webgpu/webgpu.h>
#include <webgpu/wgpu.h>

#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <nlohmann/json.hpp>
#include <stdexcept>
#include <thread>
#include <unordered_map>
#include <vector>
#include <array>
#include "gpu_batch.h"

using Json = nlohmann::json;
namespace {
std::mutex gate;
ANativeWindow* window = nullptr;
uint64_t surfaceGeneration = 0, nextId = 1;
WGPUStringView text(const std::string& s) { return {s.data(), s.size()}; }
WGPUStringView text(const char* s) { return {s, std::strlen(s)}; }
struct Texture {
  WGPUTexture handle{};
  WGPUTextureView view{};
  uint32_t width{}, height{};
  WGPUTextureView sampledView() {
    if (!view) view = wgpuTextureCreateView(handle, nullptr);
    return view;
  }
  ~Texture() {
    if (view) wgpuTextureViewRelease(view);
    if (handle) wgpuTextureRelease(handle);
  }
};
struct Target : Texture {
  WGPUTexture depth{};
  WGPUTextureView depthView{};
  bool initialized = false, depthInitialized = false;
  ~Target() {
    if (depthView) wgpuTextureViewRelease(depthView);
    if (depth) wgpuTextureRelease(depth);
  }
};
struct Buffer {
  WGPUBuffer handle{};
  size_t size{};
  bool mesh = false;
  ~Buffer() {
    if (handle) wgpuBufferRelease(handle);
  }
};
struct Shader {
  WGPUShaderModule vertex{}, fragment{};
  std::unordered_map<int, WGPURenderPipeline> pipelines;
  ~Shader() {
    for (auto& p : pipelines) wgpuRenderPipelineRelease(p.second);
    if (vertex) wgpuShaderModuleRelease(vertex);
    if (fragment) wgpuShaderModuleRelease(fragment);
  }
};
struct FrameStorage {
  WGPUBuffer vertex{}, uniforms{}, user{};
  size_t vertexCapacity{}, uniformCapacity{}, userCapacity{};
  WGPUBindGroup userGroup{};
  std::unordered_map<uint64_t, WGPUBindGroup> groups;
  std::vector<uint8_t> vertexData, uniformData, userData;
  void clearGroups() {
    for (auto& group : groups) wgpuBindGroupRelease(group.second);
    groups.clear();
    if (userGroup) wgpuBindGroupRelease(userGroup);
    userGroup = nullptr;
  }
  void forget(uint64_t id) {
    auto found = groups.find(id);
    if (found != groups.end()) { wgpuBindGroupRelease(found->second); groups.erase(found); }
  }
  ~FrameStorage() {
    clearGroups();
    if (vertex) wgpuBufferRelease(vertex);
    if (uniforms) wgpuBufferRelease(uniforms);
    if (user) wgpuBufferRelease(user);
  }
};
struct Device {
  WGPUInstance instance{};
  WGPUAdapter adapter{};
  WGPUDevice handle{};
  WGPUQueue queue{};
  WGPUBindGroupLayout layout{}, userLayout{};
  WGPUPipelineLayout pipelineLayout{};
  WGPUSampler sampler{};
  WGPUSurface surface{};
  uint32_t surfaceWidth{}, surfaceHeight{}, uniformStride{}, userStride{};
  uint64_t generation = 0;
  WGPUTextureFormat surfaceFormat{};
  WGPURenderPipeline presentPipeline{};
  std::string error;
  std::unordered_map<uint64_t, std::unique_ptr<Buffer>> buffers;
  std::unordered_map<uint64_t, std::unique_ptr<Texture>> textures;
  std::unordered_map<uint64_t, std::unique_ptr<Target>> targets;
  std::unordered_map<uint64_t, std::unique_ptr<Shader>> shaders;
  std::array<std::unique_ptr<FrameStorage>, 3> frames;
  size_t frameIndex = 0;
  ~Device() {
    for (auto& frame : frames) frame.reset();
    buffers.clear();
    textures.clear();
    targets.clear();
    shaders.clear();
    if (presentPipeline) wgpuRenderPipelineRelease(presentPipeline);
    if (surface) {
      wgpuSurfaceUnconfigure(surface);
      wgpuSurfaceRelease(surface);
    }
    if (sampler) wgpuSamplerRelease(sampler);
    if (pipelineLayout) wgpuPipelineLayoutRelease(pipelineLayout);
    if (layout) wgpuBindGroupLayoutRelease(layout);
    if (userLayout) wgpuBindGroupLayoutRelease(userLayout);
    if (queue) wgpuQueueRelease(queue);
    if (handle) wgpuDeviceRelease(handle);
    if (adapter) wgpuAdapterRelease(adapter);
    if (instance) wgpuInstanceRelease(instance);
  }
  void check() {
    if (!error.empty()) throw std::runtime_error(error);
  }
};
std::unordered_map<uint64_t, std::unique_ptr<Device>> devices;
template <class T>
T& get(std::unordered_map<uint64_t, std::unique_ptr<T>>& table, uint64_t id) {
  auto it = table.find(id);
  if (it == table.end())
    throw std::runtime_error("Disposed or foreign GPU resource");
  return *it->second;
}
void submit(Device& d, WGPUCommandEncoder encoder) {
  auto desc = WGPU_COMMAND_BUFFER_DESCRIPTOR_INIT;
  auto command = wgpuCommandEncoderFinish(encoder, &desc);
  wgpuQueueSubmit(d.queue, 1, &command);
  wgpuCommandBufferRelease(command);
  wgpuCommandEncoderRelease(encoder);
  d.check();
}
WGPUBuffer buffer(Device& d, const void* bytes, size_t size,
                  WGPUBufferUsage usage) {
  auto desc = WGPU_BUFFER_DESCRIPTOR_INIT;
  desc.size = size;
  desc.usage = usage | WGPUBufferUsage_CopyDst;
  auto b = wgpuDeviceCreateBuffer(d.handle, &desc);
  if (bytes) wgpuQueueWriteBuffer(d.queue, b, 0, bytes, size);
  d.check();
  return b;
}
WGPUTexture texture(Device& d, uint32_t width, uint32_t height,
                    WGPUTextureFormat format, WGPUTextureUsage usage) {
  auto desc = WGPU_TEXTURE_DESCRIPTOR_INIT;
  desc.size = {width, height, 1};
  desc.format = format;
  desc.usage = usage;
  return wgpuDeviceCreateTexture(d.handle, &desc);
}
WGPUShaderModule shader(Device& d, const std::string& code) {
  auto wgsl = WGPU_SHADER_SOURCE_WGSL_INIT;
  wgsl.code = text(code);
  auto desc = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
  desc.nextInChain = &wgsl.chain;
  auto s = wgpuDeviceCreateShaderModule(d.handle, &desc);
  d.check();
  return s;
}
std::unique_ptr<Device> createDevice() {
  wgpuSetLogCallback(
      [](WGPULogLevel, WGPUStringView message, void*) {
        __android_log_print(ANDROID_LOG_WARN, "AquariusGPU", "%.*s",
                            static_cast<int>(message.length), message.data);
      },
      nullptr);
  wgpuSetLogLevel(WGPULogLevel_Warn);
  auto d = std::make_unique<Device>();
  auto descriptor = WGPU_INSTANCE_DESCRIPTOR_INIT;
  WGPUInstanceExtras extras{};
  extras.chain.sType = static_cast<WGPUSType>(WGPUSType_InstanceExtras);
  extras.backends = WGPUInstanceBackend_Vulkan;
  // GFXStream advertises incomplete conformance. Permit its test adapter only
  // on an emulator.
  char hardware[PROP_VALUE_MAX]{};
  __system_property_get("ro.hardware", hardware);
  if (std::strcmp(hardware, "ranchu") == 0 ||
      std::strcmp(hardware, "goldfish") == 0)
    extras.flags = WGPUInstanceFlag_AllowUnderlyingNoncompliantAdapter;
  descriptor.nextInChain = &extras.chain;
  d->instance = wgpuCreateInstance(&descriptor);
  auto options = WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
  options.backendType = WGPUBackendType_Vulkan;
  auto callback = WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
  callback.mode = WGPUCallbackMode_AllowProcessEvents;
  callback.userdata1 = d.get();
  callback.callback = [](WGPURequestAdapterStatus status, WGPUAdapter adapter,
                         WGPUStringView message, void* user, void*) {
    auto d = static_cast<Device*>(user);
    if (status == WGPURequestAdapterStatus_Success)
      d->adapter = adapter;
    else
      d->error = std::string(message.data, message.length);
  };
  wgpuInstanceRequestAdapter(d->instance, &options, callback);
  for (int i = 0; i < 10000 && !d->adapter && d->error.empty(); i++) {
    wgpuInstanceProcessEvents(d->instance);
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  d->check();
  if (!d->adapter) throw std::runtime_error("Vulkan adapter unavailable");
  auto deviceDescriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
  deviceDescriptor.uncapturedErrorCallbackInfo.userdata1 = d.get();
  deviceDescriptor.uncapturedErrorCallbackInfo.callback =
      [](WGPUDevice const*, WGPUErrorType, WGPUStringView message, void* user,
         void*) {
        static_cast<Device*>(user)->error =
            std::string(message.data, message.length);
      };
  auto dc = WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
  dc.mode = WGPUCallbackMode_AllowProcessEvents;
  dc.userdata1 = d.get();
  dc.callback = [](WGPURequestDeviceStatus status, WGPUDevice device,
                   WGPUStringView message, void* user, void*) {
    auto d = static_cast<Device*>(user);
    if (status == WGPURequestDeviceStatus_Success)
      d->handle = device;
    else
      d->error = std::string(message.data, message.length);
  };
  wgpuAdapterRequestDevice(d->adapter, &deviceDescriptor, dc);
  for (int i = 0; i < 10000 && !d->handle && d->error.empty(); i++) {
    wgpuInstanceProcessEvents(d->instance);
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  d->check();
  if (!d->handle) throw std::runtime_error("WebGPU device unavailable");
  d->queue = wgpuDeviceGetQueue(d->handle);
  auto limits = WGPU_LIMITS_INIT;
  if (wgpuDeviceGetLimits(d->handle, &limits) != WGPUStatus_Success)
    throw std::runtime_error("Unable to read WebGPU device limits");
  auto alignment = limits.minUniformBufferOffsetAlignment;
  if (!alignment || alignment > 65536) throw std::runtime_error("Invalid uniform alignment");
  d->uniformStride = ((1120 + alignment - 1) / alignment) * alignment;
  d->userStride = ((1024 + alignment - 1) / alignment) * alignment;
  WGPUBindGroupLayoutEntry entries[3] = {WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT,
                                         WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT,
                                         WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT};
  entries[0].binding = 0;
  entries[0].visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
  entries[0].buffer.type = WGPUBufferBindingType_Uniform;
  entries[0].buffer.hasDynamicOffset = true;
  entries[0].buffer.minBindingSize = 1120;
  entries[1].binding = 1;
  entries[1].visibility = WGPUShaderStage_Fragment;
  entries[1].texture.sampleType = WGPUTextureSampleType_Float;
  entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
  entries[2].binding = 2;
  entries[2].visibility = WGPUShaderStage_Fragment;
  entries[2].sampler.type = WGPUSamplerBindingType_Filtering;
  auto layout = WGPU_BIND_GROUP_LAYOUT_DESCRIPTOR_INIT;
  layout.entryCount = 3;
  layout.entries = entries;
  d->layout = wgpuDeviceCreateBindGroupLayout(d->handle, &layout);
  auto entry = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
  entry.binding = 0;
  entry.visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
  entry.buffer.type = WGPUBufferBindingType_Uniform;
  entry.buffer.hasDynamicOffset = true;
  entry.buffer.minBindingSize = 1024;
  layout.entryCount = 1;
  layout.entries = &entry;
  d->userLayout = wgpuDeviceCreateBindGroupLayout(d->handle, &layout);
  WGPUBindGroupLayout layouts[] = {d->layout, d->userLayout};
  auto pipelineLayout = WGPU_PIPELINE_LAYOUT_DESCRIPTOR_INIT;
  pipelineLayout.bindGroupLayoutCount = 2;
  pipelineLayout.bindGroupLayouts = layouts;
  d->pipelineLayout =
      wgpuDeviceCreatePipelineLayout(d->handle, &pipelineLayout);
  auto sampler = WGPU_SAMPLER_DESCRIPTOR_INIT;
  sampler.magFilter = WGPUFilterMode_Linear;
  sampler.minFilter = WGPUFilterMode_Linear;
  d->sampler = wgpuDeviceCreateSampler(d->handle, &sampler);
  d->check();
  return d;
}
WGPURenderPipeline pipeline(Device& d, Shader& s, bool depth, int blend,
                            int mode) {
  int key = (depth ? 100 : 0) + blend * 10 + mode;
  if (s.pipelines.count(key)) return s.pipelines[key];
  WGPUVertexAttribute attributes[4] = {
      WGPU_VERTEX_ATTRIBUTE_INIT, WGPU_VERTEX_ATTRIBUTE_INIT,
      WGPU_VERTEX_ATTRIBUTE_INIT, WGPU_VERTEX_ATTRIBUTE_INIT};
  WGPUVertexFormat formats[] = {
      WGPUVertexFormat_Float32x3, WGPUVertexFormat_Float32x3,
      WGPUVertexFormat_Float32x2, WGPUVertexFormat_Float32x4};
  size_t offsets[] = {0, 12, 24, 32};
  for (int i = 0; i < 4; i++) {
    attributes[i].shaderLocation = i;
    attributes[i].offset = offsets[i];
    attributes[i].format = formats[i];
  }
  auto vertex = WGPU_VERTEX_BUFFER_LAYOUT_INIT;
  vertex.arrayStride = 48;
  vertex.attributeCount = 4;
  vertex.attributes = attributes;
  auto color = WGPU_COLOR_TARGET_STATE_INIT;
  color.format = WGPUTextureFormat_RGBA8Unorm;
  auto blending = WGPU_BLEND_STATE_INIT;
  blending.color.srcFactor = blend == 2   ? WGPUBlendFactor_Dst
                             : blend >= 3 ? WGPUBlendFactor_One
                                          : WGPUBlendFactor_SrcAlpha;
  blending.color.dstFactor = blend == 1                 ? WGPUBlendFactor_One
                             : blend == 2 || blend == 4 ? WGPUBlendFactor_Zero
                             : blend == 3 ? WGPUBlendFactor_OneMinusSrc
                                          : WGPUBlendFactor_OneMinusSrcAlpha;
  blending.alpha.srcFactor = WGPUBlendFactor_One;
  blending.alpha.dstFactor = blend == 1   ? WGPUBlendFactor_One
                             : blend == 4 ? WGPUBlendFactor_Zero
                                          : WGPUBlendFactor_OneMinusSrcAlpha;
  color.blend = &blending;
  auto fragment = WGPU_FRAGMENT_STATE_INIT;
  fragment.module = s.fragment;
  fragment.entryPoint = text("fs_main");
  fragment.targetCount = 1;
  fragment.targets = &color;
  auto depthState = WGPU_DEPTH_STENCIL_STATE_INIT;
  depthState.format = WGPUTextureFormat_Depth24Plus;
  depthState.depthWriteEnabled =
      depth ? WGPUOptionalBool_True : WGPUOptionalBool_False;
  depthState.depthCompare =
      depth ? WGPUCompareFunction_LessEqual : WGPUCompareFunction_Always;
  auto descriptor = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
  descriptor.layout = d.pipelineLayout;
  descriptor.vertex.module = s.vertex;
  descriptor.vertex.entryPoint = text("vs_main");
  descriptor.vertex.bufferCount = 1;
  descriptor.vertex.buffers = &vertex;
  descriptor.fragment = &fragment;
  descriptor.depthStencil = &depthState;
  descriptor.primitive.topology = mode == 1 ? WGPUPrimitiveTopology_LineList
                                  : mode == 0
                                      ? WGPUPrimitiveTopology_PointList
                                      : WGPUPrimitiveTopology_TriangleList;
  auto p = wgpuDeviceCreateRenderPipeline(d.handle, &descriptor);
  d.check();
  s.pipelines[key] = p;
  return p;
}
WGPURenderPassEncoder pass(Device& d, Target& t, WGPUCommandEncoder encoder,
                           const Json* clear = nullptr) {
  auto color = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
  color.view = t.sampledView();
  color.loadOp = clear || !t.initialized ? WGPULoadOp_Clear : WGPULoadOp_Load;
  color.storeOp = WGPUStoreOp_Store;
  if (clear)
    color.clearValue = {(*clear)[0], (*clear)[1], (*clear)[2], (*clear)[3]};
  auto depth = WGPU_RENDER_PASS_DEPTH_STENCIL_ATTACHMENT_INIT;
  if (!t.depthView) t.depthView = wgpuTextureCreateView(t.depth, nullptr);
  depth.view = t.depthView;
  depth.depthLoadOp =
      clear || !t.depthInitialized ? WGPULoadOp_Clear : WGPULoadOp_Load;
  depth.depthStoreOp = WGPUStoreOp_Store;
  depth.depthClearValue = 1;
  auto descriptor = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
  descriptor.colorAttachmentCount = 1;
  descriptor.colorAttachments = &color;
  descriptor.depthStencilAttachment = &depth;
  auto p = wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
  t.initialized = true;
  t.depthInitialized = true;
  return p;
}
void configureSurface(Device& d, Target& t) {
  if (d.generation != surfaceGeneration) {
    if (d.surface) {
      wgpuSurfaceUnconfigure(d.surface);
      wgpuSurfaceRelease(d.surface);
    }
    auto source = WGPU_SURFACE_SOURCE_ANDROID_NATIVE_WINDOW_INIT;
    source.window = window;
    auto sd = WGPU_SURFACE_DESCRIPTOR_INIT;
    sd.nextInChain = &source.chain;
    d.surface = wgpuInstanceCreateSurface(d.instance, &sd);
    d.generation = surfaceGeneration;
    d.surfaceWidth = d.surfaceHeight = 0;
    if (d.presentPipeline) {
      wgpuRenderPipelineRelease(d.presentPipeline);
      d.presentPipeline = nullptr;
    }
  }
  if (d.surfaceWidth == t.width && d.surfaceHeight == t.height) return;
  auto previousFormat = d.surfaceFormat;
  WGPUSurfaceCapabilities caps = WGPU_SURFACE_CAPABILITIES_INIT;
  wgpuSurfaceGetCapabilities(d.surface, d.adapter, &caps);
  if (!caps.formatCount) throw std::runtime_error("Surface has no formats");
  d.surfaceFormat = caps.formats[0];
  // Render targets contain display-encoded colours, as on the web backend.
  // Prefer a surface that preserves those bytes without another sRGB encoding.
  for (size_t i = 0; i < caps.formatCount; ++i) {
    if (caps.formats[i] == WGPUTextureFormat_BGRA8Unorm ||
        caps.formats[i] == WGPUTextureFormat_RGBA8Unorm) {
      d.surfaceFormat = caps.formats[i];
      break;
    }
  }
  wgpuSurfaceCapabilitiesFreeMembers(caps);
  if (previousFormat != d.surfaceFormat && d.presentPipeline) {
    wgpuRenderPipelineRelease(d.presentPipeline);
    d.presentPipeline = nullptr;
  }
  auto config = WGPU_SURFACE_CONFIGURATION_INIT;
  config.device = d.handle;
  config.format = d.surfaceFormat;
  config.width = t.width;
  config.height = t.height;
  config.usage = WGPUTextureUsage_RenderAttachment;
  config.presentMode = WGPUPresentMode_Fifo;
  config.alphaMode = WGPUCompositeAlphaMode_Auto;
  wgpuSurfaceConfigure(d.surface, &config);
  d.check();
  d.surfaceWidth = t.width;
  d.surfaceHeight = t.height;
}
void present(Device& d, Target& t) {
  if (!window) return;
  configureSurface(d, t);
  if (!d.presentPipeline) {
    const bool srgb = d.surfaceFormat == WGPUTextureFormat_BGRA8UnormSrgb ||
                      d.surfaceFormat == WGPUTextureFormat_RGBA8UnormSrgb;
    const std::string code =
        "@group(0) @binding(0) var image: texture_2d<f32>; @vertex fn "
        "vs(@builtin(vertex_index) i:u32)->@builtin(position) vec4<f32>{var "
        "p=array<vec2<f32>,3>(vec2<f32>(-1.0,-1.0),vec2<f32>(3.0,-1.0),vec2<"
        "f32>(-1.0,3.0));return vec4<f32>(p[i],0.0,1.0);} @fragment fn "
        "fs(@builtin(position) p:vec4<f32>)->@location(0) vec4<f32>{let c = "
        "textureLoad(image,vec2<i32>(p.xy),0);" +
        std::string(
            srgb ? "let linear = select(c.rgb/12.92, "
                   "pow((c.rgb+vec3<f32>(0.055))/1.055,vec3<f32>(2.4)), "
                   "c.rgb>vec3<f32>(0.04045));return vec4<f32>(linear,c.a);"
                 : "return c;") +
        "}";
    auto module = shader(d, code);
    auto color = WGPU_COLOR_TARGET_STATE_INIT;
    color.format = d.surfaceFormat;
    auto fragment = WGPU_FRAGMENT_STATE_INIT;
    fragment.module = module;
    fragment.entryPoint = text("fs");
    fragment.targetCount = 1;
    fragment.targets = &color;
    auto pd = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
    pd.vertex.module = module;
    pd.vertex.entryPoint = text("vs");
    pd.fragment = &fragment;
    d.presentPipeline = wgpuDeviceCreateRenderPipeline(d.handle, &pd);
    wgpuShaderModuleRelease(module);
  }
  WGPUSurfaceTexture st = WGPU_SURFACE_TEXTURE_INIT;
  wgpuSurfaceGetCurrentTexture(d.surface, &st);
  if (st.status == WGPUSurfaceGetCurrentTextureStatus_Outdated ||
      st.status == WGPUSurfaceGetCurrentTextureStatus_Lost) {
    if (st.texture) wgpuTextureRelease(st.texture);
    d.surfaceWidth = d.surfaceHeight = 0;
    configureSurface(d, t);
    st = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(d.surface, &st);
  }
  if (!st.texture) return;
  auto view = wgpuTextureCreateView(t.handle, nullptr);
  auto entry = WGPU_BIND_GROUP_ENTRY_INIT;
  entry.binding = 0;
  entry.textureView = view;
  auto groupDesc = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
  auto layout = wgpuRenderPipelineGetBindGroupLayout(d.presentPipeline, 0);
  groupDesc.layout = layout;
  groupDesc.entryCount = 1;
  groupDesc.entries = &entry;
  auto group = wgpuDeviceCreateBindGroup(d.handle, &groupDesc);
  auto encoder = wgpuDeviceCreateCommandEncoder(d.handle, nullptr);
  auto attachment = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
  attachment.view = wgpuTextureCreateView(st.texture, nullptr);
  attachment.loadOp = WGPULoadOp_Clear;
  attachment.storeOp = WGPUStoreOp_Store;
  auto pd = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
  pd.colorAttachmentCount = 1;
  pd.colorAttachments = &attachment;
  auto p = wgpuCommandEncoderBeginRenderPass(encoder, &pd);
  wgpuRenderPassEncoderSetPipeline(p, d.presentPipeline);
  wgpuRenderPassEncoderSetBindGroup(p, 0, group, 0, nullptr);
  wgpuRenderPassEncoderDraw(p, 3, 1, 0, 0);
  wgpuRenderPassEncoderEnd(p);
  wgpuRenderPassEncoderRelease(p);
  submit(d, encoder);
  wgpuSurfacePresent(d.surface);
  wgpuTextureViewRelease(view);
  wgpuTextureViewRelease(attachment.view);
  wgpuTextureRelease(st.texture);
  wgpuBindGroupRelease(group);
  wgpuBindGroupLayoutRelease(layout);
  if (st.status == WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal)
    d.surfaceWidth = d.surfaceHeight = 0;
  d.check();
}
void ensureStorage(Device& d, WGPUBuffer& handle, size_t& capacity,
                   size_t required, WGPUBufferUsage usage) {
  if (required <= capacity) return;
  if (required > 64 * 1024 * 1024) throw std::runtime_error("GPU frame storage limit exceeded");
  size_t next = capacity ? capacity : 65536;
  while (next < required) next *= 2;
  auto replacement = buffer(d, nullptr, next, usage);
  if (handle) wgpuBufferRelease(handle);
  handle = replacement; capacity = next;
}
WGPUBindGroup frameGroup(Device& d, FrameStorage& storage, uint64_t id) {
  auto existing = storage.groups.find(id);
  if (existing != storage.groups.end()) return existing->second;
  Texture& tex = d.textures.count(id) ? get(d.textures, id)
                                    : static_cast<Texture&>(get(d.targets, id));
  WGPUBindGroupEntry entries[3] = {WGPU_BIND_GROUP_ENTRY_INIT,
      WGPU_BIND_GROUP_ENTRY_INIT, WGPU_BIND_GROUP_ENTRY_INIT};
  entries[0].binding = 0; entries[0].buffer = storage.uniforms; entries[0].size = 1120;
  entries[1].binding = 1; entries[1].textureView = tex.sampledView();
  entries[2].binding = 2; entries[2].sampler = d.sampler;
  auto desc = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
  desc.layout = d.layout; desc.entryCount = 3; desc.entries = entries;
  auto group = wgpuDeviceCreateBindGroup(d.handle, &desc);
  // Limit caching even when a script continually creates new textures.
  if (storage.groups.size() >= 256) {
    auto oldest = storage.groups.begin(); wgpuBindGroupRelease(oldest->second); storage.groups.erase(oldest);
  }
  storage.groups[id] = group;
  return group;
}
void drawBatch(Device& d, ByteSpan bytes) {
  auto commands = parseBatch(bytes);
  if (commands.empty()) return;
  size_t vertexSize = 0, draws = 0;
  for (auto& c : commands) {
    auto& target = get(d.targets, c.target);
    if (c.operation == 1) continue;
    auto& s = get(d.shaders, c.shader);
    if (!d.textures.count(c.texture)) get(d.targets, c.texture);
    if (c.clip[0] > target.width || c.clip[1] > target.height ||
        c.clip[2] > target.width - c.clip[0] || c.clip[3] > target.height - c.clip[1])
      throw std::runtime_error("Invalid GPU scissor rectangle");
    pipeline(d, s, c.depth, c.blend, c.mode);
    if (c.operation == 3 && !get(d.buffers, c.mesh).mesh)
      throw std::runtime_error("Expected immutable GPU mesh");
    vertexSize += c.vertexBytes; draws++;
  }
  FrameStorage* storage = nullptr;
  if (draws) {
    auto& slot = d.frames[d.frameIndex++ % d.frames.size()];
    if (!slot) slot = std::make_unique<FrameStorage>();
    storage = slot.get();
    size_t uniformSize = draws * d.uniformStride, userSize = draws * d.userStride;
    if (uniformSize > 64 * 1024 * 1024 || userSize > 64 * 1024 * 1024)
      throw std::runtime_error("GPU uniform storage limit exceeded");
    if (uniformSize > storage->uniformCapacity || userSize > storage->userCapacity) storage->clearGroups();
    ensureStorage(d, storage->vertex, storage->vertexCapacity, vertexSize, WGPUBufferUsage_Vertex);
    ensureStorage(d, storage->uniforms, storage->uniformCapacity, uniformSize, WGPUBufferUsage_Uniform);
    ensureStorage(d, storage->user, storage->userCapacity, userSize, WGPUBufferUsage_Uniform);
    storage->vertexData.resize(vertexSize); storage->uniformData.resize(uniformSize); storage->userData.resize(userSize);
    size_t vertexOffset = 0, index = 0;
    for (auto& c : commands) if (c.operation != 1) {
      if (c.vertexBytes) std::memcpy(storage->vertexData.data() + vertexOffset, c.vertices, c.vertexBytes);
      std::memcpy(storage->uniformData.data() + index * d.uniformStride, c.uniforms, 1120);
      std::memcpy(storage->userData.data() + index * d.userStride, c.user, 1024);
      vertexOffset += c.vertexBytes; index++;
    }
    // Queue writes are ordered after earlier submissions. Only wgpu owns the
    // uploaded copies; neither JNI nor the queued work retains a V8 pointer.
    if (vertexSize) wgpuQueueWriteBuffer(d.queue, storage->vertex, 0, storage->vertexData.data(), vertexSize);
    wgpuQueueWriteBuffer(d.queue, storage->uniforms, 0, storage->uniformData.data(), uniformSize);
    wgpuQueueWriteBuffer(d.queue, storage->user, 0, storage->userData.data(), userSize);
    if (!storage->userGroup) {
      auto entry = WGPU_BIND_GROUP_ENTRY_INIT;
      entry.binding = 0; entry.buffer = storage->user; entry.size = 1024;
      auto desc = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
      desc.layout = d.userLayout; desc.entryCount = 1; desc.entries = &entry;
      storage->userGroup = wgpuDeviceCreateBindGroup(d.handle, &desc);
    }
  }
  auto encoder = wgpuDeviceCreateCommandEncoder(d.handle, nullptr);
  WGPURenderPassEncoder active = nullptr;
  uint64_t targetId = 0;
  auto endPass = [&]() {
    if (active) { wgpuRenderPassEncoderEnd(active); wgpuRenderPassEncoderRelease(active); active = nullptr; }
  };
  try {
    size_t vertexOffset = 0, index = 0;
    for (auto& c : commands) {
      auto& target = get(d.targets, c.target);
      if (c.operation == 1) {
        endPass(); Json color = {c.color[0], c.color[1], c.color[2], c.color[3]};
        active = pass(d, target, encoder, &color); targetId = c.target;
        continue;
      }
      if (!active || targetId != c.target) { endPass(); active = pass(d, target, encoder); targetId = c.target; }
      auto& s = get(d.shaders, c.shader);
      uint32_t uniformOffset = index * d.uniformStride, userOffset = index * d.userStride;
      wgpuRenderPassEncoderSetPipeline(active, pipeline(d, s, c.depth, c.blend, c.mode));
      wgpuRenderPassEncoderSetBindGroup(active, 0, frameGroup(d, *storage, c.texture), 1, &uniformOffset);
      wgpuRenderPassEncoderSetBindGroup(active, 1, storage->userGroup, 1, &userOffset);
      auto* mesh = c.operation == 3 ? &get(d.buffers, c.mesh) : nullptr;
      auto vertexBuffer = mesh ? mesh->handle : storage->vertex;
      size_t vertexBytes = mesh ? mesh->size : c.vertexBytes;
      wgpuRenderPassEncoderSetVertexBuffer(active, 0, vertexBuffer, c.operation == 3 ? 0 : vertexOffset, vertexBytes);
      wgpuRenderPassEncoderSetScissorRect(active, c.clip[0], c.clip[1], c.clip[2], c.clip[3]);
      if (c.clip[2] && c.clip[3]) wgpuRenderPassEncoderDraw(active, vertexBytes / 48, 1, 0, 0);
      vertexOffset += c.vertexBytes; index++;
    }
    endPass(); auto complete = encoder; encoder = nullptr; submit(d, complete);
  } catch (...) { endPass(); if (encoder) wgpuCommandEncoderRelease(encoder); throw; }
}
Json command(const std::string& op, const Json& a,
             ByteSpan bytes) {
  if (op == "device") {
    uint64_t id = nextId++;
    devices[id] = createDevice();
    return id;
  }
  uint64_t did = a.at("device");
  auto& d = get(devices, did);
  if (op == "disposeDevice") {
    devices.erase(did);
    return nullptr;
  }
  d.check();
  if (op == "batch") { drawBatch(d, bytes); return nullptr; }
  if (op == "poll") {
    wgpuDevicePoll(d.handle, true, nullptr);
    d.check();
    return nullptr;
  }
  if (op == "buffer" || op == "mesh") {
    if (bytes.empty() || bytes.size() > 64 * 1024 * 1024)
      throw std::runtime_error("Invalid buffer size");
    auto b = std::make_unique<Buffer>();
    b->size = bytes.size();
    b->mesh = op == "mesh";
    if (b->mesh && bytes.size() % 144) throw std::runtime_error("Invalid GPU mesh vertices");
    b->handle = buffer(d, bytes.data(), bytes.size(),
                       (b->mesh ? WGPUBufferUsage_Vertex : WGPUBufferUsage_Storage) | WGPUBufferUsage_CopySrc);
    uint64_t id = nextId++;
    d.buffers[id] = std::move(b);
    return id;
  }
  if (op == "writeBuffer") {
    auto& b = get(d.buffers, a.at("id"));
    if (bytes.size() != b.size)
      throw std::runtime_error("Buffer size mismatch");
    wgpuQueueWriteBuffer(d.queue, b.handle, 0, bytes.data(), bytes.size());
    return nullptr;
  }
  if (op == "texture" || op == "target") {
    uint32_t w = a.at("width"), h = a.at("height");
    if (!w || !h || w > 8192 || h > 8192 || uint64_t(w) * h > 16 * 1024 * 1024)
      throw std::runtime_error("Invalid texture size");
    uint64_t id = nextId++;
    if (op == "target") {
      auto t = std::make_unique<Target>();
      t->width = w;
      t->height = h;
      t->handle = texture(
          d, w, h, WGPUTextureFormat_RGBA8Unorm,
          WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_TextureBinding |
              WGPUTextureUsage_CopySrc | WGPUTextureUsage_CopyDst);
      t->depth = texture(d, w, h, WGPUTextureFormat_Depth24Plus,
                         WGPUTextureUsage_RenderAttachment);
      d.targets[id] = std::move(t);
    } else {
      auto t = std::make_unique<Texture>();
      t->width = w;
      t->height = h;
      t->handle =
          texture(d, w, h, WGPUTextureFormat_RGBA8Unorm,
                  WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst);
      if (!bytes.empty()) {
        if (bytes.size() != size_t(w) * h * 4)
          throw std::runtime_error("Texture size mismatch");
        auto dest = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
        dest.texture = t->handle;
        auto layout = WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
        layout.bytesPerRow = w * 4;
        layout.rowsPerImage = h;
        WGPUExtent3D extent = {w, h, 1};
        wgpuQueueWriteTexture(d.queue, &dest, bytes.data(), bytes.size(),
                              &layout, &extent);
      }
      d.textures[id] = std::move(t);
    }
    d.check();
    return id;
  }
  if (op == "writeTexture") {
    uint64_t id = a.at("id");
    Texture& t = d.textures.count(id)
                     ? get(d.textures, id)
                     : static_cast<Texture&>(get(d.targets, id));
    if (bytes.size() != size_t(t.width) * t.height * 4)
      throw std::runtime_error("Texture size mismatch");
    auto dest = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    dest.texture = t.handle;
    auto layout = WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
    layout.bytesPerRow = t.width * 4;
    layout.rowsPerImage = t.height;
    WGPUExtent3D extent = {t.width, t.height, 1};
    wgpuQueueWriteTexture(d.queue, &dest, bytes.data(), bytes.size(), &layout,
                          &extent);
    // A full pixel upload initializes a target just like a render clear.
    // A later draw must load these pixels instead of clearing them away.
    if (d.targets.count(id)) get(d.targets, id).initialized = true;
    return nullptr;
  }
  if (op == "shader") {
    auto s = std::make_unique<Shader>();
    s->vertex = shader(d, a.at("vertex"));
    s->fragment = shader(d, a.at("fragment"));
    uint64_t id = nextId++;
    d.shaders[id] = std::move(s);
    return id;
  }
  if (op == "release") {
    uint64_t id = a.at("id");
    for (auto& frame : d.frames) if (frame) frame->forget(id);
    d.buffers.erase(id);
    d.textures.erase(id);
    d.targets.erase(id);
    d.shaders.erase(id);
    return nullptr;
  }
  if (op == "clear") {
    auto& t = get(d.targets, a.at("id"));
    auto encoder = wgpuDeviceCreateCommandEncoder(d.handle, nullptr);
    auto p = pass(d, t, encoder, &a.at("color"));
    wgpuRenderPassEncoderEnd(p);
    wgpuRenderPassEncoderRelease(p);
    submit(d, encoder);
    return nullptr;
  }
  if (op == "present") {
    present(d, get(d.targets, a.at("id")));
    return nullptr;
  }
  if (op == "dispatch") {
    auto& b = get(d.buffers, a.at("id"));
    auto module = shader(d, a.at("source"));
    auto pd = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
    pd.compute.module = module;
    pd.compute.entryPoint = text("cs_main");
    auto pipeline = wgpuDeviceCreateComputePipeline(d.handle, &pd);
    auto layout = wgpuComputePipelineGetBindGroupLayout(pipeline, 0);
    auto entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.binding = 0;
    entry.buffer = b.handle;
    entry.size = b.size;
    auto gd = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    gd.layout = layout;
    gd.entryCount = 1;
    gd.entries = &entry;
    auto group = wgpuDeviceCreateBindGroup(d.handle, &gd);
    auto encoder = wgpuDeviceCreateCommandEncoder(d.handle, nullptr);
    auto p = wgpuCommandEncoderBeginComputePass(encoder, nullptr);
    wgpuComputePassEncoderSetPipeline(p, pipeline);
    wgpuComputePassEncoderSetBindGroup(p, 0, group, 0, nullptr);
    wgpuComputePassEncoderDispatchWorkgroups(p, a.at("groups"), 1, 1);
    wgpuComputePassEncoderEnd(p);
    wgpuComputePassEncoderRelease(p);
    submit(d, encoder);
    wgpuBindGroupRelease(group);
    wgpuBindGroupLayoutRelease(layout);
    wgpuComputePipelineRelease(pipeline);
    wgpuShaderModuleRelease(module);
    return nullptr;
  }
  throw std::runtime_error("Unknown WebGPU operation");
}
jstring javaText(JNIEnv* env, const std::string& value) {
  std::vector<jchar> utf16;
  for (size_t i = 0; i < value.size();) {
    uint32_t c = static_cast<uint8_t>(value[i++]);
    int remaining = c < 128            ? 0
                    : (c & 224) == 192 ? 1
                    : (c & 240) == 224 ? 2
                    : (c & 248) == 240 ? 3
                                       : -1;
    if (remaining < 0) {
      utf16.push_back(0xfffd);
      continue;
    }
    if (remaining) c &= (1u << (6 - remaining)) - 1;
    bool valid = true;
    for (int n = 0; n < remaining; n++) {
      if (i >= value.size() || (static_cast<uint8_t>(value[i]) & 192) != 128) {
        valid = false;
        break;
      }
      c = (c << 6) | (static_cast<uint8_t>(value[i++]) & 63);
    }
    if (!valid || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) c = 0xfffd;
    if (c > 0xffff) {
      c -= 0x10000;
      utf16.push_back(0xd800 + (c >> 10));
      utf16.push_back(0xdc00 + (c & 1023));
    } else
      utf16.push_back(c);
  }
  return env->NewString(utf16.data(), utf16.size());
}
std::string javaString(JNIEnv* env, jstring value) {
  const jchar* chars = env->GetStringChars(value, nullptr);
  std::string result;
  jsize length = env->GetStringLength(value);
  for (jsize i = 0; i < length; i++) {
    uint32_t c = chars[i];
    if (c >= 0xd800 && c <= 0xdbff && i + 1 < length &&
        chars[i + 1] >= 0xdc00 && chars[i + 1] <= 0xdfff) {
      c = 0x10000 + ((c - 0xd800) << 10) + (chars[++i] - 0xdc00);
    } else if (c >= 0xd800 && c <= 0xdfff)
      c = 0xfffd;
    if (c < 128)
      result.push_back(c);
    else if (c < 2048) {
      result.push_back(192 | (c >> 6));
      result.push_back(128 | (c & 63));
    } else if (c < 65536) {
      result.push_back(224 | (c >> 12));
      result.push_back(128 | ((c >> 6) & 63));
      result.push_back(128 | (c & 63));
    } else {
      result.push_back(240 | (c >> 18));
      result.push_back(128 | ((c >> 12) & 63));
      result.push_back(128 | ((c >> 6) & 63));
      result.push_back(128 | (c & 63));
    }
  }
  env->ReleaseStringChars(value, chars);
  return result;
}
void error(JNIEnv* env, const std::exception& e) {
  auto type = env->FindClass("java/lang/IllegalStateException");
  auto message = javaText(env, e.what());
  auto exception = env->NewObject(
      type, env->GetMethodID(type, "<init>", "(Ljava/lang/String;)V"), message);
  env->Throw(static_cast<jthrowable>(exception));
  env->DeleteLocalRef(message);
  env->DeleteLocalRef(exception);
  env->DeleteLocalRef(type);
}
}  // namespace
extern "C" JNIEXPORT jstring JNICALL
Java_org_aquariuslang_android_NativeGpu_command(JNIEnv* env, jclass, jstring op,
                                                jstring json, jobject data,
                                                jint offset, jint length) {
  std::lock_guard<std::mutex> lock(gate);
  try {
    jlong capacity = data ? env->GetDirectBufferCapacity(data) : -1;
    const auto* pointer = data ? static_cast<const uint8_t*>(env->GetDirectBufferAddress(data)) : nullptr;
    if (offset < 0 || length < 0 || length > 64 * 1024 * 1024 ||
        capacity < 0 || int64_t(offset) + length > capacity || (length && !pointer))
      throw std::runtime_error("Invalid GPU typed-array view");
    ByteSpan bytes{length ? pointer + offset : nullptr, static_cast<size_t>(length)};
    auto result =
        command(javaString(env, op), Json::parse(javaString(env, json)), bytes)
            .dump();
    return javaText(env, result);
  } catch (const std::exception& e) {
    error(env, e);
    return nullptr;
  }
}
extern "C" JNIEXPORT void JNICALL
Java_org_aquariuslang_android_NativeGpu_surface(JNIEnv* env, jclass,
                                                jobject surface) {
  std::lock_guard<std::mutex> lock(gate);
  if (window) ANativeWindow_release(window);
  window = surface ? ANativeWindow_fromSurface(env, surface) : nullptr;
  surfaceGeneration++;
}
extern "C" JNIEXPORT jbyteArray JNICALL
Java_org_aquariuslang_android_NativeGpu_read(JNIEnv* env, jclass, jlong did,
                                             jlong id, jboolean isTexture) {
  std::lock_guard<std::mutex> lock(gate);
  try {
    auto& d = get(devices, did);
    size_t size;
    uint32_t pitch = 0, width = 0, height = 0;
    auto encoder = wgpuDeviceCreateCommandEncoder(d.handle, nullptr);
    WGPUBuffer staging;
    if (isTexture) {
      auto& t = get(d.targets, id);
      width = t.width;
      height = t.height;
      pitch = (width * 4 + 255) / 256 * 256;
      size = size_t(pitch) * height;
      staging = buffer(d, nullptr, size, WGPUBufferUsage_MapRead);
      auto from = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
      from.texture = t.handle;
      auto to = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
      to.buffer = staging;
      to.layout.bytesPerRow = pitch;
      to.layout.rowsPerImage = height;
      WGPUExtent3D extent = {width, height, 1};
      wgpuCommandEncoderCopyTextureToBuffer(encoder, &from, &to, &extent);
    } else {
      auto& b = get(d.buffers, id);
      size = b.size;
      staging = buffer(d, nullptr, size, WGPUBufferUsage_MapRead);
      wgpuCommandEncoderCopyBufferToBuffer(encoder, b.handle, 0, staging, 0,
                                           size);
    }
    submit(d, encoder);
    bool done = false;
    auto callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowProcessEvents;
    callback.userdata1 = &done;
    callback.callback = [](WGPUMapAsyncStatus status, WGPUStringView,
                           void* user, void*) {
      *static_cast<bool*>(user) = status == WGPUMapAsyncStatus_Success;
    };
    wgpuBufferMapAsync(staging, WGPUMapMode_Read, 0, size, callback);
    wgpuDevicePoll(d.handle, true, nullptr);
    wgpuInstanceProcessEvents(d.instance);
    if (!done) {
      wgpuBufferRelease(staging);
      throw std::runtime_error("GPU readback failed");
    }
    const auto* data = static_cast<const uint8_t*>(
        wgpuBufferGetConstMappedRange(staging, 0, size));
    size_t length = isTexture ? size_t(width) * height * 4 : size;
    auto result = env->NewByteArray(length);
    if (isTexture)
      for (uint32_t y = 0; y < height; y++)
        env->SetByteArrayRegion(
            result, y * width * 4, width * 4,
            reinterpret_cast<const jbyte*>(data + y * pitch));
    else
      env->SetByteArrayRegion(result, 0, length,
                              reinterpret_cast<const jbyte*>(data));
    wgpuBufferUnmap(staging);
    wgpuBufferRelease(staging);
    return result;
  } catch (const std::exception& e) {
    error(env, e);
    return nullptr;
  }
}
