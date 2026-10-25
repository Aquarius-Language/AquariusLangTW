/* native.h — the engine of targets with sockets (Windows, Linux, Android): worker jobs and timers (hub.c), sockets
 * and connections (socket.c), Mbed TLS (tls.c), the HTTP/1.1 wire format (http.c) and what is built on them. Blocking
 * I/O runs on worker threads with deadlines and cancellation flags; results reach the runtime's thread with
 * aqua_post. The few OS-specific calls live in src/platform/<os>.c (platform_* below). */
#ifndef AQUA_NETWORK_NATIVE_H
#define AQUA_NETWORK_NATIVE_H

#if defined(_WIN32)
#if !defined(_WIN32_WINNT) || _WIN32_WINNT < 0x0A00
#undef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
typedef SOCKET net_fd;
#define NET_NO_SOCKET INVALID_SOCKET
#else
#include <netdb.h>
#include <netinet/in.h>
#include <sys/socket.h>
typedef int net_fd;
#define NET_NO_SOCKET (-1)
#endif
#include "../engine.h"

/* ---- Clock and platform services (src/platform/<os>.c). -------------------------------------------------------- */
static int64_t net_clock_ms(void);                 /* monotonic */
static bool platform_random(void *buffer, size_t length);
/* Adds the operating system's trusted root certificates (DER) through `add`. */
static bool platform_trusted_roots(void (*add)(void *context, const uint8_t *der, size_t length), void *context);
/* Android's network security policy for cleartext (http, ws) traffic to `host`; true elsewhere. */
static bool platform_cleartext_permitted(const char *host);
static void platform_thread_exit(void);            /* a worker thread ends (detaches it from the Java VM on Android) */
static void platform_runtime_start(void);          /* on the runtime's thread when the engine starts */

/* ---- Jobs and timers (hub.c). ------------------------------------------------------------------------------------- */
typedef struct net_job net_job;
struct net_job {
    void (*run)(net_job *job);                     /* on a worker thread */
    void (*done)(network_state *s, net_job *job);  /* on the runtime's thread afterwards; frees the job */
    void (*discard)(net_job *job);                 /* instead of done when the runtime ends first */
    engine *engine;
    net_job *queue_next, *previous, *next;
    bool finished;
};
/* Runs `job` on a worker thread (the pool grows while jobs wait for long I/O). */
static void job_start(engine *e, net_job *job);
static const atomic_bool *engine_stopping(engine *e);

/* ---- Sockets (socket.c). ------------------------------------------------------------------------------------------ */
/* How long and how a blocking operation may wait: until `deadline` (net_clock_ms, 0 = none), while neither flag is
   set. A set `cancel` reports `cancel_kind` (Cancelled for operations, Closed for resources). */
typedef struct net_io {
    int64_t deadline;
    const atomic_bool *cancel;
    net_kind cancel_kind;
    const atomic_bool *stop;
} net_io;
static net_io io_make(int32_t timeout_ms, const atomic_bool *cancel, net_kind cancel_kind, engine *e);
static bool io_check(const net_io *io, net_error *e);
enum { WAIT_READ = 1, WAIT_WRITE = 2 };
static bool net_wait(net_fd fd, int events, const net_io *io, net_error *e);

static bool sockets_start(void);
static void net_fd_close(net_fd fd);
static bool net_fd_nonblocking(net_fd fd);
static int net_last_error(void);
static bool net_would_block(int error);
/* A message for a socket error code: "Connection refused / 連線遭拒。" ... */
static void net_socket_error(net_error *e, int error, const char *context);
/* "ip:port", "[ipv6]:port"; address only when `with_port` is false. */
static void net_format_address(const struct sockaddr *address, char *out, size_t size, bool with_port);
/* DNS with the operation's deadline: getaddrinfo runs on a helper thread the caller stops waiting for. */
static bool net_resolve(const char *host, int port, int socktype, bool passive, const net_io *io, struct addrinfo **out, net_error *e);
/* A connected, non-blocking TCP socket to host:port (each resolved address in turn). */
static net_fd net_connect(const char *host, int port, const net_io *io, net_error *e);
/* A socket of `family`/`type`, non-blocking and not inherited by child processes. */
static net_fd net_socket(int family, int type, int protocol);

typedef struct tls_session tls_session;
typedef struct tls_server tls_server;
/* A TCP connection, optionally over TLS. One reader and one writer may use it at once (TLS state is locked). */
typedef struct net_conn {
    net_fd fd;
    tls_session *tls;
    net_mutex lock;
    char local[64], remote[64];
} net_conn;
static net_conn *conn_new(net_fd fd);
static void conn_free(net_conn *c);
/* > 0 bytes read, 0 at the end of the stream, < 0 on failure (`e` set). */
static long conn_read(net_conn *c, void *buffer, size_t size, const net_io *io, net_error *e);
static bool conn_write(net_conn *c, const void *data, size_t length, const net_io *io, net_error *e);
static void conn_shutdown_write(net_conn *c);
/* Idle pooled connections: false once the peer closed or sent something unexpected. */
static bool conn_idle_alive(net_conn *c);
static bool conn_tls_client(net_conn *c, const char *host, const char *alpn, const net_io *io, net_error *e);
static bool conn_tls_server(net_conn *c, tls_server *server, const net_io *io, net_error *e);

/* ---- TLS and crypto (tls.c, Mbed TLS). ---------------------------------------------------------------------------- */
enum { TLS_WANT_READ = -2, TLS_WANT_WRITE = -3, TLS_FAILED = -1, TLS_VERIFY_FAILED = -4 };
static tls_session *tls_client_start(net_fd fd, const char *host, const char *alpn, net_error *e);
static tls_session *tls_server_start(net_fd fd, tls_server *server, net_error *e);
static int tls_handshake(tls_session *s);
static int tls_recv(tls_session *s, void *buffer, size_t size);       /* > 0, 0 (close_notify), TLS_* */
static int tls_send(tls_session *s, const void *data, size_t length); /* > 0, TLS_* */
static bool tls_pending(tls_session *s);                              /* decrypted bytes already buffered */
static void tls_notify_close(tls_session *s);
static void tls_free(tls_session *s);
/* An HTTPS server identity from a PKCS#12 (.pfx/.p12) file, or PEM certificate chain and key. */
static tls_server *tls_server_load(const char *path, const char *password, net_error *e);
static void tls_server_free(tls_server *s);
static void net_sha1(const void *data, size_t length, uint8_t digest[20]);
static void net_sha256(const void *data, size_t length, uint8_t digest[32]);
static void net_base64(const uint8_t *data, size_t length, net_buffer *out);

/* ---- HTTP/1.1 messages (http.c). ---------------------------------------------------------------------------------- */
#define HTTP_HEAD_LIMIT (64 * 1024)
typedef struct http_head {
    char *method, *target;  /* requests */
    int status;             /* responses */
    int minor;              /* HTTP/1.minor */
    net_headers headers;
} http_head;
static void http_head_free(http_head *h);
/* Reads into `in` until it holds a complete head, parses it and drops it from `in`. Returns 1, 0 when the peer
   closed before sending anything, -1 on failure (`e`). */
static int http_read_head(net_conn *c, net_buffer *in, bool request, http_head *out, const net_io *io, net_error *e);
/* Message bodies: Content-Length, chunked, or (responses) until the connection closes. */
typedef struct http_body {
    enum { BODY_NONE, BODY_LENGTH, BODY_CHUNKED, BODY_CLOSE } mode;
    uint64_t remaining;      /* bytes left of the body (LENGTH) or of the current chunk (CHUNKED) */
    int chunk_state;         /* CHUNKED: 0 size line, 1 data, 2 CRLF after data, 3 trailers, 4 done */
    bool finished;
} http_body;
/* Framing of a request (`request`) or of the response to `method` with `status`; false for invalid framing. */
static bool http_body_start(http_body *b, const http_head *h, bool request, const char *method);
/* Appends the next body bytes to `out` (taking buffered bytes from `in` first). > 0 appended, 0 finished, -1 failure. */
static long http_body_read(http_body *b, net_conn *c, net_buffer *in, net_buffer *out, const net_io *io, net_error *e);
/* Writes an HTTP date (IMF-fixdate) for `seconds` since the epoch. */
static void http_date(char out[32], int64_t seconds);

#define WS_GUID "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" /* RFC 6455 handshake */

/* ---- WebSocket server ends (frames.c): an upgraded HTTP connection that `owner` keeps alive. ----------------- */
static websocket *websocket_server_new(network_state *s, net_conn *conn, net_buffer *leftover, void *owner, void (*release_owner)(void *owner), const net_options *o);

/* ---- Content-Encoding gzip and deflate (inflate.c). ---------------------------------------------------------------- */
enum { INFLATE_GZIP, INFLATE_ZLIB_OR_RAW, INFLATE_RAW };
typedef struct inflater inflater;
static inflater *inflate_new(int format);
/* Appends what `data` completes to `out`. */
static bool inflate_feed(inflater *z, const uint8_t *data, size_t length, net_buffer *out, net_error *e);
/* The body ended: false when the compressed stream did not. */
static bool inflate_end(inflater *z, net_error *e);
static void inflate_free(inflater *z);

/* ---- Cookie containers (cookies.c). ------------------------------------------------------------------------------- */
typedef struct cookie_jar cookie_jar;
static cookie_jar *jar_new(void);
static void jar_free(cookie_jar *j);
static void jar_clear(cookie_jar *j);
/* Stores one Set-Cookie header received from `url`. */
static void jar_store(cookie_jar *j, const net_url *url, const char *set_cookie);
/* The Cookie header value for a request to `url`, or NULL. */
static char *jar_header(cookie_jar *j, const net_url *url);

#endif
