/* Clipboard: Unicode text everywhere, images as PNG (plus a DIBV5 on Windows) where the OS layer supports them. */
#include "application.h"
#include "platform/platform.h"
#include <stdlib.h>
#include <string.h>

void app_clipboard_Capabilities(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value hash = aqua_hash_new(rt, 3);
    app_os_clipboard_capabilities(rt, hash);
    aqua_return(call, app_record(rt, hash));
}

typedef struct clip_task { app_task task; app_picture picture; } clip_task;
static void clip_cleanup(app_task *task) { app_picture_free(&((clip_task *)task)->picture); }
static clip_task *clip_task_new(aqua_call *call) {
    clip_task *t = app_task_new(call, sizeof *t);
    t->task.cleanup = clip_cleanup;
    return t;
}

static void formats_done(app_task *task, const app_error *e, const char *const *formats, size_t count) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    aqua_value array = aqua_array_new(task->rt, (uint32_t)count);
    for (size_t i = 0; i < count; i++) aqua_array_set(task->rt, array, (uint32_t)i, aqua_cstring(task->rt, formats[i]));
    app_task_return(task, array);
}

void app_clipboard_Formats(aqua_call *call) {
    if (app_cancelled(call)) return;
    clip_task *t = clip_task_new(call);
    app_os_clipboard_formats(&t->task, formats_done);
    app_task_yield(&t->task);
}

static void text_done(app_task *task, const app_error *e, const char *text, size_t length, const uint8_t *image, size_t image_length) {
    (void)image; (void)image_length;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_task_return(task, text ? aqua_text(task->rt, text, length) : aqua_null());
}

void app_clipboard_ReadText(aqua_call *call) {
    if (app_cancelled(call)) return;
    clip_task *t = clip_task_new(call);
    app_os_clipboard_read(&t->task, false, text_done);
    app_task_yield(&t->task);
}

static void image_done(app_task *task, const app_error *e, const char *text, size_t length, const uint8_t *image, size_t image_length) {
    (void)text; (void)length;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    if (!image) { app_task_return(task, aqua_null()); return; }
    app_error error = {0};
    app_picture picture;
    if (!app_image_decode(image, image_length, 0, &picture, &error)) { app_task_fail(task, &error); return; }
    app_task_return(task, app_picture_value(task->rt, &picture));
}

void app_clipboard_ReadImage(aqua_call *call) {
    if (app_cancelled(call)) return;
    clip_task *t = clip_task_new(call);
    app_os_clipboard_read(&t->task, true, image_done);
    app_task_yield(&t->task);
}

static void written(app_task *task, const app_error *e) {
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    app_task_return(task, aqua_void());
}

void app_clipboard_WriteText(aqua_call *call) {
    if (app_cancelled(call)) return;
    aqua_str text = aqua_arg_str(call, 0);
    clip_task *t = clip_task_new(call);
    app_os_clipboard_write(&t->task, text.data, text.length, NULL, written);
    app_task_yield(&t->task);
}

void app_clipboard_WriteImage(aqua_call *call) {
    if (app_cancelled(call)) return;
    app_error e = {0};
    clip_task *t = clip_task_new(call);
    if (!app_picture_of(aqua_call_runtime(call), aqua_arg(call, 0), &t->picture, &e)) {
        app_task_fail(&t->task, &e);
        app_task_yield(&t->task);
        return;
    }
    bool has_text = aqua_has_arg(call, 1);
    aqua_str text = has_text ? aqua_arg_str(call, 1) : (aqua_str){NULL, 0};
    app_os_clipboard_write(&t->task, text.data, text.length, &t->picture, written);
    app_task_yield(&t->task);
}
