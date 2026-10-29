/* The web engine: HTTP through the browser's fetch (streamed bodies, AbortController) and WebSocket through the
 * browser's WebSocket, reached through web-glue/network, plus setTimeout timers. Requests, limits, deadlines,
 * redirect modes, error mapping and message queues are decided here; the glue makes one Web API call per function and
 * reports events back. The browser applies CORS, cookies, forbidden headers and its own redirect limit. Every event
 * completes on the runtime's thread through aqua_post. */
#include "../engine.h"
#include <stdlib.h>
#include <string.h>

typedef void (*web_event)(void *data, int32_t kind, int32_t a, int32_t b);
AQUA_WEB_IMPORT(network_fetch) int32_t web_fetch(const char *method, const char *url, const char *headers, uint32_t headers_length,
                                                 const uint8_t *body, uint32_t body_length, int32_t has_body, int32_t credentials,
                                                 int32_t manual_redirect, web_event event, void *data);
AQUA_WEB_IMPORT(network_fetch_abort) void web_fetch_abort(int32_t handle);
AQUA_WEB_IMPORT(network_release) void web_release(int32_t handle);
AQUA_WEB_IMPORT(network_socket_open) int32_t web_socket_open(const char *url, web_event event, void *data);
AQUA_WEB_IMPORT(network_socket_open_protocols) int32_t web_socket_open_protocols(const char *url, const char *protocols, uint32_t length, web_event event, void *data);
AQUA_WEB_IMPORT(network_socket_protocol) char *web_socket_protocol(int32_t handle);
AQUA_WEB_IMPORT(network_page_host) char *web_page_host(void);
AQUA_WEB_IMPORT(network_page_port) char *web_page_port(void);
AQUA_WEB_IMPORT(network_page_secure) int32_t web_page_secure(void);
AQUA_WEB_IMPORT(network_socket_send) double web_socket_send(int32_t handle, const uint8_t *data, uint32_t length, int32_t text);
AQUA_WEB_IMPORT(network_socket_buffered) double web_socket_buffered(int32_t handle);
AQUA_WEB_IMPORT(network_socket_close) void web_socket_close(int32_t handle, int32_t code, const char *reason);
AQUA_WEB_IMPORT(network_timer_start) int32_t web_timer_start(double ms, void (*fire)(void *data), void *data);
AQUA_WEB_IMPORT(network_timer_cancel) void web_timer_cancel(int32_t handle);

enum { FETCH_HEAD = 1, FETCH_CHUNK = 2, FETCH_END = 3, FETCH_ERROR = 4 };
enum { SOCKET_OPEN = 1, SOCKET_TEXT = 2, SOCKET_BINARY = 3, SOCKET_CLOSE = 4, SOCKET_ERROR = 5 };
#define QUEUE_MESSAGES 256

/* Work the engine still owns, so a runtime that ends releases every handle the glue holds. */
typedef struct web_item { struct web_item *previous, *next; int32_t handle; int kind; void *owner; } web_item;
struct engine { network_state *state; web_item *items; };

static void item_add(engine *e, web_item *item, int kind, void *owner) {
    item->kind = kind;
    item->owner = owner;
    item->previous = NULL;
    item->next = e->items;
    if (e->items) e->items->previous = item;
    e->items = item;
}
static void item_remove(engine *e, web_item *item) {
    if (item->previous) item->previous->next = item->next;
    else if (e->items == item) e->items = item->next;
    if (item->next) item->next->previous = item->previous;
    item->previous = item->next = NULL;
}

engine *engine_create(network_state *s) {
    engine *e = calloc(1, sizeof *e);
    e->state = s;
    return e;
}

/* ---- Timers ---- */

struct net_timer {
    web_item item;
    network_state *state;
    void (*fire)(network_state *s, void *data);
    void *data;
    bool fired, cancelled;
};

static void timer_task(aqua_runtime *rt, void *data) {
    (void)rt;
    net_timer *t = data;
    if (!t->cancelled && !t->state->closing) t->fire(t->state, t->data);
    free(t);
}
static void timer_fired(void *data) {
    net_timer *t = data;
    t->fired = true;
    item_remove(t->state->engine, &t->item);
    web_release(t->item.handle);
    aqua_post(t->state->rt, timer_task, t);
}

net_timer *timer_start(network_state *s, double ms, void (*fire)(network_state *s, void *data), void *data) {
    net_timer *t = calloc(1, sizeof *t);
    t->state = s;
    t->fire = fire;
    t->data = data;
    t->item.handle = web_timer_start(ms > 0 ? ms : 0, timer_fired, t);
    item_add(s->engine, &t->item, 0, t);
    return t;
}

void timer_cancel(network_state *s, net_timer *t) {
    if (!t) return;
    if (t->fired) { t->cancelled = true; return; }
    item_remove(s->engine, &t->item);
    web_timer_cancel(t->item.handle);
    free(t);
}

/* ---- Clients and transfers ---- */

bool engine_client_open(client *c, net_error *e) { (void)c; (void)e; return true; }
void engine_client_close(client *c) { (void)c; }
void engine_client_free(client *c) { (void)c; }
bool engine_client_clear_cookies(client *c, net_error *e) { (void)c; return feature_require("cookie-control", e); }

typedef struct web_transfer {
    web_item item;
    transfer *transfer;
    network_state *state;
    net_timer *deadline;
    bool timed_out, ended, limited, unsupported;
} web_transfer;

static void transfer_task(aqua_runtime *rt, void *data) {
    (void)rt;
    web_transfer *w = data;
    transfer *t = w->transfer;
    network_state *s = w->state;
    t->engine = NULL;
    free(w);
    transfer_finished(s, t);
}

/* Ends the transfer once: releases the glue's fetch and the deadline, then delivers on the next turn. */
static void transfer_end(web_transfer *w) {
    if (w->ended) return;
    w->ended = true;
    network_state *s = w->state;
    if (w->deadline) { timer_cancel(s, w->deadline); w->deadline = NULL; }
    item_remove(s->engine, &w->item);
    web_release(w->item.handle);
    aqua_post(s->rt, transfer_task, w);
}

static void transfer_fail(web_transfer *w, net_kind kind, bool retryable, const char *message) {
    transfer *t = w->transfer;
    t->status = 0;
    headers_free(&t->headers);
    buffer_free(&t->body);
    net_error_set(&t->error, kind, retryable, "%s", message);
    transfer_end(w);
}

static void deadline_passed(network_state *s, void *data) {
    (void)s;
    web_transfer *w = data;
    w->deadline = NULL;
    w->timed_out = true;
    web_fetch_abort(w->item.handle);
}
static void restart_deadline(web_transfer *w) {
    if (w->deadline) timer_cancel(w->state, w->deadline);
    w->deadline = timer_start(w->state, w->transfer->options.timeout_ms, deadline_passed, w);
}

static void fetch_event(void *data, int32_t kind, int32_t a, int32_t b) {
    web_transfer *w = data;
    transfer *t = w->transfer;
    if (w->ended) return;
    switch (kind) {
    case FETCH_HEAD: {
        /* b: [u32 length][final url NUL][name NUL value NUL]... allocated by the glue. */
        uint8_t *block = (uint8_t *)(uintptr_t)b;
        uint32_t length;
        memcpy(&length, block, 4);
        const char *p = (const char *)block + 4, *end = p + length;
        free(t->final_url);
        t->final_url = net_strdup(p);
        p += strlen(p) + 1;
        while (p < end) {
            const char *name = p;
            p += strlen(p) + 1;
            if (p >= end) break;
            headers_add(&t->headers, name, p);
            p += strlen(p) + 1;
        }
        free(block);
        if (a < 0) { web_fetch_abort(w->item.handle); transfer_fail(w, NET_UNSUPPORTED, false, "Unsupported / 瀏覽器無法讀取手動重新導向回應。"); return; }
        t->status = a;
        const char *declared = headers_get(&t->headers, "content-length");
        if (declared && strtod(declared, NULL) > t->options.max_body) {
            w->limited = true;
            web_fetch_abort(w->item.handle);
            return;
        }
        if (t->streaming) { transfer_head(t, a); restart_deadline(w); }
        return;
    }
    case FETCH_CHUNK: {
        uint8_t *bytes = (uint8_t *)(uintptr_t)a;
        if (w->limited) { free(bytes); return; }
        if (t->body.length + (size_t)b > (size_t)t->options.max_body) {
            free(bytes);
            w->limited = true;
            web_fetch_abort(w->item.handle);
            return;
        }
        buffer_append(&t->body, bytes, (size_t)b);
        net_error e;
        bool ok = !t->streaming || transfer_stream_bytes(t, bytes, (size_t)b, &e);
        free(bytes);
        if (!ok) { web_fetch_abort(w->item.handle); transfer_fail(w, e.kind, false, e.message); return; }
        if (t->streaming) restart_deadline(w);
        return;
    }
    case FETCH_END:
        if (t->status >= 400) net_error_set(&t->error, NET_HTTP, t->status == 408 || t->status == 429 || t->status >= 500, "HTTP %d", t->status);
        transfer_end(w);
        return;
    case FETCH_ERROR: {
        char *message = (char *)(uintptr_t)b;
        if (w->limited) transfer_fail(w, NET_LIMIT, false, MSG_BODY_LIMIT);
        else if (w->timed_out) transfer_fail(w, NET_TIMEOUT, true, MSG_TIMEOUT);
        else if (atomic_load(&t->cancelled)) transfer_fail(w, NET_CANCELLED, false, MSG_CANCELLED);
        else transfer_fail(w, NET_NETWORK, true, message && *message ? message : "Network / 網路錯誤。");
        free(message);
        return;
    }
    }
}

void engine_transfer_start(network_state *s, transfer *t) {
    web_transfer *w = calloc(1, sizeof *w);
    w->transfer = t;
    w->state = s;
    t->engine = w;
    net_buffer headers = {0};
    for (uint32_t i = 0; i < t->options.headers.count; i++) {
        buffer_append(&headers, t->options.headers.items[i].name, strlen(t->options.headers.items[i].name) + 1);
        buffer_append(&headers, t->options.headers.items[i].value, strlen(t->options.headers.items[i].value) + 1);
    }
    char *url = url_format(&t->url, true);
    item_add(s->engine, &w->item, 1, w);
    w->item.handle = web_fetch(t->method, url, (const char *)headers.data, (uint32_t)headers.length, t->options.body.data,
                               (uint32_t)t->options.body.length, t->options.has_body, t->options.credentials, !t->options.follow_redirects,
                               fetch_event, w);
    free(url);
    buffer_free(&headers);
    restart_deadline(w);
}

void engine_transfer_cancel(transfer *t) {
    web_transfer *w = t->engine;
    if (w && !w->ended) web_fetch_abort(w->item.handle);
}

/* ---- WebSockets ---- */

typedef struct queued { uint8_t *data; size_t length; bool text; struct queued *next; } queued;
typedef struct web_socket {
    web_item item;
    websocket *socket;
    network_state *state;
    bool open, closed, aborted, released;
    net_error error;              /* a failure the next Receive reports */
    queued *head, *tail;
    uint32_t count;
    size_t bytes;
    aqua_pending *connecting, *receiving;
    const char *connect_name, *receive_name;
    net_timer *timer;
} web_socket;

typedef struct socket_task { aqua_pending *pending; const char *name; net_error error; bool failed; aqua_value value; } socket_task;

static void socket_task_run(aqua_runtime *rt, void *data) {
    (void)rt;
    socket_task *task = data;
    if (task->failed) net_reject(task->pending, task->name, &task->error);
    else aqua_resolve(task->pending, task->value);
    free(task);
}
static void settle_later(network_state *s, aqua_pending *pending, const char *name, const net_error *e) {
    socket_task *task = calloc(1, sizeof *task);
    task->pending = pending;
    task->name = name;
    task->value = aqua_null();
    if (e) { task->error = *e; task->failed = true; }
    aqua_post(s->rt, socket_task_run, task);
}

static void release_socket(web_socket *w) {
    if (w->released) return;
    w->released = true;
    item_remove(w->state->engine, &w->item);
    web_release(w->item.handle);
}

static web_socket *socket_of(websocket *ws) { return ws->engine; }

/* Delivers the next message, the end of the socket or its failure to a waiting Receive. */
static void deliver(web_socket *w) {
    if (!w->receiving) return;
    network_state *s = w->state;
    aqua_pending *pending = w->receiving;
    const char *name = w->receive_name;
    if (w->error.kind == NET_OK && !w->head && !w->closed) return;
    w->receiving = NULL;
    w->socket->receiving = false;
    if (w->timer) { timer_cancel(s, w->timer); w->timer = NULL; }
    if (w->error.kind != NET_OK) { net_reject(pending, name, &w->error); return; }
    queued *q = w->head;
    if (q) {
        w->head = q->next;
        if (!w->head) w->tail = NULL;
        w->count--;
        w->bytes -= q->length;
        aqua_resolve(pending, message_record(s->rt, q->data, q->length, q->text, false));
        free(q->data);
        free(q);
        return;
    }
    aqua_resolve(pending, close_record(s->rt, w->socket));
}

typedef struct socket_event_task { web_socket *socket; int32_t kind; uint8_t *data; size_t length; int32_t code; } socket_event_task;

static void socket_event_run(aqua_runtime *rt, void *data) {
    (void)rt;
    socket_event_task *task = data;
    web_socket *w = task->socket;
    network_state *s = w->state;
    websocket *ws = w->socket;
    if (s->closing) { free(task->data); free(task); websocket_unref(ws); return; }
    switch (task->kind) {
    case SOCKET_OPEN:
        w->open = true;
        if (!ws->protocol) {
            char *chosen = web_socket_protocol(w->item.handle);
            if (chosen && *chosen) ws->protocol = net_strdup(chosen);
            free(chosen);
        }
        if (w->timer) { timer_cancel(s, w->timer); w->timer = NULL; }
        if (w->connecting) {
            aqua_pending *pending = w->connecting;
            w->connecting = NULL;
            websocket_connected(s, ws, pending, w->connect_name, NULL); /* takes the connect operation's reference */
        }
        break;
    case SOCKET_TEXT: case SOCKET_BINARY:
        if (task->length > (size_t)ws->max_message || w->bytes + task->length > NET_MAX_BODY || w->count >= QUEUE_MESSAGES) {
            net_error_set(&w->error, NET_LIMIT, false, "LimitExceeded / WebSocket 佇列超過上限。");
            if (!w->released) web_socket_close(w->item.handle, 1000, "");
            w->closed = true;
            break;
        }
        queued *q = calloc(1, sizeof *q);
        q->data = task->data;
        q->length = task->length;
        q->text = task->kind == SOCKET_TEXT;
        task->data = NULL;
        if (w->tail) w->tail->next = q; else w->head = q;
        w->tail = q;
        w->count++;
        w->bytes += q->length;
        break;
    case SOCKET_CLOSE: case SOCKET_ERROR:
        if (task->kind == SOCKET_CLOSE && !ws->close_code) {
            ws->close_code = task->code ? task->code : 1005;
            if (task->data && task->data[0]) ws->close_reason = net_strdup((const char *)task->data);
        }
        if (task->kind == SOCKET_ERROR && w->error.kind == NET_OK) net_error_set(&w->error, NET_NETWORK, true, "Network / WebSocket 連線失敗。");
        w->closed = true;
        if (w->connecting) {
            aqua_pending *pending = w->connecting;
            w->connecting = NULL;
            net_error e;
            net_error_set(&e, NET_NETWORK, true, "Network / WebSocket 連線失敗。");
            if (w->timer) { timer_cancel(s, w->timer); w->timer = NULL; }
            websocket_connected(s, ws, pending, w->connect_name, &e);
        }
        break;
    }
    deliver(w);
    free(task->data);
    free(task);
    websocket_unref(ws);
}

static void socket_event(void *data, int32_t kind, int32_t a, int32_t b) {
    web_socket *w = data;
    if (w->released) { if (kind == SOCKET_TEXT || kind == SOCKET_BINARY) free((void *)(uintptr_t)a); return; }
    socket_event_task *task = calloc(1, sizeof *task);
    task->socket = w;
    task->kind = kind;
    if (kind == SOCKET_TEXT || kind == SOCKET_BINARY) { task->data = (uint8_t *)(uintptr_t)a; task->length = (size_t)b; }
    /* A close's code, and its reason in memory from malloc. */
    if (kind == SOCKET_CLOSE) { task->code = a; task->data = (uint8_t *)(uintptr_t)b; }
    atomic_fetch_add(&w->socket->refs, 1);
    aqua_post(w->state->rt, socket_event_run, task);
}

static void connect_timeout(network_state *s, void *data) {
    web_socket *w = data;
    w->timer = NULL;
    if (!w->connecting) return;
    aqua_pending *pending = w->connecting;
    w->connecting = NULL;
    w->closed = true;
    web_socket_close(w->item.handle, 1000, "");
    release_socket(w);
    net_error e;
    net_error_set(&e, NET_TIMEOUT, true, MSG_TIMEOUT);
    websocket_connected(s, w->socket, pending, w->connect_name, &e);
}

void engine_websocket_connect(network_state *s, websocket *ws, const net_url *url, const net_options *o, aqua_pending *pending, const char *name) {
    web_socket *w = calloc(1, sizeof *w);
    w->socket = ws;
    w->state = s;
    ws->engine = w;
    if (o->headers.count) {
        /* Browsers cannot set handshake headers. */
        w->released = true;
        net_error e;
        net_error_set(&e, NET_UNSUPPORTED, false, "Unsupported / 瀏覽器無法設定 WebSocket 標頭。");
        settle_later(s, pending, name, &e);
        websocket_unref(ws);
        return;
    }
    w->connecting = pending;
    w->connect_name = name;
    char *text = url_format(url, false);
    item_add(s->engine, &w->item, 2, w);
    if (o->protocols) {
        /* "a, b" as the NUL-separated list the glue reads. */
        net_buffer list = {0};
        for (const char *p = o->protocols; *p;) {
            size_t n = strcspn(p, ",");
            buffer_append(&list, p, n);
            buffer_append(&list, "", 1);
            p += n;
            while (*p == ',' || *p == ' ') p++;
        }
        w->item.handle = web_socket_open_protocols(text, (const char *)list.data, (uint32_t)list.length, socket_event, w);
        buffer_free(&list);
    } else w->item.handle = web_socket_open_protocols(text, "", 0, socket_event, w); /* reports close reasons */
    free(text);
    /* The connect operation's reference keeps the socket until websocket_connected takes it. */
    w->timer = timer_start(s, o->timeout_ms, connect_timeout, w);
}

void engine_websocket_send(network_state *s, websocket *ws, net_buffer *message, bool text, aqua_pending *pending, const char *name) {
    web_socket *w = socket_of(ws);
    net_error e;
    if (!w->open || w->closed || w->released) {
        net_error_set(&e, NET_CLOSED, false, "Closed / WebSocket 已關閉。");
        settle_later(s, pending, name, &e);
        return;
    }
    if (web_socket_buffered(w->item.handle) + (double)message->length > NET_MAX_BODY) {
        net_error_set(&e, NET_LIMIT, false, "LimitExceeded / 傳送佇列超過上限。");
        settle_later(s, pending, name, &e);
        return;
    }
    web_socket_send(w->item.handle, message->data, (uint32_t)message->length, text);
    settle_later(s, pending, name, NULL);
}

typedef struct receive_task { websocket *socket; } receive_task;
static void receive_now(aqua_runtime *rt, void *data) {
    (void)rt;
    receive_task *task = data;
    web_socket *w = socket_of(task->socket);
    if (w && !w->state->closing) deliver(w);
    websocket_unref(task->socket);
    free(task);
}

void engine_websocket_receive(network_state *s, websocket *ws, aqua_pending *pending, const char *name) {
    web_socket *w = socket_of(ws);
    ws->receiving = true;
    w->receiving = pending;
    w->receive_name = name;
    if (w->head || w->closed || w->error.kind != NET_OK) {
        receive_task *task = calloc(1, sizeof *task);
        atomic_fetch_add(&ws->refs, 1);
        task->socket = ws;
        aqua_post(s->rt, receive_now, task);
        return;
    }
    /* No deadline: the next message may take as long as it takes (the timeout limits connecting and sending). */
}

void engine_websocket_close(network_state *s, websocket *ws, int code, const char *reason, aqua_pending *pending, const char *name) {
    web_socket *w = socket_of(ws);
    if (!w->released) web_socket_close(w->item.handle, code, reason);
    w->closed = true;
    resource_close(s, &ws->base);
    settle_later(s, pending, name, NULL);
}

void engine_websocket_abort(websocket *ws) {
    web_socket *w = socket_of(ws);
    if (!w) return;
    if (!w->released) web_socket_close(w->item.handle, 1000, "");
    w->closed = true;
    w->aborted = true;
    if (w->timer) { timer_cancel(w->state, w->timer); w->timer = NULL; }
    release_socket(w);
    deliver(w);
}

void engine_websocket_free(websocket *ws) {
    web_socket *w = socket_of(ws);
    if (!w) return;
    if (w->timer) timer_cancel(w->state, w->timer);
    release_socket(w);
    while (w->head) { queued *q = w->head; w->head = q->next; free(q->data); free(q); }
    free(w);
    ws->engine = NULL;
}

/* ---- Shutdown ---- */

void engine_destroy(engine *e) {
    if (!e) return;
    while (e->items) {
        web_item *item = e->items;
        item_remove(e, item);
        if (item->kind == 0) { web_timer_cancel(item->handle); free(item->owner); }
        else {
            if (item->kind == 1) web_fetch_abort(item->handle);
            else web_socket_close(item->handle, 1000, "");
            web_release(item->handle);
            if (item->kind == 2) ((web_socket *)item->owner)->released = true;
        }
    }
    free(e);
}

#include <unistd.h>
/* The browser's crypto.getRandomValues through WASI (getentropy takes at most 256 bytes at a time). */
static bool engine_random(void *buffer, size_t length) {
    for (size_t done = 0; done < length;) {
        size_t n = length - done > 256 ? 256 : length - done;
        if (getentropy((uint8_t *)buffer + done, n) != 0) return false;
        done += n;
    }
    return true;
}
static char *engine_page_origin(void) {
    char *host = web_page_host(), *port = web_page_port();
    net_buffer origin = {0};
    if (host && *host) buffer_format(&origin, "%s://%s%s%s", web_page_secure() ? "https" : "http", host, port && *port ? ":" : "", port ? port : "");
    free(host);
    free(port);
    char *text = origin.length ? buffer_take(&origin, NULL) : NULL;
    buffer_free(&origin);
    return text;
}
