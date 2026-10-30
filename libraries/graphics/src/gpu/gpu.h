/* The renderer core on webgpu.h, shared by Processing and the WGPU module on every target: devices (with an optional
 * window surface), textures, render targets with per-pass upload arenas, WGSL shaders with the shared uniform ABI,
 * storage/vertex buffers, compute dispatch and readback.
 *
 * Operations that wait for the GPU (device requests, shader validation, buffer mapping) complete through callbacks:
 * native targets finish them before returning (processing wgpu-native's events), the web delivers them when the
 * browser's promise settles. Everything else is immediate.
 *
 * Resources belong to a device. Disposing the device releases every resource's GPU objects; the structs stay valid
 * (marked released) until their owners free them, and keep the device struct alive with a reference count. */
#ifndef GRAPHICS_GPU_H
#define GRAPHICS_GPU_H

#include <aqua/aqua.h>
#include <aqua/window.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <webgpu/webgpu.h>

#define GPU_UNIFORM_BYTES 1120   /* the Processing uniform ABI (shaders.c) */
#define GPU_USER_BYTES 4096      /* UserUniforms at group 1 */
#define GPU_FLOATS_PER_VERTEX 12 /* position xyz, normal xyz, uv, rgba */
#define GPU_MAX_FLOATS (16 * 1024 * 1024)

typedef struct gpu_device gpu_device;
typedef struct gpu_texture gpu_texture;
typedef struct gpu_target gpu_target;
typedef struct gpu_shader gpu_shader;
typedef struct gpu_buffer gpu_buffer;

/* Every resource starts with this link into its device's list. */
typedef enum gpu_kind { GPU_TEXTURE, GPU_TARGET, GPU_SHADER, GPU_BUFFER } gpu_kind;
typedef struct gpu_resource { struct gpu_resource *previous, *next; gpu_kind kind; gpu_device *device; } gpu_resource;
/* Completion of gpu_shader_prepare / gpu_target_prepare: NULL or the failure. */
typedef void (*gpu_prepare_done)(void *data, const char *error);

/* ---- Devices (device.c) ------------------------------------------------------------------------------------- */
typedef void (*gpu_device_done)(gpu_device *device, const char *error, void *data);
/* Requests an adapter and a device, presenting to `window` when given. `done` receives the device or an error. */
void gpu_device_create(aqua_runtime *rt, aqua_window *window, gpu_device_done done, void *data);
void gpu_device_retain(gpu_device *device);
void gpu_device_release(gpu_device *device);   /* frees the struct after the last reference */
void gpu_device_dispose(gpu_device *device);   /* releases every GPU object; idempotent */
bool gpu_device_disposed(const gpu_device *device);
const char *gpu_device_backend(const gpu_device *device); /* "Vulkan", "D3D12", "Metal", "WebGPU", ... */
aqua_window *gpu_device_window(const gpu_device *device);
/* The device's pending uncaptured error ("Validation: ..."), cleared by the call; NULL when there is none. */
const char *gpu_device_take_error(gpu_device *device);
/* Submits every target's open pass, keeping recorded work in order before uploads, copies and readbacks. */
void gpu_device_flush(gpu_device *device);
/* Waits for submitted work (native) or starts waiting for it (web); `done` follows. */
void gpu_device_poll(gpu_device *device, void (*done)(void *data, const char *error), void *data);
/* Presents `target` scaled onto the device's window surface (sized to the window's framebuffer). */
bool gpu_device_present(gpu_device *device, gpu_target *target, char *error, size_t error_size);
/* Releases GPU objects whose last draws were submitted (bind groups and buffers replaced during a frame). */
void gpu_device_release_retired(gpu_device *device);

/* Internal: device.c implementation details shared by the other files. */
WGPUDevice gpu_device_handle(gpu_device *device);
WGPUQueue gpu_device_queue(gpu_device *device);
void gpu_device_link(gpu_device *device, gpu_resource *resource, gpu_kind kind);
void gpu_device_unlink(gpu_resource *resource);
WGPUBindGroupLayout gpu_device_drawing_layout(gpu_device *device);
WGPUBindGroupLayout gpu_device_user_layout(gpu_device *device);
WGPUSampler gpu_device_sampler(gpu_device *device);
enum { GPU_SAMPLE_REPEAT = 1, GPU_SAMPLE_NEAREST = 2 };
WGPUSampler gpu_device_sampler_for(gpu_device *device, int sampling); /* GPU_SAMPLE_* flags */
gpu_texture *gpu_device_white(gpu_device *device);
void **gpu_device_kernel_slot(gpu_device *device);
WGPUInstance gpu_device_instance(gpu_device *device);
WGPUBindGroup gpu_device_identity_group(gpu_device *device);
void gpu_device_retire_group(gpu_device *device, WGPUBindGroup group);
void gpu_device_retire_buffer(gpu_device *device, WGPUBuffer buffer);
void gpu_device_blit(gpu_device *device, WGPUTextureView source, WGPUTextureView destination, WGPUTextureFormat format, uint32_t samples);
/* Waits until *flag is set by a callback (native) or returns at once (web, where the callback comes later). */
void gpu_device_wait(gpu_device *device, const volatile bool *flag);
void gpu_instance_wait(WGPUInstance instance, WGPUDevice device, const volatile bool *flag);
WGPUShaderModule gpu_device_shader_module(gpu_device *device, const char *source, size_t length);
WGPUBuffer gpu_device_buffer(gpu_device *device, uint64_t size, WGPUBufferUsage usage, const void *data);
void gpu_device_submit(gpu_device *device, WGPUCommandEncoder encoder);
/* Records an uncaptured-style error for the next check (used when an operation fails on our side). */
void gpu_device_error(gpu_device *device, const char *message);

/* The "callback mode" every asynchronous webgpu.h call uses: processed events natively, spontaneous on the web. */
#if defined(AQUA_OS_WEB)
#define GPU_CALLBACK_MODE WGPUCallbackMode_AllowSpontaneous
#else
#define GPU_CALLBACK_MODE WGPUCallbackMode_AllowProcessEvents
#endif
static inline WGPUStringView gpu_text(const char *s) { WGPUStringView v = {s, WGPU_STRLEN}; return v; }

/* ---- Textures (texture.c) ------------------------------------------------------------------------------------ */
typedef enum gpu_format { GPU_R8UNORM, GPU_RGBA8UNORM, GPU_RGBA8UNORM_SRGB, GPU_RGBA16FLOAT } gpu_format;
typedef struct gpu_texture_desc { int width, height, mip_levels; gpu_format format; bool cube; } gpu_texture_desc;
/* Validates a descriptor ("Invalid texture dimensions, format, dimension or mip count.", 128 MiB) and returns its byte
   length, or 0 with `error` written. */
size_t gpu_texture_bytes(const gpu_texture_desc *desc, char *error, size_t error_size);
bool gpu_format_parse(const char *name, gpu_format *format);
/* A sampled texture with all mips and layers from `bytes` (mip-major, then layer-major). */
gpu_texture *gpu_texture_create(gpu_device *device, const gpu_texture_desc *desc, const uint8_t *bytes, char *error, size_t error_size);
/* A 2D RGBA8 texture filled later with gpu_texture_upload (images). */
gpu_texture *gpu_texture_rgba(gpu_device *device, int width, int height);
/* An attachment (color, depth or multisampled) of a render target; color attachments may have `mip_levels` > 1 (drawn
   into mip 0, sampled with all levels). */
gpu_texture *gpu_texture_attachment(gpu_device *device, int width, int height, WGPUTextureFormat format, uint32_t samples, int mip_levels);
/* The color formats render targets support, by name ("rgba8unorm", "rgba16float", "r16float", "r32float", "rgba32float"). */
bool gpu_target_format_parse(const char *name, WGPUTextureFormat *format);
/* Fills mips 1.. of a color attachment from mip 0 (2x2 box filter). */
void gpu_texture_generate_mipmaps(gpu_texture *texture);
int gpu_texture_mip_levels(const gpu_texture *texture);
/* Replaces the pixels of an RGBA8 texture. Draws recorded earlier keep sampling the old pixels unless `unused`. */
void gpu_texture_upload(gpu_texture *texture, const uint8_t *rgba, bool unused);
void gpu_texture_free(gpu_texture *texture);  /* releases and frees (owner side) */
bool gpu_texture_released(const gpu_texture *texture);
int gpu_texture_width(const gpu_texture *texture);
int gpu_texture_height(const gpu_texture *texture);
bool gpu_texture_cube(const gpu_texture *texture);
WGPUTextureView gpu_texture_view(const gpu_texture *texture);            /* every mip, for sampling */
WGPUTextureView gpu_texture_attachment_view(const gpu_texture *texture); /* mip 0, for drawing */
WGPUTexture gpu_texture_handle(const gpu_texture *texture);
gpu_device *gpu_texture_device(const gpu_texture *texture);
/* The render target whose color attachment this is, if any (sampling it flushes that target first). */
gpu_target *gpu_texture_source(const gpu_texture *texture);
void gpu_texture_set_source(gpu_texture *texture, gpu_target *target);
void gpu_texture_release_handles(gpu_texture *texture);

/* ---- Render targets (target.c) ------------------------------------------------------------------------------ */
/* WGPU-module draw state codes: depth 0 none 1 less 2 read; blend 0 alpha 1 add 2 multiply 3 screen 4 replace
   5 premultiplied 6 subtract 7 lightest 8 darkest 9 exclusion (Processing's blend constants are the same codes);
   cull 0 none 1 back 2 front. */
typedef struct gpu_state { int depth, blend, cull; } gpu_state;
typedef struct gpu_rect { int x, y, width, height; } gpu_rect;
gpu_target *gpu_target_create(gpu_device *device, int width, int height, WGPUTextureFormat format);
/* A target whose draws write `colors` (1..4) attachments of one format (fragment outputs @location(0..colors-1)), each
   with `mip_levels` mips. */
gpu_target *gpu_target_create_multiple(gpu_device *device, int width, int height, WGPUTextureFormat format, int colors, int mip_levels);
int gpu_target_colors(const gpu_target *target);
void gpu_target_free(gpu_target *target);
bool gpu_target_released(const gpu_target *target);
int gpu_target_width(const gpu_target *target);
int gpu_target_height(const gpu_target *target);
WGPUTextureFormat gpu_target_format(const gpu_target *target);
gpu_texture *gpu_target_color(gpu_target *target);
gpu_texture *gpu_target_color_at(gpu_target *target, int index);
/* Resizes the attachments (clearing them) and switches between 4x multisampling (smooth) and single sampling. */
void gpu_target_configure(gpu_target *target, int width, int height, bool smooth);
void gpu_target_clear(gpu_target *target, const float rgba[4]);  /* straight RGBA, stored premultiplied */
void gpu_target_clear_stencil(gpu_target *target);
void gpu_target_flush(gpu_target *target);                       /* submits the open pass */
void gpu_target_set_state(gpu_target *target, gpu_state state);
gpu_state gpu_target_state(const gpu_target *target);
/* Processing draws: `floats` vertex floats (12 per vertex), one uniform block snapshot, an optional row-vector model
   matrix applied to the vertices on the CPU (16 floats, NULL for none), topology `mode` (0 points, 1 lines, 5 strip,
   else triangles), a blend code, a depth mode, a scissor in pixels (NULL for the whole target), an image texture (NULL
   for white) with its GPU_SAMPLE_* sampling, and stencil mode (0 none, 1 write outer, 2 cut holes, 3 test).
   Consecutive draws with equal state are merged into one GPU draw. */
bool gpu_target_draw(gpu_target *target, gpu_shader *shader, const float *vertices, size_t floats, const float *uniforms, const float *model,
                     int mode, int blend, int depth_mode, int cull, const gpu_rect *scissor, gpu_texture *texture, int sampling, int stencil,
                     char *error, size_t error_size);
/* WGPU-module draws with the target's state and identity Processing uniforms: retained vertices or procedural, inside
   `scissor` (pixels; NULL for the whole target). */
bool gpu_target_draw_retained(gpu_target *target, gpu_shader *shader, gpu_buffer *vertices, int first, int count,
                              const gpu_rect *scissor, char *error, size_t error_size);
/* Compiles the pipeline gpu_target_draw_retained will use for `shader` (see gpu_shader_prepare). */
void gpu_target_prepare(gpu_target *target, gpu_shader *shader, bool vertex_buffer, gpu_prepare_done done, void *data);
/* Reads the color attachment as straight-alpha RGBA8 rows (top-down); `done` gets the pixels or an error. */
typedef void (*gpu_pixels_done)(void *data, const uint8_t *rgba, int width, int height, const char *error);
void gpu_target_read(gpu_target *target, gpu_pixels_done done, void *data);
/* Clamps a clip rectangle (x, y, w, h in pixels) to the target, as the earlier Scissor did. */
gpu_rect gpu_scissor(int width, int height, const float *clip);
void gpu_target_release_handles(gpu_target *target);

/* ---- Shaders (shader.c, shaders.c) --------------------------------------------------------------------------- */
extern const char gpu_wgsl_header[], gpu_wgsl_vertex[], gpu_wgsl_fragment[], gpu_wgsl_blit[];
typedef void (*gpu_shader_done)(gpu_shader *shader, const char *error, void *data);
/* Compiles vertex and fragment WGSL (the shared header is prepended); validation errors come back as
   "Shader compile failed: <diagnostic>". */
void gpu_shader_create(gpu_device *device, const char *vertex, const char *fragment, gpu_shader_done done, void *data);
void gpu_shader_free(gpu_shader *shader);
bool gpu_shader_released(const gpu_shader *shader);
gpu_device *gpu_shader_device(const gpu_shader *shader);
/* UserUniforms fields: floats (`set`) or one integer (`set_int`); false with an error message. */
bool gpu_shader_set(gpu_shader *shader, const char *name, const float *values, int count, char *error, size_t error_size);
bool gpu_shader_set_int(gpu_shader *shader, const char *name, int32_t value, char *error, size_t error_size);
bool gpu_shader_set_texture(gpu_shader *shader, const char *name, gpu_texture *texture, char *error, size_t error_size);
bool gpu_shader_set_buffer(gpu_shader *shader, const char *name, gpu_buffer *buffer, char *error, size_t error_size);
/* Parses UserUniforms without a device (tests and validation): offsets of fields. */
void gpu_shader_release_handles(gpu_shader *shader);
/* target.c helpers */
WGPURenderPipeline gpu_shader_pipeline(gpu_shader *shader, int mode, int blend, int depth_mode, uint32_t samples, int stencil,
                                       WGPUTextureFormat format, int colors, int cull, bool vertex_buffer, char *error, size_t error_size);
/* Compiles the pipeline gpu_shader_pipeline would return for these arguments ahead of the first draw: asynchronously
   on the web (off the browser's GPU thread), at once natively. `done` gets NULL or the failure. */
void gpu_shader_prepare(gpu_shader *shader, int mode, int blend, int depth_mode, uint32_t samples, int stencil, WGPUTextureFormat format,
                        int colors, int cull, bool vertex_buffer, gpu_prepare_done done, void *data);
WGPUBindGroup gpu_shader_user_group(gpu_shader *shader);
WGPUBindGroup gpu_shader_texture_group(gpu_shader *shader, gpu_target *destination, char *error, size_t error_size);

/* ---- Buffers and compute (buffer.c) ------------------------------------------------------------------------- */
/* Float32 storage buffers (zeroed when `values` is NULL) and 12-float vertex buffers. */
gpu_buffer *gpu_buffer_create(gpu_device *device, const float *values, int length, bool vertex, char *error, size_t error_size);
void gpu_buffer_free(gpu_buffer *buffer);
bool gpu_buffer_released(const gpu_buffer *buffer);
int gpu_buffer_length(const gpu_buffer *buffer);
bool gpu_buffer_vertex(const gpu_buffer *buffer);
gpu_device *gpu_buffer_device(const gpu_buffer *buffer);
WGPUBuffer gpu_buffer_handle(const gpu_buffer *buffer);
void gpu_buffer_write(gpu_buffer *buffer, int offset, const float *values, int count);
typedef void (*gpu_floats_done)(void *data, const float *values, int count, const char *error);
void gpu_buffer_read(gpu_buffer *buffer, int offset, int count, gpu_floats_done done, void *data);
/* Runs `source`'s cs_main over `groups` workgroups with the buffer at group 0 binding 0. */
typedef void (*gpu_dispatch_done)(void *data, const char *error);
void gpu_dispatch(gpu_device *device, const char *source, gpu_buffer *buffer, uint32_t groups, gpu_dispatch_done done, void *data);
void gpu_buffer_release_handles(gpu_buffer *buffer);
void gpu_device_release_kernels(gpu_device *device);

/* ---- Surfaces (surface.c) ------------------------------------------------------------------------------------ */
/* A webgpu.h surface for an aqua/window.h window, or NULL. */
WGPUSurface gpu_surface_create(WGPUInstance instance, aqua_window *window);

#endif
