/* Terminal / 終端機 module: IsSupported, Run, RunScript, Start and Platform, with the old runtime's validation, options,
 * results and messages. A run is a deferred call: the script waits while the platform layer's worker thread collects
 * the process's output, and the result arrives through aqua_post. Start returns a Terminal.Process at once: its output
 * reaches OnOutput / OnError callbacks as it arrives, Wait gives the result and Kill ends the process tree. */
#include "terminal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DEFAULT_TIMEOUT_MS 60000
#define MAXIMUM_TIMEOUT_MS 3600000

static void deliver(aqua_runtime *rt, void *data);

struct terminal_state {
    aqua_runtime *rt;
    terminal_job *jobs;        /* runs in progress, owned here until delivered */
    atomic_bool posted;        /* a delivery task is queued */
};

static aqua_class *result_class, *process_class;

/* A Start call's Terminal.Process object: the job while it runs, then its result (or the collection error). */
struct terminal_process {
    terminal_job *job;
    aqua_ref result;
    char *error;
};

static void lock(terminal_job *job) { while (atomic_flag_test_and_set_explicit(&job->lock, memory_order_acquire)) {} }
static void unlock(terminal_job *job) { atomic_flag_clear_explicit(&job->lock, memory_order_release); }

/* ---- Output buffers ---- */

void terminal_output_append(terminal_output *output, const char *bytes, size_t length) {
    if (output->full || length == 0) return;
    /* Keep whole UTF-8 sequences: a lead byte starts one UTF-16 unit (two for four-byte sequences). */
    size_t keep = 0;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)bytes[i];
        if ((c & 0xC0) != 0x80) {
            size_t units = c >= 0xF0 ? 2 : 1;
            if (output->units + units > TERMINAL_OUTPUT_LIMIT) { output->full = true; break; }
            output->units += units;
        }
        keep = i + 1;
    }
    if (output->length + keep + 1 > output->capacity) {
        size_t capacity = output->capacity ? output->capacity : 4096;
        while (capacity < output->length + keep + 1) capacity *= 2;
        output->data = realloc(output->data, capacity);
        output->capacity = capacity;
    }
    memcpy(output->data + output->length, bytes, keep);
    output->length += keep;
    output->data[output->length] = 0;
}

/* ---- Results ---- */

typedef struct result { int32_t exit_code; bool timed_out; aqua_ref output, error; } result;

static void result_finalize(aqua_runtime *rt, void *data) {
    result *r = data;
    aqua_release(rt, r->output);
    aqua_release(rt, r->error);
    free(r);
}
static result *self_result(aqua_call *call) { return aqua_self(call); }
static void result_exit_code(aqua_call *call) { aqua_return_int(call, self_result(call)->exit_code); }
static void result_output(aqua_call *call) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), self_result(call)->output)); }
static void result_error(aqua_call *call) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), self_result(call)->error)); }
static void result_timed_out(aqua_call *call) { aqua_return_bool(call, self_result(call)->timed_out); }

static aqua_value text_of(aqua_runtime *rt, const terminal_output *output) {
    return aqua_text(rt, output->data ? output->data : "", output->length);
}

static aqua_value result_of(aqua_runtime *rt, const terminal_job *job) {
    result *r = calloc(1, sizeof *r);
    r->exit_code = job->timed_out ? -1 : job->exit_code;
    r->timed_out = job->timed_out;
    r->output = aqua_retain(rt, text_of(rt, &job->output));
    r->error = aqua_retain(rt, text_of(rt, &job->error_output));
    return aqua_object_new(rt, result_class, r);
}

/* ---- Streamed output ---- */

static void chunk_append(terminal_chunk *chunk, const char *bytes, size_t length) {
    if (chunk->length + length > chunk->capacity) {
        size_t capacity = chunk->capacity ? chunk->capacity : 4096;
        while (capacity < chunk->length + length) capacity *= 2;
        chunk->data = realloc(chunk->data, capacity);
        chunk->capacity = capacity;
    }
    memcpy(chunk->data + chunk->length, bytes, length);
    chunk->length += length;
}

/* How many leading bytes of the chunk end on a whole UTF-8 sequence (all of them once the stream has ended). */
static size_t chunk_complete(const terminal_chunk *chunk, bool ended) {
    size_t n = chunk->length;
    if (ended) return n;
    for (size_t back = 1; back <= 4 && back <= n; back++) {
        unsigned char c = (unsigned char)chunk->data[n - back];
        if ((c & 0xC0) == 0x80) continue;
        size_t size = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC0 ? 2 : 1;
        return size > back ? n - back : n;
    }
    return n;
}

void terminal_job_output(terminal_job *job, int stream, const char *bytes, size_t length) {
    terminal_state *state = job->state;
    lock(job);
    terminal_output_append(stream ? &job->error_output : &job->output, bytes, length);
    if (job->streams[stream]) chunk_append(&job->chunks[stream], bytes, length);
    unlock(job);
    if (job->streams[stream] && length && !atomic_exchange(&state->posted, true)) aqua_post(state->rt, deliver, state);
}

/* A callback finished: the next chunk (or the job's result) may go now. */
static void called(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    terminal_job *job = data;
    job->calling = false;
    if (!ok) {
        char *message = aqua_display(rt, result);
        fprintf(stderr, "Terminal / 終端機.Start: an output callback failed: %s\n", message);
        aqua_free(message);
    }
    if (!atomic_exchange(&job->state->posted, true)) aqua_post(rt, deliver, job->state);
}

/* Hands the next part of what the job's streams produced to its callback (runtime thread). Callbacks may wait (send
   over a network, await promises), so one runs at a time per job and the output keeps its order. True when nothing is
   left to hand over. */
static bool flush(aqua_runtime *rt, terminal_job *job, bool ended) {
    if (job->calling) return false;
    for (int stream = 0; stream < 2; stream++) {
        if (!job->streams[stream]) continue;
        lock(job);
        terminal_chunk *chunk = &job->chunks[stream];
        size_t n = chunk_complete(chunk, ended);
        char *text = NULL;
        if (n) {
            text = malloc(n);
            memcpy(text, chunk->data, n);
            memmove(chunk->data, chunk->data + n, chunk->length - n);
            chunk->length -= n;
        }
        unlock(job);
        if (!text) continue;
        aqua_value argument = aqua_text(rt, text, n);
        free(text);
        job->calling = true;
        aqua_invoke_async(rt, aqua_ref_value(rt, job->callbacks[stream]), &argument, 1, called, job);
        return false;
    }
    return true;
}

/* ---- Jobs ---- */

static void job_free(aqua_runtime *rt, terminal_job *job) {
    free(job->executable);
    for (uint32_t i = 0; i < job->count; i++) free(job->arguments[i]);
    free(job->arguments);
    free(job->directory);
    for (uint32_t i = 0; i < job->variable_count; i++) free(job->variables[i]);
    free(job->variables);
    free(job->output.data);
    free(job->error_output.data);
    free(job->error);
    for (int stream = 0; stream < 2; stream++) {
        free(job->chunks[stream].data);
        aqua_release(rt, job->callbacks[stream]);
    }
    free(job->waiters);
    free(job);
}

/* Runs on the runtime's thread: hands streamed output to its callbacks and settles every finished job. */
static void deliver(aqua_runtime *rt, void *data) {
    terminal_state *state = data;
    atomic_store(&state->posted, false);
    for (terminal_job **link = &state->jobs; *link;) {
        terminal_job *job = *link;
        bool done = atomic_load(&job->done);
        bool flushed = flush(rt, job, done);
        if (!done || !flushed) { link = &job->next; continue; }
        *link = job->next;
#if defined(TERMINAL_PROCESSES)
        terminal_platform_finish(job);
#endif
        aqua_value outcome = job->error ? aqua_null() : result_of(rt, job);
        if (job->pending) {
            if (job->error) aqua_reject(job->pending, "Terminal / 終端機.%s: %s", job->function, job->error);
            else aqua_resolve(job->pending, outcome);
        }
        for (uint32_t i = 0; i < job->waiter_count; i++) {
            if (job->error) aqua_reject(job->waiters[i], "Terminal / 終端機.Process.Wait: %s", job->error);
            else aqua_resolve(job->waiters[i], outcome);
        }
        if (job->process) {
            job->process->job = NULL;
            if (job->error) job->process->error = strdup(job->error);
            else job->process->result = aqua_retain(rt, outcome);
        }
        job_free(rt, job);
    }
}

void terminal_job_done(terminal_job *job) {
    terminal_state *state = job->state;
    atomic_store(&job->done, true);
    if (!atomic_exchange(&state->posted, true)) aqua_post(state->rt, deliver, state);
}

static void *state_create(aqua_runtime *rt) {
    terminal_state *state = calloc(1, sizeof *state);
    state->rt = rt;
    return state;
}

static void state_destroy(aqua_runtime *rt, void *data) {
    terminal_state *state = data;
    for (terminal_job *job = state->jobs; job;) {
        terminal_job *next = job->next;
#if defined(TERMINAL_PROCESSES)
        if (atomic_load(&job->done)) terminal_platform_finish(job);
        else terminal_platform_cancel(job);
#endif
        if (job->process) job->process->job = NULL;
        job_free(rt, job);
        job = next;
    }
    free(state);
}

/* ---- Processes (Start) ---- */

static void process_finalize(aqua_runtime *rt, void *data) {
    terminal_process *process = data;
    if (process->job) process->job->process = NULL; /* it keeps running; its result goes to pending waits only */
    aqua_release(rt, process->result);
    free(process->error);
    free(process);
}
static terminal_process *self_process(aqua_call *call) { return aqua_self(call); }

/* Wait() / WaitAsync(): the Terminal.Result once the process has ended and its output is collected. */
static void process_wait(aqua_call *call) {
    terminal_process *process = self_process(call);
    if (process->error) { aqua_fail(call, "Terminal / 終端機.Process.Wait: %s", process->error); return; }
    if (!process->job) { aqua_return(call, aqua_ref_value(aqua_call_runtime(call), process->result)); return; }
    terminal_job *job = process->job;
    job->waiters = realloc(job->waiters, (job->waiter_count + 1) * sizeof *job->waiters);
    job->waiters[job->waiter_count++] = aqua_defer(call);
}

/* Kill(): ends the process tree; true when it was still running. Wait() then reports its exit. */
static void process_kill(aqua_call *call) {
    terminal_process *process = self_process(call);
    bool running = process->job && !atomic_load(&process->job->done);
#if defined(TERMINAL_PROCESSES)
    if (running) terminal_platform_kill(process->job);
#endif
    aqua_return_bool(call, running);
}
static void process_running(aqua_call *call) { aqua_return_bool(call, self_process(call)->job != NULL); }
static void process_result(aqua_call *call) {
    terminal_process *process = self_process(call);
    if (process->job || !process->result) aqua_return_null(call);
    else aqua_return(call, aqua_ref_value(aqua_call_runtime(call), process->result));
}

/* ---- Validation ---- */

static void fail(aqua_call *call, const char *function, const char *message) {
    aqua_fail(call, "Terminal / 終端機.%s: %s", function, message);
}

/* Text(value) of the old runtime: a string without NUL characters, as malloc'd UTF-8. */
static char *text_value(aqua_call *call, aqua_value value, const char *function) {
    if (value.type == AQUA_STRING) {
        aqua_str s = aqua_str_of(call, value);
        if (!memchr(s.data, 0, s.length)) return strdup(s.data);
    }
    fail(call, function, "Expected a string without NUL characters.");
    return NULL;
}

static bool blank(const char *s) {
    for (; *s; s++) if (!strchr(" \t\n\r\v\f", *s)) return false;
    return true;
}

static bool same_prefix(const char *path, const char *prefix, size_t length) {
    for (size_t i = 0; i < length; i++) {
        char a = path[i], b = prefix[i];
        if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
        if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
        if (a != b) return false;
    }
    return true;
}

/* Paths naming a packaged asset (relative to the application, under it, or /bottles/...) become the asset's file, as
   the old runtime's resource mapper did; any other path stays as written. */
static char *resolve_path(aqua_call *call, const char *path) {
    aqua_runtime *rt = aqua_call_runtime(call);
    char *normal = strdup(path);
    for (char *p = normal; *p; p++) if (*p == '\\') *p = '/';
    const char *relative = normal;
    bool absolute = normal[0] == '/' || (normal[0] && normal[1] == ':');
    const char *root = aqua_application_directory(rt);
    size_t root_length = root ? strlen(root) : 0;
    while (root_length && root[root_length - 1] == '/') root_length--;
    if (same_prefix(normal, "/bottles/", 9)) relative = normal + 1;
    else if (absolute) {
        relative = root_length && strlen(normal) > root_length && normal[root_length] == '/' && same_prefix(normal, root, root_length)
            ? normal + root_length + 1 : NULL;
    }
    while (relative && relative[0] == '.' && relative[1] == '/') relative += 2;
    const char *file = relative ? aqua_asset_file(rt, relative) : NULL;
    char *resolved = strdup(file ? file : path);
    free(normal);
    return resolved;
}

/* Environment: {"NAME": "value" | null}, set in (or, null, removed from) the runtime's environment for the child. */
static bool read_environment(aqua_call *call, aqua_value variables, terminal_job *job) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (variables.type != AQUA_HASH) { fail(call, job->function, "Environment must be a map of strings."); return false; }
    uint32_t count = aqua_hash_count(rt, variables);
    char **list = realloc(job->variables, ((size_t)job->variable_count + count + 1) * sizeof *list);
    if (!list) { fail(call, job->function, "Out of memory."); return false; }
    job->variables = list;
    for (uint32_t i = 0; i < count; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, variables, i, &key, &value);
        char *name = text_value(call, key, job->function);
        if (!name) return false;
        if (!*name || strchr(name, '=')) { free(name); fail(call, job->function, "Environment names must be non-empty and without '='."); return false; }
        char *text = value.type == AQUA_NULL ? NULL : text_value(call, value, job->function);
        if (value.type != AQUA_NULL && !text) { free(name); return false; }
        size_t n = strlen(name) + (text ? strlen(text) + 2 : 1);
        char *entry = malloc(n);
        snprintf(entry, n, text ? "%s=%s" : "%s", name, text ? text : "");
        free(name);
        free(text);
        job->variables[job->variable_count++] = entry;
    }
    return true;
}

/* The options hash: WorkingDirectory, TimeoutMs and Environment, and for Start the OnOutput / OnError callbacks. */
static bool read_options(aqua_call *call, uint32_t index, terminal_job *job) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_argc(call) <= index) return true;
    aqua_value options = aqua_arg(call, index);
    if (options.type != AQUA_HASH) { fail(call, job->function, "Options must be a map."); return false; }
    uint32_t count = aqua_hash_count(rt, options);
    for (uint32_t i = 0; i < count; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, options, i, &key, &value);
        char *name = text_value(call, key, job->function);
        if (!name) return false;
        bool ok = true;
        if (strcmp(name, "WorkingDirectory") == 0) {
            char *directory = text_value(call, value, job->function);
            ok = directory != NULL;
            if (ok) { free(job->directory); job->directory = resolve_path(call, directory); free(directory); }
        } else if (strcmp(name, "Environment") == 0) {
            ok = read_environment(call, value, job);
        } else if (strcmp(name, "TimeoutMs") == 0) {
            double n = value.number;
            ok = aqua_is_number(value) && n >= 1 && n <= MAXIMUM_TIMEOUT_MS && n == (double)(int32_t)n;
            if (ok) job->timeout_ms = (int32_t)n;
            else fail(call, job->function, "TimeoutMs must be an integer in 1..3600000.");
        } else if (strcmp(job->function, "Start") == 0 && (strcmp(name, "OnOutput") == 0 || strcmp(name, "OnError") == 0)) {
            int stream = strcmp(name, "OnError") == 0;
            ok = aqua_is_callable(value);
            if (ok) {
                aqua_release(rt, job->callbacks[stream]);
                job->callbacks[stream] = aqua_retain(rt, value);
                job->streams[stream] = true;
            } else fail(call, job->function, "OnOutput and OnError must be functions.");
        } else {
            fail(call, job->function, "Unknown terminal option.");
            ok = false;
        }
        free(name);
        if (!ok) return false;
    }
    return true;
}

/* Run and RunScript wait for the result; Start (`process`) returns a Terminal.Process at once and has no timeout unless
   TimeoutMs is given. */
static void start(aqua_call *call, const char *function, bool script, bool process) {
#if !defined(TERMINAL_PROCESSES)
    (void)script; (void)process;
    fail(call, function, "Terminal processes are unavailable on this platform.");
#else
    aqua_runtime *rt = aqua_call_runtime(call);
    terminal_job *job = calloc(1, sizeof *job);
    job->function = function;
    job->timeout_ms = process ? 0 : DEFAULT_TIMEOUT_MS;
    atomic_flag_clear(&job->lock);
    uint32_t option_index;
    if (script) {
        char *text = text_value(call, aqua_arg(call, 0), function);
        if (!text) { job_free(rt, job); return; }
        option_index = 1;
#if defined(TERMINAL_WINDOWS)
        static const char prefix[] = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$OutputEncoding=[Console]::OutputEncoding;";
        static const char *const switches[] = {"-NoLogo", "-NoProfile", "-NonInteractive", "-Command"};
        job->executable = strdup("powershell.exe");
        job->count = 5;
        job->arguments = calloc(job->count, sizeof *job->arguments);
        for (uint32_t i = 0; i < 4; i++) job->arguments[i] = strdup(switches[i]);
        job->arguments[4] = malloc(sizeof prefix + strlen(text));
        memcpy(job->arguments[4], prefix, sizeof prefix - 1);
        strcpy(job->arguments[4] + sizeof prefix - 1, text);
        free(text);
#else
        job->executable = strdup(strcmp(AQUA_PLATFORM_NAME, "android") == 0 ? "/system/bin/sh" : "/bin/sh");
        job->count = 2;
        job->arguments = calloc(job->count, sizeof *job->arguments);
        job->arguments[0] = strdup("-c");
        job->arguments[1] = text;
#endif
    } else {
        char *executable = text_value(call, aqua_arg(call, 0), function);
        if (!executable) { job_free(rt, job); return; }
        option_index = 2;
        aqua_value list = aqua_arg(call, 1);
        if (list.type != AQUA_ARRAY) { free(executable); fail(call, function, "Arguments must be a string array."); job_free(rt, job); return; }
        job->count = aqua_array_length(rt, list);
        job->arguments = calloc(job->count ? job->count : 1, sizeof *job->arguments);
        for (uint32_t i = 0; i < job->count; i++) {
            job->arguments[i] = text_value(call, aqua_array_get(rt, list, i), function);
            if (!job->arguments[i]) { free(executable); job_free(rt, job); return; }
        }
        if (blank(executable)) { free(executable); fail(call, function, "Executable is required."); job_free(rt, job); return; }
        if (strchr(executable, '/') || strchr(executable, '\\')) {
            job->executable = resolve_path(call, executable);
            free(executable);
        } else job->executable = executable;
    }
    if (!read_options(call, option_index, job)) { job_free(rt, job); return; }
    terminal_state *state = aqua_state(call);
    job->state = state;
    char *error = NULL;
    if (!terminal_platform_start(job, &error)) {
        fail(call, function, error ? error : "Could not launch process.");
        free(error);
        job_free(rt, job);
        return;
    }
    job->next = state->jobs;
    state->jobs = job;
    if (!process) { job->pending = aqua_defer(call); return; }
    terminal_process *object = calloc(1, sizeof *object);
    object->job = job;
    job->process = object;
    aqua_return(call, aqua_object_new(rt, process_class, object));
#endif
}

/* ---- Module ---- */

static void is_supported(aqua_call *call) {
#if defined(TERMINAL_PROCESSES)
    aqua_return_bool(call, true);
#else
    aqua_return_bool(call, false);
#endif
}
static void run(aqua_call *call) { start(call, "Run", false, false); }
static void run_script(aqua_call *call) { start(call, "RunScript", true, false); }
static void start_process(aqua_call *call) { start(call, "Start", false, true); }

AQUA_LIBRARY(terminal) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *module = aqua_define_module(library, "Terminal|終端機");
    aqua_define_string(module, "Platform|平台", AQUA_PLATFORM_NAME);
    aqua_define_function(module, "IsSupported|是否支援", is_supported, "");
    /* Kinds are checked here, so the old messages (and the unsupported error first) stay as they were. */
    aqua_define_function(module, "Run|執行", run, "vv|v");
    aqua_define_function(module, "RunScript|執行腳本", run_script, "v|v");
    aqua_define_function(module, "Start|啟動", start_process, "vv|v");

    result_class = aqua_define_class(library, "Terminal.Result", result_finalize);
    aqua_define_property(result_class, "ExitCode|結束代碼", result_exit_code);
    aqua_define_property(result_class, "StandardOutput|標準輸出", result_output);
    aqua_define_property(result_class, "StandardError|標準錯誤", result_error);
    aqua_define_property(result_class, "TimedOut|已逾時", result_timed_out);

    process_class = aqua_define_class(library, "Terminal.Process", process_finalize);
    aqua_define_function(process_class, "Wait|等待結束", process_wait, "");
    aqua_define_async(process_class, "WaitAsync|非同步等待結束", process_wait, "");
    aqua_define_function(process_class, "Kill|結束", process_kill, "");
    aqua_define_property(process_class, "Running|執行中", process_running);
    aqua_define_property(process_class, "Result|結果", process_result);
}
