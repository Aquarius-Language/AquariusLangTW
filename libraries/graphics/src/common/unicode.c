#include "unicode.h"

uint32_t gfx_utf8_next(const char *text, size_t length, size_t *index) {
    const unsigned char *s = (const unsigned char *)text;
    size_t i = *index;
    uint32_t c = s[i];
    int extra = c < 0x80 ? 0 : (c & 0xE0) == 0xC0 ? 1 : (c & 0xF0) == 0xE0 ? 2 : (c & 0xF8) == 0xF0 ? 3 : -1;
    *index = i + 1;
    if (extra == 0) return c;
    if (extra < 0 || i + (size_t)extra >= length) return 0xFFFD;
    c &= (1u << (6 - extra)) - 1;
    for (int k = 1; k <= extra; k++) {
        if ((s[i + k] & 0xC0) != 0x80) return 0xFFFD;
        c = (c << 6) | (s[i + k] & 0x3F);
    }
    static const uint32_t minimum[4] = {0, 0x80, 0x800, 0x10000};
    if (c < minimum[extra] || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) return 0xFFFD;
    *index = i + 1 + (size_t)extra;
    return c;
}

size_t gfx_utf8_encode(uint32_t c, char *out) {
    if (c < 0x80) { out[0] = (char)c; return 1; }
    if (c < 0x800) { out[0] = (char)(0xC0 | c >> 6); out[1] = (char)(0x80 | (c & 63)); return 2; }
    if (c < 0x10000) { out[0] = (char)(0xE0 | c >> 12); out[1] = (char)(0x80 | (c >> 6 & 63)); out[2] = (char)(0x80 | (c & 63)); return 3; }
    out[0] = (char)(0xF0 | c >> 18); out[1] = (char)(0x80 | (c >> 12 & 63)); out[2] = (char)(0x80 | (c >> 6 & 63)); out[3] = (char)(0x80 | (c & 63));
    return 4;
}
