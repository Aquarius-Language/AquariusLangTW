/* Promises (承諾 / Promise) and the language's async functions and awaits, which the compiler lowers to calls of the
   builtins async@ and await@ (names no source can write). An async function runs as its own task (aqua_invoke_async)
   and settles its promise when the task ends; awaiting a pending promise defers the awaiting call like any asynchronous
   library function, so the scheduler runs other tasks until the promise settles. The native loops and the web's event
   loop drive the same tasks, so every target shares this file. Reactions (await, Then, combinators) run in the order
   they were added; Then callbacks run at a later turn, never inside the code that settles the promise. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { PENDING, FULFILLED, REJECTED };

typedef struct promise promise;
typedef void (*reaction_fn)(aqua_runtime *rt, promise *p, void *data);
typedef struct reaction { aqua_pending *pending; reaction_fn run; void *data; struct reaction *next; } reaction;
struct promise {
    int state;
    uint32_t handle;
    aqua_ref value;          /* the value, or the rejection reason */
    aqua_ref self;           /* keeps the object alive until it settles */
    bool handled;            /* awaited, given a reaction, or adopted: its rejection reaches someone */
    reaction *reactions, **last;
    promise *next_unhandled; /* rejected and not handled yet (aqua_runtime.unhandled) */
};

static aqua_class *promise_class, *async_class, *resolver_class;

static aqua_value promise_value(const promise *p) { aqua_value v = {AQUA_NATIVE, p->handle, 0}; return v; }

static void unlist(aqua_runtime *rt, promise *p) {
    for (promise **q = (promise **)&rt->unhandled; *q; q = &(*q)->next_unhandled)
        if (*q == p) { *q = p->next_unhandled; p->next_unhandled = NULL; return; }
}

static void report(aqua_runtime *rt, promise *p) {
    char *reason = format_value(rt, aqua_ref_value(rt, p->value));
    char line[2304];
    int n = snprintf(line, sizeof line, "未處理的承諾拒絕 / Unhandled promise rejection: %s\n", reason);
    platform_write(1, line, n > 0 && (size_t)n < sizeof line ? (size_t)n : strlen(line));
    free(reason);
    rt->exit_code = 1;
}

static void promise_finalize(aqua_runtime *rt, void *data) {
    promise *p = data;
    /* Collected without anyone ever seeing its rejection. */
    if (p->state == REJECTED && !p->handled) { unlist(rt, p); report(rt, p); }
    aqua_release(rt, p->value);
    for (reaction *r = p->reactions; r;) { reaction *next = r->next; free(r); r = next; }
    free(p);
}

/* Reports the rejections nobody handled when the application ends. */
void promises_finish(aqua_runtime *rt) {
    while (rt->unhandled) {
        promise *p = rt->unhandled;
        rt->unhandled = p->next_unhandled;
        p->next_unhandled = NULL; p->handled = true;
        report(rt, p);
    }
}

static aqua_value promise_new(aqua_runtime *rt, promise **out) {
    promise *p = calloc(1, sizeof *p);
    p->last = &p->reactions;
    aqua_value v = aqua_object_new(rt, promise_class, p);
    if (v.type == AQUA_ERROR) { free(p); *out = NULL; return v; }
    p->handle = v.ref;
    p->self = aqua_retain(rt, v);
    *out = p;
    return v;
}

static void mark_handled(aqua_runtime *rt, promise *p) {
    if (p->handled) return;
    p->handled = true;
    if (p->state == REJECTED) unlist(rt, p);
}

/* The error an await fails with: the reason's text. */
static aqua_value reason_error(aqua_runtime *rt, aqua_value reason) {
    if (reason.type == AQUA_STRING) { reason.type = AQUA_ERROR; return reason; }
    char *text = format_value(rt, reason);
    aqua_value error = aqua_cstring(rt, text);
    free(text);
    error.type = AQUA_ERROR;
    return error;
}

static void settle(aqua_runtime *rt, promise *p, int state, aqua_value value) {
    if (p->state != PENDING) return;
    /* An error value's reason is its message. */
    if (value.type == AQUA_ERROR) value.type = AQUA_STRING;
    p->state = state;
    p->value = aqua_retain(rt, value);
    reaction *r = p->reactions;
    p->reactions = NULL; p->last = &p->reactions;
    if (state == REJECTED && !r && !p->handled) { p->next_unhandled = rt->unhandled; rt->unhandled = p; }
    while (r) {
        reaction *next = r->next;
        if (r->pending) aqua_resolve(r->pending, state == FULFILLED ? value : reason_error(rt, value));
        else r->run(rt, p, r->data);
        free(r);
        r = next;
    }
    aqua_ref self = p->self;
    p->self = 0;
    aqua_release(rt, self);
}

static void add_reaction(aqua_runtime *rt, promise *p, aqua_pending *pending, reaction_fn run, void *data) {
    mark_handled(rt, p);
    if (p->state != PENDING) { run(rt, p, data); return; }
    reaction *r = calloc(1, sizeof *r);
    r->pending = pending; r->run = run; r->data = data;
    *p->last = r; p->last = &r->next;
}

static void resolve_with(aqua_runtime *rt, promise *p, aqua_value value);

static void adopt(aqua_runtime *rt, promise *source, void *data) {
    promise *target = data;
    if (source->state == FULFILLED) resolve_with(rt, target, aqua_ref_value(rt, source->value));
    else settle(rt, target, REJECTED, aqua_ref_value(rt, source->value));
}

/* Fulfills with a value, or follows a promise given as the value until it settles. */
static void resolve_with(aqua_runtime *rt, promise *p, aqua_value value) {
    promise *q = aqua_object_data(rt, value, promise_class);
    if (!q) { settle(rt, p, FULFILLED, value); return; }
    if (q == p) { settle(rt, p, REJECTED, aqua_cstring(rt, "承諾不能以自己解決 / A promise cannot be resolved with itself.")); return; }
    add_reaction(rt, q, NULL, adopt, p);
}

/* ---- Library functions defined with aqua_define_async --------------------------------------------------------------- */

aqua_value promise_of_call(aqua_runtime *rt, aqua_call *call) {
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (!p) return v;
    aqua_pending *pending = call->pending;
    if (pending && pending->settled) {
        /* Resolved before the function returned. */
        aqua_value result = pending->result;
        promise_settle_pending(rt, p, result);
        aqua_release(rt, pending->result_ref);
        free(pending);
    } else if (pending) pending->promise = p; /* aqua_resolve settles it; the promise stays alive until then */
    else if (call->failed || call->result.type == AQUA_ERROR) settle(rt, p, REJECTED, call->result);
    else resolve_with(rt, p, call->result);
    return v;
}

void promise_settle_pending(aqua_runtime *rt, promise *p, aqua_value result) {
    if (result.type == AQUA_ERROR) settle(rt, p, REJECTED, result);
    else resolve_with(rt, p, result);
}

/* ---- async@ and await@ ---------------------------------------------------------------------------------------------- */

typedef struct async_function { aqua_ref function; } async_function;
static void async_finalize(aqua_runtime *rt, void *data) { async_function *f = data; aqua_release(rt, f->function); free(f); }

static void async_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    if (ok) resolve_with(rt, data, result);
    else settle(rt, data, REJECTED, result);
}

static void async_call(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    async_function *f = aqua_self(call);
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (!p) { aqua_return(call, v); return; }
    /* The body runs now until it first waits or yields, then as its own task. */
    aqua_invoke_async(rt, aqua_ref_value(rt, f->function), call->arguments, call->count, async_done, p);
    aqua_return(call, v);
}

static void async_wrap(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    async_function *f = malloc(sizeof *f);
    f->function = aqua_retain(rt, aqua_arg(call, 0));
    aqua_value object = aqua_object_new(rt, async_class, f);
    if (object.type == AQUA_ERROR) { async_finalize(rt, f); aqua_return(call, object); return; }
    bool cacheable;
    aqua_return(call, member_value(rt, object.ref, 0, &cacheable));
}

static void await_value(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value v = aqua_arg(call, 0);
    promise *p = aqua_object_data(rt, v, promise_class);
    if (!p) { aqua_return(call, v); return; }
    mark_handled(rt, p);
    if (p->state == FULFILLED) { aqua_return(call, aqua_ref_value(rt, p->value)); return; }
    if (p->state == REJECTED) { call->result = reason_error(rt, aqua_ref_value(rt, p->value)); call->failed = true; return; }
    /* Only a task of the runtime's loop can wait; a callback a library runs to completion cannot. */
    if (!rt->current || !rt->current->loop) {
        aqua_fail(call, "等待 / await: 這段程式同步執行，無法等待尚未完成的承諾；請改用非同步函式或 Then / this code runs synchronously and cannot wait for a pending promise; use an async function or Then.");
        return;
    }
    add_reaction(rt, p, aqua_defer(call), NULL, NULL);
}

/* ---- Then, Catch, Finally ------------------------------------------------------------------------------------------- */

typedef struct then_data { promise *source, *derived; aqua_ref keep, on_ok, on_error; bool finally; } then_data;

static void then_free(aqua_runtime *rt, then_data *t) {
    aqua_release(rt, t->keep); aqua_release(rt, t->on_ok); aqua_release(rt, t->on_error);
    free(t);
}

static void then_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    then_data *t = data;
    if (!ok) settle(rt, t->derived, REJECTED, result);
    else if (t->finally) adopt(rt, t->source, t->derived); /* Finally passes the source's outcome on */
    else resolve_with(rt, t->derived, result);
    then_free(rt, t);
}

static double then_tick(aqua_runtime *rt, double now, void *data) {
    (void)now;
    then_data *t = data;
    aqua_value value = aqua_ref_value(rt, t->source->value);
    aqua_ref handler = t->finally ? t->on_ok : t->source->state == FULFILLED ? t->on_ok : t->on_error;
    if (!handler) { adopt(rt, t->source, t->derived); then_free(rt, t); return -1; }
    aqua_value function = aqua_ref_value(rt, handler);
    /* A script handler without parameters ignores the value. */
    uint32_t count = t->finally || aqua_function_arity(rt, function) == 0 ? 0 : 1;
    aqua_invoke_async(rt, function, &value, count, then_done, t);
    return -1;
}

static void then_react(aqua_runtime *rt, promise *p, void *data) { (void)p; aqua_schedule(rt, 0, then_tick, data); }

static void attach(aqua_call *call, aqua_value on_ok, aqua_value on_error, bool finally) {
    aqua_runtime *rt = aqua_call_runtime(call);
    promise *source = aqua_self(call), *derived;
    aqua_value v = promise_new(rt, &derived);
    if (!derived) { aqua_return(call, v); return; }
    then_data *t = calloc(1, sizeof *t);
    t->source = source; t->derived = derived; t->finally = finally;
    t->keep = aqua_retain(rt, promise_value(source));
    if (aqua_is_callable(on_ok)) t->on_ok = aqua_retain(rt, on_ok);
    if (aqua_is_callable(on_error)) t->on_error = aqua_retain(rt, on_error);
    add_reaction(rt, source, NULL, then_react, t);
    aqua_return(call, v);
}
static void promise_then(aqua_call *call) { attach(call, aqua_arg(call, 0), aqua_arg(call, 1), false); }
static void promise_catch(aqua_call *call) { attach(call, aqua_null(), aqua_arg(call, 0), false); }
static void promise_finally(aqua_call *call) { attach(call, aqua_arg(call, 0), aqua_null(), true); }

static void promise_state(aqua_call *call) {
    static const char *const names[] = {"pending", "fulfilled", "rejected"};
    aqua_return_cstring(call, names[((promise *)aqua_self(call))->state]);
}
static void promise_result(aqua_call *call, int state) {
    promise *p = aqua_self(call);
    aqua_return(call, p->state == state ? aqua_ref_value(aqua_call_runtime(call), p->value) : aqua_null());
}
static void promise_fulfilled_value(aqua_call *call) { promise_result(call, FULFILLED); }
static void promise_reason(aqua_call *call) {
    promise *p = aqua_self(call);
    /* Reading the reason handles the rejection. */
    if (p->state == REJECTED) mark_handled(aqua_call_runtime(call), p);
    promise_result(call, REJECTED);
}
static void promise_done(aqua_call *call) { aqua_return_bool(call, ((promise *)aqua_self(call))->state != PENDING); }

/* ---- The Promise / 承諾 module -------------------------------------------------------------------------------------- */

static void promise_resolve(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value value = aqua_arg(call, 0);
    if (aqua_object_data(rt, value, promise_class)) { aqua_return(call, value); return; }
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (p) settle(rt, p, FULFILLED, value);
    aqua_return(call, v);
}

static void promise_reject(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (p) settle(rt, p, REJECTED, aqua_argc(call) ? aqua_arg(call, 0) : aqua_cstring(rt, "Rejected / 已拒絕"));
    aqua_return(call, v);
}

static void promise_is(aqua_call *call) {
    aqua_return_bool(call, aqua_object_data(aqua_call_runtime(call), aqua_arg(call, 0), promise_class) != NULL);
}

typedef struct resolver { promise *target; aqua_ref keep; } resolver;
static void resolver_finalize(aqua_runtime *rt, void *data) { resolver *r = data; aqua_release(rt, r->keep); free(r); }
static void resolver_resolve(aqua_call *call) { resolver *r = aqua_self(call); resolve_with(aqua_call_runtime(call), r->target, aqua_arg(call, 0)); }
static void resolver_reject(aqua_call *call) {
    resolver *r = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    settle(rt, r->target, REJECTED, aqua_argc(call) ? aqua_arg(call, 0) : aqua_cstring(rt, "Rejected / 已拒絕"));
}
static void executor_done(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    resolver *r = data;
    if (!ok) settle(rt, r->target, REJECTED, result);
}

/* Promise.New(function(resolve, reject) { ... }): the executor runs now and settles the promise through either function. */
static void promise_create(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (!p) { aqua_return(call, v); return; }
    resolver *r = calloc(1, sizeof *r);
    r->target = p; r->keep = aqua_retain(rt, v);
    aqua_value object = aqua_object_new(rt, resolver_class, r);
    if (object.type == AQUA_ERROR) { resolver_finalize(rt, r); aqua_return(call, object); return; }
    bool cacheable;
    aqua_value functions[2] = {member_value(rt, object.ref, 0, &cacheable), member_value(rt, object.ref, 1, &cacheable)};
    aqua_invoke_async(rt, aqua_arg(call, 0), functions, 2, executor_done, r);
    aqua_return(call, v);
}

typedef struct delay { promise *p; aqua_ref value; } delay;
static double delay_tick(aqua_runtime *rt, double now, void *data) {
    (void)now;
    delay *d = data;
    resolve_with(rt, d->p, aqua_ref_value(rt, d->value));
    aqua_release(rt, d->value);
    free(d);
    return -1;
}

/* Promise.Delay(milliseconds, value?): fulfilled with the value after the delay, without blocking other tasks. */
static void promise_delay(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    double ms = aqua_arg_number(call, 0);
    if (ms < 0) { aqua_fail(call, "Promise.Delay / 承諾.延遲: the delay must not be negative."); return; }
    promise *p;
    aqua_value v = promise_new(rt, &p);
    if (!p) { aqua_return(call, v); return; }
    delay *d = malloc(sizeof *d);
    d->p = p; d->value = aqua_retain(rt, aqua_has_arg(call, 1) ? aqua_arg(call, 1) : aqua_null());
    aqua_schedule(rt, ms, delay_tick, d);
    aqua_return(call, v);
}

/* Combinators over an array of promises and values (a value counts as already fulfilled). */
enum { ALL, ALL_SETTLED, RACE, ANY };
typedef struct gather { promise *result; aqua_ref values; uint32_t remaining, waiting; int mode; } gather;
typedef struct gather_item { gather *g; uint32_t index; } gather_item;

static aqua_value settled_entry(aqua_runtime *rt, int state, aqua_value value) {
    aqua_value entry = aqua_hash_new(rt, 2);
    aqua_hash_set_field(rt, entry, "status", aqua_cstring(rt, state == FULFILLED ? "fulfilled" : "rejected"));
    aqua_hash_set_field(rt, entry, state == FULFILLED ? "value" : "reason", value);
    return entry;
}

static void gather_settle(aqua_runtime *rt, gather *g, uint32_t index, int state, aqua_value value) {
    aqua_value values = aqua_ref_value(rt, g->values);
    switch (g->mode) {
    case ALL:
        if (state == REJECTED) settle(rt, g->result, REJECTED, value);
        else aqua_array_set(rt, values, index, value);
        break;
    case ALL_SETTLED: aqua_array_set(rt, values, index, settled_entry(rt, state, value)); break;
    case RACE: if (state == FULFILLED) resolve_with(rt, g->result, value); else settle(rt, g->result, REJECTED, value); break;
    case ANY:
        if (state == FULFILLED) resolve_with(rt, g->result, value);
        else aqua_array_set(rt, values, index, value);
        break;
    }
    if (--g->remaining == 0) {
        if (g->mode == ALL || g->mode == ALL_SETTLED) settle(rt, g->result, FULFILLED, values);
        else if (g->mode == ANY)
            settle(rt, g->result, REJECTED, aqua_cstring(rt, "所有承諾都被拒絕 / All promises were rejected."));
    }
}

static void gather_react(aqua_runtime *rt, promise *p, void *data) {
    gather_item *item = data;
    gather *g = item->g;
    gather_settle(rt, g, item->index, p->state, aqua_ref_value(rt, p->value));
    free(item);
    if (--g->waiting == 0) { aqua_release(rt, g->values); free(g); }
}

static void gather_run(aqua_call *call, int mode) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value inputs = aqua_arg(call, 0);
    uint32_t count = aqua_array_length(rt, inputs);
    promise *result;
    aqua_value v = promise_new(rt, &result);
    if (!result) { aqua_return(call, v); return; }
    if (count == 0) {
        /* Nothing to wait for: All gives [], Any rejects, Race stays pending (as in JavaScript). */
        if (mode == ALL || mode == ALL_SETTLED) settle(rt, result, FULFILLED, aqua_array_new(rt, 0));
        else if (mode == ANY) settle(rt, result, REJECTED, aqua_cstring(rt, "所有承諾都被拒絕 / All promises were rejected."));
        aqua_return(call, v);
        return;
    }
    gather *g = calloc(1, sizeof *g);
    g->result = result; g->mode = mode; g->remaining = count; g->waiting = count + 1;
    g->values = aqua_retain(rt, aqua_array_new(rt, mode == RACE ? 0 : count));
    for (uint32_t i = 0; i < count; i++) {
        aqua_value item = aqua_array_get(rt, inputs, i);
        promise *p = aqua_object_data(rt, item, promise_class);
        if (!p) { gather_settle(rt, g, i, FULFILLED, item); g->waiting--; continue; }
        gather_item *entry = malloc(sizeof *entry);
        entry->g = g; entry->index = i;
        add_reaction(rt, p, NULL, gather_react, entry);
    }
    if (--g->waiting == 0) { aqua_release(rt, g->values); free(g); }
    aqua_return(call, v);
}
static void promise_all(aqua_call *call) { gather_run(call, ALL); }
static void promise_all_settled(aqua_call *call) { gather_run(call, ALL_SETTLED); }
static void promise_race(aqua_call *call) { gather_run(call, RACE); }
static void promise_any(aqua_call *call) { gather_run(call, ANY); }

typedef struct timeout { promise *result; aqua_ref keep, message; } timeout;
static double timeout_tick(aqua_runtime *rt, double now, void *data) {
    (void)now;
    timeout *t = data;
    settle(rt, t->result, REJECTED, aqua_ref_value(rt, t->message));
    aqua_release(rt, t->message); aqua_release(rt, t->keep);
    free(t);
    return -1;
}

/* Promise.Timeout(promise, milliseconds, reason?): settles like the promise, or rejects once the time runs out. */
static void promise_timeout(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    double ms = aqua_arg_number(call, 1);
    if (ms < 0) { aqua_fail(call, "Promise.Timeout / 承諾.逾時: the time limit must not be negative."); return; }
    promise *result;
    aqua_value v = promise_new(rt, &result);
    if (!result) { aqua_return(call, v); return; }
    resolve_with(rt, result, aqua_arg(call, 0));
    timeout *t = malloc(sizeof *t);
    t->result = result; t->keep = aqua_retain(rt, v);
    t->message = aqua_retain(rt, aqua_has_arg(call, 2) ? aqua_arg(call, 2) : aqua_cstring(rt, "逾時 / Timeout"));
    aqua_schedule(rt, ms, timeout_tick, t);
    aqua_return(call, v);
}

static aqua_class *promise_module;
static void promise_global(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value module = {AQUA_NATIVE, module_handle(rt, promise_module), 0};
    aqua_return(call, module);
}

void promise_library(aqua_library *library, aqua_class *globals) {
    promise_class = aqua_define_class(library, "promise", promise_finalize);
    aqua_define_function(promise_class, "Then|然後", promise_then, "v|v");
    aqua_define_function(promise_class, "Catch|捕捉", promise_catch, "f");
    aqua_define_function(promise_class, "Finally|最終", promise_finally, "f");
    aqua_define_property(promise_class, "State|狀態", promise_state);
    aqua_define_property(promise_class, "Value|值", promise_fulfilled_value);
    aqua_define_property(promise_class, "Reason|原因", promise_reason);
    aqua_define_property(promise_class, "IsDone|是否完成", promise_done);

    async_class = aqua_define_class(library, "asyncfunction", async_finalize);
    aqua_define_function(async_class, "call", async_call, "v*");
    resolver_class = aqua_define_class(library, "resolver", resolver_finalize);
    aqua_define_function(resolver_class, "resolve", resolver_resolve, "|v");
    aqua_define_function(resolver_class, "reject", resolver_reject, "|v");

    promise_module = aqua_define_module(library, "Promise|承諾");
    aqua_define_function(promise_module, "New|建立", promise_create, "f");
    aqua_define_function(promise_module, "Resolve|解決", promise_resolve, "|v");
    aqua_define_function(promise_module, "Reject|拒絕", promise_reject, "|v");
    aqua_define_function(promise_module, "All|全部", promise_all, "a");
    aqua_define_function(promise_module, "AllSettled|全部結算", promise_all_settled, "a");
    aqua_define_function(promise_module, "Race|競速", promise_race, "a");
    aqua_define_function(promise_module, "Any|任一", promise_any, "a");
    aqua_define_function(promise_module, "Delay|延遲", promise_delay, "n|v");
    aqua_define_function(promise_module, "Timeout|逾時", promise_timeout, "vn|v");
    aqua_define_function(promise_module, "IsPromise|是承諾", promise_is, "v");

    aqua_define_function(globals, "async@", async_wrap, "f");
    aqua_define_function(globals, "await@", await_value, "v");
    aqua_define_property(globals, "Promise|承諾", promise_global);
}
