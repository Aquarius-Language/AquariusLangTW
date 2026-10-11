/* A PCM sink only. Decoding and mixing stay in AquariusCore/audio/audio.wasm. */
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MA_NO_GENERATION
#define MA_NO_NULL
#define MINIAUDIO_IMPLEMENTATION
#include "vendor/miniaudio/miniaudio.h"
#if defined(_WIN32)
#define API __declspec(dllexport)
#else
#define API __attribute__((visibility("default")))
#endif
typedef struct { ma_device device; ma_pcm_rb queue; } aqua_output;
static void callback(ma_device* device, void* output, const void* input, ma_uint32 frames) {
    aqua_output* sink = device->pUserData;
    float* target = output;
    (void)input;
    memset(output, 0, frames * 8);
    while (frames) {
        ma_uint32 count = frames; void* source = NULL;
        if (ma_pcm_rb_acquire_read(&sink->queue, &count, &source) != MA_SUCCESS || !count) break;
        memcpy(target, source, count * 8);
        ma_pcm_rb_commit_read(&sink->queue, count);
        target += count * 2; frames -= count;
    }
}
API void* aqua_audio_open(void) {
    aqua_output* sink = calloc(1, sizeof(*sink));
    if (!sink) return NULL;
    if (ma_pcm_rb_init(ma_format_f32, 2, 4096, NULL, NULL, &sink->queue) != MA_SUCCESS) { free(sink); return NULL; }
    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32; config.playback.channels = 2; config.sampleRate = 48000;
    config.dataCallback = callback; config.pUserData = sink; config.periodSizeInFrames = 256;
    if (ma_device_init(NULL, &config, &sink->device) != MA_SUCCESS) { ma_pcm_rb_uninit(&sink->queue); free(sink); return NULL; }
    return sink;
}
API int aqua_audio_start(aqua_output* sink) { return ma_device_start(&sink->device); }
API int aqua_audio_stop(aqua_output* sink) { return ma_device_stop(&sink->device); }
API int aqua_audio_available(aqua_output* sink) { return (int)ma_pcm_rb_available_write(&sink->queue); }
API int aqua_audio_write(aqua_output* sink, const float* samples, int frames) {
    int written = 0;
    while (frames > 0) {
        ma_uint32 count = frames; void* target = NULL;
        if (ma_pcm_rb_acquire_write(&sink->queue, &count, &target) != MA_SUCCESS || !count) break;
        memcpy(target, samples, count * 8); ma_pcm_rb_commit_write(&sink->queue, count);
        written += count; samples += count * 2; frames -= count;
    }
    return written;
}
API void aqua_audio_close(aqua_output* sink) { if (sink) { ma_device_uninit(&sink->device); ma_pcm_rb_uninit(&sink->queue); free(sink); } }
