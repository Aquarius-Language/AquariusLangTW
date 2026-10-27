/* The value half of aqua.h: text, arrays, hashes, native objects and references, reading and writing guest memory in
   place. The host keeps no copies ("mirrors") of guest data. */
#include "host.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

char *utf16_to_utf8(const uint16_t *units, size_t count, size_t *length);
size_t utf8_to_utf16(const char *text, size_t length, uint16_t *out);

void *aqua_alloc(size_t bytes) { return malloc(bytes ? bytes : 1); }
void *aqua_realloc(void *pointer, size_t bytes) { return realloc(pointer, bytes ? bytes : 1); }
void aqua_free(void *pointer) { free(pointer); }

const char *aqua_type_name(aqua_value v) {
    switch (v.type) {
    case AQUA_INT: return "integer"; case AQUA_FLOAT: return "float"; case AQUA_DOUBLE: return "double";
    case AQUA_STRING: return "string"; case AQUA_BOOL: return "boolean"; case AQUA_ARRAY: return "array";
    case AQUA_HASH: return "hash"; case AQUA_FUNCTION: case AQUA_BUILTIN: return "function"; case AQUA_MODULE: return "module";
    case AQUA_ERROR: return "error"; case AQUA_NATIVE: return "native";
    case AQUA_STRUCT: return "struct"; case AQUA_STRUCT_TYPE: return "type";
    default: return "null";
    }
}

/* ---- Text ---- */

aqua_value aqua_text16(aqua_runtime *rt, const uint16_t *units, size_t count) {
    uint32_t text = guest(rt, GUEST_text, (uint32_t)count, 0, 0, 0);
    memcpy(guest_base(rt) + text + G_Text_Data, units, count * 2);
    aqua_value v = {AQUA_STRING, text, 0};
    return v;
}
aqua_value aqua_text(aqua_runtime *rt, const char *utf8, size_t length) {
    uint16_t small[256];
    uint16_t *units = length <= 256 ? small : malloc(length * 2);
    size_t count = utf8_to_utf16(utf8, length, units);
    aqua_value v = aqua_text16(rt, units, count);
    if (units != small) free(units);
    return v;
}
aqua_value aqua_cstring(aqua_runtime *rt, const char *utf8) { return aqua_text(rt, utf8, strlen(utf8)); }

char *aqua_text_dup(aqua_runtime *rt, aqua_value text, size_t *length) {
    if (text.type != AQUA_STRING && text.type != AQUA_ERROR) return NULL;
    return guest_text_utf8(rt, text.ref, length);
}
size_t aqua_text16_length(aqua_runtime *rt, aqua_value text) {
    return text.type == AQUA_STRING ? guest_u32(rt, text.ref + G_Text_Length) : 0;
}
const uint16_t *aqua_text16_units(aqua_runtime *rt, aqua_value text) {
    return text.type == AQUA_STRING ? (const uint16_t *)(guest_base(rt) + text.ref + G_Text_Data) : NULL;
}
char *aqua_display(aqua_runtime *rt, aqua_value v) { return format_value(rt, v); }

static bool text_equal(aqua_runtime *rt, uint32_t a, uint32_t b) {
    if (a == b) return true;
    uint32_t n = guest_u32(rt, a);
    return n == guest_u32(rt, b) && memcmp(guest_base(rt) + a + G_Text_Data, guest_base(rt) + b + G_Text_Data, (size_t)n * 2) == 0;
}
/* The language's == (typed = false) or a hash key match (typed = true: numeric tags must agree and NaN equals NaN). */
static bool values_equal(aqua_runtime *rt, aqua_value a, aqua_value b, bool typed) {
    if (aqua_is_number(a) && aqua_is_number(b))
        return (!typed || a.type == b.type) && (a.number == b.number || (typed && isnan(a.number) && isnan(b.number)));
    if (a.type != b.type) return false;
    if (a.type == AQUA_STRING) return text_equal(rt, a.ref, b.ref);
    if (a.type == AQUA_BOOL) return a.number == b.number;
    return a.ref == b.ref;
}
bool aqua_equal(aqua_runtime *rt, aqua_value a, aqua_value b) { return values_equal(rt, a, b, false); }

/* ---- Arrays ---- */

static uint32_t array_items(aqua_runtime *rt, aqua_value array) { return guest_u32(rt, array.ref + G_Array_Values); }

aqua_value aqua_array_new(aqua_runtime *rt, uint32_t length) {
    uint32_t array = guest(rt, GUEST_array, length, 0, 0, 0);
    /* The runtime leaves fresh element storage for its constructor to fill. */
    memset(guest_base(rt) + guest_u32(rt, array + G_Array_Values), 0, (size_t)length * G_Value_Size);
    aqua_value v = {AQUA_ARRAY, array, 0};
    return v;
}
uint32_t aqua_array_length(aqua_runtime *rt, aqua_value array) {
    return array.type == AQUA_ARRAY ? guest_u32(rt, array.ref + G_Array_Count) : 0;
}
aqua_value aqua_array_get(aqua_runtime *rt, aqua_value array, uint32_t index) {
    if (array.type != AQUA_ARRAY || index >= guest_u32(rt, array.ref + G_Array_Count)) return aqua_null();
    return guest_read_value(rt, array_items(rt, array) + index * G_Value_Size);
}
/* Element storage that may be written: detaches storage shared with other arrays (functional push). */
static uint32_t writable_items(aqua_runtime *rt, aqua_value array) { return guest(rt, GUEST_array_values, array.ref, 0, 0, 0); }

bool aqua_array_set(aqua_runtime *rt, aqua_value array, uint32_t index, aqua_value item) {
    if (array.type != AQUA_ARRAY || index >= guest_u32(rt, array.ref + G_Array_Count)) return false;
    guest_write_value(rt, writable_items(rt, array) + index * G_Value_Size, item);
    return true;
}
bool aqua_array_resize(aqua_runtime *rt, aqua_value array, uint32_t length) {
    if (array.type != AQUA_ARRAY) return false;
    guest(rt, GUEST_array_resize, array.ref, length, 0, 0);
    return true;
}
bool aqua_array_push(aqua_runtime *rt, aqua_value array, aqua_value item) {
    uint32_t n = aqua_array_length(rt, array);
    return aqua_array_resize(rt, array, n + 1) && aqua_array_set(rt, array, n, item);
}

static bool range(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count) {
    return array.type == AQUA_ARRAY && start <= guest_u32(rt, array.ref + G_Array_Count) && count <= guest_u32(rt, array.ref + G_Array_Count) - start;
}
#define READ_NUMBERS(NAME, T, CHECK) \
    bool NAME(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, T *out) { \
        if (!range(rt, array, start, count)) return false; \
        const uint8_t *items = guest_base(rt) + array_items(rt, array) + (size_t)start * G_Value_Size; \
        for (uint32_t i = 0; i < count; i++) { \
            aqua_value v; memcpy(&v, items + (size_t)i * G_Value_Size, sizeof v); \
            if (!aqua_is_number(v) || !(CHECK)) return false; \
            out[i] = (T)v.number; \
        } \
        return true; \
    }
READ_NUMBERS(aqua_array_read_f64, double, true)
READ_NUMBERS(aqua_array_read_f32, float, true)
READ_NUMBERS(aqua_array_read_i32, int32_t, v.number >= -2147483648.0 && v.number <= 2147483647.0)
READ_NUMBERS(aqua_array_read_u8, uint8_t, v.type == AQUA_INT && v.number >= 0 && v.number <= 255)

#define WRITE_NUMBERS(NAME, T, TAG) \
    bool NAME(aqua_runtime *rt, aqua_value array, uint32_t start, uint32_t count, const T *in TAG##_PARAMETER) { \
        if (!range(rt, array, start, count)) return false; \
        uint32_t storage = writable_items(rt, array); /* may allocate: read the base after */ \
        uint8_t *items = guest_base(rt) + storage + (size_t)start * G_Value_Size; \
        for (uint32_t i = 0; i < count; i++) { \
            aqua_value v = {TAG##_TYPE, 0, (double)in[i]}; \
            if (v.type == AQUA_FLOAT) v.number = (double)(float)v.number; \
            /* Integer values always hold an exact 32-bit integer (truncated, saturated, NaN 0) like the language's conversions. */ \
            if (v.type == AQUA_INT) v.number = v.number != v.number ? 0 : v.number <= -2147483648.0 ? -2147483648.0 \
                : v.number >= 2147483647.0 ? 2147483647.0 : (double)(int32_t)v.number; \
            memcpy(items + (size_t)i * G_Value_Size, &v, sizeof v); \
        } \
        return true; \
    }
#define CHOSEN_PARAMETER , aqua_type type
#define CHOSEN_TYPE type
#define INTEGER_PARAMETER
#define INTEGER_TYPE AQUA_INT
WRITE_NUMBERS(aqua_array_write_f64, double, CHOSEN)
WRITE_NUMBERS(aqua_array_write_f32, float, CHOSEN)
WRITE_NUMBERS(aqua_array_write_i32, int32_t, INTEGER)
WRITE_NUMBERS(aqua_array_write_u8, uint8_t, INTEGER)

aqua_value aqua_array_from_f64(aqua_runtime *rt, const double *items, uint32_t count, aqua_type type) {
    aqua_value v = aqua_array_new(rt, count);
    aqua_array_write_f64(rt, v, 0, count, items, type);
    return v;
}
aqua_value aqua_array_from_i32(aqua_runtime *rt, const int32_t *items, uint32_t count) {
    aqua_value v = aqua_array_new(rt, count);
    aqua_array_write_i32(rt, v, 0, count, items);
    return v;
}
aqua_value aqua_array_from_u8(aqua_runtime *rt, const uint8_t *items, uint32_t count) {
    aqua_value v = aqua_array_new(rt, count);
    aqua_array_write_u8(rt, v, 0, count, items);
    return v;
}

/* ---- Hashes ---- */

aqua_value aqua_hash_new(aqua_runtime *rt, uint32_t capacity) {
    uint32_t hash = guest(rt, GUEST_hash, 0, 0, 0, 0);
    if (capacity) { guest(rt, GUEST_hash_resize, hash, capacity, 0, 0); guest_set_u32(rt, hash + G_Hash_Count, 0); }
    aqua_value v = {AQUA_HASH, hash, 0};
    return v;
}
/* The pairs of a hash (pair_count), or, for readers, also a struct's fields as name/value pairs: libraries that read
   string-keyed hashes (JSON, storage, network) read structs the same way. */
static uint32_t pair_count(aqua_runtime *rt, aqua_value hash) { return hash.type == AQUA_HASH ? guest_u32(rt, hash.ref + G_Hash_Count) : 0; }
uint32_t aqua_hash_count(aqua_runtime *rt, aqua_value hash) {
    return hash.type == AQUA_STRUCT ? guest_u32(rt, hash.ref + G_Struct_Count) : pair_count(rt, hash);
}
bool aqua_hash_entry(aqua_runtime *rt, aqua_value hash, uint32_t index, aqua_value *key, aqua_value *value) {
    if (hash.type == AQUA_STRUCT) {
        if (index >= aqua_hash_count(rt, hash)) return false;
        uint32_t shape = guest_u32(rt, hash.ref + G_Struct_Shape);
        if (key) { aqua_value name = {AQUA_STRING, guest_u32(rt, shape + G_Shape_Fields + index * G_ShapeField_Size + G_ShapeField_Name), 0}; *key = name; }
        if (value) *value = guest_read_value(rt, hash.ref + G_Struct_Fields + index * G_Value_Size);
        return true;
    }
    if (index >= pair_count(rt, hash)) return false;
    uint32_t pair = guest_u32(rt, hash.ref + G_Hash_Pairs) + index * G_Pair_Size;
    if (key) *key = guest_read_value(rt, pair + G_Pair_Key);
    if (value) *value = guest_read_value(rt, pair + G_Pair_Value);
    return true;
}
static int32_t hash_find(aqua_runtime *rt, aqua_value hash, aqua_value key) {
    uint32_t count = pair_count(rt, hash), pairs = guest_u32(rt, hash.ref + G_Hash_Pairs);
    for (uint32_t i = 0; i < count; i++)
        if (values_equal(rt, guest_read_value(rt, pairs + i * G_Pair_Size + G_Pair_Key), key, true)) return (int32_t)i;
    return -1;
}
bool aqua_hash_get(aqua_runtime *rt, aqua_value hash, aqua_value key, aqua_value *value) {
    int32_t i = hash.type == AQUA_HASH ? hash_find(rt, hash, key) : -1;
    if (i < 0) return false;
    return aqua_hash_entry(rt, hash, (uint32_t)i, NULL, value);
}
bool aqua_hash_set(aqua_runtime *rt, aqua_value hash, aqua_value key, aqua_value value) {
    if (hash.type != AQUA_HASH) return false;
    if (!aqua_is_number(key) && key.type != AQUA_STRING && key.type != AQUA_BOOL) return false;
    int32_t i = hash_find(rt, hash, key);
    if (i < 0) {
        i = (int32_t)pair_count(rt, hash);
        guest(rt, GUEST_hash_resize, hash.ref, (uint32_t)i + 1, 0, 0);
    }
    uint32_t pair = guest_u32(rt, hash.ref + G_Hash_Pairs) + (uint32_t)i * G_Pair_Size;
    guest_write_value(rt, pair + G_Pair_Key, key);
    guest_write_value(rt, pair + G_Pair_Value, value);
    return true;
}
/* Compares a guest string with UTF-8 without allocating. */
static bool text_is(aqua_runtime *rt, uint32_t text, const char *utf8, size_t length) {
    uint16_t small[128];
    if (length > 128) {
        size_t n;
        char *s = guest_text_utf8(rt, text, &n);
        bool same = n == length && memcmp(s, utf8, n) == 0;
        free(s);
        return same;
    }
    size_t count = utf8_to_utf16(utf8, length, small);
    return guest_u32(rt, text) == count && memcmp(guest_base(rt) + text + G_Text_Data, small, count * 2) == 0;
}
bool aqua_hash_field(aqua_runtime *rt, aqua_value hash, const char *names, aqua_value *value) {
    uint32_t count = pair_count(rt, hash);
    for (const char *name = names; *name;) {
        const char *end = strchr(name, '|');
        size_t length = end ? (size_t)(end - name) : strlen(name);
        uint32_t pairs = count ? guest_u32(rt, hash.ref + G_Hash_Pairs) : 0;
        for (uint32_t i = 0; i < count; i++) {
            aqua_value key = guest_read_value(rt, pairs + i * G_Pair_Size + G_Pair_Key);
            if (key.type == AQUA_STRING && text_is(rt, key.ref, name, length)) {
                if (value) *value = guest_read_value(rt, pairs + i * G_Pair_Size + G_Pair_Value);
                return true;
            }
        }
        if (!end) break;
        name = end + 1;
    }
    return false;
}
bool aqua_hash_set_field(aqua_runtime *rt, aqua_value hash, const char *name, aqua_value value) {
    uint32_t count = pair_count(rt, hash), pairs = count ? guest_u32(rt, hash.ref + G_Hash_Pairs) : 0;
    size_t length = strlen(name);
    for (uint32_t i = 0; i < count; i++) {
        aqua_value key = guest_read_value(rt, pairs + i * G_Pair_Size + G_Pair_Key);
        if (key.type == AQUA_STRING && text_is(rt, key.ref, name, length)) {
            guest_write_value(rt, pairs + i * G_Pair_Size + G_Pair_Value, value);
            return true;
        }
    }
    return aqua_hash_set(rt, hash, aqua_cstring(rt, name), value);
}

/* ---- Native objects and references ---- */

aqua_value aqua_object_new(aqua_runtime *rt, aqua_class *cls, void *data) {
    aqua_value v = {AQUA_NATIVE, handle_new(rt, HANDLE_OBJECT, cls, data), 0};
    if (!v.ref) return host_error(rt, "Native resource table capacity exceeded (%u objects).", HANDLE_LIMIT);
    return v;
}
void *aqua_object_data(aqua_runtime *rt, aqua_value value, aqua_class *cls) {
    if (value.type != AQUA_NATIVE) return NULL;
    handle *h = handle_get(rt, value.ref);
    if (!h || h->kind != HANDLE_OBJECT || (cls && h->cls != cls)) return NULL;
    return h->data;
}
aqua_class *aqua_object_class(aqua_runtime *rt, aqua_value value) {
    if (value.type != AQUA_NATIVE) return NULL;
    handle *h = handle_get(rt, value.ref);
    return h && h->kind != HANDLE_BOUND ? h->cls : NULL;
}

aqua_ref aqua_retain(aqua_runtime *rt, aqua_value value) {
    return guest(rt, GUEST_pin, guest_cell(rt, value), 0, 0, 0);
}
aqua_value aqua_ref_value(aqua_runtime *rt, aqua_ref ref) { return ref ? guest_read_value(rt, ref) : aqua_null(); }
void aqua_release(aqua_runtime *rt, aqua_ref ref) { if (ref) guest(rt, GUEST_unpin, ref, 0, 0, 0); }

bool aqua_is_callable(aqua_value v) { return v.type == AQUA_FUNCTION || v.type == AQUA_NATIVE || v.type == AQUA_BUILTIN; }
int32_t aqua_function_arity(aqua_runtime *rt, aqua_value function) {
    if (function.type != AQUA_FUNCTION) return -1;
    uint32_t program = guest_u32(rt, function.ref + G_Closure_Program), index = guest_u32(rt, function.ref + G_Closure_Function);
    return (int32_t)guest_u32(rt, guest_u32(rt, program + G_Program_Functions) + index * G_Function_Size + G_Function_Arity);
}

aqua_value host_error(aqua_runtime *rt, const char *format, ...) {
    char message[1024];
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(message, sizeof message, format, arguments);
    va_end(arguments);
    aqua_value v = aqua_cstring(rt, message);
    v.type = AQUA_ERROR;
    return v;
}
