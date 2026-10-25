/* The camera library's internals. camera.c owns the two script modules (攝影機/Webcam on desktop and web, Camera/相機
 * on Android), argument validation, the four-session limit, the latest-frame mailbox, timeouts and the script objects.
 * convert.c turns any delivered pixel layout into the requested upright RGBA frame. The platform layer
 * (platform/<os>.c) only lists devices and moves frames: it calls camera_frame, camera_fail and camera_listed from any
 * thread, and camera.c delivers results on the runtime's thread through aqua_post. */
#ifndef CAMERA_H
#define CAMERA_H

#include <aqua/aqua.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Which module the platform layer serves; the other one reports itself unsupported. */
#if defined(AQUA_OS_WINDOWS) || defined(AQUA_OS_LINUX) || defined(AQUA_OS_WEB)
#define CAMERA_WEBCAM 1 /* 攝影機 / Webcam */
#elif defined(AQUA_OS_ANDROID)
#define CAMERA_MOBILE 1 /* Camera / 相機 */
#endif

#if defined(AQUA_OS_WEB)
typedef int camera_mutex; /* one thread */
static inline void camera_mutex_init(camera_mutex *m) { (void)m; }
static inline void camera_mutex_destroy(camera_mutex *m) { (void)m; }
static inline void camera_lock(camera_mutex *m) { (void)m; }
static inline void camera_unlock(camera_mutex *m) { (void)m; }
#else
#include <pthread.h>
typedef pthread_mutex_t camera_mutex;
static inline void camera_mutex_init(camera_mutex *m) { pthread_mutex_init(m, NULL); }
static inline void camera_mutex_destroy(camera_mutex *m) { pthread_mutex_destroy(m); }
static inline void camera_lock(camera_mutex *m) { pthread_mutex_lock(m); }
static inline void camera_unlock(camera_mutex *m) { pthread_mutex_unlock(m); }
#endif

/* The old runtime's failure kinds, in its order. */
typedef enum camera_failure {
    CAMERA_OK = 0,
    CAMERA_UNSUPPORTED, CAMERA_PERMISSION_DENIED, CAMERA_NOT_FOUND, CAMERA_UNAVAILABLE, CAMERA_TIMEOUT, CAMERA_CANCELLED,
    CAMERA_CLOSED, CAMERA_INVALID_ARGUMENT,
} camera_failure;

typedef enum camera_facing { CAMERA_BACK, CAMERA_FRONT, CAMERA_EXTERNAL } camera_facing;

typedef struct camera_device { char *id, *name; camera_facing facing; } camera_device;

/* ---- Pixels ---- */

typedef enum camera_format {
    CAMERA_RGBA,    /* R G B A bytes */
    CAMERA_BGRA,    /* B G R X bytes (Media Foundation RGB32) */
    CAMERA_RGB24,   /* R G B */
    CAMERA_BGR24,   /* B G R (Media Foundation RGB24) */
    CAMERA_YUYV,    /* Y0 U Y1 V (YUY2) */
    CAMERA_UYVY,    /* U Y0 V Y1 */
    CAMERA_YUV420,  /* three planes, chroma subsampled 2x2: I420, YV12, NV12 and NV21 through strides */
    CAMERA_GRAY,    /* Y only */
} camera_format;

/* One frame as a platform delivers it. Rows run top to bottom; a negative stride walks a bottom-up image. */
typedef struct camera_source {
    camera_format format;
    int32_t width, height;
    const uint8_t *planes[3];       /* packed: planes[0]; YUV420: Y, U, V (NV12: V = U + 1, both pixel stride 2) */
    int32_t strides[3];             /* bytes between rows */
    int32_t pixel_strides[3];       /* YUV420 chroma: bytes between samples (1 planar, 2 interleaved); 0 means 1 */
    int32_t rotation;               /* clockwise degrees that make the image upright: 0, 90, 180 or 270 */
} camera_source;

/* Scratch buffers and filter weights of one producer, kept between frames. */
typedef struct camera_axis { int32_t source, target, taps; int32_t *index; int16_t *weight; } camera_axis;
typedef struct camera_converter {
    uint8_t *upright; size_t upright_capacity;   /* the source as RGBA, rotated upright */
    uint8_t *middle; size_t middle_capacity;     /* after the horizontal pass */
    int32_t *sums; size_t sums_capacity;
    camera_axis x, y;
} camera_converter;

/* Draws `source` upright into `output` (width x height opaque RGBA): scaled to fit with black bars (letterbox) or to
   fill the whole frame. False when the source is malformed. */
bool camera_convert(camera_converter *c, const camera_source *source, int32_t width, int32_t height, bool letterbox, uint8_t *output);
void camera_converter_free(camera_converter *c);

/* ---- Sessions ---- */

typedef struct camera_module camera_module;
typedef struct camera_state camera_state;

typedef struct camera_session {
    camera_state *state;
    const camera_module *module;
    char *device_id;                 /* as the script gave it ("" = the default device) */
    int32_t width, height, fps, timeout_ms;
    camera_facing facing;            /* Camera: set by the platform when it opens the device */
    double started;                  /* camera_now_ms() at open */

    /* The mailbox, shared with the platform's threads under `lock`. */
    camera_mutex lock;
    uint8_t *latest;                 /* the newest complete frame */
    int32_t latest_width, latest_height;
    uint64_t sequence;               /* frames stored so far */
    double timestamp;
    bool ready;                      /* the device delivered its first frame */
    camera_failure failure;          /* the first failure, which ends the session */
    char failure_message[256];
    atomic_bool waiting;             /* the runtime waits for this session: post the next event */

    /* The producer's side (one producing thread at a time). */
    camera_converter converter;
    uint8_t *spare;                  /* the buffer the producer draws into before swapping it with `latest` */

    /* The runtime thread's side. */
    uint64_t delivered;              /* sequence of the last frame handed to the script */
    aqua_pending *open_pending, *capture_pending;
    aqua_ref capture_self;           /* the session object, held while a capture waits */
    double deadline;                 /* aqua_now_ms() by which the pending operation must settle, 0 = none */
    bool closed;                     /* the platform stopped the device */

    void *platform;
    struct camera_session *next;
} camera_session;

/* A device listing in progress (asynchronous on the web). */
typedef struct camera_listing {
    camera_state *state;             /* NULL once the runtime ended: the late result is only freed */
    const camera_module *module;
    aqua_pending *pending;
    camera_device *devices;
    uint32_t count;
    camera_failure failure;
    char failure_message[256];
    atomic_bool done;
    struct camera_listing *next;
} camera_listing;

/* How poorly a device mode serves a session (lower is better): too few frames per second cost most, then enlarging,
   aspect mismatch, oversized modes and modes that need a decoder (raw = delivered in a camera_format). */
double camera_mode_cost(const camera_session *s, int32_t width, int32_t height, double fps, bool raw);

/* Called by platform layers, from any thread. */
void camera_frame(camera_session *s, const camera_source *source, double timestamp_ms);
void camera_fail(camera_session *s, camera_failure failure, const char *message);
void camera_listed(camera_listing *listing);
/* Adds a device to a listing (copies the strings). */
void camera_listing_add(camera_listing *listing, const char *id, const char *name, camera_facing facing);
/* Monotonic milliseconds, usable from any thread. */
double camera_now_ms(void);

/* ---- The platform layer (platform/<os>.c) of the served module ---- */

/* Whether the target can capture at all (devices may still be absent). */
bool camera_platform_supported(void);
/* Lists devices: fills the listing and calls camera_listed, now or later. Never requests permission. */
void camera_platform_list(camera_listing *listing);
/* Starts the device of a new session (the session is validated and counted). Asynchronous: the first frame makes it
   ready; failures go to camera_fail. Runtime thread. */
void camera_platform_open(camera_session *s);
/* Called by every capture before it looks for a frame (runtime thread). Platforms that deliver frames on request (the
   web) store a newer one now through camera_frame or arrange for the next one; others refresh what frames depend on. */
void camera_platform_request(camera_session *s);
/* Whether the device still runs (the web: a live video track). */
bool camera_platform_live(camera_session *s);
/* Stops the device; after it returns the platform calls nothing for this session. Runtime thread; idempotent. */
void camera_platform_close(camera_session *s);

#endif
