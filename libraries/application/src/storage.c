/* Storage: the application's settings and data directories, byte values under validated keys, preferences stored as
 * AQD1 documents of schema 1, and the 20 most recent resources (deduplicated by persistent identity). */
#include "application.h"
#include "platform/platform.h"
#include <stdlib.h>
#include <string.h>

enum { RECENT_LIMIT = 20, PREFERENCE_SCHEMA = 1 };

static bool valid_key(const char *key, app_error *e) {
    size_t n = strlen(key);
    bool ok = n >= 1 && n <= 128;
    for (size_t i = 0; i < n && ok; i++) {
        char c = key[i];
        ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
    }
    if (!ok) return app_error_set(e, APP_INVALID_DATA, "Storage key must contain 1..128 ASCII letters, digits, dots, hyphens or underscores.");
    if (strcmp(key, ".") == 0 || strcmp(key, "..") == 0) return app_error_set(e, APP_INVALID_DATA, "Invalid storage key.");
    return true;
}

static void directory(aqua_call *call, bool settings) {
    app_error e = {0};
    app_resource s, d;
    if (!app_storage_directories(app_state_of(call), &s, &d, &e)) { app_fail(call, &e); return; }
    aqua_return(call, app_resource_value(aqua_call_runtime(call), settings ? &s : &d));
    app_resource_free(&s);
    app_resource_free(&d);
}

void app_storage_SettingsDirectory(aqua_call *call) { directory(call, true); }
void app_storage_DataDirectory(aqua_call *call) { directory(call, false); }

typedef struct storage_task {
    app_task task;
    char *key;
    app_bytes bytes;
    aqua_ref resource;     /* Remember: the resource object being remembered */
} storage_task;

static void storage_cleanup(app_task *task) {
    storage_task *t = (storage_task *)task;
    free(t->key);
    app_bytes_free(&t->bytes);
    aqua_release(task->rt, t->resource);
}

static storage_task *storage_task_new(aqua_call *call, const char *key) {
    storage_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = storage_cleanup;
    t->key = app_strdup(key);
    return t;
}

/* Reads the key's argument and checks it; NULL after failing the call. */
static const char *key_argument(aqua_call *call) {
    app_error e = {0};
    const char *key = aqua_arg_str(call, 0).data;
    if (!valid_key(key, &e)) { app_fail(call, &e); return NULL; }
    if (app_cancelled(call)) return NULL;
    return key;
}

static void read_done(app_task *task, const app_error *e, app_bytes *data, bool found) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_task_return(task, found ? app_bytes_value(task->rt, data->data, data->length) : aqua_null());
}

void app_storage_Read(aqua_call *call) {
    const char *key = key_argument(call);
    if (!key) return;
    storage_task *t = storage_task_new(call, key);
    app_storage_read(&t->task, key, read_done);
    app_task_yield(&t->task);
}

static void write_done(app_task *task, const app_error *e) {
    if (e) app_task_fail(task, e);
    else app_task_return(task, aqua_void());
}

void app_storage_Write(aqua_call *call) {
    const char *key = key_argument(call);
    if (!key) return;
    app_error e = {0};
    app_bytes bytes;
    if (!app_value_bytes(aqua_call_runtime(call), aqua_arg(call, 1), &bytes, &e)) { app_fail(call, &e); return; }
    storage_task *t = storage_task_new(call, key);
    t->bytes = bytes;
    app_storage_write(&t->task, key, t->bytes.data, t->bytes.length, write_done);
    app_task_yield(&t->task);
}

void app_storage_Delete(aqua_call *call) {
    const char *key = key_argument(call);
    if (!key) return;
    storage_task *t = storage_task_new(call, key);
    app_storage_delete(&t->task, key, write_done);
    app_task_yield(&t->task);
}

/* A preference document's value, or null without one; newer schemas fail. */
static bool preference_value(aqua_runtime *rt, app_bytes *data, bool found, aqua_value *out, app_error *e) {
    *out = aqua_null();
    if (!found) return true;
    uint32_t schema;
    if (!app_document_decode(rt, data->data, data->length, &schema, out, e)) return false;
    if (schema > PREFERENCE_SCHEMA) return app_error_set(e, APP_UNSUPPORTED, "Settings use a newer schema.");
    return true;
}

static void preference_done(app_task *task, const app_error *e, app_bytes *data, bool found) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    aqua_value value;
    if (!preference_value(task->rt, data, found, &value, &error)) { app_task_fail(task, &error); return; }
    app_task_return(task, value);
}

void app_storage_GetPreference(aqua_call *call) {
    const char *key = key_argument(call);
    if (!key) return;
    storage_task *t = storage_task_new(call, key);
    app_storage_read(&t->task, key, preference_done);
    app_task_yield(&t->task);
}

void app_storage_SetPreference(aqua_call *call) {
    const char *key = key_argument(call);
    if (!key) return;
    app_error e = {0};
    app_bytes document;
    if (!app_document_encode(aqua_call_runtime(call), aqua_arg(call, 1), PREFERENCE_SCHEMA, &document, &e)) { app_fail(call, &e); return; }
    storage_task *t = storage_task_new(call, key);
    t->bytes = document;
    app_storage_write(&t->task, key, t->bytes.data, t->bytes.length, write_done);
    app_task_yield(&t->task);
}

static void recent_done(app_task *task, const app_error *e, app_bytes *data, bool found) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    aqua_value value;
    if (!preference_value(task->rt, data, found, &value, &error)) { app_task_fail(task, &error); return; }
    app_task_return(task, aqua_is_nullish(value) ? aqua_array_new(task->rt, 0) : value);
}

void app_storage_Recent(aqua_call *call) {
    if (app_cancelled(call)) return;
    storage_task *t = storage_task_new(call, "recent");
    app_storage_read(&t->task, "recent", recent_done);
    app_task_yield(&t->task);
}

static bool text_field_equals(aqua_runtime *rt, aqua_value hash, const char *name, const char *text) {
    aqua_value v;
    if (!aqua_hash_field(rt, hash, name, &v) || v.type != AQUA_STRING) return text == NULL;
    if (!text) return false;
    char *s = aqua_text_dup(rt, v, NULL);
    bool same = s && strcmp(s, text) == 0;
    aqua_free(s);
    return same;
}

static void remembered(app_task *task, const app_error *e, app_bytes *data, bool found) {
    storage_task *t = (storage_task *)task;
    aqua_runtime *rt = task->rt;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    aqua_value previous;
    if (!preference_value(rt, data, found, &previous, &error)) { app_task_fail(task, &error); return; }
    app_resource *r = aqua_object_data(rt, aqua_ref_value(rt, t->resource), app_resource_class);
    aqua_value list = aqua_array_new(rt, 0);
    aqua_array_push(rt, list, app_resource_descriptor(rt, r));
    uint32_t count = previous.type == AQUA_ARRAY ? aqua_array_length(rt, previous) : 0;
    for (uint32_t i = 0; i < count && aqua_array_length(rt, list) < RECENT_LIMIT; i++) {
        aqua_value item = aqua_array_get(rt, previous, i);
        if (item.type != AQUA_HASH) continue;
        bool duplicate = r->identity ? text_field_equals(rt, item, "persistentIdentity", r->identity)
                                     : text_field_equals(rt, item, "provider", r->provider) && text_field_equals(rt, item, "id", r->id);
        if (!duplicate) aqua_array_push(rt, list, item);
    }
    app_bytes document;
    if (!app_document_encode(rt, list, PREFERENCE_SCHEMA, &document, &error)) { app_task_fail(task, &error); return; }
    app_bytes_free(&t->bytes);
    t->bytes = document;
    app_storage_write(task, "recent", t->bytes.data, t->bytes.length, write_done);
}

void app_storage_Remember(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    storage_task *t = storage_task_new(call, "recent");
    t->resource = aqua_retain(t->task.rt, aqua_arg(call, 0));
    app_storage_read(&t->task, "recent", remembered);
    app_task_yield(&t->task);
}
