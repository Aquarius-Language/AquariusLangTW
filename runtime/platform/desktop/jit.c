/* The desktop runtime JIT (CLAUDE.md rule 11): turns a hot loop of untyped code into native code while the program runs.
   The compiler stores typed variants of such loops (stencils) in the application's "aquarius.jit" custom section
   (compiler/AquariusCompiler/wasm/WasmJit.cs). When a loop gets hot its generic code asks for one (rt_jitRequest); this
   file builds a module around the loop's next variant (the application's type section, imports of its memory, table,
   native-tier globals and runtime helpers, the variant's body), and engine_install compiles it into the same store and
   adds it to the function table. A variant checks the types it assumes when entered and leaves the loop to the generic
   code otherwise, which then asks for the next variant; so results never differ from the generic code's.
   AQUARIUS_JIT=0 disables the JIT; AQUARIUS_JIT_DIAGNOSTICS=1 reports each installation on standard error. */
#include "../../host/engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { JIT_VERSION = 1 };

typedef struct { uint32_t capacity; bool disabled, diagnostics; uint32_t *attempts; } jit_state;

/* ---- reading the application module ---- */

typedef struct { const uint8_t *at, *end; bool failed; } reader;

static uint32_t read_u32(reader *r) {
    uint32_t value = 0;
    for (int shift = 0; shift < 35; shift += 7) {
        if (r->at >= r->end) { r->failed = true; return 0; }
        uint8_t b = *r->at++;
        value |= (uint32_t)(b & 127) << shift;
        if (!(b & 128)) return value;
    }
    r->failed = true;
    return 0;
}
static const uint8_t *read_bytes(reader *r, uint32_t n) {
    if ((size_t)(r->end - r->at) < n) { r->failed = true; return NULL; }
    const uint8_t *p = r->at;
    r->at += n;
    return p;
}

/* The payload of a module's section: the custom section of the given name, or the standard section id. */
static bool find_section(const uint8_t *module, size_t length, uint8_t id, const char *name, reader *section) {
    reader r = {module + 8, module + length, length < 8};
    while (!r.failed && r.at < r.end) {
        uint8_t kind = *r.at++;
        uint32_t size = read_u32(&r);
        const uint8_t *payload = read_bytes(&r, size);
        if (r.failed) break;
        if (kind != id) continue;
        reader content = {payload, payload + size, false};
        if (id == 0) {
            uint32_t n = read_u32(&content);
            const uint8_t *text = read_bytes(&content, n);
            if (content.failed || n != strlen(name) || memcmp(text, name, n) != 0) continue;
        }
        *section = content;
        return true;
    }
    return false;
}

/* ---- writing the module ---- */

typedef struct { uint8_t *data; size_t size, capacity; } buffer;

static void put(buffer *b, const void *data, size_t n) {
    if (b->size + n > b->capacity) {
        b->capacity = (b->size + n) * 2;
        b->data = realloc(b->data, b->capacity);
    }
    memcpy(b->data + b->size, data, n);
    b->size += n;
}
static void put_byte(buffer *b, uint8_t value) { put(b, &value, 1); }
static void put_u32(buffer *b, uint32_t value) {
    do { uint8_t byte = value & 127; value >>= 7; put_byte(b, byte | (value ? 128 : 0)); } while (value);
}
static void put_name(buffer *b, const char *name, size_t n) { put_u32(b, (uint32_t)n); put(b, name, n); }
static void put_section(buffer *module, uint8_t id, const buffer *content) {
    put_byte(module, id); put_u32(module, (uint32_t)content->size); put(module, content->data, content->size);
}
static void import_header(buffer *b, const char *name, uint8_t kind) { put_name(b, "app", 3); put_name(b, name, strlen(name)); put_byte(b, kind); }

/* A stencil of the section: its loop, function type, imported runtime functions (names and types) and body. */
typedef struct { uint32_t loop, type, imports; reader names; const uint8_t *body; uint32_t body_size; } stencil;

static bool read_stencil(reader *r, stencil *s) {
    s->loop = read_u32(r); s->type = read_u32(r); s->imports = read_u32(r);
    s->names = *r;
    for (uint32_t i = 0; i < s->imports && !r->failed; i++) { read_bytes(r, read_u32(r)); read_u32(r); }
    s->body_size = read_u32(r);
    s->body = read_bytes(r, s->body_size);
    return !r->failed;
}

/* The module of one stencil: types, imports (memory, table, the signal and execution globals, runtime functions), the
   stencil as function "run", its body. */
static buffer assemble(reader types, const stencil *s) {
    buffer module = {0}, section = {0};
    put(&module, "\0asm\1\0\0\0", 8);
    put(&section, types.at, (size_t)(types.end - types.at));
    put_section(&module, 1, &section); section.size = 0;
    put_u32(&section, 4 + s->imports);
    import_header(&section, "memory", 2); put_byte(&section, 0); put_u32(&section, 0);
    import_header(&section, "__indirect_function_table", 1); put_byte(&section, 0x70); put_byte(&section, 0); put_u32(&section, 0);
    import_header(&section, "aqua_signal", 3); put_byte(&section, 0x7f); put_byte(&section, 1);
    import_header(&section, "aqua_execution", 3); put_byte(&section, 0x7f); put_byte(&section, 1);
    reader names = s->names;
    for (uint32_t i = 0; i < s->imports; i++) {
        uint32_t n = read_u32(&names);
        const char *name = (const char *)read_bytes(&names, n);
        put_name(&section, "app", 3); put_name(&section, name, n); put_byte(&section, 0); put_u32(&section, read_u32(&names));
    }
    put_section(&module, 2, &section); section.size = 0;
    put_u32(&section, 1); put_u32(&section, s->type);
    put_section(&module, 3, &section); section.size = 0;
    put_u32(&section, 1); put_name(&section, "run", 3); put_byte(&section, 0); put_u32(&section, s->imports);
    put_section(&module, 7, &section); section.size = 0;
    put_u32(&section, 1); put_u32(&section, s->body_size); put(&section, s->body, s->body_size);
    put_section(&module, 10, &section);
    free(section.data);
    return module;
}

int32_t platform_jit(engine *e, int32_t loop) {
    jit_state **slot = (jit_state **)engine_jit_state(e), *state = *slot;
    if (!state) {
        state = *slot = calloc(1, sizeof *state);
        const char *setting = platform_environment("AQUARIUS_JIT"), *diagnostics = platform_environment("AQUARIUS_JIT_DIAGNOSTICS");
        state->disabled = setting && strcmp(setting, "0") == 0;
        state->diagnostics = diagnostics && strcmp(diagnostics, "1") == 0;
    }
    if (state->disabled || loop < 0) return -1;
    size_t length;
    const uint8_t *module = engine_module(e, &length);
    reader section, types;
    if (!module || !find_section(module, length, 0, "aquarius.jit", &section) || !find_section(module, length, 1, NULL, &types)) return -1;
    if (read_u32(&section) != JIT_VERSION) return -1;
    if ((uint32_t)loop >= state->capacity) {
        uint32_t capacity = (uint32_t)loop + 16;
        state->attempts = realloc(state->attempts, capacity * sizeof *state->attempts);
        memset(state->attempts + state->capacity, 0, (capacity - state->capacity) * sizeof *state->attempts);
        state->capacity = capacity;
    }
    /* The loop's variants in order, from the next one not tried yet; one that fails to install is skipped. */
    uint32_t count = read_u32(&section), seen = 0;
    stencil s;
    for (uint32_t i = 0; i < count && read_stencil(&section, &s); i++) {
        if (s.loop != (uint32_t)loop || seen++ < state->attempts[loop]) continue;
        state->attempts[loop]++;
        buffer code = assemble(types, &s);
        char *error = NULL;
        double start = platform_now_ms();
        int32_t index = engine_install(e, code.data, code.size, &error);
        if (state->diagnostics) {
            char line[256];
            snprintf(line, sizeof line, "jit: loop %d variant %u (%zu bytes, %.1f ms): %s%s\n", loop, seen - 1, code.size, platform_now_ms() - start,
                index > 0 ? "installed" : "failed: ", index > 0 ? "" : error ? error : "");
            platform_write(2, line, strlen(line));
        }
        free(code.data); free(error);
        if (index > 0) return index;
    }
    return -1;
}
