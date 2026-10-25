/* JSON between language values and UTF-8 text: null, Boolean, finite numbers, strings, arrays and string-keyed
 * hashes, at most APP_MAX_DEPTH containers deep. Parsing is strict RFC 8259 over strictly valid UTF-8; integers that
 * fit 32 bits become integers, other numbers doubles, and a duplicate key keeps its first position with the last value. */
#include "application.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- Writing ---- */

/* The shortest decimal that reads back as `n`, in ECMAScript's Number-to-String layout (1e+21, 1.5e-7, 0.001). */
static void write_number(app_bytes *out, double n) {
    if (n == 0) { app_bytes_text(out, signbit(n) ? "-0" : "0"); return; }
    char buffer[40];
    int precision = 1;
    for (; precision <= 17; precision++) {
        snprintf(buffer, sizeof buffer, "%.*e", precision - 1, n);
        if (strtod(buffer, NULL) == n) break;
    }
    /* buffer: [-]d[.ddd]e[+-]xx */
    char digits[24];
    int count = 0, exponent = 0;
    bool negative = buffer[0] == '-';
    for (const char *p = buffer + negative; *p && *p != 'e'; p++) if (*p >= '0' && *p <= '9') digits[count++] = *p;
    const char *e = strchr(buffer, 'e');
    if (e) exponent = atoi(e + 1);
    while (count > 1 && digits[count - 1] == '0') count--;
    digits[count] = 0;
    int point = exponent + 1; /* digits before the decimal point */
    if (negative) app_bytes_byte(out, '-');
    if (count <= point && point <= 21) {
        app_bytes_append(out, digits, (size_t)count);
        for (int i = count; i < point; i++) app_bytes_byte(out, '0');
    } else if (0 < point && point <= 21) {
        app_bytes_append(out, digits, (size_t)point);
        app_bytes_byte(out, '.');
        app_bytes_append(out, digits + point, (size_t)(count - point));
    } else if (-6 < point && point <= 0) {
        app_bytes_text(out, "0.");
        for (int i = point; i < 0; i++) app_bytes_byte(out, '0');
        app_bytes_append(out, digits, (size_t)count);
    } else {
        app_bytes_byte(out, (uint8_t)digits[0]);
        if (count > 1) { app_bytes_byte(out, '.'); app_bytes_append(out, digits + 1, (size_t)(count - 1)); }
        char tail[16];
        snprintf(tail, sizeof tail, "e%c%d", point - 1 < 0 ? '-' : '+', abs(point - 1));
        app_bytes_text(out, tail);
    }
}

static void write_utf8(app_bytes *out, uint32_t c) {
    uint8_t b[4];
    if (c < 0x80) { b[0] = (uint8_t)c; app_bytes_append(out, b, 1); }
    else if (c < 0x800) { b[0] = (uint8_t)(0xC0 | c >> 6); b[1] = (uint8_t)(0x80 | (c & 63)); app_bytes_append(out, b, 2); }
    else if (c < 0x10000) { b[0] = (uint8_t)(0xE0 | c >> 12); b[1] = (uint8_t)(0x80 | (c >> 6 & 63)); b[2] = (uint8_t)(0x80 | (c & 63)); app_bytes_append(out, b, 3); }
    else { b[0] = (uint8_t)(0xF0 | c >> 18); b[1] = (uint8_t)(0x80 | (c >> 12 & 63)); b[2] = (uint8_t)(0x80 | (c >> 6 & 63)); b[3] = (uint8_t)(0x80 | (c & 63)); app_bytes_append(out, b, 4); }
}

/* A JSON string from UTF-16 units: quotes, backslashes and control characters escaped; an unpaired surrogate is
   written as its \u escape. */
static void write_string(app_bytes *out, const uint16_t *units, size_t count) {
    static const char hex[] = "0123456789abcdef";
    app_bytes_byte(out, '"');
    for (size_t i = 0; i < count; i++) {
        uint32_t c = units[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < count && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            write_utf8(out, 0x10000 + ((c - 0xD800) << 10) + (units[++i] - 0xDC00));
            continue;
        }
        switch (c) {
        case '"': app_bytes_text(out, "\\\""); continue;
        case '\\': app_bytes_text(out, "\\\\"); continue;
        case '\b': app_bytes_text(out, "\\b"); continue;
        case '\f': app_bytes_text(out, "\\f"); continue;
        case '\n': app_bytes_text(out, "\\n"); continue;
        case '\r': app_bytes_text(out, "\\r"); continue;
        case '\t': app_bytes_text(out, "\\t"); continue;
        default: break;
        }
        if (c < 0x20 || (c >= 0xD800 && c <= 0xDFFF)) {
            char escape[7] = {'\\', 'u', hex[c >> 12], hex[c >> 8 & 15], hex[c >> 4 & 15], hex[c & 15], 0};
            app_bytes_text(out, escape);
        } else write_utf8(out, c);
    }
    app_bytes_byte(out, '"');
}

static bool write_text_value(aqua_runtime *rt, aqua_value text, app_bytes *out) {
    size_t count = 0;
    uint16_t *units = app_text16(rt, text, &count);
    write_string(out, units, count);
    aqua_free(units);
    return true;
}

static bool write_value(aqua_runtime *rt, aqua_value v, app_bytes *out, int depth, app_error *e) {
    switch (v.type) {
    case AQUA_NULL: case AQUA_VOID: app_bytes_text(out, "null"); return true;
    case AQUA_BOOL: app_bytes_text(out, v.number != 0 ? "true" : "false"); return true;
    case AQUA_INT: case AQUA_FLOAT: case AQUA_DOUBLE:
        if (!isfinite(v.number)) return app_error_set(e, APP_INVALID_DATA, "Expected a finite number.");
        write_number(out, v.number);
        return true;
    case AQUA_STRING: return write_text_value(rt, v, out);
    case AQUA_ARRAY: case AQUA_HASH: {
        if (depth >= APP_MAX_DEPTH) return app_error_set(e, APP_LIMIT_EXCEEDED, "JSON depth exceeds 64 (or contains a cycle).");
        bool array = v.type == AQUA_ARRAY;
        uint32_t count = array ? aqua_array_length(rt, v) : aqua_hash_count(rt, v);
        app_bytes_byte(out, array ? '[' : '{');
        for (uint32_t i = 0; i < count; i++) {
            if (i) app_bytes_byte(out, ',');
            aqua_value item;
            if (array) item = aqua_array_get(rt, v, i);
            else {
                aqua_value key;
                aqua_hash_entry(rt, v, i, &key, &item);
                if (key.type != AQUA_STRING) return app_error_set(e, APP_INVALID_DATA, "Expected a string.");
                write_text_value(rt, key, out);
                app_bytes_byte(out, ':');
            }
            if (!write_value(rt, item, out, depth + 1, e)) return false;
            if (out->length > APP_MAX_BYTES) return app_check_bytes(out->length, e);
        }
        app_bytes_byte(out, array ? ']' : '}');
        return true;
    }
    default:
        return app_error_set(e, APP_UNSUPPORTED, "JSON supports null, Boolean, finite numbers, strings, arrays and string-keyed hashes.");
    }
}

bool app_json_write(aqua_runtime *rt, aqua_value value, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    if (!write_value(rt, value, out, 0, e) || !app_check_bytes(out->length, e)) { app_bytes_free(out); return false; }
    return true;
}

/* ---- Parsing ---- */

typedef struct parser {
    aqua_runtime *rt;
    const uint8_t *p, *start, *end;
    uint16_t *units; size_t unit_count, unit_capacity; /* string scratch */
    app_error *e;
} parser;

static bool parse_fail(parser *s, const char *reason) {
    size_t line = 0, column = 0;
    for (const uint8_t *q = s->start; q < s->p && q < s->end; q++) { if (*q == '\n') { line++; column = 0; } else column++; }
    return app_error_set(s->e, APP_INVALID_DATA, "%s LineNumber: %zu | BytePositionInLine: %zu.", reason, line, column);
}

static void skip_space(parser *s) { while (s->p < s->end && (*s->p == ' ' || *s->p == '\t' || *s->p == '\n' || *s->p == '\r')) s->p++; }

static void push_unit(parser *s, uint16_t u) {
    if (s->unit_count == s->unit_capacity) {
        s->unit_capacity = s->unit_capacity ? s->unit_capacity * 2 : 64;
        s->units = realloc(s->units, s->unit_capacity * sizeof *s->units);
    }
    s->units[s->unit_count++] = u;
}

static int hex_digit(uint8_t c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

/* Decodes one strictly valid UTF-8 sequence at s->p; returns the scalar or -1. */
static int32_t next_scalar(parser *s) {
    const uint8_t *p = s->p;
    size_t left = (size_t)(s->end - p);
    uint8_t c = p[0];
    if (c < 0x80) { s->p++; return c; }
    int length = c >= 0xC2 && c <= 0xDF ? 2 : c >= 0xE0 && c <= 0xEF ? 3 : c >= 0xF0 && c <= 0xF4 ? 4 : 0;
    if (!length || left < (size_t)length) return -1;
    for (int i = 1; i < length; i++) if ((p[i] & 0xC0) != 0x80) return -1;
    int32_t v;
    if (length == 2) v = (c & 0x1F) << 6 | (p[1] & 0x3F);
    else if (length == 3) {
        v = (c & 0x0F) << 12 | (p[1] & 0x3F) << 6 | (p[2] & 0x3F);
        if (v < 0x800 || (v >= 0xD800 && v <= 0xDFFF)) return -1;
    } else {
        v = (c & 0x07) << 18 | (p[1] & 0x3F) << 12 | (p[2] & 0x3F) << 6 | (p[3] & 0x3F);
        if (v < 0x10000 || v > 0x10FFFF) return -1;
    }
    s->p += length;
    return v;
}

static bool parse_string(parser *s, aqua_value *out) {
    s->p++; /* opening quote */
    s->unit_count = 0;
    for (;;) {
        if (s->p >= s->end) return parse_fail(s, "Expected end of string, but instead reached end of data.");
        uint8_t c = *s->p;
        if (c == '"') { s->p++; break; }
        if (c < 0x20) return parse_fail(s, "Control characters are not allowed in JSON strings.");
        if (c == '\\') {
            if (s->p + 1 >= s->end) return parse_fail(s, "Expected end of string, but instead reached end of data.");
            uint8_t k = s->p[1];
            s->p += 2;
            switch (k) {
            case '"': push_unit(s, '"'); break;
            case '\\': push_unit(s, '\\'); break;
            case '/': push_unit(s, '/'); break;
            case 'b': push_unit(s, '\b'); break;
            case 'f': push_unit(s, '\f'); break;
            case 'n': push_unit(s, '\n'); break;
            case 'r': push_unit(s, '\r'); break;
            case 't': push_unit(s, '\t'); break;
            case 'u': {
                if (s->end - s->p < 4) return parse_fail(s, "Invalid \\u escape in a JSON string.");
                int v = 0;
                for (int i = 0; i < 4; i++) {
                    int d = hex_digit(s->p[i]);
                    if (d < 0) return parse_fail(s, "Invalid \\u escape in a JSON string.");
                    v = v * 16 + d;
                }
                s->p += 4;
                push_unit(s, (uint16_t)v);
                break;
            }
            default: s->p -= 1; return parse_fail(s, "Invalid escape in a JSON string.");
            }
            continue;
        }
        int32_t scalar = next_scalar(s);
        if (scalar < 0) return parse_fail(s, "Invalid UTF-8 in JSON text.");
        if (scalar >= 0x10000) {
            push_unit(s, (uint16_t)(0xD800 + ((scalar - 0x10000) >> 10)));
            push_unit(s, (uint16_t)(0xDC00 + ((scalar - 0x10000) & 0x3FF)));
        } else push_unit(s, (uint16_t)scalar);
    }
    *out = aqua_text16(s->rt, s->units, s->unit_count);
    return true;
}

static bool parse_number(parser *s, aqua_value *out) {
    const uint8_t *begin = s->p;
    bool integral = true;
    if (s->p < s->end && *s->p == '-') s->p++;
    if (s->p >= s->end || !(*s->p >= '0' && *s->p <= '9')) return parse_fail(s, "Invalid number in JSON text.");
    if (*s->p == '0') s->p++;
    else while (s->p < s->end && *s->p >= '0' && *s->p <= '9') s->p++;
    if (s->p < s->end && *s->p == '.') {
        integral = false;
        s->p++;
        if (s->p >= s->end || !(*s->p >= '0' && *s->p <= '9')) return parse_fail(s, "Invalid number in JSON text.");
        while (s->p < s->end && *s->p >= '0' && *s->p <= '9') s->p++;
    }
    if (s->p < s->end && (*s->p == 'e' || *s->p == 'E')) {
        integral = false;
        s->p++;
        if (s->p < s->end && (*s->p == '+' || *s->p == '-')) s->p++;
        if (s->p >= s->end || !(*s->p >= '0' && *s->p <= '9')) return parse_fail(s, "Invalid number in JSON text.");
        while (s->p < s->end && *s->p >= '0' && *s->p <= '9') s->p++;
    }
    char buffer[64];
    size_t length = (size_t)(s->p - begin);
    char *text = length < sizeof buffer ? buffer : malloc(length + 1);
    memcpy(text, begin, length);
    text[length] = 0;
    double n = strtod(text, NULL);
    if (text != buffer) free(text);
    if (!isfinite(n)) return app_error_set(s->e, APP_INVALID_DATA, "JSON numbers must be finite.");
    *out = integral && n >= -2147483648.0 && n <= 2147483647.0 ? aqua_int((int32_t)n) : aqua_double(n);
    return true;
}

static bool literal(parser *s, const char *word) {
    size_t n = strlen(word);
    if ((size_t)(s->end - s->p) < n || memcmp(s->p, word, n) != 0) return false;
    s->p += n;
    return true;
}

static bool parse_value(parser *s, aqua_value *out, int depth) {
    skip_space(s);
    if (s->p >= s->end) return parse_fail(s, "Expected a JSON value, but reached the end of data.");
    uint8_t c = *s->p;
    if (c == '"') return parse_string(s, out);
    if (c == '-' || (c >= '0' && c <= '9')) return parse_number(s, out);
    if (literal(s, "true")) { *out = aqua_bool(true); return true; }
    if (literal(s, "false")) { *out = aqua_bool(false); return true; }
    if (literal(s, "null")) { *out = aqua_null(); return true; }
    if (c != '[' && c != '{') {
        char reason[96];
        if (c >= 0x20 && c < 0x7F) snprintf(reason, sizeof reason, "'%c' is an invalid start of a value.", c);
        else snprintf(reason, sizeof reason, "'0x%02X' is an invalid start of a value.", c);
        return parse_fail(s, reason);
    }
    if (depth >= APP_MAX_DEPTH) return app_error_set(s->e, APP_INVALID_DATA, "The JSON value's depth exceeds the maximum depth of 64.");
    s->p++;
    if (c == '[') {
        aqua_value array = aqua_array_new(s->rt, 0);
        skip_space(s);
        if (s->p < s->end && *s->p == ']') { s->p++; *out = array; return true; }
        for (;;) {
            aqua_value item;
            if (!parse_value(s, &item, depth + 1)) return false;
            aqua_array_push(s->rt, array, item);
            skip_space(s);
            if (s->p < s->end && *s->p == ',') { s->p++; continue; }
            if (s->p < s->end && *s->p == ']') { s->p++; break; }
            return parse_fail(s, "Expected ',' or ']' after an array item.");
        }
        *out = array;
        return true;
    }
    aqua_value hash = aqua_hash_new(s->rt, 0);
    skip_space(s);
    if (s->p < s->end && *s->p == '}') { s->p++; *out = hash; return true; }
    for (;;) {
        skip_space(s);
        if (s->p >= s->end || *s->p != '"') return parse_fail(s, "Expected a property name in double quotes.");
        aqua_value key, item;
        if (!parse_string(s, &key)) return false;
        skip_space(s);
        if (s->p >= s->end || *s->p != ':') return parse_fail(s, "Expected ':' after a property name.");
        s->p++;
        if (!parse_value(s, &item, depth + 1)) return false;
        aqua_hash_set(s->rt, hash, key, item);
        skip_space(s);
        if (s->p < s->end && *s->p == ',') { s->p++; continue; }
        if (s->p < s->end && *s->p == '}') { s->p++; break; }
        return parse_fail(s, "Expected ',' or '}' after a property value.");
    }
    *out = hash;
    return true;
}

bool app_json_parse(aqua_runtime *rt, const uint8_t *text, size_t length, aqua_value *out, app_error *e) {
    if (!app_check_bytes(length, e)) return false;
    parser s = {rt, text, text, text + length, NULL, 0, 0, e};
    bool ok = parse_value(&s, out, 0);
    if (ok) {
        skip_space(&s);
        if (s.p != s.end) ok = parse_fail(&s, "The JSON text contains data after its value.");
    }
    free(s.units);
    return ok;
}
