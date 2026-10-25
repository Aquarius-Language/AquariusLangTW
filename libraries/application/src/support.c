/* Shared helpers: failures, byte buffers, values and records, cancellation tokens and calls that finish later. */
#include "application.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zlib.h>
#if defined(AQUA_OS_WINDOWS)
#include <windows.h>
#include <bcrypt.h>
#else
#include <unistd.h>
#endif

/* ---- Failures ---- */

const char *app_kind_name(app_kind kind) {
    static const char *const names[] = {"Unsupported", "PermissionDenied", "Unavailable", "Cancelled", "InvalidData",
                                        "LimitExceeded", "NotFound", "Conflict"};
    return (unsigned)kind < sizeof names / sizeof *names ? names[kind] : "InvalidData";
}

bool app_error_set(app_error *e, app_kind kind, const char *format, ...) {
    if (!e) return false;
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(e->message, sizeof e->message, format, arguments);
    va_end(arguments);
    e->failed = true;
    e->kind = kind;
    return false;
}

bool app_check_bytes(uint64_t count, app_error *e) {
    if (count > APP_MAX_BYTES) return app_error_set(e, APP_LIMIT_EXCEEDED, "Data exceeds the byte limit.");
    return true;
}

bool app_check_pixels(int64_t width, int64_t height, int64_t frames, app_error *e) {
    int64_t pixels = width * height * frames;
    if (width < 1 || height < 1 || width > APP_MAX_DIMENSION || height > APP_MAX_DIMENSION || frames < 1 ||
        frames > APP_MAX_FRAMES || pixels > APP_MAX_PIXELS)
        return app_error_set(e, APP_LIMIT_EXCEEDED, "Image dimensions or frame allocation exceeds the limit.");
    return app_check_bytes((uint64_t)pixels * 4, e);
}

void app_fail(aqua_call *call, const app_error *e) {
    aqua_fail(call, "%s: %s: %s", app_function, app_kind_name(e->kind), e->message);
}

void app_fail_with(aqua_call *call, app_kind kind, const char *format, ...) {
    app_error e;
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(e.message, sizeof e.message, format, arguments);
    va_end(arguments);
    e.kind = kind;
    app_fail(call, &e);
}

/* ---- Bytes and text ---- */

bool app_bytes_reserve(app_bytes *b, size_t capacity) {
    if (capacity <= b->capacity) return true;
    size_t next = b->capacity ? b->capacity : 256;
    while (next < capacity) next = next > SIZE_MAX / 2 ? capacity : next * 2;
    uint8_t *data = realloc(b->data, next);
    if (!data) return false;
    b->data = data;
    b->capacity = next;
    return true;
}
bool app_bytes_append(app_bytes *b, const void *data, size_t count) {
    if (!count) return true;
    if (!app_bytes_reserve(b, b->length + count)) return false;
    memcpy(b->data + b->length, data, count);
    b->length += count;
    return true;
}
bool app_bytes_byte(app_bytes *b, uint8_t value) { return app_bytes_append(b, &value, 1); }
bool app_bytes_text(app_bytes *b, const char *text) { return app_bytes_append(b, text, strlen(text)); }
void app_bytes_free(app_bytes *b) { free(b->data); b->data = NULL; b->length = b->capacity = 0; }

uint32_t app_crc32(uint32_t crc, const uint8_t *data, size_t length) {
    while (length > 0) {
        uInt part = length > 0x40000000u ? 0x40000000u : (uInt)length;
        crc = (uint32_t)crc32(crc, data, part);
        data += part;
        length -= part;
    }
    return crc;
}

bool app_value_bytes(aqua_runtime *rt, aqua_value array, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    if (array.type != AQUA_ARRAY) return app_error_set(e, APP_INVALID_DATA, "Expected a byte array.");
    uint32_t count = aqua_array_length(rt, array);
    if (!app_check_bytes(count, e)) return false;
    if (!app_bytes_reserve(out, count ? count : 1)) return app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory.");
    if (!aqua_array_read_u8(rt, array, 0, count, out->data)) {
        app_bytes_free(out);
        return app_error_set(e, APP_INVALID_DATA, "Expected bytes in 0..255.");
    }
    out->length = count;
    return true;
}

aqua_value app_bytes_value(aqua_runtime *rt, const uint8_t *data, size_t length) {
    static const uint8_t none = 0;
    return aqua_array_from_u8(rt, data ? data : &none, (uint32_t)length);
}

char *app_strdup(const char *text) { return text ? app_strndup(text, strlen(text)) : NULL; }
char *app_strndup(const char *text, size_t length) {
    char *copy = malloc(length + 1);
    memcpy(copy, text, length);
    copy[length] = 0;
    return copy;
}
char *app_printf(const char *format, ...) {
    va_list arguments, copy;
    va_start(arguments, format);
    va_copy(copy, arguments);
    int n = vsnprintf(NULL, 0, format, arguments);
    va_end(arguments);
    char *text = malloc((size_t)(n < 0 ? 0 : n) + 1);
    vsnprintf(text, (size_t)(n < 0 ? 0 : n) + 1, format, copy);
    va_end(copy);
    return text;
}
bool app_equals_ignore_case(const char *a, const char *b) {
    for (;; a++, b++) {
        char x = *a >= 'A' && *a <= 'Z' ? (char)(*a + 32) : *a, y = *b >= 'A' && *b <= 'Z' ? (char)(*b + 32) : *b;
        if (x != y) return false;
        if (!x) return true;
    }
}

void app_random_hex(char *out, size_t digits) {
    uint8_t bytes[64] = {0};
    size_t count = (digits + 1) / 2;
    if (count > sizeof bytes) count = sizeof bytes;
#if defined(AQUA_OS_WINDOWS)
    BCryptGenRandom(NULL, bytes, (ULONG)count, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
#else
    if (getentropy(bytes, count) != 0) {
        uint64_t seed = (uint64_t)(uintptr_t)out ^ (uint64_t)rand() << 21;
        for (size_t i = 0; i < count; i++) { seed = seed * 6364136223846793005u + 1442695040888963407u; bytes[i] = (uint8_t)(seed >> 56); }
    }
#endif
    static const char hex[] = "0123456789abcdef";
    for (size_t i = 0; i < digits; i++) out[i] = hex[(bytes[i / 2] >> (i % 2 ? 0 : 4)) & 15];
    out[digits] = 0;
}

/* ---- Values ---- */

void app_set(aqua_runtime *rt, aqua_value hash, const char *field, aqua_value value) { aqua_hash_set_field(rt, hash, field, value); }
void app_set_text(aqua_runtime *rt, aqua_value hash, const char *field, const char *text) {
    aqua_hash_set_field(rt, hash, field, text ? aqua_cstring(rt, text) : aqua_null());
}
aqua_value app_number(double n) {
    if (n == floor(n) && n >= -2147483648.0 && n <= 2147483647.0 && !(n == 0 && signbit(n))) return aqua_int((int32_t)n);
    return aqua_double(n);
}
aqua_value app_capability(aqua_runtime *rt, bool available, const char *restriction) {
    aqua_value hash = aqua_hash_new(rt, 2);
    app_set(rt, hash, "available", aqua_bool(available));
    app_set_text(rt, hash, "restriction", restriction);
    return hash;
}

/* A field of an options hash, matching the key without regard to ASCII case (the old options were case-insensitive). */
bool app_field(aqua_runtime *rt, aqua_value hash, const char *name, aqua_value *out) {
    if (hash.type != AQUA_HASH) return false;
    uint32_t count = aqua_hash_count(rt, hash);
    bool found = false;
    for (uint32_t i = 0; i < count; i++) {
        aqua_value key, value;
        if (!aqua_hash_entry(rt, hash, i, &key, &value) || key.type != AQUA_STRING) continue;
        char *text = aqua_text_dup(rt, key, NULL);
        bool match = text && app_equals_ignore_case(text, name);
        aqua_free(text);
        if (match) { *out = value; found = true; }
    }
    return found;
}

/* ---- Cancellation tokens and tasks ---- */

void app_token_release(app_token *token) {
    if (token && --token->references == 0) free(token);
}

static bool token_cancelled(app_token *token, app_error *e) {
    if (token && token->cancelled) return app_error_set(e, APP_CANCELLED, "The operation was canceled.");
    return true;
}

bool app_cancelled(aqua_call *call) {
    app_error e = {0};
    if (token_cancelled(app_state_of(call)->cancellation, &e)) return false;
    app_fail(call, &e);
    return true;
}

void *app_task_new(aqua_call *call, size_t size) {
    app_task *task = calloc(1, size);
    task->rt = aqua_call_runtime(call);
    task->state = app_state_of(call);
    task->call = call;
    task->name = app_function;
    task->token = task->state->cancellation;
    if (task->token) task->token->references++;
    return task;
}

static void task_free(app_task *task) {
    if (task->cleanup) task->cleanup(task);
    app_token_release(task->token);
    free(task);
}

void app_task_return(app_task *task, aqua_value result) {
    if (task->finished) return;
    task->finished = true;
    if (task->call) aqua_return(task->call, result);
    else {
        aqua_resolve(task->pending, result);
        task_free(task);
    }
}

void app_task_fail(app_task *task, const app_error *e) {
    if (task->finished) return;
    task->finished = true;
    if (task->call) {
        const char *name = app_function;
        app_function = task->name;
        app_fail(task->call, e);
        app_function = name;
    } else {
        aqua_reject(task->pending, "%s: %s: %s", task->name, app_kind_name(e->kind), e->message);
        task_free(task);
    }
}

void app_task_error(app_task *task, app_kind kind, const char *format, ...) {
    app_error e;
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(e.message, sizeof e.message, format, arguments);
    va_end(arguments);
    e.kind = kind;
    app_task_fail(task, &e);
}

void app_task_yield(app_task *task) {
    aqua_call *call = task->call;
    task->call = NULL;
    if (task->finished) task_free(task);
    else task->pending = aqua_defer(call);
}

bool app_task_cancelled(app_task *task) {
    app_error e = {0};
    if (token_cancelled(task->token, &e)) return false;
    app_task_fail(task, &e);
    return true;
}

/* Tasks.CreateCancellation / UseCancellation and the token's methods. */
void app_tasks_CreateCancellation(aqua_call *call) {
    app_token *token = calloc(1, sizeof *token);
    token->references = 1;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), app_token_class, token));
}

void app_tasks_UseCancellation(aqua_call *call) {
    app_state *state = app_state_of(call);
    aqua_value value = aqua_arg(call, 0);
    app_token *token = NULL;
    if (!aqua_is_nullish(value)) {
        token = aqua_object_data(aqua_call_runtime(call), value, app_token_class);
        if (!token) { app_fail_with(call, APP_INVALID_DATA, "Expected a resource owned by this application runtime."); return; }
        token->references++;
    }
    if (state->cancellation) app_token_release(state->cancellation);
    state->cancellation = token;
}

void app_token_finalize(aqua_runtime *rt, void *data) { (void)rt; app_token_release(data); }
void app_token_Cancel(aqua_call *call) { ((app_token *)aqua_self(call))->cancelled = true; }
void app_token_IsCancelled(aqua_call *call) { aqua_return_bool(call, ((app_token *)aqua_self(call))->cancelled); }
void app_token_Dispose(aqua_call *call) {
    /* The token stays usable by calls already holding it; a disposed token stops being the current one. */
    app_state *state = app_state_of(call);
    if (state->cancellation == aqua_self(call)) { app_token_release(state->cancellation); state->cancellation = NULL; }
}
