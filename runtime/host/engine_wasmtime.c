/* Native engine: runs the application's Wasm in Wasmtime through its C API (Windows, Linux, Android). Compiled code is
   shared by the main instance and every Thread worker, each with its own store, and cached between runs. */
#include "host.h"
#include "engine.h"
#include "sha256.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wasmtime.h>

struct engine {
    wasm_engine_t *wasm;      /* shared by workers: owned by the main engine */
    wasmtime_module_t *module;
    wasmtime_store_t *store;
    wasmtime_context_t *context;
    wasmtime_instance_t instance;
    wasmtime_memory_t memory;
    wasmtime_func_t exports[GUEST_EXPORT_COUNT];
    bool present[GUEST_EXPORT_COUNT];
    engine_service service; void *service_data;
    bool worker;
    char *trap;               /* first trap message, until taken */
};

static const char *const export_names[GUEST_EXPORT_COUNT] = {
#define X(NAME, ARITY) "aqua_" #NAME,
    GUEST_EXPORTS(X)
#undef X
};
static const uint8_t export_arities[GUEST_EXPORT_COUNT] = {
#define X(NAME, ARITY) ARITY,
    GUEST_EXPORTS(X)
#undef X
};

static char *message_of(wasmtime_error_t *error, wasm_trap_t *trap) {
    wasm_byte_vec_t text = {0};
    if (error) { wasmtime_error_message(error, &text); wasmtime_error_delete(error); }
    else if (trap) { wasm_trap_message(trap, &text); wasm_trap_delete(trap); }
    char *message = malloc(text.size + 1);
    if (text.size) memcpy(message, text.data, text.size);
    message[text.size] = 0;
    wasm_byte_vec_delete(&text);
    return message;
}

static wasm_trap_t *service_trampoline(void *env, wasmtime_caller_t *caller, wasmtime_val_raw_t *values, size_t count) {
    (void)caller; (void)count;
    engine *e = env;
    int32_t arguments[7];
    for (int i = 0; i < 7; i++) arguments[i] = values[i].i32;
    values[0].i32 = e->service(e->service_data, arguments);
    return NULL;
}

static wasm_engine_t *shared_engine(void) {
    wasm_config_t *config = wasm_config_new();
    /* Guest recursion runs on the runtime's own frame stack, so the default native stack suffices. Threads are allowed
       for Thread workers, which run in their own stores on other OS threads. */
    wasmtime_config_cranelift_opt_level_set(config, WASMTIME_OPT_LEVEL_SPEED);
    wasmtime_config_parallel_compilation_set(config, true);
    return wasm_engine_new_with_config(config);
}

static engine *instantiate(wasm_engine_t *wasm, wasmtime_module_t *module, engine_service service, void *data, char **error) {
    engine *e = calloc(1, sizeof *e);
    e->wasm = wasm; e->module = module; e->service = service; e->service_data = data;
    e->store = wasmtime_store_new(wasm, NULL, NULL);
    e->context = wasmtime_store_context(e->store);
    wasm_valtype_vec_t parameters, results;
    wasm_valtype_t *seven[7], *one[1];
    for (int i = 0; i < 7; i++) seven[i] = wasm_valtype_new(WASM_I32);
    one[0] = wasm_valtype_new(WASM_I32);
    wasm_valtype_vec_new(&parameters, 7, seven);
    wasm_valtype_vec_new(&results, 1, one);
    wasm_functype_t *type = wasm_functype_new(&parameters, &results);
    wasmtime_linker_t *linker = wasmtime_linker_new(wasm);
    wasmtime_error_t *failure = wasmtime_linker_define_func_unchecked(linker, "aquarius_v2", 11, "service", 7, type, service_trampoline, e, NULL);
    wasm_functype_delete(type);
    wasm_trap_t *trap = NULL;
    if (!failure) failure = wasmtime_linker_instantiate(linker, e->context, module, &e->instance, &trap);
    wasmtime_linker_delete(linker);
    if (failure || trap) {
        if (error) *error = message_of(failure, trap);
        wasmtime_store_delete(e->store); free(e);
        return NULL;
    }
    wasmtime_extern_t item;
    if (!wasmtime_instance_export_get(e->context, &e->instance, "memory", 6, &item) || item.kind != WASMTIME_EXTERN_MEMORY) {
        if (error) *error = strdup("The application does not export its memory.");
        wasmtime_store_delete(e->store); free(e);
        return NULL;
    }
    e->memory = item.of.memory;
    for (int i = 0; i < GUEST_EXPORT_COUNT; i++)
        if (wasmtime_instance_export_get(e->context, &e->instance, export_names[i], strlen(export_names[i]), &item) && item.kind == WASMTIME_EXTERN_FUNC) {
            e->exports[i] = item.of.func; e->present[i] = true;
        }
    return e;
}

/* Compiled code is kept between runs (compiling an application costs tens of milliseconds every start): slot file
   <cache>/wasmtime-<slot>.bin holds a module's SHA-256 and Wasmtime's serialization of its compiled code. The slot is
   the digest's first byte modulo CACHE_SLOTS, which bounds the cache; a module whose slot holds another compiles and
   takes the slot. Entries are written beside the slot and moved over it in one step, so readers see whole entries.
   Wasmtime rejects code compiled by another version or configuration, which then recompiles. The cache lives in the
   user's (or the app's) own directory: its contents are trusted like the runtime's own files. */
#define CACHE_SLOTS 32 /* an entry is a few megabytes (each application carries the guest runtime) */
static char *cache_slot(const uint8_t digest[32], char **directory) {
    *directory = platform_cache_directory();
    if (!*directory) return NULL;
    size_t n = strlen(*directory) + 32;
    char *path = malloc(n);
    snprintf(path, n, "%s/wasmtime-%02x.bin", *directory, digest[0] % CACHE_SLOTS);
    return path;
}
static wasmtime_module_t *cached_module(wasm_engine_t *wasm, const char *slot, const uint8_t digest[32]) {
    size_t length = 0;
    uint8_t *entry = platform_read_file(slot, &length);
    wasmtime_module_t *module = NULL;
    if (entry && length > 32 && memcmp(entry, digest, 32) == 0) {
        wasmtime_error_t *failure = wasmtime_module_deserialize(wasm, entry + 32, length - 32, &module);
        if (failure) { wasmtime_error_delete(failure); module = NULL; }
    }
    free(entry);
    return module;
}
static void cache_module(const wasmtime_module_t *module, const char *directory, const char *slot, const uint8_t digest[32]) {
    wasm_byte_vec_t code;
    wasmtime_error_t *failure = wasmtime_module_serialize(module, &code);
    if (failure) { wasmtime_error_delete(failure); return; }
    uint8_t *entry = malloc(code.size + 32);
    memcpy(entry, digest, 32);
    memcpy(entry + 32, code.data, code.size);
    size_t n = strlen(slot) + 64;
    char *partial = malloc(n);
    snprintf(partial, n, "%s.%p-%.0f.part", slot, (void *)entry, platform_now_ms() * 1000.0); /* unique per process */
    if (platform_make_directories(directory) && platform_write_file(partial, entry, code.size + 32)) platform_replace_file(partial, slot);
    free(partial); free(entry);
    wasm_byte_vec_delete(&code);
}

engine *engine_create(const uint8_t *module, size_t length, engine_service service, void *data, char **error) {
    wasm_engine_t *wasm = shared_engine();
    uint8_t digest[32];
    sha256(module, length, digest);
    char *directory = NULL, *slot = cache_slot(digest, &directory);
    wasmtime_module_t *compiled = slot ? cached_module(wasm, slot, digest) : NULL;
    wasmtime_error_t *failure = NULL;
    if (!compiled) {
        failure = wasmtime_module_new(wasm, module, length, &compiled);
        if (!failure && slot) cache_module(compiled, directory, slot, digest);
    }
    free(slot); free(directory);
    if (failure) { if (error) *error = message_of(failure, NULL); wasm_engine_delete(wasm); return NULL; }
    engine *e = instantiate(wasm, compiled, service, data, error);
    if (!e) { wasmtime_module_delete(compiled); wasm_engine_delete(wasm); }
    return e;
}

engine *engine_worker(engine *parent, engine_service service, void *data, char **error) {
    engine *e = instantiate(parent->wasm, parent->module, service, data, error);
    if (e) e->worker = true;
    return e;
}

void engine_destroy(engine *e) {
    if (!e) return;
    wasmtime_store_delete(e->store);
    if (!e->worker) { wasmtime_module_delete(e->module); wasm_engine_delete(e->wasm); }
    free(e->trap);
    free(e);
}

bool engine_has(engine *e, guest_export name) { return e->present[name]; }

uint32_t engine_call(engine *e, guest_export name, const uint32_t *arguments, uint32_t count) {
    if (!e->present[name]) {
        if (!e->trap) { char text[96]; snprintf(text, sizeof text, "The application lacks the runtime export %s.", export_names[name]); e->trap = strdup(text); }
        return 0;
    }
    wasmtime_val_raw_t values[4] = {0};
    for (uint32_t i = 0; i < count && i < 4; i++) values[i].i32 = (int32_t)arguments[i];
    wasm_trap_t *trap = NULL;
    wasmtime_error_t *failure = wasmtime_func_call_unchecked(e->context, &e->exports[name], values, export_arities[name] > 1 ? export_arities[name] : 1, &trap);
    if (failure || trap) {
        char *message = message_of(failure, trap);
        if (!e->trap) e->trap = message; else free(message);
        return 0;
    }
    return (uint32_t)values[0].i32;
}

uint8_t *engine_memory(engine *e, size_t *size) {
    if (size) *size = wasmtime_memory_data_size(e->context, &e->memory);
    return wasmtime_memory_data(e->context, &e->memory);
}

char *engine_take_trap(engine *e) { char *t = e->trap; e->trap = NULL; return t; }
bool engine_trapped(engine *e) { return e->trap != NULL; }
