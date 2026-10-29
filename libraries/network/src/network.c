/* The Network|網路 library's entry point: registers api.def, owns the per-runtime state and resource tracking, and
 * implements the module functions that need no engine: Try, Supports, Capabilities, EncodeQuery and ParseURL, plus
 * request options, records and the old error format "<Library.Function>: <Kind>: <message>". */
#include "engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void release_nothing(aqua_runtime *rt, void *payload);
/* Release functions and C functions of api.def (defined across the library's files). */
#define CLASS(id, name, release) static void release(aqua_runtime *rt, void *payload);
#if defined(AQUA_OS_WEB)
#define NATIVE_CLASS(id, name, release)
#define NATIVE(owner, fn, en, zh, signature, feature)
#else
#define NATIVE_CLASS(id, name, release) static void release(aqua_runtime *rt, void *payload);
#define NATIVE(owner, fn, en, zh, signature, feature) static void fn(aqua_call *call, const char *name);
#endif
#define FUNCTION(owner, fn, en, zh, signature) static void fn(aqua_call *call, const char *name);
#define FIELD(owner, field, en, zh)
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FIELD

/* ---- Errors ---- */

const char *net_kind_name(net_kind kind) {
    static const char *const names[] = {
#define X(id, name) name,
        NET_ERROR_KINDS(X)
#undef X
    };
    return names[kind];
}
bool net_error_set(net_error *e, net_kind kind, bool retryable, const char *format, ...) {
    if (!e) return false;
    e->kind = kind;
    e->retryable = retryable;
    va_list arguments;
    va_start(arguments, format);
    vsnprintf(e->message, sizeof e->message, format, arguments);
    va_end(arguments);
    return false;
}
void net_raise(aqua_call *call, const char *name, const net_error *e) {
    aqua_fail(call, "%s: %s: %s", name, net_kind_name(e->kind), e->message);
}
void net_raise_kind(aqua_call *call, const char *name, net_kind kind, const char *message) {
    net_error e;
    net_error_set(&e, kind, false, "%s", message);
    net_raise(call, name, &e);
}
void net_reject(aqua_pending *pending, const char *name, const net_error *e) {
    aqua_reject(pending, "%s: %s: %s", name, net_kind_name(e->kind), e->message);
}

/* ---- Features (NetworkCatalog.Features): English, Traditional Chinese, and whether this target has it. ---------- */
#if defined(AQUA_OS_WEB)
#define NATIVE_ONLY false
#else
#define NATIVE_ONLY true
#endif
#define NET_FEATURES(X) \
    X("http", "超文字傳輸", true) X("cookie-control", "Cookie管理", NATIVE_ONLY) X("cookies", "Cookie", true) \
    X("websocket", "網頁通訊端", true) X("tcp", "傳輸控制協定", NATIVE_ONLY) X("tls", "傳輸層安全", NATIVE_ONLY) \
    X("udp", "使用者資料報", NATIVE_ONLY) X("dns", "網域名稱解析", NATIVE_ONLY) X("tcp-listen", "監聽傳輸控制", NATIVE_ONLY) \
    X("http-server", "網頁伺服器", NATIVE_ONLY) X("http-stream", "串流傳輸", true)
static const struct feature { const char *name, *chinese; bool supported; } features[] = {
#define X(name, chinese, supported) {name, chinese, supported},
    NET_FEATURES(X)
#undef X
};
#define FEATURE_COUNT (sizeof features / sizeof features[0])

bool feature_supported(const char *name) {
    for (size_t i = 0; i < FEATURE_COUNT; i++)
        if (net_iequal(features[i].name, name) || !strcmp(features[i].chinese, name)) return features[i].supported;
    return false;
}
bool feature_require(const char *feature, net_error *e) {
    if (feature_supported(feature)) return true;
    return net_error_set(e, NET_UNSUPPORTED, false, "Unsupported / 不支援：%s / %s", AQUA_PLATFORM_NAME, feature);
}

/* ---- Objects ---- */

net_object *object_create(net_class_id cls, void *payload) {
    net_object *o = calloc(1, sizeof *o);
    if (!o) return NULL;
    o->cls = cls;
    o->payload = payload;
    return o;
}
void object_field(aqua_runtime *rt, net_object *o, int field, aqua_value value) {
    if (o->fields[field]) aqua_release(rt, o->fields[field]);
    o->fields[field] = aqua_retain(rt, value);
}
aqua_value object_get(aqua_runtime *rt, net_object *o, int field) {
    return o->fields[field] ? aqua_ref_value(rt, o->fields[field]) : aqua_null();
}
aqua_value object_wrap(aqua_runtime *rt, net_object *o) { return aqua_object_new(rt, net_classes[o->cls], o); }

aqua_value error_record(aqua_runtime *rt, const net_error *e) {
    net_object *o = object_create(CLASS_error, NULL);
    object_field(rt, o, FIELD_OF(error, kind), aqua_cstring(rt, net_kind_name(e->kind)));
    object_field(rt, o, FIELD_OF(error, message), aqua_cstring(rt, e->message));
    object_field(rt, o, FIELD_OF(error, retryable), aqua_bool(e->retryable));
    return object_wrap(rt, o);
}
aqua_value result_record(aqua_runtime *rt, bool ok, aqua_value value, const net_error *e) {
    net_object *o = object_create(CLASS_result, NULL);
    object_field(rt, o, FIELD_OF(result, success), aqua_bool(ok));
    object_field(rt, o, FIELD_OF(result, value), ok && value.type != AQUA_VOID ? value : aqua_null());
    object_field(rt, o, FIELD_OF(result, error), ok ? aqua_null() : error_record(rt, e));
    return object_wrap(rt, o);
}

/* ---- Resources ---- */

bool resource_own(network_state *s, resource *r, net_error *e) {
    r->state = s;
    if (s->resource_count >= NET_RESOURCE_LIMIT || s->closing) {
        r->open = true;
        r->close(s, r);
        r->open = false;
        return net_error_set(e, NET_LIMIT, false, "LimitExceeded / 資源上限 1024.");
    }
    r->open = true;
    r->previous = NULL;
    r->next = s->resources;
    if (s->resources) s->resources->previous = r;
    s->resources = r;
    s->resource_count++;
    return true;
}
void resource_close(network_state *s, resource *r) {
    if (!r->open) return;
    r->open = false;
    if (r->previous) r->previous->next = r->next;
    else if (s->resources == r) s->resources = r->next;
    if (r->next) r->next->previous = r->previous;
    r->previous = r->next = NULL;
    if (s->resource_count) s->resource_count--;
    r->close(s, r);
}

/* ---- Status texts (RFC 9110 and the IANA registry, as Go's http.StatusText) ---- */

#define NET_STATUS_TEXTS(X)     X(100, "Continue") X(101, "Switching Protocols") X(102, "Processing") X(103, "Early Hints")     X(200, "OK") X(201, "Created") X(202, "Accepted") X(203, "Non-Authoritative Information") X(204, "No Content")     X(205, "Reset Content") X(206, "Partial Content") X(207, "Multi-Status") X(208, "Already Reported") X(226, "IM Used")     X(300, "Multiple Choices") X(301, "Moved Permanently") X(302, "Found") X(303, "See Other") X(304, "Not Modified")     X(305, "Use Proxy") X(307, "Temporary Redirect") X(308, "Permanent Redirect")     X(400, "Bad Request") X(401, "Unauthorized") X(402, "Payment Required") X(403, "Forbidden") X(404, "Not Found")     X(405, "Method Not Allowed") X(406, "Not Acceptable") X(407, "Proxy Authentication Required") X(408, "Request Timeout")     X(409, "Conflict") X(410, "Gone") X(411, "Length Required") X(412, "Precondition Failed") X(413, "Payload Too Large")     X(414, "URI Too Long") X(415, "Unsupported Media Type") X(416, "Range Not Satisfiable") X(417, "Expectation Failed")     X(418, "I'm a teapot") X(421, "Misdirected Request") X(422, "Unprocessable Entity") X(423, "Locked")     X(424, "Failed Dependency") X(425, "Too Early") X(426, "Upgrade Required") X(428, "Precondition Required")     X(429, "Too Many Requests") X(431, "Request Header Fields Too Large") X(451, "Unavailable For Legal Reasons")     X(500, "Internal Server Error") X(501, "Not Implemented") X(502, "Bad Gateway") X(503, "Service Unavailable")     X(504, "Gateway Timeout") X(505, "HTTP Version Not Supported") X(506, "Variant Also Negotiates")     X(507, "Insufficient Storage") X(508, "Loop Detected") X(510, "Not Extended") X(511, "Network Authentication Required")

const char *net_status_text(int status) {
    switch (status) {
#define X(code, text) case code: return text;
        NET_STATUS_TEXTS(X)
#undef X
    default: return "";
    }
}

static void net_status(aqua_call *call, const char *name) {
    (void)name;
    aqua_return_cstring(call, net_status_text(aqua_arg_int(call, 0)));
}

/* ---- Options (NetworkOptions) ---- */

void options_default(net_options *o) {
    memset(o, 0, sizeof *o);
    o->timeout_ms = 10000;
    o->max_body = NET_MAX_BODY;
    o->max_redirects = 10;
    o->read_timeout_ms = 15000;
    o->idle_timeout_ms = 30000;
    o->follow_redirects = true;
    o->credentials = CREDENTIALS_SAME_ORIGIN;
}
void options_copy(net_options *to, const net_options *from) {
    *to = *from;
    memset(&to->headers, 0, sizeof to->headers);
    headers_copy(&to->headers, &from->headers);
    memset(&to->body, 0, sizeof to->body);
    if (from->has_body) buffer_append(&to->body, from->body.data, from->body.length);
    to->certificate = net_strdup(from->certificate);
    to->certificate_password = net_strdup(from->certificate_password);
}
void options_free(net_options *o) {
    headers_free(&o->headers);
    buffer_free(&o->body);
    free(o->certificate);
    free(o->certificate_password);
    memset(o, 0, sizeof *o);
}
bool port_valid(int32_t port, bool allow_zero, net_error *e) {
    if (port < (allow_zero ? 0 : 1) || port > 65535) return net_error_set(e, NET_INVALID, false, MSG_PORT);
    return true;
}
bool options_validate(const net_options *o, net_error *e) {
    if (o->timeout_ms < 1 || o->timeout_ms > 300000 || o->read_timeout_ms < 1 || o->read_timeout_ms > 300000
        || o->idle_timeout_ms < 1 || o->idle_timeout_ms > 300000 || o->max_body < 1 || o->max_body > NET_MAX_BODY || o->max_redirects < 0 || o->max_redirects > 20)
        return net_error_set(e, NET_INVALID, false, MSG_OPTIONS);
    if (o->has_body && o->body.length > (size_t)o->max_body)
        return net_error_set(e, NET_INVALID, false, "LimitExceeded / 超過上限：請求本文過大。");
    for (uint32_t i = 0; i < o->headers.count; i++)
        if (!header_valid(o->headers.items[i].name, o->headers.items[i].value)) return net_error_set(e, NET_INVALID, false, MSG_HEADER);
    return true;
}

/* A field present with a non-null value, by English then Chinese name. */
static bool option_field(aqua_runtime *rt, aqua_value hash, const char *names, aqua_value *value) {
    return aqua_hash_field(rt, hash, names, value) && !aqua_is_nullish(*value);
}
static bool read_integer(aqua_runtime *rt, aqua_value hash, const char *names, int32_t *out, net_error *e) {
    aqua_value v;
    return !option_field(rt, hash, names, &v) || value_integer(v, out, e);
}
static bool read_text(aqua_runtime *rt, aqua_value hash, const char *names, char **out, net_error *e) {
    aqua_value v;
    if (!option_field(rt, hash, names, &v)) return true;
    free(*out);
    *out = NULL;
    return value_text(rt, v, out, e);
}
/* key=value&... with Uri.EscapeDataString of string keys and values (EncodeQuery and the form option). */
static bool encode_form(aqua_runtime *rt, aqua_value hash, net_buffer *out, net_error *e) {
    if (hash.type != AQUA_HASH) return net_error_set(e, NET_INVALID, false, MSG_HASH);
    out->length = 0;
    buffer_reserve(out, 64);
    out->data[0] = 0;
    uint32_t n = aqua_hash_count(rt, hash);
    for (uint32_t i = 0; i < n; i++) {
        aqua_value key, value;
        if (!aqua_hash_entry(rt, hash, i, &key, &value)) continue;
        char *k = NULL, *v = NULL;
        if (!value_text(rt, key, &k, e) || !value_text(rt, value, &v, e)) { aqua_free(k); aqua_free(v); return false; }
        if (i) buffer_append(out, "&", 1);
        url_escape(out, k, strlen(k));
        buffer_append(out, "=", 1);
        url_escape(out, v, strlen(v));
        aqua_free(k);
        aqua_free(v);
    }
    return true;
}

bool options_read(aqua_call *call, uint32_t index, const net_options *defaults, net_options *out, net_error *e) {
    aqua_runtime *rt = aqua_call_runtime(call);
    net_options fallback;
    options_default(&fallback);
    options_copy(out, defaults ? defaults : &fallback);
    options_free(&fallback);
    if (!aqua_has_arg(call, index)) return true;
    aqua_value hash = aqua_arg(call, index), v;
    if (hash.type != AQUA_HASH) return net_error_set(e, NET_INVALID, false, MSG_HASH);
    if (option_field(rt, hash, "headers|標頭", &v)) {
        if (v.type != AQUA_HASH) return net_error_set(e, NET_INVALID, false, MSG_HASH);
        uint32_t n = aqua_hash_count(rt, v);
        for (uint32_t i = 0; i < n; i++) {
            aqua_value key, item;
            if (!aqua_hash_entry(rt, v, i, &key, &item)) continue;
            char *name = NULL, *text = NULL;
            if (!value_text(rt, key, &name, e) || !value_text(rt, item, &text, e)) { aqua_free(name); aqua_free(text); return false; }
            headers_set(&out->headers, name, text);
            aqua_free(name);
            aqua_free(text);
        }
    }
    if (option_field(rt, hash, "body|本文", &v)) {
        if (!value_payload(rt, v, &out->body, e)) return false;
        out->has_body = true;
    }
    if (option_field(rt, hash, "json|JSON", &v)) {
        if (!json_encode(rt, v, &out->body, e)) return false;
        out->has_body = true;
        headers_set(&out->headers, "Content-Type", "application/json; charset=utf-8");
    }
    if (option_field(rt, hash, "form|表單", &v)) {
        if (!encode_form(rt, v, &out->body, e)) return false;
        out->has_body = true;
        headers_set(&out->headers, "Content-Type", "application/x-www-form-urlencoded");
    }
    if (!read_integer(rt, hash, "timeout|逾時", &out->timeout_ms, e)) return false;
    if (!read_integer(rt, hash, "maxBodyBytes|本文上限", &out->max_body, e)) return false;
    if (!read_integer(rt, hash, "readTimeout|讀取逾時", &out->read_timeout_ms, e)) return false;
    if (!read_integer(rt, hash, "idleTimeout|閒置逾時", &out->idle_timeout_ms, e)) return false;
    if (!read_integer(rt, hash, "maxRedirects|重新導向上限", &out->max_redirects, e)) return false;
    if (option_field(rt, hash, "followRedirects|跟隨重新導向", &v) && !value_boolean(v, &out->follow_redirects, e)) return false;
    char *credentials = NULL;
    if (!read_text(rt, hash, "credentials|認證", &credentials, e)) return false;
    if (credentials) {
        int mode = !strcmp(credentials, "omit") ? CREDENTIALS_OMIT : !strcmp(credentials, "same-origin") ? CREDENTIALS_SAME_ORIGIN
            : !strcmp(credentials, "include") ? CREDENTIALS_INCLUDE : -1;
        aqua_free(credentials);
        if (mode < 0) {
            if (!options_validate(out, e)) return false;
            return net_error_set(e, NET_INVALID, false, "Invalid credentials / 無效的認證模式。");
        }
        out->credentials = mode;
    }
    if (!read_text(rt, hash, "certificate|憑證", &out->certificate, e)) return false;
    if (!read_text(rt, hash, "certificatePassword|憑證密碼", &out->certificate_password, e)) return false;
    return options_validate(out, e);
}

/* ---- Module functions without an engine ---- */

typedef struct try_call { aqua_pending *pending; bool finished, ok; aqua_value result; } try_call;

static aqua_value try_outcome(aqua_runtime *rt, bool ok, aqua_value result) {
    net_error e = {NET_OK, false, ""};
    if (!ok) {
        size_t length = 0;
        char *text = aqua_text_dup(rt, result, &length);
        net_error_set(&e, NET_FAILED, false, "%s", text ? text : "");
        aqua_free(text);
    }
    return result_record(rt, ok, result, &e);
}
static void try_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    try_call *t = data;
    if (!t->pending) { t->finished = true; t->ok = ok; t->result = result; return; }
    aqua_resolve(t->pending, try_outcome(rt, ok, result));
    free(t);
}
/* Try/嘗試(callback): Success/Value or the callback's failure as OperationFailed. */
void net_try(aqua_call *call, const char *name) {
    (void)name;
    aqua_runtime *rt = aqua_call_runtime(call);
    try_call *t = calloc(1, sizeof *t);
    aqua_invoke_async(rt, aqua_arg(call, 0), NULL, 0, try_done, t);
    if (t->finished) {
        aqua_return(call, try_outcome(rt, t->ok, t->result));
        free(t);
        return;
    }
    t->pending = aqua_defer(call);
}

void net_supports(aqua_call *call, const char *name) {
    (void)name;
    aqua_return_bool(call, feature_supported(aqua_arg_str(call, 0).data));
}

void net_capabilities(aqua_call *call, const char *name) {
    (void)name;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value hash = aqua_hash_new(rt, FEATURE_COUNT * 2);
    for (size_t i = 0; i < FEATURE_COUNT; i++) {
        aqua_hash_set(rt, hash, aqua_cstring(rt, features[i].name), aqua_bool(features[i].supported));
        aqua_hash_set(rt, hash, aqua_cstring(rt, features[i].chinese), aqua_bool(features[i].supported));
    }
    aqua_return(call, hash);
}

void net_encode_query(aqua_call *call, const char *name) {
    net_buffer out = {0};
    net_error e;
    if (!encode_form(aqua_call_runtime(call), aqua_arg(call, 0), &out, &e)) net_raise(call, name, &e);
    else aqua_return_text(call, (const char *)out.data, out.length);
    buffer_free(&out);
}

void net_parse_url(aqua_call *call, const char *name) {
    aqua_runtime *rt = aqua_call_runtime(call);
    net_url url;
    if (!url_parse(aqua_arg_str(call, 0).data, &url)) {
        net_error e;
        net_error_set(&e, NET_INVALID, false, "Invalid URL / 網址無效。");
        net_raise(call, name, &e);
        return;
    }
    net_object *o = object_create(CLASS_url, NULL);
    object_field(rt, o, FIELD_OF(url, scheme), aqua_cstring(rt, url.scheme));
    object_field(rt, o, FIELD_OF(url, host), aqua_cstring(rt, url.host));
    object_field(rt, o, FIELD_OF(url, port), aqua_int(url.port));
    object_field(rt, o, FIELD_OF(url, path), aqua_cstring(rt, url.path));
    object_field(rt, o, FIELD_OF(url, query), aqua_cstring(rt, url.query));
    object_field(rt, o, FIELD_OF(url, fragment), aqua_cstring(rt, url.fragment));
    url_free(&url);
    aqua_return(call, object_wrap(rt, o));
}

/* ---- Per-runtime state ---- */

static struct client *client_shared(network_state *s); /* client.c */
static void client_unref(struct client *c);

static void *state_create(aqua_runtime *rt) {
    network_state *s = calloc(1, sizeof *s);
    s->rt = rt;
    s->engine = engine_create(s);
    return s;
}
static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    network_state *s = data;
    s->closing = true;
    while (s->resources) resource_close(s, s->resources);
    engine_destroy(s->engine);
    if (s->client) client_unref(s->client);
    free(s);
}

/* ---- Registration (api.def) ---- */

static void release_nothing(aqua_runtime *rt, void *payload) { (void)rt; (void)payload; }

enum {
#define CLASS(id, name, release)
#define NATIVE_CLASS(id, name, release)
#define FUNCTION(owner, fn, en, zh, signature) ENTRY_##owner##_##fn,
#define NATIVE(owner, fn, en, zh, signature, feature) ENTRY_##owner##_##fn,
#define FIELD(owner, field, en, zh)
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FIELD
    ENTRY_COUNT
};
static char *entry_names[ENTRY_COUNT];

#if defined(AQUA_OS_WEB)
/* Targets without sockets: native-only functions fail like the old Require(feature). */
static void native_only(aqua_call *call, const char *name, const char *feature) {
    net_error e;
    feature_require(feature, &e);
    net_raise(call, name, &e);
}
#endif

#define CLASS(id, name, release)
#define NATIVE_CLASS(id, name, release)
#define FUNCTION(owner, fn, en, zh, signature) \
    static void call_##owner##_##fn(aqua_call *call) { fn(call, entry_names[ENTRY_##owner##_##fn]); }
#if defined(AQUA_OS_WEB)
#define NATIVE(owner, fn, en, zh, signature, feature) \
    static void call_##owner##_##fn(aqua_call *call) { native_only(call, entry_names[ENTRY_##owner##_##fn], feature); }
#else
#define NATIVE(owner, fn, en, zh, signature, feature) FUNCTION(owner, fn, en, zh, signature)
#endif
#define FIELD(owner, field, en, zh) \
    static void get_##owner##_##field(aqua_call *call) { \
        net_object *o = self_object(call); \
        aqua_return(call, o ? object_get(aqua_call_runtime(call), o, FIELD_OF(owner, field)) : aqua_null()); }
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FIELD

typedef void (*release_function)(aqua_runtime *rt, void *payload);
static const release_function releases[CLASS_COUNT] = {
#define CLASS(id, name, release) release,
#if defined(AQUA_OS_WEB)
#define NATIVE_CLASS(id, name, release) release_nothing,
#else
#define NATIVE_CLASS(id, name, release) release,
#endif
#define FUNCTION(owner, fn, en, zh, signature)
#define NATIVE(owner, fn, en, zh, signature, feature)
#define FIELD(owner, field, en, zh)
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FIELD
};

static void object_finalize(aqua_runtime *rt, void *data) {
    net_object *o = data;
    for (int i = 0; i < NET_FIELDS; i++) if (o->fields[i]) aqua_release(rt, o->fields[i]);
    if (o->payload) releases[o->cls](rt, o->payload);
    free(o);
}

/* "Network|網路" -> "Network". */
static char *qualified(const char *owner, const char *member) {
    size_t n = strcspn(owner, "|");
    size_t length = n + strlen(member) + 2;
    char *text = malloc(length);
    snprintf(text, length, "%.*s.%s", (int)n, owner, member);
    return text;
}

AQUA_LIBRARY(network) {
    aqua_library_state(library, state_create, state_destroy);
    static const char *const class_names[CLASS_COUNT] = {
#define CLASS(id, name, release) name,
#define NATIVE_CLASS(id, name, release) name,
#define FUNCTION(owner, fn, en, zh, signature)
#define NATIVE(owner, fn, en, zh, signature, feature)
#define FIELD(owner, field, en, zh)
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FIELD
    };
    for (int i = 0; i < CLASS_COUNT; i++)
        net_classes[i] = i == CLASS_module ? aqua_define_module(library, class_names[i]) : aqua_define_class(library, class_names[i], object_finalize);
    aqua_class *module = net_classes[CLASS_module];
#define CLASS(id, name, release)
#define NATIVE_CLASS(id, name, release)
#define FUNCTION(owner, fn, en, zh, signature) \
    if (!entry_names[ENTRY_##owner##_##fn]) entry_names[ENTRY_##owner##_##fn] = qualified(class_names[CLASS_##owner], en); \
    aqua_define_function(net_classes[CLASS_##owner], en "|" zh, call_##owner##_##fn, signature);
#define NATIVE(owner, fn, en, zh, signature, feature) FUNCTION(owner, fn, en, zh, signature)
/* The promise variant shares the function: "GetAsync|非同步取得". */
#define FUNCTION_ASYNC(owner, fn, en, zh, signature) FUNCTION(owner, fn, en, zh, signature) \
    aqua_define_async(net_classes[CLASS_##owner], en "Async|非同步" zh, call_##owner##_##fn, signature);
#define NATIVE_ASYNC(owner, fn, en, zh, signature, feature) FUNCTION_ASYNC(owner, fn, en, zh, signature)
#define FIELD(owner, field, en, zh) aqua_define_property(net_classes[CLASS_##owner], en "|" zh, get_##owner##_##field);
#include "api.def"
#undef CLASS
#undef NATIVE_CLASS
#undef NATIVE
#undef FUNCTION
#undef FUNCTION_ASYNC
#undef NATIVE_ASYNC
#undef FIELD
    aqua_define_string(module, "Platform|平台", AQUA_PLATFORM_NAME);
    aqua_define_int(module, "ContractVersion|契約版本", 1);
}
