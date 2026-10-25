/* The Thread/執行緒 library on the web: each thread runs the application's module in a Web Worker started by the glue
   (web-glue/core/threads.mjs) from a snapshot of this memory. Waiting is asynchronous: the calling script waits for
   the worker while the page keeps running. */
#include "host.h"
#include <stdlib.h>
#include <string.h>

#define WEB_IMPORT(name) __attribute__((import_module("aqua_web"), import_name(#name)))
WEB_IMPORT(thread_start) int32_t web_thread_start(const uint8_t *snapshot, uint32_t length, uint32_t closure, uint32_t arguments, uint32_t count);
WEB_IMPORT(thread_state) int32_t web_thread_state(int32_t id);            /* 0 running, 1 finished, 2 failed */
WEB_IMPORT(thread_result) uint8_t *web_thread_result(int32_t id, uint32_t *length); /* packet or error text, malloc'd */
WEB_IMPORT(thread_cancel) int32_t web_thread_cancel(int32_t id);
WEB_IMPORT(thread_close) void web_thread_close(int32_t id);
WEB_IMPORT(thread_wait) void web_thread_wait(int32_t id, int32_t timeout_ms, int32_t token);

aqua_class *thread_class;
typedef struct web_thread { int32_t id; } web_thread;
typedef struct waiting { aqua_pending *pending; int32_t id; bool result; } waiting;

void thread_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    web_thread *t = data;
    if (t->id) web_thread_close(t->id);
    free(t);
}
void threads_close(aqua_runtime *rt) { (void)rt; }

static int32_t answer(aqua_runtime *rt, int32_t output, aqua_value v) { guest_write_value(rt, (uint32_t)output, v); return 0; }

static aqua_value result_of(aqua_runtime *rt, int32_t id) {
    uint32_t length = 0;
    uint8_t *bytes = web_thread_result(id, &length);
    if (web_thread_state(id) == 2) {
        aqua_value error = host_error(rt, "Thread: %.*s", (int)length, (const char *)bytes);
        free(bytes);
        return error;
    }
    uint32_t packet = guest(rt, GUEST_allocate, length, 0, 0, 0);
    memcpy(guest_base(rt) + packet, bytes, length);
    free(bytes);
    uint32_t cell = guest(rt, GUEST_thread_decode, packet, 0, 0, 0);
    guest(rt, GUEST_thread_free_packet, packet, 0, 0, 0);
    return cell ? guest_read_value(rt, cell) : host_error(rt, "Thread: Invalid thread result packet.");
}

/* The glue calls this when a waited-for worker finishes or the wait times out. */
__attribute__((export_name("aqua_web_thread_settled")))
void aqua_web_thread_settled(waiting *w, int32_t finished) {
    aqua_runtime *rt = w->pending->rt;
    aqua_resolve(w->pending, w->result && finished ? result_of(rt, w->id) : aqua_bool(finished != 0));
    free(w);
}

static int32_t wait_for(aqua_runtime *rt, int32_t id, int32_t timeout, bool result) {
    if (!rt->current) return 1;
    waiting *w = calloc(1, sizeof *w);
    w->pending = calloc(1, sizeof *w->pending);
    w->pending->rt = rt; w->pending->task = rt->current;
    rt->current->pending = w->pending;
    w->id = id; w->result = result;
    web_thread_wait(id, timeout, (int32_t)(uintptr_t)w);
    return 1; /* the guest waits; aqua_web_thread_settled resumes it */
}

int32_t thread_service(aqua_runtime *rt, int32_t operation, int32_t arguments, int32_t count, int32_t output) {
    if (operation == 100) {
        aqua_value closure = guest_read_value(rt, (uint32_t)arguments), list = count == 2 ? guest_read_value(rt, (uint32_t)arguments + 16) : aqua_void();
        uint32_t items = 0, n = 0;
        if (list.type == AQUA_ARRAY) { items = guest(rt, GUEST_array_values, list.ref, 0, 0, 0); n = aqua_array_length(rt, list); }
        uint32_t size = guest(rt, GUEST_thread_snapshot_size, 0, 0, 0, 0);
        int32_t id = web_thread_start(guest_base(rt), size, closure.ref, items, n);
        if (id <= 0) return answer(rt, output, host_error(rt, "Thread: Close threads before starting more (limit: 64)."));
        web_thread *t = calloc(1, sizeof *t);
        t->id = id;
        return answer(rt, output, aqua_object_new(rt, thread_class, t));
    }
    if (operation == 106) {
        double ms; memcpy(&ms, guest_base(rt) + arguments + 8, 8);
        return wait_for(rt, 0, (int32_t)ms, false); /* id 0: a timer */
    }
    if (operation == 107) return answer(rt, output, aqua_bool(false));
    web_thread *t = aqua_object_data(rt, guest_read_value(rt, (uint32_t)arguments), thread_class);
    if (!t) return answer(rt, output, host_error(rt, "Thread: Expected a thread handle from this runtime."));
    int32_t timeout = -1;
    if (count == 2) { double v; memcpy(&v, guest_base(rt) + arguments + 24, 8); timeout = (int32_t)v; }
    if (operation == 104) {
        if (!t->id) return answer(rt, output, aqua_bool(true));
        web_thread_cancel(t->id);
        web_thread_close(t->id);
        t->id = 0;
        return answer(rt, output, aqua_bool(true));
    }
    if (!t->id) return answer(rt, output, host_error(rt, "Thread: Thread is closed."));
    switch (operation) {
    case 101: return web_thread_state(t->id) ? answer(rt, output, aqua_bool(true)) : wait_for(rt, t->id, timeout, false);
    case 103: return answer(rt, output, aqua_bool(web_thread_cancel(t->id) != 0));
    case 105: return answer(rt, output, aqua_bool(web_thread_state(t->id) != 0));
    case 102: return web_thread_state(t->id) ? answer(rt, output, result_of(rt, t->id)) : wait_for(rt, t->id, -1, true);
    }
    return answer(rt, output, host_error(rt, "Thread: Unknown thread operation."));
}
