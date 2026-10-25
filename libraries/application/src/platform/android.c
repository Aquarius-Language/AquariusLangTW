/* Android services through JNI (aqua/android.h): the app's private directories for Storage and temporary files, the
 * ClipboardManager (text; images are read from content URIs), Storage Access Framework pickers (ACTION_OPEN_DOCUMENT,
 * ACTION_CREATE_DOCUMENT, ACTION_OPEN_DOCUMENT_TREE) whose content:// documents are read and written through file
 * descriptors from the ContentResolver, documents the activity was opened with, and the activity view's content
 * description as the accessibility mirror. Printing and file associations need Java components and are unsupported. */
#include "platform.h"
#include <aqua/android.h>
#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* ---- JNI helpers: every call checks for a pending Java exception and turns it into a failure ---- */

static JNIEnv *jni(void) { return aqua_android_jni(); }

static bool java_failed(app_error *e, app_kind kind, const char *what) {
    JNIEnv *env = jni();
    if (!(*env)->ExceptionCheck(env)) return false;
    jthrowable error = (*env)->ExceptionOccurred(env);
    (*env)->ExceptionClear(env);
    char message[400] = "";
    jclass object = (*env)->FindClass(env, "java/lang/Object");
    jmethodID to_string = (*env)->GetMethodID(env, object, "toString", "()Ljava/lang/String;");
    jstring text = error ? (jstring)(*env)->CallObjectMethod(env, error, to_string) : NULL;
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    if (text) {
        const char *utf = (*env)->GetStringUTFChars(env, text, NULL);
        if (utf) { snprintf(message, sizeof message, "%s", utf); (*env)->ReleaseStringUTFChars(env, text, utf); }
    }
    if (strstr(message, "SecurityException")) kind = APP_PERMISSION_DENIED;
    else if (strstr(message, "FileNotFoundException")) kind = APP_NOT_FOUND;
    app_error_set(e, kind, "%s: %s", what, message[0] ? message : "Java exception");
    return true;
}

static jobject call_object(jobject target, const char *class_name, const char *method, const char *signature, ...) {
    JNIEnv *env = jni();
    jclass type = (*env)->FindClass(env, class_name);
    if (!type) return NULL;
    jmethodID id = (*env)->GetMethodID(env, type, method, signature);
    if (!id) return NULL;
    va_list arguments;
    va_start(arguments, signature);
    jobject result = (*env)->CallObjectMethodV(env, target, id, arguments);
    va_end(arguments);
    return result;
}

static jobject call_static(const char *class_name, const char *method, const char *signature, ...) {
    JNIEnv *env = jni();
    jclass type = (*env)->FindClass(env, class_name);
    if (!type) return NULL;
    jmethodID id = (*env)->GetStaticMethodID(env, type, method, signature);
    if (!id) return NULL;
    va_list arguments;
    va_start(arguments, signature);
    jobject result = (*env)->CallStaticObjectMethodV(env, type, id, arguments);
    va_end(arguments);
    return result;
}

static jstring jstr(const char *utf8) { return (*jni())->NewStringUTF(jni(), utf8); }

static char *text_of(jobject value) {
    JNIEnv *env = jni();
    if (!value) return NULL;
    jclass object = (*env)->FindClass(env, "java/lang/Object");
    jmethodID to_string = (*env)->GetMethodID(env, object, "toString", "()Ljava/lang/String;");
    jstring text = (jstring)(*env)->CallObjectMethod(env, value, to_string);
    if (!text || (*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); return NULL; }
    const char *utf = (*env)->GetStringUTFChars(env, text, NULL);
    char *copy = app_strdup(utf ? utf : "");
    if (utf) (*env)->ReleaseStringUTFChars(env, text, utf);
    return copy;
}

static jobject activity(void) { return aqua_android_activity(); }
static jobject resolver(void) { return call_object(activity(), "android/content/Context", "getContentResolver", "()Landroid/content/ContentResolver;"); }
static jobject parse_uri(const char *text) { return call_static("android/net/Uri", "parse", "(Ljava/lang/String;)Landroid/net/Uri;", jstr(text)); }

/* ---- Directories ---- */

static char *context_directory(const char *method) {
    JNIEnv *env = jni();
    (*env)->PushLocalFrame(env, 16);
    jobject file = call_object(activity(), "android/content/Context", method, "()Ljava/io/File;");
    jobject path = file ? call_object(file, "java/io/File", "getAbsolutePath", "()Ljava/lang/String;") : NULL;
    char *text = text_of(path);
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->PopLocalFrame(env, NULL);
    return text;
}

char *app_os_settings_directory(const char *application) {
    (void)application; /* the app's private storage already belongs to it alone */
    char *files = context_directory("getFilesDir"), *path = app_printf("%s/settings", files ? files : "/data/local/tmp");
    free(files);
    return path;
}
char *app_os_data_directory(const char *application) {
    (void)application;
    char *files = context_directory("getFilesDir"), *path = app_printf("%s/data", files ? files : "/data/local/tmp");
    free(files);
    return path;
}
char *app_os_temporary_directory(void) {
    char *cache = context_directory("getCacheDir");
    return cache ? cache : app_strdup("/data/local/tmp");
}

void app_platform_close(app_state *state) { (void)state; }

/* ---- Documents (content:// URIs) ---- */

/* A native file descriptor of the document, opened by the ContentResolver ("r" or "wt"). */
static int document_fd(const char *uri, const char *mode, app_error *e) {
    JNIEnv *env = jni();
    (*env)->PushLocalFrame(env, 16);
    jobject descriptor = call_object(resolver(), "android/content/ContentResolver", "openFileDescriptor",
                                     "(Landroid/net/Uri;Ljava/lang/String;)Landroid/os/ParcelFileDescriptor;", parse_uri(uri), jstr(mode));
    int fd = -1;
    if (!java_failed(e, APP_NOT_FOUND, "Document unavailable") && descriptor) {
        jclass type = (*env)->FindClass(env, "android/os/ParcelFileDescriptor");
        jmethodID detach = (*env)->GetMethodID(env, type, "detachFd", "()I");
        fd = (*env)->CallIntMethod(env, descriptor, detach);
        if (java_failed(e, APP_UNAVAILABLE, "Document unavailable")) fd = -1;
    } else if (!e->failed) app_error_set(e, APP_NOT_FOUND, "Could not open the document '%s'.", uri);
    (*env)->PopLocalFrame(env, NULL);
    return fd;
}

bool app_android_document_read(const char *uri, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    int fd = document_fd(uri, "r", e);
    if (fd < 0) return false;
    bool ok = true;
    for (;;) {
        if (!app_bytes_reserve(out, out->length + 65536)) { ok = app_error_set(e, APP_LIMIT_EXCEEDED, "Out of memory."); break; }
        ssize_t n = read(fd, out->data + out->length, 65536);
        if (n < 0 && errno == EINTR) continue;
        if (n < 0) { ok = app_error_set(e, APP_UNAVAILABLE, "%s", strerror(errno)); break; }
        if (n == 0) break;
        out->length += (size_t)n;
        if (out->length > APP_MAX_BYTES) { ok = app_error_set(e, APP_LIMIT_EXCEEDED, "LimitExceeded: Document too large"); break; }
    }
    close(fd);
    if (!ok) app_bytes_free(out);
    return ok;
}

bool app_android_document_write(const char *uri, const uint8_t *data, size_t length, app_error *e) {
    int fd = document_fd(uri, "wt", e);
    if (fd < 0) return false;
    bool ok = true;
    while (ok && length) {
        ssize_t n = write(fd, data, length);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) ok = app_error_set(e, APP_UNAVAILABLE, "%s", strerror(errno));
        else { data += n; length -= (size_t)n; }
    }
    if (ok && fsync(fd) != 0 && errno != EINVAL) ok = app_error_set(e, APP_UNAVAILABLE, "%s", strerror(errno));
    close(fd);
    return ok;
}

bool app_android_document_delete(const char *uri, app_error *e) {
    JNIEnv *env = jni();
    (*env)->PushLocalFrame(env, 16);
    jclass contract = (*env)->FindClass(env, "android/provider/DocumentsContract");
    jmethodID remove = (*env)->GetStaticMethodID(env, contract, "deleteDocument", "(Landroid/content/ContentResolver;Landroid/net/Uri;)Z");
    jboolean deleted = (*env)->CallStaticBooleanMethod(env, contract, remove, resolver(), parse_uri(uri));
    bool ok = !java_failed(e, APP_UNSUPPORTED, "The document cannot be deleted") && deleted;
    if (!ok && !e->failed) app_error_set(e, APP_UNAVAILABLE, "The document cannot be deleted.");
    (*env)->PopLocalFrame(env, NULL);
    return ok;
}

/* One row of a content query: display name, MIME type, size and modification time. */
typedef struct row { char *name, *mime; int64_t size; double modified; } row;

static bool query(jobject uri, const char *const *columns, size_t column_count, row **rows, size_t *count, app_error *e) {
    JNIEnv *env = jni();
    *rows = NULL;
    *count = 0;
    jclass string = (*env)->FindClass(env, "java/lang/String");
    jobjectArray projection = (*env)->NewObjectArray(env, (jsize)column_count, string, NULL);
    for (size_t i = 0; i < column_count; i++) (*env)->SetObjectArrayElement(env, projection, (jsize)i, jstr(columns[i]));
    jobject cursor = call_object(resolver(), "android/content/ContentResolver", "query",
                                 "(Landroid/net/Uri;[Ljava/lang/String;Ljava/lang/String;[Ljava/lang/String;Ljava/lang/String;)Landroid/database/Cursor;",
                                 uri, projection, NULL, NULL, NULL);
    if (java_failed(e, APP_UNAVAILABLE, "The document cannot be queried")) return false;
    if (!cursor) return true;
    jclass type = (*env)->FindClass(env, "android/database/Cursor");
    jmethodID next = (*env)->GetMethodID(env, type, "moveToNext", "()Z"), get_string = (*env)->GetMethodID(env, type, "getString", "(I)Ljava/lang/String;");
    jmethodID get_long = (*env)->GetMethodID(env, type, "getLong", "(I)J"), is_null = (*env)->GetMethodID(env, type, "isNull", "(I)Z");
    jmethodID close_cursor = (*env)->GetMethodID(env, type, "close", "()V");
    size_t capacity = 8;
    *rows = calloc(capacity, sizeof **rows);
    while ((*env)->CallBooleanMethod(env, cursor, next) && !(*env)->ExceptionCheck(env)) {
        if (*count == capacity) { capacity *= 2; *rows = realloc(*rows, capacity * sizeof **rows); }
        row *r = &(*rows)[(*count)++];
        memset(r, 0, sizeof *r);
        r->size = -1;
        r->modified = -1;
        for (size_t i = 0; i < column_count; i++) {
            if ((*env)->CallBooleanMethod(env, cursor, is_null, (jint)i)) continue;
            if (strcmp(columns[i], "_size") == 0) r->size = (*env)->CallLongMethod(env, cursor, get_long, (jint)i);
            else if (strcmp(columns[i], "last_modified") == 0) r->modified = (double)(*env)->CallLongMethod(env, cursor, get_long, (jint)i);
            else {
                jobject value = (*env)->CallObjectMethod(env, cursor, get_string, (jint)i);
                char *text = text_of(value);
                if (strcmp(columns[i], "mime_type") == 0) r->mime = text; else r->name = text;
                (*env)->DeleteLocalRef(env, value);
            }
        }
        if (*count > APP_MAX_ENTRIES) break;
    }
    (*env)->CallVoidMethod(env, cursor, close_cursor);
    return !java_failed(e, APP_UNAVAILABLE, "The document cannot be queried");
}

static void rows_free(row *rows, size_t count) {
    for (size_t i = 0; i < count; i++) { free(rows[i].name); free(rows[i].mime); }
    free(rows);
}

static bool is_directory_mime(const char *mime) { return mime && strcmp(mime, "vnd.android.document/directory") == 0; }

bool app_android_document_stat(const char *uri, app_stat *out, app_error *e) {
    static const char *const columns[] = {"_display_name", "mime_type", "_size", "last_modified"};
    JNIEnv *env = jni();
    memset(out, 0, sizeof *out);
    out->length = -1;
    (*env)->PushLocalFrame(env, 32);
    row *rows;
    size_t count;
    app_error ignored = {0};
    if (query(parse_uri(uri), columns, 4, &rows, &count, &ignored) && count) {
        out->exists = true;
        out->kind = is_directory_mime(rows[0].mime) ? APP_DIRECTORY : APP_FILE;
        out->length = out->kind == APP_FILE ? rows[0].size : -1;
        if (rows[0].modified >= 0) { out->modified_ms = rows[0].modified; out->has_modified = true; }
        rows_free(rows, count);
    }
    (*env)->PopLocalFrame(env, NULL);
    (void)e;
    return true;
}

/* A resource for a document URI: its display name and kind from the provider. */
static void document_resource(app_state *state, const char *uri, app_resource *r) {
    static const char *const columns[] = {"_display_name", "mime_type"};
    JNIEnv *env = jni();
    memset(r, 0, sizeof *r);
    (*env)->PushLocalFrame(env, 32);
    row *rows;
    size_t count;
    app_error ignored = {0};
    char *name = NULL;
    bool directory = false;
    if (query(parse_uri(uri), columns, 2, &rows, &count, &ignored) && count) {
        name = rows[0].name ? app_strdup(rows[0].name) : NULL;
        directory = is_directory_mime(rows[0].mime);
        rows_free(rows, count);
    }
    (*env)->PopLocalFrame(env, NULL);
    r->provider = app_strdup(state->provider);
    r->id = app_strdup(uri);
    r->location = app_strdup(uri);
    r->name = name ? name : app_strdup("document");
    r->kind = directory ? APP_DIRECTORY : APP_FILE;
    r->backend = APP_DOCUMENT;
    r->identity = app_printf("android:%s", uri);
}

bool app_android_document_list(app_state *state, const char *uri, app_resource **items, size_t *count, app_error *e) {
    static const char *const columns[] = {"document_id"};
    JNIEnv *env = jni();
    *items = NULL;
    *count = 0;
    (*env)->PushLocalFrame(env, 64);
    jobject tree = parse_uri(uri);
    jclass contract = (*env)->FindClass(env, "android/provider/DocumentsContract");
    jmethodID is_document = (*env)->GetStaticMethodID(env, contract, "isDocumentUri", "(Landroid/content/Context;Landroid/net/Uri;)Z");
    bool document = (*env)->CallStaticBooleanMethod(env, contract, is_document, activity(), tree);
    jstring id = (jstring)call_static("android/provider/DocumentsContract", document ? "getDocumentId" : "getTreeDocumentId", "(Landroid/net/Uri;)Ljava/lang/String;", tree);
    jobject children = id ? call_static("android/provider/DocumentsContract", "buildChildDocumentsUriUsingTree",
                                        "(Landroid/net/Uri;Ljava/lang/String;)Landroid/net/Uri;", tree, id) : NULL;
    bool ok = !java_failed(e, APP_UNSUPPORTED, "Resource is not a document directory") && children;
    row *rows = NULL;
    size_t row_count = 0;
    if (ok) ok = query(children, columns, 1, &rows, &row_count, e);
    if (ok && row_count > APP_MAX_ENTRIES) ok = app_error_set(e, APP_LIMIT_EXCEEDED, "LimitExceeded: Directory entries");
    if (ok) {
        *items = calloc(row_count ? row_count : 1, sizeof **items);
        for (size_t i = 0; i < row_count; i++) {
            if (!rows[i].name) continue;
            jobject child = call_static("android/provider/DocumentsContract", "buildDocumentUriUsingTree",
                                        "(Landroid/net/Uri;Ljava/lang/String;)Landroid/net/Uri;", tree, jstr(rows[i].name));
            char *text = text_of(child);
            if (text) { document_resource(state, text, &(*items)[(*count)++]); free(text); }
        }
    } else if (!e->failed) app_error_set(e, APP_UNSUPPORTED, "Resource is not a document directory");
    rows_free(rows, row_count);
    (*env)->PopLocalFrame(env, NULL);
    return ok;
}

/* ---- Pickers through the Storage Access Framework ---- */

typedef struct pick { app_task *task; app_picked done; } pick;

static void picked(int32_t result, jobject intent, void *data) {
    pick *p = data;
    JNIEnv *env = jni();
    app_resource *items = NULL;
    size_t count = 0;
    if (result == -1 && intent) {
        (*env)->PushLocalFrame(env, 64);
        jobject clip = call_object(intent, "android/content/Intent", "getClipData", "()Landroid/content/ClipData;");
        jclass clip_type = (*env)->FindClass(env, "android/content/ClipData");
        jint flags = 0;
        jclass intent_type = (*env)->FindClass(env, "android/content/Intent");
        flags = (*env)->CallIntMethod(env, intent, (*env)->GetMethodID(env, intent_type, "getFlags", "()I")) & 3;
        jint n = clip ? (*env)->CallIntMethod(env, clip, (*env)->GetMethodID(env, clip_type, "getItemCount", "()I")) : 0;
        jobject single = clip ? NULL : call_object(intent, "android/content/Intent", "getData", "()Landroid/net/Uri;");
        jint total = clip ? n : single ? 1 : 0;
        items = calloc(total ? (size_t)total : 1, sizeof *items);
        for (jint i = 0; i < total; i++) {
            jobject uri = single;
            if (clip) {
                jobject item = (*env)->CallObjectMethod(env, clip, (*env)->GetMethodID(env, clip_type, "getItemAt", "(I)Landroid/content/ClipData$Item;"), i);
                uri = item ? call_object(item, "android/content/ClipData$Item", "getUri", "()Landroid/net/Uri;") : NULL;
            }
            if (!uri) continue;
            jclass resolver_type = (*env)->FindClass(env, "android/content/ContentResolver");
            (*env)->CallVoidMethod(env, resolver(), (*env)->GetMethodID(env, resolver_type, "takePersistableUriPermission", "(Landroid/net/Uri;I)V"), uri, flags);
            if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
            /* A tree URI names its root document. */
            jclass contract = (*env)->FindClass(env, "android/provider/DocumentsContract");
            jboolean is_tree = (*env)->CallStaticBooleanMethod(env, contract, (*env)->GetStaticMethodID(env, contract, "isTreeUri", "(Landroid/net/Uri;)Z"), uri);
            if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); is_tree = false; }
            if (is_tree) {
                jobject id = call_static("android/provider/DocumentsContract", "getTreeDocumentId", "(Landroid/net/Uri;)Ljava/lang/String;", uri);
                jobject root = id ? call_static("android/provider/DocumentsContract", "buildDocumentUriUsingTree", "(Landroid/net/Uri;Ljava/lang/String;)Landroid/net/Uri;", uri, id) : NULL;
                if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
                if (root) uri = root;
            }
            char *text = text_of(uri);
            if (text) { document_resource(p->task->state, text, &items[count++]); free(text); }
        }
        (*env)->PopLocalFrame(env, NULL);
    }
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    p->done(p->task, NULL, items, count);
    free(p);
}

static void start_picker(app_task *task, const char *action, const app_picker *options, bool directory, app_picked done) {
    JNIEnv *env = jni();
    app_error e = {0};
    if (app_task_cancelled(task)) return;
    (*env)->PushLocalFrame(env, 64);
    jclass intent_type = (*env)->FindClass(env, "android/content/Intent");
    jobject intent = (*env)->NewObject(env, intent_type, (*env)->GetMethodID(env, intent_type, "<init>", "(Ljava/lang/String;)V"), jstr(action));
    if (!directory) {
        call_object(intent, "android/content/Intent", "addCategory", "(Ljava/lang/String;)Landroid/content/Intent;", jstr("android.intent.category.OPENABLE"));
        call_object(intent, "android/content/Intent", "setType", "(Ljava/lang/String;)Landroid/content/Intent;", jstr(options && options->capture ? "image/*" : "*/*"));
        call_object(intent, "android/content/Intent", "putExtra", "(Ljava/lang/String;Z)Landroid/content/Intent;",
                    jstr("android.intent.extra.ALLOW_MULTIPLE"), (jboolean)(options && options->multiple));
        if (options && options->suggested && options->suggested[0])
            call_object(intent, "android/content/Intent", "putExtra", "(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;",
                        jstr("android.intent.extra.TITLE"), jstr(options->suggested));
        /* Filters become MIME types where Android knows the extension. */
        if (options && options->filter_count && !options->capture) {
            jobject map = call_static("android/webkit/MimeTypeMap", "getSingleton", "()Landroid/webkit/MimeTypeMap;");
            jclass string = (*env)->FindClass(env, "java/lang/String");
            char *types[64];
            size_t n = 0;
            bool everything = false;
            for (size_t i = 0; i < options->filter_count; i++)
                for (size_t k = 0; k < options->filters[i].count && n < 64; k++) {
                    if (strcmp(options->filters[i].extensions[k], "*") == 0) { everything = true; continue; }
                    jobject mime = map ? call_object(map, "android/webkit/MimeTypeMap", "getMimeTypeFromExtension", "(Ljava/lang/String;)Ljava/lang/String;",
                                                     jstr(options->filters[i].extensions[k])) : NULL;
                    char *text = text_of(mime);
                    if (text) types[n++] = text; else everything = true;
                }
            if (n && !everything) {
                jobjectArray array = (*env)->NewObjectArray(env, (jsize)n, string, NULL);
                for (size_t i = 0; i < n; i++) (*env)->SetObjectArrayElement(env, array, (jsize)i, jstr(types[i]));
                call_object(intent, "android/content/Intent", "putExtra", "(Ljava/lang/String;[Ljava/lang/String;)Landroid/content/Intent;",
                            jstr("android.intent.extra.MIME_TYPES"), array);
            }
            for (size_t i = 0; i < n; i++) free(types[i]);
        }
    }
    call_object(intent, "android/content/Intent", "addFlags", "(I)Landroid/content/Intent;", (jint)(1 | 2 | 64));
    bool ok = !java_failed(&e, APP_UNAVAILABLE, "The picker cannot start");
    pick *p = NULL;
    if (ok) {
        p = calloc(1, sizeof *p);
        p->task = task; p->done = done;
        if (!aqua_android_start_activity(intent, picked, p)) { free(p); p = NULL; ok = app_error_set(&e, APP_UNAVAILABLE, "The picker cannot start."); }
        else if (java_failed(&e, APP_UNAVAILABLE, "The picker cannot start")) ok = false;
    }
    (*env)->PopLocalFrame(env, NULL);
    if (!ok) done(task, &e, NULL, 0);
}

void app_os_open_files(app_task *task, const app_picker *options, app_picked done) { start_picker(task, "android.intent.action.OPEN_DOCUMENT", options, false, done); }
void app_os_save_file(app_task *task, const app_picker *options, app_picked done) { start_picker(task, "android.intent.action.CREATE_DOCUMENT", options, false, done); }
void app_os_pick_directory(app_task *task, app_picked done) { start_picker(task, "android.intent.action.OPEN_DOCUMENT_TREE", NULL, true, done); }

/* Launch documents: the arguments the runtime was started with, then the activity intent's document. */
void app_os_launch_files(app_task *task, app_picked done) {
    JNIEnv *env = jni();
    app_error e = {0};
    uint32_t arguments = aqua_launch_count(task->rt);
    app_resource *items = calloc(arguments + 1, sizeof *items);
    size_t count = 0;
    for (uint32_t i = 0; i < arguments; i++) if (app_fs_resolve(task->state, aqua_launch_argument(task->rt, i), &items[count], &e)) count++;
    (*env)->PushLocalFrame(env, 16);
    jobject intent = call_object(activity(), "android/app/Activity", "getIntent", "()Landroid/content/Intent;");
    jobject uri = intent ? call_object(intent, "android/content/Intent", "getData", "()Landroid/net/Uri;") : NULL;
    char *text = text_of(uri);
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->PopLocalFrame(env, NULL);
    if (text && strncmp(text, "content:", 8) == 0) document_resource(task->state, text, &items[count++]);
    else if (text && strncmp(text, "file://", 7) == 0 && app_fs_resolve(task->state, text + 7, &items[count], &e)) count++;
    free(text);
    done(task, NULL, items, count);
}

/* ---- Clipboard ---- */

static jobject clipboard_manager(void) {
    return call_object(activity(), "android/content/Context", "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;", jstr("clipboard"));
}

/* The first clip item's text and image document URI, or NULLs. */
static void clip_item(char **text, char **image_uri) {
    JNIEnv *env = jni();
    *text = *image_uri = NULL;
    (*env)->PushLocalFrame(env, 32);
    jobject manager = clipboard_manager();
    jobject clip = manager ? call_object(manager, "android/content/ClipboardManager", "getPrimaryClip", "()Landroid/content/ClipData;") : NULL;
    if (clip) {
        jclass clip_type = (*env)->FindClass(env, "android/content/ClipData");
        jint n = (*env)->CallIntMethod(env, clip, (*env)->GetMethodID(env, clip_type, "getItemCount", "()I"));
        jobject item = n > 0 ? (*env)->CallObjectMethod(env, clip, (*env)->GetMethodID(env, clip_type, "getItemAt", "(I)Landroid/content/ClipData$Item;"), 0) : NULL;
        if (item) {
            *text = text_of(call_object(item, "android/content/ClipData$Item", "getText", "()Ljava/lang/CharSequence;"));
            jobject uri = call_object(item, "android/content/ClipData$Item", "getUri", "()Landroid/net/Uri;");
            if (uri) {
                char *type = text_of(call_object(resolver(), "android/content/ContentResolver", "getType", "(Landroid/net/Uri;)Ljava/lang/String;", uri));
                if (type && strncmp(type, "image/", 6) == 0) *image_uri = text_of(uri);
                free(type);
            }
        }
    }
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    (*env)->PopLocalFrame(env, NULL);
}

void app_os_clipboard_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "text", app_capability(rt, true, NULL));
    app_set(rt, hash, "image", app_capability(rt, false, "Reading images only: writing needs the application's content provider."));
    app_set(rt, hash, "multiple", app_capability(rt, false, NULL));
}

void app_os_clipboard_formats(app_task *task, void (*done)(app_task *, const app_error *, const char *const *, size_t)) {
    char *text, *image;
    clip_item(&text, &image);
    const char *formats[2];
    size_t count = 0;
    if (text) formats[count++] = "text/plain";
    if (image) formats[count++] = "image/png";
    done(task, NULL, formats, count);
    free(text);
    free(image);
}

void app_os_clipboard_read(app_task *task, bool image, void (*done)(app_task *, const app_error *, const char *, size_t, const uint8_t *, size_t)) {
    app_error e = {0};
    char *text, *uri;
    clip_item(&text, &uri);
    app_bytes bytes = {0};
    bool ok = !(image && uri) || app_android_document_read(uri, &bytes, &e);
    if (ok) done(task, NULL, text, text ? strlen(text) : 0, bytes.data, bytes.length);
    else done(task, &e, NULL, 0, NULL, 0);
    app_bytes_free(&bytes);
    free(text);
    free(uri);
}

void app_os_clipboard_write(app_task *task, const char *text, size_t text_length, const app_picture *image, app_done done) {
    JNIEnv *env = jni();
    app_error e = {0};
    if (image) { app_error_set(&e, APP_UNSUPPORTED, "Image clipboard adapter unavailable on this OS."); done(task, &e); return; }
    if (!text) { app_error_set(&e, APP_INVALID_DATA, "Clipboard content is empty."); done(task, &e); return; }
    (*env)->PushLocalFrame(env, 16);
    char *copy = app_strndup(text, text_length);
    jobject clip = call_static("android/content/ClipData", "newPlainText", "(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Landroid/content/ClipData;",
                               jstr("Aquarius"), jstr(copy));
    free(copy);
    jobject manager = clipboard_manager();
    if (manager && clip) {
        jclass type = (*env)->FindClass(env, "android/content/ClipboardManager");
        (*env)->CallVoidMethod(env, manager, (*env)->GetMethodID(env, type, "setPrimaryClip", "(Landroid/content/ClipData;)V"), clip);
    }
    bool ok = !java_failed(&e, APP_UNAVAILABLE, "The clipboard is unavailable") && manager && clip;
    if (!ok && !e.failed) app_error_set(&e, APP_UNAVAILABLE, "The clipboard is unavailable.");
    (*env)->PopLocalFrame(env, NULL);
    done(task, ok ? NULL : &e);
}

/* ---- Capabilities and the remaining services ---- */

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "files", app_capability(rt, true, "App storage, temporary files and documents chosen in system pickers"));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, true, NULL));
    app_set(rt, hash, "pickers", app_capability(rt, true, "Storage Access Framework documents"));
    app_set(rt, hash, "printing", app_capability(rt, false, "Printing needs the application's print adapter."));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, true, "Image acquisition selects an existing image."));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, false, NULL));
    app_set(rt, hash, "accessibility", app_capability(rt, true, "Content description of the activity's view."));
    app_set(rt, hash, "fileAssociations", app_capability(rt, false, "Intent filters belong to the application's manifest."));
    app_set(rt, hash, "activation", app_capability(rt, true, "Documents the activity was opened with."));
}

void app_os_print(app_task *task, const app_picture *picture, app_done done) {
    (void)picture;
    app_error e = {0};
    app_error_set(&e, APP_UNSUPPORTED, "Native printing adapter unavailable on this OS.");
    done(task, &e);
}

/* The labels and values become the activity view's content description, which screen readers announce. */
bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e) {
    (void)state;
    JNIEnv *env = jni();
    app_bytes text = {0};
    for (size_t i = 0; i < count; i++) {
        if (i) app_bytes_text(&text, "\n");
        app_bytes_text(&text, nodes[i].label);
        if (nodes[i].value) { app_bytes_text(&text, ": "); app_bytes_text(&text, nodes[i].value); }
    }
    app_bytes_byte(&text, 0);
    (*env)->PushLocalFrame(env, 16);
    jobject window = call_object(activity(), "android/app/Activity", "getWindow", "()Landroid/view/Window;");
    jobject view = window ? call_object(window, "android/view/Window", "getDecorView", "()Landroid/view/View;") : NULL;
    if (view) {
        jclass type = (*env)->FindClass(env, "android/view/View");
        (*env)->CallVoidMethod(env, view, (*env)->GetMethodID(env, type, "setContentDescription", "(Ljava/lang/CharSequence;)V"), jstr((const char *)text.data));
    }
    bool ok = !java_failed(e, APP_UNAVAILABLE, "No active graphics window accessibility adapter") && view;
    if (!ok && !e->failed) app_error_set(e, APP_UNAVAILABLE, "No active graphics window accessibility adapter.");
    (*env)->PopLocalFrame(env, NULL);
    app_bytes_free(&text);
    return ok;
}

bool app_os_register_association(const app_association *association, app_error *e) {
    (void)association;
    return app_error_set(e, APP_UNSUPPORTED, "File association registration requires a Windows adapter.");
}
