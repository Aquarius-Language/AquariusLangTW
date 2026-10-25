/* UTF-8 scalars and extended grapheme clusters (Unicode UAX #29), for keyTyped, TextInput.Backspace and Fonts. */
#ifndef GRAPHICS_UNICODE_H
#define GRAPHICS_UNICODE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Decodes the scalar at text[*index] and advances; invalid sequences decode as U+FFFD (one byte at a time). */
uint32_t gfx_utf8_next(const char *text, size_t length, size_t *index);
/* Encodes `scalar` into out (at least 4 bytes); returns the byte count. */
size_t gfx_utf8_encode(uint32_t scalar, char *out);
/* UTF-16 code units of a scalar (1 or 2). */
static inline size_t utf16_units(uint32_t scalar) { return scalar > 0xFFFF ? 2 : 1; }

/* Byte offset of the start of the last extended grapheme cluster of `text` (0 when empty or a single cluster). */
size_t gfx_grapheme_last_start(const char *text, size_t length);
/* Whether the UTF-16 offset `units` falls on a grapheme cluster boundary of `text` (0 and the end always do). */
bool gfx_grapheme_boundary_utf16(const char *text, size_t length, size_t units);

#endif
