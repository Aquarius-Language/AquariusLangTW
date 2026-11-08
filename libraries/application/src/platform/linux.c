/* Linux services, as the old Linux desktop host offered them: XDG settings and data directories and zenity file and
 * directory pickers. The text clipboard and the services without an adapter are posix.c's. */
#if !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include "platform.h"
#include <aqua/window.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

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
    char *program = zenity();
    if (!program) { *paths = NULL; *count = 0; return app_error_set(e, APP_UNSUPPORTED, "Install zenity to use Linux file and directory pickers."); }
    arguments[0] = program;
    bool ok = app_posix_picker(arguments, 1, paths, count, e);
    free(program);
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
