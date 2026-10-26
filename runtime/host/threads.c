/* The Thread/執行緒 library on native targets: each thread runs a function of the application in its own Wasmtime store,
   on an OS thread, starting from a snapshot of the caller's memory; results return as portable packets (runtime/guest
   threads.h). Workers reach only the portable builtins, Sleep and IsCancellationRequested. The web host runs threads in
   Web Workers instead (threads_web.c). */
#include "host.h"
#include "engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct platform_thread platform_thread;
typedef struct platform_signal platform_signal;
platform_thread *platform_thread_start(void (*run)(void *), void *data);
bool platform_thread_join(platform_thread *t, int32_t timeout_ms);
void platform_thread_free(platform_thread *t);
platform_signal *platform_signal_create(void);
void platform_signal_set(platform_signal *s);
bool platform_signal_wait(platform_signal *s, int32_t timeout_ms);
void platform_signal_destroy(platform_signal *s);
void platform_sleep(double ms);

#define THREAD_LIMIT 64
#define THREAD_QUANTUM 32768

typedef struct worker {
    engine *parent;
    uint8_t *snapshot; size_t snapshot_length;
    uint32_t closure, arguments, count;
    platform_thread *thread;
    platform_signal *cancel;
    _Atomic bool cancelled, done;
    uint8_t *packet; size_t packet_length;  /* result packet (u32 size + payload) */
    char *error;
    engine *instance;
    struct worker **cell;   /* the handle's cell naming this worker, cleared when the worker is reclaimed */
} worker;

typedef struct thread_state { worker *workers[THREAD_LIMIT]; uint32_t count; } thread_state;
aqua_class *thread_class; /* defined by core_library */

/* A worker's guest calls its host only for Thread.Sleep (106) and Thread.IsCancellationRequested (107); it resolves
   the builtins and refuses everything else itself (runtime/guest/runtime.c host_service). */
static int32_t worker_service(void *data, const int32_t a[7]) {
    worker *w = data;
    int32_t subject = a[2], arguments = a[4], output = a[6];
    uint8_t *memory = engine_memory(w->instance, NULL);
    aqua_value result;
    if (subject == 107) result = aqua_bool(atomic_load(&w->cancelled));
    else {
        double ms; memcpy(&ms, memory + arguments + 8, 8);
        result = aqua_bool(!platform_signal_wait(w->cancel, (int32_t)ms));
    }
    memcpy(memory + output, &result, sizeof result);
    return 0;
}

static uint32_t call(engine *e, guest_export name, uint32_t a, uint32_t b, uint32_t c) {
    uint32_t arguments[3] = {a, b, c};
    return engine_call(e, name, arguments, 3);
}

static void worker_run(void *data) {
    worker *w = data;
    char *error = NULL;
    w->instance = engine_worker(w->parent, worker_service, w, &error);
    if (!w->instance) { w->error = error; atomic_store(&w->done, true); return; }
    call(w->instance, GUEST_thread_reserve, (uint32_t)w->snapshot_length, 0, 0);
    memcpy(engine_memory(w->instance, NULL), w->snapshot, w->snapshot_length);
    free(w->snapshot); w->snapshot = NULL;
    uint32_t execution = call(w->instance, GUEST_thread_enter, w->closure, w->arguments, w->count);
    for (;;) {
        if (atomic_load(&w->cancelled)) { w->error = strdup("Execution cancelled."); break; }
        uint32_t status = call(w->instance, GUEST_run, execution, THREAD_QUANTUM, 0);
        char *trap = engine_take_trap(w->instance);
        if (trap) { w->error = trap; break; }
        if (status == G_STATUS_Yielded) continue;
        if (status != G_STATUS_Failed && status != G_STATUS_Completed) { w->error = strdup("Invalid worker execution status."); break; }
        uint32_t packet = call(w->instance, GUEST_thread_encode, call(w->instance, GUEST_result, execution, 0, 0), 0, 0);
        if (!packet) { w->error = strdup("Thread results must be portable data (no functions or host handles; depth <= 128, graph <= 4096, packet <= 16 MiB)."); break; }
        uint8_t *memory = engine_memory(w->instance, NULL);
        uint32_t size; memcpy(&size, memory + packet, 4);
        w->packet_length = (size_t)size + 4;
        w->packet = malloc(w->packet_length);
        memcpy(w->packet, memory + packet, w->packet_length);
        break;
    }
    engine_destroy(w->instance); w->instance = NULL;
    atomic_store(&w->done, true);
}

static void forget(thread_state *s, worker *w) {
    for (uint32_t i = 0; i < THREAD_LIMIT; i++) if (s->workers[i] == w) { s->workers[i] = NULL; s->count--; return; }
}

static void worker_free(worker *w) {
    if (!w) return;
    if (w->cell) *w->cell = NULL;
    atomic_store(&w->cancelled, true);
    platform_signal_set(w->cancel);
    platform_thread_join(w->thread, -1);
    platform_thread_free(w->thread);
    platform_signal_destroy(w->cancel);
    free(w->snapshot); free(w->packet); free(w->error);
    free(w);
}

/* A thread handle the guest dropped: cancel and reclaim its worker. The handle's data is a cell naming the worker,
   cleared when Close already reclaimed it. */
void thread_finalize(aqua_runtime *rt, void *data) {
    worker **cell = data;
    if (*cell && rt->threads) { forget(rt->threads, *cell); worker_free(*cell); }
    free(cell);
}

static thread_state *state(aqua_runtime *rt) {
    if (!rt->threads) rt->threads = calloc(1, sizeof(thread_state));
    return rt->threads;
}

void threads_close(aqua_runtime *rt) {
    thread_state *s = rt->threads;
    if (!s) return;
    for (uint32_t i = 0; i < THREAD_LIMIT; i++) if (s->workers[i]) atomic_store(&s->workers[i]->cancelled, true), platform_signal_set(s->workers[i]->cancel);
    for (uint32_t i = 0; i < THREAD_LIMIT; i++) { worker_free(s->workers[i]); s->workers[i] = NULL; }
    s->count = 0;
}

static int32_t answer(aqua_runtime *rt, int32_t output, aqua_value v) { guest_write_value(rt, (uint32_t)output, v); return 0; }

int32_t thread_service(aqua_runtime *rt, int32_t operation, int32_t arguments, int32_t count, int32_t output) {
    thread_state *s = state(rt);
    if (operation == 100) {
        if (s->count >= THREAD_LIMIT) return answer(rt, output, host_error(rt, "Thread: Close threads before starting more (limit: %d).", THREAD_LIMIT));
        aqua_value closure = guest_read_value(rt, (uint32_t)arguments), list = count == 2 ? guest_read_value(rt, (uint32_t)arguments + 16) : aqua_void();
        worker *w = calloc(1, sizeof *w);
        w->parent = rt->engine;
        w->closure = closure.ref;
        if (list.type == AQUA_ARRAY) { w->arguments = guest(rt, GUEST_array_values, list.ref, 0, 0, 0); w->count = aqua_array_length(rt, list); }
        w->snapshot_length = guest(rt, GUEST_thread_snapshot_size, 0, 0, 0, 0);
        w->snapshot = malloc(w->snapshot_length);
        memcpy(w->snapshot, guest_base(rt), w->snapshot_length);
        w->cancel = platform_signal_create();
        w->thread = platform_thread_start(worker_run, w);
        uint32_t slot = 0;
        while (s->workers[slot]) slot++;
        s->workers[slot] = w; s->count++;
        worker **cell = malloc(sizeof *cell);
        *cell = w; w->cell = cell;
        return answer(rt, output, aqua_object_new(rt, thread_class, cell));
    }
    if (operation == 106) {
        double ms; memcpy(&ms, guest_base(rt) + arguments + 8, 8);
        platform_sleep(ms);
        return answer(rt, output, aqua_bool(true));
    }
    if (operation == 107) return answer(rt, output, aqua_bool(false));
    worker **slot = aqua_object_data(rt, guest_read_value(rt, (uint32_t)arguments), thread_class);
    if (!slot) return answer(rt, output, host_error(rt, "Thread: Expected a thread handle from this runtime."));
    worker *w = *slot;
    int32_t timeout = -1;
    if (count == 2) { double t; memcpy(&t, guest_base(rt) + arguments + 24, 8); timeout = (int32_t)t; }
    if (operation == 104) {
        if (!w) return answer(rt, output, aqua_bool(true));
        atomic_store(&w->cancelled, true);
        platform_signal_set(w->cancel);
        bool finished = platform_thread_join(w->thread, timeout);
        if (finished) { forget(s, w); worker_free(w); *slot = NULL; }
        return answer(rt, output, aqua_bool(finished));
    }
    if (!w) return answer(rt, output, host_error(rt, "Thread: Thread is closed."));
    switch (operation) {
    case 101: return answer(rt, output, aqua_bool(platform_thread_join(w->thread, timeout)));
    case 103: {
        bool requested = !atomic_load(&w->done) && !atomic_exchange(&w->cancelled, true);
        if (requested) platform_signal_set(w->cancel);
        return answer(rt, output, aqua_bool(requested));
    }
    case 105: return answer(rt, output, aqua_bool(atomic_load(&w->done)));
    case 102: {
        platform_thread_join(w->thread, -1);
        if (w->error) return answer(rt, output, host_error(rt, "Thread: %s", w->error));
        uint32_t packet = guest(rt, GUEST_allocate, (uint32_t)w->packet_length, 0, 0, 0);
        memcpy(guest_base(rt) + packet, w->packet, w->packet_length);
        uint32_t cell = guest(rt, GUEST_thread_decode, packet, 0, 0, 0);
        guest(rt, GUEST_thread_free_packet, packet, 0, 0, 0);
        if (!cell) return answer(rt, output, host_error(rt, "Thread: Invalid thread result packet."));
        return answer(rt, output, guest_read_value(rt, cell));
    }
    }
    return answer(rt, output, host_error(rt, "Thread: Unknown thread operation."));
}
