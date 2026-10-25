/* audio.c — the Audio|音訊 module: realtime and offline devices and their sounds, with the names, aliases, argument
 * counts, limits and error messages of the previous implementation (AquariusCore/audio). Decoding, mixing and output
 * are engine.c's (output_web.c on the web).
 *
 * Lifetime: Dispose releases a device with all its sounds, or one sound; repeated Dispose does nothing and a disposed
 * object fails every other call. The runtime's end disposes every device. A sound the script no longer reaches is
 * released once it is not playing; a device it no longer reaches is released once it has no sounds left and is not
 * playing to the speakers.
 *
 * 音訊模組：即時與離線裝置及其聲音。
 */
#include "engine.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(AQUA_OS_WINDOWS)
#include <windows.h>
#endif

typedef struct audio_library audio_library;

typedef struct audio_device {
    audio_library *library;     /* NULL once the runtime ended */
    struct audio_device *next;
    audio_engine *engine;       /* NULL once disposed */
    audio_output *output;       /* realtime devices, opened by Start */
    bool offline, started;
    bool reachable;             /* the script may still reach the device object */
    uint32_t sounds;            /* sound objects of this device the script may still reach */
    bool orphans[AUDIO_MAX_SOUNDS + 1]; /* sounds still playing after the script dropped them */
} audio_device;

typedef struct audio_sound {
    audio_device *device;
    int32_t id;                 /* 0 once disposed */
} audio_sound;

struct audio_library { audio_device *devices; };

static aqua_class *device_class, *sound_class;

/* ---- Lifetime ------------------------------------------------------------------------------------------------------ */

static void device_dispose(audio_device *d) {
    if (!d->engine) return;
    audio_output_close(d->output); /* stops the output before the engine it plays goes away */
    d->output = NULL;
    d->started = false;
    audio_engine_destroy(d->engine);
    d->engine = NULL;
}

static void device_unlink(audio_device *d) {
    if (!d->library) return;
    for (audio_device **p = &d->library->devices; *p; p = &(*p)->next)
        if (*p == d) { *p = d->next; break; }
    d->library = NULL;
}

/* Frees a device nothing can reach any more, unless it is still playing to the speakers (until the runtime ends). */
static void device_collect(audio_device *d) {
    if (d->reachable || d->sounds || (d->engine && d->started)) return;
    device_dispose(d);
    device_unlink(d);
    free(d);
}

/* Releases sounds the script dropped once they stopped playing. */
static void device_sweep(audio_device *d) {
    for (int32_t id = 1; id <= AUDIO_MAX_SOUNDS; id++) {
        if (d->orphans[id] && !audio_engine_playing(d->engine, id)) {
            audio_engine_release(d->engine, id);
            d->orphans[id] = false;
        }
    }
}

static void device_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    audio_device *d = data;
    d->reachable = false;
    device_collect(d);
}

static void sound_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    audio_sound *s = data;
    audio_device *d = s->device;
    if (s->id && d->engine) {
        if (audio_engine_playing(d->engine, s->id)) d->orphans[s->id] = true;
        else audio_engine_release(d->engine, s->id);
    }
    free(s);
    d->sounds--;
    device_collect(d);
}

static void *library_create(aqua_runtime *rt) { (void)rt; return calloc(1, sizeof(audio_library)); }

static void library_destroy(aqua_runtime *rt, void *state) {
    (void)rt;
    audio_library *library = state;
    while (library->devices) {
        audio_device *d = library->devices;
        library->devices = d->next;
        d->library = NULL;
        device_dispose(d);
        if (!d->reachable && !d->sounds) free(d);
    }
    free(library);
}

/* ---- Argument checks with the previous implementation's messages ----------------------------------------------- */

#define FAIL(call, name, ...) aqua_fail(call, "Audio." name ": " __VA_ARGS__)

/* The number as the previous implementation stored it (a float), when finite and within min..max. */
static bool number_in(aqua_call *call, const char *name, uint32_t index, double min, double max, float *out) {
    double n = aqua_arg_number(call, index);
    if (isfinite(n) && n >= min && n <= max) { *out = (float)n; return true; }
    aqua_runtime *rt = aqua_call_runtime(call);
    char *low = aqua_display(rt, aqua_double(min)), *high = aqua_display(rt, aqua_double(max));
    aqua_fail(call, "Audio.%s: Expected a finite number in %s..%s.", name, low, high);
    aqua_free(low);
    aqua_free(high);
    return false;
}

static int32_t seconds_to_frames(float seconds) { return (int32_t)lround((double)seconds * AUDIO_SAMPLE_RATE); }

static const char *status_message(int32_t status) {
    switch (status) {
    case AUDIO_FULL: return "A device mixes at most 64 sounds; dispose sounds that are no longer needed.";
    case AUDIO_NO_MEMORY: return "Out of memory for audio.";
    case AUDIO_TOO_LARGE: return "Sounds are limited to 180 seconds and 128 MiB of decoded samples per device.";
    case AUDIO_UNDECODABLE: return "Expected WAV, FLAC or MP3 audio.";
    default: return "Invalid audio data.";
    }
}

static audio_device *live_device(aqua_call *call, const char *name) {
    audio_device *d = aqua_self(call);
    if (!d->engine) { aqua_fail(call, "Audio.%s: Audio device has been disposed.", name); return NULL; }
    if (d->output && audio_output_failed(d->output)) { aqua_fail(call, "Audio.%s: Audio output failed.", name); return NULL; }
    return d;
}

static audio_sound *live_sound(aqua_call *call, const char *name) {
    audio_sound *s = aqua_self(call);
    if (!s->id || !s->device->engine) { aqua_fail(call, "Audio.%s: Audio sound has been disposed.", name); return NULL; }
    if (s->device->output && audio_output_failed(s->device->output)) { aqua_fail(call, "Audio.%s: Audio output failed.", name); return NULL; }
    return s;
}

/* ---- Audio|音訊 ---------------------------------------------------------------------------------------------------- */

static void create_device(aqua_call *call, bool offline, const char *name) {
    audio_library *library = aqua_state(call);
    audio_device *d = calloc(1, sizeof *d);
    if (d) d->engine = audio_engine_create();
    if (!d || !d->engine) { free(d); aqua_fail(call, "Audio.%s: The audio engine could not start.", name); return; }
    d->offline = offline;
    d->reachable = true;
    d->library = library;
    d->next = library->devices;
    library->devices = d;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), device_class, d));
}
static void create_realtime(aqua_call *call) { create_device(call, false, "CreateDevice"); }
static void create_offline(aqua_call *call) { create_device(call, true, "CreateOfflineDevice"); }

/* ---- Audio.Device ------------------------------------------------------------------------------------------------- */

static void device_start(aqua_call *call) {
    audio_device *d = live_device(call, "Start");
    if (!d) return;
    if (d->offline) { FAIL(call, "Start", "Offline devices have no output. Use Render."); return; }
    if (d->started) return;
    if (!d->output) {
        const char *error = NULL;
        d->output = audio_output_open(d->engine, &error);
        if (!d->output) { aqua_fail(call, "Audio.Start: %s", error); return; }
    }
    if (!audio_output_start(d->output)) { FAIL(call, "Start", "Audio output could not start."); return; }
    d->started = true;
}

static void device_suspend(aqua_call *call) {
    audio_device *d = aqua_self(call);
    if (!d->engine) { FAIL(call, "Suspend", "Audio device has been disposed."); return; }
    if (!d->started) return;
    audio_output_stop(d->output);
    d->started = false;
}

static void sound_new(aqua_call *call, audio_device *d, int32_t id, const char *name) {
    if (id < 0) { aqua_fail(call, "Audio.%s: %s", name, status_message(id)); return; }
    audio_sound *s = malloc(sizeof *s);
    if (!s) { audio_engine_release(d->engine, id); aqua_fail(call, "Audio.%s: Out of memory for audio.", name); return; }
    s->device = d;
    s->id = id;
    d->sounds++;
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), sound_class, s));
}

static void device_tone(aqua_call *call) {
    audio_device *d = live_device(call, "CreateTone");
    float frequency, seconds;
    if (!d || !number_in(call, "CreateTone", 0, 20, 20000, &frequency) || !number_in(call, "CreateTone", 1, 0.001, 60, &seconds)) return;
    device_sweep(d);
    sound_new(call, d, audio_engine_tone(d->engine, frequency, seconds_to_frames(seconds)), "CreateTone");
}

static void device_buffer(aqua_call *call) {
    audio_device *d = live_device(call, "CreateBuffer");
    if (!d) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value array = aqua_arg(call, 0);
    uint32_t count = aqua_array_length(rt, array);
    if (count < 2 || count % 2 || count > (uint32_t)AUDIO_MAX_FRAMES * 2) {
        FAIL(call, "CreateBuffer", "Expected interleaved stereo samples (2..%d values).", AUDIO_MAX_FRAMES * 2);
        return;
    }
    float *samples = malloc((size_t)count * sizeof(float));
    if (!samples) { FAIL(call, "CreateBuffer", "Out of memory for audio."); return; }
    double chunk[1024];
    for (uint32_t start = 0; start < count; start += 1024) {
        uint32_t n = count - start < 1024 ? count - start : 1024;
        bool numbers = aqua_array_read_f64(rt, array, start, n, chunk);
        for (uint32_t i = 0; i < n; i++) {
            if (!numbers || !isfinite(chunk[i]) || chunk[i] < -1 || chunk[i] > 1) {
                free(samples);
                FAIL(call, "CreateBuffer", "Expected a finite number in -1..1.");
                return;
            }
            samples[start + i] = (float)chunk[i];
        }
    }
    device_sweep(d);
    sound_new(call, d, audio_engine_adopt(d->engine, samples, (int32_t)(count / 2)), "CreateBuffer");
}

/* A file's bytes when it holds 1..AUDIO_MAX_ENCODED_BYTES; *size is its size, or -1 when it cannot be opened (always on
   the web, which has no file system: sounds are packaged assets there). */
static uint8_t *read_file(const char *path, long *size) {
    *size = -1;
    FILE *file = NULL;
#if defined(AQUA_OS_WINDOWS)
    wchar_t wide[4096];
    if (MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, 4096) > 0) file = _wfopen(wide, L"rb");
#elif !defined(AQUA_OS_WEB)
    file = fopen(path, "rb");
#endif
    if (!file) return NULL;
    uint8_t *bytes = NULL;
    if (fseek(file, 0, SEEK_END) == 0) *size = ftell(file);
    if (*size > 0 && (unsigned long)*size <= AUDIO_MAX_ENCODED_BYTES && fseek(file, 0, SEEK_SET) == 0 && (bytes = malloc((size_t)*size)) &&
        fread(bytes, 1, (size_t)*size, file) != (size_t)*size) { free(bytes); bytes = NULL; *size = -1; }
    fclose(file);
    return bytes;
}

/* Load reads a packaged asset, named by its package path or by a path under the application's directory (as
   目前工作目錄 builds it); otherwise the file at `path`. */
static void device_load(aqua_call *call) {
    audio_device *d = live_device(call, "Load");
    if (!d) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_str argument = aqua_arg_str(call, 0);
    char *path = aqua_scratch(call, argument.length + 1);
    memcpy(path, argument.data, argument.length + 1);
    for (char *p = path; *p; p++) if (*p == '\\') *p = '/';
    const char *name = path, *root = aqua_application_directory(rt);
    size_t root_length = strlen(root);
    if (root_length && strncmp(name, root, root_length) == 0 && name[root_length] == '/') name += root_length + 1;
    else if (!root_length && name[0] == '/' && strncmp(name, "/bottles/", 9) != 0) name++;
    while (name[0] == '.' && name[1] == '/') name += 2;
    size_t length = 0;
    const uint8_t *bytes = aqua_asset(rt, name, &length);
    uint8_t *owned = NULL;
    if (!bytes) {
        long size;
        bytes = owned = read_file(path, &size);
        if (size < 0) { FAIL(call, "Load", "Cannot read the audio asset or file %s.", argument.data); return; }
        length = (size_t)size;
    }
    if (!length || length > AUDIO_MAX_ENCODED_BYTES) { free(owned); FAIL(call, "Load", "Encoded audio must be 1..32 MiB."); return; }
    device_sweep(d);
    int32_t id = audio_engine_decode(d->engine, bytes, length);
    free(owned);
    sound_new(call, d, id, "Load");
}

static void device_render(aqua_call *call) {
    audio_device *d = live_device(call, "Render");
    if (!d) return;
    double requested = aqua_arg_number(call, 0);
    if (requested != trunc(requested)) { FAIL(call, "Render", "Expected an integer frame count."); return; }
    float frames;
    if (!number_in(call, "Render", 0, 1, AUDIO_MAX_RENDER_FRAMES, &frames)) return;
    if (!d->offline) { FAIL(call, "Render", "Render requires an offline device."); return; }
    uint32_t count = (uint32_t)frames * AUDIO_CHANNELS;
    float *samples = aqua_scratch(call, count * sizeof(float));
    audio_engine_render(d->engine, samples, (uint32_t)frames);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value result = aqua_array_new(rt, count);
    aqua_array_write_f32(rt, result, 0, count, samples, AQUA_DOUBLE);
    aqua_return(call, result);
}

static void device_release(aqua_call *call) { device_dispose(aqua_self(call)); }

/* ---- Audio.Sound -------------------------------------------------------------------------------------------------- */

static void sound_command(aqua_call *call, const char *name, audio_command command, double value) {
    audio_sound *s = aqua_self(call);
    int32_t result = audio_engine_command(s->device->engine, s->id, command, value);
    if (result < 0) aqua_fail(call, "Audio.%s: miniaudio operation failed (%d).", name, (int)result);
}

static void sound_play(aqua_call *call) { if (live_sound(call, "Play")) sound_command(call, "Play", AUDIO_PLAY, 0); }
static void sound_pause(aqua_call *call) { if (live_sound(call, "Pause")) sound_command(call, "Pause", AUDIO_PAUSE, 0); }
static void sound_stop(aqua_call *call) { if (live_sound(call, "Stop")) sound_command(call, "Stop", AUDIO_STOP, 0); }

static void sound_scalar(aqua_call *call, const char *name, audio_command command, double min, double max) {
    float value;
    if (live_sound(call, name) && number_in(call, name, 0, min, max, &value)) sound_command(call, name, command, value);
}
static void sound_volume(aqua_call *call) { sound_scalar(call, "SetVolume", AUDIO_SET_VOLUME, 0, 1); }
static void sound_pan(aqua_call *call) { sound_scalar(call, "SetPan", AUDIO_SET_PAN, -1, 1); }
static void sound_pitch(aqua_call *call) { sound_scalar(call, "SetPitch", AUDIO_SET_PITCH, 0.25, 4); }

static void sound_looping(aqua_call *call) {
    if (live_sound(call, "SetLooping")) sound_command(call, "SetLooping", AUDIO_SET_LOOPING, aqua_arg_bool(call, 0) ? 1 : 0);
}

static void sound_seek(aqua_call *call) {
    audio_sound *s = live_sound(call, "Seek");
    if (!s) return;
    uint64_t length = audio_engine_length(s->device->engine, s->id);
    float seconds;
    if (!number_in(call, "Seek", 0, 0, (double)length / AUDIO_SAMPLE_RATE, &seconds)) return;
    int64_t frame = seconds_to_frames(seconds);
    sound_command(call, "Seek", AUDIO_SEEK, (double)(frame > (int64_t)length ? (int64_t)length : frame));
}

static void sound_playing(aqua_call *call) {
    audio_sound *s = live_sound(call, "IsPlaying");
    if (s) aqua_return_bool(call, audio_engine_playing(s->device->engine, s->id));
}
static void sound_duration(aqua_call *call) {
    audio_sound *s = live_sound(call, "GetDuration");
    if (s) aqua_return_double(call, (double)audio_engine_length(s->device->engine, s->id) / AUDIO_SAMPLE_RATE);
}
static void sound_position(aqua_call *call) {
    audio_sound *s = live_sound(call, "GetPosition");
    if (s) aqua_return_double(call, (double)audio_engine_position(s->device->engine, s->id) / AUDIO_SAMPLE_RATE);
}

static void sound_release(aqua_call *call) {
    audio_sound *s = aqua_self(call);
    if (s->id && s->device->engine) audio_engine_release(s->device->engine, s->id);
    s->id = 0;
}

AQUA_LIBRARY(audio) {
    aqua_library_state(library, library_create, library_destroy);
    aqua_class *module = aqua_define_module(library, "Audio|音訊");
    aqua_define_function(module, "CreateDevice|建立裝置", create_realtime, "");
    aqua_define_function(module, "CreateOfflineDevice|建立離線裝置", create_offline, "");
    aqua_define_int(module, "SampleRate|取樣率", AUDIO_SAMPLE_RATE);
    aqua_define_int(module, "Channels|聲道數", AUDIO_CHANNELS);

    device_class = aqua_define_class(library, "Audio.Device", device_finalize);
    aqua_define_function(device_class, "Start|啟動", device_start, "");
    aqua_define_function(device_class, "Suspend|暫停輸出", device_suspend, "");
    aqua_define_function(device_class, "CreateTone|建立音調", device_tone, "nn");
    aqua_define_function(device_class, "CreateBuffer|建立樣本", device_buffer, "a");
    aqua_define_function(device_class, "Load|載入", device_load, "s");
    aqua_define_function(device_class, "Render|產生樣本", device_render, "n");
    aqua_define_function(device_class, "Dispose|釋放", device_release, "");

    sound_class = aqua_define_class(library, "Audio.Sound", sound_finalize);
    aqua_define_function(sound_class, "Play|播放", sound_play, "");
    aqua_define_function(sound_class, "Pause|暫停", sound_pause, "");
    aqua_define_function(sound_class, "Stop|停止", sound_stop, "");
    aqua_define_function(sound_class, "SetVolume|設定音量", sound_volume, "n");
    aqua_define_function(sound_class, "SetPan|設定聲像", sound_pan, "n");
    aqua_define_function(sound_class, "SetPitch|設定音高", sound_pitch, "n");
    aqua_define_function(sound_class, "SetLooping|設定循環", sound_looping, "b");
    aqua_define_function(sound_class, "Seek|跳至", sound_seek, "n");
    aqua_define_function(sound_class, "IsPlaying|是否播放中", sound_playing, "");
    aqua_define_function(sound_class, "GetDuration|取得時長", sound_duration, "");
    aqua_define_function(sound_class, "GetPosition|取得位置", sound_position, "");
    aqua_define_function(sound_class, "Dispose|釋放", sound_release, "");
}
