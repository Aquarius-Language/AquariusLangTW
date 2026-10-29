/* WebFrame / 網頁框架: embeds other web pages (iframes) in a web application's page, placed in the application's own
 * coordinates over its drawing: a live preview beside an editor, documentation, a page of another application.
 * Placement, state and validation are here; src/platform/web.c only creates and moves the elements. Targets without
 * a page (src/platform/none.c) report it: Supported() is false and Create fails. */
#include "webframe.h"
#include <stdlib.h>
#include <string.h>

typedef struct frame { int32_t handle; bool closed; } frame;
static aqua_class *frame_class;

static void frame_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    frame *f = data;
    if (!f->closed) webframe_remove(f->handle);
    free(f);
}

static frame *live(aqua_call *call, const char *name) {
    frame *f = aqua_self(call);
    if (f->closed) { aqua_fail(call, "WebFrame.%s / 網頁框架: the frame is closed / 框架已關閉。", name); return NULL; }
    return f;
}

static bool valid_url(aqua_call *call, aqua_str url) {
    /* Pages only: no script URLs. */
    if (url.length == 0 || strncmp(url.data, "javascript:", 11) == 0 || strlen(url.data) != url.length) {
        aqua_fail(call, "WebFrame: invalid page URL / 網頁網址無效。");
        return false;
    }
    return true;
}

static void supported(aqua_call *call) { aqua_return_bool(call, webframe_supported()); }

static void create(aqua_call *call) {
    if (!webframe_supported()) { aqua_fail(call, "WebFrame.Create: Unsupported / 不支援：%s has no web page to embed into.", AQUA_PLATFORM_NAME); return; }
    aqua_str url = aqua_arg_str(call, 0);
    if (!valid_url(call, url)) return;
    int32_t handle = webframe_create(url.data);
    if (!handle) { aqua_fail(call, "WebFrame.Create: the page cannot hold another frame / 無法建立框架。"); return; }
    frame *f = calloc(1, sizeof *f);
    f->handle = handle;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), frame_class, f));
}

static void place(aqua_call *call) {
    frame *f = live(call, "Place");
    if (!f) return;
    double width = aqua_arg_number(call, 2), height = aqua_arg_number(call, 3);
    webframe_place(f->handle, aqua_arg_number(call, 0), aqua_arg_number(call, 1), width > 0 ? width : 0, height > 0 ? height : 0);
    aqua_return(call, aqua_self_value(call));
}

static void navigate(aqua_call *call) {
    frame *f = live(call, "Navigate");
    aqua_str url = aqua_arg_str(call, 0);
    if (!f || !valid_url(call, url)) return;
    webframe_navigate(f->handle, url.data);
    aqua_return(call, aqua_self_value(call));
}

static void reload(aqua_call *call) {
    frame *f = live(call, "Reload");
    if (f) { webframe_reload(f->handle); aqua_return(call, aqua_self_value(call)); }
}

static void show(aqua_call *call) {
    frame *f = live(call, "Show");
    if (f) { webframe_show(f->handle, aqua_arg_bool(call, 0)); aqua_return(call, aqua_self_value(call)); }
}

static void close_frame(aqua_call *call) {
    frame *f = aqua_self(call);
    if (!f->closed) { f->closed = true; webframe_remove(f->handle); }
    aqua_return_bool(call, true);
}

AQUA_LIBRARY(webframe) {
    aqua_class *module = aqua_define_module(library, "WebFrame|網頁框架");
    aqua_define_function(module, "Supported|支援", supported, "");
    aqua_define_function(module, "Create|建立", create, "s");
    frame_class = aqua_define_class(library, "WebFrame.Frame", frame_finalize);
    aqua_define_function(frame_class, "Place|放置", place, "nnnn");
    aqua_define_function(frame_class, "Navigate|前往", navigate, "s");
    aqua_define_function(frame_class, "Reload|重新載入", reload, "");
    aqua_define_function(frame_class, "Show|顯示", show, "b");
    aqua_define_function(frame_class, "Close|關閉", close_frame, "");
}
