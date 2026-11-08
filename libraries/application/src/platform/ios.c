/* iOS and iPadOS services: settings and data in the sandbox's Library/Application Support. The document pickers
 * (UIDocumentPickerViewController) need a UIKit adapter that does not exist yet, so pickers report themselves
 * unsupported; the text clipboard (UIPasteboard through aqua/window.h) and the services without an adapter are
 * posix.c's. */
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

static void unsupported(app_task *task, app_picked done) {
    app_error e = {0};
    app_error_set(&e, APP_UNSUPPORTED, "File and folder pickers are not available on iOS yet.");
    done(task, &e, NULL, 0);
}
void app_os_open_files(app_task *task, const app_picker *options, app_picked done) { (void)options; unsupported(task, done); }
void app_os_save_file(app_task *task, const app_picker *options, app_picked done) { (void)options; unsupported(task, done); }
void app_os_pick_directory(app_task *task, app_picked done) { unsupported(task, done); }

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "files", app_capability(rt, true, NULL));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, true, NULL));
    app_set(rt, hash, "pickers", app_capability(rt, false, "File and folder pickers are not available on iOS yet."));
    app_set(rt, hash, "printing", app_capability(rt, false, NULL));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, false, "Image acquisition needs the pickers."));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, true, NULL));
    app_set(rt, hash, "accessibility", app_capability(rt, false, NULL));
    app_set(rt, hash, "fileAssociations", app_capability(rt, false, NULL));
    app_set(rt, hash, "activation", app_capability(rt, true, "Launch resources are supplied by the embedding host."));
}
