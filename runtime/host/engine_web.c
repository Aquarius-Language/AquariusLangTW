/* Web engine: the host core runs as a side module sharing the application's memory (web-glue/core/loader.mjs). Guest
   memory is this module's own memory, so guest addresses are pointers, and the guest runtime's exports are imported
   functions the loader binds straight to the application instance. */
#include "host.h"
#include "engine.h"
#include <stdlib.h>
#include <string.h>

#define PARAMETERS_0 void
#define PARAMETERS_1 uint32_t
#define PARAMETERS_2 uint32_t, uint32_t
#define PARAMETERS_3 uint32_t, uint32_t, uint32_t
#define PARAMETERS_4 uint32_t, uint32_t, uint32_t, uint32_t
#define ARGUMENTS_0
#define ARGUMENTS_1 a[0]
#define ARGUMENTS_2 a[0], a[1]
#define ARGUMENTS_3 a[0], a[1], a[2]
#define ARGUMENTS_4 a[0], a[1], a[2], a[3]
#define X(NAME, ARITY) __attribute__((import_module("guest"), import_name("aqua_" #NAME))) extern uint32_t guest_##NAME(PARAMETERS_##ARITY);
GUEST_EXPORTS(X)
#undef X

struct engine { engine_service service; void *data; };
static engine *instance;

engine *engine_create(const uint8_t *module, size_t length, engine_service service, void *data, char **error) {
    (void)module; (void)length; (void)error;
    instance = calloc(1, sizeof *instance);
    instance->service = service; instance->data = data;
    return instance;
}
engine *engine_worker(engine *parent, engine_service service, void *data, char **error) {
    (void)parent; (void)service; (void)data;
    if (error) *error = strdup("Thread workers run in Web Workers on the web.");
    return NULL;
}
void engine_destroy(engine *e) { if (e == instance) instance = NULL; free(e); }
bool engine_has(engine *e, guest_export name) { (void)e; (void)name; return true; }

uint32_t engine_call(engine *e, guest_export name, const uint32_t *a, uint32_t count) {
    (void)e; (void)count;
    switch (name) {
#define X(NAME, ARITY) case GUEST_##NAME: return guest_##NAME(ARGUMENTS_##ARITY);
    GUEST_EXPORTS(X)
#undef X
    default: return 0;
    }
}

/* Guest addresses are offsets from address 0 of the shared memory. */
uint8_t *engine_memory(engine *e, size_t *size) {
    (void)e;
    if (size) *size = __builtin_wasm_memory_size(0) * 65536u;
    static uint8_t *volatile base = 0; /* read through a volatile so the compiler cannot fold null arithmetic */
    return base;
}
char *engine_take_trap(engine *e) { (void)e; return NULL; } /* traps unwind to the glue, which reports them */
bool engine_trapped(engine *e) { (void)e; return false; }

/* The application's aquarius_v2.service import; the glue forwards it here. */
__attribute__((export_name("aqua_web_service")))
int32_t aqua_web_service(int32_t operation, int32_t context, int32_t subject, int32_t name, int32_t arguments, int32_t count, int32_t output) {
    const int32_t a[7] = {operation, context, subject, name, arguments, count, output};
    return instance->service(instance->data, a);
}
