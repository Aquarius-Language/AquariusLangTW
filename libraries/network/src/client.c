/* HTTP clients as scripts see them on every target: Request/Get/Post, StartRequest operations, StartStream
 * operations and Response records. The engine performs each transfer; this file validates, keeps the outcome and
 * the streamed text, and delivers responses (NetworkRuntime.RegisterRequests, Operation, StreamOperation). */
#include "engine.h"
#include <stdlib.h>
#include <string.h>

/* ---- Clients ---- */

static void client_close_resource(network_state *s, resource *r) {
    (void)s;
    client *c = (client *)r;
    atomic_store(&c->closed, true);
    engine_client_close(c);
}

void client_unref(client *c) {
    if (!c || atomic_fetch_sub(&c->refs, 1) != 1) return;
    engine_client_free(c);
    options_free(&c->defaults);
    free(c);
}

static client *client_new(const net_options *defaults, net_error *e) {
    client *c = calloc(1, sizeof *c);
    atomic_init(&c->refs, 1);
    atomic_init(&c->closed, false);
    c->base.close = client_close_resource;
    options_copy(&c->defaults, defaults);
    if (!engine_client_open(c, e)) { client_unref(c); return NULL; }
    return c;
}

/* The module's default client, made on first use. */
client *client_shared(network_state *s) {
    if (!s->client) {
        net_options defaults;
        options_default(&defaults);
        net_error e;
        s->client = client_new(&defaults, &e);
        options_free(&defaults);
        if (s->client && !resource_own(s, &s->client->base, &e)) atomic_store(&s->client->closed, true);
    }
    return s->client;
}

void client_release(aqua_runtime *rt, void *payload) {
    (void)rt;
    client *c = payload;
    if (c->base.open) resource_close(c->base.state, &c->base);
    client_unref(c);
}

/* ---- Transfers ---- */

void transfer_unref(transfer *t) {
    if (!t || atomic_fetch_sub(&t->refs, 1) != 1) return;
    client_unref(t->client);
    free(t->method);
    url_free(&t->url);
    options_free(&t->options);
    net_mutex_destroy(&t->lock);
    buffer_free(&t->unread);
    free(t->final_url);
    headers_free(&t->headers);
    buffer_free(&t->body);
    free(t->waiters);
    free(t);
}

static void transfer_close_resource(network_state *s, resource *r) {
    (void)s;
    transfer *t = (transfer *)r;
    atomic_store(&t->cancelled, true);
    if (!t->done) engine_transfer_cancel(t);
}

void transfer_head(transfer *t, int status) {
    net_lock(&t->lock);
    t->head_status = status;
    net_unlock(&t->lock);
}

bool transfer_stream_bytes(transfer *t, const uint8_t *data, size_t length, net_error *e) {
    net_lock(&t->lock);
    bool ok = buffer_append(&t->unread, data, length) && t->unread.length <= NET_MAX_BODY;
    net_unlock(&t->lock);
    return ok || net_error_set(e, NET_LIMIT, false, MSG_STREAM_LIMIT);
}

/* Response payload: what JSON(), Bytes() and Header() read. */
typedef struct response_data { net_buffer body; net_headers headers; } response_data;

void response_release(aqua_runtime *rt, void *payload) {
    (void)rt;
    response_data *d = payload;
    buffer_free(&d->body);
    headers_free(&d->headers);
    free(d);
}

/* The Response record of a finished transfer (made once). */
static aqua_value transfer_response(aqua_runtime *rt, transfer *t) {
    if (t->response) return aqua_ref_value(rt, t->response);
    response_data *d = calloc(1, sizeof *d);
    d->body = t->body;
    memset(&t->body, 0, sizeof t->body);
    d->headers = t->headers;
    memset(&t->headers, 0, sizeof t->headers);
    net_object *o = object_create(CLASS_response, d);
    bool ok = t->error.kind == NET_OK && t->status >= 200 && t->status < 300;
    object_field(rt, o, FIELD_OF(response, success), aqua_bool(ok));
    object_field(rt, o, FIELD_OF(response, status), aqua_int(t->status));
    object_field(rt, o, FIELD_OF(response, url), aqua_cstring(rt, t->final_url ? t->final_url : ""));
    object_field(rt, o, FIELD_OF(response, body), aqua_text(rt, (const char *)d->body.data, d->body.length));
    object_field(rt, o, FIELD_OF(response, headers), headers_hash(rt, &d->headers, ", "));
    object_field(rt, o, FIELD_OF(response, error), t->error.kind == NET_OK ? aqua_null() : error_record(rt, &t->error));
    aqua_value value = object_wrap(rt, o);
    t->response = aqua_retain(rt, value);
    return value;
}

/* Result(): the response, closing the operation (old NetworkRuntime.Operation). */
static aqua_value operation_result(network_state *s, transfer *t) {
    aqua_value response = transfer_response(s->rt, t);
    resource_close(s, &t->base);
    return response;
}

void transfer_finished(network_state *s, transfer *t) {
    net_lock(&t->lock);
    t->done = true;
    net_unlock(&t->lock);
    if (!s->closing) {
        if (t->pending) {
            aqua_pending *pending = t->pending;
            t->pending = NULL;
            aqua_resolve(pending, transfer_response(s->rt, t));
            aqua_release(s->rt, t->response); /* a plain request keeps no object */
            t->response = 0;
        }
        for (uint32_t i = 0; i < t->waiter_count; i++) aqua_resolve(t->waiters[i], operation_result(s, t));
        t->waiter_count = 0;
    }
    transfer_unref(t);
}

void transfer_release(aqua_runtime *rt, void *payload) {
    transfer *t = payload;
    if (t->base.open) resource_close(t->base.state, &t->base);
    if (t->response) { aqua_release(rt, t->response); t->response = 0; }
    transfer_unref(t);
}

static bool method_valid(const char *method) {
    if (!*method) return false;
    for (const char *p = method; *p; p++)
        if (!header_valid((char[]){*p, 0}, NULL)) return false;
    return true;
}

typedef enum transfer_mode { MODE_CALL, MODE_OPERATION, MODE_STREAM } transfer_mode;

/* Request/Get/Post/StartRequest/StartStream on client `c`. `body_index` names Post's body argument. */
static void start_transfer(aqua_call *call, const char *name, client *c, const char *method, uint32_t url_index,
                           uint32_t options_index, int body_index, transfer_mode mode) {
    aqua_runtime *rt = aqua_call_runtime(call);
    network_state *s = aqua_state(call);
    net_error e = {NET_OK, false, ""};
    if (mode == MODE_STREAM && !feature_require("http-stream", &e)) { net_raise(call, name, &e); return; }
    transfer *t = calloc(1, sizeof *t);
    atomic_init(&t->refs, 1);
    atomic_init(&t->cancelled, false);
    net_mutex_init(&t->lock);
    t->base.close = transfer_close_resource;
    t->base.state = s;
    t->name = name;
    t->streaming = mode == MODE_STREAM;
    if (!options_read(call, options_index, &c->defaults, &t->options, &e)) { net_raise(call, name, &e); transfer_unref(t); return; }
    if (body_index >= 0) {
        if (!value_payload(rt, aqua_arg(call, (uint32_t)body_index), &t->options.body, &e)) { net_raise(call, name, &e); transfer_unref(t); return; }
        t->options.has_body = true;
    }
    t->method = net_strdup(method ? method : aqua_arg_str(call, 0).data);
    const char *url_text = aqua_arg_str(call, url_index).data;
    t->final_url = net_strdup(url_text);
    atomic_fetch_add(&c->refs, 1);
    t->client = c;
    /* Failures before any I/O become the response's error, like the old adapters. */
    if (atomic_load(&c->closed)) net_error_set(&t->error, NET_CLOSED, false, MSG_CLOSED);
    else if (!url_parse(url_text, &t->url) || (strcmp(t->url.scheme, "http") && strcmp(t->url.scheme, "https")))
        net_error_set(&t->error, NET_INVALID, false, MSG_HTTP_URL);
    else if (!method_valid(t->method)) net_error_set(&t->error, NET_INVALID, false, "Invalid HTTP method / 無效的 HTTP 方法。");
    if (mode != MODE_CALL && !resource_own(s, &t->base, &e)) { net_raise(call, name, &e); transfer_unref(t); return; }
    if (t->error.kind != NET_OK) {
        t->done = true;
        if (mode == MODE_CALL) { aqua_return(call, transfer_response(rt, t)); aqua_release(rt, t->response); t->response = 0; transfer_unref(t); return; }
    } else {
        if (mode == MODE_CALL) t->pending = aqua_defer(call);
        atomic_fetch_add(&t->refs, 1); /* the engine's */
        engine_transfer_start(s, t);
        if (mode == MODE_CALL) { transfer_unref(t); return; }
    }
    net_object *o = object_create(mode == MODE_STREAM ? CLASS_stream_operation : CLASS_operation, t);
    aqua_return(call, object_wrap(rt, o));
}

/* ---- Module and Client functions ---- */

void net_create_client(aqua_call *call, const char *name) {
    network_state *s = aqua_state(call);
    net_options defaults;
    net_error e;
    if (!options_read(call, 0, NULL, &defaults, &e)) { options_free(&defaults); net_raise(call, name, &e); return; }
    client *c = client_new(&defaults, &e);
    options_free(&defaults);
    if (!c) { net_raise(call, name, &e); return; }
    if (!resource_own(s, &c->base, &e)) { client_unref(c); net_raise(call, name, &e); return; }
    aqua_return(call, object_wrap(aqua_call_runtime(call), object_create(CLASS_client, c)));
}

#define SHARED(call) client_shared((network_state *)aqua_state(call))
void net_request(aqua_call *call, const char *name) { start_transfer(call, name, SHARED(call), NULL, 1, 2, -1, MODE_CALL); }
void net_get(aqua_call *call, const char *name) { start_transfer(call, name, SHARED(call), "GET", 0, 1, -1, MODE_CALL); }
void net_post(aqua_call *call, const char *name) { start_transfer(call, name, SHARED(call), "POST", 0, 2, 1, MODE_CALL); }
void net_start_request(aqua_call *call, const char *name) { start_transfer(call, name, SHARED(call), NULL, 1, 2, -1, MODE_OPERATION); }
void net_start_stream(aqua_call *call, const char *name) { start_transfer(call, name, SHARED(call), NULL, 1, 2, -1, MODE_STREAM); }

#define SELF_CLIENT(call) ((client *)self_payload(call))
void client_request(aqua_call *call, const char *name) { start_transfer(call, name, SELF_CLIENT(call), NULL, 1, 2, -1, MODE_CALL); }
void client_get(aqua_call *call, const char *name) { start_transfer(call, name, SELF_CLIENT(call), "GET", 0, 1, -1, MODE_CALL); }
void client_post(aqua_call *call, const char *name) { start_transfer(call, name, SELF_CLIENT(call), "POST", 0, 2, 1, MODE_CALL); }
void client_start_request(aqua_call *call, const char *name) { start_transfer(call, name, SELF_CLIENT(call), NULL, 1, 2, -1, MODE_OPERATION); }
void client_start_stream(aqua_call *call, const char *name) { start_transfer(call, name, SELF_CLIENT(call), NULL, 1, 2, -1, MODE_STREAM); }

void client_clear_cookies(aqua_call *call, const char *name) {
    client *c = SELF_CLIENT(call);
    net_error e;
    if (!feature_require("cookie-control", &e)) { net_raise(call, name, &e); return; }
    if (atomic_load(&c->closed)) { net_error_set(&e, NET_CLOSED, false, MSG_CLOSED); net_raise(call, name, &e); return; }
    if (!engine_client_clear_cookies(c, &e)) net_raise(call, name, &e);
}

void client_close(aqua_call *call, const char *name) {
    (void)name;
    client *c = SELF_CLIENT(call);
    resource_close(aqua_state(call), &c->base);
}

/* ---- Operations ---- */

#define SELF_TRANSFER(call) ((transfer *)self_payload(call))

void operation_is_done(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    net_lock(&t->lock);
    bool done = t->done;
    net_unlock(&t->lock);
    aqua_return_bool(call, done);
}
void operation_cancel(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    if (t->base.open && !t->done) { atomic_store(&t->cancelled, true); engine_transfer_cancel(t); }
}
void operation_poll(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    if (t->done) aqua_return(call, operation_result(aqua_state(call), t));
    else aqua_return_null(call);
}
void operation_wait(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    if (t->done) { aqua_return(call, operation_result(aqua_state(call), t)); return; }
    aqua_pending **waiters = realloc(t->waiters, (t->waiter_count + 1) * sizeof *waiters);
    if (!waiters) return;
    t->waiters = waiters;
    t->waiters[t->waiter_count++] = aqua_defer(call);
}
void operation_close(aqua_call *call, const char *name) {
    (void)name;
    resource_close(aqua_state(call), &SELF_TRANSFER(call)->base);
}

/* ---- Streams: Read drains decoded text, ReadLines complete lines (PendingStream). ---- */

/* The readable part of the unread text: all of it once the transfer is done, else up to the last complete UTF-8
   sequence (a multi-byte character split across chunks decodes once complete). */
static size_t readable(transfer *t) {
    if (t->done) return t->unread.length;
    return t->unread.length - utf8_incomplete_tail(t->unread.data, t->unread.length);
}

void stream_operation_read(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    net_lock(&t->lock);
    size_t n = readable(t);
    aqua_return_text(call, n ? (const char *)t->unread.data : "", n);
    buffer_consume(&t->unread, n);
    net_unlock(&t->lock);
}

void stream_operation_read_lines(aqua_call *call, const char *name) {
    (void)name;
    aqua_runtime *rt = aqua_call_runtime(call);
    transfer *t = SELF_TRANSFER(call);
    aqua_value lines = aqua_array_new(rt, 0);
    net_lock(&t->lock);
    bool done = t->done;
    size_t n = readable(t);
    const char *text = (const char *)t->unread.data;
    size_t end = n;
    while (end > 0 && text[end - 1] != '\n') end--; /* one past the last newline, or 0 */
    if (end == 0 && !(done && n > 0)) { net_unlock(&t->lock); aqua_return(call, lines); return; }
    size_t complete = done ? n : end - 1, taken = done ? n : end;
    if (done && complete > 0 && text[complete - 1] == '\n') complete--;
    for (size_t start = 0;;) {
        size_t stop = start;
        while (stop < complete && text[stop] != '\n') stop++;
        size_t length = stop - start;
        if (length && text[start + length - 1] == '\r') length--;
        aqua_array_push(rt, lines, aqua_text(rt, text + start, length));
        if (stop >= complete) break;
        start = stop + 1;
    }
    buffer_consume(&t->unread, taken);
    net_unlock(&t->lock);
    aqua_return(call, lines);
}

void stream_operation_status(aqua_call *call, const char *name) {
    (void)name;
    transfer *t = SELF_TRANSFER(call);
    net_lock(&t->lock);
    int status = t->done ? t->status : t->head_status;
    net_unlock(&t->lock);
    aqua_return_int(call, status);
}

/* ---- Response methods ---- */

#define SELF_RESPONSE(call) ((response_data *)self_payload(call))
void response_json(aqua_call *call, const char *name) {
    response_data *d = SELF_RESPONSE(call);
    aqua_value value;
    net_error e;
    if (!json_decode(aqua_call_runtime(call), d->body.data, d->body.length, &value, &e)) net_raise(call, name, &e);
    else aqua_return(call, value);
}
void response_bytes(aqua_call *call, const char *name) {
    (void)name;
    response_data *d = SELF_RESPONSE(call);
    aqua_return(call, bytes_value(aqua_call_runtime(call), d->body.data, d->body.length));
}
void response_header(aqua_call *call, const char *name) {
    (void)name;
    response_data *d = SELF_RESPONSE(call);
    const char *wanted = aqua_arg_str(call, 0).data;
    /* Repeated headers read as one value joined with ", ", like the Headers hash. */
    net_buffer joined = {0};
    bool found = false;
    for (uint32_t i = 0; i < d->headers.count; i++) {
        if (!net_iequal(d->headers.items[i].name, wanted)) continue;
        if (found) buffer_text(&joined, ", ");
        buffer_text(&joined, d->headers.items[i].value);
        found = true;
    }
    aqua_return_text(call, found ? (const char *)joined.data : "", joined.length);
    buffer_free(&joined);
}
