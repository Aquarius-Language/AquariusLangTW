/* Internal declarations of the AquariusLang host core: the code that runs an application's Wasm on every target.
 * The same sources build into the native runtime (Wasmtime, runtime/platform/<os>) and into the web host side module
 * (the browser's engine, web-glue/). Only engine_*.c and the platform layer differ between them. */
#ifndef AQUA_HOST_H
#define AQUA_HOST_H

#include <aqua/api.h>
#include <stdatomic.h>

/* ---- Guest memory layout (runtime/guest/layout.def) as offset constants. ---------------------------------------- */
enum {
#define AQUA_FIELD(STRUCT, FIELD, NAME, OFFSET) G_##STRUCT##_##NAME = OFFSET,
#define AQUA_SIZE(STRUCT, BYTES) G_##STRUCT##_Size = BYTES,
#define AQUA_TAG(ENUM, NAME, VALUE)
#define AQUA_STATUS(NAME, VALUE) G_STATUS_##NAME = VALUE,
#include "../guest/layout.def"
#undef AQUA_FIELD
#undef AQUA_SIZE
#undef AQUA_TAG
#undef AQUA_STATUS
};

/* Guest exports the host calls, in the order engines resolve them. Optional ones may be missing from older apps. */
#define GUEST_EXPORTS(X) \
    X(initialize, 1) X(execute, 4) X(invoke, 3) X(run, 2) X(result, 1) X(status, 1) X(resume, 2) \
    X(release_execution, 1) X(pin, 1) X(unpin, 1) X(value, 0) X(text, 1) X(allocate, 1) X(reserve, 1) \
    X(array, 1) X(array_values, 1) X(array_arguments, 1) X(array_resize, 2) X(hash, 1) X(hash_resize, 2) X(scope, 1) X(scope_set, 3) \
    X(scope_get, 2) X(collect, 0) X(collection_due, 0) X(collection_generation, 0) X(native_live, 1) X(pending, 0) \
    X(thread_module, 0) X(portable_module, 1) X(thread_snapshot_size, 0) X(thread_reserve, 1) X(thread_enter, 3) \
    X(thread_encode, 1) X(thread_decode, 1) X(thread_free_packet, 1)
typedef enum guest_export {
#define X(NAME, ARITY) GUEST_##NAME,
    GUEST_EXPORTS(X)
#undef X
    GUEST_EXPORT_COUNT
} guest_export;

typedef struct engine engine; /* engine.h */
/* Handles every aquarius_v2.service call of the application's Wasm (runtime/guest/runtime.c host_service). */
int32_t host_service(aqua_runtime *rt, int32_t operation, int32_t context, int32_t subject, int32_t name,
                     int32_t arguments, int32_t count, int32_t output);

/* ---- Application packages (runtime/host/application.c). -------------------------------------------------------- */
typedef struct app_asset { char *name; const uint8_t *data; uint32_t length; } app_asset;
typedef struct app_module { char *path; uint32_t function; } app_module;
typedef struct app_import { char *name; uint32_t module; } app_import;   /* import("Models") -> a bundled library module */
typedef struct application {
    uint8_t *bytes; size_t length;      /* the module, owned */
    uint32_t value_abi;                 /* 2, or 3: typed number and Boolean arrays are packed (values.c) */
    uint32_t program_address, heap_start, function_count;
    app_module *modules; uint32_t module_count, entry;
    char **platforms; uint32_t platform_count;
    app_asset *assets; uint32_t asset_count;
    bool external_assets;
    app_import *imports; uint32_t import_count;   /* ABI 4: Aquarius-source library modules by import name */
    char **libraries; uint32_t library_count;     /* ABI 4: native libraries the application needs */
} application;
bool application_parse(application *app, uint8_t *bytes, size_t length, char **error);
void application_free(application *app);
int32_t application_module(const application *app, const char *path); /* index into modules, case-insensitive */
int32_t application_asset(const application *app, const char *path);
int32_t application_import(const application *app, const char *name); /* module index of an import name, or -1 */

/* ---- Registry of libraries, classes and members (runtime/host/registry.c). -------------------------------------- */
typedef enum member_kind { MEMBER_FUNCTION, MEMBER_PROPERTY, MEMBER_INT, MEMBER_DOUBLE, MEMBER_BOOL, MEMBER_STRING, MEMBER_VALUE } member_kind;
typedef struct member {
    member_kind kind;
    char *names;            /* '|'-separated aliases, as registered */
    aqua_function function; /* function or property getter */
    char *signature;        /* argument kinds, checked before the call */
    uint32_t minimum, maximum; /* arity from the signature; maximum UINT32_MAX for repeated kinds */
    double number; char *text;
    aqua_value value;       /* MEMBER_VALUE: a fixed scalar such as a portable builtin token */
    bool async;             /* aqua_define_async: the call gives a promise of its outcome */
} member;
struct aqua_library {
    const aqua_api_table *api;  /* first field: read by aqua_api_stub.c in external libraries */
    char *name;
    void *(*create_state)(aqua_runtime *); void (*destroy_state)(aqua_runtime *, void *);
    uint32_t index;             /* position in the registry, indexes per-runtime state */
};
typedef struct member_name { uint16_t *units; uint32_t count, member; } member_name;
struct aqua_class {
    aqua_library *library;
    char *names;                /* module aliases, or the class's type name */
    bool module;
    void (*finalize)(aqua_runtime *, void *);
    member *members; uint32_t member_count, member_capacity;
    uint32_t index;             /* position in the registry */
    /* Every alias's UTF-16 units in an open-addressing table, built when its library finishes registering. */
    member_name *lookup; uint32_t lookup_capacity, lookup_members;
};
typedef struct registry {
    aqua_library **libraries; uint32_t library_count;
    aqua_class **classes; uint32_t class_count;
} registry;
extern registry host_registry;
/* Registers a library through its entry point; external libraries pass their aqua_library_entry. */
aqua_library *registry_add(const char *name, void (*entry)(aqua_library *));
aqua_library *registry_add_external(const char *name, uint32_t (*entry)(aqua_library *, uint32_t));
/* Loads an external native library (aqua_<name>.dll, libaqua_<name>.so) and registers its modules under <name>. */
bool library_load(const char *path, char **error);
void registry_ensure(void);
aqua_library *registry_library(const char *name); /* a registered library by name */
aqua_class *registry_module(const char *name);   /* a module by any alias */
int32_t class_member(const aqua_class *c, const char *name);
/* The member named by UTF-16 units (a guest Text), from the class's alias table. */
int32_t class_member_units(const aqua_class *c, const uint16_t *units, uint32_t count);
void class_define_value(aqua_class *owner, const char *names, aqua_value value);
bool names_contain(const char *names, const char *name);
const char *names_first(const char *names, char *buffer, size_t size);

/* ---- Handles: native values the guest holds (tag NATIVE). ------------------------------------------------------- */
typedef enum handle_kind { HANDLE_FREE, HANDLE_MODULE, HANDLE_OBJECT, HANDLE_BOUND } handle_kind;
typedef struct handle {
    handle_kind kind;
    aqua_class *cls;
    void *data;              /* object data, module state */
    uint32_t self;           /* HANDLE_BOUND: the object or module handle */
    uint32_t member;         /* HANDLE_BOUND: member index in the owner's class */
    uint32_t references;     /* host references keeping it alive */
    uint32_t generation;     /* guest collection generation at creation: live until a later collection decides */
    uint32_t *bound;         /* bound method handles by member index (lazily allocated), 0 = none */
    uint32_t next_free;
} handle;
#define HANDLE_LIMIT 65536u

/* ---- Scheduling. ------------------------------------------------------------------------------------------------ */
typedef struct task task;
struct aqua_pending { aqua_runtime *rt; task *task; bool settled; aqua_value result; aqua_ref result_ref; struct promise *promise; };
struct task {
    uint32_t execution;      /* guest Execution* */
    aqua_pending *pending;   /* while waiting for a deferred library call */
    aqua_done done; void *data;
    bool finished, failed;
    bool loop;               /* on the runtime loop's list: it may wait (await), unlike an execution run to completion */
    bool follow;             /* aqua_invoke_async: a promise result settles before done runs */
    aqua_value result;
    uint32_t result_pin;     /* keeps result alive until delivered */
    task *next;
};
typedef struct timer { double due; aqua_tick tick; void *data; struct timer *next; } timer;
typedef struct posted { void (*run)(aqua_runtime *, void *); void *data; struct posted *next; } posted;

/* ---- Calls in progress. ----------------------------------------------------------------------------------------- */
typedef struct scratch_block { struct scratch_block *next; size_t used, capacity; unsigned char data[]; } scratch_block;
struct aqua_call {
    aqua_runtime *rt;
    const aqua_class *owner; const member *member;
    uint32_t self;           /* handle of the object/module */
    uint32_t context;        /* guest context of the caller */
    const aqua_value *arguments; uint32_t count;
    aqua_value result;
    bool failed;
    aqua_pending *pending;
    scratch_block *scratch;
};

/* Per-script guest contexts: imports resolve relative to their script; globals are cached by the guest per context. */
typedef struct context { char *script; char *directory; } context;

struct aqua_runtime {
    application app;
    engine *engine;
    char *application_directory;
    char **launch; uint32_t launch_count;
    context *contexts; uint32_t context_count, context_capacity;
    handle *handles; uint32_t handle_count, handle_free;
    uint32_t global_handle;          /* the core globals module */
    void **states;                   /* per-library state, by library index */
    uint32_t *state_order; uint32_t state_count;
    uint32_t collection_generation;  /* last guest collection the handles were swept against */
    uint32_t pending_block;          /* guest Pending*, 0 for runtimes without one */
    uint32_t quantum;
    task *tasks; task *current;
    struct promise *unhandled;       /* rejected promises nobody handled yet (promise.c) */
    timer *timers;
    posted *posts; void *post_lock; atomic_bool post_flag;
    uint32_t *refs_free; uint32_t refs_free_count;
    double start_ms;
    int exit_code; bool finished, exiting;
    uint32_t entry_module;            /* the module runtime_start ran */
    uint32_t importing[64], import_depth; /* modules whose import is running, innermost last */
    char *materialized;              /* directory holding assets as files (desktop, Android) */
    bool materialized_created;       /* that directory was created: the runtime removes it when it ends */
    char **asset_files;              /* materialized path per asset, created on first request */
    void *platform;                  /* platform layer data */
    void *threads;                   /* Thread library state */
};

/* ---- Guest access helpers (runtime/host/guest.c). --------------------------------------------------------------- */
uint32_t guest(aqua_runtime *rt, guest_export name, uint32_t a, uint32_t b, uint32_t c, uint32_t d);
uint8_t *guest_base(aqua_runtime *rt);
uint32_t guest_u32(aqua_runtime *rt, uint32_t address);
void guest_set_u32(aqua_runtime *rt, uint32_t address, uint32_t value);
aqua_value guest_read_value(aqua_runtime *rt, uint32_t address);
void guest_write_value(aqua_runtime *rt, uint32_t address, aqua_value value);
uint32_t guest_cell(aqua_runtime *rt, aqua_value value);           /* a fresh guest value cell holding `value` */
uint32_t guest_text_ascii(aqua_runtime *rt, const char *ascii);    /* guest Text address */
char *guest_text_utf8(aqua_runtime *rt, uint32_t text, size_t *length); /* malloc'd UTF-8 of guest Text */
int32_t guest_member_index(aqua_runtime *rt, const aqua_class *c, uint32_t text); /* the member a guest Text names */

/* ---- Host core internals shared between files. ------------------------------------------------------------------ */
uint32_t handle_new(aqua_runtime *rt, handle_kind kind, aqua_class *cls, void *data);
handle *handle_get(aqua_runtime *rt, uint32_t id);
void handles_sweep(aqua_runtime *rt);
uint32_t module_handle(aqua_runtime *rt, aqua_class *module);
aqua_value member_value(aqua_runtime *rt, uint32_t self, uint32_t member, bool *cacheable);
uint32_t context_new(aqua_runtime *rt, const char *script);
void *library_state(aqua_runtime *rt, aqua_library *library);
aqua_value call_native(aqua_runtime *rt, uint32_t function_handle, const aqua_value *arguments, uint32_t count, uint32_t context, aqua_pending **pending);
aqua_value host_import(aqua_runtime *rt, uint32_t context, aqua_value name);
aqua_value host_error(aqua_runtime *rt, const char *format, ...);
int32_t thread_service(aqua_runtime *rt, int32_t operation, int32_t arguments, int32_t count, int32_t output);
void threads_close(aqua_runtime *rt);
void core_library(aqua_library *library);
void promise_library(aqua_library *library, aqua_class *globals);
void promises_finish(aqua_runtime *rt); /* reports unhandled rejections when the application ends */
/* The promise an aqua_define_async function gives for its call: settled by its result or failure, or later through its
   deferred result. */
aqua_value promise_of_call(aqua_runtime *rt, aqua_call *call);
void promise_settle_pending(aqua_runtime *rt, struct promise *p, aqua_value result);
bool promise_follow(aqua_runtime *rt, aqua_value value, aqua_done done, void *data); /* done once a returned promise settles */
char *format_value(aqua_runtime *rt, aqua_value v);
void format_number(char *buffer, size_t size, double n, aqua_type type);

/* Runs an execution until it finishes, yields to other work or waits; returns its status. */
uint32_t run_slice(aqua_runtime *rt, task *t);
/* Starts a script module of the application in a new context and runs it to completion (imports). */
bool run_module(aqua_runtime *rt, uint32_t module, uint32_t scope, aqua_value *result);
void task_finish(aqua_runtime *rt, task *t, bool ok, aqua_value result);

/* ---- Runtime lifecycle, driven by the platform layer. --------------------------------------------------------- */
typedef struct runtime_options {
    const char *application_directory;
    const char *entry;              /* module path, or NULL for the package entry */
    const char *const *launch; uint32_t launch_count;
    const char *materialize;        /* directory for asset files, NULL to skip (web) */
    /* Fuel per run slice of an execution (how often it yields to the loop); 0 for the portable default. A platform may
       choose a larger one: results never depend on it, only how often other work interleaves. */
    uint32_t quantum;
} runtime_options;
aqua_runtime *runtime_create(uint8_t *module, size_t length, const runtime_options *options, char **error);
/* Starts the entry script. */
bool runtime_start(aqua_runtime *rt, const char *entry);
/* One turn of the runtime's loop: posted work, runnable executions, due ticks. Returns the milliseconds until it has
   work again, 0 for more work now, or a negative number once the application has finished. */
double runtime_turn(aqua_runtime *rt, double budget_ms);
void runtime_destroy(aqua_runtime *rt);

/* ---- Platform layer (runtime/platform/<os>). -------------------------------------------------------------------- */
/* After the runtime's libraries released their state: windows and other platform resources still open close here. */
void platform_runtime_closing(aqua_runtime *rt);
/* Bytes of an asset the application stores outside its module, kept for the runtime's life; NULL when unavailable. */
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length);
double platform_now_ms(void);
void platform_write(int stream, const char *utf8, size_t length); /* 1 stdout, 2 stderr */
void *platform_lock_create(void);
void platform_lock(void *lock);
void platform_unlock(void *lock);
void platform_lock_destroy(void *lock);
void platform_wake(aqua_runtime *rt);      /* from any thread: the loop has posted work */
const char *platform_environment(const char *name);
/* Native shared libraries; the web loads side modules through its glue instead. */
void *platform_library_open(const char *path, char **error);
/* Finds and loads the external native library <name> an application needs (aqua_<name>.dll, libaqua_<name>.so). */
bool platform_load_library(aqua_runtime *rt, const char *name, char **error);
void *platform_library_symbol(void *library, const char *name);
bool platform_file_exists(const char *path);
uint8_t *platform_read_file(const char *path, size_t *length);
bool platform_write_file(const char *path, const uint8_t *data, size_t length);
/* Moves `from` over `to`, replacing it in one step: a reader opens either the old file or the new one. When the move
   fails, `from` is deleted. */
bool platform_replace_file(const char *from, const char *to);
/* The directory the runtime keeps compiled code in between runs (it may not exist yet), or NULL for none. */
char *platform_cache_directory(void);
bool platform_make_directories(const char *path);
void platform_remove_tree(const char *path);
char *platform_temporary_directory(const char *prefix);

#endif
