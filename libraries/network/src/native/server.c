/* The HTTP(S) server (Network.Listen), with the old Kestrel-based server's behavior: routes with {name} and a
 * trailing *, middleware, static files with ETags, CORS with exact origins, cookies, in-memory sessions, Server-Sent
 * Events and WebSocket upgrades. Worker threads accept connections, read requests (HTTP/1.1 keep-alive, bodies up to
 * maxBodyBytes), queue them (at most 128, else 503; at most 256 connections) and write what handlers produce; every
 * handler runs on the runtime's thread. A request must complete within the server's timeout, as before. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#if defined(AQUA_OS_WINDOWS)
#include <windows.h>
#else
#include <arpa/inet.h>
#include <errno.h>
#include <limits.h>
#include <netinet/tcp.h>
#include <unistd.h>
#endif

#define QUEUE_LIMIT 128
#define CONNECTION_LIMIT 256
#define HEADERS_TIMEOUT_MS 15000
#define KEEP_ALIVE_MS 30000
#define SESSION_LIMIT 10000
#define SESSION_FIELDS 128
#define SESSION_IDLE_SECONDS (30 * 60)
#define DRAIN_LIMIT (1024 * 1024)

typedef struct server server;
typedef struct connection connection;
typedef struct exchange exchange;

typedef struct route { char *method, *pattern; aqua_ref handler; } route;
typedef struct public_folder { char *prefix, *root; } public_folder;
typedef struct session_entry { char token[65]; net_headers values; int64_t expires; } session_entry;
typedef struct poll_wait { aqua_pending *pending; net_timer *timer; server *server; struct poll_wait *next; } poll_wait;

struct server {
    resource base;
    atomic_int refs;
    network_state *state;
    net_options options;
    tls_server *tls;
    net_fd listeners[2];
    int listener_count;
    char *address;
    bool secure;
    atomic_bool stopping;
    atomic_int connections;
    /* Registration (runtime thread). */
    route *routes; uint32_t route_count;
    aqua_ref *middleware; uint32_t middleware_count;
    public_folder *folders; uint32_t folder_count;
    char **origins; uint32_t origin_count;
    session_entry *sessions; uint32_t session_count;
    /* Requests from connections. */
    net_mutex lock;
    exchange *queue_head, *queue_tail;
    uint32_t queued;
    /* Dispatch (runtime thread). */
    aqua_pending *serve;
    poll_wait *polls;
    uint32_t active;
    bool closed, shut;
};

struct connection {
    atomic_int refs;
    server *server;
    net_conn *conn;
    net_buffer in;
    bool upgraded;            /* the WebSocket owns the socket */
};

struct exchange {
    atomic_int refs;
    connection *connection;
    server *server;
    /* The request, fixed once queued. */
    char *method, *path, *target, *query, *url, *remote, *origin_self;
    bool secure, keep_alive, head;
    int minor;
    net_headers headers;
    net_buffer body;
    exchange *next;
    /* Runtime thread. */
    bool active, started, chunked, upgraded, finished;
    int status;
    net_headers response;
    net_headers params;
    char *session;
    aqua_ref request_object, writer_object;
    uint32_t step;
    aqua_pending *poll;
    aqua_pending *drain;          /* UpgradeWebSocket or a writer waiting for output to flush */
    aqua_value drain_result;
    websocket *socket;
    const char *drain_name;
    /* Output to the connection's worker (lock). */
    net_mutex lock;
    pthread_cond_t wake;
    net_buffer out;
    bool complete, upgrade, failed, drain_wanted;
    net_buffer leftover;          /* bytes after an upgrade request, for the WebSocket */
    int64_t deadline;
};

static void server_unref(server *sv);
static void dispatch(server *sv);

/* ---- Small helpers ---- */

static const char *reason_phrase(int status) {
    switch (status) {
    case 100: return "Continue"; case 101: return "Switching Protocols"; case 200: return "OK"; case 201: return "Created";
    case 202: return "Accepted"; case 204: return "No Content"; case 206: return "Partial Content";
    case 301: return "Moved Permanently"; case 302: return "Found"; case 303: return "See Other"; case 304: return "Not Modified";
    case 307: return "Temporary Redirect"; case 308: return "Permanent Redirect"; case 400: return "Bad Request";
    case 401: return "Unauthorized"; case 403: return "Forbidden"; case 404: return "Not Found"; case 405: return "Method Not Allowed";
    case 408: return "Request Timeout"; case 409: return "Conflict"; case 410: return "Gone"; case 413: return "Payload Too Large";
    case 415: return "Unsupported Media Type"; case 422: return "Unprocessable Entity"; case 429: return "Too Many Requests";
    case 431: return "Request Header Fields Too Large"; case 500: return "Internal Server Error"; case 501: return "Not Implemented";
    case 502: return "Bad Gateway"; case 503: return "Service Unavailable"; case 504: return "Gateway Timeout";
    default: return "";
    }
}

/* name=value pairs of a query string or form body: '+' is a space, the last value of a name wins. */
static void parse_pairs(const char *text, size_t length, net_headers *out) {
    const char *p = text, *end = text + length;
    while (p < end) {
        const char *amp = memchr(p, '&', (size_t)(end - p));
        const char *stop = amp ? amp : end;
        if (stop > p) {
            const char *eq = memchr(p, '=', (size_t)(stop - p));
            char *key = url_unescape(p, (size_t)((eq ? eq : stop) - p), true);
            char *value = eq ? url_unescape(eq + 1, (size_t)(stop - eq - 1), true) : net_strdup("");
            headers_remove(out, key);
            headers_add(out, key, value);
            free(key);
            free(value);
        }
        p = stop + 1;
    }
}
static const char *pair_value(const net_headers *pairs, const char *name) {
    for (uint32_t i = 0; i < pairs->count; i++) if (!strcmp(pairs->items[i].name, name)) return pairs->items[i].value;
    return NULL;
}

/* Values of the Cookie header, unescaped. */
static char *cookie_value(const exchange *x, const char *name) {
    for (uint32_t i = 0; i < x->headers.count; i++) {
        if (!net_iequal(x->headers.items[i].name, "Cookie")) continue;
        const char *p = x->headers.items[i].value;
        size_t n = strlen(name);
        while (*p) {
            while (*p == ' ' || *p == ';') p++;
            const char *end = strchr(p, ';');
            if (!end) end = p + strlen(p);
            const char *eq = memchr(p, '=', (size_t)(end - p));
            if (eq && (size_t)(eq - p) == n && !strncmp(p, name, n)) {
                const char *value = eq + 1, *stop = end;
                while (stop > value && stop[-1] == ' ') stop--;
                if (stop - value >= 2 && *value == '"' && stop[-1] == '"') { value++; stop--; }
                return url_unescape(value, (size_t)(stop - value), false);
            }
            p = end;
        }
    }
    return NULL;
}

/* ---- Exchanges and connections ---- */

static void connection_unref(connection *c) {
    if (atomic_fetch_sub(&c->refs, 1) != 1) return;
    conn_free(c->conn);
    buffer_free(&c->in);
    atomic_fetch_sub(&c->server->connections, 1);
    server_unref(c->server);
    free(c);
}
static void connection_release(void *owner) { connection_unref(owner); }

static void exchange_unref(exchange *x) {
    if (atomic_fetch_sub(&x->refs, 1) != 1) return;
    free(x->method); free(x->path); free(x->target); free(x->query); free(x->url); free(x->remote); free(x->origin_self);
    headers_free(&x->headers); headers_free(&x->response); headers_free(&x->params);
    buffer_free(&x->body); buffer_free(&x->out); buffer_free(&x->leftover);
    free(x->session);
    net_mutex_destroy(&x->lock);
    pthread_cond_destroy(&x->wake);
    if (x->connection) connection_unref(x->connection);
    free(x);
}

/* Output for the worker; false once the client is gone. */
static bool exchange_send(exchange *x, const void *data, size_t length) {
    net_lock(&x->lock);
    bool ok = !x->failed;
    if (ok) buffer_append(&x->out, data, length);
    pthread_cond_signal(&x->wake);
    net_unlock(&x->lock);
    return ok;
}

/* The status line and headers, sent with the first body bytes (or at the end). */
static void send_head(exchange *x, bool ends_now) {
    if (x->started) return;
    x->started = true;
    net_buffer h = {0};
    bool bodyless = x->head || x->status == 204 || x->status == 304;
    buffer_format(&h, "HTTP/1.1 %d %s\r\n", x->status, reason_phrase(x->status));
    char date[32];
    http_date(date, (int64_t)time(NULL));
    buffer_format(&h, "Date: %s\r\n", date);
    for (uint32_t i = 0; i < x->response.count; i++)
        buffer_format(&h, "%s: %s\r\n", x->response.items[i].name, x->response.items[i].value);
    if (ends_now) {
        if (x->status != 204 && x->status != 304) buffer_text(&h, "Content-Length: 0\r\n");
    } else if (x->minor >= 1) {
        x->chunked = !bodyless;
        if (!bodyless || x->head) buffer_text(&h, "Transfer-Encoding: chunked\r\n");
    } else x->keep_alive = false;
    if (!x->keep_alive) buffer_text(&h, "Connection: close\r\n");
    buffer_text(&h, "\r\n");
    exchange_send(x, h.data, h.length);
    buffer_free(&h);
}

static void exchange_finish_output(exchange *x) {
    if (!x->upgraded) {
        if (!x->started) send_head(x, true);
        else if (x->chunked) exchange_send(x, "0\r\n\r\n", 5);
    }
    net_lock(&x->lock);
    x->complete = true;
    pthread_cond_signal(&x->wake);
    net_unlock(&x->lock);
}

/* Body bytes as the old Write: nothing for HEAD, 204 and 304. */
static bool exchange_write(exchange *x, const void *data, size_t length) {
    send_head(x, false);
    if (x->head || x->status == 204 || x->status == 304 || !length) return !x->failed;
    if (!x->chunked) return exchange_send(x, data, length);
    char size[24];
    int n = snprintf(size, sizeof size, "%zx\r\n", length);
    net_lock(&x->lock);
    bool ok = !x->failed;
    if (ok) { buffer_append(&x->out, size, (size_t)n); buffer_append(&x->out, data, length); buffer_append(&x->out, "\r\n", 2); }
    pthread_cond_signal(&x->wake);
    net_unlock(&x->lock);
    return ok;
}

static void write_simple(net_conn *c, int status, const char *body, const net_io *io) {
    net_buffer b = {0};
    char date[32];
    http_date(date, (int64_t)time(NULL));
    buffer_format(&b, "HTTP/1.1 %d %s\r\nDate: %s\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n%s",
                  status, reason_phrase(status), date, strlen(body), body);
    net_error e;
    conn_write(c, b.data, b.length, io, &e);
    buffer_free(&b);
}

static void drained(aqua_runtime *rt, void *data);
static void arrived(aqua_runtime *rt, void *data);

/* Writes what the runtime's thread produces for `x` until it completes, upgrades or fails. */
static bool serve_exchange(connection *c, exchange *x, engine *eng) {
    server *sv = c->server;
    bool upgraded_flushed = false;
    net_buffer chunk = {0};
    for (;;) {
        net_lock(&x->lock);
        while (!x->out.length && !x->complete && !x->failed && !(x->upgrade && !upgraded_flushed)) {
            /* A closing server still lets running requests finish (within their deadline). */
            if (atomic_load(engine_stopping(eng)) || net_clock_ms() >= x->deadline) { x->failed = true; break; }
            struct timespec at;
            clock_gettime(CLOCK_REALTIME, &at);
            at.tv_nsec += 50 * 1000000L;
            if (at.tv_nsec >= 1000000000L) { at.tv_sec++; at.tv_nsec -= 1000000000L; }
            pthread_cond_timedwait(&x->wake, &x->lock, &at);
        }
        if (x->failed) { net_unlock(&x->lock); break; }
        net_buffer pending = x->out;
        x->out = chunk;
        chunk = pending;
        bool complete = x->complete, upgrade = x->upgrade && !upgraded_flushed;
        net_unlock(&x->lock);
        if (chunk.length) {
            net_io io = io_make(0, NULL, NET_CLOSED, eng);
            io.deadline = x->deadline;
            net_error e;
            bool ok = conn_write(c->conn, chunk.data, chunk.length, &io, &e);
            chunk.length = 0;
            if (!ok) { net_lock(&x->lock); x->failed = true; net_unlock(&x->lock); break; }
        }
        net_lock(&x->lock);
        bool notify = x->drain_wanted && x->out.length == 0;
        if (notify) x->drain_wanted = false;
        if (upgrade && x->out.length == 0) {
            /* The 101 is out: the WebSocket reads the connection from here on. */
            upgraded_flushed = true;
            c->upgraded = true;
            buffer_append(&x->leftover, c->in.data, c->in.length);
            c->in.length = 0;
            notify = true;
        }
        net_unlock(&x->lock);
        if (notify) { atomic_fetch_add(&x->refs, 1); aqua_post(sv->state->rt, drained, x); }
        if (complete && !x->out.length && !upgrade) break;
    }
    buffer_free(&chunk);
    net_lock(&x->lock);
    bool failed = x->failed;
    bool notify = x->drain_wanted;
    x->drain_wanted = false;
    net_unlock(&x->lock);
    if (notify) { atomic_fetch_add(&x->refs, 1); aqua_post(sv->state->rt, drained, x); }
    return !failed && !upgraded_flushed && x->keep_alive;
}

typedef struct connection_job { net_job job; connection *connection; } connection_job;

static void connection_serve(connection *c, engine *eng);
static void connection_end(connection *c);
static void connection_run(net_job *job) {
    connection *c = ((connection_job *)job)->connection;
    connection_serve(c, job->engine);
    connection_end(c);
}
static void connection_serve(connection *c, engine *eng) {
    server *sv = c->server;
    net_error e;
    if (sv->tls) {
        net_io io = io_make(HEADERS_TIMEOUT_MS, &sv->stopping, NET_CLOSED, eng);
        if (!conn_tls_server(c->conn, sv->tls, &io, &e)) return;
    }
    for (bool first = true;; first = false) {
        net_io io = io_make(first ? HEADERS_TIMEOUT_MS : KEEP_ALIVE_MS, &sv->stopping, NET_CLOSED, eng);
        http_head head;
        int got = http_read_head(c->conn, &c->in, true, &head, &io, &e);
        if (got <= 0) {
            if (got < 0 && e.kind == NET_LIMIT) write_simple(c->conn, 431, "", &io);
            else if (got < 0 && e.kind == NET_NETWORK && !first) {}
            else if (got < 0 && e.kind == NET_NETWORK) write_simple(c->conn, 400, "", &io);
            return;
        }
        const char *host = headers_get(&head.headers, "Host");
        if ((head.minor >= 1 && !host) || (head.target[0] != '/' && strcmp(head.target, "*") && strncmp(head.target, "http", 4))) {
            write_simple(c->conn, 400, "", &io);
            http_head_free(&head);
            return;
        }
        http_body body;
        const char *encoding = headers_get(&head.headers, "Transfer-Encoding");
        if (!http_body_start(&body, &head, true, head.method) || (encoding && !net_itoken(encoding, "chunked"))) {
            write_simple(c->conn, encoding ? 501 : 400, "", &io);
            http_head_free(&head);
            return;
        }
        const char *expect = headers_get(&head.headers, "Expect");
        if (expect && net_iequal(expect, "100-continue") && !body.finished) {
            net_error ignored;
            conn_write(c->conn, "HTTP/1.1 100 Continue\r\n\r\n", 25, &io, &ignored);
        }
        exchange *x = calloc(1, sizeof *x);
        atomic_init(&x->refs, 1);
        net_mutex_init(&x->lock);
        pthread_cond_init(&x->wake, NULL);
        bool too_large = body.mode == BODY_LENGTH && body.remaining > (uint64_t)sv->options.max_body;
        while (!too_large) {
            long n = http_body_read(&body, c->conn, &c->in, &x->body, &io, &e);
            if (n < 0) { http_head_free(&head); exchange_unref(x); return; }
            if (x->body.length > (size_t)sv->options.max_body) too_large = true;
            if (n == 0) break;
        }
        if (too_large) { write_simple(c->conn, 413, "", &io); http_head_free(&head); exchange_unref(x); return; }
        /* The request as handlers see it. */
        x->method = head.method; head.method = NULL;
        const char *target = head.target;
        if (!strncmp(target, "http://", 7) || !strncmp(target, "https://", 8)) { const char *p = strchr(target + 8, '/'); target = p ? p : "/"; }
        const char *question = strchr(target, '?');
        size_t path_length = question ? (size_t)(question - target) : strlen(target);
        x->target = net_strndup(target, path_length);
        x->path = url_unescape(target, path_length, false);
        x->query = net_strdup(question ? question : "");
        x->minor = head.minor;
        x->secure = sv->secure;
        x->head = !strcmp(x->method, "HEAD");
        const char *connection_header = headers_get(&head.headers, "Connection");
        x->keep_alive = head.minor >= 1 ? !(connection_header && net_itoken(connection_header, "close")) : false;
        x->headers = head.headers;
        memset(&head.headers, 0, sizeof head.headers);
        net_buffer url = {0};
        buffer_format(&url, "%s://%s%s%s", sv->secure ? "https" : "http", host ? host : "", x->target, x->query);
        x->url = buffer_take(&url, NULL);
        buffer_free(&url);
        net_url self;
        if (url_parse(x->url, &self)) { x->origin_self = url_origin(&self); url_free(&self); }
        else x->origin_self = net_strdup("");
        char remote[64];
        snprintf(remote, sizeof remote, "%s", c->conn->remote);
        char *colon = strrchr(remote, ':');
        if (colon) *colon = 0;
        if (remote[0] == '[') { memmove(remote, remote + 1, strlen(remote)); char *close = strchr(remote, ']'); if (close) *close = 0; }
        x->remote = net_strdup(remote);
        x->status = 200;
        http_head_free(&head);
        atomic_fetch_add(&c->refs, 1);
        x->connection = c;
        x->server = sv;
        x->deadline = net_clock_ms() + sv->options.timeout_ms;
        /* Queue it for the runtime's thread. */
        net_lock(&sv->lock);
        bool full = sv->queued >= QUEUE_LIMIT || sv->closed;
        if (!full) {
            atomic_fetch_add(&x->refs, 1); /* the queue's */
            x->next = NULL;
            if (sv->queue_tail) sv->queue_tail->next = x; else sv->queue_head = x;
            sv->queue_tail = x;
            sv->queued++;
        }
        net_unlock(&sv->lock);
        if (full) { write_simple(c->conn, 503, "", &io); exchange_unref(x); return; }
        atomic_fetch_add(&sv->refs, 1);
        aqua_post(sv->state->rt, arrived, sv);
        bool keep = serve_exchange(c, x, eng);
        exchange_unref(x);
        if (!keep || atomic_load(&sv->stopping)) return;
    }
}

/* The socket closes with its job, whoever still refers to the connection; an upgraded one belongs to its
   WebSocket from then on. */
static void connection_end(connection *c) {
    if (c->upgraded || !c->conn) return;
    conn_free(c->conn);
    c->conn = NULL;
}
static void connection_done(network_state *s, net_job *job) {
    (void)s;
    connection_unref(((connection_job *)job)->connection);
    free(job);
}
static void connection_discard(net_job *job) { connection_unref(((connection_job *)job)->connection); free(job); }

typedef struct listener_job { net_job job; server *server; net_fd fd; } listener_job;

static void listener_run(net_job *job) {
    listener_job *l = (listener_job *)job;
    server *sv = l->server;
    net_io io = io_make(0, &sv->stopping, NET_CLOSED, job->engine);
    net_error e;
    for (;;) {
        if (!net_wait(l->fd, WAIT_READ, &io, &e)) return;
        net_fd fd = accept(l->fd, NULL, NULL);
        if (fd == NET_NO_SOCKET) continue;
        if (atomic_load(&sv->connections) >= CONNECTION_LIMIT) { net_fd_close(fd); continue; }
        net_fd_nonblocking(fd);
        int on = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (const char *)&on, sizeof on);
        connection *c = calloc(1, sizeof *c);
        atomic_init(&c->refs, 1);
        atomic_fetch_add(&sv->refs, 1);
        atomic_fetch_add(&sv->connections, 1);
        c->server = sv;
        c->conn = conn_new(fd);
        connection_job *cj = calloc(1, sizeof *cj);
        cj->connection = c;
        cj->job.run = connection_run;
        cj->job.done = connection_done;
        cj->job.discard = connection_discard;
        job_start(job->engine, &cj->job);
    }
}
/* A listener's socket closes as soon as its job stops (a closed server's port is free at once). */
static void listener_done(network_state *s, net_job *job) {
    (void)s;
    listener_job *l = (listener_job *)job;
    server *sv = l->server;
    for (int i = 0; i < sv->listener_count; i++) if (sv->listeners[i] == l->fd) { net_fd_close(l->fd); sv->listeners[i] = NET_NO_SOCKET; }
    server_unref(sv);
    free(job);
}
static void listener_discard(net_job *job) { server_unref(((listener_job *)job)->server); free(job); }

/* ---- Server lifetime ---- */

static void server_unref(server *sv) {
    if (atomic_fetch_sub(&sv->refs, 1) != 1) return;
    for (int i = 0; i < sv->listener_count; i++) if (sv->listeners[i] != NET_NO_SOCKET) net_fd_close(sv->listeners[i]);
    for (uint32_t i = 0; i < sv->route_count; i++) { free(sv->routes[i].method); free(sv->routes[i].pattern); }
    free(sv->routes);
    free(sv->middleware);
    for (uint32_t i = 0; i < sv->folder_count; i++) { free(sv->folders[i].prefix); free(sv->folders[i].root); }
    free(sv->folders);
    for (uint32_t i = 0; i < sv->origin_count; i++) free(sv->origins[i]);
    free(sv->origins);
    for (uint32_t i = 0; i < sv->session_count; i++) headers_free(&sv->sessions[i].values);
    free(sv->sessions);
    while (sv->queue_head) { exchange *x = sv->queue_head; sv->queue_head = x->next; exchange_unref(x); }
    if (sv->tls) tls_server_free(sv->tls);
    options_free(&sv->options);
    net_mutex_destroy(&sv->lock);
    free(sv->address);
    free(sv);
}

static void release_handlers(server *sv) {
    aqua_runtime *rt = sv->state->rt;
    for (uint32_t i = 0; i < sv->route_count; i++) { aqua_release(rt, sv->routes[i].handler); sv->routes[i].handler = 0; }
    for (uint32_t i = 0; i < sv->middleware_count; i++) { aqua_release(rt, sv->middleware[i]); sv->middleware[i] = 0; }
}

/* Stops accepting and serving; waiting Poll calls return false and Serve returns. */
static void server_shutdown(network_state *s, resource *r) {
    server *sv = (server *)r;
    if (sv->shut) return;
    sv->shut = true;
    sv->closed = true;
    atomic_store(&sv->stopping, true);
    for (int i = 0; i < sv->listener_count; i++) {
#if defined(AQUA_OS_WINDOWS)
        shutdown(sv->listeners[i], SD_BOTH);
#else
        shutdown(sv->listeners[i], SHUT_RDWR);
#endif
    }
    net_lock(&sv->lock);
    exchange *queue = sv->queue_head;
    sv->queue_head = sv->queue_tail = NULL;
    sv->queued = 0;
    net_unlock(&sv->lock);
    while (queue) { exchange *x = queue->next; net_lock(&queue->lock); queue->failed = true; net_unlock(&queue->lock); exchange_unref(queue); queue = x; }
    if (s->closing) return;
    release_handlers(sv);
    while (sv->polls) {
        poll_wait *w = sv->polls;
        sv->polls = w->next;
        timer_cancel(s, w->timer);
        aqua_resolve(w->pending, aqua_bool(false));
        free(w);
        server_unref(sv); /* the waiter's */
    }
    if (sv->serve) { aqua_pending *p = sv->serve; sv->serve = NULL; aqua_resolve(p, aqua_null()); }
}

void server_release(aqua_runtime *rt, void *payload) {
    (void)rt;
    server *sv = payload;
    if (sv->base.open) resource_close(sv->base.state, &sv->base);
    server_unref(sv);
}

/* Binds the listener origin: an IP, localhost (both loopbacks) or any other name/'*' (every interface). */
static bool bind_listeners(server *sv, const char *host, int port, net_error *e) {
    const char *addresses[2];
    int count = 0;
    char literal[64];
    snprintf(literal, sizeof literal, "%s", host);
    if (literal[0] == '[') { memmove(literal, literal + 1, strlen(literal)); char *close = strchr(literal, ']'); if (close) *close = 0; }
    struct in6_addr v6;
    struct in_addr v4;
    if (inet_pton(AF_INET, literal, &v4) == 1 || inet_pton(AF_INET6, literal, &v6) == 1) addresses[count++] = literal;
    else if (net_iequal(literal, "localhost")) { addresses[count++] = "127.0.0.1"; addresses[count++] = "::1"; }
    else { addresses[count++] = "0.0.0.0"; addresses[count++] = "::"; }
    for (int i = 0; i < count; i++) {
        struct addrinfo hints = {0}, *list;
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_flags = AI_NUMERICHOST | AI_PASSIVE;
        char service[8];
        snprintf(service, sizeof service, "%d", port);
        if (getaddrinfo(addresses[i], service, &hints, &list) != 0) continue;
        net_fd fd = net_socket(list->ai_family, SOCK_STREAM, 0);
        int on = 1;
#if defined(AQUA_OS_WINDOWS)
        setsockopt(fd, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&on, sizeof on);
#else
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, sizeof on);
#endif
        if (list->ai_family == AF_INET6) setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, (const char *)&on, sizeof on);
        bool ok = fd != NET_NO_SOCKET && bind(fd, list->ai_addr, (int)list->ai_addrlen) == 0 && listen(fd, 128) == 0;
        int error = net_last_error();
        freeaddrinfo(list);
        if (!ok) {
            if (fd != NET_NO_SOCKET) net_fd_close(fd);
            if (i == 0) { char context[96]; snprintf(context, sizeof context, "%s:%d", addresses[i], port); net_socket_error(e, error, context); return false; }
            continue; /* the second loopback or IPv6 is optional */
        }
        sv->listeners[sv->listener_count++] = fd;
        if (port == 0) {
            struct sockaddr_storage address;
            socklen_t length = sizeof address;
            getsockname(fd, (struct sockaddr *)&address, &length);
            port = ntohs(address.ss_family == AF_INET6 ? ((struct sockaddr_in6 *)&address)->sin6_port : ((struct sockaddr_in *)&address)->sin_port);
        }
    }
    net_buffer address = {0};
    buffer_format(&address, "%s://%s:%d", sv->secure ? "https" : "http", host, port);
    sv->address = buffer_take(&address, NULL);
    buffer_free(&address);
    return true;
}

void net_listen(aqua_call *call, const char *name) {
    network_state *s = aqua_state(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    net_error e;
    net_options o;
    if (!options_read(call, 1, NULL, &o, &e)) { options_free(&o); net_raise(call, name, &e); return; }
    net_url url;
    if (!url_parse(aqua_arg_str(call, 0).data, &url) || (strcmp(url.scheme, "http") && strcmp(url.scheme, "https"))
        || strcmp(url.path, "/") || url.query[0] || url.userinfo[0]) {
        url_free(&url);
        options_free(&o);
        net_raise_kind(call, name, NET_INVALID, "Expected HTTP(S) listener origin / 需要 HTTP(S) 監聽來源。");
        return;
    }
    server *sv = calloc(1, sizeof *sv);
    atomic_init(&sv->refs, 1);
    atomic_init(&sv->stopping, false);
    atomic_init(&sv->connections, 0);
    net_mutex_init(&sv->lock);
    sv->base.close = server_shutdown;
    sv->state = s;
    sv->options = o;
    sv->secure = !strcmp(url.scheme, "https");
    bool ok = true;
    if (sv->secure) {
        if (!o.certificate) ok = net_error_set(&e, NET_INVALID, false, "HTTPS requires PFX certificate / HTTPS 需要 PFX 憑證。");
        else {
            const char *file = aqua_asset_file(rt, o.certificate);
            sv->tls = tls_server_load(file ? file : o.certificate, o.certificate_password ? o.certificate_password : "", &e);
            ok = sv->tls != NULL;
        }
    }
    if (ok) ok = bind_listeners(sv, url.host, url.port, &e);
    url_free(&url);
    if (!ok) { server_unref(sv); net_raise(call, name, &e); return; }
    if (!resource_own(s, &sv->base, &e)) { server_unref(sv); net_raise(call, name, &e); return; }
    for (int i = 0; i < sv->listener_count; i++) {
        listener_job *l = calloc(1, sizeof *l);
        atomic_fetch_add(&sv->refs, 1);
        l->server = sv;
        l->fd = sv->listeners[i];
        l->job.run = listener_run;
        l->job.done = listener_done;
        l->job.discard = listener_discard;
        job_start(s->engine, &l->job);
    }
    net_object *object = object_create(CLASS_server, sv);
    object_field(rt, object, FIELD_OF(server, address), aqua_cstring(rt, sv->address));
    aqua_return(call, object_wrap(rt, object));
}

/* ---- Registration ---- */

#define SELF_SERVER(call) ((server *)self_payload(call))

static bool server_live(aqua_call *call, const char *name, server *sv) {
    if (!sv->closed) return true;
    net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED);
    return false;
}

void server_handle(aqua_call *call, const char *name) {
    server *sv = SELF_SERVER(call);
    if (!server_live(call, name, sv)) return;
    const char *pattern = aqua_arg_str(call, 1).data;
    if (pattern[0] != '/' || strchr(pattern, '?')) { net_raise_kind(call, name, NET_INVALID, "Invalid route / 路由須以 / 開頭。"); return; }
    route *routes = realloc(sv->routes, (sv->route_count + 1) * sizeof *routes);
    sv->routes = routes;
    route *r = &routes[sv->route_count++];
    r->method = net_strdup(aqua_arg_str(call, 0).data);
    for (char *p = r->method; *p; p++) if (*p >= 'a' && *p <= 'z') *p -= 32;
    r->pattern = net_strdup(pattern);
    r->handler = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 2));
    aqua_return(call, aqua_self_value(call));
}

void server_use(aqua_call *call, const char *name) {
    server *sv = SELF_SERVER(call);
    if (!server_live(call, name, sv)) return;
    sv->middleware = realloc(sv->middleware, (sv->middleware_count + 1) * sizeof *sv->middleware);
    sv->middleware[sv->middleware_count++] = aqua_retain(aqua_call_runtime(call), aqua_arg(call, 0));
    aqua_return(call, aqua_self_value(call));
}

void server_static(aqua_call *call, const char *name) {
    server *sv = SELF_SERVER(call);
    if (!server_live(call, name, sv)) return;
    const char *prefix = aqua_arg_str(call, 0).data;
    if (prefix[0] != '/' || strstr(prefix, "..")) { net_raise_kind(call, name, NET_INVALID, "Invalid public prefix / 無效的公開前綴。"); return; }
    sv->folders = realloc(sv->folders, (sv->folder_count + 1) * sizeof *sv->folders);
    public_folder *f = &sv->folders[sv->folder_count++];
    size_t n = strlen(prefix);
    while (n && prefix[n - 1] == '/') n--;
    f->prefix = malloc(n + 2);
    memcpy(f->prefix, prefix, n);
    f->prefix[n] = '/';
    f->prefix[n + 1] = 0;
    f->root = net_strdup(aqua_arg_str(call, 1).data);
    aqua_return(call, aqua_self_value(call));
}

void server_cors(aqua_call *call, const char *name) {
    server *sv = SELF_SERVER(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!server_live(call, name, sv)) return;
    aqua_value list = aqua_arg(call, 0);
    uint32_t n = aqua_array_length(rt, list);
    for (uint32_t i = 0; i < n; i++) {
        aqua_value item = aqua_array_get(rt, list, i);
        if (item.type != AQUA_STRING) { net_raise_kind(call, name, NET_INVALID, MSG_STRING); return; }
        aqua_str text = aqua_str_of(call, item);
        bool exact = !strcmp(text.data, "*");
        if (!exact) {
            net_url url;
            if (url_parse(text.data, &url)) {
                char *origin = url_origin(&url);
                exact = !strcmp(origin, text.data) && url.host[0];
                free(origin);
                url_free(&url);
            }
        }
        if (!exact) { net_raise_kind(call, name, NET_INVALID, "Expected exact origin / 請填入完整來源。"); return; }
        sv->origins = realloc(sv->origins, (sv->origin_count + 1) * sizeof *sv->origins);
        sv->origins[sv->origin_count++] = net_strdup(text.data);
    }
    aqua_return(call, aqua_self_value(call));
}

static bool origin_listed(server *sv, const char *origin) {
    for (uint32_t i = 0; i < sv->origin_count; i++) if (!strcmp(sv->origins[i], origin)) return true;
    return false;
}

/* ---- Request processing on the runtime's thread ---- */

static void process(server *sv, exchange *x);

static exchange *take_request(server *sv) {
    net_lock(&sv->lock);
    exchange *x = sv->queue_head;
    if (x) {
        sv->queue_head = x->next;
        if (!sv->queue_head) sv->queue_tail = NULL;
        sv->queued--;
    }
    net_unlock(&sv->lock);
    return x;
}

static void poll_timeout(network_state *s, void *data) {
    (void)s;
    poll_wait *w = data;
    server *sv = w->server;
    for (poll_wait **p = &sv->polls; *p; p = &(*p)->next) if (*p == w) { *p = w->next; break; }
    aqua_resolve(w->pending, aqua_bool(false));
    free(w);
    server_unref(sv);
}

static void dispatch(server *sv) {
    while (!sv->closed && (sv->polls || sv->serve)) {
        exchange *x = take_request(sv);
        if (!x) return;
        if (sv->polls) {
            poll_wait *w = sv->polls;
            sv->polls = w->next;
            timer_cancel(sv->state, w->timer);
            x->poll = w->pending;
            free(w);
            server_unref(sv);
        }
        process(sv, x);
    }
}

static void arrived(aqua_runtime *rt, void *data) {
    (void)rt;
    server *sv = data;
    if (!sv->state->closing) dispatch(sv);
    server_unref(sv);
}

static void dispatch_task(aqua_runtime *rt, void *data) { arrived(rt, data); }

static void finish(exchange *x) {
    server *sv = x->server;
    if (x->finished) return;
    x->finished = true;
    exchange_finish_output(x);
    x->active = false;
    aqua_runtime *rt = sv->state->rt;
    if (x->socket) {
        if (x->socket->base.open) resource_close(sv->state, &x->socket->base);
        websocket_unref(x->socket);
        x->socket = NULL;
    }
    if (x->request_object) { aqua_release(rt, x->request_object); x->request_object = 0; }
    if (x->writer_object) { aqua_release(rt, x->writer_object); x->writer_object = 0; }
    sv->active--;
    aqua_pending *poll = x->poll;
    x->poll = NULL;
    exchange_unref(x); /* the processing reference; the objects may keep it a while, inactive */
    if (poll) aqua_resolve(poll, aqua_bool(true));
    if (sv->closed && sv->active == 0 && !sv->shut) resource_close(sv->state, &sv->base);
    server_unref(sv);
}

/* A failed handler answers 500 without details (unless the response already started); the server goes on. */
static void handler_failed(exchange *x, aqua_value error) {
    (void)error;
    if (!x->started && !x->upgraded && !x->failed) {
        x->status = 500;
        headers_set(&x->response, "Content-Type", "text/plain; charset=utf-8");
        const char *body = "Internal Server Error / 伺服器內部錯誤";
        exchange_write(x, body, strlen(body));
    }
}

/* Route patterns: literal segments, {name} parameters and a final '*' taking the rest. */
static bool route_match(const char *pattern, const char *path, net_headers *params) {
    headers_free(params);
    const char *p = pattern, *q = path;
    for (;;) {
        const char *p_end = strchr(p, '/'), *q_end = strchr(q, '/');
        size_t pn = p_end ? (size_t)(p_end - p) : strlen(p);
        size_t qn = q_end ? (size_t)(q_end - q) : strlen(q);
        if (pn == 1 && p[0] == '*') { headers_add(params, "*", q); return true; }
        if (!q) return false;
        if (pn >= 2 && p[0] == '{' && p[pn - 1] == '}') {
            char *key = net_strndup(p + 1, pn - 2), *value = url_unescape(q, qn, false);
            headers_add(params, key, value);
            free(key);
            free(value);
        } else if (pn != qn || strncmp(p, q, pn)) return false;
        if (!p_end && !q_end) return true;
        if (!p_end || !q_end) {
            /* A pattern ending in a star segment also matches the path without it. */
            if (p_end && !strcmp(p_end, "/*")) { headers_add(params, "*", ""); return true; }
            return false;
        }
        p = p_end + 1;
        q = q_end + 1;
    }
}

static const char *mime_type(const char *path) {
    const char *dot = strrchr(path, '.');
    const char *slash = strrchr(path, '/');
    if (!dot || (slash && dot < slash)) return "application/octet-stream";
    static const char *const types[][2] = {
        {".html", "text/html; charset=utf-8"}, {".css", "text/css; charset=utf-8"}, {".js", "text/javascript; charset=utf-8"},
        {".mjs", "text/javascript; charset=utf-8"}, {".json", "application/json"}, {".wasm", "application/wasm"}, {".png", "image/png"},
        {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".svg", "image/svg+xml"}, {".ico", "image/x-icon"}, {".woff2", "font/woff2"},
        {".txt", "text/plain; charset=utf-8"}};
    for (size_t i = 0; i < sizeof types / sizeof types[0]; i++) if (net_iequal(dot, types[i][0])) return types[i][1];
    return "application/octet-stream";
}

static bool is_link(const char *path) {
#if defined(AQUA_OS_WINDOWS)
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)n * sizeof *wide);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, n);
    DWORD attributes = GetFileAttributesW(wide);
    free(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT);
#else
    struct stat info;
    return lstat(path, &info) == 0 && S_ISLNK(info.st_mode);
#endif
}

static int file_kind(const char *path) { /* 0 missing, 1 file, 2 directory */
#if defined(AQUA_OS_WINDOWS)
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)n * sizeof *wide);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, n);
    DWORD attributes = GetFileAttributesW(wide);
    free(wide);
    if (attributes == INVALID_FILE_ATTRIBUTES) return 0;
    return attributes & FILE_ATTRIBUTE_DIRECTORY ? 2 : 1;
#else
    struct stat info;
    if (stat(path, &info) != 0) return 0;
    return S_ISDIR(info.st_mode) ? 2 : 1;
#endif
}

static FILE *open_utf8(const char *path) {
#if defined(AQUA_OS_WINDOWS)
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    wchar_t *wide = malloc((size_t)n * sizeof *wide);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, n);
    FILE *f = _wfopen(wide, L"rb");
    free(wide);
    return f;
#else
    return fopen(path, "rb");
#endif
}

/* ReadPublicFile: a file under `root` for the request path remainder, never outside it, never through a link. */
static int public_file(server *sv, const char *root, const char *remainder, net_buffer *bytes, const char **type) {
    char *path = url_unescape(remainder, strlen(remainder), false);
    bool unsafe = strchr(path, '\\') || strchr(path, ':');
    for (const char *p = remainder; *p && !unsafe; p++) if (p[0] == '%' && p[1] == '0' && p[2] == '0') unsafe = true;
    for (const char *segment = path; !unsafe && segment;) {
        const char *end = strchr(segment, '/');
        size_t n = end ? (size_t)(end - segment) : strlen(segment);
        if ((n == 1 && segment[0] == '.') || (n == 2 && segment[0] == '.' && segment[1] == '.')) unsafe = true;
        segment = end ? end + 1 : NULL;
    }
    if (unsafe) { free(path); return 0; }
    net_buffer full = {0};
    buffer_text(&full, root);
    size_t root_length = full.length;
    while (root_length && (full.data[root_length - 1] == '/' || full.data[root_length - 1] == '\\')) root_length--;
    full.length = root_length;
    buffer_text(&full, "/");
    buffer_text(&full, path);
    char *target = buffer_cstring(&full);
    int kind = file_kind(target);
    if (kind == 2) {
        if (full.length && full.data[full.length - 1] != '/') buffer_text(&full, "/");
        buffer_text(&full, "index.html");
        target = buffer_cstring(&full);
        kind = file_kind(target);
    }
    int result = 0;
    if (kind == 1) {
        /* Links anywhere from the file up to the file system root are refused. */
        bool linked = false;
        char *walk = net_strdup(target);
        for (;;) {
            if (is_link(walk)) { linked = true; break; }
            char *slash = strrchr(walk, '/');
#if defined(AQUA_OS_WINDOWS)
            char *backslash = strrchr(walk, '\\');
            if (backslash > slash) slash = backslash;
#endif
            if (!slash || slash == walk) break;
            *slash = 0;
            if (!*walk || (walk[1] == ':' && !walk[2])) break;
        }
        free(walk);
        FILE *f = linked ? NULL : open_utf8(target);
        if (f) {
            uint8_t chunk[65536];
            size_t n;
            result = 1;
            while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) {
                if (bytes->length + n > NET_MAX_BODY) { result = -1; break; }
                buffer_append(bytes, chunk, n);
            }
            fclose(f);
            *type = mime_type(target);
        }
    } else if (kind == 0 && root[0] != '/' && !(root[0] && root[1] == ':')) {
        /* Relative roots may name packaged assets. */
        net_buffer asset = {0};
        buffer_text(&asset, root);
        while (asset.length && asset.data[asset.length - 1] == '/') asset.length--;
        buffer_text(&asset, "/");
        buffer_text(&asset, path);
        if (!path[0] || path[strlen(path) - 1] == '/') buffer_text(&asset, "index.html");
        size_t length;
        const uint8_t *data = aqua_asset(sv->state->rt, buffer_cstring(&asset), &length);
        if (data) {
            if (length > NET_MAX_BODY) result = -1;
            else { buffer_append(bytes, data, length); result = 1; *type = mime_type(buffer_cstring(&asset)); }
        }
        buffer_free(&asset);
    }
    buffer_free(&full);
    free(path);
    return result;
}

static void respond_text(exchange *x, int status, const char *text) {
    x->status = status;
    headers_set(&x->response, "Content-Type", "text/plain; charset=utf-8");
    exchange_write(x, text, strlen(text));
}

/* After middleware and routes: static files, then 404 or 405. */
static void respond_rest(exchange *x) {
    server *sv = x->server;
    bool get = !strcmp(x->method, "GET") || x->head;
    for (uint32_t i = 0; get && i < sv->folder_count; i++) {
        public_folder *f = &sv->folders[i];
        if (strncmp(x->path, f->prefix, strlen(f->prefix)) != 0) continue;
        net_buffer bytes = {0};
        const char *type = NULL;
        int found = public_file(sv, f->root, x->path + strlen(f->prefix), &bytes, &type);
        if (found < 0) { buffer_free(&bytes); handler_failed(x, aqua_cstring(sv->state->rt, "LimitExceeded / 靜態檔案過大。")); return; }
        if (!found) { buffer_free(&bytes); continue; }
        uint8_t digest[32];
        net_sha256(bytes.data, bytes.length, digest);
        char etag[68];
        etag[0] = '"';
        for (int k = 0; k < 32; k++) snprintf(etag + 1 + k * 2, 3, "%02x", digest[k]);
        etag[65] = '"';
        etag[66] = 0;
        headers_set(&x->response, "ETag", etag);
        headers_set(&x->response, "Content-Type", type);
        const char *match = headers_get(&x->headers, "If-None-Match");
        if (match && !strcmp(match, etag)) x->status = 304;
        else {
            /* Whole files are sent with their length. */
            x->started = true;
            net_buffer h = {0};
            char date[32];
            http_date(date, (int64_t)time(NULL));
            buffer_format(&h, "HTTP/1.1 %d %s\r\nDate: %s\r\n", x->status, reason_phrase(x->status), date);
            for (uint32_t k = 0; k < x->response.count; k++) buffer_format(&h, "%s: %s\r\n", x->response.items[k].name, x->response.items[k].value);
            buffer_format(&h, "Content-Length: %zu\r\n%s\r\n", bytes.length, x->keep_alive ? "" : "Connection: close\r\n");
            exchange_send(x, h.data, h.length);
            if (!x->head) exchange_send(x, bytes.data, bytes.length);
            buffer_free(&h);
        }
        buffer_free(&bytes);
        return;
    }
    net_buffer allow = {0};
    net_headers params = {0};
    for (uint32_t i = 0; i < sv->route_count; i++) {
        if (!route_match(sv->routes[i].pattern, x->path, &params)) continue;
        bool seen = false;
        for (uint32_t k = 0; k < i; k++) if (!strcmp(sv->routes[k].method, sv->routes[i].method) && route_match(sv->routes[k].pattern, x->path, &params)) seen = true;
        if (seen) continue;
        if (allow.length) buffer_text(&allow, ", ");
        buffer_text(&allow, sv->routes[i].method);
    }
    headers_free(&params);
    if (allow.length) {
        x->status = 405;
        headers_set(&x->response, "Allow", (char *)allow.data);
        respond_text(x, 405, "Method Not Allowed / 方法不允許");
    } else respond_text(x, 404, "Not Found / 找不到資源");
    buffer_free(&allow);
}

static void handler_done(aqua_runtime *rt, bool ok, aqua_value result, void *data);
static void run_middleware(exchange *x);

static void invoke(exchange *x, aqua_ref function) {
    aqua_runtime *rt = x->server->state->rt;
    aqua_value arguments[2] = {aqua_ref_value(rt, x->request_object), aqua_ref_value(rt, x->writer_object)};
    aqua_invoke_async(rt, aqua_ref_value(rt, function), arguments, 2, handler_done, x);
}

static void run_route(exchange *x) {
    server *sv = x->server;
    for (uint32_t i = 0; i < sv->route_count; i++) {
        route *r = &sv->routes[i];
        if (strcmp(r->method, x->method) && strcmp(r->method, "*") && !(x->head && !strcmp(r->method, "GET"))) continue;
        if (!route_match(r->pattern, x->path, &x->params)) continue;
        x->step = sv->middleware_count + 1;
        invoke(x, r->handler);
        return;
    }
    headers_free(&x->params);
    respond_rest(x);
    finish(x);
}

static void run_middleware(exchange *x) {
    server *sv = x->server;
    if (x->step < sv->middleware_count && sv->middleware[x->step]) { invoke(x, sv->middleware[x->step]); return; }
    run_route(x);
}

static void handler_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    exchange *x = data;
    server *sv = x->server;
    if (!ok) { handler_failed(x, result); finish(x); return; }
    if (x->step < sv->middleware_count) {
        if (result.type == AQUA_BOOL && result.number == 0) { finish(x); return; }
        x->step++;
        run_middleware(x);
        return;
    }
    finish(x);
}

static aqua_value request_object(server *sv, exchange *x) {
    aqua_runtime *rt = sv->state->rt;
    atomic_fetch_add(&x->refs, 1);
    net_object *o = object_create(CLASS_request, x);
    object_field(rt, o, FIELD_OF(request, method), aqua_cstring(rt, x->method));
    object_field(rt, o, FIELD_OF(request, path), aqua_cstring(rt, x->path));
    object_field(rt, o, FIELD_OF(request, url), aqua_cstring(rt, x->url));
    object_field(rt, o, FIELD_OF(request, remote_address), aqua_cstring(rt, x->remote));
    object_field(rt, o, FIELD_OF(request, body), aqua_text(rt, x->body.length ? (const char *)x->body.data : "", x->body.length));
    object_field(rt, o, FIELD_OF(request, headers), headers_hash(rt, &x->headers, ","));
    object_field(rt, o, FIELD_OF(request, is_secure), aqua_bool(x->secure));
    return object_wrap(rt, o);
}

static void process(server *sv, exchange *x) {
    aqua_runtime *rt = sv->state->rt;
    atomic_fetch_add(&sv->refs, 1);
    sv->active++;
    x->active = true;
    x->request_object = aqua_retain(rt, request_object(sv, x));
    atomic_fetch_add(&x->refs, 1);
    x->writer_object = aqua_retain(rt, object_wrap(rt, object_create(CLASS_writer, x)));
    headers_set(&x->response, "X-Content-Type-Options", "nosniff");
    const char *origin = headers_get(&x->headers, "Origin");
    bool any = origin_listed(sv, "*");
    bool same = origin && !strcmp(origin, x->origin_self);
    if (origin && *origin && !same && !any && !origin_listed(sv, origin)) {
        respond_text(x, 403, "Origin denied / 來源遭拒。");
        finish(x);
        return;
    }
    if (origin && *origin && !same) {
        headers_set(&x->response, "Access-Control-Allow-Origin", any ? "*" : origin);
        headers_set(&x->response, "Vary", "Origin");
        if (!any) headers_set(&x->response, "Access-Control-Allow-Credentials", "true");
    }
    if (!strcmp(x->method, "OPTIONS") && origin && *origin) {
        const char *requested = headers_get(&x->headers, "Access-Control-Request-Headers");
        headers_set(&x->response, "Access-Control-Allow-Methods", "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS");
        headers_set(&x->response, "Access-Control-Allow-Headers", requested ? requested : "");
        x->status = 204;
        finish(x);
        return;
    }
    x->step = 0;
    run_middleware(x);
}

/* ---- Poll, Serve, Close ---- */

void server_poll(aqua_call *call, const char *name) {
    server *sv = SELF_SERVER(call);
    int32_t timeout = aqua_has_arg(call, 0) ? aqua_arg_int(call, 0) : 0;
    if (timeout < 0 || timeout > 300000) { net_raise_kind(call, name, NET_INVALID, MSG_WAIT); return; }
    if (!server_live(call, name, sv)) return;
    exchange *x = sv->polls ? NULL : take_request(sv);
    if (x) {
        atomic_fetch_add(&x->refs, 1);
        process(sv, x);
        bool done = x->finished;
        if (!done) x->poll = aqua_defer(call);
        else aqua_return_bool(call, true);
        exchange_unref(x);
        return;
    }
    if (timeout == 0) { aqua_return_bool(call, false); return; }
    poll_wait *w = calloc(1, sizeof *w);
    w->pending = aqua_defer(call);
    w->server = sv;
    atomic_fetch_add(&sv->refs, 1);
    poll_wait **tail = &sv->polls;
    while (*tail) tail = &(*tail)->next;
    *tail = w;
    w->timer = timer_start(aqua_state(call), timeout, poll_timeout, w);
}

void server_serve(aqua_call *call, const char *name) {
    (void)name;
    server *sv = SELF_SERVER(call);
    if (sv->closed || sv->serve) return;
    sv->serve = aqua_defer(call);
    atomic_fetch_add(&sv->refs, 1);
    aqua_post(aqua_call_runtime(call), dispatch_task, sv);
}

void server_close(aqua_call *call, const char *name) {
    (void)name;
    server *sv = SELF_SERVER(call);
    sv->closed = true;
    if (sv->active == 0) resource_close(aqua_state(call), &sv->base);
}

/* ---- Request ---- */

#define SELF_EXCHANGE(call) ((exchange *)self_payload(call))

static exchange *live_exchange(aqua_call *call, const char *name) {
    exchange *x = SELF_EXCHANGE(call);
    if (x->active) return x;
    net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED);
    return NULL;
}

void exchange_release(aqua_runtime *rt, void *payload) { (void)rt; exchange_unref(payload); }

void request_header(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    const char *wanted = aqua_arg_str(call, 0).data;
    net_buffer joined = {0};
    bool found = false;
    for (uint32_t i = 0; i < x->headers.count; i++) {
        if (!net_iequal(x->headers.items[i].name, wanted)) continue;
        if (found) buffer_text(&joined, ",");
        buffer_text(&joined, x->headers.items[i].value);
        found = true;
    }
    aqua_return_text(call, found ? (const char *)joined.data : "", joined.length);
    buffer_free(&joined);
}

void request_query(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    net_headers pairs = {0};
    const char *q = x->query[0] == '?' ? x->query + 1 : x->query;
    /* Repeated names join with ',' like Kestrel's query collection. */
    const char *p = q, *end = q + strlen(q);
    const char *wanted = aqua_arg_str(call, 0).data;
    net_buffer joined = {0};
    bool found = false;
    while (p < end) {
        const char *amp = memchr(p, '&', (size_t)(end - p));
        const char *stop = amp ? amp : end;
        const char *eq = memchr(p, '=', (size_t)(stop - p));
        char *key = url_unescape(p, (size_t)((eq ? eq : stop) - p), true);
        if (stop > p && !strcmp(key, wanted)) {
            char *value = eq ? url_unescape(eq + 1, (size_t)(stop - eq - 1), true) : net_strdup("");
            if (found) buffer_text(&joined, ",");
            buffer_text(&joined, value);
            found = true;
            free(value);
        }
        free(key);
        p = stop + 1;
    }
    headers_free(&pairs);
    aqua_return_text(call, found ? (const char *)joined.data : "", joined.length);
    buffer_free(&joined);
}

void request_cookie(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    char *value = cookie_value(x, aqua_arg_str(call, 0).data);
    aqua_return_cstring(call, value ? value : "");
    free(value);
}

void request_param(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    const char *value = pair_value(&x->params, aqua_arg_str(call, 0).data);
    aqua_return_cstring(call, value ? value : "");
}

void request_json(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    aqua_value value;
    net_error e;
    if (!json_decode(aqua_call_runtime(call), x->body.data, x->body.length, &value, &e)) net_raise(call, name, &e);
    else aqua_return(call, value);
}

void request_form(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    const char *type = headers_get(&x->headers, "Content-Type");
    if (!type || !net_iprefix(type, "application/x-www-form-urlencoded")) { net_raise_kind(call, name, NET_INVALID, "Expected URL-encoded form / 需要網址編碼表單。"); return; }
    net_headers pairs = {0};
    parse_pairs(x->body.length ? (const char *)x->body.data : "", x->body.length, &pairs);
    aqua_return(call, headers_hash(aqua_call_runtime(call), &pairs, ","));
    headers_free(&pairs);
}

void request_bytes(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (x) aqua_return(call, bytes_value(aqua_call_runtime(call), x->body.data, x->body.length));
}

/* ---- Writer ---- */

static bool set_header(aqua_call *call, const char *name, exchange *x, const char *header, const char *value, bool append) {
    if (!header_valid(header, value)) { net_raise_kind(call, name, NET_INVALID, MSG_HEADER); return false; }
    if (x->started || x->upgraded) { net_raise_kind(call, name, NET_NETWORK, "Headers are read-only, response has already started. / 回應已開始，無法設定標頭。"); return false; }
    if (append) headers_add(&x->response, header, value);
    else headers_set(&x->response, header, value);
    return true;
}

/* The old Type(): a content type while the response has not started. */
static void content_type(exchange *x, const char *type) { if (!x->started) headers_set(&x->response, "Content-Type", type); }

static void drained(aqua_runtime *rt, void *data) {
    (void)rt;
    exchange *x = data;
    network_state *s = x->server->state;
    if (x->drain && !s->closing) {
        aqua_pending *p = x->drain;
        x->drain = NULL;
        net_lock(&x->lock);
        bool failed = x->failed;
        net_unlock(&x->lock);
        net_error e;
        if (failed) {
            net_error_set(&e, NET_CLOSED, false, MSG_CLOSED);
            net_reject(p, x->drain_name, &e);
        } else if (x->upgraded && !x->socket) {
            /* The 101 is out: the socket takes over the connection and any bytes the client already sent. */
            atomic_fetch_add(&x->connection->refs, 1);
            websocket *ws = websocket_server_new(s, x->connection->conn, &x->leftover, x->connection, connection_release, &x->server->options);
            if (!resource_own(s, &ws->base, &e)) { websocket_unref(ws); net_reject(p, x->drain_name, &e); }
            else { x->socket = ws; aqua_resolve(p, websocket_object(s->rt, ws)); }
        } else aqua_resolve(p, x->drain_result);
    }
    exchange_unref(x);
}

/* Sends a body piece; waits (deferred) while more than 1 MiB is still unsent to the client. */
static void write_body(aqua_call *call, const char *name, exchange *x, const void *data, size_t length) {
    if (length > NET_MAX_BODY) { net_raise_kind(call, name, NET_INVALID, "Body too large / 本文過大。"); return; }
    if (!exchange_write(x, data, length)) { net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED); return; }
    net_lock(&x->lock);
    bool wait = x->out.length > DRAIN_LIMIT && !x->failed && !x->drain;
    if (wait) x->drain_wanted = true;
    net_unlock(&x->lock);
    if (!wait) { aqua_return(call, aqua_self_value(call)); return; }
    x->drain_result = aqua_self_value(call);
    x->drain_name = name;
    x->drain = aqua_defer(call);
}

void writer_status(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    int32_t status = aqua_arg_int(call, 0);
    if (status < 200 || status > 599) { net_raise_kind(call, name, NET_INVALID, "Invalid status / 狀態碼須介於 200..599。"); return; }
    if (x->started) { net_raise_kind(call, name, NET_NETWORK, "StatusCode cannot be set because the response has already started. / 回應已開始，無法設定狀態碼。"); return; }
    x->status = status;
    aqua_return(call, aqua_self_value(call));
}

void writer_header(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (x && set_header(call, name, x, aqua_arg_str(call, 0).data, aqua_arg_str(call, 1).data, false)) aqua_return(call, aqua_self_value(call));
}


/* Set-Cookie with Path=/, SameSite=Lax, HttpOnly and (over HTTPS) Secure unless the options say otherwise. */
typedef struct cookie_options { char *path, *same_site; bool secure, http_only, has_max_age; int32_t max_age; } cookie_options;

static bool cookie_options_read(aqua_runtime *rt, aqua_value hash, const exchange *x, cookie_options *o, net_error *e) {
    memset(o, 0, sizeof *o);
    o->secure = x->secure;
    o->http_only = true;
    if (hash.type != AQUA_HASH) return true;
    aqua_value v;
#define PRESENT(names) (aqua_hash_field(rt, hash, names, &v) && !aqua_is_nullish(v))
    if (PRESENT("path|路徑") && !value_text(rt, v, &o->path, e)) return false;
    if (PRESENT("sameSite|同站") && !value_text(rt, v, &o->same_site, e)) return false;
    if (PRESENT("secure|安全") && !value_boolean(v, &o->secure, e)) return false;
    if (PRESENT("httpOnly|僅HTTP") && !value_boolean(v, &o->http_only, e)) return false;
    if (PRESENT("maxAge|有效秒數")) { if (!value_integer(v, &o->max_age, e)) return false; o->has_max_age = true; }
#undef PRESENT
    return true;
}
static void cookie_options_free(cookie_options *o) { aqua_free(o->path); aqua_free(o->same_site); }

static bool cookie_header(const char *cookie, const char *value, const cookie_options *o, net_buffer *out, net_error *e) {
    if (!cookie[0]) return net_error_set(e, NET_INVALID, false, "Invalid cookie name / Cookie 名稱無效。");
    for (const char *p = cookie; *p; p++) {
        char c = *p;
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-'))
            return net_error_set(e, NET_INVALID, false, "Invalid cookie name / Cookie 名稱無效。");
    }
    const char *path = o->path ? o->path : "/";
    if (path[0] != '/') return net_error_set(e, NET_INVALID, false, "Invalid cookie path / Cookie 路徑無效。");
    for (const char *p = path; *p; p++) if ((unsigned char)*p < 32 || *p == ';') return net_error_set(e, NET_INVALID, false, "Invalid cookie path / Cookie 路徑無效。");
    const char *same = o->same_site ? o->same_site : "Lax";
    if (strcmp(same, "Lax") && strcmp(same, "Strict") && strcmp(same, "None")) return net_error_set(e, NET_INVALID, false, "Invalid SameSite / 無效同站設定。");
    if (!strcmp(same, "None") && !o->secure) return net_error_set(e, NET_INVALID, false, "SameSite=None requires Secure / 跨站 Cookie 需要安全傳輸。");
    out->length = 0;
    buffer_format(out, "%s=", cookie);
    url_escape(out, value, strlen(value));
    buffer_format(out, "; Path=%s; SameSite=%s", path, same);
    if (o->http_only) buffer_text(out, "; HttpOnly");
    if (o->secure) buffer_text(out, "; Secure");
    if (o->has_max_age) buffer_format(out, "; Max-Age=%d", o->max_age);
    return true;
}

static bool add_cookie(aqua_call *call, const char *name, exchange *x, const char *cookie, const char *value, const cookie_options *o) {
    net_buffer header = {0};
    net_error e;
    bool ok = cookie_header(cookie, value, o, &header, &e);
    if (!ok) net_raise(call, name, &e);
    else ok = set_header(call, name, x, "Set-Cookie", (const char *)header.data, true);
    buffer_free(&header);
    return ok;
}

void writer_cookie(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    cookie_options o;
    net_error e;
    if (!cookie_options_read(aqua_call_runtime(call), aqua_arg(call, 2), x, &o, &e)) { cookie_options_free(&o); net_raise(call, name, &e); return; }
    if (add_cookie(call, name, x, aqua_arg_str(call, 0).data, aqua_arg_str(call, 1).data, &o)) aqua_return(call, aqua_self_value(call));
    cookie_options_free(&o);
}

void writer_text(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    aqua_str text = aqua_arg_str(call, 0);
    content_type(x, "text/plain; charset=utf-8");
    write_body(call, name, x, text.data, text.length);
}

void writer_json(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    net_buffer json = {0};
    net_error e;
    if (!json_encode(aqua_call_runtime(call), aqua_arg(call, 0), &json, &e)) { buffer_free(&json); net_raise(call, name, &e); return; }
    content_type(x, "application/json; charset=utf-8");
    write_body(call, name, x, json.data, json.length);
    buffer_free(&json);
}

void writer_bytes(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    net_buffer bytes = {0};
    net_error e;
    if (!value_bytes(aqua_call_runtime(call), aqua_arg(call, 0), &bytes, &e)) { buffer_free(&bytes); net_raise(call, name, &e); return; }
    content_type(x, "application/octet-stream");
    write_body(call, name, x, bytes.data, bytes.length);
    buffer_free(&bytes);
}

void writer_redirect(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    int32_t status = aqua_has_arg(call, 1) ? aqua_arg_int(call, 1) : 302;
    if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308) { net_raise_kind(call, name, NET_INVALID, "Invalid redirect / 重新導向狀態無效。"); return; }
    if (x->started) { net_raise_kind(call, name, NET_NETWORK, "StatusCode cannot be set because the response has already started. / 回應已開始，無法設定狀態碼。"); return; }
    x->status = status;
    if (!set_header(call, name, x, "Location", aqua_arg_str(call, 0).data, false)) return;
    write_body(call, name, x, "", 0);
}

/* A Server-Sent Event: "event:", an optional "id:" and one "data:" line per text line, sent at once. */
void writer_event(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    aqua_str event = aqua_arg_str(call, 0), text = aqua_arg_str(call, 1);
    aqua_str id = aqua_has_arg(call, 2) ? aqua_arg_str(call, 2) : (aqua_str){"", 0};
    if (strpbrk(event.data, "\r\n") || strpbrk(id.data, "\r\n") || strlen(id.data) != id.length) { net_raise_kind(call, name, NET_INVALID, "Invalid SSE field / 事件欄位無效。"); return; }
    content_type(x, "text/event-stream; charset=utf-8");
    if (!x->started) headers_set(&x->response, "Cache-Control", "no-cache");
    net_buffer out = {0};
    buffer_format(&out, "event: %s\n", event.data);
    if (id.length) buffer_format(&out, "id: %s\n", id.data);
    const char *p = text.data, *end = text.data + text.length;
    for (;;) {
        const char *stop = p;
        while (stop < end && *stop != '\r' && *stop != '\n') stop++;
        buffer_text(&out, "data: ");
        buffer_append(&out, p, (size_t)(stop - p));
        buffer_text(&out, "\n");
        if (stop >= end) break;
        p = stop + (stop[0] == '\r' && stop + 1 < end && stop[1] == '\n' ? 2 : 1);
    }
    buffer_text(&out, "\n");
    write_body(call, name, x, out.data, out.length);
    buffer_free(&out);
}

void writer_upgrade(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    const char *upgrade = headers_get(&x->headers, "Upgrade"), *connection = headers_get(&x->headers, "Connection");
    const char *key = headers_get(&x->headers, "Sec-WebSocket-Key");
    if (strcmp(x->method, "GET") || !upgrade || !net_iequal(upgrade, "websocket") || !connection || !net_itoken(connection, "upgrade") || !key || x->upgraded) {
        net_raise_kind(call, name, NET_INVALID, "Expected WebSocket upgrade / 需要 WebSocket 升級請求。");
        return;
    }
    if (x->started) { net_raise_kind(call, name, NET_NETWORK, "Headers are read-only, response has already started. / 回應已開始，無法設定標頭。"); return; }
    net_buffer accept = {0}, input = {0};
    buffer_text(&input, key);
    buffer_text(&input, WS_GUID);
    uint8_t digest[20];
    net_sha1(input.data, input.length, digest);
    net_base64(digest, sizeof digest, &accept);
    net_buffer h = {0};
    buffer_text(&h, "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n");
    buffer_format(&h, "Sec-WebSocket-Accept: %s\r\n", (char *)accept.data);
    for (uint32_t i = 0; i < x->response.count; i++)
        if (!net_iprefix(x->response.items[i].name, "Content-")) buffer_format(&h, "%s: %s\r\n", x->response.items[i].name, x->response.items[i].value);
    buffer_text(&h, "\r\n");
    x->started = true;
    x->upgraded = true;
    net_lock(&x->lock);
    buffer_append(&x->out, h.data, h.length);
    x->upgrade = true;
    pthread_cond_signal(&x->wake);
    net_unlock(&x->lock);
    buffer_free(&h);
    buffer_free(&accept);
    buffer_free(&input);
    x->drain_result = aqua_void();
    x->drain_name = name;
    x->drain = aqua_defer(call);
}

/* ---- Sessions: random 64-hex-digit identifiers in the aqua_session cookie, 30 idle minutes, JSON values. ---- */

typedef struct session_handle { exchange *exchange; char token[65]; bool destroyed; } session_handle;

void session_release(aqua_runtime *rt, void *payload) {
    (void)rt;
    session_handle *h = payload;
    exchange_unref(h->exchange);
    free(h);
}

static session_entry *find_session(server *sv, const char *token) {
    for (uint32_t i = 0; i < sv->session_count; i++) if (!strcmp(sv->sessions[i].token, token)) return &sv->sessions[i];
    return NULL;
}

void request_session(aqua_call *call, const char *name) {
    exchange *x = live_exchange(call, name);
    if (!x) return;
    server *sv = x->server;
    int64_t now = (int64_t)time(NULL);
    uint32_t kept = 0;
    for (uint32_t i = 0; i < sv->session_count; i++) {
        if (sv->sessions[i].expires < now) { headers_free(&sv->sessions[i].values); continue; }
        sv->sessions[kept++] = sv->sessions[i];
    }
    sv->session_count = kept;
    char *cookie = x->session ? net_strdup(x->session) : cookie_value(x, "aqua_session");
    session_entry *entry = cookie ? find_session(sv, cookie) : NULL;
    free(cookie);
    if (!entry) {
        if (sv->session_count >= SESSION_LIMIT) { net_raise_kind(call, name, NET_NETWORK, "Session limit / 工作階段已滿。"); return; }
        uint8_t random[32];
        platform_random(random, sizeof random);
        session_entry fresh;
        memset(&fresh, 0, sizeof fresh);
        for (int i = 0; i < 32; i++) snprintf(fresh.token + i * 2, 3, "%02x", random[i]);
        cookie_options o;
        net_error e;
        cookie_options_read(aqua_call_runtime(call), aqua_null(), x, &o, &e);
        if (!add_cookie(call, name, x, "aqua_session", fresh.token, &o)) return;
        sv->sessions = realloc(sv->sessions, (sv->session_count + 1) * sizeof *sv->sessions);
        sv->sessions[sv->session_count] = fresh;
        entry = &sv->sessions[sv->session_count++];
        free(x->session);
        x->session = net_strdup(fresh.token);
    }
    entry->expires = now + SESSION_IDLE_SECONDS;
    session_handle *h = calloc(1, sizeof *h);
    atomic_fetch_add(&x->refs, 1);
    h->exchange = x;
    memcpy(h->token, entry->token, sizeof h->token);
    aqua_return(call, object_wrap(aqua_call_runtime(call), object_create(CLASS_session, h)));
}

static session_handle *live_session(aqua_call *call, const char *name) {
    session_handle *h = self_payload(call);
    if (h->exchange->active && !h->destroyed) return h;
    net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED);
    return NULL;
}

void session_get(aqua_call *call, const char *name) {
    session_handle *h = live_session(call, name);
    if (!h) return;
    session_entry *entry = find_session(h->exchange->server, h->token);
    const char *json = entry ? pair_value(&entry->values, aqua_arg_str(call, 0).data) : NULL;
    if (!json) { aqua_return_null(call); return; }
    aqua_value value;
    net_error e;
    if (json_decode(aqua_call_runtime(call), (const uint8_t *)json, strlen(json), &value, &e)) aqua_return(call, value);
    else net_raise(call, name, &e);
}

void session_set(aqua_call *call, const char *name) {
    session_handle *h = live_session(call, name);
    if (!h) return;
    session_entry *entry = find_session(h->exchange->server, h->token);
    if (!entry) { net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED); return; }
    net_buffer json = {0};
    net_error e;
    if (!json_encode(aqua_call_runtime(call), aqua_arg(call, 1), &json, &e)) { buffer_free(&json); net_raise(call, name, &e); return; }
    const char *key = aqua_arg_str(call, 0).data;
    bool exists = pair_value(&entry->values, key) != NULL;
    if ((entry->values.count >= SESSION_FIELDS && !exists) || utf16_length((const char *)json.data, json.length) > 65536) {
        buffer_free(&json);
        net_raise_kind(call, name, NET_INVALID, "Session limit / 工作階段資料超過上限。");
        return;
    }
    bool replaced = false;
    for (uint32_t i = 0; i < entry->values.count && !replaced; i++)
        if (!strcmp(entry->values.items[i].name, key)) { free(entry->values.items[i].value); entry->values.items[i].value = net_strdup((char *)json.data); replaced = true; }
    if (!replaced) headers_add(&entry->values, key, (char *)json.data);
    buffer_free(&json);
}

void session_destroy(aqua_call *call, const char *name) {
    session_handle *h = live_session(call, name);
    if (!h) return;
    server *sv = h->exchange->server;
    session_entry *entry = find_session(sv, h->token);
    if (entry) {
        headers_free(&entry->values);
        size_t index = (size_t)(entry - sv->sessions);
        memmove(entry, entry + 1, (sv->session_count - index - 1) * sizeof *entry);
        sv->session_count--;
    }
    h->destroyed = true;
    cookie_options o;
    net_error e;
    cookie_options_read(aqua_call_runtime(call), aqua_null(), h->exchange, &o, &e);
    o.has_max_age = true;
    o.max_age = 0;
    add_cookie(call, name, h->exchange, "aqua_session", "", &o);
}
