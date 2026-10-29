/* Executions and the runtime's loop. Every guest execution (the entry script, imports, callbacks) runs in slices of
   the guest's fuel quantum. An execution that calls a deferred library function waits without blocking the others;
   ticks (frame drivers, timers) and work posted from other threads run between slices. The platform layer drives
   runtime_turn: a loop on native targets, the browser's event loop on the web. */
#include "host.h"
#include "engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void platform_wait(aqua_runtime *rt, double timeout_ms); /* sleep until posted work, OS events or the timeout */

static aqua_value execution_result(aqua_runtime *rt, uint32_t execution) {
    return guest_read_value(rt, guest(rt, GUEST_result, execution, 0, 0, 0));
}

static void unlink_task(aqua_runtime *rt, task *t) {
    for (task **p = &rt->tasks; *p; p = &(*p)->next) if (*p == t) { *p = t->next; return; }
}

void task_finish(aqua_runtime *rt, task *t, bool ok, aqua_value result) {
    t->finished = true; t->failed = !ok; t->result = result;
    aqua_ref keep = aqua_retain(rt, result);
    if (t->execution) guest(rt, GUEST_release_execution, t->execution, 0, 0, 0);
    t->execution = 0;
    if (t->done) t->done(rt, ok, result, t->data);
    else if (!ok && !rt->finished) {
        /* A failing callback nobody waits for ends the application, like a failing script. */
        char *text = format_value(rt, result);
        platform_write(1, text, strlen(text));
        platform_write(1, "\n", 1);
        free(text);
        rt->exit_code = 1; rt->exiting = true;
    }
    aqua_release(rt, keep);
}

uint32_t run_slice(aqua_runtime *rt, task *t) {
    task *previous = rt->current;
    rt->current = t;
    uint32_t status = guest(rt, GUEST_run, t->execution, rt->quantum, 0, 0);
    rt->current = previous;
    char *trap = engine_take_trap(rt->engine);
    if (trap) {
        task_finish(rt, t, false, host_error(rt, "WebAssembly trap: %s", trap));
        free(trap);
        return G_STATUS_Failed;
    }
    handles_sweep(rt);
    switch (status) {
    case G_STATUS_Yielded:
        if (guest(rt, GUEST_collection_due, 0, 0, 0, 0)) { guest(rt, GUEST_collect, 0, 0, 0, 0); handles_sweep(rt); }
        break;
    case G_STATUS_Failed: task_finish(rt, t, false, execution_result(rt, t->execution)); break;
    case G_STATUS_Completed: task_finish(rt, t, true, execution_result(rt, t->execution)); break;
    }
    return status;
}

static void process_posts(aqua_runtime *rt) {
    if (!atomic_exchange(&rt->post_flag, false)) return;
    platform_lock(rt->post_lock);
    posted *list = rt->posts;
    rt->posts = NULL;
    platform_unlock(rt->post_lock);
    /* Run in posting order. */
    posted *ordered = NULL;
    while (list) { posted *next = list->next; list->next = ordered; ordered = list; list = next; }
    while (ordered) { posted *next = ordered->next; ordered->run(rt, ordered->data); free(ordered); ordered = next; }
}

/* Runs a task that is not on the loop's list until it finishes. Waiting for a deferred result is allowed natively,
   where the platform keeps delivering posted work; the web cannot block its only thread. */
static void delivered(aqua_runtime *rt, bool ok, aqua_value result, void *data) { (void)rt; (void)ok; (void)result; (void)data; }
static bool run_to_end(aqua_runtime *rt, task *t) {
    if (!t->done) t->done = delivered; /* the caller receives the result (an import's error is reported once, by it) */
    while (!t->finished) {
        uint32_t status = run_slice(rt, t);
        if (status != G_STATUS_Suspended || t->finished) continue;
#if defined(AQUA_OS_WEB)
        task_finish(rt, t, false, host_error(rt, "This call cannot wait for an asynchronous operation on the web; call it from the main script or an event handler."));
        return false;
#else
        while (t->pending) { process_posts(rt); if (t->pending) platform_wait(rt, 50); }
#endif
    }
    return !t->failed;
}

bool run_module(aqua_runtime *rt, uint32_t module, uint32_t scope, aqua_value *result) {
    uint32_t context = context_new(rt, rt->app.modules[module].path);
    task t = {0};
    t.execution = guest(rt, GUEST_execute, rt->app.program_address, rt->app.modules[module].function, scope, context);
    bool ok = run_to_end(rt, &t);
    *result = t.result;
    return ok;
}

static uint32_t start_execution(aqua_runtime *rt, aqua_value function, const aqua_value *arguments, uint32_t count) {
    uint32_t values = guest(rt, GUEST_allocate, count * G_Value_Size, 0, 0, 0);
    memcpy(guest_base(rt) + values, arguments, (size_t)count * G_Value_Size);
    return guest(rt, GUEST_invoke, function.ref, values, count, 0);
}

bool aqua_invoke(aqua_runtime *rt, aqua_value function, const aqua_value *arguments, uint32_t count, aqua_value *result) {
    aqua_value outcome;
    bool ok;
    if (function.type == AQUA_FUNCTION) {
        task t = {0};
        t.execution = start_execution(rt, function, arguments, count);
        ok = run_to_end(rt, &t);
        outcome = t.result;
    } else if (function.type == AQUA_NATIVE) {
        aqua_pending *pending = NULL;
        outcome = call_native(rt, function.ref, arguments, count, 0, &pending);
        if (pending) {
#if defined(AQUA_OS_WEB)
            outcome = host_error(rt, "This call cannot wait for an asynchronous operation on the web.");
#else
            while (!pending->settled) { process_posts(rt); if (!pending->settled) platform_wait(rt, 50); }
            outcome = pending->result;
            aqua_release(rt, pending->result_ref);
#endif
            free(pending);
        }
        ok = outcome.type != AQUA_ERROR;
    } else {
        outcome = host_error(rt, "Not a function: %s", aqua_type_name(function));
        ok = false;
    }
    if (result) *result = outcome;
    return ok;
}

typedef struct native_wait { aqua_done done; void *data; } native_wait;

void aqua_invoke_async(aqua_runtime *rt, aqua_value function, const aqua_value *arguments, uint32_t count, aqua_done done, void *data) {
    if (function.type == AQUA_FUNCTION) {
        task *t = calloc(1, sizeof *t);
        t->execution = start_execution(rt, function, arguments, count);
        t->done = done; t->data = data; t->loop = true;
        /* Run the first slice now, so callbacks that need no waiting complete before this returns. */
        uint32_t status = run_slice(rt, t);
        if (t->finished) { free(t); return; }
        (void)status;
        t->next = rt->tasks; rt->tasks = t;
        return;
    }
    if (function.type != AQUA_NATIVE) { if (done) done(rt, false, host_error(rt, "Not a function: %s", aqua_type_name(function)), data); return; }
    aqua_pending *pending = NULL;
    aqua_value result = call_native(rt, function.ref, arguments, count, 0, &pending);
    if (!pending) { if (done) done(rt, result.type != AQUA_ERROR, result, data); return; }
    task *t = calloc(1, sizeof *t);
    t->pending = pending; t->done = done; t->data = data;
    pending->task = t;
    t->next = rt->tasks; rt->tasks = t;
}

/* ---- Deferred library calls ---- */

aqua_pending *aqua_defer(aqua_call *call) {
    if (!call->pending) { call->pending = calloc(1, sizeof *call->pending); call->pending->rt = call->rt; }
    return call->pending;
}
aqua_runtime *aqua_pending_runtime(aqua_pending *pending) { return pending->rt; }

void aqua_resolve(aqua_pending *pending, aqua_value result) {
    if (!pending || pending->settled) return;
    aqua_runtime *rt = pending->rt;
    pending->settled = true;
    if (pending->promise) { promise_settle_pending(rt, pending->promise, result); free(pending); return; }
    task *t = pending->task;
    if (!t) { pending->result = result; pending->result_ref = aqua_retain(rt, result); return; } /* a synchronous waiter takes it */
    t->pending = NULL;
    if (t->execution) {
        guest(rt, GUEST_resume, t->execution, guest_cell(rt, result), 0, 0);
    } else {
        /* A native function called through aqua_invoke_async. */
        unlink_task(rt, t);
        if (t->done) t->done(rt, result.type != AQUA_ERROR, result, t->data);
        free(t);
    }
    free(pending);
}

void aqua_vreject(aqua_pending *pending, const char *format, va_list arguments) {
    if (!pending) return;
    char message[2048];
    vsnprintf(message, sizeof message, format, arguments);
    aqua_resolve(pending, host_error(pending->rt, "%s", message));
}
void aqua_reject(aqua_pending *pending, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    aqua_vreject(pending, format, arguments);
    va_end(arguments);
}

/* ---- Posted work and ticks ---- */

void aqua_post(aqua_runtime *rt, void (*run)(aqua_runtime *, void *), void *data) {
    posted *p = malloc(sizeof *p);
    p->run = run; p->data = data;
    platform_lock(rt->post_lock);
    p->next = rt->posts;
    rt->posts = p;
    platform_unlock(rt->post_lock);
    atomic_store(&rt->post_flag, true);
    platform_wake(rt);
}

static void insert_timer(aqua_runtime *rt, timer *t) {
    timer **p = &rt->timers;
    while (*p && (*p)->due <= t->due) p = &(*p)->next;
    t->next = *p;
    *p = t;
}
void aqua_schedule(aqua_runtime *rt, double delay_ms, aqua_tick tick, void *data) {
    timer *t = malloc(sizeof *t);
    t->due = aqua_now_ms(rt) + (delay_ms > 0 ? delay_ms : 0); t->tick = tick; t->data = data;
    insert_timer(rt, t);
}

/* ---- The entry script and the loop ---- */

static void main_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)data;
    /* The script's final value is shown like print shows it; nothing for a final declaration or print. */
    if (result.type != AQUA_VOID) {
        char *text = format_value(rt, result);
        platform_write(1, text, strlen(text));
        platform_write(1, "\n", 1);
        free(text);
    }
    if (!ok) { rt->exit_code = 1; rt->exiting = true; }
    rt->finished = true;
}

/* The native libraries the application declares must be registered before its code runs: default libraries are part
   of the runtime, external ones are found next to the application (on the web the glue links them before this). */
static bool require_libraries(aqua_runtime *rt) {
    for (uint32_t i = 0; i < rt->app.library_count; i++) {
        if (registry_library(rt->app.libraries[i])) continue;
        char *error = NULL;
        if (platform_load_library(rt, rt->app.libraries[i], &error)) continue;
        char text[1024];
        snprintf(text, sizeof text, "This application needs the library %s: %s\n", rt->app.libraries[i], error ? error : "not found.");
        platform_write(2, text, strlen(text));
        free(error);
        return false;
    }
    return true;
}

bool runtime_start(aqua_runtime *rt, const char *entry) {
    if (!require_libraries(rt)) { rt->exit_code = 1; return false; }
    int32_t module = entry ? application_module(&rt->app, entry) : (int32_t)rt->app.entry;
    if (module < 0) {
        char text[512];
        snprintf(text, sizeof text, "Packaged script not found: %s\n", entry);
        platform_write(2, text, strlen(text));
        rt->exit_code = 1;
        return false;
    }
    uint32_t pending = guest(rt, GUEST_pending, 0, 0, 0, 0);
    /* Members the guest may cache never change while the runtime lives (live properties are marked uncacheable),
       so one era serves the whole run. */
    if (pending) guest_set_u32(rt, pending + G_Pending_HostEra, 1);
    rt->entry_module = (uint32_t)module;
    uint32_t context = context_new(rt, rt->app.modules[module].path);
    uint32_t scope = guest(rt, GUEST_scope, 0, 0, 0, 0);
    task *t = calloc(1, sizeof *t);
    t->execution = guest(rt, GUEST_execute, rt->app.program_address, rt->app.modules[module].function, scope, context);
    t->done = main_done; t->loop = true;
    t->next = rt->tasks; rt->tasks = t;
    return true;
}

double runtime_turn(aqua_runtime *rt, double budget_ms) {
    double deadline = aqua_now_ms(rt) + budget_ms;
    process_posts(rt);
    bool runnable = false;
    for (task *t = rt->tasks; t && !rt->exiting;) {
        task *next = t->next;
        if (!t->finished && !t->pending && t->execution) {
            run_slice(rt, t);
            if (!t->finished && !t->pending) runnable = true;
        }
        if (t->finished) { unlink_task(rt, t); free(t); }
        t = next;
        if (aqua_now_ms(rt) >= deadline) { if (t) runnable = true; break; }
    }
    double now = aqua_now_ms(rt);
    while (!rt->exiting && rt->timers && rt->timers->due <= now) {
        timer *t = rt->timers;
        rt->timers = t->next;
        double next = t->tick(rt, now, t->data);
        if (next >= 0) { t->due = aqua_now_ms(rt) + next; insert_timer(rt, t); }
        else free(t);
        now = aqua_now_ms(rt);
        if (now >= deadline) break;
    }
    if (rt->exiting) return -1;
    /* A tick may have settled a deferred call (aqua_resolve), making its task runnable again. */
    for (task *t = rt->tasks; t && !runnable; t = t->next) runnable = !t->finished && !t->pending && t->execution;
    if (runnable || atomic_load(&rt->post_flag)) return 0;
    if (!rt->tasks && !rt->timers && rt->finished) { promises_finish(rt); return -1; }
    if (rt->timers) { double wait = rt->timers->due - aqua_now_ms(rt); return wait > 0 ? wait : 0; }
    return 1e9; /* only waiting executions: sleep until posted work arrives */
}
