/* aqua/image.h — the image object libraries exchange, part of API version 1: decoded files (Images), camera frames
 * (Webcam, Camera), inference inputs (ONNX, MediaPipe) and conversions to and from Processing images all use it, so a
 * library never needs another library's types. Scripts see width, height, pixelFormat, alpha, metadata and Pixels().
 *
 * 函式庫之間交換的影像物件：寬、高、像素格式、透明度、中繼資料與 Pixels()。
 */
#ifndef AQUA_IMAGE_H
#define AQUA_IMAGE_H
#include "aqua.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum aqua_alpha { AQUA_ALPHA_NONE = 0, AQUA_ALPHA_STRAIGHT = 1, AQUA_ALPHA_PREMULTIPLIED = 2 } aqua_alpha;

typedef struct aqua_image {
    int32_t width, height;
    uint8_t *rgba;          /* top-down RGBA8, width * height * 4 bytes, owned by the image */
    aqua_alpha alpha;
    char *pixel_format;     /* the source's pixel format ("Rgba32", "Rgb24", "Gray8", ...), owned */
    aqua_ref metadata;      /* a hash of source metadata (orientation, resolution, ...), or 0 */
} aqua_image;

/* A new image object of `width` x `height` RGBA pixels, copied from `rgba` (or zero when NULL). */
aqua_value aqua_image_new(aqua_runtime *rt, int32_t width, int32_t height, const uint8_t *rgba, aqua_alpha alpha);
/* The image behind a value, or NULL when it is not an image object. */
aqua_image *aqua_image_of(aqua_runtime *rt, aqua_value value);
/* Replaces the image's metadata hash (retained) and source pixel format. */
void aqua_image_describe(aqua_runtime *rt, aqua_image *image, aqua_value metadata, const char *pixel_format);

#ifdef __cplusplus
}
#endif
#endif
