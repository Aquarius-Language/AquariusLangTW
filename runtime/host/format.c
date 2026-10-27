/* Display text of values, as print/印出 and the result of a script show them on every platform. Numbers use the
   shortest text that reads back to the same value; scientific notation (1E+17, 1E-05) starts at the same exponents as
   the language's earlier .NET host: 10^17 for doubles, 10^9 for floats, and below 10^-4. */
#include "host.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct text_builder { char *data; size_t length, capacity; } text_builder;
static void put(text_builder *b, const char *s, size_t n) {
    if (b->length + n + 1 > b->capacity) {
        b->capacity = (b->length + n + 1) * 2;
        b->data = realloc(b->data, b->capacity);
    }
    memcpy(b->data + b->length, s, n);
    b->length += n;
    b->data[b->length] = 0;
}
static void puts_(text_builder *b, const char *s) { put(b, s, strlen(s)); }

/* Shortest round-trip digits of |n| (no leading/trailing zeros) and the exponent of the first digit. */
static int shortest_digits(double n, bool single, char *digits, int *exponent) {
    char buffer[40];
    int limit = single ? 9 : 17;
    for (int precision = 1; precision <= limit; precision++) {
        snprintf(buffer, sizeof buffer, "%.*e", precision - 1, n);
        bool exact = single ? (float)strtod(buffer, NULL) == (float)n : strtod(buffer, NULL) == n;
        if (exact || precision == limit) break;
    }
    /* buffer is d[.ddd]e±x */
    int count = 0;
    char *p = buffer;
    for (; *p && *p != 'e'; p++) if (*p >= '0' && *p <= '9') digits[count++] = *p;
    *exponent = atoi(p + 1);
    while (count > 1 && digits[count - 1] == '0') count--;
    digits[count] = 0;
    return count;
}

void format_number(char *out, size_t size, double n, aqua_type type) {
    if (type == AQUA_INT) { snprintf(out, size, "%d", (int32_t)n); return; }
    if (isnan(n)) { snprintf(out, size, "NaN"); return; }
    if (isinf(n)) { snprintf(out, size, n < 0 ? "-Infinity" : "Infinity"); return; }
    if (n == 0) { snprintf(out, size, signbit(n) ? "-0" : "0"); return; }
    bool single = type == AQUA_FLOAT;
    char digits[24]; int exponent;
    int count = shortest_digits(fabs(n), single, digits, &exponent);
    text_builder b = {0};
    if (n < 0) puts_(&b, "-");
    if (exponent >= (single ? 9 : 17) || exponent < -4) {
        put(&b, digits, 1);
        if (count > 1) { puts_(&b, "."); put(&b, digits + 1, (size_t)count - 1); }
        char tail[8];
        snprintf(tail, sizeof tail, "E%c%02d", exponent < 0 ? '-' : '+', exponent < 0 ? -exponent : exponent);
        puts_(&b, tail);
    } else if (exponent < 0) {
        puts_(&b, "0.");
        for (int i = -1; i > exponent; i--) puts_(&b, "0");
        put(&b, digits, (size_t)count);
    } else {
        int whole = exponent + 1;
        if (count <= whole) { put(&b, digits, (size_t)count); for (int i = count; i < whole; i++) puts_(&b, "0"); }
        else { put(&b, digits, (size_t)whole); puts_(&b, "."); put(&b, digits + whole, (size_t)(count - whole)); }
    }
    snprintf(out, size, "%s", b.data);
    free(b.data);
}

static void format_into(aqua_runtime *rt, text_builder *b, aqua_value v, int depth) {
    char number[64];
    switch (v.type) {
    case AQUA_INT: case AQUA_FLOAT: case AQUA_DOUBLE:
        format_number(number, sizeof number, v.number, (aqua_type)v.type);
        puts_(b, number);
        return;
    case AQUA_BOOL: puts_(b, v.number != 0 ? "真" : "假"); return;
    case AQUA_NULL: puts_(b, "null"); return;
    case AQUA_VOID: return;
    case AQUA_BREAK: puts_(b, "中斷"); return;
    case AQUA_STRING: case AQUA_ERROR: {
        if (v.type == AQUA_ERROR) puts_(b, "ERROR: ");
        size_t length;
        char *text = guest_text_utf8(rt, v.ref, &length);
        put(b, text, length);
        free(text);
        return;
    }
    case AQUA_MODULE: puts_(b, "module"); return;
    case AQUA_STRUCT: case AQUA_STRUCT_TYPE: {
        /* 節點{左: null, 值: 3} for a value, 結構 節點 for its type: names come from the type in guest memory. */
        uint32_t shape = v.type == AQUA_STRUCT ? guest_u32(rt, v.ref + G_Struct_Shape) : v.ref;
        size_t length;
        char *name = guest_text_utf8(rt, guest_u32(rt, shape + G_Shape_Name), &length);
        if (v.type == AQUA_STRUCT_TYPE) puts_(b, "結構 ");
        put(b, name, length);
        free(name);
        if (v.type == AQUA_STRUCT_TYPE) return;
        if (depth > 64) { puts_(b, "{...}"); return; }
        uint32_t count = guest_u32(rt, v.ref + G_Struct_Count);
        puts_(b, "{");
        for (uint32_t i = 0; i < count; i++) {
            if (i) puts_(b, ", ");
            char *field = guest_text_utf8(rt, guest_u32(rt, shape + G_Shape_Fields + i * G_ShapeField_Size + G_ShapeField_Name), &length);
            put(b, field, length);
            free(field);
            puts_(b, ": ");
            format_into(rt, b, guest_read_value(rt, v.ref + G_Struct_Fields + i * G_Value_Size), depth + 1);
        }
        puts_(b, "}");
        return;
    }
    case AQUA_BUILTIN: puts_(b, "builtin function"); return;
    case AQUA_FUNCTION: {
        /* 函式(a, b) {\n...\n}: parameter names come from the function's definition in program memory. */
        uint32_t program = guest_u32(rt, v.ref + G_Closure_Program), index = guest_u32(rt, v.ref + G_Closure_Function);
        uint32_t function = guest_u32(rt, program + G_Program_Functions) + index * G_Function_Size;
        uint32_t arity = guest_u32(rt, function + G_Function_Arity), names = guest_u32(rt, function + G_Function_Parameters);
        puts_(b, "函式(");
        for (uint32_t i = 0; i < arity; i++) {
            if (i) puts_(b, ", ");
            size_t length;
            char *name = guest_text_utf8(rt, guest_u32(rt, names + i * 4), &length);
            put(b, name, length);
            free(name);
        }
        puts_(b, ") {\n...\n}");
        return;
    }
    case AQUA_ARRAY: {
        if (depth > 64) { puts_(b, "[...]"); return; }
        uint32_t count = aqua_array_length(rt, v);
        puts_(b, "[");
        for (uint32_t i = 0; i < count; i++) {
            if (i) puts_(b, ", ");
            format_into(rt, b, aqua_array_get(rt, v, i), depth + 1);
        }
        puts_(b, "]");
        return;
    }
    case AQUA_HASH: {
        if (depth > 64) { puts_(b, "{...}"); return; }
        uint32_t count = aqua_hash_count(rt, v);
        puts_(b, "{");
        for (uint32_t i = 0; i < count; i++) {
            aqua_value key, item;
            aqua_hash_entry(rt, v, i, &key, &item);
            if (i) puts_(b, ", ");
            format_into(rt, b, key, depth + 1);
            puts_(b, ": ");
            format_into(rt, b, item, depth + 1);
        }
        puts_(b, "}");
        return;
    }
    case AQUA_NATIVE: {
        handle *h = handle_get(rt, v.ref);
        if (!h || h->kind == HANDLE_BOUND) { puts_(b, "builtin function"); return; }
        if (h->kind == HANDLE_MODULE) { puts_(b, "module"); return; }
        char name[96];
        puts_(b, names_first(h->cls->names, name, sizeof name));
        return;
    }
    default: puts_(b, "unknown"); return;
    }
}

char *format_value(aqua_runtime *rt, aqua_value v) {
    text_builder b = {0};
    put(&b, "", 0);
    format_into(rt, &b, v, 0);
    return b.data;
}
