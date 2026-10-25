/* The web layer: virtual files and Storage over IndexedDB, picked files, the clipboard, printing and the semantic DOM
 * mirror, through the glue domains web-glue/{storage,files,clipboard,print,accessibility}. Locations are
 * persistent:/path (IndexedDB records), download:/name (saved through a download, kept for the run),
 * temporary:/lease/child (memory, for a live Temporary() lease) and selected:<n> (picked files and handles). The glue
 * only moves data; paths, conflicts, limits, transfers and errors are decided here. */
#include "platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* ---- Glue imports ---- */

typedef void (*glue_opened)(void *data, int32_t handle, char *error);
typedef void (*glue_finished)(void *data, char *error);
typedef void (*glue_record)(void *data, int32_t found, uint8_t *bytes, uint32_t length, int32_t directory, double modified);
typedef void (*glue_keys)(void *data, char *keys, uint32_t length);
typedef void (*glue_json)(void *data, char *json, char *error);
typedef void (*glue_bytes)(void *data, uint8_t *bytes, uint32_t length, char *error);
typedef void (*glue_clip)(void *data, char *text, uint32_t length, int32_t has_text, uint8_t *png, uint32_t png_length, char *error);

AQUA_WEB_IMPORT(storage_available) int32_t glue_storage_available(void);
AQUA_WEB_IMPORT(storage_open) void glue_storage_open(const char *name, uint32_t length, const char *store, uint32_t store_length, glue_opened done, void *data);
AQUA_WEB_IMPORT(storage_close) void glue_storage_close(int32_t database);
AQUA_WEB_IMPORT(storage_begin) int32_t glue_storage_begin(int32_t database, int32_t write);
AQUA_WEB_IMPORT(storage_finish) void glue_storage_finish(int32_t transaction, glue_finished done, void *data);
AQUA_WEB_IMPORT(storage_abort) void glue_storage_abort(int32_t transaction);
AQUA_WEB_IMPORT(storage_get) void glue_storage_get(int32_t transaction, const char *key, uint32_t length, glue_record done, void *data);
AQUA_WEB_IMPORT(storage_put) void glue_storage_put(int32_t transaction, const char *key, uint32_t length, const uint8_t *bytes, uint32_t size, int32_t directory, double modified);
AQUA_WEB_IMPORT(storage_delete) void glue_storage_delete(int32_t transaction, const char *key, uint32_t length);
AQUA_WEB_IMPORT(storage_keys) void glue_storage_keys(int32_t transaction, glue_keys done, void *data);

AQUA_WEB_IMPORT(files_features) int32_t glue_files_features(void);
AQUA_WEB_IMPORT(files_pick_open) void glue_files_pick_open(int32_t multiple, const char *types, uint32_t length, glue_json done, void *data);
AQUA_WEB_IMPORT(files_pick_input) void glue_files_pick_input(int32_t multiple, const char *accept, uint32_t length, int32_t capture, glue_json done, void *data);
AQUA_WEB_IMPORT(files_pick_save) void glue_files_pick_save(const char *name, uint32_t name_length, const char *types, uint32_t length, glue_json done, void *data);
AQUA_WEB_IMPORT(files_pick_directory) void glue_files_pick_directory(glue_json done, void *data);
AQUA_WEB_IMPORT(files_launch) void glue_files_launch(glue_json done, void *data);
AQUA_WEB_IMPORT(files_info) void glue_files_info(int32_t handle, glue_json done, void *data);
AQUA_WEB_IMPORT(files_read) void glue_files_read(int32_t handle, glue_bytes done, void *data);
AQUA_WEB_IMPORT(files_writable) int32_t glue_files_writable(int32_t handle);
AQUA_WEB_IMPORT(files_write) void glue_files_write(int32_t handle, const uint8_t *bytes, uint32_t length, glue_finished done, void *data);
AQUA_WEB_IMPORT(files_entries) void glue_files_entries(int32_t handle, glue_json done, void *data);
AQUA_WEB_IMPORT(files_remove) void glue_files_remove(int32_t handle, int32_t recursive, glue_finished done, void *data);
AQUA_WEB_IMPORT(files_download) void glue_files_download(const char *name, uint32_t name_length, const uint8_t *bytes, uint32_t length);
AQUA_WEB_IMPORT(files_release) void glue_files_release(int32_t handle);

AQUA_WEB_IMPORT(clipboard_features) int32_t glue_clipboard_features(void);
AQUA_WEB_IMPORT(clipboard_read) void glue_clipboard_read(int32_t image, glue_clip done, void *data);
AQUA_WEB_IMPORT(clipboard_types) void glue_clipboard_types(glue_json done, void *data);
AQUA_WEB_IMPORT(clipboard_write) void glue_clipboard_write(const char *text, uint32_t length, int32_t has_text, const uint8_t *png, uint32_t png_length, glue_finished done, void *data);
AQUA_WEB_IMPORT(print_image) void glue_print_image(const uint8_t *png, uint32_t length, glue_finished done, void *data);
AQUA_WEB_IMPORT(accessibility_update) void glue_accessibility_update(const char *json, uint32_t length);
AQUA_WEB_IMPORT(accessibility_close) void glue_accessibility_close(void);

enum { PICK_OPEN = 1, PICK_SAVE = 2, PICK_DIRECTORY = 4, LAUNCH_QUEUE = 8, SECURE = 16, FOCUSED = 32, PRINT = 64, LOCAL_FONTS = 128 };
enum { CLIP_API = 1, CLIP_READ = 2, CLIP_WRITE = 4, CLIP_ITEM = 8, CLIP_PNG = 16 };

/* "Name: message" from the glue (freed here) as a failure of the matching kind. */
static void glue_failure(char *text, app_error *e) {
    const char *message = text ? text : "Error: unknown failure";
    const char *colon = strstr(message, ": ");
    size_t name_length = colon ? (size_t)(colon - message) : 0;
    static const struct { const char *name; app_kind kind; } kinds[] = {
        {"AbortError", APP_CANCELLED}, {"NotAllowedError", APP_PERMISSION_DENIED}, {"SecurityError", APP_PERMISSION_DENIED},
        {"NotFoundError", APP_NOT_FOUND}, {"InvalidStateError", APP_UNAVAILABLE}, {"NotReadableError", APP_UNAVAILABLE},
        {"QuotaExceededError", APP_LIMIT_EXCEEDED}, {"NotSupportedError", APP_UNSUPPORTED}, {"TypeMismatchError", APP_INVALID_DATA}};
    app_kind kind = APP_INVALID_DATA;
    for (size_t i = 0; i < sizeof kinds / sizeof *kinds; i++)
        if (strlen(kinds[i].name) == name_length && strncmp(message, kinds[i].name, name_length) == 0) kind = kinds[i].kind;
    app_error_set(e, kind, "%s", colon ? colon + 2 : message);
    free(text);
}

static bool is_abort(const char *text) { return text && strncmp(text, "AbortError:", 11) == 0; }

static double now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_REALTIME, &t);
    return (double)t.tv_sec * 1000.0 + (double)(t.tv_nsec / 1000000);
}

/* ---- Per-runtime web state ---- */

typedef struct memory_file { char *id; app_bytes data; bool directory; double modified; struct memory_file *next; } memory_file;
typedef struct waiter { void *context; void (*run)(void *context, int32_t database, const app_error *e); struct waiter *next; } waiter;
typedef struct handle_list { int32_t handle; struct handle_list *next; } handle_list;
typedef struct web {
    int32_t database;          /* IndexedDB handle once open */
    int opening;               /* 0 not yet, 1 opening, 2 open, 3 failed */
    app_error failure;
    waiter *waiters;
    char *name;                /* "aquarius:<application>" */
    memory_file *memory;       /* temporary:/ and download:/ contents */
    handle_list *handles;      /* glue handles of picked files, released with the runtime */
    uint32_t next_selected;
} web;

static web *web_of(app_state *state) {
    if (!state->files) {
        web *w = calloc(1, sizeof *w);
        const char *id = aqua_environment(state->rt, "AQUARIUS_APPLICATION_ID");
        w->name = app_printf("aquarius:%s", id && *id ? id : "application");
        state->files = w;
    }
    return state->files;
}

void app_fs_close_state(app_state *state) {
    web *w = state->files;
    if (!w) return;
    for (memory_file *f = w->memory, *next; f; f = next) { next = f->next; free(f->id); app_bytes_free(&f->data); free(f); }
    for (handle_list *h = w->handles, *next; h; h = next) { next = h->next; glue_files_release(h->handle); free(h); }
    if (w->opening == 2) glue_storage_close(w->database);
    free(w->name);
    free(w);
    state->files = NULL;
}

void app_platform_close(app_state *state) { (void)state; glue_accessibility_close(); }

/* OS file streams do not exist on the web (streams there are buffers committed on Flush/Close). */
int64_t app_os_read(app_os_file *f, void *b, size_t c, app_error *e) { (void)f; (void)b; (void)c; app_error_set(e, APP_UNSUPPORTED, "No OS files on the web."); return -1; }
bool app_os_write(app_os_file *f, const void *d, size_t c, app_error *e) { (void)f; (void)d; (void)c; return app_error_set(e, APP_UNSUPPORTED, "No OS files on the web."); }
int64_t app_os_seek(app_os_file *f, int64_t o, int origin, app_error *e) { (void)f; (void)o; (void)origin; app_error_set(e, APP_UNSUPPORTED, "No OS files on the web."); return -1; }
bool app_os_flush(app_os_file *f, app_error *e) { (void)f; return app_error_set(e, APP_UNSUPPORTED, "No OS files on the web."); }
void app_os_close(app_os_file *f) { (void)f; }

/* ---- The database: opened on first use, shared by the runtime's operations ---- */

static void database_opened(void *data, int32_t handle, char *error) {
    web *w = data;
    if (handle) { w->database = handle; w->opening = 2; }
    else { w->opening = 3; glue_failure(error, &w->failure); }
    waiter *list = w->waiters;
    w->waiters = NULL;
    for (waiter *n = list, *next; n; n = next) { next = n->next; n->run(n->context, w->database, w->opening == 2 ? NULL : &w->failure); free(n); }
}

static void with_database(app_state *state, void *context, void (*run)(void *context, int32_t database, const app_error *e)) {
    web *w = web_of(state);
    if (w->opening == 0 && !glue_storage_available()) {
        w->opening = 3;
        app_error_set(&w->failure, APP_UNSUPPORTED, "IndexedDB persistent storage unavailable");
    }
    if (w->opening == 2 || w->opening == 3) { run(context, w->database, w->opening == 2 ? NULL : &w->failure); return; }
    waiter *n = calloc(1, sizeof *n);
    n->context = context; n->run = run; n->next = w->waiters;
    w->waiters = n;
    if (w->opening == 0) {
        w->opening = 1;
        glue_storage_open(w->name, (uint32_t)strlen(w->name), "resources", 9, database_opened, w);
    }
}

/* One IndexedDB transaction of a task: `start` issues requests (and more from their callbacks), `complete` runs once
   the transaction committed or aborted. An operation that finds a failure sets op->error and aborts. */
typedef struct web_op web_op;
typedef struct record { char *key; bool found, directory; app_bytes bytes; double modified; web_op *op; } record;
struct web_op {
    app_task *task;
    int32_t transaction;
    bool write;
    void (*start)(web_op *op);
    void (*complete)(web_op *op, const app_error *e);
    app_error error;
    char *path, *target;       /* store keys */
    bool flag, overwrite, move;
    const uint8_t *data; size_t length;
    record *records; size_t count, pending;
    record destination;
    void *done;                /* the backend caller's callback */
};

static void record_free(record *r) { free(r->key); app_bytes_free(&r->bytes); }
static void op_free(web_op *op) {
    for (size_t i = 0; i < op->count; i++) record_free(&op->records[i]);
    free(op->records);
    record_free(&op->destination);
    free(op->path); free(op->target);
    free(op);
}

static void op_finished(void *data, char *error) {
    web_op *op = data;
    app_error e = {0};
    if (op->error.failed) { free(error); op->complete(op, &op->error); }
    else if (error) { glue_failure(error, &e); op->complete(op, &e); }
    else op->complete(op, NULL);
    op_free(op);
}

static void op_begin(void *context, int32_t database, const app_error *e) {
    web_op *op = context;
    if (e) { op->complete(op, e); op_free(op); return; }
    op->transaction = glue_storage_begin(database, op->write);
    if (!op->transaction) {
        app_error failure = {0};
        app_error_set(&failure, APP_UNAVAILABLE, "Persistent transaction failed");
        op->complete(op, &failure);
        op_free(op);
        return;
    }
    glue_storage_finish(op->transaction, op_finished, op);
    op->start(op);
}

static void op_run(app_task *task, web_op *op) {
    op->task = task;
    with_database(task->state, op, op_begin);
}

static web_op *op_new(const char *path, bool write, void (*start)(web_op *), void (*complete)(web_op *, const app_error *), void *done) {
    web_op *op = calloc(1, sizeof *op);
    op->path = app_strdup(path);
    op->write = write;
    op->start = start;
    op->complete = complete;
    op->done = done;
    return op;
}

static void op_fail(web_op *op, app_kind kind, const char *message) {
    if (!op->error.failed) app_error_set(&op->error, kind, "%s", message);
    glue_storage_abort(op->transaction);
}

static void got_record(void *data, int32_t found, uint8_t *bytes, uint32_t length, int32_t directory, double modified) {
    record *r = data;
    r->found = found != 0;
    r->directory = directory != 0;
    r->modified = modified;
    if (bytes) { r->bytes.data = bytes; r->bytes.length = r->bytes.capacity = length; }
}

static void get_destination(web_op *op) { glue_storage_get(op->transaction, op->path, (uint32_t)strlen(op->path), got_record, &op->destination); }

/* ---- Resources ---- */

static char *scheme_id(const char *scheme, const char *path) { return app_printf("%s:/%s", scheme, path); }

static memory_file *memory_find(web *w, const char *id) {
    for (memory_file *f = w->memory; f; f = f->next) if (strcmp(f->id, id) == 0) return f;
    return NULL;
}

static memory_file *memory_put(web *w, const char *id) {
    memory_file *f = memory_find(w, id);
    if (!f) { f = calloc(1, sizeof *f); f->id = app_strdup(id); f->next = w->memory; w->memory = f; }
    return f;
}

static app_lease *live_lease(app_state *state, const char *name, size_t length) {
    for (app_lease *l = state->leases; l; l = l->next)
        if (!l->disposed && strlen(l->location) == length && strncmp(l->location, name, length) == 0) return l;
    return NULL;
}

static void fill(app_state *state, app_resource *r, app_backend backend, char *id, const char *location, int32_t kind) {
    memset(r, 0, sizeof *r);
    r->provider = app_strdup(state->provider);
    r->id = id;
    r->location = app_strdup(location);
    const char *slash = strrchr(location, '/');
    r->name = app_strdup(slash ? slash + 1 : location);
    r->kind = kind;
    r->backend = backend;
}

bool app_fs_capabilities(aqua_runtime *rt, aqua_value hash) {
    bool storage = glue_storage_available() != 0;
    app_set(rt, hash, "paths", aqua_bool(false));
    app_set(rt, hash, "streams", aqua_bool(true));
    app_set(rt, hash, "seek", aqua_bool(true));
    app_set(rt, hash, "directories", aqua_bool(true));
    app_set(rt, hash, "atomicReplace", aqua_bool(storage));
    app_set(rt, hash, "persistent", aqua_bool(storage));
    return true;
}

bool app_fs_resolve(app_state *state, const char *location, app_resource *out, app_error *e) {
    static const char *const schemes[] = {"persistent", "download", "temporary"};
    static const app_backend backends[] = {APP_PERSISTENT, APP_DOWNLOAD, APP_TEMPORARY};
    for (int i = 0; i < 3; i++) {
        size_t n = strlen(schemes[i]);
        if (strncmp(location, schemes[i], n) != 0 || location[n] != ':') continue;
        const char *path = location + n + 1;
        if (*path == '/') path++;
        if (!app_archive_name_valid(path, strlen(path))) return app_error_set(e, APP_INVALID_DATA, "Invalid archive resource name");
        int32_t kind = APP_FILE;
        if (backends[i] == APP_TEMPORARY) {
            const char *slash = strchr(path, '/');
            if (!live_lease(state, path, slash ? (size_t)(slash - path) : strlen(path)))
                return app_error_set(e, APP_NOT_FOUND, "Temporary resource lease has ended");
            char *id = scheme_id("temporary", path);
            memory_file *f = memory_find(web_of(state), id);
            if (f && f->directory) kind = APP_DIRECTORY;
            fill(state, out, APP_TEMPORARY, id, path, kind);
            out->lease = 0;
            return true;
        }
        fill(state, out, backends[i], scheme_id(schemes[i], path), path, kind);
        if (backends[i] == APP_PERSISTENT) out->identity = app_printf("%s:%s", web_of(state)->name, path);
        return true;
    }
    return app_error_set(e, APP_UNSUPPORTED, "Use a selected resource, persistent:/name or download:/name");
}

/* A resource for a picked file or handle. */
static void selected(app_state *state, app_resource *r, int32_t handle, const char *name, int32_t kind) {
    web *w = web_of(state);
    handle_list *h = calloc(1, sizeof *h);
    h->handle = handle; h->next = w->handles; w->handles = h;
    char *id = app_printf("selected:%u", ++w->next_selected);
    fill(state, r, APP_SELECTED, id, name, kind);
    free(r->name);
    r->name = app_strdup(name);
    r->handle = handle;
}

/* Resources from the glue's JSON list [{handle, name, kind}]. */
static app_resource *selected_list(app_task *task, const char *json, size_t *count) {
    aqua_value list;
    app_error e = {0};
    *count = 0;
    if (!json || !app_json_parse(task->rt, (const uint8_t *)json, strlen(json), &list, &e) || list.type != AQUA_ARRAY) return NULL;
    uint32_t n = aqua_array_length(task->rt, list);
    app_resource *items = calloc(n ? n : 1, sizeof *items);
    for (uint32_t i = 0; i < n; i++) {
        aqua_value item = aqua_array_get(task->rt, list, i), handle, name, kind;
        if (!aqua_hash_field(task->rt, item, "handle", &handle) || !aqua_hash_field(task->rt, item, "name", &name)) continue;
        aqua_hash_field(task->rt, item, "kind", &kind);
        char *text = aqua_text_dup(task->rt, name, NULL);
        selected(task->state, &items[(*count)++], (int32_t)handle.number, text ? text : "", (int32_t)kind.number);
        aqua_free(text);
    }
    return items;
}

/* ---- Stat ---- */

typedef void (*stat_done)(app_task *, const app_error *, const app_stat *);

static void stat_complete(web_op *op, const app_error *e) {
    app_stat s = {.length = -1};
    if (!e && op->destination.found) {
        s.exists = true;
        s.kind = op->destination.directory ? APP_DIRECTORY : APP_FILE;
        if (!op->destination.directory) s.length = (int64_t)op->destination.bytes.length;
        if (op->destination.modified >= 0) { s.modified_ms = op->destination.modified; s.has_modified = true; }
    }
    ((stat_done)op->done)(op->task, e, &s);
}

typedef struct info_op { app_task *task; stat_done done; } info_op;
static void info_done(void *data, char *json, char *error) {
    info_op *op = data;
    app_error e = {0};
    app_stat s = {.length = -1};
    aqua_value value, kind, size, modified;
    if (error || !json) { glue_failure(error, &e); op->done(op->task, &e, &s); free(json); free(op); return; }
    if (app_json_parse(op->task->rt, (const uint8_t *)json, strlen(json), &value, &e)) {
        s.exists = true;
        aqua_hash_field(op->task->rt, value, "kind", &kind);
        s.kind = (int32_t)kind.number;
        if (aqua_hash_field(op->task->rt, value, "size", &size) && aqua_is_number(size)) s.length = (int64_t)size.number;
        if (aqua_hash_field(op->task->rt, value, "modified", &modified) && aqua_is_number(modified)) { s.modified_ms = modified.number; s.has_modified = true; }
    }
    free(json);
    op->done(op->task, e.failed ? &e : NULL, &s);
    free(op);
}

void app_fs_stat(app_task *task, const app_resource *r, stat_done done) {
    web *w = web_of(task->state);
    app_stat s = {.length = -1};
    switch (r->backend) {
    case APP_PERSISTENT: {
        web_op *op = op_new(r->location, false, get_destination, stat_complete, (void *)done);
        op_run(task, op);
        return;
    }
    case APP_TEMPORARY: case APP_DOWNLOAD: {
        memory_file *f = memory_find(w, r->id);
        if (f) { s.exists = true; s.kind = f->directory ? APP_DIRECTORY : APP_FILE; if (!f->directory) s.length = (int64_t)f->data.length; }
        done(task, NULL, &s);
        return;
    }
    case APP_SELECTED: {
        info_op *op = calloc(1, sizeof *op);
        op->task = task; op->done = done;
        glue_files_info(r->handle, info_done, op);
        return;
    }
    default: break;
    }
    app_error e = {0};
    app_error_set(&e, APP_INVALID_DATA, "Expected resource owned by this browser provider");
    done(task, &e, &s);
}

/* ---- Read ---- */

typedef void (*read_done)(app_task *, const app_error *, app_bytes *);

static void read_complete(web_op *op, const app_error *e) {
    app_error error = {0};
    if (!e && (!op->destination.found || op->destination.directory)) { app_error_set(&error, APP_NOT_FOUND, "File resource missing"); e = &error; }
    if (!e && !app_check_bytes(op->destination.bytes.length, &error)) e = &error;
    ((read_done)op->done)(op->task, e, &op->destination.bytes);
}

typedef struct bytes_op { app_task *task; read_done done; } bytes_op;
static void bytes_read(void *data, uint8_t *bytes, uint32_t length, char *error) {
    bytes_op *op = data;
    app_error e = {0};
    app_bytes b = {bytes, length, length};
    if (error) glue_failure(error, &e);
    else app_check_bytes(length, &e);
    op->done(op->task, e.failed ? &e : NULL, &b);
    app_bytes_free(&b);
    free(op);
}

void app_fs_read(app_task *task, const app_resource *r, read_done done) {
    app_error e = {0};
    app_bytes empty = {0};
    switch (r->backend) {
    case APP_PERSISTENT: op_run(task, op_new(r->location, false, get_destination, read_complete, (void *)done)); return;
    case APP_TEMPORARY: case APP_DOWNLOAD: {
        memory_file *f = memory_find(web_of(task->state), r->id);
        if (!f || f->directory) { app_error_set(&e, APP_NOT_FOUND, "Resource has no data"); done(task, &e, &empty); return; }
        app_bytes copy = {0};
        app_bytes_append(&copy, f->data.data, f->data.length);
        done(task, NULL, &copy);
        app_bytes_free(&copy);
        return;
    }
    case APP_SELECTED: {
        bytes_op *op = calloc(1, sizeof *op);
        op->task = task; op->done = done;
        glue_files_read(r->handle, bytes_read, op);
        return;
    }
    default: app_error_set(&e, APP_INVALID_DATA, "Expected resource owned by this browser provider"); done(task, &e, &empty); return;
    }
}

/* ---- Replace ---- */

typedef void (*replace_done)(app_task *, const app_error *, bool);

static void put_start(web_op *op) {
    glue_storage_put(op->transaction, op->path, (uint32_t)strlen(op->path), op->data, (uint32_t)op->length, op->flag, now_ms());
}
static void replace_complete(web_op *op, const app_error *e) { ((replace_done)op->done)(op->task, e, true); }

typedef struct write_op { app_task *task; replace_done done; } write_op;
static void selected_written(void *data, char *error) {
    write_op *op = data;
    app_error e = {0};
    if (error) glue_failure(error, &e);
    op->done(op->task, e.failed ? &e : NULL, true);
    free(op);
}

void app_fs_replace(app_task *task, const app_resource *r, const uint8_t *data, size_t length, bool require_atomic, replace_done done) {
    app_error e = {0};
    if (!app_check_bytes(length, &e)) { done(task, &e, false); return; }
    if (app_task_cancelled(task)) return;
    web *w = web_of(task->state);
    switch (r->backend) {
    case APP_PERSISTENT: {
        web_op *op = op_new(r->location, true, put_start, replace_complete, (void *)done);
        op->data = data; op->length = length;
        op_run(task, op);
        return;
    }
    case APP_SELECTED: {
        if (!glue_files_writable(r->handle)) { app_error_set(&e, APP_PERMISSION_DENIED, "Selected File is read-only"); done(task, &e, false); return; }
        write_op *op = calloc(1, sizeof *op);
        op->task = task; op->done = done;
        glue_files_write(r->handle, data, (uint32_t)length, selected_written, op);
        return;
    }
    case APP_TEMPORARY: case APP_DOWNLOAD: {
        if (require_atomic && r->backend == APP_DOWNLOAD) { app_error_set(&e, APP_UNSUPPORTED, "Downloads cannot guarantee atomic replacement"); done(task, &e, false); return; }
        memory_file *f = memory_find(w, r->id);
        if (f && f->directory) { app_error_set(&e, APP_PERMISSION_DENIED, "Access to the resource '%s' is denied.", r->id); done(task, &e, false); return; }
        if (r->backend == APP_TEMPORARY) {
            /* children of a temporary directory need their parent */
            const char *slash = strrchr(r->location, '/');
            if (slash) {
                char *parent = app_printf("temporary:/%.*s", (int)(slash - r->location), r->location);
                memory_file *p = memory_find(w, parent);
                free(parent);
                if (!p || !p->directory) { app_error_set(&e, APP_NOT_FOUND, "Could not find a part of the path '%s'.", r->id); done(task, &e, false); return; }
            } else if (!f) { app_error_set(&e, APP_NOT_FOUND, "Temporary resource lease has ended"); done(task, &e, false); return; }
        }
        f = memory_put(w, r->id);
        f->data.length = 0;
        app_bytes_append(&f->data, data, length);
        f->modified = now_ms();
        if (r->backend == APP_DOWNLOAD) glue_files_download(r->name, (uint32_t)strlen(r->name), data, (uint32_t)length);
        done(task, NULL, r->backend == APP_TEMPORARY);
        return;
    }
    default: app_error_set(&e, APP_INVALID_DATA, "Expected resource owned by this browser provider"); done(task, &e, false); return;
    }
}

/* ---- Directories ---- */

static void directory_complete(web_op *op, const app_error *e) { ((app_done)op->done)(op->task, e); }

void app_fs_create_directory(app_task *task, const app_resource *r, app_done done) {
    app_error e = {0};
    if (r->backend == APP_PERSISTENT) {
        web_op *op = op_new(r->location, true, put_start, directory_complete, (void *)done);
        op->flag = true;
        op_run(task, op);
        return;
    }
    if (r->backend == APP_TEMPORARY) {
        memory_file *f = memory_put(web_of(task->state), r->id);
        f->directory = true;
        done(task, NULL);
        return;
    }
    app_error_set(&e, APP_UNSUPPORTED, "Create directories in persistent storage");
    done(task, &e);
}

/* Keys directly inside `prefix` ("dir/"): the children of a directory. */
static bool direct_child(const char *key, const char *prefix) {
    size_t n = strlen(prefix);
    return strncmp(key, prefix, n) == 0 && key[n] && !strchr(key + n, '/');
}

typedef void (*list_done)(app_task *, const app_error *, app_resource *, size_t);

static void listed_keys(void *data, char *keys, uint32_t length) {
    web_op *op = data;
    char *prefix = app_printf("%s/", op->path);
    size_t capacity = 16;
    op->records = calloc(capacity, sizeof *op->records);
    for (uint32_t i = 0; i < length;) {
        const char *key = keys + i;
        size_t n = strlen(key);
        i += (uint32_t)n + 1;
        if (!direct_child(key, prefix)) continue;
        if (op->count == capacity) { capacity *= 2; op->records = realloc(op->records, capacity * sizeof *op->records); }
        record *r = &op->records[op->count++];
        memset(r, 0, sizeof *r);
        r->key = app_strdup(key);
        if (op->count > APP_MAX_ENTRIES) { op_fail(op, APP_LIMIT_EXCEEDED, "Directory entries"); break; }
    }
    free(prefix);
    free(keys);
    if (op->error.failed) return;
    for (size_t i = 0; i < op->count; i++) glue_storage_get(op->transaction, op->records[i].key, (uint32_t)strlen(op->records[i].key), got_record, &op->records[i]);
}

static void list_start(web_op *op) { glue_storage_keys(op->transaction, listed_keys, op); }

static int compare_records(const void *a, const void *b) { return strcmp(((const record *)a)->key, ((const record *)b)->key); }

static void list_complete(web_op *op, const app_error *e) {
    list_done done = (list_done)op->done;
    if (e) { done(op->task, e, NULL, 0); return; }
    qsort(op->records, op->count, sizeof *op->records, compare_records);
    app_resource *items = calloc(op->count ? op->count : 1, sizeof *items);
    size_t n = 0;
    for (size_t i = 0; i < op->count; i++) {
        if (!op->records[i].found) continue;
        app_error ignored = {0};
        char *location = app_printf("persistent:/%s", op->records[i].key);
        if (app_fs_resolve(op->task->state, location, &items[n], &ignored)) { items[n].kind = op->records[i].directory ? APP_DIRECTORY : APP_FILE; n++; }
        free(location);
    }
    done(op->task, NULL, items, n);
}

typedef struct entries_op { app_task *task; list_done done; } entries_op;
static void entries_listed(void *data, char *json, char *error) {
    entries_op *op = data;
    app_error e = {0};
    if (error) { glue_failure(error, &e); op->done(op->task, &e, NULL, 0); free(op); return; }
    size_t count;
    app_resource *items = selected_list(op->task, json, &count);
    free(json);
    if (count > APP_MAX_ENTRIES) {
        for (size_t i = 0; i < count; i++) app_resource_free(&items[i]);
        free(items);
        app_error_set(&e, APP_LIMIT_EXCEEDED, "Directory entries");
        op->done(op->task, &e, NULL, 0);
    } else op->done(op->task, NULL, items, count);
    free(op);
}

void app_fs_enumerate(app_task *task, const app_resource *r, list_done done) {
    app_error e = {0};
    web *w = web_of(task->state);
    if (r->backend == APP_PERSISTENT) { op_run(task, op_new(r->location, false, list_start, list_complete, (void *)done)); return; }
    if (r->backend == APP_SELECTED && r->kind == APP_DIRECTORY) {
        entries_op *op = calloc(1, sizeof *op);
        op->task = task; op->done = done;
        glue_files_entries(r->handle, entries_listed, op);
        return;
    }
    memory_file *dir = r->backend == APP_TEMPORARY ? memory_find(w, r->id) : NULL;
    if (!dir || !dir->directory) { app_error_set(&e, APP_UNSUPPORTED, "Resource is not a persistent directory"); done(task, &e, NULL, 0); return; }
    char *prefix = app_printf("%s/", r->id);
    size_t count = 0, capacity = 8;
    char **ids = malloc(capacity * sizeof *ids);
    for (memory_file *f = w->memory; f; f = f->next)
        if (direct_child(f->id, prefix)) { if (count == capacity) { capacity *= 2; ids = realloc(ids, capacity * sizeof *ids); } ids[count++] = f->id; }
    free(prefix);
    for (size_t i = 1; i < count; i++) for (size_t k = i; k > 0 && strcmp(ids[k - 1], ids[k]) > 0; k--) { char *t = ids[k]; ids[k] = ids[k - 1]; ids[k - 1] = t; }
    app_resource *items = calloc(count ? count : 1, sizeof *items);
    size_t n = 0;
    for (size_t i = 0; i < count; i++) if (app_fs_resolve(task->state, ids[i], &items[n], &e)) n++;
    free(ids);
    done(task, NULL, items, n);
}

/* ---- Delete ---- */

static void deleted_keys(void *data, char *keys, uint32_t length) {
    web_op *op = data;
    char *prefix = app_printf("%s/", op->path);
    size_t children = 0;
    for (uint32_t i = 0; i < length; i += (uint32_t)strlen(keys + i) + 1) if (strncmp(keys + i, prefix, strlen(prefix)) == 0) children++;
    if (children && !op->flag) { free(prefix); free(keys); op_fail(op, APP_CONFLICT, "Directory not empty"); return; }
    for (uint32_t i = 0; i < length; i += (uint32_t)strlen(keys + i) + 1)
        if (strncmp(keys + i, prefix, strlen(prefix)) == 0) glue_storage_delete(op->transaction, keys + i, (uint32_t)strlen(keys + i));
    glue_storage_delete(op->transaction, op->path, (uint32_t)strlen(op->path));
    free(prefix);
    free(keys);
}
static void delete_start(web_op *op) { glue_storage_keys(op->transaction, deleted_keys, op); }

typedef struct remove_op { app_task *task; app_done done; } remove_op;
static void selected_removed(void *data, char *error) {
    remove_op *op = data;
    app_error e = {0};
    if (error) glue_failure(error, &e);
    op->done(op->task, e.failed ? &e : NULL);
    free(op);
}

void app_fs_delete(app_task *task, const app_resource *r, bool recursive, app_done done) {
    app_error e = {0};
    web *w = web_of(task->state);
    switch (r->backend) {
    case APP_PERSISTENT: {
        web_op *op = op_new(r->location, true, delete_start, directory_complete, (void *)done);
        op->flag = recursive;
        op_run(task, op);
        return;
    }
    case APP_TEMPORARY: case APP_DOWNLOAD: {
        char *prefix = app_printf("%s/", r->id);
        bool children = false;
        for (memory_file *f = w->memory; f; f = f->next) if (strncmp(f->id, prefix, strlen(prefix)) == 0) children = true;
        if (children && !recursive) { free(prefix); app_error_set(&e, APP_CONFLICT, "Directory not empty"); done(task, &e); return; }
        for (memory_file **p = &w->memory; *p;) {
            memory_file *f = *p;
            if (strcmp(f->id, r->id) == 0 || strncmp(f->id, prefix, strlen(prefix)) == 0) { *p = f->next; free(f->id); app_bytes_free(&f->data); free(f); }
            else p = &f->next;
        }
        free(prefix);
        if (r->backend == APP_TEMPORARY && !strchr(r->location, '/')) {
            app_lease *lease = live_lease(task->state, r->location, strlen(r->location));
            if (lease) lease->disposed = true;
        }
        done(task, NULL);
        return;
    }
    case APP_SELECTED: {
        remove_op *op = calloc(1, sizeof *op);
        op->task = task; op->done = done;
        glue_files_remove(r->handle, recursive, selected_removed, op);
        return;
    }
    default: app_error_set(&e, APP_INVALID_DATA, "Expected resource owned by this browser provider"); done(task, &e); return;
    }
}

/* ---- Copy and move: one transaction between persistent locations, read-then-write otherwise ---- */

static void transfer_got(void *data, int32_t found, uint8_t *bytes, uint32_t length, int32_t directory, double modified);

static void transfer_keys(void *data, char *keys, uint32_t length) {
    web_op *op = data;
    char *prefix = app_printf("%s/", op->path);
    size_t capacity = 16;
    op->records = calloc(capacity, sizeof *op->records);
    for (uint32_t i = 0; i < length; i += (uint32_t)strlen(keys + i) + 1) {
        const char *key = keys + i;
        if (strcmp(key, op->path) != 0 && strncmp(key, prefix, strlen(prefix)) != 0) continue;
        if (op->count == capacity) { capacity *= 2; op->records = realloc(op->records, capacity * sizeof *op->records); }
        memset(&op->records[op->count], 0, sizeof *op->records);
        op->records[op->count].op = op;
        op->records[op->count++].key = app_strdup(key);
    }
    free(prefix);
    free(keys);
    op->pending = op->count + 1;
    op->destination.key = app_strdup(op->target);
    op->destination.op = op;
    glue_storage_get(op->transaction, op->target, (uint32_t)strlen(op->target), transfer_got, &op->destination);
    for (size_t i = 0; i < op->count; i++) glue_storage_get(op->transaction, op->records[i].key, (uint32_t)strlen(op->records[i].key), transfer_got, &op->records[i]);
}

static void transfer_got(void *data, int32_t found, uint8_t *bytes, uint32_t length, int32_t directory, double modified) {
    record *r = data;
    got_record(r, found, bytes, length, directory, modified);
    web_op *op = r->op;
    if (--op->pending) return;
    record *root = NULL;
    uint64_t total = 0;
    for (size_t i = 0; i < op->count; i++) {
        if (strcmp(op->records[i].key, op->path) == 0) root = &op->records[i];
        total += op->records[i].bytes.length;
    }
    if (!root || !root->found) { op_fail(op, APP_NOT_FOUND, "Source missing"); return; }
    if (op->destination.found && (!op->overwrite || root->directory || op->destination.directory)) { op_fail(op, APP_CONFLICT, "Destination exists"); return; }
    if (op->count > APP_MAX_ENTRIES) { op_fail(op, APP_LIMIT_EXCEEDED, "Resource entries"); return; }
    if (total > APP_MAX_BYTES) { op_fail(op, APP_LIMIT_EXCEEDED, "Data exceeds the byte limit"); return; }
    size_t from = strlen(op->path);
    for (size_t i = 0; i < op->count; i++) {
        record *s = &op->records[i];
        char *key = app_printf("%s%s", op->target, s->key + from);
        glue_storage_put(op->transaction, key, (uint32_t)strlen(key), s->bytes.data, (uint32_t)s->bytes.length, s->directory, s->modified >= 0 ? s->modified : now_ms());
        if (op->move) glue_storage_delete(op->transaction, s->key, (uint32_t)strlen(s->key));
        free(key);
    }
}

static void transfer_start(web_op *op) { glue_storage_keys(op->transaction, transfer_keys, op); }

typedef struct copy_op { app_resource from, to; bool overwrite; app_done done; app_bytes data; } copy_op;
static void copy_free(copy_op *c) { app_resource_free(&c->from); app_resource_free(&c->to); app_bytes_free(&c->data); free(c); }

static void copy_written(app_task *task, const app_error *e, bool atomic) {
    (void)atomic;
    copy_op *c = task->backend;
    task->backend = NULL;
    app_done done = c->done;
    copy_free(c);
    done(task, e);
}
static void copy_read(app_task *task, const app_error *e, app_bytes *data) {
    copy_op *c = task->backend;
    if (e) { task->backend = NULL; app_done done = c->done; copy_free(c); done(task, e); return; }
    app_bytes_append(&c->data, data->data, data->length);
    app_fs_replace(task, &c->to, c->data.data, c->data.length, false, copy_written);
}
static void copy_checked(app_task *task, const app_error *e, const app_stat *s) {
    copy_op *c = task->backend;
    app_error error = {0};
    if (!e && s->exists && !c->overwrite) { app_error_set(&error, APP_CONFLICT, "Destination exists"); e = &error; }
    if (e) { task->backend = NULL; app_done done = c->done; copy_free(c); done(task, e); return; }
    app_fs_read(task, &c->from, copy_read);
}

static bool overlapping(const char *a, const char *b) {
    size_t na = strlen(a), nb = strlen(b);
    return strcmp(a, b) == 0 || (nb > na && strncmp(b, a, na) == 0 && b[na] == '/') || (na > nb && strncmp(a, b, nb) == 0 && a[nb] == '/');
}

static void transfer(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, bool move, app_done done) {
    app_error e = {0};
    if (from->backend == APP_PERSISTENT && to->backend == APP_PERSISTENT) {
        if (overlapping(from->location, to->location)) { app_error_set(&e, APP_CONFLICT, "Overlapping resource locations"); done(task, &e); return; }
        web_op *op = op_new(from->location, true, transfer_start, directory_complete, (void *)done);
        op->target = app_strdup(to->location);
        op->overwrite = overwrite;
        op->move = move;
        op_run(task, op);
        return;
    }
    if (from->backend == APP_TEMPORARY && to->backend == APP_TEMPORARY) {
        /* Within the run's memory: whole directories copy or move like persistent ones. */
        web *w = web_of(task->state);
        memory_file *source = memory_find(w, from->id), *target = memory_find(w, to->id);
        const char *slash = strrchr(to->location, '/');
        char *parent_id = slash ? app_printf("temporary:/%.*s", (int)(slash - to->location), to->location) : NULL;
        memory_file *parent = parent_id ? memory_find(w, parent_id) : NULL;
        free(parent_id);
        if (overlapping(from->id, to->id)) app_error_set(&e, APP_CONFLICT, "Overlapping resource locations");
        else if (!source) app_error_set(&e, APP_NOT_FOUND, "Source missing");
        else if (target && (!overwrite || source->directory || target->directory)) app_error_set(&e, APP_CONFLICT, "Destination exists");
        else if (!parent || !parent->directory) app_error_set(&e, APP_NOT_FOUND, "Could not find a part of the path '%s'.", to->id);
        if (e.failed) { done(task, &e); return; }
        size_t from_length = strlen(from->id), count = 0;
        for (memory_file *f = w->memory; f; f = f->next) if (strncmp(f->id, from->id, from_length) == 0 && (!f->id[from_length] || f->id[from_length] == '/')) count++;
        if (count > APP_MAX_ENTRIES) { app_error_set(&e, APP_LIMIT_EXCEEDED, "Resource entries"); done(task, &e); return; }
        memory_file **sources = malloc((count ? count : 1) * sizeof *sources);
        size_t n = 0;
        for (memory_file *f = w->memory; f; f = f->next) if (strncmp(f->id, from->id, from_length) == 0 && (!f->id[from_length] || f->id[from_length] == '/')) sources[n++] = f;
        for (size_t i = 0; i < n; i++) {
            char *id = app_printf("%s%s", to->id, sources[i]->id + from_length);
            memory_file *copy = memory_put(w, id);
            free(id);
            copy->directory = sources[i]->directory;
            copy->data.length = 0;
            app_bytes_append(&copy->data, sources[i]->data.data, sources[i]->data.length);
            copy->modified = sources[i]->modified;
        }
        if (move)
            for (memory_file **p = &w->memory; *p;) {
                memory_file *f = *p;
                bool moved = false;
                for (size_t i = 0; i < n && !moved; i++) moved = sources[i] == f;
                if (moved) { *p = f->next; free(f->id); app_bytes_free(&f->data); free(f); }
                else p = &f->next;
            }
        free(sources);
        done(task, NULL);
        return;
    }
    if (move) { app_error_set(&e, APP_UNSUPPORTED, "Move requires two persistent resources"); done(task, &e); return; }
    copy_op *c = calloc(1, sizeof *c);
    c->from = app_resource_copy(from);
    c->to = app_resource_copy(to);
    c->overwrite = overwrite;
    c->done = done;
    task->backend = c;
    app_fs_stat(task, &c->to, copy_checked);
}

void app_fs_copy(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done) { transfer(task, from, to, overwrite, false, done); }
void app_fs_move(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done) { transfer(task, from, to, overwrite, true, done); }

/* ---- Temporary leases ---- */

bool app_fs_temporary(app_state *state, bool directory, app_resource *out, app_error *e) {
    char name[33];
    app_random_hex(name, 32);
    app_lease *lease = calloc(1, sizeof *lease);
    lease->id = ++state->next_lease;
    lease->directory = directory;
    lease->location = app_strdup(name);
    lease->next = state->leases;
    state->leases = lease;
    char *id = scheme_id("temporary", name);
    memory_file *f = memory_put(web_of(state), id);
    f->directory = directory;
    free(id);
    if (!app_fs_resolve(state, f->id, out, e)) return false;
    out->kind = directory ? APP_DIRECTORY : APP_FILE;
    out->lease = lease->id;
    return true;
}

void app_fs_dispose(app_state *state, app_lease *lease) {
    web *w = state->files;
    lease->disposed = true;
    if (!w) return;
    char *id = scheme_id("temporary", lease->location), *prefix = app_printf("%s/", id);
    for (memory_file **p = &w->memory; *p;) {
        memory_file *f = *p;
        if (strcmp(f->id, id) == 0 || strncmp(f->id, prefix, strlen(prefix)) == 0) { *p = f->next; free(f->id); app_bytes_free(&f->data); free(f); }
        else p = &f->next;
    }
    free(id);
    free(prefix);
}

/* ---- Streams: the whole content in memory, committed by Flush/Close ---- */

typedef struct open_op { app_resource resource; int mode; void (*done)(app_task *, const app_error *, app_stream *); } open_op;

static void open_read(app_task *task, const app_error *e, app_bytes *data) {
    open_op *o = task->backend;
    task->backend = NULL;
    if (e) { app_resource_free(&o->resource); void (*done)(app_task *, const app_error *, app_stream *) = o->done; free(o); done(task, e, NULL); return; }
    app_stream *s = app_stream_new(task->state, o->mode);
    app_bytes_append(&s->memory, data->data, data->length);
    s->position = o->mode == APP_OPEN_APPEND ? s->memory.length : 0;
    s->start = o->mode == APP_OPEN_APPEND ? s->memory.length : 0;
    s->resource = o->resource;
    void (*done)(app_task *, const app_error *, app_stream *) = o->done;
    free(o);
    done(task, NULL, s);
}

void app_fs_open(app_task *task, const app_resource *r, int mode, void (*done)(app_task *, const app_error *, app_stream *)) {
    app_error e = {0};
    if (mode != APP_OPEN_READ && r->backend == APP_SELECTED && !glue_files_writable(r->handle)) {
        app_error_set(&e, APP_PERMISSION_DENIED, "Resource is read-only");
        done(task, &e, NULL);
        return;
    }
    if (mode == APP_OPEN_CREATE) {
        app_stream *s = app_stream_new(task->state, mode);
        s->resource = app_resource_copy(r);
        s->dirty = true;
        done(task, NULL, s);
        return;
    }
    open_op *o = calloc(1, sizeof *o);
    o->resource = app_resource_copy(r);
    o->mode = mode;
    o->done = done;
    task->backend = o;
    app_fs_read(task, &o->resource, open_read);
}

/* ---- Storage: "settings/<key>" records of the same database ---- */

static app_resource storage_directory(app_state *state, const char *name) {
    app_resource r;
    char *location = app_printf("persistent:/%s", name);
    fill(state, &r, APP_PERSISTENT, location, name, APP_DIRECTORY);
    return r;
}

bool app_storage_directories(app_state *state, app_resource *settings, app_resource *data, app_error *e) {
    (void)e;
    *settings = storage_directory(state, "settings");
    *data = storage_directory(state, "data");
    return true;
}

typedef void (*key_read)(app_task *, const app_error *, app_bytes *, bool);
static void key_complete(web_op *op, const app_error *e) {
    bool found = !e && op->destination.found && !op->destination.directory;
    ((key_read)op->done)(op->task, e, &op->destination.bytes, found);
}

void app_storage_read(app_task *task, const char *key, key_read done) {
    char *path = app_printf("settings/%s", key);
    op_run(task, op_new(path, false, get_destination, key_complete, (void *)done));
    free(path);
}

void app_storage_write(app_task *task, const char *key, const uint8_t *data, size_t length, app_done done) {
    app_error e = {0};
    if (!app_check_bytes(length, &e)) { done(task, &e); return; }
    char *path = app_printf("settings/%s", key);
    web_op *op = op_new(path, true, put_start, directory_complete, (void *)done);
    op->data = data; op->length = length;
    op_run(task, op);
    free(path);
}

static void key_delete_start(web_op *op) { glue_storage_delete(op->transaction, op->path, (uint32_t)strlen(op->path)); }
void app_storage_delete(app_task *task, const char *key, app_done done) {
    char *path = app_printf("settings/%s", key);
    op_run(task, op_new(path, true, key_delete_start, directory_complete, (void *)done));
    free(path);
}

/* ---- Clipboard ---- */

static bool ready(app_error *e) {
    int32_t features = glue_files_features();
    if (!(features & SECURE)) return app_error_set(e, APP_PERMISSION_DENIED, "Secure context required");
    if (!(features & FOCUSED)) return app_error_set(e, APP_PERMISSION_DENIED, "Focus the application first");
    return true;
}

void app_os_clipboard_capabilities(aqua_runtime *rt, aqua_value hash) {
    int32_t c = glue_clipboard_features();
    app_set(rt, hash, "text", app_capability(rt, (c & CLIP_API) != 0, "Secure context, focus and browser permission required"));
    app_set(rt, hash, "image", app_capability(rt, (c & (CLIP_READ | CLIP_WRITE | CLIP_ITEM)) == (CLIP_READ | CLIP_WRITE | CLIP_ITEM),
                                              "PNG clipboard support depends on browser permissions and activation"));
    app_set(rt, hash, "multiple", app_capability(rt, (c & (CLIP_WRITE | CLIP_ITEM)) == (CLIP_WRITE | CLIP_ITEM), NULL));
}

typedef struct clip_op { app_task *task; void *done; } clip_op;

static void types_read(void *data, char *json, char *error) {
    clip_op *op = data;
    void (*done)(app_task *, const app_error *, const char *const *, size_t) = op->done;
    app_error e = {0};
    aqua_value list;
    if (error) { glue_failure(error, &e); done(op->task, &e, NULL, 0); free(op); return; }
    const char *formats[64];
    char *texts[64];
    size_t count = 0;
    if (json && app_json_parse(op->task->rt, (const uint8_t *)json, strlen(json), &list, &e) && list.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(op->task->rt, list);
        for (uint32_t i = 0; i < n && count < 64; i++) {
            texts[count] = aqua_text_dup(op->task->rt, aqua_array_get(op->task->rt, list, i), NULL);
            if (texts[count]) { formats[count] = texts[count]; count++; }
        }
    }
    free(json);
    done(op->task, NULL, formats, count);
    for (size_t i = 0; i < count; i++) aqua_free(texts[i]);
    free(op);
}

void app_os_clipboard_formats(app_task *task, void (*done)(app_task *, const app_error *, const char *const *, size_t)) {
    app_error e = {0};
    int32_t c = glue_clipboard_features();
    if (!ready(&e)) { done(task, &e, NULL, 0); return; }
    if (!(c & CLIP_API)) { app_error_set(&e, APP_UNSUPPORTED, "Clipboard API unavailable"); done(task, &e, NULL, 0); return; }
    if (!(c & CLIP_READ)) { app_error_set(&e, APP_UNSUPPORTED, "Browser cannot query clipboard formats"); done(task, &e, NULL, 0); return; }
    clip_op *op = calloc(1, sizeof *op);
    op->task = task; op->done = (void *)done;
    glue_clipboard_types(types_read, op);
}

typedef void (*clip_read_done)(app_task *, const app_error *, const char *, size_t, const uint8_t *, size_t);
static void clipboard_read(void *data, char *text, uint32_t length, int32_t has_text, uint8_t *png, uint32_t png_length, char *error) {
    clip_op *op = data;
    app_error e = {0};
    if (error) glue_failure(error, &e);
    else if (!app_check_bytes(length, &e)) {}
    else if (!app_check_bytes(png_length, &e)) {}
    if (e.failed) ((clip_read_done)op->done)(op->task, &e, NULL, 0, NULL, 0);
    else ((clip_read_done)op->done)(op->task, NULL, has_text ? text : NULL, length, png, png_length);
    free(text);
    free(png);
    free(op);
}

void app_os_clipboard_read(app_task *task, bool image, clip_read_done done) {
    app_error e = {0};
    if (!ready(&e)) { done(task, &e, NULL, 0, NULL, 0); return; }
    if (!(glue_clipboard_features() & CLIP_API)) { app_error_set(&e, APP_UNSUPPORTED, "Clipboard API unavailable"); done(task, &e, NULL, 0, NULL, 0); return; }
    clip_op *op = calloc(1, sizeof *op);
    op->task = task; op->done = (void *)done;
    glue_clipboard_read(image, clipboard_read, op);
}

typedef struct finish_op { app_task *task; app_done done; app_bytes keep; } finish_op;
static void finished(void *data, char *error) {
    finish_op *op = data;
    app_error e = {0};
    if (error) glue_failure(error, &e);
    op->done(op->task, e.failed ? &e : NULL);
    app_bytes_free(&op->keep);
    free(op);
}

void app_os_clipboard_write(app_task *task, const char *text, size_t text_length, const app_picture *image, app_done done) {
    app_error e = {0};
    int32_t c = glue_clipboard_features();
    if (!ready(&e)) { done(task, &e); return; }
    if (!(c & CLIP_API)) { app_error_set(&e, APP_UNSUPPORTED, "Clipboard API unavailable"); done(task, &e); return; }
    if (!text && !image) { app_error_set(&e, APP_INVALID_DATA, "Empty clipboard content"); done(task, &e); return; }
    finish_op *op = calloc(1, sizeof *op);
    op->task = task; op->done = done;
    if (image) {
        if ((c & (CLIP_WRITE | CLIP_ITEM | CLIP_PNG)) != (CLIP_WRITE | CLIP_ITEM | CLIP_PNG)) { free(op); app_error_set(&e, APP_UNSUPPORTED, "PNG clipboard unavailable"); done(task, &e); return; }
        app_encode_options options;
        app_encode_defaults(&options);
        if (!app_image_encode(image, APP_PNG, &options, &op->keep, &e)) { free(op); done(task, &e); return; }
    }
    glue_clipboard_write(text ? text : "", (uint32_t)text_length, text != NULL, op->keep.data, (uint32_t)op->keep.length, finished, op);
}

/* ---- Application services ---- */

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    int32_t f = glue_files_features();
    app_set(rt, hash, "files", app_capability(rt, true, "Selected resources, downloads and scoped persistent storage"));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, glue_storage_available() != 0, "Origin quota and eviction apply"));
    app_set(rt, hash, "pickers", app_capability(rt, true, "User activation required; input/download fallbacks"));
    app_set(rt, hash, "directoryPicker", app_capability(rt, (f & PICK_DIRECTORY) != 0, NULL));
    app_set(rt, hash, "printing", app_capability(rt, (f & PRINT) != 0, "Browser owns dialog; completion/cancellation cannot be confirmed"));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, true, "Image picker; mobile browsers may offer camera capture"));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, (f & LOCAL_FONTS) != 0, "Local font permission required"));
    app_set(rt, hash, "accessibility", app_capability(rt, true, "Semantic DOM mirror of application-owned state"));
    app_set(rt, hash, "fileAssociations", app_capability(rt, false, "Installable web app manifest/OS installation must declare associations"));
    app_set(rt, hash, "activation", app_capability(rt, (f & LAUNCH_QUEUE) != 0, "Requires installed web app with file handlers"));
}

typedef struct pick_op { app_task *task; app_picked done; bool save; char *suggested; } pick_op;

static void picked(void *data, char *json, char *error) {
    pick_op *op = data;
    app_error e = {0};
    app_resource *items = NULL;
    size_t count = 0;
    if (error && !(is_abort(error) && !(op->task->token && op->task->token->cancelled))) glue_failure(error, &e);
    else { free(error); items = selected_list(op->task, json, &count); }
    free(json);
    op->done(op->task, e.failed ? &e : NULL, items, count);
    free(op->suggested);
    free(op);
}

/* Extensions browsers accept in picker types: ASCII letters and digits ("*" and others accept every file). */
static bool browser_extension(const char *e) {
    if (!*e) return false;
    for (const char *c = e; *c; c++) if (!((*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9'))) return false;
    return true;
}

/* The picker's accept types: [{"description": name, "accept": {"application/octet-stream": [".png", ...]}}]. */
static void picker_types(const app_picker *options, app_bytes *out) {
    app_bytes_byte(out, '[');
    size_t written = 0;
    for (size_t i = 0; options && i < options->filter_count; i++) {
        const app_filter *f = &options->filters[i];
        size_t real = 0;
        for (size_t k = 0; k < f->count; k++) if (browser_extension(f->extensions[k])) real++;
        if (!real) continue;
        if (written++) app_bytes_byte(out, ',');
        app_bytes json = {0};
        app_error ignored = {0};
        (void)ignored;
        app_bytes_text(out, "{\"description\":\"");
        for (const char *c = f->name; *c; c++) { if (*c == '"' || *c == '\\') app_bytes_byte(out, '\\'); app_bytes_byte(out, (uint8_t)*c); }
        app_bytes_text(out, "\",\"accept\":{\"application/octet-stream\":[");
        size_t n = 0;
        for (size_t k = 0; k < f->count; k++) {
            if (!browser_extension(f->extensions[k])) continue;
            if (n++) app_bytes_byte(out, ',');
            app_bytes_text(out, "\".");
            app_bytes_text(out, f->extensions[k]);
            app_bytes_byte(out, '"');
        }
        app_bytes_text(out, "]}}");
        app_bytes_free(&json);
    }
    app_bytes_byte(out, ']');
    app_bytes_byte(out, 0);
}

static void accept_list(const app_picker *options, app_bytes *out) {
    for (size_t i = 0; options && i < options->filter_count; i++)
        for (size_t k = 0; k < options->filters[i].count; k++) {
            if (!browser_extension(options->filters[i].extensions[k])) continue;
            if (out->length) app_bytes_byte(out, ',');
            app_bytes_byte(out, '.');
            app_bytes_text(out, options->filters[i].extensions[k]);
        }
    app_bytes_byte(out, 0);
}

static pick_op *pick_new(app_task *task, app_picked done) {
    pick_op *op = calloc(1, sizeof *op);
    op->task = task; op->done = done;
    return op;
}

void app_os_open_files(app_task *task, const app_picker *options, app_picked done) {
    app_error e = {0};
    if (!ready(&e)) { done(task, &e, NULL, 0); return; }
    app_bytes text = {0};
    if ((glue_files_features() & PICK_OPEN) && !options->capture) {
        picker_types(options, &text);
        glue_files_pick_open(options->multiple, (const char *)text.data, (uint32_t)text.length - 1, picked, pick_new(task, done));
    } else {
        accept_list(options, &text);
        glue_files_pick_input(options->multiple, (const char *)text.data, (uint32_t)text.length - 1, options->capture, picked, pick_new(task, done));
    }
    app_bytes_free(&text);
}

void app_os_save_file(app_task *task, const app_picker *options, app_picked done) {
    app_error e = {0};
    if (!ready(&e)) { done(task, &e, NULL, 0); return; }
    const char *name = options->suggested && options->suggested[0] ? options->suggested : "document.aqd";
    if (glue_files_features() & PICK_SAVE) {
        app_bytes text = {0};
        picker_types(options, &text);
        glue_files_pick_save(name, (uint32_t)strlen(name), (const char *)text.data, (uint32_t)text.length - 1, picked, pick_new(task, done));
        app_bytes_free(&text);
        return;
    }
    char *location = app_printf("download:/%s", name);
    app_resource *item = calloc(1, sizeof *item);
    bool ok = app_fs_resolve(task->state, location, item, &e);
    free(location);
    if (!ok) { free(item); done(task, &e, NULL, 0); return; }
    done(task, NULL, item, 1);
}

void app_os_pick_directory(app_task *task, app_picked done) {
    app_error e = {0};
    if (!ready(&e)) { done(task, &e, NULL, 0); return; }
    if (!(glue_files_features() & PICK_DIRECTORY)) { app_error_set(&e, APP_UNSUPPORTED, "Directory picker unavailable"); done(task, &e, NULL, 0); return; }
    glue_files_pick_directory(picked, pick_new(task, done));
}

void app_os_launch_files(app_task *task, app_picked done) { glue_files_launch(picked, pick_new(task, done)); }

void app_os_print(app_task *task, const app_picture *picture, app_done done) {
    app_error e = {0};
    finish_op *op = calloc(1, sizeof *op);
    op->task = task; op->done = done;
    app_encode_options options;
    app_encode_defaults(&options);
    if (!app_image_encode(picture, APP_PNG, &options, &op->keep, &e)) { free(op); done(task, &e); return; }
    glue_print_image(op->keep.data, (uint32_t)op->keep.length, finished, op);
}

static void json_text(app_bytes *out, const char *text) {
    if (!text) { app_bytes_text(out, "null"); return; }
    app_bytes_byte(out, '"');
    for (const char *c = text; *c; c++) {
        if (*c == '"' || *c == '\\') { app_bytes_byte(out, '\\'); app_bytes_byte(out, (uint8_t)*c); }
        else if ((uint8_t)*c < 0x20) { char escape[8]; snprintf(escape, sizeof escape, "\\u%04x", (uint8_t)*c); app_bytes_text(out, escape); }
        else app_bytes_byte(out, (uint8_t)*c);
    }
    app_bytes_byte(out, '"');
}

bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e) {
    (void)state; (void)e;
    app_bytes json = {0};
    app_bytes_byte(&json, '[');
    for (size_t i = 0; i < count; i++) {
        if (i) app_bytes_byte(&json, ',');
        app_bytes_text(&json, "{\"id\":"); json_text(&json, nodes[i].id);
        app_bytes_text(&json, ",\"role\":"); json_text(&json, nodes[i].role);
        app_bytes_text(&json, ",\"label\":"); json_text(&json, nodes[i].label);
        app_bytes_text(&json, ",\"value\":"); json_text(&json, nodes[i].value);
        app_bytes_text(&json, nodes[i].disabled ? ",\"disabled\":true}" : ",\"disabled\":false}");
    }
    app_bytes_byte(&json, ']');
    glue_accessibility_update((const char *)json.data, (uint32_t)json.length);
    app_bytes_free(&json);
    return true;
}

bool app_os_register_association(const app_association *association, app_error *e) {
    (void)association;
    return app_error_set(e, APP_UNSUPPORTED, "Browser file handlers require a web app manifest and installation");
}
