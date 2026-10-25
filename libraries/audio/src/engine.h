/* engine.h — the sound engine of one Audio device: decoding, tone generation and mixing (miniaudio, engine.c), and the
 * realtime output that plays what the mixer renders (miniaudio's device API in engine.c on native targets,
 * web-glue/audio through output_web.c on the web). The format is fixed: 48 kHz interleaved stereo float, clamped to
 * -1..1. Every function is called on the runtime's thread; the native output's pump thread only calls
 * audio_engine_render, which the engine serializes with the other calls.
 *
 * 音訊裝置的引擎：解碼、音調、混音與即時輸出。
 */
#ifndef AUDIO_ENGINE_H
#define AUDIO_ENGINE_H
#include <aqua/aqua.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define AUDIO_SAMPLE_RATE 48000
#define AUDIO_CHANNELS 2
#define AUDIO_MAX_SOUNDS 64                            /* sounds one device mixes */
#define AUDIO_MAX_FRAMES (AUDIO_SAMPLE_RATE * 180)     /* longest sound: 180 s */
#define AUDIO_MAX_TONE_FRAMES (AUDIO_SAMPLE_RATE * 60) /* longest tone: 60 s */
#define AUDIO_MAX_PCM_BYTES (128u * 1024 * 1024)       /* decoded samples one device holds */
#define AUDIO_MAX_ENCODED_BYTES (32u * 1024 * 1024)    /* largest encoded file */
#define AUDIO_MAX_RENDER_FRAMES 4096                   /* largest offline Render */

/* Results below zero of the functions returning a sound id (1..AUDIO_MAX_SOUNDS). */
typedef enum audio_status {
    AUDIO_INVALID = -2,     /* data or value out of range */
    AUDIO_FULL = -3,        /* AUDIO_MAX_SOUNDS sounds already exist */
    AUDIO_NO_MEMORY = -4,
    AUDIO_TOO_LARGE = -5,   /* longer than AUDIO_MAX_FRAMES or beyond AUDIO_MAX_PCM_BYTES */
    AUDIO_UNDECODABLE = -6, /* not WAV, FLAC or MP3, or damaged */
} audio_status;

typedef enum audio_command {
    AUDIO_PLAY, AUDIO_PAUSE, AUDIO_STOP, AUDIO_SET_VOLUME, AUDIO_SET_PAN, AUDIO_SET_PITCH, AUDIO_SET_LOOPING, AUDIO_SEEK
} audio_command;

typedef struct audio_engine audio_engine;

audio_engine *audio_engine_create(void);
void audio_engine_destroy(audio_engine *engine);
/* A sine tone of `frames` frames (amplitude 0.25, 5 ms fades), samples the engine takes ownership of (allocated with
   malloc, `frames` interleaved stereo frames), or encoded WAV/FLAC/MP3 bytes decoded and resampled to the engine's
   format. Each returns the new sound's id or an audio_status. */
int32_t audio_engine_tone(audio_engine *engine, float frequency, int32_t frames);
int32_t audio_engine_adopt(audio_engine *engine, float *samples, int32_t frames);
int32_t audio_engine_decode(audio_engine *engine, const uint8_t *bytes, size_t length);
/* Controls a sound: `value` is the volume (0..1), pan (-1..1), pitch (0.25..4), looping (0 or 1) or the frame to seek
   to. Returns 0 or an audio_status. */
int32_t audio_engine_command(audio_engine *engine, int32_t sound, audio_command command, double value);
bool audio_engine_playing(audio_engine *engine, int32_t sound);
uint64_t audio_engine_length(audio_engine *engine, int32_t sound);   /* frames */
uint64_t audio_engine_position(audio_engine *engine, int32_t sound); /* frames */
void audio_engine_release(audio_engine *engine, int32_t sound);
/* Mixes the next `frames` interleaved stereo frames of every playing sound into `output`, clamped to -1..1. */
void audio_engine_render(audio_engine *engine, float *output, uint32_t frames);

/* The realtime output of a device: plays what the engine renders, ahead of the hardware by a short queue, on a
   thread (native) or the browser's audio rendering thread (web) without ever blocking the runtime's thread. */
typedef struct audio_output audio_output;
audio_output *audio_output_open(audio_engine *engine, const char **error);
bool audio_output_start(audio_output *output);
void audio_output_stop(audio_output *output);
void audio_output_close(audio_output *output);
/* Whether the output stopped working after it started (a failed device or audio processor). */
bool audio_output_failed(audio_output *output);

#endif
