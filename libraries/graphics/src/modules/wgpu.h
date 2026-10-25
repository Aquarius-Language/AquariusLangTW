/* What the WGPU module shares with Processing: texture objects (WGPU.Texture), loading texture assets and windows. */
#ifndef GRAPHICS_WGPU_MODULE_H
#define GRAPHICS_WGPU_MODULE_H

#include <aqua/aqua.h>
#include <aqua/window.h>
#include "../gpu/gpu.h"

/* A WGPU.Texture object for `texture` (owned), or for a render target's color attachment (`target` given: disposing
   the texture disposes the target). `keep` is an object the texture keeps alive (its device or target), or null. */
aqua_value gfx_texture_object_new(aqua_runtime *rt, gpu_device *device, gpu_texture *texture, gpu_target *target, aqua_value keep);
/* The live texture of a WGPU.Texture object, or NULL. */
gpu_texture *gfx_texture_object_texture(aqua_runtime *rt, aqua_value value);
/* Releases the texture's GPU objects (Dispose); the object stays until collected. */
void gfx_texture_object_dispose(aqua_runtime *rt, aqua_value value);
/* Reads arguments (path, width, height, format, dimension, mipLevels) from `first` and loads the texture asset
   (raw or gzip) onto `device`; the result keeps `keep` alive. Returns the object or fails the call. */
bool gfx_texture_load(aqua_call *call, gpu_device *device, uint32_t first, aqua_value keep, aqua_value *result);
/* The aqua/window.h window of a WGPU.Window object or a shared window object, or NULL. */
aqua_window *gfx_wgpu_window_of(aqua_runtime *rt, aqua_value value);

#endif
