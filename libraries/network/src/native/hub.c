/* Worker threads and timers of one runtime. Jobs run blocking I/O on a pool that grows while every worker is busy
 * (long reads, accepts and server connections each hold one) and shrinks after 30 idle seconds. A job's `done` runs
 * on the runtime's thread through aqua_post. Timers wait on one thread and fire through aqua_post as well. When the
 * runtime ends, engine_destroy raises the stop flag every wait checks, waits for running jobs, and frees the rest. */
#include "native.h"
#include <stdlib.h>
#include <time.h>
#if !defined(AQUA_OS_WINDOWS)
#include <errno.h>
#endif

struct net_timer {
    int64_t due;
    void (*fire)(network_state *s, void *data);
    void *data;
    bool posted, cancelled;
    engine *engine;
    net_timer *next;          /* waiting timers, by due time */
    net_timer *previous_all, *next_all;
};

struct engine {
    network_state *state;
    aqua_runtime *rt;
    pthread_mutex_t lock;
    pthread_cond_t work;      /* queued jobs, stop */
    pthread_cond_t changed;   /* a worker or the timer thread exited, a job finished */
    net_job *queue_head, *queue_tail;
    net_job *jobs;            /* every job not yet delivered */
    uint32_t workers, idle, running, queued;
    atomic_bool stopping;
    pthread_cond_t timer_wake;
    net_timer *timers;        /* waiting, by due time */
    net_timer *all_timers;    /* waiting or posted */
    bool timer_thread;
};

static void absolute_time(struct timespec *at, int64_t delay_ms) {
    clock_gettime(CLOCK_REALTIME, at);
    if (delay_ms < 0) delay_ms = 0;
    at->tv_sec += (time_t)(delay_ms / 1000);
    at->tv_nsec += (long)(delay_ms % 1000) * 1000000L;
    if (at->tv_nsec >= 1000000000L) { at->tv_sec++; at->tv_nsec -= 1000000000L; }
}

engine *engine_create(network_state *s) {
    sockets_start();
    platform_runtime_start();
    engine *e = calloc(1, sizeof *e);
    e->state = s;
    e->rt = s->rt;
    pthread_mutex_init(&e->lock, NULL);
    pthread_cond_init(&e->work, NULL);
    pthread_cond_init(&e->changed, NULL);
    pthread_cond_init(&e->timer_wake, NULL);
    atomic_init(&e->stopping, false);
    return e;
}
const atomic_bool *engine_stopping(engine *e) { return &e->stopping; }

/* ---- Jobs ---- */

static void job_delivered(aqua_runtime *rt, void *data) {
    (void)rt;
    net_job *job = data;
    engine *e = job->engine;
    pthread_mutex_lock(&e->lock);
    if (job->previous) job->previous->next = job->next; else e->jobs = job->next;
    if (job->next) job->next->previous = job->previous;
    pthread_mutex_unlock(&e->lock);
    job->done(e->state, job);
}

static void *worker(void *data) {
    engine *e = data;
    pthread_mutex_lock(&e->lock);
    for (;;) {
        while (!e->queue_head && !atomic_load(&e->stopping)) {
            e->idle++;
            struct timespec at;
            absolute_time(&at, 30000);
            int waited = pthread_cond_timedwait(&e->work, &e->lock, &at);
            e->idle--;
            if (waited != 0 && !e->queue_head) goto exit;
        }
        if (atomic_load(&e->stopping)) break;
        net_job *job = e->queue_head;
        e->queue_head = job->queue_next;
        e->queued--;
        if (!e->queue_head) e->queue_tail = NULL;
        e->running++;
        pthread_mutex_unlock(&e->lock);
        job->run(job);
        pthread_mutex_lock(&e->lock);
        e->running--;
        job->finished = true;
        if (!atomic_load(&e->stopping)) aqua_post(e->rt, job_delivered, job);
        pthread_cond_broadcast(&e->changed);
    }
exit:
    e->workers--;
    pthread_cond_broadcast(&e->changed);
    pthread_mutex_unlock(&e->lock);
    platform_thread_exit();
    return NULL;
}

void job_start(engine *e, net_job *job) {
    job->engine = e;
    job->queue_next = NULL;
    job->finished = false;
    pthread_mutex_lock(&e->lock);
    job->previous = NULL;
    job->next = e->jobs;
    if (e->jobs) e->jobs->previous = job;
    e->jobs = job;
    if (e->queue_tail) e->queue_tail->queue_next = job; else e->queue_head = job;
    e->queue_tail = job;
    e->queued++;
    if (e->queued > e->idle) {
        pthread_t thread;
        pthread_attr_t attributes;
        pthread_attr_init(&attributes);
        pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
        pthread_attr_setstacksize(&attributes, 512 * 1024);
        if (pthread_create(&thread, &attributes, worker, e) == 0) e->workers++;
        pthread_attr_destroy(&attributes);
    }
    pthread_cond_signal(&e->work);
    pthread_mutex_unlock(&e->lock);
}

/* ---- Timers ---- */

static void timer_delivered(aqua_runtime *rt, void *data) {
    (void)rt;
    net_timer *t = data;
    engine *e = t->engine;
    pthread_mutex_lock(&e->lock);
    if (t->previous_all) t->previous_all->next_all = t->next_all; else e->all_timers = t->next_all;
    if (t->next_all) t->next_all->previous_all = t->previous_all;
    bool cancelled = t->cancelled;
    pthread_mutex_unlock(&e->lock);
    if (!cancelled && !e->state->closing) t->fire(e->state, t->data);
    free(t);
}

static void *timer_thread(void *data) {
    engine *e = data;
    pthread_mutex_lock(&e->lock);
    while (!atomic_load(&e->stopping)) {
        int64_t now = net_clock_ms();
        net_timer *t = e->timers;
        if (t && t->due <= now) {
            e->timers = t->next;
            t->posted = true;
            aqua_post(e->rt, timer_delivered, t);
            continue;
        }
        if (!t) pthread_cond_wait(&e->timer_wake, &e->lock);
        else {
            struct timespec at;
            absolute_time(&at, t->due - now);
            pthread_cond_timedwait(&e->timer_wake, &e->lock, &at);
        }
    }
    e->timer_thread = false;
    pthread_cond_broadcast(&e->changed);
    pthread_mutex_unlock(&e->lock);
    return NULL;
}

net_timer *timer_start(network_state *s, double ms, void (*fire)(network_state *s, void *data), void *data) {
    engine *e = s->engine;
    net_timer *t = calloc(1, sizeof *t);
    t->due = net_clock_ms() + (int64_t)(ms > 0 ? ms : 0);
    t->fire = fire;
    t->data = data;
    t->engine = e;
    pthread_mutex_lock(&e->lock);
    t->next_all = e->all_timers;
    if (e->all_timers) e->all_timers->previous_all = t;
    e->all_timers = t;
    net_timer **p = &e->timers;
    while (*p && (*p)->due <= t->due) p = &(*p)->next;
    t->next = *p;
    *p = t;
    if (!e->timer_thread) {
        pthread_t thread;
        if (pthread_create(&thread, NULL, timer_thread, e) == 0) { pthread_detach(thread); e->timer_thread = true; }
    }
    pthread_cond_signal(&e->timer_wake);
    pthread_mutex_unlock(&e->lock);
    return t;
}

void timer_cancel(network_state *s, net_timer *t) {
    engine *e = s->engine;
    if (!t) return;
    pthread_mutex_lock(&e->lock);
    if (t->posted) { t->cancelled = true; pthread_mutex_unlock(&e->lock); return; }
    for (net_timer **p = &e->timers; *p; p = &(*p)->next) if (*p == t) { *p = t->next; break; }
    if (t->previous_all) t->previous_all->next_all = t->next_all; else e->all_timers = t->next_all;
    if (t->next_all) t->next_all->previous_all = t->previous_all;
    pthread_mutex_unlock(&e->lock);
    free(t);
}

/* ---- Shutdown ---- */

void engine_destroy(engine *e) {
    if (!e) return;
    pthread_mutex_lock(&e->lock);
    atomic_store(&e->stopping, true);
    pthread_cond_broadcast(&e->work);
    pthread_cond_broadcast(&e->timer_wake);
    while (e->workers || e->timer_thread) pthread_cond_wait(&e->changed, &e->lock);
    pthread_mutex_unlock(&e->lock);    /* Nothing runs any more: jobs never delivered and timers never fired are discarded. Their posted tasks, if any,
       are dropped unrun with the runtime. */
    while (e->jobs) {
        net_job *job = e->jobs;
        e->jobs = job->next;
        if (job->discard) job->discard(job); else free(job);
    }
    while (e->all_timers) { net_timer *t = e->all_timers; e->all_timers = t->next_all; free(t); }
    pthread_cond_destroy(&e->work);
    pthread_cond_destroy(&e->changed);
    pthread_cond_destroy(&e->timer_wake);
    pthread_mutex_destroy(&e->lock);
    free(e);
}

static bool engine_random(void *buffer, size_t length) { return platform_random(buffer, length); }
static char *engine_page_origin(void) { return NULL; }
