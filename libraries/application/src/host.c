/* Application: host capabilities, launch documents, open/save/directory pickers, image acquisition, printing, the
 * accessibility mirror and per-user file associations. Options are hashes with case-insensitive field names. */
#include "application.h"
#include "platform/platform.h"
#include <stdlib.h>
#include <string.h>

void app_application_Capabilities(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value hash = aqua_hash_new(rt, 12);
    app_os_capabilities(rt, hash);
    aqua_return(call, app_record(rt, hash));
}

/* An environment variable, or null. On the web the page supplies them: AQUARIUS_QUERY is the page's query string. */
void app_application_Environment(aqua_call *call) {
    const char *value = aqua_environment(aqua_call_runtime(call), aqua_arg_str(call, 0).data);
    if (value) aqua_return_cstring(call, value);
    else aqua_return_null(call);
}

/* ---- Options ---- */

static bool text_option(aqua_runtime *rt, aqua_value hash, const char *name, char **out, bool required, app_error *e) {
    aqua_value v;
    *out = NULL;
    if (!app_field(rt, hash, name, &v) || aqua_is_nullish(v)) {
        if (required) return app_error_set(e, APP_INVALID_DATA, "The JSON property '%s' is required.", name);
        return true;
    }
    if (v.type != AQUA_STRING) return app_error_set(e, APP_INVALID_DATA, "The JSON value of '%s' could not be converted to a string.", name);
    *out = aqua_text_dup(rt, v, NULL);
    return true;
}

static void picker_free(app_picker *p) {
    aqua_free(p->title);
    aqua_free(p->suggested);
    for (size_t i = 0; i < p->filter_count; i++) {
        aqua_free(p->filters[i].name);
        for (size_t k = 0; k < p->filters[i].count; k++) aqua_free(p->filters[i].extensions[k]);
        free(p->filters[i].extensions);
    }
    free(p->filters);
    memset(p, 0, sizeof *p);
}

static bool picker_of(aqua_runtime *rt, aqua_value options, app_picker *p, app_error *e) {
    memset(p, 0, sizeof *p);
    if (options.type != AQUA_HASH) return app_error_set(e, APP_INVALID_DATA, "Invalid options.");
    if (!text_option(rt, options, "title", &p->title, false, e) || !text_option(rt, options, "suggestedName", &p->suggested, false, e)) return false;
    aqua_value v;
    if (app_field(rt, options, "multiple", &v) && !aqua_is_nullish(v)) {
        if (v.type != AQUA_BOOL) return app_error_set(e, APP_INVALID_DATA, "The JSON value of 'multiple' could not be converted to a Boolean.");
        p->multiple = v.number != 0;
    }
    if (app_field(rt, options, "filters", &v) && !aqua_is_nullish(v)) {
        if (v.type != AQUA_ARRAY) return app_error_set(e, APP_INVALID_DATA, "The JSON value of 'filters' could not be converted to a filter list.");
        uint32_t count = aqua_array_length(rt, v);
        p->filters = calloc(count ? count : 1, sizeof *p->filters);
        for (uint32_t i = 0; i < count; i++) {
            aqua_value filter = aqua_array_get(rt, v, i), extensions;
            if (filter.type != AQUA_HASH) return app_error_set(e, APP_INVALID_DATA, "Invalid options.");
            app_filter *f = &p->filters[p->filter_count++];
            if (!text_option(rt, filter, "name", &f->name, false, e)) return false;
            if (!f->name) { f->name = aqua_alloc(1); f->name[0] = 0; }
            if (!app_field(rt, filter, "extensions", &extensions) || extensions.type != AQUA_ARRAY)
                return app_error_set(e, APP_INVALID_DATA, "The JSON value of 'extensions' could not be converted to a list of strings.");
            uint32_t n = aqua_array_length(rt, extensions);
            f->extensions = calloc(n ? n : 1, sizeof *f->extensions);
            for (uint32_t k = 0; k < n; k++) {
                aqua_value ext = aqua_array_get(rt, extensions, k);
                if (ext.type != AQUA_STRING) return app_error_set(e, APP_INVALID_DATA, "The JSON value of 'extensions' could not be converted to a list of strings.");
                char *text = aqua_text_dup(rt, ext, NULL);
                /* Simple extensions only: letters and digits ("png", ".png", "星板"), or "*" for every file. */
                const char *bare = text[0] == '.' ? text + 1 : text;
                bool valid = strcmp(bare, "*") == 0 || bare[0] != 0;
                for (const char *c = bare; *c && valid && strcmp(bare, "*") != 0; c++)
                    valid = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || (uint8_t)*c >= 0x80;
                if (!valid) { aqua_free(text); return app_error_set(e, APP_INVALID_DATA, "Filters require simple filename extensions."); }
                memmove(text, bare, strlen(bare) + 1);
                f->extensions[f->count++] = text;
            }
        }
    }
    if (p->suggested && strlen(p->suggested) >= 65535) return app_error_set(e, APP_INVALID_DATA, "Suggested name too long.");
    return true;
}

/* ---- Pickers ---- */

typedef struct host_task { app_task task; app_picker picker; bool single; app_resource selected; app_picture picture; } host_task;

static void host_cleanup(app_task *task) {
    host_task *t = (host_task *)task;
    picker_free(&t->picker);
    app_resource_free(&t->selected);
    app_picture_free(&t->picture);
}

static host_task *host_task_new(aqua_call *call) {
    host_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = host_cleanup;
    return t;
}

static void free_resources(app_resource *items, size_t count) {
    for (size_t i = 0; i < count; i++) app_resource_free(&items[i]);
    free(items);
}

static void picked(app_task *task, const app_error *e, app_resource *items, size_t count) {
    host_task *t = (host_task *)task;
    if (e) { free_resources(items, count); app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) { free_resources(items, count); return; }
    aqua_value result;
    if (t->single) result = count ? app_resource_value(task->rt, &items[0]) : aqua_null();
    else {
        result = aqua_array_new(task->rt, (uint32_t)count);
        for (size_t i = 0; i < count; i++) aqua_array_set(task->rt, result, (uint32_t)i, app_resource_value(task->rt, &items[i]));
    }
    free_resources(items, count);
    app_task_return(task, result);
}

void app_application_LaunchFiles(aqua_call *call) {
    if (app_cancelled(call)) return;
    host_task *t = host_task_new(call);
    app_os_launch_files(&t->task, picked);
    app_task_yield(&t->task);
}

static void pick(aqua_call *call, bool save) {
    if (app_cancelled(call)) return;
    host_task *t = host_task_new(call);
    app_error e = {0};
    if (!picker_of(aqua_call_runtime(call), aqua_arg(call, 0), &t->picker, &e)) app_task_fail(&t->task, &e);
    else if (save) { t->single = true; app_os_save_file(&t->task, &t->picker, picked); }
    else app_os_open_files(&t->task, &t->picker, picked);
    app_task_yield(&t->task);
}

void app_application_OpenFiles(aqua_call *call) { pick(call, false); }
void app_application_SaveFile(aqua_call *call) { pick(call, true); }

void app_application_PickDirectory(aqua_call *call) {
    if (app_cancelled(call)) return;
    host_task *t = host_task_new(call);
    t->single = true;
    app_os_pick_directory(&t->task, picked);
    app_task_yield(&t->task);
}

/* AcquireImage: picks one image file (mobile browsers may offer the camera), decodes its first frame, or null. */
static void acquired(app_task *task, const app_error *e, app_bytes *data) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_error error = {0};
    app_picture picture;
    if (!app_image_decode(data->data, data->length, 0, &picture, &error)) { app_task_fail(task, &error); return; }
    app_task_return(task, app_picture_value(task->rt, &picture));
}

static void image_picked(app_task *task, const app_error *e, app_resource *items, size_t count) {
    host_task *t = (host_task *)task;
    if (e) { free_resources(items, count); app_task_fail(task, e); return; }
    if (!count) { free_resources(items, count); app_task_return(task, aqua_null()); return; }
    t->selected = items[0];
    memset(&items[0], 0, sizeof items[0]);
    free_resources(items, count);
    app_fs_read(task, &t->selected, acquired);
}

void app_application_AcquireImage(aqua_call *call) {
    if (app_cancelled(call)) return;
    static const char *const extensions[] = {"png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff"};
    host_task *t = host_task_new(call);
    app_picker *p = &t->picker;
    p->title = aqua_alloc(13); memcpy(p->title, "Select image", 13);
    p->capture = true;
    p->filters = calloc(1, sizeof *p->filters);
    p->filter_count = 1;
    p->filters[0].name = aqua_alloc(7); memcpy(p->filters[0].name, "Images", 7);
    p->filters[0].extensions = calloc(7, sizeof(char *));
    for (int i = 0; i < 7; i++) {
        size_t n = strlen(extensions[i]) + 1;
        p->filters[0].extensions[i] = aqua_alloc(n);
        memcpy(p->filters[0].extensions[i], extensions[i], n);
    }
    p->filters[0].count = 7;
    app_os_open_files(&t->task, p, image_picked);
    app_task_yield(&t->task);
}

static void printed(app_task *task, const app_error *e) {
    if (e) app_task_fail(task, e);
    else if (!app_task_cancelled(task)) app_task_return(task, aqua_void());
}

void app_application_Print(aqua_call *call) {
    if (app_cancelled(call)) return;
    host_task *t = host_task_new(call);
    app_error e = {0};
    if (!app_picture_of(aqua_call_runtime(call), aqua_arg(call, 0), &t->picture, &e)) app_task_fail(&t->task, &e);
    else app_os_print(&t->task, &t->picture, printed);
    app_task_yield(&t->task);
}

/* ---- Accessibility and associations ---- */

void app_application_Accessibility(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_arg(call, 0);
    app_error e = {0};
    if (list.type != AQUA_ARRAY) { app_fail_with(call, APP_INVALID_DATA, "Invalid options."); return; }
    uint32_t count = aqua_array_length(rt, list);
    if (count > APP_MAX_ENTRIES) { app_fail_with(call, APP_INVALID_DATA, "Invalid accessibility node list."); return; }
    app_node *nodes = calloc(count ? count : 1, sizeof *nodes);
    bool ok = true;
    for (uint32_t i = 0; i < count && ok; i++) {
        aqua_value node = aqua_array_get(rt, list, i), v;
        if (node.type != AQUA_HASH) { ok = app_error_set(&e, APP_INVALID_DATA, "Invalid accessibility node list."); break; }
        ok = text_option(rt, node, "id", &nodes[i].id, false, &e) && text_option(rt, node, "role", &nodes[i].role, false, &e) &&
             text_option(rt, node, "label", &nodes[i].label, false, &e) && text_option(rt, node, "value", &nodes[i].value, false, &e);
        if (ok && app_field(rt, node, "disabled", &v) && !aqua_is_nullish(v)) {
            if (v.type != AQUA_BOOL) ok = app_error_set(&e, APP_INVALID_DATA, "The JSON value of 'disabled' could not be converted to a Boolean.");
            else nodes[i].disabled = v.number != 0;
        }
        if (ok && (!nodes[i].id || !nodes[i].id[0] || !nodes[i].role || !nodes[i].label)) ok = app_error_set(&e, APP_INVALID_DATA, "Invalid accessibility node list.");
        for (uint32_t k = 0; ok && k < i; k++) if (strcmp(nodes[k].id, nodes[i].id) == 0) ok = app_error_set(&e, APP_INVALID_DATA, "Invalid accessibility node list.");
    }
    if (ok) ok = app_os_accessibility(app_state_of(call), nodes, count, &e);
    for (uint32_t i = 0; i < count; i++) { aqua_free(nodes[i].id); aqua_free(nodes[i].role); aqua_free(nodes[i].label); aqua_free(nodes[i].value); }
    free(nodes);
    if (!ok) app_fail(call, &e);
}

void app_application_RegisterFileAssociation(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value options = aqua_arg(call, 0), arguments;
    app_error e = {0};
    app_association a = {0};
    bool ok = options.type == AQUA_HASH || app_error_set(&e, APP_INVALID_DATA, "Invalid options.");
    if (ok) ok = text_option(rt, options, "extension", &a.extension, true, &e) && text_option(rt, options, "applicationId", &a.application, true, &e) &&
                 text_option(rt, options, "description", &a.description, true, &e) && text_option(rt, options, "executable", &a.executable, true, &e);
    if (ok) {
        if (!app_field(rt, options, "arguments", &arguments) || arguments.type != AQUA_ARRAY) ok = app_error_set(&e, APP_INVALID_DATA, "The JSON property 'arguments' is required.");
        else {
            uint32_t n = aqua_array_length(rt, arguments);
            a.arguments = calloc(n ? n : 1, sizeof *a.arguments);
            for (uint32_t i = 0; i < n && ok; i++) {
                aqua_value item = aqua_array_get(rt, arguments, i);
                if (item.type != AQUA_STRING) ok = app_error_set(&e, APP_INVALID_DATA, "The JSON value of 'arguments' could not be converted to a list of strings.");
                else a.arguments[a.count++] = aqua_text_dup(rt, item, NULL);
            }
        }
    }
    if (ok) ok = app_os_register_association(&a, &e);
    aqua_free(a.extension); aqua_free(a.application); aqua_free(a.description); aqua_free(a.executable);
    for (size_t i = 0; i < a.count; i++) aqua_free(a.arguments[i]);
    free(a.arguments);
    if (!ok) app_fail(call, &e);
}
