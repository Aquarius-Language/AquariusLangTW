/* output_web.c — realtime output on the web through web-glue/audio: an AudioContext whose AudioWorklet plays the
 * blocks this mixer renders on the main thread and reports each block it finished. Rendering is paced by those reports
 * (no timer keeps the application alive): the mixer stays QUEUE_FRAMES ahead of the browser's audio clock, enough to
 * ride out a busy main thread. The glue resumes the context at the first user gesture when the browser's autoplay
 * policy holds it suspended.
 *
 * 網頁即時輸出：主執行緒混音，AudioWorklet 播放並回報已播放的影格數。
 */
#include "engine.h"
#include <stdlib.h>

AQUA_WEB_IMPORT(audio_open) int32_t web_audio_open(int32_t sample_rate, void (*played)(void *data, int32_t frames), void *data);
AQUA_WEB_IMPORT(audio_start) void web_audio_start(int32_t context);
AQUA_WEB_IMPORT(audio_suspend) void web_audio_suspend(int32_t context);
AQUA_WEB_IMPORT(audio_push) void web_audio_push(int32_t context, const float *planar, int32_t frames);
AQUA_WEB_IMPORT(audio_close) void web_audio_close(int32_t context);

#define QUEUE_FRAMES 3072 /* 64 ms */
#define BLOCK_FRAMES 512

struct audio_output {
    audio_engine *engine;
    int32_t context;
    int32_t queued; /* frames pushed and not yet played */
    bool running, failed;
    float mixed[BLOCK_FRAMES * AUDIO_CHANNELS];
    float planar[BLOCK_FRAMES * AUDIO_CHANNELS]; /* left block, then right block */
};

static void fill(audio_output *o) {
    while (o->running && o->queued < QUEUE_FRAMES) {
        audio_engine_render(o->engine, o->mixed, BLOCK_FRAMES);
        for (int32_t i = 0; i < BLOCK_FRAMES; i++) {
            o->planar[i] = o->mixed[i * 2];
            o->planar[BLOCK_FRAMES + i] = o->mixed[i * 2 + 1];
        }
        web_audio_push(o->context, o->planar, BLOCK_FRAMES);
        o->queued += BLOCK_FRAMES;
    }
}

/* The glue reports each played block (or -1 when the audio processor failed). */
static void played(void *data, int32_t frames) {
    audio_output *o = data;
    if (frames < 0) { o->failed = true; o->running = false; return; }
    o->queued -= frames;
    if (o->queued < 0) o->queued = 0;
    fill(o);
}

audio_output *audio_output_open(audio_engine *engine, const char **error) {
    *error = "Audio output requires AudioContext and AudioWorklet in a secure context (HTTPS or localhost).";
    audio_output *o = calloc(1, sizeof *o);
    if (!o) return NULL;
    o->engine = engine;
    o->context = web_audio_open(AUDIO_SAMPLE_RATE, played, o);
    if (!o->context) { free(o); return NULL; }
    *error = NULL;
    return o;
}

bool audio_output_start(audio_output *o) {
    if (o->failed) return false;
    if (o->running) return true;
    o->running = true;
    web_audio_start(o->context);
    fill(o);
    return true;
}

void audio_output_stop(audio_output *o) {
    if (!o->running) return;
    o->running = false;
    web_audio_suspend(o->context);
}

void audio_output_close(audio_output *o) {
    if (!o) return;
    web_audio_close(o->context);
    free(o);
}

bool audio_output_failed(audio_output *o) { return o->failed; }
