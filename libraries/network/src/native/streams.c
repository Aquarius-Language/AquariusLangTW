/* Raw sockets for scripts: Resolve, ConnectTCP/ConnectTLS streams, ListenTCP listeners and OpenUDP datagrams, with
 * the old adapters' limits (reads 1..16 MiB, datagrams up to 65507 bytes, waits 0..300000 ms) and messages. Blocking
 * calls are jobs the script waits for; Accept(0) and the socket options answer at once. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#include <mstcpip.h>
#if !defined(SIO_UDP_CONNRESET)
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif
#else
#include <arpa/inet.h>
#include <netinet/tcp.h>
#endif

#define MAX_DATAGRAM 65507
#define MSG_NO_HOST "No such host is known / 找不到主機。"

/* ---- Socket resources ---- */

typedef struct socket_resource {
    resource base;
    atomic_int refs;
    atomic_bool closed;
    net_conn *conn;        /* streams */
    net_fd fd;             /* listeners and datagrams */
    int32_t timeout_ms;
    int family;
    engine *engine;
    uint32_t busy;         /* jobs using the socket (runtime thread) */
} socket_resource;

/* The socket itself closes once no job uses it, so a closed listener stops accepting at once. */
static void release_io(socket_resource *k) {
    if (k->conn) { conn_free(k->conn); k->conn = NULL; }
    else if (k->fd != NET_NO_SOCKET) { net_fd_close(k->fd); k->fd = NET_NO_SOCKET; }
}

static void socket_close(network_state *s, resource *r) {
    (void)s;
    socket_resource *k = (socket_resource *)r;
    atomic_store(&k->closed, true);
    net_fd fd = k->conn ? k->conn->fd : k->fd;
#if defined(AQUA_OS_WINDOWS)
    shutdown(fd, SD_BOTH);
#else
    shutdown(fd, SHUT_RDWR);
#endif
    if (k->busy == 0) release_io(k);
}
static void socket_unref(socket_resource *k) {
    if (atomic_fetch_sub(&k->refs, 1) != 1) return;
    release_io(k);
    free(k);
}
static socket_resource *socket_new(network_state *s, net_conn *conn, net_fd fd, int32_t timeout_ms) {
    socket_resource *k = calloc(1, sizeof *k);
    atomic_init(&k->refs, 1);
    atomic_init(&k->closed, false);
    k->base.close = socket_close;
    k->base.state = s;
    k->conn = conn;
    k->fd = conn ? NET_NO_SOCKET : fd;
    k->timeout_ms = timeout_ms;
    k->engine = s->engine;
    return k;
}
static void socket_release(void *payload) {
    socket_resource *k = payload;
    if (k->base.open) resource_close(k->base.state, &k->base);
    socket_unref(k);
}
void stream_release(aqua_runtime *rt, void *payload) { (void)rt; socket_release(payload); }
void listener_release(aqua_runtime *rt, void *payload) { (void)rt; socket_release(payload); }
void datagram_release(aqua_runtime *rt, void *payload) { (void)rt; socket_release(payload); }

static net_io socket_wait_io(socket_resource *k, int32_t timeout_ms) { return io_make(timeout_ms, &k->closed, NET_CLOSED, k->engine); }

static bool stream_live(aqua_call *call, const char *name, socket_resource *k) {
    if (k->base.open) return true;
    net_raise_kind(call, name, NET_CLOSED, MSG_CLOSED);
    return false;
}

/* A tracked Stream object for a connection; NULL after failing `pending` (resource limit). */
static bool stream_object(network_state *s, net_conn *conn, int32_t timeout_ms, aqua_value *out, net_error *e) {
    socket_resource *k = socket_new(s, conn, NET_NO_SOCKET, timeout_ms);
    if (!resource_own(s, &k->base, e)) { socket_unref(k); return false; }
    net_object *o = object_create(CLASS_stream, k);
    object_field(s->rt, o, FIELD_OF(stream, local_address), aqua_cstring(s->rt, conn->local));
    object_field(s->rt, o, FIELD_OF(stream, remote_address), aqua_cstring(s->rt, conn->remote));
    *out = object_wrap(s->rt, o);
    return true;
}

/* ---- Jobs ---- */

typedef enum socket_operation { OP_RESOLVE, OP_CONNECT, OP_READ, OP_WRITE, OP_ACCEPT, OP_SEND, OP_RECEIVE } socket_operation;
typedef struct socket_job {
    net_job job;
    socket_operation op;
    socket_resource *socket;
    aqua_pending *pending;
    const char *name;
    net_error error;
    bool failed, text, tls;
    char *host;
    int port;
    int32_t size, timeout_ms;
    net_buffer data;
    net_conn *conn;              /* connect, accept */
    char address[64];            /* receive */
    int peer_port;
    char **addresses; uint32_t address_count;
    atomic_bool cancel;          /* resolve and connect have no socket to close */
} socket_job;

static void socket_job_free(socket_job *j) {
    if (j->socket) {
        if (--j->socket->busy == 0 && atomic_load(&j->socket->closed)) release_io(j->socket);
        socket_unref(j->socket);
    }
    if (j->conn) conn_free(j->conn);
    for (uint32_t i = 0; i < j->address_count; i++) free(j->addresses[i]);
    free(j->addresses);
    free(j->host);
    buffer_free(&j->data);
    free(j);
}
static void socket_job_discard(net_job *job) { socket_job_free((socket_job *)job); }

static void socket_run(net_job *job) {
    socket_job *j = (socket_job *)job;
    net_error *e = &j->error;
    socket_resource *k = j->socket;
    j->failed = true;
    switch (j->op) {
    case OP_RESOLVE: {
        net_io io = io_make(10000, &j->cancel, NET_CANCELLED, job->engine);
        struct addrinfo *list;
        if (!net_resolve(j->host, 0, SOCK_STREAM, false, &io, &list, e)) {
            if (e->kind == NET_DNS) net_error_set(e, NET_NETWORK, true, "%s (%s)", MSG_NO_HOST, j->host);
            return;
        }
        for (struct addrinfo *a = list; a; a = a->ai_next) {
            char text[64];
            net_format_address(a->ai_addr, text, sizeof text, false);
            bool seen = false;
            for (uint32_t i = 0; i < j->address_count; i++) if (!strcmp(j->addresses[i], text)) seen = true;
            if (seen) continue;
            j->addresses = realloc(j->addresses, (j->address_count + 1) * sizeof *j->addresses);
            j->addresses[j->address_count++] = net_strdup(text);
        }
        freeaddrinfo(list);
        break;
    }
    case OP_CONNECT: {
        net_io io = io_make(j->timeout_ms, &j->cancel, NET_CANCELLED, job->engine);
        net_fd fd = net_connect(j->host, j->port, &io, e);
        if (fd == NET_NO_SOCKET) {
            if (e->kind == NET_DNS) net_error_set(e, NET_NETWORK, true, "%s (%s)", MSG_NO_HOST, j->host);
            return;
        }
        j->conn = conn_new(fd);
        if (j->tls && !conn_tls_client(j->conn, j->host, NULL, &io, e)) return;
        break;
    }
    case OP_READ: {
        net_io io = socket_wait_io(k, k->timeout_ms);
        if (!buffer_reserve(&j->data, (size_t)j->size)) { net_error_set(e, NET_LIMIT, false, MSG_READ_SIZE); return; }
        long n = conn_read(k->conn, j->data.data, (size_t)j->size, &io, e);
        if (n < 0) return;
        j->data.length = (size_t)n;
        break;
    }
    case OP_WRITE: {
        net_io io = socket_wait_io(k, k->timeout_ms);
        if (!conn_write(k->conn, j->data.data, j->data.length, &io, e)) return;
        break;
    }
    case OP_ACCEPT: {
        net_io io = socket_wait_io(k, j->timeout_ms);
        for (;;) {
            if (!net_wait(k->fd, WAIT_READ, &io, e)) {
                if (e->kind == NET_TIMEOUT) { j->failed = false; return; } /* no connection: null */
                return;
            }
            net_fd fd = accept(k->fd, NULL, NULL);
            if (fd == NET_NO_SOCKET) {
                if (net_would_block(net_last_error())) continue;
                net_socket_error(e, net_last_error(), NULL);
                return;
            }
            net_fd_nonblocking(fd);
            j->conn = conn_new(fd);
            break;
        }
        break;
    }
    case OP_SEND: {
        net_io io = socket_wait_io(k, k->timeout_ms);
        struct addrinfo *list;
        if (!net_resolve(j->host, j->port, SOCK_DGRAM, false, &io, &list, e)) {
            if (e->kind == NET_DNS) net_error_set(e, NET_NETWORK, true, "%s (%s)", MSG_NO_HOST, j->host);
            return;
        }
        struct addrinfo *target = NULL;
        for (struct addrinfo *a = list; a && !target; a = a->ai_next) if (a->ai_family == k->family) target = a;
        if (!target) { freeaddrinfo(list); net_error_set(e, NET_NETWORK, false, "Address family not supported / 位址類型不相符。 (%s)", j->host); return; }
        for (;;) {
            long n = sendto(k->fd, (const char *)j->data.data, (int)j->data.length, 0, target->ai_addr, (int)target->ai_addrlen);
            if (n >= 0) break;
            int error = net_last_error();
            if (!net_would_block(error)) { net_socket_error(e, error, j->host); freeaddrinfo(list); return; }
            if (!net_wait(k->fd, WAIT_WRITE, &io, e)) { freeaddrinfo(list); return; }
        }
        freeaddrinfo(list);
        break;
    }
    case OP_RECEIVE: {
        net_io io = socket_wait_io(k, k->timeout_ms);
        buffer_reserve(&j->data, 65536);
        for (;;) {
            struct sockaddr_storage from;
            socklen_t length = sizeof from;
            long n = recvfrom(k->fd, (char *)j->data.data, 65536, 0, (struct sockaddr *)&from, &length);
            if (n >= 0) {
                if (n > j->size) { net_error_set(e, NET_LIMIT, false, "LimitExceeded / 資料報超過讀取上限。"); return; }
                j->data.length = (size_t)n;
                net_format_address((struct sockaddr *)&from, j->address, sizeof j->address, false);
                j->peer_port = ntohs(from.ss_family == AF_INET6 ? ((struct sockaddr_in6 *)&from)->sin6_port : ((struct sockaddr_in *)&from)->sin_port);
                break;
            }
            int error = net_last_error();
            if (!net_would_block(error)) { net_socket_error(e, error, NULL); return; }
            if (!net_wait(k->fd, WAIT_READ, &io, e)) return;
        }
        break;
    }
    }
    j->failed = false;
}

static void socket_done(network_state *s, net_job *job) {
    socket_job *j = (socket_job *)job;
    aqua_runtime *rt = s->rt;
    if (j->failed) { net_reject(j->pending, j->name, &j->error); socket_job_free(j); return; }
    aqua_value result = aqua_null();
    net_error e;
    switch (j->op) {
    case OP_RESOLVE:
        result = aqua_array_new(rt, 0);
        for (uint32_t i = 0; i < j->address_count; i++) aqua_array_push(rt, result, aqua_cstring(rt, j->addresses[i]));
        break;
    case OP_CONNECT: case OP_ACCEPT:
        if (j->conn) {
            int32_t timeout = j->op == OP_ACCEPT ? j->socket->timeout_ms : j->timeout_ms;
            net_conn *conn = j->conn;
            j->conn = NULL;
            if (!stream_object(s, conn, timeout, &result, &e)) { net_reject(j->pending, j->name, &e); socket_job_free(j); return; }
        }
        break;
    case OP_READ:
        result = j->text ? aqua_text(rt, j->data.length ? (const char *)j->data.data : "", j->data.length) : bytes_value(rt, j->data.data, j->data.length);
        break;
    case OP_RECEIVE: {
        net_object *o = object_create(CLASS_packet, NULL);
        object_field(rt, o, FIELD_OF(packet, data), bytes_value(rt, j->data.data, j->data.length));
        object_field(rt, o, FIELD_OF(packet, text), aqua_text(rt, j->data.length ? (const char *)j->data.data : "", j->data.length));
        object_field(rt, o, FIELD_OF(packet, address), aqua_cstring(rt, j->address));
        object_field(rt, o, FIELD_OF(packet, port), aqua_int(j->peer_port));
        result = object_wrap(rt, o);
        break;
    }
    default: break;
    }
    aqua_resolve(j->pending, result);
    socket_job_free(j);
}

static socket_job *socket_job_new(socket_operation op, socket_resource *k, const char *name) {
    socket_job *j = calloc(1, sizeof *j);
    j->op = op;
    j->name = name;
    atomic_init(&j->cancel, false);
    if (k) { atomic_fetch_add(&k->refs, 1); k->busy++; j->socket = k; }
    j->job.run = socket_run;
    j->job.done = socket_done;
    j->job.discard = socket_job_discard;
    return j;
}
static void socket_job_go(aqua_call *call, socket_job *j) {
    j->pending = aqua_defer(call);
    job_start(((network_state *)aqua_state(call))->engine, &j->job);
}

/* ---- Module functions ---- */

void net_resolve_host(aqua_call *call, const char *name) {
    socket_job *j = socket_job_new(OP_RESOLVE, NULL, name);
    j->host = net_strdup(aqua_arg_str(call, 0).data);
    socket_job_go(call, j);
}

static void connect_stream(aqua_call *call, const char *name, bool tls) {
    net_error e;
    net_options o;
    int32_t port = aqua_arg_int(call, 1);
    if (!port_valid(port, false, &e)) { net_raise(call, name, &e); return; }
    if (!options_read(call, 2, NULL, &o, &e)) { options_free(&o); net_raise(call, name, &e); return; }
    socket_job *j = socket_job_new(OP_CONNECT, NULL, name);
    j->host = net_strdup(aqua_arg_str(call, 0).data);
    j->port = port;
    j->tls = tls;
    j->timeout_ms = o.timeout_ms;
    options_free(&o);
    socket_job_go(call, j);
}
void net_connect_tcp(aqua_call *call, const char *name) { connect_stream(call, name, false); }
void net_connect_tls(aqua_call *call, const char *name) { connect_stream(call, name, true); }

/* Binds a socket to an IP literal and port (0: the system chooses). */
static net_fd bind_socket(const char *address, int port, int type, int *family, net_error *e) {
    struct addrinfo hints = {0}, *list;
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = type;
    hints.ai_flags = AI_NUMERICHOST | AI_PASSIVE;
    char service[8];
    snprintf(service, sizeof service, "%d", port);
    if (getaddrinfo(address, service, &hints, &list) != 0) { net_error_set(e, NET_INVALID, false, "Invalid IP address / IP 位址無效：%s", address); return NET_NO_SOCKET; }
    net_fd fd = net_socket(list->ai_family, type, 0);
    if (fd == NET_NO_SOCKET) { net_socket_error(e, net_last_error(), address); freeaddrinfo(list); return NET_NO_SOCKET; }
    int on = 1;
#if defined(AQUA_OS_WINDOWS)
    setsockopt(fd, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&on, sizeof on);
#else
    if (type == SOCK_STREAM) setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, sizeof on);
#endif
    if (list->ai_family == AF_INET6) setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, (const char *)&on, sizeof on);
    if (bind(fd, list->ai_addr, (int)list->ai_addrlen) != 0) {
        char context[96];
        snprintf(context, sizeof context, strchr(address, ':') ? "[%s]:%d" : "%s:%d", address, port);
        net_socket_error(e, net_last_error(), context);
        net_fd_close(fd);
        freeaddrinfo(list);
        return NET_NO_SOCKET;
    }
    *family = list->ai_family;
    freeaddrinfo(list);
    return fd;
}

static void local_address(net_fd fd, char *out, size_t size) {
    struct sockaddr_storage address;
    socklen_t length = sizeof address;
    out[0] = 0;
    if (getsockname(fd, (struct sockaddr *)&address, &length) == 0) net_format_address((struct sockaddr *)&address, out, size, true);
}

static void open_socket(aqua_call *call, const char *name, int type) {
    network_state *s = aqua_state(call);
    net_error e;
    net_options o;
    int32_t port = aqua_arg_int(call, 1);
    if (!port_valid(port, true, &e)) { net_raise(call, name, &e); return; }
    if (!options_read(call, 2, NULL, &o, &e)) { options_free(&o); net_raise(call, name, &e); return; }
    int32_t timeout = o.timeout_ms;
    options_free(&o);
    int family = AF_INET;
    net_fd fd = bind_socket(aqua_arg_str(call, 0).data, port, type, &family, &e);
    if (fd == NET_NO_SOCKET) { net_raise(call, name, &e); return; }
    if (type == SOCK_STREAM && listen(fd, 128) != 0) { net_socket_error(&e, net_last_error(), NULL); net_fd_close(fd); net_raise(call, name, &e); return; }
#if defined(AQUA_OS_WINDOWS)
    if (type == SOCK_DGRAM) {
        /* An ICMP "port unreachable" must not fail later receives. */
        BOOL off = FALSE;
        DWORD bytes = 0;
        WSAIoctl(fd, SIO_UDP_CONNRESET, &off, sizeof off, NULL, 0, &bytes, NULL, NULL);
    }
#endif
    socket_resource *k = socket_new(s, NULL, fd, timeout);
    k->family = family;
    if (!resource_own(s, &k->base, &e)) { socket_unref(k); net_raise(call, name, &e); return; }
    char address[64];
    local_address(fd, address, sizeof address);
    net_object *object = object_create(type == SOCK_STREAM ? CLASS_listener : CLASS_datagram, k);
    object_field(s->rt, object, type == SOCK_STREAM ? FIELD_OF(listener, local_address) : FIELD_OF(datagram, local_address), aqua_cstring(s->rt, address));
    aqua_return(call, object_wrap(s->rt, object));
}
void net_listen_tcp(aqua_call *call, const char *name) { open_socket(call, name, SOCK_STREAM); }
void net_open_udp(aqua_call *call, const char *name) { open_socket(call, name, SOCK_DGRAM); }

/* ---- Streams ---- */

#define SELF_RESOURCE(call) ((socket_resource *)self_payload(call))

static void read_stream(aqua_call *call, const char *name, bool text) {
    socket_resource *k = SELF_RESOURCE(call);
    int32_t size = aqua_arg_int(call, 0);
    if (size < 1 || size > NET_MAX_BODY) { net_raise_kind(call, name, NET_INVALID, MSG_READ_SIZE); return; }
    if (!stream_live(call, name, k)) return;
    socket_job *j = socket_job_new(OP_READ, k, name);
    j->size = size;
    j->text = text;
    socket_job_go(call, j);
}
void stream_read(aqua_call *call, const char *name) { read_stream(call, name, false); }
void stream_read_text(aqua_call *call, const char *name) { read_stream(call, name, true); }

static void write_stream(aqua_call *call, const char *name, net_buffer *bytes) {
    socket_resource *k = SELF_RESOURCE(call);
    if (!stream_live(call, name, k)) return;
    if (bytes->length > NET_MAX_BODY) { net_raise_kind(call, name, NET_INVALID, "Write too large"); return; }
    socket_job *j = socket_job_new(OP_WRITE, k, name);
    j->data = *bytes;
    memset(bytes, 0, sizeof *bytes);
    socket_job_go(call, j);
}
void stream_write(aqua_call *call, const char *name) {
    net_buffer bytes = {0};
    net_error e;
    if (!value_bytes(aqua_call_runtime(call), aqua_arg(call, 0), &bytes, &e)) net_raise(call, name, &e);
    else write_stream(call, name, &bytes);
    buffer_free(&bytes);
}
void stream_write_text(aqua_call *call, const char *name) {
    aqua_str text = aqua_arg_str(call, 0);
    net_buffer bytes = {0};
    buffer_append(&bytes, text.data, text.length);
    write_stream(call, name, &bytes);
    buffer_free(&bytes);
}
void stream_shutdown_write(aqua_call *call, const char *name) {
    socket_resource *k = SELF_RESOURCE(call);
    if (stream_live(call, name, k)) conn_shutdown_write(k->conn);
}
void stream_close(aqua_call *call, const char *name) { (void)name; resource_close(aqua_state(call), &SELF_RESOURCE(call)->base); }

/* ---- Listeners ---- */

void listener_accept(aqua_call *call, const char *name) {
    socket_resource *k = SELF_RESOURCE(call);
    int32_t timeout = aqua_has_arg(call, 0) ? aqua_arg_int(call, 0) : 10000;
    if (timeout < 0 || timeout > 300000) { net_raise_kind(call, name, NET_INVALID, MSG_WAIT); return; }
    if (!stream_live(call, name, k)) return;
    if (timeout == 0) {
        /* Poll: a pending connection or null right away. */
        net_fd fd = accept(k->fd, NULL, NULL);
        if (fd == NET_NO_SOCKET) { aqua_return_null(call); return; }
        net_fd_nonblocking(fd);
        aqua_value stream;
        net_error e;
        if (!stream_object(aqua_state(call), conn_new(fd), k->timeout_ms, &stream, &e)) { net_raise(call, name, &e); return; }
        aqua_return(call, stream);
        return;
    }
    socket_job *j = socket_job_new(OP_ACCEPT, k, name);
    j->timeout_ms = timeout;
    socket_job_go(call, j);
}
void listener_close(aqua_call *call, const char *name) { (void)name; resource_close(aqua_state(call), &SELF_RESOURCE(call)->base); }

/* ---- Datagrams ---- */

void datagram_send(aqua_call *call, const char *name) {
    socket_resource *k = SELF_RESOURCE(call);
    net_error e;
    int32_t port = aqua_arg_int(call, 1);
    if (!port_valid(port, false, &e)) { net_raise(call, name, &e); return; }
    if (!stream_live(call, name, k)) return;
    socket_job *j = socket_job_new(OP_SEND, k, name);
    if (!value_payload(aqua_call_runtime(call), aqua_arg(call, 2), &j->data, &e)) { socket_job_free(j); net_raise(call, name, &e); return; }
    if (j->data.length > MAX_DATAGRAM) { socket_job_free(j); net_raise_kind(call, name, NET_INVALID, "Datagram too large / 資料報過大。"); return; }
    j->host = net_strdup(aqua_arg_str(call, 0).data);
    j->port = port;
    socket_job_go(call, j);
}
void datagram_receive(aqua_call *call, const char *name) {
    socket_resource *k = SELF_RESOURCE(call);
    int32_t size = aqua_has_arg(call, 0) ? aqua_arg_int(call, 0) : MAX_DATAGRAM;
    if (size < 1 || size > 65535) { net_raise_kind(call, name, NET_INVALID, MSG_READ_SIZE); return; }
    if (!stream_live(call, name, k)) return;
    socket_job *j = socket_job_new(OP_RECEIVE, k, name);
    j->size = size;
    socket_job_go(call, j);
}

static void membership(aqua_call *call, const char *name, bool join) {
    socket_resource *k = SELF_RESOURCE(call);
    if (!stream_live(call, name, k)) return;
    const char *group = aqua_arg_str(call, 0).data;
    int result = -1;
    struct in_addr v4;
    struct in6_addr v6;
    if (inet_pton(AF_INET, group, &v4) == 1) {
        struct ip_mreq request = {0};
        request.imr_multiaddr = v4;
        request.imr_interface.s_addr = htonl(INADDR_ANY);
        result = setsockopt(k->fd, IPPROTO_IP, join ? IP_ADD_MEMBERSHIP : IP_DROP_MEMBERSHIP, (const char *)&request, sizeof request);
    } else if (inet_pton(AF_INET6, group, &v6) == 1) {
        struct ipv6_mreq request = {0};
        request.ipv6mr_multiaddr = v6;
        result = setsockopt(k->fd, IPPROTO_IPV6, join ? IPV6_JOIN_GROUP : IPV6_LEAVE_GROUP, (const char *)&request, sizeof request);
    } else { net_raise_kind(call, name, NET_INVALID, "Invalid IP address / IP 位址無效。"); return; }
    if (result != 0) {
        net_error e;
        net_socket_error(&e, net_last_error(), group);
        net_raise(call, name, &e);
    }
}
void datagram_join(aqua_call *call, const char *name) { membership(call, name, true); }
void datagram_leave(aqua_call *call, const char *name) { membership(call, name, false); }
void datagram_broadcast(aqua_call *call, const char *name) {
    socket_resource *k = SELF_RESOURCE(call);
    if (!stream_live(call, name, k)) return;
    int on = aqua_arg_bool(call, 0) ? 1 : 0;
    if (setsockopt(k->fd, SOL_SOCKET, SO_BROADCAST, (const char *)&on, sizeof on) != 0) {
        net_error e;
        net_socket_error(&e, net_last_error(), NULL);
        net_raise(call, name, &e);
    }
}
void datagram_close(aqua_call *call, const char *name) { (void)name; resource_close(aqua_state(call), &SELF_RESOURCE(call)->base); }
