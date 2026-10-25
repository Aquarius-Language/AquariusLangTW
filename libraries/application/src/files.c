/* Files: opaque, scoped resources ({provider, id, name, kind, persistentIdentity}) and their operations, which the
 * backend carries out: the OS file system on native targets (files_native.c), virtual locations on the web (web.c).
 * Reads are bounded, saves stage and replace atomically where the backend can, and streams are owned by the runtime. */
#include "application.h"
#include "platform/platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- Resource objects ---- */

app_resource app_resource_copy(const app_resource *r) {
    app_resource c = *r;
    c.provider = app_strdup(r->provider);
    c.id = app_strdup(r->id);
    c.name = app_strdup(r->name);
    c.identity = app_strdup(r->identity);
    c.location = app_strdup(r->location);
    return c;
}

void app_resource_free(app_resource *r) {
    free(r->provider); free(r->id); free(r->name); free(r->identity); free(r->location);
    memset(r, 0, sizeof *r);
}

aqua_value app_resource_value(aqua_runtime *rt, const app_resource *r) {
    app_resource *data = malloc(sizeof *data);
    *data = app_resource_copy(r);
    return aqua_object_new(rt, app_resource_class, data);
}

app_resource *app_resource_arg(aqua_call *call, uint32_t index) {
    app_resource *r = aqua_object_data(aqua_call_runtime(call), aqua_arg(call, index), app_resource_class);
    if (!r || strcmp(r->provider, app_state_of(call)->provider) != 0) {
        app_fail_with(call, APP_INVALID_DATA, "Expected a resource owned by this application runtime.");
        return NULL;
    }
    return r;
}

aqua_value app_resource_descriptor(aqua_runtime *rt, const app_resource *r) {
    aqua_value hash = aqua_hash_new(rt, 5);
    app_set_text(rt, hash, "provider", r->provider);
    app_set_text(rt, hash, "id", r->id);
    app_set_text(rt, hash, "name", r->name);
    app_set(rt, hash, "kind", aqua_int(r->kind));
    app_set_text(rt, hash, "persistentIdentity", r->identity);
    return hash;
}

void app_resource_finalize(aqua_runtime *rt, void *data) { (void)rt; app_resource_free(data); free(data); }
static app_resource *self_resource(aqua_call *call) { return aqua_self(call); }
void app_resource_provider(aqua_call *call) { aqua_return_cstring(call, self_resource(call)->provider); }
void app_resource_id(aqua_call *call) { aqua_return_cstring(call, self_resource(call)->id); }
void app_resource_name(aqua_call *call) { aqua_return_cstring(call, self_resource(call)->name); }
void app_resource_kind(aqua_call *call) { aqua_return_int(call, self_resource(call)->kind); }
void app_resource_identity(aqua_call *call) {
    const char *identity = self_resource(call)->identity;
    if (identity) aqua_return_cstring(call, identity); else aqua_return_null(call);
}

/* Temporary resources end with Dispose() or with the runtime. */
void app_resource_Dispose(aqua_call *call) {
    app_state *state = app_state_of(call);
    app_resource *r = self_resource(call);
    for (app_lease *lease = state->leases; lease; lease = lease->next)
        if (r->lease && lease->id == r->lease) { if (!lease->disposed) app_fs_dispose(state, lease); return; }
    app_fail_with(call, APP_INVALID_DATA, "Expected a resource owned by this application runtime.");
}

void app_files_close(app_state *state) {
    for (app_lease *lease = state->leases, *next; lease; lease = next) {
        next = lease->next;
        if (!lease->disposed) app_fs_dispose(state, lease);
        free(lease->location);
        free(lease);
    }
    state->leases = NULL;
    app_fs_close_state(state);
}

/* ---- Module functions ---- */

void app_files_Capabilities(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value hash = aqua_hash_new(rt, 6);
    app_fs_capabilities(rt, hash);
    aqua_return(call, app_record(rt, hash));
}

void app_files_Resolve(aqua_call *call) {
    app_error e = {0};
    app_resource r = {0};
    if (!app_fs_resolve(app_state_of(call), aqua_arg_str(call, 0).data, &r, &e)) { app_fail(call, &e); return; }
    aqua_return(call, app_resource_value(aqua_call_runtime(call), &r));
    app_resource_free(&r);
}

/* Tasks of the module's asynchronous operations. */
typedef struct files_task {
    app_task task;
    app_encoding encoding;
    app_bytes bytes;              /* data being saved */
} files_task;

static void files_cleanup(app_task *task) { app_bytes_free(&((files_task *)task)->bytes); }

static files_task *files_task_new(aqua_call *call) {
    files_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = files_cleanup;
    return t;
}

static void finish_void(app_task *task, const app_error *e) {
    if (e) app_task_fail(task, e);
    else if (!app_task_cancelled(task)) app_task_return(task, aqua_void());
}

static void stat_done(app_task *task, const app_error *e, const app_stat *s) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    aqua_runtime *rt = task->rt;
    aqua_value hash = aqua_hash_new(rt, 4);
    app_set(rt, hash, "exists", aqua_bool(s->exists));
    app_set(rt, hash, "kind", aqua_int(s->kind));
    app_set(rt, hash, "length", s->exists && s->kind == APP_FILE && s->length >= 0 ? app_number((double)s->length) : aqua_null());
    if (s->exists && s->has_modified) {
        /* ISO 8601 in UTC, as the old runtime's DateTimeOffset text. */
        double ms = s->modified_ms;
        int64_t seconds = (int64_t)floor(ms / 1000);
        int64_t ticks = (int64_t)llround((ms - (double)seconds * 1000) * 10000); /* 100 ns */
        if (ticks >= 10000000) { seconds++; ticks -= 10000000; }
        int64_t days = seconds >= 0 ? seconds / 86400 : (seconds - 86399) / 86400, rest = seconds - days * 86400;
        /* civil from days (Howard Hinnant) */
        int64_t z = days + 719468, era = (z >= 0 ? z : z - 146096) / 146097;
        int64_t doe = z - era * 146097, yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        int64_t y = yoe + era * 400, doy = doe - (365 * yoe + yoe / 4 - yoe / 100), mp = (5 * doy + 2) / 153;
        int64_t d = doy - (153 * mp + 2) / 5 + 1, m = mp < 10 ? mp + 3 : mp - 9;
        if (m <= 2) y++;
        char text[64], fraction[16] = "";
        if (ticks) {
            snprintf(fraction, sizeof fraction, ".%07lld", (long long)ticks);
            for (size_t n = strlen(fraction); n > 1 && fraction[n - 1] == '0'; n--) fraction[n - 1] = 0;
        }
        snprintf(text, sizeof text, "%04lld-%02lld-%02lldT%02lld:%02lld:%02lld%s+00:00", (long long)y, (long long)m, (long long)d,
                 (long long)(rest / 3600), (long long)(rest / 60 % 60), (long long)(rest % 60), fraction);
        app_set_text(rt, hash, "modified", text);
    } else app_set(rt, hash, "modified", aqua_null());
    app_task_return(task, app_record(rt, hash));
}

void app_files_Stat(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_stat(&t->task, r, stat_done);
    app_task_yield(&t->task);
}

static void bytes_done(app_task *task, const app_error *e, app_bytes *data) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_task_return(task, app_bytes_value(task->rt, data->data, data->length));
}

void app_files_ReadBytes(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_read(&t->task, r, bytes_done);
    app_task_yield(&t->task);
}

static void text_done(app_task *task, const app_error *e, app_bytes *data) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    aqua_value text;
    if (!app_text_decode(task->rt, data->data, data->length, ((files_task *)task)->encoding, &text, &error)) { app_task_fail(task, &error); return; }
    app_task_return(task, text);
}

void app_files_ReadText(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    app_error e = {0};
    app_encoding encoding;
    if (!r || app_cancelled(call)) return;
    if (!app_encoding_of(aqua_arg_str(call, 1).data, &encoding, &e)) { app_fail(call, &e); return; }
    files_task *t = files_task_new(call);
    t->encoding = encoding;
    app_fs_read(&t->task, r, text_done);
    app_task_yield(&t->task);
}

static void saved(app_task *task, const app_error *e, bool atomic) {
    if (e) app_task_fail(task, e);
    else app_task_return(task, aqua_bool(atomic));
}

static void save(aqua_call *call, app_resource *r, app_bytes bytes, bool require_atomic) {
    files_task *t = files_task_new(call);
    t->bytes = bytes;
    app_fs_replace(&t->task, r, t->bytes.data, t->bytes.length, require_atomic, saved);
    app_task_yield(&t->task);
}

void app_files_SaveBytes(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    app_error e = {0};
    app_bytes bytes;
    if (!r || app_cancelled(call)) return;
    if (!app_value_bytes(aqua_call_runtime(call), aqua_arg(call, 1), &bytes, &e)) { app_fail(call, &e); return; }
    save(call, r, bytes, aqua_has_arg(call, 2) && aqua_arg_bool(call, 2));
}

void app_files_SaveText(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    app_error e = {0};
    app_encoding encoding;
    app_bytes bytes;
    if (!r || app_cancelled(call)) return;
    if (!app_encoding_of(aqua_arg_str(call, 2).data, &encoding, &e) ||
        !app_text_encode(aqua_call_runtime(call), aqua_arg(call, 1), encoding, &bytes, &e)) { app_fail(call, &e); return; }
    save(call, r, bytes, aqua_has_arg(call, 3) && aqua_arg_bool(call, 3));
}

static void listed(app_task *task, const app_error *e, app_resource *items, size_t count) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) { for (size_t i = 0; i < count; i++) app_resource_free(&items[i]); free(items); return; }
    aqua_value array = aqua_array_new(task->rt, (uint32_t)count);
    for (size_t i = 0; i < count; i++) {
        aqua_array_set(task->rt, array, (uint32_t)i, app_resource_value(task->rt, &items[i]));
        app_resource_free(&items[i]);
    }
    free(items);
    app_task_return(task, array);
}

void app_files_Enumerate(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_enumerate(&t->task, r, listed);
    app_task_yield(&t->task);
}

void app_files_CreateDirectory(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_create_directory(&t->task, r, finish_void);
    app_task_yield(&t->task);
}

void app_files_Delete(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    if (!r || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_delete(&t->task, r, aqua_has_arg(call, 1) && aqua_arg_bool(call, 1), finish_void);
    app_task_yield(&t->task);
}

void app_files_Copy(aqua_call *call) {
    app_resource *from = app_resource_arg(call, 0), *to = from ? app_resource_arg(call, 1) : NULL;
    if (!to || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_copy(&t->task, from, to, aqua_has_arg(call, 2) && aqua_arg_bool(call, 2), finish_void);
    app_task_yield(&t->task);
}

void app_files_Move(aqua_call *call) {
    app_resource *from = app_resource_arg(call, 0), *to = from ? app_resource_arg(call, 1) : NULL;
    if (!to || app_cancelled(call)) return;
    files_task *t = files_task_new(call);
    app_fs_move(&t->task, from, to, aqua_has_arg(call, 2) && aqua_arg_bool(call, 2), finish_void);
    app_task_yield(&t->task);
}

void app_files_Temporary(aqua_call *call) {
    app_state *state = app_state_of(call);
    app_error e = {0};
    app_resource r = {0};
    if (app_cancelled(call)) return;
    if (!app_fs_temporary(state, aqua_arg_bool(call, 0), &r, &e)) { app_fail(call, &e); return; }
    aqua_return(call, app_resource_value(aqua_call_runtime(call), &r));
    app_resource_free(&r);
}

void app_files_ReadAssetBytes(aqua_call *call) {
    size_t length = 0;
    aqua_str path = aqua_arg_str(call, 0);
    if (app_cancelled(call)) return;
    const uint8_t *data = aqua_asset(aqua_call_runtime(call), path.data, &length);
    if (!data) { app_fail_with(call, APP_NOT_FOUND, "Could not find the packaged asset '%s'.", path.data); return; }
    app_error e = {0};
    if (!app_check_bytes(length, &e)) { app_fail(call, &e); return; }
    aqua_return(call, app_bytes_value(aqua_call_runtime(call), data, length));
}

void app_files_ReadAssetText(aqua_call *call) {
    size_t length = 0;
    app_error e = {0};
    app_encoding encoding;
    aqua_str path = aqua_arg_str(call, 0);
    if (app_cancelled(call)) return;
    if (!app_encoding_of(aqua_arg_str(call, 1).data, &encoding, &e)) { app_fail(call, &e); return; }
    const uint8_t *data = aqua_asset(aqua_call_runtime(call), path.data, &length);
    if (!data) { app_fail_with(call, APP_NOT_FOUND, "Could not find the packaged asset '%s'.", path.data); return; }
    aqua_value text;
    if (!app_check_bytes(length, &e) || !app_text_decode(aqua_call_runtime(call), data, length, encoding, &text, &e)) { app_fail(call, &e); return; }
    aqua_return(call, text);
}

/* ---- Streams ---- */

static bool mode_of(const char *name, int *mode, app_error *e) {
    static const char *const names[] = {"Read", "Create", "OpenWrite", "Append"};
    for (int i = 0; i < 4; i++) if (app_equals_ignore_case(name, names[i])) { *mode = i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Requested value '%s' was not found.", name);
}

app_stream *app_stream_new(app_state *state, int mode) {
    app_stream *s = calloc(1, sizeof *s);
    s->state = state;
    s->mode = mode;
    s->can_read = mode == APP_OPEN_READ;
    s->can_write = mode != APP_OPEN_READ;
    s->can_seek = true;
    s->next = state->streams;
    state->streams = s;
    return s;
}

static void stream_release(app_stream *s) {
    if (s->file) { app_os_close(s->file); s->file = NULL; }
    app_bytes_free(&s->memory);
    app_resource_free(&s->resource);
    s->closed = true;
}

static void stream_unlink(app_stream *s) {
    if (!s->state) return;
    for (app_stream **p = &s->state->streams; *p; p = &(*p)->next) if (*p == s) { *p = s->next; break; }
    s->state = NULL;
}

/* Streams still open when the runtime ends are closed (OS files) or discarded (unflushed web buffers). */
void app_state_close_streams(app_state *state) {
    for (app_stream *s = state->streams, *next; s; s = next) {
        next = s->next;
        stream_release(s);
        s->state = NULL;
    }
    state->streams = NULL;
}

void app_stream_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    app_stream *s = data;
    stream_unlink(s);
    stream_release(s);
    free(s);
}

static void opened(app_task *task, const app_error *e, app_stream *stream) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) { stream_unlink(stream); stream_release(stream); free(stream); return; }
    app_task_return(task, aqua_object_new(task->rt, app_stream_class, stream));
}

void app_files_Open(aqua_call *call) {
    app_resource *r = app_resource_arg(call, 0);
    app_error e = {0};
    int mode;
    if (!r || app_cancelled(call)) return;
    if (!mode_of(aqua_arg_str(call, 1).data, &mode, &e)) { app_fail(call, &e); return; }
    files_task *t = files_task_new(call);
    app_fs_open(&t->task, r, mode, opened);
    app_task_yield(&t->task);
}

static app_stream *live_stream(aqua_call *call) {
    app_stream *s = aqua_self(call);
    if (s->closed) { app_fail_with(call, APP_INVALID_DATA, "Expected a resource owned by this application runtime."); return NULL; }
    if (app_cancelled(call)) return NULL;
    return s;
}

void app_stream_can_read(aqua_call *call) { aqua_return_bool(call, ((app_stream *)aqua_self(call))->can_read); }
void app_stream_can_write(aqua_call *call) { aqua_return_bool(call, ((app_stream *)aqua_self(call))->can_write); }
void app_stream_can_seek(aqua_call *call) { aqua_return_bool(call, ((app_stream *)aqua_self(call))->can_seek); }

void app_stream_Read(aqua_call *call) {
    app_stream *s = live_stream(call);
    if (!s) return;
    int32_t count = aqua_arg_int(call, 0);
    app_error e = {0};
    if (count < 0 || !app_check_bytes((uint64_t)count, &e)) {
        if (count < 0) app_error_set(&e, APP_INVALID_DATA, "Non-negative number required. (Parameter 'count')");
        app_fail(call, &e);
        return;
    }
    if (!s->can_read) { app_fail_with(call, APP_UNSUPPORTED, "Stream does not support reading."); return; }
    uint8_t *buffer = malloc(count ? (size_t)count : 1);
    int64_t used;
    if (s->file) used = app_os_read(s->file, buffer, (size_t)count, &e);
    else {
        size_t left = s->position < s->memory.length ? s->memory.length - s->position : 0;
        used = (int64_t)((size_t)count < left ? (size_t)count : left);
        memcpy(buffer, s->memory.data + s->position, (size_t)used);
        s->position += (size_t)used;
    }
    if (used < 0) { free(buffer); app_fail(call, &e); return; }
    aqua_return(call, app_bytes_value(aqua_call_runtime(call), buffer, (size_t)used));
    free(buffer);
}

void app_stream_Write(aqua_call *call) {
    app_stream *s = live_stream(call);
    if (!s) return;
    app_error e = {0};
    app_bytes bytes;
    if (!app_value_bytes(aqua_call_runtime(call), aqua_arg(call, 0), &bytes, &e)) { app_fail(call, &e); return; }
    if (!s->can_write) { app_bytes_free(&bytes); app_fail_with(call, APP_UNSUPPORTED, "Stream does not support writing."); return; }
    bool ok = true;
    if (s->file) ok = app_os_write(s->file, bytes.data, bytes.length, &e);
    else {
        size_t end = s->position + bytes.length;
        if (!app_check_bytes(end > s->memory.length ? end : s->memory.length, &e)) ok = false;
        else {
            if (end > s->memory.length) {
                app_bytes_reserve(&s->memory, end);
                memset(s->memory.data + s->memory.length, 0, end - s->memory.length);
                s->memory.length = end;
            }
            if (bytes.length) memcpy(s->memory.data + s->position, bytes.data, bytes.length);
            s->position = end;
            s->dirty = true;
        }
    }
    app_bytes_free(&bytes);
    if (!ok) app_fail(call, &e);
}

void app_stream_Seek(aqua_call *call) {
    app_stream *s = live_stream(call);
    if (!s) return;
    double offset = aqua_arg_number(call, 0);
    app_error e = {0};
    int origin;
    aqua_str name = aqua_arg_str(call, 1);
    if (!isfinite(offset) || offset != floor(offset)) { app_fail_with(call, APP_INVALID_DATA, "Expected an integer."); return; }
    if (app_equals_ignore_case(name.data, "Begin")) origin = APP_SEEK_BEGIN;
    else if (app_equals_ignore_case(name.data, "Current")) origin = APP_SEEK_CURRENT;
    else if (app_equals_ignore_case(name.data, "End")) origin = APP_SEEK_END;
    else { app_fail_with(call, APP_INVALID_DATA, "Requested value '%s' was not found.", name.data); return; }
    int64_t position;
    if (s->file) position = app_os_seek(s->file, (int64_t)offset, origin, &e);
    else {
        double base = origin == APP_SEEK_BEGIN ? 0 : origin == APP_SEEK_CURRENT ? (double)s->position : (double)s->memory.length;
        double next = base + offset;
        if (next < 0) { position = -1; app_error_set(&e, APP_UNAVAILABLE, "An attempt was made to move the position before the beginning of the stream."); }
        else if (s->mode == APP_OPEN_APPEND && next < (double)s->start) { position = -1; app_error_set(&e, APP_UNAVAILABLE, "Unable seek backward to overwrite data that previously existed in a file opened in Append mode."); }
        else if (!app_check_bytes((uint64_t)next, &e)) position = -1;
        else { s->position = (size_t)next; position = (int64_t)next; }
    }
    if (position < 0) { app_fail(call, &e); return; }
    aqua_return_double(call, (double)position);
}

/* Web streams commit their buffer through the backend; OS streams flush their file. */
typedef struct stream_task { app_task task; app_stream *stream; aqua_ref self; bool close; } stream_task;

static void stream_cleanup(app_task *task) { aqua_release(task->rt, ((stream_task *)task)->self); }

static void committed(app_task *task, const app_error *e, bool atomic) {
    (void)atomic;
    stream_task *t = (stream_task *)task;
    if (e) { app_task_fail(task, e); return; }
    t->stream->dirty = false;
    if (t->close) { stream_unlink(t->stream); stream_release(t->stream); }
    app_task_return(task, aqua_void());
}

static void flush_or_close(aqua_call *call, bool close) {
    app_stream *s = aqua_self(call);
    if (s->closed) {
        if (close) return; /* Close is idempotent */
        app_fail_with(call, APP_INVALID_DATA, "Expected a resource owned by this application runtime.");
        return;
    }
    if (app_cancelled(call)) return;
    app_error e = {0};
    if (s->file) {
        if (!app_os_flush(s->file, &e)) { app_fail(call, &e); return; }
        if (close) { stream_unlink(s); stream_release(s); }
        return;
    }
    if (!s->dirty) { if (close) { stream_unlink(s); stream_release(s); } return; }
    stream_task *t = app_task_new(call, sizeof *t);
    t->stream = s;
    t->close = close;
    t->self = aqua_retain(t->task.rt, aqua_self_value(call));
    t->task.cleanup = stream_cleanup;
    app_fs_replace(&t->task, &s->resource, s->memory.data, s->memory.length, false, committed);
    app_task_yield(&t->task);
}

void app_stream_Flush(aqua_call *call) { flush_or_close(call, false); }
void app_stream_Close(aqua_call *call) { flush_or_close(call, true); }
