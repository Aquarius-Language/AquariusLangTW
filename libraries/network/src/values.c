/* Buffers, header lists and conversions between language values and bytes, text and JSON. JSON follows the old
 * library's System.Text.Json behavior: strict RFC 8259 input, depth 64, duplicate object keys keep the last value,
 * integer literals in int32 range become integers and every other number a double. */
#include "network.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- Buffers ---- */

bool buffer_reserve(net_buffer *b, size_t extra) {
    if (b->capacity - b->length > extra) return true;
    size_t capacity = b->capacity ? b->capacity : 256;
    while (capacity - b->length <= extra) {
        if (capacity > SIZE_MAX / 2) return false;
        capacity *= 2;
    }
    uint8_t *data = realloc(b->data, capacity);
    if (!data) return false;
    b->data = data;
    b->capacity = capacity;
    return true;
}
bool buffer_append(net_buffer *b, const void *data, size_t length) {
    if (!buffer_reserve(b, length)) return false;
    if (length) memcpy(b->data + b->length, data, length);
    b->length += length;
    b->data[b->length] = 0;
    return true;
}
bool buffer_text(net_buffer *b, const char *text) { return buffer_append(b, text, strlen(text)); }
bool buffer_format(net_buffer *b, const char *format, ...) {
    char small[256];
    va_list arguments;
    va_start(arguments, format);
    int n = vsnprintf(small, sizeof small, format, arguments);
    va_end(arguments);
    if (n < 0) return false;
    if ((size_t)n < sizeof small) return buffer_append(b, small, (size_t)n);
    if (!buffer_reserve(b, (size_t)n + 1)) return false;
    va_start(arguments, format);
    vsnprintf((char *)b->data + b->length, (size_t)n + 1, format, arguments);
    va_end(arguments);
    b->length += (size_t)n;
    return true;
}
void buffer_consume(net_buffer *b, size_t count) {
    if (count >= b->length) { b->length = 0; if (b->data) b->data[0] = 0; return; }
    memmove(b->data, b->data + count, b->length - count);
    b->length -= count;
    b->data[b->length] = 0;
}
char *buffer_cstring(net_buffer *b) {
    if (!buffer_reserve(b, 0)) return NULL;
    b->data[b->length] = 0;
    return (char *)b->data;
}
char *buffer_take(net_buffer *b, size_t *length) {
    char *text = malloc(b->length + 1);
    if (!text) return NULL;
    if (b->length) memcpy(text, b->data, b->length);
    text[b->length] = 0;
    if (length) *length = b->length;
    b->length = 0;
    return text;
}
void buffer_free(net_buffer *b) { free(b->data); b->data = NULL; b->length = b->capacity = 0; }

/* ---- Text ---- */

char *net_strndup(const char *text, size_t length) {
    char *copy = malloc(length + 1);
    if (!copy) return NULL;
    memcpy(copy, text, length);
    copy[length] = 0;
    return copy;
}
char *net_strdup(const char *text) { return text ? net_strndup(text, strlen(text)) : NULL; }
static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + 32 : c; }
bool net_iequal(const char *a, const char *b) {
    while (*a && lower((unsigned char)*a) == lower((unsigned char)*b)) { a++; b++; }
    return *a == 0 && *b == 0;
}
bool net_iprefix(const char *text, const char *prefix) {
    while (*prefix && lower((unsigned char)*text) == lower((unsigned char)*prefix)) { text++; prefix++; }
    return *prefix == 0;
}
const char *net_itoken(const char *list, const char *token) {
    size_t n = strlen(token);
    for (const char *p = list; p && *p;) {
        while (*p == ' ' || *p == '\t' || *p == ',') p++;
        const char *end = p;
        while (*end && *end != ',') end++;
        const char *last = end;
        while (last > p && (last[-1] == ' ' || last[-1] == '\t')) last--;
        if ((size_t)(last - p) == n) {
            size_t i = 0;
            while (i < n && lower((unsigned char)p[i]) == lower((unsigned char)token[i])) i++;
            if (i == n) return p;
        }
        p = end;
    }
    return NULL;
}

size_t utf8_incomplete_tail(const uint8_t *data, size_t length) {
    for (size_t back = 1; back <= 3 && back <= length; back++) {
        uint8_t c = data[length - back];
        if ((c & 0xC0) == 0x80) continue;          /* continuation byte: keep looking for the lead */
        size_t need = c >= 0xF0 && c <= 0xF4 ? 4 : c >= 0xE0 ? 3 : c >= 0xC2 && c < 0xE0 ? 2 : 1;
        return need > back ? back : 0;
    }
    return 0;
}
size_t utf16_length(const char *utf8, size_t length) {
    size_t units = 0;
    for (size_t i = 0; i < length; i++) {
        uint8_t c = (uint8_t)utf8[i];
        if ((c & 0xC0) != 0x80) units += c >= 0xF0 ? 2 : 1;
    }
    return units;
}

/* ---- Headers ---- */

void headers_add(net_headers *h, const char *name, const char *value) {
    if (h->count == h->capacity) {
        uint32_t capacity = h->capacity ? h->capacity * 2 : 8;
        net_header *items = realloc(h->items, capacity * sizeof *items);
        if (!items) return;
        h->items = items;
        h->capacity = capacity;
    }
    h->items[h->count].name = net_strdup(name);
    h->items[h->count].value = net_strdup(value);
    h->count++;
}
void headers_remove(net_headers *h, const char *name) {
    uint32_t kept = 0;
    for (uint32_t i = 0; i < h->count; i++) {
        if (net_iequal(h->items[i].name, name)) { free(h->items[i].name); free(h->items[i].value); continue; }
        h->items[kept++] = h->items[i];
    }
    h->count = kept;
}
void headers_set(net_headers *h, const char *name, const char *value) {
    for (uint32_t i = 0; i < h->count; i++) {
        if (!net_iequal(h->items[i].name, name)) continue;
        free(h->items[i].value);
        h->items[i].value = net_strdup(value);
        /* Later duplicates of the name go. */
        for (uint32_t j = h->count; j-- > i + 1;) {
            if (!net_iequal(h->items[j].name, name)) continue;
            free(h->items[j].name); free(h->items[j].value);
            memmove(&h->items[j], &h->items[j + 1], (h->count - j - 1) * sizeof *h->items);
            h->count--;
        }
        return;
    }
    headers_add(h, name, value);
}
const char *headers_get(const net_headers *h, const char *name) {
    for (uint32_t i = 0; i < h->count; i++) if (net_iequal(h->items[i].name, name)) return h->items[i].value;
    return NULL;
}
void headers_copy(net_headers *to, const net_headers *from) {
    for (uint32_t i = 0; i < from->count; i++) headers_add(to, from->items[i].name, from->items[i].value);
}
void headers_free(net_headers *h) {
    for (uint32_t i = 0; i < h->count; i++) { free(h->items[i].name); free(h->items[i].value); }
    free(h->items);
    h->items = NULL;
    h->count = h->capacity = 0;
}
aqua_value headers_hash(aqua_runtime *rt, const net_headers *h, const char *separator) {
    aqua_value hash = aqua_hash_new(rt, h->count);
    bool *used = calloc(h->count + 1, 1);
    net_buffer joined = {0};
    for (uint32_t i = 0; i < h->count; i++) {
        if (used[i]) continue;
        joined.length = 0;
        buffer_text(&joined, h->items[i].value);
        for (uint32_t j = i + 1; j < h->count; j++) {
            if (used[j] || !net_iequal(h->items[i].name, h->items[j].name)) continue;
            used[j] = true;
            buffer_text(&joined, separator);
            buffer_text(&joined, h->items[j].value);
        }
        aqua_hash_set(rt, hash, aqua_cstring(rt, h->items[i].name), aqua_text(rt, (const char *)joined.data, joined.length));
    }
    buffer_free(&joined);
    free(used);
    return hash;
}
bool header_valid(const char *name, const char *value) {
    if (!name || !*name) return false;
    for (const char *p = name; *p; p++) {
        unsigned char c = (unsigned char)*p;
        bool alnum = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        if (!alnum && !strchr("!#$%&'*+-.^_`|~", c)) return false;
    }
    return !value || (!strchr(value, '\r') && !strchr(value, '\n'));
}

/* ---- Language values ---- */

bool value_text(aqua_runtime *rt, aqua_value v, char **out, net_error *e) {
    if (v.type != AQUA_STRING) return net_error_set(e, NET_INVALID, false, MSG_STRING);
    *out = aqua_text_dup(rt, v, NULL);
    return *out != NULL;
}
bool value_integer(aqua_value v, int32_t *out, net_error *e) {
    if (!aqua_is_number(v) || !isfinite(v.number)) return net_error_set(e, NET_INVALID, false, "Expected a finite number.");
    if (v.number != trunc(v.number)) return net_error_set(e, NET_INVALID, false, "Expected an integer.");
    if (v.number < -2147483648.0 || v.number > 2147483647.0) return net_error_set(e, NET_INVALID, false, "Arithmetic operation resulted in an overflow.");
    *out = (int32_t)v.number;
    return true;
}
bool value_boolean(aqua_value v, bool *out, net_error *e) {
    if (v.type != AQUA_BOOL) return net_error_set(e, NET_INVALID, false, "Expected a Boolean.");
    *out = v.number != 0;
    return true;
}
bool value_bytes(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e) {
    if (v.type != AQUA_ARRAY) return net_error_set(e, NET_INVALID, false, MSG_BYTES);
    uint32_t n = aqua_array_length(rt, v);
    if (n > NET_MAX_BYTES) return net_error_set(e, NET_LIMIT, false, "Data exceeds the byte limit.");
    out->length = 0;
    if (!buffer_reserve(out, n)) return net_error_set(e, NET_LIMIT, false, "Data exceeds the byte limit.");
    if (n && !aqua_array_read_u8(rt, v, 0, n, out->data)) return net_error_set(e, NET_INVALID, false, MSG_BYTES);
    out->length = n;
    out->data[n] = 0;
    return true;
}
bool value_payload(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e) {
    out->length = 0;
    if (v.type == AQUA_STRING) {
        size_t length;
        char *text = aqua_text_dup(rt, v, &length);
        bool ok = text && buffer_append(out, text, length);
        aqua_free(text);
        return ok || net_error_set(e, NET_LIMIT, false, "Data exceeds the byte limit.");
    }
    if (v.type == AQUA_ARRAY) return value_bytes(rt, v, out, e);
    return json_encode(rt, v, out, e);
}
aqua_value bytes_value(aqua_runtime *rt, const uint8_t *data, size_t length) {
    return aqua_array_from_u8(rt, data, (uint32_t)length);
}

/* ---- JSON output ---- */

static void json_string(net_buffer *out, const char *text, size_t length) {
    static const char hex[] = "0123456789abcdef";
    buffer_append(out, "\"", 1);
    size_t start = 0;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)text[i];
        if (c >= 0x20 && c != '"' && c != '\\') continue;
        buffer_append(out, text + start, i - start);
        start = i + 1;
        switch (c) {
        case '"': buffer_append(out, "\\\"", 2); break;
        case '\\': buffer_append(out, "\\\\", 2); break;
        case '\n': buffer_append(out, "\\n", 2); break;
        case '\r': buffer_append(out, "\\r", 2); break;
        case '\t': buffer_append(out, "\\t", 2); break;
        case '\b': buffer_append(out, "\\b", 2); break;
        case '\f': buffer_append(out, "\\f", 2); break;
        default: { char escape[6] = {'\\', 'u', '0', '0', hex[c >> 4], hex[c & 15]}; buffer_append(out, escape, 6); }
        }
    }
    buffer_append(out, text + start, length - start);
    buffer_append(out, "\"", 1);
}

/* The shortest text that reads back as the same double. */
static void json_number(net_buffer *out, double n) {
    char text[40];
    for (int precision = 1; precision <= 17; precision++) {
        snprintf(text, sizeof text, "%.*g", precision, n);
        if (strtod(text, NULL) == n) break;
    }
    buffer_text(out, text);
}

static bool encode(aqua_runtime *rt, aqua_value v, net_buffer *out, int depth, net_error *e) {
    if (depth > NET_JSON_DEPTH) return net_error_set(e, NET_LIMIT, false, "JSON depth exceeds 64 (or contains a cycle).");
    switch (v.type) {
    case AQUA_VOID: case AQUA_NULL: return buffer_text(out, "null");
    case AQUA_BOOL: return buffer_text(out, v.number != 0 ? "true" : "false");
    case AQUA_INT: return buffer_format(out, "%d", (int32_t)v.number);
    case AQUA_FLOAT: case AQUA_DOUBLE:
        if (!isfinite(v.number)) return net_error_set(e, NET_INVALID, false, "Expected a finite number.");
        json_number(out, v.number);
        return true;
    case AQUA_STRING: {
        size_t length;
        char *text = aqua_text_dup(rt, v, &length);
        json_string(out, text ? text : "", text ? length : 0);
        aqua_free(text);
        return true;
    }
    case AQUA_ARRAY: {
        uint32_t n = aqua_array_length(rt, v);
        buffer_append(out, "[", 1);
        for (uint32_t i = 0; i < n; i++) {
            if (i) buffer_append(out, ",", 1);
            if (!encode(rt, aqua_array_get(rt, v, i), out, depth + 1, e)) return false;
        }
        return buffer_append(out, "]", 1);
    }
    case AQUA_HASH: {
        uint32_t n = aqua_hash_count(rt, v);
        buffer_append(out, "{", 1);
        for (uint32_t i = 0; i < n; i++) {
            aqua_value key, item;
            if (!aqua_hash_entry(rt, v, i, &key, &item)) continue;
            if (key.type != AQUA_STRING) return net_error_set(e, NET_INVALID, false, MSG_STRING);
            if (i) buffer_append(out, ",", 1);
            if (!encode(rt, key, out, depth + 1, e)) return false;
            buffer_append(out, ":", 1);
            if (!encode(rt, item, out, depth + 1, e)) return false;
        }
        return buffer_append(out, "}", 1);
    }
    default:
        return net_error_set(e, NET_INVALID, false, "JSON supports null, Boolean, finite numbers, strings, arrays and string-keyed hashes.");
    }
}
bool json_encode(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e) {
    out->length = 0;
    if (!buffer_reserve(out, 64)) return net_error_set(e, NET_LIMIT, false, "Data exceeds the byte limit.");
    out->data[0] = 0;
    return encode(rt, v, out, 0, e);
}

/* ---- JSON input ---- */

typedef struct json_reader {
    aqua_runtime *rt;
    const uint8_t *start, *p, *end;
    net_buffer text;
    net_error *e;
} json_reader;

static bool json_fail(json_reader *r, const char *what) {
    return net_error_set(r->e, NET_INVALID, false, "Invalid JSON / JSON 格式無效：%s (byte %zu).", what, (size_t)(r->p - r->start));
}
static void skip_space(json_reader *r) {
    while (r->p < r->end && (*r->p == ' ' || *r->p == '\t' || *r->p == '\n' || *r->p == '\r')) r->p++;
}
static int hex_digit(uint8_t c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
static bool read_hex4(json_reader *r, uint32_t *out) {
    if (r->end - r->p < 4) return json_fail(r, "short \\u escape");
    uint32_t n = 0;
    for (int i = 0; i < 4; i++) {
        int d = hex_digit(r->p[i]);
        if (d < 0) return json_fail(r, "bad \\u escape");
        n = n * 16 + (uint32_t)d;
    }
    r->p += 4;
    *out = n;
    return true;
}
static void put_utf8(net_buffer *b, uint32_t c) {
    uint8_t s[4];
    size_t n;
    if (c < 0x80) { s[0] = (uint8_t)c; n = 1; }
    else if (c < 0x800) { s[0] = (uint8_t)(0xC0 | c >> 6); s[1] = (uint8_t)(0x80 | (c & 63)); n = 2; }
    else if (c < 0x10000) { s[0] = (uint8_t)(0xE0 | c >> 12); s[1] = (uint8_t)(0x80 | (c >> 6 & 63)); s[2] = (uint8_t)(0x80 | (c & 63)); n = 3; }
    else { s[0] = (uint8_t)(0xF0 | c >> 18); s[1] = (uint8_t)(0x80 | (c >> 12 & 63)); s[2] = (uint8_t)(0x80 | (c >> 6 & 63)); s[3] = (uint8_t)(0x80 | (c & 63)); n = 4; }
    buffer_append(b, s, n);
}
/* Reads a string after its opening quote into r->text. */
static bool read_string(json_reader *r) {
    r->text.length = 0;
    const uint8_t *run = r->p;
    while (r->p < r->end) {
        uint8_t c = *r->p;
        if (c == '"') { buffer_append(&r->text, run, (size_t)(r->p - run)); r->p++; return true; }
        if (c < 0x20) return json_fail(r, "control character in string");
        if (c != '\\') { r->p++; continue; }
        buffer_append(&r->text, run, (size_t)(r->p - run));
        if (++r->p >= r->end) break;
        c = *r->p++;
        switch (c) {
        case '"': case '\\': case '/': buffer_append(&r->text, &c, 1); break;
        case 'b': buffer_append(&r->text, "\b", 1); break;
        case 'f': buffer_append(&r->text, "\f", 1); break;
        case 'n': buffer_append(&r->text, "\n", 1); break;
        case 'r': buffer_append(&r->text, "\r", 1); break;
        case 't': buffer_append(&r->text, "\t", 1); break;
        case 'u': {
            uint32_t unit;
            if (!read_hex4(r, &unit)) return false;
            if (unit >= 0xD800 && unit < 0xDC00 && r->end - r->p >= 6 && r->p[0] == '\\' && r->p[1] == 'u') {
                const uint8_t *back = r->p;
                uint32_t low;
                r->p += 2;
                if (!read_hex4(r, &low)) return false;
                if (low >= 0xDC00 && low < 0xE000) unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                else r->p = back;
            }
            if (unit >= 0xD800 && unit < 0xE000) unit = 0xFFFD; /* lone surrogate */
            put_utf8(&r->text, unit);
            break;
        }
        default: r->p--; return json_fail(r, "bad escape");
        }
        run = r->p;
    }
    return json_fail(r, "unterminated string");
}
static bool read_number(json_reader *r, aqua_value *out) {
    const uint8_t *s = r->p;
    bool integer = true;
    if (r->p < r->end && *r->p == '-') r->p++;
    if (r->p >= r->end) return json_fail(r, "bad number");
    if (*r->p == '0') r->p++;
    else if (*r->p >= '1' && *r->p <= '9') while (r->p < r->end && *r->p >= '0' && *r->p <= '9') r->p++;
    else return json_fail(r, "bad number");
    if (r->p < r->end && *r->p == '.') {
        integer = false;
        r->p++;
        if (r->p >= r->end || *r->p < '0' || *r->p > '9') return json_fail(r, "bad fraction");
        while (r->p < r->end && *r->p >= '0' && *r->p <= '9') r->p++;
    }
    if (r->p < r->end && (*r->p == 'e' || *r->p == 'E')) {
        integer = false;
        r->p++;
        if (r->p < r->end && (*r->p == '+' || *r->p == '-')) r->p++;
        if (r->p >= r->end || *r->p < '0' || *r->p > '9') return json_fail(r, "bad exponent");
        while (r->p < r->end && *r->p >= '0' && *r->p <= '9') r->p++;
    }
    char text[400];
    size_t n = (size_t)(r->p - s);
    if (n >= sizeof text) n = sizeof text - 1;
    memcpy(text, s, n);
    text[n] = 0;
    double d = strtod(text, NULL);
    if (integer && d >= -2147483648.0 && d <= 2147483647.0) *out = aqua_int((int32_t)d);
    else *out = aqua_double(d);
    return true;
}
static bool literal(json_reader *r, const char *word) {
    size_t n = strlen(word);
    if ((size_t)(r->end - r->p) < n || memcmp(r->p, word, n) != 0) return json_fail(r, "unexpected token");
    r->p += n;
    return true;
}
static bool read_value(json_reader *r, aqua_value *out, int depth) {
    skip_space(r);
    if (r->p >= r->end) return json_fail(r, "unexpected end");
    switch (*r->p) {
    case '{': {
        if (depth >= NET_JSON_DEPTH) return json_fail(r, "depth exceeds 64");
        r->p++;
        aqua_value hash = aqua_hash_new(r->rt, 8);
        skip_space(r);
        if (r->p < r->end && *r->p == '}') { r->p++; *out = hash; return true; }
        for (;;) {
            skip_space(r);
            if (r->p >= r->end || *r->p != '"') return json_fail(r, "expected property name");
            r->p++;
            if (!read_string(r)) return false;
            aqua_value key = aqua_text(r->rt, (const char *)r->text.data, r->text.length);
            skip_space(r);
            if (r->p >= r->end || *r->p != ':') return json_fail(r, "expected ':'");
            r->p++;
            aqua_value item;
            if (!read_value(r, &item, depth + 1)) return false;
            aqua_hash_set(r->rt, hash, key, item);
            skip_space(r);
            if (r->p < r->end && *r->p == ',') { r->p++; continue; }
            if (r->p < r->end && *r->p == '}') { r->p++; *out = hash; return true; }
            return json_fail(r, "expected ',' or '}'");
        }
    }
    case '[': {
        if (depth >= NET_JSON_DEPTH) return json_fail(r, "depth exceeds 64");
        r->p++;
        aqua_value array = aqua_array_new(r->rt, 0);
        skip_space(r);
        if (r->p < r->end && *r->p == ']') { r->p++; *out = array; return true; }
        for (;;) {
            aqua_value item;
            if (!read_value(r, &item, depth + 1)) return false;
            aqua_array_push(r->rt, array, item);
            skip_space(r);
            if (r->p < r->end && *r->p == ',') { r->p++; continue; }
            if (r->p < r->end && *r->p == ']') { r->p++; *out = array; return true; }
            return json_fail(r, "expected ',' or ']'");
        }
    }
    case '"':
        r->p++;
        if (!read_string(r)) return false;
        *out = aqua_text(r->rt, (const char *)r->text.data, r->text.length);
        return true;
    case 't': *out = aqua_bool(true); return literal(r, "true");
    case 'f': *out = aqua_bool(false); return literal(r, "false");
    case 'n': *out = aqua_null(); return literal(r, "null");
    default: return read_number(r, out);
    }
}
bool json_decode(aqua_runtime *rt, const uint8_t *data, size_t length, aqua_value *out, net_error *e) {
    json_reader r = {rt, data, data, data + length, {0}, e};
    if (length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) r.p += 3;
    bool ok = read_value(&r, out, 0);
    if (ok) {
        skip_space(&r);
        if (r.p != r.end) ok = json_fail(&r, "text after the value");
    }
    buffer_free(&r.text);
    return ok;
}
