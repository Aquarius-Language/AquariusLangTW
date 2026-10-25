/* pose.h — portable MediaPipe BlazePose pre- and post-processing (pose.c), one state per tracker. The tracker
 * (mediapipe.c) moves model tensors in and out of these buffers and runs the two ONNX sessions in between.
 *
 * 姿態追蹤的前處理與後處理：信箱縮放、錨點解碼、旋轉裁切、熱圖細化、座標還原與濾波。 */
#ifndef ONNX_POSE_H
#define ONNX_POSE_H
#include <stdint.h>

#define POSE_MAX_PIXELS (1920 * 1080)
#define POSE_ANCHORS 2254

typedef struct pose {
    const uint8_t *rgba;                                 /* the frame being analyzed: straight-alpha RGBA8 */
    float detector[224 * 224 * 3];                       /* person detector input, NCHW in [-1, 1] */
    float boxes[POSE_ANCHORS * 12], scores[POSE_ANCHORS]; /* detector outputs */
    float input[256 * 256 * 3];                          /* landmark model input, NHWC in [0, 1] */
    float landmarks[195], heatmap[64 * 64 * 39], flag[1]; /* landmark model outputs */
    float result[166];                                   /* presence, then 33 x (x, y, z, visibility, presence) */
    float previous[165], velocity[99];
    int width, height, tracked, filtered;
    float cx, cy, radius, ux, uy, now, last, last_detection;
} pose;

void pose_reset(pose *p);
/* Starts a frame: -2 invalid size or time, -1 skip (reacquiring too soon after a loss), 1 run the detector first,
   0 track with the previous region. */
int pose_begin(pose *p, int width, int height, float time);
void pose_detector_input(pose *p);
/* Decodes the detector outputs into a region; 0 when no person was found. */
int pose_detect(pose *p);
void pose_landmark_input(pose *p);
/* Projects, refines and filters the landmark outputs into `result`; 0 when the pose was lost. */
int pose_finish(pose *p);
#endif
