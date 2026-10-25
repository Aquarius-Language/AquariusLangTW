/* The cookie container of a native HTTP client (RFC 6265): Set-Cookie with Expires, Max-Age, Domain, Path, Secure
 * and HttpOnly; Cookie headers for matching requests, longest paths first. Cookies live in memory only, as before. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define COOKIE_LIMIT 3000

typedef struct cookie {
    char *name, *value, *domain, *path;
    bool host_only, secure, persistent;
    int64_t expires, created;
} cookie;

struct cookie_jar {
    net_mutex lock;
    cookie *items;
    uint32_t count, capacity;
    int64_t sequence;
};

cookie_jar *jar_new(void) {
    cookie_jar *j = calloc(1, sizeof *j);
    net_mutex_init(&j->lock);
    return j;
}

static void cookie_free(cookie *c) { free(c->name); free(c->value); free(c->domain); free(c->path); }

void jar_clear(cookie_jar *j) {
    net_lock(&j->lock);
    for (uint32_t i = 0; i < j->count; i++) cookie_free(&j->items[i]);
    j->count = 0;
    net_unlock(&j->lock);
}

void jar_free(cookie_jar *j) {
    if (!j) return;
    jar_clear(j);
    free(j->items);
    net_mutex_destroy(&j->lock);
    free(j);
}

static bool is_ip(const char *host) {
    if (strchr(host, ':')) return true;
    for (const char *p = host; *p; p++) if ((*p < '0' || *p > '9') && *p != '.') return false;
    return true;
}

static bool domain_match(const char *host, const char *domain) {
    if (net_iequal(host, domain)) return true;
    size_t h = strlen(host), d = strlen(domain);
    return h > d && host[h - d - 1] == '.' && net_iequal(host + h - d, domain) && !is_ip(host);
}

static bool path_match(const char *request, const char *path) {
    size_t n = strlen(path);
    if (strncmp(request, path, n) != 0) return false;
    return request[n] == 0 || path[n - 1] == '/' || request[n] == '/';
}

/* RFC 6265 5.1.1: the date's time, day, month and year tokens in any order. */
static bool cookie_date(const char *text, int64_t *out) {
    int hour = -1, minute = 0, second = 0, day = -1, month = -1, year = -1;
    static const char *const months = "janfebmaraprmayjunjulaugsepoctnovdec";
    const char *p = text;
    while (*p) {
        while (*p && !((*p >= '0' && *p <= '9') || ((*p | 32) >= 'a' && (*p | 32) <= 'z') || *p == ':')) p++;
        const char *start = p;
        while (*p && ((*p >= '0' && *p <= '9') || ((*p | 32) >= 'a' && (*p | 32) <= 'z') || *p == ':')) p++;
        size_t n = (size_t)(p - start);
        if (!n) break;
        int h, m, s;
        char token[32];
        if (n >= sizeof token) continue;
        memcpy(token, start, n);
        token[n] = 0;
        if (hour < 0 && sscanf(token, "%d:%d:%d", &h, &m, &s) == 3) { hour = h; minute = m; second = s; continue; }
        if (day < 0 && n <= 2 && token[0] >= '0' && token[0] <= '9') { day = atoi(token); continue; }
        if (month < 0 && n >= 3) {
            char lower3[4] = {(char)(token[0] | 32), (char)(token[1] | 32), (char)(token[2] | 32), 0};
            const char *found = strstr(months, lower3);
            if (found && (found - months) % 3 == 0) { month = (int)(found - months) / 3; continue; }
        }
        if (year < 0 && (n == 2 || n == 4) && token[0] >= '0' && token[0] <= '9') {
            year = atoi(token);
            if (n == 2) year += year < 70 ? 2000 : 1900;
        }
    }
    if (hour < 0 || day < 1 || day > 31 || month < 0 || year < 1601 || hour > 23 || minute > 59 || second > 59) return false;
    /* Days from the civil date (Howard Hinnant's algorithm). */
    int y = year - (month < 2);
    int era = (y >= 0 ? y : y - 399) / 400;
    int yoe = y - era * 400;
    int mp = (month + 9) % 12;
    int doy = (153 * mp + 2) / 5 + day - 1;
    int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = (int64_t)era * 146097 + doe - 719468;
    *out = days * 86400 + hour * 3600 + minute * 60 + second;
    return true;
}

static char *trimmed(const char *start, const char *end) {
    while (start < end && (*start == ' ' || *start == '\t')) start++;
    while (end > start && (end[-1] == ' ' || end[-1] == '\t')) end--;
    return net_strndup(start, (size_t)(end - start));
}

void jar_store(cookie_jar *j, const net_url *url, const char *header) {
    const char *semicolon = strchr(header, ';');
    const char *pair_end = semicolon ? semicolon : header + strlen(header);
    const char *equal = memchr(header, '=', (size_t)(pair_end - header));
    if (!equal) return;
    cookie c = {0};
    c.name = trimmed(header, equal);
    c.value = trimmed(equal + 1, pair_end);
    if (!c.name[0]) { cookie_free(&c); return; }
    char *host = url_hostname(url);
    int64_t now = (int64_t)time(NULL);
    bool max_age = false;
    for (const char *p = semicolon; p && *p;) {
        p++;
        const char *end = strchr(p, ';');
        if (!end) end = p + strlen(p);
        const char *eq = memchr(p, '=', (size_t)(end - p));
        char *key = trimmed(p, eq ? eq : end);
        char *value = eq ? trimmed(eq + 1, end) : net_strdup("");
        if (net_iequal(key, "Max-Age")) {
            char *rest;
            long long seconds = strtoll(value, &rest, 10);
            if (*value && !*rest) { c.expires = seconds <= 0 ? 0 : now + seconds; c.persistent = true; max_age = true; }
        } else if (net_iequal(key, "Expires") && !max_age) {
            int64_t when;
            if (cookie_date(value, &when)) { c.expires = when; c.persistent = true; }
        } else if (net_iequal(key, "Domain") && value[0]) {
            free(c.domain);
            c.domain = net_strdup(value[0] == '.' ? value + 1 : value);
            for (char *d = c.domain; *d; d++) if (*d >= 'A' && *d <= 'Z') *d += 32;
        } else if (net_iequal(key, "Path")) {
            free(c.path);
            c.path = value[0] == '/' ? net_strdup(value) : NULL;
        } else if (net_iequal(key, "Secure")) c.secure = true;
        free(key);
        free(value);
        p = end;
    }
    if (c.domain) {
        if (!domain_match(host, c.domain)) { free(host); cookie_free(&c); return; }
    } else { c.domain = net_strdup(host); c.host_only = true; }
    free(host);
    if (!c.path) {
        const char *last = strrchr(url->path, '/');
        c.path = last && last != url->path ? net_strndup(url->path, (size_t)(last - url->path)) : net_strdup("/");
    }
    net_lock(&j->lock);
    c.created = j->sequence++;
    for (uint32_t i = 0; i < j->count; i++) {
        cookie *old = &j->items[i];
        if (strcmp(old->name, c.name) || strcmp(old->domain, c.domain) || strcmp(old->path, c.path)) continue;
        c.created = old->created;
        cookie_free(old);
        memmove(old, old + 1, (j->count - i - 1) * sizeof *old);
        j->count--;
        break;
    }
    bool expired = c.persistent && c.expires <= now;
    if (expired) { cookie_free(&c); net_unlock(&j->lock); return; }
    if (j->count >= COOKIE_LIMIT) { cookie_free(&j->items[0]); memmove(j->items, j->items + 1, (j->count - 1) * sizeof *j->items); j->count--; }
    if (j->count == j->capacity) {
        uint32_t capacity = j->capacity ? j->capacity * 2 : 16;
        cookie *items = realloc(j->items, capacity * sizeof *items);
        if (!items) { cookie_free(&c); net_unlock(&j->lock); return; }
        j->items = items;
        j->capacity = capacity;
    }
    j->items[j->count++] = c;
    net_unlock(&j->lock);
}

char *jar_header(cookie_jar *j, const net_url *url) {
    char *host = url_hostname(url);
    bool secure = !strcmp(url->scheme, "https") || !strcmp(url->scheme, "wss");
    int64_t now = (int64_t)time(NULL);
    net_lock(&j->lock);
    uint32_t kept = 0;
    for (uint32_t i = 0; i < j->count; i++) {
        if (j->items[i].persistent && j->items[i].expires <= now) { cookie_free(&j->items[i]); continue; }
        j->items[kept++] = j->items[i];
    }
    j->count = kept;
    cookie **matches = malloc((j->count + 1) * sizeof *matches);
    uint32_t n = 0;
    for (uint32_t i = 0; i < j->count; i++) {
        cookie *c = &j->items[i];
        bool domain = c->host_only ? net_iequal(host, c->domain) : domain_match(host, c->domain);
        if (domain && path_match(url->path, c->path) && (!c->secure || secure)) matches[n++] = c;
    }
    /* Longer paths first, then older cookies. */
    for (uint32_t i = 1; i < n; i++) {
        cookie *c = matches[i];
        uint32_t k = i;
        while (k > 0) {
            size_t a = strlen(matches[k - 1]->path), b = strlen(c->path);
            if (a > b || (a == b && matches[k - 1]->created <= c->created)) break;
            matches[k] = matches[k - 1];
            k--;
        }
        matches[k] = c;
    }
    net_buffer header = {0};
    for (uint32_t i = 0; i < n; i++) {
        if (i) buffer_text(&header, "; ");
        buffer_format(&header, "%s=%s", matches[i]->name, matches[i]->value);
    }
    net_unlock(&j->lock);
    free(matches);
    free(host);
    char *text = n ? buffer_take(&header, NULL) : NULL;
    buffer_free(&header);
    return text;
}
