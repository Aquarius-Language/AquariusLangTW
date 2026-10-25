/* The files backend of native targets: resources are absolute OS paths. Saves stage a neighboring temporary file,
 * flush it and replace the target atomically; copies stage too; directory copies refuse symbolic links and copying a
 * directory into itself. Everything completes synchronously. Android also reaches content:// documents. */
#include "application.h"
#include "platform/platform.h"
#include <stdlib.h>
#include <string.h>

static char *join(const char *directory, const char *name) {
    size_t n = strlen(directory);
    bool slash = n && (directory[n - 1] == '/' || directory[n - 1] == app_os_separator);
    return app_printf("%s%s%s", directory, slash ? "" : (char[]){app_os_separator, 0}, name);
}

static const char *base_name(const char *path) {
    const char *name = path;
    for (const char *p = path; *p; p++) if (*p == '/' || *p == '\\') name = p + 1;
    return name;
}

static char *stage_name(const char *path) {
    char id[33];
    app_random_hex(id, 32);
    return app_printf("%s.aquarius-%s.tmp", path, id);
}

bool app_fs_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "paths", aqua_bool(true));
    app_set(rt, hash, "streams", aqua_bool(true));
    app_set(rt, hash, "seek", aqua_bool(true));
    app_set(rt, hash, "directories", aqua_bool(true));
    app_set(rt, hash, "atomicReplace", aqua_bool(true));
    app_set(rt, hash, "persistent", aqua_bool(true));
    return true;
}

/* A resource of this runtime for an absolute path. */
static bool path_resource(app_state *state, char *full, app_resource *out, app_error *e) {
    app_stat s;
    if (!app_os_stat(full, &s, e)) { free(full); return false; }
    memset(out, 0, sizeof *out);
    out->provider = app_strdup(state->provider);
    out->id = full;
    out->location = app_strdup(full);
    out->name = app_strdup(base_name(full));
    out->kind = s.exists && s.kind == APP_DIRECTORY ? APP_DIRECTORY : APP_FILE;
    out->backend = APP_PATH;
#if defined(AQUA_OS_WINDOWS)
    char *identity = app_printf("desktop:%s", full);
    for (char *p = identity; *p; p++) if (*p >= 'a' && *p <= 'z') *p = (char)(*p - 32);
    out->identity = identity;
#else
    out->identity = app_printf("desktop:%s", full);
#endif
    return true;
}

bool app_fs_resolve(app_state *state, const char *location, app_resource *out, app_error *e) {
    char *full = app_os_full_path(location, e);
    return full && path_resource(state, full, out, e);
}

static bool path_of(const app_resource *r, app_error *e) {
    if (r->kind == APP_ASSET || (r->backend != APP_PATH && r->backend != APP_DOCUMENT))
        return app_error_set(e, APP_INVALID_DATA, "Resource belongs to another provider or is a packaged asset.");
    return true;
}

void app_fs_stat(app_task *task, const app_resource *r, void (*done)(app_task *, const app_error *, const app_stat *)) {
    app_error e = {0};
    app_stat s = {0};
    bool ok = path_of(r, &e);
#if defined(AQUA_OS_ANDROID)
    if (ok && r->backend == APP_DOCUMENT) ok = app_android_document_stat(r->location, &s, &e);
    else
#endif
    if (ok) ok = app_os_stat(r->location, &s, &e);
    if (ok && s.kind == APP_DIRECTORY) s.length = -1;
    done(task, ok ? NULL : &e, &s);
}

/* The whole file, bounded by the byte limit. */
static bool read_file(const char *path, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    app_stat s;
    if (!app_os_stat(path, &s, e)) return false;
    if (s.exists && s.kind == APP_DIRECTORY) return app_error_set(e, APP_PERMISSION_DENIED, "Access to the path '%s' is denied.", path);
    if (s.exists && !app_check_bytes((uint64_t)s.length, e)) return false;
    app_os_file *file = app_os_open(path, APP_OPEN_READ, e);
    if (!file) return false;
    bool ok = true;
    for (;;) {
        if (!app_bytes_reserve(out, out->length + 65536)) { ok = app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory."); break; }
        int64_t n = app_os_read(file, out->data + out->length, 65536, e);
        if (n < 0) { ok = false; break; }
        if (n == 0) break;
        out->length += (size_t)n;
        if (out->length > APP_MAX_BYTES) { ok = app_error_set(e, APP_LIMIT_EXCEEDED, "Stream exceeds the byte limit."); break; }
    }
    app_os_close(file);
    if (!ok) app_bytes_free(out);
    return ok;
}

void app_fs_read(app_task *task, const app_resource *r, void (*done)(app_task *, const app_error *, app_bytes *)) {
    app_error e = {0};
    app_bytes data = {0};
    bool ok = path_of(r, &e);
#if defined(AQUA_OS_ANDROID)
    if (ok && r->backend == APP_DOCUMENT) ok = app_android_document_read(r->location, &data, &e);
    else
#endif
    if (ok) ok = read_file(r->location, &data, &e);
    done(task, ok ? NULL : &e, &data);
    app_bytes_free(&data);
}

/* Writes a neighboring stage file, flushes it and moves it over `path`; the stage never outlives the call. */
static bool replace_file(const char *path, const uint8_t *data, size_t length, app_error *e) {
    app_stat s;
    if (!app_os_stat(path, &s, e)) return false;
    if (s.exists && s.kind == APP_DIRECTORY) return app_error_set(e, APP_PERMISSION_DENIED, "Access to the path '%s' is denied.", path);
    char *stage = stage_name(path);
    bool ok = app_os_write_new(stage, data, length, e);
    if (ok) ok = s.exists ? app_os_replace(stage, path, e) : app_os_rename(stage, path, false, e);
    if (!ok) { app_error ignored = {0}; app_os_remove_file(stage, &ignored); }
    free(stage);
    return ok;
}

void app_fs_replace(app_task *task, const app_resource *r, const uint8_t *data, size_t length, bool require_atomic,
                    void (*done)(app_task *, const app_error *, bool)) {
    (void)require_atomic; /* native replacement is always atomic */
    app_error e = {0};
    bool ok = path_of(r, &e) && app_check_bytes(length, &e);
#if defined(AQUA_OS_ANDROID)
    if (ok && r->backend == APP_DOCUMENT) {
        ok = !require_atomic ? app_android_document_write(r->location, data, length, &e)
                             : app_error_set(&e, APP_UNSUPPORTED, "Documents of other applications cannot be replaced atomically.");
        done(task, ok ? NULL : &e, false);
        return;
    }
#endif
    if (ok && app_task_cancelled(task)) return;
    if (ok) ok = replace_file(r->location, data, length, &e);
    done(task, ok ? NULL : &e, ok);
}

void app_fs_enumerate(app_task *task, const app_resource *r, void (*done)(app_task *, const app_error *, app_resource *, size_t)) {
    app_error e = {0};
    app_resource *items = NULL;
    size_t count = 0;
    char **names = NULL;
    size_t name_count = 0;
    bool ok = path_of(r, &e);
#if defined(AQUA_OS_ANDROID)
    if (ok && r->backend == APP_DOCUMENT) {
        ok = app_android_document_list(task->state, r->location, &items, &count, &e);
        done(task, ok ? NULL : &e, items, count);
        return;
    }
#endif
    if (ok) ok = app_os_list(r->location, &names, &name_count, &e);
    if (ok && name_count > APP_MAX_ENTRIES) ok = app_error_set(&e, APP_LIMIT_EXCEEDED, "Directory exceeds 4096 entries.");
    if (ok) {
        items = calloc(name_count ? name_count : 1, sizeof *items);
        for (size_t i = 0; i < name_count && ok; i++) {
            ok = path_resource(task->state, join(r->location, names[i]), &items[i], &e);
            if (ok) count++;
        }
    }
    for (size_t i = 0; i < name_count; i++) free(names[i]);
    free(names);
    if (!ok) {
        for (size_t i = 0; i < count; i++) app_resource_free(&items[i]);
        free(items);
        items = NULL;
        count = 0;
    }
    done(task, ok ? NULL : &e, items, count);
}

void app_fs_create_directory(app_task *task, const app_resource *r, app_done done) {
    app_error e = {0};
    bool ok = path_of(r, &e) && app_os_mkdirs(r->location, &e);
    done(task, ok ? NULL : &e);
}

static bool remove_tree(const char *path, app_error *e) {
    app_stat s;
    if (!app_os_stat(path, &s, e)) return false;
    if (!s.exists) return true;
    if (s.kind != APP_DIRECTORY || app_os_is_link(path)) return s.kind == APP_DIRECTORY ? app_os_remove_directory(path, e) : app_os_remove_file(path, e);
    char **names;
    size_t count;
    if (!app_os_list(path, &names, &count, e)) return false;
    bool ok = true;
    for (size_t i = 0; i < count; i++) {
        if (ok) { char *child = join(path, names[i]); ok = remove_tree(child, e); free(child); }
        free(names[i]);
    }
    free(names);
    return ok && app_os_remove_directory(path, e);
}

void app_fs_delete(app_task *task, const app_resource *r, bool recursive, app_done done) {
    app_error e = {0};
    app_stat s;
    bool ok = path_of(r, &e);
#if defined(AQUA_OS_ANDROID)
    if (ok && r->backend == APP_DOCUMENT) { ok = app_android_document_delete(r->location, &e); done(task, ok ? NULL : &e); return; }
#endif
    if (ok) ok = app_os_stat(r->location, &s, &e);
    if (ok) {
        if (s.exists && s.kind == APP_DIRECTORY) ok = recursive ? remove_tree(r->location, &e) : app_os_remove_directory(r->location, &e);
        else ok = app_os_remove_file(r->location, &e);
    }
    done(task, ok ? NULL : &e);
}

/* Copies a file through a neighboring stage, so a failure never leaves a truncated destination. */
static bool copy_file(const char *from, const char *to, bool overwrite, app_task *task, app_error *e) {
    app_os_file *input = app_os_open(from, APP_OPEN_READ, e);
    if (!input) return false;
    char *stage = stage_name(to);
    app_os_file *output = NULL;
    bool ok = app_os_write_new(stage, NULL, 0, e) && (output = app_os_open(stage, APP_OPEN_WRITE, e)) != NULL;
    uint8_t *buffer = malloc(65536);
    while (ok) {
        int64_t n = app_os_read(input, buffer, 65536, e);
        if (n < 0) ok = false;
        else if (n == 0) break;
        else ok = app_os_write(output, buffer, (size_t)n, e);
    }
    free(buffer);
    if (output) { if (ok) ok = app_os_flush(output, e); app_os_close(output); }
    app_os_close(input);
    if (ok && task->token && task->token->cancelled) ok = app_error_set(e, APP_CANCELLED, "The operation was canceled.");
    if (ok) ok = app_os_rename(stage, to, overwrite, e);
    if (!ok) { app_error ignored = {0}; app_os_remove_file(stage, &ignored); }
    free(stage);
    return ok;
}

static bool copy_directory(const char *from, const char *to, int *entries, app_task *task, app_error *e) {
    char **names;
    size_t count;
    if (!app_os_list(from, &names, &count, e)) return false;
    bool ok = true;
    for (size_t i = 0; i < count; i++) {
        if (ok) {
            char *source = join(from, names[i]), *target = join(to, names[i]);
            app_stat s;
            if (task->token && task->token->cancelled) ok = app_error_set(e, APP_CANCELLED, "The operation was canceled.");
            else if (++*entries > APP_MAX_ENTRIES) ok = app_error_set(e, APP_LIMIT_EXCEEDED, "Directory copy exceeds 4096 entries.");
            else if (app_os_is_link(source)) ok = app_error_set(e, APP_UNSUPPORTED, "Recursive copying does not follow symbolic links.");
            else if (!app_os_stat(source, &s, e)) ok = false;
            else if (s.kind == APP_DIRECTORY) ok = app_os_mkdirs(target, e) && copy_directory(source, target, entries, task, e);
            else ok = copy_file(source, target, false, task, e);
            free(source);
            free(target);
        }
        free(names[i]);
    }
    free(names);
    return ok;
}

static bool inside(const char *path, const char *directory) {
    size_t n = strlen(directory);
    while (n && (directory[n - 1] == '/' || directory[n - 1] == '\\')) n--;
    char *prefix = app_strndup(directory, n);
    size_t length = strlen(path);
    bool result = false;
    if (length > n && (path[n] == '/' || path[n] == '\\')) {
        char *head = app_strndup(path, n);
        result = app_os_same_path(head, prefix);
        free(head);
    }
    free(prefix);
    return result;
}

void app_fs_copy(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done) {
    app_error e = {0};
    app_stat s, target;
    bool ok = path_of(from, &e) && path_of(to, &e);
    if (ok && (from->backend != APP_PATH || to->backend != APP_PATH)) ok = app_error_set(&e, APP_UNSUPPORTED, "Copying documents is not supported.");
    if (ok) ok = app_os_stat(from->location, &s, &e);
    if (ok && s.exists && s.kind == APP_DIRECTORY) {
        if (app_os_same_path(from->location, to->location) || inside(to->location, from->location))
            ok = app_error_set(&e, APP_INVALID_DATA, "Directory cannot be copied into itself.");
        else if (!app_os_stat(to->location, &target, &e)) ok = false;
        else if (target.exists) ok = app_error_set(&e, APP_CONFLICT, "Directory copy requires a new destination.");
        else {
            char *stage = stage_name(to->location);
            int entries = 0;
            ok = app_os_mkdirs(stage, &e) && copy_directory(from->location, stage, &entries, task, &e) && app_os_rename(stage, to->location, false, &e);
            app_error ignored = {0};
            remove_tree(stage, &ignored);
            free(stage);
        }
    } else if (ok) ok = copy_file(from->location, to->location, overwrite, task, &e);
    done(task, ok ? NULL : &e);
}

void app_fs_move(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done) {
    app_error e = {0};
    app_stat s;
    bool ok = path_of(from, &e) && path_of(to, &e);
    if (ok && (from->backend != APP_PATH || to->backend != APP_PATH)) ok = app_error_set(&e, APP_UNSUPPORTED, "Moving documents is not supported.");
    if (ok) ok = app_os_stat(from->location, &s, &e);
    if (ok && s.exists && s.kind == APP_DIRECTORY && overwrite) ok = app_error_set(&e, APP_UNSUPPORTED, "Directory replacement is not supported.");
    else if (ok && !s.exists) ok = app_error_set(&e, APP_NOT_FOUND, "Could not find file '%s'.", from->location);
    else if (ok) ok = app_os_rename(from->location, to->location, overwrite, &e);
    done(task, ok ? NULL : &e);
}

bool app_fs_temporary(app_state *state, bool directory, app_resource *out, app_error *e) {
    char id[33];
    app_random_hex(id, 32);
    char *base = app_os_temporary_directory(), *name = app_printf("aquarius-%s", id), *path = join(base, name);
    free(base);
    free(name);
    bool ok = directory ? app_os_mkdirs(path, e) : app_os_write_new(path, NULL, 0, e);
    if (ok) ok = path_resource(state, app_strdup(path), out, e);
    if (!ok) { free(path); return false; }
    app_lease *lease = calloc(1, sizeof *lease);
    lease->id = ++state->next_lease;
    lease->directory = directory;
    lease->location = path;
    lease->next = state->leases;
    state->leases = lease;
    out->lease = lease->id;
    return true;
}

void app_fs_dispose(app_state *state, app_lease *lease) {
    (void)state;
    app_error ignored = {0};
    if (lease->directory) remove_tree(lease->location, &ignored);
    else app_os_remove_file(lease->location, &ignored);
    lease->disposed = true;
}

void app_fs_open(app_task *task, const app_resource *r, int mode, void (*done)(app_task *, const app_error *, app_stream *)) {
    app_error e = {0};
    if (!path_of(r, &e)) { done(task, &e, NULL); return; }
    if (r->backend == APP_DOCUMENT) {
        app_error_set(&e, APP_UNSUPPORTED, "Streams over documents are not supported; read or save the whole document.");
        done(task, &e, NULL);
        return;
    }
    app_os_file *file = app_os_open(r->location, mode, &e);
    if (!file) { done(task, &e, NULL); return; }
    app_stream *stream = app_stream_new(task->state, mode);
    stream->file = file;
    done(task, NULL, stream);
}

void app_fs_close_state(app_state *state) { (void)state; }

/* ---- Storage: files in the settings directory ---- */

static bool valid_identifier(const char *key) {
    size_t n = strlen(key);
    if (n < 1 || n > 128 || strcmp(key, ".") == 0 || strcmp(key, "..") == 0) return false;
    for (size_t i = 0; i < n; i++) {
        char c = key[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
    }
    return true;
}

static bool storage_paths(app_state *state, char **settings, char **data) {
    const char *root = aqua_environment(state->rt, "AQUARIUS_STORAGE_ROOT");
    if (root && *root) {
        *settings = app_strdup(root);
        *data = join(root, "data");
        return true;
    }
    const char *id = aqua_environment(state->rt, "AQUARIUS_APPLICATION_ID");
    if (!id || !valid_identifier(id)) id = "AquariusLang";
    *settings = app_os_settings_directory(id);
    *data = app_os_data_directory(id);
    return *settings && *data;
}

static app_resource directory_resource(app_state *state, char *path, const char *name) {
    app_resource r = {0};
    r.provider = app_strdup(state->provider);
    r.id = path;
    r.location = app_strdup(path);
    r.name = app_strdup(name);
    r.kind = APP_DIRECTORY;
    r.backend = APP_PATH;
    return r;
}

bool app_storage_directories(app_state *state, app_resource *settings, app_resource *data, app_error *e) {
    char *s = NULL, *d = NULL;
    if (!storage_paths(state, &s, &d)) { free(s); free(d); return app_error_set(e, APP_UNAVAILABLE, "The application storage directories are unavailable."); }
    *settings = directory_resource(state, s, "settings");
    *data = directory_resource(state, d, "data");
    return true;
}

static char *key_path(app_state *state, const char *key) {
    char *settings = NULL, *data = NULL;
    if (!storage_paths(state, &settings, &data)) { free(settings); free(data); return NULL; }
    char *path = join(settings, key);
    free(settings);
    free(data);
    return path;
}

void app_storage_read(app_task *task, const char *key, void (*done)(app_task *, const app_error *, app_bytes *, bool)) {
    app_error e = {0};
    app_bytes data = {0};
    char *path = key_path(task->state, key);
    app_stat s;
    bool ok = path != NULL, found = false;
    if (!ok) app_error_set(&e, APP_UNAVAILABLE, "The application storage directories are unavailable.");
    if (ok) ok = app_os_stat(path, &s, &e);
    if (ok && s.exists) { found = true; ok = read_file(path, &data, &e); }
    free(path);
    done(task, ok ? NULL : &e, &data, found);
    app_bytes_free(&data);
}

void app_storage_write(app_task *task, const char *key, const uint8_t *data, size_t length, app_done done) {
    app_error e = {0};
    char *settings = NULL, *unused = NULL;
    bool ok = app_check_bytes(length, &e) && storage_paths(task->state, &settings, &unused);
    if (!ok && !e.failed) app_error_set(&e, APP_UNAVAILABLE, "The application storage directories are unavailable.");
    if (ok) ok = app_os_mkdirs(settings, &e);
    if (ok) { char *path = join(settings, key); ok = replace_file(path, data, length, &e); free(path); }
    free(settings);
    free(unused);
    done(task, ok ? NULL : &e);
}

void app_storage_delete(app_task *task, const char *key, app_done done) {
    app_error e = {0};
    char *path = key_path(task->state, key);
    bool ok = path && app_os_remove_file(path, &e);
    if (!path) app_error_set(&e, APP_UNAVAILABLE, "The application storage directories are unavailable.");
    free(path);
    done(task, ok ? NULL : &e);
}

/* ---- Picked and launch paths ---- */

void app_resolve_paths(app_task *task, const char *const *paths, size_t count, app_picked done) {
    app_error e = {0};
    app_resource *items = calloc(count ? count : 1, sizeof *items);
    size_t resolved = 0;
    bool ok = true;
    for (size_t i = 0; i < count && ok; i++) if ((ok = app_fs_resolve(task->state, paths[i], &items[i], &e))) resolved++;
    if (!ok) { for (size_t i = 0; i < resolved; i++) app_resource_free(&items[i]); resolved = 0; }
    done(task, ok ? NULL : &e, items, resolved);
}

#if !defined(AQUA_OS_ANDROID)
/* Documents opened with the application: `app.wasm --documents a b` or the arguments of a standalone executable. */
void app_os_launch_files(app_task *task, app_picked done) {
    uint32_t count = aqua_launch_count(task->rt);
    const char **paths = calloc(count ? count : 1, sizeof *paths);
    for (uint32_t i = 0; i < count; i++) paths[i] = aqua_launch_argument(task->rt, i);
    app_resolve_paths(task, paths, count, done);
    free(paths);
}
#endif
