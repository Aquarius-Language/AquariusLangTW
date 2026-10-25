#include "files.h"
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#include <windows.h>
#include <direct.h>
#elif !defined(AQUA_OS_WEB)
#include <errno.h>
#include <sys/stat.h>
#endif

/* The package path of `path`, or NULL when it names something outside the package. */
static char *package_path(aqua_runtime *rt, const char *path) {
    size_t length = strlen(path);
    char *p = malloc(length + 1);
    for (size_t i = 0; i <= length; i++) p[i] = path[i] == '\\' ? '/' : path[i];
    if (strncmp(p, "/bottles/", 9) == 0) { memmove(p, p + 1, length); return p; }
    const char *root = aqua_application_directory(rt);
    size_t root_length = root ? strlen(root) : 0;
    while (root_length && (root[root_length - 1] == '/' || root[root_length - 1] == '\\')) root_length--;
    if (root_length && strncmp(p, root, root_length) == 0 && p[root_length] == '/') {
        memmove(p, p + root_length + 1, length - root_length);
        return p;
    }
    bool absolute = p[0] == '/' || (p[0] && p[1] == ':');
    if (absolute) { free(p); return NULL; }
    char *q = p;
    while (q[0] == '.' && q[1] == '/') q += 2;
    if (q != p) memmove(p, q, strlen(q) + 1);
    return p;
}

#if !defined(AQUA_OS_WEB)
static FILE *open_file(const char *path, const char *mode) {
#if defined(AQUA_OS_WINDOWS)
    wchar_t wide[4096], wide_mode[8];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, 4096)) return NULL;
    MultiByteToWideChar(CP_UTF8, 0, mode, -1, wide_mode, 8);
    return _wfopen(wide, wide_mode);
#else
    return fopen(path, mode);
#endif
}
#endif

bool gfx_read_resource(aqua_runtime *rt, const char *path, gfx_bytes *out, char *error, size_t error_size) {
    memset(out, 0, sizeof *out);
    char *asset = package_path(rt, path);
    if (asset) {
        size_t length = 0;
        const uint8_t *data = aqua_asset(rt, asset, &length);
        free(asset);
        if (data) { out->data = data; out->length = length; return true; }
    }
#if defined(AQUA_OS_WEB)
    snprintf(error, error_size, "Could not find file '%s'.", path);
    return false;
#else
    FILE *file = open_file(path, "rb");
    if (!file) {
        /* A relative path that is no asset is read relative to the application directory, like packaged scripts. */
        const char *root = aqua_application_directory(rt);
        bool absolute = path[0] == '/' || path[0] == '\\' || (path[0] && path[1] == ':');
        if (!absolute && root && *root) {
            size_t n = strlen(root) + strlen(path) + 2;
            char *full = malloc(n);
            snprintf(full, n, "%s/%s", root, path);
            file = open_file(full, "rb");
            free(full);
        }
    }
    if (!file) { snprintf(error, error_size, "Could not find file '%s'.", path); return false; }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    if (size < 0 || size > 1024L * 1024 * 1024) { fclose(file); snprintf(error, error_size, "Could not read file '%s'.", path); return false; }
    uint8_t *data = malloc((size_t)size + 1);
    size_t read = fread(data, 1, (size_t)size, file);
    fclose(file);
    if (read != (size_t)size) { free(data); snprintf(error, error_size, "Could not read file '%s'.", path); return false; }
    out->data = data; out->length = (size_t)size; out->owned = true;
    return true;
#endif
}

void gfx_bytes_free(gfx_bytes *bytes) {
    if (bytes->owned) free((void *)bytes->data);
    memset(bytes, 0, sizeof *bytes);
}

#if !defined(AQUA_OS_WEB)
static void make_directories(const char *path) {
    char *copy = malloc(strlen(path) + 1);
    strcpy(copy, path);
    for (char *p = copy + 1; *p; p++) {
        if (*p != '/' && *p != '\\') continue;
        char saved = *p;
        *p = 0;
        if (!(p - copy == 2 && copy[1] == ':')) {
#if defined(AQUA_OS_WINDOWS)
            wchar_t wide[4096];
            if (MultiByteToWideChar(CP_UTF8, 0, copy, -1, wide, 4096)) CreateDirectoryW(wide, NULL);
#else
            mkdir(copy, 0777);
#endif
        }
        *p = saved;
    }
    free(copy);
}
#endif

#if defined(AQUA_OS_WEB)
/* The files glue domain's download (web-glue/files): the browser saves the bytes under the file's name. */
AQUA_WEB_IMPORT(files_download) void web_download(const char *name, int32_t name_length, const uint8_t *bytes, int32_t length);
#endif

bool gfx_write_file(const char *path, const uint8_t *data, size_t length, char *error, size_t error_size) {
#if defined(AQUA_OS_WEB)
    (void)error; (void)error_size;
    const char *name = path;
    for (const char *p = path; *p; p++) if (*p == '/' || *p == '\\') name = p + 1;
    web_download(name, (int32_t)strlen(name), data, (int32_t)length);
    return true;
#else
    make_directories(path);
    FILE *file = open_file(path, "wb");
    if (!file) { snprintf(error, error_size, "Could not write file '%s'.", path); return false; }
    bool ok = fwrite(data, 1, length, file) == length;
    ok = fclose(file) == 0 && ok;
    if (!ok) snprintf(error, error_size, "Could not write file '%s'.", path);
    return ok;
#endif
}

const char *gfx_extension(const char *path, char *buffer, size_t size) {
    const char *dot = NULL;
    for (const char *p = path; *p; p++) {
        if (*p == '.') dot = p;
        else if (*p == '/' || *p == '\\') dot = NULL;
    }
    size_t n = 0;
    if (dot) for (; dot[n] && n + 1 < size; n++) buffer[n] = (char)tolower((unsigned char)dot[n]);
    buffer[n] = 0;
    return buffer;
}
