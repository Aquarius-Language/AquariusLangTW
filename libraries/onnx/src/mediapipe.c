/* The MediaPipe|姿態追蹤 module: a single-person BlazePose tracker over two ONNX sessions (person detector and pose
 * landmarks). pose.c does all image and tensor math; this file only moves tensors between pose.c's buffers and the
 * sessions, like the old PoseTracker. Both sessions run on one provider chain: the detector resolves the selection
 * and the landmark model opens with exactly the detector's providers, so the stages never mix devices.
 *
 * 姿態追蹤模組：人物偵測與姿態關鍵點兩個 ONNX 工作階段，前後處理在 pose.c。 */
#include "onnx.h"
#include "pose.h"
#include <aqua/image.h>
#include <float.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define PREFIX "姿態追蹤"
#define MAX_TRACKERS 4

static aqua_class *tracker_class, *result_class;

struct pose_tracker {
    onnx_state *owner;
    pose *pose;                        /* NULL once disposed */
    onnx_session *detector, *landmark;
    const char *boxes, *scores, *points, *confidence, *heatmap; /* output names, owned by the sessions */
    onnx_provider backend;
    uint8_t *straight;                 /* straight-alpha copy of premultiplied frames */
    size_t straight_size;
    pose_tracker *next;
};

typedef struct pose_result {
    bool has_pose, detection_ran;
    float confidence;
    float landmarks[165];
    uint32_t landmark_count;
    double total_ms, inference_ms;
} pose_result;

/* ---- Trackers ------------------------------------------------------------------------------------------------- */
static void close_tracker(pose_tracker *t) {
    if (t->owner) {
        for (pose_tracker **p = &t->owner->trackers; *p; p = &(*p)->next) {
            if (*p == t) { *p = t->next; t->owner->tracker_count--; break; }
        }
        t->owner = NULL;
    }
    backend_close(t->landmark);
    backend_close(t->detector);
    t->landmark = t->detector = NULL;
    free(t->pose);
    t->pose = NULL;
    free(t->straight);
    t->straight = NULL;
}

static void tracker_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    close_tracker(data);
    free(data);
}

void mediapipe_close_trackers(onnx_state *state) {
    while (state->trackers) close_tracker(state->trackers);
}

/* The unique float32 output with `count` elements. */
static const char *output_named(const onnx_session *session, int64_t count) {
    uint32_t n;
    const onnx_value_info *outputs = backend_outputs(session, &n);
    const char *found = NULL;
    uint32_t matches = 0;
    for (uint32_t i = 0; i < n; i++) {
        int64_t product = 1;
        for (uint32_t d = 0; d < outputs[i].rank; d++) product *= outputs[i].shape[d];
        if (outputs[i].type == ONNX_FLOAT32 && product == count) { found = outputs[i].name; matches++; }
    }
    return matches == 1 ? found : NULL;
}

static bool single_input(const onnx_session *session, const int64_t shape[4]) {
    uint32_t n;
    const onnx_value_info *inputs = backend_inputs(session, &n);
    if (n != 1 || inputs[0].type != ONNX_FLOAT32 || inputs[0].rank != 4) return false;
    for (int d = 0; d < 4; d++) if (inputs[0].shape[d] != shape[d]) return false;
    return true;
}

static void create_tracker(aqua_call *call) {
    static const char *const function = "建立姿態追蹤器";
    aqua_runtime *rt = aqua_call_runtime(call);
    onnx_state *state = aqua_state(call);
    if (state->tracker_count >= MAX_TRACKERS) { aqua_fail(call, PREFIX ".%s：最多只能同時建立四個姿態追蹤器。", function); return; }
    onnx_selection selection = onnx_auto;
    onnx_error error = {0};
    if (aqua_has_arg(call, 2) && !onnx_selection_of(rt, aqua_arg(call, 2), &selection, &error)) {
        aqua_fail(call, PREFIX ".%s：%s", function, error.message);
        return;
    }
    onnx_model detector_model = {0}, landmark_model = {0};
    bool read = onnx_model_read(call, aqua_arg(call, 0), &detector_model, &error) &&
        onnx_model_read(call, aqua_arg(call, 1), &landmark_model, &error);
    pose_tracker *t = calloc(1, sizeof *t);
    bool ok = read && t;
    if (ok) ok = (t->detector = onnx_session_open(&detector_model, &selection, &error)) != NULL;
    if (ok) {
        const onnx_plan *plan = backend_plan(t->detector);
        onnx_selection same = {ONNX_EXPLICIT, plan->count, {0}};
        for (uint32_t i = 0; i < plan->count; i++) same.providers[i] = plan->devices[i]->provider;
        ok = (t->landmark = onnx_session_open(&landmark_model, &same, &error)) != NULL;
    }
    onnx_model_free(&detector_model);
    onnx_model_free(&landmark_model);
    if (ok) {
        static const int64_t detector_shape[4] = {1, 3, 224, 224}, landmark_shape[4] = {1, 256, 256, 3};
        if (!single_input(t->detector, detector_shape) || !single_input(t->landmark, landmark_shape))
            ok = onnx_fail(&error, ONNX_INVALID_ARGUMENT, "必須使用相容的 MediaPipe 人物偵測與姿態模型。");
    }
    if (ok) {
        t->boxes = output_named(t->detector, POSE_ANCHORS * 12);
        t->scores = output_named(t->detector, POSE_ANCHORS);
        t->points = output_named(t->landmark, 195);
        t->confidence = output_named(t->landmark, 1);
        t->heatmap = output_named(t->landmark, 64 * 64 * 39);
        if (!t->boxes || !t->scores || !t->points || !t->confidence || !t->heatmap)
            ok = onnx_fail(&error, ONNX_INVALID_ARGUMENT, "姿態模型的輸出介面不相容。");
    }
    if (ok && !(t->pose = calloc(1, sizeof *t->pose))) ok = onnx_fail(&error, ONNX_ENGINE, "記憶體不足。");
    if (!ok) {
        if (t) { close_tracker(t); free(t); }
        aqua_fail(call, PREFIX ".%s：%s", function, error.message);
        return;
    }
    pose_reset(t->pose);
    t->backend = backend_plan(t->detector)->devices[0]->provider;
    t->owner = state;
    t->next = state->trackers;
    state->trackers = t;
    state->tracker_count++;
    aqua_return(call, aqua_object_new(rt, tracker_class, t));
}

static pose_tracker *tracker_self(aqua_call *call) { return aqua_self(call); }
static void tracker_backend(aqua_call *call) { aqua_return_cstring(call, onnx_provider_id(tracker_self(call)->backend)); }

/* Runs a session on its single float32 input (a pose.c buffer, bound in place) and copies each requested output into
   the matching pose.c buffer. */
static bool run(onnx_session *session, float *input, const char *const *names, float *const *targets, const size_t *counts,
                uint32_t count, onnx_error *error) {
    uint32_t n;
    const onnx_value_info *info = backend_inputs(session, &n);
    onnx_tensor tensor;
    if (!onnx_tensor_init(&tensor, ONNX_FLOAT32, info[0].shape, info[0].rank, input, error)) return false;
    const onnx_tensor *inputs[1] = {&tensor};
    onnx_tensor outputs[3];
    if (!backend_run(session, (const char *const *)&info[0].name, inputs, 1, names, outputs, count, error)) return false;
    bool ok = true;
    for (uint32_t i = 0; i < count; i++) {
        if (outputs[i].type != ONNX_FLOAT32 || outputs[i].count != counts[i]) ok = onnx_fail(error, ONNX_ENGINE, "姿態模型的輸出介面不相容。");
        else memcpy(targets[i], outputs[i].data, counts[i] * sizeof(float));
        onnx_tensor_free(&outputs[i]);
    }
    return ok;
}

static void result_finalize(aqua_runtime *rt, void *data) { (void)rt; free(data); }

static void analyze(aqua_call *call) {
    static const char *const function = "分析";
    aqua_runtime *rt = aqua_call_runtime(call);
    pose_tracker *t = tracker_self(call);
    aqua_image *image = aqua_image_of(rt, aqua_arg(call, 0));
    if (!image) { aqua_fail(call, PREFIX ".%s：必須傳入影像物件。", function); return; }
    aqua_value time_value = aqua_arg(call, 1);
    if (!aqua_is_number(time_value)) { aqua_fail(call, PREFIX ".%s：時間必須是毫秒數。", function); return; }
    if (!t->pose) { aqua_fail(call, PREFIX ".%s：姿態追蹤器已關閉。", function); return; }
    double time = time_value.number;
    if (!isfinite(time) || time < 0 || time > FLT_MAX || image->width > 1920 || image->height > 1920 ||
        (int64_t)image->width * image->height > POSE_MAX_PIXELS) {
        aqua_fail(call, PREFIX ".%s：影像上限為 1920 × 1080（支援直式），時間必須是非負有限毫秒數。", function);
        return;
    }
    pose *p = t->pose;
    double start = aqua_now_ms(rt), inference = 0;
    int detect = pose_begin(p, image->width, image->height, (float)time);
    if (detect == -2) { aqua_fail(call, PREFIX ".%s：姿態影像或時間無效。", function); return; }
    pose_result *result = calloc(1, sizeof *result);
    onnx_error error = {0};
    bool ok = result != NULL, found = true;
    if (ok && detect >= 0) {
        size_t bytes = (size_t)image->width * (size_t)image->height * 4;
        p->rgba = image->rgba;
        if (image->alpha == AQUA_ALPHA_PREMULTIPLIED) {
            if (t->straight_size < bytes) {
                free(t->straight);
                t->straight = malloc(bytes);
                t->straight_size = t->straight ? bytes : 0;
            }
            if (!t->straight) ok = onnx_fail(&error, ONNX_ENGINE, "記憶體不足。");
            for (size_t i = 0; ok && i < bytes; i += 4) {
                uint8_t a = image->rgba[i + 3];
                for (int c = 0; c < 3; c++) {
                    double v = a ? round(image->rgba[i + c] * 255.0 / a) : 0;
                    t->straight[i + c] = (uint8_t)(v > 255 ? 255 : v);
                }
                t->straight[i + 3] = a;
            }
            p->rgba = t->straight;
        }
        if (ok && detect == 1) {
            pose_detector_input(p);
            double timer = aqua_now_ms(rt);
            const char *names[2] = {t->boxes, t->scores};
            float *targets[2] = {p->boxes, p->scores};
            size_t counts[2] = {POSE_ANCHORS * 12, POSE_ANCHORS};
            ok = run(t->detector, p->detector, names, targets, counts, 2, &error);
            inference += aqua_now_ms(rt) - timer;
            found = ok && pose_detect(p);
        }
        if (ok && found) {
            pose_landmark_input(p);
            double timer = aqua_now_ms(rt);
            const char *names[3] = {t->points, t->confidence, t->heatmap};
            float *targets[3] = {p->landmarks, p->flag, p->heatmap};
            size_t counts[3] = {195, 1, 64 * 64 * 39};
            ok = run(t->landmark, p->input, names, targets, counts, 3, &error);
            inference += aqua_now_ms(rt) - timer;
            if (ok) pose_finish(p);
        }
        p->rgba = NULL;
    }
    if (!ok) {
        free(result);
        aqua_fail(call, PREFIX ".%s：%s", function, error.message);
        return;
    }
    if (found) {
        result->confidence = p->result[0];
        result->has_pose = p->result[0] >= .5f;
        if (result->has_pose) {
            memcpy(result->landmarks, p->result + 1, sizeof result->landmarks);
            result->landmark_count = 165;
        }
    }
    result->total_ms = aqua_now_ms(rt) - start;
    result->inference_ms = inference;
    result->detection_ran = detect == 1;
    aqua_return(call, aqua_object_new(rt, result_class, result));
}

static void reset(aqua_call *call) {
    pose_tracker *t = tracker_self(call);
    if (!t->pose) { aqua_fail(call, PREFIX ".重設：姿態追蹤器已關閉。"); return; }
    pose_reset(t->pose);
}

static void dispose(aqua_call *call) { close_tracker(tracker_self(call)); }

static const pose_result *result_self(aqua_call *call) { return aqua_self(call); }
static void result_has_pose(aqua_call *call) { aqua_return_bool(call, result_self(call)->has_pose); }
static void result_confidence(aqua_call *call) { aqua_return_double(call, result_self(call)->confidence); }
static void result_landmarks(aqua_call *call) {
    const pose_result *r = result_self(call);
    double values[165];
    for (uint32_t i = 0; i < r->landmark_count; i++) values[i] = r->landmarks[i];
    aqua_return(call, aqua_array_from_f64(aqua_call_runtime(call), values, r->landmark_count, AQUA_DOUBLE));
}
static void result_total(aqua_call *call) { aqua_return_double(call, result_self(call)->total_ms); }
static void result_inference(aqua_call *call) { aqua_return_double(call, result_self(call)->inference_ms); }
static void result_detection(aqua_call *call) { aqua_return_bool(call, result_self(call)->detection_ran); }

void mediapipe_module(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "MediaPipe|姿態追蹤");
    aqua_define_function(module, "CreatePoseTracker|建立姿態追蹤器", create_tracker, "vv|v");

    tracker_class = aqua_define_class(library, "MediaPipe.PoseTracker", tracker_finalize);
    aqua_define_property(tracker_class, "Backend|後端", tracker_backend);
    aqua_define_function(tracker_class, "Analyze|分析", analyze, "vv");
    aqua_define_function(tracker_class, "Reset|重設", reset, "");
    aqua_define_function(tracker_class, "Dispose|釋放", dispose, "");

    result_class = aqua_define_class(library, "MediaPipe.PoseResult", result_finalize);
    aqua_define_property(result_class, "HasPose|有人物", result_has_pose);
    aqua_define_property(result_class, "Confidence|信心", result_confidence);
    aqua_define_property(result_class, "Landmarks|關鍵點", result_landmarks);
    aqua_define_property(result_class, "TotalMilliseconds|總耗時", result_total);
    aqua_define_property(result_class, "InferenceMilliseconds|模型耗時", result_inference);
    aqua_define_property(result_class, "DetectionRan|執行人物偵測", result_detection);
}
