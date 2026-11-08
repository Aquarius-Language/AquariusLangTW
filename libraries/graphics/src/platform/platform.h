/* The thin per-OS layer: one line of text in the platform's fonts (its box and an alpha raster of it) and the
 * installed font families. Windows uses GDI (src/platform/windows.c), Linux Pango and Cairo (linux.c), macOS and iOS
 * Core Text (apple.c), Android android.graphics through JNI (android.c), the web Canvas 2D through web-glue/canvas
 * (web.c). Layout, caching and
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
/* Draws `text` white into an alpha mask (row-major, zeroed by the caller) of the box (width x height, as above) with
   `margin` pixels on every side, the box's top-left at (margin, margin): glyphs drawn outside their font's box (an
   underscore below the descent in some fonts and sizes) land in the margin instead of being cut off. */
bool gfx_platform_text_raster(const char *font, float size, const char *text, size_t length, int width, int height, int margin,
                              uint8_t *alpha);
/* The advance of `text`: where a following character would start, measured from the box's left edge (where every
   platform's raster puts the pen), fractional where the platform gives it. Carets and selections sit at advances.
   False when the platform has no text system. */
bool gfx_platform_text_advance(const char *font, float size, const char *text, size_t length, double *advance);

/* A font argument is a family, optionally followed by ":<weight>" (1..1000) and ":italic", as drawUI names styled text
   ("Segoe UI:700:italic"). Splits it into the family (truncated to `size` bytes), the weight (400 without one) and the
   slant; each platform applies them to its own font description. */
static inline void gfx_font_parse(const char *font, char *family, size_t size, int *weight, bool *italic) {
    *weight = 400;
    *italic = false;
    size_t end = 0;
    while (font[end]) end++;
    for (;;) {
        size_t colon = end;
        while (colon > 0 && font[colon - 1] != ':') colon--;
        if (colon == 0) break;
        const char *part = font + colon;
        size_t length = end - colon;
        int number = 0;
        bool digits = length > 0 && length <= 4;
        for (size_t i = 0; i < length && digits; i++) digits = part[i] >= '0' && part[i] <= '9', number = number * 10 + (part[i] - '0');
        if (digits && number >= 1 && number <= 1000) *weight = number;
        else if (length == 6 && part[0] == 'i' && part[1] == 't' && part[2] == 'a' && part[3] == 'l' && part[4] == 'i' && part[5] == 'c') *italic = true;
        else break;
        end = colon - 1;
    }
    size_t n = end < size - 1 ? end : size - 1;
    for (size_t i = 0; i < n; i++) family[i] = font[i];
    family[n] = 0;
}
/* Installed font faces: `add` runs once per face; `done` follows with an error message or NULL. Synchronous except
   on the web, where the browser may ask for permission first. */
typedef void (*platform_font_add)(void *data, const char *family, const char *style);
void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data);
bool gfx_platform_font_available(const char *family);

#endif
