/* macOS services: settings and data in ~/Library/Application Support (Apple's place for both), and the system's open,
 * save and folder panels through osascript (AppleScript's choose file, choose file name and choose folder), which needs
 * no Objective-C in the library. The text clipboard and the services without an adapter are posix.c's. */
#include "platform.h"
#include <stdlib.h>
#include <string.h>

static char *support(const char *application) {
    const char *home = getenv("HOME");
    return app_printf("%s/Library/Application Support/%s", home && *home ? home : "/tmp", application);
}
char *app_os_settings_directory(const char *application) { return support(application); }
char *app_os_data_directory(const char *application) { return support(application); }
char *app_os_temporary_directory(void) {
    const char *directory = getenv("TMPDIR");
    return app_strdup(directory && directory[0] == '/' ? directory : "/tmp");
}

/* ---- Panels through osascript ---- */

/* `text` as an AppleScript string literal. */
static void quoted(app_bytes *script, const char *text) {
    app_bytes_byte(script, '"');
    for (const char *c = text ? text : ""; *c; c++) {
        if (*c == '"' || *c == '\\') app_bytes_byte(script, '\\');
        app_bytes_byte(script, (uint8_t)*c);
    }
    app_bytes_byte(script, '"');
}

/* Runs one AppleScript that prints the chosen POSIX paths, one per line; osascript exits with 1 when the user cancels. */
static void run_script(app_task *task, app_bytes *script, app_picked done) {
    app_bytes_byte(script, 0);
    char *arguments[] = {"/usr/bin/osascript", "-e", (char *)script->data, NULL};
    app_error e = {0};
    char **paths;
    size_t count;
    if (app_task_cancelled(task)) { app_bytes_free(script); return; }
    bool ok = app_posix_picker(arguments, 1, &paths, &count, &e);
    app_bytes_free(script);
    if (!ok) { done(task, &e, NULL, 0); return; }
    if (app_task_cancelled(task)) { for (size_t i = 0; i < count; i++) free(paths[i]); free(paths); return; }
    app_resolve_paths(task, (const char *const *)paths, count, done);
    for (size_t i = 0; i < count; i++) free(paths[i]);
    free(paths);
}

static bool valid_extension(const char *e) {
    if (!*e) return false;
    for (const char *c = e; *c; c++) if (!((*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || (unsigned char)*c >= 0x80)) return false;
    return true;
}

void app_os_open_files(app_task *task, const app_picker *o, app_picked done) {
    app_bytes script = {0};
    app_bytes_text(&script, "set picked to choose file with prompt ");
    quoted(&script, o->title);
    /* "of type" takes extensions; a filter with "*" allows every file. */
    bool any = o->filter_count == 0, first = true;
    for (size_t i = 0; i < o->filter_count; i++)
        for (size_t k = 0; k < o->filters[i].count; k++) if (strcmp(o->filters[i].extensions[k], "*") == 0) any = true;
    if (!any) {
        app_bytes_text(&script, " of type {");
        for (size_t i = 0; i < o->filter_count; i++)
            for (size_t k = 0; k < o->filters[i].count; k++) {
                if (!valid_extension(o->filters[i].extensions[k])) continue;
                if (!first) app_bytes_byte(&script, ',');
                quoted(&script, o->filters[i].extensions[k]);
                first = false;
            }
        app_bytes_text(&script, "}");
    }
    if (o->multiple) app_bytes_text(&script, " with multiple selections allowed");
    app_bytes_text(&script, "\nif class of picked is not list then set picked to {picked}\nset out to \"\"\n"
                            "repeat with f in picked\nset out to out & POSIX path of f & linefeed\nend repeat\nreturn out");
    run_script(task, &script, done);
}

void app_os_save_file(app_task *task, const app_picker *o, app_picked done) {
    app_bytes script = {0};
    app_bytes_text(&script, "return POSIX path of (choose file name with prompt ");
    quoted(&script, o->title);
    if (o->suggested && o->suggested[0]) { app_bytes_text(&script, " default name "); quoted(&script, o->suggested); }
    app_bytes_text(&script, ")");
    run_script(task, &script, done);
}

void app_os_pick_directory(app_task *task, app_picked done) {
    app_bytes script = {0};
    app_bytes_text(&script, "return POSIX path of (choose folder)");
    run_script(task, &script, done);
}

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "files", app_capability(rt, true, NULL));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, true, NULL));
    app_set(rt, hash, "pickers", app_capability(rt, true, NULL));
    app_set(rt, hash, "printing", app_capability(rt, false, NULL));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, true, "Image acquisition selects an existing image; scanner/camera hardware adapter is not installed."));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, true, NULL));
    app_set(rt, hash, "accessibility", app_capability(rt, false, "Read-only MSAA mirror; requires an active graphics window."));
    app_set(rt, hash, "fileAssociations", app_capability(rt, false, "Registration is explicit and per-user."));
    app_set(rt, hash, "activation", app_capability(rt, true, "Launch resources are supplied by the embedding host."));
}
