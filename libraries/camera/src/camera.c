/* The 攝影機/Webcam/網路攝影機 and Camera/相機 modules: names, arities, validation, session ownership, the latest-frame
 * mailbox, timeouts and messages of the old runtime (WebcamRuntime.cs, CameraRuntime.cs). Opening, listing (on the web)
 * and capturing wait as deferred calls; whatever the platform reports from its threads arrives through aqua_post. */
#include "camera.h"
#include <aqua/image.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <time.h>
#endif

#define MAX_SESSIONS 4
#define TIMEOUT_MS 10000 /* the old WebcamOptions/CameraOptions default; scripts cannot change it */

/* The script-facing differences between the two modules. */
struct camera_module {
    bool served;                       /* the platform layer serves this module on this target */
    bool english;                      /* Camera: English function and failure names in messages, stretched frames */
    const char *prefix;                /* 攝影機 or 相機 */
    const char *unsupported;           /* the message where the module is not served */
    aqua_class *session_class, *frame_class, *device_class;
    /* Function names as messages show them. */
    const char *f_list, *f_open, *f_capture;
};

struct camera_state {
    aqua_runtime *rt;
    camera_session *sessions;          /* opening and open sessions (closed ones until collected) */
    camera_listing *listings;          /* listings the platform still runs */
    atomic_bool posted;                /* a delivery task is queued */
    bool ticking;                      /* the timeout tick is scheduled */
};

static camera_module webcam = {
#if defined(CAMERA_WEBCAM)
    .served = true,
#endif
    .prefix = "攝影機", .unsupported = "此平台請使用相機模組（Camera）。",
    .f_list = "列出裝置", .f_open = "開啟", .f_capture = "擷取影格",
};
static camera_module mobile = {
#if defined(CAMERA_MOBILE)
    .served = true,
#endif
    .english = true, .prefix = "相機", .unsupported = "此平台請使用網路攝影機。",
    .f_list = "Enumerate", .f_open = "Open", .f_capture = "Capture",
};

static void deliver(aqua_runtime *rt, void *data);

/* ---- Messages ---- */

static const char *kind_name(const camera_module *m, camera_failure f) {
    static const char *const chinese[] = {"", "不支援", "權限遭拒", "找不到裝置", "裝置無法使用", "擷取逾時", "已取消", "已關閉", "參數無效"};
    static const char *const english[] = {"", "Unsupported", "PermissionDenied", "NotFound", "Unavailable", "Timeout", "Cancelled", "Closed", "InvalidArgument"};
    return (m->english ? english : chinese)[f];
}

/* "攝影機.開啟：參數無效：…" (Webcam) or "相機.Open：InvalidArgument：…" (Camera). */
static void format_failure(char *out, size_t size, const camera_module *m, const char *function, camera_failure f, const char *message) {
    snprintf(out, size, "%s.%s：%s：%s", m->prefix, function, kind_name(m, f), message);
}
static void fail(aqua_call *call, const camera_module *m, const char *function, camera_failure f, const char *message) {
    char text[512];
    format_failure(text, sizeof text, m, function, f, message);
    aqua_fail(call, "%s", text);
}
static void reject(aqua_pending *pending, const camera_module *m, const char *function, camera_failure f, const char *message) {
    char text[512];
    format_failure(text, sizeof text, m, function, f, message);
    aqua_reject(pending, "%s", text);
}
static const char *closed_message(const camera_module *m) { return m->english ? "相機工作階段已關閉。" : "攝影機工作階段已關閉。"; }

double camera_now_ms(void) {
#if defined(AQUA_OS_WINDOWS)
    static LARGE_INTEGER frequency;
    LARGE_INTEGER now;
    if (!frequency.QuadPart) QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&now);
    return (double)now.QuadPart * 1000.0 / (double)frequency.QuadPart;
#else
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec * 1000.0 + (double)t.tv_nsec / 1e6;
#endif
}

/* ---- Devices and frames ---- */

static const char *facing_name(camera_facing f) { return f == CAMERA_FRONT ? "Front" : f == CAMERA_EXTERNAL ? "External" : "Back"; }

typedef struct device_object { char *id, *name; camera_facing facing; } device_object;
static void device_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    device_object *d = data;
    free(d->id); free(d->name); free(d);
}
static device_object *self_device(aqua_call *call) { return aqua_self(call); }
static void device_id(aqua_call *call) { aqua_return_cstring(call, self_device(call)->id); }
static void device_name(aqua_call *call) { aqua_return_cstring(call, self_device(call)->name); }
static void device_facing(aqua_call *call) { aqua_return_cstring(call, facing_name(self_device(call)->facing)); }

typedef struct frame_object { int32_t width, height; double sequence, timestamp; camera_facing facing; aqua_ref image; } frame_object;
static void frame_finalize(aqua_runtime *rt, void *data) {
    frame_object *f = data;
    aqua_release(rt, f->image);
    free(f);
}
static frame_object *self_frame(aqua_call *call) { return aqua_self(call); }
static void frame_width(aqua_call *call) { aqua_return_int(call, self_frame(call)->width); }
static void frame_height(aqua_call *call) { aqua_return_int(call, self_frame(call)->height); }
static void frame_sequence(aqua_call *call) { aqua_return_double(call, self_frame(call)->sequence); }
static void frame_timestamp(aqua_call *call) { aqua_return_double(call, self_frame(call)->timestamp); }
static void frame_facing(aqua_call *call) { aqua_return_cstring(call, facing_name(self_frame(call)->facing)); }
static void frame_image(aqua_call *call) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), self_frame(call)->image)); }
/* 取得像素: a new array of the RGBA bytes (red, green, blue, opacity), top row first. */
static void frame_pixels(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_image *image = aqua_image_of(rt, aqua_ref_value(rt, self_frame(call)->image));
    aqua_return(call, aqua_array_from_u8(rt, image->rgba, (uint32_t)image->width * (uint32_t)image->height * 4));
}

/* The listed devices as script objects (takes the strings). */
static aqua_value device_list(aqua_runtime *rt, camera_listing *l) {
    aqua_value list = aqua_array_new(rt, l->count);
    for (uint32_t i = 0; i < l->count; i++) {
        device_object *d = calloc(1, sizeof *d);
        d->id = l->devices[i].id; d->name = l->devices[i].name; d->facing = l->devices[i].facing;
        l->devices[i].id = l->devices[i].name = NULL;
        aqua_array_set(rt, list, i, aqua_object_new(rt, l->module->device_class, d));
    }
    return list;
}

static void listing_free(camera_listing *l) {
    for (uint32_t i = 0; i < l->count; i++) { free(l->devices[i].id); free(l->devices[i].name); }
    free(l->devices);
    free(l);
}

void camera_listing_add(camera_listing *l, const char *id, const char *name, camera_facing facing) {
    camera_device *grown = realloc(l->devices, (l->count + 1) * sizeof *l->devices);
    if (!grown) return;
    l->devices = grown;
    l->devices[l->count].id = strdup(id ? id : "");
    l->devices[l->count].name = strdup(name ? name : "");
    l->devices[l->count].facing = facing;
    l->count++;
}

void camera_listed(camera_listing *l) {
    camera_state *state = l->state;
    if (!state) { listing_free(l); return; } /* the runtime ended while the platform listed */
    atomic_store(&l->done, true);
    if (l->pending && !atomic_exchange(&state->posted, true)) aqua_post(state->rt, deliver, state);
}

/* ---- Sessions ---- */

static void session_free(camera_session *s) {
    camera_mutex_destroy(&s->lock);
    camera_converter_free(&s->converter);
    free(s->latest);
    free(s->spare);
    free(s->device_id);
    free(s);
}

static void unlink_session(camera_session *s) {
    for (camera_session **p = &s->state->sessions; *p; p = &(*p)->next) if (*p == s) { *p = s->next; return; }
}

/* Asks for a delivery on the runtime's thread when the runtime waits for this session. Any thread. */
static void notify(camera_session *s) {
    camera_state *state = s->state;
    if (atomic_load(&s->waiting) && !atomic_exchange(&state->posted, true)) aqua_post(state->rt, deliver, state);
}

void camera_frame(camera_session *s, const camera_source *source, double timestamp_ms) {
    /* Webcam frames have the requested size (letterboxed); Camera frames are the upright image stretched to it, turned
       to portrait when the image is. */
    bool letterbox = !s->module->english, turned = !letterbox && (source->rotation == 90 || source->rotation == 270);
    int32_t width = turned ? s->height : s->width, height = turned ? s->width : s->height;
    if (!s->spare) s->spare = malloc((size_t)s->width * (size_t)s->height * 4);
    if (!s->spare || !camera_convert(&s->converter, source, width, height, letterbox, s->spare)) {
        camera_fail(s, CAMERA_UNAVAILABLE, s->module->english ? "相機傳回無效影格。" : "攝影機傳回無效影格。");
        return;
    }
    camera_lock(&s->lock);
    uint8_t *previous = s->latest;
    s->latest = s->spare;
    s->spare = previous;
    s->latest_width = width;
    s->latest_height = height;
    s->sequence++;
    s->timestamp = timestamp_ms;
    s->ready = true;
    camera_unlock(&s->lock);
    notify(s);
}

void camera_fail(camera_session *s, camera_failure failure, const char *message) {
    camera_lock(&s->lock);
    if (s->failure == CAMERA_OK) {
        s->failure = failure;
        snprintf(s->failure_message, sizeof s->failure_message, "%s", message);
    }
    camera_unlock(&s->lock);
    notify(s);
}

/* Stops the device. A capture still waiting fails as closed. Runtime thread. */
static void close_session(camera_session *s) {
    if (s->closed) return;
    s->closed = true;
    camera_platform_close(s);
    atomic_store(&s->waiting, false);
    s->deadline = 0;
    if (s->capture_pending) {
        reject(s->capture_pending, s->module, s->module->f_capture, CAMERA_CLOSED, closed_message(s->module));
        s->capture_pending = NULL;
    }
    aqua_release(s->state->rt, s->capture_self);
    s->capture_self = 0;
}

static void session_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    camera_session *s = data;
    close_session(s);
    unlink_session(s);
    session_free(s);
}

static camera_session *self_session(aqua_call *call) { return aqua_self(call); }

/* Open: not closed, not failed and still running. */
static bool session_open(camera_session *s) {
    if (s->closed) return false;
    camera_lock(&s->lock);
    bool failed = s->failure != CAMERA_OK;
    camera_unlock(&s->lock);
    return !failed && camera_platform_live(s);
}

static bool has_new_frame(camera_session *s) {
    camera_lock(&s->lock);
    bool fresh = s->ready && s->sequence > s->delivered;
    camera_unlock(&s->lock);
    return fresh;
}

/* The newest undelivered frame as an independent snapshot: later frames never change it. Runtime thread. */
static aqua_value take_frame(camera_session *s) {
    aqua_runtime *rt = s->state->rt;
    frame_object *f = calloc(1, sizeof *f);
    camera_lock(&s->lock);
    aqua_value image = aqua_image_new(rt, s->latest_width, s->latest_height, s->latest, AQUA_ALPHA_OPAQUE);
    f->width = s->latest_width; f->height = s->latest_height;
    f->sequence = (double)s->sequence; f->timestamp = s->timestamp;
    s->delivered = s->sequence;
    camera_unlock(&s->lock);
    f->facing = s->facing;
    f->image = aqua_retain(rt, image);
    return aqua_object_new(rt, s->module->frame_class, f);
}

/* ---- Deliveries on the runtime's thread ---- */

static void deliver(aqua_runtime *rt, void *data) {
    camera_state *state = data;
    atomic_store(&state->posted, false);
    for (camera_listing **p = &state->listings; *p;) {
        camera_listing *l = *p;
        if (!atomic_load(&l->done)) { p = &l->next; continue; }
        *p = l->next;
        if (l->failure != CAMERA_OK) reject(l->pending, l->module, l->module->f_list, l->failure, l->failure_message);
        else aqua_resolve(l->pending, device_list(rt, l));
        listing_free(l);
    }
    for (camera_session *s = state->sessions, *next; s; s = next) {
        next = s->next;
        if (s->closed || (!s->open_pending && !s->capture_pending)) continue;
        camera_lock(&s->lock);
        camera_failure failure = s->failure;
        char message[sizeof s->failure_message];
        memcpy(message, s->failure_message, sizeof message);
        bool ready = s->ready;
        camera_unlock(&s->lock);
        if (s->open_pending) {
            aqua_pending *pending = s->open_pending;
            if (failure != CAMERA_OK) {
                /* Never handed to the script: the session ends here. */
                const camera_module *m = s->module;
                s->open_pending = NULL;
                close_session(s);
                unlink_session(s);
                session_free(s);
                reject(pending, m, m->f_open, failure, message);
                continue;
            }
            if (!ready) continue;
            s->open_pending = NULL;
            s->deadline = 0;
            aqua_resolve(pending, aqua_object_new(rt, s->module->session_class, s));
        } else {
            aqua_pending *pending = s->capture_pending;
            if (failure != CAMERA_OK) {
                /* A failed capture ends the session; the script may open a new one. */
                s->capture_pending = NULL;
                close_session(s);
                reject(pending, s->module, s->module->f_capture, failure, message);
                continue;
            }
            if (!has_new_frame(s)) continue;
            s->capture_pending = NULL;
            s->deadline = 0;
            aqua_resolve(pending, take_frame(s));
            aqua_release(rt, s->capture_self);
            s->capture_self = 0;
        }
        atomic_store(&s->waiting, false);
    }
}

/* Fails operations whose deadline passed; runs every 100 ms while one waits. */
static double timeout_tick(aqua_runtime *rt, double now, void *data) {
    camera_state *state = data;
    bool waiting = false;
    for (camera_session *s = state->sessions; s; s = s->next) {
        if (!s->deadline) continue;
        if (now >= s->deadline) {
            s->deadline = 0;
            camera_fail(s, CAMERA_TIMEOUT, s->module->english ? "相機操作逾時，工作階段已關閉。" : "攝影機等待新影格逾時。");
        } else waiting = true;
    }
    deliver(rt, state);
    if (!waiting) state->ticking = false;
    return waiting ? 100 : -1;
}

static void watch_deadline(camera_session *s) {
    camera_state *state = s->state;
    s->deadline = aqua_now_ms(state->rt) + s->timeout_ms;
    if (state->ticking) return;
    state->ticking = true;
    aqua_schedule(state->rt, 100, timeout_tick, state);
}

/* ---- Module functions ---- */

static void is_supported(aqua_call *call, camera_module *m) { aqua_return_bool(call, m->served && camera_platform_supported()); }

static void list_devices(aqua_call *call, camera_module *m) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!m->served) {
        /* As before: Camera lists nothing where it is unavailable; Webcam reports itself unsupported. */
        if (m->english) aqua_return(call, aqua_array_new(rt, 0));
        else fail(call, m, m->f_list, CAMERA_UNSUPPORTED, m->unsupported);
        return;
    }
    if (!camera_platform_supported()) { fail(call, m, m->f_list, CAMERA_UNSUPPORTED, "此環境未提供攝影機介面。"); return; }
    camera_state *state = aqua_state(call);
    camera_listing *l = calloc(1, sizeof *l);
    l->state = state;
    l->module = m;
    camera_platform_list(l);
    if (atomic_load(&l->done)) { /* listed at once (native targets) */
        if (l->failure != CAMERA_OK) fail(call, m, m->f_list, l->failure, l->failure_message);
        else aqua_return(call, device_list(rt, l));
        listing_free(l);
        return;
    }
    l->pending = aqua_defer(call);
    l->next = state->listings;
    state->listings = l;
}

/* Integer(value) of the old runtimes: a positive integral number. */
static bool positive_integer(aqua_value v, int32_t *out) {
    if (!aqua_is_number(v) || !(v.number >= 1 && v.number <= 2147483647.0) || v.number != (double)(int64_t)v.number) return false;
    *out = (int32_t)v.number;
    return true;
}

static void open_session(aqua_call *call, camera_module *m) {
    aqua_value id = aqua_arg(call, 0);
    if (id.type != AQUA_STRING) {
        fail(call, m, m->f_open, CAMERA_INVALID_ARGUMENT, m->english ? "裝置識別碼必須是字串。" : "裝置識別碼必須是字串；空字串表示預設裝置。");
        return;
    }
    int32_t width, height, fps;
    if (!positive_integer(aqua_arg(call, 1), &width) || !positive_integer(aqua_arg(call, 2), &height) || !positive_integer(aqua_arg(call, 3), &fps)) {
        fail(call, m, m->f_open, CAMERA_INVALID_ARGUMENT, "尺寸及影格率必須是正整數。");
        return;
    }
    if (width > 1920 || height > 1080) {
        fail(call, m, m->f_open, CAMERA_INVALID_ARGUMENT, m->english ? "相機尺寸、影格率或逾時無效。" : "寬度必須介於 1 至 1920，高度必須介於 1 至 1080。");
        return;
    }
    if (fps > 60) {
        fail(call, m, m->f_open, CAMERA_INVALID_ARGUMENT, m->english ? "相機尺寸、影格率或逾時無效。" : "影格率必須介於 1 至 60。");
        return;
    }
    camera_state *state = aqua_state(call);
    uint32_t open = 0;
    for (camera_session *s = state->sessions; s; s = s->next) if (session_open(s)) open++;
    if (open >= MAX_SESSIONS) {
        fail(call, m, m->f_open, CAMERA_UNAVAILABLE, m->english ? "最多四個相機工作階段。" : "最多只能同時開啟四個攝影機工作階段。");
        return;
    }
    if (!m->served) { fail(call, m, m->f_open, CAMERA_UNSUPPORTED, m->unsupported); return; }
    if (!camera_platform_supported()) { fail(call, m, m->f_open, CAMERA_UNSUPPORTED, "此環境未提供攝影機介面。"); return; }

    camera_session *s = calloc(1, sizeof *s);
    s->state = state;
    s->module = m;
    s->device_id = strdup(aqua_str_of(call, id).data);
    s->width = width; s->height = height; s->fps = fps; s->timeout_ms = TIMEOUT_MS;
    s->started = camera_now_ms();
    camera_mutex_init(&s->lock);
    s->open_pending = aqua_defer(call);
    atomic_store(&s->waiting, true);
    s->next = state->sessions;
    state->sessions = s;
    watch_deadline(s);
    /* The platform reports the first frame or a failure later, even when it knows at once. */
    camera_platform_open(s);
}

#define MODULE_FUNCTIONS(NAME) \
    static void NAME##_is_supported(aqua_call *call) { is_supported(call, &NAME); } \
    static void NAME##_list(aqua_call *call) { list_devices(call, &NAME); } \
    static void NAME##_open(aqua_call *call) { open_session(call, &NAME); }
MODULE_FUNCTIONS(webcam)
MODULE_FUNCTIONS(mobile)

/* ---- Session methods ---- */

static void session_is_open(aqua_call *call) { aqua_return_bool(call, session_open(self_session(call))); }

/* 釋放 / Dispose: closes the device; safe to repeat. */
static void session_dispose(aqua_call *call) { close_session(self_session(call)); }

/* 擷取影格 / Capture: the newest frame not yet delivered, waiting only when there is none. */
static void session_capture(aqua_call *call) {
    camera_session *s = self_session(call);
    const camera_module *m = s->module;
    if (!session_open(s)) {
        close_session(s);
        fail(call, m, m->f_capture, CAMERA_CLOSED, closed_message(m));
        return;
    }
    if (s->capture_pending) {
        fail(call, m, m->f_capture, CAMERA_UNAVAILABLE, "同一工作階段不能同時擷取多張影格。");
        return;
    }
    camera_platform_request(s);
    if (has_new_frame(s)) { aqua_return(call, take_frame(s)); return; }
    s->capture_pending = aqua_defer(call);
    s->capture_self = aqua_retain(aqua_call_runtime(call), aqua_self_value(call));
    atomic_store(&s->waiting, true);
    watch_deadline(s);
    /* A frame or failure that arrived before `waiting` was set is delivered now. */
    camera_lock(&s->lock);
    bool missed = s->failure != CAMERA_OK || (s->ready && s->sequence > s->delivered);
    camera_unlock(&s->lock);
    if (missed) notify(s);
}

/* ---- Runtime state ---- */

static void *state_create(aqua_runtime *rt) {
    camera_state *state = calloc(1, sizeof *state);
    state->rt = rt;
    return state;
}

/* Objects were finalized before this runs; what remains are sessions still opening and listings in progress. */
static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    camera_state *state = data;
    for (camera_session *s = state->sessions, *next; s; s = next) {
        next = s->next;
        if (!s->closed) { s->closed = true; camera_platform_close(s); }
        session_free(s);
    }
    for (camera_listing *l = state->listings, *next; l; l = next) {
        next = l->next;
        if (atomic_load(&l->done)) listing_free(l);
        else l->state = NULL; /* the platform finishes it later; camera_listed then only frees it */
    }
    free(state);
}

/* ---- Registration ---- */

static void define_module(aqua_library *library, camera_module *m, const char *names, aqua_function supported, aqua_function list,
                          aqua_function open, const char *session_name, const char *frame_name, const char *device_name_) {
    bool english = m->english;
    aqua_class *module = aqua_define_module(library, names);
    aqua_define_function(module, english ? "IsSupported|是否支援" : "是否支援", supported, "");
    aqua_define_function(module, english ? "Enumerate|列出裝置" : "列出裝置", list, "");
    aqua_define_function(module, english ? "Open|開啟" : "開啟", open, "vvvv"); /* kinds checked here, with the old messages */

    m->session_class = aqua_define_class(library, session_name, session_finalize);
    aqua_define_function(m->session_class, english ? "IsOpen|是否開啟" : "是否開啟", session_is_open, "");
    aqua_define_function(m->session_class, english ? "Capture|擷取影格" : "擷取影格", session_capture, "");
    aqua_define_function(m->session_class, english ? "Dispose|釋放" : "釋放", session_dispose, "");

    m->frame_class = aqua_define_class(library, frame_name, frame_finalize);
    aqua_define_property(m->frame_class, "寬度", frame_width);
    aqua_define_property(m->frame_class, "高度", frame_height);
    aqua_define_property(m->frame_class, "序號", frame_sequence);
    aqua_define_property(m->frame_class, "時間戳記", frame_timestamp);
    if (english) aqua_define_property(m->frame_class, "方向", frame_facing);
    aqua_define_property(m->frame_class, "影像", frame_image);
    aqua_define_function(m->frame_class, english ? "GetPixels|取得像素" : "取得像素", frame_pixels, "");

    m->device_class = aqua_define_class(library, device_name_, device_finalize);
    aqua_define_property(m->device_class, "識別碼", device_id);
    aqua_define_property(m->device_class, "名稱", device_name);
    if (english) aqua_define_property(m->device_class, "方向", device_facing);
}

AQUA_LIBRARY(camera) {
    aqua_library_state(library, state_create, state_destroy);
    /* 攝影機 keeps the old catalog's Traditional Chinese function names only; Camera has both languages. */
    define_module(library, &webcam, "攝影機|Webcam|網路攝影機", webcam_is_supported, webcam_list, webcam_open,
                  "攝影機.工作階段", "攝影機.影格", "攝影機.裝置");
    define_module(library, &mobile, "Camera|相機", mobile_is_supported, mobile_list, mobile_open,
                  "Camera.Session", "Camera.Frame", "Camera.Device");
}
