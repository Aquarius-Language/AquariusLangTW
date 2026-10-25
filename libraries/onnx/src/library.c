/* The onnx library's entry point: the ONNX|推論引擎|ONNXRuntime and MediaPipe|姿態追蹤 modules and their per-runtime
 * state (open sessions and pose trackers, released when the runtime ends). */
#include "onnx.h"
#include <stdlib.h>

static void *create_state(aqua_runtime *rt) {
    (void)rt;
    return calloc(1, sizeof(onnx_state));
}

static void destroy_state(aqua_runtime *rt, void *data) {
    (void)rt;
    mediapipe_close_trackers(data);
    onnx_close_sessions(data);
    free(data);
}

AQUA_LIBRARY(onnx) {
    onnx_module(library);
    mediapipe_module(library);
    aqua_library_state(library, create_state, destroy_state);
}
