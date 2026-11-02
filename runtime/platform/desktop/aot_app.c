/* The per-application half of an ahead-of-time application library (see aot_app.h), compiled with the application's
   wasm2c translation (module name "app", header AQUA_AOT_HEADER) and AQUA_AOT_DIGEST, its module's SHA-256 as 64 hex
   digits. Stack exhaustion is not counted per call (a thread-local counter doubled a call's cost): native recursion is
   bounded by the native tier's depth limit, guest recursion runs on heap frames, and the launcher's stack is large. */
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include AQUA_AOT_HEADER
#include "wasm-rt-exceptions.h"
#include "wasm-rt-impl.h"
#include "aot_app.h"
#include "guest_exports.h"

struct w2c_aquarius__v2 { aot_service service; void *context; };
typedef struct { w2c_app module; struct w2c_aquarius__v2 imports; } instance;

u32 w2c_aquarius__v2_service(struct w2c_aquarius__v2 *imports, u32 a, u32 b, u32 c, u32 d, u32 e, u32 f, u32 g) {
    int32_t arguments[7] = {(int32_t)a, (int32_t)b, (int32_t)c, (int32_t)d, (int32_t)e, (int32_t)f, (int32_t)g};
    return (u32)imports->service(imports->context, arguments);
}

static WASM_RT_THREAD_LOCAL bool thread_ready;
static void ready_thread(void) { if (!thread_ready) { wasm_rt_init_thread(); thread_ready = true; } }

static void *create(aot_service service, void *context) {
    static bool initialized;
    if (!initialized) { wasm_rt_init(); initialized = true; }
    ready_thread();
    instance *i = calloc(1, sizeof *i);
    i->imports.service = service; i->imports.context = context;
    wasm2c_app_instantiate(&i->module, &i->imports);
    return i;
}

static void destroy(void *pointer) { instance *i = pointer; wasm2c_app_free(&i->module); free(i); }

#define ARGUMENTS_0
#define ARGUMENTS_1 , a[0]
#define ARGUMENTS_2 , a[0], a[1]
#define ARGUMENTS_3 , a[0], a[1], a[2]
#define ARGUMENTS_4 , a[0], a[1], a[2], a[3]

static uint32_t call(void *pointer, int name, const uint32_t *a, char **trap) {
    instance *i = pointer;
    ready_thread();
    /* A guest call may reach the host, which may call the guest again: each level keeps its own trap target. */
    wasm_rt_jmp_buf outer;
    memcpy(&outer, &g_wasm_rt_jmp_buf, sizeof outer);
    WASM_RT_UNWIND_TARGET *outer_target = wasm_rt_get_unwind_target();
    uint32_t result = 0;
    wasm_rt_trap_t code = wasm_rt_impl_try();
    if (code == WASM_RT_TRAP_NONE) {
        enum {
#define X(NAME, ARITY) EXPORT_##NAME,
            GUEST_EXPORTS(X)
#undef X
        };
        switch (name) {
#define X(NAME, ARITY) case EXPORT_##NAME: result = w2c_app_aqua_##NAME(&i->module ARGUMENTS_##ARITY); break;
            GUEST_EXPORTS(X)
#undef X
            default: break;
        }
    } else {
        const char *message = wasm_rt_strerror(code);
        *trap = malloc(strlen(message) + 16);
        sprintf(*trap, "wasm trap: %s", message);
    }
    memcpy(&g_wasm_rt_jmp_buf, &outer, sizeof outer);
    wasm_rt_set_unwind_target(outer_target);
    return result;
}

static uint8_t *memory(void *pointer, size_t *size) {
    wasm_rt_memory_t *m = w2c_app_memory(&((instance *)pointer)->module);
    if (size) *size = m->size;
    return m->data;
}

static int hex(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0; }

AQUA_AOT_EXPORT const aot_app *aqua_aot_app(void) {
    static aot_app app = {AQUA_AOT_VERSION, {0}, create, destroy, call, memory};
    static const char digest[] = AQUA_AOT_DIGEST;
    for (int k = 0; k < 32; k++) app.digest[k] = (uint8_t)(hex(digest[2 * k]) << 4 | hex(digest[2 * k + 1]));
    return &app;
}
