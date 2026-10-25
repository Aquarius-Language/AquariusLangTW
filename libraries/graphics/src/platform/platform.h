/* The thin per-OS layer: one line of text in the platform's fonts (its box and an alpha raster of it) and the
 * installed font families. Windows uses GDI (src/platform/windows.c), Linux Pango and Cairo (linux.c), Android
 * android.graphics through JNI (android.c), the web Canvas 2D through web-glue/canvas (web.c). Layout, caching and
 * metrics policy stay in src/processing/text.c and src/modules/fonts.c. */
#ifndef GRAPHICS_PLATFORM_H
#define GRAPHICS_PLATFORM_H

#include <aqua/aqua.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* The raster box of `text` (UTF-8, one line) at `size` pixels in `font` ("sans-serif", "monospace" or a family):
   what textWidth reports and the size of the raster. False when the platform has no text system. */
bool gfx_platform_text_box(const char *font, float size, const char *text, size_t length, int *width, int *height);
/* Draws `text` white into a width x height alpha mask (row-major, zeroed by the caller), as boxed above. */
bool gfx_platform_text_raster(const char *font, float size, const char *text, size_t length, int width, int height, uint8_t *alpha);
/* How many pixels the platform's box is wider than the text's advance (GDI's box adds 2). */
int gfx_platform_text_padding(void);

/* Installed font faces: `add` runs once per face; `done` follows with an error message or NULL. Synchronous except
   on the web, where the browser may ask for permission first. */
typedef void (*platform_font_add)(void *data, const char *family, const char *style);
void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data);
bool gfx_platform_font_available(const char *family);

#endif
