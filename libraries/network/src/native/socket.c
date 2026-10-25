/* Sockets on Winsock and BSD sockets: non-blocking descriptors waited on in short slices so every blocking operation
 * honors its deadline, its cancellation flag and the runtime's stop flag; DNS on a helper thread for the same reason;
 * connections that read and write through TLS when it is on. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#include <mstcpip.h>
#else
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <unistd.h>
#endif

#define WAIT_SLICE_MS 50

#if defined(AQUA_OS_WINDOWS)
static pthread_once_t winsock_once = PTHREAD_ONCE_INIT;
static void winsock_start(void) { WSADATA data; WSAStartup(MAKEWORD(2, 2), &data); }
bool sockets_start(void) { pthread_once(&winsock_once, winsock_start); return true; }
void net_fd_close(net_fd fd) { if (fd != NET_NO_SOCKET) closesocket(fd); }
int net_last_error(void) { return WSAGetLastError(); }
bool net_would_block(int error) { return error == WSAEWOULDBLOCK || error == WSAEINPROGRESS || error == WSAEINTR; }
bool net_fd_nonblocking(net_fd fd) { u_long on = 1; return ioctlsocket(fd, FIONBIO, &on) == 0; }
net_fd net_socket(int family, int type, int protocol) {
    net_fd fd = WSASocketW(family, type, protocol, NULL, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT);
    if (fd != NET_NO_SOCKET && !net_fd_nonblocking(fd)) { closesocket(fd); return NET_NO_SOCKET; }
    return fd;
}
#define SEND_FLAGS 0
#else
bool sockets_start(void) { return true; }
void net_fd_close(net_fd fd) { if (fd >= 0) close(fd); }
int net_last_error(void) { return errno; }
bool net_would_block(int error) { return error == EAGAIN || error == EWOULDBLOCK || error == EINPROGRESS || error == EINTR; }
bool net_fd_nonblocking(net_fd fd) { int flags = fcntl(fd, F_GETFL, 0); return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0; }
net_fd net_socket(int family, int type, int protocol) {
    return socket(family, type | SOCK_NONBLOCK | SOCK_CLOEXEC, protocol);
}
#define SEND_FLAGS MSG_NOSIGNAL
#endif

net_io io_make(int32_t timeout_ms, const atomic_bool *cancel, net_kind cancel_kind, engine *e) {
    net_io io = {timeout_ms > 0 ? net_clock_ms() + timeout_ms : 0, cancel, cancel_kind, e ? engine_stopping(e) : NULL};
    return io;
}

bool io_check(const net_io *io, net_error *e) {
    if (io->stop && atomic_load(io->stop)) return net_error_set(e, NET_CLOSED, false, MSG_CLOSED);
    if (io->cancel && atomic_load(io->cancel))
        return net_error_set(e, io->cancel_kind, false, io->cancel_kind == NET_CANCELLED ? MSG_CANCELLED : MSG_CLOSED);
    if (io->deadline && net_clock_ms() >= io->deadline) return net_error_set(e, NET_TIMEOUT, true, MSG_TIMEOUT);
    return true;
}

bool net_wait(net_fd fd, int events, const net_io *io, net_error *e) {
    for (;;) {
        if (!io_check(io, e)) return false;
        int64_t slice = WAIT_SLICE_MS;
        if (io->deadline) { int64_t left = io->deadline - net_clock_ms(); if (left < slice) slice = left > 0 ? left : 0; }
#if defined(AQUA_OS_WINDOWS)
        /* select reports failed connects (exceptfds), which WSAPoll did not on older Windows. */
        fd_set readable, writable, failed;
        FD_ZERO(&readable); FD_ZERO(&writable); FD_ZERO(&failed);
        if (events & WAIT_READ) FD_SET(fd, &readable);
        if (events & WAIT_WRITE) FD_SET(fd, &writable);
        FD_SET(fd, &failed);
        struct timeval wait = {(long)(slice / 1000), (long)(slice % 1000) * 1000};
        int n = select(0, &readable, &writable, &failed, &wait);
        if (n > 0) return true;
        if (n < 0) { net_socket_error(e, net_last_error(), NULL); return false; }
#else
        struct pollfd p = {fd, (short)(((events & WAIT_READ) ? POLLIN : 0) | ((events & WAIT_WRITE) ? POLLOUT : 0)), 0};
        int n = poll(&p, 1, (int)slice);
        if (n > 0) return true;
        if (n < 0 && errno != EINTR) { net_socket_error(e, errno, NULL); return false; }
#endif
    }
}

void net_socket_error(net_error *e, int error, const char *context) {
    const char *text;
#if defined(AQUA_OS_WINDOWS)
    switch (error) {
    case WSAECONNREFUSED: text = "Connection refused / 連線遭拒。"; break;
    case WSAECONNRESET: text = "Connection reset by peer / 連線被對方重設。"; break;
    case WSAECONNABORTED: case WSAESHUTDOWN: case WSAENOTCONN: text = "Connection closed / 連線已關閉。"; break;
    case WSAETIMEDOUT: text = "Connection timed out / 連線逾時。"; break;
    case WSAEHOSTUNREACH: text = "Host unreachable / 無法連到主機。"; break;
    case WSAENETUNREACH: case WSAENETDOWN: text = "Network unreachable / 網路無法連線。"; break;
    case WSAEADDRINUSE: text = "Address already in use / 位址已被使用。"; break;
    case WSAEADDRNOTAVAIL: text = "Address not available / 位址無法使用。"; break;
    case WSAEACCES: text = "Permission denied / 權限不足。"; break;
    case WSAEMSGSIZE: text = "Message too long / 訊息過長。"; break;
    default: text = NULL;
    }
#else
    switch (error) {
    case ECONNREFUSED: text = "Connection refused / 連線遭拒。"; break;
    case ECONNRESET: text = "Connection reset by peer / 連線被對方重設。"; break;
    case ECONNABORTED: case EPIPE: case ESHUTDOWN: case ENOTCONN: text = "Connection closed / 連線已關閉。"; break;
    case ETIMEDOUT: text = "Connection timed out / 連線逾時。"; break;
    case EHOSTUNREACH: text = "Host unreachable / 無法連到主機。"; break;
    case ENETUNREACH: case ENETDOWN: text = "Network unreachable / 網路無法連線。"; break;
    case EADDRINUSE: text = "Address already in use / 位址已被使用。"; break;
    case EADDRNOTAVAIL: text = "Address not available / 位址無法使用。"; break;
    case EACCES: case EPERM: text = "Permission denied / 權限不足。"; break;
    case EMSGSIZE: text = "Message too long / 訊息過長。"; break;
    default: text = NULL;
    }
#endif
    char generic[64];
    if (!text) { snprintf(generic, sizeof generic, "Socket error %d / 通訊端錯誤 %d。", error, error); text = generic; }
    if (context) net_error_set(e, NET_NETWORK, true, "%s (%s)", text, context);
    else net_error_set(e, NET_NETWORK, true, "%s", text);
}

void net_format_address(const struct sockaddr *address, char *out, size_t size, bool with_port) {
    char host[INET6_ADDRSTRLEN] = "";
    int port = 0;
    if (address->sa_family == AF_INET6) {
        const struct sockaddr_in6 *a = (const struct sockaddr_in6 *)address;
        inet_ntop(AF_INET6, &a->sin6_addr, host, sizeof host);
        port = ntohs(a->sin6_port);
        if (with_port) snprintf(out, size, "[%s]:%d", host, port);
        else snprintf(out, size, "%s", host);
        return;
    }
    const struct sockaddr_in *a = (const struct sockaddr_in *)address;
    inet_ntop(AF_INET, &a->sin_addr, host, sizeof host);
    port = ntohs(a->sin_port);
    if (with_port) snprintf(out, size, "%s:%d", host, port);
    else snprintf(out, size, "%s", host);
}

/* ---- DNS ---- */

typedef struct dns_lookup {
    atomic_int refs;
    pthread_mutex_t lock;
    pthread_cond_t finished;
    bool done;
    int status;
    struct addrinfo hints, *result;
    char *host;
    char port[8];
} dns_lookup;

static void dns_unref(dns_lookup *d) {
    if (atomic_fetch_sub(&d->refs, 1) != 1) return;
    if (d->result) freeaddrinfo(d->result);
    pthread_mutex_destroy(&d->lock);
    pthread_cond_destroy(&d->finished);
    free(d->host);
    free(d);
}
static void *dns_thread(void *data) {
    dns_lookup *d = data;
    struct addrinfo *result = NULL;
    int status = getaddrinfo(d->host, d->port, &d->hints, &result);
    pthread_mutex_lock(&d->lock);
    d->status = status;
    d->result = result;
    d->done = true;
    pthread_cond_signal(&d->finished);
    pthread_mutex_unlock(&d->lock);
    dns_unref(d);
    platform_thread_exit();
    return NULL;
}

bool net_resolve(const char *host, int port, int socktype, bool passive, const net_io *io, struct addrinfo **out, net_error *e) {
    struct addrinfo hints = {0};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = socktype;
    hints.ai_flags = passive ? AI_PASSIVE : 0;
    char service[8];
    snprintf(service, sizeof service, "%d", port);
    /* Address literals need no lookup. */
    struct addrinfo numeric = hints;
    numeric.ai_flags |= AI_NUMERICHOST;
    if (getaddrinfo(host, service, &numeric, out) == 0) return true;
    dns_lookup *d = calloc(1, sizeof *d);
    atomic_init(&d->refs, 2);
    pthread_mutex_init(&d->lock, NULL);
    pthread_cond_init(&d->finished, NULL);
    d->hints = hints;
    d->host = net_strdup(host);
    memcpy(d->port, service, sizeof service);
    pthread_t thread;
    if (pthread_create(&thread, NULL, dns_thread, d) != 0) {
        atomic_store(&d->refs, 1);
        dns_unref(d);
        return net_error_set(e, NET_DNS, true, MSG_DNS);
    }
    pthread_detach(thread);
    bool ok = true;
    pthread_mutex_lock(&d->lock);
    while (!d->done) {
        if (!io_check(io, e)) { ok = false; break; }
        struct timespec at;
        clock_gettime(CLOCK_REALTIME, &at);
        at.tv_nsec += WAIT_SLICE_MS * 1000000L;
        if (at.tv_nsec >= 1000000000L) { at.tv_sec++; at.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(&d->finished, &d->lock, &at);
    }
    if (ok && (d->status != 0 || !d->result)) ok = net_error_set(e, NET_DNS, true, MSG_DNS);
    if (ok) { *out = d->result; d->result = NULL; }
    pthread_mutex_unlock(&d->lock);
    dns_unref(d);
    return ok;
}

#define CONNECT_ATTEMPTS 8
#define CONNECT_STAGGER_MS 250

/* Which of `fds` finished connecting (writable or failed), waiting at most `wait_ms`; -1 when none did. */
static int connect_ready(const net_fd *fds, int count, int64_t wait_ms) {
#if defined(AQUA_OS_WINDOWS)
    fd_set writable, failed;
    FD_ZERO(&writable); FD_ZERO(&failed);
    for (int i = 0; i < count; i++) if (fds[i] != NET_NO_SOCKET) { FD_SET(fds[i], &writable); FD_SET(fds[i], &failed); }
    struct timeval wait = {(long)(wait_ms / 1000), (long)(wait_ms % 1000) * 1000};
    if (select(0, NULL, &writable, &failed, &wait) <= 0) return -1;
    for (int i = 0; i < count; i++)
        if (fds[i] != NET_NO_SOCKET && (FD_ISSET(fds[i], &writable) || FD_ISSET(fds[i], &failed))) return i;
#else
    struct pollfd p[CONNECT_ATTEMPTS];
    for (int i = 0; i < count; i++) { p[i].fd = fds[i]; p[i].events = POLLOUT; p[i].revents = 0; }
    if (poll(p, (nfds_t)count, (int)wait_ms) <= 0) return -1;
    for (int i = 0; i < count; i++) if (fds[i] != NET_NO_SOCKET && p[i].revents) return i;
#endif
    return -1;
}

/* Connects to the resolved addresses in order, starting the next one when the previous has not answered within
   250 ms or failed (RFC 8305 "Happy Eyeballs"), so a name such as localhost whose first address refuses slowly
   (Windows retries refused connections for seconds) still connects at once. The first success wins. */
net_fd net_connect(const char *host, int port, const net_io *io, net_error *e) {
    struct addrinfo *list;
    if (!net_resolve(host, port, SOCK_STREAM, false, io, &list, e)) return NET_NO_SOCKET;
    char context[300];
    snprintf(context, sizeof context, strchr(host, ':') ? "[%s]:%d" : "%s:%d", host, port);
    net_error failure;
    net_error_set(&failure, NET_NETWORK, true, "Connection failed / 連線失敗。 (%s)", context);
    struct addrinfo *addresses[CONNECT_ATTEMPTS];
    int count = 0;
    for (struct addrinfo *a = list; a && count < CONNECT_ATTEMPTS; a = a->ai_next) addresses[count++] = a;
    net_fd fds[CONNECT_ATTEMPTS];
    for (int i = 0; i < count; i++) fds[i] = NET_NO_SOCKET;
    net_fd winner = NET_NO_SOCKET;
    int started = 0, pending = 0;
    int64_t next_start = net_clock_ms();
    while (winner == NET_NO_SOCKET) {
        if (!io_check(io, &failure)) break;
        int64_t now = net_clock_ms();
        if (started < count && (now >= next_start || pending == 0)) {
            struct addrinfo *a = addresses[started];
            net_fd fd = net_socket(a->ai_family, a->ai_socktype, a->ai_protocol);
            started++;
            next_start = now + CONNECT_STAGGER_MS;
            if (fd == NET_NO_SOCKET) { net_socket_error(&failure, net_last_error(), context); continue; }
            if (connect(fd, a->ai_addr, (int)a->ai_addrlen) == 0) { winner = fd; break; }
            int error = net_last_error();
            if (!net_would_block(error)) { net_socket_error(&failure, error, context); net_fd_close(fd); continue; }
            fds[started - 1] = fd;
            pending++;
        }
        if (pending == 0) { if (started >= count) break; continue; }
        int64_t wait = WAIT_SLICE_MS;
        if (started < count && next_start - now < wait) wait = next_start - now > 0 ? next_start - now : 0;
        if (io->deadline && io->deadline - now < wait) wait = io->deadline - now > 0 ? io->deadline - now : 0;
        int ready = connect_ready(fds, count, wait);
        if (ready < 0) continue;
        int status = 0;
        socklen_t length = sizeof status;
        getsockopt(fds[ready], SOL_SOCKET, SO_ERROR, (char *)&status, &length);
        if (status == 0) { winner = fds[ready]; fds[ready] = NET_NO_SOCKET; break; }
        net_socket_error(&failure, status, context);
        net_fd_close(fds[ready]);
        fds[ready] = NET_NO_SOCKET;
        pending--;
        next_start = now; /* a failure starts the next address at once */
    }
    for (int i = 0; i < count; i++) net_fd_close(fds[i]);
    freeaddrinfo(list);
    if (winner == NET_NO_SOCKET) *e = failure;
    return winner;
}

/* ---- Connections ---- */

net_conn *conn_new(net_fd fd) {
    net_conn *c = calloc(1, sizeof *c);
    c->fd = fd;
    net_mutex_init(&c->lock);
    struct sockaddr_storage address;
    socklen_t length = sizeof address;
    if (getsockname(fd, (struct sockaddr *)&address, &length) == 0) net_format_address((struct sockaddr *)&address, c->local, sizeof c->local, true);
    length = sizeof address;
    if (getpeername(fd, (struct sockaddr *)&address, &length) == 0) net_format_address((struct sockaddr *)&address, c->remote, sizeof c->remote, true);
    return c;
}

void conn_free(net_conn *c) {
    if (!c) return;
    if (c->tls) tls_free(c->tls);
    net_fd_close(c->fd);
    net_mutex_destroy(&c->lock);
    free(c);
}

long conn_read(net_conn *c, void *buffer, size_t size, const net_io *io, net_error *e) {
    if (size > 1 << 30) size = 1 << 30;
    for (;;) {
        if (!io_check(io, e)) return -1;
        int want;
        if (c->tls) {
            net_lock(&c->lock);
            int n = tls_recv(c->tls, buffer, size);
            net_unlock(&c->lock);
            if (n >= 0) return n;
            if (n == TLS_WANT_READ) want = WAIT_READ;
            else if (n == TLS_WANT_WRITE) want = WAIT_WRITE;
            else { net_error_set(e, NET_NETWORK, true, "TLS connection failed / TLS 連線失敗。"); return -1; }
        } else {
            long n = recv(c->fd, buffer, (int)size, 0);
            if (n >= 0) return n;
            int error = net_last_error();
            if (!net_would_block(error)) { net_socket_error(e, error, c->remote); return -1; }
            want = WAIT_READ;
        }
        if (!net_wait(c->fd, want, io, e)) return -1;
    }
}

bool conn_write(net_conn *c, const void *data, size_t length, const net_io *io, net_error *e) {
    const uint8_t *p = data;
    while (length > 0) {
        if (!io_check(io, e)) return false;
        size_t chunk = length > (1 << 20) ? (1 << 20) : length;
        int want;
        if (c->tls) {
            net_lock(&c->lock);
            int n = tls_send(c->tls, p, chunk);
            net_unlock(&c->lock);
            if (n > 0) { p += n; length -= (size_t)n; continue; }
            if (n == TLS_WANT_READ) want = WAIT_READ;
            else if (n == TLS_WANT_WRITE) want = WAIT_WRITE;
            else return net_error_set(e, NET_NETWORK, true, "TLS connection failed / TLS 連線失敗。");
        } else {
            long n = send(c->fd, (const char *)p, (int)chunk, SEND_FLAGS);
            if (n > 0) { p += n; length -= (size_t)n; continue; }
            int error = net_last_error();
            if (n < 0 && !net_would_block(error)) { net_socket_error(e, error, c->remote); return false; }
            want = WAIT_WRITE;
        }
        if (!net_wait(c->fd, want, io, e)) return false;
    }
    return true;
}

void conn_shutdown_write(net_conn *c) {
    if (c->tls) { net_lock(&c->lock); tls_notify_close(c->tls); net_unlock(&c->lock); }
#if defined(AQUA_OS_WINDOWS)
    shutdown(c->fd, SD_SEND);
#else
    shutdown(c->fd, SHUT_WR);
#endif
}

bool conn_idle_alive(net_conn *c) {
    if (c->tls && tls_pending(c->tls)) return false;
#if defined(AQUA_OS_WINDOWS)
    fd_set readable;
    FD_ZERO(&readable);
    FD_SET(c->fd, &readable);
    struct timeval none = {0, 0};
    return select(0, &readable, NULL, NULL, &none) == 0;
#else
    struct pollfd p = {c->fd, POLLIN, 0};
    return poll(&p, 1, 0) == 0;
#endif
}

static bool handshake(net_conn *c, const net_io *io, net_error *e) {
    for (;;) {
        int r = tls_handshake(c->tls);
        if (r == 0) return true;
        int want = r == TLS_WANT_READ ? WAIT_READ : r == TLS_WANT_WRITE ? WAIT_WRITE : 0;
        if (!want) return net_error_set(e, NET_TLS, false, MSG_TLS);
        if (!net_wait(c->fd, want, io, e)) return false;
    }
}

bool conn_tls_client(net_conn *c, const char *host, const char *alpn, const net_io *io, net_error *e) {
    c->tls = tls_client_start(c->fd, host, alpn, e);
    return c->tls && handshake(c, io, e);
}

bool conn_tls_server(net_conn *c, tls_server *server, const net_io *io, net_error *e) {
    c->tls = tls_server_start(c->fd, server, e);
    return c->tls && handshake(c, io, e);
}
