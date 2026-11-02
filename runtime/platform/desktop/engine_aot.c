/* The ahead-of-time desktop engine (aquarius-aot, CLAUDE.md rule 11): runs the application from its ahead-of-time
   library (aot_app.h), compiled from the same .wasm by aqua build --optimize: <executable>.aot.dll / .aot.so beside the
   executable, or AQUARIUS_AOT for `aquarius-aot app.wasm`. The library must be built from this very module (its
   SHA-256). The module bytes still serve the host's metadata and the JIT's stencils; the runtime JIT installs no
   modules in this engine (engine_install declines), so untyped loops without a native region run generically. */
#include "../../host/host.h"
#include "../../host/engine.h"
#include "../../host/sha256.h"
#include "aot_app.h"
#include "desktop.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct engine {
    const aot_app *app;
    void *instance;
    engine_service service; void *service_data;
    char *trap;
    const uint8_t *bytes; size_t length;
    void *jit;
};

static int32_t service(void *context, const int32_t arguments[7]) {
    engine *e = context;
    /* Operation 5 is a hot loop asking the runtime JIT. */
    return arguments[0] == 5 ? platform_jit(e, arguments[2]) : e->service(e->service_data, arguments);
}

/* The library beside the executable (its name without .exe, then .aot.dll / .aot.so), or AQUARIUS_AOT. */
static char *library_path(void) {
    const char *setting = platform_environment("AQUARIUS_AOT");
    if (setting && *setting) return strdup(setting);
    char *self = platform_executable_path();
    if (!self) return NULL;
    size_t n = strlen(self);
#if defined(_WIN32)
    if (n > 4 && (strcmp(self + n - 4, ".exe") == 0 || strcmp(self + n - 4, ".EXE") == 0)) n -= 4;
    const char *suffix = ".aot.dll";
#else
    const char *suffix = ".aot.so";
#endif
    char *path = malloc(n + strlen(suffix) + 1);
    memcpy(path, self, n); strcpy(path + n, suffix);
    free(self);
    return path;
}

static const aot_app *load(const uint8_t *module, size_t length, char **error) {
    static const aot_app *app;
    if (app) return app;
    char *path = library_path();
    if (!path) { *error = strdup("Cannot find this executable."); return NULL; }
    char *failure = NULL;
    void *library = platform_library_open(path, &failure);
    aot_entry entry = library ? (aot_entry)platform_library_symbol(library, "aqua_aot_app") : NULL;
    const aot_app *candidate = entry ? entry() : NULL;
    uint8_t digest[32];
    sha256(module, length, digest);
    if (!candidate || candidate->version != AQUA_AOT_VERSION || memcmp(candidate->digest, digest, 32) != 0) {
        size_t n = strlen(path) + (failure ? strlen(failure) : 0) + 160;
        *error = malloc(n);
        snprintf(*error, n, "%s is missing or was not built from this application (build it with aqua build --optimize)%s%s",
            path, failure ? ": " : ".", failure ? failure : "");
        free(failure); free(path);
        return NULL;
    }
    free(failure); free(path);
    return app = candidate;
}

static engine *instantiate(const aot_app *app, engine_service service_function, void *data) {
    engine *e = calloc(1, sizeof *e);
    e->app = app; e->service = service_function; e->service_data = data;
    e->instance = app->create(service, e);
    return e;
}

engine *engine_create(const uint8_t *module, size_t length, engine_service service_function, void *data, char **error) {
    char *failure = NULL;
    const aot_app *app = load(module, length, &failure);
    if (!app) { if (error) *error = failure; else free(failure); return NULL; }
    engine *e = instantiate(app, service_function, data);
    e->bytes = module; e->length = length;
    return e;
}

engine *engine_worker(engine *parent, engine_service service_function, void *data, char **error) {
    (void)error;
    engine *e = instantiate(parent->app, service_function, data);
    e->bytes = parent->bytes; e->length = parent->length;
    return e;
}

void engine_destroy(engine *e) {
    if (!e) return;
    e->app->destroy(e->instance);
    free(e->trap); free(e->jit); free(e);
}

bool engine_has(engine *e, guest_export name) { (void)e; (void)name; return true; }

uint32_t engine_call(engine *e, guest_export name, const uint32_t *arguments, uint32_t count) {
    uint32_t a[4] = {0};
    for (uint32_t i = 0; i < count && i < 4; i++) a[i] = arguments[i];
    char *trap = NULL;
    uint32_t result = e->app->call(e->instance, (int)name, a, &trap);
    if (trap) { if (!e->trap) e->trap = trap; else free(trap); }
    return result;
}

uint8_t *engine_memory(engine *e, size_t *size) { return e->app->memory(e->instance, size); }
char *engine_take_trap(engine *e) { char *t = e->trap; e->trap = NULL; return t; }
bool engine_trapped(engine *e) { return e->trap != NULL; }

const uint8_t *engine_module(engine *e, size_t *length) { *length = e->length; return e->bytes; }
void **engine_jit_state(engine *e) { return &e->jit; }

int32_t engine_install(engine *e, const uint8_t *module, size_t length, char **error) {
    (void)e; (void)module; (void)length;
    *error = strdup("The ahead-of-time engine installs no runtime JIT modules.");
    return -1;
}
