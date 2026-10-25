/* Reading the paths scripts pass (STBImage.Load, GL.ReadText) and writing captures (GL.SavePPM). A path into the
 * application (目前工作目錄 + "/checker.ppm", or relative to it) names a packaged asset when the application has one;
 * anything else is a file. Paths are UTF-8 on every platform. */
#include "opengl.h"
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#include <direct.h>
#include <windows.h>
#else
#include <sys/stat.h>
#endif

static char *message(const char *what, const char *path, const char *reason) {
    size_t n = strlen(what) + strlen(path) + strlen(reason) + 8;
    char *text = aqua_alloc(n);
    snprintf(text, n, "%s %s: %s", what, path, reason);
    return text;
}

static FILE *open_file(const char *path, const char *mode) {
#if defined(AQUA_OS_WINDOWS)
    wchar_t wide_path[4096], wide_mode[8];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide_path, 4096) || !MultiByteToWideChar(CP_UTF8, 0, mode, -1, wide_mode, 8)) return NULL;
    return _wfopen(wide_path, wide_mode);
#else
    return fopen(path, mode);
#endif
}

static void make_directory(const char *path) {
#if defined(AQUA_OS_WINDOWS)
    wchar_t wide[4096];
    if (MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, 4096)) _wmkdir(wide);
#else
    mkdir(path, 0777);
#endif
}

uint8_t *opengl_read(aqua_runtime *rt, const char *path, size_t *length, char **error) {
    size_t n = strlen(path);
    char *name = aqua_alloc(n + 1);
    for (size_t i = 0; i <= n; i++) name[i] = path[i] == '\\' ? '/' : path[i];
    const char *root = aqua_application_directory(rt);
    size_t root_length = strlen(root);
    const char *asset_name = root_length && strncmp(name, root, root_length) == 0 && name[root_length] == '/' ? name + root_length + 1 : name;
    size_t size = 0;
    const uint8_t *asset = aqua_asset(rt, asset_name, &size);
    aqua_free(name);
    if (asset) {
        uint8_t *copy = aqua_alloc(size ? size : 1);
        memcpy(copy, asset, size);
        *length = size;
        return copy;
    }
    FILE *file = open_file(path, "rb");
    if (!file) { *error = message("Cannot open", path, strerror(errno)); return NULL; }
    uint8_t *bytes = NULL;
    size_t used = 0, capacity = 0;
    for (;;) {
        if (used == capacity) {
            capacity = capacity ? capacity * 2 : 65536;
            bytes = aqua_realloc(bytes, capacity);
        }
        size_t got = fread(bytes + used, 1, capacity - used, file);
        used += got;
        if (got == 0) break;
    }
    bool failed = ferror(file) != 0;
    fclose(file);
    if (failed) { aqua_free(bytes); *error = message("Cannot read", path, "read error"); return NULL; }
    *length = used;
    return bytes;
}

bool opengl_write(const char *path, const void *bytes, size_t length, char **error) {
    /* Parent directories first, as Directory.CreateDirectory did. */
    size_t n = strlen(path);
    char *prefix = aqua_alloc(n + 1);
    memcpy(prefix, path, n + 1);
    for (size_t i = 1; i < n; i++) {
        if (prefix[i] != '/' && prefix[i] != '\\') continue;
        if (prefix[i - 1] == ':' || prefix[i - 1] == '/' || prefix[i - 1] == '\\') continue;
        char separator = prefix[i];
        prefix[i] = 0;
        make_directory(prefix);
        prefix[i] = separator;
    }
    aqua_free(prefix);
    FILE *file = open_file(path, "wb");
    if (!file) { *error = message("Cannot create", path, strerror(errno)); return false; }
    bool ok = fwrite(bytes, 1, length, file) == length;
    ok = fclose(file) == 0 && ok;
    if (!ok) *error = message("Cannot write", path, "write error");
    return ok;
}
