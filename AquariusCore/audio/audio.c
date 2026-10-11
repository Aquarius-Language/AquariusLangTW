/* Aquarius audio library ABI 1. All decoding and mixing execute in this Wasm. */
#include <stdio.h>
#define fopen audio_no_fopen
#define fclose audio_no_fclose
#define fread audio_no_fread
#define fwrite audio_no_fwrite
#define fseek audio_no_fseek
#define ftell audio_no_ftell
#define ferror audio_no_ferror
#define clearerr audio_no_clearerr
static FILE* fopen(const char*, const char*);
static int fclose(FILE*);
static size_t fread(void*, size_t, size_t, FILE*);
static size_t fwrite(const void*, size_t, size_t, FILE*);
static int fseek(FILE*, long, int);
static long ftell(FILE*);
static int ferror(FILE*);
static void clearerr(FILE*);
#define MA_NO_DEVICE_IO
#define MA_NO_THREADING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_ENCODING
#define MINIAUDIO_IMPLEMENTATION
#include "../../native/vendor/miniaudio/miniaudio.h"

/* The library is memory-only. Disallow the unused default VFS so libc cannot
   introduce filesystem/WASI imports through decoder backend function tables. */
FILE* fopen(const char* path, const char* mode) { (void)path; (void)mode; return NULL; }
int fclose(FILE* file) { (void)file; return EOF; }
size_t fread(void* data, size_t size, size_t count, FILE* file) { (void)data; (void)size; (void)count; (void)file; return 0; }
size_t fwrite(const void* data, size_t size, size_t count, FILE* file) { (void)data; (void)size; (void)count; (void)file; return 0; }
int fseek(FILE* file, long offset, int origin) { (void)file; (void)offset; (void)origin; return -1; }
long ftell(FILE* file) { (void)file; return -1; }
int ferror(FILE* file) { (void)file; return 1; }
void clearerr(FILE* file) { (void)file; }

#define MAX_SOUNDS 64
#define MAX_FRAMES (48000 * 180)
#define MAX_PCM_BYTES (128 * 1024 * 1024)
typedef struct { ma_audio_buffer buffer; ma_sound sound; float* pcm; ma_uint64 frames; } aqua_sound;
static ma_engine engine;
static aqua_sound sounds[MAX_SOUNDS];
static int ready;
static size_t pcm_bytes;
static float output[4096 * 2];

int audio_abi(void) { return 1; }
int audio_alloc(int bytes) { return bytes > 0 && bytes <= MAX_PCM_BYTES ? (int)(uintptr_t)malloc(bytes) : 0; }
int audio_free(int pointer) { free((void*)(uintptr_t)pointer); return 0; }
int audio_init(void) {
    if (ready) return 0;
    ma_engine_config config = ma_engine_config_init();
    config.noDevice = MA_TRUE; config.channels = 2; config.sampleRate = 48000;
    config.noAutoStart = MA_TRUE;
    int result = ma_engine_init(&config, &engine);
    if (result == MA_SUCCESS) ready = 1;
    return result;
}
static aqua_sound* get(int id) { return ready && id > 0 && id <= MAX_SOUNDS && sounds[id-1].pcm ? &sounds[id-1] : NULL; }
static int install(float* pcm, ma_uint64 frames) {
    if (!ready || !frames || frames > MAX_FRAMES || frames * 8 > MAX_PCM_BYTES - pcm_bytes) { free(pcm); return -2; }
    for (int i = 0; i < MAX_SOUNDS; ++i) if (!sounds[i].pcm) {
        aqua_sound* s = &sounds[i];
        ma_audio_buffer_config config = ma_audio_buffer_config_init(ma_format_f32, 2, frames, pcm, NULL);
        config.sampleRate = 48000;
        int result = ma_audio_buffer_init(&config, &s->buffer);
        if (result != MA_SUCCESS) { free(pcm); return result; }
        result = ma_sound_init_from_data_source(&engine, &s->buffer, MA_SOUND_FLAG_NO_SPATIALIZATION, NULL, &s->sound);
        if (result != MA_SUCCESS) { ma_audio_buffer_uninit(&s->buffer); free(pcm); return result; }
        s->pcm = pcm; s->frames = frames; pcm_bytes += frames * 8;
        return i + 1;
    }
    free(pcm); return -3;
}
static float scalar(int bits) { union { int i; float f; } value = { .i = bits }; return value.f; }
static int bits(float f) { union { int i; float f; } value = { .f = f }; return value.i; }
int audio_buffer(int pointer, int frames) {
    if (!ready || frames <= 0 || frames > MAX_FRAMES || (size_t)frames * 8 > MAX_PCM_BYTES - pcm_bytes) return -2;
    float* pcm = malloc((size_t)frames * 8);
    if (!pcm) return -4;
    memcpy(pcm, (void*)(uintptr_t)pointer, (size_t)frames * 8);
    for (int i = 0; i < frames * 2; ++i) if (!isfinite(pcm[i]) || fabsf(pcm[i]) > 1) { free(pcm); return -2; }
    return install(pcm, frames);
}
int audio_tone(int frequency_bits, int frames) {
    float frequency = scalar(frequency_bits);
    if (!ready || !isfinite(frequency) || frequency < 20 || frequency > 20000 || frames <= 0 || frames > 48000 * 60 || (size_t)frames * 8 > MAX_PCM_BYTES - pcm_bytes) return -2;
    float* pcm = malloc((size_t)frames * 8);
    if (!pcm) return -4;
    ma_waveform waveform;
    ma_waveform_config config = ma_waveform_config_init(ma_format_f32, 2, 48000, ma_waveform_type_sine, 0.25, frequency);
    int result = ma_waveform_init(&config, &waveform);
    if (result != MA_SUCCESS) { free(pcm); return result; }
    ma_waveform_read_pcm_frames(&waveform, pcm, frames, NULL);
    ma_waveform_uninit(&waveform);
    /* A short deterministic envelope avoids clicks at the endpoints. */
    for (int i = 0; i < frames; ++i) {
        float gain = fminf(1, fminf((float)i / 240, (float)(frames - 1 - i) / 240));
        pcm[i*2] *= gain; pcm[i*2+1] *= gain;
    }
    return install(pcm, frames);
}
int audio_decode(int pointer, int length) {
    if (!ready || length <= 0 || length > 32 * 1024 * 1024) return -2;
    ma_decoder decoder;
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 2, 48000);
    int result = ma_decoder_init_memory((void*)(uintptr_t)pointer, length, &config, &decoder);
    if (result != MA_SUCCESS) return result;
    ma_uint64 frames = 0;
    result = ma_decoder_get_length_in_pcm_frames(&decoder, &frames);
    if (result != MA_SUCCESS || !frames || frames > MAX_FRAMES || frames * 8 > MAX_PCM_BYTES - pcm_bytes) { ma_decoder_uninit(&decoder); return -2; }
    float* pcm = malloc(frames * 8);
    if (!pcm) { ma_decoder_uninit(&decoder); return -4; }
    ma_uint64 read = 0;
    result = ma_decoder_read_pcm_frames(&decoder, pcm, frames, &read);
    ma_decoder_uninit(&decoder);
    if ((result != MA_SUCCESS && result != MA_AT_END) || !read) { free(pcm); return -2; }
    for (ma_uint64 i = 0; i < read * 2; ++i) if (!isfinite(pcm[i])) { free(pcm); return -2; }
    if (read < frames) {
        float* trimmed = realloc(pcm, read * 8);
        if (!trimmed) { free(pcm); return -4; }
        pcm = trimmed;
    }
    return install(pcm, read);
}
int audio_command(int id, int command, int value) {
    aqua_sound* s = get(id);
    if (!s) return -1;
    float n = scalar(value);
    switch (command) {
        case 0: if (ma_sound_at_end(&s->sound)) ma_sound_seek_to_pcm_frame(&s->sound, 0); return ma_sound_start(&s->sound);
        case 1: return ma_sound_stop(&s->sound);
        case 2: ma_sound_stop(&s->sound); return ma_sound_seek_to_pcm_frame(&s->sound, 0);
        case 3: if (!isfinite(n) || n < 0 || n > 1) return -2; ma_sound_set_volume(&s->sound, n); return 0;
        case 4: if (!isfinite(n) || n < -1 || n > 1) return -2; ma_sound_set_pan(&s->sound, n); return 0;
        case 5: if (!isfinite(n) || n < 0.25 || n > 4) return -2; ma_sound_set_pitch(&s->sound, n); return 0;
        case 6: if (value != 0 && value != 1) return -2; ma_sound_set_looping(&s->sound, value); return 0;
        case 7: if (value < 0 || (ma_uint64)value > s->frames) return -2; return ma_sound_seek_to_pcm_frame(&s->sound, value);
        default: return -2;
    }
}
int audio_query(int id, int query) {
    aqua_sound* s = get(id);
    if (!s) return -1;
    if (query == 0) return ma_sound_is_playing(&s->sound) && !ma_sound_at_end(&s->sound);
    if (query == 1) return (int)s->frames;
    if (query == 2) { ma_uint64 cursor = 0; ma_sound_get_cursor_in_pcm_frames(&s->sound, &cursor); return (int)cursor; }
    return -1;
}
int audio_release(int id) {
    aqua_sound* s = get(id);
    if (!s) return -1;
    ma_sound_uninit(&s->sound); ma_audio_buffer_uninit(&s->buffer);
    pcm_bytes -= s->frames * 8; free(s->pcm); memset(s, 0, sizeof(*s)); return 0;
}
int audio_render(int frames) {
    if (!ready || frames < 1 || frames > 4096) return 0;
    memset(output, 0, frames * 8);
    if (ma_engine_read_pcm_frames(&engine, output, frames, NULL) != MA_SUCCESS) return 0;
    for (int i = 0; i < frames * 2; ++i) output[i] = fmaxf(-1, fminf(1, output[i]));
    return (int)(uintptr_t)output;
}
int audio_dispose(void) {
    if (!ready) return 0;
    for (int i = 1; i <= MAX_SOUNDS; ++i) if (get(i)) audio_release(i);
    ma_engine_uninit(&engine); ready = 0; return 0;
}
