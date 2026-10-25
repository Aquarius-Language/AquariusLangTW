/* Paths: lexical slash paths with "/", drive ("C:/") and UNC ("//server/share/") roots. No file system access, no
 * symbolic link resolution, no authorization: Normalize, Join, Name, Extension, Parent and Relative. */
#include "application.h"
#include <stdlib.h>
#include <string.h>

typedef struct path { char *text; size_t length; } path;

static void path_free(path *p) { free(p->text); p->text = NULL; }

/* Normalizes `input` into `out` (a malloc'd string). */
static bool normalize(const char *input, size_t length, path *out, app_error *e) {
    if (memchr(input, 0, length)) return app_error_set(e, APP_INVALID_DATA, "Path contains NUL.");
    char *s = app_strndup(input, length);
    for (size_t i = 0; i < length; i++) if (s[i] == '\\') s[i] = '/';
    app_bytes result = {0};
    const char *rest = s;
    if (length >= 2 && s[0] == '/' && s[1] == '/') {
        /* //server/share: the first two non-empty pieces form the root. */
        const char *pieces[2]; size_t sizes[2]; int found = 0;
        const char *p = s + 2;
        while (*p && found < 2) {
            while (*p == '/') p++;
            if (!*p) break;
            const char *start = p;
            while (*p && *p != '/') p++;
            pieces[found] = start; sizes[found] = (size_t)(p - start); found++;
        }
        if (found < 2) { free(s); return app_error_set(e, APP_INVALID_DATA, "UNC path requires server and share."); }
        app_bytes_text(&result, "//");
        app_bytes_append(&result, pieces[0], sizes[0]);
        app_bytes_byte(&result, '/');
        app_bytes_append(&result, pieces[1], sizes[1]);
        app_bytes_byte(&result, '/');
        rest = p;
    } else if (length >= 2 && ((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z')) && s[1] == ':') {
        if (length < 3 || s[2] != '/') { free(s); return app_error_set(e, APP_INVALID_DATA, "Drive-relative paths are ambiguous."); }
        char root[3] = {(char)(s[0] >= 'a' ? s[0] - 32 : s[0]), ':', '/'};
        app_bytes_append(&result, root, 3);
        rest = s + 3;
    } else if (length >= 1 && s[0] == '/') {
        app_bytes_byte(&result, '/');
        rest = s + 1;
    }
    size_t root_length = result.length;
    /* Components: "." skipped, ".." removes the previous component, or stays when the path is relative. */
    const char **parts = malloc((length / 2 + 2) * sizeof *parts);
    size_t *sizes = malloc((length / 2 + 2) * sizeof *sizes);
    size_t count = 0;
    for (const char *p = rest; *p;) {
        while (*p == '/') p++;
        if (!*p) break;
        const char *start = p;
        while (*p && *p != '/') p++;
        size_t n = (size_t)(p - start);
        if (n == 1 && start[0] == '.') continue;
        if (n == 2 && start[0] == '.' && start[1] == '.') {
            if (count > 0 && !(sizes[count - 1] == 2 && parts[count - 1][0] == '.' && parts[count - 1][1] == '.')) { count--; continue; }
            if (root_length) continue;
        }
        parts[count] = start; sizes[count] = n; count++;
    }
    for (size_t i = 0; i < count; i++) {
        if (i) app_bytes_byte(&result, '/');
        app_bytes_append(&result, parts[i], sizes[i]);
    }
    free(parts); free(sizes); free(s);
    if (result.length == 0) app_bytes_byte(&result, '.');
    app_bytes_byte(&result, 0);
    out->text = (char *)result.data;
    out->length = result.length - 1;
    return true;
}

/* Length of the root of a normalized path: "//server/share/", "/", "C:/" or none. */
static size_t root_length(const path *p) {
    const char *s = p->text;
    if (p->length >= 2 && s[0] == '/' && s[1] == '/') {
        int slashes = 0;
        for (size_t i = 0; i < p->length; i++) if (s[i] == '/' && ++slashes == 4) return i + 1;
        return p->length; /* "//server/share" was normalized with its trailing slash */
    }
    if (p->length >= 1 && s[0] == '/') return 1;
    if (p->length >= 3 && s[1] == ':' && s[2] == '/') return 3;
    return 0;
}

static bool string_argument(aqua_call *call, uint32_t index, path *out) {
    app_error e = {0};
    aqua_str text = aqua_arg_str(call, index);
    if (normalize(text.data, text.length, out, &e)) return true;
    app_fail(call, &e);
    return false;
}

static void return_path(aqua_call *call, path *p) { aqua_return_text(call, p->text, p->length); path_free(p); }

void app_paths_Normalize(aqua_call *call) {
    path p;
    if (string_argument(call, 0, &p)) return_path(call, &p);
}

void app_paths_Join(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value parts = aqua_arg(call, 0);
    uint32_t count = aqua_array_length(rt, parts);
    app_bytes joined = {0};
    app_error e = {0};
    for (uint32_t i = 0; i < count; i++) {
        aqua_value part = aqua_array_get(rt, parts, i);
        if (part.type != AQUA_STRING) { app_bytes_free(&joined); app_fail_with(call, APP_INVALID_DATA, "Expected a string."); return; }
        aqua_str text = aqua_str_of(call, part);
        path n;
        if (!normalize(text.data, text.length, &n, &e)) { app_bytes_free(&joined); app_fail(call, &e); return; }
        if (root_length(&n) > 0 || joined.length == 0) joined.length = 0;
        else app_bytes_byte(&joined, '/');
        app_bytes_append(&joined, n.text, n.length);
        path_free(&n);
    }
    path result;
    bool ok = normalize(joined.data ? (const char *)joined.data : "", joined.length, &result, &e);
    app_bytes_free(&joined);
    if (!ok) { app_fail(call, &e); return; }
    return_path(call, &result);
}

static void name_of(const path *p, const char **start, size_t *length) {
    size_t root = root_length(p);
    if (p->length == root || (p->length == 1 && p->text[0] == '.')) { *start = ""; *length = 0; return; }
    const char *slash = strrchr(p->text, '/');
    *start = slash ? slash + 1 : p->text;
    *length = p->length - (size_t)(*start - p->text);
}

void app_paths_Name(aqua_call *call) {
    path p;
    if (!string_argument(call, 0, &p)) return;
    const char *start; size_t length;
    name_of(&p, &start, &length);
    aqua_return_text(call, start, length);
    path_free(&p);
}

void app_paths_Extension(aqua_call *call) {
    path p;
    if (!string_argument(call, 0, &p)) return;
    const char *start; size_t length;
    name_of(&p, &start, &length);
    size_t dot = length;
    for (size_t i = length; i-- > 0;) if (start[i] == '.') { dot = i; break; }
    if (dot == length || dot == 0) aqua_return_text(call, "", 0);
    else aqua_return_text(call, start + dot, length - dot);
    path_free(&p);
}

void app_paths_Parent(aqua_call *call) {
    path p;
    if (!string_argument(call, 0, &p)) return;
    size_t root = root_length(&p);
    if (p.length == root) { return_path(call, &p); return; }
    const char *slash = strrchr(p.text, '/');
    if (!slash) aqua_return_text(call, ".", 1);
    else if ((size_t)(slash - p.text) < root) aqua_return_text(call, p.text, root);
    else aqua_return_text(call, p.text, (size_t)(slash - p.text));
    path_free(&p);
}

/* The non-empty, non-"." components of a normalized path, including its root's pieces ("C:", server, share). */
static size_t components(const path *p, const char **parts, size_t *sizes) {
    size_t count = 0;
    for (const char *s = p->text; *s;) {
        while (*s == '/') s++;
        if (!*s) break;
        const char *start = s;
        while (*s && *s != '/') s++;
        size_t n = (size_t)(s - start);
        if (n == 1 && start[0] == '.') continue;
        parts[count] = start; sizes[count] = n; count++;
    }
    return count;
}

void app_paths_Relative(aqua_call *call) {
    path from, target;
    if (!string_argument(call, 0, &from)) return;
    if (!string_argument(call, 1, &target)) { path_free(&from); return; }
    size_t a_root = root_length(&from), b_root = root_length(&target);
    if (a_root != b_root || memcmp(from.text, target.text, a_root) != 0) {
        path_free(&from); path_free(&target);
        app_fail_with(call, APP_INVALID_DATA, "Paths have different roots.");
        return;
    }
    const char **a = malloc((from.length + 2) * sizeof *a), **b = malloc((target.length + 2) * sizeof *b);
    size_t *as = malloc((from.length + 2) * sizeof *as), *bs = malloc((target.length + 2) * sizeof *bs);
    size_t na = components(&from, a, as), nb = components(&target, b, bs), i = 0;
    while (i < na && i < nb && as[i] == bs[i] && memcmp(a[i], b[i], as[i]) == 0) i++;
    app_bytes out = {0};
    for (size_t k = i; k < na; k++) { if (out.length) app_bytes_byte(&out, '/'); app_bytes_text(&out, ".."); }
    for (size_t k = i; k < nb; k++) { if (out.length) app_bytes_byte(&out, '/'); app_bytes_append(&out, b[k], bs[k]); }
    if (out.length == 0) aqua_return_text(call, ".", 1);
    else aqua_return_text(call, (const char *)out.data, out.length);
    app_bytes_free(&out);
    free(a); free(b); free(as); free(bs);
    path_free(&from); path_free(&target);
}
