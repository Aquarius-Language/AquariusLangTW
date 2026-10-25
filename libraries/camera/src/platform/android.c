/* Camera / 相機 on Android: NDK Camera2. Opening asks for android.permission.CAMERA, opens the camera with a YUV_420_888
 * image reader at the supported size closest to the request (aspect first, then area, at most 1920x1080) and a
 * repeating preview request at the closest frame-rate range. Each image is converted upright (sensor orientation and
 * display rotation, front cameras compensated, never mirrored) on the camera's callback thread. */
#include "../camera.h"
#if defined(AQUA_OS_ANDROID)
#include <aqua/android.h>
#include <camera/NdkCameraCaptureSession.h>
#include <camera/NdkCameraDevice.h>
#include <camera/NdkCameraManager.h>
#include <camera/NdkCameraMetadata.h>
#include <camera/NdkCaptureRequest.h>
#include <math.h>
#include <media/NdkImage.h>
#include <media/NdkImageReader.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct android_session {
    camera_session *session;
    camera_mutex lock;                 /* the image listener and device callbacks against closing */
    bool closing, closed, asking;      /* asking: the permission request is outstanding */
    int32_t sensor;                    /* ACAMERA_SENSOR_ORIENTATION */
    bool front;
    atomic_int display;                /* display rotation in degrees, refreshed on the runtime thread */
    ACameraManager *manager;
    ACameraDevice *device;
    AImageReader *reader;
    ACaptureSessionOutputContainer *outputs;
    ACaptureSessionOutput *output;
    ACameraOutputTarget *target;
    ACaptureRequest *request;
    ACameraCaptureSession *capture;
    ACameraDevice_StateCallbacks device_callbacks;
    ACameraCaptureSession_stateCallbacks session_callbacks;
    AImageReader_ImageListener listener;
} android_session;

static const char *const unavailable = "相機無法使用；請確認裝置或是否被其他程式占用。";

static camera_facing facing_of(ACameraMetadata *metadata) {
    ACameraMetadata_const_entry entry;
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_LENS_FACING, &entry) != ACAMERA_OK || !entry.count) return CAMERA_BACK;
    return entry.data.u8[0] == ACAMERA_LENS_FACING_FRONT ? CAMERA_FRONT : entry.data.u8[0] == ACAMERA_LENS_FACING_EXTERNAL ? CAMERA_EXTERNAL : CAMERA_BACK;
}

/* The display's rotation in degrees (Display.getRotation() * 90). */
static int32_t display_rotation(void) {
    JNIEnv *env = aqua_android_jni();
    jobject activity = aqua_android_activity();
    jint rotation = 0;
    jclass activity_class = (*env)->GetObjectClass(env, activity);
    jobject manager = (*env)->CallObjectMethod(env, activity, (*env)->GetMethodID(env, activity_class, "getWindowManager", "()Landroid/view/WindowManager;"));
    if (!(*env)->ExceptionCheck(env) && manager) {
        jclass manager_class = (*env)->FindClass(env, "android/view/WindowManager");
        jobject display = (*env)->CallObjectMethod(env, manager, (*env)->GetMethodID(env, manager_class, "getDefaultDisplay", "()Landroid/view/Display;"));
        if (!(*env)->ExceptionCheck(env) && display) {
            jclass display_class = (*env)->FindClass(env, "android/view/Display");
            rotation = (*env)->CallIntMethod(env, display, (*env)->GetMethodID(env, display_class, "getRotation", "()I"));
            (*env)->DeleteLocalRef(env, display_class);
            (*env)->DeleteLocalRef(env, display);
        }
        (*env)->DeleteLocalRef(env, manager_class);
        (*env)->DeleteLocalRef(env, manager);
    }
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); rotation = 0; }
    (*env)->DeleteLocalRef(env, activity_class);
    return (rotation & 3) * 90;
}

/* ---- Callbacks on the camera's threads ---- */

static void image_available(void *context, AImageReader *reader) {
    android_session *p = context;
    camera_lock(&p->lock);
    AImage *image = NULL;
    if (p->closing || AImageReader_acquireLatestImage(reader, &image) != AMEDIA_OK || !image) { camera_unlock(&p->lock); return; }
    int32_t width = 0, height = 0;
    int64_t timestamp = 0;
    AImage_getWidth(image, &width);
    AImage_getHeight(image, &height);
    AImage_getTimestamp(image, &timestamp);
    camera_source src;
    memset(&src, 0, sizeof src);
    src.format = CAMERA_YUV420;
    src.width = width; src.height = height;
    bool complete = width > 0 && height > 0;
    for (int i = 0; i < 3 && complete; i++) {
        uint8_t *data = NULL;
        int length = 0;
        int32_t row = 0, pixel = 0;
        complete = AImage_getPlaneData(image, i, &data, &length) == AMEDIA_OK && AImage_getPlaneRowStride(image, i, &row) == AMEDIA_OK &&
                   AImage_getPlanePixelStride(image, i, &pixel) == AMEDIA_OK && data && row > 0 && pixel > 0;
        int32_t w = i == 0 ? width : (width + 1) / 2, h = i == 0 ? height : (height + 1) / 2;
        /* Truncated planes are refused (CameraPixels.java's check). */
        if (complete && (int64_t)(h - 1) * row + (int64_t)(w - 1) * pixel >= length) complete = false;
        src.planes[i] = data; src.strides[i] = row; src.pixel_strides[i] = pixel;
    }
    if (complete && src.pixel_strides[0] == 1) {
        int32_t display = atomic_load(&p->display);
        src.rotation = (p->sensor + (p->front ? display : -display) + 360) % 360;
        camera_frame(p->session, &src, (double)timestamp / 1e6);
    }
    AImage_delete(image);
    camera_unlock(&p->lock);
}

static void report(android_session *p, camera_failure failure, const char *message) {
    camera_lock(&p->lock);
    if (!p->closing) camera_fail(p->session, failure, message);
    camera_unlock(&p->lock);
}
static void disconnected(void *context, ACameraDevice *device) { (void)device; report(context, CAMERA_UNAVAILABLE, "相機已中斷連線。"); }
static void device_error(void *context, ACameraDevice *device, int error) {
    (void)device;
    char message[96];
    snprintf(message, sizeof message, "相機發生錯誤（%d）。", error);
    report(context, CAMERA_UNAVAILABLE, message);
}
static void session_state(void *context, ACameraCaptureSession *session) { (void)context; (void)session; }

/* ---- Opening ---- */

static void release(android_session *p) {
    if (p->capture) { ACameraCaptureSession_stopRepeating(p->capture); ACameraCaptureSession_close(p->capture); }
    if (p->request) ACaptureRequest_free(p->request);
    if (p->target) ACameraOutputTarget_free(p->target);
    if (p->outputs && p->output) ACaptureSessionOutputContainer_remove(p->outputs, p->output);
    if (p->output) ACaptureSessionOutput_free(p->output);
    if (p->outputs) ACaptureSessionOutputContainer_free(p->outputs);
    if (p->device) ACameraDevice_close(p->device);     /* no device callbacks after it returns */
    if (p->reader) AImageReader_delete(p->reader);      /* no images after it returns */
    if (p->manager) ACameraManager_delete(p->manager);
    p->capture = NULL; p->request = NULL; p->target = NULL; p->output = NULL; p->outputs = NULL;
    p->device = NULL; p->reader = NULL; p->manager = NULL;
}

/* The camera to open: the given id, or the first back camera (else the first one) for "". */
static char *choose_camera(ACameraManager *manager, const char *wanted) {
    ACameraIdList *list = NULL;
    if (ACameraManager_getCameraIdList(manager, &list) != ACAMERA_OK || !list) return NULL;
    char *chosen = NULL;
    for (int i = 0; i < list->numCameras && !chosen; i++) {
        const char *id = list->cameraIds[i];
        if (*wanted) { if (!strcmp(id, wanted)) chosen = strdup(id); continue; }
        ACameraMetadata *metadata = NULL;
        bool back = ACameraManager_getCameraCharacteristics(manager, id, &metadata) == ACAMERA_OK && facing_of(metadata) == CAMERA_BACK;
        if (metadata) ACameraMetadata_free(metadata);
        if (back) chosen = strdup(id);
    }
    if (!chosen && !*wanted && list->numCameras > 0) chosen = strdup(list->cameraIds[0]);
    ACameraManager_deleteCameraIdList(list);
    return chosen;
}

static void start(android_session *p) {
    camera_session *s = p->session;
    p->manager = ACameraManager_create();
    char *id = p->manager ? choose_camera(p->manager, s->device_id) : NULL;
    if (!id) { camera_fail(s, CAMERA_NOT_FOUND, "找不到指定的相機；請重新列出裝置。"); return; }
    ACameraMetadata *metadata = NULL;
    if (ACameraManager_getCameraCharacteristics(p->manager, id, &metadata) != ACAMERA_OK) { free(id); camera_fail(s, CAMERA_UNAVAILABLE, unavailable); return; }
    s->facing = facing_of(metadata);
    p->front = s->facing == CAMERA_FRONT;
    ACameraMetadata_const_entry entry;
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_SENSOR_ORIENTATION, &entry) == ACAMERA_OK && entry.count) p->sensor = entry.data.i32[0];
    /* The output size: closest aspect ratio, then closest area (CameraBackend.java). */
    int32_t width = 0, height = 0;
    double best = INFINITY, aspect = (double)s->width / s->height, area = (double)s->width * s->height;
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS, &entry) == ACAMERA_OK) {
        for (uint32_t i = 0; i + 3 < entry.count; i += 4) {
            const int32_t *c = entry.data.i32 + i;
            if (c[0] != AIMAGE_FORMAT_YUV_420_888 || c[3] != ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS_OUTPUT || c[1] > 1920 || c[2] > 1080 || c[1] < 1 || c[2] < 1) continue;
            double cost = fabs((double)c[1] / c[2] - aspect) * 100000 + fabs((double)c[1] * c[2] - area);
            if (cost < best) { best = cost; width = c[1]; height = c[2]; }
        }
    }
    int32_t range[2] = {0, 0};
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES, &entry) == ACAMERA_OK) {
        for (uint32_t i = 0; i + 1 < entry.count; i += 2)
            if (!range[1] || abs(entry.data.i32[i + 1] - s->fps) < abs(range[1] - s->fps)) { range[0] = entry.data.i32[i]; range[1] = entry.data.i32[i + 1]; }
    }
    ACameraMetadata_free(metadata);
    if (!width) { free(id); camera_fail(s, CAMERA_UNAVAILABLE, "相機沒有可用的輸出尺寸。"); return; }

    ANativeWindow *window = NULL;
    p->listener.context = p;
    p->listener.onImageAvailable = image_available;
    p->device_callbacks.context = p;
    p->device_callbacks.onDisconnected = disconnected;
    p->device_callbacks.onError = device_error;
    p->session_callbacks.context = p;
    p->session_callbacks.onClosed = session_state;
    p->session_callbacks.onReady = session_state;
    p->session_callbacks.onActive = session_state;
    camera_status_t status = ACAMERA_OK;
    bool ok = AImageReader_new(width, height, AIMAGE_FORMAT_YUV_420_888, 2, &p->reader) == AMEDIA_OK &&
              AImageReader_setImageListener(p->reader, &p->listener) == AMEDIA_OK &&
              AImageReader_getWindow(p->reader, &window) == AMEDIA_OK;
    if (ok) ok = (status = ACameraManager_openCamera(p->manager, id, &p->device_callbacks, &p->device)) == ACAMERA_OK;
    if (ok) ok = ACaptureSessionOutputContainer_create(&p->outputs) == ACAMERA_OK && ACaptureSessionOutput_create(window, &p->output) == ACAMERA_OK &&
                 ACaptureSessionOutputContainer_add(p->outputs, p->output) == ACAMERA_OK && ACameraOutputTarget_create(window, &p->target) == ACAMERA_OK &&
                 ACameraDevice_createCaptureRequest(p->device, TEMPLATE_PREVIEW, &p->request) == ACAMERA_OK &&
                 ACaptureRequest_addTarget(p->request, p->target) == ACAMERA_OK;
    if (ok && range[1]) ACaptureRequest_setEntry_i32(p->request, ACAMERA_CONTROL_AE_TARGET_FPS_RANGE, 2, range);
    if (ok) ok = ACameraDevice_createCaptureSession(p->device, p->outputs, &p->session_callbacks, &p->capture) == ACAMERA_OK &&
                 ACameraCaptureSession_setRepeatingRequest(p->capture, NULL, 1, &p->request, NULL) == ACAMERA_OK;
    free(id);
    if (!ok) {
        bool denied = status == ACAMERA_ERROR_PERMISSION_DENIED;
        camera_fail(s, denied ? CAMERA_PERMISSION_DENIED : CAMERA_UNAVAILABLE, denied ? "相機權限遭拒。" : unavailable);
    }
}

static void permitted(bool granted, void *data) {
    android_session *p = data;
    p->asking = false;
    if (p->closed) { camera_mutex_destroy(&p->lock); free(p); return; } /* closed while asking */
    if (!granted) { camera_fail(p->session, CAMERA_PERMISSION_DENIED, "相機權限遭拒；請在系統設定中允許此應用程式使用相機。"); return; }
    start(p);
}

/* ---- camera.h ---- */

bool camera_platform_supported(void) { return true; }

/* Listing needs no permission and never opens a camera. */
void camera_platform_list(camera_listing *l) {
    ACameraManager *manager = ACameraManager_create();
    ACameraIdList *list = NULL;
    if (manager && ACameraManager_getCameraIdList(manager, &list) == ACAMERA_OK && list) {
        for (int i = 0; i < list->numCameras; i++) {
            const char *id = list->cameraIds[i];
            ACameraMetadata *metadata = NULL;
            camera_facing facing = CAMERA_BACK;
            if (ACameraManager_getCameraCharacteristics(manager, id, &metadata) == ACAMERA_OK) facing = facing_of(metadata);
            if (metadata) ACameraMetadata_free(metadata);
            char name[64];
            snprintf(name, sizeof name, "Camera %s", id);
            camera_listing_add(l, id, name, facing);
        }
        ACameraManager_deleteCameraIdList(list);
    } else {
        l->failure = CAMERA_UNAVAILABLE;
        snprintf(l->failure_message, sizeof l->failure_message, "%s", "無法列出相機。");
    }
    if (manager) ACameraManager_delete(manager);
    camera_listed(l);
}

void camera_platform_open(camera_session *s) {
    android_session *p = calloc(1, sizeof *p);
    p->session = s;
    camera_mutex_init(&p->lock);
    atomic_store(&p->display, display_rotation());
    s->platform = p;
    p->asking = true;
    aqua_android_request_permission("android.permission.CAMERA", permitted, p); /* may answer at once */
}

void camera_platform_request(camera_session *s) {
    android_session *p = s->platform;
    if (p) atomic_store(&p->display, display_rotation());
}

bool camera_platform_live(camera_session *s) { (void)s; return true; }

void camera_platform_close(camera_session *s) {
    android_session *p = s->platform;
    if (!p) return;
    s->platform = NULL;
    p->closed = true;
    if (p->asking) return; /* permitted() frees it when the answer comes */
    camera_lock(&p->lock);
    p->closing = true;
    camera_unlock(&p->lock);
    release(p);
    camera_mutex_destroy(&p->lock);
    free(p);
}
#endif
