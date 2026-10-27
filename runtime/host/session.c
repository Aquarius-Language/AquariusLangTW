/* A running application: its engine, guest contexts, the native handle table, the aquarius_v2.service protocol and
   imports. */
#include "host.h"
#include "engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Default libraries linked into this runtime, from CMake's AQUA_DEFAULT_LIBRARIES list: X(name) per library. */
#ifdef AQUA_HAS_DEFAULT_LIBRARIES
#include "default_libraries.h"
#endif
#ifndef AQUA_DEFAULT_LIBRARIES
#define AQUA_DEFAULT_LIBRARIES
#endif
#define X(name) void aqua_library_##name(aqua_library *library);
AQUA_DEFAULT_LIBRARIES
#undef X

static bool registered;
static aqua_class *core_globals;
void registry_ensure(void);
static void register_libraries(void) { registry_ensure(); }
void registry_ensure(void) {
    if (registered) return;
    registered = true;
    registry_add("core", core_library);
    core_globals = host_registry.classes[0];
#define X(name) registry_add(#name, aqua_library_##name);
    AQUA_DEFAULT_LIBRARIES
#undef X
}

static char *copy(const char *s) { if (!s) return NULL; size_t n = strlen(s) + 1; char *c = malloc(n); memcpy(c, s, n); return c; }

static int32_t service_entry(void *data, const int32_t a[7]) { return host_service(data, a[0], a[1], a[2], a[3], a[4], a[5], a[6]); }

/* ---- Contexts ---- */

uint32_t context_new(aqua_runtime *rt, const char *script) {
    if (rt->context_count == rt->context_capacity) {
        rt->context_capacity = rt->context_capacity ? rt->context_capacity * 2 : 16;
        rt->contexts = realloc(rt->contexts, rt->context_capacity * sizeof *rt->contexts);
    }
    context *c = &rt->contexts[rt->context_count];
    c->script = copy(script);
    const char *slash = strrchr(script, '/');
    c->directory = malloc(strlen(script) + 1);
    size_t n = slash ? (size_t)(slash - script) : 0;
    memcpy(c->directory, script, n);
    c->directory[n] = 0;
    return rt->context_count++;
}

/* ---- Handles ---- */

uint32_t handle_new(aqua_runtime *rt, handle_kind kind, aqua_class *cls, void *data) {
    uint32_t id = rt->handle_free;
    if (id) rt->handle_free = rt->handles[id].next_free;
    else {
        if (rt->handle_count >= HANDLE_LIMIT) return 0;
        id = rt->handle_count++;
    }
    handle *h = &rt->handles[id];
    memset(h, 0, sizeof *h);
    h->kind = kind; h->cls = cls; h->data = data;
    /* The guest may have collected since the last sweep: a handle made now postdates that collection. */
    h->generation = guest(rt, GUEST_collection_generation, 0, 0, 0, 0);
    return id;
}
handle *handle_get(aqua_runtime *rt, uint32_t id) {
    if (id == 0 || id >= rt->handle_count || rt->handles[id].kind == HANDLE_FREE) return NULL;
    return &rt->handles[id];
}
static void handle_free(aqua_runtime *rt, uint32_t id) {
    handle *h = &rt->handles[id];
    if (h->kind == HANDLE_OBJECT && h->cls->finalize) h->cls->finalize(rt, h->data);
    free(h->bound);
    memset(h, 0, sizeof *h);
    h->next_free = rt->handle_free;
    rt->handle_free = id;
}

/* After each guest collection: free handles the guest no longer reaches, unless the host still references them or they
   were created after that collection. A bound method keeps its object alive. */
void handles_sweep(aqua_runtime *rt) {
    uint32_t generation = guest(rt, GUEST_collection_generation, 0, 0, 0, 0);
    if (generation == rt->collection_generation) return;
    rt->collection_generation = generation;
    uint32_t count = rt->handle_count;
    unsigned char *live = calloc(count ? count : 1, 1);
    for (uint32_t i = 1; i < count; i++) {
        handle *h = &rt->handles[i];
        if (h->kind == HANDLE_FREE) continue;
        if (h->kind == HANDLE_MODULE || h->references || h->generation == generation || guest(rt, GUEST_native_live, i, 0, 0, 0)) live[i] = 1;
    }
    for (uint32_t i = 1; i < count; i++)
        if (live[i] && rt->handles[i].kind == HANDLE_BOUND) live[rt->handles[i].self] = 1;
    for (uint32_t i = 1; i < count; i++) {
        handle *h = &rt->handles[i];
        if (h->kind == HANDLE_FREE || live[i]) continue;
        /* A dead method handle leaves its owner's cache. */
        if (h->kind == HANDLE_BOUND) { handle *self = &rt->handles[h->self]; if (self->bound) self->bound[h->member] = 0; }
    }
    for (uint32_t i = 1; i < count; i++) if (rt->handles[i].kind != HANDLE_FREE && !live[i]) handle_free(rt, i);
    free(live);
}

/* ---- Modules, members and library state ---- */

void *library_state(aqua_runtime *rt, aqua_library *library) {
    if (!library->create_state) return NULL;
    if (!rt->states[library->index]) {
        rt->states[library->index] = library->create_state(rt);
        rt->state_order = realloc(rt->state_order, (rt->state_count + 1) * sizeof *rt->state_order);
        rt->state_order[rt->state_count++] = library->index;
    }
    return rt->states[library->index];
}

uint32_t module_handle(aqua_runtime *rt, aqua_class *module) {
    for (uint32_t i = 1; i < rt->handle_count; i++)
        if (rt->handles[i].kind == HANDLE_MODULE && rt->handles[i].cls == module) return i;
    return handle_new(rt, HANDLE_MODULE, module, library_state(rt, module->library));
}

static uint32_t bound_handle(aqua_runtime *rt, uint32_t self, uint32_t member) {
    handle *h = &rt->handles[self];
    if (!h->bound) h->bound = calloc(h->cls->member_count, sizeof *h->bound);
    if (h->bound[member]) return h->bound[member];
    uint32_t id = handle_new(rt, HANDLE_BOUND, NULL, NULL);
    if (!id) return 0;
    h = &rt->handles[self]; /* the table may have moved */
    rt->handles[id].self = self; rt->handles[id].member = member;
    h->bound[member] = id;
    return id;
}

/* The value of member `member` of the object or module `self`. Properties are live: never cacheable by the guest. */
aqua_value member_value(aqua_runtime *rt, uint32_t self, uint32_t member_index, bool *cacheable) {
    handle *h = handle_get(rt, self);
    const member *m = &h->cls->members[member_index];
    *cacheable = true;
    switch (m->kind) {
    case MEMBER_FUNCTION: {
        aqua_value v = {AQUA_NATIVE, bound_handle(rt, self, member_index), 0};
        /* Handed to the guest after its latest collection: keep it through the sweep of that collection. */
        if (v.ref) rt->handles[v.ref].generation = guest(rt, GUEST_collection_generation, 0, 0, 0, 0);
        return v;
    }
    case MEMBER_INT: return aqua_int((int32_t)m->number);
    case MEMBER_DOUBLE: return aqua_double(m->number);
    case MEMBER_BOOL: return aqua_bool(m->number != 0);
    case MEMBER_STRING: *cacheable = false; return aqua_cstring(rt, m->text);
    case MEMBER_VALUE: return m->value;
    case MEMBER_PROPERTY: {
        *cacheable = false;
        aqua_call call = {0};
        call.rt = rt; call.owner = h->cls; call.member = m; call.self = self; call.result = aqua_void();
        m->function(&call);
        for (scratch_block *b = call.scratch; b;) { scratch_block *next = b->next; free(b); b = next; }
        return call.result;
    }
    }
    return aqua_void();
}

static int32_t lookup_member(aqua_runtime *rt, uint32_t self, uint32_t name, aqua_value *out, bool *cacheable) {
    handle *h = handle_get(rt, self);
    if (!h || h->kind == HANDLE_BOUND) return -1;
    size_t length;
    char *key = guest_text_utf8(rt, name, &length);
    int32_t index = class_member(h->cls, key);
    free(key);
    if (index < 0) return -1;
    *out = member_value(rt, self, (uint32_t)index, cacheable);
    return index;
}

/* ---- The service protocol (runtime/guest/runtime.c host_service) ---- */

int32_t host_service(aqua_runtime *rt, int32_t operation, int32_t context, int32_t subject, int32_t name, int32_t arguments, int32_t count, int32_t output) {
    switch (operation) {
    case 0: { /* resolve a global name in a context */
        aqua_value value; bool cacheable;
        handle *globals = handle_get(rt, rt->global_handle);
        size_t length;
        char *key = guest_text_utf8(rt, (uint32_t)name, &length);
        int32_t index = class_member(globals->cls, key);
        free(key);
        if (index < 0) return 1;
        const member *m = &globals->cls->members[index];
        if (m->kind == MEMBER_PROPERTY) {
            /* Context-dependent globals (the script's directory) are computed for the requesting script. */
            aqua_call call = {0};
            call.rt = rt; call.owner = globals->cls; call.member = m; call.self = rt->global_handle; call.context = (uint32_t)context; call.result = aqua_void();
            m->function(&call);
            for (scratch_block *b = call.scratch; b;) { scratch_block *next = b->next; free(b); b = next; }
            value = call.result;
        } else value = member_value(rt, rt->global_handle, (uint32_t)index, &cacheable);
        guest_write_value(rt, (uint32_t)output, value);
        return 0;
    }
    case 1: { /* call a native function */
        aqua_value small[16];
        aqua_value *values = count <= 16 ? small : malloc((size_t)count * sizeof *values);
        memcpy(values, guest_base(rt) + arguments, (size_t)count * sizeof *values);
        aqua_pending *pending = NULL;
        aqua_value result = call_native(rt, (uint32_t)subject, values, (uint32_t)count, (uint32_t)context, &pending);
        if (values != small) free(values);
        if (pending && result.type != AQUA_ERROR) {
            pending->task = rt->current;
            if (rt->current) rt->current->pending = pending;
            return 1;
        }
        guest_write_value(rt, (uint32_t)output, result);
        return 0;
    }
    case 2: { /* read a member of a native module or object */
        aqua_value value; bool cacheable = true;
        if (lookup_member(rt, (uint32_t)subject, (uint32_t)name, &value, &cacheable) < 0) return 1;
        guest_write_value(rt, (uint32_t)output, value);
        return cacheable ? 0 : 2;
    }
    case 3: { /* a guest scope holding a native module's members, for computed member calls */
        handle *h = handle_get(rt, (uint32_t)subject);
        if (!h || h->kind == HANDLE_BOUND) return 1;
        uint32_t scope = guest(rt, GUEST_scope, 0, 0, 0, 0);
        for (uint32_t i = 0; i < h->cls->member_count; i++) {
            bool cacheable;
            aqua_value v = member_value(rt, (uint32_t)subject, i, &cacheable);
            const char *names = h->cls->members[i].names;
            for (const char *p = names; *p;) {
                const char *end = strchr(p, '|');
                size_t n = end ? (size_t)(end - p) : strlen(p);
                aqua_value text = aqua_text(rt, p, n);
                guest(rt, GUEST_scope_set, scope, text.ref, guest_cell(rt, v), 0);
                if (!end) break;
                p = end + 1;
            }
        }
        aqua_value module = {AQUA_MODULE, scope, 0};
        guest_write_value(rt, (uint32_t)output, module);
        return 0;
    }
    case 4: return thread_service(rt, subject, arguments, count, output);
    case 5: return -1; /* a runtime JIT request (runtime/guest/runtime.c rt_jitRequest): engines with a JIT answer it themselves */
    }
    return 1;
}

/* ---- Imports ---- */

static bool ends_with(const char *s, const char *suffix) {
    size_t a = strlen(s), b = strlen(suffix);
    if (a < b) return false;
    for (size_t i = 0; i < b; i++) {
        char x = s[a - b + i], y = suffix[i];
        if ((x | 32) != (y | 32)) return false;
    }
    return true;
}

/* Joins `relative` onto `directory` and removes "." and ".." segments; NULL when it escapes the package. */
static char *normalize_path(const char *directory, const char *relative) {
    size_t capacity = strlen(directory) + strlen(relative) + 2;
    char *joined = malloc(capacity);
    snprintf(joined, capacity, "%s%s%s", directory, *directory ? "/" : "", relative);
    for (char *p = joined; *p; p++) if (*p == '\\') *p = '/';
    char **segments = malloc(capacity * sizeof *segments);
    size_t count = 0;
    for (char *token = strtok(joined, "/"); token; token = strtok(NULL, "/")) {
        if (strcmp(token, ".") == 0) continue;
        if (strcmp(token, "..") == 0) { if (count == 0) { free(segments); free(joined); return NULL; } count--; continue; }
        segments[count++] = token;
    }
    char *result = malloc(capacity);
    result[0] = 0;
    for (size_t i = 0; i < count; i++) { if (i) strcat(result, "/"); strcat(result, segments[i]); }
    free(segments); free(joined);
    return result;
}

aqua_value host_import(aqua_runtime *rt, uint32_t context, aqua_value name) {
    if (name.type != AQUA_STRING) return host_error(rt, "import / 匯入 expects a module name or .aqua path.");
    uint32_t portable = guest(rt, GUEST_portable_module, name.ref, 0, 0, 0);
    if (portable) { aqua_value v = {AQUA_MODULE, portable, 0}; return v; }
    size_t length;
    char *text = guest_text_utf8(rt, name.ref, &length);
    aqua_value result;
    aqua_class *module = registry_module(text);
    if (module) {
        aqua_value v = {AQUA_NATIVE, module_handle(rt, module), 0};
        free(text);
        return v;
    }
    char *path = NULL;
    if (!ends_with(text, ".aqua")) {
        /* Aquarius-source library modules bundled into the application, by any of their import names. */
        int32_t bundled = application_import(&rt->app, text);
        if (bundled >= 0) path = copy(rt->app.modules[bundled].path);
        else if (rt->app.import_count == 0) {
            /* ABI 3 applications bundle them as .aquarius/<name>.aqua. */
            size_t n = length + 16;
            path = malloc(n);
            snprintf(path, n, ".aquarius/%s.aqua", text);
        }
        if (!path || application_module(&rt->app, path) < 0) { result = host_error(rt, "Module not found: %s", text); free(path); free(text); return result; }
    } else {
        const char *relative = text;
        const char *directory = context < rt->context_count ? rt->contexts[context].directory : "";
        size_t app_length = strlen(rt->application_directory);
        if (strncmp(text, "/bottles/", 9) == 0) { relative = text + 1; directory = ""; }
        else if (app_length && strncmp(text, rt->application_directory, app_length) == 0 && (text[app_length] == '/' || text[app_length] == '\\')) {
            relative = text + app_length + 1; directory = ""; /* absolute paths into the package (目前工作目錄 + "x.aqua") */
        } else if (!app_length && text[0] == '/') { relative = text + 1; directory = ""; }
        else if (text[0] == '/' || (text[0] && text[1] == ':')) { result = host_error(rt, "匯入: %s is outside the application package.", text); free(text); return result; }
        path = normalize_path(directory, relative);
        if (!path || application_module(&rt->app, path) < 0) {
            result = host_error(rt, "匯入: Packaged script not found: %s", path ? path : text);
            free(path); free(text);
            return result;
        }
    }
    uint32_t index = (uint32_t)application_module(&rt->app, path);
    /* A module importing one whose import is still running (or the running entry) would recurse forever. */
    bool cycle = !rt->finished && index == rt->entry_module;
    for (uint32_t i = 0; i < rt->import_depth && !cycle; i++) cycle = rt->importing[i] == index;
    if (cycle || rt->import_depth == 64) {
        char chain[2048];
        size_t used = (size_t)snprintf(chain, sizeof chain, "%s", rt->app.modules[rt->entry_module].path);
        for (uint32_t i = 0; i < rt->import_depth && used < sizeof chain; i++)
            used += (size_t)snprintf(chain + used, sizeof chain - used, " -> %s", rt->app.modules[rt->importing[i]].path);
        if (used < sizeof chain) snprintf(chain + used, sizeof chain - used, " -> %s", rt->app.modules[index].path);
        result = host_error(rt, cycle ? "匯入: 循環匯入 / Circular import: %s" : "匯入: Imports nested too deeply: %s", chain);
        free(path); free(text);
        return result;
    }
    uint32_t scope = guest(rt, GUEST_scope, 0, 0, 0, 0);
    aqua_value module_value = {AQUA_MODULE, scope, 0};
    aqua_ref keep = aqua_retain(rt, module_value);
    aqua_value outcome;
    rt->importing[rt->import_depth++] = index;
    bool ok = run_module(rt, index, scope, &outcome);
    rt->import_depth--;
    aqua_release(rt, keep);
    free(path); free(text);
    return ok ? module_value : outcome;
}

/* ---- Lifecycle ---- */

aqua_runtime *runtime_create(uint8_t *module, size_t length, const runtime_options *options, char **error) {
    register_libraries();
    aqua_runtime *rt = calloc(1, sizeof *rt);
    if (!application_parse(&rt->app, module, length, error)) { free(rt); return NULL; }
    rt->start_ms = platform_now_ms();
    rt->quantum = options && options->quantum ? options->quantum : 65536;
    rt->application_directory = copy(options && options->application_directory ? options->application_directory : "");
    for (char *p = rt->application_directory; *p; p++) if (*p == '\\') *p = '/';
    if (options && options->launch_count) {
        rt->launch = calloc(options->launch_count, sizeof *rt->launch);
        for (uint32_t i = 0; i < options->launch_count; i++) rt->launch[i] = copy(options->launch[i]);
        rt->launch_count = options->launch_count;
    }
    rt->handles = calloc(HANDLE_LIMIT, sizeof *rt->handles);
    rt->handle_count = 1;
    rt->states = calloc(host_registry.library_count + 64, sizeof *rt->states);
    rt->post_lock = platform_lock_create();
    rt->engine = engine_create(rt->app.bytes, rt->app.length, service_entry, rt, error);
    if (!rt->engine) { rt->app.bytes = NULL; application_free(&rt->app); free(rt->handles); free(rt->states); free(rt); return NULL; }
    guest(rt, GUEST_initialize, rt->app.heap_start, 0, 0, 0);
    rt->collection_generation = guest(rt, GUEST_collection_generation, 0, 0, 0, 0);
    rt->global_handle = module_handle(rt, core_globals);
    if (options && options->materialize) rt->materialized = copy(options->materialize);
    rt->asset_files = calloc(rt->app.asset_count + 1, sizeof *rt->asset_files);
    return rt;
}

void runtime_destroy(aqua_runtime *rt) {
    if (!rt) return;
    threads_close(rt);
    for (uint32_t i = 1; i < rt->handle_count; i++) if (rt->handles[i].kind != HANDLE_FREE) handle_free(rt, i);
    for (uint32_t i = rt->state_count; i-- > 0;) {
        aqua_library *library = host_registry.libraries[rt->state_order[i]];
        if (library->destroy_state) library->destroy_state(rt, rt->states[library->index]);
    }
    platform_runtime_closing(rt);
    engine_destroy(rt->engine);
    for (task *t = rt->tasks; t;) { task *next = t->next; free(t); t = next; }
    for (timer *t = rt->timers; t;) { timer *next = t->next; free(t); t = next; }
    for (posted *p = rt->posts; p;) { posted *next = p->next; free(p); p = next; }
    for (uint32_t i = 0; i < rt->context_count; i++) { free(rt->contexts[i].script); free(rt->contexts[i].directory); }
    for (uint32_t i = 0; i < rt->launch_count; i++) free(rt->launch[i]);
    /* Only a directory an asset was written to (removing a tree costs ~50 ms on Windows even when there is none). */
    if (rt->materialized_created) platform_remove_tree(rt->materialized);
    platform_lock_destroy(rt->post_lock);
    for (uint32_t i = 0; i < rt->app.asset_count; i++) free(rt->asset_files[i]);
    free(rt->asset_files);
    application_free(&rt->app);
    free(rt->contexts); free(rt->launch); free(rt->handles); free(rt->states); free(rt->state_order);
    free(rt->application_directory); free(rt->materialized);
    free(rt);
}

/* ---- Runtime services of aqua.h ---- */

double aqua_now_ms(aqua_runtime *rt) { return platform_now_ms() - rt->start_ms; }
const char *aqua_platform(aqua_runtime *rt) { (void)rt; return AQUA_PLATFORM_NAME; }
void aqua_write(aqua_runtime *rt, const char *utf8, size_t length) { (void)rt; platform_write(1, utf8, length); }
void aqua_write_error(aqua_runtime *rt, const char *utf8, size_t length) { (void)rt; platform_write(2, utf8, length); }
const char *aqua_application_directory(aqua_runtime *rt) { return rt->application_directory; }
uint32_t aqua_launch_count(aqua_runtime *rt) { return rt->launch_count; }
const char *aqua_launch_argument(aqua_runtime *rt, uint32_t index) { return index < rt->launch_count ? rt->launch[index] : NULL; }
const char *aqua_environment(aqua_runtime *rt, const char *name) { (void)rt; return platform_environment(name); }

/* The packaged name of an asset a library asks for: "/bottles/<x>" and paths inside the application's directory
   (目前工作目錄 + "/assets/x") name the same assets as their package-relative form. */
static const char *asset_name(aqua_runtime *rt, const char *path) {
    if (strncmp(path, "/bottles/", 9) == 0) return path + 1;
    size_t n = rt->application_directory ? strlen(rt->application_directory) : 0;
    if (n && strncmp(path, rt->application_directory, n) == 0 && (path[n] == '/' || path[n] == '\\')) return path + n + 1;
    if (!n && path[0] == '/') return path + 1; /* The web has an empty application directory. */
    return path;
}
const uint8_t *aqua_asset(aqua_runtime *rt, const char *path, size_t *length) {
    int32_t i = application_asset(&rt->app, asset_name(rt, path));
    if (i < 0) return NULL;
    if (!rt->app.assets[i].data) {
        /* Assets stored beside the module (Android packages them as APK assets) load on first use. */
        size_t n = 0;
        rt->app.assets[i].data = platform_asset(rt, rt->app.assets[i].name, &n);
        if (!rt->app.assets[i].data) return NULL;
        rt->app.assets[i].length = (uint32_t)n;
    }
    if (length) *length = rt->app.assets[i].length;
    return rt->app.assets[i].data;
}
const char *aqua_asset_file(aqua_runtime *rt, const char *path) {
    int32_t i = application_asset(&rt->app, asset_name(rt, path));
    /* aqua_asset loads assets stored beside the module (Android) before they can become files. */
    if (i < 0 || !rt->materialized || !aqua_asset(rt, path, NULL)) return NULL;
    if (rt->asset_files[i]) return rt->asset_files[i];
    /* Assets become files under the runtime's private directory the first time a library asks for one by path. */
    size_t n = strlen(rt->materialized) + strlen(rt->app.assets[i].name) + 2;
    char *file = malloc(n);
    snprintf(file, n, "%s/%s", rt->materialized, rt->app.assets[i].name);
    char *slash = strrchr(file, '/');
    *slash = 0;
    rt->materialized_created |= platform_make_directories(file);
    *slash = '/';
    if (!platform_write_file(file, rt->app.assets[i].data, rt->app.assets[i].length)) { free(file); return NULL; }
    return rt->asset_files[i] = file;
}
