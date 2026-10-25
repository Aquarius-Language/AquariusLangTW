/* The shared image object of aqua/image.h. Its class belongs to the core library so every library can create and read
   images without depending on another library. */
#include "host.h"
#include <aqua/image.h>
#include <stdlib.h>
#include <string.h>

aqua_class *image_class;

static void image_finalize(aqua_runtime *rt, void *data) {
    aqua_image *image = data;
    aqua_release(rt, image->metadata);
    free(image->rgba); free(image->pixel_format); free(image);
}

static aqua_image *self(aqua_call *call) { return aqua_self(call); }
static void width(aqua_call *call) { aqua_return_int(call, self(call)->width); }
static void height(aqua_call *call) { aqua_return_int(call, self(call)->height); }
static void pixel_format(aqua_call *call) { aqua_return_cstring(call, self(call)->pixel_format ? self(call)->pixel_format : "Rgba32"); }
static void alpha(aqua_call *call) {
    static const char *const names[] = {"Opaque", "Straight", "Premultiplied"};
    aqua_return_cstring(call, names[self(call)->alpha]);
}
static void metadata(aqua_call *call) {
    aqua_image *image = self(call);
    aqua_return(call, image->metadata ? aqua_ref_value(aqua_call_runtime(call), image->metadata) : aqua_hash_new(aqua_call_runtime(call), 0));
}
/* Pixels(): the RGBA bytes as an array of integers 0..255. */
static void pixels(aqua_call *call) {
    aqua_image *image = self(call);
    aqua_return(call, aqua_array_from_u8(aqua_call_runtime(call), image->rgba, (uint32_t)image->width * (uint32_t)image->height * 4));
}

void image_library(aqua_library *library) {
    image_class = aqua_define_class(library, "image", image_finalize);
    aqua_define_property(image_class, "width|寬度", width);
    aqua_define_property(image_class, "height|高度", height);
    aqua_define_property(image_class, "pixelFormat|像素格式", pixel_format);
    aqua_define_property(image_class, "alpha|透明度", alpha);
    aqua_define_property(image_class, "metadata|中繼資料", metadata);
    aqua_define_function(image_class, "Pixels|像素", pixels, "");
}

aqua_value aqua_image_new(aqua_runtime *rt, int32_t w, int32_t h, const uint8_t *rgba, aqua_alpha alpha_kind) {
    if (w <= 0 || h <= 0 || (int64_t)w * h > (int64_t)16 * 1024 * 1024) return host_error(rt, "Invalid image size %dx%d.", w, h);
    aqua_image *image = calloc(1, sizeof *image);
    size_t bytes = (size_t)w * (size_t)h * 4;
    image->width = w; image->height = h; image->alpha = alpha_kind;
    image->rgba = rgba ? malloc(bytes) : calloc(bytes, 1);
    if (rgba) memcpy(image->rgba, rgba, bytes);
    return aqua_object_new(rt, image_class, image);
}

aqua_image *aqua_image_of(aqua_runtime *rt, aqua_value value) { return aqua_object_data(rt, value, image_class); }

void aqua_image_describe(aqua_runtime *rt, aqua_image *image, aqua_value metadata_value, const char *format) {
    aqua_release(rt, image->metadata);
    image->metadata = metadata_value.type == AQUA_HASH ? aqua_retain(rt, metadata_value) : 0;
    free(image->pixel_format);
    image->pixel_format = format ? strdup(format) : NULL;
}
