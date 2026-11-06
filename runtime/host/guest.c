/* Typed access to guest memory and the guest runtime's exports. Guest addresses are 32-bit offsets into the
   application's linear memory; the base is re-read after every guest call because memory growth can move it. */
#include "host.h"
#include "engine.h"
#include <stdlib.h>
#include <string.h>

uint8_t *guest_base(aqua_runtime *rt) { return engine_memory(rt->engine, NULL); }

uint32_t guest(aqua_runtime *rt, guest_export name, uint32_t a, uint32_t b, uint32_t c, uint32_t d) {
    const uint32_t arguments[4] = {a, b, c, d};
    return engine_call(rt->engine, name, arguments, 4);
}

uint32_t guest_u32(aqua_runtime *rt, uint32_t address) {
    uint32_t v;
    memcpy(&v, guest_base(rt) + address, 4);
    return v;
}
void guest_set_u32(aqua_runtime *rt, uint32_t address, uint32_t value) { memcpy(guest_base(rt) + address, &value, 4); }

aqua_value guest_read_value(aqua_runtime *rt, uint32_t address) {
    aqua_value v;
    memcpy(&v, guest_base(rt) + address, sizeof v);
    return v;
}
void guest_write_value(aqua_runtime *rt, uint32_t address, aqua_value value) { memcpy(guest_base(rt) + address, &value, sizeof value); }

uint32_t guest_cell(aqua_runtime *rt, aqua_value value) {
    uint32_t cell = guest(rt, GUEST_value, 0, 0, 0, 0);
    guest_write_value(rt, cell, value);
    return cell;
}

uint32_t guest_text_ascii(aqua_runtime *rt, const char *ascii) {
    uint32_t length = (uint32_t)strlen(ascii);
    uint32_t text = guest(rt, GUEST_text, length, 0, 0, 0);
    uint8_t *units = guest_base(rt) + text + G_Text_Data;
    for (uint32_t i = 0; i < length; i++) { units[i * 2] = (uint8_t)ascii[i]; units[i * 2 + 1] = 0; }
    return text;
}

/* The code point at units[*i] (a surrogate pair advances *i past both halves); unpaired surrogates become U+FFFD. */
static uint32_t utf16_next(const uint16_t *units, size_t count, size_t *i) {
    uint32_t c = units[*i];
    if (c >= 0xd800 && c < 0xdc00 && *i + 1 < count && units[*i + 1] >= 0xdc00 && units[*i + 1] < 0xe000)
        c = 0x10000 + ((c - 0xd800) << 10) + (units[++*i] - 0xdc00);
    else if (c >= 0xd800 && c < 0xe000) c = 0xfffd;
    return c;
}

/* The UTF-8 length of UTF-16 text, so a conversion allocates exactly what it writes: multi-megabyte strings passed
   to native code (JSON, data URIs) then cost one buffer of their own size instead of three times it. */
static size_t utf16_utf8_length(const uint16_t *units, size_t count) {
    size_t n = 0;
    for (size_t i = 0; i < count; i++) {
        uint32_t c = utf16_next(units, count, &i);
        n += c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
    }
    return n;
}

/* UTF-16 to UTF-8 into `out` (capacity utf16_utf8_length + 1), terminated. Returns the byte count. */
static size_t utf16_to_utf8_into(const uint16_t *units, size_t count, char *out) {
    size_t n = 0;
    for (size_t i = 0; i < count; i++) {
        uint32_t c = utf16_next(units, count, &i);
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xc0 | c >> 6); out[n++] = (char)(0x80 | (c & 0x3f)); }
        else if (c < 0x10000) { out[n++] = (char)(0xe0 | c >> 12); out[n++] = (char)(0x80 | (c >> 6 & 0x3f)); out[n++] = (char)(0x80 | (c & 0x3f)); }
        else { out[n++] = (char)(0xf0 | c >> 18); out[n++] = (char)(0x80 | (c >> 12 & 0x3f)); out[n++] = (char)(0x80 | (c >> 6 & 0x3f)); out[n++] = (char)(0x80 | (c & 0x3f)); }
    }
    out[n] = 0;
    return n;
}

/* UTF-16 to malloc'd UTF-8, replacing unpaired surrogates with U+FFFD. */
char *utf16_to_utf8(const uint16_t *units, size_t count, size_t *length) {
    char *out = malloc(utf16_utf8_length(units, count) + 1);
    size_t n = utf16_to_utf8_into(units, count, out);
    if (length) *length = n;
    return out;
}

/* UTF-8 to UTF-16 into `out` (capacity >= length units); invalid sequences become U+FFFD. Returns the unit count.
   With `out` NULL it only counts the units, so callers can allocate exactly. */
size_t utf8_to_utf16(const char *text, size_t length, uint16_t *out) {
#define PUT(unit) (out ? (void)(out[n] = (uint16_t)(unit)) : (void)0, n++)
    const unsigned char *s = (const unsigned char *)text;
    size_t n = 0, i = 0;
    while (i < length) {
        uint32_t c = s[i], extra = 0, minimum = 0;
        if (c < 0x80) extra = 0;
        else if ((c & 0xe0) == 0xc0) { c &= 0x1f; extra = 1; minimum = 0x80; }
        else if ((c & 0xf0) == 0xe0) { c &= 0x0f; extra = 2; minimum = 0x800; }
        else if ((c & 0xf8) == 0xf0) { c &= 0x07; extra = 3; minimum = 0x10000; }
        else { PUT(0xfffd); i++; continue; }
        if (extra && i + extra >= length) { PUT(0xfffd); break; } /* truncated sequence */
        bool valid = true;
        for (uint32_t k = 1; k <= extra; k++) {
            if ((s[i + k] & 0xc0) != 0x80) { valid = false; break; }
            c = c << 6 | (s[i + k] & 0x3f);
        }
        if (!valid || c < minimum || c > 0x10ffff || (c >= 0xd800 && c < 0xe000)) { PUT(0xfffd); i++; continue; }
        i += extra + 1;
        if (c >= 0x10000) { c -= 0x10000; PUT(0xd800 + (c >> 10)); PUT(0xdc00 + (c & 0x3ff)); }
        else PUT(c);
    }
#undef PUT
    return n;
}

int32_t guest_member_index(aqua_runtime *rt, const aqua_class *c, uint32_t text) {
    return class_member_units(c, (const uint16_t *)(guest_base(rt) + text + G_Text_Data), guest_u32(rt, text + G_Text_Length));
}

/* Guest Text's units, read in place (valid until the next guest call). */
static const uint16_t *text_units(aqua_runtime *rt, uint32_t text) { return (const uint16_t *)(guest_base(rt) + text + G_Text_Data); }

size_t guest_text_utf8_length(aqua_runtime *rt, uint32_t text) {
    return utf16_utf8_length(text_units(rt, text), guest_u32(rt, text + G_Text_Length));
}
size_t guest_text_utf8_into(aqua_runtime *rt, uint32_t text, char *out) {
    return utf16_to_utf8_into(text_units(rt, text), guest_u32(rt, text + G_Text_Length), out);
}
char *guest_text_utf8(aqua_runtime *rt, uint32_t text, size_t *length) {
    char *utf8 = malloc(guest_text_utf8_length(rt, text) + 1);
    size_t n = guest_text_utf8_into(rt, text, utf8);
    if (length) *length = n;
    return utf8;
}
