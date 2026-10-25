/* Linux services, as the old Linux desktop host offered them: XDG settings and data directories, the window system's
 * Unicode text clipboard (GLFW through aqua/window.h), and zenity file and directory pickers. Image clipboard formats,
 * printing, the accessibility mirror and file associations have no adapter here. */
#if !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include "platform.h"
#include <aqua/window.h>
#include <errno.h>
#include <poll.h>
#include <spawn.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

static char *xdg(const char *variable, const char *fallback, const char *application) {
    const char *base = getenv(variable);
    if (base && base[0] == '/') return app_printf("%s/%s", base, application);
    const char *home = getenv("HOME");
    return app_printf("%s/%s/%s", home && *home ? home : "/tmp", fallback, application);
}
char *app_os_settings_directory(const char *application) { return xdg("XDG_CONFIG_HOME", ".config", application); }
char *app_os_data_directory(const char *application) { return xdg("XDG_DATA_HOME", ".local/share", application); }
char *app_os_temporary_directory(void) {
    const char *directory = getenv("TMPDIR");
    return app_strdup(directory && directory[0] == '/' ? directory : "/tmp");
}

void app_platform_close(app_state *state) { (void)state; }

/* ---- zenity ---- */

static char *zenity(void) {
    const char *path = getenv("PATH");
    if (!path) return NULL;
    for (const char *p = path; *p;) {
        const char *end = strchr(p, ':');
        size_t n = end ? (size_t)(end - p) : strlen(p);
        if (n) {
            char *candidate = app_printf("%.*s/zenity", (int)n, p);
            if (access(candidate, X_OK) == 0) return candidate;
            free(candidate);
        }
        if (!end) break;
        p = end + 1;
    }
    return NULL;
}

static bool pickers_available(void) { char *z = zenity(); bool found = z != NULL; free(z); return found; }

/* Runs zenity with `arguments`; the chosen paths, none when the user cancelled (exit status 1). */
static bool run_zenity(char **arguments, char ***paths, size_t *count, app_error *e) {
    *paths = NULL;
    *count = 0;
    char *program = zenity();
    if (!program) return app_error_set(e, APP_UNSUPPORTED, "Install zenity to use Linux file and directory pickers.");
    int output[2], errors[2];
    if (pipe(output) != 0 || pipe(errors) != 0) { free(program); return app_error_set(e, APP_UNAVAILABLE, "File dialog failed: %s", strerror(errno)); }
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_adddup2(&actions, output[1], 1);
    posix_spawn_file_actions_adddup2(&actions, errors[1], 2);
    posix_spawn_file_actions_addclose(&actions, output[0]);
    posix_spawn_file_actions_addclose(&actions, errors[0]);
    arguments[0] = program;
    pid_t child;
    int spawned = posix_spawn(&child, program, &actions, NULL, arguments, environ);
    posix_spawn_file_actions_destroy(&actions);
    close(output[1]);
    close(errors[1]);
    free(program);
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
    } else if (code != 1) ok = app_error_set(e, APP_UNAVAILABLE, "File dialog failed: %s", (char *)err.data);
    app_bytes_free(&out);
    app_bytes_free(&err);
    return ok;
}

static bool valid_extension(const char *e) {
    if (strcmp(e, "*") == 0) return true;
    if (!*e) return false;
    for (const char *c = e; *c; c++) if (!((*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || (unsigned char)*c >= 0x80)) return false;
    return true;
}

static void dialog(app_task *task, char **arguments, size_t argument_count, app_picked done) {
    app_error e = {0};
    char **paths;
    size_t count;
    arguments[argument_count] = NULL;
    if (app_task_cancelled(task)) return;
    bool ok = run_zenity(arguments, &paths, &count, &e);
    for (size_t i = 1; i < argument_count; i++) free(arguments[i]);
    if (!ok) { done(task, &e, NULL, 0); return; }
    if (app_task_cancelled(task)) { for (size_t i = 0; i < count; i++) free(paths[i]); free(paths); return; }
    app_resolve_paths(task, (const char *const *)paths, count, done);
    for (size_t i = 0; i < count; i++) free(paths[i]);
    free(paths);
}

static void file_dialog(app_task *task, const app_picker *o, bool save, app_picked done) {
    char *arguments[64 + 8];
    size_t n = 1;
    arguments[n++] = app_strdup("--file-selection");
    arguments[n++] = app_printf("--title=%s", o->title ? o->title : "");
    if (save) { arguments[n++] = app_strdup("--save"); arguments[n++] = app_strdup("--confirm-overwrite"); }
    else if (o->multiple) { arguments[n++] = app_strdup("--multiple"); arguments[n++] = app_strdup("--separator=\n"); }
    if (o->suggested && o->suggested[0]) arguments[n++] = app_printf("--filename=%s", o->suggested);
    for (size_t i = 0; i < o->filter_count && n < 64; i++) {
        app_bytes pattern = {0};
        app_bytes_text(&pattern, "--file-filter=");
        app_bytes_text(&pattern, o->filters[i].name);
        app_bytes_text(&pattern, " |");
        for (size_t k = 0; k < o->filters[i].count; k++) {
            if (!valid_extension(o->filters[i].extensions[k])) continue;
            app_bytes_text(&pattern, strcmp(o->filters[i].extensions[k], "*") == 0 ? " *" : " *.");
            if (strcmp(o->filters[i].extensions[k], "*") != 0) app_bytes_text(&pattern, o->filters[i].extensions[k]);
        }
        app_bytes_byte(&pattern, 0);
        arguments[n++] = (char *)pattern.data;
    }
    dialog(task, arguments, n, done);
}

void app_os_open_files(app_task *task, const app_picker *options, app_picked done) { file_dialog(task, options, false, done); }
void app_os_save_file(app_task *task, const app_picker *options, app_picked done) { file_dialog(task, options, true, done); }
void app_os_pick_directory(app_task *task, app_picked done) {
    char *arguments[4] = {NULL, app_strdup("--file-selection"), app_strdup("--directory"), NULL};
    dialog(task, arguments, 3, done);
}

/* ---- Capabilities, clipboard, unsupported services ---- */

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    bool pickers = pickers_available();
    app_set(rt, hash, "files", app_capability(rt, true, NULL));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, true, NULL));
    app_set(rt, hash, "pickers", app_capability(rt, pickers, pickers ? NULL : "Install zenity for Linux file pickers."));
    app_set(rt, hash, "printing", app_capability(rt, false, NULL));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, pickers, "Image acquisition selects an existing image; scanner/camera hardware adapter is not installed."));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, true, NULL));
    app_set(rt, hash, "accessibility", app_capability(rt, false, "Read-only MSAA mirror; requires an active graphics window."));
    app_set(rt, hash, "fileAssociations", app_capability(rt, false, "Registration is explicit and per-user."));
    app_set(rt, hash, "activation", app_capability(rt, true, "Launch resources are supplied by the embedding host."));
}

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

/* The node list is validated by the caller; Linux has no assistive-technology publication for it. */
bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e) {
    (void)state; (void)nodes; (void)count; (void)e;
    return true;
}

bool app_os_register_association(const app_association *association, app_error *e) {
    (void)association;
    return app_error_set(e, APP_UNSUPPORTED, "File association registration requires a Windows adapter.");
}
