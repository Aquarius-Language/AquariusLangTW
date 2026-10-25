/* Textures: sampled textures from bytes (any mip/layer layout of the portable descriptor), RGBA8 image textures and
   render-target attachments. */
#include "gpu.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct gpu_texture {
    gpu_resource link;
    WGPUTexture handle;
    WGPUTextureView view;
    int width, height;
    bool cube, sampled_rgba;
    gpu_target *source;
};

static int bytes_per_pixel(gpu_format format) { return format == GPU_RGBA16FLOAT ? 8 : format == GPU_R8UNORM ? 1 : 4; }
static WGPUTextureFormat wgpu_format(gpu_format format) {
    switch (format) {
    case GPU_R8UNORM: return WGPUTextureFormat_R8Unorm;
    case GPU_RGBA16FLOAT: return WGPUTextureFormat_RGBA16Float;
    case GPU_RGBA8UNORM_SRGB: return WGPUTextureFormat_RGBA8UnormSrgb;
    default: return WGPUTextureFormat_RGBA8Unorm;
    }
}

bool gpu_format_parse(const char *name, gpu_format *format) {
    if (strcmp(name, "r8unorm") == 0) *format = GPU_R8UNORM;
    else if (strcmp(name, "rgba8unorm") == 0) *format = GPU_RGBA8UNORM;
    else if (strcmp(name, "rgba8unorm-srgb") == 0) *format = GPU_RGBA8UNORM_SRGB;
    else if (strcmp(name, "rgba16float") == 0) *format = GPU_RGBA16FLOAT;
    else return false;
    return true;
}

size_t gpu_texture_bytes(const gpu_texture_desc *d, char *error, size_t error_size) {
    int largest = d->width > d->height ? d->width : d->height;
    int maximum_mips = 1;
    for (int size = largest; size > 1; size >>= 1) maximum_mips++;
    if (d->width < 1 || d->width > 8192 || d->height < 1 || d->height > 8192 || (d->cube && d->width != d->height) ||
        d->mip_levels < 1 || d->mip_levels > maximum_mips) {
        snprintf(error, error_size, "Invalid texture dimensions, format, dimension or mip count.");
        return 0;
    }
    uint64_t total = 0;
    int layers = d->cube ? 6 : 1;
    for (int mip = 0; mip < d->mip_levels; mip++) {
        uint64_t w = (uint64_t)(d->width >> mip > 1 ? d->width >> mip : 1), h = (uint64_t)(d->height >> mip > 1 ? d->height >> mip : 1);
        total += w * h * (uint64_t)layers * (uint64_t)bytes_per_pixel(d->format);
    }
    if (total > 128u * 1024 * 1024) { snprintf(error, error_size, "Texture data exceeds 128 MiB."); return 0; }
    return (size_t)total;
}

static gpu_texture *create(gpu_device *device, const WGPUTextureDescriptor *descriptor, const WGPUTextureViewDescriptor *view) {
    gpu_texture *t = calloc(1, sizeof *t);
    t->link.next = t->link.previous = &t->link;
    t->handle = wgpuDeviceCreateTexture(gpu_device_handle(device), descriptor);
    if (!t->handle) { free(t); return NULL; }
    t->view = wgpuTextureCreateView(t->handle, view);
    t->width = (int)descriptor->size.width;
    t->height = (int)descriptor->size.height;
    gpu_device_link(device, &t->link, GPU_TEXTURE);
    return t;
}

gpu_texture *gpu_texture_create(gpu_device *device, const gpu_texture_desc *d, const uint8_t *bytes, char *error, size_t error_size) {
    if (!gpu_texture_bytes(d, error, error_size)) return NULL;
    int layers = d->cube ? 6 : 1, pixel = bytes_per_pixel(d->format);
    WGPUTextureDescriptor descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size.width = (uint32_t)d->width; descriptor.size.height = (uint32_t)d->height;
    descriptor.size.depthOrArrayLayers = (uint32_t)layers;
    descriptor.format = wgpu_format(d->format);
    descriptor.mipLevelCount = (uint32_t)d->mip_levels;
    descriptor.sampleCount = 1;
    descriptor.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst;
    WGPUTextureViewDescriptor view = WGPU_TEXTURE_VIEW_DESCRIPTOR_INIT;
    view.format = descriptor.format;
    view.dimension = d->cube ? WGPUTextureViewDimension_Cube : WGPUTextureViewDimension_2D;
    view.mipLevelCount = descriptor.mipLevelCount;
    view.arrayLayerCount = (uint32_t)layers;
    view.aspect = WGPUTextureAspect_All;
    gpu_texture *t = create(device, &descriptor, &view);
    if (!t) { snprintf(error, error_size, "wgpu texture creation failed."); return NULL; }
    t->cube = d->cube;
    size_t offset = 0;
    for (int mip = 0; mip < d->mip_levels; mip++) {
        uint32_t w = (uint32_t)(d->width >> mip > 1 ? d->width >> mip : 1), h = (uint32_t)(d->height >> mip > 1 ? d->height >> mip : 1);
        size_t size = (size_t)w * h * (size_t)layers * (size_t)pixel;
        WGPUTexelCopyTextureInfo copy = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
        copy.texture = t->handle; copy.mipLevel = (uint32_t)mip; copy.aspect = WGPUTextureAspect_All;
        WGPUTexelCopyBufferLayout layout = WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
        layout.bytesPerRow = w * (uint32_t)pixel; layout.rowsPerImage = h;
        WGPUExtent3D extent = {w, h, (uint32_t)layers};
        wgpuQueueWriteTexture(gpu_device_queue(device), &copy, bytes + offset, size, &layout, &extent);
        offset += size;
    }
    const char *failure = gpu_device_take_error(device);
    if (failure) { snprintf(error, error_size, "%s", failure); gpu_texture_free(t); return NULL; }
    return t;
}

gpu_texture *gpu_texture_rgba(gpu_device *device, int width, int height) {
    WGPUTextureDescriptor descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size.width = (uint32_t)width; descriptor.size.height = (uint32_t)height; descriptor.size.depthOrArrayLayers = 1;
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1; descriptor.sampleCount = 1;
    descriptor.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst | WGPUTextureUsage_CopySrc;
    gpu_texture *t = create(device, &descriptor, NULL);
    if (t) t->sampled_rgba = true;
    return t;
}

gpu_texture *gpu_texture_attachment(gpu_device *device, int width, int height, WGPUTextureFormat format, uint32_t samples) {
    WGPUTextureDescriptor descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size.width = (uint32_t)width; descriptor.size.height = (uint32_t)height; descriptor.size.depthOrArrayLayers = 1;
    descriptor.format = format;
    descriptor.mipLevelCount = 1; descriptor.sampleCount = samples;
    descriptor.usage = WGPUTextureUsage_RenderAttachment;
    bool color = format == WGPUTextureFormat_RGBA8Unorm || format == WGPUTextureFormat_RGBA16Float;
    if (samples == 1 && color) descriptor.usage |= WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopySrc | WGPUTextureUsage_CopyDst;
    return create(device, &descriptor, NULL);
}

void gpu_texture_upload(gpu_texture *t, const uint8_t *rgba, bool unused) {
    if (!t->handle) return;
    gpu_device *device = t->link.device;
    /* Queue writes run before the next submission: submit recorded draws first so they keep the old pixels. */
    if (!unused) gpu_device_flush(device);
    WGPUTexelCopyTextureInfo copy = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    copy.texture = t->handle; copy.aspect = WGPUTextureAspect_All;
    WGPUTexelCopyBufferLayout layout = WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
    layout.bytesPerRow = (uint32_t)t->width * 4; layout.rowsPerImage = (uint32_t)t->height;
    WGPUExtent3D extent = {(uint32_t)t->width, (uint32_t)t->height, 1};
    wgpuQueueWriteTexture(gpu_device_queue(device), &copy, rgba, (size_t)t->width * (size_t)t->height * 4, &layout, &extent);
}

void gpu_texture_release_handles(gpu_texture *t) {
    if (t->view) wgpuTextureViewRelease(t->view);
    if (t->handle) wgpuTextureRelease(t->handle);
    t->view = NULL; t->handle = NULL;
}

void gpu_texture_free(gpu_texture *t) {
    if (!t) return;
    if (t->link.device && !gpu_device_disposed(t->link.device) && t->handle) gpu_device_flush(t->link.device);
    gpu_texture_release_handles(t);
    gpu_device_unlink(&t->link);
    free(t);
}

bool gpu_texture_released(const gpu_texture *t) { return !t || !t->handle; }
int gpu_texture_width(const gpu_texture *t) { return t->width; }
int gpu_texture_height(const gpu_texture *t) { return t->height; }
bool gpu_texture_cube(const gpu_texture *t) { return t->cube; }
WGPUTextureView gpu_texture_view(const gpu_texture *t) { return t->view; }
WGPUTexture gpu_texture_handle(const gpu_texture *t) { return t->handle; }
gpu_device *gpu_texture_device(const gpu_texture *t) { return t->link.device; }
gpu_target *gpu_texture_source(const gpu_texture *t) { return t->source; }
void gpu_texture_set_source(gpu_texture *t, gpu_target *target) { t->source = target; }
