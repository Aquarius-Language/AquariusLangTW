/* engine.c — decoding, tone generation and mixing of the Audio library with miniaudio, and on native targets the
 * realtime output through miniaudio's device API (WASAPI, PulseAudio/ALSA, AAudio/OpenSL ES, ...). The same mixer runs
 * on every target; the web plays its blocks through output_web.c instead of a device.
 *
 * miniaudio is compiled into this file only, with every symbol private (MA_API static), so another library vendoring
 * it never clashes. Its SIMD paths are disabled and floating-point contraction is off (CMakeLists.txt), so every
 * target computes bit-identical PCM from the same input and operations, as the single audio.wasm of the previous
 * implementation did.
 *
 * 以 miniaudio 解碼、產生音調及混音；原生平台並以其裝置介面輸出。
 */
#include "engine.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define MA_API static
/* The two upstream definitions MA_API does not cover keep their linkage, under this library's prefix. */
#define ma_atomic_global_lock audio_ma_atomic_global_lock
#define ma_android_sdk_version audio_ma_android_sdk_version
#define MA_NO_ENCODING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_SSE2
#define MA_NO_AVX2
#define MA_NO_NEON
#if defined(AQUA_OS_WEB)
#define MA_NO_DEVICE_IO
#define MA_NO_THREADING
#else
#define MA_NO_NULL /* without hardware, Start fails instead of playing into a silent null device */
#endif
#define MINIAUDIO_IMPLEMENTATION
#include "../third_party/miniaudio/miniaudio.h"

#if !defined(AQUA_OS_WEB)
#include <pthread.h>
#include <stdatomic.h>
#include <time.h>
#endif

#define FRAME_BYTES (AUDIO_CHANNELS * sizeof(float))
#define FADE_FRAMES 240 /* 5 ms fades at both ends of a tone avoid clicks */

typedef struct voice {
    ma_audio_buffer buffer;
    ma_sound sound;
    float *samples; /* NULL for a free voice */
    ma_uint64 frames;
} voice;

struct audio_engine {
    ma_engine engine;
    voice voices[AUDIO_MAX_SOUNDS];
    size_t pcm_bytes;
#if !defined(AQUA_OS_WEB)
    pthread_mutex_t lock; /* the runtime's thread and the output's pump thread take turns */
#endif
};

static void engine_lock(audio_engine *e) {
#if defined(AQUA_OS_WEB)
    (void)e;
#else
    pthread_mutex_lock(&e->lock);
#endif
}
static void engine_unlock(audio_engine *e) {
#if defined(AQUA_OS_WEB)
    (void)e;
#else
    pthread_mutex_unlock(&e->lock);
#endif
}

audio_engine *audio_engine_create(void) {
    audio_engine *e = calloc(1, sizeof *e);
    if (!e) return NULL;
    ma_engine_config config = ma_engine_config_init();
    config.noDevice = MA_TRUE; /* the engine only mixes; the output (or Render) pulls its frames */
    config.channels = AUDIO_CHANNELS;
    config.sampleRate = AUDIO_SAMPLE_RATE;
    config.noAutoStart = MA_TRUE;
    if (ma_engine_init(&config, &e->engine) != MA_SUCCESS) { free(e); return NULL; }
#if !defined(AQUA_OS_WEB)
    pthread_mutex_init(&e->lock, NULL);
#endif
    return e;
}

static voice *voice_of(audio_engine *e, int32_t sound) {
    return sound > 0 && sound <= AUDIO_MAX_SOUNDS && e->voices[sound - 1].samples ? &e->voices[sound - 1] : NULL;
}

static void voice_free(audio_engine *e, voice *v) {
    ma_sound_uninit(&v->sound);
    ma_audio_buffer_uninit(&v->buffer);
    e->pcm_bytes -= (size_t)v->frames * FRAME_BYTES;
    free(v->samples);
    memset(v, 0, sizeof *v);
}

void audio_engine_destroy(audio_engine *e) {
    if (!e) return;
    for (int32_t i = 0; i < AUDIO_MAX_SOUNDS; i++) if (e->voices[i].samples) voice_free(e, &e->voices[i]);
    ma_engine_uninit(&e->engine);
#if !defined(AQUA_OS_WEB)
    pthread_mutex_destroy(&e->lock);
#endif
    free(e);
}

/* Makes `samples` (owned from here on) a sound of the engine. */
static int32_t install(audio_engine *e, float *samples, ma_uint64 frames) {
    engine_lock(e);
    int32_t result = AUDIO_FULL;
    if (!frames || frames > AUDIO_MAX_FRAMES || frames * FRAME_BYTES > AUDIO_MAX_PCM_BYTES - e->pcm_bytes) result = AUDIO_TOO_LARGE;
    else for (int32_t i = 0; i < AUDIO_MAX_SOUNDS; i++) {
        voice *v = &e->voices[i];
        if (v->samples) continue;
        ma_audio_buffer_config config = ma_audio_buffer_config_init(ma_format_f32, AUDIO_CHANNELS, frames, samples, NULL);
        config.sampleRate = AUDIO_SAMPLE_RATE;
        if (ma_audio_buffer_init(&config, &v->buffer) != MA_SUCCESS) { result = AUDIO_NO_MEMORY; break; }
        if (ma_sound_init_from_data_source(&e->engine, &v->buffer, MA_SOUND_FLAG_NO_SPATIALIZATION, NULL, &v->sound) != MA_SUCCESS) {
            ma_audio_buffer_uninit(&v->buffer);
            result = AUDIO_NO_MEMORY;
            break;
        }
        v->samples = samples;
        v->frames = frames;
        e->pcm_bytes += (size_t)frames * FRAME_BYTES;
        engine_unlock(e);
        return i + 1;
    }
    engine_unlock(e);
    free(samples);
    return result;
}

int32_t audio_engine_tone(audio_engine *e, float frequency, int32_t frames) {
    if (!isfinite(frequency) || frequency < 20 || frequency > 20000 || frames <= 0 || frames > AUDIO_MAX_TONE_FRAMES) return AUDIO_INVALID;
    float *samples = malloc((size_t)frames * FRAME_BYTES);
    if (!samples) return AUDIO_NO_MEMORY;
    ma_waveform waveform;
    ma_waveform_config config = ma_waveform_config_init(ma_format_f32, AUDIO_CHANNELS, AUDIO_SAMPLE_RATE, ma_waveform_type_sine, 0.25, frequency);
    if (ma_waveform_init(&config, &waveform) != MA_SUCCESS) { free(samples); return AUDIO_INVALID; }
    ma_waveform_read_pcm_frames(&waveform, samples, (ma_uint64)frames, NULL);
    ma_waveform_uninit(&waveform);
    for (int32_t i = 0; i < frames; i++) {
        float gain = fminf(1, fminf((float)i / FADE_FRAMES, (float)(frames - 1 - i) / FADE_FRAMES));
        samples[i * 2] *= gain;
        samples[i * 2 + 1] *= gain;
    }
    return install(e, samples, (ma_uint64)frames);
}

int32_t audio_engine_adopt(audio_engine *e, float *samples, int32_t frames) {
    if (frames <= 0) { free(samples); return AUDIO_INVALID; }
    return install(e, samples, (ma_uint64)frames);
}

int32_t audio_engine_decode(audio_engine *e, const uint8_t *bytes, size_t length) {
    if (!length || length > AUDIO_MAX_ENCODED_BYTES) return AUDIO_INVALID;
    /* Decoding runs outside the engine's lock: the output keeps playing meanwhile. */
    ma_decoder decoder;
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, AUDIO_CHANNELS, AUDIO_SAMPLE_RATE);
    if (ma_decoder_init_memory(bytes, length, &config, &decoder) != MA_SUCCESS) return AUDIO_UNDECODABLE;
    ma_uint64 frames = 0;
    if (ma_decoder_get_length_in_pcm_frames(&decoder, &frames) != MA_SUCCESS || !frames) { ma_decoder_uninit(&decoder); return AUDIO_UNDECODABLE; }
    if (frames > AUDIO_MAX_FRAMES || frames * FRAME_BYTES > AUDIO_MAX_PCM_BYTES) { ma_decoder_uninit(&decoder); return AUDIO_TOO_LARGE; }
    float *samples = malloc((size_t)frames * FRAME_BYTES);
    if (!samples) { ma_decoder_uninit(&decoder); return AUDIO_NO_MEMORY; }
    ma_uint64 read = 0;
    ma_result result = ma_decoder_read_pcm_frames(&decoder, samples, frames, &read);
    ma_decoder_uninit(&decoder);
    if ((result != MA_SUCCESS && result != MA_AT_END) || !read) { free(samples); return AUDIO_UNDECODABLE; }
    for (ma_uint64 i = 0; i < read * AUDIO_CHANNELS; i++) if (!isfinite(samples[i])) { free(samples); return AUDIO_UNDECODABLE; }
    if (read < frames) {
        float *trimmed = realloc(samples, (size_t)read * FRAME_BYTES);
        if (!trimmed) { free(samples); return AUDIO_NO_MEMORY; }
        samples = trimmed;
    }
    return install(e, samples, read);
}

int32_t audio_engine_command(audio_engine *e, int32_t sound, audio_command command, double value) {
    engine_lock(e);
    voice *v = voice_of(e, sound);
    float n = (float)value;
    ma_result result = MA_SUCCESS;
    if (!v) result = MA_INVALID_ARGS;
    else switch (command) {
    case AUDIO_PLAY:
        /* A sound that played to its end starts over. */
        if (ma_sound_at_end(&v->sound)) ma_sound_seek_to_pcm_frame(&v->sound, 0);
        result = ma_sound_start(&v->sound);
        break;
    case AUDIO_PAUSE: result = ma_sound_stop(&v->sound); break;
    case AUDIO_STOP:
        ma_sound_stop(&v->sound);
        result = ma_sound_seek_to_pcm_frame(&v->sound, 0);
        break;
    case AUDIO_SET_VOLUME:
        if (!isfinite(n) || n < 0 || n > 1) result = MA_INVALID_ARGS; else ma_sound_set_volume(&v->sound, n);
        break;
    case AUDIO_SET_PAN:
        if (!isfinite(n) || n < -1 || n > 1) result = MA_INVALID_ARGS; else ma_sound_set_pan(&v->sound, n);
        break;
    case AUDIO_SET_PITCH:
        if (!isfinite(n) || n < 0.25f || n > 4) result = MA_INVALID_ARGS; else ma_sound_set_pitch(&v->sound, n);
        break;
    case AUDIO_SET_LOOPING:
        if (value != 0 && value != 1) result = MA_INVALID_ARGS; else ma_sound_set_looping(&v->sound, value == 1);
        break;
    case AUDIO_SEEK:
        if (!(value >= 0 && value <= (double)v->frames)) result = MA_INVALID_ARGS;
        else result = ma_sound_seek_to_pcm_frame(&v->sound, (ma_uint64)value);
        break;
    default: result = MA_INVALID_ARGS; break;
    }
    engine_unlock(e);
    return result == MA_SUCCESS ? 0 : result == MA_INVALID_ARGS ? AUDIO_INVALID : (int32_t)result;
}

bool audio_engine_playing(audio_engine *e, int32_t sound) {
    engine_lock(e);
    voice *v = voice_of(e, sound);
    bool playing = v && ma_sound_is_playing(&v->sound) && !ma_sound_at_end(&v->sound);
    engine_unlock(e);
    return playing;
}

uint64_t audio_engine_length(audio_engine *e, int32_t sound) {
    engine_lock(e);
    voice *v = voice_of(e, sound);
    uint64_t frames = v ? v->frames : 0;
    engine_unlock(e);
    return frames;
}

uint64_t audio_engine_position(audio_engine *e, int32_t sound) {
    engine_lock(e);
    voice *v = voice_of(e, sound);
    ma_uint64 cursor = 0;
    if (v) ma_sound_get_cursor_in_pcm_frames(&v->sound, &cursor);
    engine_unlock(e);
    return cursor;
}

void audio_engine_release(audio_engine *e, int32_t sound) {
    engine_lock(e);
    voice *v = voice_of(e, sound);
    if (v) voice_free(e, v);
    engine_unlock(e);
}

void audio_engine_render(audio_engine *e, float *output, uint32_t frames) {
    memset(output, 0, (size_t)frames * FRAME_BYTES);
    engine_lock(e);
    ma_engine_read_pcm_frames(&e->engine, output, frames, NULL);
    engine_unlock(e);
    for (uint32_t i = 0; i < frames * AUDIO_CHANNELS; i++) output[i] = fmaxf(-1, fminf(1, output[i]));
}

#if !defined(AQUA_OS_WEB)
/* ---- Native output ------------------------------------------------------------------------------------------------
 * The device's callback (miniaudio's audio thread) only copies frames out of a lock-free single-producer ring. A pump
 * thread keeps about QUEUE_FRAMES (21 ms) rendered ahead in BLOCK_FRAMES blocks; the callback wakes it after each
 * read with a try-lock, so the callback never waits, and the pump also wakes on a short timeout. */
#define RING_FRAMES 4096
#define QUEUE_FRAMES 1024
#define BLOCK_FRAMES 256

struct audio_output {
    audio_engine *engine;
    ma_device device;
    ma_pcm_rb ring;
    pthread_mutex_t lock; /* guards only the pump's wait */
    pthread_cond_t wake;
    pthread_t pump;
    atomic_bool running;
    bool pumping;
};

static void output_callback(ma_device *device, void *samples, const void *input, ma_uint32 frames) {
    audio_output *o = device->pUserData;
    float *target = samples;
    (void)input;
    while (frames) {
        ma_uint32 count = frames;
        void *source = NULL;
        if (ma_pcm_rb_acquire_read(&o->ring, &count, &source) != MA_SUCCESS || !count) break;
        memcpy(target, source, count * FRAME_BYTES);
        ma_pcm_rb_commit_read(&o->ring, count);
        target += count * AUDIO_CHANNELS;
        frames -= count;
    }
    memset(target, 0, frames * FRAME_BYTES); /* an underrun plays silence */
    if (pthread_mutex_trylock(&o->lock) == 0) {
        pthread_cond_signal(&o->wake);
        pthread_mutex_unlock(&o->lock);
    }
}

static void *output_pump(void *data) {
    audio_output *o = data;
    float block[BLOCK_FRAMES * AUDIO_CHANNELS];
    while (atomic_load(&o->running)) {
        while (atomic_load(&o->running) && ma_pcm_rb_available_read(&o->ring) < QUEUE_FRAMES) {
            audio_engine_render(o->engine, block, BLOCK_FRAMES);
            const float *source = block;
            for (ma_uint32 left = BLOCK_FRAMES; left;) {
                ma_uint32 count = left;
                void *target = NULL;
                if (ma_pcm_rb_acquire_write(&o->ring, &count, &target) != MA_SUCCESS || !count) break;
                memcpy(target, source, count * FRAME_BYTES);
                ma_pcm_rb_commit_write(&o->ring, count);
                source += count * AUDIO_CHANNELS;
                left -= count;
            }
        }
        pthread_mutex_lock(&o->lock);
        if (atomic_load(&o->running) && ma_pcm_rb_available_read(&o->ring) >= QUEUE_FRAMES) {
            struct timespec until;
            timespec_get(&until, TIME_UTC);
            until.tv_nsec += 20 * 1000000L;
            if (until.tv_nsec >= 1000000000L) { until.tv_sec++; until.tv_nsec -= 1000000000L; }
            pthread_cond_timedwait(&o->wake, &o->lock, &until);
        }
        pthread_mutex_unlock(&o->lock);
    }
    return NULL;
}

audio_output *audio_output_open(audio_engine *engine, const char **error) {
    *error = "No audio output device is available.";
    audio_output *o = calloc(1, sizeof *o);
    if (!o) return NULL;
    o->engine = engine;
    if (ma_pcm_rb_init(ma_format_f32, AUDIO_CHANNELS, RING_FRAMES, NULL, NULL, &o->ring) != MA_SUCCESS) { free(o); return NULL; }
    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32;
    config.playback.channels = AUDIO_CHANNELS;
    config.sampleRate = AUDIO_SAMPLE_RATE;
    config.periodSizeInFrames = BLOCK_FRAMES;
    config.dataCallback = output_callback;
    config.pUserData = o;
    config.aaudio.usage = ma_aaudio_usage_game;
    config.aaudio.contentType = ma_aaudio_content_type_music;
    if (ma_device_init(NULL, &config, &o->device) != MA_SUCCESS) { ma_pcm_rb_uninit(&o->ring); free(o); return NULL; }
    pthread_mutex_init(&o->lock, NULL);
    pthread_cond_init(&o->wake, NULL);
    *error = NULL;
    return o;
}

static void pump_stop(audio_output *o) {
    if (!o->pumping) return;
    atomic_store(&o->running, false);
    pthread_mutex_lock(&o->lock);
    pthread_cond_signal(&o->wake);
    pthread_mutex_unlock(&o->lock);
    pthread_join(o->pump, NULL);
    o->pumping = false;
}

bool audio_output_start(audio_output *o) {
    if (o->pumping) return true;
    atomic_store(&o->running, true);
    if (pthread_create(&o->pump, NULL, output_pump, o) != 0) { atomic_store(&o->running, false); return false; }
    o->pumping = true;
    if (ma_device_start(&o->device) != MA_SUCCESS) { pump_stop(o); return false; }
    return true;
}

void audio_output_stop(audio_output *o) {
    pump_stop(o);
    ma_device_stop(&o->device);
}

void audio_output_close(audio_output *o) {
    if (!o) return;
    pump_stop(o);
    ma_device_uninit(&o->device);
    ma_pcm_rb_uninit(&o->ring);
    pthread_cond_destroy(&o->wake);
    pthread_mutex_destroy(&o->lock);
    free(o);
}

bool audio_output_failed(audio_output *o) { (void)o; return false; }
#endif
