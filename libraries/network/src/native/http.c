/* HTTP/1.1 messages on a connection (RFC 9112): heads up to 64 KiB, bodies framed by Content-Length, chunked
 * transfer coding or (responses) the end of the connection. Shared by the client (transfer.c), the server (server.c)
 * and WebSocket handshakes (frames.c). */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

void http_head_free(http_head *h) {
    free(h->method);
    free(h->target);
    headers_free(&h->headers);
    memset(h, 0, sizeof *h);
}

static const char *find_end(const uint8_t *data, size_t length) {
    for (size_t i = 0; i + 3 < length; i++)
        if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return (const char *)data + i + 4;
    /* Tolerate bare LF line ends. */
    for (size_t i = 0; i + 1 < length; i++)
        if (data[i] == '\n' && data[i + 1] == '\n') return (const char *)data + i + 2;
    return NULL;
}

static bool parse_head(char *text, bool request, http_head *h) {
    char *line = text, *next;
    char *eol = strchr(line, '\n');
    if (!eol) return false;
    *eol = 0;
    if (eol > line && eol[-1] == '\r') eol[-1] = 0;
    next = eol + 1;
    if (request) {
        char *space = strchr(line, ' ');
        if (!space) return false;
        h->method = net_strndup(line, (size_t)(space - line));
        char *target = space + 1;
        char *space2 = strchr(target, ' ');
        if (!space2 || strncmp(space2 + 1, "HTTP/1.", 7) != 0) return false;
        h->target = net_strndup(target, (size_t)(space2 - target));
        h->minor = space2[8] == '0' ? 0 : 1;
        if (!*h->method || !*h->target) return false;
    } else {
        if (strncmp(line, "HTTP/1.", 7) != 0 || !line[7]) return false;
        h->minor = line[7] == '0' ? 0 : 1;
        const char *status = line + 8;
        while (*status == ' ') status++;
        if (status[0] < '1' || status[0] > '9' || status[1] < '0' || status[1] > '9' || status[2] < '0' || status[2] > '9') return false;
        h->status = (status[0] - '0') * 100 + (status[1] - '0') * 10 + (status[2] - '0');
    }
    for (line = next; *line; line = next) {
        eol = strchr(line, '\n');
        if (!eol) eol = line + strlen(line);
        next = *eol ? eol + 1 : eol;
        *eol = 0;
        if (eol > line && eol[-1] == '\r') eol[-1] = 0;
        if (!*line) break;
        if (*line == ' ' || *line == '\t') {
            /* Obsolete line folding joins the previous value. */
            if (!h->headers.count) return false;
            net_header *last = &h->headers.items[h->headers.count - 1];
            net_buffer joined = {0};
            buffer_text(&joined, last->value);
            buffer_text(&joined, " ");
            while (*line == ' ' || *line == '\t') line++;
            buffer_text(&joined, line);
            free(last->value);
            last->value = buffer_take(&joined, NULL);
            buffer_free(&joined);
            continue;
        }
        char *colon = strchr(line, ':');
        if (!colon || colon == line) return false;
        *colon = 0;
        if (!header_valid(line, NULL)) return false;
        char *value = colon + 1;
        while (*value == ' ' || *value == '\t') value++;
        size_t n = strlen(value);
        while (n && (value[n - 1] == ' ' || value[n - 1] == '\t')) value[--n] = 0;
        headers_add(&h->headers, line, value);
    }
    return true;
}

int http_read_head(net_conn *c, net_buffer *in, bool request, http_head *out, const net_io *io, net_error *e) {
    memset(out, 0, sizeof *out);
    /* Empty lines before a request are ignored (RFC 9112 2.2). */
    for (;;) {
        while (in->length >= 2 && in->data[0] == '\r' && in->data[1] == '\n') buffer_consume(in, 2);
        while (in->length >= 1 && in->data[0] == '\n') buffer_consume(in, 1);
        const char *end = find_end(in->data, in->length);
        if (end) {
            size_t length = (size_t)((const uint8_t *)end - in->data);
            char *text = net_strndup((const char *)in->data, length);
            buffer_consume(in, length);
            bool ok = parse_head(text, request, out);
            free(text);
            if (!ok) { http_head_free(out); net_error_set(e, NET_NETWORK, false, "Invalid HTTP message / HTTP 訊息格式錯誤。"); return -1; }
            return 1;
        }
        if (in->length > HTTP_HEAD_LIMIT) { net_error_set(e, NET_LIMIT, false, "LimitExceeded / HTTP 標頭過大。"); return -1; }
        if (!buffer_reserve(in, 16384)) { net_error_set(e, NET_LIMIT, false, "LimitExceeded / HTTP 標頭過大。"); return -1; }
        long n = conn_read(c, in->data + in->length, in->capacity - in->length - 1, io, e);
        if (n < 0) return -1;
        if (n == 0) {
            if (in->length == 0) return 0;
            net_error_set(e, NET_NETWORK, true, "The connection closed during the HTTP header / HTTP 標頭傳輸中連線已關閉。");
            return -1;
        }
        in->length += (size_t)n;
        in->data[in->length] = 0;
    }
}

bool http_body_start(http_body *b, const http_head *h, bool request, const char *method) {
    memset(b, 0, sizeof *b);
    if (!request && (!strcmp(method, "HEAD") || h->status == 204 || h->status == 304 || (h->status >= 100 && h->status < 200))) {
        b->mode = BODY_NONE; b->finished = true; return true;
    }
    const char *encoding = headers_get(&h->headers, "Transfer-Encoding");
    if (encoding && net_itoken(encoding, "chunked")) { b->mode = BODY_CHUNKED; return true; }
    const char *length = headers_get(&h->headers, "Content-Length");
    if (length) {
        uint64_t n = 0;
        const char *p = length;
        if (!*p) return false;
        for (; *p >= '0' && *p <= '9'; p++) { n = n * 10 + (uint64_t)(*p - '0'); if (n > (1ull << 50)) return false; }
        while (*p == ' ') p++;
        if (*p && *p != ',') return false;
        b->mode = BODY_LENGTH;
        b->remaining = n;
        b->finished = n == 0;
        return true;
    }
    if (request) { b->mode = BODY_NONE; b->finished = true; return true; }
    b->mode = BODY_CLOSE;
    return true;
}

/* Fills `in` with more bytes; false at the end of the stream (`*eof`) or on failure. */
static bool fill(net_conn *c, net_buffer *in, const net_io *io, net_error *e, bool *eof) {
    if (!buffer_reserve(in, 16384)) return net_error_set(e, NET_LIMIT, false, MSG_BODY_LIMIT);
    long n = conn_read(c, in->data + in->length, in->capacity - in->length - 1, io, e);
    if (n < 0) return false;
    if (n == 0) { *eof = true; return false; }
    in->length += (size_t)n;
    in->data[in->length] = 0;
    return true;
}

static long fail_truncated(net_error *e) {
    net_error_set(e, NET_NETWORK, true, "The connection closed before the body ended / 本文傳輸中連線已關閉。");
    return -1;
}

long http_body_read(http_body *b, net_conn *c, net_buffer *in, net_buffer *out, const net_io *io, net_error *e) {
    bool eof = false;
    for (;;) {
        if (b->finished) return 0;
        switch (b->mode) {
        case BODY_NONE: b->finished = true; return 0;
        case BODY_LENGTH: case BODY_CLOSE: {
            if (in->length == 0 && !fill(c, in, io, e, &eof)) {
                if (eof && b->mode == BODY_CLOSE) { b->finished = true; return 0; }
                return eof ? fail_truncated(e) : -1;
            }
            size_t n = in->length;
            if (b->mode == BODY_LENGTH && n > b->remaining) n = (size_t)b->remaining;
            buffer_append(out, in->data, n);
            buffer_consume(in, n);
            if (b->mode == BODY_LENGTH) { b->remaining -= n; if (b->remaining == 0) b->finished = true; }
            return (long)n;
        }
        case BODY_CHUNKED:
            if (b->chunk_state == 0 || b->chunk_state == 2 || b->chunk_state == 3) {
                uint8_t *line_end = memchr(in->data ? in->data : (uint8_t *)"", '\n', in->length);
                if (!line_end) {
                    if (in->length > 8192) { net_error_set(e, NET_NETWORK, false, "Invalid chunked body / 分段本文格式錯誤。"); return -1; }
                    if (!fill(c, in, io, e, &eof)) return eof ? fail_truncated(e) : -1;
                    continue;
                }
                size_t line_length = (size_t)(line_end - in->data) + 1;
                if (b->chunk_state == 0) {
                    uint64_t size = 0;
                    size_t i = 0, digits = 0;
                    for (; i < line_length; i++) {
                        char ch = (char)in->data[i];
                        int d = ch >= '0' && ch <= '9' ? ch - '0' : (ch | 32) >= 'a' && (ch | 32) <= 'f' ? (ch | 32) - 'a' + 10 : -1;
                        if (d < 0) break;
                        size = size * 16 + (uint64_t)d;
                        if (++digits > 15) { net_error_set(e, NET_NETWORK, false, "Invalid chunked body / 分段本文格式錯誤。"); return -1; }
                    }
                    if (!digits) { net_error_set(e, NET_NETWORK, false, "Invalid chunked body / 分段本文格式錯誤。"); return -1; }
                    b->remaining = size;
                    b->chunk_state = size ? 1 : 3;
                } else if (b->chunk_state == 2) {
                    b->chunk_state = 0;
                } else {
                    /* Trailer fields until an empty line. */
                    bool empty = line_length == 1 || (line_length == 2 && in->data[0] == '\r');
                    if (empty) b->chunk_state = 4, b->finished = true;
                }
                buffer_consume(in, line_length);
                if (b->finished) return 0;
                continue;
            }
            if (in->length == 0 && !fill(c, in, io, e, &eof)) return eof ? fail_truncated(e) : -1;
            size_t n = in->length < b->remaining ? in->length : (size_t)b->remaining;
            buffer_append(out, in->data, n);
            buffer_consume(in, n);
            b->remaining -= n;
            if (b->remaining == 0) b->chunk_state = 2;
            return (long)n;
        }
    }
}

void http_date(char out[32], int64_t seconds) {
    static const char *const days[] = {"Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"};
    static const char *const months[] = {"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
    time_t t = (time_t)seconds;
    struct tm tm;
#if defined(AQUA_OS_WINDOWS)
    gmtime_s(&tm, &t);
#else
    gmtime_r(&t, &tm);
#endif
    snprintf(out, 32, "%s, %02d %s %04d %02d:%02d:%02d GMT", days[tm.tm_wday], tm.tm_mday, months[tm.tm_mon], tm.tm_year + 1900, tm.tm_hour, tm.tm_min, tm.tm_sec);
}
