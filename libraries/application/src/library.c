/* The application library's entry point: registers the modules and classes of modules.def and records.def, and
 * creates the per-runtime state (resources provider, temporary leases, open streams, cancellation). */
#include "application.h"
#include <stdlib.h>
#include <string.h>

/* The functions and getters the table names, implemented in the owners' source files. */
#define MODULE(owner, names)
#define CLASS(owner, type) void app_##owner##_finalize(aqua_runtime *rt, void *data);
#define FUNCTION(owner, name, alias, signature) void app_##owner##_##name(aqua_call *call);
#define PROPERTY(owner, name, getter) void getter(aqua_call *call);
#include "modules.def"
#undef MODULE
#undef CLASS
#undef FUNCTION
#undef PROPERTY

APP_THREAD_LOCAL const char *app_function = "";

/* Each function runs through a wrapper that names it for error messages ("ReadBytes: NotFound: ..."). */
#define MODULE(owner, names)
#define CLASS(owner, type)
#define FUNCTION(owner, name, alias, signature) \
    static void call_##owner##_##name(aqua_call *call) { app_function = #name; app_##owner##_##name(call); }
#define PROPERTY(owner, name, getter)
#include "modules.def"
#undef MODULE
#undef CLASS
#undef FUNCTION
#undef PROPERTY

aqua_class *app_record_class, *app_resource_class, *app_stream_class, *app_editor_class, *app_token_class;

/* ---- Records: a native object over a retained hash; each field of records.def reads the hash ---- */

typedef struct record { aqua_ref hash; } record;

static void record_finalize(aqua_runtime *rt, void *data) {
    record *r = data;
    aqua_release(rt, r->hash);
    free(r);
}

static void record_field(aqua_call *call, const char *name) {
    record *r = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value value;
    if (!r || !aqua_hash_field(rt, aqua_ref_value(rt, r->hash), name, &value)) {
        aqua_fail(call, "Module member not found: %s", name);
        return;
    }
    aqua_return(call, value);
}

#define RECORD_FIELD(name) static void record_##name(aqua_call *call) { record_field(call, #name); }
#include "records.def"
#undef RECORD_FIELD

aqua_value app_record(aqua_runtime *rt, aqua_value hash) {
    record *r = malloc(sizeof *r);
    r->hash = aqua_retain(rt, hash);
    return aqua_object_new(rt, app_record_class, r);
}

/* ---- Per-runtime state ---- */

void app_files_close(app_state *state);       /* files.c: temporary leases, backend data */
void app_platform_close(app_state *state);    /* platform/<os>.c */

static void *state_create(aqua_runtime *rt) {
    app_state *state = calloc(1, sizeof *state);
    state->rt = rt;
    char id[33];
    app_random_hex(id, 32);
#if defined(AQUA_OS_WEB)
    memcpy(state->provider, "browser-", 8);
#else
    memcpy(state->provider, "desktop-", 8);
#endif
    memcpy(state->provider + 8, id, 33);
    return state;
}

static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    app_state *state = data;
    app_state_close_streams(state);
    app_files_close(state);
    app_platform_close(state);
    if (state->cancellation) app_token_release(state->cancellation);
    free(state);
}

app_state *app_state_of(aqua_call *call) { return aqua_state(call); }

AQUA_LIBRARY(application) {
    aqua_library_state(library, state_create, state_destroy);
    app_record_class = aqua_define_class(library, "record", record_finalize);
#define RECORD_FIELD(name) aqua_define_property(app_record_class, #name, record_##name);
#include "records.def"
#undef RECORD_FIELD

    aqua_class *files = NULL, *resource = NULL, *stream = NULL, *paths = NULL, *images = NULL, *serialization = NULL,
               *storage = NULL, *clipboard = NULL, *textedit = NULL, *editor = NULL, *application = NULL, *tasks = NULL,
               *token = NULL;
#define MODULE(owner, names) owner = aqua_define_module(library, names);
#define CLASS(owner, type) owner = aqua_define_class(library, type, app_##owner##_finalize);
#define FUNCTION(owner, name, alias, signature) aqua_define_function(owner, #name "|" alias, call_##owner##_##name, signature);
#define PROPERTY(owner, name, getter) aqua_define_property(owner, #name, getter);
#include "modules.def"
#undef MODULE
#undef CLASS
#undef FUNCTION
#undef PROPERTY
    (void)files; (void)paths; (void)images; (void)serialization; (void)storage; (void)clipboard; (void)textedit;
    (void)application; (void)tasks;
    app_resource_class = resource;
    app_stream_class = stream;
    app_editor_class = editor;
    app_token_class = token;
}
