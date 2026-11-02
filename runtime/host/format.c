/* Display text of values, as print/印出 and the result of a script show them on every platform. Numbers are formatted
   by the guest runtime's decimal.h (exact shortest digits; scientific notation such as 1E+17 and 1E-05 from the same
   exponents as the language's earlier .NET host), so 印出 and String.From agree on every target. */
#include "host.h"
#include "../guest/decimal.h"
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

void format_number(char *out, size_t size, double n, aqua_type type) {
    if (type == AQUA_INT) { snprintf(out, size, "%d", (int32_t)n); return; }
    char text[32];
    int length = format_number_text(text, n, type == AQUA_FLOAT);
    snprintf(out, size, "%.*s", length, text);
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
