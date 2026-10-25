/* 攝影機 on Linux: Video4Linux2 capture devices (/dev/video*). Each session has one thread that opens the node, picks
 * the uncompressed mode that best serves the request (YUYV, UYVY, NV12, NV21, YU12, YV12, RGB3, BGR3 or GREY),
 * streams memory-mapped buffers and hands them to camera_frame. Closing wakes the thread through a pipe. */
#include "../camera.h"
#if defined(AQUA_OS_LINUX)
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <unistd.h>

#define BUFFERS 4

typedef struct v4l2_session {
    pthread_t thread;
    bool started;
    int wake[2];                    /* closing writes a byte */
} v4l2_session;

static const char *const unavailable = "攝影機無法提供完整影格；請確認權限、裝置連線、影格率或是否被其他程式占用。";

static int control(int fd, unsigned long request, void *argument) {
    int r;
    do r = ioctl(fd, request, argument); while (r < 0 && errno == EINTR);
    return r;
}

/* ---- Devices ---- */

static int compare(const void *a, const void *b) { return strcmp(*(char *const *)a, *(char *const *)b); }

/* Capture nodes in ordinal order of their paths (a node the user may not open is listed too: opening explains). */
static uint32_t video_nodes(char ***out) {
    char **nodes = NULL;
    uint32_t count = 0;
    DIR *dev = opendir("/dev");
    struct dirent *entry;
    while (dev && (entry = readdir(dev))) {
        const char *n = entry->d_name;
        if (strncmp(n, "video", 5) != 0 || !n[5] || strspn(n + 5, "0123456789") != strlen(n + 5)) continue;
        char path[64];
        snprintf(path, sizeof path, "/dev/%s", n);
        int fd = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
        if (fd >= 0) {
            struct v4l2_capability capability;
            memset(&capability, 0, sizeof capability);
            bool capture = control(fd, VIDIOC_QUERYCAP, &capability) == 0 &&
                           ((capability.capabilities & V4L2_CAP_DEVICE_CAPS ? capability.device_caps : capability.capabilities) & V4L2_CAP_VIDEO_CAPTURE);
            close(fd);
            if (!capture) continue; /* metadata and output nodes of the same camera */
        }
        nodes = realloc(nodes, (count + 1) * sizeof *nodes);
        nodes[count++] = strdup(path);
    }
    if (dev) closedir(dev);
    if (count) qsort(nodes, count, sizeof *nodes, compare);
    *out = nodes;
    return count;
}

static void free_nodes(char **nodes, uint32_t count) {
    for (uint32_t i = 0; i < count; i++) free(nodes[i]);
    free(nodes);
}

static char *node_name(const char *path) {
    char file[128], text[256] = "";
    snprintf(file, sizeof file, "/sys/class/video4linux/%s/name", path + 5);
    FILE *f = fopen(file, "r");
    if (f) {
        if (!fgets(text, sizeof text, f)) text[0] = 0;
        fclose(f);
    }
    size_t n = strlen(text);
    while (n && strchr(" \t\r\n", text[n - 1])) text[--n] = 0;
    return strdup(n ? text : path + 5);
}

/* ---- Modes ---- */

static bool pixel_format(uint32_t fourcc, camera_format *format) {
    switch (fourcc) {
    case V4L2_PIX_FMT_YUYV: *format = CAMERA_YUYV; return true;
    case V4L2_PIX_FMT_UYVY: *format = CAMERA_UYVY; return true;
    case V4L2_PIX_FMT_NV12: case V4L2_PIX_FMT_NV21: case V4L2_PIX_FMT_YUV420: case V4L2_PIX_FMT_YVU420: *format = CAMERA_YUV420; return true;
    case V4L2_PIX_FMT_RGB24: *format = CAMERA_RGB24; return true;
    case V4L2_PIX_FMT_BGR24: *format = CAMERA_BGR24; return true;
    case V4L2_PIX_FMT_GREY: *format = CAMERA_GRAY; return true;
    }
    return false;
}

typedef struct mode { uint32_t fourcc, width, height; double fps, cost; } mode;

static double best_rate(int fd, uint32_t fourcc, uint32_t width, uint32_t height, int32_t wanted) {
    struct v4l2_frmivalenum interval;
    memset(&interval, 0, sizeof interval);
    interval.pixel_format = fourcc; interval.width = width; interval.height = height;
    double best = 0;
    for (interval.index = 0; control(fd, VIDIOC_ENUM_FRAMEINTERVALS, &interval) == 0; interval.index++) {
        if (interval.type != V4L2_FRMIVAL_TYPE_DISCRETE) {
            /* A range: the wanted rate when it lies inside, else the fastest. */
            double fastest = interval.stepwise.min.numerator ? (double)interval.stepwise.min.denominator / interval.stepwise.min.numerator : 0;
            return wanted <= fastest ? wanted : fastest;
        }
        if (!interval.discrete.numerator) continue;
        double fps = (double)interval.discrete.denominator / interval.discrete.numerator;
        if (best == 0 || (best < wanted ? fps > best : fps >= wanted && fps < best)) best = fps;
    }
    return best;
}

static void consider(camera_session *s, int fd, uint32_t fourcc, uint32_t width, uint32_t height, mode *best) {
    double fps = best_rate(fd, fourcc, width, height, s->fps);
    double cost = camera_mode_cost(s, (int32_t)width, (int32_t)height, fps, true);
    if (!best->fourcc || cost < best->cost) { best->fourcc = fourcc; best->width = width; best->height = height; best->fps = fps; best->cost = cost; }
}

static mode choose_mode(camera_session *s, int fd) {
    mode best = {0};
    struct v4l2_fmtdesc description;
    memset(&description, 0, sizeof description);
    description.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    for (description.index = 0; control(fd, VIDIOC_ENUM_FMT, &description) == 0; description.index++) {
        camera_format format;
        if (!pixel_format(description.pixelformat, &format)) continue;
        struct v4l2_frmsizeenum size;
        memset(&size, 0, sizeof size);
        size.pixel_format = description.pixelformat;
        bool listed = false;
        for (size.index = 0; control(fd, VIDIOC_ENUM_FRAMESIZES, &size) == 0; size.index++) {
            listed = true;
            if (size.type == V4L2_FRMSIZE_TYPE_DISCRETE) { consider(s, fd, size.pixel_format, size.discrete.width, size.discrete.height, &best); continue; }
            /* A range: the requested size clamped into it. */
            uint32_t w = (uint32_t)s->width, h = (uint32_t)s->height;
            w = w < size.stepwise.min_width ? size.stepwise.min_width : w > size.stepwise.max_width ? size.stepwise.max_width : w;
            h = h < size.stepwise.min_height ? size.stepwise.min_height : h > size.stepwise.max_height ? size.stepwise.max_height : h;
            consider(s, fd, size.pixel_format, w, h, &best);
            break;
        }
        if (!listed) consider(s, fd, description.pixelformat, (uint32_t)s->width, (uint32_t)s->height, &best);
    }
    return best;
}

/* ---- The session thread ---- */

typedef struct mapping { void *start; size_t length; } mapping;

static void *capture(void *data) {
    camera_session *s = data;
    v4l2_session *p = s->platform;
    mapping buffers[BUFFERS];
    uint32_t mapped = 0;
    bool streaming = false;
    char **nodes = NULL;
    uint32_t count = video_nodes(&nodes);
    const char *path = NULL;
    for (uint32_t i = 0; i < count && !path; i++) if (!*s->device_id || strcmp(nodes[i], s->device_id) == 0) path = nodes[i];
    int fd = -1;
    if (!path) { camera_fail(s, CAMERA_NOT_FOUND, "找不到指定的攝影機；請重新列出裝置。"); goto done; }
    fd = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) {
        int e = errno;
        camera_fail(s, e == EACCES || e == EPERM ? CAMERA_PERMISSION_DENIED : e == ENOENT || e == ENODEV ? CAMERA_NOT_FOUND : CAMERA_UNAVAILABLE,
                    e == EACCES || e == EPERM ? "沒有開啟攝影機裝置的權限；請將使用者加入 video 群組。" : unavailable);
        goto done;
    }
    struct v4l2_capability capability;
    memset(&capability, 0, sizeof capability);
    uint32_t caps = 0;
    if (control(fd, VIDIOC_QUERYCAP, &capability) == 0) caps = capability.capabilities & V4L2_CAP_DEVICE_CAPS ? capability.device_caps : capability.capabilities;
    if (!(caps & V4L2_CAP_VIDEO_CAPTURE) || !(caps & V4L2_CAP_STREAMING)) { camera_fail(s, CAMERA_UNAVAILABLE, "此裝置不是可串流的影像擷取裝置。"); goto done; }
    mode chosen = choose_mode(s, fd);
    if (!chosen.fourcc) { camera_fail(s, CAMERA_UNAVAILABLE, "此攝影機只提供壓縮影格（例如 MJPEG），目前不支援。"); goto done; }

    struct v4l2_format format;
    memset(&format, 0, sizeof format);
    format.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    format.fmt.pix.pixelformat = chosen.fourcc;
    format.fmt.pix.width = chosen.width;
    format.fmt.pix.height = chosen.height;
    format.fmt.pix.field = V4L2_FIELD_ANY;
    camera_format pixels;
    if (control(fd, VIDIOC_S_FMT, &format) != 0 || !pixel_format(format.fmt.pix.pixelformat, &pixels) || !format.fmt.pix.width || !format.fmt.pix.height) {
        camera_fail(s, CAMERA_UNAVAILABLE, unavailable);
        goto done;
    }
    if (chosen.fps > 0) {
        struct v4l2_streamparm rate;
        memset(&rate, 0, sizeof rate);
        rate.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        if (control(fd, VIDIOC_G_PARM, &rate) == 0 && (rate.parm.capture.capability & V4L2_CAP_TIMEPERFRAME)) {
            rate.parm.capture.timeperframe.numerator = 1000;
            rate.parm.capture.timeperframe.denominator = (uint32_t)(chosen.fps * 1000 + 0.5);
            control(fd, VIDIOC_S_PARM, &rate);
        }
    }
    struct v4l2_requestbuffers request;
    memset(&request, 0, sizeof request);
    request.count = BUFFERS; request.type = V4L2_BUF_TYPE_VIDEO_CAPTURE; request.memory = V4L2_MEMORY_MMAP;
    if (control(fd, VIDIOC_REQBUFS, &request) != 0 || request.count < 1) { camera_fail(s, CAMERA_UNAVAILABLE, unavailable); goto done; }
    for (uint32_t i = 0; i < request.count && i < BUFFERS; i++) {
        struct v4l2_buffer b;
        memset(&b, 0, sizeof b);
        b.type = V4L2_BUF_TYPE_VIDEO_CAPTURE; b.memory = V4L2_MEMORY_MMAP; b.index = i;
        if (control(fd, VIDIOC_QUERYBUF, &b) != 0) break;
        void *start = mmap(NULL, b.length, PROT_READ | PROT_WRITE, MAP_SHARED, fd, b.m.offset);
        if (start == MAP_FAILED) break;
        buffers[mapped].start = start; buffers[mapped].length = b.length;
        mapped++;
        if (control(fd, VIDIOC_QBUF, &b) != 0) break;
    }
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    if (!mapped || control(fd, VIDIOC_STREAMON, &type) != 0) {
        camera_fail(s, CAMERA_UNAVAILABLE, unavailable);
        goto done;
    }
    streaming = true;

    uint32_t width = format.fmt.pix.width, height = format.fmt.pix.height, fourcc = format.fmt.pix.pixelformat;
    uint32_t line = format.fmt.pix.bytesperline;
    if (!line) line = fourcc == V4L2_PIX_FMT_YUYV || fourcc == V4L2_PIX_FMT_UYVY ? width * 2 : fourcc == V4L2_PIX_FMT_RGB24 || fourcc == V4L2_PIX_FMT_BGR24 ? width * 3 : width;
    size_t needed = pixels == CAMERA_YUV420 ? (size_t)line * height + (size_t)line * ((height + 1) / 2) : (size_t)line * height;
    for (;;) {
        struct pollfd fds[2] = {{fd, POLLIN, 0}, {p->wake[0], POLLIN, 0}};
        if (poll(fds, 2, -1) < 0) { if (errno == EINTR) continue; camera_fail(s, CAMERA_UNAVAILABLE, unavailable); break; }
        if (fds[1].revents) break;
        if (fds[0].revents & (POLLERR | POLLHUP)) { camera_fail(s, CAMERA_UNAVAILABLE, unavailable); break; }
        struct v4l2_buffer b;
        memset(&b, 0, sizeof b);
        b.type = V4L2_BUF_TYPE_VIDEO_CAPTURE; b.memory = V4L2_MEMORY_MMAP;
        if (control(fd, VIDIOC_DQBUF, &b) != 0) {
            if (errno == EAGAIN) continue;
            camera_fail(s, CAMERA_UNAVAILABLE, unavailable); /* ENODEV: unplugged */
            break;
        }
        if (b.index < mapped && !(b.flags & V4L2_BUF_FLAG_ERROR) && b.bytesused >= needed) {
            const uint8_t *base = buffers[b.index].start;
            camera_source src;
            memset(&src, 0, sizeof src);
            src.format = pixels;
            src.width = (int32_t)width; src.height = (int32_t)height;
            src.planes[0] = base; src.strides[0] = (int32_t)line;
            if (pixels == CAMERA_YUV420) {
                const uint8_t *chroma = base + (size_t)line * height;
                if (fourcc == V4L2_PIX_FMT_NV12 || fourcc == V4L2_PIX_FMT_NV21) {
                    bool nv21 = fourcc == V4L2_PIX_FMT_NV21;
                    src.planes[1] = chroma + (nv21 ? 1 : 0); src.planes[2] = chroma + (nv21 ? 0 : 1);
                    src.strides[1] = src.strides[2] = (int32_t)line;
                    src.pixel_strides[1] = src.pixel_strides[2] = 2;
                } else {
                    size_t quarter = (size_t)(line / 2) * ((height + 1) / 2);
                    bool yv12 = fourcc == V4L2_PIX_FMT_YVU420;
                    src.planes[yv12 ? 2 : 1] = chroma; src.planes[yv12 ? 1 : 2] = chroma + quarter;
                    src.strides[1] = src.strides[2] = (int32_t)(line / 2);
                }
            }
            camera_frame(s, &src, camera_now_ms() - s->started);
        }
        if (control(fd, VIDIOC_QBUF, &b) != 0) { camera_fail(s, CAMERA_UNAVAILABLE, unavailable); break; }
    }
done:
    if (streaming) control(fd, VIDIOC_STREAMOFF, &type);
    for (uint32_t i = 0; i < mapped; i++) munmap(buffers[i].start, buffers[i].length);
    if (fd >= 0) close(fd);
    free_nodes(nodes, count);
    return NULL;
}

/* ---- camera.h ---- */

bool camera_platform_supported(void) { return true; }

void camera_platform_list(camera_listing *l) {
    char **nodes = NULL;
    uint32_t count = video_nodes(&nodes);
    for (uint32_t i = 0; i < count; i++) {
        char *name = node_name(nodes[i]);
        camera_listing_add(l, nodes[i], name, CAMERA_EXTERNAL);
        free(name);
    }
    free_nodes(nodes, count);
    camera_listed(l);
}

void camera_platform_open(camera_session *s) {
    v4l2_session *p = calloc(1, sizeof *p);
    s->platform = p;
    p->wake[0] = p->wake[1] = -1;
    if (pipe(p->wake) != 0 || !(p->started = pthread_create(&p->thread, NULL, capture, s) == 0)) camera_fail(s, CAMERA_UNAVAILABLE, unavailable);
}

void camera_platform_request(camera_session *s) { (void)s; }

bool camera_platform_live(camera_session *s) { (void)s; return true; }

void camera_platform_close(camera_session *s) {
    v4l2_session *p = s->platform;
    if (!p) return;
    if (p->wake[1] >= 0) { char byte = 1; ssize_t ignored = write(p->wake[1], &byte, 1); (void)ignored; }
    if (p->started) pthread_join(p->thread, NULL);
    for (int i = 0; i < 2; i++) if (p->wake[i] >= 0) close(p->wake[i]);
    free(p);
    s->platform = NULL;
}
#endif
