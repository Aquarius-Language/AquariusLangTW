/* The STBImage module: stb_image 2.30 (third_party/stb, compiled here with every symbol static) decodes PNG, JPEG,
 * BMP, TGA, PSD, GIF, HDR, PIC and PNM files or packaged assets into RGBA8 GL_DATA for glTexImage2D. */
#include "opengl.h"
#include <stdlib.h>
#include <string.h>

#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_NO_STDIO /* files and assets are read by opengl_read */
#define STBI_MALLOC(size) aqua_alloc(size)
#define STBI_REALLOC(pointer, size) aqua_realloc(pointer, size)
#define STBI_FREE(pointer) aqua_free(pointer)
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-function"
#endif
#include "../third_party/stb/stb_image.h"
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

/* STBImage.Load(path, flip) -> [width, height, 4, GL_DATA of RGBA8 rows, bottom row first when flip is true]. */
static void load(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    const char *path = opengl_text(call, "Load", 0);
    if (!path) return;
    aqua_value flip = aqua_arg(call, 1);
    if (flip.type != AQUA_BOOL) { opengl_fail(call, "Load", "Expected a Boolean flip flag."); return; }
    size_t length;
    char *error = NULL;
    uint8_t *bytes = opengl_read(rt, path, &length, &error);
    if (!bytes) { opengl_fail(call, "Load", "%s", error); aqua_free(error); return; }
    int width = 0, height = 0, channels = 0;
    stbi_uc *pixels = length <= INT32_MAX ? stbi_load_from_memory(bytes, (int)length, &width, &height, &channels, 4) : NULL;
    aqua_free(bytes);
    if (!pixels) {
        const char *reason = stbi_failure_reason();
        opengl_fail(call, "Load", "%s", reason ? reason : "Image decoding failed.");
        return;
    }
    uint64_t size = (uint64_t)width * (uint64_t)height * 4;
    if (size > GL_DATA_LIMIT) { stbi_image_free(pixels); opengl_fail(call, "Load", "The image is larger than 256 MiB."); return; }
    gl_data *data;
    aqua_value value = gl_data_new(rt, (uint32_t)size, &data);
    size_t row = (size_t)width * 4;
    for (int y = 0; y < height; y++)
        memcpy(data->bytes + (size_t)y * row, pixels + (size_t)(flip.number != 0 ? height - 1 - y : y) * row, row);
    stbi_image_free(pixels);
    aqua_value result = aqua_array_new(rt, 4);
    aqua_array_set(rt, result, 0, aqua_int(width));
    aqua_array_set(rt, result, 1, aqua_int(height));
    aqua_array_set(rt, result, 2, aqua_int(4));
    aqua_array_set(rt, result, 3, value);
    aqua_return(call, result);
}

void opengl_register_image(aqua_library *library) {
    aqua_class *images = aqua_define_module(library, "STBImage");
    aqua_define_function(images, "Load|載入", load, "vv");
}
