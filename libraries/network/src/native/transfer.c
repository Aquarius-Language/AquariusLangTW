/* The native HTTP/1.1 client, like the old HttpClient with SocketsHttpHandler: pooled keep-alive connections per
 * client, a cookie container (unless credentials are "omit"), redirects (300-303, 307, 308; never HTTPS to HTTP),
 * gzip/deflate decoding, a total deadline for buffered requests and an inactivity deadline for streams, bodies limited
 * to maxBodyBytes. Each transfer runs as one job on a worker thread. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if !defined(AQUA_OS_WINDOWS)
#include <netinet/tcp.h>
#endif

#define POOL_LIMIT 32
#define POOL_IDLE_MS 60000
#define POOL_LIFETIME_MS (5 * 60000)

typedef struct pooled { net_conn *conn; char *key; int64_t idle_since, created; struct pooled *next; } pooled;
typedef struct client_engine {
    cookie_jar *jar;
    net_mutex lock;
    pooled *idle;
    uint32_t idle_count;
} client_engine;

bool engine_client_open(client *c, net_error *e) {
    (void)e;
    client_engine *ce = calloc(1, sizeof *ce);
    ce->jar = jar_new();
    net_mutex_init(&ce->lock);
    c->engine = ce;
    return true;
}

static void pool_drain(client_engine *ce) {
    net_lock(&ce->lock);
    pooled *list = ce->idle;
    ce->idle = NULL;
    ce->idle_count = 0;
    net_unlock(&ce->lock);
    while (list) { pooled *next = list->next; conn_free(list->conn); free(list->key); free(list); list = next; }
}

void engine_client_close(client *c) { if (c->engine) pool_drain(c->engine); }

void engine_client_free(client *c) {
    client_engine *ce = c->engine;
    if (!ce) return;
    pool_drain(ce);
    jar_free(ce->jar);
    net_mutex_destroy(&ce->lock);
    free(ce);
    c->engine = NULL;
}

bool engine_client_clear_cookies(client *c, net_error *e) {
    (void)e;
    jar_clear(((client_engine *)c->engine)->jar);
    return true;
}

static char *pool_key(const net_url *url) {
    net_buffer b = {0};
    buffer_format(&b, "%s://%s:%d", url->scheme, url->host, url->port);
    char *key = buffer_take(&b, NULL);
    buffer_free(&b);
    return key;
}

static net_conn *pool_take(client_engine *ce, const char *key) {
    int64_t now = net_clock_ms();
    for (;;) {
        net_lock(&ce->lock);
        pooled **p = &ce->idle;
        while (*p && strcmp((*p)->key, key)) p = &(*p)->next;
        pooled *found = *p;
        if (found) { *p = found->next; ce->idle_count--; }
        net_unlock(&ce->lock);
        if (!found) return NULL;
        net_conn *conn = found->conn;
        bool fresh = now - found->idle_since < POOL_IDLE_MS && now - found->created < POOL_LIFETIME_MS && conn_idle_alive(conn);
        free(found->key);
        free(found);
        if (fresh) return conn;
        conn_free(conn);
    }
}

static void pool_put(client_engine *ce, const char *key, net_conn *conn, int64_t created) {
    pooled *p = calloc(1, sizeof *p);
    p->conn = conn;
    p->key = net_strdup(key);
    p->idle_since = net_clock_ms();
    p->created = created;
    net_lock(&ce->lock);
    p->next = ce->idle;
    ce->idle = p;
    ce->idle_count++;
    pooled *drop = NULL;
    if (ce->idle_count > POOL_LIMIT) {
        pooled **q = &ce->idle;
        while ((*q)->next) q = &(*q)->next;
        drop = *q;
        *q = NULL;
        ce->idle_count--;
    }
    net_unlock(&ce->lock);
    if (drop) { conn_free(drop->conn); free(drop->key); free(drop); }
}

static void set_nodelay(net_fd fd) {
    int on = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (const char *)&on, sizeof on);
}

static net_conn *open_connection(const net_url *url, const net_io *io, net_error *e) {
    char *host = url_hostname(url);
    net_fd fd = net_connect(host, url->port, io, e);
    if (fd == NET_NO_SOCKET) { free(host); return NULL; }
    set_nodelay(fd);
    net_conn *conn = conn_new(fd);
    if (!strcmp(url->scheme, "https") && !conn_tls_client(conn, host, "http/1.1", io, e)) { conn_free(conn); conn = NULL; }
    free(host);
    return conn;
}

static bool idempotent(const char *method) {
    return !strcmp(method, "GET") || !strcmp(method, "HEAD") || !strcmp(method, "OPTIONS") || !strcmp(method, "PUT")
        || !strcmp(method, "DELETE") || !strcmp(method, "TRACE");
}

/* The request head of one hop. */
static void request_head(net_buffer *out, const char *method, const net_url *url, const net_headers *headers,
                         const char *cookies, bool has_body, size_t body_length) {
    out->length = 0;
    buffer_format(out, "%s %s%s HTTP/1.1\r\n", method, url->path, url->query);
    const char *host = headers_get(headers, "Host");
    if (host) buffer_format(out, "Host: %s\r\n", host);
    else if (url->explicit_port && url->port != url_default_port(url->scheme)) buffer_format(out, "Host: %s:%d\r\n", url->host, url->port);
    else buffer_format(out, "Host: %s\r\n", url->host);
    for (uint32_t i = 0; i < headers->count; i++) {
        const net_header *h = &headers->items[i];
        if (net_iequal(h->name, "Host") || net_iequal(h->name, "Content-Length") || net_iequal(h->name, "Transfer-Encoding")) continue;
        if (cookies && net_iequal(h->name, "Cookie")) continue;
        buffer_format(out, "%s: %s\r\n", h->name, h->value);
    }
    if (cookies) {
        const char *own = headers_get(headers, "Cookie");
        if (own) buffer_format(out, "Cookie: %s; %s\r\n", own, cookies);
        else buffer_format(out, "Cookie: %s\r\n", cookies);
    }
    if (!headers_get(headers, "Accept-Encoding")) buffer_text(out, "Accept-Encoding: gzip, deflate\r\n");
    if (has_body) buffer_format(out, "Content-Length: %zu\r\n", body_length);
    else if (!strcmp(method, "POST") || !strcmp(method, "PUT") || !strcmp(method, "PATCH")) buffer_text(out, "Content-Length: 0\r\n");
    buffer_text(out, "\r\n");
}

static bool is_redirect(int status) { return status == 300 || status == 301 || status == 302 || status == 303 || status == 307 || status == 308; }

typedef struct transfer_job { net_job job; transfer *t; } transfer_job;

/* Runs the exchange, filling the transfer's outcome. */
static void transfer_exchange(transfer *t, engine *eng) {
    client_engine *ce = t->client->engine;
    const net_options *o = &t->options;
    net_error *e = &t->error;
    net_io io = io_make(o->timeout_ms, &t->cancelled, NET_CANCELLED, eng);
    char *method = net_strdup(t->method);
    net_url url;
    char *text = url_format(&t->url, true);
    url_parse(text, &url);
    free(text);
    net_headers headers = {0};
    headers_copy(&headers, &o->headers);
    bool has_body = o->has_body;
    bool cookies_on = o->credentials != CREDENTIALS_OMIT;
    net_buffer head = {0}, in = {0}, raw = {0};
    http_head response = {0};
    net_conn *conn = NULL;
    inflater *decoder = NULL;
    int redirects = 0;
    bool ok = false;
    for (;;) {
        if (!strcmp(url.scheme, "http")) {
            char *host = url_hostname(&url);
            bool permitted = platform_cleartext_permitted(host);
            if (!permitted) net_error_set(e, NET_NETWORK, false, "CLEARTEXT communication to %s not permitted by network security policy / 網路安全政策不允許明文連線。", host);
            free(host);
            if (!permitted) break;
        }
        char *key = pool_key(&url);
        char *cookies = cookies_on ? jar_header(ce->jar, &url) : NULL;
        request_head(&head, method, &url, &headers, cookies, has_body, has_body ? o->body.length : 0);
        free(cookies);
        int64_t created = net_clock_ms();
        bool reused = false;
        int got = -1;
        for (int attempt = 0; attempt < 2; attempt++) {
            conn = attempt == 0 ? pool_take(ce, key) : NULL;
            reused = conn != NULL;
            if (!conn) conn = open_connection(&url, &io, e);
            if (!conn) break;
            in.length = 0;
            got = -1;
            bool sent = conn_write(conn, head.data, head.length, &io, e) && (!has_body || conn_write(conn, o->body.data, o->body.length, &io, e));
            if (sent) {
                /* Skip interim 1xx responses. */
                do {
                    http_head_free(&response);
                    got = http_read_head(conn, &in, false, &response, &io, e);
                } while (got == 1 && response.status >= 100 && response.status < 200 && response.status != 101);
            }
            if (got == 1) break;
            conn_free(conn);
            conn = NULL;
            /* A pooled connection the server closed fails before any response: an idempotent request is sent once
               more on a new connection. Others (POST...) are never resent, as before; idle connections are checked
               before reuse, which makes this rare. */
            bool retry = attempt == 0 && reused && io_check(&io, NULL) && idempotent(method) && (got == 0 || e->kind == NET_NETWORK);
            if (!retry) {
                if (got == 0) net_error_set(e, NET_NETWORK, true, "The server closed the connection without a response / 伺服器未回應即關閉連線。");
                break;
            }
            memset(e, 0, sizeof *e);
        }
        if (!conn) { free(key); break; }
        if (cookies_on)
            for (uint32_t i = 0; i < response.headers.count; i++)
                if (net_iequal(response.headers.items[i].name, "Set-Cookie")) jar_store(ce->jar, &url, response.headers.items[i].value);
        http_body body;
        if (!http_body_start(&body, &response, false, method)) {
            net_error_set(e, NET_NETWORK, false, "Invalid HTTP message / HTTP 訊息格式錯誤。");
            free(key);
            break;
        }
        /* Redirects. */
        const char *location = headers_get(&response.headers, "Location");
        if (is_redirect(response.status) && location && o->follow_redirects && o->max_redirects > 0 && redirects < o->max_redirects) {
            net_url target;
            bool follow = url_resolve(&url, location, &target) && (!strcmp(target.scheme, "http") || !strcmp(target.scheme, "https"))
                && !(!strcmp(url.scheme, "https") && !strcmp(target.scheme, "http"));
            if (follow) {
                /* Drain a small body to keep the connection; otherwise close it. */
                net_buffer discard = {0};
                bool reusable = body.mode != BODY_CLOSE && !(body.mode == BODY_LENGTH && body.remaining > 65536);
                long n = 1;
                while (reusable && n > 0 && discard.length <= 65536) { discard.length = 0; n = http_body_read(&body, conn, &in, &discard, &io, e); }
                const char *connection = headers_get(&response.headers, "Connection");
                if (reusable && n == 0 && in.length == 0 && response.minor >= 1 && !(connection && net_itoken(connection, "close"))) pool_put(ce, key, conn, created);
                else conn_free(conn);
                conn = NULL;
                buffer_free(&discard);
                free(key);
                if (!io_check(&io, e)) { url_free(&target); break; }
                int status = response.status;
                bool to_get = (status == 303 && strcmp(method, "HEAD")) || ((status == 301 || status == 302) && !strcmp(method, "POST"));
                if (to_get) {
                    free(method);
                    method = net_strdup("GET");
                    has_body = false;
                    headers_remove(&headers, "Content-Type");
                }
                headers_remove(&headers, "Authorization");
                url_free(&url);
                url = target;
                redirects++;
                http_head_free(&response);
                continue;
            }
            url_free(&target);
        }
        /* The final response. */
        const char *length = headers_get(&response.headers, "Content-Length");
        if (length && body.mode == BODY_LENGTH && body.remaining > (uint64_t)o->max_body) {
            net_error_set(e, NET_LIMIT, false, MSG_BODY_LIMIT);
            free(key);
            break;
        }
        const char *encoding = headers_get(&response.headers, "Content-Encoding");
        if (encoding && (net_iequal(encoding, "gzip") || net_iequal(encoding, "x-gzip"))) decoder = inflate_new(INFLATE_GZIP);
        else if (encoding && net_iequal(encoding, "deflate")) decoder = inflate_new(INFLATE_ZLIB_OR_RAW);
        if (decoder) { headers_remove(&response.headers, "Content-Encoding"); headers_remove(&response.headers, "Content-Length"); }
        if (t->streaming) { transfer_head(t, response.status); io = io_make(o->timeout_ms, &t->cancelled, NET_CANCELLED, eng); }
        bool body_ok = true;
        for (;;) {
            size_t before = t->body.length;
            long n;
            if (decoder) {
                raw.length = 0;
                n = http_body_read(&body, conn, &in, &raw, &io, e);
                if (n > 0 && !inflate_feed(decoder, raw.data, raw.length, &t->body, e)) n = -1;
                if (n == 0 && !inflate_end(decoder, e)) n = -1;
            } else n = http_body_read(&body, conn, &in, &t->body, &io, e);
            if (n < 0) { body_ok = false; break; }
            if (t->body.length > (size_t)o->max_body) { net_error_set(e, NET_LIMIT, false, MSG_BODY_LIMIT); body_ok = false; break; }
            if (t->streaming && t->body.length > before) {
                if (!transfer_stream_bytes(t, t->body.data + before, t->body.length - before, e)) { body_ok = false; break; }
                io = io_make(o->timeout_ms, &t->cancelled, NET_CANCELLED, eng);
            }
            if (n == 0) break;
        }
        if (!body_ok) { free(key); break; }
        const char *connection = headers_get(&response.headers, "Connection");
        if (body.mode != BODY_CLOSE && in.length == 0 && response.minor >= 1 && !(connection && net_itoken(connection, "close"))) {
            pool_put(ce, key, conn, created);
            conn = NULL;
        }
        free(key);
        t->status = response.status;
        free(t->final_url);
        t->final_url = url_format(&url, true);
        t->headers = response.headers;
        memset(&response.headers, 0, sizeof response.headers);
        if (response.status >= 400) {
            int s = response.status;
            net_error_set(e, NET_HTTP, s == 408 || s == 429 || s >= 500, "HTTP %d", s);
        }
        ok = true;
        break;
    }
    if (!ok) {
        /* Failures report status 0 with the requested URL, no headers and no body. */
        t->status = 0;
        buffer_free(&t->body);
        headers_free(&t->headers);
        if (e->kind == NET_OK) net_error_set(e, NET_NETWORK, true, "Request failed / 請求失敗。");
    }
    conn_free(conn);
    inflate_free(decoder);
    http_head_free(&response);
    headers_free(&headers);
    buffer_free(&head);
    buffer_free(&in);
    buffer_free(&raw);
    url_free(&url);
    free(method);
}

static void transfer_run(net_job *job) {
    transfer_job *j = (transfer_job *)job;
    transfer_exchange(j->t, job->engine);
}
static void transfer_done(network_state *s, net_job *job) {
    transfer *t = ((transfer_job *)job)->t;
    free(job);
    transfer_finished(s, t);
}
static void transfer_discard(net_job *job) {
    transfer_unref(((transfer_job *)job)->t);
    free(job);
}

void engine_transfer_start(network_state *s, transfer *t) {
    transfer_job *j = calloc(1, sizeof *j);
    j->t = t;
    j->job.run = transfer_run;
    j->job.done = transfer_done;
    j->job.discard = transfer_discard;
    job_start(s->engine, &j->job);
}

void engine_transfer_cancel(transfer *t) { (void)t; /* the worker sees t->cancelled within one wait slice */ }
