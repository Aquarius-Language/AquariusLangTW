/* Processing's time and date functions and its text helpers: number formatting (nf, nfc, nfs, nfp), hex and binary
   conversion, str, trim, split, splitTokens and join. */
#include "processing.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* ---- Time and date (local time) ---- */

static void date_field(aqua_call *call, int which) {
    time_t now = time(NULL);
    struct tm t = *localtime(&now); /* sketches run on the runtime's thread */
    int values[6] = {t.tm_year + 1900, t.tm_mon + 1, t.tm_mday, t.tm_hour, t.tm_min, t.tm_sec};
    aqua_return_int(call, values[which]);
}
static void p_year(aqua_call *call, processing *p) { (void)p; date_field(call, 0); }
static void p_month(aqua_call *call, processing *p) { (void)p; date_field(call, 1); }
static void p_day(aqua_call *call, processing *p) { (void)p; date_field(call, 2); }
static void p_hour(aqua_call *call, processing *p) { (void)p; date_field(call, 3); }
static void p_minute(aqua_call *call, processing *p) { (void)p; date_field(call, 4); }
static void p_second(aqua_call *call, processing *p) { (void)p; date_field(call, 5); }

/* ---- Number formatting ---- */

/* Formats |n| with at least `left` integer digits (0: no padding) and `right` decimals (< 0: an integer), optionally
   grouping thousands, after `sign` ("" for negatives only, " " or "+" before positives). */
static void format_number(char *out, size_t size, double n, int left, int right, bool commas, const char *sign) {
    char digits[400];
    double magnitude = fabs(n);
    if (right < 0) snprintf(digits, sizeof digits, "%.0f", trunc(magnitude));
    else snprintf(digits, sizeof digits, "%.*f", right, magnitude);
    char *point = strchr(digits, '.');
    size_t integer = point ? (size_t)(point - digits) : strlen(digits);
    char body[1024];
    size_t b = 0;
    for (int pad = left - (int)integer; pad > 0 && b < 300; pad--) body[b++] = '0';
    for (size_t i = 0; i < integer && b < sizeof body - 2; i++) {
        body[b++] = digits[i];
        size_t remaining = integer - i - 1;
        if (commas && remaining && remaining % 3 == 0) body[b++] = ',';
    }
    if (point) for (const char *q = point; *q && b < sizeof body - 1; q++) body[b++] = *q;
    body[b] = 0;
    bool negative = n < 0 && strspn(body, "0.,") != strlen(body);
    snprintf(out, size, "%s%s", negative ? "-" : sign, body);
}

/* nf(num), nf(num, digits) (an integer) and nf(num, left, right). */
static bool format_arguments(aqua_call *call, double *n, int *left, int *right) {
    *n = gfx_number(call, 0);
    uint32_t count = aqua_argc(call);
    *left = count >= 2 ? gfx_int(call, 1) : 0;
    *right = count == 3 ? gfx_int(call, 2) : -1;
    if (aqua_failed(call)) return false;
    if (*left < 0 || *left > 300 || *right > 100) { gfx_fail(call, "Digits must be 0..300 (left) and at most 100 (right)."); return false; }
    return true;
}

static void nf_family(aqua_call *call, const char *sign, bool commas) {
    double n;
    int left, right;
    if (!format_arguments(call, &n, &left, &right)) return;
    /* nf(float) without digits keeps the number's own decimals. */
    if (right < 0 && n != trunc(n) && aqua_argc(call) == 1) {
        char text[64];
        snprintf(text, sizeof text, "%.15g", fabs(n));
        const char *point = strchr(text, '.');
        right = point ? (int)strlen(point + 1) : 0;
    }
    char out[1024];
    format_number(out, sizeof out, n, left, right, commas, sign);
    aqua_return_cstring(call, out);
}
static void p_nf(aqua_call *call, processing *p) { (void)p; nf_family(call, "", false); }
static void p_nfs(aqua_call *call, processing *p) { (void)p; nf_family(call, " ", false); }
static void p_nfp(aqua_call *call, processing *p) { (void)p; nf_family(call, "+", false); }
/* nfc(num[, right]): thousands separators, no integer padding. */
static void p_nfc(aqua_call *call, processing *p) {
    (void)p;
    double n = gfx_number(call, 0);
    int right = aqua_argc(call) == 2 ? gfx_int(call, 1) : -1;
    if (aqua_failed(call)) return;
    if (right > 100) { gfx_fail(call, "Digits must be at most 100."); return; }
    if (right < 0 && n != trunc(n)) {
        char text[64];
        snprintf(text, sizeof text, "%.15g", fabs(n));
        const char *point = strchr(text, '.');
        right = point ? (int)strlen(point + 1) : 0;
    }
    char out[1024];
    format_number(out, sizeof out, n, 0, right, true, "");
    aqua_return_cstring(call, out);
}

/* hex(n[, digits]) and binary(n[, digits]) of the number's 32-bit pattern; unhex/unbinary parse them back. */
static void radix_text(aqua_call *call, int bits_per_digit, int default_digits) {
    double n = gfx_number(call, 0);
    int digits = aqua_argc(call) == 2 ? gfx_int(call, 1) : default_digits;
    if (aqua_failed(call)) return;
    if (n != trunc(n) || n < -2147483648.0 || n > 4294967295.0) { gfx_fail(call, "Expected an integer in -2147483648..4294967295."); return; }
    if (digits < 0 || digits > 32) { gfx_fail(call, "Digits must be 0..32."); return; }
    uint32_t value = n < 0 ? (uint32_t)(int32_t)n : (uint32_t)n;
    char out[40];
    int length = 32 / bits_per_digit;
    for (int i = 0; i < length; i++) out[i] = "0123456789ABCDEF"[(value >> ((length - 1 - i) * bits_per_digit)) & ((1u << bits_per_digit) - 1)];
    out[length] = 0;
    const char *start = out;
    if (digits == 0) { while (*start == '0' && start[1]) start++; }
    else start = out + (length > digits ? length - digits : 0);
    aqua_return_cstring(call, start);
}
static void p_hex(aqua_call *call, processing *p) { (void)p; radix_text(call, 4, 8); }
static void p_binary(aqua_call *call, processing *p) { (void)p; radix_text(call, 1, 0); }
static void parse_radix(aqua_call *call, int radix) {
    aqua_str s = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    char *end;
    unsigned long long v = strtoull(s.data, &end, radix);
    if (end == s.data || *end || s.length > 32) { gfx_fail(call, radix == 16 ? "Expected hexadecimal digits." : "Expected binary digits."); return; }
    aqua_return_double(call, (double)(uint32_t)v);
}
static void p_unhex(aqua_call *call, processing *p) { (void)p; parse_radix(call, 16); }
static void p_unbinary(aqua_call *call, processing *p) { (void)p; parse_radix(call, 2); }

/* ---- Strings ---- */

static void p_str(aqua_call *call, processing *p) {
    aqua_value v = aqua_arg(call, 0);
    if (v.type == AQUA_STRING) { aqua_return(call, v); return; }
    char *text = aqua_display(p->rt, v);
    aqua_return_cstring(call, text);
    aqua_free(text);
}

/* Whitespace as Processing's trim() sees it, including no-break spaces. */
static size_t space_at(const char *s, size_t length, size_t i) {
    unsigned char c = (unsigned char)s[i];
    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v') return 1;
    if (c == 0xC2 && i + 1 < length && (unsigned char)s[i + 1] == 0xA0) return 2;
    return 0;
}
static void p_trim(aqua_call *call, processing *p) {
    (void)p;
    aqua_str s = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    size_t start = 0, end = s.length, k;
    while (start < end && (k = space_at(s.data, s.length, start))) start += k;
    while (end > start) {
        if (space_at(s.data, s.length, end - 1) == 1) { end--; continue; }
        if (end >= start + 2 && (unsigned char)s.data[end - 2] == 0xC2 && (unsigned char)s.data[end - 1] == 0xA0) { end -= 2; continue; }
        break;
    }
    char *out = aqua_scratch(call, end - start + 1);
    memcpy(out, s.data + start, end - start);
    out[end - start] = 0;
    aqua_return_cstring(call, out);
}

static void push_piece(aqua_runtime *rt, aqua_value array, const char *s, size_t length) {
    char small[256], *copy = length < sizeof small ? small : malloc(length + 1);
    memcpy(copy, s, length);
    copy[length] = 0;
    aqua_array_push(rt, array, aqua_cstring(rt, copy));
    if (copy != small) free(copy);
}

/* split(text, delimiter): every occurrence of the delimiter separates pieces (empty pieces kept). */
static void p_split(aqua_call *call, processing *p) {
    aqua_str s = gfx_text(call, 0), d = gfx_text(call, 1);
    if (aqua_failed(call)) return;
    if (!d.length) { gfx_fail(call, "The delimiter must not be empty."); return; }
    aqua_value array = aqua_array_new(p->rt, 0);
    size_t start = 0;
    for (size_t i = 0; i + d.length <= s.length;) {
        if (memcmp(s.data + i, d.data, d.length) == 0) { push_piece(p->rt, array, s.data + start, i - start); i += d.length; start = i; }
        else i++;
    }
    push_piece(p->rt, array, s.data + start, s.length - start);
    aqua_return(call, array);
}

/* splitTokens(text[, tokens]): runs of any token character (whitespace by default) separate pieces. */
static void p_splitTokens(aqua_call *call, processing *p) {
    aqua_str s = gfx_text(call, 0);
    aqua_str tokens = aqua_argc(call) == 2 ? gfx_text(call, 1) : (aqua_str){" \t\n\r\f\v", 6};
    if (aqua_failed(call)) return;
    aqua_value array = aqua_array_new(p->rt, 0);
    size_t start = 0;
    bool in_piece = false;
    for (size_t i = 0; i <= s.length; i++) {
        bool separator = i == s.length || memchr(tokens.data, s.data[i], tokens.length) != NULL;
        if (separator && in_piece) { push_piece(p->rt, array, s.data + start, i - start); in_piece = false; }
        else if (!separator && !in_piece) { start = i; in_piece = true; }
    }
    aqua_return(call, array);
}

/* join(array, separator): the items' text, as print shows them. */
static void p_join(aqua_call *call, processing *p) {
    aqua_value array = gfx_array(call, 0);
    aqua_str separator = gfx_text(call, 1);
    if (aqua_failed(call)) return;
    uint32_t n = aqua_array_length(p->rt, array);
    size_t length = 0, capacity = 256;
    char *out = malloc(capacity);
    for (uint32_t i = 0; i < n; i++) {
        aqua_value item = aqua_array_get(p->rt, array, i);
        char *text = item.type == AQUA_STRING ? aqua_text_dup(p->rt, item, NULL) : aqua_display(p->rt, item);
        size_t t = strlen(text), extra = t + (i ? separator.length : 0);
        while (length + extra + 1 > capacity) { capacity *= 2; out = realloc(out, capacity); }
        if (i) { memcpy(out + length, separator.data, separator.length); length += separator.length; }
        memcpy(out + length, text, t);
        length += t;
        aqua_free(text);
    }
    out[length] = 0;
    aqua_return_cstring(call, out);
    free(out);
}

#define DATA(X) \
    X(year, "year|年", 0, 0) X(month, "month|月", 0, 0) X(day, "day|日", 0, 0) X(hour, "hour|時", 0, 0) X(minute, "minute|分", 0, 0) \
    X(second, "second|秒", 0, 0) X(nf, "nf|格式化數字", 1, 3) X(nfs, "nfs|格式化數字空格", 1, 3) X(nfp, "nfp|格式化數字正號", 1, 3) \
    X(nfc, "nfc|格式化數字千分位", 1, 2) X(hex, "hex|十六進位", 1, 2) X(unhex, "unhex|解析十六進位", 1, 1) X(binary, "binary|二進位", 1, 2) \
    X(unbinary, "unbinary|解析二進位", 1, 1) X(str, "str|轉文字", 1, 1) X(trim, "trim|修剪空白", 1, 1) X(split, "split|分割", 2, 2) \
    X(splitTokens, "splitTokens|分割記號", 1, 2) X(join, "join|連接", 2, 2)
DATA(MODULE_WRAPPER)

void gfx_data_register(aqua_class *module) { DATA(MODULE_DEFINE) }
