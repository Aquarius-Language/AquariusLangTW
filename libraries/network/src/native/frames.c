/* WebSocket (RFC 6455) on native connections: the client handshake, frames for clients (masked) and server
 * upgrades (unmasked), fragmented messages up to maxBodyBytes, ping/pong and the closing handshake. Each Connect,
 * Send, Receive and Close runs as a job; bytes of incomplete frames stay buffered, so a Receive that times out leaves
 * the socket usable. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>



typedef struct ws_engine {
    net_conn *conn;
    bool server;                /* the server end of an upgrade: sends unmasked, expects masked frames */
    void *owner;                /* server ends: the exchange keeping the connection */
    void (*release_owner)(void *owner);
    atomic_bool aborted;
    net_mutex write_lock;       /* one frame at a time */
    net_buffer in;              /* received bytes not yet parsed */
    net_buffer message;         /* a fragmented message being assembled */
    int message_opcode;
    bool close_sent, close_received;
    engine *engine;
} ws_engine;

typedef struct ws_job {
    net_job job;
    websocket *ws;
    aqua_pending *pending;
    const char *name;
    net_error error;
    bool failed;
    /* connect */
    net_url url;
    net_headers headers;
    char *protocols;
    /* send, close */
    net_buffer data;
    int opcode;
    /* receive */
    bool is_text, closed;
} ws_job;

static ws_engine *ws_of(websocket *ws) { return ws->engine; }

void engine_websocket_free(websocket *ws) {
    ws_engine *w = ws->engine;
    if (!w) return;
    if (w->owner) w->release_owner(w->owner);
    else conn_free(w->conn);
    net_mutex_destroy(&w->write_lock);
    buffer_free(&w->in);
    buffer_free(&w->message);
    free(w);
    ws->engine = NULL;
}

void engine_websocket_abort(websocket *ws) {
    ws_engine *w = ws->engine;
    if (!w) return;
    atomic_store(&w->aborted, true);
    if (w->conn) {
#if defined(AQUA_OS_WINDOWS)
        shutdown(w->conn->fd, SD_BOTH);
#else
        shutdown(w->conn->fd, SHUT_RDWR);
#endif
    }
}

static ws_engine *ws_engine_new(engine *e) {
    ws_engine *w = calloc(1, sizeof *w);
    atomic_init(&w->aborted, false);
    net_mutex_init(&w->write_lock);
    w->engine = e;
    return w;
}

websocket *websocket_server_new(network_state *s, net_conn *conn, net_buffer *leftover, void *owner, void (*release_owner)(void *), const net_options *o) {
    websocket *ws = websocket_new(s, o);
    ws_engine *w = ws_engine_new(s->engine);
    w->conn = conn;
    w->server = true;
    w->owner = owner;
    w->release_owner = release_owner;
    if (leftover && leftover->length) buffer_append(&w->in, leftover->data, leftover->length);
    ws->engine = w;
    return ws;
}

static net_io ws_io(websocket *ws) {
    ws_engine *w = ws_of(ws);
    return io_make(ws->timeout_ms, &w->aborted, NET_CLOSED, w->engine);
}

static bool write_frame(websocket *ws, int opcode, const uint8_t *payload, size_t length, const net_io *io, net_error *e) {
    ws_engine *w = ws_of(ws);
    uint8_t header[14];
    size_t n = 0;
    header[n++] = (uint8_t)(0x80 | opcode);
    uint8_t mask_bit = w->server ? 0 : 0x80;
    if (length < 126) header[n++] = (uint8_t)(mask_bit | length);
    else if (length < 65536) { header[n++] = mask_bit | 126; header[n++] = (uint8_t)(length >> 8); header[n++] = (uint8_t)length; }
    else { header[n++] = mask_bit | 127; for (int i = 7; i >= 0; i--) header[n++] = (uint8_t)((uint64_t)length >> (8 * i)); }
    net_buffer frame = {0};
    buffer_reserve(&frame, n + 4 + length);
    buffer_append(&frame, header, n);
    if (!w->server) {
        uint8_t mask[4];
        platform_random(mask, sizeof mask);
        buffer_append(&frame, mask, 4);
        size_t ws_start = frame.length;
        buffer_append(&frame, payload, length);
        for (size_t i = 0; i < length; i++) frame.data[ws_start + i] ^= mask[i & 3];
    } else buffer_append(&frame, payload, length);
    net_lock(&w->write_lock);
    bool ok = conn_write(w->conn, frame.data, frame.length, io, e);
    net_unlock(&w->write_lock);
    buffer_free(&frame);
    return ok;
}

static bool ws_protocol_error(net_error *e) { return net_error_set(e, NET_NETWORK, false, "WebSocket protocol error / WebSocket 協定錯誤。"); }

/* Reads frames until a whole data message or a close frame; handles control frames on the way. */
static bool receive_message(websocket *ws, ws_job *j, const net_io *io) {
    ws_engine *w = ws_of(ws);
    net_error *e = &j->error;
    if (w->close_received) { j->closed = true; return true; }
    for (;;) {
        /* Parse a complete frame from the buffer, reading more when needed. */
        const uint8_t *d = w->in.data;
        size_t have = w->in.length;
        size_t need = 2;
        uint64_t length = 0;
        bool masked = false;
        if (have >= 2) {
            masked = (d[1] & 0x80) != 0;
            length = d[1] & 0x7F;
            if (length == 126) need = 4;
            else if (length == 127) need = 10;
            if (have >= need) {
                if (need == 4) length = (uint64_t)d[2] << 8 | d[3];
                else if (need == 10) { length = 0; for (int i = 0; i < 8; i++) length = length << 8 | d[2 + i]; }
                if (masked) need += 4;
                if (length > (uint64_t)ws->max_message + 125) {
                    atomic_store(&w->aborted, true);
                    return net_error_set(e, NET_LIMIT, false, MSG_MESSAGE_LIMIT);
                }
                need += (size_t)length;
            }
        }
        if (have < need) {
            if (!buffer_reserve(&w->in, 16384)) return net_error_set(e, NET_LIMIT, false, MSG_MESSAGE_LIMIT);
            long n = conn_read(w->conn, w->in.data + w->in.length, w->in.capacity - w->in.length - 1, io, e);
            if (n < 0) return false;
            if (n == 0) { w->close_received = true; j->closed = true; ws->close_code = 1006; return true; } /* closed without a close frame */
            w->in.length += (size_t)n;
            continue;
        }
        bool fin = (d[0] & 0x80) != 0;
        int opcode = d[0] & 0x0F;
        if (d[0] & 0x70) return ws_protocol_error(e);
        if (w->server && !masked) return ws_protocol_error(e);
        size_t header = need - (size_t)length;
        uint8_t *payload = w->in.data + header;
        if (masked) { const uint8_t *mask = payload - 4; for (size_t i = 0; i < length; i++) payload[i] ^= mask[i & 3]; }
        if (opcode >= 8) {
            if (!fin || length > 125) return ws_protocol_error(e);
            if (opcode == 8) {
                w->close_received = true;
                ws->close_code = length >= 2 ? (int32_t)(payload[0] << 8 | payload[1]) : 1005;
                if (length > 2) ws->close_reason = net_strndup((const char *)payload + 2, (size_t)length - 2);
                /* RFC 6455 5.5.1: answer a close with a close echoing its status code. */
                if (!w->close_sent && w->conn) {
                    net_error ignored;
                    w->close_sent = true;
                    write_frame(ws, 8, payload, length >= 2 ? 2 : 0, io, &ignored);
                }
                buffer_consume(&w->in, need);
                j->closed = true;
                return true;
            }
            if (opcode == 9) {
                uint8_t pong[125];
                memcpy(pong, payload, (size_t)length);
                buffer_consume(&w->in, need);
                if (!w->close_sent && !write_frame(ws, 10, pong, (size_t)length, io, e)) return false;
                continue;
            }
            if (opcode != 10) return ws_protocol_error(e);
            buffer_consume(&w->in, need);
            continue;
        }
        if (opcode == 0 ? w->message_opcode == 0 : (opcode != 1 && opcode != 2) || w->message_opcode != 0) return ws_protocol_error(e);
        if (opcode) w->message_opcode = opcode;
        if (w->message.length + length > (uint64_t)ws->max_message) {
            atomic_store(&w->aborted, true);
            return net_error_set(e, NET_LIMIT, false, MSG_MESSAGE_LIMIT);
        }
        buffer_append(&w->message, payload, (size_t)length);
        buffer_consume(&w->in, need);
        if (!fin) continue;
        j->is_text = w->message_opcode == 1;
        j->data = w->message;
        memset(&w->message, 0, sizeof w->message);
        w->message_opcode = 0;
        return true;
    }
}

/* ---- Jobs ---- */

static ws_job *ws_job_new(websocket *ws, aqua_pending *pending, const char *name, void (*run)(net_job *), void (*done)(network_state *, net_job *)) {
    ws_job *j = calloc(1, sizeof *j);
    atomic_fetch_add(&ws->refs, 1);
    j->ws = ws;
    j->pending = pending;
    j->name = name;
    j->job.run = run;
    j->job.done = done;
    return j;
}
static void ws_job_free(ws_job *j) {
    websocket_unref(j->ws);
    url_free(&j->url);
    headers_free(&j->headers);
    free(j->protocols);
    buffer_free(&j->data);
    free(j);
}
static void ws_job_discard(net_job *job) { ws_job_free((ws_job *)job); }
static void ws_start(network_state *s, ws_job *j) { j->job.discard = ws_job_discard; job_start(s->engine, &j->job); }

/* Settles a send or close job: null or the error. */
static void ws_settle(network_state *s, ws_job *j) {
    (void)s;
    if (j->failed) net_reject(j->pending, j->name, &j->error);
    else aqua_resolve(j->pending, aqua_null());
}

static void connect_run(net_job *job) {
    ws_job *j = (ws_job *)job;
    websocket *ws = j->ws;
    ws_engine *w = ws_of(ws);
    net_error *e = &j->error;
    net_io io = ws_io(ws);
    char *host = url_hostname(&j->url);
    bool secure = !strcmp(j->url.scheme, "wss");
    net_buffer request = {0};
    http_head response = {0};
    j->failed = true;
    if (!secure && !platform_cleartext_permitted(host)) {
        net_error_set(e, NET_NETWORK, false, "CLEARTEXT communication to %s not permitted by network security policy / 網路安全政策不允許明文連線。", host);
        goto end;
    }
    net_fd fd = net_connect(host, j->url.port, &io, e);
    if (fd == NET_NO_SOCKET) goto end;
    w->conn = conn_new(fd);
    if (secure && !conn_tls_client(w->conn, host, "http/1.1", &io, e)) goto end;
    uint8_t nonce[16];
    platform_random(nonce, sizeof nonce);
    net_buffer key = {0};
    net_base64(nonce, sizeof nonce, &key);
    buffer_format(&request, "GET %s%s HTTP/1.1\r\n", j->url.path, j->url.query);
    if (j->url.explicit_port && j->url.port != url_default_port(j->url.scheme)) buffer_format(&request, "Host: %s:%d\r\n", j->url.host, j->url.port);
    else buffer_format(&request, "Host: %s\r\n", j->url.host);
    buffer_format(&request, "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n", (char *)key.data);
    for (uint32_t i = 0; i < j->headers.count; i++) buffer_format(&request, "%s: %s\r\n", j->headers.items[i].name, j->headers.items[i].value);
    if (j->protocols) buffer_format(&request, "Sec-WebSocket-Protocol: %s\r\n", j->protocols);
    buffer_text(&request, "\r\n");
    if (!conn_write(w->conn, request.data, request.length, &io, e)) { buffer_free(&key); goto end; }
    int got = http_read_head(w->conn, &w->in, false, &response, &io, e);
    if (got != 1) {
        if (got == 0) net_error_set(e, NET_NETWORK, true, "The server closed the connection during the WebSocket handshake / WebSocket 交握時連線已關閉。");
        buffer_free(&key);
        goto end;
    }
    if (response.status != 101) {
        net_error_set(e, NET_NETWORK, true, "The server returned status code '%d' when status code '101' was expected.", response.status);
        buffer_free(&key);
        goto end;
    }
    /* Sec-WebSocket-Accept: base64(SHA-1(key + GUID)). */
    buffer_text(&key, WS_GUID);
    uint8_t digest[20];
    net_sha1(key.data, key.length, digest);
    net_buffer expected = {0};
    net_base64(digest, sizeof digest, &expected);
    const char *accept = headers_get(&response.headers, "Sec-WebSocket-Accept");
    const char *upgrade = headers_get(&response.headers, "Upgrade");
    bool valid = accept && !strcmp(accept, (char *)expected.data) && upgrade && net_iequal(upgrade, "websocket");
    buffer_free(&expected);
    buffer_free(&key);
    if (!valid) { net_error_set(e, NET_NETWORK, true, "The WebSocket handshake was invalid / WebSocket 交握無效。"); goto end; }
    /* The server picks one of the offered subprotocols, or none. */
    const char *chosen = headers_get(&response.headers, "Sec-WebSocket-Protocol");
    if (chosen && (!j->protocols || !net_itoken(j->protocols, chosen))) {
        net_error_set(e, NET_NETWORK, false, "The server chose a subprotocol the client did not offer / 伺服器選擇了未提供的子協定。");
        goto end;
    }
    if (chosen) ws->protocol = net_strdup(chosen);
    j->failed = false;
end:
    if (j->failed && (e->kind == NET_DNS)) e->kind = NET_NETWORK;
    http_head_free(&response);
    buffer_free(&request);
    free(host);
}

static void connect_done(network_state *s, net_job *job) {
    ws_job *j = (ws_job *)job;
    websocket *ws = j->ws;
    aqua_pending *pending = j->pending;
    const char *name = j->name;
    net_error error = j->error;
    bool failed = j->failed;
    atomic_fetch_add(&ws->refs, 1); /* the reference websocket_connected takes over */
    ws_job_free(j);
    websocket_connected(s, ws, pending, name, failed ? &error : NULL);
}

void engine_websocket_connect(network_state *s, websocket *ws, const net_url *url, const net_options *o, aqua_pending *pending, const char *name) {
    ws->engine = ws_engine_new(s->engine);
    ws_job *j = ws_job_new(ws, pending, name, connect_run, connect_done);
    websocket_unref(ws); /* the job holds the connect operation's reference */
    char *text = url_format(url, false);
    url_parse(text, &j->url);
    free(text);
    headers_copy(&j->headers, &o->headers);
    j->protocols = net_strdup(o->protocols);
    ws_start(s, j);
}

static void send_run(net_job *job) {
    ws_job *j = (ws_job *)job;
    net_io io = ws_io(j->ws);
    ws_engine *w = ws_of(j->ws);
    if (w->close_sent) { j->failed = !net_error_set(&j->error, NET_CLOSED, false, MSG_CLOSED); return; }
    j->failed = !write_frame(j->ws, j->opcode, j->data.data, j->data.length, &io, &j->error);
}
static void send_done(network_state *s, net_job *job) {
    ws_job *j = (ws_job *)job;
    ws_settle(s, j);
    ws_job_free(j);
}

void engine_websocket_send(network_state *s, websocket *ws, net_buffer *message, bool text, aqua_pending *pending, const char *name) {
    ws_job *j = ws_job_new(ws, pending, name, send_run, send_done);
    j->data = *message;
    memset(message, 0, sizeof *message);
    j->opcode = text ? 1 : 2;
    ws_start(s, j);
}

static void receive_run(net_job *job) {
    ws_job *j = (ws_job *)job;
    /* A WebSocket waits for its next message as long as it takes (as the browser's does); the timeout limits
       connecting and sending. Promise.Timeout(socket.ReceiveAsync(), ms) limits one wait. */
    net_io io = io_make(0, &ws_of(j->ws)->aborted, NET_CLOSED, ws_of(j->ws)->engine);
    j->failed = !receive_message(j->ws, j, &io);
}
static void receive_done(network_state *s, net_job *job) {
    ws_job *j = (ws_job *)job;
    j->ws->receiving = false;
    if (j->failed) net_reject(j->pending, j->name, &j->error);
    else aqua_resolve(j->pending, j->closed ? close_record(s->rt, j->ws)
                                            : message_record(s->rt, j->data.data, j->data.length, j->is_text, false));
    if (j->failed && j->error.kind == NET_LIMIT) resource_close(s, &j->ws->base);
    ws_job_free(j);
}

void engine_websocket_receive(network_state *s, websocket *ws, aqua_pending *pending, const char *name) {
    ws->receiving = true;
    ws_start(s, ws_job_new(ws, pending, name, receive_run, receive_done));
}

static void close_run(net_job *job) {
    ws_job *j = (ws_job *)job;
    ws_engine *w = ws_of(j->ws);
    net_io io = ws_io(j->ws);
    if (w->close_sent || !w->conn) return;
    w->close_sent = true;
    /* A failed close frame still disposes the socket. */
    if (!write_frame(j->ws, 8, j->data.data, j->data.length, &io, &j->error)) j->failed = j->error.kind == NET_TIMEOUT;
}
static void close_done(network_state *s, net_job *job) {
    ws_job *j = (ws_job *)job;
    resource_close(s, &j->ws->base);
    ws_settle(s, j);
    ws_job_free(j);
}

void engine_websocket_close(network_state *s, websocket *ws, int code, const char *reason, aqua_pending *pending, const char *name) {
    ws_job *j = ws_job_new(ws, pending, name, close_run, close_done);
    uint8_t status[2] = {(uint8_t)(code >> 8), (uint8_t)code};
    buffer_append(&j->data, status, 2);
    buffer_append(&j->data, reason, strlen(reason));
    ws_start(s, j);
}
