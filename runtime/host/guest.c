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

/* UTF-16 to UTF-8, replacing unpaired surrogates with U+FFFD. */
char *utf16_to_utf8(const uint16_t *units, size_t count, size_t *length) {
    char *out = malloc(count * 3 + 1);
    size_t n = 0;
    for (size_t i = 0; i < count; i++) {
        uint32_t c = units[i];
        if (c >= 0xd800 && c < 0xdc00 && i + 1 < count && units[i + 1] >= 0xdc00 && units[i + 1] < 0xe000)
            c = 0x10000 + ((c - 0xd800) << 10) + (units[++i] - 0xdc00);
        else if (c >= 0xd800 && c < 0xe000) c = 0xfffd;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xc0 | c >> 6); out[n++] = (char)(0x80 | (c & 0x3f)); }
        else if (c < 0x10000) { out[n++] = (char)(0xe0 | c >> 12); out[n++] = (char)(0x80 | (c >> 6 & 0x3f)); out[n++] = (char)(0x80 | (c & 0x3f)); }
        else { out[n++] = (char)(0xf0 | c >> 18); out[n++] = (char)(0x80 | (c >> 12 & 0x3f)); out[n++] = (char)(0x80 | (c >> 6 & 0x3f)); out[n++] = (char)(0x80 | (c & 0x3f)); }
    }
    out[n] = 0;
    if (length) *length = n;
    return out;
}

/* UTF-8 to UTF-16 into `out` (capacity >= length units); invalid sequences become U+FFFD. Returns the unit count. */
size_t utf8_to_utf16(const char *text, size_t length, uint16_t *out) {
    const unsigned char *s = (const unsigned char *)text;
    size_t n = 0, i = 0;
    while (i < length) {
        uint32_t c = s[i], extra = 0, minimum = 0;
        if (c < 0x80) extra = 0;
        else if ((c & 0xe0) == 0xc0) { c &= 0x1f; extra = 1; minimum = 0x80; }
        else if ((c & 0xf0) == 0xe0) { c &= 0x0f; extra = 2; minimum = 0x800; }
        else if ((c & 0xf8) == 0xf0) { c &= 0x07; extra = 3; minimum = 0x10000; }
        else { out[n++] = 0xfffd; i++; continue; }
        if (extra && i + extra >= length) { out[n++] = 0xfffd; break; } /* truncated sequence */
        bool valid = true;
        for (uint32_t k = 1; k <= extra; k++) {
            if ((s[i + k] & 0xc0) != 0x80) { valid = false; break; }
            c = c << 6 | (s[i + k] & 0x3f);
        }
        if (!valid || c < minimum || c > 0x10ffff || (c >= 0xd800 && c < 0xe000)) { out[n++] = 0xfffd; i++; continue; }
        i += extra + 1;
        if (c >= 0x10000) { c -= 0x10000; out[n++] = (uint16_t)(0xd800 + (c >> 10)); out[n++] = (uint16_t)(0xdc00 + (c & 0x3ff)); }
        else out[n++] = (uint16_t)c;
    }
    return n;
}

int32_t guest_member_index(aqua_runtime *rt, const aqua_class *c, uint32_t text) {
    return class_member_units(c, (const uint16_t *)(guest_base(rt) + text + G_Text_Data), guest_u32(rt, text + G_Text_Length));
}

char *guest_text_utf8(aqua_runtime *rt, uint32_t text, size_t *length) {
    uint32_t count = guest_u32(rt, text + G_Text_Length);
    uint16_t *units = malloc((size_t)count * 2 + 2);
    memcpy(units, guest_base(rt) + text + G_Text_Data, (size_t)count * 2);
    char *utf8 = utf16_to_utf8(units, count, length);
    free(units);
    return utf8;
}
