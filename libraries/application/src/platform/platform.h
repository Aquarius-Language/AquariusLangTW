/* What the per-OS layers provide (platform/windows*.c, posix.c + linux.c or android.c, web.c). The library's logic
 * (validation, limits, staging, records, codecs) stays in the shared sources; these functions only reach the OS.
 * Operations that wait on the user or a Web API take an app_task and call their `done` exactly once, synchronously
 * on native targets. */
#ifndef APP_PLATFORM_H
#define APP_PLATFORM_H

#include "../application.h"

/* ---- File system (native targets: windows.c, posix.c) ------------------------------------------------------- */
typedef struct app_stat { bool exists; int32_t kind; int64_t length; double modified_ms; bool has_modified; } app_stat;
typedef struct app_os_file app_os_file;
enum { APP_OPEN_READ, APP_OPEN_CREATE, APP_OPEN_WRITE, APP_OPEN_APPEND };
enum { APP_SEEK_BEGIN, APP_SEEK_CURRENT, APP_SEEK_END };
extern const char app_os_separator;
char *app_os_full_path(const char *path, app_error *e);        /* absolute and lexically normalized, native separators */
bool app_os_stat(const char *path, app_stat *out, app_error *e);
bool app_os_is_link(const char *path);                          /* a symbolic link or other reparse point */
bool app_os_list(const char *path, char ***names, size_t *count, app_error *e); /* names, sorted */
bool app_os_mkdirs(const char *path, app_error *e);
bool app_os_remove_file(const char *path, app_error *e);        /* a missing file is not an error */
bool app_os_remove_directory(const char *path, app_error *e);   /* an empty directory */
bool app_os_rename(const char *from, const char *to, bool overwrite, app_error *e);
bool app_os_replace(const char *stage, const char *target, app_error *e); /* atomically replaces an existing target */
bool app_os_write_new(const char *path, const uint8_t *data, size_t length, app_error *e); /* new file, flushed */
bool app_os_same_path(const char *a, const char *b);            /* case-insensitive on Windows */
char *app_os_temporary_directory(void);
app_os_file *app_os_open(const char *path, int mode, app_error *e);
int64_t app_os_read(app_os_file *file, void *buffer, size_t count, app_error *e);
bool app_os_write(app_os_file *file, const void *data, size_t count, app_error *e);
int64_t app_os_seek(app_os_file *file, int64_t offset, int origin, app_error *e);
bool app_os_flush(app_os_file *file, app_error *e);
void app_os_close(app_os_file *file);

/* Storage roots for an application id: settings (roaming) and data (local) directories. */
char *app_os_settings_directory(const char *application);
char *app_os_data_directory(const char *application);

#if defined(AQUA_OS_ANDROID)
/* content:// documents (pickers, the launching intent) through the ContentResolver (android.c). */
bool app_android_document_stat(const char *uri, app_stat *out, app_error *e);
bool app_android_document_read(const char *uri, app_bytes *out, app_error *e);
bool app_android_document_write(const char *uri, const uint8_t *data, size_t length, app_error *e);
bool app_android_document_delete(const char *uri, app_error *e);
bool app_android_document_list(app_state *state, const char *uri, app_resource **items, size_t *count, app_error *e);
#endif

/* ---- Files backend (files_native.c or web.c) -------------------------------------------------------------- */
typedef void (*app_done)(app_task *task, const app_error *e);
bool app_fs_capabilities(aqua_runtime *rt, aqua_value hash);
bool app_fs_resolve(app_state *state, const char *location, app_resource *out, app_error *e);
void app_fs_stat(app_task *task, const app_resource *r, void (*done)(app_task *task, const app_error *e, const app_stat *stat));
/* `data` belongs to the callee after the callback returns. */
void app_fs_read(app_task *task, const app_resource *r, void (*done)(app_task *task, const app_error *e, app_bytes *data));
void app_fs_replace(app_task *task, const app_resource *r, const uint8_t *data, size_t length, bool require_atomic,
                    void (*done)(app_task *task, const app_error *e, bool atomic));
void app_fs_enumerate(app_task *task, const app_resource *r, void (*done)(app_task *task, const app_error *e, app_resource *items, size_t count));
void app_fs_create_directory(app_task *task, const app_resource *r, app_done done);
void app_fs_delete(app_task *task, const app_resource *r, bool recursive, app_done done);
void app_fs_copy(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done);
void app_fs_move(app_task *task, const app_resource *from, const app_resource *to, bool overwrite, app_done done);
bool app_fs_temporary(app_state *state, bool directory, app_resource *out, app_error *e);
void app_fs_dispose(app_state *state, app_lease *lease);
/* A stream over the resource: an OS file on native targets, a buffer committed on Flush/Close on the web. */
void app_fs_open(app_task *task, const app_resource *r, int mode, void (*done)(app_task *task, const app_error *e, app_stream *stream));
void app_fs_close_state(app_state *state);

/* Storage keys (files in the settings directory, IndexedDB "settings/<key>" records on the web). */
bool app_storage_directories(app_state *state, app_resource *settings, app_resource *data, app_error *e);
void app_storage_read(app_task *task, const char *key, void (*done)(app_task *task, const app_error *e, app_bytes *data, bool found));
void app_storage_write(app_task *task, const char *key, const uint8_t *data, size_t length, app_done done);
void app_storage_delete(app_task *task, const char *key, app_done done);

/* ---- Streams (files.c), with the backend's own operations ---------------------------------------------------- */
struct app_stream {
    app_stream *next;            /* state->streams */
    app_state *state;
    int mode;
    bool closed, can_read, can_write, can_seek;
    app_os_file *file;           /* native */
    app_bytes memory;            /* web: the whole content */
    size_t position, start;      /* web: position; Append mode cannot seek before start */
    bool dirty;
    app_resource resource;       /* web: committed on Flush/Close */
};
app_stream *app_stream_new(app_state *state, int mode);

/* ---- Clipboard --------------------------------------------------------------------------------------------- */
void app_os_clipboard_capabilities(aqua_runtime *rt, aqua_value hash);
void app_os_clipboard_formats(app_task *task, void (*done)(app_task *task, const app_error *e, const char *const *formats, size_t count));
/* Text as UTF-8 (NULL when the clipboard has none) and, when asked, an encoded image (PNG or BMP bytes) or NULL. */
void app_os_clipboard_read(app_task *task, bool image, void (*done)(app_task *task, const app_error *e, const char *text,
                                                                    size_t text_length, const uint8_t *image_data, size_t image_length));
void app_os_clipboard_write(app_task *task, const char *text, size_t text_length, const app_picture *image, app_done done);

/* ---- Application services ------------------------------------------------------------------------------------ */
typedef struct app_filter { char *name; char **extensions; size_t count; } app_filter;
typedef struct app_picker {
    char *title, *suggested;
    bool multiple, capture;      /* capture: AcquireImage (mobile browsers may offer the camera) */
    app_filter *filters; size_t filter_count;
} app_picker;
typedef void (*app_picked)(app_task *task, const app_error *e, app_resource *resources, size_t count);
void app_os_capabilities(aqua_runtime *rt, aqua_value hash);
void app_os_launch_files(app_task *task, app_picked done);
void app_os_open_files(app_task *task, const app_picker *options, app_picked done);
void app_os_save_file(app_task *task, const app_picker *options, app_picked done);
void app_os_pick_directory(app_task *task, app_picked done);
void app_os_print(app_task *task, const app_picture *picture, app_done done);
typedef struct app_node { char *id, *role, *label, *value; bool disabled; } app_node;
bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e);
typedef struct app_association { char *extension, *application, *description, *executable; char **arguments; size_t count; } app_association;
bool app_os_register_association(const app_association *association, app_error *e);
/* Resources for picked or launch paths (native targets). */
void app_resolve_paths(app_task *task, const char *const *paths, size_t count, app_picked done);

#endif
