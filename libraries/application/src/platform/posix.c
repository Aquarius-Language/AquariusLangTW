/* POSIX file system primitives (Linux, macOS and Android) and the desktop's program-run pickers. Paths are UTF-8 bytes; full paths are lexical, as .NET's
 * Path.GetFullPath on Unix (no symbolic link resolution). */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include "platform.h"
#if defined(AQUA_DESKTOP)
#include <aqua/window.h>
#endif
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

const char app_os_separator = '/';

static bool exists(const char *path) { struct stat s; return stat(path, &s) == 0; }

static char *parent_of(const char *path) {
    const char *slash = strrchr(path, '/');
    if (!slash) return app_strdup(".");
    if (slash == path) return app_strdup("/");
    return app_strndup(path, (size_t)(slash - path));
}

static bool fail(app_error *e, int code, const char *path) {
    switch (code) {
    case ENOENT: case ENOTDIR: {
        char *parent = parent_of(path);
        bool file = exists(parent);
        free(parent);
        return file ? app_error_set(e, APP_NOT_FOUND, "Could not find file '%s'.", path)
                    : app_error_set(e, APP_NOT_FOUND, "Could not find a part of the path '%s'.", path);
    }
    case EACCES: case EPERM: case EROFS: return app_error_set(e, APP_PERMISSION_DENIED, "Access to the path '%s' is denied.", path);
    case EEXIST: return app_error_set(e, APP_UNAVAILABLE, "The file '%s' already exists.", path);
    case ENOTEMPTY: return app_error_set(e, APP_UNAVAILABLE, "Directory not empty : '%s'", path);
    case EISDIR: return app_error_set(e, APP_PERMISSION_DENIED, "Access to the path '%s' is denied.", path);
    case ENOSPC: return app_error_set(e, APP_UNAVAILABLE, "No space left on device : '%s'", path);
    default: return app_error_set(e, APP_UNAVAILABLE, "%s : '%s'", strerror(code), path);
    }
}

char *app_os_full_path(const char *path, app_error *e) {
    if (!*path) { app_error_set(e, APP_INVALID_DATA, "The path is empty. (Parameter 'path')"); return NULL; }
    char *joined;
    if (path[0] == '/') joined = app_strdup(path);
    else {
        char directory[4096];
        if (!getcwd(directory, sizeof directory)) { fail(e, errno, path); return NULL; }
        joined = app_printf("%s/%s", directory, path);
    }
    /* Lexical normalization: "." and empty components dropped, ".." removes the previous component. */
    size_t n = strlen(joined), out = 0;
    char *result = malloc(n + 2);
    result[out++] = '/';
    for (size_t i = 0; i < n;) {
        while (i < n && joined[i] == '/') i++;
        size_t start = i;
        while (i < n && joined[i] != '/') i++;
        size_t length = i - start;
        if (length == 0 || (length == 1 && joined[start] == '.')) continue;
        if (length == 2 && joined[start] == '.' && joined[start + 1] == '.') {
            if (out > 1) { out--; while (out > 1 && result[out - 1] != '/') out--; if (out > 1) out--; }
            continue;
        }
        if (out > 1) result[out++] = '/';
        memcpy(result + out, joined + start, length);
        out += length;
    }
    result[out] = 0;
    free(joined);
    return result;
}

bool app_os_stat(const char *path, app_stat *out, app_error *e) {
    (void)e;
    memset(out, 0, sizeof *out);
    out->length = -1;
    struct stat s;
    if (stat(path, &s) != 0) return true;
    out->exists = true;
    out->kind = S_ISDIR(s.st_mode) ? APP_DIRECTORY : APP_FILE;
    if (out->kind == APP_FILE) out->length = (int64_t)s.st_size;
#if defined(AQUA_OS_MACOS) || defined(AQUA_OS_IOS)
    const struct timespec modified = s.st_mtimespec;   /* Apple's name for POSIX st_mtim */
#else
    const struct timespec modified = s.st_mtim;
#endif
    out->modified_ms = (double)modified.tv_sec * 1000.0 + (double)(modified.tv_nsec / 100) / 10000.0;
    out->has_modified = true;
    return true;
}

bool app_os_is_link(const char *path) { struct stat s; return lstat(path, &s) == 0 && S_ISLNK(s.st_mode); }

static int compare_names(const void *a, const void *b) { return strcmp(*(char *const *)a, *(char *const *)b); }

bool app_os_list(const char *path, char ***names, size_t *count, app_error *e) {
    *names = NULL;
    *count = 0;
    DIR *directory = opendir(path);
    if (!directory) return fail(e, errno, path);
    size_t capacity = 16;
    char **list = malloc(capacity * sizeof *list);
    struct dirent *entry;
    while ((entry = readdir(directory))) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;
        if (*count == capacity) { capacity *= 2; list = realloc(list, capacity * sizeof *list); }
        list[(*count)++] = app_strdup(entry->d_name);
    }
    closedir(directory);
    qsort(list, *count, sizeof *list, compare_names);
    *names = list;
    return true;
}

bool app_os_mkdirs(const char *path, app_error *e) {
    char *copy = app_strdup(path);
    size_t n = strlen(copy);
    bool ok = true;
    for (size_t i = 1; i <= n && ok; i++) {
        if (i < n && copy[i] != '/') continue;
        char saved = copy[i];
        copy[i] = 0;
        if (mkdir(copy, 0777) != 0) {
            int code = errno;
            struct stat s;
            if (code != EEXIST) ok = fail(e, code, copy);
            else if (stat(copy, &s) == 0 && !S_ISDIR(s.st_mode))
                ok = app_error_set(e, APP_UNAVAILABLE, "Cannot create '%s' because a file or directory with the same name already exists.", copy);
        }
        copy[i] = saved;
    }
    free(copy);
    return ok;
}

bool app_os_remove_file(const char *path, app_error *e) {
    if (unlink(path) == 0) return true;
    int code = errno;
    if (code == ENOENT) {
        char *parent = parent_of(path);
        bool ok = exists(parent);
        free(parent);
        if (ok) return true;
    }
    return fail(e, code, path);
}

bool app_os_remove_directory(const char *path, app_error *e) { return rmdir(path) == 0 || fail(e, errno == EEXIST ? ENOTEMPTY : errno, path); }

bool app_os_rename(const char *from, const char *to, bool overwrite, app_error *e) {
    if (!overwrite && exists(to)) return app_error_set(e, APP_UNAVAILABLE, "The file '%s' already exists.", to);
    if (rename(from, to) == 0) return true;
    int code = errno;
    return fail(e, code, exists(from) ? to : from);
}

bool app_os_replace(const char *stage, const char *target, app_error *e) { return rename(stage, target) == 0 || fail(e, errno, target); }

bool app_os_write_new(const char *path, const uint8_t *data, size_t length, app_error *e) {
    int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0666);
    if (fd < 0) return fail(e, errno, path);
    bool ok = true;
    while (ok && length) {
        ssize_t n = write(fd, data, length);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) ok = false;
        else { data += n; length -= (size_t)n; }
    }
    int code = errno;
    if (ok && fsync(fd) != 0) { ok = false; code = errno; }
    close(fd);
    if (!ok) { unlink(path); return fail(e, code, path); }
    return true;
}

bool app_os_same_path(const char *a, const char *b) { return strcmp(a, b) == 0; }

struct app_os_file { int fd; int mode; int64_t append_start; char *path; };

app_os_file *app_os_open(const char *path, int mode, app_error *e) {
    static const int flags[] = {O_RDONLY, O_WRONLY | O_CREAT | O_TRUNC, O_WRONLY, O_WRONLY | O_CREAT};
    int fd = open(path, flags[mode] | O_CLOEXEC, 0666);
    if (fd < 0) { fail(e, errno, path); return NULL; }
    struct stat s;
    if (fstat(fd, &s) == 0 && S_ISDIR(s.st_mode)) { close(fd); fail(e, EISDIR, path); return NULL; }
    app_os_file *f = calloc(1, sizeof *f);
    f->fd = fd;
    f->mode = mode;
    f->path = app_strdup(path);
    if (mode == APP_OPEN_APPEND) f->append_start = lseek(fd, 0, SEEK_END);
    return f;
}

int64_t app_os_read(app_os_file *f, void *buffer, size_t count, app_error *e) {
    for (;;) {
        ssize_t n = read(f->fd, buffer, count);
        if (n >= 0) return n;
        if (errno != EINTR) { fail(e, errno, f->path); return -1; }
    }
}

bool app_os_write(app_os_file *f, const void *data, size_t count, app_error *e) {
    const uint8_t *p = data;
    while (count) {
        ssize_t n = write(f->fd, p, count);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return fail(e, errno, f->path);
        p += n;
        count -= (size_t)n;
    }
    return true;
}

int64_t app_os_seek(app_os_file *f, int64_t offset, int origin, app_error *e) {
    int64_t current = lseek(f->fd, 0, SEEK_CUR), end = lseek(f->fd, 0, SEEK_END);
    lseek(f->fd, current, SEEK_SET);
    int64_t target = (origin == APP_SEEK_BEGIN ? 0 : origin == APP_SEEK_CURRENT ? current : end) + offset;
    if (target < 0) { app_error_set(e, APP_UNAVAILABLE, "An attempt was made to move the position before the beginning of the stream."); return -1; }
    if (f->mode == APP_OPEN_APPEND && target < f->append_start) {
        app_error_set(e, APP_UNAVAILABLE, "Unable seek backward to overwrite data that previously existed in a file opened in Append mode.");
        return -1;
    }
    int64_t result = lseek(f->fd, target, SEEK_SET);
    if (result < 0) { fail(e, errno, f->path); return -1; }
    return result;
}

bool app_os_flush(app_os_file *f, app_error *e) {
    if (f->mode == APP_OPEN_READ) return true;
    return fsync(f->fd) == 0 || fail(e, errno, f->path);
}

void app_os_close(app_os_file *f) {
    if (!f) return;
    close(f->fd);
    free(f->path);
    free(f);
}

/* ---- Pickers run as programs (zenity on Linux, osascript on macOS) ---- */

#if defined(AQUA_DESKTOP)
#include <poll.h>
#include <spawn.h>
#include <sys/wait.h>
extern char **environ;

bool app_posix_picker(char **arguments, int cancelled, char ***paths, size_t *count, app_error *e) {
    *paths = NULL;
    *count = 0;
    int output[2], errors[2];
    if (pipe(output) != 0 || pipe(errors) != 0) return app_error_set(e, APP_UNAVAILABLE, "File dialog failed: %s", strerror(errno));
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_adddup2(&actions, output[1], 1);
    posix_spawn_file_actions_adddup2(&actions, errors[1], 2);
    posix_spawn_file_actions_addclose(&actions, output[0]);
    posix_spawn_file_actions_addclose(&actions, errors[0]);
    pid_t child;
    int spawned = posix_spawn(&child, arguments[0], &actions, NULL, arguments, environ);
    posix_spawn_file_actions_destroy(&actions);
    close(output[1]);
    close(errors[1]);
    if (spawned != 0) { close(output[0]); close(errors[0]); return app_error_set(e, APP_UNAVAILABLE, "File dialog failed: %s", strerror(spawned)); }
    app_bytes out = {0}, err = {0};
    struct pollfd fds[2] = {{output[0], POLLIN, 0}, {errors[0], POLLIN, 0}};
    int open_count = 2;
    while (open_count > 0) {
        if (poll(fds, 2, -1) < 0) { if (errno == EINTR) continue; break; }
        for (int i = 0; i < 2; i++) {
            if (fds[i].fd < 0 || !(fds[i].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            char buffer[4096];
            ssize_t n = read(fds[i].fd, buffer, sizeof buffer);
            if (n > 0) app_bytes_append(i == 0 ? &out : &err, buffer, (size_t)n);
            else { close(fds[i].fd); fds[i].fd = -1; open_count--; }
        }
    }
    int status = 0;
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {}
    app_bytes_byte(&out, 0);
    app_bytes_byte(&err, 0);
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    bool ok = true;
    if (code == 0) {
        char *text = (char *)out.data;
        size_t capacity = 4;
        *paths = malloc(capacity * sizeof **paths);
        for (char *line = strtok(text, "\n"); line; line = strtok(NULL, "\n")) {
            size_t n = strlen(line);
            while (n && line[n - 1] == '\r') line[--n] = 0;
            if (!n) continue;
            if (*count == capacity) { capacity *= 2; *paths = realloc(*paths, capacity * sizeof **paths); }
            (*paths)[(*count)++] = app_strdup(line);
        }
    } else if (code != cancelled) ok = app_error_set(e, APP_UNAVAILABLE, "File dialog failed: %s", (char *)err.data);
    app_bytes_free(&out);
    app_bytes_free(&err);
    return ok;
}

/* ---- Services through aqua/window.h (the window system's text clipboard) and the ones no POSIX desktop adapter has ---- */

void app_platform_close(app_state *state) { (void)state; }

void app_os_clipboard_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "text", app_capability(rt, true, NULL));
    app_set(rt, hash, "image", app_capability(rt, false, "Native image clipboard adapter is currently Windows-only."));
    app_set(rt, hash, "multiple", app_capability(rt, false, NULL));
}

void app_os_clipboard_formats(app_task *task, void (*done)(app_task *, const app_error *, const char *const *, size_t)) {
    char *text = aqua_clipboard_text(task->rt);
    const char *formats[1] = {"text/plain"};
    done(task, NULL, formats, text && text[0] ? 1 : 0);
    aqua_free(text);
}

void app_os_clipboard_read(app_task *task, bool image, void (*done)(app_task *, const app_error *, const char *, size_t, const uint8_t *, size_t)) {
    (void)image;
    char *text = aqua_clipboard_text(task->rt);
    done(task, NULL, text, text ? strlen(text) : 0, NULL, 0);
    aqua_free(text);
}

void app_os_clipboard_write(app_task *task, const char *text, size_t text_length, const app_picture *image, app_done done) {
    app_error e = {0};
    if (image) { app_error_set(&e, APP_UNSUPPORTED, "Image clipboard adapter unavailable on this OS."); done(task, &e); return; }
    if (!text) { app_error_set(&e, APP_INVALID_DATA, "Clipboard content is empty."); done(task, &e); return; }
    char *copy = app_strndup(text, text_length);
    bool ok = aqua_clipboard_set_text(task->rt, copy);
    free(copy);
    if (!ok) app_error_set(&e, APP_UNAVAILABLE, "The window system's clipboard is unavailable.");
    done(task, ok ? NULL : &e);
}

void app_os_print(app_task *task, const app_picture *picture, app_done done) {
    (void)picture;
    app_error e = {0};
    app_error_set(&e, APP_UNSUPPORTED, "Native printing adapter unavailable on this OS.");
    done(task, &e);
}

/* The node list is validated by the caller; no POSIX desktop publishes it to assistive technology. */
bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e) {
    (void)state; (void)nodes; (void)count; (void)e;
    return true;
}

bool app_os_register_association(const app_association *association, app_error *e) {
    (void)association;
    return app_error_set(e, APP_UNSUPPORTED, "File association registration requires a Windows adapter.");
}
#endif
