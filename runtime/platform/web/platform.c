/* The web layer of the host: the runtime runs on the page's main thread and the browser's event loop drives it.
   Everything it needs from the browser comes from web-glue/core (imports of module "aqua_web"); the glue calls the
   exports below to create the runtime, deliver assets and run turns. */
#include "../../host/host.h"
#include <stdlib.h>
#include <string.h>

#define WEB_IMPORT(name) __attribute__((import_module("aqua_web"), import_name(#name)))
WEB_IMPORT(now) double web_now(void);
WEB_IMPORT(write) void web_write(int32_t stream, const char *text, size_t length);
WEB_IMPORT(wake) void web_wake(void);
WEB_IMPORT(closing) void web_closing(void);
WEB_IMPORT(environment) char *web_environment(const char *name);

static aqua_runtime *runtime;

double platform_now_ms(void) { return web_now(); }
void platform_write(int stream, const char *utf8, size_t length) { web_write(stream, utf8, length); }
void *platform_lock_create(void) { return (void *)1; }
void platform_lock(void *lock) { (void)lock; }
void platform_unlock(void *lock) { (void)lock; }
void platform_lock_destroy(void *lock) { (void)lock; }
void platform_wake(aqua_runtime *rt) { (void)rt; web_wake(); }
void platform_wait(aqua_runtime *rt, double timeout_ms) { (void)rt; (void)timeout_ms; } /* never blocks: run_to_end refuses to wait */
/* The page's runtime settings (startApplication's environment option); values stay valid for the page's life. */
const char *platform_environment(const char *name) { return web_environment(name); }
void *platform_library_open(const char *path, char **error) { (void)path; if (error) *error = strdup("Native libraries load through the web glue."); return NULL; }
void *platform_library_symbol(void *library, const char *name) { (void)library; (void)name; return NULL; }
bool platform_write_file(const char *path, const uint8_t *data, size_t length) { (void)path; (void)data; (void)length; return false; }
bool platform_make_directories(const char *path) { (void)path; return false; }
void platform_remove_tree(const char *path) { (void)path; }
char *platform_temporary_directory(const char *prefix) { (void)prefix; return NULL; }
bool platform_execute(aqua_runtime *rt, const char *program, const char *const *arguments, uint32_t count, char **error) {
    (void)rt; (void)program; (void)arguments; (void)count; (void)error;
    return false;
}
void web_windows_close(aqua_runtime *rt);
void platform_runtime_closing(aqua_runtime *rt) { web_windows_close(rt); web_closing(); }

/* ---- Exports the glue drives ---- */

/* Takes ownership of `module` (allocated with malloc by the glue). Returns 0 on success, else an error text. */
__attribute__((export_name("aqua_web_create")))
const char *aqua_web_create(uint8_t *module, uint32_t length, const char *directory) {
    static char *error;
    free(error); error = NULL;
    runtime_options options = {directory, NULL, NULL, 0, NULL};
    runtime = runtime_create(module, length, &options, &error);
    return runtime ? NULL : (error ? error : "Cannot load the application.");
}

/* Registers a library side module the glue linked: its aqua_library_entry, passed as a function table index.
   Returns false when the library rejects this runtime's API version. */
void registry_ensure(void);
__attribute__((export_name("aqua_web_add_library")))
bool aqua_web_add_library(const char *name, uint32_t (*entry)(aqua_library *, uint32_t)) {
    registry_ensure();
    return registry_add_external(name, entry) != NULL;
}

/* Asset bytes of an externally stored asset (website deployments keep them beside program.wasm). */
__attribute__((export_name("aqua_web_asset_count"))) uint32_t aqua_web_asset_count(void) { return runtime->app.asset_count; }
__attribute__((export_name("aqua_web_asset_name"))) const char *aqua_web_asset_name(uint32_t i) { return runtime->app.assets[i].name; }
__attribute__((export_name("aqua_web_asset_set")))
void aqua_web_asset_set(uint32_t i, const uint8_t *data, uint32_t length) {
    runtime->app.assets[i].data = data;
    runtime->app.assets[i].length = length;
}

__attribute__((export_name("aqua_web_start"))) int32_t aqua_web_start(const char *entry) { return runtime_start(runtime, entry && *entry ? entry : NULL) ? 0 : 1; }
/* Milliseconds until the runtime has work again (0: now), or -1 once the application has finished. */
__attribute__((export_name("aqua_web_turn"))) double aqua_web_turn(double budget_ms) { return runtime_turn(runtime, budget_ms); }
__attribute__((export_name("aqua_web_exit_code"))) int32_t aqua_web_exit_code(void) { return runtime->exit_code; }
__attribute__((export_name("aqua_web_destroy"))) void aqua_web_destroy(void) { runtime_destroy(runtime); runtime = NULL; }
__attribute__((export_name("aqua_web_cancel"))) void aqua_web_cancel(void) { if (runtime) { runtime->exiting = true; runtime->exit_code = 1; } }
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) { (void)rt; (void)name; (void)length; return NULL; } /* the glue delivers them */
