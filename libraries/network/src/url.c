/* Absolute URLs as the old library read them with .NET's Uri: lower-case scheme and host, the scheme's default port
 * when none is written, a percent-encoded path with dot segments removed, and the query and fragment with their
 * leading '?' and '#'. Reference resolution follows RFC 3986 section 5.2 (Location headers). */
#include "network.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int url_default_port(const char *scheme) {
    if (!strcmp(scheme, "http") || !strcmp(scheme, "ws")) return 80;
    if (!strcmp(scheme, "https") || !strcmp(scheme, "wss")) return 443;
    if (!strcmp(scheme, "ftp")) return 21;
    return -1;
}

static bool is_hex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
static int hex_value(char c) { return c <= '9' ? c - '0' : (c | 32) - 'a' + 10; }

/* Copies a path, query or fragment, percent-encoding what a URI may not contain literally. */
static void append_encoded(net_buffer *out, const char *text, size_t length) {
    static const char hex[] = "0123456789ABCDEF";
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)text[i];
        bool keep = c > 0x20 && c < 0x7F && !strchr("\"<>\\^`{|}", c);
        if (c == '%') keep = i + 2 < length && is_hex(text[i + 1]) && is_hex(text[i + 2]);
        if (keep) buffer_append(out, &text[i], 1);
        else { char e[3] = {'%', hex[c >> 4], hex[c & 15]}; buffer_append(out, e, 3); }
    }
}

/* RFC 3986 5.2.4 on an encoded path. */
static char *remove_dot_segments(const char *path) {
    size_t n = strlen(path);
    char *out = malloc(n + 2);
    size_t length = 0;
    const char *p = path;
    while (*p) {
        if (!strncmp(p, "../", 3)) p += 3;
        else if (!strncmp(p, "./", 2)) p += 2;
        else if (!strncmp(p, "/./", 3)) p += 2;
        else if (!strcmp(p, "/.")) { out[length++] = '/'; break; }
        else if (!strncmp(p, "/../", 4) || !strcmp(p, "/..")) {
            p += 3;
            while (length > 0 && out[length - 1] != '/') length--;
            if (length > 0) length--;
            if (!*p) { out[length++] = '/'; break; }
        } else if (!strcmp(p, ".") || !strcmp(p, "..")) break;
        else {
            if (*p == '/') out[length++] = *p++;
            while (*p && *p != '/') out[length++] = *p++;
        }
    }
    out[length] = 0;
    return out;
}

void url_free(net_url *url) {
    free(url->scheme); free(url->host); free(url->path); free(url->query); free(url->fragment); free(url->userinfo);
    memset(url, 0, sizeof *url);
}

static bool fail(net_url *url) { url_free(url); return false; }

bool url_parse(const char *input, net_url *url) {
    memset(url, 0, sizeof *url);
    while (*input == ' ' || *input == '\t' || *input == '\n' || *input == '\r') input++;
    size_t length = strlen(input);
    while (length && (input[length - 1] == ' ' || input[length - 1] == '\t' || input[length - 1] == '\n' || input[length - 1] == '\r')) length--;
    const char *p = input, *end = input + length;
    /* scheme */
    const char *s = p;
    if (p >= end || !((*p | 32) >= 'a' && (*p | 32) <= 'z')) return false;
    while (p < end && (((*p | 32) >= 'a' && (*p | 32) <= 'z') || (*p >= '0' && *p <= '9') || *p == '+' || *p == '-' || *p == '.')) p++;
    if (p >= end || *p != ':') return false;
    url->scheme = net_strndup(s, (size_t)(p - s));
    for (char *c = url->scheme; *c; c++) if (*c >= 'A' && *c <= 'Z') *c += 32;
    p++;
    bool web = !strcmp(url->scheme, "http") || !strcmp(url->scheme, "https") || !strcmp(url->scheme, "ws") || !strcmp(url->scheme, "wss");
    /* Like .NET, web schemes accept backslashes as slashes. */
    net_buffer normal = {0};
    buffer_append(&normal, p, (size_t)(end - p));
    char *rest = buffer_cstring(&normal);
    if (web) for (char *c = rest; *c && *c != '?' && *c != '#'; c++) if (*c == '\\') *c = '/';
    const char *r = rest;
    url->port = url_default_port(url->scheme);
    if (r[0] == '/' && r[1] == '/') {
        r += 2;
        const char *authority = r;
        while (*r && *r != '/' && *r != '?' && *r != '#') r++;
        const char *authority_end = r;
        const char *at = NULL;
        for (const char *c = authority; c < authority_end; c++) if (*c == '@') at = c;
        url->userinfo = at ? net_strndup(authority, (size_t)(at - authority)) : net_strdup("");
        const char *h = at ? at + 1 : authority;
        const char *host_end, *port_start = NULL;
        if (*h == '[') {
            const char *close = memchr(h, ']', (size_t)(authority_end - h));
            if (!close) { buffer_free(&normal); return fail(url); }
            host_end = close + 1;
            if (host_end < authority_end) { if (*host_end != ':') { buffer_free(&normal); return fail(url); } port_start = host_end + 1; }
        } else {
            host_end = h;
            while (host_end < authority_end && *host_end != ':') host_end++;
            if (host_end < authority_end) port_start = host_end + 1;
        }
        if (host_end == h && web) { buffer_free(&normal); return fail(url); }
        url->host = net_strndup(h, (size_t)(host_end - h));
        for (char *c = url->host; *c; c++) {
            if (*c >= 'A' && *c <= 'Z') *c += 32;
            if ((unsigned char)*c <= 0x20 || strchr("<>\"{}|\\^`%", *c)) { buffer_free(&normal); return fail(url); }
        }
        if (port_start && port_start < authority_end) {
            long port = 0;
            for (const char *c = port_start; c < authority_end; c++) {
                if (*c < '0' || *c > '9') { buffer_free(&normal); return fail(url); }
                port = port * 10 + (*c - '0');
                if (port > 65535) { buffer_free(&normal); return fail(url); }
            }
            url->port = (int)port;
            url->explicit_port = true;
        }
    } else {
        if (web) { buffer_free(&normal); return fail(url); }
        url->host = net_strdup("");
        url->userinfo = net_strdup("");
    }
    const char *path = r;
    while (*r && *r != '?' && *r != '#') r++;
    net_buffer encoded = {0};
    append_encoded(&encoded, path, (size_t)(r - path));
    char *raw_path = buffer_take(&encoded, NULL);
    if (url->host[0] || raw_path[0] == '/') {
        char *clean = remove_dot_segments(raw_path);
        free(raw_path);
        raw_path = clean;
        if (!raw_path[0] && (web || url->host[0])) { free(raw_path); raw_path = net_strdup("/"); }
    }
    url->path = raw_path;
    const char *query = r;
    if (*r == '?') { r++; while (*r && *r != '#') r++; }
    append_encoded(&encoded, query, (size_t)(r - query));
    url->query = buffer_take(&encoded, NULL);
    append_encoded(&encoded, r, strlen(r));
    url->fragment = buffer_take(&encoded, NULL);
    buffer_free(&encoded);
    buffer_free(&normal);
    return true;
}

bool url_resolve(const net_url *base, const char *reference, net_url *out) {
    memset(out, 0, sizeof *out);
    /* An absolute reference stands alone. */
    const char *c = reference;
    if ((*c | 32) >= 'a' && (*c | 32) <= 'z') {
        while (((*c | 32) >= 'a' && (*c | 32) <= 'z') || (*c >= '0' && *c <= '9') || *c == '+' || *c == '-' || *c == '.') c++;
        if (*c == ':') return url_parse(reference, out);
    }
    net_buffer text = {0};
    if (reference[0] == '/' && reference[1] == '/') {
        buffer_format(&text, "%s:%s", base->scheme, reference);
    } else {
        char *origin = url_format(&(net_url){.scheme = base->scheme, .host = base->host, .port = base->port,
            .explicit_port = base->explicit_port, .path = "", .query = "", .fragment = "", .userinfo = ""}, false);
        buffer_text(&text, origin);
        free(origin);
        if (reference[0] == '/') buffer_text(&text, reference);
        else if (reference[0] == '?') { buffer_text(&text, base->path); buffer_text(&text, reference); }
        else if (reference[0] == '#') { buffer_text(&text, base->path); buffer_text(&text, base->query); buffer_text(&text, reference); }
        else if (!reference[0]) { buffer_text(&text, base->path); buffer_text(&text, base->query); }
        else {
            const char *slash = strrchr(base->path, '/');
            buffer_append(&text, base->path, slash ? (size_t)(slash - base->path) + 1 : 0);
            if (!slash) buffer_text(&text, "/");
            buffer_text(&text, reference);
        }
    }
    bool ok = url_parse(buffer_cstring(&text), out);
    buffer_free(&text);
    return ok;
}

char *url_format(const net_url *url, bool with_fragment) {
    net_buffer b = {0};
    buffer_format(&b, "%s:", url->scheme);
    if (url->host[0] || url_default_port(url->scheme) >= 0) {
        buffer_format(&b, "//%s", url->host);
        if (url->explicit_port && url->port != url_default_port(url->scheme)) buffer_format(&b, ":%d", url->port);
    }
    buffer_text(&b, url->path);
    buffer_text(&b, url->query);
    if (with_fragment) buffer_text(&b, url->fragment);
    char *text = buffer_take(&b, NULL);
    buffer_free(&b);
    return text;
}

char *url_origin(const net_url *url) {
    net_buffer b = {0};
    buffer_format(&b, "%s://%s", url->scheme, url->host);
    if (url->explicit_port && url->port != url_default_port(url->scheme)) buffer_format(&b, ":%d", url->port);
    char *text = buffer_take(&b, NULL);
    buffer_free(&b);
    return text;
}

char *url_hostname(const net_url *url) {
    size_t n = strlen(url->host);
    if (n >= 2 && url->host[0] == '[') return net_strndup(url->host + 1, n - 2);
    return net_strdup(url->host);
}

void url_escape(net_buffer *out, const char *text, size_t length) {
    static const char hex[] = "0123456789ABCDEF";
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)text[i];
        bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
        if (unreserved) buffer_append(out, &text[i], 1);
        else { char e[3] = {'%', hex[c >> 4], hex[c & 15]}; buffer_append(out, e, 3); }
    }
}

char *url_unescape(const char *text, size_t length, bool plus_is_space) {
    char *out = malloc(length + 1);
    size_t n = 0;
    for (size_t i = 0; i < length; i++) {
        if (text[i] == '%' && i + 2 < length && is_hex(text[i + 1]) && is_hex(text[i + 2])) {
            out[n++] = (char)(hex_value(text[i + 1]) * 16 + hex_value(text[i + 2]));
            i += 2;
        } else out[n++] = plus_is_space && text[i] == '+' ? ' ' : text[i];
    }
    out[n] = 0;
    return out;
}
