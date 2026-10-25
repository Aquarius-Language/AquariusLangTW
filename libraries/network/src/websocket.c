/* WebSocket objects on every target: ConnectWebSocket, SendText, SendBytes, Receive and Close with the old limits
 * and messages. Engines exchange the frames: src/native for clients and server upgrades, src/web the browser's
 * WebSocket. */
#include "engine.h"
#include <stdlib.h>
#include <string.h>

static void websocket_close_resource(network_state *s, resource *r) {
    (void)s;
    engine_websocket_abort((websocket *)r);
}

websocket *websocket_new(network_state *s, const net_options *o) {
    websocket *ws = calloc(1, sizeof *ws);
    atomic_init(&ws->refs, 1);
    ws->base.close = websocket_close_resource;
    ws->base.state = s;
    ws->timeout_ms = o->timeout_ms;
    ws->max_message = o->max_body;
    return ws;
}


void websocket_unref(websocket *ws) {
    if (!ws || atomic_fetch_sub(&ws->refs, 1) != 1) return;
    engine_websocket_free(ws);
    free(ws);
}

void websocket_release(aqua_runtime *rt, void *payload) {
    (void)rt;
    websocket *ws = payload;
    if (ws->base.open) resource_close(ws->base.state, &ws->base);
    websocket_unref(ws);
}

aqua_value websocket_object(aqua_runtime *rt, websocket *ws) {
    atomic_fetch_add(&ws->refs, 1);
    return object_wrap(rt, object_create(CLASS_websocket, ws));
}

aqua_value message_record(aqua_runtime *rt, const uint8_t *data, size_t length, bool is_text, bool closed) {
    net_object *o = object_create(CLASS_message, NULL);
    object_field(rt, o, FIELD_OF(message, text), aqua_text(rt, length ? (const char *)data : "", length));
    object_field(rt, o, FIELD_OF(message, data), bytes_value(rt, data, length));
    object_field(rt, o, FIELD_OF(message, is_text), aqua_bool(is_text));
    object_field(rt, o, FIELD_OF(message, closed), aqua_bool(closed));
    return object_wrap(rt, o);
}

void websocket_connected(network_state *s, websocket *ws, aqua_pending *pending, const char *name, const net_error *e) {
    net_error own;
    if (s->closing) { websocket_unref(ws); return; }
    if (e) { net_reject(pending, name, e); websocket_unref(ws); return; }
    if (!resource_own(s, &ws->base, &own)) { net_reject(pending, name, &own); websocket_unref(ws); return; }
    aqua_value object = websocket_object(s->rt, ws);
    websocket_unref(ws); /* the object holds it now */
    aqua_resolve(pending, object);
}

void net_connect_websocket(aqua_call *call, const char *name) {
    network_state *s = aqua_state(call);
    net_error e;
    net_options o;
    net_url url;
    if (!feature_require("websocket", &e)) { net_raise(call, name, &e); return; }
    if (!options_read(call, 1, NULL, &o, &e)) { options_free(&o); net_raise(call, name, &e); return; }
    if (!url_parse(aqua_arg_str(call, 0).data, &url) || (strcmp(url.scheme, "ws") && strcmp(url.scheme, "wss"))) {
        url_free(&url);
        options_free(&o);
        net_error_set(&e, NET_INVALID, false, MSG_WS_URL);
        net_raise(call, name, &e);
        return;
    }
    websocket *ws = websocket_new(s, &o);
    engine_websocket_connect(s, ws, &url, &o, aqua_defer(call), name);
    url_free(&url);
    options_free(&o);
}

#define SELF_SOCKET(call) ((websocket *)self_payload(call))

static bool socket_live(aqua_call *call, const char *name, websocket *ws) {
    if (ws->base.open) return true;
    net_error e;
    net_error_set(&e, NET_CLOSED, false, MSG_CLOSED);
    net_raise(call, name, &e);
    return false;
}

static void send_message(aqua_call *call, const char *name, net_buffer *message, bool text) {
    websocket *ws = SELF_SOCKET(call);
    if (!socket_live(call, name, ws)) return;
    if (message->length > (size_t)ws->max_message) {
        net_error e;
        net_error_set(&e, NET_INVALID, false, "Message too large");
        net_raise(call, name, &e);
        return;
    }
    engine_websocket_send(aqua_state(call), ws, message, text, aqua_defer(call), name);
}

void websocket_send_text(aqua_call *call, const char *name) {
    aqua_str text = aqua_arg_str(call, 0);
    net_buffer message = {0};
    buffer_append(&message, text.data, text.length);
    send_message(call, name, &message, true);
    buffer_free(&message);
}

void websocket_send_bytes(aqua_call *call, const char *name) {
    net_buffer message = {0};
    net_error e;
    if (!value_bytes(aqua_call_runtime(call), aqua_arg(call, 0), &message, &e)) net_raise(call, name, &e);
    else send_message(call, name, &message, false);
    buffer_free(&message);
}

void websocket_receive(aqua_call *call, const char *name) {
    websocket *ws = SELF_SOCKET(call);
    if (!socket_live(call, name, ws)) return;
    if (ws->receiving) {
        net_error e;
        net_error_set(&e, NET_INVALID, false, "InvalidArgument / 不允許同時接收。");
        net_raise(call, name, &e);
        return;
    }
    engine_websocket_receive(aqua_state(call), ws, aqua_defer(call), name);
}

/* Close([code, reason]): sends the close frame and disposes the socket; closing twice does nothing. */
void websocket_close(aqua_call *call, const char *name) {
    websocket *ws = SELF_SOCKET(call);
    if (!ws->base.open) return;
    int32_t code = aqua_has_arg(call, 0) ? aqua_arg_int(call, 0) : 1000;
    aqua_str reason = aqua_has_arg(call, 1) ? aqua_arg_str(call, 1) : (aqua_str){"", 0};
    /* WebSocketCloseStatus: 1000..4999 without the codes reserved for the protocol, a reason of at most 123 bytes. */
    if (code < 1000 || code > 4999 || code == 1004 || code == 1005 || code == 1006 || code == 1015 || reason.length > 123) {
        net_error e;
        net_error_set(&e, NET_INVALID, false, "Invalid close status or reason / 關閉代碼或原因無效。");
        net_raise(call, name, &e);
        return;
    }
    engine_websocket_close(aqua_state(call), ws, code, reason.data, aqua_defer(call), name);
}
