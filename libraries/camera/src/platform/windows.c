/* 攝影機 on Windows: Media Foundation video capture devices. Each session has one thread that activates the device,
 * picks the native mode that best serves the request, reads samples synchronously and hands their pixels (NV12, YUY2,
 * UYVY, I420, YV12, RGB32 or RGB24; compressed modes are decoded by Media Foundation) to camera_frame. Closing asks the
 * thread to stop after its current read; only a device that stopped delivering has its media source shut down from
 * the closing thread (a source reader can wait forever on a read issued while its source shuts down). */
#include "../camera.h"
#if defined(AQUA_OS_WINDOWS)
#define COBJMACROS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct mf_session {
    pthread_t thread;
    bool started;
    atomic_bool stop;
    atomic_bool finished;           /* the thread released the device and is about to return */
    camera_mutex lock;              /* guards source while the thread publishes it */
    IMFMediaSource *source;
} mf_session;

static const char *const unavailable = "攝影機無法提供完整影格；請確認權限、裝置連線、影格率或是否被其他程式占用。";
static const char *const denied = "攝影機權限遭拒；請在 Windows 設定的「隱私權與安全性 > 相機」允許桌面應用程式使用攝影機。";

static char *narrow(const wchar_t *text) {
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    char *utf8 = malloc(n > 0 ? (size_t)n : 1);
    if (n <= 0 || !WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, n, NULL, NULL)) utf8[0] = 0;
    return utf8;
}

static bool media_foundation(void) {
    static int started; /* 0 unknown, 1 running, -1 unavailable; Media Foundation stays started for the process */
    if (!started) started = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_LITE)) ? 1 : -1;
    return started > 0;
}

/* COM for the calling thread; true when the caller must uninitialize it. */
static bool com_enter(void) {
    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    return hr == S_OK || hr == S_FALSE;
}

static HRESULT capture_devices(IMFActivate ***devices, UINT32 *count) {
    IMFAttributes *attributes = NULL;
    HRESULT hr = MFCreateAttributes(&attributes, 1);
    if (SUCCEEDED(hr)) hr = IMFAttributes_SetGUID(attributes, &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (SUCCEEDED(hr)) hr = MFEnumDeviceSources(attributes, devices, count);
    if (attributes) IMFAttributes_Release(attributes);
    return hr;
}

static char *device_string(IMFActivate *device, const GUID *key) {
    wchar_t *text = NULL;
    UINT32 length = 0;
    if (FAILED(IMFActivate_GetAllocatedString(device, key, &text, &length))) return strdup("");
    char *utf8 = narrow(text);
    CoTaskMemFree(text);
    return utf8;
}

/* ---- Modes and pixels ---- */

static bool pixel_format(const GUID *subtype, camera_format *format) {
    if (IsEqualGUID(subtype, &MFVideoFormat_NV12) || IsEqualGUID(subtype, &MFVideoFormat_I420) || IsEqualGUID(subtype, &MFVideoFormat_IYUV) ||
        IsEqualGUID(subtype, &MFVideoFormat_YV12)) *format = CAMERA_YUV420;
    else if (IsEqualGUID(subtype, &MFVideoFormat_YUY2)) *format = CAMERA_YUYV;
    else if (IsEqualGUID(subtype, &MFVideoFormat_UYVY)) *format = CAMERA_UYVY;
    else if (IsEqualGUID(subtype, &MFVideoFormat_RGB32) || IsEqualGUID(subtype, &MFVideoFormat_ARGB32)) *format = CAMERA_BGRA;
    else if (IsEqualGUID(subtype, &MFVideoFormat_RGB24)) *format = CAMERA_BGR24;
    else return false;
    return true;
}

static void frame_size(IMFMediaType *type, UINT32 *width, UINT32 *height) {
    UINT64 packed = 0;
    IMFMediaType_GetUINT64(type, &MF_MT_FRAME_SIZE, &packed);
    *width = (UINT32)(packed >> 32);
    *height = (UINT32)packed;
}

static double frame_rate(IMFMediaType *type) {
    UINT64 packed = 0;
    if (FAILED(IMFMediaType_GetUINT64(type, &MF_MT_FRAME_RATE, &packed)) || !(UINT32)packed) return 0;
    return (double)(UINT32)(packed >> 32) / (double)(UINT32)packed;
}

/* The native mode serving the session best, made current; then a decoded output type when that mode is compressed. */
static HRESULT choose_mode(camera_session *s, IMFSourceReader *reader) {
    const DWORD stream = (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM;
    IMFMediaType *best = NULL;
    double best_cost = 0;
    for (DWORD i = 0;; i++) {
        IMFMediaType *type = NULL;
        if (FAILED(IMFSourceReader_GetNativeMediaType(reader, stream, i, &type))) break;
        GUID subtype;
        camera_format format;
        UINT32 width, height;
        frame_size(type, &width, &height);
        bool raw = SUCCEEDED(IMFMediaType_GetGUID(type, &MF_MT_SUBTYPE, &subtype)) && pixel_format(&subtype, &format);
        double cost = camera_mode_cost(s, (int32_t)width, (int32_t)height, frame_rate(type), raw);
        if (!best || cost < best_cost) {
            if (best) IMFMediaType_Release(best);
            best = type;
            best_cost = cost;
        } else IMFMediaType_Release(type);
    }
    if (!best) return MF_E_INVALIDMEDIATYPE;
    HRESULT hr = IMFSourceReader_SetCurrentMediaType(reader, stream, NULL, best);
    GUID subtype;
    camera_format format;
    bool raw = SUCCEEDED(IMFMediaType_GetGUID(best, &MF_MT_SUBTYPE, &subtype)) && pixel_format(&subtype, &format);
    IMFMediaType_Release(best);
    if (SUCCEEDED(hr) && !raw) {
        /* A compressed mode (MJPG, H.264): ask Media Foundation's decoder for a layout convert.c reads. */
        static const GUID *const outputs[] = {&MFVideoFormat_NV12, &MFVideoFormat_YUY2, &MFVideoFormat_RGB32};
        hr = MF_E_INVALIDMEDIATYPE;
        for (size_t i = 0; i < sizeof outputs / sizeof *outputs && FAILED(hr); i++) {
            IMFMediaType *output = NULL;
            hr = MFCreateMediaType(&output);
            if (SUCCEEDED(hr)) hr = IMFMediaType_SetGUID(output, &MF_MT_MAJOR_TYPE, &MFMediaType_Video);
            if (SUCCEEDED(hr)) hr = IMFMediaType_SetGUID(output, &MF_MT_SUBTYPE, outputs[i]);
            if (SUCCEEDED(hr)) hr = IMFSourceReader_SetCurrentMediaType(reader, stream, NULL, output);
            if (output) IMFMediaType_Release(output);
        }
    }
    return hr;
}

/* The current output layout: pixel format, buffer size, the visible size (decoders pad to their block size) and the
   stride of the first row (negative when bottom-up). */
typedef struct layout { GUID subtype; camera_format format; UINT32 width, height, visible_width, visible_height; LONG stride; } layout;

static HRESULT current_layout(IMFSourceReader *reader, layout *out) {
    IMFMediaType *type = NULL;
    HRESULT hr = IMFSourceReader_GetCurrentMediaType(reader, (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &type);
    if (FAILED(hr)) return hr;
    hr = IMFMediaType_GetGUID(type, &MF_MT_SUBTYPE, &out->subtype);
    if (SUCCEEDED(hr) && !pixel_format(&out->subtype, &out->format)) hr = MF_E_INVALIDMEDIATYPE;
    frame_size(type, &out->width, &out->height);
    MFVideoArea area;
    bool cropped = SUCCEEDED(IMFMediaType_GetBlob(type, &MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8 *)&area, sizeof area, NULL)) &&
                   area.Area.cx > 0 && area.Area.cy > 0 && (UINT32)area.Area.cx <= out->width && (UINT32)area.Area.cy <= out->height;
    out->visible_width = cropped ? (UINT32)area.Area.cx : out->width;
    out->visible_height = cropped ? (UINT32)area.Area.cy : out->height;
    UINT32 stride = 0;
    if (SUCCEEDED(hr)) {
        if (SUCCEEDED(IMFMediaType_GetUINT32(type, &MF_MT_DEFAULT_STRIDE, &stride))) out->stride = (LONG)(INT32)stride;
        else hr = MFGetStrideForBitmapInfoHeader(out->subtype.Data1, out->width, &out->stride);
    }
    IMFMediaType_Release(type);
    if (SUCCEEDED(hr) && (!out->width || !out->height)) hr = MF_E_INVALIDMEDIATYPE;
    return hr;
}

/* Describes a locked frame (scan0 = the top row, pitch = bytes to the next row down) for convert.c. */
static bool describe(const layout *l, BYTE *scan0, LONG pitch, size_t available, camera_source *src) {
    memset(src, 0, sizeof *src);
    src->format = l->format;
    src->width = (int32_t)l->visible_width;
    src->height = (int32_t)l->visible_height;
    size_t rows = l->height, magnitude = (size_t)(pitch < 0 ? -pitch : pitch);
    src->planes[0] = scan0;
    src->strides[0] = pitch;
    if (l->format == CAMERA_YUV420) {
        if (pitch <= 0) return false;
        const BYTE *chroma = scan0 + magnitude * rows;
        if (IsEqualGUID(&l->subtype, &MFVideoFormat_NV12)) {
            if (available && available < magnitude * rows * 3 / 2) return false;
            src->planes[1] = chroma; src->planes[2] = chroma + 1;
            src->strides[1] = src->strides[2] = pitch;
            src->pixel_strides[1] = src->pixel_strides[2] = 2;
        } else {
            size_t half = magnitude / 2, quarter = half * ((rows + 1) / 2);
            if (available && available < magnitude * rows + quarter * 2) return false;
            bool yv12 = IsEqualGUID(&l->subtype, &MFVideoFormat_YV12);
            src->planes[yv12 ? 2 : 1] = chroma;
            src->planes[yv12 ? 1 : 2] = chroma + quarter;
            src->strides[1] = src->strides[2] = (int32_t)half;
        }
    } else if (available && available < magnitude * rows) return false;
    return true;
}

/* Reads samples until stopped or failed. The reader's mode is chosen here. */
static void read_frames(camera_session *s, IMFSourceReader *reader) {
    mf_session *p = s->platform;
    layout l;
    HRESULT hr = IMFSourceReader_SetStreamSelection(reader, (DWORD)MF_SOURCE_READER_ALL_STREAMS, FALSE);
    if (SUCCEEDED(hr)) hr = IMFSourceReader_SetStreamSelection(reader, (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);
    if (SUCCEEDED(hr)) hr = choose_mode(s, reader);
    if (SUCCEEDED(hr)) hr = current_layout(reader, &l);
    if (FAILED(hr)) { camera_fail(s, CAMERA_UNAVAILABLE, unavailable); return; }
    while (!atomic_load(&p->stop)) {
        DWORD stream = 0, flags = 0;
        LONGLONG time = 0;
        IMFSample *sample = NULL;
        hr = IMFSourceReader_ReadSample(reader, (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &stream, &flags, &time, &sample);
        if (atomic_load(&p->stop)) { if (sample) IMFSample_Release(sample); break; }
        if (FAILED(hr) || (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM))) {
            if (sample) IMFSample_Release(sample);
            camera_fail(s, hr == E_ACCESSDENIED ? CAMERA_PERMISSION_DENIED : CAMERA_UNAVAILABLE, hr == E_ACCESSDENIED ? denied : unavailable);
            break;
        }
        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) && FAILED(current_layout(reader, &l))) {
            if (sample) IMFSample_Release(sample);
            camera_fail(s, CAMERA_UNAVAILABLE, unavailable);
            break;
        }
        if (!sample) continue; /* a stream tick: no frame */
        IMFMediaBuffer *buffer = NULL;
        if (SUCCEEDED(IMFSample_ConvertToContiguousBuffer(sample, &buffer))) {
            camera_source src;
            IMF2DBuffer *planar = NULL;
            BYTE *scan0 = NULL;
            LONG pitch = 0;
            if (SUCCEEDED(IMFMediaBuffer_QueryInterface(buffer, &IID_IMF2DBuffer, (void **)&planar)) &&
                SUCCEEDED(IMF2DBuffer_Lock2D(planar, &scan0, &pitch))) {
                if (describe(&l, scan0, pitch, 0, &src)) camera_frame(s, &src, camera_now_ms() - s->started);
                IMF2DBuffer_Unlock2D(planar);
            } else {
                BYTE *data = NULL;
                DWORD capacity = 0, length = 0;
                if (SUCCEEDED(IMFMediaBuffer_Lock(buffer, &data, &capacity, &length))) {
                    LONG stride = l.stride;
                    BYTE *top = stride < 0 ? data + (size_t)(-stride) * (l.height - 1) : data;
                    if (describe(&l, top, stride, length, &src)) camera_frame(s, &src, camera_now_ms() - s->started);
                    IMFMediaBuffer_Unlock(buffer);
                }
            }
            if (planar) IMF2DBuffer_Release(planar);
            IMFMediaBuffer_Release(buffer);
        }
        IMFSample_Release(sample);
    }
}

/* ---- The session thread ---- */

static void *capture(void *data) {
    camera_session *s = data;
    mf_session *p = s->platform;
    bool com = com_enter();
    IMFActivate **devices = NULL, *chosen = NULL;
    UINT32 count = 0;
    IMFMediaSource *source = NULL;
    IMFSourceReader *reader = NULL;
    IMFAttributes *attributes = NULL;
    HRESULT hr = capture_devices(&devices, &count);
    for (UINT32 i = 0; SUCCEEDED(hr) && i < count && !chosen; i++) {
        char *link = device_string(devices[i], &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
        if (!*s->device_id || strcmp(link, s->device_id) == 0) chosen = devices[i];
        free(link);
    }
    if (!chosen) { camera_fail(s, CAMERA_NOT_FOUND, "找不到指定的攝影機；請重新列出裝置。"); goto done; }
    hr = IMFActivate_ActivateObject(chosen, &IID_IMFMediaSource, (void **)&source);
    if (FAILED(hr)) { camera_fail(s, hr == E_ACCESSDENIED ? CAMERA_PERMISSION_DENIED : CAMERA_UNAVAILABLE, hr == E_ACCESSDENIED ? denied : unavailable); goto done; }
    camera_lock(&p->lock);
    bool stopping = atomic_load(&p->stop);
    if (!stopping) { p->source = source; IMFMediaSource_AddRef(source); }
    camera_unlock(&p->lock);
    if (stopping) goto done;
    /* Video processing lets a compressed device mode decode to the layouts convert.c reads. */
    hr = MFCreateAttributes(&attributes, 1);
    if (SUCCEEDED(hr)) hr = IMFAttributes_SetUINT32(attributes, &MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromMediaSource(source, attributes, &reader);
    if (FAILED(hr)) { camera_fail(s, hr == E_ACCESSDENIED ? CAMERA_PERMISSION_DENIED : CAMERA_UNAVAILABLE, hr == E_ACCESSDENIED ? denied : unavailable); goto done; }
    read_frames(s, reader);
done:
    if (reader) IMFSourceReader_Release(reader);
    if (attributes) IMFAttributes_Release(attributes);
    if (source) { IMFMediaSource_Shutdown(source); IMFMediaSource_Release(source); }
    for (UINT32 i = 0; i < count; i++) IMFActivate_Release(devices[i]);
    CoTaskMemFree(devices);
    if (com) CoUninitialize();
    atomic_store(&p->finished, true);
    return NULL;
}

/* ---- camera.h ---- */

bool camera_platform_supported(void) { return media_foundation(); }

void camera_platform_list(camera_listing *l) {
    bool com = com_enter();
    IMFActivate **devices = NULL;
    UINT32 count = 0;
    if (FAILED(capture_devices(&devices, &count))) {
        l->failure = CAMERA_UNAVAILABLE;
        snprintf(l->failure_message, sizeof l->failure_message, "%s", "無法列出攝影機裝置。");
    }
    for (UINT32 i = 0; i < count; i++) {
        char *id = device_string(devices[i], &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
        char *name = device_string(devices[i], &MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
        camera_listing_add(l, id, *name ? name : id, CAMERA_EXTERNAL);
        free(id);
        free(name);
        IMFActivate_Release(devices[i]);
    }
    CoTaskMemFree(devices);
    if (com) CoUninitialize();
    camera_listed(l);
}

void camera_platform_open(camera_session *s) {
    mf_session *p = calloc(1, sizeof *p);
    camera_mutex_init(&p->lock);
    s->platform = p;
    p->started = pthread_create(&p->thread, NULL, capture, s) == 0;
    if (!p->started) camera_fail(s, CAMERA_UNAVAILABLE, unavailable);
}

void camera_platform_request(camera_session *s) { (void)s; }

bool camera_platform_live(camera_session *s) { (void)s; return true; }

void camera_platform_close(camera_session *s) {
    mf_session *p = s->platform;
    if (!p) return;
    atomic_store(&p->stop, true);
    if (p->started) {
        /* A streaming device delivers within a frame interval; give a stalled one a second before ending its read. */
        for (int waited = 0; waited < 1000 && !atomic_load(&p->finished); waited += 5) Sleep(5);
        if (!atomic_load(&p->finished)) {
            camera_lock(&p->lock);
            if (p->source) IMFMediaSource_Shutdown(p->source);
            camera_unlock(&p->lock);
        }
        pthread_join(p->thread, NULL);
    }
    if (p->source) IMFMediaSource_Release(p->source);
    camera_mutex_destroy(&p->lock);
    free(p);
    s->platform = NULL;
}
#endif
