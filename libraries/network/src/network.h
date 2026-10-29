/* network.h — internal declarations of the Network|網路 library.
 *
 * Shared code (this folder) owns the language-facing API on every target: registration, argument and option
 * validation, records, JSON, URLs, the HTTP client's bookkeeping, WebSocket message handling and error messages.
 * An engine moves the bytes: src/native (sockets, Mbed TLS, worker threads) on Windows, Linux and Android, and
 * src/web (the browser's fetch and WebSocket through web-glue/network) on the web. engine.h is the boundary.
 *
 * 網路函式庫內部宣告：共用程式碼負責語言介面、驗證與協定邏輯，原生或網頁引擎負責實際 I/O。 */
#ifndef AQUA_NETWORK_H
#define AQUA_NETWORK_H

#include <aqua/aqua.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* The library is one translation unit (src/network_native.c, src/network_web.c): shared helpers are static, and a
   target that does not need one of them (the web has no server) leaves it unused without a warning. */
#define NET_SHARED static __attribute__((unused))

#define NET_MAX_BODY (16 * 1024 * 1024)   /* request, response, message and unread-stream limit */
#define NET_MAX_BYTES (64 * 1024 * 1024)  /* largest byte array a script may pass */
#define NET_RESOURCE_LIMIT 1024           /* open clients, sockets, operations and servers per runtime */
#define NET_JSON_DEPTH 64

/* ---- Locks: worker threads share progress with the runtime thread natively; the web has one thread. ---------- */
#if defined(AQUA_OS_WEB)
typedef struct net_mutex { int unused; } net_mutex;
static inline void net_mutex_init(net_mutex *m) { (void)m; }
static inline void net_mutex_destroy(net_mutex *m) { (void)m; }
static inline void net_lock(net_mutex *m) { (void)m; }
static inline void net_unlock(net_mutex *m) { (void)m; }
#else
#include <pthread.h>
typedef pthread_mutex_t net_mutex;
static inline void net_mutex_init(net_mutex *m) { pthread_mutex_init(m, NULL); }
static inline void net_mutex_destroy(net_mutex *m) { pthread_mutex_destroy(m); }
static inline void net_lock(net_mutex *m) { pthread_mutex_lock(m); }
static inline void net_unlock(net_mutex *m) { pthread_mutex_unlock(m); }
#endif

/* ---- Errors: the kinds scripts see in Error.Kind / 錯誤.種類 (NetworkRuntime.Failure). -------------------------- */
#define NET_ERROR_KINDS(X) \
    X(NET_OK, "") X(NET_NETWORK, "Network") X(NET_TIMEOUT, "Timeout") X(NET_CANCELLED, "Cancelled") \
    X(NET_CLOSED, "Closed") X(NET_DNS, "DNS") X(NET_TLS, "TLS") X(NET_INVALID, "InvalidArgument") \
    X(NET_LIMIT, "LimitExceeded") X(NET_UNSUPPORTED, "Unsupported") X(NET_HTTP, "HTTP") X(NET_FAILED, "OperationFailed")
typedef enum net_kind {
#define X(id, name) id,
    NET_ERROR_KINDS(X)
#undef X
} net_kind;
typedef struct net_error { net_kind kind; bool retryable; char message[480]; } net_error;
NET_SHARED const char *net_kind_name(net_kind kind);
NET_SHARED const char *net_status_text(int status); /* "Not Found" for 404, "" for unknown codes */
/* Sets the error and returns false, so validation reads `return net_error_set(e, ...);`. */
NET_SHARED bool net_error_set(net_error *e, net_kind kind, bool retryable, const char *format, ...);
/* Fails a call as the old runtime did: "<Library.Function>: <Kind>: <message>". */
NET_SHARED void net_raise(aqua_call *call, const char *name, const net_error *e);
NET_SHARED void net_reject(aqua_pending *pending, const char *name, const net_error *e);
NET_SHARED void net_raise_kind(aqua_call *call, const char *name, net_kind kind, const char *message);
/* The library's fixed messages, shared by every target. */
#define MSG_CLOSED "Resource closed / 資源已關閉。"
#define MSG_TIMEOUT "Operation timed out or cancelled / 操作逾時或已取消。"
#define MSG_CANCELLED "Request cancelled / 請求已取消。"
#define MSG_DNS "Host resolution failed / 主機解析失敗。"
#define MSG_TLS "TLS validation failed / TLS 驗證失敗。"
#define MSG_OPTIONS "InvalidArgument / 參數無效：逾時 1..300000 ms、本文上限 1..16777216、重新導向 0..20。"
#define MSG_HEADER "Invalid HTTP header / 無效的 HTTP 標頭。"
#define MSG_PORT "Invalid port / 連接埠必須介於 1..65535。"
#define MSG_HASH "Expected options hash / 請傳入雜湊表。"
#define MSG_STRING "Expected a string."
#define MSG_BYTES "Expected a byte array / 需要位元組陣列。"
#define MSG_READ_SIZE "Invalid read size / 讀取大小無效。"
#define MSG_WAIT "Invalid timeout / 逾時須介於 0..300000。"
#define MSG_HTTP_URL "Expected HTTP(S) URL / 需要 HTTP(S) 網址。"
#define MSG_WS_URL "Expected ws:// or wss:// / 需要 WebSocket 網址。"
#define MSG_BODY_LIMIT "LimitExceeded / 回應本文過大。"
#define MSG_STREAM_LIMIT "LimitExceeded / 尚未讀取的串流文字過多。"
#define MSG_MESSAGE_LIMIT "LimitExceeded / WebSocket 訊息過大。"

/* ---- Bytes, text and headers (values.c). ------------------------------------------------------------------------- */
typedef struct net_buffer { uint8_t *data; size_t length, capacity; } net_buffer;
NET_SHARED bool buffer_reserve(net_buffer *b, size_t extra);
NET_SHARED bool buffer_append(net_buffer *b, const void *data, size_t length);
NET_SHARED bool buffer_text(net_buffer *b, const char *text);
NET_SHARED bool buffer_format(net_buffer *b, const char *format, ...);
NET_SHARED void buffer_consume(net_buffer *b, size_t count); /* drops `count` bytes from the front */
NET_SHARED char *buffer_cstring(net_buffer *b);              /* NUL-terminates in place; the buffer keeps ownership */
NET_SHARED char *buffer_take(net_buffer *b, size_t *length); /* NUL-terminated malloc'd copy taken from the buffer */
NET_SHARED void buffer_free(net_buffer *b);

NET_SHARED char *net_strdup(const char *text);
NET_SHARED char *net_strndup(const char *text, size_t length);
NET_SHARED bool net_iequal(const char *a, const char *b);                  /* ASCII case-insensitive */
NET_SHARED bool net_iprefix(const char *text, const char *prefix);
NET_SHARED const char *net_itoken(const char *list, const char *token);    /* a comma-separated header token, or NULL */
/* Bytes of incomplete UTF-8 at the end of `data` (0..3), held back until more arrive. */
NET_SHARED size_t utf8_incomplete_tail(const uint8_t *data, size_t length);
NET_SHARED size_t utf16_length(const char *utf8, size_t length);

typedef struct net_header { char *name, *value; } net_header;
typedef struct net_headers { net_header *items; uint32_t count, capacity; } net_headers;
NET_SHARED void headers_add(net_headers *h, const char *name, const char *value);
NET_SHARED void headers_set(net_headers *h, const char *name, const char *value); /* replaces a header of the same name */
NET_SHARED const char *headers_get(const net_headers *h, const char *name);       /* case-insensitive, first match */
NET_SHARED void headers_remove(net_headers *h, const char *name);
NET_SHARED void headers_copy(net_headers *to, const net_headers *from);
NET_SHARED void headers_free(net_headers *h);
/* A hash of the headers, names as received and repeated names joined with `separator` (", " or ","). */
NET_SHARED aqua_value headers_hash(aqua_runtime *rt, const net_headers *h, const char *separator);
/* Header name: an RFC 9110 token; value: no CR or LF. */
NET_SHARED bool header_valid(const char *name, const char *value);

/* Language values. */
NET_SHARED bool value_text(aqua_runtime *rt, aqua_value v, char **out, net_error *e);       /* strings only, malloc'd */
NET_SHARED bool value_integer(aqua_value v, int32_t *out, net_error *e);                    /* finite whole int32 */
NET_SHARED bool value_boolean(aqua_value v, bool *out, net_error *e);
NET_SHARED bool value_bytes(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e); /* byte arrays only */
NET_SHARED bool value_payload(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e); /* text, bytes, else JSON */
NET_SHARED aqua_value bytes_value(aqua_runtime *rt, const uint8_t *data, size_t length);
/* JSON with System.Text.Json's limits: depth 64, int32 integers become integers, other numbers doubles. */
NET_SHARED bool json_encode(aqua_runtime *rt, aqua_value v, net_buffer *out, net_error *e);
NET_SHARED bool json_decode(aqua_runtime *rt, const uint8_t *data, size_t length, aqua_value *out, net_error *e);

/* ---- URLs (url.c). ----------------------------------------------------------------------------------------------- */
typedef struct net_url {
    char *scheme;    /* lower case */
    char *host;      /* lower case; IPv6 in brackets */
    int port;        /* explicit, else the scheme's default (-1 when it has none) */
    bool explicit_port;
    char *path;      /* percent-encoded, at least "/" for hierarchical schemes */
    char *query;     /* "" or "?..." */
    char *fragment;  /* "" or "#..." */
    char *userinfo;  /* "" or "user:password" */
} net_url;
NET_SHARED bool url_parse(const char *text, net_url *url);
/* Resolves `reference` (a Location header) against `base` into a new absolute URL. */
NET_SHARED bool url_resolve(const net_url *base, const char *reference, net_url *out);
NET_SHARED void url_free(net_url *url);
/* scheme://host[:port]path?query (no fragment, no userinfo). */
NET_SHARED char *url_format(const net_url *url, bool with_fragment);
/* "scheme://host[:port]" with the port only when it is not the scheme's default. */
NET_SHARED char *url_origin(const net_url *url);
/* The host without IPv6 brackets. */
NET_SHARED char *url_hostname(const net_url *url);
NET_SHARED int url_default_port(const char *scheme);
/* Uri.EscapeDataString: every byte except RFC 3986 unreserved characters as %XX. */
NET_SHARED void url_escape(net_buffer *out, const char *text, size_t length);
/* Uri.UnescapeDataString (optionally '+' as space); invalid escapes stay as written. */
NET_SHARED char *url_unescape(const char *text, size_t length, bool plus_is_space);

/* ---- Classes, records and objects (api.def, network.c). ---------------------------------------------------------- */
typedef enum net_class_id {
#define CLASS(id, name, release) CLASS_##id,
#define NATIVE_CLASS(id, name, release) CLASS_##id,
#define FUNCTION(owner, fn, en, zh, signature)
#define NATIVE(owner, fn, en, zh, signature, feature)
#define FIELD(owner, field, en, zh)
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef FUNCTION
#undef NATIVE
#undef FIELD
    CLASS_COUNT
} net_class_id;

/* Field positions: each class's fields count from 0 (FIELD_OF(response, status)). */
enum {
#define CLASS(id, name, release) FIELDS_START_##id,
#define NATIVE_CLASS(id, name, release) FIELDS_START_##id,
#define FUNCTION(owner, fn, en, zh, signature)
#define NATIVE(owner, fn, en, zh, signature, feature)
#define FIELD(owner, field, en, zh) FIELD_##owner##_##field,
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef FUNCTION
#undef NATIVE
#undef FIELD
};
#define FIELD_OF(owner, field) (FIELD_##owner##_##field - FIELDS_START_##owner - 1)
#define NET_FIELDS 10

/* Every native object of this library: retained field values plus the class's C data (a resource for sockets,
   clients and servers; the response or request data for records with methods). */
typedef struct net_object {
    net_class_id cls;
    aqua_ref fields[NET_FIELDS];
    void *payload;
} net_object;
static aqua_class *net_classes[CLASS_COUNT];
NET_SHARED net_object *object_create(net_class_id cls, void *payload);
NET_SHARED void object_field(aqua_runtime *rt, net_object *o, int field, aqua_value value);
NET_SHARED aqua_value object_get(aqua_runtime *rt, net_object *o, int field);
NET_SHARED aqua_value object_wrap(aqua_runtime *rt, net_object *o); /* the language value owning `o` */
/* Self of a method call: the object data and its payload. */
static inline net_object *self_object(aqua_call *call) { return (net_object *)aqua_self(call); }
static inline void *self_payload(aqua_call *call) { net_object *o = self_object(call); return o ? o->payload : NULL; }

/* Records scripts read as fields. */
NET_SHARED aqua_value error_record(aqua_runtime *rt, const net_error *e);                   /* Network.Error */
NET_SHARED aqua_value result_record(aqua_runtime *rt, bool ok, aqua_value value, const net_error *e); /* Network.Result */

/* ---- Per-runtime state and resources (network.c). ---------------------------------------------------------------- */
typedef struct engine engine; /* native hub or web state (engine.h) */
typedef struct network_state network_state;
typedef struct resource resource;
/* An open thing scripts close: clients, operations, sockets, listeners, servers. `close` releases its I/O; the
   memory stays alive while objects or engine work refer to it (each type counts its own references). */
struct resource {
    void (*close)(network_state *state, resource *r);
    network_state *state;
    resource *previous, *next;
    bool open;
};
struct network_state {
    aqua_runtime *rt;
    engine *engine;
    struct client *client;     /* the module's shared default client */
    resource *resources;       /* open resources, newest first */
    uint32_t resource_count;
    bool closing;              /* the runtime is ending: engine completions are dropped */
};
/* Tracks `r` (the old 1024-resource limit); on failure closes it and sets LimitExceeded. */
NET_SHARED bool resource_own(network_state *s, resource *r, net_error *e);
/* Untracks and closes `r` once; later calls do nothing. */
NET_SHARED void resource_close(network_state *s, resource *r);
static inline bool resource_open(const resource *r) { return r->open; }

/* Feature checks: "Unsupported / 不支援：<platform> / <feature>". */
NET_SHARED bool feature_supported(const char *name);
NET_SHARED bool feature_require(const char *feature, net_error *e);

/* ---- Request options (network.c). -------------------------------------------------------------------------------- */
enum { CREDENTIALS_OMIT, CREDENTIALS_SAME_ORIGIN, CREDENTIALS_INCLUDE };
typedef struct net_options {
    int32_t timeout_ms, max_body, max_redirects;
    int32_t read_timeout_ms, idle_timeout_ms; /* servers: a request's head, an idle keep-alive connection */
    bool follow_redirects;
    int credentials;
    net_headers headers;
    net_buffer body; bool has_body;
    char *certificate, *certificate_password;
} net_options;
NET_SHARED void options_default(net_options *o);
NET_SHARED void options_copy(net_options *to, const net_options *from);
NET_SHARED void options_free(net_options *o);
/* Reads the optional options hash at argument `index` over `defaults` and validates it (NetworkOptions.Validate). */
NET_SHARED bool options_read(aqua_call *call, uint32_t index, const net_options *defaults, net_options *out, net_error *e);
NET_SHARED bool options_validate(const net_options *o, net_error *e);
NET_SHARED bool port_valid(int32_t port, bool allow_zero, net_error *e);

#endif
