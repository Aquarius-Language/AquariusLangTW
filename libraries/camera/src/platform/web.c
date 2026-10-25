/* 攝影機 on the web: getUserMedia through web-glue/camera. The glue owns the MediaStream and its <video>; for each
 * frame it copies the decoded pixels at the video's own size into memory given here, and convert.c makes the
 * requested frame. Listing, opening and waiting for the next decoded frame are asynchronous glue calls whose callbacks
 * land below. Browser failures arrive as DOMException names, mapped here to the old failure kinds and messages. */
#include "../camera.h"
#if defined(AQUA_OS_WEB)
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef void (*web_result)(void *data, int32_t status, char *text, uint32_t length); /* status 0 ok, else text = name */
AQUA_WEB_IMPORT(camera_supported) int32_t web_camera_supported(void);
AQUA_WEB_IMPORT(camera_list) void web_camera_list(web_result done, void *data);
AQUA_WEB_IMPORT(camera_open) int32_t web_camera_open(const char *id, uint32_t length, int32_t width, int32_t height, int32_t fps, web_result done, void *data);
AQUA_WEB_IMPORT(camera_frame) int32_t web_camera_frame(int32_t handle, int32_t *size, double *time);
AQUA_WEB_IMPORT(camera_read) int32_t web_camera_read(int32_t handle, uint8_t *pixels, int32_t width, int32_t height);
AQUA_WEB_IMPORT(camera_wait) void web_camera_wait(int32_t handle, void (*done)(void *data), void *data);
AQUA_WEB_IMPORT(camera_live) int32_t web_camera_live(int32_t handle);
AQUA_WEB_IMPORT(camera_close) void web_camera_close(int32_t handle);

typedef struct web_session {
    int32_t handle;
    bool opened, waiting;
    double shown;                   /* the decoded frame read last (its media time), -1 before the first */
    uint8_t *pixels; size_t capacity;
} web_session;

/* The old browser adapter's failures: DOMException names to kinds, with its messages. */
static camera_failure failure_of(const char *name, const char **message) {
    if (!strcmp(name, "NotAllowedError") || !strcmp(name, "SecurityError")) {
        *message = "攝影機權限遭拒；請在瀏覽器或系統設定中允許使用攝影機。";
        return CAMERA_PERMISSION_DENIED;
    }
    if (!strcmp(name, "NotFoundError")) { *message = "找不到指定的攝影機；請重新列出裝置。"; return CAMERA_NOT_FOUND; }
    if (!strcmp(name, "AbortError")) { *message = "攝影機操作已取消。"; return CAMERA_CANCELLED; }
    *message = "攝影機無法使用；請確認裝置連線、影格率或是否被其他程式占用。";
    return CAMERA_UNAVAILABLE;
}

/* Reads the decoded frame when it is newer than the last one read; true when one was stored. */
static bool read_frame(camera_session *s) {
    web_session *p = s->platform;
    int32_t size[2];
    double time;
    if (!web_camera_frame(p->handle, size, &time) || time == p->shown || size[0] < 1 || size[1] < 1) return false;
    size_t bytes = (size_t)size[0] * (size_t)size[1] * 4;
    if (bytes > p->capacity) {
        uint8_t *grown = realloc(p->pixels, bytes);
        if (!grown) return false;
        p->pixels = grown;
        p->capacity = bytes;
    }
    if (!web_camera_read(p->handle, p->pixels, size[0], size[1])) return false;
    p->shown = time;
    camera_source src;
    memset(&src, 0, sizeof src);
    src.format = CAMERA_RGBA;
    src.width = size[0]; src.height = size[1];
    src.planes[0] = p->pixels; src.strides[0] = size[0] * 4;
    camera_frame(s, &src, camera_now_ms() - s->started);
    return true;
}

static void frame_decoded(void *data) {
    camera_session *s = data;
    web_session *p = s->platform;
    p->waiting = false;
    camera_platform_request(s);
}

static void opened(void *data, int32_t status, char *text, uint32_t length) {
    (void)length;
    camera_session *s = data;
    web_session *p = s->platform;
    if (status) {
        const char *message;
        camera_failure failure = failure_of(text ? text : "", &message);
        camera_fail(s, failure, message);
    } else {
        p->opened = true;
        camera_platform_request(s); /* the first frame makes the session ready */
    }
    free(text);
}

static void listed(void *data, int32_t status, char *text, uint32_t length) {
    camera_listing *l = data;
    if (status) {
        const char *message;
        l->failure = failure_of(text ? text : "", &message);
        snprintf(l->failure_message, sizeof l->failure_message, "%s", message);
    } else {
        /* id NUL name NUL ...; the browser hides labels until permission is granted. */
        for (uint32_t at = 0, number = 1; at < length; number++) {
            const char *id = text + at;
            at += (uint32_t)strlen(id) + 1;
            if (at >= length + 1) break;
            const char *name = text + at;
            at += (uint32_t)strlen(name) + 1;
            char fallback[32];
            snprintf(fallback, sizeof fallback, "攝影機 %u", number);
            camera_listing_add(l, id, *name ? name : fallback, CAMERA_EXTERNAL);
        }
    }
    free(text);
    camera_listed(l);
}

/* ---- camera.h ---- */

bool camera_platform_supported(void) { return web_camera_supported() != 0; }

void camera_platform_list(camera_listing *l) { web_camera_list(listed, l); }

void camera_platform_open(camera_session *s) {
    web_session *p = calloc(1, sizeof *p);
    p->shown = -1;
    s->platform = p;
    p->handle = web_camera_open(s->device_id, (uint32_t)strlen(s->device_id), s->width, s->height, s->fps, opened, s);
}

void camera_platform_request(camera_session *s) {
    web_session *p = s->platform;
    if (!p || !p->opened || read_frame(s) || p->waiting) return;
    p->waiting = true;
    web_camera_wait(p->handle, frame_decoded, s);
}

bool camera_platform_live(camera_session *s) {
    web_session *p = s->platform;
    return p && web_camera_live(p->handle);
}

void camera_platform_close(camera_session *s) {
    web_session *p = s->platform;
    if (!p) return;
    web_camera_close(p->handle); /* the glue calls nothing back for this camera afterwards */
    free(p->pixels);
    free(p);
    s->platform = NULL;
}
#endif
