/* An ahead-of-time application library (aqua build --optimize, CLAUDE.md rule 11): the application's Wasm translated to
   C by the pinned wasm2c and compiled with the pinned Clang into <app>.aot.dll / <app>.aot.so beside the executable,
   with aot_app.c. The ahead-of-time engine (host/engine_aot.c) loads it (or, on iOS, links it) and drives it through this
   table; everything else in the runtime is the same prebuilt code as aquarius's. */
#ifndef AQUA_AOT_APP_H
#define AQUA_AOT_APP_H
#include <stddef.h>
#include <stdint.h>

#define AQUA_AOT_VERSION 1

/* Every aquarius_v2.service call of the application, with the context given to create. */
typedef int32_t (*aot_service)(void *context, const int32_t arguments[7]);

typedef struct aot_app {
    uint32_t version;
    uint8_t digest[32];   /* SHA-256 of the application module the library was compiled from */
    void *(*create)(aot_service service, void *context);
    void (*destroy)(void *instance);
    /* Calls guest export name (guest_exports.h order) with its arguments; a trap returns 0 with *trap set (malloc'd). */
    uint32_t (*call)(void *instance, int name, const uint32_t *arguments, char **trap);
    uint8_t *(*memory)(void *instance, size_t *size);
} aot_app;

#if defined(AQUA_EMBED)
#define AQUA_AOT_EXPORT /* linked into a native library output, which exports only its own functions */
#elif defined(_WIN32)
#define AQUA_AOT_EXPORT __declspec(dllexport)
#else
#define AQUA_AOT_EXPORT __attribute__((visibility("default")))
#endif
/* The library's one export. */
typedef const aot_app *(*aot_entry)(void);
#endif
