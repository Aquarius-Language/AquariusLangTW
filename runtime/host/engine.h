/* The Wasm instance an application runs in. engine_wasmtime.c implements it natively; engine_web.c implements it in
   the web host side module, where guest memory is the host's own memory and guest exports are imported functions. */
#ifndef AQUA_ENGINE_H
#define AQUA_ENGINE_H
#include "host.h"

/* Receives every aquarius_v2.service call: operation, context, subject, name, arguments, count, output. */
typedef int32_t (*engine_service)(void *data, const int32_t arguments[7]);

engine *engine_create(const uint8_t *module, size_t length, engine_service service, void *data, char **error);
/* Another instance of the same compiled module, for Thread workers (native only). */
engine *engine_worker(engine *parent, engine_service service, void *data, char **error);
void engine_destroy(engine *e);
uint32_t engine_call(engine *e, guest_export name, const uint32_t *arguments, uint32_t count);
bool engine_has(engine *e, guest_export name);
/* Base of guest memory and its current size; the base can move after a call that grows memory. */
uint8_t *engine_memory(engine *e, size_t *size);
/* The first trap a call raised since the last take, owned by the caller; NULL when none. */
char *engine_take_trap(engine *e);
bool engine_trapped(engine *e);

/* The runtime JIT (native engines; runtime/platform/desktop/jit.c, none elsewhere): answers a hot loop's request
   (runtime/guest/runtime.c rt_jitRequest) with the table index of its next native variant, or -1. */
int32_t platform_jit(engine *e, int32_t loop);
/* For the JIT, while the guest waits in its request: the application module, a slot for the JIT's own state (freed with
   the engine), and installing a module that imports the application's exports (as "app"): its "run" function joins the
   function table, whose index is returned (-1 on failure, with the reason in *error). */
const uint8_t *engine_module(engine *e, size_t *length);
void **engine_jit_state(engine *e);
int32_t engine_install(engine *e, const uint8_t *module, size_t length, char **error);
#endif
