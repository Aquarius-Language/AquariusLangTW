/* Image files and gzip for Processing images, saved frames and texture assets, on the vendored stb_image and
   stb_image_write (third_party/stb, compiled into codecs.c with private symbols). */
#ifndef GRAPHICS_CODECS_H
#define GRAPHICS_CODECS_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Decodes PNG, JPEG, BMP, TGA, GIF, PNM, PSD or HDR bytes to RGBA8 (malloc'd), or NULL with a reason. */
uint8_t *gfx_codec_decode(const uint8_t *bytes, size_t length, int *width, int *height, const char **reason);
/* PNG bytes of an RGBA8 image (malloc'd), or NULL. */
uint8_t *gfx_codec_png(const uint8_t *rgba, int width, int height, size_t *length);
/* Inflates a gzip member into exactly `expected` bytes (malloc'd); NULL when corrupt or of another size. */
uint8_t *gfx_codec_gunzip(const uint8_t *bytes, size_t length, size_t expected);

#endif
