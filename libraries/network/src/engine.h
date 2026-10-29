/* engine.h — what the shared code needs from the engine that moves bytes: src/native on targets with sockets,
 * src/web on the web. Every engine function is called on the runtime's thread. Operations that complete later always
 * complete asynchronously (never before the starting call returns), on the runtime's thread, by settling the
 * aqua_pending they were given or by calling back the shared functions declared at the end. */
#ifndef AQUA_NETWORK_ENGINE_H
#define AQUA_NETWORK_ENGINE_H

#include "network.h"

/* ---- Lifecycle and timers. ---------------------------------------------------------------------------------------- */
static engine *engine_create(network_state *s);
/* Stops all engine work for a runtime that is ending; no completion runs afterwards. */
static void engine_destroy(engine *e);
/* One-shot timer: `fire` runs on the runtime's thread after `ms` unless cancelled first. Pending timers never keep
   the application alive. A fired timer is gone: do not cancel it afterwards. */
typedef struct net_timer net_timer;
static net_timer *timer_start(network_state *s, double ms, void (*fire)(network_state *s, void *data), void *data);
static void timer_cancel(network_state *s, net_timer *t);

/* ---- HTTP clients and transfers (client.c owns the objects). ----------------------------------------------------- */
typedef struct client {
    resource base;
    atomic_int refs;
    net_options defaults;
    atomic_bool closed;
    void *engine;                 /* cookie jar and connection pool (native) */
} client;

typedef struct transfer {
    resource base;                /* operations are tracked resources; plain requests are not */
    atomic_int refs;
    atomic_bool cancelled;        /* Cancel(), Close() or the runtime ending */
    client *client;
    char *method;
    net_url url;
    net_options options;          /* the request's own copy: headers, body, limits */
    bool streaming;               /* StartStream: chunks are decoded for Read/ReadLines as they arrive */
    const char *name;             /* "Network.Get" ... for messages */
    net_mutex lock;               /* guards the progress fields below while the engine runs */
    int head_status;              /* streaming: the status once headers arrived */
    net_buffer unread;            /* streaming: decoded text not yet read (may end inside a UTF-8 sequence) */
    bool done;
    /* The outcome, written by the engine before transfer_finished. */
    int status;
    char *final_url;
    net_headers headers;
    net_buffer body;
    net_error error;
    /* Runtime thread. */
    aqua_pending *pending;        /* Request/Get/Post waiting for the response */
    aqua_pending **waiters; uint32_t waiter_count;
    aqua_ref response;            /* the Response object once made */
    void *engine;
} transfer;

static bool engine_client_open(client *c, net_error *e);
static void engine_client_close(client *c);          /* Close(): idle connections end; running transfers continue */
static void engine_client_free(client *c);           /* the last reference went */
static bool engine_client_clear_cookies(client *c, net_error *e);
/* Starts a transfer, holding a reference until it finishes with transfer_finished. */
static void engine_transfer_start(network_state *s, transfer *t);
static void engine_transfer_cancel(transfer *t);     /* after setting t->cancelled */

/* Called by engines (any thread natively; the runtime's on the web). */
static void transfer_head(transfer *t, int status);
/* Streams decode body bytes for Read/ReadLines; false (and LimitExceeded) when too much text is unread. */
static bool transfer_stream_bytes(transfer *t, const uint8_t *data, size_t length, net_error *e);
/* Runtime thread: the outcome fields are set; delivers the response to whoever waits and drops the engine's
   reference. */
static void transfer_finished(network_state *s, transfer *t);
static void transfer_unref(transfer *t);

/* ---- WebSockets (websocket.c owns the object). ------------------------------------------------------------------- */
typedef struct websocket {
    resource base;
    atomic_int refs;
    int32_t timeout_ms, max_message;
    bool receiving;               /* one Receive at a time */
    char *protocol;               /* the subprotocol the handshake chose, NULL for none */
    int32_t close_code;           /* the peer's close: its code (1005 without one, 1006 when the connection dropped) */
    char *close_reason;
    void *engine;
} websocket;
/* Secure random bytes from the operating system or the browser. */
static bool engine_random(void *buffer, size_t length);
/* The page's origin ("https://host:port") on the web, NULL elsewhere; malloc'd. */
static char *engine_page_origin(void);
static websocket *websocket_new(network_state *s, const net_options *o);
static void websocket_unref(websocket *ws);
/* A Message record (Network.Message). */
static aqua_value message_record(aqua_runtime *rt, const uint8_t *data, size_t length, bool is_text, bool closed);
/* The record of the peer's close, with its code and reason. */
static aqua_value close_record(aqua_runtime *rt, const websocket *ws);
/* Each settles `pending`: connect with the WebSocket object, send and close with null, receive with a Message. */
static void engine_websocket_connect(network_state *s, websocket *ws, const net_url *url, const net_options *o, aqua_pending *pending, const char *name);
/* The engine finished connecting (`e` NULL) or failed: tracks the socket and settles `pending`. */
static void websocket_connected(network_state *s, websocket *ws, aqua_pending *pending, const char *name, const net_error *e);
static void engine_websocket_send(network_state *s, websocket *ws, net_buffer *message, bool text, aqua_pending *pending, const char *name);
static void engine_websocket_receive(network_state *s, websocket *ws, aqua_pending *pending, const char *name);
/* Sends the close frame, then closes the resource (Close(code, reason) disposes the socket). */
static void engine_websocket_close(network_state *s, websocket *ws, int code, const char *reason, aqua_pending *pending, const char *name);
/* The resource closes: abort I/O and free the engine's data once its work drains. */
static void engine_websocket_abort(websocket *ws);
/* Frees the engine's data when the last reference to `ws` goes. */
static void engine_websocket_free(websocket *ws);
/* The language object for `ws` (made once; the socket holds it while open). */
static aqua_value websocket_object(aqua_runtime *rt, websocket *ws);

#endif
